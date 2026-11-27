using Tinyverse.World;
using UnityEngine;

namespace Tinyverse.Building
{
    /// <summary>Pure geometry for the build tools. No scene access, so it is all EditMode-testable.</summary>
    public static class BuildMath
    {
        /// <summary>
        /// Below this sine of the angle between a ray and an axis (or a plane), a drag along it is treated as
        /// degenerate and ignored for the frame instead of producing a huge jump. 0.02 is about 1.1 degrees.
        /// </summary>
        public const float ParallelEpsilon = 0.02f;

        /// <summary>Furthest a drag may reach along the ray, in metres.</summary>
        public const float MaxRayDistance = 5000f;

        /// <summary>Gap left between a placed prim and the surface under it, against z-fighting.</summary>
        public const float RestGap = 0.001f;

        // ------------------------------------------------------------------ rays

        /// <summary>Intersects a ray with a plane. False if the ray is (nearly) parallel, the hit is behind the
        /// ray origin or further than <paramref name="maxDistance"/>.</summary>
        public static bool RayPlane(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 hit,
            float maxDistance = MaxRayDistance)
        {
            hit = default;
            var n = planeNormal.normalized;
            var d = ray.direction.normalized;
            float denom = Vector3.Dot(d, n);
            if (Mathf.Abs(denom) < ParallelEpsilon) return false;
            float t = Vector3.Dot(planePoint - ray.origin, n) / denom;
            if (t < 0f || t > maxDistance || float.IsNaN(t)) return false;
            hit = ray.origin + d * t;
            return true;
        }

        /// <summary>
        /// The coordinate along an axis line (through <paramref name="origin"/>) of the point on the line closest
        /// to the ray. False when the ray is nearly parallel to the axis or the closest point is behind the
        /// camera or out of reach; the caller then keeps the previous value.
        /// </summary>
        public static bool AxisParameter(Ray ray, Vector3 origin, Vector3 axis, out float t,
            float maxDistance = MaxRayDistance)
        {
            t = 0f;
            var a = axis.normalized;
            var d = ray.direction.normalized;
            // The plane containing the axis that faces the ray most squarely.
            var perp = d - a * Vector3.Dot(d, a);
            float sin = perp.magnitude;
            if (sin < ParallelEpsilon) return false;
            var n = perp / sin;
            float rayT = Vector3.Dot(origin - ray.origin, n) / Vector3.Dot(d, n);
            if (rayT < 0f || rayT > maxDistance || float.IsNaN(rayT)) return false;
            var hit = ray.origin + d * rayT;
            t = Vector3.Dot(hit - origin, a);
            return !float.IsNaN(t) && !float.IsInfinity(t);
        }

        /// <summary>
        /// The in-plane direction from a ring's centre to the point under the ray, for rotation drags. Uses the
        /// ray/plane hit when the plane faces the camera well enough, else the point on the ray closest to the
        /// centre projected into the plane (works edge-on). False if degenerate.
        /// </summary>
        public static bool RingVector(Ray ray, Vector3 center, Vector3 axis, out Vector3 v)
        {
            var a = axis.normalized;
            var d = ray.direction.normalized;
            Vector3 p;
            if (Mathf.Abs(Vector3.Dot(d, a)) >= 0.15f && RayPlane(ray, center, a, out var hit))
                p = hit;
            else
                p = ray.origin + d * Mathf.Max(0f, Vector3.Dot(center - ray.origin, d));
            v = p - center;
            v -= a * Vector3.Dot(v, a);
            return v.sqrMagnitude > 1e-10f;
        }

        /// <summary>Signed angle in degrees from <paramref name="from"/> to <paramref name="to"/> about <paramref name="axis"/>
        /// (positive is counter-clockwise looking down the axis towards the origin, matching Quaternion.AngleAxis).</summary>
        public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis) => Vector3.SignedAngle(from, to, axis);

        /// <summary>Shortest distance between a ray (t ≥ 0) and a segment; <paramref name="rayT"/> is the distance along the ray.</summary>
        public static float RaySegmentDistance(Ray ray, Vector3 a, Vector3 b, out float rayT) =>
            RaySegmentDistance(ray, a, b, out rayT, out _);

        /// <summary>As above, also giving the closest point on the segment.</summary>
        public static float RaySegmentDistance(Ray ray, Vector3 a, Vector3 b, out float rayT, out Vector3 segmentPoint)
        {
            var d1 = ray.direction.normalized;
            var d2 = b - a;
            var r = ray.origin - a;
            float aa = 1f, e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (e < 1e-12f)
            {
                s = Mathf.Max(0f, -Vector3.Dot(d1, r));
                rayT = s;
                segmentPoint = a;
                return (ray.origin + d1 * s - a).magnitude;
            }
            float c = Vector3.Dot(d1, r);
            float bb = Vector3.Dot(d1, d2);
            float denom = aa * e - bb * bb;
            s = denom > 1e-10f ? Mathf.Max(0f, (bb * f - c * e) / denom) : 0f;
            t = Mathf.Clamp01((bb * s + f) / e);
            // Recompute s for the clamped t, then t again for the clamped s.
            s = Mathf.Max(0f, (bb * t - c) / aa);
            t = Mathf.Clamp01((bb * s + f) / e);
            rayT = s;
            segmentPoint = a + d2 * t;
            return (ray.origin + d1 * s - segmentPoint).magnitude;
        }

