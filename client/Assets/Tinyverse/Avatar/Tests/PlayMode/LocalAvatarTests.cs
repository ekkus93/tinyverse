using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    public class LocalAvatarTests
    {
        private AvatarTestScene scene;
        private const float SpeedTolerance = 0.02f;

        [SetUp]
        public void SetUp() => scene = new AvatarTestScene();

        [TearDown]
        public void TearDown() => scene.Dispose();

        private static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        /// <summary>Runs frames, checking per-frame speed against <paramref name="limit"/> and the pose against its mode.</summary>
        private static IEnumerator RunChecked(LocalAvatar avatar, int frames, float limit, bool horizontalOnly)
        {
            var limits = avatar.Limits;
            for (int i = 0; i < frames; i++)
            {
                var before = avatar.transform.position;
                yield return null;
                var delta = avatar.transform.position - before;
                if (horizontalOnly) delta.y = 0f;
                Assert.That(delta.magnitude, Is.LessThanOrEqualTo(limit * Time.deltaTime + 1e-4f),
                    $"frame {i}: moved at {delta.magnitude / Time.deltaTime} m/s");
                AssertPoseWithinServerRules(avatar, $"frame {i}");
            }
        }

        /// <summary>
        /// The server's rule: on foot, horizontal speed within the mode's limit and vertical within +10 / −55 here
        /// (the server allows −60); flying, 3D speed within fly speed. Checks the reported pose.
        /// </summary>
        private static void AssertPoseWithinServerRules(LocalAvatar avatar, string where)
        {
            var pose = avatar.CurrentPose;
            var v = pose.Velocity;
            var limits = avatar.Limits;
            Assert.That(pose.Mode == MoveKind.Fly, Is.EqualTo(avatar.IsFlying), $"{where}: Fly reported only while flying");
            if (pose.Mode == MoveKind.Fly)
            {
                Assert.That(v.magnitude, Is.LessThanOrEqualTo(limits.FlySpeed + 1e-3f), $"{where}: {v.magnitude} m/s flying");
                return;
            }
            float h = new Vector2(v.x, v.z).magnitude;
            Assert.That(h, Is.LessThanOrEqualTo(limits.SpeedFor(pose.Mode) + 1e-3f), $"{where}: {h} m/s horizontal in {pose.Mode}");
            Assert.That(v.y, Is.InRange(-55f - 1e-3f, 10f), $"{where}: vertical {v.y} m/s");
        }

        private static IEnumerator Settle(LocalAvatar avatar)
        {
            for (int i = 0; i < 120 && !avatar.IsGrounded; i++) yield return null;
            yield return Frames(5);
            Assert.That(avatar.IsGrounded, Is.True, "avatar should be standing");
        }

        [UnityTest]
        public IEnumerator WalkAndRunCoverExpectedDistanceWithinLimits()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);

            var start = avatar.transform.position;
            input.Move = Vector2.up;
            yield return RunChecked(avatar, 120, avatar.Limits.WalkSpeed, true);
            float walked = Vector3.Distance(start, avatar.transform.position);
            Assert.That(walked, Is.InRange(7.4f, 8.0f), "2 s at 4 m/s minus the acceleration ramp");
            Assert.That(avatar.CurrentPose.Mode, Is.EqualTo(MoveKind.Walk));
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo("walk"));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(avatar.CurrentPose.Yaw, 0f)), Is.LessThan(1f));
            Assert.That(avatar.transform.position.z, Is.GreaterThan(start.z + 7f), "forward is +Z with view yaw 0");

            start = avatar.transform.position;
            input.Run = true;
            yield return RunChecked(avatar, 120, avatar.Limits.RunSpeed, true);
            float ran = Vector3.Distance(start, avatar.transform.position);
            Assert.That(ran, Is.InRange(15.0f, 16.0f), "2 s at 8 m/s minus the ramp from 4 m/s");
            Assert.That(avatar.CurrentPose.Mode, Is.EqualTo(MoveKind.Run));
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo("run"));

            // Diagonal is not faster.
            input.Move = new Vector2(1f, 1f);
            yield return RunChecked(avatar, 60, avatar.Limits.RunSpeed, true);
            Assert.That(avatar.CurrentPose.Velocity.magnitude, Is.EqualTo(8f).Within(0.05f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(avatar.CurrentPose.Yaw, 45f)), Is.LessThan(2f), "turned smoothly to face travel");

            input.Reset();
            yield return Frames(30);
            Assert.That(avatar.CurrentPose.Velocity.magnitude, Is.LessThan(0.01f));
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo("idle"));
        }

        [UnityTest]
        public IEnumerator MovementIsRelativeToViewYaw()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            avatar.ViewYaw = () => 90f;
            yield return Settle(avatar);
            input.Move = Vector2.up;
            yield return Frames(60);
            var p = avatar.transform.position;
            Assert.That(p.x, Is.GreaterThan(3f));
            Assert.That(Mathf.Abs(p.z), Is.LessThan(0.05f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(avatar.CurrentPose.Yaw, 90f)), Is.LessThan(1f), "faces away from the camera");
        }

        [UnityTest]
        public IEnumerator FlyToggleUpAndDownAndLanding()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);

            input.ToggleFly();
            yield return null;
            Assert.That(avatar.IsFlying, Is.True);
            Assert.That(avatar.CurrentPose.Mode, Is.EqualTo(MoveKind.Fly));

            float y0 = avatar.transform.position.y;
            input.Up = true;
            input.Run = true;
            yield return RunChecked(avatar, 60, avatar.Limits.FlySpeed, false);
            float climbed = avatar.transform.position.y - y0;
            Assert.That(climbed, Is.InRange(10f, 16f), "1 s of climbing at up to 16 m/s");
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo("fly"));

            // Diagonal climb is not faster than level flight.
            input.Move = new Vector2(1f, 1f);
            yield return RunChecked(avatar, 60, avatar.Limits.FlySpeed, false);

            // Hover.
            input.Reset();
            yield return Frames(60);
            var hover = avatar.transform.position;
            yield return Frames(30);
            Assert.That(Vector3.Distance(hover, avatar.transform.position), Is.LessThan(0.01f), "hovers without input");
            Assert.That(avatar.IsFlying, Is.True);

            // Descend until landing ends the flight.
            input.Down = true;
            input.Run = true;
            for (int i = 0; i < 600 && avatar.IsFlying; i++)
            {
                var before = avatar.transform.position;
                yield return null;
                Assert.That((avatar.transform.position - before).magnitude / Time.deltaTime, Is.LessThanOrEqualTo(16f + SpeedTolerance));
            }
            Assert.That(avatar.IsFlying, Is.False, "landing while descending ends flight");
            Assert.That(avatar.transform.position.y, Is.EqualTo(0f).Within(0.06f));
            input.Reset();
            yield return Frames(3);
            Assert.That(avatar.IsGrounded, Is.True);

            // Toggle on then off in the air: falls back down.
            input.ToggleFly();
            input.Up = true;
            yield return Frames(30);
            input.Up = false;
            Assert.That(avatar.transform.position.y, Is.GreaterThan(1f));
            input.ToggleFly();
            yield return null;
            Assert.That(avatar.IsFlying, Is.False);
            for (int i = 0; i < 300 && !avatar.IsGrounded; i++) yield return null;
            Assert.That(avatar.IsGrounded, Is.True);
            Assert.That(avatar.transform.position.y, Is.EqualTo(0f).Within(0.06f));
        }

        [UnityTest]
        public IEnumerator GravityLandsOnGroundAndStepsOntoLowBox()
        {
            scene.Ground();
            scene.Box(new Vector3(0f, 0.15f, 7f), new Vector3(4f, 0.3f, 10f), "Step");
            var (avatar, input) = scene.Local(new Vector3(0f, 6f, 0f));
            yield return null;
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo("fall"));
            for (int i = 0; i < 240 && !avatar.IsGrounded; i++)
            {
                yield return null;
                AssertPoseWithinServerRules(avatar, $"frame {i}");
            }
            Assert.That(avatar.IsGrounded, Is.True);
            Assert.That(avatar.transform.position.y, Is.EqualTo(0f).Within(0.06f));

            input.Move = Vector2.up;
            yield return Frames(90);
            var p = avatar.transform.position;
            Assert.That(p.z, Is.GreaterThan(3f), "walked onto the box");
            Assert.That(p.y, Is.EqualTo(0.3f).Within(0.06f), "standing on the 0.3 m box");
            Assert.That(avatar.IsGrounded, Is.True);
        }

        [UnityTest]
        public IEnumerator JumpGoesUpAndComesBack()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Jump();
            float peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                yield return null;
                peak = Mathf.Max(peak, avatar.transform.position.y);
                AssertPoseWithinServerRules(avatar, $"frame {i}");
            }
            Assert.That(peak, Is.InRange(0.6f, 1.2f));
            Assert.That(avatar.IsGrounded, Is.True);
        }

        [UnityTest]
        public IEnumerator WalkingJumpReportsWalkWithTrueVelocity()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Move = Vector2.up;
            yield return Frames(30);
            input.Jump();
            yield return null;
            var pose = avatar.CurrentPose;
            Assert.That(avatar.IsGrounded, Is.False);
            Assert.That(pose.Mode, Is.EqualTo(MoveKind.Walk), "a walking jump is still Walk");
            Assert.That(pose.Animation, Is.EqualTo("fall"));
            Assert.That(pose.Velocity.y, Is.EqualTo(avatar.JumpSpeed).Within(0.5f), "true vertical velocity");
            Assert.That(pose.Velocity.magnitude, Is.GreaterThan(avatar.Limits.WalkSpeed), "3D speed is not clamped to the walk limit");
            for (int i = 0; i < 90; i++)
            {
                var before = avatar.transform.position;
                yield return null;
                AssertPoseWithinServerRules(avatar, $"frame {i}");
                var d = avatar.transform.position - before;
                Assert.That(new Vector2(d.x, d.z).magnitude, Is.LessThanOrEqualTo(avatar.Limits.WalkSpeed * Time.deltaTime + 1e-4f));
                Assert.That(d.y / Time.deltaTime, Is.InRange(-55.01f, 10f));
                if (!avatar.IsGrounded) Assert.That(avatar.CurrentPose.Mode, Is.EqualTo(MoveKind.Walk));
            }
            Assert.That(avatar.IsGrounded, Is.True);
        }

        [UnityTest]
        public IEnumerator RunningJumpReportsRun()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Move = Vector2.up;
            input.Run = true;
            yield return Frames(30);
            input.Jump();
            yield return null;
            input.Run = false; // released mid-air: still moving at run speed, so still Run
            yield return Frames(5);
            Assert.That(avatar.IsGrounded, Is.False);
            Assert.That(avatar.CurrentPose.Mode, Is.EqualTo(MoveKind.Run));
            AssertPoseWithinServerRules(avatar, "mid-air");
        }

        [UnityTest]
        public IEnumerator LongFallReportsWalkAndCapsAt55()
        {
            scene.Ground();
            var (avatar, _) = scene.Local(new Vector3(0f, 300f, 0f));
            float minVy = 0f;
            for (int i = 0; i < 1200 && !avatar.IsGrounded; i++)
            {
                var before = avatar.transform.position;
                yield return null;
                var pose = avatar.CurrentPose;
                minVy = Mathf.Min(minVy, pose.Velocity.y);
                Assert.That(pose.Mode, Is.EqualTo(MoveKind.Walk), $"frame {i}: a fall is Walk");
                if (!avatar.IsGrounded) Assert.That(pose.Animation, Is.EqualTo("fall"));
                AssertPoseWithinServerRules(avatar, $"frame {i}");
                Assert.That((avatar.transform.position.y - before.y) / Time.deltaTime, Is.GreaterThanOrEqualTo(-55.01f));
            }
            Assert.That(minVy, Is.EqualTo(-LocalAvatar.MaxFallSpeed).Within(0.01f), "reached the terminal speed, not clamped lower");
            Assert.That(avatar.IsGrounded, Is.True);
            Assert.That(avatar.transform.position.y, Is.EqualTo(0f).Within(0.06f));
        }

        [UnityTest]
        public IEnumerator TeleportLandsExactlyAndClearsVelocity()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Move = Vector2.up;
            input.Run = true;
            yield return Frames(60);
            Assert.That(avatar.CurrentPose.Velocity.magnitude, Is.GreaterThan(7f));

            AvatarPose? teleported = null;
            avatar.Teleported += p => teleported = p;
            input.Reset();
            var target = new Vector3(12.5f, 7.25f, -3.75f);
            avatar.Teleport(new AvatarPose(target, 123f, new Vector3(9, 9, 9), MoveKind.Fly, "fly"));
            Assert.That(avatar.transform.position, Is.EqualTo(target));
            Assert.That(avatar.CurrentPose.Position, Is.EqualTo(target));
            Assert.That(avatar.CurrentPose.Velocity, Is.EqualTo(Vector3.zero));
            Assert.That(avatar.CurrentPose.Yaw, Is.EqualTo(123f).Within(1e-3f));
            Assert.That(avatar.IsFlying, Is.True);
            Assert.That(teleported.HasValue, Is.True);

            yield return Frames(10);
            Assert.That(Vector3.Distance(avatar.transform.position, target), Is.LessThan(1e-4f), "no leftover velocity");

            // A ground teleport also clears velocity and the avatar settles where it was put.
            var ground = new Vector3(-20f, 0f, 30f);
            input.Move = Vector2.up;
            input.Run = true;
            avatar.Teleport(new AvatarPose(ground, 0f));
            input.Reset();
            Assert.That(avatar.transform.position, Is.EqualTo(ground));
            Assert.That(avatar.IsFlying, Is.False);
            yield return Frames(10);
            Assert.That(Vector3.Distance(avatar.transform.position, ground), Is.LessThan(0.06f));
        }

        [UnityTest]
        public IEnumerator InputDisabledStopsMovement()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Move = Vector2.up;
            yield return Frames(30);
            avatar.InputEnabled = false;
            input.ToggleFly();
            input.Jump();
            yield return Frames(20);
            var p = avatar.transform.position;
            yield return Frames(60);
            Assert.That(Vector3.Distance(p, avatar.transform.position), Is.LessThan(0.005f));
            Assert.That(avatar.IsFlying, Is.False, "fly toggle ignored while disabled");
            avatar.InputEnabled = true;
            yield return Frames(30);
            Assert.That(Vector3.Distance(p, avatar.transform.position), Is.GreaterThan(0.5f), "moves again when re-enabled");
        }

        [UnityTest]
        public IEnumerator StaysInsideBounds()
        {
            scene.Ground();
            var limits = MoveLimits.Default;
            limits.BoundsMin = new Vector3(-2f, 0f, -2f);
            limits.BoundsMax = new Vector3(2f, 5f, 2f);
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f), 0f, limits);
            yield return Settle(avatar);
            input.Move = new Vector2(1f, 1f);
            input.Run = true;
            yield return Frames(120);
            var p = avatar.transform.position;
            Assert.That(p.x, Is.LessThanOrEqualTo(2f));
            Assert.That(p.z, Is.LessThanOrEqualTo(2f));
            Assert.That(p.x, Is.GreaterThan(1.9f));

            input.ToggleFly();
            input.Up = true;
            yield return Frames(120);
            Assert.That(avatar.transform.position.y, Is.LessThanOrEqualTo(5f));
            Assert.That(avatar.transform.position.y, Is.GreaterThan(4.9f));

            avatar.Teleport(new AvatarPose(new Vector3(50f, 50f, -50f), 0f));
            Assert.That(avatar.transform.position, Is.EqualTo(new Vector3(2f, 5f, -2f)), "teleport clamps into bounds");
        }

        [UnityTest]
        public IEnumerator PoseChangedFiresWhileMovingOnly()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            yield return Frames(10);
            int count = 0;
            avatar.PoseChanged += _ => count++;
            yield return Frames(30);
            Assert.That(count, Is.EqualTo(0), "standing still raises nothing");
            Assert.That(avatar.IsMoving, Is.False);
            input.Move = Vector2.up;
            yield return Frames(30);
            Assert.That(count, Is.GreaterThanOrEqualTo(29));
            Assert.That(avatar.IsMoving, Is.True);
        }

        [UnityTest]
        public IEnumerator WalksThroughRemoteAvatars()
        {
            scene.Ground();
            var remote = scene.Remote(7, "Ghost");
            remote.SnapTo(new AvatarPose(new Vector3(0f, 0f, 2f), 180f));
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            yield return Settle(avatar);
            input.Move = Vector2.up;
            yield return Frames(90);
            Assert.That(avatar.transform.position.z, Is.GreaterThan(4f));
        }

        [UnityTest]
        public IEnumerator OrbitCameraDrivesViewAndPullsIn()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
            var cam = scene.Track(OrbitCamera.Create());
            cam.Follow(avatar);
            Assert.That(cam.Camera, Is.Not.Null);
            yield return Settle(avatar);
            yield return Frames(30);

            Assert.That(cam.CurrentDistance, Is.EqualTo(cam.Distance).Within(0.05f));
            var toAvatar = avatar.transform.position + Vector3.up * cam.PivotHeight - cam.transform.position;
            Assert.That(Vector3.Angle(toAvatar, cam.transform.forward), Is.LessThan(1f), "camera looks at the avatar");

            cam.Orbit(new Vector2(90f, 0f));
            input.Move = Vector2.up;
            yield return Frames(60);
            Assert.That(avatar.transform.position.x, Is.GreaterThan(2f), "forward follows the camera heading");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(avatar.CurrentPose.Yaw, cam.Yaw)), Is.LessThan(1f), "faces away from the camera");
            input.Reset();
            yield return Frames(30);

            // A wall behind the avatar pulls the camera in.
            var back = Quaternion.Euler(0f, cam.Yaw, 0f) * Vector3.back;
            scene.Box(avatar.transform.position + back * 2f + Vector3.up * 2f, new Vector3(6f, 6f, 0.3f) , "Wall")
                .transform.rotation = Quaternion.Euler(0f, cam.Yaw, 0f);
            Physics.SyncTransforms();
            yield return Frames(5);
            Assert.That(cam.CurrentDistance, Is.LessThan(2f));

            // Zooming all the way in is first person and hides the avatar.
            for (int i = 0; i < 30; i++) cam.Zoom(1f);
            Assert.That(cam.Distance, Is.EqualTo(0f));
            yield return Frames(60);
            Assert.That(cam.IsFirstPerson, Is.True);
            Assert.That(((PlaceholderAvatarVisual)avatar.Visual).Visible, Is.False);
            for (int i = 0; i < 30; i++) cam.Zoom(-1f);
            Assert.That(cam.Distance, Is.EqualTo(cam.MaxDistance));
        }
    }
}
