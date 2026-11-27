using System;
using System.Collections.Generic;

namespace Tinyverse.Host
{
    /// <summary>Lifecycle of a hosted server, as reported by <see cref="ServerProcess.StateChanged"/>.</summary>
    public enum HostState
    {
        Stopped,
        /// <summary>Running <c>init</c> (new world) or <c>password</c> (existing world) before serving.</summary>
        Initialising,
        /// <summary><c>run</c> has been started; waiting for its "listening on" line.</summary>
        Starting,
        Running,
        Stopping,
        /// <summary>Start failed or the server exited unexpectedly; see <see cref="ServerProcess.LastError"/>.</summary>
        Failed,
    }

    /// <summary>What the player chose on the Host screen.</summary>
    public sealed class HostSettings
    {
        public const int DefaultPort = 7878;
        public const string DefaultBindAddress = "0.0.0.0";

        /// <summary>The world directory. Created with <c>init</c> if it does not contain <c>world.toml</c> yet.</summary>
        public string WorldDirectory { get; set; }

        /// <summary>Name for a new world. Ignored for an existing world.</summary>
        public string WorldName { get; set; }

        /// <summary>Description for a new world. Ignored for an existing world.</summary>
        public string Description { get; set; }

        public int Port { get; set; } = DefaultPort;

        public string BindAddress { get; set; } = DefaultBindAddress;

        /// <summary>
        /// Join password. For a new world: null or empty means no password.
        /// For an existing world: null leaves the password unchanged, empty clears it,
        /// anything else sets it (through <c>tinyverse-server password</c>).
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// Serve plain <c>ws://</c> even if the world folder holds <c>cert.pem</c> and <c>key.pem</c> (passes <c>--no-tls</c>).
        /// </summary>
        public bool DisableTls { get; set; }

        /// <summary>Never includes the password.</summary>
        public override string ToString() =>
            $"HostSettings(dir={WorldDirectory}, name={WorldName}, port={Port}, bind={BindAddress}, tls={(DisableTls ? "off" : "auto")}, password={(Password == null ? "unchanged" : Password.Length == 0 ? "none" : "set")})";
    }

    /// <summary>Returned by <see cref="ServerProcess.StartAsync"/> once the server is listening.</summary>
    public sealed class HostStartResult
    {
        internal HostStartResult(string localUrl, bool tls, int port, IReadOnlyList<string> lanAddresses,
            IReadOnlyList<string> lanUrls, string ownerToken, string worldDirectory)
        {
            LocalUrl = localUrl;
            Tls = tls;
            Port = port;
            LanAddresses = lanAddresses;
            LanUrls = lanUrls;
            OwnerToken = ownerToken;
            WorldDirectory = worldDirectory;
        }

        /// <summary>
        /// The URL this client connects to, e.g. <c>ws://127.0.0.1:7878/ws</c>, or <c>wss://127.0.0.1:7878/ws</c>
        /// with TLS (127.0.0.1 is covered by the server's self-signed certificate by default).
        /// </summary>
        public string LocalUrl { get; }

        /// <summary>True if the server serves TLS (its ready line said <c>wss://</c>). All URLs then use <c>wss://</c>.</summary>
        public bool Tls { get; }

        /// <summary>The port the server reported in its ready line.</summary>
        public int Port { get; }

        /// <summary>
        /// This machine's IPv4 addresses for other people on the network, best first: show the first one as
        /// "friends join with". The default-route interface comes first, VPN/Docker/bridge interfaces last.
        /// Empty if bound to loopback; just the bind address if bound to one specific address.
        /// </summary>
        public IReadOnlyList<string> LanAddresses { get; }

        /// <summary>
        /// <see cref="LanAddresses"/> as <c>ws://addr:port/ws</c> (or <c>wss://</c>) URLs. With the default self-signed
        /// certificate, which covers only localhost and 127.0.0.1, other machines will see a name mismatch.
        /// </summary>
        public IReadOnlyList<string> LanUrls { get; }

        /// <summary>Contents of <c>owner.token</c> (trimmed), or null if the file is missing or unreadable.</summary>
        public string OwnerToken { get; }

        public string WorldDirectory { get; }

        /// <summary>Never includes the owner token.</summary>
        public override string ToString() =>
            $"HostStartResult(local={LocalUrl}, lan=[{string.Join(", ", LanUrls)}], token={(OwnerToken == null ? "missing" : "present")})";
    }