        /// <summary>Distance from a point to a ray (t ≥ 0).</summary>
        public static float RayPointDistance(Ray ray, Vector3 p, out float rayT)
        {
            var d = ray.direction.normalized;
            rayT = Mathf.Max(0f, Vector3.Dot(p - ray.origin, d));
            return (ray.origin + d * rayT - p).magnitude;
        }

        /// <summary>World size that covers <paramref name="screenFraction"/> of the screen height at the point's depth.</summary>
        public static float ScreenConstantSize(bool orthographic, float fieldOfView, float orthographicSize,
            Vector3 cameraPosition, Vector3 cameraForward, Vector3 point, float screenFraction)
        {
            if (orthographic) return Mathf.Max(1e-3f, orthographicSize * 2f * screenFraction);
            float depth = Mathf.Max(0.05f, Vector3.Dot(point - cameraPosition, cameraForward.normalized));
            return depth * 2f * Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad) * screenFraction;
        }

        // ------------------------------------------------------------------ snapping and limits

        /// <summary>Rounds to the nearest multiple of <paramref name="step"/>; unchanged when step ≤ 0.</summary>
        public static float Snap(float value, float step)
        {
            if (!(step > 0f)) return value;
            return Mathf.Round(value / step) * step;
        }

        public static Vector3 Snap(Vector3 v, float step) => new Vector3(Snap(v.x, step), Snap(v.y, step), Snap(v.z, step));

        public static bool InBounds(Vector3 p, Vector3 min, Vector3 max) =>
            p.x >= min.x && p.y >= min.y && p.z >= min.z && p.x <= max.x && p.y <= max.y && p.z <= max.z;

        public static Vector3 ClampToBounds(Vector3 p, Vector3 min, Vector3 max) =>
            new Vector3(Mathf.Clamp(p.x, min.x, max.x), Mathf.Clamp(p.y, min.y, max.y), Mathf.Clamp(p.z, min.z, max.z));

        public static bool ScaleInLimits(Vector3 s, float min, float max) =>
            s.x >= min && s.y >= min && s.z >= min && s.x <= max && s.y <= max && s.z <= max;

        public static Vector3 ClampScale(Vector3 s, float min, float max) =>
            new Vector3(Mathf.Clamp(s.x, min, max), Mathf.Clamp(s.y, min, max), Mathf.Clamp(s.z, min, max));

        /// <summary>One scale component after a drag: snapped (never below one step) and clamped to the limits.</summary>
        public static float ScaleComponent(float value, float snapStep, float min, float max)
        {
            if (snapStep > 0f) value = Mathf.Max(snapStep, Snap(value, snapStep));
            return Mathf.Clamp(value, min, max);
        }

        /// <summary>
        /// Uniform scale by <paramref name="factor"/>, keeping proportions. With snapping, the largest axis lands
        /// on a multiple of the step. The factor is limited so that every axis stays within the limits.
        /// </summary>
        public static Vector3 ScaleUniform(Vector3 start, float factor, float snapStep, float min, float max)
        {
            float largest = Mathf.Max(start.x, Mathf.Max(start.y, start.z));
            float smallest = Mathf.Min(start.x, Mathf.Min(start.y, start.z));
            if (!(largest > 0f) || !(smallest > 0f)) return ClampScale(start, min, max);
            if (float.IsNaN(factor) || factor < 0f) factor = 0f;
            if (snapStep > 0f)
                factor = Mathf.Max(snapStep, Snap(largest * factor, snapStep)) / largest;
            float lo = min / smallest, hi = max / largest;
            if (lo <= hi) factor = Mathf.Clamp(factor, lo, hi);
            return ClampScale(start * factor, min, max);
        }

        /// <summary>The rotation delta snapped to <paramref name="step"/> degrees (unchanged when step ≤ 0).</summary>
        public static float SnapAngle(float degrees, float step) => Snap(degrees, step);

        // ------------------------------------------------------------------ shapes

        /// <summary>
        /// Support function of a prim: the largest extent of the shape from its centre along world direction
        /// <paramref name="dir"/> (unit), for the given scale and rotation. Exact for the shapes' true geometry.
        /// </summary>
        public static float Support(PrimShape shape, Vector3 scale, Quaternion rotation, Vector3 dir)
        {
            if (shape == PrimShape.Plane) scale.y = 0f;
            // Into unit-shape space: h(d) = h_unit(S * R^-1 * d).
            var local = Quaternion.Inverse(Normalised(rotation)) * dir;
            var v = Vector3.Scale(local, scale);
            float horiz = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            switch (shape)
            {
                case PrimShape.Sphere:
                    return 0.5f * v.magnitude;
                case PrimShape.Cylinder:
                    return 0.5f * Mathf.Abs(v.y) + 0.5f * horiz;
                case PrimShape.Cone:
                    return Mathf.Max(0.5f * v.y, -0.5f * v.y + 0.5f * horiz);
                case PrimShape.Wedge:
                {
                    // Bottom face at y = -0.5, full height at -Z: top edge at y = +0.5, z = -0.5.
                    float bottom = -0.5f * v.y + 0.5f * Mathf.Abs(v.x) + 0.5f * Mathf.Abs(v.z);
                    float top = 0.5f * v.y + 0.5f * Mathf.Abs(v.x) - 0.5f * v.z;
                    return Mathf.Max(bottom, top);
                }
                case PrimShape.Plane:
                    return 0.5f * (Mathf.Abs(v.x) + Mathf.Abs(v.z));
                default:
                    return 0.5f * (Mathf.Abs(v.x) + Mathf.Abs(v.y) + Mathf.Abs(v.z));
            }
        }

        /// <summary>
        /// Where to put a prim's centre so it rests on a surface (hit point and normal) without sinking into it.
        /// With snapping, the two world coordinates across the surface's dominant axis are snapped to the grid
        /// and the third is solved so the prim still touches the (planar) surface.
        /// </summary>
        public static Vector3 RestingCentre(PrimShape shape, Vector3 scale, Quaternion rotation, Vector3 hitPoint,
            Vector3 normal, float snapStep)
        {
            var n = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;
            float lift = Support(shape, scale, rotation, -n) + RestGap;
            var c = hitPoint + n * lift;
            if (!(snapStep > 0f)) return c;

            int dom = DominantAxis(n);
            var planePoint = c; // the centre must stay on the plane through c with normal n
            for (int i = 0; i < 3; i++)
                if (i != dom) c[i] = Snap(c[i], snapStep);
            // Solve dot(c - planePoint, n) = 0 for the dominant coordinate.
            float rest = 0f;
            for (int i = 0; i < 3; i++)
                if (i != dom) rest += (c[i] - planePoint[i]) * n[i];
            c[dom] = planePoint[dom] - rest / n[dom];
            return c;
        }

        public static int DominantAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            if (y >= x && y >= z) return 1;
            return x >= z ? 0 : 2;
        }

        /// <summary>The transform scale a prim is shown with: a plane ignores Y.</summary>
        public static Vector3 VisibleScale(PrimShape shape, Vector3 scale) =>
            shape == PrimShape.Plane ? new Vector3(scale.x, 1f, scale.z) : scale;

        public static Quaternion Normalised(Quaternion q)
        {
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (len < 1e-6f || float.IsNaN(len)) return Quaternion.identity;
            return new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
        }

        public static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

        // ------------------------------------------------------------------ comparisons

        const float PositionTolerance = 1e-3f;
        const float ScaleTolerance = 1e-3f;
        const float AngleTolerance = 0.05f;
        const float ColorTolerance = 1f / 512f;

        /// <summary>Same position, rotation and scale within float round-trip tolerance.</summary>
        public static bool SameTransform(in PrimData a, in PrimData b) =>
            (a.Position - b.Position).sqrMagnitude <= PositionTolerance * PositionTolerance
            && (a.Scale - b.Scale).sqrMagnitude <= ScaleTolerance * ScaleTolerance
            && AngleBetween(a.Rotation, b.Rotation) <= AngleTolerance;

        /// <summary>Same user-visible state (everything except id and owner), within tolerance.</summary>
        public static bool SameState(in PrimData a, in PrimData b) =>
            a.Shape == b.Shape && a.Material == b.Material && a.Solid == b.Solid && a.Locked == b.Locked
            && (a.Name ?? "") == (b.Name ?? "")
            && Mathf.Abs(a.Color.r - b.Color.r) <= ColorTolerance && Mathf.Abs(a.Color.g - b.Color.g) <= ColorTolerance
            && Mathf.Abs(a.Color.b - b.Color.b) <= ColorTolerance && Mathf.Abs(a.Color.a - b.Color.a) <= ColorTolerance
            && SameTransform(a, b);

        /// <summary>Angle between two rotations in degrees, robust for nearly equal and unnormalised quaternions.</summary>
        public static float AngleBetween(Quaternion a, Quaternion b)
        {
            a = Normalised(a);
            b = Normalised(b);
            float dot = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return dot >= 1f ? 0f : 2f * Mathf.Acos(dot) * Mathf.Rad2Deg;
        }
    }
}
