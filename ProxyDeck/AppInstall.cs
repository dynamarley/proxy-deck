using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ProxyDeck
{
    internal static class AppInstall
    {
        internal static bool RelocateIfNeeded()
        {
            string target = Path.Combine(SettingsStore.DataRoot, "ProxyDeck.exe");
            if (String.Equals(Path.GetFullPath(Application.ExecutablePath), target, StringComparison.OrdinalIgnoreCase)) return false;
            if (Process.GetProcessesByName("ProxyDeck").Any(p => { try { return p.Id != Process.GetCurrentProcess().Id && String.Equals(p.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase); } catch { return false; } }))
            {
                MessageBox.Show("Proxy Deck is already running. To update it, select Exit and stop system from the tray menu, then open this installer again.", "Proxy Deck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            Directory.CreateDirectory(SettingsStore.DataRoot);
            using (var framework = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                if (framework == null || Convert.ToInt32(framework.GetValue("Release", 0)) < 528040)
                    throw new InvalidOperationException("Proxy Deck requires .NET Framework 4.8: https://dotnet.microsoft.com/download/dotnet-framework/net48");
            if (File.Exists(target)) File.Copy(target, target + ".previous", true);
            File.Copy(Application.ExecutablePath, target, true);
            string config = Application.ExecutablePath + ".config";
            if (File.Exists(config)) File.Copy(config, target + ".config", true);
            foreach (string document in new[] { "Restore-Autostart.ps1", "README.md", "THIRD-PARTY-NOTICES.md", "LICENSE" })
                using (var resource = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ProxyDeck." + document))
                using (var reader = new StreamReader(resource)) File.WriteAllText(Path.Combine(SettingsStore.DataRoot, document), reader.ReadToEnd(), new System.Text.UTF8Encoding(true));
            string iconPath = Path.Combine(SettingsStore.DataRoot, "ProxyDeck.ico");
            using (var icon = Art.Icon(true)) using (var file = File.Create(iconPath)) icon.Save(file);
            // COM shortcut creation keeps installation per-user without PowerShell execution policy.
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Proxy Deck.lnk"));
            shortcut.TargetPath = target; shortcut.WorkingDirectory = SettingsStore.DataRoot; shortcut.IconLocation = iconPath + ",0";
            shortcut.Description = "CLIProxy with optional Tailscale and account limits in the system tray."; shortcut.Save();
            using (var registration = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProxyDeck"))
            {
                registration.SetValue("DisplayName", "Proxy Deck"); registration.SetValue("DisplayVersion", "0.2.0");
                registration.SetValue("Publisher", "Proxy Deck contributors"); registration.SetValue("InstallLocation", SettingsStore.DataRoot);
                registration.SetValue("DisplayIcon", iconPath); registration.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                registration.SetValue("NoModify", 1, RegistryValueKind.DWord); registration.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = SettingsStore.DataRoot }); return true;
        }
        internal static void Uninstall()
        {
            string scriptPath = Path.Combine(SettingsStore.DataRoot, "Uninstall.ps1");
            using (var source = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ProxyDeck.Uninstall.ps1"))
            using (var reader = new StreamReader(source)) File.WriteAllText(scriptPath, reader.ReadToEnd(), new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
