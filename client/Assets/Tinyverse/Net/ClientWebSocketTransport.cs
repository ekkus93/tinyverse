using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary>
    /// <see cref="IWebSocketTransport"/> on <see cref="System.Net.WebSockets.ClientWebSocket"/> for <c>ws://</c>,
    /// and on <see cref="TlsWebSocket"/> (our own TLS and certificate check, see there) for <c>wss://</c>.
    /// A <c>wss://</c> URI may carry a certificate pin as its fragment (<c>#sha256=…</c>, see
    /// <see cref="CertificatePin"/>); a refused certificate throws <see cref="TinyverseCertificateException"/>.
    /// </summary>
    public sealed class ClientWebSocketTransport : IWebSocketTransport
    {
        /// <summary>Default largest inbound message accepted: 4 MB.</summary>
        public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

        readonly byte[] buffer = new byte[64 * 1024];
        readonly int maxMessageBytes;
        volatile WebSocket socket;
        TlsWebSocket tls;

        public ClientWebSocketTransport(int maxMessageBytes = DefaultMaxMessageBytes)
        {
            if (maxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            this.maxMessageBytes = maxMessageBytes;
        }

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (socket != null) throw new InvalidOperationException("A transport connects once.");
            if (uri.Scheme == "wss")
            {
                tls = await TlsWebSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                socket = tls.Socket;
                return;
            }
            var plain = new ClientWebSocket();
            socket = plain;
            await plain.ConnectAsync(new Uri(CertificatePin.Strip(uri.OriginalString)), cancellationToken).ConfigureAwait(false);
        }

        WebSocket Socket => socket ?? throw new InvalidOperationException("Not connected.");

        public Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken) =>
            Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cancellationToken);

        public async Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            var s = Socket;
            MemoryStream assembled = null;
            while (true)
            {
                var result = await s.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return WebSocketFrame.Closed((int?)result.CloseStatus, result.CloseStatusDescription);

                if (result.EndOfMessage && assembled == null)
                {
                    var single = new byte[result.Count];
                    Buffer.BlockCopy(buffer, 0, single, 0, result.Count);
                    return Frame(result.MessageType, single);
                }

                if (assembled == null) assembled = new MemoryStream();
                if (assembled.Length + result.Count > maxMessageBytes)
                {
                    Abort();
                    throw new InvalidDataException($"Inbound message larger than {maxMessageBytes} bytes.");
                }
                assembled.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Frame(result.MessageType, assembled.ToArray());
            }
        }

        static WebSocketFrame Frame(WebSocketMessageType type, byte[] data) =>
            type == WebSocketMessageType.Text ? WebSocketFrame.Text(data) : WebSocketFrame.Binary(data);

        public Task CloseAsync(string reason, CancellationToken cancellationToken)
        {
            var s = socket;
            if (s == null) return Task.CompletedTask;
            var state = s.State;
            if (state != WebSocketState.Open && state != WebSocketState.CloseReceived) return Task.CompletedTask;
            return s.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "", cancellationToken);
        }

        public void Abort()
        {
            try { socket?.Abort(); } catch { /* never throws */ }
            try { tls?.Abort(); } catch { /* never throws */ }
        }

        public void Dispose()
        {
            try { socket?.Dispose(); } catch { /* never throws */ }
            try { tls?.Dispose(); } catch { /* never throws */ }
        }
    }
}
