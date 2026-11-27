using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Building
{
    /// <summary>Gizmo meshes generated in code, in units of the gizmo size. Created once and shared.</summary>
    static class GizmoMeshes
    {
        static Mesh arrow, ring, scaleHandle, cube;
        static readonly Mesh[] planes = new Mesh[3];

        /// <summary>A shaft and cone head along +Y.</summary>
        public static Mesh Arrow
        {
            get
            {
                if (arrow != null) return arrow;
                var b = new Builder();
                float headStart = GizmoGeometry.ArrowLength - GizmoGeometry.ArrowHeadLength;
                b.Cylinder(GizmoGeometry.ShaftStart, headStart, GizmoGeometry.ShaftRadius, 12);
                b.Cone(headStart, GizmoGeometry.ArrowLength, GizmoGeometry.ArrowHeadRadius, 16);
                return arrow = b.ToMesh("GizmoArrow");
            }
        }

        /// <summary>A shaft along +Y ending in a cube.</summary>
        public static Mesh ScaleHandle
        {
            get
            {
                if (scaleHandle != null) return scaleHandle;
                var b = new Builder();
                float h = GizmoGeometry.ScaleCubeHalf;
                b.Cylinder(GizmoGeometry.ShaftStart, GizmoGeometry.ScaleHandleDistance - h, GizmoGeometry.ShaftRadius, 12);
                b.Box(new Vector3(0, GizmoGeometry.ScaleHandleDistance, 0), new Vector3(h, h, h));
                return scaleHandle = b.ToMesh("GizmoScaleHandle");
            }
        }

        /// <summary>A cube at the origin, for the uniform-scale handle.</summary>
        public static Mesh Cube
        {
            get
            {
                if (cube != null) return cube;
                var b = new Builder();
                float h = GizmoGeometry.UniformCubeHalf;
                b.Box(Vector3.zero, new Vector3(h, h, h));
                return cube = b.ToMesh("GizmoCube");
            }
        }

        /// <summary>A torus around +Y.</summary>
        public static Mesh Ring
        {
            get
            {
                if (ring != null) return ring;
                var b = new Builder();
                b.Torus(GizmoGeometry.RingRadius, GizmoGeometry.RingTube, 96, 6);
                return ring = b.ToMesh("GizmoRing");
            }
        }

        /// <summary>The square handle for the plane whose normal is axis <paramref name="normalAxis"/>.</summary>
        public static Mesh Plane(int normalAxis)
        {
            if (planes[normalAxis] != null) return planes[normalAxis];
            var n = BuildMath.Axis(normalAxis);
            var u = BuildMath.Axis((normalAxis + 1) % 3);
            var v = BuildMath.Axis((normalAxis + 2) % 3);
            float lo = GizmoGeometry.PlaneHandleMin, hi = GizmoGeometry.PlaneHandleMax;
            var b = new Builder();
            b.Quad(u * lo + v * lo, u * hi + v * lo, u * hi + v * hi, u * lo + v * hi, n);
            return planes[normalAxis] = b.ToMesh("GizmoPlane" + normalAxis);
        }

        sealed class Builder
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<int> t = new List<int>();

            int Vertex(Vector3 p, Vector3 normal)
            {
                v.Add(p);
                n.Add(normal);
                return v.Count - 1;
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                int i0 = Vertex(a, normal), i1 = Vertex(b, normal), i2 = Vertex(c, normal), i3 = Vertex(d, normal);
                t.Add(i0); t.Add(i1); t.Add(i2);
                t.Add(i0); t.Add(i2); t.Add(i3);
            }

            public void Box(Vector3 c, Vector3 h)
            {
                for (int i = 0; i < 3; i++)
                {
                    var a = BuildMath.Axis(i);
                    var u = BuildMath.Axis((i + 1) % 3);
                    var w = BuildMath.Axis((i + 2) % 3);
                    var ha = Vector3.Scale(a, h); var hu = Vector3.Scale(u, h); var hw = Vector3.Scale(w, h);
                    Quad(c + ha - hu - hw, c + ha + hu - hw, c + ha + hu + hw, c + ha - hu + hw, a);
                    Quad(c - ha - hu + hw, c - ha + hu + hw, c - ha + hu - hw, c - ha - hu - hw, -a);
                }
            }

            public void Cylinder(float y0, float y1, float r, int seg)
            {
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                    var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                    int p0 = Vertex(d0 * r + Vector3.up * y0, d0), p1 = Vertex(d1 * r + Vector3.up * y0, d1);
                    int p2 = Vertex(d1 * r + Vector3.up * y1, d1), p3 = Vertex(d0 * r + Vector3.up * y1, d0);
                    t.Add(p0); t.Add(p2); t.Add(p1);
                    t.Add(p0); t.Add(p3); t.Add(p2);
                }
            }

            public void Cone(float y0, float y1, float r, int seg)
            {
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                    var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                    var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                    var mid = (d0 + d1).normalized;
                    var sideN = (mid * (y1 - y0) + Vector3.up * r).normalized;
                    int b0 = Vertex(d0 * r + Vector3.up * y0, sideN), b1 = Vertex(d1 * r + Vector3.up * y0, sideN);
                    int apex = Vertex(Vector3.up * y1, sideN);
                    t.Add(b0); t.Add(apex); t.Add(b1);
                    int c = Vertex(Vector3.up * y0, Vector3.down);
                    int c0 = Vertex(d0 * r + Vector3.up * y0, Vector3.down), c1 = Vertex(d1 * r + Vector3.up * y0, Vector3.down);
                    t.Add(c); t.Add(c0); t.Add(c1);
                }
            }

            public void Torus(float radius, float tube, int seg, int sides)
            {
                int start = v.Count;
                for (int i = 0; i <= seg; i++)
                {
                    float a = i * Mathf.PI * 2f / seg;
                    var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    for (int j = 0; j <= sides; j++)
                    {
                        float b = j * Mathf.PI * 2f / sides;
                        var nrm = dir * Mathf.Cos(b) + Vector3.up * Mathf.Sin(b);
                        Vertex(dir * radius + nrm * tube, nrm);
                    }
                }
                int row = sides + 1;
                for (int i = 0; i < seg; i++)
                {
                    for (int j = 0; j < sides; j++)
                    {
                        int a = start + i * row + j, b = a + row, c = b + 1, d = a + 1;
                        t.Add(a); t.Add(d); t.Add(c);
                        t.Add(a); t.Add(c); t.Add(b);
                    }
                }
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
