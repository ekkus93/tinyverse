using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using P = Tinyverse.Proto;

namespace Tinyverse.Net
{
    /// <summary>
    /// The client side of one Tinyverse session over one WebSocket (reconnecting when it drops).
    /// <para>
    /// Threading: receiving, sending and the keepalive run on the thread pool. Everything they
    /// produce is queued, and all events are raised from <see cref="Pump"/> on the caller's thread
    /// (call it once per frame on the Unity main thread). <see cref="Send"/>, the convenience
    /// senders and <see cref="RequestAsync"/> may be called from any thread. This class never
    /// touches Unity APIs except through <see cref="ConnectionSettings.HandlerExceptionLogger"/>,
    /// which is only called from <see cref="Pump"/>.
    /// </para>
    /// </summary>
    public sealed class TinyverseConnection : IDisposable
    {
        public const uint ProtocolVersion = 1;

        // ---- events (all raised from Pump) -------------------------------------------------

        public event Action<ConnectionState> StateChanged;

        /// <summary>A handshake succeeded: after ConnectAsync and after every reconnect. The
        /// snapshot state has just been reset; its chunks follow.</summary>
        public event Action<P.Welcome> Welcomed;

        /// <summary>Every server message after Welcome, including Pong and Ack, before its typed event.</summary>
        public event Action<P.ServerMessage> MessageReceived;

        // Typed events: (body, reply_to). reply_to is 0 unless the message answers one of our seqs.
        public event Action<P.Snapshot, uint> Snapshot;
        public event Action<P.AvatarJoined, uint> AvatarJoined;
        public event Action<P.AvatarLeft, uint> AvatarLeft;
        public event Action<P.AvatarStates, uint> AvatarStates;
        public event Action<P.AvatarCorrection, uint> AvatarCorrection;
        public event Action<P.AvatarUpdated, uint> AvatarUpdated;
        public event Action<P.ChatMessage, uint> ChatMessage;
        public event Action<P.ChatHistory, uint> ChatHistory;
        public event Action<P.ObjectCreated, uint> ObjectCreated;
        public event Action<P.ObjectUpdated, uint> ObjectUpdated;
        public event Action<P.ObjectDeleted, uint> ObjectDeleted;
        public event Action<P.Observation, uint> Observation;
        public event Action<P.Error, uint> Error;
        public event Action<P.Kicked, uint> Kicked;

        /// <summary>Raised after the Snapshot event of the chunk flagged <c>last</c>.</summary>
        public event Action<SnapshotState> SnapshotComplete;

        /// <summary>A reconnect attempt (1-based) will start after the given delay.</summary>
        public event Action<int, TimeSpan> ReconnectScheduled;

        /// <summary>
        /// The session ended for good: (reason, wasKicked). Raised once per session that reached
        /// Connected — after Kicked, Close(), an authentication refusal on reconnect, or when
        /// reconnecting gives up. Not raised when ConnectAsync itself fails (it throws instead).
        /// </summary>
        public event Action<string, bool> Disconnected;

        // ---- state -------------------------------------------------------------------------

        readonly ConnectionSettings settings;
        readonly IClock clock;
        readonly object gate = new object();
        readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();
        readonly ConcurrentDictionary<uint, TaskCompletionSource<P.ServerMessage>> pending =
            new ConcurrentDictionary<uint, TaskCompletionSource<P.ServerMessage>>();

        // Guarded by gate.
        CancellationTokenSource sessionCts;
        ConnectOptions options;
        Uri uri;
        volatile Link link;
        bool everConnected;
        bool disposed;

        volatile ConnectionState state = ConnectionState.Disconnected;
        volatile P.Welcome welcome;
        volatile P.Appearance appearance;
        int seq;

        // Clock sync, guarded by timeLock.
        readonly object timeLock = new object();
        readonly Queue<(long rtt, long offset)> samples = new Queue<(long, long)>();
        long rttMs = -1;
        long offsetMs;

