using System;
using System.Collections.Generic;
using Tinyverse.Avatars;
using Tinyverse.Net;
using Tinyverse.UI;
using Tinyverse.World;
using UnityEngine;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap
{
    /// <summary>What a <see cref="WorldSession"/> needs besides the connection.</summary>
    public sealed class SessionOptions
    {
        /// <summary>Replaces the keyboard for the local avatar (tests); null keeps the keyboard.</summary>
        public ILocalAvatarInput Input;
        /// <summary>Walk a small figure (<c>--demo-walk</c>).</summary>
        public bool DemoWalk;
        /// <summary>Replaces mouse and keyboard for the build tools (tests); null keeps them.</summary>
        public Tinyverse.Building.IBuildInput BuildInput;
        /// <summary>Show off build mode (<c>--demo-build</c>).</summary>
        public bool DemoBuild;
        /// <summary>The world is hosted by this client (only changes the wording of a few lines).</summary>
        public bool Hosted;
    }

    /// <summary>
    /// The in-world wiring of one connection: builds the world on every Welcome, keeps avatars and
    /// objects in step with the server, reports the own avatar, and links chat, people, status and
    /// toasts to the UI. Main thread only; call <see cref="Tick"/> once per frame.
    /// The HUD half lives in <c>WorldSession.Hud.cs</c>.
    /// </summary>
    public sealed partial class WorldSession : IDisposable
    {
        readonly TinyverseConnection conn;
        readonly UIRoot ui;
        readonly Camera camera;
        readonly SessionOptions options;
        readonly AvatarStateSender sender = new AvatarStateSender();
        readonly List<Action> unsubscribe = new List<Action>();
        readonly Dictionary<ulong, RemoteAvatar> remotes = new Dictionary<ulong, RemoteAvatar>();
        readonly Dictionary<ulong, P.AvatarInfo> people = new Dictionary<ulong, P.AvatarInfo>();
        readonly DemoWalk demo;
        readonly DemoBuild demoBuild;

        GameObject root;
        Transform avatarsRoot;
        KeyboardAvatarInput ownedKeyboard;
        readonly Queue<string> recentSent = new Queue<string>();
        bool disposed;
        bool ended;

        /// <summary>The session ended for good: (reason, wasKicked). Raised once, from <see cref="Tick"/>.</summary>
        public event Action<string, bool> Ended;

        /// <summary>Raised after the world has been rebuilt from a Welcome and its snapshot is complete.</summary>
        public event Action Ready;

        public TinyverseConnection Connection => conn;
        public UIRoot UI => ui;
        public P.Welcome Welcome { get; private set; }
        public P.Role Role { get; private set; } = P.Role.Visitor;
        public string UserId => Welcome?.UserId ?? "";
        public ulong OwnAvatarId => Welcome?.AvatarId ?? 0;
        public string WorldName => Welcome?.World?.Name ?? "";

        public WorldEnvironment Environment { get; }
        public ObjectRegistry Objects { get; }
        public LocalAvatar LocalAvatar { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public IReadOnlyDictionary<ulong, RemoteAvatar> RemoteAvatars => remotes;
        /// <summary>Everyone present, including the own avatar, by avatar id.</summary>
        public IReadOnlyDictionary<ulong, P.AvatarInfo> People => people;

        /// <summary>How many Welcomes have been handled (1 after joining, more after reconnects).</summary>
        public int WelcomeCount { get; private set; }
        public bool SnapshotComplete { get; private set; }
        public int CorrectionCount { get; private set; }
        public string LastCorrectionReason { get; private set; }
        public int StatesSent { get; private set; }

        public WorldSession(TinyverseConnection connection, UIRoot ui, Camera camera, SessionOptions options = null)
        {
            conn = connection ?? throw new ArgumentNullException(nameof(connection));
            this.ui = ui;
            this.camera = camera;
            this.options = options ?? new SessionOptions();
            if (this.options.DemoWalk) demo = new DemoWalk();
            if (this.options.DemoBuild)
            {
                demoBuild = new DemoBuild(this);
                this.options.BuildInput = demoBuild.Input;
            }

            root = new GameObject("World");
            avatarsRoot = new GameObject("Avatars").transform;
            avatarsRoot.SetParent(root.transform, false);
            var objectsRoot = new GameObject("Objects").transform;
            objectsRoot.SetParent(root.transform, false);
            Environment = new WorldEnvironment(root.transform);
            Objects = new ObjectRegistry(objectsRoot);

            Subscribe();
            if (ui != null) WireUI();
            else WireBuild();
        }

        // ---------------------------------------------------------------- events

        void On<T>(Action<T> add, Action<T> remove, T handler)
        {
            add(handler);
            unsubscribe.Add(() => remove(handler));
        }

        void Subscribe()
        {
            On<Action<P.Welcome>>(h => conn.Welcomed += h, h => conn.Welcomed -= h, OnWelcomed);
            On<Action<P.Snapshot, uint>>(h => conn.Snapshot += h, h => conn.Snapshot -= h, OnSnapshot);
            On<Action<SnapshotState>>(h => conn.SnapshotComplete += h, h => conn.SnapshotComplete -= h, _ => OnSnapshotComplete());
            On<Action<P.AvatarJoined, uint>>(h => conn.AvatarJoined += h, h => conn.AvatarJoined -= h, (m, _) => OnAvatarJoined(m.Avatar));
            On<Action<P.AvatarLeft, uint>>(h => conn.AvatarLeft += h, h => conn.AvatarLeft -= h, (m, _) => OnAvatarLeft(m.AvatarId));
            On<Action<P.AvatarStates, uint>>(h => conn.AvatarStates += h, h => conn.AvatarStates -= h, (m, _) => OnAvatarStates(m));
            On<Action<P.AvatarCorrection, uint>>(h => conn.AvatarCorrection += h, h => conn.AvatarCorrection -= h, (m, _) => OnCorrection(m));
            On<Action<P.AvatarUpdated, uint>>(h => conn.AvatarUpdated += h, h => conn.AvatarUpdated -= h, (m, _) => OnAvatarUpdated(m.Avatar));
            On<Action<P.ObjectCreated, uint>>(h => conn.ObjectCreated += h, h => conn.ObjectCreated -= h, (m, r) => OnObjectCreated(m, r));
            On<Action<P.ObjectUpdated, uint>>(h => conn.ObjectUpdated += h, h => conn.ObjectUpdated -= h, (m, r) => OnObjectUpdated(m, r));
            On<Action<P.ObjectDeleted, uint>>(h => conn.ObjectDeleted += h, h => conn.ObjectDeleted -= h, (m, r) => OnObjectDeleted(m, r));
            On<Action<P.ChatMessage, uint>>(h => conn.ChatMessage += h, h => conn.ChatMessage -= h, (m, _) => OnChat(m.Entry));
            On<Action<P.ChatHistory, uint>>(h => conn.ChatHistory += h, h => conn.ChatHistory -= h, (m, _) => OnChatHistory(m));
            On<Action<P.Error, uint>>(h => conn.Error += h, h => conn.Error -= h, OnError);
            On<Action<Net.ConnectionState>>(h => conn.StateChanged += h, h => conn.StateChanged -= h, OnStateChanged);
            On<Action<int, TimeSpan>>(h => conn.ReconnectScheduled += h, h => conn.ReconnectScheduled -= h, OnReconnectScheduled);
            On<Action<string, bool>>(h => conn.Disconnected += h, h => conn.Disconnected -= h, OnDisconnected);
        }

        // ---------------------------------------------------------------- welcome and snapshot

        void OnWelcomed(P.Welcome w)
        {
            bool reconnect = WelcomeCount > 0;
            WelcomeCount++;
            Welcome = w;
            Role = w.Role;
            SnapshotComplete = false;

            // Rebuild from scratch: nothing from the previous connection survives.
            Objects.Clear();
            foreach (var r in remotes.Values) if (r != null) UnityEngine.Object.Destroy(r.gameObject);
            remotes.Clear();
            people.Clear();

            Environment.Apply(ProtoConvert.ToEnvironment(w.World));
            SetUpLocalAvatar(w);
            if (w.Avatar != null) people[w.Avatar.Id] = w.Avatar;

            HudWelcomed(w, reconnect);
            Debug.Log($"[Session] welcomed to '{w.World?.Name}' as avatar {w.AvatarId} ({w.Role}){(reconnect ? " after reconnecting" : "")}");
        }

        void SetUpLocalAvatar(P.Welcome w)
        {
            var info = w.Avatar;
            var limits = ProtoConvert.ToMoveLimits(w.Limits, w.World);
            var pose = info?.Transform != null
                ? ProtoConvert.ToPose(info.Transform)
                : new AvatarPose(ProtoConvert.ToVector3(w.World?.SpawnPoint), 0f);
            var look = ProtoConvert.ToLook(info?.Appearance, w.UserId);
            string name = info?.Name ?? "";

            if (LocalAvatar == null)
            {
                LocalAvatar = LocalAvatar.Create(name, look, limits, pose);
                LocalAvatar.transform.SetParent(avatarsRoot, true);
                ILocalAvatarInput inner = demo != null ? demo.Input : options.Input;
                if (inner == null) inner = ownedKeyboard = new KeyboardAvatarInput();
                // Build chords (Ctrl+D, Ctrl+Z) must not also strafe or descend.
                LocalAvatar.InputSource = new ChordMaskedInput(inner, () => build != null && build.IsChordModifierHeld);
                LocalAvatar.Teleported += _ => sender.Reset();
            }
            else
            {
                LocalAvatar.SetLimits(limits);
                LocalAvatar.SetLook(look);
                LocalAvatar.SetName(name);
                LocalAvatar.Teleport(pose);
            }
            LocalAvatar.Id = w.AvatarId;
            sender.Reset();

            if (camera != null)
            {
                if (Orbit == null) Orbit = OrbitCamera.Create(camera);
                Orbit.Follow(LocalAvatar);
                // The demo walk steers in world space; its camera looks back at the spawn point (below).
                if (demo != null) LocalAvatar.ViewYaw = () => 0f;
                NameTag.ViewCamera = camera;
                if (ui != null) Orbit.IsPointerOverUI = () => ui.IsPointerOverUI;
            }
        }

        void OnSnapshot(P.Snapshot chunk, uint _)
        {
            foreach (var a in chunk.Avatars) OnAvatarJoined(a, announce: false);
            foreach (var o in chunk.Objects) Objects.Upsert(ProtoConvert.ToPrim(o));
        }

        void OnSnapshotComplete()
        {
            SnapshotComplete = true;
            RefreshPeople();
            Ready?.Invoke();
        }

        // ---------------------------------------------------------------- avatars

        void OnAvatarJoined(P.AvatarInfo a, bool announce = true)
        {
            if (a == null) return;
            people[a.Id] = a;
            if (a.Id == OwnAvatarId) return;
            if (!remotes.TryGetValue(a.Id, out var remote) || remote == null)
            {
                remote = RemoteAvatar.Create(a.Id, a.Name ?? "", ProtoConvert.ToLook(a.Appearance, a.UserId),
                    ProtoConvert.IsAgent(a.Kind), avatarsRoot);
                remote.ServerTime = () => conn.EstimatedServerTimeMs / 1000.0;
                remotes[a.Id] = remote;
            }
            remote.SnapTo(ProtoConvert.ToPose(a.Transform));
            if (announce)
            {
                AddSystem($"{a.Name} joined{(ProtoConvert.IsAgent(a.Kind) ? " (AI agent)" : "")}.");
                RefreshPeople();
            }
        }

        void OnAvatarLeft(ulong id)
        {
            people.TryGetValue(id, out var info);
            people.Remove(id);
            if (remotes.TryGetValue(id, out var remote))
            {
                remotes.Remove(id);
                if (remote != null) UnityEngine.Object.Destroy(remote.gameObject);
            }
            if (info != null) AddSystem($"{info.Name} left.");
            RefreshPeople();
        }

        void OnAvatarStates(P.AvatarStates batch)
        {
            double t = batch.ServerTimeMs / 1000.0;
            foreach (var e in batch.States)
            {
                if (e.AvatarId == OwnAvatarId)
                {
                    // Only while the server moves us (MoveTo), which this client never asks for.
                    if (LocalAvatar != null && e.Transform != null) LocalAvatar.Teleport(ProtoConvert.ToPose(e.Transform));
                    continue;
                }
                if (remotes.TryGetValue(e.AvatarId, out var remote) && remote != null && e.Transform != null)
                {
                    var pose = ProtoConvert.ToPose(e.Transform);
                    remote.PushState(t, pose);
                    if (people.TryGetValue(e.AvatarId, out var info)) info.Transform = e.Transform;
                }
            }
        }

        void OnCorrection(P.AvatarCorrection c)
        {
            CorrectionCount++;
            LastCorrectionReason = c.Reason ?? "";
            if (LocalAvatar != null && c.Transform != null)
            {
                LocalAvatar.Teleport(ProtoConvert.ToPose(c.Transform));
                if (Orbit != null) Orbit.SnapToTarget();
            }
            Debug.Log($"[Session] position corrected by the server: {c.Reason}; now {Time.unscaledTimeAsDouble:0.000}, " +
                      $"server kept {ProtoConvert.ToPose(c.Transform)}; recent reports: {string.Join(" | ", recentSent)}");
            Toast($"The server moved you back ({(string.IsNullOrEmpty(c.Reason) ? "correction" : c.Reason)}).", ToastKind.Warning);
        }

        void OnAvatarUpdated(P.AvatarInfo a)
        {
            if (a == null) return;
            people.TryGetValue(a.Id, out var old);
            people[a.Id] = a;
            if (a.Id == OwnAvatarId)
            {
                if (LocalAvatar != null)
                {
                    LocalAvatar.SetName(a.Name ?? "");
                    LocalAvatar.SetLook(ProtoConvert.ToLook(a.Appearance, a.UserId));
                }
                if (a.Role != Role)
                {
                    Role = a.Role;
                    Toast($"Your role is now {ProtoConvert.ToUI(a.Role).ToString().ToLowerInvariant()}.", ToastKind.Info);
                    RoleChanged();
                }
            }
            else if (remotes.TryGetValue(a.Id, out var remote) && remote != null)
            {
                remote.SetName(a.Name ?? "");
                remote.SetLook(ProtoConvert.ToLook(a.Appearance, a.UserId));
                remote.SetAgent(ProtoConvert.IsAgent(a.Kind));
            }
            if (old != null && old.Name != a.Name) AddSystem($"{old.Name} is now called {a.Name}.");
            RefreshPeople();
        }

        // ---------------------------------------------------------------- objects

        void OnObjectCreated(P.ObjectCreated m, uint replyTo)
        {
            if (m.Object == null) return;
            Objects.Upsert(ProtoConvert.ToPrim(m.Object));
            BuildObjectCreated(m, replyTo);
        }

        void OnObjectUpdated(P.ObjectUpdated m, uint replyTo)
        {
            if (m.Object == null) return;
            // Smooth edits made by others; our own land where we put them.
            Objects.Upsert(ProtoConvert.ToPrim(m.Object), smooth: m.ByAvatarId != OwnAvatarId);
            BuildObjectChanged(m.Object.Id);
        }

        void OnObjectDeleted(P.ObjectDeleted m, uint replyTo)
        {
            Objects.Remove(m.Id);
            BuildObjectRemoved(m.Id);
        }

        // ---------------------------------------------------------------- per frame

        /// <summary>Pumps the connection, gates input and advances smoothing. Once per frame, before the avatars move.</summary>
        public void Tick(float dt)
        {
            if (disposed) return;
            conn.Pump();
            if (disposed) return;

            if (LocalAvatar != null)
            {
                bool blocked = ui != null && (ui.IsTextInputFocused || ui.IsModalOpen);
                LocalAvatar.InputEnabled = !blocked;
                if (Orbit != null) Orbit.InputEnabled = !blocked;
                demo?.Tick(dt, LocalAvatar.IsFlying);
                if (demo != null && Orbit != null)
                {
                    // Keep the spawn area (where others build) in view behind the walking avatar.
                    var toCentre = ProtoConvert.ToVector3(Welcome?.World?.SpawnPoint) - LocalAvatar.transform.position;
                    if (new Vector2(toCentre.x, toCentre.z).sqrMagnitude > 4f)
                        Orbit.Yaw = Mathf.LerpAngle(Orbit.Yaw, AvatarMotion.YawOf(toCentre), 1f - Mathf.Exp(-2f * dt));
                }

            }
            Objects.Tick(dt);
            BuildTick(dt);
            HudTick(dt);
        }

        /// <summary>
        /// Reports the own avatar. Call after the avatars have moved this frame (LateUpdate): a report
        /// must carry the movement of the same frame in which it is sent, or the server, which times
        /// reports by their arrival, sees uneven frames as bursts of speed.
        /// </summary>
        public void LateTick()
        {
            if (disposed || LocalAvatar == null) return;
            if (conn.State == Net.ConnectionState.Connected && Welcome != null &&
                sender.ShouldSend(LocalAvatar.CurrentPose, Time.unscaledTimeAsDouble))
            {
                if (conn.SendAvatarState(ProtoConvert.ToProto(LocalAvatar.CurrentPose)) != 0)
                {
                    StatesSent++;
                    var p = LocalAvatar.CurrentPose.Position;
                    recentSent.Enqueue($"{Time.unscaledTimeAsDouble:0.000} ({p.x:0.00},{p.y:0.00},{p.z:0.00}) {LocalAvatar.CurrentPose.Mode}");
                    while (recentSent.Count > 6) recentSent.Dequeue();
                }
            }
        }

        void OnDisconnected(string reason, bool kicked)
        {
            if (ended) return;
            ended = true;
            Debug.Log($"[Session] ended: {reason} (kicked: {kicked})");
            Ended?.Invoke(reason, kicked);
        }

        /// <summary>Closes the connection and destroys everything this session created.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var u in unsubscribe)
            {
                try { u(); } catch (Exception e) { Debug.LogException(e); }
            }
            unsubscribe.Clear();
            conn.Dispose();
            BuildDispose();
            HudDispose();
            Objects.Clear();
            Environment.Clear();
            if (Orbit != null)
            {
                Orbit.Follow(null);
                UnityEngine.Object.Destroy(Orbit);
                Orbit = null;
            }
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            LocalAvatar = null;
            ownedKeyboard?.Dispose();
            ownedKeyboard = null;
            remotes.Clear();
            people.Clear();
        }
    }
}
