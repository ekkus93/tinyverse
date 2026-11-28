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
    /// A rigged, animated person (see THIRD_PARTY.md). <see cref="AvatarStyle.Modern"/>: a MakeHuman person made by
    /// tools/avatars, one model per body preset with the humanoid skeleton, the body, the top and the trousers with
    /// shoes, plus one hair model per style. <see cref="AvatarStyle.Classic"/>: the CC0 Quaternius figures, whose
    /// outfit model carries the skeleton and the clothes and whose head, eyes and eyebrows come from the base
    /// character, cut at the neck. Hair models are bound to the same skeleton by bone name. Feet at the origin,
    /// facing +Z. Colours come from <see cref="AvatarLook"/>; animation from <see cref="Animate"/>.
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
            bool modern = preset.Style == AvatarStyle.Modern;
            var skeleton = Resources.Load<GameObject>((modern ? HumanCatalog.ModernModelsFolder : HumanCatalog.ModelsFolder) + preset.SkeletonModel);
            var head = modern ? null : Resources.Load<GameObject>(HumanCatalog.ModelsFolder + preset.HeadModel);
            if (skeleton == null || (!modern && head == null) || !HumanAnimationPlayer.ClipsAvailable)
            {
                Debug.LogWarning($"[Avatar] assets for body preset '{preset.Id}' ({preset.Style}) are missing; drawing the placeholder");
                return null;
            }
            var go = Instantiate(skeleton, parent, false);
            go.name = "Human " + preset.Id;
            var figure = go.AddComponent<HumanFigure>();
            if (modern) figure.BuildModern(preset);
            else figure.Build(preset, head);
            figure.SetUpAnimator();
            return figure;
        }

        /// <summary>
        /// A generated MakeHuman person (tools/avatars): the model carries the skeleton and three skinned meshes,
        /// Body (skin and eyes), Top and Bottom (trousers and shoes), each with its own material. The hair model
        /// (hair, eyebrows, eyelashes) is added by <see cref="SetHair"/>.
        /// </summary>
        private void BuildModern(BodyPreset preset)
        {
            Preset = preset;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (!bones.ContainsKey(t.name)) bones[t.name] = t;
            var skin = Instance(preset.SkinMaterial, HumanCatalog.ModernMaterialsFolder);
            var top = Instance(preset.TopMaterial, HumanCatalog.ModernMaterialsFolder);
            var bottom = Instance(preset.BottomMaterial, HumanCatalog.ModernMaterialsFolder);
            materials[HumanPart.Skin] = skin;
            materials[HumanPart.Hands] = skin;
            materials[HumanPart.Top] = top;
            materials[HumanPart.Bottom] = bottom;
            materials[HumanPart.Shoes] = bottom;
            headTop = 0f;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.name == "Body") Assign(smr, skin);
                else if (smr.name == "Top") Assign(smr, top);
                else if (smr.name == "Bottom") Assign(smr, bottom);
                else continue;
                Configure(smr);
                bodyRenderers.Add(smr);
                if (smr.sharedMesh != null) headTop = Mathf.Max(headTop, RestTop(transform, smr.transform, smr.sharedMesh));
            }
            Height = headTop;
        }

        private void SetUpAnimator()
        {
            animator = GetComponent<Animator>();
            if (animator == null) animator = gameObject.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.runtimeAnimatorController = null;
            player = new HumanAnimationPlayer(animator, name);
            var head = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            restHeadBone = head != null ? transform.localPosition.y + transform.InverseTransformPoint(head.position).y : Height;
        }

        private float restHeadBone;

        /// <summary>
        /// Top of the head (or hair) as posed now, metres above the avatar's position (the figure's parent): the head
        /// bone's height plus how far the top was above it in the rest pose.
        /// </summary>
        public float CurrentHeadTop
        {
            get
            {
                var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head == null) return Height;
                return transform.localPosition.y + transform.InverseTransformPoint(head.position).y + Height - restHeadBone;
            }
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
        }

        private static string HairMaterialFor(BodyPreset preset) => preset.Gender == HumanCatalog.Feminine ? "AvatarHair2" : "AvatarHair1";

        /// <summary>Applies colours and hair style. A change of body preset needs a new figure.</summary>
        public void ApplyLook(AvatarLook look)
        {
            Colors = HumanCatalog.ColorsFor(look);
            SetColor(materials[HumanPart.Skin], Colors.Skin);
            if (Preset.Style == AvatarStyle.Classic)
            {
                // The face texture is normalised to about 0.92 of the tint; match the untextured hands to it.
                SetColor(materials[HumanPart.Hands], Colors.Skin * 0.92f);
                SetColor(materials[HumanPart.Hair], Colors.Hair);
                SetColor(materials[HumanPart.Eyebrows], Colors.Hair * 0.85f);
            }
            SetColor(materials[HumanPart.Top], Colors.Top);
            SetColor(materials[HumanPart.Bottom], Colors.Bottom);

            var style = HumanCatalog.FindHairStyle(look.HairStyle, Preset);
            if (style != Hair) SetHair(style);
            else if (Preset.Style == AvatarStyle.Modern) SetColor(materials[HumanPart.Hair], Colors.Hair);
        }

        private void SetHair(HairStyle style)
        {
            Hair = style;
            foreach (var r in hairRenderers)
                if (r != null) Destroy(r.gameObject);
            hairRenderers.Clear();
            hairNames.Clear();
            bool modern = Preset.Style == AvatarStyle.Modern;
            if (modern)
            {
                // Each modern hair style has its own atlas, so its own material (hair, eyebrows and eyelashes).
                var old = materials.TryGetValue(HumanPart.Hair, out var m) ? m : null;
                var hairMat = Instance(HumanCatalog.ModernHairModel(Preset, style), HumanCatalog.ModernMaterialsFolder);
                materials[HumanPart.Hair] = hairMat;
                materials[HumanPart.Eyebrows] = hairMat;
                SetColor(hairMat, Colors.Hair);
                DestroyMaterial(old);
            }
            float top = headTop;
            foreach (var model in style.ModelsFor(Preset))
            {
                var prefab = Resources.Load<GameObject>((modern ? HumanCatalog.ModernModelsFolder : HumanCatalog.ModelsFolder) + model);
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
                    top = Mathf.Max(top, modern ? RestTop(source.transform.root, source.transform, source.sharedMesh) : source.sharedMesh.bounds.max.y);
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

        /// <summary>
        /// Poses the figure for <paramref name="pose"/>: in open water (see <see cref="AvatarWater"/>) and not flying
        /// it swims, and the figure is lowered so the swimming clips sit at the depth they were authored at (the
        /// flying clips share their assets, lifted clear of the position).
        /// </summary>
        public void Animate(in AvatarPose pose, float dt)
        {
            if (player == null) return;
            bool water = pose.Animation != AvatarAnimations.Fly && AvatarWater.IsOpenWater(pose.Position);
            player.Update(HumanAnimationLogic.Choose(pose.Animation, pose.Velocity, water), dt);
            transform.localPosition = new Vector3(0f, player.SwimDrop * animator.humanScale, 0f);
        }

        /// <summary>The share of the swimming clips in the pose, 0..1.</summary>
        public float SwimShare => player != null ? player.SwimShare : 0f;

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

        /// <summary>
        /// The lowest point of the drawn figure (every skinned vertex of the body and hair, as posed now), metres
        /// above the avatar's position (the origin of the figure's parent). For tests and tools: it bakes the skinned
        /// meshes into <paramref name="scratch"/>, so outside the editor it needs readable meshes.
        /// </summary>
        public float LowestPoint(Mesh scratch)
        {
            float lowest = float.MaxValue;
            var origin = transform.parent != null ? transform.parent.position.y : 0f;
            foreach (var r in Renderers)
            {
                if (!(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                smr.BakeMesh(scratch, true);
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach (var v in scratch.vertices) lowest = Mathf.Min(lowest, m.MultiplyPoint3x4(v).y);
            }
            return lowest == float.MaxValue ? 0f : lowest - origin;
        }

        private void OnDestroy()
        {
            player?.Dispose();
            foreach (var m in new HashSet<Material>(materials.Values)) DestroyMaterial(m);
            materials.Clear();
        }

        private static void DestroyMaterial(Material m)
        {
            if (m == null) return;
            if (Application.isPlaying) Destroy(m);
            else DestroyImmediate(m);
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

        private static Material Instance(string name, string folder = HumanCatalog.MaterialsFolder)
        {
            var source = Resources.Load<Material>(folder + name);
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

        /// <summary>Top of a mesh's bounds in the space of <paramref name="root"/> (the model may rotate its mesh nodes).</summary>
        internal static float RestTop(Transform root, Transform meshNode, Mesh mesh)
        {
            var m = root.worldToLocalMatrix * meshNode.localToWorldMatrix;
            var b = mesh.bounds;
            float top = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                top = Mathf.Max(top, m.MultiplyPoint3x4(c).y);
            }
            return top;
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
