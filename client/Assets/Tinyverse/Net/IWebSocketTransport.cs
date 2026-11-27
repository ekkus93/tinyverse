using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Net
{
    /// <summary>What a <see cref="WebSocketFrame"/> holds.</summary>
    public enum WebSocketFrameType
    {
        Binary,
        Text,
        /// <summary>The peer closed the connection; no more frames follow.</summary>
        Close,
    }

    /// <summary>One complete WebSocket message (all fragments joined), or the peer's close.</summary>
    public readonly struct WebSocketFrame
    {
        public WebSocketFrameType Type { get; }

        /// <summary>The payload of a Binary or Text message; null for Close.</summary>
        public byte[] Data { get; }

        /// <summary>The close status code sent by the peer, if any.</summary>
        public int? CloseStatus { get; }

        /// <summary>The close reason sent by the peer, if any.</summary>
        public string CloseDescription { get; }

        WebSocketFrame(WebSocketFrameType type, byte[] data, int? closeStatus, string closeDescription)
        {
            Type = type;
            Data = data;
            CloseStatus = closeStatus;
            CloseDescription = closeDescription;
        }

        public static WebSocketFrame Binary(byte[] data) => new WebSocketFrame(WebSocketFrameType.Binary, data, null, null);
        public static WebSocketFrame Text(byte[] data) => new WebSocketFrame(WebSocketFrameType.Text, data, null, null);
        public static WebSocketFrame Closed(int? status, string description) =>
            new WebSocketFrame(WebSocketFrameType.Close, null, status, description);
    }

    /// <summary>
    /// A client WebSocket, reduced to what <see cref="TinyverseConnection"/> needs. One instance is
    /// used for one connection attempt only. The connection guarantees at most one outstanding
    /// <see cref="SendBinaryAsync"/> and at most one outstanding <see cref="ReceiveAsync"/> at a time.
    /// </summary>
    public interface IWebSocketTransport : IDisposable
    {
        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

        /// <summary>Sends one binary message.</summary>
        Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken);

        /// <summary>
        /// Waits for the next complete message. Returns a <see cref="WebSocketFrameType.Close"/> frame
        /// when the peer closed the connection; throws if the connection failed.
        /// </summary>
        Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken);

        /// <summary>Starts a graceful close (sends a close frame).</summary>
        Task CloseAsync(string reason, CancellationToken cancellationToken);

        /// <summary>Drops the connection at once. Pending operations fail. Never throws.</summary>
        void Abort();
    }
}
