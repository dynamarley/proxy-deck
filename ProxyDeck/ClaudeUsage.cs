using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ProxyDeck
{
    internal static class ClaudeUsage
    {
        internal static Quota Window(Dictionary<string, object> data, string kind, string percentKey = "utilization")
        {
            double used; DateTimeOffset reset;
            bool known = Double.TryParse(Json.Str(data, percentKey), NumberStyles.Float, CultureInfo.InvariantCulture, out used) &&
                !Double.IsNaN(used) && !Double.IsInfinity(used) && used >= 0 && used <= 100;
            return new Quota { Known = known, Remaining = known ? 100 - used : 0, Kind = kind,
                Reset = DateTimeOffset.TryParse(Json.Str(data, "resets_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out reset) ? reset : (DateTimeOffset?)null };
        }
        internal static Quota FableWindow(Dictionary<string, object> usage)
        {
            var scoped = Json.Items(Json.Get(usage, "limits")).Select(Json.Map).Where(limit => {
                var model = Json.Map(Json.Get(Json.Map(Json.Get(limit, "scope")), "model"));
                string label = Json.Str(model, "display_name").Trim();
                return Json.Str(limit, "kind") == "weekly_scoped" &&
                    (label.Equals("Fable", StringComparison.OrdinalIgnoreCase) || label.Equals("Fable 5", StringComparison.OrdinalIgnoreCase) ||
                    Json.Str(model, "id").StartsWith("claude-fable-", StringComparison.OrdinalIgnoreCase));
            }).OrderByDescending(limit => Json.Bool(limit, "is_active")).ToList();
            foreach (var limit in scoped)
            {
                var quota = Window(limit, "weekly", "percent"); if (quota.Known) return quota;
            }
            var legacy = Json.Get(usage, "seven_day_fable");
            return legacy != null ? Window(Json.Map(legacy), "weekly") : scoped.Count > 0 ? new Quota { Kind = "weekly" } : null;
        }
        internal static int? Resets(Dictionary<string, object> usage, DateTimeOffset now, bool fableOnly)
        {
            var status = Json.Map(Json.Get(usage, "cedar_ember"));
            if (!(Json.Get(status, "eligible") is bool)) return null;
            if (!Json.Bool(status, "eligible")) return 0;
            object records = Json.Get(status, "grants");
            if (!(records is System.Collections.IList)) return null;
            long total = 0;
            var seen = new HashSet<string>();
            foreach (var item in Json.Items(records))
            {
                var grant = Json.Map(item); int left, earned;
                string id = Json.Str(grant, "id");
                if (id.Length > 0 && !seen.Add(id)) return null;
                if (!Int32.TryParse(Json.Str(grant, "resets_left"), out left) || left < 0 || !(Json.Get(grant, "paused") is bool)) return null;
                if (Json.Get(grant, "resets_total") != null && (!Int32.TryParse(Json.Str(grant, "resets_total"), out earned) || earned < left)) return null;
                bool available = !Json.Bool(grant, "paused") && left > 0;
                foreach (string key in new[] { "starts_at", "ends_at" })
                {
                    if (Json.Get(grant, key) == null) continue;
                    DateTimeOffset date;
                    if (!DateTimeOffset.TryParse(Json.Str(grant, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date)) return null;
                    if (key == "starts_at" ? now < date : now >= date) available = false;
                }
                if (available && fableOnly && !(Json.Get(grant, "gives_back_weekly") is bool)) return null;
                if (available && (!fableOnly || Json.Bool(grant, "gives_back_weekly"))) total += left;
                if (total > Int32.MaxValue) return null;
            }
            return (int)total;
        }
        internal static Account Parse(Dictionary<string, object> auth, Dictionary<string, object> usage, DateTimeOffset now)
        {
            var shortWindow = Window(Json.Map(Json.Get(usage, "five_hour")), "five_hour");
            var longWindow = Window(Json.Map(Json.Get(usage, "seven_day")), "weekly");
            return new Account { Id = Json.Str(auth, "name"), Name = "Claude", Email = "Manual use only", Short = shortWindow, Long = longWindow,
                FableWeekly = FableWindow(usage), SupportsManualResets = true, ManualResets = Resets(usage, now, false), FableManualResets = Resets(usage, now, true),
                Available = shortWindow.Known && longWindow.Known && shortWindow.Remaining > 0 && longWindow.Remaining > 0 };
        }
    }
}
