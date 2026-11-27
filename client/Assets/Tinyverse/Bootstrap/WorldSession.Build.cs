using System;
using Tinyverse.Avatars;
using Tinyverse.Building;
using Tinyverse.UI;
using Tinyverse.World;
using UnityEngine;
using P = Tinyverse.Proto;
using BuildTool = Tinyverse.Building.BuildTool;
using UIBuildTool = Tinyverse.UI.BuildTool;

namespace Tinyverse.Bootstrap
{
    // The build-mode half of the session: BuildController ⇄ build panel ⇄ network.
    public sealed partial class WorldSession
    {
        BuildController build;
        GatedBuildInput buildInput;

        /// <summary>The build tools; null without a camera.</summary>
        public BuildController Build => build;

        public bool BuildMode => build != null && build.Enabled;

        /// <summary>Turns build mode on or off (the B key and the build button call this).</summary>
        public void SetBuildMode(bool on)
        {
            if (build == null) return;
            build.Enabled = on;
            ui?.SetBuildMode(on);
            if (on) RefreshBuildPanel();
        }

        void WireBuild()
        {
            if (camera == null) return;
            buildInput = new GatedBuildInput(options.BuildInput ?? new InputSystemBuildInput(), options.BuildInput == null,
                () => ui != null && (ui.IsModalOpen || ui.IsTextInputFocused));
            build = BuildController.Create(Objects, camera, buildInput);
            build.transform.SetParent(root.transform, false);
            build.Enabled = false;
            build.IsPointerOverUI = () => ui != null && ui.IsPointerOverUI;
            build.IsTextInputFocused = () => ui != null && (ui.IsTextInputFocused || ui.IsModalOpen);
            build.AvatarPosition = () => LocalAvatar != null ? LocalAvatar.transform.position : Vector3.zero;
            build.AvatarForward = () => LocalAvatar != null ? LocalAvatar.transform.forward : Vector3.forward;
            build.IsPlacementSurface = c => (Environment.Ground != null && c.gameObject == Environment.Ground) || Objects.TryGetId(c, out _);
            build.CanEdit = d => Permissions.CanEdit(Role, UserId, d.OwnerId, d.Locked);

            // Controller → network.
            build.CreateRequested += OnBuildCreate;
            build.UpdateRequested += OnBuildUpdate;
            build.DeleteRequested += OnBuildDelete;
            build.Refused += msg => Toast(msg, ToastKind.Warning);

            if (ui == null) return;
            var panel = ui.Build;

            // Controller → panel.
            build.ToolChanged += t => panel.SetTool((UIBuildTool)(int)t);
            build.SnapChanged += on => panel.SetSnap(on, build.MoveSnap);
            build.UndoStateChanged += (u, r) => panel.SetUndoRedo(u, r);
            build.SelectionChanged += _ => ShowSelection();
            build.SelectedDataChanged += d =>
            {
                if (build.IsDragging) panel.UpdateSelectionTransform(d.Position, d.Rotation.eulerAngles, d.Scale);
                else ShowSelection();
            };

            // Panel → controller.
            UIOn<Action>(h => ui.BuildModeToggled += h, h => ui.BuildModeToggled -= h, () => SetBuildMode(!BuildMode));
            UIOn<Action<ShapeKind>>(h => panel.ShapeSelected += h, h => panel.ShapeSelected -= h,
                s => build.CurrentShape = ProtoConvert.ToPrimShape(s));
            UIOn<Action<UIBuildTool>>(h => panel.ToolSelected += h, h => panel.ToolSelected -= h, t => build.Tool = (BuildTool)(int)t);
            UIOn<Action<bool, float>>(h => panel.SnapChanged += h, h => panel.SnapChanged -= h, (on, step) =>
            {
                build.MoveSnap = step;
                build.ScaleSnap = Mathf.Min(step, 1f);
                build.SnapEnabled = on;
            });
            UIOn<Action<Color>>(h => panel.ColorChanged += h, h => panel.ColorChanged -= h, c => build.CurrentColor = c);
            UIOn<Action<Color>>(h => panel.ColorCommitted += h, h => panel.ColorCommitted -= h, c =>
            {
                build.CurrentColor = c;
                if (build.SelectionEditable) build.ApplyColorToSelection();
            });
            UIOn<Action<MaterialKind>>(h => panel.MaterialSelected += h, h => panel.MaterialSelected -= h, m =>
            {
                build.CurrentMaterial = ProtoConvert.ToPrimMaterial(m);
                if (build.SelectionEditable) build.ApplyMaterialToSelection();
            });
            UIOn<Action<string>>(h => panel.NameChanged += h, h => panel.NameChanged -= h, n => EditSelected(d => d.Name = n));
            UIOn<Action<Vector3>>(h => panel.PositionChanged += h, h => panel.PositionChanged -= h, v => EditSelected(d => d.Position = v));
            UIOn<Action<Vector3>>(h => panel.RotationChanged += h, h => panel.RotationChanged -= h, v => EditSelected(d => d.Rotation = Quaternion.Euler(v)));
            UIOn<Action<Vector3>>(h => panel.ScaleChanged += h, h => panel.ScaleChanged -= h, v => EditSelected(d => d.Scale = v));
            UIOn<Action<bool>>(h => panel.SolidChanged += h, h => panel.SolidChanged -= h, v => EditSelected(d => d.Solid = v));
            UIOn<Action<bool>>(h => panel.LockedChanged += h, h => panel.LockedChanged -= h, v => EditSelected(d => d.Locked = v));
            UIOn<Action>(h => panel.DuplicateRequested += h, h => panel.DuplicateRequested -= h, () => build.DuplicateSelection());
            UIOn<Action>(h => panel.DeleteRequested += h, h => panel.DeleteRequested -= h, () => build.DeleteSelection());
            UIOn<Action>(h => panel.UndoRequested += h, h => panel.UndoRequested -= h, () => build.Undo());
            UIOn<Action>(h => panel.RedoRequested += h, h => panel.RedoRequested -= h, () => build.Redo());

            // Start the controller from what the panel shows.
            build.CurrentShape = ProtoConvert.ToPrimShape(panel.Shape);
            build.CurrentMaterial = ProtoConvert.ToPrimMaterial(panel.Material);
            build.CurrentColor = panel.Color;
            build.MoveSnap = panel.SnapStep;
            build.SnapEnabled = panel.SnapEnabled;
            build.Tool = (BuildTool)(int)panel.Tool;
            panel.SetUndoRedo(build.CanUndo, build.CanRedo);
        }