        SnapshotState snapshotState = new SnapshotState();

        public TinyverseConnection() : this(new ConnectionSettings()) { }

        public TinyverseConnection(ConnectionSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.settings = settings.Clone();
            if (this.settings.TransportFactory == null) throw new ArgumentException("TransportFactory is null", nameof(settings));
            clock = this.settings.Clock ?? SystemClock.Instance;
        }

        // ---- public properties -------------------------------------------------------------

        /// <summary>The current state. Updated immediately; StateChanged follows on the next Pump.</summary>
        public ConnectionState State => state;

        /// <summary>The latest Welcome, or null before the first one.</summary>
        public P.Welcome Welcome => welcome;

        /// <summary>The snapshot assembled from the chunks pumped since the latest Welcome. Main thread only.</summary>
        public SnapshotState SnapshotState => snapshotState;

        /// <summary>Latest measured round trip in milliseconds, or -1 before the first Pong.</summary>
        public long RoundTripTimeMs
        {
            get { lock (timeLock) return rttMs; }
        }

        /// <summary>
        /// Estimated server clock minus local clock, in milliseconds (server time ≈ local Unix time + offset).
        /// Taken from the Pong with the lowest round trip among the last 8; from Welcome until then.
        /// </summary>
        public long ServerClockOffsetMs
        {
            get { lock (timeLock) return offsetMs; }
        }

        /// <summary>Current server time estimate in Unix milliseconds.</summary>
        public long EstimatedServerTimeMs => clock.UnixTimeMs + ServerClockOffsetMs;

        // ---- connect -----------------------------------------------------------------------

