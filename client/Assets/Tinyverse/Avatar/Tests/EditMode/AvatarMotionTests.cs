using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    public class AvatarMotionTests
    {
        private static readonly MoveLimits Limits = MoveLimits.Default;

        private static void AssertVec(Vector3 actual, Vector3 expected, float tol = 1e-4f)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tol), $"{actual} != {expected}");
        }

        [Test]
        public void ForwardFollowsViewYaw()
        {
            AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(0, 1), 0f), Vector3.forward);
            AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(0, 1), 90f), Vector3.right);
            AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(1, 0), 0f), Vector3.right);
            AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(1, 0), 90f), Vector3.back);
            AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(0, -1), 180f), Vector3.forward);
        }

        [Test]
        public void ViewRelativeMatchesUnityRotation()
        {
            for (float yaw = -180f; yaw < 360f; yaw += 37f)
            {
                var expected = Quaternion.Euler(0f, yaw, 0f) * new Vector3(0.6f, 0f, -0.8f);
                AssertVec(AvatarMotion.ViewRelativeDirection(new Vector2(0.6f, -0.8f), yaw), expected);
            }
        }

        [Test]
        public void DiagonalIsNotFaster()
        {
            var v = AvatarMotion.GroundVelocity(new Vector2(1, 1), 30f, false, Limits);
            Assert.That(v.magnitude, Is.EqualTo(Limits.WalkSpeed).Within(1e-4f));
            v = AvatarMotion.GroundVelocity(new Vector2(1, 1), 30f, true, Limits);
            Assert.That(v.magnitude, Is.EqualTo(Limits.RunSpeed).Within(1e-4f));
            v = AvatarMotion.GroundVelocity(new Vector2(5, -7), 0f, true, Limits);
            Assert.That(v.magnitude, Is.LessThanOrEqualTo(Limits.RunSpeed + 1e-4f));
        }

        [Test]
        public void ModeSpeeds()
        {
            Assert.That(AvatarMotion.GroundVelocity(Vector2.up, 0f, false, Limits).magnitude, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(AvatarMotion.GroundVelocity(Vector2.up, 0f, true, Limits).magnitude, Is.EqualTo(8f).Within(1e-4f));
            Assert.That(AvatarMotion.FlyVelocity(Vector2.up, 0f, 0f, true, Limits).magnitude, Is.EqualTo(16f).Within(1e-4f));
            Assert.That(AvatarMotion.FlyVelocity(Vector2.up, 0f, 0f, false, Limits, 0.5f).magnitude, Is.EqualTo(8f).Within(1e-4f));
            Assert.That(AvatarMotion.GroundVelocity(Vector2.zero, 0f, true, Limits), Is.EqualTo(Vector3.zero));
            Assert.That(Limits.SpeedFor(MoveKind.Run), Is.EqualTo(8f));
        }

        [Test]
        public void FlyingDiagonallyUpIsNotFaster()
        {
            var v = AvatarMotion.FlyVelocity(new Vector2(1, 1), 0f, 1f, true, Limits);
            Assert.That(v.magnitude, Is.EqualTo(16f).Within(1e-3f));
            Assert.That(v.y, Is.GreaterThan(0f));
            var up = AvatarMotion.FlyVelocity(Vector2.zero, 0f, 1f, true, Limits);
            AssertVec(up, new Vector3(0, 16, 0));
            var down = AvatarMotion.FlyVelocity(Vector2.zero, 0f, -3f, false, Limits);
            AssertVec(down, new Vector3(0, -8, 0));
        }

        [Test]
        public void AccelerationNeverOvershoots()
        {
            var v = Vector3.zero;
            var target = new Vector3(4f, 0f, 0f);
            for (int i = 0; i < 100; i++)
            {
                v = AvatarMotion.Accelerate(v, target, 30f, 40f, 0.016f);
                Assert.That(v.magnitude, Is.LessThanOrEqualTo(4f + 1e-4f));
            }
            AssertVec(v, target);
            // Turning 90 degrees stays within the limit at every step.
            var turned = new Vector3(0f, 0f, 4f);
            for (int i = 0; i < 100; i++)
            {
                v = AvatarMotion.Accelerate(v, turned, 30f, 40f, 0.016f);
                Assert.That(v.magnitude, Is.LessThanOrEqualTo(4f + 1e-4f));
            }
            AssertVec(v, turned);
        }

        [Test]
        public void BoundsClamping()
        {
            var l = Limits;
            l.BoundsMin = new Vector3(-10, 0, -20);
            l.BoundsMax = new Vector3(10, 50, 20);
            AssertVec(AvatarMotion.ClampToBounds(new Vector3(5, 5, 5), l), new Vector3(5, 5, 5));
            AssertVec(AvatarMotion.ClampToBounds(new Vector3(15, -3, -25), l), new Vector3(10, 0, -20));
            AssertVec(AvatarMotion.ClampToBounds(new Vector3(-11, 60, 21), l), new Vector3(-10, 50, 20));
            Assert.That(AvatarMotion.InBounds(new Vector3(10, 0, 20), l), Is.True);
            Assert.That(AvatarMotion.InBounds(new Vector3(10.01f, 0, 20), l), Is.False);
            // Swapped min/max still clamps sensibly.
            var swapped = l;
            swapped.BoundsMin = l.BoundsMax;
            swapped.BoundsMax = l.BoundsMin;
            AssertVec(AvatarMotion.ClampToBounds(new Vector3(15, -3, -25), swapped), new Vector3(10, 0, -20));
        }

        [Test]
        public void YawConventions()
        {
            Assert.That(AvatarMotion.YawOf(Vector3.forward), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(AvatarMotion.YawOf(Vector3.right), Is.EqualTo(90f).Within(1e-4f));
            Assert.That(AvatarMotion.YawOf(Vector3.left), Is.EqualTo(270f).Within(1e-4f));
            Assert.That(AvatarMotion.NormalizeYaw(-90f), Is.EqualTo(270f).Within(1e-4f));
            Assert.That(AvatarMotion.NormalizeYaw(720f), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void ReportedModeIsTheTrueMode()
        {
            Assert.That(AvatarMotion.ClassifyMode(3.9f, false, false, Limits), Is.EqualTo(MoveKind.Walk));
            Assert.That(AvatarMotion.ClassifyMode(4f, false, false, Limits), Is.EqualTo(MoveKind.Walk), "walking (or a walking jump or fall)");
            Assert.That(AvatarMotion.ClassifyMode(0f, false, false, Limits), Is.EqualTo(MoveKind.Walk), "falling straight down");
            Assert.That(AvatarMotion.ClassifyMode(0f, false, true, Limits), Is.EqualTo(MoveKind.Run));
            Assert.That(AvatarMotion.ClassifyMode(7.5f, false, false, Limits), Is.EqualTo(MoveKind.Run), "airborne from a running jump, Shift released");
            Assert.That(AvatarMotion.ClassifyMode(0f, true, false, Limits), Is.EqualTo(MoveKind.Fly));
            Assert.That(AvatarMotion.ClassifyMode(0f, true, true, Limits), Is.EqualTo(MoveKind.Fly));
        }

        [Test]
        public void AnimationHints()
        {
            Assert.That(AvatarMotion.AnimationFor(false, true, 0f, Limits), Is.EqualTo("idle"));
            Assert.That(AvatarMotion.AnimationFor(false, true, 4f, Limits), Is.EqualTo("walk"));
            Assert.That(AvatarMotion.AnimationFor(false, true, 8f, Limits), Is.EqualTo("run"));
            Assert.That(AvatarMotion.AnimationFor(false, false, 4f, Limits), Is.EqualTo("fall"));
            Assert.That(AvatarMotion.AnimationFor(true, false, 0f, Limits), Is.EqualTo("fly"));
        }

        [Test]
        public void NameTagScaleIsCappedNearAndGrowsFar()
        {
            const float fov = 60f, min = 0.017f, max = 0.028f, maxScale = 4f;
            float tan = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            foreach (float depth in new[] { 0.5f, 1f, 2f, 5f, 10f, 20f, 40f, 100f })
            {
                float scale = NameTag.ScaleFor(depth, fov, min, max, maxScale);
                float fraction = NameTag.BaseLineHeight * scale / (2f * depth * tan);
                Assert.That(fraction, Is.LessThanOrEqualTo(max + 1e-5f), $"depth {depth}");
                Assert.That(scale, Is.LessThanOrEqualTo(maxScale + 1e-5f));
                if (scale < maxScale - 1e-3f) Assert.That(fraction, Is.GreaterThanOrEqualTo(min - 1e-5f), $"depth {depth}");
            }
            Assert.That(NameTag.ScaleFor(1f, fov, min, max, maxScale), Is.LessThan(0.5f), "shrinks when near");
            Assert.That(NameTag.ScaleFor(30f, fov, min, max, maxScale), Is.GreaterThan(2f), "grows when far");
        }

        [Test]
        public void NameTagFadesNearAndFar()
        {
            Assert.That(NameTag.AlphaFor(0.5f, 1f, 2f, 35f, 50f), Is.EqualTo(0f));
            Assert.That(NameTag.AlphaFor(1f, 1f, 2f, 35f, 50f), Is.EqualTo(0f));
            Assert.That(NameTag.AlphaFor(1.5f, 1f, 2f, 35f, 50f), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(NameTag.AlphaFor(10f, 1f, 2f, 35f, 50f), Is.EqualTo(1f));
            Assert.That(NameTag.AlphaFor(42.5f, 1f, 2f, 35f, 50f), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(NameTag.AlphaFor(60f, 1f, 2f, 35f, 50f), Is.EqualTo(0f));
        }
    }
}
