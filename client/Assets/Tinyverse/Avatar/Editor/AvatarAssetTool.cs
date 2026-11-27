using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tinyverse.Avatars.Editor
{
    /// <summary>
    /// Batch-mode helpers that prepared the avatar assets from the Quaternius CC0 packs (see THIRD_PARTY.md). Run
    /// once by hand; the results (import settings in the .meta files, .anim clips, textures, materials) are
    /// committed, so a normal build needs none of this.
    /// <code>Unity -batchmode -quit -projectPath client -executeMethod Tinyverse.Avatars.Editor.AvatarAssetTool.Prepare -avatarSource DIR</code>
    /// DIR holds the unzipped "Universal Base Characters[Standard]", "Modular Character Outfits - Fantasy[Standard]"
    /// and "Universal Animation Library[Standard]" folders.
    /// </summary>
    public static class AvatarAssetTool
    {
        public const string Root = "Assets/Tinyverse/Avatar/Resources/Tinyverse/Avatar";
        public const string ModelsFolder = Root + "/Models";
        public const string AnimationsFolder = Root + "/Animations";
        public const string TexturesFolder = Root + "/Textures";
        public const string MaterialsFolder = Root;
        public const string StagingFolder = "Assets/Tinyverse/Avatar/Staging";

        /// <summary>Animation library clip name → saved clip name.</summary>
        private static readonly (string source, string target)[] Clips =
        {
            ("Idle_Loop", "Idle"),
            ("Walk_Loop", "Walk"),
            ("Jog_Fwd_Loop", "Jog"),
            ("Sprint_Loop", "Sprint"),
            ("Swim_Idle_Loop", "Hover"),
            ("Swim_Fwd_Loop", "FlyForward"),
            ("Jump_Loop", "Fall"),
        };

        /// <summary>The models that define a skeleton and humanoid avatar (one per body preset).</summary>
        private static readonly string[] SkeletonModels = { "Male_Peasant", "Female_Peasant" };

        public static void Prepare()
        {
            ConfigureModels();
            PrepareTextures(ArgValue("-avatarSource"));
            CreateMaterials();
            ExtractClips();
            Inspect();
        }

        private static string ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Import settings: axis conversion baked, no materials, humanoid rigs for the skeleton models.</summary>
        public static void ConfigureModels()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { ModelsFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                string name = Path.GetFileNameWithoutExtension(path);
                bool skeleton = SkeletonModels.Contains(name);
                importer.bakeAxisConversion = true;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.importAnimation = false;
                importer.importBlendShapes = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.importVisibility = false;
                importer.isReadable = true; // meshes are cut and re-split at runtime
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.animationType = skeleton ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
                importer.avatarSetup = skeleton ? ModelImporterAvatarSetup.CreateFromThisModel : ModelImporterAvatarSetup.NoAvatar;
                importer.optimizeGameObjects = false;
                importer.SaveAndReimport();
            }
            AssetDatabase.Refresh();

            // Unity's automatic mapping misses the female head; both rigs use the same bone names, so the female
            // avatar takes the male bone mapping with its own skeleton.
            var male = (ModelImporter)AssetImporter.GetAtPath(ModelsFolder + "/Male_Peasant.fbx");
            var femalePath = ModelsFolder + "/Female_Peasant.fbx";
            var female = (ModelImporter)AssetImporter.GetAtPath(femalePath);
            var description = female.humanDescription;
            description.human = male.humanDescription.human;
            var femaleModel = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(femalePath));
            femaleModel.name = "Female_Peasant";
            description.skeleton = femaleModel.GetComponentsInChildren<Transform>(true)
                .Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale })
                .ToArray();
            Object.DestroyImmediate(femaleModel);
            female.humanDescription = description;
            female.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            female.SaveAndReimport();
        }

        /// <summary>The animation library as a humanoid, the needed clips saved as .anim assets.</summary>
        public static void ExtractClips()
        {
            var path = StagingFolder + "/UAL1_Standard.fbx";
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning("[AvatarAssetTool] no animation library staged; skipping clips");
                return;
            }
            importer.bakeAxisConversion = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            var defaults = importer.defaultClipAnimations;
            var chosen = new List<ModelImporterClipAnimation>();
            foreach (var (source, target) in Clips)
            {
                var clip = defaults.FirstOrDefault(c => c.takeName.EndsWith("|" + source) || c.takeName == source);
                if (clip == null)
                {
                    Debug.LogError($"[AvatarAssetTool] clip {source} not found");
                    continue;
                }
                clip.name = target;
                clip.loopTime = true;
                clip.loopPose = true;
                clip.lockRootRotation = true;
                clip.lockRootHeightY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalOrientation = true;
                clip.keepOriginalPositionY = true;
                clip.keepOriginalPositionXZ = true;
                chosen.Add(clip);
            }
            importer.clipAnimations = chosen.ToArray();
            importer.SaveAndReimport();

            Directory.CreateDirectory(AnimationsFolder);
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview")) continue;
                var copy = Object.Instantiate(clip);
                copy.name = clip.name;
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                AnimationUtility.SetAnimationClipSettings(copy, settings);
                string target = $"{AnimationsFolder}/{clip.name}.anim";
                AssetDatabase.DeleteAsset(target);
                AssetDatabase.CreateAsset(copy, target);
                Debug.Log($"[AvatarAssetTool] saved {target} ({clip.length:0.00} s, human {clip.humanMotion})");
            }
            AssetDatabase.SaveAssets();
            MeasureRootMotion();
        }

        /// <summary>Logs the ground speed each clip was authored for, from the root-motion version of the library.</summary>
        public static void MeasureRootMotion()
        {
            var path = StagingFolder + "/UAL1_Standard_RM.fbx";
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return;
            importer.bakeAxisConversion = true;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            var chosen = new List<ModelImporterClipAnimation>();
            foreach (var (source, target) in Clips)
            {
                var clip = importer.defaultClipAnimations.FirstOrDefault(c => c.takeName.EndsWith("|" + source) || c.takeName == source);
                if (clip == null) continue;
                clip.name = target;
                chosen.Add(clip);
            }
            importer.clipAnimations = chosen.ToArray();
            importer.SaveAndReimport();
            foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                if (!clip.name.StartsWith("__preview"))
                    Debug.Log($"[AvatarAssetTool] RM {clip.name}: average speed {clip.averageSpeed.ToString("0.000")} ({clip.averageSpeed.magnitude:0.00} m/s), length {clip.length:0.000}");
        }

        // ---------------------------------------------------------------- textures

        /// <summary>
        /// Greyscale base maps normalised so that the typical skin or hair texel is near white (the material colour
        /// then sets the tint), and smaller normal maps.
        /// </summary>
        public static void PrepareTextures(string sourceDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                Debug.LogWarning("[AvatarAssetTool] -avatarSource not given; skipping textures");
                return;
            }
            Directory.CreateDirectory(TexturesFolder);
            string ubc = Path.Combine(sourceDir, "Universal Base Characters[Standard]");
            string outfits = Path.Combine(sourceDir, "Modular Character Outfits - Fantasy[Standard]");
            Grey(Path.Combine(ubc, "Base Characters/Textures/T_Superhero_Male_Ligh.png"), "Skin_Masculine.png", 1024);
            Grey(Path.Combine(ubc, "Base Characters/Textures/T_Superhero_Female_Light_BaseColor.png"), "Skin_Feminine.png", 1024);
            Grey(Path.Combine(ubc, "Hairstyles/Textures/T_Hair_1_BaseColor.png"), "Hair_1.png", 512);
            Grey(Path.Combine(ubc, "Hairstyles/Textures/T_Hair_2_BaseColor.png"), "Hair_2.png", 512);
            Normal(Path.Combine(outfits, "Textures/Peasant/T_Peasant_Normal.png"), "Cloth_Normal.png", 1024);
            File.Copy(Path.Combine(ubc, "Base Characters/Textures/T_Eye_Brown.png"), Path.Combine(TexturesFolder, "Eye.png"), true);
            AssetDatabase.Refresh();

            foreach (var file in new[] { "Skin_Masculine", "Skin_Feminine", "Hair_1", "Hair_2", "Eye", "Cloth_Normal" })
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath($"{TexturesFolder}/{file}.png");
                ti.textureType = file == "Cloth_Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = file != "Cloth_Normal";
                ti.mipmapEnabled = true;
                ti.alphaSource = TextureImporterAlphaSource.None;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }
        }

        private static Texture2D Load(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(path))) throw new IOException("cannot read " + path);
            return tex;
        }

        private static Color[] Downscale(Texture2D src, int size)
        {
            int f = src.width / size;
            var px = src.GetPixels();
            var result = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Color sum = Color.clear;
                for (int dy = 0; dy < f; dy++)
                for (int dx = 0; dx < f; dx++)
                    sum += px[(y * f + dy) * src.width + x * f + dx];
                result[y * size + x] = sum / (f * f);
            }
            return result;
        }

        private static void Grey(string source, string target, int size)
        {
            var src = Load(source);
            var px = Downscale(src, size);
            var lum = px.Select(c => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b).ToArray();
            // Normalise on the bright part of the texture (skin or hair, not the dark underwear or background).
            var sorted = lum.Where(l => l > 0.08f).OrderBy(l => l).ToArray();
            float reference = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.75f)] : 1f;
            float gain = 0.92f / Mathf.Max(0.05f, reference);
            var dst = new Texture2D(size, size, TextureFormat.RGB24, false, true);
            dst.SetPixels(lum.Select(l => { float v = Mathf.Clamp01(l * gain); return new Color(v, v, v, 1f); }).ToArray());
            File.WriteAllBytes(Path.Combine(TexturesFolder, target), dst.EncodeToPNG());
            Debug.Log($"[AvatarAssetTool] {target}: reference luminance {reference:0.000}, gain {gain:0.00}");
        }

        private static void Normal(string source, string target, int size)
        {
            var src = Load(source);
            var px = Downscale(src, size);
            for (int i = 0; i < px.Length; i++)
            {
                var n = new Vector3(px[i].r * 2f - 1f, px[i].g * 2f - 1f, px[i].b * 2f - 1f).normalized;
                px[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
            }
            var dst = new Texture2D(size, size, TextureFormat.RGB24, false, true);
            dst.SetPixels(px);
            File.WriteAllBytes(Path.Combine(TexturesFolder, target), dst.EncodeToPNG());
        }

        // ---------------------------------------------------------------- materials

        /// <summary>Plain URP Lit materials; the visual instantiates them and sets the colours.</summary>
        public static void CreateMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Tex(out var skinM, "Skin_Masculine");
            Tex(out var skinF, "Skin_Feminine");
            Tex(out var hair1, "Hair_1");
            Tex(out var hair2, "Hair_2");
            Tex(out var eye, "Eye");
            Tex(out var clothNormal, "Cloth_Normal");
            Mat(lit, "AvatarSkinMasculine", skinM, null, 0.35f);
            Mat(lit, "AvatarSkinFeminine", skinF, null, 0.35f);
            Mat(lit, "AvatarSkinPlain", null, null, 0.35f);
            Mat(lit, "AvatarHair1", hair1, null, 0.25f);
            Mat(lit, "AvatarHair2", hair2, null, 0.25f);
            Mat(lit, "AvatarEye", eye, null, 0.7f);
            Mat(lit, "AvatarCloth", null, clothNormal, 0.15f);
            AssetDatabase.SaveAssets();
        }

        private static void Tex(out Texture2D tex, string name) =>
            tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexturesFolder}/{name}.png");

        private static void Mat(Shader shader, string name, Texture2D baseMap, Texture2D normal, float smoothness)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = m == null;
            if (create) m = new Material(shader);
            m.shader = shader;
            m.name = name;
            m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BaseMap", baseMap);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            else
            {
                m.SetTexture("_BumpMap", null);
                m.DisableKeyword("_NORMALMAP");
            }
            if (create) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
        }

        // ---------------------------------------------------------------- inspection

        /// <summary>Writes the hierarchy, meshes, bounds and bone positions of every model to Logs/avatar-inspect.txt.</summary>
        public static void Inspect()
        {
            var sb = new StringBuilder();
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Tinyverse/Avatar" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                sb.AppendLine("=== " + path);
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                sb.AppendLine($"anim {importer.animationType} bakeAxis {importer.bakeAxisConversion}");
                var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<UnityEngine.Avatar>().FirstOrDefault();
                if (avatar != null) sb.AppendLine($"avatar {avatar.name} human {avatar.isHuman} valid {avatar.isValid}");
                var inst = Object.Instantiate(go);
                Dump(inst.transform, 0, sb);
                foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var m = smr.sharedMesh;
                    sb.AppendLine($"  SMR {smr.name} mesh {m?.name} verts {m?.vertexCount} sub {m?.subMeshCount} bones {smr.bones.Length} root {smr.rootBone?.name} bounds {m?.bounds}");
                }
                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>())
                    if (!clip.name.StartsWith("__preview"))
                        sb.AppendLine($"  CLIP {clip.name} len {clip.length:0.00} human {clip.humanMotion} avgSpeed {clip.averageSpeed} loop {clip.isLooping}");
                Object.DestroyImmediate(inst);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { AnimationsFolder }))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
                sb.AppendLine($"ANIM {clip.name} len {clip.length:0.00} human {clip.humanMotion} loop {clip.isLooping} curves {AnimationUtility.GetCurveBindings(clip).Length}");
            }
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/avatar-inspect.txt", sb.ToString());
        }

        private static void Dump(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 12) return;
            sb.AppendLine($"{new string(' ', depth * 2)}{t.name} pos {t.position.ToString("0.000")} lrot {t.localRotation.eulerAngles.ToString("0.0")}");
            foreach (Transform c in t) Dump(c, depth + 1, sb);
        }
    }
}
