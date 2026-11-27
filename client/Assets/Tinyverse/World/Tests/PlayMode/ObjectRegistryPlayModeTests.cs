using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace Tinyverse.World.Tests
{
    public class ObjectRegistryPlayModeTests
    {
        GameObject root;
        ObjectRegistry reg;
        WorldEnvironment env;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("PlayModeWorld");
            reg = new ObjectRegistry(root.transform);
        }

        [TearDown]
        public void TearDown()
        {
            reg.Clear();
            env?.Clear();
            env = null;
            Object.Destroy(root);
        }

        static PrimData Prim(ulong id, Vector3 pos, PrimShape shape = PrimShape.Box) => new PrimData
        {
            Id = id,
            Shape = shape,
            Material = (PrimMaterial)(id % 5),
            Position = pos,
            Rotation = Quaternion.Euler(0, id % 360, 0),
            Scale = new Vector3(1, 0.5f + id % 3, 1),
            Color = new Color((id % 7) / 7f, (id % 11) / 11f, (id % 13) / 13f, 1),
            Solid = id % 4 != 0,
        };

        [UnityTest]
        public IEnumerator SmoothingReachesTargetThenStops()
        {
            reg.Upsert(Prim(1, Vector3.zero));
            var tf = reg.GetGameObject(1).transform;
            var target = Prim(1, new Vector3(10, 2, -4));
            target.Rotation = Quaternion.Euler(0, 90, 0);
            target.Scale = new Vector3(3, 3, 3);
            reg.Upsert(target, smooth: true);
            Assert.That(reg.SmoothingCount, Is.EqualTo(1));

            bool sawIntermediate = false;
            float start = Time.realtimeSinceStartup;
            while (reg.SmoothingCount > 0 && Time.realtimeSinceStartup - start < 2f)
            {
                yield return null;
                reg.Tick(Time.deltaTime);
                if (tf.position.x > 0.01f && tf.position.x < 9.99f) sawIntermediate = true;
            }
            Assert.That(reg.SmoothingCount, Is.EqualTo(0), "smoothing finished");
            Assert.That(Time.realtimeSinceStartup - start, Is.LessThan(1f));
            Assert.That(tf.position, Is.EqualTo(target.Position));
            Assert.That(tf.localScale, Is.EqualTo(target.Scale));
            Assert.That(Quaternion.Angle(tf.rotation, target.Rotation), Is.LessThan(0.01f));
            if (!sawIntermediate) Debug.Log("[WorldPerf] frames were too long to observe an intermediate smoothing step");

            // Once finished, Tick no longer touches the transform.
            tf.position = new Vector3(-1, -1, -1);
            for (int i = 0; i < 5; i++)
            {
                yield return null;
                reg.Tick(Time.deltaTime);
            }
            Assert.That(tf.position, Is.EqualTo(new Vector3(-1, -1, -1)));
        }

        [UnityTest]
        public IEnumerator TenThousandPrims()
        {
            const int n = 10000;
            env = new WorldEnvironment(root.transform);
            env.Apply(new EnvironmentSettings { GroundSize = 512, TimeOfDay = 14, BoundsMin = new Vector3(-256, 0, -256), BoundsMax = new Vector3(256, 128, 256) });

            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
                positions[i] = new Vector3((i % 100) * 2.5f - 125f, 1f + (i / 5000), (i / 100 % 50) * 2.5f - 62f);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                reg.Upsert(Prim((ulong)i + 1, positions[i], (PrimShape)(i % 6)));
            sw.Stop();
            double createMs = sw.Elapsed.TotalMilliseconds;
            Assert.That(reg.Count, Is.EqualTo(n));
            yield return null;

            // Colour-only change on every prim: no GameObject rebuild.
            var someGo = reg.GetGameObject(1234);
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                var p = Prim((ulong)i + 1, positions[i], (PrimShape)(i % 6));
                p.Color = Color.white;
                reg.Upsert(p);
            }
            sw.Stop();
            double recolourMs = sw.Elapsed.TotalMilliseconds;
            Assert.That(reg.GetGameObject(1234), Is.SameAs(someGo));

            // Tick with nothing smoothing, measured over many frames.
            const int frames = 120;
            double idleTickMs = 0, frameMs = 0;
            for (int f = 0; f < frames; f++)
            {
                yield return null;
                frameMs += Time.unscaledDeltaTime * 1000.0;
                sw.Restart();
                reg.Tick(Time.deltaTime);
                sw.Stop();
                idleTickMs += sw.Elapsed.TotalMilliseconds;
            }
            idleTickMs /= frames;
            frameMs /= frames;

            // 1,000 prims smoothing at once (a large remote edit burst).
            for (int i = 0; i < 1000; i++)
            {
                var p = Prim((ulong)i + 1, positions[i] + Vector3.up * 3, (PrimShape)(i % 6));
                p.Color = Color.white;
                reg.Upsert(p, smooth: true);
            }
            Assert.That(reg.SmoothingCount, Is.EqualTo(1000));
            double busyTickMs = 0;
            int busyFrames = 0;
            while (reg.SmoothingCount > 0 && busyFrames < 1000)
            {
                yield return null;
                sw.Restart();
                reg.Tick(Mathf.Min(Time.deltaTime, 0.02f));
                sw.Stop();
                busyTickMs += sw.Elapsed.TotalMilliseconds;
                busyFrames++;
            }
            busyTickMs /= Mathf.Max(1, busyFrames);
            Assert.That(reg.SmoothingCount, Is.EqualTo(0));

            sw.Restart();
            reg.Clear();
            sw.Stop();
            double clearMs = sw.Elapsed.TotalMilliseconds;

            string report = $"[WorldPerf] {n} prims: create {createMs:F0} ms ({createMs * 1000 / n:F1} us each), " +
                            $"recolour all {recolourMs:F1} ms, idle Tick {idleTickMs * 1000:F2} us/frame, " +
                            $"Tick with 1000 smoothing {busyTickMs:F3} ms/frame over {busyFrames} frames, clear {clearMs:F0} ms, " +
                            $"headless frame time {frameMs:F2} ms (no GPU; not a rendering measurement)";
            Debug.Log(report);
            TestContext.WriteLine(report);

            Assert.That(createMs, Is.LessThan(15000), "creation of 10,000 prims");
            Assert.That(recolourMs, Is.LessThan(2000), "colour-only Upsert of 10,000 prims");
            Assert.That(idleTickMs, Is.LessThan(0.05), "Tick must cost nothing with no smoothing");
            Assert.That(busyTickMs, Is.LessThan(5), "Tick with 1000 smoothing prims");
        }
    }
}
