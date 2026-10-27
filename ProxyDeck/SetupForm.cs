using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProxyDeck
{
    internal sealed class SetupForm : Form
    {
        readonly Controller controller;
        readonly AppSettings options;
        readonly Panel content = new Panel();
        readonly Label heading = new Label(), subtitle = new Label(), steps = new Label();
        readonly Button next = new Button(), back = new Button();
        readonly TextBox directory = new TextBox(), key = new TextBox();
        readonly NumericUpDown port = new NumericUpDown(), delay = new NumericUpDown();
        readonly CheckBox remote = new CheckBox(), warm = new CheckBox(), exclusive = new CheckBox(), manage = new CheckBox(), weekly = new CheckBox();
        readonly Label feedback = new Label();
        CancellationTokenSource cancellation;
        int page;
        bool busy, installed;
        internal SetupForm(Controller model, bool enableTailscale = false)
        {
            controller = model; options = SettingsStore.Current.Copy();
            Text = "Proxy Deck · Setup & components"; BackColor = Art.Back; ForeColor = Art.Text;
            Font = new Font("Segoe UI", 10); ClientSize = new Size(660, 650); MinimumSize = new Size(676, 689);
            StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            heading.SetBounds(28, 24, 600, 40); heading.Font = new Font("Segoe UI", 21, FontStyle.Bold);
            subtitle.SetBounds(30, 72, 600, 48); subtitle.ForeColor = Art.Muted;
            steps.SetBounds(30, 125, 600, 22); steps.Font = new Font("Segoe UI", 9, FontStyle.Bold); steps.ForeColor = Art.Red;
            content.SetBounds(28, 162, 604, 406); content.AutoScroll = true;
            next.SetBounds(462, 595, 170, 38); back.SetBounds(28, 595, 120, 38);
            back.Text = "Back";
            Style(next, true); Style(back, false); next.Click += async (s, e) => await Next(); back.Click += (s, e) => { if (!busy) { page--; Render(); } };
            Controls.AddRange(new Control[] { heading, subtitle, steps, content, next, back });
            directory.Text = options.ProxyDirectory; key.UseSystemPasswordChar = true; key.AccessibleName = "Existing CLIProxy management key";
            port.Minimum = 1024; port.Maximum = 65535; port.Value = options.Port;
            delay.Minimum = 1; delay.Maximum = 15; delay.Value = options.ResetDelaySeconds / 60;
            remote.Checked = options.UseTailscale || enableTailscale; warm.Checked = options.AutoWarm; exclusive.Checked = options.ExclusiveTailscale; manage.Checked = options.ManageExistingProxy;
            weekly.Checked = options.PreferWeeklyQuota;
            remote.CheckedChanged += (s, e) => exclusive.Enabled = remote.Checked;
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; cancellation.Cancel(); feedback.Text = "Canceling the download; waiting for the current setup step to finish…"; } };
            Render();
        }
        static void Style(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Art.Edge;
            b.BackColor = primary ? Art.Red : Art.Card; b.ForeColor = primary ? Color.White : Art.Text; b.Cursor = Cursors.Hand;
        }
        Label TextAt(string text, int y, int height, bool bold)
        {
            var label = new Label { Text = text, ForeColor = bold ? Art.Text : Art.Muted, Font = new Font("Segoe UI", bold ? 11 : 9.5f, bold ? FontStyle.Bold : FontStyle.Regular) };
            label.SetBounds(0, y, 582, height); content.Controls.Add(label); return label;
        }
        void Field(Control c, int y, int width) { c.SetBounds(0, y, width, 30); c.BackColor = Art.Card; c.ForeColor = Art.Text; content.Controls.Add(c); }
        void Toggle(CheckBox c, string text, int y) { c.Text = text; c.SetBounds(0, y, 580, 34); c.ForeColor = Art.Text; content.Controls.Add(c); }
        void Render()
        {
            content.Controls.Clear(); back.Visible = page > 0 && page < 3; next.Enabled = true;
            steps.Text = new[] { "01  CONNECTION", "02  FEATURES", "03  SETUP SUMMARY", "04  PREPARING", "05  ACCOUNTS & FINAL CHECK" }[page];
            if (page == 0)
            {
                heading.Text = "Let's get you set up."; subtitle.Text = "Install CLIProxy or connect to an existing setup. Your accounts are preserved.";
                TextAt("CLIProxyAPI folder", 0, 26, true); Field(directory, 35, 492);
                var browse = new Button { Text = "Browse…" }; browse.SetBounds(502, 35, 80, 30); Style(browse, false); content.Controls.Add(browse);
                browse.Click += (s, e) => { using (var picker = new FolderBrowserDialog { Description = "CLIProxy folder", SelectedPath = directory.Text }) if (picker.ShowDialog(this) == DialogResult.OK) directory.Text = picker.SelectedPath; };
                TextAt("If CLIProxy is missing, the official Windows x64 package is installed here.\nFor an existing setup, cli-proxy-api.exe and config.yaml must share a folder.", 78, 48, false);
                TextAt("Local API port", 141, 25, true); Field(port, 173, 120);
                TextAt("Existing management key (leave blank for a new installation)", 222, 26, true); Field(key, 255, 582);
                TextAt("An existing Proxy Deck key is detected automatically. For other setups,\nenter your management key. It is stored using Windows user encryption.", 291, 48, false);
                Toggle(manage, "Also manage the existing CLIProxy process", 350);
                next.Text = "Continue";
            }
            else if (page == 1)
            {
                heading.Text = "Choose what you need."; subtitle.Text = "Quota tracking is included. Remote access and Auto Warm are optional.";
                TextAt("INCLUDED  ·  CLIProxy + Codex Fleet Manager", 0, 28, true);
                TextAt("Fleet Manager reads account quotas. Quota routing is optional;\nmodel retries stay disabled.", 34, 45, false);
                Toggle(remote, "Secure phone access with Tailscale", 91);
                TextAt("Downloads the official installer if needed. Installation and service setup\nrequire administrator approval. Sign-in and Serve may need a browser step.", 133, 45, false);
                Toggle(exclusive, "Run Tailscale only while Proxy Deck is open", 181); exclusive.Enabled = remote.Checked;
                TextAt("Leave unchecked if you use Tailscale for other tasks. When checked,\nthe service starts on demand and stops when you exit Proxy Deck.", 221, 43, false);
                Toggle(warm, "Automatically warm up accounts after quota resets", 274);
                TextAt("Recommended: check 1 minute after reset; send one tiny request if needed.\nGive all CLIProxy accounts the same priority to monitor them together.\nRequests may use a small amount of quota. The system must be running.", 313, 61, false);
                Field(delay, 376, 68); var label = TextAt("min after reset · recommended: 1", 379, 24, false); label.Left = 82; label.Width = 440;
                Toggle(weekly, "Prefer weekly quota; keep the active account when close", 426);
                TextAt("Within 10 percentage points, keep the active account until its 5-hour\nquota runs out or it becomes unavailable. Updates every 30–90 seconds.\nKeep all Codex accounts at the same CLIProxy priority, normally 0.", 466, 65, false);
                next.Text = "Review setup";
            }
            else if (page == 2)
            {
                heading.Text = "Review your setup"; subtitle.Text = "Your selected components will be prepared. Proxy Deck will not start with Windows.";
                bool existing = File.Exists(Path.Combine(directory.Text, "cli-proxy-api.exe"));
                TextAt(existing ? "CLIProxy · using your existing installation" : "CLIProxyAPI " + Catalog.Proxy.Version + " · will be installed", 0, 30, true);
                TextAt("Source: github.com/router-for-me/CLIProxyAPI\nCodex Fleet Manager " + Catalog.Fleet.Version + " · quota reader will be installed\nSource: github.com/doer-ee/cpa-plugin-codex-fleet-manager", 44, 76, false);
                TextAt("Tailscale · " + (remote.Checked ? File.Exists(Paths.Tailscale) ? "using your existing installation" : "official MSI will be installed" : "will not be installed or managed"), 137, 28, true);
                TextAt("Auto Warm · " + (warm.Checked ? "on, " + delay.Value + " min after reset" : "off"), 176, 28, true);
                TextAt("Weekly quota routing · " + (weekly.Checked ? "highest remaining first" : "off"), 207, 28, true);
                TextAt("Address · http://127.0.0.1:" + port.Value + "\nFolder · " + directory.Text, 243, 48, false);
                TextAt("Your existing config.yaml is backed up before changes. Downloads are\nchecked with SHA-256; the Tailscale publisher signature is verified.\nYou will sign in to your accounts in the browser after installation.", 292, 72, false);
                next.Text = "Install & prepare";
            }
            else if (page == 3)
            {
                heading.Text = "Preparing your system"; subtitle.Text = "Downloading and preparing your selected components.";
                feedback.SetBounds(0, 15, 580, 250); feedback.ForeColor = Art.Muted; content.Controls.Add(feedback);
                var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 }; bar.SetBounds(0, 280, 580, 7); content.Controls.Add(bar);
                next.Text = "Preparing…"; next.Enabled = false;
            }
            else
            {
                heading.Text = "Last step: your accounts."; subtitle.Text = "Sign in through your browser. Use the same button for each additional account.";
                var login = new Button { Text = "＋  Connect Codex account" }; login.SetBounds(0, 5, 282, 42); Style(login, true); content.Controls.Add(login);
                login.Click += async (s, e) => { login.Enabled = false; try { feedback.Text = "Waiting for browser sign-in…"; await controller.AddAccountAsync(); feedback.Text = "Account connected. " + controller.View.Accounts.Count + (controller.View.Accounts.Count == 1 ? " account monitored." : " accounts monitored."); } catch (Exception error) { feedback.Text = Controller.Friendly(error); } finally { if (!login.IsDisposed) login.Enabled = true; } };
                var retry = new Button { Text = "Check connection again" }; retry.SetBounds(298, 5, 282, 42); Style(retry, false); content.Controls.Add(retry);
                retry.Click += async (s, e) => { retry.Enabled = false; try { await controller.StartAsync(); feedback.Text = StatusText(); } finally { retry.Enabled = true; } };
                TextAt("T3 Code and other clients", 79, 26, true);
                TextAt("OpenAI-compatible API: http://127.0.0.1:" + options.Port + "/v1\nChoose Copy client API key from the tray menu.\nUse this address and key to add a custom provider in T3 Code.", 115, 76, false);
                if (options.UseTailscale)
                {
                    var tail = new Button { Text = "Sign in to Tailscale" }; tail.SetBounds(0, 211, 282, 38); Style(tail, false); content.Controls.Add(tail);
                    tail.Click += async (s, e) => { try { await controller.LoginTailscaleAsync(); } catch (Exception error) { feedback.Text = Controller.Friendly(error); } };
                    var serve = new Button { Text = "Tailscale HTTPS / Serve settings" }; serve.SetBounds(298, 211, 282, 38); Style(serve, false); content.Controls.Add(serve);
                    serve.Click += (s, e) => Controller.Open("https://login.tailscale.com/admin/dns");
                }
                feedback.SetBounds(0, 273, 580, 122); feedback.ForeColor = Art.Mint; feedback.Text = StatusText(); content.Controls.Add(feedback);
                next.Text = "Open quota dashboard";
            }
        }
        string StatusText()
        {
            return "CLIProxy: " + (controller.View.ProxyReady ? "ready" : "needs attention") + "  ·  " + controller.View.Accounts.Count + (controller.View.Accounts.Count == 1 ? " account\n" : " accounts\n") +
                (options.UseTailscale ? "Tailscale: " + (controller.View.NetworkReady ? "connected" : "waiting for sign-in / HTTPS setup") + "\n" : "Local access is ready.\n") +
                (controller.View.Error.Length > 0 ? controller.View.Error : "You can also connect accounts later from the tray menu.");
        }
        async Task Next()
        {
            if (page == 4) { DialogResult = DialogResult.OK; Close(); return; }
            if (page == 0)
            {
                try
                {
                    if (!String.Equals(Path.GetFullPath(directory.Text.Trim()).TrimEnd('\\'), options.ProxyDirectory, StringComparison.OrdinalIgnoreCase))
                    { options.ProtectedManagementKey = ""; options.ProtectedAccessKey = ""; options.ManagedInstall = false; }
                    options.ProxyDirectory = directory.Text.Trim(); options.Port = (int)port.Value; options.Validate();
                    if (File.Exists(Path.Combine(options.ProxyDirectory, "cli-proxy-api.exe")) && key.Text.Length == 0 &&
                        options.ProtectedManagementKey.Length == 0 && !File.Exists(Path.Combine(options.ProxyDirectory, "local-credentials.json")))
                        throw new InvalidOperationException("Enter the management key for your existing CLIProxy installation.");
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            }
            if (page < 2) { page++; Render(); return; }
            options.ProxyDirectory = directory.Text.Trim(); options.Port = (int)port.Value;
            options.UseTailscale = remote.Checked; options.ExclusiveTailscale = remote.Checked && exclusive.Checked;
            options.AutoWarm = warm.Checked; options.ResetDelaySeconds = (int)delay.Value * 60; options.ManageExistingProxy = manage.Checked;
            options.PreferWeeklyQuota = weekly.Checked;
            if (key.Text.Length > 0) options.ProtectedManagementKey = SettingsStore.Protect(key.Text.Trim());
            page = 3; busy = true; cancellation = new CancellationTokenSource(); Render();
            bool wasRunning = controller.View.Desired;
            var previous = SettingsStore.Current;
            Exception failed = null;
            try
            {
                Action<string> report = value => { feedback.Text = value; };
                if (wasRunning) { report("Stopping the running system"); await controller.StopAsync(); if (controller.View.Error.Length > 0) throw new InvalidOperationException(controller.View.Error); }
                await Provisioner.InstallAsync(options, report, cancellation.Token);
                options.Completed = false; SettingsStore.Save(options);
                if (options.UseTailscale)
                {
                    await Provisioner.InstallTailscale(report, cancellation.Token); report("Preparing Tailscale service permissions");
                    await ServiceSetup.Configure(options.ExclusiveTailscale);
                }
                options.Completed = true; SettingsStore.Save(options); installed = true;
                controller.ResetConfiguration();
                report("Starting CLIProxy and the quota dashboard"); await controller.StartAsync();
                page = 4; Render();
            }
            catch (Exception error)
            {
                if (!installed && previous.Completed) SettingsStore.Save(previous);
                failed = error;
            }
            if (failed != null)
            {
                if (wasRunning) await controller.StartAsync();
                page = 2; Render();
                MessageBox.Show(this, failed is OperationCanceledException ? "Download canceled. You can try again." : Controller.Friendly(failed), "Setup did not complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            busy = false; cancellation.Dispose();
        }
        internal void ExportImage(string path, int previewPage)
        {
            page = previewPage; Render();
            Show(); PerformLayout(); Application.DoEvents();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(path); }
            Hide();
        }
    }
}
