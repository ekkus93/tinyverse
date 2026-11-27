using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tinyverse.Host;
using UnityEngine;
using HostState = Tinyverse.Host.HostState;
using UIHostState = Tinyverse.UI.HostState;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// Hosting from the client: checks the port, finds the bundled server, starts it with
    /// <see cref="ServerProcess"/>, forwards its log and state to a sink (the Host screen) and stops it
    /// gracefully. Main thread only; call <see cref="Tick"/> every frame while it exists.
    /// </summary>
    public sealed class HostingController : IDisposable
    {
        public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

        ServerProcess process;
        CancellationTokenSource startCts;

        /// <summary>One line of server output.</summary>
        public event Action<string> LogLine;

        /// <summary>The state as the Host screen shows it, with a status sentence.</summary>
        public event Action<UIHostState, string> StateChanged;

        /// <summary>The server stopped without being asked (after it was running).</summary>
        public event Action<string> Died;

        /// <summary>Overrides <see cref="ServerLocator"/> (tests); null searches as usual.</summary>
        public string ServerBinaryOverride { get; set; }

        public bool IsRunning => process != null && process.State == HostState.Running;
        public bool IsActive => process != null && process.State != HostState.Stopped && process.State != HostState.Failed;
        public HostStartResult Current => process?.Current;
        public ServerProcess Process => process;

        /// <summary>The address friends should type: the first LAN URL, or the local URL when there is none.</summary>
        public string ShareAddress
        {
            get
            {
                var r = Current;
                if (r == null) return "";
                var lan = r.LanAddresses.FirstOrDefault();
                return lan != null ? (r.Port == 7878 ? lan : lan + ":" + r.Port) : r.LocalUrl;
            }
        }

        /// <summary>
        /// Starts hosting. Throws <see cref="HostException"/> with a sentence for the player when the port is
        /// taken, no server binary exists, or the server fails to start; <see cref="OperationCanceledException"/>
        /// when stopped meanwhile.
        /// </summary>
        public async Task<HostStartResult> StartAsync(HostSettings settings)
        {
            if (IsActive) throw new InvalidOperationException("Already hosting.");
            string portProblem = PortCheck.Check(settings.Port, settings.BindAddress);
            if (portProblem != null) throw new HostException(HostErrorKind.PortInUse, portProblem);

            var location = ServerLocator.Locate(ServerBinaryOverride);
            if (!location.Found) throw new HostException(HostErrorKind.LaunchFailed, location.Explanation);
            Emit(location.Explanation);

            DisposeProcess();
            process = new ServerProcess(location.Path);
            process.LogLine += l => LogLine?.Invoke(l);
            process.StateChanged += OnState;
            process.Failed += msg => { if (wasRunning) Died?.Invoke(msg); };
            wasRunning = false;
            startCts = new CancellationTokenSource();
            Report(UIHostState.Starting, "Starting the server…");
            var result = await process.StartAsync(settings, startCts.Token);
            return result;
        }

        bool wasRunning;

        void OnState(HostState s)
        {
            switch (s)
            {
                case HostState.Initialising: Report(UIHostState.Starting, "Creating the world…"); break;
                case HostState.Starting: Report(UIHostState.Starting, "Starting the server…"); break;
                case HostState.Running:
                    wasRunning = true;
                    Report(UIHostState.Running, "Running at " + (process.Current?.LocalUrl ?? "")); break;
                case HostState.Stopping: Report(UIHostState.Stopping, "Stopping and saving the world…"); break;
                case HostState.Failed: Report(UIHostState.Failed, process.LastError ?? "The server failed."); break;
                default: Report(UIHostState.Stopped, "Stopped."); break;
            }
        }

        void Report(UIHostState s, string text) => StateChanged?.Invoke(s, text);

        void Emit(string line) => LogLine?.Invoke("[host] " + line);

        public void Tick() => process?.Pump();

        /// <summary>Asks the server to stop (it saves the world) and waits up to <see cref="StopTimeout"/>.</summary>
        public async Task<StopResult> StopAsync()
        {
            if (process == null) return null;
            startCts?.Cancel();
            var r = await process.StopAsync(StopTimeout);
            process.Pump();
            Emit(r.Message);
            return r;
        }

        /// <summary>Stops synchronously, for <c>Application.quitting</c>.</summary>
        public StopResult StopNow()
        {
            if (process == null) return null;
            startCts?.Cancel();
            var r = process.Stop(StopTimeout);
            Debug.Log("[Hosting] " + r.Message);
            return r;
        }

        void DisposeProcess()
        {
            if (process == null) return;
            process.Dispose();
            process = null;
        }

        public void Dispose()
        {
            StopNow();
            DisposeProcess();
        }

        /// <summary>The Host screen's defaults: the newest world under the default root, or a new folder.</summary>
        public static UI.HostSettings Defaults(string profileDirectory, string displayName)
        {
            string root = WorldDirectories.DefaultRoot(profileDirectory);
            var worlds = WorldDirectories.List(root);
            var newest = worlds.OrderByDescending(w => SafeWriteTime(w.Directory)).FirstOrDefault();
            return new UI.HostSettings
            {
                WorldName = newest?.Name ?? "My World",
                Description = "",
                WorldFolder = newest?.Directory ?? WorldDirectories.NewWorldDirectory(root, "My World"),
                Port = UI.HostSettings.DefaultPort,
                Password = "",
                DisplayName = displayName ?? "",
            };
        }

        static DateTime SafeWriteTime(string dir)
        {
            try { return File.GetLastWriteTimeUtc(Path.Combine(dir, "world.db")); }
            catch (Exception) { return DateTime.MinValue; }
        }

        /// <summary>The Host module's settings from the Host screen's form.</summary>
        public static HostSettings ToHostSettings(UI.HostSettings s)
        {
            string dir = s.WorldFolder;
            bool exists = WorldDirectories.IsWorld(dir);
            return new HostSettings
            {
                WorldDirectory = dir,
                WorldName = s.WorldName,
                Description = s.Description,
                Port = s.Port > 0 ? s.Port : HostSettings.DefaultPort,
                // For an existing world an empty field keeps its password rather than silently clearing it.
                Password = exists && string.IsNullOrEmpty(s.Password) ? null : s.Password,
            };
        }
    }
}
