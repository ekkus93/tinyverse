using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    public class RemoteAvatarTests
    {
        private AvatarTestScene scene;

        [SetUp]
        public void SetUp() => scene = new AvatarTestScene();

        [TearDown]
        public void TearDown() => scene.Dispose();

        private const float Radius = 10f;
        private const float Speed = 4f;

        private static AvatarPose PathAt(double t)
        {
            float w = Speed / Radius;
            float a = (float)t * w;
            var pos = new Vector3(Mathf.Sin(a) * Radius, 0f, Mathf.Cos(a) * Radius);
            var vel = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a)) * Speed;
            return new AvatarPose(pos, AvatarMotion.YawOf(vel), vel, MoveKind.Walk, AvatarAnimations.Walk);
        }

        [UnityTest]
        public IEnumerator FollowsJitteredPathSmoothly()
        {
            var remote = scene.Remote(42, "Walker");
            double t0 = Time.timeAsDouble;
            remote.ServerTime = () => Time.timeAsDouble - t0;
            var rng = new System.Random(1234);

            // Server states every 50 ms, delivered after 30–90 ms (so some arrive out of order), some twice.
            var pending = new List<(double deliverAt, double stamp)>();
            double nextStamp = 0.0;
            float maxError = 0f, maxStep = 0f;
            Vector3? last = null;
            int frames = (int)(5f / AvatarTestScene.Dt);
            for (int f = 0; f < frames; f++)
            {
                double now = Time.timeAsDouble - t0;
                while (nextStamp <= now)
                {
                    double latency = 0.03 + rng.NextDouble() * 0.06;
                    pending.Add((nextStamp + latency, nextStamp));
                    if (rng.NextDouble() < 0.1) pending.Add((nextStamp + latency + 0.01, nextStamp));
                    nextStamp += 0.05;
                }
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].deliverAt > now) continue;
                    remote.PushState(pending[i].stamp, PathAt(pending[i].stamp));
                    pending.RemoveAt(i);
                }

                yield return null;

                var pos = remote.transform.position;
                if (f > 30)
                {
                    double renderTime = remote.EstimatedServerTime - remote.InterpolationDelay;
                    float error = Vector3.Distance(pos, PathAt(renderTime).Position);
                    maxError = Mathf.Max(maxError, error);
                    if (last.HasValue) maxStep = Mathf.Max(maxStep, Vector3.Distance(pos, last.Value));
                    float yawError = Mathf.Abs(Mathf.DeltaAngle(remote.CurrentPose.Yaw, PathAt(renderTime).Yaw));
                    Assert.That(yawError, Is.LessThan(2f));
                }
                last = pos;
                Assert.That(remote.Buffer.Count, Is.LessThanOrEqualTo(remote.Buffer.Capacity));
            }

            Debug.Log($"[RemoteAvatarTests] max error {maxError:0.0000} m, max frame step {maxStep:0.0000} m");
            Assert.That(maxError, Is.LessThan(0.05f), "follows the path closely");
            Assert.That(maxStep, Is.LessThanOrEqualTo(Speed * AvatarTestScene.Dt * 1.05f), "no per-frame jump beyond its speed");
            Assert.That(remote.Buffer.Count, Is.LessThan(10), "old samples are discarded");
            Assert.That(remote.CurrentPose.Animation, Is.EqualTo("walk"));
        }

        [UnityTest]
        public IEnumerator ExtrapolatesBrieflyThenStops()
        {
            var remote = scene.Remote(1, "Runner");
            double t0 = Time.timeAsDouble;
            remote.ServerTime = () => Time.timeAsDouble - t0;
            var v = new Vector3(8f, 0f, 0f);
            for (int i = 0; i <= 10; i++)
                remote.PushState(i * 0.05, new AvatarPose(v * (i * 0.05f), 90f, v, MoveKind.Run, "run"));
            // Last sample at t = 0.5 s, x = 4. Wait until well past it.
            while (Time.timeAsDouble - t0 < 2.0) yield return null;
            float x = remote.transform.position.x;
            Assert.That(x, Is.EqualTo(4f + 8f * 0.25f).Within(0.01f), "extrapolated 0.25 s then held");
        }

        [UnityTest]
        public IEnumerator SnapsAcrossATeleport()
        {
            var remote = scene.Remote(2, "Jumper");
            double t0 = Time.timeAsDouble;
            remote.ServerTime = () => Time.timeAsDouble - t0;
            remote.PushState(0.0, new AvatarPose(Vector3.zero, 0f));
            remote.PushState(0.05, new AvatarPose(new Vector3(100f, 0f, 0f), 0f));
            var seen = new List<float>();
            while (Time.timeAsDouble - t0 < 0.4)
            {
                yield return null;
                seen.Add(remote.transform.position.x);
            }
            foreach (var x in seen)
                Assert.That(x == 0f || x == 100f, Is.True, $"slid through x = {x}");
            Assert.That(seen[seen.Count - 1], Is.EqualTo(100f));
        }

        [UnityTest]
        public IEnumerator WorksWithoutAServerClock()
        {
            var remote = scene.Remote(3, "NoClock");
            double serverStart = 5000.0;
            double t0 = Time.unscaledTimeAsDouble;
            double next = 0;
            // The built-in estimate runs on real (unscaled) time, so loop for a real second.
            while (Time.unscaledTimeAsDouble - t0 < 1.0)
            {
                double elapsed = Time.unscaledTimeAsDouble - t0;
                while (next <= elapsed)
                {
                    remote.PushState(serverStart + next, new AvatarPose(new Vector3((float)next * 2f, 0f, 0f), 90f, new Vector3(2f, 0, 0)));
                    next += 0.05;
                }
                yield return null;
            }
            double elapsedNow = Time.unscaledTimeAsDouble - t0;
            float expected = (float)(elapsedNow - remote.InterpolationDelay) * 2f;
            Assert.That(remote.transform.position.x, Is.EqualTo(expected).Within(0.3f), "drawn about 100 ms behind");
        }

        [UnityTest]
        public IEnumerator FirstStateIsShownImmediately()
        {
            var remote = scene.Remote(4, "Spawn");
            remote.ServerTime = () => 0.0;
            remote.PushState(10.0, new AvatarPose(new Vector3(3f, 1f, 4f), 270f));
            Assert.That(remote.transform.position, Is.EqualTo(new Vector3(3f, 1f, 4f)));
            yield return null;
            Assert.That(remote.transform.position, Is.EqualTo(new Vector3(3f, 1f, 4f)));
        }

        [UnityTest]
        public IEnumerator NameTagAndAgentBadge()
        {
            var remote = scene.Remote(5, "Robo", agent: true);
            var tag = remote.Visual.NameTag;
            Assert.That(tag, Is.Not.Null);
            Assert.That(tag.Text, Is.EqualTo("Robo"));
            Assert.That(tag.ShowsAgentBadge, Is.True);
            Assert.That(tag.BadgeText.gameObject.activeInHierarchy, Is.True);
            Assert.That(tag.NameText.font, Is.Not.Null, "built-in font loaded");
            remote.SetName("Robo 2");
            Assert.That(tag.Text, Is.EqualTo("Robo 2"));
            remote.SetAgent(false);
            Assert.That(tag.BadgeText.gameObject.activeInHierarchy, Is.False);

            var human = scene.Remote(6, "Ann");
            Assert.That(human.Visual.NameTag.ShowsAgentBadge, Is.False);

            // Faces the camera, fades out far away.
            var camGo = new GameObject("TestCamera");
            var cam = scene.Track(camGo.AddComponent<Camera>());
            NameTag.ViewCamera = cam;
            remote.SnapTo(new AvatarPose(Vector3.zero, 0f));
            cam.transform.position = new Vector3(0f, 2f, -5f);
            cam.transform.rotation = Quaternion.Euler(10f, 20f, 0f);
            yield return null;
            Assert.That(Quaternion.Angle(tag.transform.rotation, cam.transform.rotation), Is.LessThan(0.1f));
            Assert.That(tag.Alpha, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(tag.NameText.GetComponent<MeshRenderer>().enabled, Is.True);
            var b = tag.NameText.GetComponent<MeshRenderer>().bounds;
            Debug.Log($"[RemoteAvatarTests] name tag bounds at 5 m: {b.size}");

            cam.transform.position = new Vector3(0f, 2f, -30f);
            yield return null;
            Assert.That(tag.transform.localScale.x, Is.GreaterThan(2f), "grows with distance to stay readable");

            cam.transform.position = new Vector3(0f, 2f, -80f);
            yield return null;
            Assert.That(tag.Alpha, Is.EqualTo(0f));
            Assert.That(tag.NameText.GetComponent<MeshRenderer>().enabled, Is.False);
        }

        [UnityTest]
        public IEnumerator TriggerColliderMapsBackToId()
        {
            var remote = scene.Remote(9001, "Target");
            remote.SnapTo(new AvatarPose(new Vector3(5f, 0f, 5f), 0f));
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
            Assert.That(remote.Collider.isTrigger, Is.True);

            bool hit = Physics.Raycast(new Vector3(5f, 10f, 5f), Vector3.down, out var info, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            Assert.That(hit, Is.True);
            Assert.That(AvatarId.TryGet(info.collider, out var id), Is.True);
            Assert.That(id, Is.EqualTo(9001UL));
            Assert.That(RemoteAvatar.TryGetFromCollider(info.collider, out var found), Is.True);
            Assert.That(found, Is.SameAs(remote));

            remote.SnapTo(new AvatarPose(new Vector3(-5f, 0f, -5f), 0f));
            Physics.SyncTransforms();
            Assert.That(Physics.Raycast(new Vector3(-5f, 10f, -5f), Vector3.down, out info, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide), Is.True, "collider follows the avatar");
        }

        [UnityTest]
        public IEnumerator PlaceholderLooksAndAnimates()
        {
            var remote = scene.Remote(10, "Look");
            var visual = (PlaceholderAvatarVisual)remote.Visual;
            Assert.That(visual.Height, Is.EqualTo(1.8f).Within(0.05f));
            remote.SetLook(AvatarLook.WithBody(new Color(1f, 0.5f, 0f)));
            Assert.That(visual.BodyColor.r, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(visual.BodyColor.g, Is.EqualTo(0.5f).Within(1e-3f));
            remote.SetLook(default);
            var c = visual.BodyColor;
            var d = AvatarLook.Default.BodyColor;
            Assert.That(new Vector4(c.r - d.r, c.g - d.g, c.b - d.b, c.a - d.a).magnitude, Is.LessThan(1e-3f), "unset look falls back to the default");

            // Every hint animates without errors.
            foreach (var anim in new[] { "idle", "walk", "run", "fly", "fall", "", null, "dance" })
            {
                remote.SnapTo(new AvatarPose(Vector3.zero, 0f, new Vector3(0, 0, 5f), MoveKind.Run, anim));
                for (int i = 0; i < 5; i++)
                {
                    visual.UpdatePose(remote.CurrentPose, AvatarTestScene.Dt);
                    yield return null;
                }
            }
            Assert.That(visual.GetComponentsInChildren<MeshRenderer>().Length, Is.GreaterThanOrEqualTo(5));
            visual.SetVisible(false);
            Assert.That(visual.Visible, Is.False);
            Assert.That(visual.GetComponentsInChildren<Collider>().Length, Is.EqualTo(0), "visual parts have no colliders");
        }
    }
}
