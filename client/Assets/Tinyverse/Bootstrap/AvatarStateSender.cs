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
            bool changed = !hasLast || Changed(last, pose);
            bool moving = changed;
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
    /// <c>--demo-walk</c>: drives the local avatar along a small figure — walking, running, a jump and a
    /// short flight — so automated runs and screenshots show movement.
    /// </summary>
    public sealed class DemoWalk
    {
        public readonly ScriptedAvatarInput Input = new ScriptedAvatarInput();
        float t;
        int phase = -1;

        /// <summary>Advances the script; call once per frame before the avatar updates.</summary>
        public void Tick(float dt, bool isFlying)
        {
            t += dt;
            // A 24 s cycle: walk a circle, run a circle, jump twice, fly up and land.
            float c = t % 24f;
            int p = c < 8f ? 0 : c < 14f ? 1 : c < 17f ? 2 : c < 18.5f ? 3 : 4;
            float turn = t * 0.55f;
            Input.Move = new Vector2(Mathf.Sin(turn), Mathf.Cos(turn));
            Input.Run = p == 1;
            Input.Up = p == 3;
            Input.Down = p == 4; // descending onto the ground ends the flight
            if (p != phase)
            {
                if (p == 2) Input.Jump();
                if (p == 3 && !isFlying) Input.ToggleFly();
                phase = p;
            }
            if (p == 2 && Mathf.Abs(c - 15.5f) < dt) Input.Jump();
        }
    }
}
