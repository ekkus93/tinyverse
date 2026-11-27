using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    public class PoseBufferTests
    {
        private static AvatarPose At(float x, float yaw = 0f, Vector3 velocity = default) =>
            new AvatarPose(new Vector3(x, 0f, 0f), yaw, velocity, MoveKind.Walk, AvatarAnimations.Walk);

        [Test]
        public void EmptyBufferHasNoSample()
        {
            var b = new PoseBuffer();
            Assert.That(b.TrySample(1.0, out _), Is.False);
        }

        [Test]
        public void InterpolatesBetweenSamples()
        {
            var b = new PoseBuffer();
            b.Add(1.0, At(0f));
            b.Add(1.1, At(0.4f));
            Assert.That(b.TrySample(1.025, out var p), Is.True);
            Assert.That(p.Position.x, Is.EqualTo(0.1f).Within(1e-4f));
            b.TrySample(1.1, out p);
            Assert.That(p.Position.x, Is.EqualTo(0.4f).Within(1e-4f));
        }

        [Test]
        public void BeforeFirstSampleHoldsFirst()
        {
            var b = new PoseBuffer();
            b.Add(1.0, At(2f));
            b.Add(1.1, At(3f));
            b.TrySample(0.5, out var p);
            Assert.That(p.Position.x, Is.EqualTo(2f));
        }

        [Test]
        public void YawTakesShortestArcAcross180()
        {
            Assert.That(PoseInterpolation.LerpYaw(170f, -170f, 0.5f), Is.EqualTo(180f).Within(1e-3f));
            Assert.That(PoseInterpolation.LerpYaw(350f, 10f, 0.5f), Is.EqualTo(0f).Within(1e-3f).Or.EqualTo(360f).Within(1e-3f));
            Assert.That(PoseInterpolation.LerpYaw(10f, 350f, 0.25f), Is.EqualTo(5f).Within(1e-3f));
            Assert.That(PoseInterpolation.LerpYaw(-90f, 90f, 0f), Is.EqualTo(270f).Within(1e-3f));

            var b = new PoseBuffer();
            b.Add(0.0, At(0f, 175f));
            b.Add(0.1, At(0f, -175f));
            b.TrySample(0.075, out var p);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(p.Yaw, 182.5f)), Is.LessThan(1e-3f));
            Assert.That(p.Yaw, Is.InRange(0f, 360f));
        }

        [Test]
        public void ExtrapolatesFromVelocityUpToCap()
        {
            var b = new PoseBuffer { MaxExtrapolation = 0.25f };
            b.Add(1.0, At(0f, 0f, new Vector3(4f, 0f, 0f)));
            b.TrySample(1.1, out var p);
            Assert.That(p.Position.x, Is.EqualTo(0.4f).Within(1e-4f));
            b.TrySample(1.25, out p);
            Assert.That(p.Position.x, Is.EqualTo(1.0f).Within(1e-4f));
            b.TrySample(5.0, out p);
            Assert.That(p.Position.x, Is.EqualTo(1.0f).Within(1e-4f), "extrapolation is capped");
        }

        [Test]
        public void TeleportSnapsInsteadOfSliding()
        {
            var b = new PoseBuffer();
            b.Add(1.0, At(0f));
            b.Add(1.05, At(50f));
            b.TrySample(1.025, out var p);
            Assert.That(p.Position.x, Is.EqualTo(0f), "holds the old pose");
            b.TrySample(1.05, out p);
            Assert.That(p.Position.x, Is.EqualTo(50f), "then snaps");
        }

        [Test]
        public void FastFlightAcrossAGapIsNotATeleport()
        {
            // 16 m/s for one second: long, but plausible.
            Assert.That(PoseInterpolation.IsTeleport(At(0f), At(16f), 1.0, 4f, 25f), Is.False);
            Assert.That(PoseInterpolation.IsTeleport(At(0f), At(3f), 0.01, 4f, 25f), Is.False, "short hops always interpolate");
            Assert.That(PoseInterpolation.IsTeleport(At(0f), At(6f), 0.05, 4f, 25f), Is.True);
        }

        [Test]
        public void OutOfOrderSamplesAreSorted()
        {
            var b = new PoseBuffer();
            Assert.That(b.Add(1.2, At(2f)), Is.True);
            Assert.That(b.Add(1.0, At(0f)), Is.True);
            Assert.That(b.Add(1.1, At(1f)), Is.True);
            Assert.That(b.Count, Is.EqualTo(3));
            Assert.That(b[0].Time, Is.EqualTo(1.0));
            Assert.That(b[1].Time, Is.EqualTo(1.1));
            Assert.That(b[2].Time, Is.EqualTo(1.2));
            b.TrySample(1.15, out var p);
            Assert.That(p.Position.x, Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void DuplicatesAreIgnored()
        {
            var b = new PoseBuffer();
            Assert.That(b.Add(1.0, At(0f)), Is.True);
            Assert.That(b.Add(1.0, At(9f)), Is.False);
            Assert.That(b.Count, Is.EqualTo(1));
            b.TrySample(1.0, out var p);
            Assert.That(p.Position.x, Is.EqualTo(0f));
            Assert.That(b.Add(double.NaN, At(0f)), Is.False);
        }

        [Test]
        public void BufferIsBounded()
        {
            var b = new PoseBuffer(8);
            for (int i = 0; i < 100; i++) b.Add(i * 0.05, At(i));
            Assert.That(b.Count, Is.EqualTo(8));
            Assert.That(b.NewestTime, Is.EqualTo(99 * 0.05).Within(1e-9));
            Assert.That(b.Add(0.0, At(0f)), Is.False, "older than everything in a full buffer");
            Assert.That(b.Count, Is.EqualTo(8));
        }

        [Test]
        public void DiscardKeepsTheCurrentSpan()
        {
            var b = new PoseBuffer();
            for (int i = 0; i < 10; i++) b.Add(i * 0.1, At(i));
            b.DiscardBefore(0.55);
            Assert.That(b[0].Time, Is.EqualTo(0.5).Within(1e-9));
            b.TrySample(0.55, out var p);
            Assert.That(p.Position.x, Is.EqualTo(5.5f).Within(1e-3f));
        }

        [Test]
        public void ModeAndAnimationSwitchAtMidpoint()
        {
            var a = new AvatarPose(Vector3.zero, 0f, default, MoveKind.Walk, AvatarAnimations.Walk);
            var c = new AvatarPose(Vector3.one, 0f, default, MoveKind.Fly, AvatarAnimations.Fly);
            Assert.That(PoseInterpolation.Interpolate(a, c, 0.4f).Animation, Is.EqualTo(AvatarAnimations.Walk));
            Assert.That(PoseInterpolation.Interpolate(a, c, 0.6f).Mode, Is.EqualTo(MoveKind.Fly));
        }
    }
}
