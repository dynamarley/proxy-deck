using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ProxyDeck
{
    internal static class Art
    {
        internal static readonly Color Back = Color.FromArgb(13, 15, 19), Card = Color.FromArgb(19, 22, 27),
            Edge = Color.FromArgb(36, 41, 49), Text = Color.FromArgb(235, 239, 246), Muted = Color.FromArgb(132, 143, 159),
            Mint = Color.FromArgb(45, 214, 164), Red = Color.FromArgb(246, 79, 99), Amber = Color.FromArgb(244, 181, 86);
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        internal static void Box(Graphics g, RectangleF r, Color color, float radius, bool border)
        {
            using (var p = Round(r, radius))
            { using (var brush = new SolidBrush(color)) g.FillPath(brush, p); if (border) using (var pen = new Pen(Edge)) g.DrawPath(pen, p); }
        }
        internal static void Label(Graphics g, string text, float x, float y, float width, float size, Color color, bool bold, bool right)
        {
            using (var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = right ? StringAlignment.Far : StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, new RectangleF(x, y, width, size * 1.8f), format);
        }
        internal static void Mark(Graphics g, float x, float y, float size, Color color)
        {
            using (var brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, new[] { new PointF(x, y), new PointF(x + size * .42f, y + size * .24f), new PointF(x + size * .42f, y + size * .81f), new PointF(x + size * .1f, y + size * .57f) });
                g.FillPolygon(brush, new[] { new PointF(x + size * .55f, y + size * .33f), new PointF(x + size, y + size * .05f), new PointF(x + size * .9f, y + size * .67f), new PointF(x + size * .55f, y + size) });
            }
        }
        internal static Icon Icon(bool active)
        {
            using (var bitmap = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Mark(g, 3, 3, 26, active ? Red : Muted);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (var original = System.Drawing.Icon.FromHandle(handle)) return (Icon)original.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    }

    internal sealed class DeckForm : Form
    {
        const float DesignWidth = 360, MaxPanelHeight = 640, CardHeight = 128, CardStride = 138, AccountsTop = 156, Footer = 184;
        readonly Controller controller;
        readonly Dictionary<string, RectangleF> hits = new Dictionary<string, RectangleF>();
        readonly ToolTip tips = new ToolTip { InitialDelay = 350, AutoPopDelay = 10000 };
        float zoom = 1, scroll, scrollGrab;
        string hover = "", toast = "";
        DateTimeOffset toastUntil;
        bool refreshing, draggingScroll;
        internal bool Preview;
        internal event Action ContextRequested;
        internal event Action TailscaleSetupRequested;
        internal DeckForm(Controller model)
        {
            controller = model; Text = "Proxy Deck"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; TopMost = true; BackColor = Art.Back; AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) zoom = Math.Max(1, g.DpiX / 96f);
            using (var icon = Art.Icon(true)) Icon = (Icon)icon.Clone();
            SetSizeAndPosition();
            Deactivate += (s, e) => { if (!Preview) Hide(); };
            KeyPreview = true;
        }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ClassStyle |= 0x20000; return p; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int yes = 1, corner = 2, border = 0x00312924; DwmSetWindowAttribute(Handle, 20, ref yes, 4); DwmSetWindowAttribute(Handle, 33, ref corner, 4); DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        internal void SetSizeAndPosition()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            float height = Math.Min(AccountsTop + AccountContentHeight + Footer, MaxPanelHeight);
            height = Math.Min(height, Math.Max(1, work.Height / zoom - 24));
            Size = new Size((int)(DesignWidth * zoom), (int)Math.Ceiling(height * zoom));
            Location = new Point(work.Right - Width - (int)(12 * zoom), work.Bottom - Height - (int)(12 * zoom));
        }
        internal static bool HasFable(Account account) { return account.FableWeekly != null || account.FableManualResets.HasValue; }
        internal static float AccountHeight(Account account) { return CardHeight + (HasFable(account) ? 48 : 0); }
        float AccountContentHeight { get { return controller.View.Accounts.Count == 0 ? CardHeight : controller.View.Accounts.Sum(a => AccountHeight(a) + CardStride - CardHeight) - (CardStride - CardHeight); } }
        float AccountViewport { get { return Math.Max(1, ClientSize.Height / zoom - AccountsTop - Footer); } }
        float ScrollLimit { get { return Math.Max(0, AccountContentHeight - AccountViewport); } }
        RectangleF ScrollThumb
        {
            get
            {
                float viewport = AccountViewport, thumb = Math.Min(viewport, Math.Max(32, viewport * viewport / AccountContentHeight));
                float offset = scroll / Math.Max(1, ScrollLimit) * (viewport - thumb);
                return new RectangleF(349, AccountsTop + offset, 4, thumb);
            }
        }
        void ScrollTo(float value) { scroll = Math.Max(0, Math.Min(value, ScrollLimit)); Invalidate(); }
        internal void UpdateView()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(UpdateView)); return; }
            SetSizeAndPosition(); Invalidate();
        }
        internal void OpenPanel()
        {
            if (!controller.View.ProxyReady) { Toast("Start the system first."); return; }
            Controller.Open(Paths.Api + "/v0/resource/plugins/codex-fleet-manager/status");
        }
        internal void OpenProxyDashboard()
        {
            if (!controller.View.ProxyReady) { Toast("Start the system first."); return; }
            Controller.Open(Paths.Api + "/management.html");
        }
        internal void CopyKey()
        {
            try { Clipboard.SetText(controller.ManagementKey()); Toast("Management key copied to the clipboard."); }
            catch { Toast("Could not read the key. Check your CLIProxy installation."); }
        }
        internal void CopyRemote()
        {
            if (controller.View.RemoteUrl.Length == 0) { Toast("Start the system to get the phone link."); return; }
            Clipboard.SetText(controller.View.RemoteUrl); Toast("Phone dashboard link copied.");
        }
        internal void Toast(string text) { toast = text; toastUntil = DateTimeOffset.Now.AddSeconds(7); Invalidate(); }
        internal void ShowPanel() { SetSizeAndPosition(); Show(); Activate(); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.KeyCode == Keys.PageDown) { ScrollTo(scroll + AccountViewport - 20); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { ScrollTo(scroll - AccountViewport + 20); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { ScrollTo(0); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { ScrollTo(ScrollLimit); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else base.OnFormClosing(e);
        }
        void Hit(string id, float x, float y, float width, float height) { hits[id] = new RectangleF(x, y, width, height); }
        Color Hovered(string id, Color normal, Color brighter) { return hover == id ? brighter : normal; }
        void DrawSwitch(Graphics g, string id, bool enabled, float x, float y)
        {
            Color color = controller.View.Busy ? Art.Muted : enabled ? Art.Mint : Art.Muted;
            Art.Label(g, enabled ? "On" : "Off", x - 28, y - 2, 24, 10, color, true, true);
            Art.Box(g, new RectangleF(x, y, 32, 17), enabled ? Color.FromArgb(27, 97, 80) : Color.FromArgb(48, 54, 63), 9, false);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, x + (enabled ? 17 : 3), y + 3, 11, 11);
            Hit(id, x - 30, y - 5, 66, 27);
        }
        void DrawButton(Graphics g, string id, string text, float x, float y, float width, bool danger)
        {
            var c = Hovered(id, danger ? Color.FromArgb(37, 23, 29) : Color.FromArgb(27, 31, 38), danger ? Color.FromArgb(58, 29, 37) : Color.FromArgb(38, 44, 53));
            Art.Box(g, new RectangleF(x, y, width, 32), c, 8, true);
            using (var font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(danger ? Color.FromArgb(249, 139, 151) : Art.Text))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, brush, new RectangleF(x, y, width, 32), format);
            Hit(id, x, y, width, 32);
        }
        void Dot(Graphics g, float x, float y, Color color, float size)
        { using (var b = new SolidBrush(Color.FromArgb(22, color))) g.FillEllipse(b, x - 3, y - 3, size + 6, size + 6); using (var b = new SolidBrush(color)) g.FillEllipse(b, x, y, size, size); }
        static Color QuotaColor(Quota q) { return !q.Known ? Art.Muted : q.Remaining <= 0 ? Art.Red : q.Remaining < 20 ? Art.Amber : Art.Mint; }
        static string Percent(Quota q) { return q.Known ? Math.Floor(q.Remaining).ToString() + "%" : "—"; }
        internal static string ResetLabel(Quota q)
        {
            if (!q.Reset.HasValue) return "Awaiting reset time";
            var date = q.Reset.Value.ToLocalTime();
            var remaining = date - DateTimeOffset.Now;
            if (remaining.TotalSeconds < 0) return "Checking reset";
            string duration = remaining.TotalHours >= 1 ? ((int)remaining.TotalHours) + "h " + remaining.Minutes + "m" : Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + "m";
            return date.ToString("HH:mm") + " · " + duration;
        }
        void Bar(Graphics g, float x, float y, float width, Quota quota, bool quiet)
        {
            Art.Box(g, new RectangleF(x, y, width, quiet ? 3 : 5), Color.FromArgb(38, 43, 51), 3, false);
            if (quota.Known && quota.Remaining > 0)
                Art.Box(g, new RectangleF(x, y, Math.Max(quiet ? 3 : 5, width * (float)quota.Remaining / 100f), quiet ? 3 : 5), quiet ? Color.FromArgb(130, QuotaColor(quota)) : QuotaColor(quota), 3, false);
        }
        void DrawAccount(Graphics g, Account a, float y)
        {
            Art.Box(g, new RectangleF(16, y, 328, AccountHeight(a)), Art.Card, 10, true);
            Dot(g, 30, y + 19, controller.View.Desired ? a.Available ? Art.Mint : Art.Red : Art.Muted, 5);
            Art.Label(g, a.Name, 43, y + 11, 175, 13, Art.Text, true, false);
            Art.Label(g, a.Email, 29, y + 31, 202, 11, Art.Muted, false, false);
            Art.Label(g, "5 HOURS", 253, y + 10, 77, 8, Art.Muted, true, true);
            Art.Label(g, Percent(a.Short), 240, y + 22, 90, 25, QuotaColor(a.Short), true, true);
            Art.Label(g, "5-hour remaining", 29, y + 55, 112, 10.5f, Art.Muted, false, false);
            Art.Label(g, ResetLabel(a.Short), 137, y + 55, 193, 10.5f, Art.Muted, false, true);
            Bar(g, 29, y + 74, 301, a.Short, false);
            string kind = a.Long.Kind == "monthly" ? "Monthly" : "Weekly";
            Art.Label(g, kind, 29, y + 85, 130, 11, Art.Muted, false, false);
            Art.Label(g, Percent(a.Long), 242, y + 83, 88, 13, Art.Text, true, true);
            Bar(g, 29, y + 106, 301, a.Long, true);
            string reset = a.Long.Reset.HasValue ? a.Long.Reset.Value.ToLocalTime().ToString("dd MMM · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : "Reset time unavailable";
            if (a.SupportsManualResets)
                Art.Label(g, "Manual resets · " + (a.ManualResets.HasValue ? a.ManualResets.Value.ToString() : "—"), 29, y + 110, 122, 9.5f,
                    a.ManualResets.GetValueOrDefault() > 0 ? Art.Mint : Art.Muted, true, false);
            Art.Label(g, reset, a.SupportsManualResets ? 153 : 29, y + 112, a.SupportsManualResets ? 177 : 301, 8.5f, Art.Muted, false, true);
            if (HasFable(a))
            {
                var fable = a.FableWeekly ?? new Quota();
                Art.Label(g, "Fable weekly", 29, y + 133, 166, 11, Art.Muted, false, false);
                Art.Label(g, Percent(fable), 242, y + 131, 88, 13, Art.Text, true, true);
                Bar(g, 29, y + 153, 301, fable, true);
                Art.Label(g, "Fable resets · " + (a.FableManualResets.HasValue ? a.FableManualResets.Value.ToString() : "—"), 29, y + 158, 122, 9.5f,
                    a.FableManualResets.GetValueOrDefault() > 0 ? Art.Mint : Art.Muted, true, false);
                string date = fable.Reset.HasValue ? fable.Reset.Value.ToLocalTime().ToString("dd MMM · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-US")) : "Reset time unavailable";
                Art.Label(g, date, 153, y + 160, 177, 8.5f, Art.Muted, false, true);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.Clear(Art.Back); g.ScaleTransform(zoom, zoom); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            hits.Clear(); var view = controller.View; float height = ClientSize.Height / zoom;
            Art.Mark(g, 20, 18, 23, Art.Red);
            Art.Label(g, "PROXY DECK", 54, 22, 180, 13, Art.Text, true, false);
            if (hover == "refresh") Art.Box(g, new RectangleF(279, 17, 27, 27), Color.FromArgb(30, 34, 42), 7, false);
            using (var pen = new Pen(refreshing ? Art.Mint : Art.Muted, 1.4f))
            { g.DrawArc(pen, 286, 23, 12, 12, 25, 285); g.DrawLines(pen, new[] { new PointF(297, 20), new PointF(299, 25), new PointF(294, 25) }); }
            Hit("refresh", 279, 17, 27, 27);
            if (hover == "hide") Art.Box(g, new RectangleF(314, 17, 27, 27), Color.FromArgb(30, 34, 42), 7, false);
            using (var pen = new Pen(Art.Muted, 1.2f)) { g.DrawLine(pen, 323, 26, 331, 34); g.DrawLine(pen, 331, 26, 323, 34); }
            Hit("hide", 314, 17, 27, 27);
            using (var pen = new Pen(Art.Edge)) g.DrawLine(pen, 16, 62, 344, 62);

            Art.Box(g, new RectangleF(16, 70, 328, 54), Art.Card, 10, true);
            string state = view.Busy ? "STARTING" : view.Ready ? "SYSTEM READY" : view.Desired ? "needs attention" : "SYSTEM STOPPED";
            Art.Label(g, state, 28, 79, 277, 8.5f, view.Ready ? Art.Mint : view.Busy ? Art.Amber : Art.Muted, true, false);
            Dot(g, 29, 108, view.ProxyReady ? Art.Mint : Art.Muted, 5);
            Art.Label(g, "CLIProxy", 42, 101, 88, 11.5f, Art.Text, false, false);
            Art.Label(g, view.ProxyReady ? "ready" : "off", 108, 103, 48, 10, Art.Muted, false, true);
            using (var pen = new Pen(Art.Edge)) g.DrawLine(pen, 176, 99, 176, 118);
            if (hover == "tailscale") Art.Box(g, new RectangleF(183, 96, 152, 28), Color.FromArgb(29, 35, 42), 6, false);
            Dot(g, 192, 108, view.NetworkReady ? Art.Mint : Art.Muted, 5);
            Art.Label(g, "Tailscale", 205, 101, 65, 11.5f, Art.Text, false, false);
            DrawSwitch(g, "tailscale", SettingsStore.Current.UseTailscale, 299, 103);
            hits["tailscale"] = new RectangleF(183, 95, 152, 28);
            Art.Label(g, "ACCOUNTS", 19, 136, 140, 9, Art.Muted, true, false);
            Art.Label(g, view.Accounts.Count + (view.Accounts.Count == 1 ? " account" : " accounts"), 222, 135, 120, 10, Art.Muted, false, true);

            float viewport = AccountViewport;
            scroll = Math.Max(0, Math.Min(scroll, ScrollLimit));
            var saved = g.Save(); g.SetClip(new RectangleF(12, AccountsTop - 1, 336, viewport));
            if (view.Accounts.Count == 0)
            {
                Art.Box(g, new RectangleF(16, AccountsTop, 328, CardHeight), Art.Card, 10, true);
                Art.Label(g, view.Busy ? "Preparing your system…" : view.Desired ? "Refresh to load account limits" : "Ready in one click", 30, AccountsTop + 28, 300, 15, Art.Text, true, false);
                Art.Label(g, view.Busy ? view.Stage : view.Desired ? "Select Refresh to check your accounts." : "Start to prepare your connection and accounts.", 30, AccountsTop + 59, 295, 11, Art.Muted, false, false);
            }
            float accountY = AccountsTop - scroll;
            for (int i = 0; i < view.Accounts.Count; i++)
            {
                float y = accountY;
                if (y > AccountsTop + viewport) break;
                if (y + AccountHeight(view.Accounts[i]) >= AccountsTop) DrawAccount(g, view.Accounts[i], y);
                accountY += AccountHeight(view.Accounts[i]) + CardStride - CardHeight;
            }
            if (scroll > 0)
                using (var fade = new LinearGradientBrush(new RectangleF(12, AccountsTop - 1, 336, 16), Art.Back, Color.FromArgb(0, Art.Back), LinearGradientMode.Vertical))
                    g.FillRectangle(fade, 12, AccountsTop - 1, 336, 16);
            if (scroll < ScrollLimit)
                using (var fade = new LinearGradientBrush(new RectangleF(12, AccountsTop + viewport - 16, 336, 16), Color.FromArgb(0, Art.Back), Art.Back, LinearGradientMode.Vertical))
                    g.FillRectangle(fade, 12, AccountsTop + viewport - 16, 336, 16);
            g.Restore(saved);
            if (ScrollLimit > 0)
            {
                Hit("scroll", 344, AccountsTop, 12, viewport);
                Art.Box(g, new RectangleF(350, AccountsTop, 2, viewport), Art.Edge, 1, false);
                Art.Box(g, ScrollThumb, draggingScroll || hover == "scroll" ? Color.FromArgb(113, 145, 143) : Color.FromArgb(64, 77, 83), 2, false);
            }

            float footer = height - Footer + 3;
            bool hasError = !String.IsNullOrWhiteSpace(view.Error);
            Art.Box(g, new RectangleF(16, footer, 328, 31), Hovered("warmup", Color.FromArgb(17, 29, 27), Color.FromArgb(23, 42, 36)), 8, false);
            bool warmup = view.ProxyReady ? view.Warmup : SettingsStore.Current.AutoWarm;
            Dot(g, 29, footer + 13, view.Desired && warmup ? Art.Mint : Art.Muted, 4);
            Art.Label(g, "Auto Warm after reset", 41, footer + 7, 218, 10.5f, Art.Muted, false, false);
            DrawSwitch(g, "warmup", warmup, 299, footer + 7);
            hits["warmup"] = new RectangleF(16, footer, 328, 31);
            DrawButton(g, "panel", "Quota dashboard", 16, footer + 40, 159, false);
            DrawButton(g, "key", "Copy management key", 185, footer + 40, 159, false);
            DrawButton(g, "proxy-dashboard", "CLIProxy dashboard", 16, footer + 80, 328, false);
            DrawButton(g, "power", view.Busy ? view.Stage + "…" : view.Desired ? "Stop system" : "Start system", 16, footer + 120, 328, view.Desired);
            string age = "Quota data has not been loaded";
            if (view.ReadAt.HasValue)
            {
                double seconds = view.Accounts.Count == 0 ? 0 : view.Accounts.Max(a => a.CacheAge) + (DateTimeOffset.Now - view.ReadAt.Value).TotalSeconds;
                age = "Quota data · " + (seconds < 60 ? "just now" : ((int)(seconds / 60)) + "m ago");
                if (!view.Desired) age = "System stopped · last known limits";
            }
            if (toastUntil > DateTimeOffset.Now) age = toast;
            else if (hasError) age = view.Error;
            Art.Label(g, age, 18, footer + 162, 285, 9, hasError ? Art.Amber : Art.Muted, false, false);
            Art.Label(g, "•••", 312, footer + 153, 29, 15, Art.Muted, true, true); Hit("menu", 310, footer + 151, 34, 28);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); var point = new PointF(e.X / zoom, e.Y / zoom);
            if (draggingScroll)
            {
                float travel = AccountViewport - ScrollThumb.Height;
                ScrollTo((point.Y - AccountsTop - scrollGrab) / Math.Max(1, travel) * ScrollLimit);
                return;
            }
            string next = hits.FirstOrDefault(hit => hit.Value.Contains(point)).Key ?? "";
            Cursor = next.Length > 0 ? Cursors.Hand : Cursors.Default;
            if (next != hover)
            {
                hover = next;
                string hint = next == "scroll" ? "Scroll or drag to view more accounts." :
                    next == "refresh" ? "Refresh account limits. Displayed percentages only update when you click." :
                    next == "warmup" ? "Click to turn Auto Warm on or off. Changes are saved immediately." :
                    next == "tailscale" ? SettingsStore.Current.ExclusiveTailscale ? "Click to start or stop Tailscale. The local proxy stays running." :
                    "Click to enable or disable this app's phone access. Other Tailscale connections stay running." : controller.View.Error;
                tips.SetToolTip(this, hint); Invalidate();
            }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = ""; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e.Y / zoom >= AccountsTop && e.Y / zoom < AccountsTop + AccountViewport) ScrollTo(scroll - e.Delta / 2f);
            base.OnMouseWheel(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var point = new PointF(e.X / zoom, e.Y / zoom);
            RectangleF track;
            if (e.Button != MouseButtons.Left || !hits.TryGetValue("scroll", out track) || !track.Contains(point)) return;
            var thumb = ScrollThumb;
            if (!thumb.Contains(point))
            {
                float travel = AccountViewport - thumb.Height;
                ScrollTo((point.Y - AccountsTop - thumb.Height / 2) / Math.Max(1, travel) * ScrollLimit);
            }
            scrollGrab = Math.Max(0, Math.Min(ScrollThumb.Height, point.Y - ScrollThumb.Top));
            draggingScroll = true; Capture = true; Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) { draggingScroll = false; Invalidate(); }
        }
        protected override async void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
            if (draggingScroll) { draggingScroll = false; Capture = false; Invalidate(); return; }
            var point = new PointF(e.X / zoom, e.Y / zoom);
            string id = hits.FirstOrDefault(hit => hit.Value.Contains(point)).Key;
            await ActivateAsync(id);
        }
        internal async System.Threading.Tasks.Task ActivateAsync(string id)
        {
            try
            {
                if (id == "hide") Hide();
                else if (id == "panel") OpenPanel();
                else if (id == "proxy-dashboard") OpenProxyDashboard();
                else if (id == "key") CopyKey();
                else if (id == "menu" && ContextRequested != null) ContextRequested();
                else if (id == "power" && !controller.View.Busy) { if (controller.View.Desired) await controller.StopAsync(); else await controller.StartAsync(); }
                else if (id == "warmup" && !controller.View.Busy)
                {
                    await controller.SetWarmupAsync(!controller.View.Warmup);
                    Toast(controller.View.Warmup ? "Auto Warm is on." : "Auto Warm is off.");
                }
                else if (id == "tailscale" && !controller.View.Busy)
                {
                    if (!SettingsStore.Current.UseTailscale && !controller.TailscaleConfigured)
                    {
                        if (TailscaleSetupRequested != null) TailscaleSetupRequested();
                        else Toast("Enable Tailscale in Setup & components first.");
                        return;
                    }
                    await controller.SetTailscaleAsync(!SettingsStore.Current.UseTailscale);
                    Toast(SettingsStore.Current.UseTailscale ? "Tailscale access is on." : "Tailscale access is off.");
                }
                else if (id == "refresh" && !refreshing && !controller.View.Busy && controller.View.Desired)
                {
                    refreshing = true; Invalidate(); controller.AllowAuthRetry();
                    await controller.RefreshAsync(true, true); refreshing = false; Invalidate();
                }
            }
            catch (Exception error) { refreshing = false; Toast(Controller.Friendly(error)); }
        }
        protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
        internal void ExportImage(string path)
        {
            SetSizeAndPosition(); CreateControl();
            using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
    }
}
