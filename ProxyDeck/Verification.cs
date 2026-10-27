using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ProxyDeck
{
    internal static class Verification
    {
        sealed class TestFleet : HttpMessageHandler
        {
            internal readonly Dictionary<string, object> Root = Json.Parse("{\"accounts\":[],\"settings\":{\"handle_enabled\":false,\"enable_usage_feedback\":false,\"enable_reset_probe\":true,\"retry_enabled\":false,\"custom_field\":\"preserve\"}}");
            internal int Patches, Puts;
            internal bool FailPatch;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                string path = request.RequestUri.AbsolutePath;
                object result = Root;
                if (path.EndsWith("/auth-files")) result = new { files = new object[0] };
                else if (request.Method.Method == "PATCH")
                {
                    if (FailPatch) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var patch = Json.Parse(await request.Content.ReadAsStringAsync());
                    if (patch.Count != 2 || !patch.ContainsKey("auth_id") || !patch.ContainsKey("scheduler_priority")) throw new InvalidOperationException("Routing must only patch plugin priority.");
                    var account = Json.Items(Root["accounts"]).Select(Json.Map).Single(a => Json.Str(a, "auth_id") == Json.Str(patch, "auth_id"));
                    account["scheduler_priority"] = patch["scheduler_priority"]; Patches++;
                    result = new { ok = true };
                }
                else if (path.EndsWith("/settings"))
                {
                    if (request.Method == HttpMethod.Put) { Root["settings"] = Json.Parse(await request.Content.ReadAsStringAsync()); Puts++; }
                    result = Root["settings"];
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JavaScriptSerializer().Serialize(result)) };
            }
        }

        static Dictionary<string, object> WeeklyAccount(string id, double remaining)
        {
            return Json.Parse(new JavaScriptSerializer().Serialize(new { auth_id = id, scheduler_priority = 0, cpa_priority = 0, available = true,
                alias = "Keep alias", long_window = new { kind = "weekly", remaining_percent = remaining, reset_at = DateTimeOffset.UtcNow.AddDays(3).ToString("o") } }));
        }

        static async Task VerifyWeeklyRouting(List<string> passed)
        {
            var high = WeeklyAccount("high", 97); var low = WeeklyAccount("low", 40); var third = WeeklyAccount("third", 23);
            // Earlier reset used to outrank remaining percentage in Fleet's default pressure sort.
            Json.Map(low["long_window"])["reset_at"] = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
            Check(Controller.WeeklyPriority(high, DateTimeOffset.UtcNow) > Controller.WeeklyPriority(low, DateTimeOffset.UtcNow), "weekly remaining quota outranks an earlier reset", passed);
            var invalid = WeeklyAccount("invalid", 101);
            Check(Controller.WeeklyPriority(invalid, DateTimeOffset.UtcNow) == 0, "invalid quota is never treated as full", passed);
            Json.Map(invalid["long_window"])["remaining_percent"] = "NaN";
            Check(Controller.WeeklyPriority(invalid, DateTimeOffset.UtcNow) == 0, "non-finite quota cannot corrupt routing", passed);
            invalid = WeeklyAccount("invalid", 100); Json.Map(invalid["long_window"])["reset_at"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("o");
            Check(Controller.WeeklyPriority(invalid, DateTimeOffset.UtcNow) == 0, "expired quota waits for fresh evidence", passed);
            invalid = WeeklyAccount("invalid", 100); Json.Map(invalid["long_window"])["kind"] = "monthly";
            Check(Controller.WeeklyPriority(invalid, DateTimeOffset.UtcNow) == 0, "monthly windows are not mislabeled as weekly", passed);
            var fake = new TestFleet(); fake.Root["accounts"] = new object[] { high, low, third };
            SettingsStore.Current.PreferWeeklyQuota = true;
            using (var model = new Controller(handler: fake))
            {
                await model.RefreshAsync(false, false);
                var settings = Json.Map(fake.Root["settings"]);
                Check(model.View.Error.Length == 0 && fake.Patches == 3 && fake.Puts == 1 && Json.Bool(settings, "handle_enabled"), "quota poll patches every account before enabling Fleet routing", passed);
                Check(Json.Bool(settings, "enable_reset_probe") && !Json.Bool(settings, "retry_enabled") && Json.Str(settings, "custom_field") == "preserve" && Json.Str(high, "alias") == "Keep alias" && Json.Str(high, "cpa_priority") == "0", "routing preserves warmup, retries, account metadata and CPA priority", passed);
                await model.RefreshAsync(false, false);
                Check(fake.Patches == 3 && fake.Puts == 1, "unchanged quota polling performs no writes", passed);
                Json.Map(high["long_window"])["remaining_percent"] = 30;
                await model.RefreshAsync(false, false);
                Check(fake.Patches == 4 && Convert.ToInt32(low["scheduler_priority"]) > Convert.ToInt32(high["scheduler_priority"]), "quota changes automatically switch priority to the fuller account", passed);
                Json.Map(low["long_window"])["remaining_percent"] = 10;
                await model.RefreshAsync(false, false, false);
                Check(fake.Patches == 4, "diagnostic quota reads never update routing", passed);
                SettingsStore.Current.PreferWeeklyQuota = false;
                await model.RefreshAsync(false, false);
                Check(fake.Patches == 4, "routing opt-out stops automatic priority updates", passed);
                await model.ApplyFleetSettingsAsync();
                Check(!Json.Bool(Json.Map(fake.Root["settings"]), "handle_enabled"), "applying routing opt-out disables the Fleet scheduler", passed);
                SettingsStore.Current.PreferWeeklyQuota = true;
                fake.FailPatch = true;
                await model.RefreshAsync(false, false);
                Check(model.View.Error.Length > 0 && !Json.Bool(Json.Map(fake.Root["settings"]), "handle_enabled"), "failed priority writes do not enable routing with incomplete priorities", passed);
            }
            SettingsStore.Current.PreferWeeklyQuota = false;
        }

        // A separate tailnet cannot be provisioned in CI. Exercise the real controller
        // and settings persistence with a stateful Tailscale boundary, never the host service.
        sealed class TestNetwork
        {
            internal bool Owned, ForeignRoot, FailRemove, FailStart, Running = true;
            internal int Stops, PendingStatuses;
            internal readonly List<string> Commands = new List<string>();
            internal Task Service(bool running)
            {
                if (running && FailStart) throw new InvalidOperationException("Simulated service start failure.");
                Running = running; if (!running) Stops++;
                return Task.FromResult(0);
            }
            internal Task<string> Run(string exe, string args, int timeout)
            {
                Commands.Add(args);
                if (args == "status --json")
                {
                    string state = PendingStatuses > 0 ? "Starting" : Running ? "Running" : "Stopped";
                    if (PendingStatuses > 0) PendingStatuses--;
                    return Task.FromResult("{\"BackendState\":\"" + state + "\",\"Self\":{\"DNSName\":\"test-node.example.ts.net.\"}}");
                }
                if (args == "serve status --json")
                {
                    var handlers = new Dictionary<string, object> { { "/other", new { Proxy = "http://127.0.0.1:9999" } } };
                    if (Owned || ForeignRoot) handlers["/"] = new { Proxy = ForeignRoot ? "http://127.0.0.1:9998" : Paths.Api };
                    var web = new Dictionary<string, object> { { "test-node.example.ts.net:" + SettingsStore.Current.Port, new { Handlers = handlers } } };
                    return Task.FromResult(new JavaScriptSerializer().Serialize(new { Web = web }));
                }
                if (args.EndsWith(" --set-path=/ off", StringComparison.Ordinal))
                {
                    if (FailRemove) throw new InvalidOperationException("Simulated Serve update failure.");
                    Owned = false; return Task.FromResult("");
                }
                if (args == "serve --bg --yes --https=" + SettingsStore.Current.Port + " " + Paths.Api) { Owned = true; return Task.FromResult(""); }
                throw new InvalidOperationException("Unexpected Tailscale command in verification.");
            }
        }
        static void Check(bool result, string scenario, List<string> passed)
        { if (!result) throw new InvalidOperationException("Verification failed: " + scenario); passed.Add(scenario); }
        internal static async Task<int> Run(string output, bool integration)
        {
            var passed = new List<string>(); string error = "";
            string originalRoot = SettingsStore.DataRoot; var original = SettingsStore.Current;
            string testRoot = Path.Combine(Path.GetTempPath(), "ProxyDeck-tests-" + Guid.NewGuid().ToString("N"));
            Controller controller = null;
            try
            {
                SettingsStore.DataRoot = testRoot; SettingsStore.Current = new AppSettings { ProxyDirectory = Path.Combine(testRoot, "CLI Proxy Unicode \u03A9") };
                Directory.CreateDirectory(testRoot);
                string sample = "config-version: 8\nserver:\n  host: \"localhost\"\n  port: 1234\nplugins:\n  enabled: false\n  configs:\n    other:\n      enabled: true\n# end\n";
                string edited = ConfigEditor.Set(sample, new[] { "plugins", "configs", "codex-fleet-manager", "enabled" }, "true");
                edited = ConfigEditor.Set(edited, new[] { "plugins", "enabled" }, "true");
                Check(edited.Contains("    other:\n      enabled: true") && edited.Contains("    codex-fleet-manager:\n      enabled: true") && edited.Contains("  port: 1234"), "preserves unrelated YAML and adds nested plugin settings", passed);
                Check(ConfigEditor.Set(edited, new[] { "plugins", "enabled" }, "true") == edited, "configuration edit is idempotent", passed);
                bool rejected = false;
                try { ConfigEditor.Set("plugins: {enabled: false}\n", new[] { "plugins", "enabled" }, "true"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "unsupported flow YAML rejected before writes", passed);
                rejected = false;
                try { ConfigEditor.Set("plugins:\n  enabled: false\n  enabled: true\n", new[] { "plugins", "enabled" }, "true"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "ambiguous duplicate YAML keys rejected", passed);
                string samePort = "config-version: 8\r\nserver:\r\n  port: 8317 # keep this comment\r\n";
                Check(Provisioner.ConfigureExistingPort(samePort, 8317) == samePort, "unchanged v8 port preserves inline comments and line endings", passed);
                string legacy = "port: 8317 # existing API\nplugins:\n  enabled: true\n";
                Check(Provisioner.ConfigureExistingPort(legacy, 8317) == legacy, "matching legacy port leaves the configuration unchanged", passed);
                rejected = false;
                try { Provisioner.ConfigureExistingPort(legacy, 8318); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "legacy port mismatch is rejected before installation changes", passed);
                rejected = false;
                try { Provisioner.ConfigureExistingPort("port: {invalid: 8317}\n", 8317); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "unreadable legacy port is rejected without guessing", passed);
                rejected = false;
                try { Downloads.SafeEntry(testRoot, "../outside.exe"); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "archive traversal blocked", passed);
                string secret = SettingsStore.NewKey(), protectedKey = SettingsStore.Protect(secret);
                Check(protectedKey != secret && SettingsStore.Unprotect(protectedKey) == secret, "management key encrypted with per-user DPAPI", passed);
                var options = SettingsStore.Current;
                options.ProtectedManagementKey = protectedKey;
                await VerifyWeeklyRouting(passed);
                SettingsStore.Save(options); Check(!SettingsStore.Load().UseTailscale && !SettingsStore.Load().AutoWarm, "fresh installation defaults to local mode and no warmup", passed);
                string generated = Provisioner.NewConfig(options, secret, SettingsStore.NewKey(), Path.Combine(options.ProxyDirectory, "plugins"));
                Check(generated.Contains("allow-remote: false") && generated.Contains("enable_reset_probe: false") && generated.Contains("handle_enabled: false"), "new proxy starts loopback-only with passive Fleet settings", passed);
                File.WriteAllText(Path.Combine(testRoot, "tampered.zip"), "tampered");
                Check(!Downloads.Verify(Path.Combine(testRoot, "tampered.zip"), Catalog.Fleet.Sha256), "tampered dependency rejected", passed);
                rejected = false;
                try { await ServiceSetup.VerifySignature(Path.Combine(testRoot, "tampered.zip")); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "unsigned Tailscale installer rejected before elevation or execution", passed);
                if (File.Exists(Paths.Tailscale)) { await ServiceSetup.VerifySignature(Paths.Tailscale); Check(true, "official installed Tailscale publisher recognized", passed); }
                if (integration)
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); options.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                    await Provisioner.InstallAsync(options, s => { }, CancellationToken.None);
                    SettingsStore.Save(options);
                    Check(File.Exists(Path.Combine(options.ProxyDirectory, "cli-proxy-api.exe")), "official CLIProxy download verified and installed in a Unicode path", passed);
                    var network = new TestNetwork();
                    controller = new Controller(network.Run, network.Service); await controller.StartAsync();
                    Check(controller.View.Ready && controller.View.Error.Length == 0 && controller.View.Accounts.Count == 0, "clean local proxy starts without Tailscale or logged-in accounts", passed);
                    using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.ManagementKey());
                        var settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(!Json.Bool(settings, "enable_reset_probe") && !Json.Bool(settings, "handle_enabled") && !Json.Bool(settings, "retry_enabled"), "actual Fleet settings keep warmup and routing disabled", passed);
                        await controller.SetWarmupAsync(true);
                        settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(Json.Bool(settings, "enable_reset_probe") && Json.Str(settings, "refresh_after_reset_delay") == "1m0s" && !Json.Bool(settings, "probe_on_provisional_roster"), "opt-in warmup applies recommended reset delay through the actual API", passed);
                        Check(controller.View.Warmup && SettingsStore.Load().AutoWarm, "Auto Warm toggle persists its confirmed enabled state", passed);
                        var valid = SettingsStore.Current.Copy(); var invalid = valid.Copy(); invalid.ProtectedManagementKey = SettingsStore.Protect(SettingsStore.NewKey()); SettingsStore.Save(invalid);
                        rejected = false;
                        try { await controller.SetWarmupAsync(false); } catch (AuthException) { rejected = true; }
                        Check(rejected && controller.View.Warmup && SettingsStore.Load().AutoWarm && !controller.View.Busy, "failed Auto Warm request does not persist a false off state or leave controls busy", passed);
                        SettingsStore.Save(valid); controller.AllowAuthRetry();
                        await controller.SetWarmupAsync(false);
                        settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(!Json.Bool(settings, "enable_reset_probe") && !SettingsStore.Load().AutoWarm, "warmup can be disabled and the choice survives settings reload", passed);
                        int proxyId = Controller.MatchingProcesses(Paths.ProxyExe).Select(process => { int id = process.Id; process.Dispose(); return id; }).Single();
                        network.PendingStatuses = 2; await controller.SetTailscaleAsync(true);
                        Check(SettingsStore.Load().UseTailscale && controller.View.NetworkReady && network.Owned && network.PendingStatuses == 0, "Tailscale toggle waits for backend startup before confirming phone access", passed);
                        await controller.SetTailscaleAsync(false);
                        Check(!SettingsStore.Load().UseTailscale && !network.Owned && network.Stops == 0 && network.Running, "shared Tailscale toggle removes only app access and leaves the service running", passed);
                        options = SettingsStore.Current; options.ExclusiveTailscale = true; SettingsStore.Save(options);
                        await controller.SetTailscaleAsync(true); await controller.SetTailscaleAsync(false);
                        Check(network.Stops == 1 && !network.Running && SettingsStore.Load().ExclusiveTailscale, "exclusive Tailscale toggle stops the service and preserves the on-demand preference", passed);
                        options = SettingsStore.Current; options.ExclusiveTailscale = false; SettingsStore.Save(options);
                        await controller.SetTailscaleAsync(true); network.FailRemove = true; rejected = false;
                        try { await controller.SetTailscaleAsync(false); } catch (InvalidOperationException) { rejected = true; }
                        Check(rejected && SettingsStore.Load().UseTailscale && network.Owned && !controller.View.Busy, "failed Tailscale disable does not claim or persist that access is off", passed);
                        network.FailRemove = false; await controller.SetTailscaleAsync(false);
                        network.FailStart = true; rejected = false;
                        try { await controller.SetTailscaleAsync(true); } catch (InvalidOperationException) { rejected = true; }
                        Check(rejected && !SettingsStore.Load().UseTailscale && !controller.View.Busy, "failed Tailscale enable preserves the previous disabled preference", passed);
                        network.FailStart = false; network.ForeignRoot = true; rejected = false;
                        try { await controller.SetTailscaleAsync(true); } catch (NetworkSetupException) { rejected = true; }
                        Check(rejected && network.ForeignRoot && !network.Owned && !SettingsStore.Load().UseTailscale, "Tailscale enable refuses to replace another app's root handler", passed);
                        network.ForeignRoot = false;
                        using (var response = await client.GetAsync(Paths.Api + "/")) Check(response.IsSuccessStatusCode && Controller.MatchingProcesses(Paths.ProxyExe).Select(process => { int id = process.Id; process.Dispose(); return id; }).Single() == proxyId, "network toggles keep the same local proxy process and API running", passed);
                        Check(!network.Commands.Any(value => value.Contains("reset") || value.StartsWith("down", StringComparison.Ordinal)), "network toggles never reset Serve or disconnect the shared tailnet", passed);
                    }
                    options = SettingsStore.Current;
                    await controller.StopAsync();
                    Check(!Controller.MatchingProcesses(Paths.ProxyExe).Any(), "owned proxy stops cleanly", passed);
                    string config = Path.Combine(options.ProxyDirectory, "config.yaml");
                    File.WriteAllText(config, ConfigEditor.Set(File.ReadAllText(config), new[] { "plugins", "enabled" }, "false") + "\n# keep-custom-marker\n");
                    string before = File.ReadAllText(config);
                    int oldPort = options.Port;
                    do
                    {
                        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                        options.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                    } while (options.Port == oldPort);
                    await Provisioner.InstallAsync(options, s => { }, CancellationToken.None);
                    Check(File.ReadAllText(config).Contains("# keep-custom-marker") && Directory.GetFiles(options.ProxyDirectory, "config.yaml.proxydeck-*.bak").Length > 0, "reinstall preserves existing configuration and creates a backup", passed);
                    Check(ConfigEditor.Read(File.ReadAllText(config), "server", "port") == options.Port.ToString(), "existing v8 configuration uses the newly selected API port", passed);
                    Check(Directory.GetFiles(options.ProxyDirectory, "config.yaml.proxydeck-*.bak").Any(file => File.ReadAllText(file) == before), "port change preserves the exact previous configuration in a backup", passed);
                    SettingsStore.Save(options); controller.ResetConfiguration();
                    await controller.StartAsync(); Check(controller.View.Ready, "repaired installation restarts on the newly selected API port", passed); await controller.StopAsync();
                }
            }
            catch (Exception failure) { error = failure.GetType().Name + ": " + Controller.Friendly(failure); }
            if (controller != null) { await controller.StopAsync(); controller.Dispose(); }
            SettingsStore.DataRoot = originalRoot; SettingsStore.Current = original;
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { ok = error.Length == 0, integration = integration, passed = passed, error = error, testDirectory = testRoot }));
            return error.Length == 0 ? 0 : 1;
        }
    }
}