        /// <summary>
        /// Opens the socket, sends Hello, waits for Welcome and completes with it. Throws
        /// <see cref="TinyverseServerException"/> when the server refuses the Hello,
        /// <see cref="TimeoutException"/> on connect or handshake timeout, and
        /// <see cref="OperationCanceledException"/> when cancelled or closed meanwhile, and
        /// <see cref="TinyverseCertificateException"/> when a <c>wss://</c> server's certificate is refused
        /// (the URL may pin one with <c>#sha256=…</c>, see <see cref="CertificatePin"/>).
        /// Only valid in state Disconnected.
        /// </summary>
        public async Task<P.Welcome> ConnectAsync(ConnectOptions connectOptions, CancellationToken cancellationToken = default)
        {
            if (connectOptions == null) throw new ArgumentNullException(nameof(connectOptions));
            if (!Uri.TryCreate(connectOptions.Url, UriKind.Absolute, out var target) ||
                (target.Scheme != "ws" && target.Scheme != "wss"))
                throw new ArgumentException($"Not a ws:// or wss:// URL: \"{connectOptions.Url}\"", nameof(connectOptions));
            if (!CertificatePin.TrySplit(connectOptions.Url, out _, out _, out var pinError))
                throw new ArgumentException(pinError, nameof(connectOptions));

            CancellationTokenSource session;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TinyverseConnection));
                if (state != ConnectionState.Disconnected)
                    throw new InvalidOperationException($"ConnectAsync called in state {state}.");
                session = sessionCts = new CancellationTokenSource();
                options = connectOptions.Clone();
                uri = target;
                appearance = options.Appearance;
                welcome = null;
                everConnected = false;
                lock (timeLock)
                {
                    samples.Clear();
                    rttMs = -1;
                }
                SetState(ConnectionState.Connecting);
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token))
            {
                try
                {
                    var (newLink, newWelcome) = await HandshakeAsync(options.Token, true, linked.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (sessionCts == session && !session.IsCancellationRequested)
                        {
                            Activate(newLink, newWelcome);
                            return newWelcome;
                        }
                    }
                    DisposeTransport(newLink.Transport);
                    throw new OperationCanceledException("The connection was closed while connecting.");
                }
                catch (Exception)
                {
                    lock (gate)
                    {
                        if (sessionCts == session)
                        {
                            sessionCts = null;
                            SetState(ConnectionState.Disconnected);
                        }
                    }
                    throw;
                }
            }
        }

        async Task<(Link, P.Welcome)> HandshakeAsync(string token, bool initial, CancellationToken ct)
        {
            var transport = settings.TransportFactory();
            try
            {
                await WithTimeout(async t => { await transport.ConnectAsync(uri, t).ConfigureAwait(false); return true; },
                    settings.ConnectTimeout, "Connecting", ct).ConfigureAwait(false);

                if (initial)
                {
                    lock (gate)
                    {
                        ct.ThrowIfCancellationRequested();
                        SetState(ConnectionState.Joining);
                    }
                }

                var hello = new P.Hello
                {
                    ProtocolVersion = ProtocolVersion,
                    Kind = options.Kind,
                    Name = options.DisplayName,
                    Password = options.Password,
                    Token = token ?? "",
                    ClientName = options.ClientName,
                };
                var currentAppearance = appearance;
                if (currentAppearance != null) hello.Appearance = currentAppearance.Clone();
                var helloBytes = new P.ClientMessage { Seq = NextSeq(), Hello = hello }.ToByteArray();

                var w = await WithTimeout(async t =>
                {
                    await transport.SendBinaryAsync(helloBytes, t).ConfigureAwait(false);
                    while (true)
                    {
                        var frame = await transport.ReceiveAsync(t).ConfigureAwait(false);
                        if (frame.Type == WebSocketFrameType.Close)
                            throw new TinyverseConnectionException(
                                "The server closed the connection during the handshake" +
                                (string.IsNullOrEmpty(frame.CloseDescription) ? "." : $": {frame.CloseDescription}"));
                        if (frame.Type != WebSocketFrameType.Binary) continue;

                        var msg = P.ServerMessage.Parser.ParseFrom(frame.Data);
                        switch (msg.BodyCase)
                        {
                            case P.ServerMessage.BodyOneofCase.Welcome:
                                return msg.Welcome;
                            case P.ServerMessage.BodyOneofCase.Error:
                                throw new TinyverseServerException(msg.Error.Code, msg.Error.Message);
                            case P.ServerMessage.BodyOneofCase.Kicked:
                                throw new TinyverseConnectionException($"Kicked during the handshake: {msg.Kicked.Reason}");
                            case P.ServerMessage.BodyOneofCase.Pong:
                                continue;
                            default:
                                throw new TinyverseConnectionException(
                                    $"Expected Welcome from the server but got {msg.BodyCase}.");
                        }
                    }
                }, settings.HandshakeTimeout, "The handshake", ct).ConfigureAwait(false);

                return (new Link(transport), w);
            }
            catch
            {
                transport.Abort();
                DisposeTransport(transport);
                throw;
            }
        }

        // Caller holds gate.
        void Activate(Link l, P.Welcome w)
        {
            link = l;
            welcome = w;
            everConnected = true;
            long now = clock.MonotonicMs;
            l.LastReceivedMs = now;
            l.LastPingSentMs = now - (long)settings.PingInterval.TotalMilliseconds; // ping at once
            lock (timeLock)
            {
                if (samples.Count == 0) offsetMs = w.ServerTimeMs - clock.UnixTimeMs;
            }

            SetState(ConnectionState.Connected);
            inbox.Enqueue(() =>
            {
                snapshotState = new SnapshotState();
                Welcomed?.Invoke(w);
            });

            l.Receiver = Task.Run(() => ReceiveLoop(l));
            l.Writer = Task.Run(() => WriteLoop(l));
            l.Keepalive = Task.Run(() => KeepaliveLoop(l));
        }

        // ---- background loops --------------------------------------------------------------

        async Task ReceiveLoop(Link l)
        {
            string reason = "The connection was lost.";
            try
            {
                while (true)
                {
                    var frame = await l.Transport.ReceiveAsync(l.Cts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref l.LastReceivedMs, clock.MonotonicMs);
                    if (frame.Type == WebSocketFrameType.Close)
                    {
                        reason = string.IsNullOrEmpty(frame.CloseDescription)
                            ? "The server closed the connection."
                            : $"The server closed the connection: {frame.CloseDescription}";
                        break;
                    }
                    if (frame.Type != WebSocketFrameType.Binary) continue;

                    P.ServerMessage msg;
                    try
                    {
                        msg = P.ServerMessage.Parser.ParseFrom(frame.Data);
                    }
                    catch (InvalidProtocolBufferException)
                    {
                        continue; // Skip undecodable frames rather than drop the session.
                    }
                    OnReceived(l, msg);
                }
            }
            catch (Exception e)
            {
                reason = l.LostReason ?? $"The connection was lost: {e.Message}";
            }
            ConnectionLost(l, l.LostReason ?? reason);
        }

        void OnReceived(Link l, P.ServerMessage msg)
        {
            switch (msg.BodyCase)
            {
                case P.ServerMessage.BodyOneofCase.Kicked:
                    l.KickReason = msg.Kicked.Reason ?? "";
                    break;
                case P.ServerMessage.BodyOneofCase.Pong:
                    OnPong(msg.Pong);
                    break;
            }
            inbox.Enqueue(() => Dispatch(msg));
        }

        void OnPong(P.Pong pong)
        {
            long now = clock.UnixTimeMs;
            long rtt = now - pong.ClientTimeMs;
            if (rtt < 0) return;
            long offset = pong.ServerTimeMs + rtt / 2 - now;
            lock (timeLock)
            {
                rttMs = rtt;
                samples.Enqueue((rtt, offset));
                while (samples.Count > 8) samples.Dequeue();
                long best = long.MaxValue;
                foreach (var s in samples)
                {
                    if (s.rtt < best)
                    {
                        best = s.rtt;
                        offsetMs = s.offset;
                    }
                }
            }
        }

        async Task WriteLoop(Link l)
        {
            try
            {
                while (true)
                {
                    await l.Queue.WaitAsync(l.Cts.Token).ConfigureAwait(false);
                    while (l.Queue.TryDequeue(out var frame))
                        await l.Transport.SendBinaryAsync(frame, l.Cts.Token).ConfigureAwait(false);
                    if (l.Queue.IsCompleted) return;
                }
            }
            catch (Exception e)
            {
                if (!l.Cts.IsCancellationRequested) ConnectionLost(l, $"Sending failed: {e.Message}");
            }
        }

        async Task KeepaliveLoop(Link l)
        {
            long pingMs = (long)settings.PingInterval.TotalMilliseconds;
            long deadMs = (long)settings.DeadConnectionTimeout.TotalMilliseconds;
            try
            {
                while (!l.Cts.IsCancellationRequested)
                {
                    long now = clock.MonotonicMs;
                    if (now - Interlocked.Read(ref l.LastReceivedMs) >= deadMs)
                    {
                        l.LostReason = $"No data from the server for {settings.DeadConnectionTimeout.TotalSeconds:0.#} s.";
                        l.Transport.Abort();
                        ConnectionLost(l, l.LostReason);
                        return;
                    }
                    if (now - l.LastPingSentMs >= pingMs)
                    {
                        l.LastPingSentMs = now;
                        var ping = new P.ClientMessage { Ping = new P.Ping { ClientTimeMs = clock.UnixTimeMs } };
                        l.Queue.Enqueue(ping.ToByteArray(), false);
                    }
                    await clock.Delay(settings.KeepaliveCheckInterval, l.Cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                ConnectionLost(l, $"Keepalive failed: {e.Message}");
            }
        }

        // ---- loss and reconnect ------------------------------------------------------------

        void ConnectionLost(Link l, string reason)
        {
            if (Interlocked.Exchange(ref l.Lost, 1) != 0) return;
            CancelQuietly(l.Cts);
            l.Transport.Abort();
            DisposeTransport(l.Transport);
            FailPending(new TinyverseConnectionException(reason));

            CancellationTokenSource session = null;
            lock (gate)
            {
                if (link != l) return; // Closed or superseded meanwhile.
                link = null;
                if (sessionCts == null) return;

                if (l.KickReason != null)
                {
                    FinalDisconnect(string.IsNullOrEmpty(l.KickReason) ? "Kicked by the host." : l.KickReason, true);
                }
                else if (settings.MaxReconnectAttempts <= 0)
                {
                    FinalDisconnect(reason, false);
                }
                else
                {
                    SetState(ConnectionState.Reconnecting);
                    session = sessionCts;
                }
            }
            if (session != null) Task.Run(() => ReconnectLoop(session, reason));
        }

        async Task ReconnectLoop(CancellationTokenSource session, string lastReason)
        {
            for (int attempt = 1; attempt <= settings.MaxReconnectAttempts; attempt++)
            {
                var delay = BackoffDelay(attempt);
                int n = attempt;
                inbox.Enqueue(() => ReconnectScheduled?.Invoke(n, delay));
                try
                {
                    await clock.Delay(delay, session.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var w = welcome;
                string token = w != null && !string.IsNullOrEmpty(w.SessionToken) ? w.SessionToken : options.Token;
                try
                {
                    var (newLink, newWelcome) = await HandshakeAsync(token, false, session.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (sessionCts == session && !session.IsCancellationRequested)
                        {
                            Activate(newLink, newWelcome);
                            return;
                        }
                    }
                    DisposeTransport(newLink.Transport);
                    return;
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {
                    return;
                }
                catch (TinyverseServerException e) when (IsFatalRefusal(e.Code))
                {
                    lock (gate)
                    {
                        if (sessionCts == session) FinalDisconnect(e.Message, false);
                    }
                    return;
                }
                catch (TinyverseCertificateException e)
                {
                    // The server now presents another certificate: retrying will not help.
                    lock (gate)
                    {
                        if (sessionCts == session) FinalDisconnect(e.Message, false);
                    }
                    return;
                }
                catch (Exception e)
                {
                    lastReason = e.Message;
                }
            }
            lock (gate)
            {
                if (sessionCts == session)
                    FinalDisconnect($"Could not reconnect after {settings.MaxReconnectAttempts} attempts: {lastReason}", false);
            }
        }

        TimeSpan BackoffDelay(int attempt)
        {
            double ms = settings.ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
            return TimeSpan.FromMilliseconds(Math.Min(ms, settings.ReconnectMaxDelay.TotalMilliseconds));
        }

        static bool IsFatalRefusal(P.ErrorCode code) =>
            code == P.ErrorCode.AuthFailed || code == P.ErrorCode.InvalidToken ||
            code == P.ErrorCode.Banned || code == P.ErrorCode.UnsupportedVersion;

        // Caller holds gate.
        void FinalDisconnect(string reason, bool kicked)
        {
            var session = sessionCts;
            sessionCts = null;
            SetState(ConnectionState.Disconnected);
            if (everConnected) inbox.Enqueue(() => Disconnected?.Invoke(reason, kicked));
            if (session != null) Task.Run(() => CancelQuietly(session));
        }

        // ---- close -------------------------------------------------------------------------

        /// <summary>Ends the session: no reconnect follows. Safe to call at any time; never throws.</summary>
        public void Close()
        {
            try
            {
                Link l;
                CancellationTokenSource session;
                lock (gate)
                {
                    session = sessionCts;
                    l = link;
                    if (session == null && l == null) return;
                    sessionCts = null;
                    link = null;
                    SetState(ConnectionState.Disconnected);
                    if (everConnected) inbox.Enqueue(() => Disconnected?.Invoke("Closed by the client.", false));
                }
                CancelQuietly(session);
                FailPending(new TinyverseConnectionException("The connection was closed."));
                if (l != null && Interlocked.Exchange(ref l.Lost, 1) == 0) Task.Run(() => ShutdownAsync(l));
            }
            catch
            {
                // Close never throws.
            }
        }

        static async Task ShutdownAsync(Link l)
        {
            try
            {
                l.Queue.Complete();
                await Task.WhenAny(l.Writer ?? Task.CompletedTask, Task.Delay(500)).ConfigureAwait(false);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                {
                    var close = l.Transport.CloseAsync("closed by client", cts.Token);
                    await Task.WhenAny(close, Task.Delay(1000)).ConfigureAwait(false);
                    ObserveFault(close);
                }
            }
            catch
            {
                // Best effort.
            }
            finally
            {
                CancelQuietly(l.Cts);
                l.Transport.Abort();
                DisposeTransport(l.Transport);
            }
        }

        /// <summary>Same as <see cref="Close"/>; the instance cannot connect again afterwards.</summary>
        public void Dispose()
        {
            Close();
            lock (gate) disposed = true;
        }

        // ---- pump --------------------------------------------------------------------------

        /// <summary>Raises all queued events on the calling thread. Call once per frame.</summary>
        public void Pump()
        {
            int n = inbox.Count;
            for (int i = 0; i < n && inbox.TryDequeue(out var action); i++)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    try { settings.HandlerExceptionLogger?.Invoke(e); } catch { /* ignore */ }
                }
            }
        }

        void Dispatch(P.ServerMessage msg)
        {
            uint r = msg.ReplyTo;
            if (r != 0 && pending.TryRemove(r, out var tcs))
            {
                if (msg.BodyCase == P.ServerMessage.BodyOneofCase.Error)
                    tcs.TrySetException(new TinyverseServerException(msg.Error.Code, msg.Error.Message, r));
                else
                    tcs.TrySetResult(msg);
            }

            MessageReceived?.Invoke(msg);
            switch (msg.BodyCase)
            {
                case P.ServerMessage.BodyOneofCase.Snapshot:
                    var assembled = snapshotState;
                    assembled.Add(msg.Snapshot);
                    Snapshot?.Invoke(msg.Snapshot, r);
                    if (msg.Snapshot.Last) SnapshotComplete?.Invoke(assembled);
                    break;
                case P.ServerMessage.BodyOneofCase.AvatarJoined: AvatarJoined?.Invoke(msg.AvatarJoined, r); break;
                case P.ServerMessage.BodyOneofCase.AvatarLeft: AvatarLeft?.Invoke(msg.AvatarLeft, r); break;
                case P.ServerMessage.BodyOneofCase.AvatarStates: AvatarStates?.Invoke(msg.AvatarStates, r); break;
                case P.ServerMessage.BodyOneofCase.AvatarCorrection: AvatarCorrection?.Invoke(msg.AvatarCorrection, r); break;
                case P.ServerMessage.BodyOneofCase.AvatarUpdated: AvatarUpdated?.Invoke(msg.AvatarUpdated, r); break;
                case P.ServerMessage.BodyOneofCase.ChatMessage: ChatMessage?.Invoke(msg.ChatMessage, r); break;
                case P.ServerMessage.BodyOneofCase.ChatHistory: ChatHistory?.Invoke(msg.ChatHistory, r); break;
                case P.ServerMessage.BodyOneofCase.ObjectCreated: ObjectCreated?.Invoke(msg.ObjectCreated, r); break;
                case P.ServerMessage.BodyOneofCase.ObjectUpdated: ObjectUpdated?.Invoke(msg.ObjectUpdated, r); break;
                case P.ServerMessage.BodyOneofCase.ObjectDeleted: ObjectDeleted?.Invoke(msg.ObjectDeleted, r); break;
                case P.ServerMessage.BodyOneofCase.Observation: Observation?.Invoke(msg.Observation, r); break;
                case P.ServerMessage.BodyOneofCase.Error: Error?.Invoke(msg.Error, r); break;
                case P.ServerMessage.BodyOneofCase.Kicked: Kicked?.Invoke(msg.Kicked, r); break;
            }
        }

        // ---- sending -----------------------------------------------------------------------

        /// <summary>
        /// Queues a message. Sets and returns its seq. Returns 0 (and drops the message) when not
        /// Connected, e.g. while reconnecting. Any thread.
        /// </summary>
        public uint Send(P.ClientMessage message)
        {
            Validate(message);
            var l = link;
            if (l == null) return 0;
            uint s = NextSeq();
            message.Seq = s;
            if (message.BodyCase == P.ClientMessage.BodyOneofCase.SetAppearance && message.SetAppearance.Appearance != null)
                appearance = message.SetAppearance.Appearance.Clone();
            return l.Queue.Enqueue(message.ToByteArray(), message.BodyCase == P.ClientMessage.BodyOneofCase.AvatarState) ? s : 0;
        }

        /// <summary>
        /// Sends a message and completes with the first server message whose reply_to matches its
        /// seq (completed from Pump, before that message's events are raised). Throws
        /// <see cref="TinyverseServerException"/> for an Error reply, <see cref="TimeoutException"/>
        /// after the timeout (default <see cref="ConnectionSettings.RequestTimeout"/>), and
        /// <see cref="TinyverseConnectionException"/> when not connected or the connection drops.
        /// </summary>
        public Task<P.ServerMessage> RequestAsync(P.ClientMessage message, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Validate(message);
            var l = link;
            if (l == null)
                return Task.FromException<P.ServerMessage>(new TinyverseConnectionException("Not connected."));

            var tcs = new TaskCompletionSource<P.ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            uint s = NextSeq();
            message.Seq = s;
            pending[s] = tcs;
            if (message.BodyCase == P.ClientMessage.BodyOneofCase.SetAppearance && message.SetAppearance.Appearance != null)
                appearance = message.SetAppearance.Appearance.Clone();
            if (!l.Queue.Enqueue(message.ToByteArray(), false))
            {
                pending.TryRemove(s, out _);
                return Task.FromException<P.ServerMessage>(new TinyverseConnectionException("Not connected."));
            }

            var limit = timeout ?? settings.RequestTimeout;
            var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            clock.Delay(limit, timer.Token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    if (cancellationToken.IsCancellationRequested && pending.TryRemove(s, out _))
                        tcs.TrySetCanceled(cancellationToken);
                }
                else if (pending.TryRemove(s, out _))
                {
                    tcs.TrySetException(new TimeoutException(
                        $"No reply to {message.BodyCase} (seq {s}) within {limit.TotalSeconds:0.#} s."));
                }
            }, TaskScheduler.Default);
            tcs.Task.ContinueWith(_ =>
            {
                CancelQuietly(timer);
                timer.Dispose();
            }, TaskScheduler.Default);
            return tcs.Task;
        }

        static void Validate(P.ClientMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (message.BodyCase == P.ClientMessage.BodyOneofCase.None)
                throw new ArgumentException("The message has no body.", nameof(message));
            if (message.BodyCase == P.ClientMessage.BodyOneofCase.Hello)
                throw new ArgumentException("Hello is sent by ConnectAsync.", nameof(message));
        }

        public uint SendAvatarState(P.AvatarTransform transform) =>
            Send(new P.ClientMessage { AvatarState = new P.AvatarState { Transform = transform } });

        public uint SendChat(P.ChatChannel channel, string text) =>
            Send(new P.ClientMessage { Chat = new P.Chat { Channel = channel, Text = text ?? "" } });

        public uint MoveTo(P.Vec3 target, P.MoveMode mode = P.MoveMode.Walk) =>
            Send(new P.ClientMessage { MoveTo = new P.MoveTo { Target = target, Mode = mode } });

        public uint Stop() => Send(new P.ClientMessage { Stop = new P.Stop() });

        public uint RequestChatHistory(long sinceMs = 0, uint limit = 0) =>
            Send(new P.ClientMessage { ChatHistoryRequest = new P.ChatHistoryRequest { SinceMs = sinceMs, Limit = limit } });

        public uint CreateObject(P.ObjectSpec spec) =>
            Send(new P.ClientMessage { ObjectCreate = new P.ObjectCreate { Spec = spec } });

        public uint UpdateObject(P.ObjectUpdate update) => Send(new P.ClientMessage { ObjectUpdate = update });

        public uint DeleteObject(ulong id) =>
            Send(new P.ClientMessage { ObjectDelete = new P.ObjectDelete { Id = id } });

        public uint Observe(float radius = 0) =>
            Send(new P.ClientMessage { Observe = new P.Observe { Radius = radius } });

        /// <summary>Also remembered for the Hello of any reconnect.</summary>
        public uint SetAppearance(P.Appearance appearance) =>
            Send(new P.ClientMessage { SetAppearance = new P.SetAppearance { Appearance = appearance } });

        public uint Kick(string userId, string reason = "") =>
            Send(Admin(new P.Admin { Kick = new P.Admin.Types.Kick { UserId = userId ?? "", Reason = reason ?? "" } }));

        public uint Ban(string userId, string reason = "") =>
            Send(Admin(new P.Admin { Ban = new P.Admin.Types.Ban { UserId = userId ?? "", Reason = reason ?? "" } }));

        public uint Unban(string userId) =>
            Send(Admin(new P.Admin { Unban = new P.Admin.Types.Unban { UserId = userId ?? "" } }));

        public uint SetRole(string userId, P.Role role) =>
            Send(Admin(new P.Admin { SetRole = new P.Admin.Types.SetRole { UserId = userId ?? "", Role = role } }));

        static P.ClientMessage Admin(P.Admin admin) => new P.ClientMessage { Admin = admin };

        // ---- helpers -----------------------------------------------------------------------

        uint NextSeq()
        {
            while (true)
            {
                uint s = unchecked((uint)Interlocked.Increment(ref seq));
                if (s != 0) return s;
            }
        }

        // Caller holds gate.
        void SetState(ConnectionState s)
        {
            if (state == s) return;
            state = s;
            inbox.Enqueue(() => StateChanged?.Invoke(s));
        }

        void FailPending(Exception e)
        {
            foreach (var key in pending.Keys)
                if (pending.TryRemove(key, out var tcs)) tcs.TrySetException(e);
        }

        async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, string what,
            CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var op = operation(cts.Token);
                var timer = clock.Delay(timeout, cts.Token);
                var done = await Task.WhenAny(op, timer).ConfigureAwait(false);
                cts.Cancel();
                if (done != op)
                {
                    ObserveFault(op);
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException($"{what} timed out after {timeout.TotalSeconds:0.#} s.");
                }
                ObserveFault(timer);
                return await op.ConfigureAwait(false);
            }
        }

        static void ObserveFault(Task t) =>
            t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        static void CancelQuietly(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); } catch { /* disposed or callback threw */ }
        }

        static void DisposeTransport(IWebSocketTransport t)
        {
            try { t.Dispose(); } catch { /* never throws */ }
        }

        sealed class Link
        {
            public readonly IWebSocketTransport Transport;
            public readonly CancellationTokenSource Cts = new CancellationTokenSource();
            public readonly SendQueue Queue = new SendQueue();
            public long LastReceivedMs;
            public long LastPingSentMs;
            public volatile string KickReason;
            public volatile string LostReason;
            public int Lost;
            public Task Receiver, Writer, Keepalive;

            public Link(IWebSocketTransport transport) => Transport = transport;
        }
    }
}
