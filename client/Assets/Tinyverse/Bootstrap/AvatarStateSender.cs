using Tinyverse.Avatars;
using UnityEngine;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// Decides when to report the own avatar (SPEC 5.2): at <see cref="MovingRateHz"/> while the pose
    /// is changing, at <see cref="IdleRateHz"/> while it is not, and at once on the frame movement
    /// stops so others see the stop without waiting for the idle report. Pure; time is passed in.
    /// </summary>
    public sealed class AvatarStateSender
    {
        public const float MovingRateHz = 15f;
        public const float IdleRateHz = 1f;

        double lastSent = double.NegativeInfinity;
        bool lastMoving;
        bool hasLast;
        AvatarPose last;

        /// <summary>Forget everything (after a teleport or a new session): the next call sends.</summary>
        public void Reset()
        {
            lastSent = double.NegativeInfinity;
            hasLast = false;
            lastMoving = false;
        }

        /// <summary>True if <paramref name="pose"/> should be sent now; records it as sent if so.</summary>
        public bool ShouldSend(AvatarPose pose, double nowSeconds)
        {
            bool moving = hasLast && Changed(last, pose);
            double since = nowSeconds - lastSent;
            bool send;
            if (!hasLast) send = true;
            else if (moving) send = since >= 1.0 / MovingRateHz - 1e-6;
            else if (lastMoving) send = true; // just stopped: report the stop now
            else send = since >= 1.0 / IdleRateHz - 1e-6;

            if (send)
            {
                lastSent = nowSeconds;
                last = pose;
                hasLast = true;
                lastMoving = moving;
            }
            return send;
        }

        /// <summary>Whether the pose differs from the last one sent by more than noise.</summary>
        public static bool Changed(in AvatarPose a, in AvatarPose b) =>
            (a.Position - b.Position).sqrMagnitude > 1e-6f ||
            Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)) > 0.1f ||
            (a.Velocity - b.Velocity).sqrMagnitude > 1e-4f ||
            a.Mode != b.Mode ||
            (a.Animation ?? "") != (b.Animation ?? "");
    }

    /// <summary>
    /// <c>--demo-walk</c>: drives the local avatar along a small figure — walking, running, jumps, flights,
    /// a drop from the air and a landing — so automated runs and screenshots show movement.
    /// </summary>
    public sealed class DemoWalk
    {
        public readonly ScriptedAvatarInput Input = new ScriptedAvatarInput();
        readonly bool swim;
        float t;
        int phase = -1;

        /// <param name="swim">
        /// <c>--demo-swim</c> instead: from a shore facing -Z (the island template's spawn), stand, walk into the
        /// sea and swim, tread water, take off and fly low over the water, fly back and hover over the beach, land.
        /// </param>
        public DemoWalk(bool swim = false) => this.swim = swim;

        /// <summary>Advances the script; call once per frame before the avatar updates.</summary>
        public void Tick(float dt, bool isFlying)
        {
            t += dt;
            if (swim)
            {
                TickSwim(isFlying);
                return;
            }
            // A 30 s cycle: walk a circle, run a circle, jump twice, fly up and along, stop flying in
            // mid-air and drop, fly up again and descend to land.
            float c = t % 30f;
            int p = c < 8f ? 0 : c < 14f ? 1 : c < 17f ? 2 : c < 19f ? 3 : c < 21f ? 4 : c < 23f ? 5 : c < 24.5f ? 6 : 7;
            float turn = t * 0.55f;
            Input.Move = p == 5 ? Vector2.zero : new Vector2(Mathf.Sin(turn), Mathf.Cos(turn));
            Input.Run = p == 1 || p == 4;
            Input.Up = p == 3 || p == 6;
            Input.Down = p == 7; // descending onto the ground ends the flight
            if (p != phase)
            {
                if (p == 2) Input.Jump();
                if ((p == 3 || p == 6) && !isFlying) Input.ToggleFly();
                if (p == 5 && isFlying) Input.ToggleFly(); // the drop: gravity takes over
                phase = p;
            }
            if (p == 2 && Mathf.Abs(c - 15.5f) < dt) Input.Jump();
        }

        void TickSwim(bool isFlying)
        {
            // A 30 s cycle (seconds): 0 stand, 3 walk out to sea (away from the island's jetty, which runs out
            // along -Z from the spawn point), 8 tread water, 12 swim along the shore, 15 take off and hover at
            // the surface, 17 fly low along the shore, 18.5 fly back to the beach, 21.5 hover over the sand,
            // 26 descend and land.
            float c = t % 30f;
            int p = c < 3f ? 0 : c < 8f ? 1 : c < 12f ? 2 : c < 15f ? 3 : c < 17f ? 4 : c < 18.5f ? 5 : c < 21.5f ? 6 : c < 26f ? 7 : 8;
            Input.Move = p == 1 ? new Vector2(-0.6f, -0.8f) : p == 3 || p == 5 ? Vector2.left : p == 6 ? new Vector2(0.8f, 0.6f) : Vector2.zero;
            Input.Run = false;
            Input.Up = false;
            Input.Down = p == 8;
            if (p != phase)
            {
                if (p == 4 && !isFlying) Input.ToggleFly();
                if (p < 4 && isFlying) Input.ToggleFly();
                phase = p;
            }
        }
    }
}
