using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tinyverse.Host;
using Tinyverse.Net;
using Tinyverse.UI;
using UnityEngine;
using UnityEngine.Rendering;
using P = Tinyverse.Proto;
using UIHostState = Tinyverse.UI.HostState;
using UIHostSettings = Tinyverse.UI.HostSettings;

namespace Tinyverse.Bootstrap
{
    /// <summary>The application's top-level states.</summary>
    public enum AppState
    {
        /// <summary>Connect or Host screen, idle.</summary>
        FrontEnd,
        /// <summary>Joining a world.</summary>
        Connecting,
        InWorld,
        /// <summary>Starting the bundled server (before joining it).</summary>
        Hosting,
    }

    /// <summary>
    /// The application: front end, connecting, in world and hosting. Owns the camera, the UI, the
    /// stores, the hosting controller and the current <see cref="WorldSession"/>. Main thread only.
    /// </summary>
    public sealed class App : IDisposable
    {
        readonly CommandLine args;
        readonly string profile;
        readonly SettingsStore settingsStore;
        readonly FileTokenStore tokens;
        readonly HostingController hosting = new HostingController();

        CancellationTokenSource connectCts;
        Tinyverse.Avatars.OrbitCamera appliedOrbit;
        bool hostedSession;
        bool shutDown;

        public AppState State { get; private set; } = AppState.FrontEnd;
        public UIRoot UI { get; }
        public Camera Camera { get; }
        public WorldSession Session { get; private set; }
        public HostingController Hosting => hosting;
        public CommandLine Args => args;
        public string ProfileDirectory => profile;

        /// <summary>Raised whenever <see cref="State"/> changes.</summary>
        public event Action<AppState> StateChanged;

        public App(CommandLine commandLine, Transform parent = null)
        {
            args = commandLine ?? new CommandLine();
            profile = string.IsNullOrEmpty(args.ProfileDirectory) ? Application.persistentDataPath : System.IO.Path.GetFullPath(args.ProfileDirectory);
            System.IO.Directory.CreateDirectory(profile);
            settingsStore = new SettingsStore(profile);
            settingsStore.Load();
            tokens = new FileTokenStore(profile);
            foreach (var e in args.Errors) Debug.LogWarning("[App] " + e);
            Debug.Log($"[App] profile {profile}");

            Camera = CreateCamera(parent);
            UI = UIRoot.Create(parent);
            WireFrontEnd();
            WireHosting();
            ApplySettings(settingsStore.Current.client);
            ShowConnectScreen(null);
        }

