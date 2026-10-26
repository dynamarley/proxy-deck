using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TimeoutException = System.TimeoutException;

namespace ProxyDeck
{
    internal static class Paths
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        internal static string Proxy { get { return SettingsStore.Current.ProxyDirectory; } }
        internal static string ProxyExe { get { return Path.Combine(Proxy, "cli-proxy-api.exe"); } }
        internal static readonly string Tailscale = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        internal static string Api { get { return "http://127.0.0.1:" + SettingsStore.Current.Port; } }
        internal const string Fleet = "/v0/management/plugins/codex-fleet-manager";
        internal static void Log(string message)
        {
            try
            {
                lock (typeof(Paths))
                {
                    Directory.CreateDirectory(SettingsStore.DataRoot);
                    string file = Path.Combine(SettingsStore.DataRoot, "proxy-deck.log");
                    if (File.Exists(file) && new FileInfo(file).Length > 131072)
                        File.WriteAllLines(file, File.ReadAllLines(file).Reverse().Take(120).Reverse());
                    File.AppendAllText(file, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    internal static class Json
    {
        internal static Dictionary<string, object> Parse(string text) { return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static object Get(Dictionary<string, object> map, string key) { object value; return map.TryGetValue(key, out value) ? value : null; }
        internal static string Str(Dictionary<string, object> map, string key) { return Convert.ToString(Get(map, key), CultureInfo.InvariantCulture) ?? ""; }
        internal static bool Bool(Dictionary<string, object> map, string key) { return Get(map, key) is bool && (bool)Get(map, key); }
        internal static IEnumerable<object> Items(object value) { var list = value as IEnumerable; return list == null || value is string ? Enumerable.Empty<object>() : list.Cast<object>(); }
    }

    internal sealed class Quota
    {
        internal bool Known;
        internal double Remaining;
        internal DateTimeOffset? Reset;
        internal string Kind;
        internal static Quota Parse(object value)
        {
            var d = Json.Map(value);
            var q = new Quota { Kind = Json.Str(d, "kind") };
            double percent;
            q.Known = !Json.Bool(d, "missing") && Json.Get(d, "remaining_percent") != null &&
                Double.TryParse(Json.Str(d, "remaining_percent"), NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
            if (q.Known) q.Remaining = Math.Max(0, Math.Min(100, Convert.ToDouble(Json.Get(d, "remaining_percent"), CultureInfo.InvariantCulture)));
            DateTimeOffset reset;
            if (DateTimeOffset.TryParse(Json.Str(d, "reset_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out reset)) q.Reset = reset;
            return q;
        }
    }

    internal sealed class Account
    {
        internal string Id, Name, Email;
        internal bool Available;
        internal double CacheAge;
        internal Quota Short, Long;
    }

    internal sealed class Snapshot
    {
        internal bool Desired, Busy, ProxyReady, NetworkReady, Warmup;
        internal string Stage = "Ready to start", Error = "", RemoteUrl = "";
        internal DateTimeOffset? ReadAt;
        internal List<Account> Accounts = new List<Account>();
        internal bool Ready { get { return Desired && ProxyReady && (!SettingsStore.Current.UseTailscale || NetworkReady) && !Busy && Error.Length == 0; } }
    }

    internal sealed class AuthException : Exception
    {
        internal AuthException() : base("Management key rejected. Automatic retries have been paused.") { }
    }
    internal sealed class NetworkSetupException : InvalidOperationException
    {
        internal NetworkSetupException(string message) : base(message) { }
    }

    internal sealed class Controller : IDisposable
    {
        internal Snapshot View = new Snapshot();
        internal event Action Changed;
        readonly HttpClient http;
        readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim refresh = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        Process proxyProcess;
        bool proxyStartedHere;
        bool authBlocked;
        DateTimeOffset lastNetworkCheck = DateTimeOffset.MinValue, lastQuotaRefresh = DateTimeOffset.MinValue;
        internal Controller()
        {
            http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.Timeout = TimeSpan.FromSeconds(6);
        }

        void Signal() { var callback = Changed; if (callback != null) callback(); }
        void Stage(string value) { View.Stage = value; Signal(); }

        internal static async Task<string> Run(string exe, string args, int timeout)
        {
            return await Task.Run(() =>
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                    p.Start();
                    var output = p.StandardOutput.ReadToEndAsync();
                    var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } throw new TimeoutException("The operation timed out."); }
                    Task.WaitAll(output, error);
                    if (p.ExitCode != 0) throw new InvalidOperationException("The operation failed (" + p.ExitCode + ").");
                    return output.Result;
                }
            });
        }

        internal string ManagementKey()
        {
            if (SettingsStore.Current.ProtectedManagementKey.Length > 0) return SettingsStore.Unprotect(SettingsStore.Current.ProtectedManagementKey);
            var data = Json.Parse(File.ReadAllText(Path.Combine(Paths.Proxy, "local-credentials.json")));
            string key = Json.Str(data, "managementKey");
            if (String.IsNullOrWhiteSpace(key)) throw new AuthException();
            return key;
        }

        internal string AccessKey()
        {
            if (SettingsStore.Current.ProtectedAccessKey.Length > 0) return SettingsStore.Unprotect(SettingsStore.Current.ProtectedAccessKey);
            string file = Path.Combine(Paths.Proxy, "local-credentials.json");
            if (File.Exists(file)) { string key = Json.Str(Json.Parse(File.ReadAllText(file)), "accessKey"); if (key.Length > 0) return key; }
            throw new InvalidOperationException("Get your existing client API key from the CLIProxy management panel.");
        }

        async Task<Dictionary<string, object>> Api(string path, bool post)
        { return await Api(path, post ? HttpMethod.Post : HttpMethod.Get, post ? (object)new { } : null); }

        async Task<Dictionary<string, object>> Api(string path, HttpMethod method, object body)
        {
            if (authBlocked) throw new AuthException();
            using (var request = new HttpRequestMessage(method, Paths.Api + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ManagementKey());
                if (body != null) request.Content = new StringContent(new JavaScriptSerializer().Serialize(body), System.Text.Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        authBlocked = true;
                        throw new AuthException();
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Fleet Manager or the management API is unavailable. Open Setup & components to repair it.");
                    response.EnsureSuccessStatusCode();
                    return Json.Parse(await response.Content.ReadAsStringAsync());
                }
            }
        }

        internal static string MaskEmail(string email)
        {
            int at = email.IndexOf('@');
            if (at < 0) return "Codex account";
            return email.Substring(0, Math.Min(4, at)) + "…" + email.Substring(at);
        }

        internal static List<Account> ParseAccounts(Dictionary<string, object> root, Dictionary<string, string> labels)
        {
            var result = new List<Account>();
            foreach (var item in Json.Items(Json.Get(root, "accounts")))
            {
                var data = Json.Map(item);
                string id = Json.Str(data, "auth_id"), email;
                if (!labels.TryGetValue(id, out email)) email = "";
                double age;
                Double.TryParse(Json.Str(data, "cache_age_seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out age);
                result.Add(new Account { Id = id, Name = "Account " + (result.Count + 1).ToString("00"), Email = MaskEmail(email),
                    Available = Json.Bool(data, "available"), Short = Quota.Parse(Json.Get(data, "five_hour")), Long = Quota.Parse(Json.Get(data, "long_window")), CacheAge = Math.Max(0, age) });
            }
            return result;
        }

        async Task ReadNetwork(bool ensureServe)
        {
            if (!SettingsStore.Current.UseTailscale) { View.NetworkReady = false; View.RemoteUrl = ""; return; }
            View.NetworkReady = false;
            lastNetworkCheck = DateTimeOffset.UtcNow;
            var status = Json.Parse(await Run(Paths.Tailscale, "status --json", 8000));
            string state = Json.Str(status, "BackendState");
            if (ensureServe && state == "Stopped")
            {
                await Run(Paths.Tailscale, "up --timeout=15s", 20000);
                status = Json.Parse(await Run(Paths.Tailscale, "status --json", 8000));
                state = Json.Str(status, "BackendState");
            }
            View.NetworkReady = state == "Running";
            if (state == "NeedsLogin")
            {
                throw new NetworkSetupException("Open Setup & components to sign in to Tailscale.");
            }
            if (state == "NeedsMachineAuth") throw new NetworkSetupException("Approve this computer in the Tailscale admin console.");
            if (!View.NetworkReady) throw new InvalidOperationException("Tailscale is disconnected. Check your Tailscale account.");
            string dns = Json.Str(Json.Map(Json.Get(status, "Self")), "DNSName").TrimEnd('.');
            string endpoint = dns + ":" + SettingsStore.Current.Port;
            if (dns.Length > 0) View.RemoteUrl = "https://" + endpoint + "/v0/resource/plugins/codex-fleet-manager/status";
            if (dns.Length == 0) { View.NetworkReady = false; throw new NetworkSetupException("Complete the Tailscale MagicDNS and HTTPS setup."); }
            try
            {
                var serve = Json.Parse(await Run(Paths.Tailscale, "serve status --json", 8000));
                var web = Json.Map(Json.Get(serve, "Web"));
                var node = Json.Map(Json.Get(web, endpoint));
                var handler = Json.Map(Json.Get(Json.Map(Json.Get(node, "Handlers")), "/"));
                if (Json.Str(handler, "Proxy") != Paths.Api)
                {
                    View.NetworkReady = false;
                    if (ensureServe)
                    {
                        await Run(Paths.Tailscale, "serve --bg --yes --https=" + SettingsStore.Current.Port + " " + Paths.Api, 15000);
                        // Confirm the persistent handler, including after first-time Serve consent.
                        var verified = Json.Parse(await Run(Paths.Tailscale, "serve status --json", 8000));
                        var configured = Json.Map(Json.Get(Json.Map(Json.Get(verified, "Web")), endpoint));
                        View.NetworkReady = Json.Str(Json.Map(Json.Get(Json.Map(Json.Get(configured, "Handlers")), "/")), "Proxy") == Paths.Api;
                    }
                    if (!View.NetworkReady) throw new NetworkSetupException("Enable Tailscale HTTPS/Serve, then check the connection again.");
                }
            }
            catch (Exception error) { View.NetworkReady = false; if (error is NetworkSetupException) throw; throw new NetworkSetupException("Enable Tailscale HTTPS/Serve, then check the connection again."); }
            lastNetworkCheck = DateTimeOffset.UtcNow;
        }

        internal async Task StartAsync()
        {
            if (!await lifecycle.WaitAsync(0)) return;
            View.Desired = true; View.Busy = true; View.Error = ""; Signal();
            try
            {
                Exception networkError = null;
                if (SettingsStore.Current.UseTailscale)
                {
                try
                {
                Stage("Preparing Tailscale");
                await Task.Run(() =>
                {
                    using (var service = new ServiceController("Tailscale"))
                    {
                        if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        service.Refresh();
                        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                });
                // Persisted Tailscale login and Serve configuration survive service stops.
                for (int i = 0; i < 8; i++)
                {
                    try { await ReadNetwork(true); networkError = null; break; }
                    catch (Exception error) { networkError = error; if (error is NetworkSetupException) break; }
                    if (i < 7) await Task.Delay(1000);
                }
                }
                catch (Exception error) { networkError = error; }
                }
                Stage("Starting CLIProxy");
                await EnsureProxy();
                if (SettingsStore.Current.ApplyFleetSettings) await ApplyFleetSettingsAsync();
                Stage("Reading account limits");
                // The HTTP listener can be ready before Fleet Manager's initial
                // roster/quota refresh. Wait for that data instead of a fixed delay.
                for (int i = 0; i < 12; i++)
                {
                    await RefreshAsync(false, false);
                    if (View.Accounts.Count > 0 || View.Error.Length > 0) break;
                    await Task.Delay(1000);
                }
                if (networkError != null) throw networkError;
                Stage("SYSTEM READY");
            }
            catch (Exception error)
            {
                View.Error = Friendly(error); Stage(View.ProxyReady ? "Local proxy ready · check the network" : "Startup failed");
                Paths.Log("start failed: " + error.GetType().Name);
            }
            finally { View.Busy = false; lifecycle.Release(); Signal(); }
        }

        async Task EnsureProxy()
        {
            if (!File.Exists(Paths.ProxyExe)) throw new FileNotFoundException("CLIProxy installation not found.");
            var existing = MatchingProcesses(Paths.ProxyExe).ToArray();
            if (existing.Length > 1) throw new InvalidOperationException("Multiple CLIProxy processes are running. Close the other launcher.");
            if (existing.Length == 1) proxyProcess = existing[0];
            else
            {
                if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == SettingsStore.Current.Port))
                    throw new InvalidOperationException("The API port is already in use. Check your port or the other launcher.");
                proxyStartedHere = true; proxyProcess = new Process();
                proxyProcess.StartInfo = new ProcessStartInfo(Paths.ProxyExe, "-config \"" + Path.Combine(Paths.Proxy, "config.yaml") + "\"")
                { WorkingDirectory = Paths.Proxy, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                // Drain output without copying upstream content or credentials into app logs.
                proxyProcess.OutputDataReceived += (s, e) => { };
                proxyProcess.ErrorDataReceived += (s, e) => { };
                proxyProcess.Start(); proxyProcess.BeginOutputReadLine(); proxyProcess.BeginErrorReadLine();
                Paths.Log("proxy started");
            }
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (var response = await http.GetAsync(Paths.Api + "/"))
                        if (response.IsSuccessStatusCode) { View.ProxyReady = true; return; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                if (proxyProcess.HasExited) throw new InvalidOperationException("CLIProxy could not start. Check the configured API port.");
                await Task.Delay(500);
            }
            throw new TimeoutException("CLIProxy did not respond.");
        }

        internal static IEnumerable<Process> MatchingProcesses(string path)
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                bool matches = false;
                try { matches = String.Equals(p.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase); } catch { }
                if (matches) yield return p; else p.Dispose();
            }
        }

        internal async Task RefreshAsync(bool requestFresh, bool checkNetwork)
        {
            if (!await refresh.WaitAsync(0)) return;
            try
            {
                if (requestFresh && DateTimeOffset.UtcNow - lastQuotaRefresh > TimeSpan.FromMinutes(1))
                {
                    await Api(Paths.Fleet + "/refresh", true);
                    lastQuotaRefresh = DateTimeOffset.UtcNow;
                    await Task.Delay(1800);
                }
                if (names.Count == 0 || requestFresh)
                {
                    var roster = await Api("/v0/management/auth-files", false);
                    foreach (var item in Json.Items(Json.Get(roster, "files")))
                    {
                        var account = Json.Map(item);
                        string name = Json.Str(account, "name");
                        if (name.Length > 0) names[name] = Json.Str(account, "email");
                    }
                }
                var root = await Api(Paths.Fleet + "/status?format=json", false);
                View.Accounts = ParseAccounts(root, names);
                View.Warmup = Json.Bool(Json.Map(Json.Get(root, "settings")), "enable_reset_probe");
                View.ReadAt = DateTimeOffset.Now; View.ProxyReady = true;
                if (!View.Busy && (!SettingsStore.Current.UseTailscale || View.NetworkReady)) View.Error = "";
                if (checkNetwork && DateTimeOffset.UtcNow - lastNetworkCheck > TimeSpan.FromMinutes(2)) await ReadNetwork(false);
            }
            catch (Exception error)
            {
                View.Error = Friendly(error);
                if (!(error is AuthException))
                {
                    var proxy = MatchingProcesses(Paths.ProxyExe).ToArray();
                    View.ProxyReady = proxy.Length > 0;
                    foreach (var process in proxy) process.Dispose();
                }
                Paths.Log("quota read failed: " + error.GetType().Name);
            }
            finally { refresh.Release(); Signal(); }
        }

        internal void AllowAuthRetry() { authBlocked = false; }
        internal void ResetConfiguration()
        {
            authBlocked = false; names.Clear(); View.Accounts.Clear(); View.ReadAt = null;
            View.RemoteUrl = ""; View.NetworkReady = false; View.ProxyReady = false;
            lastQuotaRefresh = lastNetworkCheck = DateTimeOffset.MinValue;
        }

        internal async Task ApplyFleetSettingsAsync()
        {
            var settings = await Api(Paths.Fleet + "/settings", false);
            // GET returns a settings payload. Keep every unrelated field intact.
            settings["handle_enabled"] = false;
            settings["retry_enabled"] = false;
            settings["enable_reset_probe"] = SettingsStore.Current.AutoWarm;
            settings["probe_on_provisional_roster"] = false;
            settings["refresh_on_startup"] = true;
            settings["refresh_after_reset_delay"] = SettingsStore.Current.ResetDelaySeconds + "s";
            await Api(Paths.Fleet + "/settings", HttpMethod.Put, settings);
            SettingsStore.Current.ApplyFleetSettings = false; SettingsStore.Save(SettingsStore.Current);
        }

        internal async Task AddAccountAsync()
        {
            if (!View.ProxyReady) throw new InvalidOperationException("Start CLIProxy first.");
            var session = await Api("/v0/management/codex-auth-url?is_webui=true", false);
            string url = Json.Str(session, "url"), state = Json.Str(session, "state");
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target) || target.Scheme != "https" || state.Length == 0) throw new InvalidOperationException("Could not get the account sign-in link.");
            Open(url);
            for (int i = 0; i < 150; i++)
            {
                await Task.Delay(2000);
                if (!View.Desired) throw new InvalidOperationException("The system was stopped. Account sign-in was canceled.");
                var status = await Api("/v0/management/get-auth-status?state=" + Uri.EscapeDataString(state), false);
                string outcome = Json.Str(status, "status");
                if (outcome == "ok") { await RefreshAsync(true, false); return; }
                if (outcome == "error") throw new InvalidOperationException("Account sign-in failed. Please try again.");
            }
            throw new TimeoutException("Account sign-in timed out. You can try connecting again.");
        }

        internal async Task LoginTailscaleAsync()
        {
            if (!File.Exists(Paths.Tailscale)) throw new InvalidOperationException("Install the Tailscale component first.");
            await Run(Paths.Tailscale, "login --timeout=15s", 20000);
        }

        internal async Task StopAsync()
        {
            await lifecycle.WaitAsync();
            View.Desired = false; View.Busy = true; View.Error = ""; Stage("Stopping the system");
            try
            {
                await Task.Run(() =>
                {
                    foreach (var process in MatchingProcesses(Paths.ProxyExe))
                    {
                        try { if (proxyStartedHere || SettingsStore.Current.ManageExistingProxy) { process.Kill(); process.WaitForExit(5000); } } finally { process.Dispose(); }
                    }
                    if (!SettingsStore.Current.UseTailscale || !SettingsStore.Current.ExclusiveTailscale) return;
                    // Close the vendor tray UI so it cannot reconnect after the service is stopped.
                    string vendorUi = Path.Combine(Path.GetDirectoryName(Paths.Tailscale), "tailscale-ipn.exe");
                    foreach (var process in MatchingProcesses(vendorUi))
                    {
                        try { process.Kill(); process.WaitForExit(2000); } finally { process.Dispose(); }
                    }
                    using (var service = new ServiceController("Tailscale"))
                    {
                        if (service.Status != ServiceControllerStatus.Stopped)
                        {
                            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                    }
                });
                View.ProxyReady = false; View.NetworkReady = false; Stage("SYSTEM STOPPED"); Paths.Log("stack stopped");
            }
            catch (Exception error) { View.Error = Friendly(error); Stage("Shutdown failed"); Paths.Log("stop failed: " + error.GetType().Name); }
            finally { View.Busy = false; lifecycle.Release(); Signal(); }
        }

        internal static void Open(string target) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        internal static string Friendly(Exception error)
        {
            if (error is AuthException || error is FileNotFoundException || error is TimeoutException) return error.Message;
            if (error is System.ComponentModel.Win32Exception || error is UnauthorizedAccessException) return "Service permission is missing. Open Setup & components and allow the Windows administrator prompt.";
            if (error is HttpRequestException) return "Could not reach the quota service. Try Refresh shortly.";
            if (error is TaskCanceledException) return "The connection timed out. Select Refresh to try again.";
            if (error is InvalidOperationException && error.InnerException is System.ComponentModel.Win32Exception) return "Tailscale service permission is missing. Run its service setup again.";
            return error is InvalidOperationException ? error.Message : "The operation failed. Try reopening the app.";
        }
        public void Dispose() { http.Dispose(); if (proxyProcess != null) proxyProcess.Dispose(); }
    }
}