        void EditSelected(Action<RefBox> change)
        {
            if (build == null || !build.SelectedId.HasValue || !Objects.TryGet(build.SelectedId.Value, out var d)) return;
            var box = new RefBox { Data = d };
            change(box);
            build.SetSelectedData(box.Data);
        }

        /// <summary>Lets the inspector lambdas change one field of a struct.</summary>
        sealed class RefBox
        {
            public PrimData Data;
            public string Name { set => Data.Name = value; }
            public Vector3 Position { set => Data.Position = value; }
            public Quaternion Rotation { set => Data.Rotation = value; }
            public Vector3 Scale { set => Data.Scale = value; }
            public bool Solid { set => Data.Solid = value; }
            public bool Locked { set => Data.Locked = value; }
        }

        void BuildWelcomed(P.Welcome w)
        {
            if (build == null) return;
            var limits = w.Limits;
            build.SetLimits(ProtoConvert.ToVector3(w.World?.BoundsMin), ProtoConvert.ToVector3(w.World?.BoundsMax),
                limits != null && limits.MinScale > 0 ? limits.MinScale : 0.01f,
                limits != null && limits.MaxScale > 0 ? limits.MaxScale : 64f);
            build.Select(null);
            BuildRoleChanged();
        }

        void BuildRoleChanged()
        {
            if (build == null) return;
            build.CanBuild = Permissions.CanBuild(Role);
            ui?.Build.SetBuildAllowed(build.CanBuild);
            ShowSelection();
        }

        void RefreshBuildPanel()
        {
            if (ui == null || build == null) return;
            ui.Build.SetBuildAllowed(build.CanBuild);
            ui.Build.SetTool((UIBuildTool)(int)build.Tool);
            ui.Build.SetSnap(build.SnapEnabled, build.MoveSnap);
            ui.Build.SetUndoRedo(build.CanUndo, build.CanRedo);
            ShowSelection();
        }

        void ShowSelection()
        {
            if (ui == null || build == null) return;
            if (!build.SelectedId.HasValue || !Objects.TryGet(build.SelectedId.Value, out var d))
            {
                ui.Build.ClearSelection();
                return;
            }
            ui.Build.SetSelection(new SelectedObjectInfo
            {
                Id = d.Id,
                Name = d.Name,
                Shape = ProtoConvert.ToUI(d.Shape),
                Position = d.Position,
                Rotation = d.Rotation.eulerAngles,
                Scale = d.Scale,
                Color = d.Color,
                Material = ProtoConvert.ToUI(d.Material),
                Solid = d.Solid,
                Locked = d.Locked,
                OwnerName = OwnerName(d.OwnerId),
            }, Permissions.CanEdit(Role, UserId, d.OwnerId, d.Locked));
        }

        /// <summary>A person's name for a user id: you, someone present, or a short id.</summary>
        public string OwnerName(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return "";
            if (userId == UserId) return "you";
            foreach (var a in people.Values)
                if (a.UserId == userId) return a.Name;
            return userId.Length > 8 ? "user " + userId.Substring(0, 8) : userId;
        }

        // ---------------------------------------------------------------- controller → network

