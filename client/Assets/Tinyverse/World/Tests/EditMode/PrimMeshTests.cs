using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.World.Tests
{
    public class PrimMeshTests
    {
        static readonly PrimShape[] AllShapes = (PrimShape[])System.Enum.GetValues(typeof(PrimShape));

        static void AssertValid(Mesh mesh, PrimShape shape)
        {
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.isReadable, Is.True, "mesh colliders need readable meshes");
            var v = mesh.vertices;
            var n = mesh.normals;
            var uv = mesh.uv;
            var tris = mesh.triangles;
            Assert.That(v.Length, Is.GreaterThan(0));
            Assert.That(n.Length, Is.EqualTo(v.Length), "normals");
            Assert.That(uv.Length, Is.EqualTo(v.Length), "uvs");
            Assert.That(tris.Length % 3, Is.EqualTo(0));

            // Bounds are the unit cube (a plane is flat in Y).
            var b = mesh.bounds;
            Vector3 expected = shape == PrimShape.Plane ? new Vector3(1, 0, 1) : Vector3.one;
            Assert.That(Vector3.Distance(b.size, expected), Is.LessThan(1e-4f), $"{shape} bounds size {b.size}");
            Assert.That(b.center.magnitude, Is.LessThan(1e-4f), $"{shape} bounds centre {b.center}");
            foreach (var p in v)
            {
                Assert.That(Mathf.Abs(p.x) <= 0.5f + 1e-5f && Mathf.Abs(p.y) <= 0.5f + 1e-5f && Mathf.Abs(p.z) <= 0.5f + 1e-5f,
                    $"{shape} vertex {p} outside the unit cube");
            }

            foreach (var normal in n)
                Assert.That(normal.magnitude, Is.EqualTo(1f).Within(1e-4f), $"{shape} normal {normal}");
            foreach (var t in uv)
                Assert.That(t.x >= -1e-5f && t.x <= 1 + 1e-5f && t.y >= -1e-5f && t.y <= 1 + 1e-5f, $"{shape} uv {t}");

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = v[tris[i]], c = v[tris[i + 1]], d = v[tris[i + 2]];
                Vector3 face = Vector3.Cross(c - a, d - a);
                Assert.That(face.magnitude * 0.5f, Is.GreaterThan(1e-6f), $"{shape} degenerate triangle {i / 3}");
                // Winding agrees with the vertex normals (front faces point outwards).
                Vector3 avg = n[tris[i]] + n[tris[i + 1]] + n[tris[i + 2]];
                Assert.That(Vector3.Dot(face.normalized, avg.normalized), Is.GreaterThan(0.2f), $"{shape} triangle {i / 3} winding");
            }
        }

        [Test]
        public void RenderMeshesAreValid([ValueSource(nameof(AllShapes))] PrimShape shape)
        {
            AssertValid(PrimMeshes.Get(shape), shape);
        }

        [Test]
        public void CollisionMeshesAreValidAndConvexFriendly([ValueSource(nameof(AllShapes))] PrimShape shape)
        {
            var mesh = PrimMeshes.GetCollision(shape);
            AssertValid(mesh, shape);
            Assert.That(mesh.triangles.Length / 3, Is.LessThanOrEqualTo(255), "PhysX convex hull polygon limit");
        }

        [Test]
        public void MeshesAreShared()
        {
            foreach (var s in AllShapes)
                Assert.That(PrimMeshes.Get(s), Is.SameAs(PrimMeshes.Get(s)));
            Assert.That(PrimMeshes.Get(PrimShape.Box), Is.Not.SameAs(PrimMeshes.Get(PrimShape.Sphere)));
        }

        [Test]
        public void WedgeIsFullHeightAtBackAndSlopesToFront()
        {
            var mesh = PrimMeshes.Get(PrimShape.Wedge);
            var v = mesh.vertices;
            var n = mesh.normals;
            // Every vertex at the front edge (+Z) lies on the bottom.
            foreach (var p in v.Where(p => p.z > 0.49f))
                Assert.That(p.y, Is.EqualTo(-0.5f).Within(1e-5f), $"front vertex {p}");
            // The top is reached only at the back (-Z).
            Assert.That(v.Any(p => p.y > 0.49f), Is.True);
            foreach (var p in v.Where(p => p.y > 0.49f))
                Assert.That(p.z, Is.EqualTo(-0.5f).Within(1e-5f), $"top vertex {p}");
            // There is a slope facing up and towards +Z.
            var expected = new Vector3(0, 1, 1).normalized;
            Assert.That(n.Any(x => Vector3.Dot(x, expected) > 0.999f), Is.True, "slope normal");
        }

        [Test]
        public void WedgeColliderSlopesTheRightWay()
        {
            var go = new GameObject("wedge");
            try
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.convex = true;
                mc.sharedMesh = PrimMeshes.GetCollision(PrimShape.Wedge);
                Physics.SyncTransforms();
                // Straight down at z = -0.25 (towards the back) hits at y = 0.25; at z = +0.25 at y = -0.25.
                Assert.That(mc.Raycast(new Ray(new Vector3(0, 2, -0.25f), Vector3.down), out var back, 5f), Is.True);
                Assert.That(back.point.y, Is.EqualTo(0.25f).Within(0.01f));
                Assert.That(mc.Raycast(new Ray(new Vector3(0, 2, 0.25f), Vector3.down), out var front, 5f), Is.True);
                Assert.That(front.point.y, Is.EqualTo(-0.25f).Within(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ConeApexIsUpAndBaseIsDown()
        {
            var v = PrimMeshes.Get(PrimShape.Cone).vertices;
            foreach (var p in v.Where(p => p.y > 0.49f))
                Assert.That(new Vector2(p.x, p.z).magnitude, Is.LessThan(1e-4f), $"apex vertex {p}");
            float baseRadius = v.Where(p => p.y < -0.49f).Max(p => new Vector2(p.x, p.z).magnitude);
            Assert.That(baseRadius, Is.EqualTo(0.5f).Within(1e-4f));
            // Side normals point up and out.
            var n = PrimMeshes.Get(PrimShape.Cone).normals;
            Assert.That(n.Count(x => x.y > 0.3f && x.y < 0.6f), Is.GreaterThan(0));
        }

        [Test]
        public void CylinderAxisIsY()
        {
            var v = PrimMeshes.Get(PrimShape.Cylinder).vertices;
            foreach (var p in v)
                Assert.That(new Vector2(p.x, p.z).magnitude, Is.LessThanOrEqualTo(0.5f + 1e-4f));
            Assert.That(v.Count(p => p.y > 0.49f), Is.GreaterThan(PrimMeshes.RenderSegments));
        }

        [Test]
        public void PlaneIsVisibleFromBothSides()
        {
            var n = PrimMeshes.Get(PrimShape.Plane).normals;
            Assert.That(n.Any(x => x == Vector3.up), Is.True);
            Assert.That(n.Any(x => x == Vector3.down), Is.True);
            Assert.That(PrimMeshes.Get(PrimShape.Plane).triangles.Length / 3, Is.EqualTo(4));
        }

        [Test]
        public void SphereIsRound()
        {
            foreach (var p in PrimMeshes.Get(PrimShape.Sphere).vertices)
                Assert.That(p.magnitude, Is.EqualTo(0.5f).Within(1e-4f));
        }
    }
}
