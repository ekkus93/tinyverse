using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.Avatars
{
    /// <summary>The coloured parts of a human figure.</summary>
    public enum HumanPart
    {
        Skin,
        Hands,
        Hair,
        Eyebrows,
        Top,
        Bottom,
        Shoes,
    }

    /// <summary>
    /// A rigged, animated person assembled from the CC0 Quaternius models (see THIRD_PARTY.md): the outfit model
    /// of the body preset carries the humanoid skeleton and the clothes (top, trousers, shoes); the head, eyes and
    /// eyebrows come from the base character, cut at the neck; hair models are bound to the same skeleton. Feet at
    /// the origin, facing +Z. Colours come from <see cref="AvatarLook"/>; animation from <see cref="Animate"/>.
    /// </summary>
    public sealed class HumanFigure : MonoBehaviour
    {
        public static readonly Color ShoeColor = new Color(0.17f, 0.12f, 0.09f);

        private static readonly Dictionary<Mesh, Mesh> headMeshes = new Dictionary<Mesh, Mesh>();
        private static readonly Dictionary<Mesh, Mesh> armMeshes = new Dictionary<Mesh, Mesh>();

        private readonly Dictionary<string, Transform> bones = new Dictionary<string, Transform>();
        private readonly Dictionary<HumanPart, Material> materials = new Dictionary<HumanPart, Material>();
        private readonly List<Renderer> bodyRenderers = new List<Renderer>();
        private readonly List<Renderer> hairRenderers = new List<Renderer>();
        private Material eyeMaterial;
        private Animator animator;
        private HumanAnimationPlayer player;
        private float headTop;
        private bool visible = true;

        public BodyPreset Preset { get; private set; }
        public HairStyle Hair { get; private set; }
        public HumanColors Colors { get; private set; }
        public Animator Animator => animator;
        public HumanAnimationPlayer Player => player;

        /// <summary>Top of the head (or hair) above the feet in the rest pose, metres.</summary>
        public float Height { get; private set; } = 1.8f;

        /// <summary>The names of the hair models worn.</summary>
        public IReadOnlyList<string> HairModels => hairNames;
        private readonly List<string> hairNames = new List<string>();

        /// <summary>Builds a figure of <paramref name="preset"/> under <paramref name="parent"/>; null if its assets are missing.</summary>
        public static HumanFigure Create(Transform parent, BodyPreset preset)
        {
            if (preset == null) return null;
            var skeleton = Resources.Load<GameObject>(HumanCatalog.ModelsFolder + preset.SkeletonModel);
            var head = Resources.Load<GameObject>(HumanCatalog.ModelsFolder + preset.HeadModel);
            if (skeleton == null || head == null || !HumanAnimationPlayer.ClipsAvailable)
            {
                Debug.LogWarning($"[Avatar] assets for body preset '{preset.Id}' are missing; drawing the placeholder");
                return null;
            }
            var go = Instantiate(skeleton, parent, false);
            go.name = "Human " + preset.Id;
            var figure = go.AddComponent<HumanFigure>();
            figure.Build(preset, head);
            return figure;
        }

        private void Build(BodyPreset preset, GameObject headModel)
        {
            Preset = preset;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (!bones.ContainsKey(t.name)) bones[t.name] = t;

            materials[HumanPart.Skin] = Instance(preset.SkinMaterial);
            materials[HumanPart.Hands] = Instance(HumanCatalog.SkinPlainMaterial);
            materials[HumanPart.Hair] = Instance(HairMaterialFor(preset));
            materials[HumanPart.Eyebrows] = Instance(HairMaterialFor(preset));
            materials[HumanPart.Top] = Instance(HumanCatalog.ClothMaterial);
            materials[HumanPart.Bottom] = Instance(HumanCatalog.ClothMaterial);
            materials[HumanPart.Shoes] = Instance(HumanCatalog.ClothMaterial);
            SetColor(materials[HumanPart.Shoes], ShoeColor);
            eyeMaterial = Resources.Load<Material>(HumanCatalog.MaterialsFolder + HumanCatalog.EyeMaterial);

            // The outfit: its renderers already sit on this skeleton.
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string n = smr.name;
                if (n.Contains("_Arms"))
                {
                    smr.sharedMesh = SplitHands(smr.sharedMesh, smr.bones);
                    smr.sharedMaterials = new[] { materials[HumanPart.Top], materials[HumanPart.Hands] };
                }
                else if (n.Contains("_Body")) Assign(smr, materials[HumanPart.Top]);
                else if (n.Contains("_Legs")) Assign(smr, materials[HumanPart.Bottom]);
                else if (n.Contains("_Feet")) Assign(smr, materials[HumanPart.Shoes]);
                Configure(smr);
                bodyRenderers.Add(smr);
            }

            // The head from the base character, bound to this skeleton.
            headTop = 1.75f;
            foreach (var source in headModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                SkinnedMeshRenderer smr;
                if (source.name == "Eyes") smr = Attach(source, source.sharedMesh, eyeMaterial);
                else if (source.name == "Eyebrows") smr = Attach(source, source.sharedMesh, materials[HumanPart.Eyebrows]);
                else
                {
                    var mesh = CutHead(source.sharedMesh, source.bones);
                    smr = Attach(source, mesh, materials[HumanPart.Skin]);
                    headTop = Mathf.Max(headTop, TopOf(mesh));
                }
                if (smr != null) bodyRenderers.Add(smr);
            }
            Height = headTop;

            animator = GetComponent<Animator>();
            if (animator == null) animator = gameObject.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = null;
            player = new HumanAnimationPlayer(animator, name);
        }

        private static string HairMaterialFor(BodyPreset preset) => preset.Id == HumanCatalog.Feminine ? "AvatarHair2" : "AvatarHair1";

        /// <summary>Applies colours and hair style. A change of body preset needs a new figure.</summary>
        public void ApplyLook(AvatarLook look)
        {
            Colors = HumanCatalog.ColorsFor(look);
            SetColor(materials[HumanPart.Skin], Colors.Skin);
            // The face texture is normalised to about 0.92 of the tint; match the untextured hands to it.
            SetColor(materials[HumanPart.Hands], Colors.Skin * 0.92f);
            SetColor(materials[HumanPart.Hair], Colors.Hair);
            SetColor(materials[HumanPart.Eyebrows], Colors.Hair * 0.85f);
            SetColor(materials[HumanPart.Top], Colors.Top);
            SetColor(materials[HumanPart.Bottom], Colors.Bottom);

            var style = HumanCatalog.FindHairStyle(look.HairStyle, Preset);
            if (style != Hair) SetHair(style);
        }

        private void SetHair(HairStyle style)
        {
            Hair = style;
            foreach (var r in hairRenderers)
                if (r != null) Destroy(r.gameObject);
            hairRenderers.Clear();
            hairNames.Clear();
            float top = headTop;
            foreach (var model in style.ModelsFor(Preset))
            {
                var prefab = Resources.Load<GameObject>(HumanCatalog.ModelsFolder + model);
                if (prefab == null)
                {
                    Debug.LogWarning($"[Avatar] hair model {model} missing");
                    continue;
                }
                foreach (var source in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var smr = Attach(source, source.sharedMesh, materials[HumanPart.Hair]);
                    if (smr == null) continue;
                    smr.enabled = visible;
                    hairRenderers.Add(smr);
                    top = Mathf.Max(top, source.sharedMesh.bounds.max.y);
                }
                hairNames.Add(model);
            }
            Height = top;
        }

        public void SetVisible(bool show)
        {
            visible = show;
            foreach (var r in bodyRenderers) if (r != null) r.enabled = show;
            foreach (var r in hairRenderers) if (r != null) r.enabled = show;
        }

        public void Animate(in AvatarPose pose, float dt) => player?.Update(HumanAnimationLogic.Choose(pose.Animation, pose.Velocity), dt);

        /// <summary>The colour a part is drawn with (for tests and tools).</summary>
        public Color ColorOf(HumanPart part) =>
            materials.TryGetValue(part, out var m) && m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.clear;

        /// <summary>Every renderer of the figure, body and hair.</summary>
        public IEnumerable<Renderer> Renderers
        {
            get
            {
                foreach (var r in bodyRenderers) if (r != null) yield return r;
                foreach (var r in hairRenderers) if (r != null) yield return r;
            }
        }

        private void OnDestroy()
        {
            player?.Dispose();
            foreach (var m in materials.Values)
                if (m != null)
                {
                    if (Application.isPlaying) Destroy(m);
                    else DestroyImmediate(m);
                }
            materials.Clear();
        }

        // ---------------------------------------------------------------- assembly

        /// <summary>A skinned renderer under this figure drawing <paramref name="mesh"/> with the source's bones mapped by name.</summary>
        private SkinnedMeshRenderer Attach(SkinnedMeshRenderer source, Mesh mesh, Material material)
        {
            var srcBones = source.bones;
            var mapped = new Transform[srcBones.Length];
            for (int i = 0; i < srcBones.Length; i++)
            {
                if (srcBones[i] == null || !bones.TryGetValue(srcBones[i].name, out mapped[i]))
                {
                    Debug.LogWarning($"[Avatar] {source.name}: bone {(srcBones[i] != null ? srcBones[i].name : "null")} not in the skeleton");
                    return null;
                }
            }
            var go = new GameObject(source.name);
            go.transform.SetParent(transform, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = mapped;
            smr.rootBone = source.rootBone != null && bones.TryGetValue(source.rootBone.name, out var root) ? root : transform;
            Assign(smr, material);
            Configure(smr);
            return smr;
        }

        private static void Assign(SkinnedMeshRenderer smr, Material m)
        {
            var mats = new Material[Mathf.Max(1, smr.sharedMesh != null ? smr.sharedMesh.subMeshCount : 1)];
            for (int i = 0; i < mats.Length; i++) mats[i] = m;
            smr.sharedMaterials = mats;
        }

        private void Configure(SkinnedMeshRenderer smr)
        {
            smr.shadowCastingMode = ShadowCastingMode.On;
            smr.receiveShadows = true;
            smr.updateWhenOffscreen = false;
            smr.skinnedMotionVectors = false;
            smr.quality = SkinQuality.Bone4;
            // Generous bounds around the root (at the feet): flying stretches the body out horizontally.
            if (smr.rootBone != null)
            {
                var centre = smr.rootBone.InverseTransformPoint(transform.TransformPoint(new Vector3(0f, 1f, 0f)));
                smr.localBounds = new Bounds(centre, Vector3.one * 2.6f);
            }
        }

        private static Material Instance(string name)
        {
            var source = Resources.Load<Material>(HumanCatalog.MaterialsFolder + name);
            if (source == null)
            {
                Debug.LogWarning($"[Avatar] material {name} missing; using a runtime URP Lit");
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                return new Material(shader != null ? shader : Shader.Find("Standard")) { name = name + " (fallback)" };
            }
            return new Material(source) { name = name + " (instance)" };
        }

        private static void SetColor(Material m, Color c)
        {
            if (m == null) return;
            c.a = 1f;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        // ---------------------------------------------------------------- mesh surgery

        private static bool IsHeadBone(string bone) => bone == "Head" || bone == "neck_01";

        private static bool IsHandBone(string bone) =>
            bone.StartsWith("hand_") || bone.StartsWith("thumb_") || bone.StartsWith("index_") ||
            bone.StartsWith("middle_") || bone.StartsWith("ring_") || bone.StartsWith("pinky_");

        /// <summary>The bone with the largest weight on each vertex.</summary>
        private static string[] DominantBones(Mesh mesh, Transform[] meshBones)
        {
            var weights = mesh.boneWeights;
            var result = new string[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                var w = weights[i];
                int best = w.boneIndex0;
                float bw = w.weight0;
                if (w.weight1 > bw) { best = w.boneIndex1; bw = w.weight1; }
                if (w.weight2 > bw) { best = w.boneIndex2; bw = w.weight2; }
                if (w.weight3 > bw) { best = w.boneIndex3; }
                result[i] = best >= 0 && best < meshBones.Length && meshBones[best] != null ? meshBones[best].name : "";
            }
            return result;
        }

        private static List<int> AllTriangles(Mesh mesh)
        {
            var all = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) all.AddRange(mesh.GetTriangles(s));
            return all;
        }

        /// <summary>The base body cut down to the head and neck (the clothes cover the rest). Cached per source mesh.</summary>
        internal static Mesh CutHead(Mesh source, Transform[] meshBones)
        {
            if (headMeshes.TryGetValue(source, out var cached) && cached != null) return cached;
            var dominant = DominantBones(source, meshBones);
            var tris = AllTriangles(source);
            var kept = new List<int>();
            for (int i = 0; i + 2 < tris.Count; i += 3)
                if (IsHeadBone(dominant[tris[i]]) && IsHeadBone(dominant[tris[i + 1]]) && IsHeadBone(dominant[tris[i + 2]]))
                    kept.AddRange(new[] { tris[i], tris[i + 1], tris[i + 2] });
            var mesh = Instantiate(source);
            mesh.name = source.name + " (head)";
            mesh.subMeshCount = 1;
            mesh.SetTriangles(kept, 0, true);
            headMeshes[source] = mesh;
            return mesh;
        }

        /// <summary>Highest vertex used by a triangle, metres above the feet in the rest pose.</summary>
        internal static float TopOf(Mesh mesh)
        {
            var v = mesh.vertices;
            float top = float.MinValue;
            foreach (int i in AllTriangles(mesh)) top = Mathf.Max(top, v[i].y);
            return top == float.MinValue ? 0f : top;
        }

        /// <summary>The outfit's arms with two submeshes: sleeves (0) and hands (1). Cached per source mesh.</summary>
        internal static Mesh SplitHands(Mesh source, Transform[] meshBones)
        {
            if (armMeshes.TryGetValue(source, out var cached) && cached != null) return cached;
            var dominant = DominantBones(source, meshBones);
            var tris = AllTriangles(source);
            var cloth = new List<int>();
            var skin = new List<int>();
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                bool hand = IsHandBone(dominant[tris[i]]) && IsHandBone(dominant[tris[i + 1]]) && IsHandBone(dominant[tris[i + 2]]);
                (hand ? skin : cloth).AddRange(new[] { tris[i], tris[i + 1], tris[i + 2] });
            }
            var mesh = Instantiate(source);
            mesh.name = source.name + " (split)";
            mesh.subMeshCount = 2;
            mesh.SetTriangles(cloth, 0, true);
            mesh.SetTriangles(skin, 1, true);
            armMeshes[source] = mesh;
            return mesh;
        }
    }
}
