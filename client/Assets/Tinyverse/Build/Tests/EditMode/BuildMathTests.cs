using NUnit.Framework;
using Tinyverse.World;
using UnityEngine;

namespace Tinyverse.Building.Tests
{
    public class BuildMathTests
    {
        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        // ------------------------------------------------------------------ rays

        [Test]
        public void RayPlaneHitsAndRejectsGrazingAndBehind()
        {
            var ray = new Ray(new Vector3(0, 10, -10), new Vector3(0, -1, 1));
            Assert.That(BuildMath.RayPlane(ray, Vector3.zero, Vector3.up, out var hit), Is.True);
            Assert.That((hit - Vector3.zero).magnitude, Is.LessThan(1e-4f));

            var grazing = new Ray(new Vector3(0, 1, 0), new Vector3(1, 0.001f, 0));
            Assert.That(BuildMath.RayPlane(grazing, Vector3.zero, Vector3.up, out _), Is.False);

            var away = new Ray(new Vector3(0, 1, 0), Vector3.up);
            Assert.That(BuildMath.RayPlane(away, Vector3.zero, Vector3.up, out _), Is.False);
        }

        [Test]
        public void AxisParameterRecoversPointFromManyAngles()
        {
            var origin = new Vector3(3, 1, -2);
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward, new Vector3(1, 1, 0).normalized };
            var rng = new System.Random(7);
            int checkedCount = 0;
            foreach (var axis in axes)
            {
                for (int i = 0; i < 200; i++)
                {
                    float p = (float)(rng.NextDouble() * 10 - 5);
                    var target = origin + axis * p;
                    var dir = Random(rng);
                    var cam = target - dir * (2f + (float)rng.NextDouble() * 30f);
                    var ray = new Ray(cam, dir);
                    float sin = Vector3.Cross(dir, axis).magnitude;
                    bool ok = BuildMath.AxisParameter(ray, origin, axis, out float t);
                    if (sin < BuildMath.ParallelEpsilon)
                    {
                        Assert.That(ok, Is.False);
                        continue;
                    }
                    Assert.That(ok, Is.True, $"axis {axis} dir {dir}");
                    Assert.That(Finite(t), Is.True);
                    if (sin < 0.05f) continue;
                    Assert.That(t, Is.EqualTo(p).Within(1e-2f), $"axis {axis} dir {dir}");
                    checkedCount++;
                }
            }
            Assert.That(checkedCount, Is.GreaterThan(600));
        }

        static Vector3 Random(System.Random rng)
        {
            var v = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            return v.sqrMagnitude < 1e-4f ? Vector3.forward : v.normalized;
        }

        [Test]
        public void AxisDragWithCameraAlmostAlongAxisDoesNotExplode()
        {
            var origin = Vector3.zero;
            // Exactly parallel, and a hair off parallel, and just inside the usable range.
            foreach (float tilt in new[] { 0f, 1e-6f, 1e-3f, 0.01f, 0.025f, 0.05f })
            {
                var dir = new Vector3(tilt, 0, 1).normalized;
                var ray = new Ray(new Vector3(0.3f, 0.2f, -20f), dir);
                bool ok = BuildMath.AxisParameter(ray, origin, Vector3.forward, out float t);
                Assert.That(Finite(t), Is.True);
                if (ok) Assert.That(Mathf.Abs(t), Is.LessThan(BuildMath.MaxRayDistance + 50f));
                if (tilt < 0.015f) Assert.That(ok, Is.False, $"tilt {tilt}");
            }
            // Ray pointing away from the axis plane (behind the camera).
            var behind = new Ray(new Vector3(0, 0, -5), new Vector3(0, 1, -1).normalized);
            bool r = BuildMath.AxisParameter(behind, Vector3.zero, Vector3.right, out float tb);
            Assert.That(Finite(tb), Is.True);
            Assert.That(r, Is.False);
        }

        [Test]
        public void PlaneDragAtSteepAndShallowAngles()
        {
            var center = new Vector3(1, 2, 3);
            foreach (var n in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                foreach (var camDir in new[] { new Vector3(0.2f, -1, 0.3f), new Vector3(1, -0.05f, 0.1f), new Vector3(-0.3f, 0.4f, 1f) })
                {
                    var target = center + Vector3.Cross(n, new Vector3(0.3f, 0.7f, 0.2f)) * 2f;
                    var d = camDir.normalized;
                    var ray = new Ray(target - d * 15f, d);
                    bool ok = BuildMath.RayPlane(ray, center, n, out var hit);
                    if (Mathf.Abs(Vector3.Dot(d, n)) < BuildMath.ParallelEpsilon)
                    {
                        Assert.That(ok, Is.False);
                        continue;
                    }
                    Assert.That(ok, Is.True);
                    Assert.That((hit - target).magnitude, Is.LessThan(1e-3f));
                }
            }
        }

