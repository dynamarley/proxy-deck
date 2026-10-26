using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ProxyDeck
{
    internal sealed class Dependency
    {
        internal string Name, Version, Url, Sha256, FileName;
    }
    internal static class Catalog
    {
        internal static readonly Dependency Proxy = new Dependency {
            Name = "CLIProxyAPI", Version = "8.0.44", FileName = "cliproxy.zip",
            Url = "https://github.com/router-for-me/CLIProxyAPI/releases/download/v8.0.44/CLIProxyAPI_8.0.44_windows_amd64.zip",
            Sha256 = "fad909f3b1ef873dbf5484c586d87db61576dc353cbc6e7cb9262a1770e77e2e" };
        internal static readonly Dependency Fleet = new Dependency {
            Name = "Codex Fleet Manager", Version = "0.4.3", FileName = "fleet.zip",
            Url = "https://github.com/doer-ee/cpa-plugin-codex-fleet-manager/releases/download/v0.4.3/codex-fleet-manager_0.4.3_windows_amd64.zip",
            Sha256 = "c6c69be9f7407a166aeae6c342a6ae54db480c4261694479a7a97aae38277160" };
    }
    internal static class Downloads
    {
        internal static bool Verify(string file, string expected)
        {
            using (var hash = SHA256.Create()) using (var stream = File.OpenRead(file))
                return String.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase);
        }
        internal static async Task<string> Get(Dependency item, string cache, Action<string> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(cache);
            string file = Path.Combine(cache, item.Sha256 + ".zip");
            if (File.Exists(file) && Verify(file, item.Sha256)) return file;
            progress(item.Name + " " + item.Version + " downloading");
            string pending = file + ".download";
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5); client.DefaultRequestHeaders.UserAgent.ParseAdd("ProxyDeck/0.2");
                using (var response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = File.Create(pending))
                    {
                        byte[] buffer = new byte[81920]; long total = 0; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancel)) > 0)
                        {
                            total += read; if (total > 256L * 1024 * 1024) throw new InvalidDataException("The download exceeds the expected size limit.");
                            await output.WriteAsync(buffer, 0, read, cancel);
                        }
                    }
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (!Verify(pending, item.Sha256)) { File.Delete(pending); throw new InvalidDataException(item.Name + " SHA-256 verification failed. Nothing was installed."); }
            if (File.Exists(file)) File.Delete(file);
            File.Move(pending, file); return file;
        }
        internal static string SafeEntry(string destination, string name)
        {
            string root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
            string path = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || name.Contains(":")) throw new InvalidDataException("Invalid archive path.");
            return path;
        }
        internal static void Extract(string archive, string destination)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    string path = SafeEntry(destination, entry.FullName);
                    total += entry.Length; if (total > 512L * 1024 * 1024) throw new InvalidDataException("The archive is too large.");
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path, true);
                }
            }
        }
    }
    internal static class Provisioner
    {
        internal static string NewConfig(AppSettings options, string managementKey, string accessKey, string pluginsDirectory)
        {
            Func<string, string> quote = s => new JavaScriptSerializer().Serialize(s);
            return "# Created by Proxy Deck. Bind to loopback; remote access only through Tailscale.\nconfig-version: 8\nserver:\n  host: \"127.0.0.1\"\n  port: " + options.Port +
                "\n  trusted-proxies: [\"127.0.0.1\", \"::1\"]\nmanagement:\n  allow-remote: " + options.UseTailscale.ToString().ToLowerInvariant() +
                "\n  secret-key: " + quote(managementKey) + "\naccess:\n  api-keys:\n    - " + quote(accessKey) +
                "\noauth:\n  auth-dir: " + quote(Path.Combine(options.ProxyDirectory, "auths").Replace('\\', '/')) +
                "\nplugins:\n  enabled: true\n  dir: " + quote(pluginsDirectory.Replace('\\', '/')) +
                "\n  configs:\n    codex-fleet-manager:\n      enabled: true\n      priority: 1\n      handle_enabled: false\n      retry_enabled: false\n      enable_reset_probe: false\n      probe_on_provisional_roster: false\n";
        }
        internal static async Task InstallAsync(AppSettings options, Action<string> progress, CancellationToken cancel)
        {
            options.Validate();
            if (Environment.Is64BitOperatingSystem == false || !String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "AMD64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This package requires Windows x64. ARM64 and 32-bit Windows are not supported yet.");
            string root = options.ProxyDirectory, executable = Path.Combine(root, "cli-proxy-api.exe"), config = Path.Combine(root, "config.yaml");
            bool existing = File.Exists(executable);
            if (Controller.MatchingProcesses(executable).Any()) throw new InvalidOperationException("Stop this CLIProxy process before applying settings. You can use Stop system in the tray menu.");
            if (existing && !File.Exists(config)) throw new InvalidOperationException("The selected folder does not contain config.yaml.");
            if (!existing && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Choose an empty folder for a new CLIProxy installation. Existing files were not overwritten.");
            string cache = Path.Combine(SettingsStore.DataRoot, "downloads");
            string proxyZip = existing ? null : await Downloads.Get(Catalog.Proxy, cache, progress, cancel);
            string fleetZip = await Downloads.Get(Catalog.Fleet, cache, progress, cancel);
            cancel.ThrowIfCancellationRequested();
            // Stage and verify all downloaded code before modifying a user's config.
            string stage = Path.Combine(cache, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string sibling = null;
            try
            {
            Downloads.Extract(fleetZip, Path.Combine(stage, "fleet"));
            string library = Directory.GetFiles(Path.Combine(stage, "fleet"), "*.dll", SearchOption.AllDirectories).Single(p => Path.GetFileName(p).StartsWith("codex-fleet-manager", StringComparison.Ordinal));
            string pluginRoot = Path.Combine(root, "plugins"), pendingConfig = null, originalConfig = null;
            if (!existing)
            {
                Downloads.Extract(proxyZip, Path.Combine(stage, "proxy"));
                string stagedExe = Directory.GetFiles(Path.Combine(stage, "proxy"), "cli-proxy-api.exe", SearchOption.AllDirectories).Single();
                progress("Installing CLIProxy");
                Directory.CreateDirectory(Path.Combine(stage, "install"));
                foreach (string file in Directory.GetFiles(Path.GetDirectoryName(stagedExe))) File.Copy(file, Path.Combine(stage, "install", Path.GetFileName(file)), false);
                string management = SettingsStore.NewKey(), access = SettingsStore.NewKey();
                options.ProtectedManagementKey = SettingsStore.Protect(management); options.ProtectedAccessKey = SettingsStore.Protect(access);
                options.ManagedInstall = true; options.ManageExistingProxy = true; options.InstalledProxyVersion = Catalog.Proxy.Version;
                // Persist recovery keys before starting CPA, which hashes its management key.
                options.Completed = false; SettingsStore.Save(options);
                SettingsStore.AtomicWrite(Path.Combine(stage, "install", "config.yaml"), NewConfig(options, management, access, pluginRoot));
                Directory.CreateDirectory(Path.Combine(stage, "install", "auths"));
            }
            else
            {
                progress("Backing up existing settings");
                string yaml = File.ReadAllText(config), edited = yaml;
                originalConfig = yaml;
                // Require the v8 layout for network edits; legacy layouts need explicit migration.
                if (options.UseTailscale && !System.Text.RegularExpressions.Regex.IsMatch(yaml, @"(?m)^config-version:[ \t]*8[ \t]*(?:#.*)?\r?$"))
                    throw new InvalidOperationException("Automatic Tailscale setup requires the CLIProxy v8 configuration format. Migrate your existing configuration first.");
                if (options.UseTailscale)
                {
                    edited = ConfigEditor.Set(edited, new[] { "server", "host" }, "\"127.0.0.1\"");
                    edited = ConfigEditor.Set(edited, new[] { "server", "trusted-proxies" }, "[\"127.0.0.1\", \"::1\"]");
                    edited = ConfigEditor.Set(edited, new[] { "management", "allow-remote" }, "true");
                }
                // Preserve custom plugin directories. Read just this scalar, without YAML guessing.
                string raw = ConfigEditor.Read(yaml, "plugins", "dir");
                if (raw.Length > 0)
                {
                    if (raw.StartsWith("\"")) raw = new JavaScriptSerializer().Deserialize<string>(raw);
                    else if (raw.StartsWith("'")) raw = raw.Trim('\'').Replace("''", "'");
                    else if (raw.Contains("#") || raw.Contains("{") || raw.Contains("&") || raw.Contains("*")) throw new InvalidOperationException("plugins.dir must be a simple folder path.");
                    if (raw.StartsWith("~/") || raw.StartsWith("~\\")) raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw.Substring(2));
                    pluginRoot = Path.IsPathRooted(raw) ? raw : Path.Combine(root, raw);
                }
                edited = ConfigEditor.Set(edited, new[] { "plugins", "enabled" }, "true");
                edited = ConfigEditor.Set(edited, new[] { "plugins", "configs", "codex-fleet-manager", "enabled" }, "true");
                edited = ConfigEditor.Set(edited, new[] { "plugins", "configs", "codex-fleet-manager", "handle_enabled" }, "false");
                edited = ConfigEditor.Set(edited, new[] { "plugins", "configs", "codex-fleet-manager", "enable_reset_probe" }, "false");
                if (edited != yaml) pendingConfig = edited;
            }
            // A filename pinned to a version lets CPA select deterministically.
            string destination = existing ? Path.Combine(pluginRoot, "windows", "amd64") : Path.Combine(stage, "install", "plugins", "windows", "amd64"); Directory.CreateDirectory(destination);
            string target = Path.Combine(destination, "codex-fleet-manager-v" + Catalog.Fleet.Version + ".dll");
            if (File.Exists(target) && !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(library)))
                File.Copy(target, target + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak", false);
            if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(library))) File.Copy(library, target, true);
            foreach (string notice in Directory.GetFiles(Path.Combine(stage, "fleet"), "*LICENSE*", SearchOption.AllDirectories)) File.Copy(notice, Path.Combine(destination, "FleetManager-" + Path.GetFileName(notice)), true);
            if (pendingConfig != null)
            {
                if (File.ReadAllText(config) != originalConfig) throw new InvalidOperationException("The CLIProxy configuration changed during setup. Please try again.");
                File.Copy(config, config + ".proxydeck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".bak", false);
                SettingsStore.AtomicWrite(config, pendingConfig);
            }
            if (!existing)
            {
                // Assemble next to the destination, then atomically rename on that volume.
                sibling = root + ".proxydeck-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(sibling);
                string assembled = Path.Combine(stage, "install");
                foreach (string folder in Directory.GetDirectories(assembled, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(sibling, folder.Substring(assembled.Length + 1)));
                foreach (string file in Directory.GetFiles(assembled, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(sibling, file.Substring(assembled.Length + 1)), false);
                if (Directory.Exists(root)) Directory.Delete(root, false); // only an empty user-selected directory
                Directory.Move(sibling, root); sibling = null;
            }
            options.InstalledFleetVersion = Catalog.Fleet.Version; options.ApplyFleetSettings = true;
            progress("Core components are ready");
            }
            finally
            {
                // Both paths are exact, generated GUID directories; never recursively delete root.
                if (Path.GetFullPath(stage).StartsWith(Path.GetFullPath(cache).TrimEnd('\\') + "\\stage-", StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(stage, true); } catch { }
                if (sibling != null && Path.GetFullPath(sibling).StartsWith(Path.GetFullPath(root) + ".proxydeck-", StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(sibling, true); } catch { }
            }
        }
        internal static async Task InstallTailscale(Action<string> progress, CancellationToken cancel)
        {
            if (File.Exists(Paths.Tailscale)) return;
            progress("Preparing the official Tailscale Windows installer");
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                var metadata = Json.Parse(await client.GetStringAsync("https://pkgs.tailscale.com/stable/?mode=json"));
                string name = Json.Str(Json.Map(Json.Get(metadata, "MSIs")), "amd64");
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^tailscale-setup-\d+\.\d+\.\d+-amd64\.msi$")) throw new InvalidDataException("Could not validate the Tailscale package metadata.");
                string file = Path.Combine(SettingsStore.DataRoot, "downloads", name);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (var input = await client.GetStreamAsync("https://pkgs.tailscale.com/stable/" + name))
                using (var output = File.Create(file)) await input.CopyToAsync(output, 81920, cancel);
                cancel.ThrowIfCancellationRequested();
                // Authenticode is checked by the elevated helper before msiexec can run.
                await ServiceSetup.Elevate("--install-tailscale", file);
                if (!File.Exists(Paths.Tailscale)) throw new InvalidOperationException("Tailscale installation did not complete. You can try again.");
            }
        }
    }
}
