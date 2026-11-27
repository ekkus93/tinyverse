using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World
{
    /// <summary>
    /// Creates, updates and removes the GameObjects of prims from plain data. Main thread only.
    /// Each prim is one GameObject with a MeshFilter (shared mesh), a MeshRenderer (shared preset
    /// material plus a MaterialPropertyBlock carrying the instanced colour and highlight) and one
    /// collider, a trigger when the prim is not solid.
    /// </summary>
    public sealed class ObjectRegistry
    {
        /// <summary>How long a smoothed transform change takes, in seconds.</summary>
        public const float SmoothDuration = 0.1f;

        /// <summary>Smallest scale per axis, matching the protocol's minimum.</summary>
        public const float MinScale = 0.01f;

        /// <summary>Thickness of a plane's box collider, in metres.</summary>
        public const float PlaneColliderThickness = 0.02f;

        enum ColliderKind { None, Box, Sphere, ConvexMesh }

        sealed class Entry
        {
            public PrimData Data;
            public GameObject Go;
            public Transform Tf;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Collider Collider;
            public ColliderKind Kind;
            public bool Local;
            public bool Highlighted;

            // Smoothing state; SmoothIndex is the position in the smoothing list, or -1.
            public int SmoothIndex = -1;
            public float Elapsed;
            public Vector3 FromPos, ToPos, FromScale, ToScale;
            public Quaternion FromRot, ToRot;
        }

        readonly Transform parent;
        readonly Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();
        readonly Dictionary<Collider, ulong> byCollider = new Dictionary<Collider, ulong>();
        readonly List<Entry> smoothing = new List<Entry>();
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public ObjectRegistry(Transform parent)
        {
            this.parent = parent;
        }

        public event Action<PrimData> Added;
        public event Action<PrimData> Changed;
        public event Action<ulong> Removed;

        /// <summary>The transform prims are created under (may be null: scene root).</summary>
        public Transform Parent => parent;

        public int Count => entries.Count;

        /// <summary>Number of prims currently easing towards a target. Tick costs only this many.</summary>
        public int SmoothingCount => smoothing.Count;

        /// <summary>The stored data of every prim, in no particular order.</summary>
        public IEnumerable<PrimData> All
        {
            get
            {
                foreach (var e in entries.Values) yield return e.Data;
            }
        }

        public bool Contains(ulong id) => entries.ContainsKey(id);

        /// <summary>
        /// Creates a prim or changes an existing one. With <paramref name="smooth"/> the visible
        /// transform eases to the new one over <see cref="SmoothDuration"/>; otherwise it snaps.
        /// The GameObject is kept for every change; only the collider is replaced when its type
        /// must change. The stored data is normalised (zero quaternion becomes identity, scale
        /// clamped to <see cref="MinScale"/>).
        /// </summary>
        public void Upsert(PrimData data, bool smooth = false)
        {
            data = Normalise(data);
            if (!entries.TryGetValue(data.Id, out var e))
            {
                e = Create(data);
                entries.Add(data.Id, e);
                Added?.Invoke(e.Data);
                return;
            }

            var old = e.Data;
            e.Data = data;

            if (old.Shape != data.Shape)
                e.Filter.sharedMesh = PrimMeshes.Get(data.Shape);
            UpdateCollider(e, old.Shape != data.Shape);
            if (old.Solid != data.Solid)
                e.Collider.isTrigger = !data.Solid;
            if (old.Material != data.Material)
                ApplyMaterial(e);
            if (old.Color != data.Color)
                ApplyProperties(e);

            if (!e.Local)
            {
                if (smooth)
                    BeginSmoothing(e);
                else
                    Snap(e);
            }
            Changed?.Invoke(e.Data);
        }

        /// <summary>Destroys a prim. Returns false if the id is unknown.</summary>
        public bool Remove(ulong id)
        {
            if (!entries.TryGetValue(id, out var e)) return false;
            entries.Remove(id);
            StopSmoothing(e);
            if (e.Collider != null) byCollider.Remove(e.Collider);
            DestroyObject(e.Go);
            Removed?.Invoke(id);
            return true;
        }

        /// <summary>Destroys every prim, raising <see cref="Removed"/> for each.</summary>
        public void Clear()
        {
            var ids = new List<ulong>(entries.Keys);
            foreach (var id in ids) Remove(id);
            smoothing.Clear();
            byCollider.Clear();
        }

        public bool TryGet(ulong id, out PrimData data)
        {
            if (entries.TryGetValue(id, out var e))
            {
                data = e.Data;
                return true;
            }
            data = default;
            return false;
        }

        /// <summary>The prim's GameObject, or null if the id is unknown.</summary>
        public GameObject GetGameObject(ulong id) => entries.TryGetValue(id, out var e) ? e.Go : null;

        /// <summary>Which prim a collider belongs to (for raycast picking).</summary>
        public bool TryGetId(Collider collider, out ulong id)
        {
            if (collider != null && byCollider.TryGetValue(collider, out id)) return true;
            id = 0;
            return false;
        }

        /// <summary>
        /// While true, <see cref="Upsert"/> for this id updates the stored data but leaves the
        /// visible transform to whoever is dragging it. When released, the prim eases back to
        /// the stored data.
        /// </summary>
        public void SetLocallyControlled(ulong id, bool controlled)
        {
            if (!entries.TryGetValue(id, out var e) || e.Local == controlled) return;
            e.Local = controlled;
            if (controlled)
                StopSmoothing(e);
            else
                BeginSmoothing(e);
        }

        public bool IsLocallyControlled(ulong id) => entries.TryGetValue(id, out var e) && e.Local;

        /// <summary>Turns the pulsing selection highlight on or off. Costs nothing per frame.</summary>
        public void SetHighlighted(ulong id, bool highlighted)
        {
            if (!entries.TryGetValue(id, out var e) || e.Highlighted == highlighted) return;
            e.Highlighted = highlighted;
            ApplyProperties(e);
        }

        public bool IsHighlighted(ulong id) => entries.TryGetValue(id, out var e) && e.Highlighted;

        /// <summary>Advances smoothing. Touches only prims that are currently easing.</summary>
        public void Tick(float deltaTime)
        {
            for (int i = smoothing.Count - 1; i >= 0; i--)
            {
                var e = smoothing[i];
                e.Elapsed += deltaTime;
                float t = e.Elapsed >= SmoothDuration ? 1f : e.Elapsed / SmoothDuration;
                if (t >= 1f)
                {
                    e.Tf.SetLocalPositionAndRotation(e.ToPos, e.ToRot);
                    e.Tf.localScale = e.ToScale;
                    StopSmoothing(e);
                    continue;
                }
                float s = t * t * (3f - 2f * t);
                e.Tf.SetLocalPositionAndRotation(Vector3.LerpUnclamped(e.FromPos, e.ToPos, s),
                    Quaternion.SlerpUnclamped(e.FromRot, e.ToRot, s));
                e.Tf.localScale = Vector3.LerpUnclamped(e.FromScale, e.ToScale, s);
            }
        }

        // ---------------------------------------------------------------- internals

        static PrimData Normalise(PrimData d)
        {
            var q = d.Rotation;
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (len < 1e-6f || float.IsNaN(len)) d.Rotation = Quaternion.identity;
            else if (Mathf.Abs(len - 1f) > 1e-5f) d.Rotation = new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
            d.Scale = new Vector3(Mathf.Max(MinScale, d.Scale.x), Mathf.Max(MinScale, d.Scale.y), Mathf.Max(MinScale, d.Scale.z));
            if ((int)d.Shape < 0 || (int)d.Shape > (int)PrimShape.Plane) d.Shape = PrimShape.Box;
            if ((int)d.Material < 0 || (int)d.Material > (int)PrimMaterial.Emissive) d.Material = PrimMaterial.Matte;
            return d;
        }

        // The scale actually applied to the transform: a plane ignores Y.
        static Vector3 VisibleScale(in PrimData d) =>
            d.Shape == PrimShape.Plane ? new Vector3(d.Scale.x, 1f, d.Scale.z) : d.Scale;

        Entry Create(PrimData data)
        {
            var go = new GameObject("Prim " + data.Id);
            var tf = go.transform;
            tf.SetParent(parent, false);
            tf.SetLocalPositionAndRotation(data.Position, data.Rotation);
            tf.localScale = VisibleScale(data);

            var e = new Entry
            {
                Data = data,
                Go = go,
                Tf = tf,
                Filter = go.AddComponent<MeshFilter>(),
                Renderer = go.AddComponent<MeshRenderer>(),
            };
            e.Filter.sharedMesh = PrimMeshes.Get(data.Shape);
            e.Renderer.lightProbeUsage = LightProbeUsage.Off; // ambient probe only; keeps instancing simple
            e.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Simple;
            e.Renderer.allowOcclusionWhenDynamic = true;
            ApplyMaterial(e);
            ApplyProperties(e);
            UpdateCollider(e, true);
            e.Collider.isTrigger = !data.Solid;
            return e;
        }

        void ApplyMaterial(Entry e)
        {
            e.Renderer.sharedMaterial = PrimMaterials.Get(e.Data.Material);
            e.Renderer.shadowCastingMode = e.Data.Material == PrimMaterial.Glass ? ShadowCastingMode.Off : ShadowCastingMode.On;
        }

        // Every renderer carries both instanced properties so they all batch together.
        void ApplyProperties(Entry e)
        {
            block.Clear();
            block.SetVector(PrimMaterials.ColorId, PrimMaterials.ToShaderColor(e.Data.Color));
            block.SetFloat(PrimMaterials.HighlightId, e.Highlighted ? 1f : 0f);
            e.Renderer.SetPropertyBlock(block);
        }

        static ColliderKind KindFor(in PrimData d)
        {
            switch (d.Shape)
            {
                case PrimShape.Box:
                case PrimShape.Plane:
                    return ColliderKind.Box;
                case PrimShape.Sphere:
                    // A SphereCollider only fits an evenly scaled sphere.
                    float max = Mathf.Max(d.Scale.x, Mathf.Max(d.Scale.y, d.Scale.z));
                    float min = Mathf.Min(d.Scale.x, Mathf.Min(d.Scale.y, d.Scale.z));
                    return max - min <= 0.001f * max ? ColliderKind.Sphere : ColliderKind.ConvexMesh;
                default:
                    return ColliderKind.ConvexMesh;
            }
        }

        // Makes the collider match the shape, replacing the component only if its type changes.
        void UpdateCollider(Entry e, bool shapeChanged)
        {
            var kind = KindFor(e.Data);
            if (kind != e.Kind || e.Collider == null)
            {
                bool trigger = !e.Data.Solid;
                if (e.Collider != null)
                {
                    byCollider.Remove(e.Collider);
                    DestroyObject(e.Collider);
                }
                switch (kind)
                {
                    case ColliderKind.Box: e.Collider = e.Go.AddComponent<BoxCollider>(); break;
                    case ColliderKind.Sphere: e.Collider = e.Go.AddComponent<SphereCollider>(); break;
                    default:
                        var mc = e.Go.AddComponent<MeshCollider>();
                        mc.convex = true;
                        e.Collider = mc;
                        break;
                }
                e.Collider.isTrigger = trigger;
                e.Kind = kind;
                byCollider[e.Collider] = e.Data.Id;
                shapeChanged = true;
            }
            if (!shapeChanged) return;
            switch (e.Collider)
            {
                case BoxCollider box:
                    box.center = Vector3.zero;
                    box.size = e.Data.Shape == PrimShape.Plane ? new Vector3(1f, PlaneColliderThickness, 1f) : Vector3.one;
                    break;
                case SphereCollider sphere:
                    sphere.center = Vector3.zero;
                    sphere.radius = 0.5f;
                    break;
                case MeshCollider mesh:
                    mesh.sharedMesh = PrimMeshes.GetCollision(e.Data.Shape);
                    break;
            }
        }

        void Snap(Entry e)
        {
            StopSmoothing(e);
            e.Tf.SetLocalPositionAndRotation(e.Data.Position, e.Data.Rotation);
            e.Tf.localScale = VisibleScale(e.Data);
        }

        void BeginSmoothing(Entry e)
        {
            e.Tf.GetLocalPositionAndRotation(out e.FromPos, out e.FromRot);
            e.FromScale = e.Tf.localScale;
            e.ToPos = e.Data.Position;
            e.ToRot = e.Data.Rotation;
            e.ToScale = VisibleScale(e.Data);
            e.Elapsed = 0;
            if (e.FromPos == e.ToPos && e.FromRot == e.ToRot && e.FromScale == e.ToScale)
            {
                StopSmoothing(e);
                return;
            }
            if (e.SmoothIndex < 0)
            {
                e.SmoothIndex = smoothing.Count;
                smoothing.Add(e);
            }
        }

        void StopSmoothing(Entry e)
        {
            int i = e.SmoothIndex;
            if (i < 0) return;
            int last = smoothing.Count - 1;
            if (i != last)
            {
                var moved = smoothing[last];
                smoothing[i] = moved;
                moved.SmoothIndex = i;
            }
            smoothing.RemoveAt(last);
            e.SmoothIndex = -1;
        }

        internal static void DestroyObject(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
