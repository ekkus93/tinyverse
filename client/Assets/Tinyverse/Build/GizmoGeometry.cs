using UnityEngine;

namespace Tinyverse.Building
{
    /// <summary>
    /// Shape of the gizmos in units of the gizmo size, and picking against them by pure ray maths (the gizmo has
    /// no colliders, so it never interferes with placement or selection rays).
    /// </summary>
    public static class GizmoGeometry
    {
        public const float ShaftStart = 0.15f;
        public const float ArrowLength = 1f;
        public const float ArrowHeadLength = 0.22f;
        public const float ArrowHeadRadius = 0.07f;
        public const float ShaftRadius = 0.018f;
        public const float AxisPickRadius = 0.09f;

        public const float PlaneHandleMin = 0.25f;
        public const float PlaneHandleMax = 0.5f;

        public const float RingRadius = 1f;
        public const float RingTube = 0.016f;
        public const float RingPickTolerance = 0.08f;
        public const int RingPickSegments = 64;

        public const float ScaleHandleDistance = 0.92f;
        public const float ScaleCubeHalf = 0.07f;
        public const float UniformCubeHalf = 0.1f;
        public const float UniformPickRadius = 0.16f;

        public static int AxisIndex(BuildHandle h)
        {
            switch (h)
            {
                case BuildHandle.MoveX: case BuildHandle.MoveYZ: case BuildHandle.RotateX: case BuildHandle.ScaleX: return 0;
                case BuildHandle.MoveY: case BuildHandle.MoveXZ: case BuildHandle.RotateY: case BuildHandle.ScaleY: return 1;
                case BuildHandle.MoveZ: case BuildHandle.MoveXY: case BuildHandle.RotateZ: case BuildHandle.ScaleZ: return 2;
                default: return -1;
            }
        }

        public static bool IsMoveAxis(BuildHandle h) => h == BuildHandle.MoveX || h == BuildHandle.MoveY || h == BuildHandle.MoveZ;
        public static bool IsMovePlane(BuildHandle h) => h == BuildHandle.MoveXY || h == BuildHandle.MoveYZ || h == BuildHandle.MoveXZ;
        public static bool IsRotate(BuildHandle h) => h == BuildHandle.RotateX || h == BuildHandle.RotateY || h == BuildHandle.RotateZ;
        public static bool IsScaleAxis(BuildHandle h) => h == BuildHandle.ScaleX || h == BuildHandle.ScaleY || h == BuildHandle.ScaleZ;

        static readonly BuildHandle[] moveAxes = { BuildHandle.MoveX, BuildHandle.MoveY, BuildHandle.MoveZ };
        static readonly BuildHandle[] movePlanes = { BuildHandle.MoveYZ, BuildHandle.MoveXZ, BuildHandle.MoveXY };
        static readonly BuildHandle[] rings = { BuildHandle.RotateX, BuildHandle.RotateY, BuildHandle.RotateZ };
        static readonly BuildHandle[] scaleAxes = { BuildHandle.ScaleX, BuildHandle.ScaleY, BuildHandle.ScaleZ };

        /// <summary>
        /// The handle under the ray, nearest the camera first. <paramref name="axes"/> orients the gizmo
        /// (identity for the world-aligned move and rotate gizmos, the prim's rotation for scale).
        /// </summary>
        public static BuildHandle Pick(BuildTool tool, Vector3 center, Quaternion axes, float size, Ray ray,
            bool hideScaleY = false)
        {
            var best = BuildHandle.None;
            float bestT = float.PositiveInfinity;
            void Consider(BuildHandle h, float t)
            {
                if (t < bestT)
                {
                    bestT = t;
                    best = h;
                }
            }

            switch (tool)
            {
                case BuildTool.Move:
                    for (int i = 0; i < 3; i++)
                    {
                        var a = axes * BuildMath.Axis(i);
                        float d = BuildMath.RaySegmentDistance(ray, center + a * (ShaftStart * size), center + a * (ArrowLength * size), out float t);
                        if (d <= AxisPickRadius * size) Consider(moveAxes[i], t);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        var n = axes * BuildMath.Axis(i);
                        var u = axes * BuildMath.Axis((i + 1) % 3);
                        var v = axes * BuildMath.Axis((i + 2) % 3);
                        if (!BuildMath.RayPlane(ray, center, n, out var hit)) continue;
                        var local = (hit - center) / size;
                        float lu = Vector3.Dot(local, u), lv = Vector3.Dot(local, v);
                        if (lu >= PlaneHandleMin && lu <= PlaneHandleMax && lv >= PlaneHandleMin && lv <= PlaneHandleMax)
                            Consider(movePlanes[i], (hit - ray.origin).magnitude);
                    }
                    break;

                case BuildTool.Rotate:
                    for (int i = 0; i < 3; i++)
                    {
                        if (RingDistance(ray, center, axes * BuildMath.Axis(i), RingRadius * size, out float t, out _) <= RingPickTolerance * size)
                            Consider(rings[i], t);
                    }
                    break;

                case BuildTool.Scale:
                    for (int i = 0; i < 3; i++)
                    {
                        if (i == 1 && hideScaleY) continue;
                        var a = axes * BuildMath.Axis(i);
                        float d = BuildMath.RaySegmentDistance(ray, center + a * (ShaftStart * size), center + a * ((ScaleHandleDistance + ScaleCubeHalf) * size), out float t);
                        if (d <= AxisPickRadius * size) Consider(scaleAxes[i], t);
                    }
                    {
                        float d = BuildMath.RayPointDistance(ray, center, out float t);
                        if (d <= UniformPickRadius * size) Consider(BuildHandle.ScaleUniform, t - UniformPickRadius * size);
                    }
                    break;
            }
            return best;
        }

        /// <summary>Distance from a ray to a ring (circle around <paramref name="axis"/>), with the ring point nearest the ray.</summary>
        public static float RingDistance(Ray ray, Vector3 center, Vector3 axis, float radius, out float rayT, out Vector3 ringPoint)
        {
            var a = axis.normalized;
            var u = Vector3.Cross(a, Mathf.Abs(a.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(a, u);
            float best = float.PositiveInfinity;
            rayT = 0f;
            ringPoint = center + u * radius;
            var prev = center + u * radius;
            for (int k = 1; k <= RingPickSegments; k++)
            {
                float ang = k * (Mathf.PI * 2f / RingPickSegments);
                var p = center + (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * radius;
                float d = BuildMath.RaySegmentDistance(ray, prev, p, out float t, out var onSegment);
                if (d < best)
                {
                    best = d;
                    rayT = t;
                    ringPoint = onSegment;
                }
                prev = p;
            }
            // Push the chord point back onto the circle.
            var off = ringPoint - center;
            off -= a * Vector3.Dot(off, a);
            if (off.sqrMagnitude > 1e-12f) ringPoint = center + off.normalized * radius;
            return best;
        }
    }
}
