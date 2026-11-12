using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ProxyDeck
{
    internal static class ClaudeVerification
    {
        sealed class Fake
        {
            internal Dictionary<string, object> Usage = Json.Parse("{\"five_hour\":{\"utilization\":0,\"resets_at\":null},\"seven_day\":{\"utilization\":20}}");
            internal int Generations, UsageReads;
            internal bool FailGeneration, RetryChain, MissingModel;
            internal object ForcePrefix = true;
            internal string CredentialPrefix = ClaudeWarmup.Prefix;
            internal string UsageUrl, UsageAgent;
            internal Task<Dictionary<string, object>> Send(string path, HttpMethod method, object payload, bool access)
            {
                if (path == "/v0/management/auth-files")
                    return Task.FromResult(Json.Parse("{\"files\":[{\"name\":\"claude-test.json\",\"auth_index\":\"test\",\"provider\":\"claude\",\"request_retry\":0}]}"));
                if (path == Paths.Fleet + "/settings")
                    return Task.FromResult(new Dictionary<string, object> { { "retry_enabled", RetryChain } });
                if (path == "/v0/management/force-model-prefix")
                    return Task.FromResult(new Dictionary<string, object> { { "force-model-prefix", ForcePrefix } });
                if (path == "/v0/management/auth-files/download?name=claude-test.json")
                    return Task.FromResult(new Dictionary<string, object> { { "type", "claude" }, { "prefix", CredentialPrefix }, { "request_retry", 0 } });
                if (path == "/v1/models")
                    return Task.FromResult(Json.Parse(MissingModel ? "{\"data\":[]}" : "{\"data\":[{\"id\":\"manual-claude/claude-opus-5-5\",\"owned_by\":\"anthropic\"}]}"));
                if (path == "/v0/management/api-call")
                {
                    var body = Json.Parse(new JavaScriptSerializer().Serialize(payload));
                    UsageUrl = Json.Str(body, "url"); UsageAgent = Json.Str(Json.Map(Json.Get(body, "header")), "User-Agent");
                    if (access || (UsageUrl != "https://api.anthropic.com/api/oauth/usage" && UsageUrl != "https://api.anthropic.com/api/oauth/usage?cedar_ember=1") || Json.Str(body, "method") != "GET") throw new Exception("Wrong quota route.");
                    UsageReads++;
                    return Task.FromResult(new Dictionary<string, object> { { "status_code", 200 }, { "body", new JavaScriptSerializer().Serialize(Usage) } });
                }
                if (path == "/v1/messages")
                {
                    var body = Json.Parse(new JavaScriptSerializer().Serialize(payload));
                    if (!access || method != HttpMethod.Post || Json.Str(body, "model") != ClaudeWarmup.Model ||
                        Json.Str(Json.Map(Json.Get(body, "output_config")), "effort") != "high" || Json.Str(body, "max_tokens") != "32")
                        throw new Exception("Wrong warmup model/effort.");
                    Generations++;
                    if (FailGeneration) throw new TimeoutException();
                    return Task.FromResult(new Dictionary<string, object>());
                }
                throw new Exception("Unexpected Claude route.");
            }
        }
        internal static async Task Run(string root, List<string> passed)
        {
            Action<bool, string, List<string>> check = (ok, label, results) => { if (!ok) throw new InvalidOperationException(label); results.Add(label); };
            var now = DateTimeOffset.UtcNow;
            var fake = new Fake();
            check(ClaudeWarmup.Due(fake.Usage, now, 60), "Claude inactive 5-hour window is eligible for one warmup", passed);
            Json.Map(fake.Usage["five_hour"])["resets_at"] = now.AddHours(3).ToString("o");
            check(!ClaudeWarmup.Due(fake.Usage, now, 60), "Claude active window at zero usage is not warmed again", passed);
            Json.Map(fake.Usage["five_hour"])["resets_at"] = now.AddSeconds(-30).ToString("o");
            check(!ClaudeWarmup.Due(fake.Usage, now, 60) && ClaudeWarmup.Due(fake.Usage, now.AddSeconds(31), 60), "Claude warmup respects the delay after reset", passed);
            Json.Map(fake.Usage["seven_day"])["utilization"] = 100;
            check(!ClaudeWarmup.Due(fake.Usage, now.AddMinutes(5), 60), "Claude exhausted weekly quota blocks warmup and extra usage", passed);
            fake.Usage["seven_day"] = null;
            check(!ClaudeWarmup.Due(fake.Usage, now.AddMinutes(5), 60), "Claude unknown weekly quota cannot trigger generation", passed);
            fake = new Fake(); fake.Usage["seven_day_opus"] = new { utilization = 100 };
            // Convert to the same dictionary representation as a JSON response.
            fake.Usage = Json.Parse(new JavaScriptSerializer().Serialize(fake.Usage));
            check(!ClaudeWarmup.Due(fake.Usage, now, 60), "Claude exhausted Opus-specific quota blocks warmup", passed);
            fake = new Fake();
            string path = Path.Combine(root, "claude-state.json");
            var worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(false, 60, now);
            check(fake.Generations == 0 && worker.Account != null && !File.Exists(path), "Claude Auto Warm off and read-only checks only read quota", passed);
            int reads = fake.UsageReads;
            await worker.Tick(false, 60, now.AddSeconds(1), true);
            check(fake.UsageReads == reads + 1 && fake.Generations == 0, "explicit Claude refresh bypasses quota cache without enabling warmup", passed);
            await worker.Tick(true, 60, now.AddMinutes(6));
            check(fake.Generations == 1 && File.Exists(path), "Claude warmup uses only explicit Opus 5.5 high and reserves its attempt", passed);
            await worker.Tick(true, 60, now.AddMinutes(12));
            worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now.AddMinutes(18));
            check(fake.Generations == 1, "Claude unverified idle window is not retried after polling or restart", passed);
            Json.Map(fake.Usage["five_hour"])["resets_at"] = now.AddMinutes(35).ToString("o");
            await worker.Tick(true, 60, now.AddMinutes(24));
            Json.Map(fake.Usage["five_hour"])["resets_at"] = null;
            await worker.Tick(true, 60, now.AddMinutes(30));
            check(fake.Generations == 1, "Claude transient missing reset cannot reactivate a known active window", passed);
            await worker.Tick(true, 60, now.AddMinutes(40));
            check(fake.Generations == 2, "Claude observing a new window permits one later idle-window activation", passed);
            fake = new Fake { FailGeneration = true };
            path = Path.Combine(root, "claude-timeout.json"); worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now);
            worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now.AddMinutes(20));
            check(fake.Generations == 1, "Claude ambiguous generation timeout never causes an automatic retry", passed);
            fake = new Fake { FailGeneration = true };
            Json.Map(fake.Usage["five_hour"])["resets_at"] = now.AddMinutes(-1).ToString("o");
            path = Path.Combine(root, "claude-timeout-boundary.json"); worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now);
            Json.Map(fake.Usage["five_hour"])["resets_at"] = null;
            worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now.AddMinutes(20));
            check(fake.Generations == 1, "Claude expired-to-idle usage cannot repeat an ambiguous attempt after restart", passed);
            fake = new Fake { FailGeneration = true };
            path = Path.Combine(root, "claude-timeout-idle.json"); worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now);
            Json.Map(fake.Usage["five_hour"])["resets_at"] = now.AddMinutes(-1).ToString("o");
            worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now.AddMinutes(20));
            check(fake.Generations == 1, "Claude idle-to-expired usage cannot repeat an ambiguous attempt after restart", passed);
            fake = new Fake();
            path = Path.Combine(root, "claude-legacy-idle.json");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(new { attempted_window = "claude-test.json:idle:", attempted_at = now.ToString("o") }));
            worker = new ClaudeWarmup(path, fake.Send);
            await worker.Tick(true, 60, now.AddMinutes(20));
            check(fake.Generations == 0, "existing durable idle reservations remain effective after upgrading", passed);
            fake = new Fake { RetryChain = true };
            worker = new ClaudeWarmup(Path.Combine(root, "claude-chain.json"), fake.Send);
            await worker.Tick(true, 60, now);
            check(fake.Generations == 0, "Claude warmup refuses an enabled model fallback chain", passed);
            foreach (object force in new object[] { false, null, "true" })
            {
                fake = new Fake { ForcePrefix = force };
                path = Path.Combine(root, "claude-prefix-" + Guid.NewGuid().ToString("N") + ".json");
                worker = new ClaudeWarmup(path, fake.Send);
                await worker.Tick(true, 60, now);
                check(fake.Generations == 0, "Claude warmup refuses disabled or unknown strict-prefix enforcement", passed);
            }
            fake = new Fake { CredentialPrefix = "other-account" };
            worker = new ClaudeWarmup(Path.Combine(root, "claude-wrong-prefix.json"), fake.Send);
            await worker.Tick(true, 60, now);
            check(fake.Generations == 0, "Claude warmup verifies the selected credential prefix even when the model catalog matches", passed);
            fake = new Fake { MissingModel = true };
            worker = new ClaudeWarmup(Path.Combine(root, "claude-model.json"), fake.Send);
            await worker.Tick(true, 60, now);
            check(fake.Generations == 0, "Claude missing explicit model never falls back to another model", passed);
            fake = new Fake();
            var boundary = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
            Json.Map(fake.Usage["five_hour"])["resets_at"] = boundary.ToString("o");
            worker = new ClaudeWarmup(Path.Combine(root, "claude-jitter.json"), fake.Send);
            await worker.Tick(true, 60, boundary.AddMinutes(10));
            Json.Map(fake.Usage["five_hour"])["resets_at"] = boundary.AddMilliseconds(500).ToString("o");
            await worker.Tick(true, 60, boundary.AddMinutes(20));
            check(fake.Generations == 1, "Claude fractional reset timestamp jitter cannot duplicate a warmup", passed);
            fake = new Fake();
            fake.Usage = Json.Parse("{\"five_hour\":{\"utilization\":12},\"seven_day\":{\"utilization\":41},\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":25,\"resets_at\":\"2030-01-08T10:00:00Z\",\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}],\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"resets_left\":1,\"resets_total\":1,\"paused\":false,\"gives_back_weekly\":true,\"usable_now\":false},{\"resets_left\":1,\"resets_total\":1,\"paused\":false,\"gives_back_weekly\":false}]}}");
            worker = new ClaudeWarmup(Path.Combine(root, "claude-fable.json"), fake.Send);
            await worker.Tick(false, 60, boundary);
            check(worker.Account.SupportsManualResets && worker.Account.ManualResets == 2,
                "Claude displays saved reset rights even when redemption is not currently usable", passed);
            check(fake.UsageUrl == "https://api.anthropic.com/api/oauth/usage?cedar_ember=1" && fake.UsageAgent == "claude-cli/2.1.280 (external, cli)",
                "Claude usage requests include reset inventory and a supported client identity", passed);
            check(fake.Generations == 0, "Fable quota and reset inventory reads never generate or redeem", passed);
            check(worker.Account.FableWeekly != null && worker.Account.FableWeekly.Known && worker.Account.FableWeekly.Remaining == 75 &&
                worker.Account.Long.Remaining == 59 && worker.Account.FableManualResets == 1,
                "Fable weekly quota and its reset rights remain distinct from overall Claude limits", passed);
            var displayed = worker.Account.Copy();
            fake.Usage["cedar_ember"] = Json.Parse("{\"eligible\":true,\"grants\":[]}");
            foreach (var limit in Json.Items(fake.Usage["limits"])) Json.Map(limit)["percent"] = 55;
            await worker.Tick(false, 60, boundary.AddSeconds(1), true);
            check(displayed.FableWeekly.Remaining == 75 && displayed.FableManualResets == 1 && displayed.ManualResets == 2 &&
                worker.Account.FableWeekly.Remaining == 45 && worker.Account.FableManualResets == 0,
                "background Claude updates cannot mutate the last copied Fable snapshot", passed);
            var legacy = ClaudeUsage.Parse(new Dictionary<string, object>(), Json.Parse("{\"seven_day_fable\":{\"utilization\":20}}"), boundary);
            check(legacy.FableWeekly.Remaining == 80 && !legacy.ManualResets.HasValue && !legacy.FableManualResets.HasValue,
                "legacy Fable windows remain readable and missing reset inventory stays unknown", passed);
            var active = Json.Parse("{\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":10,\"scope\":{\"model\":{\"display_name\":\"Fable\"}}},{\"kind\":\"weekly_scoped\",\"percent\":60,\"is_active\":true,\"scope\":{\"model\":{\"display_name\":\"Fable 5\"}}}]}");
            check(ClaudeUsage.FableWindow(active).Remaining == 40, "active Fable weekly scope takes precedence over inactive duplicates", passed);
            check(!ClaudeUsage.FableWindow(Json.Parse("{\"limits\":[{\"kind\":\"weekly_scoped\",\"percent\":120,\"scope\":{\"model\":{\"display_name\":\"Fable\"}}}]}" )).Known &&
                !ClaudeUsage.FableWindow(Json.Parse("{\"seven_day_fable\":{\"utilization\":-1}}")).Known,
                "malformed Fable percentages are never shown as a valid remaining quota", passed);
            var inventory = Json.Parse("{\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"resets_left\":1,\"paused\":false,\"gives_back_weekly\":true,\"ends_at\":\"2029-12-31T00:00:00Z\"},{\"resets_left\":2,\"paused\":true,\"gives_back_weekly\":true},{\"resets_left\":3,\"paused\":false,\"gives_back_weekly\":true,\"starts_at\":\"2030-01-02T00:00:00Z\"},{\"resets_left\":1,\"paused\":false,\"gives_back_weekly\":false}]}}");
            check(ClaudeUsage.Resets(inventory, boundary, false) == 1 && ClaudeUsage.Resets(inventory, boundary, true) == 0,
                "expired, paused and future grants do not inflate the available reset inventory", passed);
            check(!ClaudeUsage.Resets(Json.Parse("{\"cedar_ember\":{\"eligible\":true,\"grants\":[{\"resets_left\":-1,\"paused\":false}]}}"), boundary, true).HasValue,
                "malformed reset grants remain unknown instead of claiming zero rights", passed);
            foreach (object classification in new object[] { null, "true" })
            {
                var grant = new Dictionary<string, object> { { "resets_left", 2 }, { "paused", false }, { "gives_back_weekly", classification } };
                var unknown = Json.Parse(new JavaScriptSerializer().Serialize(new { cedar_ember = new { eligible = true, grants = new[] { grant } } }));
                check(ClaudeUsage.Resets(unknown, boundary, false) == 2 && !ClaudeUsage.Resets(unknown, boundary, true).HasValue,
                    "unknown Fable grant classification preserves the known total without claiming zero Fable resets", passed);
            }
        }
    }
}
