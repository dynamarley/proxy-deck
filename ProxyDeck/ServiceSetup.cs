using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.ServiceProcess;

namespace ProxyDeck
{
    internal static class ServiceSetup
    {
        internal static async Task Elevate(string mode, string argument)
        {
            using (var process = Process.Start(new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,
                mode + " \"" + argument.Replace("\"", "") + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
            {
                await Task.Run(() => process.WaitForExit());
                if (process.ExitCode != 0) throw new InvalidOperationException("Tailscale setup did not complete. Check administrator access and your connection.");
            }
        }
        internal static async Task Configure(bool exclusive)
        {
            string request = Path.Combine(SettingsStore.DataRoot, "service-request.json"); Directory.CreateDirectory(SettingsStore.DataRoot);
            File.WriteAllText(request, new JavaScriptSerializer().Serialize(new { sid = WindowsIdentity.GetCurrent().User.Value, exclusive = exclusive }));
            await Elevate("--configure-service", request);
        }
        internal static Task VerifySignature(string file)
        {
            string escaped = file.Replace("'", "''");
            string script = "$s=Get-AuthenticodeSignature -LiteralPath '" + escaped + "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Tailscale Inc\\.?[, ]'){exit 1}; exit 0";
            return Controller.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), 30000);
        }
        internal static async Task<int> Helper(string mode, string argument)
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return 1;
            if (mode == "--install-tailscale")
            {
                string file = Path.GetFullPath(argument);
                string allowed = Path.GetFullPath(Path.Combine(SettingsStore.DataRoot, "downloads")) + "\\";
                if (!file.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"^tailscale-setup-\d+\.\d+\.\d+-amd64\.msi$")) return 1;
                using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                await VerifySignature(file);
                using (var install = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
                    "/i \"" + file + "\" /passive /norestart") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    await Task.Run(() => install.WaitForExit());
                    if (install.ExitCode != 0 && install.ExitCode != 3010) return 1;
                }
                }
                return 0;
            }
            if (mode != "--configure-service" || Path.GetFullPath(argument) != Path.Combine(SettingsStore.DataRoot, "service-request.json")) return 1;
            var input = Json.Parse(File.ReadAllText(argument));
            var sid = new SecurityIdentifier(Json.Str(input, "sid"));
            // Elevation must be for this same desktop user, never silently another account.
            if (!sid.Equals(WindowsIdentity.GetCurrent().User)) throw new InvalidOperationException("Approve elevation using the same Windows account.");
            bool exclusive = Json.Bool(input, "exclusive");
            string sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            string sddl = (await Controller.Run(sc, "sdshow Tailscale", 10000)).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).First(s => s.StartsWith("D:") || s.StartsWith("O:"));
            string backup = Path.Combine(SettingsStore.DataRoot, "tailscale-service-backup.json");
            using (var service = new ServiceController("Tailscale"))
            {
                if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            if (!File.Exists(backup))
            {
                int start;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tailscale")) start = Convert.ToInt32(key.GetValue("Start"));
                var prefs = Json.Parse(await Controller.Run(Paths.Tailscale, "debug prefs", 10000));
                SettingsStore.AtomicWrite(backup, new JavaScriptSerializer().Serialize(new { sddl = sddl, start = start, unattended = Json.Bool(prefs, "ForceDaemon") }));
            }
            var descriptor = new RawSecurityDescriptor(sddl); const int mask = 0x20034;
            bool granted = descriptor.DiscretionaryAcl.Cast<GenericAce>().OfType<CommonAce>().Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier.Equals(sid) && (a.AccessMask & mask) == mask);
            if (!granted)
            {
                descriptor.DiscretionaryAcl.InsertAce(descriptor.DiscretionaryAcl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, mask, sid, false, null));
                await Controller.Run(sc, "sdset Tailscale \"" + descriptor.GetSddlForm(AccessControlSections.Access) + "\"", 10000);
            }
            await Controller.Run(Paths.Tailscale, "set --unattended=true", 10000);
            if (exclusive)
            {
                await Controller.Run(sc, "config Tailscale start= demand", 10000);
                // Preserve the vendor startup shortcut so the user's on-demand choice survives sign-in.
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                string link = Path.Combine(startup, "Tailscale.lnk"), saved = Path.Combine(SettingsStore.DataRoot, "startup-backup", "Tailscale.lnk");
                if (File.Exists(link) && !File.Exists(saved)) { Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.Move(link, saved); }
            }
            return 0;
        }
    }
}
