using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>
    /// A bounded, time-ordered buffer of timestamped poses for one remote avatar, and the sampling rule that turns
    /// it into a pose at any render time: interpolate between the two samples around it, extrapolate briefly from
    /// velocity past the newest one, and step instead of sliding across a teleport. Plain C#, no Unity objects.
    /// </summary>
    public sealed class PoseBuffer
    {
        public const int DefaultCapacity = 32;

        /// <summary>Two samples closer in time than this are the same sample.</summary>
        public const double DuplicateEpsilon = 1e-6;

        private readonly List<TimedPose> samples;

        public PoseBuffer(int capacity = DefaultCapacity)
        {
            Capacity = Mathf.Max(2, capacity);
            samples = new List<TimedPose>(Capacity + 1);
        }

        /// <summary>Most samples kept; the oldest is dropped when a new one would exceed it.</summary>
        public int Capacity { get; }

        /// <summary>Longest time past the newest sample that the pose is extrapolated from its velocity, seconds.</summary>
        public float MaxExtrapolation { get; set; } = 0.25f;

        /// <summary>A jump between consecutive samples longer than this may be a teleport, metres.</summary>
        public float TeleportDistance { get; set; } = 4f;

        /// <summary>
        /// A jump longer than <see cref="TeleportDistance"/> is a teleport only if covering it in the time between
        /// the samples would need more than this speed, metres per second. The default is above the fly limit
        /// with the server's tolerance (16 × 1.5), so fast flight across a gap still interpolates.
        /// </summary>
        public float MaxPlausibleSpeed { get; set; } = 25f;

        public int Count => samples.Count;

        public TimedPose this[int index] => samples[index];

        public double OldestTime => samples.Count > 0 ? samples[0].Time : double.NaN;

        public double NewestTime => samples.Count > 0 ? samples[samples.Count - 1].Time : double.NaN;

        public void Clear() => samples.Clear();

        /// <summary>
        /// Inserts a sample in time order. Returns false, and changes nothing, for a non-finite time, a duplicate
        /// timestamp, or a sample older than everything in a full buffer.
        /// </summary>
        public bool Add(double time, AvatarPose pose)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return false;
            int index = samples.Count;
            while (index > 0 && samples[index - 1].Time > time) index--;
            if (index > 0 && time - samples[index - 1].Time < DuplicateEpsilon) return false;
            if (index < samples.Count && samples[index].Time - time < DuplicateEpsilon) return false;
            if (index == 0 && samples.Count >= Capacity) return false;
            samples.Insert(index, new TimedPose(time, pose));
            while (samples.Count > Capacity) samples.RemoveAt(0);
            return true;
        }

        /// <summary>Drops samples that can no longer affect sampling at <paramref name="renderTime"/> or later.</summary>
        public void DiscardBefore(double renderTime)
        {
            // Keep the newest sample at or before renderTime: it is the start of the current interpolation span.
            int keepFrom = 0;
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                if (samples[i].Time <= renderTime)
                {
                    keepFrom = i;
                    break;
                }
            }
            if (keepFrom > 0) samples.RemoveRange(0, keepFrom);
        }

        /// <summary>The pose at <paramref name="renderTime"/>. False only when the buffer is empty.</summary>
        public bool TrySample(double renderTime, out AvatarPose pose)
        {
            int n = samples.Count;
            if (n == 0)
            {
                pose = default;
                return false;
            }

            if (renderTime <= samples[0].Time)
            {
                pose = samples[0].Pose;
                return true;
            }

            var newest = samples[n - 1];
            if (renderTime >= newest.Time)
            {
                pose = PoseInterpolation.Extrapolate(newest.Pose, (float)(renderTime - newest.Time), MaxExtrapolation);
                return true;
            }

            int i = n - 2;
            while (i > 0 && samples[i].Time > renderTime) i--;
            var a = samples[i];
            var b = samples[i + 1];
            double span = b.Time - a.Time;
            if (PoseInterpolation.IsTeleport(a.Pose, b.Pose, span, TeleportDistance, MaxPlausibleSpeed))
            {
                // Hold the old pose until the new one is due, then snap. Never slide across a teleport.
                pose = a.Pose;
                return true;
            }

            float t = span > 0 ? (float)((renderTime - a.Time) / span) : 1f;
            pose = PoseInterpolation.Interpolate(a.Pose, b.Pose, t);
            return true;
        }
    }

    /// <summary>A pose with the server time it describes.</summary>
    public readonly struct TimedPose
    {
        public readonly double Time;
        public readonly AvatarPose Pose;

        public TimedPose(double time, AvatarPose pose)
        {
            Time = time;
            Pose = pose;
        }
    }

    /// <summary>Pure functions used by <see cref="PoseBuffer"/>.</summary>
    public static class PoseInterpolation
    {
        /// <summary>
        /// Blends two poses. Position and velocity linearly, yaw by the shortest arc (result in [0, 360)); mode and
        /// animation are discrete and switch at the midpoint. <paramref name="t"/> is clamped to [0, 1].
        /// </summary>
        public static AvatarPose Interpolate(in AvatarPose a, in AvatarPose b, float t)
        {
            t = Mathf.Clamp01(t);
            var late = t >= 0.5f;
            return new AvatarPose
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Yaw = LerpYaw(a.Yaw, b.Yaw, t),
                Velocity = Vector3.Lerp(a.Velocity, b.Velocity, t),
                Mode = late ? b.Mode : a.Mode,
                Animation = late ? b.Animation : a.Animation,
            };
        }

        /// <summary>Interpolates a heading by the shortest arc; the result is in [0, 360).</summary>
        public static float LerpYaw(float from, float to, float t)
        {
            float delta = Mathf.DeltaAngle(from, to);
            return AvatarMotion.NormalizeYaw(from + delta * Mathf.Clamp01(t));
        }

        /// <summary>Moves a pose forward along its velocity for <paramref name="seconds"/>, at most <paramref name="cap"/>.</summary>
        public static AvatarPose Extrapolate(in AvatarPose pose, float seconds, float cap)
        {
            float s = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, cap));
            var result = pose;
            result.Position = pose.Position + pose.Velocity * s;
            return result;
        }

        /// <summary>
        /// True when going from <paramref name="a"/> to <paramref name="b"/> in <paramref name="seconds"/> is a jump
        /// rather than movement: longer than <paramref name="teleportDistance"/> and faster than
        /// <paramref name="maxPlausibleSpeed"/>.
        /// </summary>
        public static bool IsTeleport(in AvatarPose a, in AvatarPose b, double seconds, float teleportDistance, float maxPlausibleSpeed)
        {
            float distance = Vector3.Distance(a.Position, b.Position);
            if (distance <= teleportDistance) return false;
            return distance > maxPlausibleSpeed * Mathf.Max(0f, (float)seconds);
        }
    }
}
