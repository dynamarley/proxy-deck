using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ProxyDeck
{
    // A separate opt-in worker. It never supplies an account to Codex routing.
    internal sealed class ClaudeWarmup
    {
        internal const string Prefix = "manual-claude";
        internal const string Model = Prefix + "/claude-opus-5-5(high)";
        readonly Func<string, HttpMethod, object, bool, Task<Dictionary<string, object>>> send;
        readonly string statePath;
        DateTimeOffset nextCheck;
        internal Account Account;
        string status = "Waiting for Claude quota";
        internal string Status
        {
            get { return status; }
            private set { if (status != value) { status = value; Paths.Log("Claude: " + value); } }
        }
        internal ClaudeWarmup(string path, Func<string, HttpMethod, object, bool, Task<Dictionary<string, object>>> request)
        { statePath = path; send = request; }

        internal static bool Utilization(Dictionary<string, object> window, out double used)
        {
            return Double.TryParse(Json.Str(window, "utilization"), NumberStyles.Float, CultureInfo.InvariantCulture, out used) &&
                !Double.IsNaN(used) && !Double.IsInfinity(used) && used >= 0 && used <= 100;
        }
        static DateTimeOffset? Reset(Dictionary<string, object> window)
        {
            DateTimeOffset value;
            return DateTimeOffset.TryParse(Json.Str(window, "resets_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) ? value : (DateTimeOffset?)null;
        }
        static string WindowKey(string name, DateTimeOffset? reset)
        {
            // Usage responses can jitter by fractions of a second for the same
            // boundary. Round to the nearest minute to keep one durable window ID.
            return name + ":" + (reset.HasValue ? ((reset.Value.ToUnixTimeSeconds() + 30) / 60).ToString(CultureInfo.InvariantCulture) : "");
        }
        internal static bool Due(Dictionary<string, object> usage, DateTimeOffset now, int delay)
        {
            double five, week, opus;
            var shortWindow = Json.Map(Json.Get(usage, "five_hour"));
            var longWindow = Json.Map(Json.Get(usage, "seven_day"));
            if (!Utilization(shortWindow, out five) || !Utilization(longWindow, out week) || week >= 100) return false;
            var opusWindow = Json.Map(Json.Get(usage, "seven_day_opus"));
            if (Utilization(opusWindow, out opus) && opus >= 100) return false;
            var reset = Reset(shortWindow);
            return reset.HasValue ? now >= reset.Value.AddSeconds(delay) : five == 0;
        }
        async Task<Dictionary<string, object>> ReadUsage(string index)
        {
            var result = await send("/v0/management/api-call", HttpMethod.Post, new {
                auth_index = index, method = "GET", url = "https://api.anthropic.com/api/oauth/usage?cedar_ember=1",
                header = new Dictionary<string, string> { { "Authorization", "Bearer $TOKEN$" }, { "anthropic-beta", "oauth-2025-04-20" }, { "anthropic-version", "2023-06-01" }, { "User-Agent", "claude-cli/2.1.280 (external, cli)" } }
            }, false);
            if (Json.Str(result, "status_code") != "200") throw new InvalidOperationException("Claude quota unavailable.");
            return Json.Parse(Json.Str(result, "body"));
        }
        internal async Task Tick(bool enabled, int resetDelay, DateTimeOffset now, bool forceRead = false)
        {
            if (!forceRead && now < nextCheck) return;
            nextCheck = now.AddMinutes(5);
            try
            {
                var roster = await send("/v0/management/auth-files", HttpMethod.Get, null, false);
                var matches = Json.Items(Json.Get(roster, "files")).Select(Json.Map).Where(a =>
                    Json.Str(a, "provider") == "claude" && !Json.Bool(a, "disabled")).ToList();
                if (matches.Count != 1) { Account = null; Status = "Claude warmup requires one enabled Claude account."; return; }
                var auth = matches[0]; string index = Json.Str(auth, "auth_index");
                if (index.Length == 0) return;
                var usage = await ReadUsage(index);
                Account = ClaudeUsage.Parse(auth, usage, now);
                Status = enabled ? "Claude Auto Warm is on." : "Claude Auto Warm is off.";
                var window = Json.Map(Json.Get(usage, "five_hour"));
                if (!enabled) return;
                var state = File.Exists(statePath) ? Json.Parse(File.ReadAllText(statePath)) : new Dictionary<string, object>();
                string observed = WindowKey(Json.Str(auth, "name"), Reset(window));
                if (Reset(window).HasValue && Json.Str(state, "observed_window") != observed)
                {
                    state["observed_window"] = observed;
                    state["observed_reset"] = Reset(window).Value.ToUniversalTime().ToString("o");
                    state["observed_auth"] = Json.Str(auth, "name");
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                    SettingsStore.AtomicWrite(statePath, new JavaScriptSerializer().Serialize(state));
                }
                if (!Due(usage, now, resetDelay)) return;
                DateTimeOffset knownReset;
                if (!Reset(window).HasValue && Json.Str(state, "observed_auth") == Json.Str(auth, "name") &&
                    DateTimeOffset.TryParse(Json.Str(state, "observed_reset"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out knownReset) &&
                    now < knownReset.AddSeconds(resetDelay)) return;
                string previousWindow = Json.Str(state, "observed_auth") == Json.Str(auth, "name") ? Json.Str(state, "observed_window") : "";
                string key = Reset(window).HasValue ? observed : previousWindow.Length > 0 ? previousWindow : Json.Str(auth, "name") + ":idle";
                if (Json.Str(state, "attempted_window") == key ||
                    Json.Str(state, "attempted_window") == Json.Str(auth, "name") + ":idle:" + previousWindow) return;
                // An idle response and a subsequently reported stale boundary can
                // describe the same window. Only a boundary after our reservation
                // is evidence that another attempt belongs to a new window.
                DateTimeOffset attemptedAt, observedReset;
                if (Json.Str(state, "attempted_window").StartsWith(Json.Str(auth, "name") + ":", StringComparison.Ordinal) &&
                    DateTimeOffset.TryParse(Json.Str(state, "attempted_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out attemptedAt) &&
                    DateTimeOffset.TryParse(Json.Str(state, "observed_reset"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out observedReset) &&
                    attemptedAt >= observedReset) return;
                var fleet = await send(Paths.Fleet + "/settings", HttpMethod.Get, null, false);
                if (Json.Bool(fleet, "retry_enabled")) { Status = "Disable model retry chains before Claude warmup."; return; }
                if (Json.Str(auth, "request_retry") != "0") { Status = "Set Claude credential request_retry to zero before warmup."; return; }
                var enforcement = await send("/v0/management/force-model-prefix", HttpMethod.Get, null, false);
                if (!Json.Bool(enforcement, "force-model-prefix")) { Status = "Enable force-model-prefix before Claude warmup."; return; }
                // The pinned CLIProxy version omits prefix from the roster. Read
                // the selected file without persisting or logging its contents.
                var credential = await send("/v0/management/auth-files/download?name=" + Uri.EscapeDataString(Json.Str(auth, "name")), HttpMethod.Get, null, false);
                if (Json.Str(credential, "type") != "claude" || Json.Str(credential, "prefix") != Prefix || Json.Str(credential, "request_retry") != "0")
                { Status = "Set the Claude credential prefix to manual-claude and request_retry to zero."; return; }
                var catalog = await send("/v1/models", HttpMethod.Get, null, true);
                if (!Json.Items(Json.Get(catalog, "data")).Select(Json.Map).Any(m => Json.Str(m, "id") == Prefix + "/claude-opus-5-5" && Json.Str(m, "owned_by") == "anthropic"))
                { Status = "The manual-claude Opus 5.5 model is unavailable."; return; }
                // Reserve on disk before sending. An ambiguous timeout, process crash
                // or unverified response cannot cause another request in this window.
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                state["attempted_window"] = key; state["attempted_at"] = now.ToString("o");
                SettingsStore.AtomicWrite(statePath, new JavaScriptSerializer().Serialize(state));
                await send("/v1/messages", HttpMethod.Post, new {
                    model = Model, max_tokens = 32, stream = false,
                    thinking = new { type = "adaptive" }, output_config = new { effort = "high" },
                    messages = new[] { new { role = "user", content = "Reply only OK." } }
                }, true);
                usage = await ReadUsage(index);
                Account = ClaudeUsage.Parse(auth, usage, now);
                var verifiedReset = Reset(Json.Map(Json.Get(usage, "five_hour")));
                if (verifiedReset.HasValue)
                {
                    state["observed_window"] = WindowKey(Json.Str(auth, "name"), verifiedReset);
                    state["observed_reset"] = verifiedReset.Value.ToUniversalTime().ToString("o");
                    state["observed_auth"] = Json.Str(auth, "name");
                    SettingsStore.AtomicWrite(statePath, new JavaScriptSerializer().Serialize(state));
                }
                Status = Reset(Json.Map(Json.Get(usage, "five_hour"))).HasValue ? "Claude quota window is active." : "Claude warmup sent; window not confirmed.";
                Paths.Log("Claude warmup completed: " + Status);
            }
            catch (Exception error)
            {
                Status = "Claude quota/warmup failed; no automatic generation retry.";
                nextCheck = now.AddMinutes(15);
                if (Account != null) Account.Available = false;
                Paths.Log("Claude warmup: " + error.GetType().Name);
            }
        }
    }
}
