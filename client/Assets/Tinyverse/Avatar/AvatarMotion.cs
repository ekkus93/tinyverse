using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>Pure movement maths for the local avatar. No state, no Unity objects.</summary>
    public static class AvatarMotion
    {
        /// <summary>
        /// Turns a stick or WASD vector (x = right, y = forward) into a horizontal world direction relative to a
        /// view heading. The result has length at most 1, so diagonals are never faster than straight lines.
        /// </summary>
        public static Vector3 ViewRelativeDirection(Vector2 input, float viewYaw)
        {
            if (float.IsNaN(input.x) || float.IsNaN(input.y)) return Vector3.zero;
            input = Vector2.ClampMagnitude(input, 1f);
            float rad = viewYaw * Mathf.Deg2Rad;
            float sin = Mathf.Sin(rad), cos = Mathf.Cos(rad);
            var forward = new Vector3(sin, 0f, cos);
            var right = new Vector3(cos, 0f, -sin);
            return Vector3.ClampMagnitude(right * input.x + forward * input.y, 1f);
        }

        /// <summary>Target horizontal velocity on the ground: walk speed, or run speed while <paramref name="run"/>.</summary>
        public static Vector3 GroundVelocity(Vector2 input, float viewYaw, bool run, in MoveLimits limits)
        {
            float speed = run ? limits.RunSpeed : limits.WalkSpeed;
            return ViewRelativeDirection(input, viewYaw) * Mathf.Max(0f, speed);
        }

        /// <summary>
        /// Target velocity while flying. <paramref name="vertical"/> is +1 up, −1 down. Full fly speed while
        /// <paramref name="fast"/>, otherwise <paramref name="slowFactor"/> of it. The 3D length never exceeds
        /// the fly limit, so climbing diagonally is not faster than flying level.
        /// </summary>
        public static Vector3 FlyVelocity(Vector2 input, float viewYaw, float vertical, bool fast, in MoveLimits limits, float slowFactor = 0.5f)
        {
            float speed = Mathf.Max(0f, limits.FlySpeed) * (fast ? 1f : Mathf.Clamp01(slowFactor));
            var dir = ViewRelativeDirection(input, viewYaw) + Vector3.up * Mathf.Clamp(vertical, -1f, 1f);
            return Vector3.ClampMagnitude(dir, 1f) * speed;
        }

        /// <summary>
        /// Moves <paramref name="current"/> toward <paramref name="target"/> at a bounded rate: <paramref name="accel"/>
        /// when speeding up, <paramref name="decel"/> when slowing down or turning. Every intermediate value lies on
        /// the segment between the two, so it is never faster than the faster of them.
        /// </summary>
        public static Vector3 Accelerate(Vector3 current, Vector3 target, float accel, float decel, float dt)
        {
            bool speedingUp = target.sqrMagnitude > current.sqrMagnitude && Vector3.Dot(current, target) >= 0f;
            float rate = speedingUp ? accel : decel;
            return Vector3.MoveTowards(current, target, Mathf.Max(0f, rate) * Mathf.Max(0f, dt));
        }

        /// <summary>Clamps a feet position into the world bounds.</summary>
        public static Vector3 ClampToBounds(Vector3 position, in MoveLimits limits)
        {
            return new Vector3(
                ClampAxis(position.x, limits.BoundsMin.x, limits.BoundsMax.x),
                ClampAxis(position.y, limits.BoundsMin.y, limits.BoundsMax.y),
                ClampAxis(position.z, limits.BoundsMin.z, limits.BoundsMax.z));
        }

        /// <summary>True when the position is inside the bounds (inclusive).</summary>
        public static bool InBounds(Vector3 position, in MoveLimits limits) => ClampToBounds(position, limits) == position;

        private static float ClampAxis(float v, float min, float max)
        {
            if (min > max) (min, max) = (max, min);
            if (float.IsNaN(v)) return (min + max) * 0.5f;
            return v < min ? min : v > max ? max : v;
        }

        /// <summary>Heading in degrees [0, 360) of a horizontal direction; 0 = +Z, clockwise seen from above.</summary>
        public static float YawOf(Vector3 direction) => NormalizeYaw(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg);

        /// <summary>Wraps an angle into [0, 360).</summary>
        public static float NormalizeYaw(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0f;
            float y = yaw % 360f;
            if (y < 0f) y += 360f;
            return y >= 360f ? 0f : y;
        }

        /// <summary>
        /// The move mode to report: Fly only while really flying; on foot (including jumps and falls) Run while the
        /// run key is held or the horizontal speed is still above the walk limit (a running jump, slowing down after
        /// releasing Shift), otherwise Walk. The server checks only horizontal speed against Walk and Run.
        /// </summary>
        public static MoveKind ClassifyMode(float horizontalSpeed, bool flying, bool running, in MoveLimits limits)
        {
            const float eps = 0.01f;
            if (flying) return MoveKind.Fly;
            if (running || horizontalSpeed > limits.WalkSpeed + eps) return MoveKind.Run;
            return MoveKind.Walk;
        }

        /// <summary>The animation hint for the local avatar's state.</summary>
        public static string AnimationFor(bool flying, bool grounded, float horizontalSpeed, in MoveLimits limits)
        {
            if (flying) return AvatarAnimations.Fly;
            if (!grounded) return AvatarAnimations.Fall;
            if (horizontalSpeed > limits.WalkSpeed + 0.25f) return AvatarAnimations.Run;
            if (horizontalSpeed > 0.2f) return AvatarAnimations.Walk;
            return AvatarAnimations.Idle;
        }
    }
}
