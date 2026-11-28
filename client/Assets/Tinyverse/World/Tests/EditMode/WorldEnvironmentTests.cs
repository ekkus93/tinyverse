using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World.Tests
{
    public class WorldEnvironmentTests
    {
        GameObject root;
        WorldEnvironment env;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("EnvTestRoot");
            env = new WorldEnvironment(root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            env.Clear();
            Object.DestroyImmediate(root);
        }

        static EnvironmentSettings Settings(float size = 128f, string sky = "day", float hour = 12f) => new EnvironmentSettings
        {
            GroundSize = size,
            Sky = sky,
            TimeOfDay = hour,
            BoundsMin = new Vector3(-50, 0, -50),
            BoundsMax = new Vector3(50, 100, 50),
        };

        [Test]
        public void GroundHasColliderOfTheRightSizeAtZero()
        {
            env.Apply(Settings(128f));
            var ground = env.Ground;
            Assert.That(ground, Is.Not.Null);
            var col = ground.GetComponent<BoxCollider>();
            Assert.That(col, Is.Not.Null);
            Assert.That(col.isTrigger, Is.False);
            Physics.SyncTransforms();
            var b = col.bounds;
            Assert.That(b.size.x, Is.EqualTo(128f).Within(1e-3f));
            Assert.That(b.size.z, Is.EqualTo(128f).Within(1e-3f));
            Assert.That(b.max.y, Is.EqualTo(0f).Within(1e-4f), "top of the ground is y = 0");
            Assert.That(Physics.Raycast(new Vector3(60, 5, -60), Vector3.down, out var hit, 10f), Is.True);
            Assert.That(hit.point.y, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ground.GetComponent<MeshRenderer>().sharedMaterial.shader.name, Is.EqualTo(WorldEnvironment.GroundShaderName), "procedural grid");
        }

        [Test]
        public void ApplyIsIdempotent()
        {
            env.Apply(Settings(128f));
            var first = env.Root;
            var ground = env.Ground;
            env.Apply(Settings(128f));
            env.Apply(Settings(300f, "overcast", 17f));
            Assert.That(env.Root, Is.SameAs(first));
            Assert.That(env.Ground, Is.SameAs(ground));
            Assert.That(root.transform.childCount, Is.EqualTo(1));
            int grounds = 0, lights = 0, bounds = 0;
            foreach (Transform t in first.transform)
            {
                if (t.name == "Ground") grounds++;
                if (t.GetComponent<Light>() != null) lights++;
                if (t.name == "Bounds") bounds++;
            }
            Assert.That(grounds, Is.EqualTo(1));
            Assert.That(lights, Is.EqualTo(1));
            Assert.That(bounds, Is.EqualTo(1));
            Physics.SyncTransforms();
            Assert.That(ground.GetComponent<BoxCollider>().bounds.size.x, Is.EqualTo(300f).Within(1e-3f));
            Assert.That(env.Current.Sky, Is.EqualTo("overcast"));
        }

        [Test]
        public void GroundPresetsSwapTheGroundMaterial()
        {
            env.Apply(Settings());
            var renderer = env.Ground.GetComponent<MeshRenderer>();
            var grid = Resources.Load<Material>(WorldEnvironment.GroundMaterialPath);
            Assert.That(env.Current.Ground, Is.EqualTo("grid"), "empty means the grid");
            Assert.That(renderer.sharedMaterial, Is.SameAs(grid));
            foreach (string preset in WorldEnvironment.GroundPresets)
            {
                var s = Settings();
                s.Ground = " " + preset.ToUpperInvariant() + " ";
                env.Apply(s);
                Assert.That(env.Current.Ground, Is.EqualTo(preset));
                var expected = Resources.Load<Material>(WorldEnvironment.GroundMaterialPathFor(preset));
                Assert.That(expected, Is.Not.Null, preset + " has a material asset under Resources");
                Assert.That(renderer.sharedMaterial, Is.SameAs(expected), preset);
                Assert.That(renderer.sharedMaterial.shader.name, Is.EqualTo(WorldEnvironment.GroundShaderName), preset);
            }
            Assert.That(WorldEnvironment.GroundMaterialPathFor("water"), Is.EqualTo("Tinyverse/World/Materials/GroundWater"));
            var lava = Settings();
            lava.Ground = "lava";
            env.Apply(lava);
            Assert.That(env.Current.Ground, Is.EqualTo("grid"), "unknown presets fall back to the grid");
            Assert.That(renderer.sharedMaterial, Is.SameAs(grid));
            Physics.SyncTransforms();
            Assert.That(env.Ground.GetComponent<BoxCollider>().bounds.max.y, Is.EqualTo(0f).Within(1e-4f), "still a plane at y = 0");
        }

        [Test]
        public void UnknownSkyFallsBackToDay()
        {
            env.Apply(Settings(sky: "lava-planet"));
            Assert.That(env.Current.Sky, Is.EqualTo("day"));
            env.Apply(Settings(sky: null));
            Assert.That(env.Current.Sky, Is.EqualTo("day"));
            env.Apply(Settings(sky: " Overcast "));
            Assert.That(env.Current.Sky, Is.EqualTo("overcast"));
        }

        [Test]
        public void DefaultsFillIn()
        {
            env.Apply(new EnvironmentSettings { TimeOfDay = 30f });
            Assert.That(env.Current.GroundSize, Is.EqualTo(WorldEnvironment.DefaultGroundSize));
            Assert.That(env.Current.TimeOfDay, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(env.BoundsMarkers, Is.Null, "no bounds, no markers");
        }

        [Test]
        public void SunFollowsTimeOfDay()
        {
            env.Apply(Settings(hour: 12f));
            var sun = env.Sun;
            Assert.That(RenderSettings.sun, Is.SameAs(sun));
            Assert.That(sun.type, Is.EqualTo(LightType.Directional));
            Assert.That(sun.transform.forward.y, Is.LessThan(-0.8f), "noon sun shines down");
            float noon = sun.intensity;

            env.Apply(Settings(hour: 7f));
            Assert.That(sun.transform.forward.x, Is.LessThan(0f), "morning sun comes from the east (+X)");
            Assert.That(sun.intensity, Is.LessThan(noon));

            env.Apply(Settings(hour: 17f));
            Assert.That(sun.transform.forward.x, Is.GreaterThan(0f), "evening sun comes from the west");

            env.Apply(Settings(hour: 0f));
            Assert.That(sun.intensity, Is.LessThan(noon * 0.2f), "night is dark");
            Assert.That(sun.transform.forward.y, Is.LessThan(0f), "moonlight still comes from above");

            Assert.That(WorldEnvironment.SunDirection(12f).y, Is.GreaterThan(0.8f));
            Assert.That(WorldEnvironment.SunDirection(0f).y, Is.LessThan(-0.8f));
            Assert.That(WorldEnvironment.SunDirection(6f).y, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void SetsSkyAmbientAndFog()
        {
            env.Apply(Settings());
            Assert.That(RenderSettings.skybox, Is.Not.Null);
            Assert.That(RenderSettings.skybox.shader.name, Is.EqualTo("Skybox/Procedural"));
            Assert.That(RenderSettings.skybox, Is.Not.SameAs(Resources.Load<Material>(WorldEnvironment.SkyMaterialPath)), "the asset is never modified");
            Assert.That(RenderSettings.fog, Is.True);
            // The shaders compute exponential-squared fog themselves (no FOG_* keywords).
            Assert.That(RenderSettings.fogMode, Is.EqualTo(FogMode.ExponentialSquared));
            Assert.That(RenderSettings.fogDensity, Is.EqualTo(WorldEnvironment.FogDensityFactor / 128f).Within(1e-6f));
            // Haze matches the bright sky horizon by day, so the ground edge does not read as a line.
            Assert.That(RenderSettings.fogColor.grayscale, Is.GreaterThan(0.75f));
            Assert.That(RenderSettings.ambientMode, Is.EqualTo(AmbientMode.Trilight));
            Color day = RenderSettings.ambientSkyColor;
            env.Apply(Settings(hour: 0f));
            Assert.That(RenderSettings.ambientSkyColor.grayscale, Is.LessThan(day.grayscale));
        }

        [Test]
        public void ClearRemovesEverythingAndRestoresRenderSettings()
        {
            bool fog = RenderSettings.fog;
            var skybox = RenderSettings.skybox;
            env.Apply(Settings());
            var created = env.Root;
            env.Clear();
            Assert.That(created == null, Is.True);
            Assert.That(env.Root, Is.Null);
            Assert.That(root.transform.childCount, Is.EqualTo(0));
            Assert.That(RenderSettings.fog, Is.EqualTo(fog));
            Assert.That(RenderSettings.skybox, Is.SameAs(skybox));
            env.Clear(); // twice is fine
            env.Apply(Settings());
            Assert.That(env.Ground, Is.Not.Null, "can be applied again after Clear");
        }
    }
}
