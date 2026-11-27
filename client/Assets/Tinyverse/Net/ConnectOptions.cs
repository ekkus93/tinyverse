using System;
using Tinyverse.Proto;

namespace Tinyverse.Net
{
    /// <summary>The state of a <see cref="TinyverseConnection"/>.</summary>
    public enum ConnectionState
    {
        Disconnected,
        /// <summary>Opening the WebSocket.</summary>
        Connecting,
        /// <summary>Socket open, Hello sent, waiting for Welcome.</summary>
        Joining,
        /// <summary>Welcome received; live.</summary>
        Connected,
        /// <summary>The connection dropped and is being re-established.</summary>
        Reconnecting,
    }

    /// <summary>What to send in <c>Hello</c>.</summary>
    public sealed class ConnectOptions
    {
        /// <summary>WebSocket URL, e.g. <c>ws://host:7878/ws</c> (see <see cref="WorldAddress"/>).</summary>
        public string Url { get; set; }

        public string DisplayName { get; set; } = "";
        public string Password { get; set; } = "";

        /// <summary>Session token from an earlier Welcome, or an agent token. Empty on a first visit.</summary>
        public string Token { get; set; } = "";

        public ClientKind Kind { get; set; } = ClientKind.Human;

        /// <summary>May be null.</summary>
        public Appearance Appearance { get; set; }

        /// <summary>Name and version of the client software, for the host's log.</summary>
        public string ClientName { get; set; } = "tinyverse-unity";

        internal ConnectOptions Clone() => new ConnectOptions
        {
            Url = Url,
            DisplayName = DisplayName ?? "",
            Password = Password ?? "",
            Token = Token ?? "",
            Kind = Kind,
            Appearance = Appearance?.Clone(),
            ClientName = ClientName ?? "",
        };
    }

    /// <summary>
    /// Tunables and dependencies of a <see cref="TinyverseConnection"/>. Read once when the
    /// connection is constructed.
    /// </summary>
    public sealed class ConnectionSettings
    {
        /// <summary>Creates one transport per connection attempt.</summary>
        public Func<IWebSocketTransport> TransportFactory { get; set; } = () => new ClientWebSocketTransport();

        public IClock Clock { get; set; } = SystemClock.Instance;

        /// <summary>Limit for opening the WebSocket.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Limit from sending Hello to receiving Welcome.</summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Default timeout of <see cref="TinyverseConnection.RequestAsync"/>.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>A connection with no frame from the server for this long is treated as dead.</summary>
        public TimeSpan DeadConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>How often the keepalive loop checks the two intervals above.</summary>
        public TimeSpan KeepaliveCheckInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Attempts after an unexpected drop; 0 disables reconnecting.</summary>
        public int MaxReconnectAttempts { get; set; } = 8;

        /// <summary>Delay before the first reconnect attempt; doubles for each further attempt.</summary>
        public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Receives exceptions thrown by event handlers during <see cref="TinyverseConnection.Pump"/>.</summary>
        public Action<Exception> HandlerExceptionLogger { get; set; } = e => UnityEngine.Debug.LogException(e);

        internal ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();
    }
}
