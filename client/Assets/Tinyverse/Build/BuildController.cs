using System;
using System.Collections.Generic;
using Tinyverse.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.Building
{
    /// <summary>
    /// The in-world build tools: placement preview, selection, move/rotate/scale gizmos, duplicate, delete and
    /// undo. Reads <see cref="ObjectRegistry"/>, never talks to the network: every change is raised as an intent
    /// event for the glue to send, and the server's answer comes back through the registry and the Notify methods.
    /// <para>Keys (build mode on, no text field focused): 1 Place, 2 Select, 3 Move, 4 Rotate, 5 Scale; G toggles
    /// snapping; Delete or Backspace deletes; Ctrl+D duplicates; Ctrl+Z undoes; Ctrl+Y or Ctrl+Shift+Z redoes;
    /// Escape cancels a drag, else deselects. Left mouse button clicks and drags.</para>
    /// </summary>
    public sealed class BuildController : MonoBehaviour
    {
        /// <summary>Most <see cref="UpdateRequested"/> per second while dragging (plus one at the end).</summary>
        public const float UpdateRate = 10f;

        /// <summary>How long after a drag the prim stays under local control waiting for the server's echo.</summary>
        public const float ReleaseTimeout = 0.75f;

        /// <summary>Distance in front of the camera the preview floats at when no surface is under the pointer.</summary>
        public const float FloatDistance = 5f;

        /// <summary>Gizmo height as a fraction of the screen height.</summary>
        public const float GizmoScreenFraction = 0.14f;

        /// <summary>Uniform-scale handle: pointer pixels for a factor of e.</summary>
        public const float UniformScalePixels = 200f;

        public const int MaxNameLength = 64;

        // ------------------------------------------------------------------ events

        /// <summary>Create a prim (Id = 0). Answer with <see cref="NotifyCreated"/> or <see cref="NotifyCreateFailed"/>.</summary>
        public event Action<PrimData, int> CreateRequested;
        /// <summary>Change a prim to this state (throttled to <see cref="UpdateRate"/> while dragging).</summary>
        public event Action<ulong, PrimData> UpdateRequested;
        public event Action<ulong> DeleteRequested;
        /// <summary>The selection changed; null when nothing is selected.</summary>
        public event Action<PrimData?> SelectionChanged;
        /// <summary>The selected prim's values changed (live while dragging, or from the server).</summary>
        public event Action<PrimData> SelectedDataChanged;
        public event Action<BuildTool> ToolChanged;
        /// <summary>Snapping was toggled (by the G key or the property).</summary>
        public event Action<bool> SnapChanged;
        public event Action<bool, bool> UndoStateChanged;
        /// <summary>A local check refused an action; the message is for the user.</summary>
        public event Action<string> Refused;

        // ------------------------------------------------------------------ state set by the glue

        /// <summary>Whether this user's role may create prims.</summary>
        public bool CanBuild { get; set; } = true;
        /// <summary>Whether this user may edit a prim. Null means yes. False keeps it selectable but read-only.</summary>
        public Func<PrimData, bool> CanEdit { get; set; }
        public Func<bool> IsPointerOverUI { get; set; }
        public Func<bool> IsTextInputFocused { get; set; }
        public Func<Vector3> AvatarPosition { get; set; }
        public Func<Vector3> AvatarForward { get; set; }

        /// <summary>
        /// Which non-prim colliders placement and selection rays may land on (the ground). Default: any non-trigger
        /// collider that is not a CharacterController or CapsuleCollider (avatars). Set it to e.g.
        /// <c>c => c.gameObject == environment.Ground</c> to be exact. Prims are always surfaces.
        /// </summary>
        public Func<Collider, bool> IsPlacementSurface { get; set; }

        public PrimShape CurrentShape { get; set; } = PrimShape.Box;
        /// <summary>Passed into <see cref="PrimData.Color"/> unchanged.</summary>
        public Color CurrentColor { get; set; } = Color.white;
        public PrimMaterial CurrentMaterial { get; set; } = PrimMaterial.Matte;
        /// <summary>Size of newly placed prims (clamped to the scale limits).</summary>
        public Vector3 PlaceScale { get; set; } = Vector3.one;
        /// <summary>Rotation of newly placed prims.</summary>
        public Quaternion PlaceRotation { get; set; } = Quaternion.identity;
        /// <summary>Tilt placed prims so their +Y follows the surface normal (default off: upright).</summary>
        public bool AlignToSurface { get; set; }

        public float MoveSnap { get; set; } = 0.5f;
        public float RotateSnap { get; set; } = 15f;
        public float ScaleSnap { get; set; } = 0.25f;

        public bool SnapEnabled
        {
            get => snap;
            set
            {
                if (snap == value) return;
                snap = value;
                SnapChanged?.Invoke(value);
            }
        }

        /// <summary>Build mode. Off cancels any drag, deselects, hides gizmo and preview and ignores input.</summary>
        public bool Enabled
        {
            get => enabledFlag;
            set
            {
                if (enabledFlag == value) return;
                if (!value)
                {
                    CancelDrag();
                    Select(null);
                    HideVisuals();
                }
                enabledFlag = value;
            }
        }

        public BuildTool Tool
        {
            get => tool;
            set
            {
                if (tool == value) return;
                CancelDrag();
                tool = value;
                ToolChanged?.Invoke(value);
            }
        }

        /// <summary>The input seam. Defaults to mouse and keyboard; tests assign a <see cref="ScriptedBuildInput"/>.</summary>
        public IBuildInput Input
        {
            get => input;
            set
            {
                if (ownsInput && input is IDisposable d && !ReferenceEquals(value, input)) d.Dispose();
                ownsInput = false;
                input = value;
            }
        }

        public Camera Camera { get; set; }
        public ObjectRegistry Registry => registry;
        public BuildLimits Limits => limits;

        // ------------------------------------------------------------------ read-only state

        public ulong? SelectedId => selected;
        /// <summary>True if something is selected and the user may edit it.</summary>
        public bool SelectionEditable => selected.HasValue && registry.TryGet(selected.Value, out var d) && Editable(d);
        public bool IsDragging => dragging;
        public BuildHandle ActiveHandle => dragging ? dragHandle : BuildHandle.None;
        public BuildHandle HoveredHandle => hovered;
        public bool CanUndo => history.CanUndo;
        public bool CanRedo => history.CanRedo;
        public UndoHistory History => history;
        public bool GizmoVisible => gizmo != null && gizmo.Visible;
        public bool PreviewVisible => preview != null && preview.activeSelf;
        /// <summary>What a click would place now (valid while <see cref="PreviewVisible"/>).</summary>
        public PrimData PreviewData => previewData;
        /// <summary>True when the previewed placement is allowed (in bounds and <see cref="CanBuild"/>).</summary>
        public bool PreviewValid => previewValid;

        /// <summary>
        /// True while build mode is on and Ctrl is held: the build keys own Ctrl+D/Z/Y. The glue can mask the
        /// avatar's Ctrl (descend) and D (strafe) while this is true.
        /// </summary>
        public bool IsChordModifierHeld => enabledFlag && ctrlHeld;

        // ------------------------------------------------------------------ fields

        ObjectRegistry registry;
        IBuildInput input;
        bool ownsInput;
        bool enabledFlag;
        bool snap = true;
        BuildTool tool = BuildTool.Select;
        BuildLimits limits = BuildLimits.Default;
        ulong? selected;
        ulong? pendingSelect;
        bool ctrlHeld;
        BuildHandle hovered;

        TransformGizmo gizmo;
        GameObject preview;
        MeshFilter previewFilter;
        MeshRenderer previewRenderer;
        PrimData previewData;
        bool previewValid;
        MaterialPropertyBlock block;

        readonly UndoHistory history = new UndoHistory();
        Target target;
        int nextToken = 1;
        readonly Dictionary<int, PrimData> createTokens = new Dictionary<int, PrimData>();
        readonly Dictionary<ulong, PrimData> lastRequested = new Dictionary<ulong, PrimData>();

        struct PendingRelease
        {
            public ulong Id;
            public PrimData Target;
            public float Deadline;
        }

        readonly List<PendingRelease> pendingReleases = new List<PendingRelease>();
        float now;

        // Drag state.
        bool dragging;
        BuildHandle dragHandle;
        ulong dragId;
        PrimData dragStart, dragLive, lastSent;
        float lastSendTime;
        bool sentAny;
        Vector3 dragAxis;
        float dragS0;
        Vector3 dragHit0;
        Vector3 ringPrev;
        float ringAngle;
        bool ringTangentMode;
        Vector2 dragPointer0, tangentScreen;
        float tangentPixelRadius;

        static readonly RaycastHit[] hitBuffer = new RaycastHit[256];
        static readonly IComparer<RaycastHit> byDistance = Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

        // ------------------------------------------------------------------ creation

        /// <summary>Creates the controller on its own GameObject, reading mouse and keyboard.</summary>
        public static BuildController Create(ObjectRegistry registry, Camera camera) => Create(registry, camera, null);

        /// <summary>Creates the controller with a given input seam (null: mouse and keyboard).</summary>
        public static BuildController Create(ObjectRegistry registry, Camera camera, IBuildInput input)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            var go = new GameObject("BuildController");
            var c = go.AddComponent<BuildController>();
            c.Init(registry, camera, input);
            return c;
        }

        void Init(ObjectRegistry reg, Camera cam, IBuildInput inp)
        {
            registry = reg;
            Camera = cam;
            target = new Target(this);
            block = new MaterialPropertyBlock();
            if (inp != null)
            {
                input = inp;
                ownsInput = false;
            }
            else
            {
                input = new InputSystemBuildInput();
                ownsInput = true;
            }
            registry.Added += OnAdded;
            registry.Changed += OnChanged;
            registry.Removed += OnRemoved;
            history.Changed += () => UndoStateChanged?.Invoke(history.CanUndo, history.CanRedo);

            gizmo = new TransformGizmo(transform);
            preview = new GameObject("BuildPreview");
            preview.transform.SetParent(transform, false);
            previewFilter = preview.AddComponent<MeshFilter>();
            previewRenderer = preview.AddComponent<MeshRenderer>();
            previewRenderer.sharedMaterial = BuildAssets.PreviewMaterial;
            previewRenderer.shadowCastingMode = ShadowCastingMode.Off;
            previewRenderer.receiveShadows = false;
            previewRenderer.lightProbeUsage = LightProbeUsage.Off;
            previewRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            preview.SetActive(false);
        }

        void OnDestroy()
        {
            if (registry != null)
            {
                registry.Added -= OnAdded;
                registry.Changed -= OnChanged;
                registry.Removed -= OnRemoved;
                if (dragging) registry.SetLocallyControlled(dragId, false);
                foreach (var p in pendingReleases) registry.SetLocallyControlled(p.Id, false);
                if (selected.HasValue) registry.SetHighlighted(selected.Value, false);
            }
            pendingReleases.Clear();
            if (ownsInput && input is IDisposable d) d.Dispose();
            input = null;
            gizmo?.Destroy();
        }

        /// <summary>Sets the world bounds (for prim centres) and the per-axis scale limits.</summary>
        public void SetLimits(Vector3 boundsMin, Vector3 boundsMax, float minScale, float maxScale)
        {
            limits = new BuildLimits
            {
                BoundsMin = Vector3.Min(boundsMin, boundsMax),
                BoundsMax = Vector3.Max(boundsMin, boundsMax),
                MinScale = Mathf.Max(ObjectRegistry.MinScale, Mathf.Min(minScale, maxScale)),
                MaxScale = Mathf.Max(minScale, maxScale),
            };
        }

        // ------------------------------------------------------------------ frame

        void Update()
        {
            if (input == null) return;
            Step(input.Poll(), Time.unscaledTime);
        }

        void LateUpdate()
        {
            // After the camera has moved: keep the gizmo at constant screen size.
            if (enabledFlag) LayoutGizmo();
        }

        /// <summary>Runs one frame of the tools with the given input at time <paramref name="time"/> (seconds).</summary>
        public void Step(BuildInputFrame f, float time)
        {
            now = time;
            ProcessPendingTimeouts();
            ctrlHeld = f.Ctrl;
            if (!enabledFlag || registry == null)
            {
                HideVisuals();
                return;
            }

            if (!(IsTextInputFocused?.Invoke() ?? false))
                HandleKeys(f);

            var cam = Camera;
            bool hasRay = f.HasPointer && cam != null && TryRay(cam, f.Pointer, out _);
            Ray ray = hasRay ? cam.ScreenPointToRay(f.Pointer) : default;

            if (dragging)
            {
                if (hasRay) UpdateDrag(ray, f);
                if (dragging && (f.PrimaryReleased || !f.PrimaryHeld)) EndDrag();
                HidePreview();
                LayoutGizmo();
                return;
            }

            bool overUI = IsPointerOverUI?.Invoke() ?? false;
            bool usable = hasRay && !overUI;

            if (tool == BuildTool.Place)
            {
                gizmo.Hide();
                hovered = BuildHandle.None;
                if (usable)
                {
                    UpdatePreview(ray);
                    if (f.PrimaryPressed) TryPlace();
                }
                else
                {
                    HidePreview();
                }
                return;
            }

            HidePreview();
            LayoutGizmo();
            hovered = BuildHandle.None;
            if (usable && gizmo.Visible)
                hovered = GizmoGeometry.Pick(gizmo.Mode, gizmoCenter, gizmoAxes, gizmoSize, ray, gizmoHideY);
            gizmo.SetHover(hovered);

            if (usable && f.PrimaryPressed)
            {
                if (hovered != BuildHandle.None)
                    BeginDrag(hovered, ray, f.Pointer);
                else
                    Select(PickPrim(ray));
            }
        }

        static bool TryRay(Camera cam, Vector2 p, out Ray ray)
        {
            ray = default;
            var r = cam.pixelRect;
            if (r.width < 1f || r.height < 1f) return false;
            ray = cam.ScreenPointToRay(p);
            var d = ray.direction;
            return !(float.IsNaN(d.x) || float.IsNaN(d.y) || float.IsNaN(d.z)) && d.sqrMagnitude > 0.5f;
        }

        void HandleKeys(BuildInputFrame f)
        {
            var k = f.KeysPressed;
            if (k == BuildKeys.None) return;

            if ((k & BuildKeys.Escape) != 0)
            {
                if (dragging) CancelDrag();
                else Select(null);
                return;
            }
            if (dragging) return;

            if (f.Ctrl)
            {
                if ((k & BuildKeys.Z) != 0)
                {
                    if (f.Shift) Redo();
                    else Undo();
                }
                if ((k & BuildKeys.Y) != 0) Redo();
                if ((k & BuildKeys.D) != 0) DuplicateSelection();
                return;
            }

            if ((k & BuildKeys.Digit1) != 0) Tool = BuildTool.Place;
            if ((k & BuildKeys.Digit2) != 0) Tool = BuildTool.Select;
            if ((k & BuildKeys.Digit3) != 0) Tool = BuildTool.Move;
            if ((k & BuildKeys.Digit4) != 0) Tool = BuildTool.Rotate;
            if ((k & BuildKeys.Digit5) != 0) Tool = BuildTool.Scale;
            if ((k & BuildKeys.G) != 0) SnapEnabled = !SnapEnabled;
            if ((k & (BuildKeys.Delete | BuildKeys.Backspace)) != 0) DeleteSelection();
        }

        void HideVisuals()
        {
            gizmo?.Hide();
            HidePreview();
            hovered = BuildHandle.None;
        }

        void HidePreview()
        {
            if (preview != null && preview.activeSelf) preview.SetActive(false);
        }

        bool Editable(in PrimData d) => CanEdit == null || CanEdit(d);

        // ------------------------------------------------------------------ gizmo layout

        Vector3 gizmoCenter;
        Quaternion gizmoAxes = Quaternion.identity;
        float gizmoSize = 1f;
        bool gizmoHideY;

        void LayoutGizmo()
        {
            if (gizmo == null) return;
            bool show = enabledFlag && selected.HasValue && Camera != null
                        && (tool == BuildTool.Move || tool == BuildTool.Rotate || tool == BuildTool.Scale);
            GameObject go = null;
            PrimData data = default;
            if (show)
            {
                go = registry.GetGameObject(selected.Value);
                show = go != null && registry.TryGet(selected.Value, out data) && Editable(data);
            }
            if (!show)
            {
                gizmo.Hide();
                return;
            }
            var tf = go.transform;
            gizmoCenter = tf.position;
            gizmoAxes = tool == BuildTool.Scale ? tf.rotation : Quaternion.identity;
            gizmoSize = GizmoSize(gizmoCenter);
            gizmoHideY = data.Shape == PrimShape.Plane;
            gizmo.Show(tool, gizmoCenter, gizmoAxes, gizmoSize, gizmoHideY);
            gizmo.SetHover(dragging ? dragHandle : hovered);
        }

        float GizmoSize(Vector3 p)
        {
            var cam = Camera;
            var ct = cam.transform;
            return BuildMath.ScreenConstantSize(cam.orthographic, cam.fieldOfView, cam.orthographicSize, ct.position,
                ct.forward, p, GizmoScreenFraction);
        }

        // ------------------------------------------------------------------ picking

        int RaycastSorted(Ray ray)
        {
            Physics.SyncTransforms();
            int n = Physics.RaycastNonAlloc(ray, hitBuffer, BuildMath.MaxRayDistance, Physics.AllLayers, QueryTriggerInteraction.Collide);
            Array.Sort(hitBuffer, 0, n, byDistance);
            return n;
        }

        bool IsSurface(Collider c)
        {
            if (IsPlacementSurface != null) return IsPlacementSurface(c);
            return !c.isTrigger && !(c is CharacterController) && !(c is CapsuleCollider);
        }

        /// <summary>The prim under the ray, or null. Non-prim surfaces (the ground) block; avatars do not.</summary>
        ulong? PickPrim(Ray ray)
        {
            int n = RaycastSorted(ray);
            for (int i = 0; i < n; i++)
            {
                var c = hitBuffer[i].collider;
                if (registry.TryGetId(c, out var id)) return id;
                if (IsSurface(c)) return null;
            }
            return null;
        }

        bool SurfaceHit(Ray ray, out RaycastHit hit)
        {
            int n = RaycastSorted(ray);
            for (int i = 0; i < n; i++)
            {
                var c = hitBuffer[i].collider;
                if (registry.TryGetId(c, out _) || IsSurface(c))
                {
                    hit = hitBuffer[i];
                    return true;
                }
            }
            hit = default;
            return false;
        }

        // ------------------------------------------------------------------ placement

        PrimData NewPrim()
        {
            return new PrimData
            {
                Id = 0,
                Name = "",
                Shape = CurrentShape,
                Material = CurrentMaterial,
                Rotation = BuildMath.Normalised(PlaceRotation),
                Scale = BuildMath.ClampScale(PlaceScale, limits.MinScale, limits.MaxScale),
                Color = CurrentColor,
                Solid = true,
                Locked = false,
            };
        }

        /// <summary>Where a click with this ray would place the current shape.</summary>
        public PrimData ComputePlacement(Ray ray, out bool onSurface)
        {
            var d = NewPrim();
            float step = snap ? MoveSnap : 0f;
            onSurface = SurfaceHit(ray, out var hit);
            if (onSurface)
            {
                var n = hit.normal.sqrMagnitude > 1e-6f ? hit.normal.normalized : Vector3.up;
                if (AlignToSurface) d.Rotation = Quaternion.FromToRotation(Vector3.up, n) * d.Rotation;
                d.Position = BuildMath.RestingCentre(d.Shape, d.Scale, d.Rotation, hit.point, n, step);
            }
            else
            {
                d.Position = BuildMath.Snap(ray.GetPoint(FloatDistance), step);
            }
            return d;
        }

        void UpdatePreview(Ray ray)
        {
            previewData = ComputePlacement(ray, out _);
            previewValid = CanBuild && BuildMath.InBounds(previewData.Position, limits.BoundsMin, limits.BoundsMax);
            previewFilter.sharedMesh = PrimMeshes.Get(previewData.Shape);
            var tf = preview.transform;
            tf.SetPositionAndRotation(previewData.Position, previewData.Rotation);
            tf.localScale = BuildMath.VisibleScale(previewData.Shape, previewData.Scale);
            block.Clear();
            if (previewValid)
            {
                // PrimData.Color is sRGB; convert it as the registry does for its shader, so the preview matches the placed prim.
                var c = PrimMaterials.ToShaderColor(previewData.Color);
                block.SetVector(BuildAssets.ColorId, new Vector4(c.x, c.y, c.z, 0.5f));
            }
            else
            {
                block.SetColor(BuildAssets.ColorId, new Color(1f, 0.12f, 0.08f, 0.55f));
            }
            previewRenderer.SetPropertyBlock(block);
            if (!preview.activeSelf) preview.SetActive(true);
        }

        void TryPlace()
        {
            if (!CanBuild)
            {
                Refuse("You don't have permission to build here.");
                return;
            }
            if (!BuildMath.InBounds(previewData.Position, limits.BoundsMin, limits.BoundsMax))
            {
                Refuse("That is outside the world bounds.");
                return;
            }
            RequestCreate(previewData);
        }

        /// <summary>
        /// Places the current shape a little in front of the avatar, resting on whatever is below. Returns the
        /// request token, or 0 if refused.
        /// </summary>
        public int PlaceInFrontOfAvatar()
        {
            if (!CanBuild)
            {
                Refuse("You don't have permission to build here.");
                return 0;
            }
            if (AvatarPosition == null)
            {
                Refuse("No avatar position.");
                return 0;
            }
            var d = NewPrim();
            var pos = AvatarPosition();
            var fwd = AvatarForward != null ? AvatarForward() : Vector3.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            float reach = 1.5f + BuildMath.Support(d.Shape, d.Scale, d.Rotation, -fwd);
            var at = pos + fwd * reach;
            float step = snap ? MoveSnap : 0f;
            if (SurfaceHit(new Ray(at + Vector3.up * 3f, Vector3.down), out var hit) && hit.point.y <= pos.y + 2f)
                d.Position = BuildMath.RestingCentre(d.Shape, d.Scale, d.Rotation, hit.point, hit.normal, step);
            else
                d.Position = BuildMath.Snap(at + Vector3.up * BuildMath.Support(d.Shape, d.Scale, d.Rotation, Vector3.down), step);
            if (!BuildMath.InBounds(d.Position, limits.BoundsMin, limits.BoundsMax))
            {
                Refuse("That is outside the world bounds.");
                return 0;
            }
            return RequestCreate(d);
        }

        int RequestCreate(PrimData d)
        {
            d.Id = 0;
            int token = nextToken++;
            createTokens[token] = d;
            CreateRequested?.Invoke(d, token);
            return token;
        }

        void Refuse(string message) => Refused?.Invoke(message);

        // ------------------------------------------------------------------ selection

        /// <summary>Selects a prim (highlighting it), or clears the selection with null. Unknown ids clear it.</summary>
        public void Select(ulong? id)
        {
            PrimData data = default;
            if (id.HasValue && !registry.TryGet(id.Value, out data)) id = null;
            if (id == selected) return;
            if (dragging) CancelDrag();
            if (selected.HasValue) registry.SetHighlighted(selected.Value, false);
            selected = id;
            pendingSelect = null;
            hovered = BuildHandle.None;
            if (id.HasValue) registry.SetHighlighted(id.Value, true);
            LayoutGizmo();
            SelectionChanged?.Invoke(id.HasValue ? data : (PrimData?)null);
        }

        void OnAdded(PrimData d)
        {
            if (pendingSelect == d.Id) Select(d.Id);
        }

        void OnChanged(PrimData d)
        {
            if (lastRequested.TryGetValue(d.Id, out var lr) && BuildMath.SameState(lr, d))
                lastRequested.Remove(d.Id);

            for (int i = pendingReleases.Count - 1; i >= 0; i--)
            {
                var p = pendingReleases[i];
                if (p.Id != d.Id || !BuildMath.SameTransform(p.Target, d)) continue;
                pendingReleases.RemoveAt(i);
                registry.SetLocallyControlled(d.Id, false);
            }

            if (selected == d.Id && !(dragging && dragId == d.Id))
                SelectedDataChanged?.Invoke(d);
        }

        void OnRemoved(ulong id)
        {
            if (dragging && dragId == id) dragging = false;
            for (int i = pendingReleases.Count - 1; i >= 0; i--)
                if (pendingReleases[i].Id == id) pendingReleases.RemoveAt(i);
            lastRequested.Remove(id);
            if (pendingSelect == id) pendingSelect = null;
            if (selected == id)
            {
                selected = null;
                hovered = BuildHandle.None;
                gizmo?.Hide();
                SelectionChanged?.Invoke(null);
            }
        }

        // ------------------------------------------------------------------ dragging

        void BeginDrag(BuildHandle h, Ray ray, Vector2 pointer)
        {
            if (!selected.HasValue || !registry.TryGet(selected.Value, out var data) || !Editable(data)) return;
            var center = data.Position;
            float size = GizmoSize(center);
            int axis = GizmoGeometry.AxisIndex(h);

            if (GizmoGeometry.IsMoveAxis(h))
            {
                dragAxis = BuildMath.Axis(axis);
                if (!BuildMath.AxisParameter(ray, center, dragAxis, out dragS0)) return;
            }
            else if (GizmoGeometry.IsMovePlane(h))
            {
                dragAxis = BuildMath.Axis(axis);
                if (!BuildMath.RayPlane(ray, center, dragAxis, out dragHit0)) return;
            }
            else if (GizmoGeometry.IsRotate(h))
            {
                dragAxis = BuildMath.Axis(axis);
                ringAngle = 0f;
                ringTangentMode = Mathf.Abs(Vector3.Dot(ray.direction.normalized, dragAxis)) < 0.2f;
                if (ringTangentMode)
                {
                    GizmoGeometry.RingDistance(ray, center, dragAxis, GizmoGeometry.RingRadius * size, out _, out var grab);
                    var tangent = Vector3.Cross(dragAxis, grab - center).normalized;
                    var cam = Camera;
                    Vector2 s0 = cam.WorldToScreenPoint(grab);
                    Vector2 s1 = cam.WorldToScreenPoint(grab + tangent * (0.25f * size));
                    Vector2 sc = cam.WorldToScreenPoint(center);
                    tangentScreen = (s1 - s0).sqrMagnitude > 1e-6f ? (s1 - s0).normalized : Vector2.right;
                    tangentPixelRadius = Mathf.Max(30f, (s0 - sc).magnitude);
                    dragPointer0 = pointer;
                }
                else if (!BuildMath.RingVector(ray, center, dragAxis, out ringPrev))
                {
                    return;
                }
            }
            else if (GizmoGeometry.IsScaleAxis(h))
            {
                dragAxis = data.Rotation * BuildMath.Axis(axis);
                if (!BuildMath.AxisParameter(ray, center, dragAxis, out dragS0)) return;
                float minGrab = 0.1f * size;
                if (Mathf.Abs(dragS0) < minGrab) dragS0 = dragS0 < 0f ? -minGrab : minGrab;
            }
            else if (h == BuildHandle.ScaleUniform)
            {
                dragPointer0 = pointer;
            }
            else
            {
                return;
            }

            // Re-taking a prim that still waits for its last echo: keep local control, drop the wait.
            for (int i = pendingReleases.Count - 1; i >= 0; i--)
                if (pendingReleases[i].Id == data.Id) pendingReleases.RemoveAt(i);

            dragging = true;
            dragHandle = h;
            dragId = data.Id;
            dragStart = dragLive = lastSent = data;
            sentAny = false;
            lastSendTime = float.NegativeInfinity;
            registry.SetLocallyControlled(data.Id, true);
            ApplyTransform(data);
        }

        void UpdateDrag(Ray ray, BuildInputFrame f)
        {
            var c = dragStart;
            var center = dragStart.Position;
            var h = dragHandle;
            int axis = GizmoGeometry.AxisIndex(h);

            if (GizmoGeometry.IsMoveAxis(h))
            {
                if (!BuildMath.AxisParameter(ray, center, dragAxis, out float s)) return;
                var p = dragStart.Position + dragAxis * (s - dragS0);
                if (snap) p[axis] = BuildMath.Snap(p[axis], MoveSnap);
                c.Position = BuildMath.ClampToBounds(p, limits.BoundsMin, limits.BoundsMax);
            }
            else if (GizmoGeometry.IsMovePlane(h))
            {
                if (!BuildMath.RayPlane(ray, center, dragAxis, out var hit)) return;
                var p = dragStart.Position + (hit - dragHit0);
                p[axis] = dragStart.Position[axis];
                if (snap)
                {
                    int u = (axis + 1) % 3, v = (axis + 2) % 3;
                    p[u] = BuildMath.Snap(p[u], MoveSnap);
                    p[v] = BuildMath.Snap(p[v], MoveSnap);
                }
                c.Position = BuildMath.ClampToBounds(p, limits.BoundsMin, limits.BoundsMax);
            }
            else if (GizmoGeometry.IsRotate(h))
            {
                if (ringTangentMode)
                {
                    if (!f.HasPointer) return;
                    ringAngle = Vector2.Dot(f.Pointer - dragPointer0, tangentScreen) / tangentPixelRadius * Mathf.Rad2Deg;
                }
                else
                {
                    if (!BuildMath.RingVector(ray, center, dragAxis, out var v)) return;
                    ringAngle += BuildMath.SignedAngle(ringPrev, v, dragAxis);
                    ringPrev = v;
                }
                float angle = snap ? BuildMath.SnapAngle(ringAngle, RotateSnap) : ringAngle;
                c.Rotation = BuildMath.Normalised(Quaternion.AngleAxis(angle, dragAxis) * dragStart.Rotation);
            }
            else if (GizmoGeometry.IsScaleAxis(h))
            {
                if (!BuildMath.AxisParameter(ray, center, dragAxis, out float s)) return;
                float factor = Mathf.Max(0f, s / dragS0);
                var sc = dragStart.Scale;
                sc[axis] = BuildMath.ScaleComponent(sc[axis] * factor, snap ? ScaleSnap : 0f, limits.MinScale, limits.MaxScale);
                c.Scale = sc;
            }
            else if (h == BuildHandle.ScaleUniform)
            {
                if (!f.HasPointer) return;
                var dp = f.Pointer - dragPointer0;
                float factor = Mathf.Exp((dp.x + dp.y) / UniformScalePixels);
                c.Scale = BuildMath.ScaleUniform(dragStart.Scale, factor, snap ? ScaleSnap : 0f, limits.MinScale, limits.MaxScale);
            }

            if (BuildMath.SameTransform(c, dragLive) && c.Position == dragLive.Position && c.Scale == dragLive.Scale
                && c.Rotation == dragLive.Rotation)
                return;
            dragLive = c;
            ApplyTransform(c);
            SelectedDataChanged?.Invoke(c);
            if (now - lastSendTime >= 1f / UpdateRate - 1e-4f && !BuildMath.SameTransform(dragLive, lastSent))
                SendDrag();
        }

        void SendDrag()
        {
            lastSent = dragLive;
            lastSendTime = now;
            sentAny = true;
            lastRequested[dragId] = dragLive;
            UpdateRequested?.Invoke(dragId, dragLive);
        }

        void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            bool changed = !BuildMath.SameTransform(dragLive, dragStart);
            if (changed || sentAny)
            {
                pendingReleases.Add(new PendingRelease { Id = dragId, Target = dragLive, Deadline = now + ReleaseTimeout });
                if (changed) history.RecordModify(dragId, dragStart, dragLive);
                lastRequested[dragId] = dragLive;
                UpdateRequested?.Invoke(dragId, dragLive);
            }
            else
            {
                registry.SetLocallyControlled(dragId, false);
            }
        }

        /// <summary>Cancels a drag in progress and puts the prim back where it was (Escape).</summary>
        public void CancelDrag()
        {
            if (!dragging) return;
            dragging = false;
            ApplyTransform(dragStart);
            if (sentAny)
            {
                // The server already has intermediate states: ask it for the original back.
                pendingReleases.Add(new PendingRelease { Id = dragId, Target = dragStart, Deadline = now + ReleaseTimeout });
                lastRequested[dragId] = dragStart;
                UpdateRequested?.Invoke(dragId, dragStart);
            }
            else
            {
                registry.SetLocallyControlled(dragId, false);
            }
            if (selected == dragId) SelectedDataChanged?.Invoke(dragStart);
        }

        void ApplyTransform(in PrimData d)
        {
            var go = registry.GetGameObject(d.Id);
            if (go == null) return;
            var tf = go.transform;
            tf.SetLocalPositionAndRotation(d.Position, d.Rotation);
            tf.localScale = BuildMath.VisibleScale(d.Shape, d.Scale);
        }

        void ProcessPendingTimeouts()
        {
            for (int i = pendingReleases.Count - 1; i >= 0; i--)
            {
                var p = pendingReleases[i];
                if (now < p.Deadline) continue;
                pendingReleases.RemoveAt(i);
                if (!(dragging && dragId == p.Id)) registry.SetLocallyControlled(p.Id, false);
            }
        }

        /// <summary>True while the prim is held locally after an edit, waiting for the server's echo.</summary>
        public bool IsAwaitingEcho(ulong id)
        {
            foreach (var p in pendingReleases)
                if (p.Id == id) return true;
            return false;
        }

        // ------------------------------------------------------------------ edits from the glue

        /// <summary>Requests a change of the selected prim with undo, showing transform changes at once.</summary>
        void RequestEdit(PrimData before, PrimData after)
        {
            history.RecordModify(before.Id, before, after);
            RequestUpdate(before.Id, after);
        }

        void RequestUpdate(ulong id, PrimData data)
        {
            data.Id = id;
            if (registry.TryGet(id, out var stored) && !BuildMath.SameTransform(stored, data))
            {
                registry.SetLocallyControlled(id, true);
                ApplyTransform(data);
                for (int i = pendingReleases.Count - 1; i >= 0; i--)
                    if (pendingReleases[i].Id == id) pendingReleases.RemoveAt(i);
                pendingReleases.Add(new PendingRelease { Id = id, Target = data, Deadline = now + ReleaseTimeout });
            }
            lastRequested[id] = data;
            if (selected == id) SelectedDataChanged?.Invoke(data);
            UpdateRequested?.Invoke(id, data);
        }

        bool TryEditableSelection(out PrimData data)
        {
            data = default;
            if (!selected.HasValue || !registry.TryGet(selected.Value, out data)) return false;
            if (!Editable(data))
            {
                Refuse("You can't edit this object.");
                return false;
            }
            return true;
        }

        /// <summary>Applies <see cref="CurrentColor"/> to the selected prim.</summary>
        public void ApplyColorToSelection()
        {
            if (!TryEditableSelection(out var d) || d.Color == CurrentColor) return;
            var after = d;
            after.Color = CurrentColor;
            RequestEdit(d, after);
        }

        /// <summary>Applies <see cref="CurrentMaterial"/> to the selected prim.</summary>
        public void ApplyMaterialToSelection()
        {
            if (!TryEditableSelection(out var d) || d.Material == CurrentMaterial) return;
            var after = d;
            after.Material = CurrentMaterial;
            RequestEdit(d, after);
        }

        /// <summary>
        /// The inspector edited the selected prim (any fields: transform, name, flags, colour, material, shape).
        /// Validated against the limits; refused values raise <see cref="Refused"/> and change nothing.
        /// </summary>
        public void SetSelectedData(PrimData data)
        {
            if (dragging) return;
            if (!TryEditableSelection(out var d)) return;
            data.Id = d.Id;
            data.OwnerId = d.OwnerId;
            data.Rotation = BuildMath.Normalised(data.Rotation);
            data.Name = data.Name ?? "";
            if (data.Name.Length > MaxNameLength)
            {
                Refuse($"Names are at most {MaxNameLength} characters.");
                return;
            }
            if (!IsFinite(data.Position) || !BuildMath.InBounds(data.Position, limits.BoundsMin, limits.BoundsMax))
            {
                Refuse("That position is outside the world bounds.");
                return;
            }
            if (!IsFinite(data.Scale) || !BuildMath.ScaleInLimits(data.Scale, limits.MinScale, limits.MaxScale))
            {
                Refuse($"Sizes must be between {limits.MinScale:0.##} and {limits.MaxScale:0.##} m.");
                return;
            }
            if ((int)data.Shape < 0 || (int)data.Shape > (int)PrimShape.Plane) data.Shape = d.Shape;
            if ((int)data.Material < 0 || (int)data.Material > (int)PrimMaterial.Emissive) data.Material = d.Material;
            if (BuildMath.SameState(d, data)) return;
            RequestEdit(d, data);
        }

        static bool IsFinite(Vector3 v) =>
            !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        /// <summary>Copies the selected prim, offset by one move-snap step along +X; the copy is selected when created.</summary>
        public void DuplicateSelection()
        {
            if (!selected.HasValue || !registry.TryGet(selected.Value, out var d)) return;
            if (!CanBuild)
            {
                Refuse("You don't have permission to build here.");
                return;
            }
            float step = MoveSnap > 0f ? MoveSnap : 0.5f;
            var copy = d;
            copy.Id = 0;
            copy.OwnerId = null;
            var p = d.Position + Vector3.right * step;
            if (!BuildMath.InBounds(p, limits.BoundsMin, limits.BoundsMax)) p = d.Position - Vector3.right * step;
            copy.Position = BuildMath.ClampToBounds(p, limits.BoundsMin, limits.BoundsMax);
            RequestCreate(copy);
        }

        /// <summary>Deletes the selected prim (undoable). The selection clears when the server removes it.</summary>
        public void DeleteSelection()
        {
            if (dragging) return;
            if (!TryEditableSelection(out var d)) return;
            history.RecordDelete(d);
            DeleteRequested?.Invoke(d.Id);
        }

        public void Undo()
        {
            if (dragging) return;
            Report(history.Undo(target), "undo");
        }

        public void Redo()
        {
            if (dragging) return;
            Report(history.Redo(target), "redo");
        }

        void Report(UndoResult r, string what)
        {
            if (r == UndoResult.Dropped)
                Refuse($"Can't {what}: the object was deleted or changed by someone else.");
            else if (r == UndoResult.Busy)
                Refuse($"Can't {what} yet: waiting for the server.");
        }

        // ------------------------------------------------------------------ replies from the glue

        /// <summary>The server created the prim requested with this token.</summary>
        public void NotifyCreated(int requestToken, ulong id)
        {
            if (createTokens.TryGetValue(requestToken, out var d))
            {
                createTokens.Remove(requestToken);
                d.Id = id;
                if (registry.TryGet(id, out var stored)) d.OwnerId = stored.OwnerId;
                history.RecordCreate(d);
                if (registry.Contains(id)) Select(id);
                else pendingSelect = id;
                return;
            }
            history.NotifyCreated(requestToken, id);
        }

        public void NotifyCreateFailed(int requestToken, string reason)
        {
            if (createTokens.Remove(requestToken)) return;
            history.NotifyCreateFailed(requestToken);
        }

        /// <summary>
        /// The server refused an edit or delete of this prim. Ends any drag on it, eases it back to the server's
        /// state and forgets the newest undo step about it. Does not raise <see cref="Refused"/> (the glue has the
        /// server's message).
        /// </summary>
        public void NotifyRefused(ulong id, string reason)
        {
            if (dragging && dragId == id) dragging = false;
            for (int i = pendingReleases.Count - 1; i >= 0; i--)
                if (pendingReleases[i].Id == id) pendingReleases.RemoveAt(i);
            registry.SetLocallyControlled(id, false);
            lastRequested.Remove(id);
            history.DropLatest(id);
            if (selected == id && registry.TryGet(id, out var d)) SelectedDataChanged?.Invoke(d);
        }

        // ------------------------------------------------------------------ undo target

        sealed class Target : IUndoTarget
        {
            readonly BuildController c;

            public Target(BuildController c)
            {
                this.c = c;
            }

            public bool IsInState(ulong id, in PrimData expected)
            {
                if (!c.registry.TryGet(id, out var cur)) return false;
                if (BuildMath.SameState(cur, expected)) return true;
                return c.lastRequested.TryGetValue(id, out var lr) && BuildMath.SameState(lr, expected);
            }

            public void RequestUpdate(ulong id, PrimData data) => c.RequestUpdate(id, data);

            public void RequestDelete(ulong id) => c.DeleteRequested?.Invoke(id);

            public int ReserveToken() => c.nextToken++;

            public void RequestCreate(PrimData data, int token) => c.CreateRequested?.Invoke(data, token);
        }
    }
}
