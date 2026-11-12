// Runs the embedded page's actual data functions without a browser or live keys.
const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const html = fs.readFileSync(require('node:path').join(__dirname, 'PhoneDashboard.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const usage = { five_hour: { utilization: 12 }, seven_day: { utilization: 41 },
 limits: [{ kind: 'weekly_scoped', percent: 25, scope: { model: { display_name: 'Fable' } } }],
 cedar_ember: { eligible: true, grants: [
  { resets_left: 1, resets_total: 1, paused: false, gives_back_weekly: true, usable_now: false },
  { resets_left: 1, resets_total: 1, paused: false, gives_back_weekly: false }
 ] } };
let calls = [];
const context = vm.createContext({console, AbortController, setTimeout, clearTimeout,
 document: { getElementById: () => ({ value: '', addEventListener() {} }) },
 sessionStorage: { getItem() { return null; } },
 fetch: async (url, options) => { calls.push({url, options}); return { ok: true, status: 200, json: async () => ({status_code: 200, body: JSON.stringify(usage)}) }; }
});
vm.runInContext(script, context);
(async () => {
 assert.equal(calls.length, 0, 'opening the phone page never requests quota automatically');
 const accounts = await vm.runInContext('loadAccounts({}, [{provider:"claude",auth_index:"fixture"}])', context);
 assert.equal(accounts[0].fable?.remaining, 75, 'Fable weekly remains distinct from overall weekly');
 assert.equal(accounts[0].long.remaining, 59);
 assert.equal(accounts[0].manualResets, 2, 'saved grants count even before a limit is exhausted');
 assert.equal(accounts[0].fableResets, 1, 'only Fable grants count as Fable resets');
 const request = JSON.parse(calls[0].options.body);
 assert.equal(request.method, 'GET'); assert.equal(request.url, 'https://api.anthropic.com/api/oauth/usage?cedar_ember=1');
 assert.equal(request.header['User-Agent'], 'claude-cli/2.1.280 (external, cli)');
 assert.equal(vm.runInContext('claudeResetCount({},true)', context), null);
 assert.equal(vm.runInContext('claudeResetCount({cedar_ember:{eligible:true,grants:[]}},true)', context), 0);
 assert.equal(vm.runInContext('fableWindow({seven_day_fable:{utilization:20}}).remaining', context), 80);
 assert.equal(vm.runInContext('claudeResetCount({cedar_ember:{eligible:true,grants:[{resets_left:-1,paused:false}]}},true)', context), null);
 console.log(JSON.stringify({ok:true, checks:12}));
})().catch(error => {console.error(error.message);process.exitCode=1;});
