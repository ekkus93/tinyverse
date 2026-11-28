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

        /// <summary>
        /// Review images of the standing idle only: every figure front and side, close-ups, and the change from
        /// walking to standing still and back. With <c>-idleBefore DIR</c> (an earlier run's output) it also writes
        /// <c>before-after.png</c>, that run's line-up above this one's.
        /// <code>Unity -batchmode -projectPath client -executeMethod Tinyverse.Avatars.Editor.AvatarGallery.RenderIdle -galleryOut DIR [-idleBefore DIR]</code>
        /// </summary>
        public static void RenderIdle()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-galleryOut");
            outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Logs/idle-review";
            int b = Array.IndexOf(args, "-idleBefore");
            string beforeDir = b >= 0 && b + 1 < args.Length ? args[b + 1] : null;
            Directory.CreateDirectory(outDir);
            var before = HumanCatalog.Style;
            try
            {
                SetUpScene();
                IdleShots();
            }
            finally
            {
                HumanCatalog.Style = before;
                Clear();
            }
            Stack(Path.Combine(outDir, "lineup.png"), Path.Combine(outDir, "idle-lineup-front.png"), Path.Combine(outDir, "idle-lineup-side.png"));
            if (beforeDir != null)
                Stack(Path.Combine(outDir, "before-after.png"), Path.Combine(beforeDir, "idle-lineup-front.png"), Path.Combine(outDir, "idle-lineup-front.png"),
                    Path.Combine(beforeDir, "idle-lineup-side.png"), Path.Combine(outDir, "idle-lineup-side.png"));
            Debug.Log("[AvatarGallery] wrote " + outDir);
        }

        /// <summary>
        /// Review images of flying at ground level: every figure standing, hovering and flying forward (two moments of
        /// each loop) side by side on the ground (<c>flight-lineup.png</c>), and taking off, flying off, stopping
        /// and landing frame by frame (<c>flight-transition-*.png</c>).
        /// <code>Unity -batchmode -projectPath client -executeMethod Tinyverse.Avatars.Editor.AvatarGallery.RenderFlight -galleryOut DIR</code>
        /// </summary>
        public static void RenderFlight()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-galleryOut");
            outDir = i >= 0 && i + 1 < args.Length ? args[i + 1] : "Logs/flight-review";
            Directory.CreateDirectory(outDir);
            var before = HumanCatalog.Style;
            try
            {
                SetUpScene();
                FlightShots();
            }
            finally
            {
                HumanCatalog.Style = before;
                Clear();
            }
            Debug.Log("[AvatarGallery] wrote " + outDir);
        }

        private static void FlightShots()
        {
            // Each figure, side view from low down so any gap under the body (or limbs through the ground) shows.
            var rows = new List<string>();
            var poses = new (string hint, float speed, float seconds)[]
            {
                ("idle", 0f, 1.5f), ("fly", 0f, 1.5f), ("fly", 0f, 3.1f), ("fly", 12f, 1.5f), ("fly", 12f, 1.8f),
            };
            var spots = new[] { -5.2f, -3.4f, -1.6f, 1.2f, 4.4f }; // the flying ones lie a body length apart
            foreach (var (style, preset) in Everyone())
            {
                for (int k = 0; k < poses.Length; k++)
                {
                    var f = Figure(style, preset, new Vector3(spots[k], 0f, 0f), 90f);
                    Pose(f, poses[k].hint, poses[k].speed, poses[k].seconds);
                }
                string name = $"flight-{style}-{preset}";
                Shoot(name, new Vector3(0f, 0.75f, -9.5f), new Vector3(0f, 0.85f, 0f), 36f, 1600, 560);
                rows.Add(Path.Combine(outDir, name + ".png"));
                Clear();
            }
            Stack(Path.Combine(outDir, "flight-lineup.png"), rows.ToArray());

            // Taking off from standing, flying off, stopping and landing: frames at the given times after each change.
            foreach (var (style, preset) in new[] { (AvatarStyle.Modern, HumanCatalog.Masculine), (AvatarStyle.Modern, HumanCatalog.FeminineSlim), (AvatarStyle.Classic, HumanCatalog.Feminine) })
            {
                var f = Figure(style, preset, Vector3.zero, 90f);
                var frames = new List<string>();
                void Frame(string tag)
                {
                    string name = $"takeoff-{style}-{preset}-{frames.Count:00}-{tag}";
                    Shoot(name, new Vector3(0f, 0.85f, -3.6f), new Vector3(0f, 0.85f, 0f), 40f, 360, 620);
                    frames.Add(Path.Combine(outDir, name + ".png"));
                }
                Pose(f, "idle", 0f, 1.5f);
                Frame("standing");
                void Steps(string hint, float speed, string tag, params float[] times)
                {
                    float done = 0f;
                    foreach (var t in times)
                    {
                        Pose(f, hint, speed, t - done);
                        done = t;
                        Frame($"{tag}-{t:0.00}s");
                    }
                }
                Steps("fly", 0f, "hover", 0.1f, 0.25f, 0.5f, 1.2f);
                Steps("fly", 12f, "fly", 0.1f, 0.25f, 0.6f);
                Steps("fly", 0f, "stop", 0.15f, 0.4f, 1.0f);
                Steps("idle", 0f, "land", 0.1f, 0.25f, 0.8f);
                Stack(Path.Combine(outDir, $"flight-transition-{style}-{preset}.png"), Horizontal, frames.ToArray());
                foreach (var p in frames) File.Delete(p);
                Clear();
            }
        }

        private static List<(AvatarStyle, string)> Everyone()
        {
            var all = new List<(AvatarStyle, string)>();
            foreach (var p in HumanCatalog.ModernPresets) all.Add((AvatarStyle.Modern, p.Id));
            foreach (var p in HumanCatalog.ClassicPresets) all.Add((AvatarStyle.Classic, p.Id));
            return all;
        }

        private static void IdleShots()
        {
            var everyone = Everyone();
            // Line-up, front and left side, after 2.5 s of standing.
            for (int i = 0; i < everyone.Count; i++)
                Pose(Figure(everyone[i].Item1, everyone[i].Item2, new Vector3((i - (everyone.Count - 1) * 0.5f) * 0.9f, 0f, 0f), 180f), "idle", 0f, 1.5f);
            Shoot("idle-lineup-front", new Vector3(0f, 1.0f, -6.4f), new Vector3(0f, 0.88f, 0f), 26f, 1600, 800);
            foreach (var go in spawned) go.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            Shoot("idle-lineup-side", new Vector3(0f, 1.0f, -6.4f), new Vector3(0f, 0.88f, 0f), 26f, 1600, 800);
            Clear();

            // Each figure on its own, front, side and from above the shoulder (hands against the hips).
            foreach (var (style, preset) in everyone)
            {
                var f = Figure(style, preset, Vector3.zero, 180f);
                Pose(f, "idle", 0f, 1.5f);
                string n = $"idle-{style}-{preset}";
                Shoot(n + "-front", new Vector3(0f, 1.0f, -3.1f), new Vector3(0f, 0.92f, 0f), 38f, 720, 1000);
                Shoot(n + "-side", new Vector3(-3.1f, 1.0f, 0f), new Vector3(0f, 0.92f, 0f), 38f, 720, 1000);
                Shoot(n + "-hands", new Vector3(-0.9f, 1.25f, -1.3f), new Vector3(0f, 0.85f, 0f), 40f, 900, 900);
                Stack(Path.Combine(outDir, n + ".png"), Horizontal, Path.Combine(outDir, n + "-front.png"), Path.Combine(outDir, n + "-side.png"), Path.Combine(outDir, n + "-hands.png"));
                Clear();
            }

            // Walking, stopping and starting again, side view: frames at the given times after each change.
            foreach (var (style, preset) in new[] { (AvatarStyle.Modern, HumanCatalog.Masculine), (AvatarStyle.Modern, HumanCatalog.Feminine), (AvatarStyle.Classic, HumanCatalog.Masculine) })
            {
                var f = Figure(style, preset, Vector3.zero, 90f);
                var frames = new List<string>();
                void Frame(string tag)
                {
                    string name = $"stop-{style}-{preset}-{frames.Count:00}-{tag}";
                    Shoot(name, new Vector3(0f, 1.0f, -3.0f), new Vector3(0f, 0.9f, 0f), 40f, 480, 720);
                    frames.Add(Path.Combine(outDir, name + ".png"));
                }
                Pose(f, "walk", 1.2f, 2.0f);
                Frame("walking");
                float done = 0f;
                foreach (var t in new[] { 0.1f, 0.2f, 0.35f, 0.5f, 0.8f, 1.5f })
                {
                    Pose(f, "idle", 0f, t - done);
                    done = t;
                    Frame($"stopped-{t:0.00}s");
                }
                done = 0f;
                foreach (var t in new[] { 0.15f, 0.35f, 0.7f })
                {
                    Pose(f, "walk", 1.2f, t - done);
                    done = t;
                    Frame($"walking-again-{t:0.00}s");
                }
                Stack(Path.Combine(outDir, $"stop-after-walk-{style}-{preset}.png"), Horizontal, frames.ToArray());
                foreach (var p in frames) File.Delete(p);
                Clear();
            }
        }

        private const bool Horizontal = true;

        private static void Stack(string target, params string[] sources) => Stack(target, false, sources);

        /// <summary>Joins PNG images into one, top to bottom (or left to right), with a thin gap.</summary>
        private static void Stack(string target, bool horizontal, params string[] sources)
        {
            var images = new List<Texture2D>();
            foreach (var s in sources)
            {
                if (!File.Exists(s)) { Debug.LogWarning("[AvatarGallery] missing " + s); continue; }
                var t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                t.LoadImage(File.ReadAllBytes(s));
                images.Add(t);
            }
            if (images.Count == 0) return;
            const int gap = 6;
            int w = 0, h = 0;
            foreach (var t in images)
            {
                if (horizontal) { w += t.width + gap; h = Mathf.Max(h, t.height); }
                else { h += t.height + gap; w = Mathf.Max(w, t.width); }
            }
            var result = new Texture2D(w, h, TextureFormat.RGB24, false);
            var fill = new Color32[w * h];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(30, 30, 30, 255);
            result.SetPixels32(fill);
            int x = 0, y = h;
            foreach (var t in images)
            {
                if (horizontal) { result.SetPixels(x, h - t.height, t.width, t.height, t.GetPixels()); x += t.width + gap; }
                else { y -= t.height; result.SetPixels(0, y, t.width, t.height, t.GetPixels()); y -= gap; }
                Object.DestroyImmediate(t);
            }
            result.Apply();
            File.WriteAllBytes(target, result.EncodeToPNG());
            Object.DestroyImmediate(result);
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
            // Without a player loop the skinning is not redone after the bones move; ask for it on every render.
            foreach (var go in spawned)
                if (go != null)
                    foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                        smr.forceMatrixRecalculationPerRender = true;
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
                        float y = clips[i].hint == "fall" ? 0.6f : 0f; // flying is drawn above its position
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
