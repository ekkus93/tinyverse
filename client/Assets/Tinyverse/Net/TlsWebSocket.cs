using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary>
    /// Opens a <c>wss://</c> WebSocket by hand: TCP, then <see cref="SslStream"/> with our own certificate
    /// check, then the HTTP upgrade, then <see cref="WebSocket.CreateFromStream"/> for the framing.
    /// <para>
    /// Why: Unity's Mono <see cref="ClientWebSocket"/> (checked in 6000.6 against the real server) ignores
    /// both <c>ClientWebSocketOptions.RemoteCertificateValidationCallback</c> and
    /// <c>ServicePointManager.ServerCertificateValidationCallback</c>: the callbacks are never called and a
    /// self-signed certificate always fails with UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED. <see cref="SslStream"/>
    /// does call its callback, and <see cref="WebSocket.CreateFromStream"/> works over it.
    /// </para>
    /// <para>
    /// The check: with a pin (<c>#sha256=…</c> in the URL) the certificate must have exactly that SHA-256, and
    /// nothing else is checked; without one the system's verdict must be "no errors". A refusal throws
    /// <see cref="TinyverseCertificateException"/> with the presented fingerprint.
    /// </para>
    /// </summary>
    internal sealed class TlsWebSocket : IDisposable
    {
        const string AcceptMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        const int MaxResponseHeaderBytes = 16 * 1024;

        // TLS 1.2 and 1.3. .NET Standard 2.1 has no SslProtocols.Tls13 name, but Unity's Mono runtime knows the
        // value (0x3000) and negotiates TLS 1.3 with it.
        const SslProtocols Protocols = SslProtocols.Tls12 | (SslProtocols)0x3000;

        TcpClient tcp;
        SslStream ssl;

        public WebSocket Socket { get; private set; }

        /// <summary>SHA-256 of the certificate the server presented (lower-case hex), once known.</summary>
        public string PresentedFingerprint { get; private set; }

        TlsWebSocket() { }

        /// <summary>Connects to a <c>wss://</c> URI, honouring a <c>#sha256=</c> pin in its fragment.</summary>
        public static async Task<TlsWebSocket> ConnectAsync(Uri uri, CancellationToken ct)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (uri.Scheme != "wss") throw new ArgumentException("Not a wss:// URI.", nameof(uri));
            if (!CertificatePin.TrySplit(uri.OriginalString, out _, out var pin, out var error))
                throw new ArgumentException(error, nameof(uri));

            var c = new TlsWebSocket();
            try
            {
                using (ct.Register(c.Dispose))
                {
                    await c.ConnectCoreAsync(uri, pin, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                return c;
            }
            catch (Exception e)
            {
                c.Dispose();
                if (ct.IsCancellationRequested && !(e is TinyverseCertificateException))
                    throw new OperationCanceledException("Connecting was cancelled.", e, ct);
                throw;
            }
        }

        async Task ConnectCoreAsync(Uri uri, string pin, CancellationToken ct)
        {
            string host = uri.DnsSafeHost;
            int port = uri.Port;
            string key = CertificatePin.HostPortKey(uri);

            tcp = await ConnectTcpAsync(host, port).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            bool refused = false;
            string policy = null;
            ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
            {
                if (certificate == null)
                {
                    refused = true;
                    policy = "no certificate";
                    return false;
                }
                PresentedFingerprint = CertificatePin.Sha256Hex(certificate.GetRawCertData());
                policy = errors.ToString();
                bool ok = pin != null ? PresentedFingerprint == pin : errors == SslPolicyErrors.None;
                refused = !ok;
                return ok;
            });
            try
            {
                await ssl.AuthenticateAsClientAsync(host, null, Protocols, false)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (refused && PresentedFingerprint != null)
            {
                throw new TinyverseCertificateException(
                    pin != null ? CertificateProblem.PinMismatch : CertificateProblem.Untrusted,
                    key, PresentedFingerprint, pin, policy, e);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                throw new TinyverseConnectionException(
                    $"The TLS handshake with {key} failed ({Innermost(e).Message}); the server may not serve TLS on this port (try ws:// instead of wss://).", e);
            }
            ct.ThrowIfCancellationRequested();

            await UpgradeAsync(uri, ct).ConfigureAwait(false);
            // Keep-alive is ours (Ping/Pong messages); no WebSocket-level pings.
            Socket = WebSocket.CreateFromStream(ssl, false, null, TimeSpan.Zero);
        }

        static async Task<TcpClient> ConnectTcpAsync(string host, int port)
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            Exception last = null;
            foreach (var address in addresses)
            {
                var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                try
                {
                    await client.ConnectAsync(address, port).ConfigureAwait(false);
                    return client;
                }
                catch (Exception e)
                {
                    last = e;
                    client.Dispose();
                }
            }
            if (last is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused)
                throw new TinyverseConnectionException("Connection refused (is the server running, and on this port?).", last);
            throw new TinyverseConnectionException(
                last != null ? $"Could not connect to {host}:{port}: {last.Message}" : $"{host} has no address.", last);
        }

        async Task UpgradeAsync(Uri uri, CancellationToken ct)
        {
            var nonce = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            string key = Convert.ToBase64String(nonce);
            string hostHeader = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            string request =
                $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                $"Host: {hostHeader}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(request);
            await ssl.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await ssl.FlushAsync(ct).ConfigureAwait(false);

            // One byte at a time, so that nothing after the header is consumed: frames may follow at once.
            var header = new StringBuilder();
            var one = new byte[1];
            while (!EndsWithBlankLine(header))
            {
                int n = await ssl.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                if (n == 0) throw new TinyverseConnectionException("The server closed the connection during the WebSocket handshake.");
                if (header.Length >= MaxResponseHeaderBytes) throw new TinyverseConnectionException("The server's WebSocket handshake reply is too long.");
                header.Append((char)one[0]);
            }

            string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string status = lines.Length > 0 ? lines[0] : "";
            string[] parts = status.Split(' ');
            if (parts.Length < 2 || parts[1] != "101")
                throw new TinyverseConnectionException(
                    $"The server answered \"{status}\" instead of upgrading to a WebSocket (is the path {uri.AbsolutePath} right?).");
            string accept = null;
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
                    accept = line.Substring(colon + 1).Trim();
            }
            string expected;
            using (var sha1 = SHA1.Create())
                expected = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptMagic)));
            if (accept != expected) throw new TinyverseConnectionException("The server's WebSocket handshake reply is invalid.");
        }

        static bool EndsWithBlankLine(StringBuilder sb)
        {
            int n = sb.Length;
            return n >= 4 && sb[n - 4] == '\r' && sb[n - 3] == '\n' && sb[n - 2] == '\r' && sb[n - 1] == '\n';
        }

        static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }

        public void Abort()
        {
            try { Socket?.Abort(); } catch { /* never throws */ }
            try { tcp?.Close(); } catch { /* never throws */ }
        }

        public void Dispose()
        {
            try { Socket?.Dispose(); } catch { /* never throws */ }
            try { ssl?.Dispose(); } catch { /* never throws */ }
            try { tcp?.Dispose(); } catch { /* never throws */ }
        }
    }
}
