using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyDeck
{
    // Serves only the embedded page. Authenticated API calls stay on CLIProxy,
    // through the existing Tailscale origin; this listener never handles keys.
    internal sealed class PhoneDashboard : IDisposable
    {
        internal const string Path = "/v0/resource/plugins/codex-fleet-manager/status";
        internal static string Target { get { return SettingsStore.Current.PhoneDashboardPort == 0 ? "" : "http://127.0.0.1:" + SettingsStore.Current.PhoneDashboardPort; } }
        internal string PreviousTarget = "";
        TcpListener listener;
        readonly SemaphoreSlim clients = new SemaphoreSlim(8, 8);
        readonly byte[] page;
        internal PhoneDashboard()
        {
            using (var stream = typeof(PhoneDashboard).Assembly.GetManifestResourceStream("ProxyDeck.PhoneDashboard.html"))
            using (var reader = new StreamReader(stream)) page = Encoding.UTF8.GetBytes(reader.ReadToEnd());
        }
        internal void EnsureStarted()
        {
            if (listener != null) return;
            PreviousTarget = Target;
            var next = new TcpListener(IPAddress.Loopback, SettingsStore.Current.PhoneDashboardPort);
            try { next.Start(16); }
            catch (SocketException) { next.Stop(); next = new TcpListener(IPAddress.Loopback, 0); next.Start(16); }
            try
            {
                int port = ((IPEndPoint)next.LocalEndpoint).Port;
                if (port == SettingsStore.Current.Port)
                {
                    next.Stop(); next = new TcpListener(IPAddress.Loopback, 0); next.Start(16);
                    port = ((IPEndPoint)next.LocalEndpoint).Port;
                }
                if (port != SettingsStore.Current.PhoneDashboardPort)
                {
                    var options = SettingsStore.Current.Copy(); options.PhoneDashboardPort = port; SettingsStore.Save(options);
                }
                listener = next;
                Accept(next);
            }
            catch { next.Stop(); throw; }
        }
        async void Accept(TcpListener active)
        {
            try
            {
                while (true)
                {
                    var client = await active.AcceptTcpClientAsync().ConfigureAwait(false);
                    if (!clients.Wait(0)) { client.Close(); continue; }
                    ThreadPool.QueueUserWorkItem(_ => { try { Serve(client); } finally { clients.Release(); } });
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
        void Serve(TcpClient client)
        {
            using (client)
            try
            {
                client.ReceiveTimeout = 4000; client.SendTimeout = 4000;
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[8192]; int length = 0; string request = "";
                    DateTime deadline = DateTime.UtcNow.AddSeconds(4);
                    while (length < buffer.Length)
                    {
                        int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (remaining <= 0) return;
                        client.ReceiveTimeout = remaining;
                        int count = stream.Read(buffer, length, buffer.Length - length);
                        if (count == 0) return;
                        length += count; request = Encoding.ASCII.GetString(buffer, 0, length);
                        if (request.Contains("\r\n\r\n")) break;
                    }
                    var parts = request.Split(new[] { '\r', '\n' }, 2)[0].Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "", path = parts.Length > 1 ? parts[1].Split('?')[0] : "";
                    int code = !request.Contains("\r\n\r\n") ? 431 : method != "GET" && method != "HEAD" ? 405 : path != "/" && path != Path && path != Path + "/" ? 404 : 200;
                    byte[] body = code == 200 ? page : Encoding.UTF8.GetBytes("Page unavailable.");
                    string headers = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Error") + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length +
                        "\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n" +
                        "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'\r\n\r\n";
                    byte[] head = Encoding.ASCII.GetBytes(headers); stream.Write(head, 0, head.Length);
                    if (method != "HEAD") stream.Write(body, 0, body.Length);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
        public void Dispose() { var active = listener; listener = null; if (active != null) active.Stop(); }
    }
}
