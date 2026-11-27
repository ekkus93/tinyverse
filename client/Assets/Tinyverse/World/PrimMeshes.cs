using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tinyverse.World
{
    /// <summary>
    /// Shared meshes for the prim shapes, generated in code. Each fits a 1 m cube
    /// centred on the origin. Meshes are created on first use and never destroyed;
    /// callers must not modify them.
    /// </summary>
    public static class PrimMeshes
    {
        /// <summary>Segments around the axis of the round render meshes.</summary>
        public const int RenderSegments = 32;

        /// <summary>Segments around the axis of the low-poly convex collision meshes.</summary>
        public const int CollisionSegments = 16;

        static readonly Mesh[] render = new Mesh[6];
        static readonly Mesh[] collision = new Mesh[6];

        /// <summary>The shared render mesh for a shape.</summary>
        public static Mesh Get(PrimShape shape)
        {
            int i = (int)shape;
            if (render[i] == null)
            {
                render[i] = Build(shape, RenderSegments);
                render[i].name = "Prim" + shape;
            }
            return render[i];
        }

        /// <summary>
        /// A low-poly mesh for a convex <see cref="MeshCollider"/> (well under PhysX's 255-polygon
        /// limit). Same bounds as <see cref="Get"/>. Box and plane return the render mesh (their
        /// colliders are boxes). The convex hull is cooked once here, so each MeshCollider that
        /// uses the mesh reuses it instead of cooking its own.
        /// </summary>
        public static Mesh GetCollision(PrimShape shape)
        {
            if (shape == PrimShape.Box || shape == PrimShape.Plane)
                return Get(shape);
            int i = (int)shape;
            if (collision[i] == null)
            {
                var mesh = Build(shape, CollisionSegments);
                mesh.name = "PrimCollision" + shape;
                Physics.BakeMesh(mesh.GetEntityId(), true);
                collision[i] = mesh;
            }
            return collision[i];
        }

        static Mesh Build(PrimShape shape, int segments)
        {
            var b = new Builder();
            switch (shape)
            {
                case PrimShape.Box: BuildBox(b); break;
                case PrimShape.Sphere: BuildSphere(b, segments, Mathf.Max(4, segments / 2)); break;
                case PrimShape.Cylinder: BuildCylinder(b, segments); break;
                case PrimShape.Cone: BuildCone(b, segments); break;
                case PrimShape.Wedge: BuildWedge(b); break;
                case PrimShape.Plane: BuildPlane(b); break;
            }
            return b.ToMesh();
        }

        // Adds a planar quad a-b-c-d (in order around its edge) facing `normal`.
        static void Quad(Builder b, Vector3 a, Vector3 c1, Vector3 c2, Vector3 d, Vector3 normal)
        {
            int i0 = b.Vertex(a, normal, new Vector2(0, 0));
            int i1 = b.Vertex(c1, normal, new Vector2(1, 0));
            int i2 = b.Vertex(c2, normal, new Vector2(1, 1));
            int i3 = b.Vertex(d, normal, new Vector2(0, 1));
            b.Triangle(i0, i1, i2);
            b.Triangle(i0, i2, i3);
        }

        static void BuildBox(Builder b)
        {
            const float h = 0.5f;
            // +X, -X, +Y, -Y, +Z, -Z
            Quad(b, new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(h, h, -h), Vector3.right);
            Quad(b, new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(-h, h, h), Vector3.left);
            Quad(b, new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(-h, h, h), Vector3.up);
            Quad(b, new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(-h, -h, -h), Vector3.down);
            Quad(b, new Vector3(h, -h, h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(h, h, h), Vector3.forward);
            Quad(b, new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h), Vector3.back);
        }

        static void BuildSphere(Builder b, int lon, int lat)
        {
            const float r = 0.5f;
            int[,] idx = new int[lat + 1, lon + 1];
            for (int y = 0; y <= lat; y++)
            {
                float v = (float)y / lat;
                float theta = v * Mathf.PI; // 0 at the north pole (+Y)
                for (int x = 0; x <= lon; x++)
                {
                    float u = (float)x / lon;
                    float phi = u * 2f * Mathf.PI;
                    // At the poles, place each vertex's UV in the middle of its segment.
                    if (y == 0 || y == lat) u = Mathf.Min(x + 0.5f, lon - 0.5f) / lon;
                    var n = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                    if (y == 0) n = Vector3.up;
                    if (y == lat) n = Vector3.down;
                    idx[y, x] = b.Vertex(n * r, n, new Vector2(u, 1f - v));
                }
            }
            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = idx[y, x], c = idx[y, x + 1], d = idx[y + 1, x], e = idx[y + 1, x + 1];
                    // The pole rows collapse to one triangle per segment (no degenerate triangles).
                    if (y != 0) b.Triangle(a, c, e);
                    if (y != lat - 1) b.Triangle(a, e, d);
                }
            }
        }

        static void BuildCylinder(Builder b, int seg)
        {
            const float r = 0.5f, h = 0.5f;
            var side = new int[seg + 1, 2];
            for (int i = 0; i <= seg; i++)
            {
                float u = (float)i / seg;
                float a = u * 2f * Mathf.PI;
                var n = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                side[i, 0] = b.Vertex(n * r + Vector3.down * h, n, new Vector2(u, 0));
                side[i, 1] = b.Vertex(n * r + Vector3.up * h, n, new Vector2(u, 1));
            }
            for (int i = 0; i < seg; i++)
            {
                b.Triangle(side[i, 0], side[i, 1], side[i + 1, 1]);
                b.Triangle(side[i, 0], side[i + 1, 1], side[i + 1, 0]);
            }
            Cap(b, seg, h, Vector3.up);
            Cap(b, seg, -h, Vector3.down);
        }

        static void Cap(Builder b, int seg, float y, Vector3 normal)
        {
            const float r = 0.5f;
            int centre = b.Vertex(new Vector3(0, y, 0), normal, new Vector2(0.5f, 0.5f));
            var ring = new int[seg];
            for (int i = 0; i < seg; i++)
            {
                float a = (float)i / seg * 2f * Mathf.PI;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                ring[i] = b.Vertex(new Vector3(c * r, y, s * r), normal, new Vector2(0.5f + c * 0.5f, 0.5f + s * 0.5f));
            }
            for (int i = 0; i < seg; i++)
                b.Triangle(centre, ring[i], ring[(i + 1) % seg]);
        }

        static void BuildCone(Builder b, int seg)
        {
            const float r = 0.5f, h = 0.5f;
            // Slope normal: radial component = height (1), vertical = radius (0.5).
            Vector3 SlopeNormal(float a) => new Vector3(Mathf.Cos(a), r, Mathf.Sin(a)).normalized;
            for (int i = 0; i < seg; i++)
            {
                float u0 = (float)i / seg, u1 = (float)(i + 1) / seg;
                float a0 = u0 * 2f * Mathf.PI, a1 = u1 * 2f * Mathf.PI, am = (a0 + a1) * 0.5f;
                int apex = b.Vertex(new Vector3(0, h, 0), SlopeNormal(am), new Vector2((u0 + u1) * 0.5f, 1));
                int v0 = b.Vertex(new Vector3(Mathf.Cos(a0) * r, -h, Mathf.Sin(a0) * r), SlopeNormal(a0), new Vector2(u0, 0));
                int v1 = b.Vertex(new Vector3(Mathf.Cos(a1) * r, -h, Mathf.Sin(a1) * r), SlopeNormal(a1), new Vector2(u1, 0));
                b.Triangle(apex, v0, v1);
            }
            Cap(b, seg, -h, Vector3.down);
        }

        static void BuildWedge(Builder b)
        {
            const float h = 0.5f;
            // Bottom, back (-Z, full height), slope (from top-back edge down to bottom-front edge), two sides.
            Quad(b, new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(-h, -h, -h), Vector3.down);
            Quad(b, new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h), Vector3.back);
            var slope = new Vector3(0, 1, 1).normalized;
            Quad(b, new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, -h, h), new Vector3(-h, -h, h), slope);
            int a = b.Vertex(new Vector3(h, -h, -h), Vector3.right, new Vector2(0, 0));
            int c = b.Vertex(new Vector3(h, -h, h), Vector3.right, new Vector2(1, 0));
            int d = b.Vertex(new Vector3(h, h, -h), Vector3.right, new Vector2(0, 1));
            b.Triangle(a, c, d);
            a = b.Vertex(new Vector3(-h, -h, h), Vector3.left, new Vector2(0, 0));
            c = b.Vertex(new Vector3(-h, -h, -h), Vector3.left, new Vector2(1, 0));
            d = b.Vertex(new Vector3(-h, h, -h), Vector3.left, new Vector2(1, 1));
            b.Triangle(a, c, d);
        }

        static void BuildPlane(Builder b)
        {
            const float h = 0.5f;
            // Two faces with opposite winding, so a shared back-face-culled material shows both sides.
            Quad(b, new Vector3(-h, 0, -h), new Vector3(h, 0, -h), new Vector3(h, 0, h), new Vector3(-h, 0, h), Vector3.up);
            Quad(b, new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h), new Vector3(-h, 0, -h), Vector3.down);
        }

        sealed class Builder
        {
            readonly List<Vector3> positions = new List<Vector3>();
            readonly List<Vector3> normals = new List<Vector3>();
            readonly List<Vector2> uvs = new List<Vector2>();
            readonly List<int> indices = new List<int>();

            public int Vertex(Vector3 p, Vector3 n, Vector2 uv)
            {
                positions.Add(p);
                normals.Add(n.normalized);
                uvs.Add(uv);
                return positions.Count - 1;
            }

            // Adds a triangle, ordering it so its face normal agrees with the vertex normals
            // (Unity's front faces: cross(b - a, c - a) points outward).
            public void Triangle(int a, int b, int c)
            {
                Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                Vector3 n = normals[a] + normals[b] + normals[c];
                indices.Add(a);
                if (Vector3.Dot(face, n) >= 0)
                {
                    indices.Add(b);
                    indices.Add(c);
                }
                else
                {
                    indices.Add(c);
                    indices.Add(b);
                }
            }

            public Mesh ToMesh()
            {
                var mesh = new Mesh();
                mesh.indexFormat = IndexFormat.UInt16;
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(indices, 0, true);
                mesh.RecalculateTangents();
                mesh.UploadMeshData(false); // keep readable: mesh colliders and tests read it
                return mesh;
            }
        }
    }
}