        [Test]
        public void RaySegmentDistance()
        {
            var ray = new Ray(new Vector3(0, 0, -5), Vector3.forward);
            float d = BuildMath.RaySegmentDistance(ray, new Vector3(1, 0, 0), new Vector3(1, 0, 3), out float t);
            Assert.That(d, Is.EqualTo(1f).Within(1e-4f));
            d = BuildMath.RaySegmentDistance(ray, new Vector3(-1, 2, 0), new Vector3(1, 2, 0), out t);
            Assert.That(d, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(t, Is.EqualTo(5f).Within(1e-4f));
            // Segment behind the ray origin: distance to the origin.
            d = BuildMath.RaySegmentDistance(ray, new Vector3(0, 1, -10), new Vector3(0, 1, -8), out t);
            Assert.That(t, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(d, Is.EqualTo(new Vector3(0, 1, -3).magnitude).Within(1e-3f));
        }

        // ------------------------------------------------------------------ rotation

        [Test]
        public void RotationAboutEachAxisFollowsTheRing([Values(0, 1, 2)] int axisIndex)
        {
            var axis = BuildMath.Axis(axisIndex);
            var u = BuildMath.Axis((axisIndex + 1) % 3);
            var w = BuildMath.Axis((axisIndex + 2) % 3);
            var center = new Vector3(2, 1, 5);
            // Camera looking down the axis at an angle, so the ring plane faces it.
            var camPos = center + axis * 8f + u * 3f + w * 2f;

            Vector3 Point(float deg) => center + (u * Mathf.Cos(deg * Mathf.Deg2Rad) + w * Mathf.Sin(deg * Mathf.Deg2Rad)) * 1.5f;
            Ray RayTo(Vector3 p) => new Ray(camPos, (p - camPos).normalized);

            Assert.That(BuildMath.RingVector(RayTo(Point(10)), center, axis, out var prev), Is.True);
            var first = prev;
            float total = 0;
            for (float a = 10; a <= 250; a += 15)
            {
                Assert.That(BuildMath.RingVector(RayTo(Point(a)), center, axis, out var v), Is.True);
                total += BuildMath.SignedAngle(prev, v, axis);
                prev = v;
            }
            // Accumulated beyond 180 degrees without wrapping.
            Assert.That(total, Is.EqualTo(240f).Within(0.5f));

            // The rotation built from the angle carries the grabbed point onto the pointer.
            var rot = Quaternion.AngleAxis(total, axis);
            var carried = rot * first;
            Assert.That(Vector3.Angle(carried, prev), Is.LessThan(0.5f));
        }

        [Test]
        public void RingVectorEdgeOnStaysFinite()
        {
            var center = Vector3.zero;
            var ray = new Ray(new Vector3(0.5f, 0, -10), Vector3.forward); // exactly in the Y ring's plane
            Assert.That(BuildMath.RingVector(ray, center, Vector3.up, out var v), Is.True);
            Assert.That(Finite(v.x) && Finite(v.y) && Finite(v.z), Is.True);
            Assert.That(Mathf.Abs(Vector3.Dot(v, Vector3.up)), Is.LessThan(1e-5f));
            // Straight through the centre along the plane: degenerate, reported as such.
            var through = new Ray(new Vector3(0, 0, -10), Vector3.forward);
            BuildMath.RingVector(through, center, Vector3.up, out var v2);
            Assert.That(Finite(v2.x) && Finite(v2.z), Is.True);
        }

        // ------------------------------------------------------------------ snapping and limits

        [Test]
        public void Snapping()
        {
            Assert.That(BuildMath.Snap(1.26f, 0.25f), Is.EqualTo(1.25f).Within(1e-6f));
            Assert.That(BuildMath.Snap(-1.13f, 0.25f), Is.EqualTo(-1.25f).Within(1e-6f));
            Assert.That(BuildMath.Snap(-1.2f, 0.25f), Is.EqualTo(-1.25f).Within(1e-6f));
            Assert.That(BuildMath.Snap(0.7f, 0f), Is.EqualTo(0.7f));
            Assert.That(BuildMath.Snap(0.7f, -1f), Is.EqualTo(0.7f));
            Assert.That(BuildMath.SnapAngle(37f, 15f), Is.EqualTo(30f));
            Assert.That(BuildMath.SnapAngle(-23f, 15f), Is.EqualTo(-30f));
            Assert.That(BuildMath.Snap(new Vector3(0.24f, 0.26f, -0.74f), 0.5f), Is.EqualTo(new Vector3(0, 0.5f, -0.5f)));
        }

        [Test]
        public void BoundsClamping()
        {
            var min = new Vector3(-10, 0, -10);
            var max = new Vector3(10, 50, 10);
            Assert.That(BuildMath.InBounds(new Vector3(0, 1, 0), min, max), Is.True);
            Assert.That(BuildMath.InBounds(new Vector3(0, -0.01f, 0), min, max), Is.False);
            Assert.That(BuildMath.ClampToBounds(new Vector3(20, -5, -11), min, max), Is.EqualTo(new Vector3(10, 0, -10)));
        }

        [Test]
        public void ScaleClamping()
        {
            Assert.That(BuildMath.ScaleComponent(100f, 0.25f, 0.01f, 64f), Is.EqualTo(64f));
            Assert.That(BuildMath.ScaleComponent(-3f, 0f, 0.01f, 64f), Is.EqualTo(0.01f));
            Assert.That(BuildMath.ScaleComponent(0.05f, 0.25f, 0.01f, 64f), Is.EqualTo(0.25f), "never snaps to zero");
            Assert.That(BuildMath.ScaleComponent(1.37f, 0.25f, 0.01f, 64f), Is.EqualTo(1.25f).Within(1e-6f));

            var s = BuildMath.ScaleUniform(new Vector3(1, 2, 4), 2f, 0f, 0.01f, 64f);
            Assert.That(s, Is.EqualTo(new Vector3(2, 4, 8)));
            // Limited by the largest axis: proportions kept.
            s = BuildMath.ScaleUniform(new Vector3(1, 2, 4), 100f, 0f, 0.01f, 64f);
            Assert.That(s.z, Is.EqualTo(64f).Within(1e-4f));
            Assert.That(s.x, Is.EqualTo(16f).Within(1e-3f));
            // Limited by the smallest axis.
            s = BuildMath.ScaleUniform(new Vector3(0.1f, 1, 1), 0.0001f, 0f, 0.01f, 64f);
            Assert.That(s.x, Is.EqualTo(0.01f).Within(1e-5f));
            Assert.That(s.y, Is.EqualTo(0.1f).Within(1e-4f));
            // Snapped: largest axis on the grid.
            s = BuildMath.ScaleUniform(new Vector3(1, 2, 4), 1.3f, 0.5f, 0.01f, 64f);
            Assert.That(s.z, Is.EqualTo(5f).Within(1e-4f));
            Assert.That(s.x, Is.EqualTo(1.25f).Within(1e-4f));
            Assert.That(BuildMath.ScaleInLimits(s, 0.01f, 64f), Is.True);
            Assert.That(BuildMath.ScaleInLimits(new Vector3(1, 65, 1), 0.01f, 64f), Is.False);
        }

        [Test]
        public void ScreenConstantSizeGrowsWithDistance()
        {
            float near = BuildMath.ScreenConstantSize(false, 60f, 5f, Vector3.zero, Vector3.forward, new Vector3(0, 0, 5), 0.1f);
            float far = BuildMath.ScreenConstantSize(false, 60f, 5f, Vector3.zero, Vector3.forward, new Vector3(1, 0, 10), 0.1f);
            Assert.That(far / near, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(near, Is.EqualTo(5f * 2f * Mathf.Tan(30f * Mathf.Deg2Rad) * 0.1f).Within(1e-5f));
            float behind = BuildMath.ScreenConstantSize(false, 60f, 5f, Vector3.zero, Vector3.forward, new Vector3(0, 0, -5), 0.1f);
            Assert.That(behind, Is.GreaterThan(0f));
            Assert.That(BuildMath.ScreenConstantSize(true, 60f, 5f, Vector3.zero, Vector3.forward, new Vector3(0, 0, 50), 0.1f), Is.EqualTo(1f).Within(1e-5f));
        }

        // ------------------------------------------------------------------ resting on surfaces

        static readonly PrimShape[] shapes =
            { PrimShape.Box, PrimShape.Sphere, PrimShape.Cylinder, PrimShape.Cone, PrimShape.Wedge, PrimShape.Plane };

        // Signed distance of the lowest mesh vertex above the surface plane.
        static float Clearance(PrimShape shape, Vector3 scale, Quaternion rot, Vector3 centre, Vector3 point, Vector3 normal)
        {
            var mesh = PrimMeshes.Get(shape);
            var vis = BuildMath.VisibleScale(shape, scale);
            float min = float.PositiveInfinity;
            foreach (var v in mesh.vertices)
            {
                var w = centre + rot * Vector3.Scale(v, vis);
                min = Mathf.Min(min, Vector3.Dot(w - point, normal));
            }
            return min;
        }

        [Test]
        public void RestsOnFlatAndTiltedSurfaces([ValueSource(nameof(shapes))] PrimShape shape)
        {
            var normals = new[]
            {
                Vector3.up,
                Quaternion.Euler(30, 0, 20) * Vector3.up,
                Vector3.right, // a wall
                Quaternion.Euler(0, 40, 70) * Vector3.up,
            };
            var rotations = new[] { Quaternion.identity, Quaternion.Euler(0, 35, 0), Quaternion.Euler(20, 50, 80) };
            var scales = new[] { Vector3.one, new Vector3(2, 0.5f, 3), new Vector3(0.3f, 4, 0.3f) };
            var point = new Vector3(1.3f, 2.2f, -0.7f);
            foreach (var n in normals)
            foreach (var rot in rotations)
            foreach (var scale in scales)
            {
                var c = BuildMath.RestingCentre(shape, scale, rot, point, n, 0f);
                float clearance = Clearance(shape, scale, rot, c, point, n);
                // Round meshes approximate the true surface, so the lowest vertex may sit slightly above it.
                float tolerance = 0.006f * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) + 1e-4f;
                Assert.That(clearance, Is.EqualTo(BuildMath.RestGap).Within(tolerance),
                    $"{shape} n={n} rot={rot.eulerAngles} scale={scale}");
            }
        }

        [Test]
        public void SnappedRestingStaysOnTheSurface([ValueSource(nameof(shapes))] PrimShape shape)
        {
            // Flat ground: x and z on the grid, y from the shape.
            var c = BuildMath.RestingCentre(shape, new Vector3(1, 2, 1), Quaternion.identity, new Vector3(1.3f, 0, -0.6f), Vector3.up, 0.5f);
            Assert.That(c.x, Is.EqualTo(1.5f).Within(1e-5f));
            Assert.That(c.z, Is.EqualTo(-0.5f).Within(1e-5f));
            float expectedY = shape == PrimShape.Plane ? 0f : 1f;
            Assert.That(c.y, Is.EqualTo(expectedY + BuildMath.RestGap).Within(1e-4f));

            // Tilted surface: snapped across, still touching.
            var n = Quaternion.Euler(25, 0, -15) * Vector3.up;
            var p = new Vector3(0.3f, 1f, 0.2f);
            c = BuildMath.RestingCentre(shape, Vector3.one, Quaternion.identity, p, n, 0.5f);
            Assert.That(c.x, Is.EqualTo(BuildMath.Snap(c.x, 0.5f)).Within(1e-5f));
            Assert.That(c.z, Is.EqualTo(BuildMath.Snap(c.z, 0.5f)).Within(1e-5f));
            Assert.That(Clearance(shape, Vector3.one, Quaternion.identity, c, p, n), Is.EqualTo(BuildMath.RestGap).Within(0.01f));

            // A wall: y and z snapped, x off the wall.
            c = BuildMath.RestingCentre(shape, Vector3.one, Quaternion.identity, new Vector3(4f, 1.3f, 0.6f), Vector3.right, 0.5f);
            Assert.That(c.y, Is.EqualTo(1.5f).Within(1e-5f));
            Assert.That(c.z, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(c.x, Is.GreaterThan(4f));
        }

        [Test]
        public void SupportMatchesMeshExtents([ValueSource(nameof(shapes))] PrimShape shape)
        {
            var mesh = PrimMeshes.Get(shape);
            var rng = new System.Random(3);
            var scale = new Vector3(1.5f, 0.7f, 2.2f);
            var rot = Quaternion.Euler(10, 70, -30);
            var vis = BuildMath.VisibleScale(shape, scale);
            for (int i = 0; i < 50; i++)
            {
                var d = Random(rng);
                float meshMax = float.NegativeInfinity;
                foreach (var v in mesh.vertices) meshMax = Mathf.Max(meshMax, Vector3.Dot(rot * Vector3.Scale(v, vis), d));
                float s = BuildMath.Support(shape, scale, rot, d);
                Assert.That(s, Is.GreaterThanOrEqualTo(meshMax - 1e-4f), shape.ToString());
                Assert.That(s - meshMax, Is.LessThan(0.02f), shape.ToString());
            }
        }

        // ------------------------------------------------------------------ comparisons

        [Test]
        public void SameStateToleratesRoundTripNoise()
        {
            var a = new PrimData { Id = 1, Name = null, Position = new Vector3(1, 2, 3), Rotation = Quaternion.Euler(10, 20, 30), Scale = Vector3.one, Color = Color.red };
            var b = a;
            b.Id = 2;
            b.OwnerId = "someone";
            b.Name = "";
            b.Position += new Vector3(1e-5f, 0, 0);
            b.Rotation = new Quaternion(-a.Rotation.x, -a.Rotation.y, -a.Rotation.z, -a.Rotation.w);
            Assert.That(BuildMath.SameState(a, b), Is.True);
            b.Solid = true;
            Assert.That(BuildMath.SameState(a, b), Is.False);
            Assert.That(BuildMath.SameTransform(a, b), Is.True);
            b.Position += Vector3.up * 0.1f;
            Assert.That(BuildMath.SameTransform(a, b), Is.False);
        }
    }
}
