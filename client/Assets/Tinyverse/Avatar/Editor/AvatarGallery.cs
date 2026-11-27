using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Tinyverse.Avatars.Editor
{
    /// <summary>
    /// Renders review images of the human avatars (both styles, every preset, skin tone, hair style and clip) in a
    /// plain daylight scene, for judging how they look without starting a world:
    /// <code>Unity -batchmode -projectPath client -executeMethod Tinyverse.Avatars.Editor.AvatarGallery.Render -galleryOut DIR</code>
    /// (needs a GPU, so not with -nographics).
    /// </summary>
    public static class AvatarGallery
    {
        private static readonly Color[] SkinTones =
        {
            Hex("#f6dccb"), Hex("#efc8ae"), Hex("#e3b190"), Hex("#d69f7e"), Hex("#c68a62"),
            Hex("#ad7650"), Hex("#8d5a3a"), Hex("#70452b"), Hex("#56331f"), Hex("#3e2416"),
        };

        private static Color Hex(string h) => ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.magenta;

        private static string outDir;
        private static Camera cam;
        private static readonly List<GameObject> spawned = new List<GameObject>();

        public static void Render()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-galleryOut");
            outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Logs/gallery";
            Directory.CreateDirectory(outDir);
            var before = HumanCatalog.Style;
            try
            {
                SetUpScene();
                Shots();
            }
            finally
            {
                HumanCatalog.Style = before;
                Clear();
            }
            Debug.Log("[AvatarGallery] wrote " + outDir);
        }

        private static void SetUpScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;
            sun.lightmapBakeType = LightmapBakeType.Realtime;
            sun.renderMode = LightRenderMode.ForcePixel;
            sunGo.transform.rotation = Quaternion.Euler(40f, 25f, 0f); // from behind the camera's left shoulder
            RenderSettings.sun = sun;
            var sky = new Color(0.52f, 0.62f, 0.78f);
            var horizon = new Color(0.62f, 0.64f, 0.62f);
            var groundCol = new Color(0.33f, 0.32f, 0.29f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = horizon;
            RenderSettings.ambientGroundColor = groundCol;
            RenderSettings.ambientIntensity = 1f;
            // Nothing is baked here, so the probe the shaders read is set by hand (as the world does).
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(horizon * 0.8f);
            sh.AddDirectionalLight(Vector3.up, (sky - horizon * 0.5f) * 0.9f, 1f);
            sh.AddDirectionalLight(Vector3.down, groundCol * 0.4f, 1f);
            RenderSettings.ambientProbe = sh;
            RenderSettings.fog = false;
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(10f, 1f, 10f);
            var gm = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            gm.SetColor("_BaseColor", new Color(0.55f, 0.57f, 0.5f));
            ground.GetComponent<Renderer>().sharedMaterial = gm;
            var camGo = new GameObject("GalleryCamera");
            cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.72f, 0.86f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.allowHDR = true;
        }

        private static HumanFigure Figure(AvatarStyle style, string preset, Vector3 pos, float yaw, Color? skin = null, string hair = "", Color? hairColor = null)
        {
            HumanCatalog.Style = style;
            var root = new GameObject($"{style} {preset}");
            root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            spawned.Add(root);
            var f = HumanFigure.Create(root.transform, HumanCatalog.FindPreset(preset, style));
            var look = AvatarLook.Default;
            look.BodyPreset = preset;
            look.HairStyle = hair;
            if (skin.HasValue) look.SkinColor = skin.Value;
            if (hairColor.HasValue) look.HairColor = hairColor.Value;
            f.ApplyLook(look);
            Pose(f, "idle", 0f, 1.0f);
            return f;
        }

        private static void Pose(HumanFigure f, string hint, float speed, float seconds)
        {
            var pose = new AvatarPose(f.transform.position, 0f, f.transform.parent.forward * speed, MoveKind.Walk, hint);
            int frames = Mathf.Max(1, Mathf.RoundToInt(seconds * 60f));
            for (int i = 0; i < frames; i++) f.Animate(pose, 1f / 60f);
        }

        private static void Clear()
        {
            foreach (var go in spawned) if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
        }

        private static void Shoot(string name, Vector3 from, Vector3 at, float fov, int w = 1280, int h = 720)
        {
            cam.transform.position = from;
            cam.transform.LookAt(at);
            cam.fieldOfView = fov;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render(); // the first render after a change sets up shadows and lights; keep the second
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }

        private static void Shots()
        {
            // 1. Line-up, front and back: the modern presets, then the classic ones.
            var lineup = new List<(AvatarStyle, string)>();
            foreach (var p in HumanCatalog.ModernPresets) lineup.Add((AvatarStyle.Modern, p.Id));
            foreach (var p in HumanCatalog.ClassicPresets) lineup.Add((AvatarStyle.Classic, p.Id));
            for (int i = 0; i < lineup.Count; i++)
                Figure(lineup[i].Item1, lineup[i].Item2, new Vector3((i - (lineup.Count - 1) * 0.5f) * 0.9f, 0f, 0f), 180f);
            Shoot("lineup-front", new Vector3(0f, 1.1f, -6.2f), new Vector3(0f, 0.95f, 0f), 40f);
            Shoot("lineup-back", new Vector3(0f, 1.1f, 6.2f), new Vector3(0f, 0.95f, 0f), 40f);
            // Figures face -Z here (yaw 180) so the camera at -Z sees their fronts.
            Clear();

            // 2. Faces, front and three-quarter.
            foreach (var (style, preset) in lineup)
            {
                var f = Figure(style, preset, Vector3.zero, 180f);
                var head = f.Animator.GetBoneTransform(HumanBodyBones.Head).position + new Vector3(0f, 0.08f, 0f);
                Shoot($"face-{style}-{preset}", head + new Vector3(0f, 0.02f, -0.75f), head, 30f, 720, 720);
                Shoot($"face34-{style}-{preset}", head + new Vector3(-0.5f, 0.05f, -0.55f), head, 30f, 720, 720);
                Shoot($"faceside-{style}-{preset}", head + new Vector3(-0.8f, 0f, 0f), head, 30f, 720, 720);
                Clear();
            }

            // 3. Skin tones, light to dark, both modern genders.
            foreach (var preset in new[] { HumanCatalog.Masculine, HumanCatalog.Feminine })
            {
                for (int i = 0; i < SkinTones.Length; i++)
                    Figure(AvatarStyle.Modern, preset, new Vector3((i - 4.5f) * 0.62f, 0f, 0f), 180f, SkinTones[i]);
                Shoot($"tones-{preset}", new Vector3(0f, 1.2f, -6.8f), new Vector3(0f, 1.0f, 0f), 45f);
                Shoot($"tones-faces-{preset}", new Vector3(0f, 1.5f, -4.6f), new Vector3(0f, 1.38f, 0f), 42f);
                Clear();
            }

            // 4. Every hair style on both modern genders (and classic for comparison).
            foreach (var (style, preset) in new[] { (AvatarStyle.Modern, HumanCatalog.Masculine), (AvatarStyle.Modern, HumanCatalog.Feminine), (AvatarStyle.Classic, HumanCatalog.Masculine), (AvatarStyle.Classic, HumanCatalog.Feminine) })
            {
                var hairs = HumanCatalog.HairStyles;
                var colors = new[] { Hex("#2c1f17"), Hex("#6e4a2e"), Hex("#c9a36a"), Hex("#151210"), Hex("#8f3c1e"), Hex("#4a3122") };
                for (int i = 0; i < hairs.Count; i++)
                    Figure(style, preset, new Vector3((i - 2.5f) * 0.7f, 0f, 0f), 160f, null, hairs[i].Id, colors[i]);
                Shoot($"hair-{style}-{preset}", new Vector3(0f, 1.62f, -2.9f), new Vector3(0f, 1.5f, 0f), 50f);
                Shoot($"hair-back-{style}-{preset}", new Vector3(0f, 1.62f, 2.9f), new Vector3(0f, 1.5f, 0f), 50f);
                Clear();
            }

            // 5. The clips, side view, modern masculine and feminine and classic masculine.
            var clips = new (string name, string hint, float speed, Vector3 vel)[]
            {
                ("idle", "idle", 0f, Vector3.zero), ("walk", "walk", 1.2f, Vector3.zero), ("jog", "walk", 4f, Vector3.zero),
                ("sprint", "run", 8f, Vector3.zero), ("hover", "fly", 0f, Vector3.zero), ("fly", "fly", 12f, Vector3.zero), ("fall", "fall", 0f, Vector3.zero),
            };
            foreach (var (style, preset) in new[] { (AvatarStyle.Modern, HumanCatalog.Masculine), (AvatarStyle.Modern, HumanCatalog.Feminine), (AvatarStyle.Classic, HumanCatalog.Masculine) })
            {
                foreach (var phase in new[] { 0.35f, 0.6f })
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        float y = clips[i].hint == "fly" || clips[i].hint == "fall" ? 0.6f : 0f;
                        var f = Figure(style, preset, new Vector3((i - 3f) * 1.25f, y, 0f), 90f);
                        Pose(f, clips[i].hint, clips[i].speed, 1.5f + phase);
                    }
                    Shoot($"clips-{style}-{preset}-{phase:0.00}", new Vector3(0f, 1.3f, -8.2f), new Vector3(0f, 1.0f, 0f), 45f);
                    Clear();
                }
            }

            // 6. Close-up of shoulders and arms while jogging (clothes clipping, wrists).
            foreach (var preset in new[] { HumanCatalog.Masculine, HumanCatalog.Feminine, HumanCatalog.MasculineHeavy, HumanCatalog.FeminineSlim })
            {
                for (int k = 0; k < 4; k++)
                {
                    var f = Figure(AvatarStyle.Modern, preset, new Vector3((k - 1.5f) * 1.1f, 0f, 0f), 200f);
                    Pose(f, k < 2 ? "walk" : "run", k < 2 ? 4f : 8f, 1.2f + k * 0.13f);
                }
                Shoot($"motion-closeup-{preset}", new Vector3(0f, 1.3f, -3.6f), new Vector3(0f, 1.1f, 0f), 50f);
                Clear();
            }
        }
    }
}
