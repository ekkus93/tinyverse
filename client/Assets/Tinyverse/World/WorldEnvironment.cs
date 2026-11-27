using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World
{
    /// <summary>The world's static environment, as sent once by the server.</summary>
    public struct EnvironmentSettings
    {
        /// <summary>Side length in metres of the square ground, centred on the origin at y = 0. 0 or less means 256.</summary>
        public float GroundSize;
        /// <summary>Sky preset name: "day" (default) or "overcast". Unknown or empty names mean "day".</summary>
        public string Sky;
        /// <summary>Hour of the day, 0..24 (wrapped).</summary>
        public float TimeOfDay;
        /// <summary>World bounds; a soft border is drawn on the ground at their XZ footprint when max &gt; min.</summary>
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
    }

    /// <summary>
    /// Ground, sun, sky, ambient light and fog. <see cref="Apply"/> may be called any number
    /// of times; it builds the objects once and updates them after. <see cref="Clear"/>
    /// destroys them and restores the RenderSettings it changed.
    /// </summary>
    public sealed class WorldEnvironment
    {
        public const float DefaultGroundSize = 256f;
        public const string DefaultSky = "day";
        public const string GroundMaterialPath = "Tinyverse/World/Materials/Ground";
        public const string SkyMaterialPath = "Tinyverse/World/Materials/Sky";

        /// <summary>Metres covered by one repeat of the ground texture (a 2 x 2 checker of 8 m cells with 1 m lines).</summary>
        public const float GroundTileMetres = 16f;

        static readonly string[] skyNames = { "day", "overcast" };

        readonly Transform parent;
        GameObject root;
        GameObject ground;
        Light sun;
        ReflectionProbe probe;
        GameObject bounds;
        Material groundMaterial;
        Material skyMaterial;
        Texture2D groundTexture;
        Saved saved;
        bool hasSaved;

        public WorldEnvironment(Transform parent)
        {
            this.parent = parent;
        }

        /// <summary>The names <see cref="EnvironmentSettings.Sky"/> understands.</summary>
        public static string[] SkyPresets => (string[])skyNames.Clone();

        /// <summary>Root of everything this environment created, or null before Apply / after Clear.</summary>
        public GameObject Root => root;
        public GameObject Ground => ground;
        public Light Sun => sun;
        public GameObject BoundsMarkers => bounds;

        /// <summary>The settings last applied, with defaults filled in.</summary>
        public EnvironmentSettings Current { get; private set; }

        public void Apply(EnvironmentSettings settings)
        {
            settings = Resolve(settings);
            Current = settings;
            if (!hasSaved)
            {
                saved = Saved.Capture();
                hasSaved = true;
            }
            if (root == null) Build();

            ApplyGround(settings.GroundSize);
            ApplyBounds(settings);
            ApplyLighting(settings);
        }

        public void Clear()
        {
            ObjectRegistry.DestroyObject(root);
            ObjectRegistry.DestroyObject(groundMaterial);
            ObjectRegistry.DestroyObject(skyMaterial);
            ObjectRegistry.DestroyObject(groundTexture);
            root = ground = bounds = null;
            sun = null;
            probe = null;
            groundMaterial = skyMaterial = null;
            groundTexture = null;
            if (hasSaved)
            {
                saved.Restore();
                hasSaved = false;
            }
        }

        /// <summary>Fills in defaults: ground size, a known sky name, time wrapped to 0..24.</summary>
        public static EnvironmentSettings Resolve(EnvironmentSettings s)
        {
            if (!(s.GroundSize > 0) || float.IsInfinity(s.GroundSize)) s.GroundSize = DefaultGroundSize;
            string sky = (s.Sky ?? "").Trim().ToLowerInvariant();
            s.Sky = Array.IndexOf(skyNames, sky) >= 0 ? sky : DefaultSky;
            float t = float.IsNaN(s.TimeOfDay) || float.IsInfinity(s.TimeOfDay) ? 12f : s.TimeOfDay;
            t %= 24f;
            if (t < 0) t += 24f;
            s.TimeOfDay = t;
            return s;
        }

        /// <summary>
        /// Unit vector from the scene towards the sun for an hour of the day: rises in the east
        /// (+X) at 6, highest (60 degrees, towards -Z) at 12, sets in the west at 18, below the
        /// horizon at night.
        /// </summary>
        public static Vector3 SunDirection(float hour)
        {
            float a = (hour - 6f) / 12f * Mathf.PI;
            const float tilt = 60f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a), Mathf.Sin(a) * Mathf.Sin(tilt), -Mathf.Sin(a) * Mathf.Cos(tilt)).normalized;
        }

        // ---------------------------------------------------------------- building

        void Build()
        {
            root = new GameObject("Environment");
            root.transform.SetParent(parent, false);

            ground = new GameObject("Ground");
            ground.transform.SetParent(root.transform, false);
            ground.AddComponent<MeshFilter>().sharedMesh = GroundMesh();
            var renderer = ground.AddComponent<MeshRenderer>();
            var box = ground.AddComponent<BoxCollider>();
            box.center = new Vector3(0, -0.5f, 0); // top face at y = 0
            box.size = Vector3.one;

            groundTexture = MakeGroundTexture();
            var groundAsset = Resources.Load<Material>(GroundMaterialPath);
            groundMaterial = groundAsset != null ? new Material(groundAsset) : FallbackLit();
            groundMaterial.name = "Ground (instance)";
            groundMaterial.mainTexture = groundTexture;
            renderer.sharedMaterial = groundMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;

            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(root.transform, false);
            sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.85f;

            var probeGo = new GameObject("Sky Reflection");
            probeGo.transform.SetParent(root.transform, false);
            probe = probeGo.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.clearFlags = ReflectionProbeClearFlags.Skybox;
            probe.cullingMask = 0; // sky only
            probe.resolution = 128;
            probe.importance = 0;

            var skyAsset = Resources.Load<Material>(SkyMaterialPath);
            if (skyAsset != null) skyMaterial = new Material(skyAsset) { name = "Sky (instance)" };
            else
            {
                var shader = Shader.Find("Skybox/Procedural");
                if (shader != null) skyMaterial = new Material(shader) { name = "Sky (runtime)" };
                Debug.LogError($"[World] {SkyMaterialPath} missing from Resources");
            }
        }

        static Material FallbackLit()
        {
            Debug.LogError($"[World] {GroundMaterialPath} missing from Resources");
            return new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }

        static Mesh groundMesh;

        static Mesh GroundMesh()
        {
            if (groundMesh != null) return groundMesh;
            groundMesh = new Mesh { name = "Ground" };
            groundMesh.SetVertices(new[]
            {
                new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f),
            });
            groundMesh.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            groundMesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
            groundMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            groundMesh.RecalculateTangents();
            groundMesh.RecalculateBounds();
            return groundMesh;
        }

        // 16 m per repeat: a 2 x 2 checker of 8 m cells whose brightness differs by a few percent,
        // thin lines every metre and slightly stronger ones on the 8 m cell edges.
        static Texture2D MakeGroundTexture()
        {
            const int size = 1024;
            const int perMetre = size / 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = "GroundGrid",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            var basis = new Color32(118, 128, 112, 255);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool checker = ((x / (size / 2)) + (y / (size / 2))) % 2 == 0;
                    float k = checker ? 1.0f : 0.94f;
                    int mx = x % perMetre, my = y % perMetre;
                    bool metreLine = mx == 0 || my == 0;
                    int cx = x % (size / 2), cy = y % (size / 2);
                    bool cellLine = cx <= 1 || cx >= size / 2 - 1 || cy <= 1 || cy >= size / 2 - 1;
                    if (cellLine) k *= 0.80f;
                    else if (metreLine) k *= 0.90f;
                    px[y * size + x] = new Color32((byte)(basis.r * k), (byte)(basis.g * k), (byte)(basis.b * k), 255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true); // no longer readable: frees the CPU copy
            return tex;
        }

        void ApplyGround(float size)
        {
            ground.transform.localScale = new Vector3(size, 1f, size);
            float tiles = size / GroundTileMetres;
            groundMaterial.mainTextureScale = new Vector2(tiles, tiles);
            // Centre the checker on the origin.
            float offset = (tiles * 0.5f) % 1f;
            groundMaterial.mainTextureOffset = new Vector2(-offset, -offset);
            if (probe != null) probe.size = new Vector3(size * 2f, Mathf.Max(200f, size), size * 2f);
        }

        void ApplyBounds(EnvironmentSettings s)
        {
            ObjectRegistry.DestroyObject(bounds);
            bounds = null;
            Vector3 min = s.BoundsMin, max = s.BoundsMax;
            if (!(max.x > min.x) || !(max.z > min.z)) return;

            bounds = new GameObject("Bounds");
            bounds.transform.SetParent(root.transform, false);
            const float width = 0.12f, height = 0.04f;
            float cx = (min.x + max.x) * 0.5f, cz = (min.z + max.z) * 0.5f;
            float sx = max.x - min.x, sz = max.z - min.z;
            float y = Mathf.Max(0f, min.y) + height * 0.5f + 0.005f;
            var block = new MaterialPropertyBlock();
            block.SetVector(PrimMaterials.ColorId, new Vector4(0.25f, 0.55f, 0.9f, 1f) * 0.35f);
            block.SetFloat(PrimMaterials.HighlightId, 0f);
            Edge(new Vector3(cx, y, min.z), new Vector3(sx + width, height, width));
            Edge(new Vector3(cx, y, max.z), new Vector3(sx + width, height, width));
            Edge(new Vector3(min.x, y, cz), new Vector3(width, height, sz + width));
            Edge(new Vector3(max.x, y, cz), new Vector3(width, height, sz + width));

            void Edge(Vector3 centre, Vector3 scale)
            {
                var go = new GameObject("Edge");
                go.transform.SetParent(bounds.transform, false);
                go.transform.localPosition = centre;
                go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = PrimMeshes.Get(PrimShape.Box);
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = PrimMaterials.Get(PrimMaterial.Emissive);
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.SetPropertyBlock(block);
            }
        }

        struct SkyLook
        {
            public Color Tint;
            public float Atmosphere;
            public float Exposure;
            public float SunScale;
            public float AmbientScale;
            public float FogScale;
            public float Desaturate;
        }

        static SkyLook Look(string sky)
        {
            switch (sky)
            {
                case "overcast":
                    return new SkyLook
                    {
                        Tint = new Color(0.55f, 0.56f, 0.58f), Atmosphere = 2.4f, Exposure = 0.9f,
                        SunScale = 0.35f, AmbientScale = 1.25f, FogScale = 2.2f, Desaturate = 0.6f,
                    };
                default:
                    return new SkyLook
                    {
                        Tint = new Color(0.5f, 0.5f, 0.5f), Atmosphere = 1.0f, Exposure = 1.3f,
                        SunScale = 1f, AmbientScale = 1f, FogScale = 1f, Desaturate = 0f,
                    };
            }
        }

        void ApplyLighting(EnvironmentSettings s)
        {
            var look = Look(s.Sky);
            Vector3 toSun = SunDirection(s.TimeOfDay);
            float elevation = toSun.y;
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.08f, 0.25f, elevation));
            float high = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.5f, elevation));

            // Sun by day, a dim bluish moon opposite it by night.
            if (elevation > -0.02f)
            {
                sun.transform.rotation = Quaternion.LookRotation(-toSun, Vector3.up);
                sun.color = Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.96f, 0.9f), high);
                sun.intensity = 1.4f * look.SunScale * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.02f, 0.35f, elevation));
            }
            else
            {
                Vector3 toMoon = -toSun;
                sun.transform.rotation = Quaternion.LookRotation(-toMoon, Vector3.up);
                sun.color = new Color(0.62f, 0.72f, 1f);
                sun.intensity = 0.15f * look.SunScale * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.2f, toMoon.y));
            }
            sun.shadows = sun.intensity > 0.01f ? LightShadows.Soft : LightShadows.None;

            // Ambient: trilight sky / horizon / ground.
            Color skyDay = new Color(0.42f, 0.5f, 0.65f), skyDusk = new Color(0.4f, 0.3f, 0.35f), skyNight = new Color(0.02f, 0.03f, 0.06f);
            Color horizonDay = new Color(0.55f, 0.6f, 0.68f), horizonDusk = new Color(0.7f, 0.45f, 0.3f), horizonNight = new Color(0.03f, 0.035f, 0.05f);
            Color skyCol = Color.Lerp(skyNight, Color.Lerp(skyDusk, skyDay, high), day);
            Color horizon = Color.Lerp(horizonNight, Color.Lerp(horizonDusk, horizonDay, high), day);
            if (look.Desaturate > 0)
            {
                skyCol = Desaturate(skyCol, look.Desaturate);
                horizon = Desaturate(horizon, look.Desaturate);
            }
            skyCol *= look.AmbientScale;
            horizon *= look.AmbientScale;
            Color groundCol = horizon * 0.45f;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = skyCol;
            RenderSettings.ambientEquatorColor = horizon;
            RenderSettings.ambientGroundColor = groundCol;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.ambientProbe = TrilightProbe(skyCol, horizon, groundCol);

            // Fog matched to the horizon, scaled so the far edge of the ground fades softly.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Color.Lerp(horizon, horizon * 1.15f, day);
            RenderSettings.fogDensity = 0.8f / Mathf.Max(s.GroundSize, 100f) * look.FogScale;

            RenderSettings.sun = sun;
            if (skyMaterial != null)
            {
                skyMaterial.SetColor("_SkyTint", look.Tint);
                skyMaterial.SetColor("_GroundColor", Color.Lerp(new Color(0.05f, 0.05f, 0.06f), new Color(0.37f, 0.36f, 0.33f), day));
                skyMaterial.SetFloat("_AtmosphereThickness", look.Atmosphere);
                skyMaterial.SetFloat("_Exposure", Mathf.Lerp(0.12f, look.Exposure, day));
                skyMaterial.SetFloat("_SunSize", elevation > -0.02f ? 0.04f : 0.02f);
                skyMaterial.SetFloat("_SunSizeConvergence", 5f);
                skyMaterial.SetFloat("_SunDisk", s.Sky == "overcast" ? 0f : 2f);
                RenderSettings.skybox = skyMaterial;
            }

            // Bake the sky into the reflection probe once; impossible without a GPU.
            if (probe != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                probe.RenderProbe();
        }

        static Color Desaturate(Color c, float amount)
        {
            float g = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
            return Color.Lerp(c, new Color(g, g, g), amount);
        }

        // Spherical harmonics for a trilight ambient: horizon all round, sky from above, ground from below.
        static SphericalHarmonicsL2 TrilightProbe(Color sky, Color horizon, Color ground)
        {
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(horizon);
            sh.AddDirectionalLight(Vector3.up, (sky - horizon) * 0.8f, 1f);
            sh.AddDirectionalLight(Vector3.down, (ground - horizon) * 0.8f, 1f);
            return sh;
        }

        struct Saved
        {
            AmbientMode ambientMode;
            Color ambientSky, ambientEquator, ambientGround;
            float ambientIntensity;
            SphericalHarmonicsL2 ambientProbe;
            bool fog;
            FogMode fogMode;
            Color fogColor;
            float fogDensity;
            Material skybox;
            Light sun;

            public static Saved Capture() => new Saved
            {
                ambientMode = RenderSettings.ambientMode,
                ambientSky = RenderSettings.ambientSkyColor,
                ambientEquator = RenderSettings.ambientEquatorColor,
                ambientGround = RenderSettings.ambientGroundColor,
                ambientIntensity = RenderSettings.ambientIntensity,
                ambientProbe = RenderSettings.ambientProbe,
                fog = RenderSettings.fog,
                fogMode = RenderSettings.fogMode,
                fogColor = RenderSettings.fogColor,
                fogDensity = RenderSettings.fogDensity,
                skybox = RenderSettings.skybox,
                sun = RenderSettings.sun,
            };

            public void Restore()
            {
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientSkyColor = ambientSky;
                RenderSettings.ambientEquatorColor = ambientEquator;
                RenderSettings.ambientGroundColor = ambientGround;
                RenderSettings.ambientIntensity = ambientIntensity;
                RenderSettings.ambientProbe = ambientProbe;
                RenderSettings.fog = fog;
                RenderSettings.fogMode = fogMode;
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.skybox = skybox;
                RenderSettings.sun = sun;
            }
        }
    }
}