        void OnBuildCreate(PrimData d, int token)
        {
            Request(new P.ClientMessage { ObjectCreate = new P.ObjectCreate { Spec = ProtoConvert.ToSpec(d) } },
                reply =>
                {
                    if (reply.BodyCase == P.ServerMessage.BodyOneofCase.ObjectCreated && reply.ObjectCreated.Object != null)
                        build?.NotifyCreated(token, reply.ObjectCreated.Object.Id);
                    else build?.NotifyCreateFailed(token, "unexpected reply " + reply.BodyCase);
                },
                error =>
                {
                    build?.NotifyCreateFailed(token, error);
                    Toast(error, ToastKind.Error);
                });
        }

        void OnBuildUpdate(ulong id, PrimData d)
        {
            if (!Objects.TryGet(id, out var current)) return;
            var update = ProtoConvert.ToUpdate(current, d);
            Request(new P.ClientMessage { ObjectUpdate = update }, null, error =>
            {
                build?.NotifyRefused(id, error);
                Toast(error, ToastKind.Error);
            });
        }

        void OnBuildDelete(ulong id)
        {
            Request(new P.ClientMessage { ObjectDelete = new P.ObjectDelete { Id = id } }, null, error =>
            {
                build?.NotifyRefused(id, error);
                Toast(error, ToastKind.Error);
            });
        }

        void BuildObjectCreated(P.ObjectCreated m, uint replyTo) { }

        void BuildObjectChanged(ulong id)
        {
            if (build != null && build.SelectedId == id && !build.IsDragging) ShowSelection();
        }

        void BuildObjectRemoved(ulong id) { }

        void BuildTick(float dt)
        {
            if (ui != null && !ui.HotkeysEnabled) ui.HotkeysEnabled = true;
            // Escape with a selection in build mode deselects instead of opening the menu.
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (ui != null && build != null && build.Enabled && build.SelectedId.HasValue && kb != null &&
                kb.escapeKey.wasPressedThisFrame && !ui.IsTextInputFocused && !ui.IsModalOpen)
            {
                if (build.IsDragging) build.CancelDrag();
                else build.Select(null);
                ui.HotkeysEnabled = false; // for this frame only: UIRoot updates after the session
            }
            demoBuild?.Tick(dt);
        }

        void BuildDispose()
        {
            if (build != null)
            {
                build.Enabled = false;
                UnityEngine.Object.Destroy(build.gameObject);
                build = null;
            }
            buildInput?.Dispose();
            buildInput = null;
            ui?.SetBuildMode(false);
        }
    }

    /// <summary>
    /// The avatar's input with the build chords masked: while Ctrl is held in build mode, no movement
    /// and no descending, so Ctrl+D and Ctrl+Z only act on the build tools.
    /// </summary>
    public sealed class ChordMaskedInput : ILocalAvatarInput
    {
        readonly ILocalAvatarInput inner;
        readonly Func<bool> masked;

        public ChordMaskedInput(ILocalAvatarInput inner, Func<bool> masked)
        {
            this.inner = inner;
            this.masked = masked;
        }

        public ILocalAvatarInput Inner => inner;
        bool Masked => masked != null && masked();
        public Vector2 Move => Masked ? Vector2.zero : inner.Move;
        public bool Run => inner.Run;
        public bool Up => inner.Up;
        public bool Down => !Masked && inner.Down;
        public bool ConsumeJump() => inner.ConsumeJump();
        public bool ConsumeToggleFly() => inner.ConsumeToggleFly();
    }

    /// <summary>
    /// The build tools' input, minus what the UI owns: digits come through the panel (UIRoot reads them
    /// and raises ToolSelected, so the tool changes exactly once), Escape is handled by the session (deselect, else the menu), and
    /// nothing reaches the tools while a modal or text field is active.
    /// </summary>
    public sealed class GatedBuildInput : IBuildInput, IDisposable
    {
        readonly IBuildInput inner;
        readonly bool owned;
        readonly Func<bool> blocked;

        public GatedBuildInput(IBuildInput inner, bool owned, Func<bool> blocked)
        {
            this.inner = inner;
            this.owned = owned;
            this.blocked = blocked;
        }

        public IBuildInput Inner => inner;

        public BuildInputFrame Poll()
        {
            var f = inner.Poll();
            const BuildKeys digits = BuildKeys.Digit1 | BuildKeys.Digit2 | BuildKeys.Digit3 | BuildKeys.Digit4 | BuildKeys.Digit5;
            f.KeysPressed &= ~digits;
            f.KeysPressed &= ~BuildKeys.Escape;
            if (blocked != null && blocked())
            {
                f.KeysPressed = BuildKeys.None;
                f.PrimaryPressed = false;
                f.HasPointer = false;
            }
            return f;
        }

        public void Dispose()
        {
            if (owned && inner is IDisposable d) d.Dispose();
        }
    }
}