    /// <summary>Outcome of <see cref="ServerProcess.Stop"/> / <see cref="ServerProcess.StopAsync"/>.</summary>
    public sealed class StopResult
    {
        internal StopResult(bool wasRunning, bool graceful, bool killed, int? exitCode, string message)
        {
            WasRunning = wasRunning;
            Graceful = graceful;
            Killed = killed;
            ExitCode = exitCode;
            Message = message;
        }

        /// <summary>False if there was no server process to stop.</summary>
        public bool WasRunning { get; }

        /// <summary>The server exited by itself after the shutdown request (so it flushed the world).</summary>
        public bool Graceful { get; }

        /// <summary>The server did not exit within the timeout and was killed hard; recent changes may be lost.</summary>
        public bool Killed { get; }

        public int? ExitCode { get; }

        /// <summary>A sentence for the player.</summary>
        public string Message { get; }

        public override string ToString() => Message;
    }

    public enum HostErrorKind
    {
        /// <summary>The settings are unusable (missing directory, bad port).</summary>
        InvalidSettings,
        /// <summary>The server binary could not be started at all.</summary>
        LaunchFailed,
        /// <summary><c>init</c> (or <c>password</c>) exited non-zero or timed out.</summary>
        InitFailed,
        /// <summary>The port is already in use.</summary>
        PortInUse,
        /// <summary><c>run</c> exited before printing its ready line.</summary>
        ExitedBeforeReady,
        /// <summary><c>run</c> printed nothing that says it is ready within the timeout.</summary>
        ReadyTimeout,
        /// <summary>The server exited while running without being asked to.</summary>
        UnexpectedExit,
    }

    /// <summary>
    /// A hosting failure with a message meant for the player. Never contains the password or owner token.
    /// </summary>
    public sealed class HostException : Exception
    {
        public HostException(HostErrorKind kind, string message, IReadOnlyList<string> recentOutput = null, int? exitCode = null)
            : base(message)
        {
            Kind = kind;
            RecentOutput = recentOutput ?? Array.Empty<string>();
            ExitCode = exitCode;
        }

        public HostErrorKind Kind { get; }

        /// <summary>The last lines of server output (already redacted), also included in <see cref="Exception.Message"/>.</summary>
        public IReadOnlyList<string> RecentOutput { get; }

        public int? ExitCode { get; }
    }

    /// <summary>How the server is kept from outliving the client if the client dies without stopping it.</summary>
    [Flags]
    public enum OrphanGuard
    {
        None = 0,
        /// <summary>Linux: launch through <c>setpriv --pdeathsig TERM</c>, so the kernel sends SIGTERM when the client dies.</summary>
        ParentDeathSignal = 1,
        /// <summary>POSIX: a small <c>/bin/sh</c> watchdog polls the client pid and sends SIGTERM, then SIGKILL, when it is gone.</summary>
        Watchdog = 2,
        /// <summary>Everything available on this platform.</summary>
        Auto = ParentDeathSignal | Watchdog,
    }

    /// <summary>Tuning for <see cref="ServerProcess"/>. The defaults suit the game.</summary>
    public sealed class ServerProcessOptions
    {
        /// <summary>How long <c>init</c> or <c>password</c> may take.</summary>
        public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long <c>run</c> may take to print its "listening on" line.</summary>
        public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Graceful-stop timeout used by <see cref="ServerProcess.Dispose"/>, cancellation and failed starts.</summary>
        public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How many recent output lines are kept (and at most queued for <see cref="ServerProcess.Pump"/>).</summary>
        public int LogCapacity { get; set; } = 500;

        /// <summary>How many of the last output lines go into an error message.</summary>
        public int ErrorTailLines { get; set; } = 15;

        public OrphanGuard OrphanGuard { get; set; } = OrphanGuard.Auto;

        /// <summary>After the client is gone, how long the watchdog waits after SIGTERM before SIGKILL.</summary>
        public int WatchdogKillAfterSeconds { get; set; } = 30;

        /// <summary>Test seam: the pid the watchdog watches instead of this process.</summary>
        internal int? WatchdogClientPidOverride { get; set; }

        /// <summary>Test seam: extra environment variables for the server process.</summary>
        internal Dictionary<string, string> Environment { get; } = new Dictionary<string, string>();
    }
}