        static Camera CreateCamera(Transform parent)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            if (parent != null) go.transform.SetParent(parent, false);
            var cam = go.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1500f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, -6f), Quaternion.Euler(-8f, 0f, 0f));
            go.AddComponent<AudioListener>();
            return cam;
        }

        void SetState(AppState s)
        {
            if (State == s) return;
            State = s;
            Debug.Log($"[App] state {s}");
            StateChanged?.Invoke(s);
        }

        // ---------------------------------------------------------------- front end

        void WireFrontEnd()
        {
            UI.Connect.SetVersionText("Tinyverse " + Application.version);
            UI.Connect.ConnectRequested += r => Connect(r.Address, r.DisplayName, r.Password);
            UI.Connect.CancelRequested += CancelConnect;
            UI.Connect.HostRequested += ShowHostScreen;
            UI.Connect.RecentWorldRemoved += w => tokens.Remove(w.Address);
            UI.Menu.ResumeRequested += () => UI.CloseMenu();
            UI.Menu.DisconnectRequested += RequestDisconnect;
            UI.Menu.QuitRequested += Quit;
            UI.Settings.SetQualityLevels(QualitySettings.names);
            UI.Settings.SetSettings(settingsStore.Current.client);
            UI.Settings.SettingsChanged += s =>
            {
                ApplySettings(s);
                settingsStore.Current.client = s;
                settingsStore.Save();
            };
        }

        /// <summary>Shows the Connect screen with the recent worlds and the last name and address; an error if given.</summary>
        public void ShowConnectScreen(string error)
        {
            var recent = tokens.GetRecentWorlds();
            UI.Connect.SetRecentWorlds(recent.Select(w => new RecentWorld(settingsStore.Current.WorldName(w.Url), w.Url, w.LastConnected.LocalDateTime)).ToList());
            var stored = settingsStore.Current;
            string address = !string.IsNullOrEmpty(stored.lastAddress) ? stored.lastAddress : recent.FirstOrDefault()?.Url ?? "";
            string name = !string.IsNullOrEmpty(stored.lastName) ? stored.lastName : recent.FirstOrDefault()?.DisplayName ?? "";
            if (string.IsNullOrEmpty(UI.Connect.Address)) UI.Connect.SetAddress(address);
            if (string.IsNullOrEmpty(UI.Connect.DisplayName)) UI.Connect.SetDisplayName(name);
            UI.Connect.SetBusy(false);
            if (error != null) UI.Connect.ShowError(error);
            UI.ShowScreen(UIScreen.Connect);
            ResetCamera();
            SetState(AppState.FrontEnd);
        }

        public void ShowHostScreen()
        {
            if (!hosting.IsActive)
            {
                UI.Host.SetDefaults(HostingController.Defaults(profile, LastName()));
                UI.Host.SetState(UIHostState.Stopped, null);
            }
            UI.ShowScreen(UIScreen.Host);
        }

        string LastName()
        {
            var n = settingsStore.Current.lastName;
            return !string.IsNullOrEmpty(n) ? n : tokens.GetRecentWorlds().FirstOrDefault()?.DisplayName ?? "";
        }

        void ResetCamera()
        {
            Camera.transform.SetPositionAndRotation(new Vector3(0f, 1.7f, -6f), Quaternion.Euler(-8f, 0f, 0f));
        }

        // ---------------------------------------------------------------- connecting

        /// <summary>
        /// Joins a world. <paramref name="token"/> overrides the stored session token (the owner token when hosting).
        /// A <c>wss://</c> address may pin the certificate (<c>#sha256=…</c>); otherwise a fingerprint remembered for
        /// its host and port is pinned. An unknown certificate asks the player whether to trust it; a changed one is
        /// refused, with the choice to forget the old fingerprint.
        /// </summary>
        public async void Connect(string address, string displayName, string password, string token = null, bool hosted = false)
        {
            if (State == AppState.Connecting || State == AppState.InWorld) return;
            if (!WorldAddress.TryNormalize(address, out var normalized, out var error))
            {
                ConnectFailed(error, hosted);
                return;
            }
            // The world is known by its address without the pin: tokens, names and recent worlds.
            string url = CertificatePin.Strip(normalized);
            string givenPin = CertificatePin.PinOf(normalized);
            var stored = settingsStore.Current;
            stored.lastName = displayName ?? "";
            if (!hosted) stored.lastAddress = address ?? "";
            settingsStore.Save();
            string pin = givenPin ?? (url.StartsWith("wss://", StringComparison.Ordinal) ? stored.PinFor(url) : null);
            string connectUrl = pin != null ? CertificatePin.WithPin(url, pin) : url;

            if (token == null && IsHostedUrl(url)) token = hosting.Current.OwnerToken;
            hosted |= IsHostedUrl(url);
            string storedToken = token ?? tokens.Get(url)?.SessionToken ?? "";

            SetState(AppState.Connecting);
            if (hosted) UI.Host.SetState(UIHostState.Running, "Joining your world…");
            else UI.Connect.SetBusy(true, $"Connecting to {url}…");
            connectCts?.Dispose();
            connectCts = new CancellationTokenSource();
            var ct = connectCts.Token;

            TinyverseConnection conn = null;
            try
            {
                conn = await ConnectOnce(connectUrl, displayName, password, storedToken, ct);
                if (conn == null && !string.IsNullOrEmpty(storedToken) && token == null)
                {
                    // INVALID_TOKEN: forget it and join as a new user, once.
                    Debug.Log("[App] stored token refused; retrying without it");
                    tokens.ForgetToken(url);
                    conn = await ConnectOnce(connectUrl, displayName, password, "", ct);
                }
                if (conn == null) throw new TinyverseServerException(P.ErrorCode.InvalidToken, "");
            }
            catch (OperationCanceledException)
            {
                if (!shutDown) ConnectFailed(null, hosted);
                return;
            }
            catch (TinyverseCertificateException e)
            {
                if (!shutDown)
                    CertificateRefused(e, url, givenPin != null, hosted,
                        () => Connect(address, displayName, password, token, hosted));
                return;
            }
            catch (TinyverseServerException e)
            {
                ConnectFailed(ErrorText.Describe(e.Code, e.ServerMessage), hosted);
                return;
            }
            catch (TimeoutException e)
            {
                ConnectFailed($"No answer from {url}: {e.Message}", hosted);
                return;
            }
            catch (Exception e)
            {
                ConnectFailed($"Could not reach {url}: {Unwrap(e)}", hosted);
                return;
            }
            if (shutDown || ct.IsCancellationRequested)
            {
                conn.Dispose();
                if (!shutDown) ConnectFailed(null, hosted);
                return;
            }
            var w = conn.Welcome;
            tokens.Remember(url, w.SessionToken, displayName, DateTimeOffset.Now);
            settingsStore.Current.SetWorldName(url, w.World?.Name);
            // Joined with a pin (pasted, or accepted earlier): this certificate is the one for this address now.
            if (pin != null) settingsStore.Current.RememberPin(url, pin);
            settingsStore.Save();
            StartSession(conn, hosted);
        }

        /// <summary>
        /// A <c>wss://</c> world's certificate was refused. Unknown: ask whether to trust it, and remember it if so.
        /// Changed from the remembered one: refuse, offering to forget the old one. Not the one pinned in the address
        /// the player gave: refuse.
        /// </summary>
        void CertificateRefused(TinyverseCertificateException e, string url, bool pinFromAddress, bool hosted, Action retry)
        {
            Debug.Log($"[App] certificate refused: {e.Message}");
            ConnectFailed(null, hosted);
            // Shown as in the address (lower-case hex after #sha256=), so it can be compared with the Host screen.
            string presented = ShowFingerprint(e.Fingerprint);
            if (e.Problem == CertificateProblem.Untrusted)
            {
                UI.Confirm.Ask("Trust this world?",
                    $"This world's certificate is not known.\n\n{e.HostPort}\nFingerprint (SHA-256):\n{presented}\n\n" +
                    "Trust it only if it matches the end of the address on the host's Host screen (after #sha256=), " +
                    "or what `tinyverse-server tls fingerprint` prints. Trust it?",
                    "Trust",
                    () =>
                    {
                        settingsStore.Current.RememberPin(url, e.Fingerprint);
                        settingsStore.Save();
                        retry();
                    },
                    () => ConnectFailed("Not joined: you did not trust the world's certificate.", hosted));
                return;
            }
            if (pinFromAddress)
            {
                ConnectFailed($"The world's certificate does not match the fingerprint in the address: it is {e.Fingerprint}. " +
                              "Ask the host for the current address.", hosted);
                return;
            }
            UI.Confirm.Ask("Certificate changed",
                $"The certificate of {e.HostPort} has changed since you last joined.\n\n" +
                $"Remembered:\n{ShowFingerprint(e.ExpectedPin)}\nNow:\n{presented}\n\n" +
                "This happens when the host makes a new certificate, but it can also mean that someone is intercepting the connection. " +
                "Forget the remembered certificate only if the host confirms the change.",
                "Forget old certificate",
                () =>
                {
                    settingsStore.Current.ForgetPin(url);
                    settingsStore.Save();
                    retry();
                },
                () => ConnectFailed($"Not joined: the certificate of {e.HostPort} has changed. Its fingerprint is now {e.Fingerprint}.", hosted));
        }

        /// <summary>A fingerprint as hex in two lines of 32 digits, as it appears in a pinned address.</summary>
        public static string ShowFingerprint(string hex)
        {
            if (!CertificatePin.TryParse(hex, out var h)) return hex ?? "";
            return h.Substring(0, 32) + "\n" + h.Substring(32);
        }

        /// <summary>The certificate fingerprint remembered for an address (lower-case hex), or null.</summary>
        public string RememberedPin(string address) =>
            WorldAddress.TryNormalize(address, out var url, out _) ? settingsStore.Current.PinFor(url) : null;

        static string Unwrap(Exception e)
        {
            // Our own connection errors are already worded for the player; below them is library detail.
            while (e.InnerException != null && !(e is TinyverseConnectionException)) e = e.InnerException;
            return e.Message;
        }

        /// <summary>One attempt. Returns null when the server refused the token (INVALID_TOKEN).</summary>
        async Task<TinyverseConnection> ConnectOnce(string url, string name, string password, string token, CancellationToken ct)
        {
            var conn = new TinyverseConnection(new ConnectionSettings());
            var options = new ConnectOptions
            {
                Url = url,
                DisplayName = name ?? "",
                Password = password ?? "",
                Token = token ?? "",
                Kind = P.ClientKind.Human,
                Appearance = ProtoConvert.ToAppearance(SettingsStore.ParseBodyColor(settingsStore.Current.bodyColor)),
                ClientName = "tinyverse-unity/" + Application.version,
            };
            try
            {
                await conn.ConnectAsync(options, ct);
                return conn;
            }
            catch (TinyverseServerException e) when (e.Code == P.ErrorCode.InvalidToken && !string.IsNullOrEmpty(token))
            {
                conn.Dispose();
                return null;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        bool IsHostedUrl(string url)
        {
            var r = hosting.Current;
            if (r == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            bool local = u.IsLoopback || r.LanAddresses.Contains(u.Host);
            return local && u.Port == r.Port;
        }

        void ConnectFailed(string error, bool hosted)
        {
            if (hosted && hosting.IsActive)
            {
                UI.Host.SetState(UIHostState.Running, null);
                if (error != null) UI.Host.ShowError("Could not join your world: " + error);
                UI.ShowScreen(UIScreen.Host);
                SetState(AppState.FrontEnd);
                return;
            }
            UI.Connect.SetBusy(false);
            ShowConnectScreen(error);
        }

        void CancelConnect() => connectCts?.Cancel();

        // ---------------------------------------------------------------- in world

        void StartSession(TinyverseConnection conn, bool hosted)
        {
            hostedSession = hosted;
            Session = new WorldSession(conn, UI, Camera, new SessionOptions
            {
                DemoWalk = args.DemoWalk,
                DemoBuild = args.DemoBuild,
                Hosted = hosted,
            });
            Session.Ended += OnSessionEnded;
            // The Host screen said "Joining your world…" meanwhile; back to the running status.
            if (hosted && hosting.Current != null) UI.Host.SetState(UIHostState.Running, "Running at " + hosting.Current.LocalUrl);
            UI.Connect.SetBusy(false);
            UI.Connect.ClearError();
            UI.Chat.Clear();
            UI.ShowScreen(UIScreen.Hud);
            SetState(AppState.InWorld);
        }

        void OnSessionEnded(string reason, bool kicked)
        {
            EndSession();
            ShowConnectScreen(kicked ? $"You were removed from the world: {reason}" : $"Disconnected: {reason}");
        }

        void EndSession()
        {
            if (Session == null) return;
            Session.Ended -= OnSessionEnded;
            Session.Dispose();
            Session = null;
            appliedOrbit = null;
        }

        /// <summary>The menu's Leave: asks whether to keep hosting when this client hosts the world.</summary>
        public void RequestDisconnect()
        {
            if (!hostedSession || !hosting.IsActive)
            {
                Leave();
                return;
            }
            UI.Confirm.Ask("Stop hosting too?",
                "You are hosting this world. Stop the server (the world is saved), or keep it running for others and leave.",
                "Stop hosting",
                () => { Leave(); StopHosting(); },
                () => Leave());
        }

        /// <summary>Leaves the world for the Connect screen (the hosted server, if any, keeps running).</summary>
        public void Leave()
        {
            EndSession();
            if (hosting.IsActive) UI.Connect.SetAddress(hosting.Current?.LocalJoinUrl ?? UI.Connect.Address);
            ShowConnectScreen(null);
        }

        // ---------------------------------------------------------------- hosting

        void WireHosting()
        {
            UI.Host.StartRequested += s => StartHosting(s);
            UI.Host.StopRequested += () => StopHosting();
            UI.Host.BackRequested += () => ShowConnectScreen(null);
            UI.Host.JoinRequested += () =>
            {
                if (!hosting.IsActive || hosting.Current == null) return;
                var name = UI.Host.Settings.DisplayName;
                Connect(hosting.Current.LocalJoinUrl, string.IsNullOrEmpty(name) ? "Host" : name,
                    UI.Host.Settings.Password, hosted: true);
            };
            UI.Host.AddressCopied += a => UI.Toasts.Info("Copied " + a);
            hosting.LogLine += line => UI.Host.AppendLog(line);
            hosting.StateChanged += (s, text) => UI.Host.SetState(s, text);
            hosting.Died += msg =>
            {
                UI.Host.ShowError(msg);
                UI.Toasts.Error("Your world's server stopped: " + msg);
            };
        }

        /// <summary>Starts the bundled server for the Host screen's settings, then joins it as the owner.</summary>
        public async void StartHosting(UIHostSettings s)
        {
            if (hosting.IsActive || State == AppState.Connecting || State == AppState.InWorld) return;
            settingsStore.Current.lastName = s.DisplayName ?? "";
            settingsStore.Save();
            UI.Host.ShowError(null);
            UI.Host.ClearLog();
            UI.Host.SetShareAddress(null);
            UI.ShowScreen(UIScreen.Host);
            SetState(AppState.Hosting);
            HostStartResult result;
            try
            {
                result = await hosting.StartAsync(HostingController.ToHostSettings(s));
            }
            catch (OperationCanceledException)
            {
                if (!shutDown) SetState(AppState.FrontEnd);
                return;
            }
            catch (Exception e)
            {
                if (shutDown) return;
                UI.Host.SetState(UIHostState.Failed, "Could not start hosting.");
                UI.Host.ShowError(e.Message);
                SetState(AppState.FrontEnd);
                return;
            }
            if (shutDown) return;
            UI.Host.SetShareAddress(hosting.ShareAddress);
            if (result.OwnerToken == null) UI.Toasts.Warning("owner.token is missing: you join without owner rights.");
            SetState(AppState.FrontEnd);
            // With TLS the local address carries the certificate's pin: the host never sees a trust prompt.
            Connect(result.LocalJoinUrl, string.IsNullOrEmpty(s.DisplayName) ? "Host" : s.DisplayName, s.Password, result.OwnerToken ?? "", hosted: true);
        }

        /// <summary>Hosts the world in a directory (the <c>--host</c> option).</summary>
        public void HostFromCommandLine(string directory, int? port)
        {
            var d = HostingController.Defaults(profile, LastName());
            d.WorldFolder = System.IO.Path.GetFullPath(directory);
            d.WorldName = WorldDirectories.ReadName(d.WorldFolder) ?? System.IO.Path.GetFileName(d.WorldFolder.TrimEnd('/'));
            if (port.HasValue) d.Port = port.Value;
            if (!string.IsNullOrEmpty(args.Name)) d.DisplayName = args.Name;
            if (string.IsNullOrEmpty(d.DisplayName)) d.DisplayName = "Host";
            UI.Host.SetDefaults(d);
            StartHosting(d);
        }

        public async void StopHosting()
        {
            if (Session != null && hostedSession) Leave();
            var r = await hosting.StopAsync();
            if (r != null) UI.Toasts.Info(r.Message);
            UI.Host.SetShareAddress(null);
        }

        // ---------------------------------------------------------------- settings

        /// <summary>Applies settings live: camera, quality, window, volume and UI scale.</summary>
        public void ApplySettings(ClientSettings s)
        {
            s = SettingsStore.Sanitise(s);
            Camera.fieldOfView = s.FieldOfView;
            var orbit = Session?.Orbit;
            if (orbit != null)
            {
                orbit.OrbitSensitivity = 0.2f * s.MouseSensitivity;
                orbit.InvertY = s.InvertY;
            }
            appliedOrbit = orbit;
            int q = Mathf.Clamp(s.QualityIndex, 0, Math.Max(0, QualitySettings.names.Length - 1));
            if (QualitySettings.GetQualityLevel() != q) QualitySettings.SetQualityLevel(q, true);
            if (!Application.isBatchMode && !Application.isEditor && Screen.fullScreen != s.Fullscreen)
                Screen.fullScreenMode = s.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            AudioListener.volume = s.MasterVolume;
            UI.UIScale = s.UIScale;
        }

        // ---------------------------------------------------------------- frame and shutdown

        public void Tick(float dt)
        {
            hosting.Tick();
            if (Session != null)
            {
                Session.Tick(dt);
                if (Session != null && Session.Orbit != null && Session.Orbit != appliedOrbit)
                    ApplySettings(settingsStore.Current.client);
            }
        }

        /// <summary>After the avatars have moved: reports the own avatar.</summary>
        public void LateTick() => Session?.LateTick();

        public void Quit()
        {
            Shutdown();
            Application.Quit();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        /// <summary>Leaves the world and stops a hosted server (blocking, so the world is saved). Idempotent.</summary>
        public void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            connectCts?.Cancel();
            EndSession();
            hosting.Dispose();
        }

        public void Dispose()
        {
            Shutdown();
            if (UI != null) UnityEngine.Object.Destroy(UI.gameObject);
            if (Camera != null) UnityEngine.Object.Destroy(Camera.gameObject);
        }
    }
}
