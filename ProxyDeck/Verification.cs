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
                rejected = false;
                try { Downloads.SafeEntry(testRoot, "../outside.exe"); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "archive traversal blocked", passed);
                string secret = SettingsStore.NewKey(), protectedKey = SettingsStore.Protect(secret);
                Check(protectedKey != secret && SettingsStore.Unprotect(protectedKey) == secret, "management key encrypted with per-user DPAPI", passed);
                var options = SettingsStore.Current;
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
                    controller = new Controller(); await controller.StartAsync();
                    Check(controller.View.Ready && controller.View.Error.Length == 0 && controller.View.Accounts.Count == 0, "clean local proxy starts without Tailscale or logged-in accounts", passed);
                    using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }))
                    {
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", controller.ManagementKey());
                        var settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(!Json.Bool(settings, "enable_reset_probe") && !Json.Bool(settings, "handle_enabled") && !Json.Bool(settings, "retry_enabled"), "actual Fleet settings keep warmup and routing disabled", passed);
                        options.AutoWarm = true; options.ApplyFleetSettings = true; await controller.ApplyFleetSettingsAsync();
                        settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(Json.Bool(settings, "enable_reset_probe") && Json.Str(settings, "refresh_after_reset_delay") == "1m0s" && !Json.Bool(settings, "probe_on_provisional_roster"), "opt-in warmup applies recommended reset delay through the actual API", passed);
                        options.AutoWarm = false; options.ApplyFleetSettings = true; await controller.ApplyFleetSettingsAsync();
                        settings = Json.Parse(await client.GetStringAsync(Paths.Api + Paths.Fleet + "/settings"));
                        Check(!Json.Bool(settings, "enable_reset_probe"), "warmup can be disabled again", passed);
                    }
                    await controller.StopAsync();
                    Check(!Controller.MatchingProcesses(Paths.ProxyExe).Any(), "owned proxy stops cleanly", passed);
                    string config = Path.Combine(options.ProxyDirectory, "config.yaml");
                    File.WriteAllText(config, ConfigEditor.Set(File.ReadAllText(config), new[] { "plugins", "enabled" }, "false") + "\n# keep-custom-marker\n");
                    string before = File.ReadAllText(config);
                    await Provisioner.InstallAsync(options, s => { }, CancellationToken.None);
                    Check(File.ReadAllText(config).Contains("# keep-custom-marker") && Directory.GetFiles(options.ProxyDirectory, "config.yaml.proxydeck-*.bak").Length > 0, "reinstall preserves existing configuration and creates a backup", passed);
                    await controller.StartAsync(); Check(controller.View.Ready, "repaired installation restarts", passed); await controller.StopAsync();
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
