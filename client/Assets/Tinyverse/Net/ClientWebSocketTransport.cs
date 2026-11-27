using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary><see cref="IWebSocketTransport"/> on <see cref="System.Net.WebSockets.ClientWebSocket"/>.</summary>
    public sealed class ClientWebSocketTransport : IWebSocketTransport
    {
        /// <summary>Default largest inbound message accepted: 4 MB.</summary>
        public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;

        readonly ClientWebSocket socket = new ClientWebSocket();
        readonly byte[] buffer = new byte[64 * 1024];
        readonly int maxMessageBytes;

        public ClientWebSocketTransport(int maxMessageBytes = DefaultMaxMessageBytes)
        {
            if (maxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
            this.maxMessageBytes = maxMessageBytes;
        }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
            socket.ConnectAsync(uri, cancellationToken);

        public Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken) =>
            socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, cancellationToken);

        public async Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            MemoryStream assembled = null;
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
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
                    socket.Abort();
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
            var state = socket.State;
            if (state != WebSocketState.Open && state != WebSocketState.CloseReceived) return Task.CompletedTask;
            return socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "", cancellationToken);
        }

        public void Abort()
        {
            try { socket.Abort(); } catch { /* never throws */ }
        }

        public void Dispose()
        {
            try { socket.Dispose(); } catch { /* never throws */ }
        }
    }
}
