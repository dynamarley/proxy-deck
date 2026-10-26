using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ProxyDeck
{
    public sealed class AppSettings
    {
        public int Schema = 2;
        public bool Completed;
        public string ProxyDirectory = "";
        public int Port = 8317;
        public bool UseTailscale;
        public bool ExclusiveTailscale;
        public bool ManageExistingProxy;
        public bool ManagedInstall;
        public bool AutoWarm;
        public int ResetDelaySeconds = 60;
        public bool ApplyFleetSettings;
        public string ProtectedManagementKey = "";
        public string ProtectedAccessKey = "";
        public string InstalledProxyVersion = "";
        public string InstalledFleetVersion = "";

        public AppSettings Copy() { return new JavaScriptSerializer().Deserialize<AppSettings>(new JavaScriptSerializer().Serialize(this)); }
        internal void Validate()
        {
            if (Schema != 2) throw new InvalidOperationException("This settings file requires a newer version of Proxy Deck.");
            if (Port < 1024 || Port > 65535) throw new InvalidOperationException("The port must be between 1024 and 65535.");
            if (ResetDelaySeconds < 60 || ResetDelaySeconds > 900) throw new InvalidOperationException("The warmup delay must be between 1 and 15 minutes.");
            if (String.IsNullOrWhiteSpace(ProxyDirectory) || !Path.IsPathRooted(ProxyDirectory)) throw new InvalidOperationException("Choose a valid CLIProxy folder.");
            ProxyDirectory = Path.GetFullPath(ProxyDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.GetParent(ProxyDirectory) == null) throw new InvalidOperationException("Choose a subfolder for CLIProxy instead of a drive root.");
        }
    }

    internal static class SettingsStore
    {
        internal static string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxyDeck");
        internal static string FilePath { get { return Path.Combine(DataRoot, "settings.json"); } }
        static AppSettings current;
        internal static AppSettings Current { get { return current ?? (current = Load()); } set { current = value; } }
        internal static string DefaultProxy { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIProxyAPI"); } }
        internal static AppSettings Load()
        {
            if (File.Exists(FilePath))
            {
                try { var settings = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath)); settings.Validate(); return settings; }
                catch { throw new InvalidOperationException("Could not read Proxy Deck settings. Back up settings.json and run setup again."); }
            }
            var result = new AppSettings { ProxyDirectory = DefaultProxy };
            // Migrate only the previous Proxy Deck installation, never arbitrary CPA users.
            if (File.Exists(Path.Combine(DataRoot, "setup-backup.json")) && File.Exists(Path.Combine(DefaultProxy, "local-credentials.json")))
            {
                result.Completed = true; result.UseTailscale = true; result.ExclusiveTailscale = true;
                result.ManageExistingProxy = true; result.AutoWarm = true;
            }
            return result;
        }
        internal static void Save(AppSettings settings)
        {
            settings.Validate(); Directory.CreateDirectory(DataRoot);
            AtomicWrite(FilePath, new JavaScriptSerializer().Serialize(settings));
            Current = settings;
        }
        internal static void AtomicWrite(string path, string text)
        {
            string pending = path + ".pending";
            File.WriteAllText(pending, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(pending, path, path + ".bak"); else File.Move(pending, path);
        }
        internal static string Protect(string value)
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        internal static string Unprotect(string value)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        internal static string NewKey()
        {
            byte[] data = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(data);
            return "pd-" + BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }
    }

    // Narrow block-mapping editor: no general YAML deserialization and no guessing
    // around anchors/flow maps. Unsupported files fail before any bytes are changed.
    internal static class ConfigEditor
    {
        internal static string Read(string yaml, params string[] path)
        {
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n'); int start = 0, end = lines.Length, indent = 0;
            for (int depth = 0; depth < path.Length; depth++)
            {
                int found = -1;
                for (int i = start; i < end; i++)
                {
                    string t = lines[i].TrimStart(' ');
                    if (lines[i].Length - t.Length == indent && t.StartsWith(path[depth] + ":", StringComparison.Ordinal))
                    { if (found >= 0) throw new InvalidOperationException("Duplicate configuration key: " + path[depth]); found = i; }
                }
                if (found < 0) return "";
                string tail = lines[found].TrimStart(' ').Substring(path[depth].Length + 1).Trim();
                if (depth == path.Length - 1) return tail;
                if (tail.Length > 0 && !tail.StartsWith("#")) throw new InvalidOperationException("Standard block-style YAML configuration is required.");
                start = found + 1;
                int blockEnd = start;
                while (blockEnd < end) { string t = lines[blockEnd].TrimStart(' '); if (t.Length > 0 && !t.StartsWith("#") && lines[blockEnd].Length - t.Length <= indent) break; blockEnd++; }
                end = blockEnd; indent += 2;
                for (int i = start; i < end; i++) { string t = lines[i].TrimStart(' '); if (t.Length > 0 && !t.StartsWith("#")) { indent = lines[i].Length - t.Length; break; } }
            }
            return "";
        }
        internal static string Set(string yaml, string[] path, string value)
        {
            var lines = new List<string>(yaml.Replace("\r\n", "\n").Split('\n'));
            SetAt(lines, 0, lines.Count, 0, path, 0, value);
            return String.Join("\n", lines);
        }
        static void SetAt(List<string> lines, int start, int end, int indent, string[] path, int depth, string value)
        {
            int found = -1;
            for (int i = start; i < end; i++)
            {
                string trimmed = lines[i].TrimStart(' ');
                if (trimmed.StartsWith("#") || trimmed.Length == 0) continue;
                if (lines[i].Contains("\t") || trimmed == "---" || trimmed.StartsWith("<<:")) throw new InvalidOperationException("This YAML layout cannot be edited automatically. Use standard block-style settings.");
                if (lines[i].Length - trimmed.Length == indent && trimmed.StartsWith(path[depth] + ":", StringComparison.Ordinal))
                { if (found >= 0) throw new InvalidOperationException("Duplicate setting in configuration: " + path[depth]); found = i; }
            }
            if (found < 0)
            {
                int index = end;
                string prefix = new string(' ', indent) + path[depth] + ":";
                lines.Insert(index, prefix + (depth == path.Length - 1 ? " " + value : ""));
                if (depth < path.Length - 1) SetAt(lines, index + 1, index + 1, indent + 2, path, depth + 1, value);
                return;
            }
            if (depth == path.Length - 1) { lines[found] = new string(' ', indent) + path[depth] + ": " + value; return; }
            string tail = lines[found].Trim().Substring(path[depth].Length + 1).Trim();
            if (tail.Length > 0 && !tail.StartsWith("#")) throw new InvalidOperationException("Nested settings must use block-style YAML: " + path[depth]);
            int blockEnd = found + 1;
            while (blockEnd < end)
            {
                string t = lines[blockEnd].TrimStart(' ');
                if (t.Length > 0 && !t.StartsWith("#") && lines[blockEnd].Length - t.Length <= indent) break;
                blockEnd++;
            }
            int childIndent = indent + 2;
            for (int i = found + 1; i < blockEnd; i++)
            { string t = lines[i].TrimStart(' '); if (t.Length > 0 && !t.StartsWith("#")) { childIndent = lines[i].Length - t.Length; break; } }
            SetAt(lines, found + 1, blockEnd, childIndent, path, depth + 1, value);
        }
    }
}
