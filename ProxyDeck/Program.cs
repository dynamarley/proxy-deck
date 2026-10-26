using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ProxyDeck
{
    internal sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Art.Card; } }
        public override Color ImageMarginGradientBegin { get { return Art.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Art.Card; } }
        public override Color ImageMarginGradientEnd { get { return Art.Card; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(40, 46, 56); } }
        public override Color MenuItemBorder { get { return Art.Edge; } }
        public override Color MenuBorder { get { return Art.Edge; } }
        public override Color SeparatorDark { get { return Art.Edge; } }
        public override Color SeparatorLight { get { return Art.Edge; } }
    }

    internal sealed class DeckContext : ApplicationContext
    {
        readonly Controller controller = new Controller();
        readonly DeckForm form;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly System.Windows.Forms.Timer timer;
        readonly EventWaitHandle showEvent;
        readonly EventWaitHandle exitEvent;
        Icon currentIcon;
        bool exiting, polling, configuring;
        DateTimeOffset lastPoll = DateTimeOffset.MinValue;
        internal DeckContext(EventWaitHandle signal, EventWaitHandle exitSignal)
        {
            showEvent = signal; exitEvent = exitSignal; form = new DeckForm(controller); MainForm = form;
            menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false, BackColor = Art.Card, ForeColor = Art.Text, Font = new Font("Segoe UI", 9.5f), Padding = new Padding(5) };
            Add("Open Proxy Deck", () => form.ShowPanel());
            AddAsync("Setup & components…", Configure);
            menu.Items.Add(new ToolStripSeparator());
            AddAsync("Start system", () => controller.StartAsync());
            AddAsync("Stop system", () => controller.StopAsync());
            menu.Items.Add(new ToolStripSeparator());
            Add("Open quota dashboard", () => form.OpenPanel());
            Add("Copy phone link", () => form.CopyRemote());
            Add("Copy management key", () => form.CopyKey());
            Add("Copy client API key", () => { try { Clipboard.SetText(controller.AccessKey()); } catch (Exception error) { MessageBox.Show(Controller.Friendly(error), "Proxy Deck"); } });
            AddAsync("Connect Codex account…", async () => { try { await controller.AddAccountAsync(); } catch (Exception error) { MessageBox.Show(Controller.Friendly(error), "Connect account"); } });
            AddAsync("Sign in to Tailscale…", async () => { try { await controller.LoginTailscaleAsync(); } catch (Exception error) { MessageBox.Show(Controller.Friendly(error), "Tailscale"); } });
            menu.Items.Add(new ToolStripSeparator());
            AddAsync("Exit and stop system", Quit);
            currentIcon = Art.Icon(null, true);
            tray = new NotifyIcon { Icon = currentIcon, Text = "Proxy Deck · starting", Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (form.Visible) form.Hide(); else { form.ShowPanel(); lastPoll = DateTimeOffset.MinValue; } } };
            form.ContextRequested += () => menu.Show(Cursor.Position);
            controller.Changed += Update;
            form.Shown += async (s, e) => { await controller.StartAsync(); lastPoll = DateTimeOffset.Now; };
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += Tick; timer.Start(); form.ShowPanel();
        }
        void Add(string label, Action action)
        {
            var item = menu.Items.Add(label); item.ForeColor = Art.Text; item.Click += (s, e) => action();
        }
        void AddAsync(string label, Func<Task> action)
        {
            var item = menu.Items.Add(label); item.ForeColor = Art.Text;
            item.Click += async (s, e) => { if (!exiting && !controller.View.Busy) await action(); };
        }
        void Update()
        {
            if (form.IsDisposed) return;
            if (form.InvokeRequired) { form.BeginInvoke(new Action(Update)); return; }
            form.UpdateView();
            var usable = controller.View.Accounts.Where(a => a.Short.Known).ToArray();
            int? percent = controller.View.Ready && usable.Length > 0 ? (int?)Math.Floor(usable.Max(a => a.Short.Remaining)) : null;
            Icon next = Art.Icon(percent, controller.View.Desired); tray.Icon = next; currentIcon.Dispose(); currentIcon = next;
            string label = controller.View.Desired ? "Proxy Deck" : "Proxy Deck · stopped";
            if (controller.View.Desired && usable.Length > 0) label += " · " + String.Join(" / ", usable.Select(a => (int)a.Short.Remaining + "%"));
            tray.Text = label.Length > 63 ? label.Substring(0, 63) : label;
        }
        async void Tick(object sender, EventArgs e)
        {
            if (exiting || configuring) return;
            if (exitEvent.WaitOne(0)) { exitEvent.Reset(); await Quit(); return; }
            if (showEvent.WaitOne(0)) { showEvent.Reset(); form.ShowPanel(); lastPoll = DateTimeOffset.MinValue; }
            if (form.Visible) form.Invalidate();
            if (polling || controller.View.Busy || !controller.View.Desired) return;
            int interval = form.Visible ? 30 : 90;
            if (DateTimeOffset.Now - lastPoll < TimeSpan.FromSeconds(interval)) return;
            polling = true; lastPoll = DateTimeOffset.Now;
            try
            {
                if (!controller.View.ProxyReady) await controller.StartAsync();
                else await controller.RefreshAsync(form.Visible, true);
            }
            finally { polling = false; }
        }
        Task Configure()
        {
            if (configuring || controller.View.Busy || polling) return Task.FromResult(0);
            configuring = true; form.Hide();
            try { using (var setup = new SetupForm(controller)) setup.ShowDialog(); }
            finally { configuring = false; form.ShowPanel(); }
            return Task.FromResult(0);
        }
        async Task Quit()
        {
            if (exiting) return; exiting = true; timer.Stop();
            tray.Text = "Proxy Deck · stopping the system";
            await controller.StopAsync();
            if (controller.View.Error.Length > 0)
            {
                // Never report a clean shutdown if the service could not be stopped.
                exiting = false; timer.Start(); form.ShowPanel(); return;
            }
            ExitThread();
        }
        protected override void ExitThreadCore()
        {
            timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); currentIcon.Dispose(); menu.Dispose();
            form.Dispose(); controller.Dispose(); base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            try
            {
                if (args.Length == 1 && args[0] == "--shutdown")
                {
                    string currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    try { using (var signal = EventWaitHandle.OpenExisting("Local\\ProxyDeckExit-" + currentSid)) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--uninstall") { AppInstall.Uninstall(); return 0; }
                if (args.Length == 2 && (args[0] == "--configure-service" || args[0] == "--install-tailscale"))
                    return ServiceSetup.Helper(args[0], args[1]).GetAwaiter().GetResult();
                if (args.Length > 0) return Diagnostics(args).GetAwaiter().GetResult();
                if (AppInstall.RelocateIfNeeded()) return 0;
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                bool first;
                using (var mutex = new Mutex(true, "Local\\ProxyDeck-" + sid, out first))
                using (var signal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\ProxyDeckShow-" + sid))
                using (var exitSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\ProxyDeckExit-" + sid))
                {
                    if (!first) { signal.Set(); return 0; }
                    if (!SettingsStore.Current.Completed)
                    {
                        using (var controller = new Controller())
                        using (var setup = new SetupForm(controller))
                            if (setup.ShowDialog() != DialogResult.OK) return 0;
                    }
                    else SettingsStore.Save(SettingsStore.Current);
                    Application.ThreadException += (s, e) => Paths.Log("UI error: " + e.Exception.GetType().Name);
                    Application.Run(new DeckContext(signal, exitSignal)); mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception error)
            {
                Paths.Log("fatal: " + error.GetType().Name);
                if (args.Length > 1 && args[0] != "--install-tailscale" && args[0] != "--configure-service") File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new { ok = false, error = error.GetType().Name }));
                else MessageBox.Show(Controller.Friendly(error), "Proxy Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        static async Task<int> Diagnostics(string[] args)
        {
            if (args.Length < 2) return 2;
            string output = Path.GetFullPath(args[1]);
            if (args[0] == "--integration-test") return await Verification.Run(output, true);
            if (args[0] == "--verify") return await Verification.Run(output, false);
            if (args[0] == "--setup-preview")
            {
                using (var model = new Controller()) using (var setup = new SetupForm(model))
                {
                    setup.ExportImage(output, 0);
                    setup.ExportImage(Path.ChangeExtension(output, ".features.png"), 1);
                    setup.ExportImage(Path.ChangeExtension(output, ".review.png"), 2);
                    setup.ExportImage(Path.ChangeExtension(output, ".accounts.png"), 4);
                }
                return 0;
            }
            if (args[0] == "--make-icon") { using (var icon = Art.Icon(null, true)) using (var file = File.Create(output)) icon.Save(file); return 0; }
            if (args[0] == "--self-test")
            {
                var fixture = Json.Parse("{\"accounts\":[{\"auth_id\":\"a\",\"available\":true,\"five_hour\":{\"remaining_percent\":62,\"reset_at\":\"2026-10-27T02:00:00+03:00\"},\"long_window\":{\"remaining_percent\":48,\"kind\":\"weekly\"}},{\"auth_id\":\"b\",\"available\":false,\"five_hour\":{\"remaining_percent\":0},\"long_window\":{\"missing\":true}},{\"auth_id\":\"c\",\"five_hour\":{\"missing\":true},\"long_window\":{\"remaining_percent\":100}}]}");
                var accounts = Controller.ParseAccounts(fixture, new Dictionary<string, string> { { "a", "someone@example.com" } });
                bool ok = accounts.Count == 3 && accounts[0].Short.Known && accounts[0].Short.Remaining == 62 && accounts[0].Short.Reset.HasValue &&
                    accounts[1].Short.Known && accounts[1].Short.Remaining == 0 && !accounts[1].Long.Known && !accounts[2].Short.Known && accounts[2].Long.Remaining == 100 &&
                    accounts[0].Email == "some…@example.com";
                File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { ok = ok, scenarios = new[] { "remaining allowance", "exhausted versus unknown", "reset timezone", "account masking", "multiple accounts" } }));
                return ok ? 0 : 1;
            }
            using (var controller = new Controller())
            {
                if (args[0] == "--lifecycle-test")
                {
                    await controller.StopAsync();
                    bool stopped;
                    using (var service = new ServiceController("Tailscale")) stopped = service.Status == ServiceControllerStatus.Stopped;
                    stopped = stopped && !Controller.MatchingProcesses(Paths.ProxyExe).Any();
                    await controller.StartAsync();
                    bool ready = controller.View.Ready && controller.View.Accounts.Count > 0 && controller.View.Error.Length == 0;
                    bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { ok = stopped && ready, elevated = elevated, stopped = stopped, restarted = ready, accounts = controller.View.Accounts.Count, proxyProcesses = Controller.MatchingProcesses(Paths.ProxyExe).Count(), error = controller.View.Error }));
                    return stopped && ready ? 0 : 1;
                }
                controller.View.Desired = true;
                await controller.RefreshAsync(false, true);
                if (args[0] == "--render-preview")
                {
                    // UI diagnostics must run on the STA thread; no service start or model request.
                    using (var form = new DeckForm(controller) { Preview = true }) form.ExportImage(output);
                    return 0;
                }
                bool healthy = controller.View.ProxyReady && (!SettingsStore.Current.UseTailscale || controller.View.NetworkReady) && controller.View.Error.Length == 0;
                File.WriteAllText(output, new JavaScriptSerializer().Serialize(new { ok = healthy, accounts = controller.View.Accounts.Count, proxyReady = controller.View.ProxyReady, networkReady = controller.View.NetworkReady, warmup = controller.View.Warmup, error = controller.View.Error }));
                return healthy ? 0 : 1;
            }
        }
    }
}
