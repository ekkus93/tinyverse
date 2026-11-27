using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Tinyverse.Host.Internal;
using Tinyverse.Host.Platform;

namespace Tinyverse.Host
{
    /// <summary>
    /// Starts, supervises and stops one <c>tinyverse-server</c> child process.
    /// <para>
    /// Threading: every method may be called from the main thread. Output is read on background threads;
    /// <see cref="LogLine"/>, <see cref="StateChanged"/> and <see cref="Failed"/> are only ever raised from
    /// <see cref="Pump"/>, which the owner calls once per frame on the main thread. <see cref="State"/> and
    /// <see cref="LastError"/> always show the latest values, which may be ahead of the events pumped so far.
    /// </para>
    /// <para>
    /// Secrets: the password and the owner token never appear in log lines, <see cref="LastError"/> or exception
    /// messages, and the command line is never logged.
    /// </para>
    /// </summary>
    public sealed class ServerProcess : IDisposable
    {
        private const string HostPrefix = "[host] ";

        private static readonly Regex PortInUsePattern = new Regex(
            @"(?i)address already in use|address in use|addrinuse|os error 98\b|os error 48\b|os error 10048\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string _serverPath;
        private readonly ServerProcessOptions _options;
        private readonly ServerPlatform _platform;

        // State, guarded by _lock.
        private readonly object _lock = new object();
        private HostState _state = HostState.Stopped;
        private string _lastError;
        private ChildProcess _current;
        private Process _watchdog;
        private bool _stopRequested;
        private bool _disposed;
        private HostStartResult _result;
        private Redactor _redactor = new Redactor();
        private TaskCompletionSource<(bool Tls, int Port)> _ready;
        private string _certificateSha256;
        private readonly List<ChildProcess> _launches = new List<ChildProcess>();

        // Serialises Stop calls.
        private readonly object _stopGate = new object();

        // Output and pending events, guarded by _eventLock.
        private readonly object _eventLock = new object();
        private readonly LineRing _recent;
        private readonly Queue<KeyValuePair<long, string>> _pendingLines = new Queue<KeyValuePair<long, string>>();
        private readonly Queue<PendingState> _pendingStates = new Queue<PendingState>();
        private long _sequence;
        private int _droppedLines;

        private struct PendingState
        {
            public long Sequence;
            public HostState State;
            public string Error;
        }

        /// <param name="serverBinaryPath">Path to <c>tinyverse-server</c>, usually from <see cref="ServerLocator"/>.</param>
        public ServerProcess(string serverBinaryPath, ServerProcessOptions options = null)
            : this(serverBinaryPath, options, ServerPlatform.Current)
        {
        }

        internal ServerProcess(string serverBinaryPath, ServerProcessOptions options, ServerPlatform platform)
        {
            if (string.IsNullOrWhiteSpace(serverBinaryPath)) throw new ArgumentException("A server binary path is required.", nameof(serverBinaryPath));
            _serverPath = Path.GetFullPath(serverBinaryPath);
            _options = options ?? new ServerProcessOptions();
            _platform = platform;
            _recent = new LineRing(Math.Max(1, _options.LogCapacity));
        }

        /// <summary>One line of server output (or a <c>[host]</c> note from this class). Raised from <see cref="Pump"/> only.</summary>
        public event Action<string> LogLine;

        /// <summary>Raised from <see cref="Pump"/> only, in order, once per transition.</summary>
        public event Action<HostState> StateChanged;

        /// <summary>Raised from <see cref="Pump"/> just before <c>StateChanged(Failed)</c>, with the message for the player.</summary>
        public event Action<string> Failed;

        public string ServerBinaryPath => _serverPath;

        /// <summary>The latest state (not necessarily pumped yet).</summary>
        public HostState State { get { lock (_lock) return _state; } }

        /// <summary>Why the last start failed or the server died; null otherwise. Cleared by the next start.</summary>
        public string LastError { get { lock (_lock) return _lastError; } }

        /// <summary>What <see cref="StartAsync"/> returned while the server is running; null otherwise.</summary>
        public HostStartResult Current { get { lock (_lock) return _state == HostState.Running ? _result : null; } }

        /// <summary>Pid of the current child (init, password or run), or null.</summary>
        public int? ProcessId { get { lock (_lock) return _current != null && !_current.HasExited ? _current.Pid : (int?)null; } }

        internal int? WatchdogProcessId
        {
            get
            {
                lock (_lock)
                {
                    try { return _watchdog != null && !_watchdog.HasExited ? _watchdog.Id : (int?)null; }
                    catch (Exception) { return null; }
                }
            }
        }

        /// <summary>Test seam: every child launched so far (file, arguments, whether the password was in its environment).</summary>
        internal IReadOnlyList<ChildProcess> Launches { get { lock (_lock) return _launches.ToArray(); } }

        /// <summary>A snapshot of the most recent output lines (at most <see cref="ServerProcessOptions.LogCapacity"/>), oldest first.</summary>
        public IReadOnlyList<string> GetRecentLines(int max = int.MaxValue)
        {
            lock (_eventLock) return _recent.Tail(max);
        }

        // ---------------------------------------------------------------- start

        /// <summary>
        /// Creates the world if needed (or applies a password change to an existing one), starts <c>run</c> and
        /// completes when the server reports it is listening.
        /// </summary>
        /// <exception cref="HostException">Start failed; the state is <see cref="HostState.Failed"/>.</exception>
        /// <exception cref="OperationCanceledException">Cancelled or stopped before ready; the state is <see cref="HostState.Stopped"/>.</exception>
        /// <exception cref="InvalidOperationException">Already starting or running.</exception>
        public Task<HostStartResult> StartAsync(HostSettings settings, CancellationToken cancellationToken = default)
        {
            // Usage errors throw synchronously; everything else is reported through the task.
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            string invalid = Validate(settings);
            bool needsTool = invalid == null &&
                (!WorldDirectories.IsWorld(Path.GetFullPath(settings.WorldDirectory)) || settings.Password != null);
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ServerProcess));
                if (_state != HostState.Stopped && _state != HostState.Failed)
                    throw new InvalidOperationException($"The server is already {_state.ToString().ToLowerInvariant()}.");
                _stopRequested = false;
                _lastError = null;
                _result = null;
                _redactor = new Redactor();
                _redactor.Add(settings.Password);
                // Claim the instance now so a second StartAsync cannot slip in.
                if (invalid == null) SetStateLocked(needsTool ? HostState.Initialising : HostState.Starting, null);
            }
            if (invalid != null)
                return Task.FromException<HostStartResult>(Fail(HostErrorKind.InvalidSettings, invalid, null, new List<string>()));
            return StartGuarded(settings, cancellationToken);
        }

        private async Task<HostStartResult> StartGuarded(HostSettings settings, CancellationToken cancellationToken)
        {
            try
            {
                return await StartCore(settings, Path.GetFullPath(settings.WorldDirectory), cancellationToken).ConfigureAwait(false);
            }
            catch (HostException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                throw Fail(HostErrorKind.LaunchFailed, "Could not start hosting: " + e.Message, null);
            }
        }

        private static string Validate(HostSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.WorldDirectory)) return "Choose a folder for the world.";
            if (s.Port < 1 || s.Port > 65535) return $"The port must be between 1 and 65535 (got {s.Port}).";
            if (string.IsNullOrWhiteSpace(s.BindAddress)) return "The bind address is empty.";
            try { Path.GetFullPath(s.WorldDirectory); }
            catch (Exception) { return "The world folder is not a valid path."; }
            return null;
        }

        private async Task<HostStartResult> StartCore(HostSettings settings, string dir, CancellationToken ct)
        {
            if (!WorldDirectories.IsWorld(dir))
            {
                SetState(HostState.Initialising);
                HostLog("Creating a new world in " + dir);
                string parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var args = new List<string> { "init", dir };
                if (!string.IsNullOrWhiteSpace(settings.WorldName)) { args.Add("--name"); args.Add(settings.WorldName.Trim()); }
                if (!string.IsNullOrWhiteSpace(settings.Description)) { args.Add("--description"); args.Add(settings.Description.Trim()); }
                // The password goes through the environment, never the command line (visible in the process list).
                string password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password;
                await RunToolAsync(args, dir, "create the world", password, ct).ConfigureAwait(false);
            }
            else if (settings.Password != null)
            {
                SetState(HostState.Initialising);
                var args = new List<string> { "password", dir };
                if (settings.Password.Length == 0) { HostLog("Removing the join password"); args.Add("--clear"); }
                else { HostLog("Setting the join password"); args.Add("--set-from-env"); }
                await RunToolAsync(args, dir, "change the world's password",
                    settings.Password.Length == 0 ? null : settings.Password, ct).ConfigureAwait(false);
            }

            ThrowIfStopRequested(ct);
            string token = ReadOwnerToken(dir);
            Redactor redactor;
            lock (_lock) redactor = _redactor;
            redactor.Add(token);
            if (token == null) HostLog("owner.token was not found in the world folder; you will join without owner rights.");

            SetState(HostState.Starting);
            HostLog($"Starting the server on port {settings.Port.ToString(CultureInfo.InvariantCulture)}");

            string file = _serverPath;
            var runArgs = new List<string>
            {
                "run", dir,
                "--port", settings.Port.ToString(CultureInfo.InvariantCulture),
                "--bind", settings.BindAddress.Trim(),
            };
            if (settings.DisableTls) runArgs.Add("--no-tls");
            else if (settings.CreateCertificate) runArgs.Add("--self-signed");
            bool parentDeath = (_options.OrphanGuard & OrphanGuard.ParentDeathSignal) != 0
                && _platform.TryWrapForParentDeath(ref file, runArgs);

            var ready = new TaskCompletionSource<(bool Tls, int Port)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _ready = ready;
                _certificateSha256 = null;
            }

            ChildProcess child;
            try
            {
                child = await ChildProcess.Launch(file, runArgs, dir, OnServerLine, "tinyverse-server-run", _options.Environment).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw Fail(HostErrorKind.LaunchFailed, $"Could not start the server ({_serverPath}): {e.Message}", null, new List<string>());
            }

            bool stoppedMeanwhile;
            lock (_lock)
            {
                _current = child;
                _launches.Add(child);
                stoppedMeanwhile = _stopRequested;
            }
            if (stoppedMeanwhile)
            {
                Terminate(child, _options.StopTimeout);
                throw new OperationCanceledException("Hosting was stopped before the server was ready.");
            }

            StartWatchdog(child);
            if (!parentDeath && (_options.OrphanGuard & OrphanGuard.ParentDeathSignal) != 0)
                HostLog("setpriv --pdeathsig is not available; relying on the watchdog to stop the server if the game crashes.");
            _ = child.Exited.ContinueWith(t => OnRunExited(child, t.Result), TaskScheduler.Default);

            Task timeout = Task.Delay(_options.ReadyTimeout, ct);
            Task winner = await Task.WhenAny(ready.Task, child.Exited, timeout).ConfigureAwait(false);

            if (winner == ready.Task)
            {
                // Cancelled by Stop: the state is (or is about to be) Stopped.
                if (ready.Task.IsCanceled) throw new OperationCanceledException("Hosting was stopped before the server was ready.");
                (bool tls, int port) = ready.Task.Result;
                HostStartResult result = BuildResult(settings, dir, tls, port, token);
                bool exited, stopped;
                lock (_lock)
                {
                    stopped = _stopRequested;
                    exited = child.HasExited;
                    if (!stopped && !exited)
                    {
                        _result = result;
                        SetStateLocked(HostState.Running, null);
                    }
                }
                if (stopped) throw new OperationCanceledException("Hosting was stopped before the server was ready.");
                if (exited)
                    throw Fail(HostErrorKind.UnexpectedExit,
                        $"The server stopped right after starting (exit code {child.Exited.Result}).", child.Exited.Result);
                // The best two network addresses; the rest are usually VPN and container networks.
                string network = string.Join(", ", System.Linq.Enumerable.Take(result.LanJoinUrls, 2)) +
                                 (result.LanJoinUrls.Count > 2 ? $" and {result.LanJoinUrls.Count - 2} more" : "");
                HostLog("The world is open at " + result.LocalUrl +
                        (result.LanJoinUrls.Count > 0 ? " (on the network: " + network + ")" : ""));
                return result;
            }

            if (winner == child.Exited)
            {
                ThrowIfStopRequested(ct);
                int code = child.Exited.Result;
                List<string> tail = GetTail();
                if (tail.Exists(l => PortInUsePattern.IsMatch(l)))
                    throw Fail(HostErrorKind.PortInUse,
                        $"Port {settings.Port} is already in use, so the server could not start. Another server or program is using it: choose a different port, or close that program.",
                        code, tail);
                throw Fail(HostErrorKind.ExitedBeforeReady, $"The server stopped before it was ready (exit code {code}).", code, tail);
            }

            // Timed out or cancelled.
            if (ct.IsCancellationRequested) throw CancelStart(ct);
            ThrowIfStopRequested(ct);
            Terminate(child, _options.StopTimeout);
            ThrowIfStopRequested(ct);
            throw Fail(HostErrorKind.ReadyTimeout,
                $"The server did not report that it was ready within {_options.ReadyTimeout.TotalSeconds:0.#} seconds, so it was stopped.",
                null);
        }

        /// <summary>Runs <c>init</c> or <c>password</c> to completion. Output is held back until the owner token is known.</summary>
        private async Task RunToolAsync(List<string> args, string dir, string purpose, string password, CancellationToken ct)
        {
            var buffered = new List<string>();
            Action<string> onLine = l => { lock (buffered) buffered.Add(l); };

            ChildProcess child;
            try
            {
                child = await ChildProcess.Launch(_serverPath, args, null, onLine, "tinyverse-server-tool", _options.Environment, password).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw Fail(HostErrorKind.LaunchFailed, $"Could not start the server ({_serverPath}): {e.Message}", null, new List<string>());
            }

            bool stopped;
            lock (_lock)
            {
                _current = child;
                _launches.Add(child);
                stopped = _stopRequested;
            }
            if (stopped) Terminate(child, TimeSpan.FromSeconds(2));

            Task timeout = Task.Delay(_options.InitTimeout, ct);
            Task winner = await Task.WhenAny(child.Exited, timeout).ConfigureAwait(false);
            bool timedOut = winner != child.Exited;
            if (timedOut) Terminate(child, TimeSpan.FromSeconds(2));

            // Learn the token (init may print it) before any of this output is shown.
            Redactor redactor;
            lock (_lock) redactor = _redactor;
            redactor.Add(ReadOwnerToken(dir));
            List<string> lines;
            lock (buffered) lines = new List<string>(buffered);
            var shown = new List<string>(lines.Count);
            foreach (string l in lines)
            {
                string r = redactor.Apply(l);
                shown.Add(r);
                EmitLine(r);
            }
            lock (_lock)
            {
                if (_current == child) _current = null;
            }

            if (ct.IsCancellationRequested) throw CancelStart(ct);
            ThrowIfStopRequested(ct);
            if (timedOut)
                throw Fail(HostErrorKind.InitFailed,
                    $"Could not {purpose}: the server did not finish within {_options.InitTimeout.TotalSeconds:0.#} seconds.",
                    null, Tail(shown));
            int code = child.Exited.Result;
            if (code != 0)
                throw Fail(HostErrorKind.InitFailed, $"Could not {purpose} (exit code {code}).", code, Tail(shown));
        }

        private HostStartResult BuildResult(HostSettings settings, string dir, bool tls, int port, string token)
        {
            string bind = settings.BindAddress.Trim();
            string local;
            List<string> lan;
            if (Addresses.IsWildcard(bind))
            {
                // 127.0.0.1 is also what the self-signed certificate covers by default.
                local = Addresses.Url(tls, "127.0.0.1", port);
                lan = Addresses.LanIPv4();
            }
            else if (Addresses.IsLoopback(bind))
            {
                local = Addresses.Url(tls, bind, port);
                lan = new List<string>();
            }
            else
            {
                // Bound to one interface: 127.0.0.1 would not reach it.
                local = Addresses.Url(tls, bind, port);
                lan = new List<string> { bind.Trim('[', ']') };
            }
            var urls = lan.ConvertAll(a => Addresses.Url(tls, a, port));
            string pin;
            lock (_lock) pin = _certificateSha256;
            return new HostStartResult(local, tls, port, lan.AsReadOnly(), urls.AsReadOnly(), token, dir, pin);
        }

        private static string ReadOwnerToken(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "owner.token");
                if (!File.Exists(path)) return null;
                string t = File.ReadAllText(path).Trim();
                return t.Length == 0 ? null : t;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void StartWatchdog(ChildProcess child)
        {
            if ((_options.OrphanGuard & OrphanGuard.Watchdog) == 0) return;
            try
            {
                int client = _options.WatchdogClientPidOverride ?? Process.GetCurrentProcess().Id;
                Process w = _platform.StartWatchdog(client, child.Pid, _options.WatchdogKillAfterSeconds);
                if (w == null) return;
                lock (_lock)
                {
                    StopWatchdogLocked();
                    _watchdog = w;
                }
            }
            catch (Exception e)
            {
                HostLog("Could not start the watchdog: " + e.Message);
            }
        }

        private void StopWatchdogLocked()
        {
            Process w = _watchdog;
            _watchdog = null;
            if (w == null) return;
            try { if (!w.HasExited) w.Kill(); } catch (Exception) { /* gone */ }
            try { w.WaitForExit(1000); } catch (Exception) { /* gone */ }
            try { w.Dispose(); } catch (Exception) { /* ignore */ }
        }

        private void OnRunExited(ChildProcess child, int code)
        {
            string message;
            lock (_lock)
            {
                if (_current != child || _state != HostState.Running || _stopRequested) return;
                _current = null;
                _result = null;
                StopWatchdogLocked();
                message = BuildMessage($"The server stopped unexpectedly (exit code {code}).", GetTail());
                SetStateLocked(HostState.Failed, message);
            }
            HostLog("The server stopped unexpectedly (exit code " + code + ").");
        }

        private void ThrowIfStopRequested(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) throw CancelStart(ct);
            lock (_lock)
            {
                if (_stopRequested) throw new OperationCanceledException("Hosting was stopped before the server was ready.");
            }
        }

        private Exception CancelStart(CancellationToken ct)
        {
            StopCore(_options.StopTimeout);
            return new OperationCanceledException("Hosting was cancelled.", ct);
        }

        /// <summary>Moves to Failed with a message for the player and returns the exception to throw.</summary>
        private Exception Fail(HostErrorKind kind, string message, int? exitCode, List<string> tail = null)
        {
            if (tail == null) tail = GetTail();
            string full;
            ChildProcess live;
            lock (_lock)
            {
                if (_stopRequested) return new OperationCanceledException("Hosting was stopped before the server was ready.");
                full = BuildMessage(message, tail);
                live = _current;
                _current = null;
                _result = null;
                StopWatchdogLocked();
                SetStateLocked(HostState.Failed, full);
            }
            // Never leave a process behind on a failure path.
            if (live != null && !live.HasExited) Terminate(live, _options.StopTimeout);
            var safeTail = new List<string>(tail.Count);
            foreach (string l in tail) safeTail.Add(BuildMessage(l, new List<string>()));
            return new HostException(kind, full, safeTail.AsReadOnly(), exitCode);
        }

        private string BuildMessage(string message, List<string> tail)
        {
            string full = tail.Count > 0 ? message + "\n\nLast server output:\n" + string.Join("\n", tail) : message;
            Redactor r;
            lock (_lock) r = _redactor;
            return r.Apply(full);
        }

        private List<string> GetTail() => GetRecentLinesList(_options.ErrorTailLines);

        private List<string> Tail(List<string> lines)
        {
            int n = Math.Min(lines.Count, Math.Max(0, _options.ErrorTailLines));
            return lines.GetRange(lines.Count - n, n);
        }

        private List<string> GetRecentLinesList(int max)
        {
            lock (_eventLock) return _recent.Tail(max);
        }

        // ---------------------------------------------------------------- stop

        /// <summary>
        /// Asks the server to shut down cleanly (SIGTERM, so it flushes the world), waits up to
        /// <paramref name="timeout"/>, then kills it hard. Runs the blocking work on a pool thread.
        /// </summary>
        public Task<StopResult> StopAsync(TimeSpan timeout) => Task.Run(() => StopCore(timeout));

        /// <summary>
        /// Blocking version of <see cref="StopAsync"/>. Call it from <c>Application.quitting</c>: the engine
        /// will not wait for a task there. Safe to call in any state and more than once.
        /// </summary>
        public StopResult Stop(TimeSpan timeout) => StopCore(timeout);

        private StopResult StopCore(TimeSpan timeout)
        {
            lock (_stopGate)
            {
                ChildProcess child;
                lock (_lock)
                {
                    bool active = _state == HostState.Initialising || _state == HostState.Starting
                        || _state == HostState.Running || _state == HostState.Stopping;
                    if (active) _stopRequested = true;
                    child = _current;
                    _ready?.TrySetCanceled();
                    if (child == null || child.HasExited)
                    {
                        _current = null;
                        _result = null;
                        StopWatchdogLocked();
                        if (active) SetStateLocked(HostState.Stopped, null);
                        return new StopResult(false, false, false, null, "The server is not running.");
                    }
                    SetStateLocked(HostState.Stopping, null);
                }

                HostLog("Stopping the server");
                StopResult r = Terminate(child, timeout);

                lock (_lock)
                {
                    if (_current == child) _current = null;
                    _result = null;
                    StopWatchdogLocked();
                    SetStateLocked(HostState.Stopped, null);
                }
                HostLog(r.Message);
                return r;
            }
        }

        /// <summary>SIGTERM, wait, SIGKILL. No state changes.</summary>
        private StopResult Terminate(ChildProcess child, TimeSpan timeout)
        {
            if (child.HasExited)
                return new StopResult(true, true, false, child.Exited.Result, "The server had already stopped.");

            bool requested = _platform.TryRequestStop(child.Pid, out string error);
            if (!requested)
            {
                HostLog("Could not ask the server to stop cleanly (" + error + "); stopping it hard.");
            }
            else if (child.Exited.Wait(timeout))
            {
                int code = child.Exited.Result;
                return new StopResult(true, true, false, code,
                    code == 0 ? "The server stopped and saved the world." : $"The server stopped (exit code {code}).");
            }

            child.Kill();
            bool gone = child.Exited.Wait(TimeSpan.FromSeconds(5));
            string why = requested
                ? $"The server did not stop within {timeout.TotalSeconds:0.#} seconds, so it was killed. Changes from the last few seconds may not have been saved."
                : "The server was killed. Changes from the last few seconds may not have been saved.";
            if (!gone) why += " It may still be running (pid " + child.Pid + ").";
            return new StopResult(true, false, true, gone ? child.Exited.Result : (int?)null, why);
        }

        /// <summary>Stops the server (graceful, then hard after <see cref="ServerProcessOptions.StopTimeout"/>). Blocks.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            StopCore(_options.StopTimeout);
        }

        // ---------------------------------------------------------------- output and events

        private void OnServerLine(string raw)
        {
            Redactor r;
            TaskCompletionSource<(bool Tls, int Port)> ready;
            lock (_lock)
            {
                r = _redactor;
                ready = _ready;
            }
            string line = r.Apply(raw);
            EmitLine(line);
            // Parse the raw line: redaction must never be able to hide the ready line.
            if (CertificateLine.TryParse(raw, out string sha256))
                lock (_lock) _certificateSha256 = sha256;
            if (ready != null && !ready.Task.IsCompleted && ReadyLine.TryParse(raw, out bool tls, out _, out int port))
                ready.TrySetResult((tls, port));
        }

        private void HostLog(string message)
        {
            Redactor r;
            lock (_lock) r = _redactor;
            EmitLine(HostPrefix + r.Apply(message));
        }

        private void EmitLine(string line)
        {
            lock (_eventLock)
            {
                _recent.Add(line);
                _pendingLines.Enqueue(new KeyValuePair<long, string>(++_sequence, line));
                while (_pendingLines.Count > _recent.Capacity)
                {
                    _pendingLines.Dequeue();
                    _droppedLines++;
                }
            }
        }

        private void SetState(HostState state, string error = null)
        {
            lock (_lock) SetStateLocked(state, error);
        }

        private void SetStateLocked(HostState state, string error)
        {
            if (_state == state && error == null) return;
            _state = state;
            if (state == HostState.Failed) _lastError = error;
            lock (_eventLock)
                _pendingStates.Enqueue(new PendingState { Sequence = ++_sequence, State = state, Error = error });
        }

        /// <summary>
        /// Raises the queued <see cref="LogLine"/>, <see cref="Failed"/> and <see cref="StateChanged"/> events,
        /// in the order they happened. Call once per frame on the main thread.
        /// </summary>
        public void Pump()
        {
            KeyValuePair<long, string>[] lines;
            PendingState[] states;
            int dropped;
            lock (_eventLock)
            {
                if (_pendingLines.Count == 0 && _pendingStates.Count == 0 && _droppedLines == 0) return;
                lines = _pendingLines.ToArray();
                states = _pendingStates.ToArray();
                dropped = _droppedLines;
                _pendingLines.Clear();
                _pendingStates.Clear();
                _droppedLines = 0;
            }

            if (dropped > 0) Raise(LogLine, $"{HostPrefix}({dropped} earlier lines were skipped)");
            int i = 0, j = 0;
            while (i < lines.Length || j < states.Length)
            {
                if (j >= states.Length || (i < lines.Length && lines[i].Key < states[j].Sequence))
                {
                    Raise(LogLine, lines[i++].Value);
                }
                else
                {
                    PendingState s = states[j++];
                    if (s.State == HostState.Failed && s.Error != null) Raise(Failed, s.Error);
                    Raise(StateChanged, s.State);
                }
            }
        }

        private static void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null) return;
            try { handler(value); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }
    }
}
