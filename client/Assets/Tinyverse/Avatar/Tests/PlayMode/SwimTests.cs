using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>
    /// Swimming in open water (<see cref="AvatarWater"/>): here the sea is everything at x &lt; 0 at the level of the
    /// ground, the land everything at x ≥ 0. Every avatar that is not flying swims there, drawn treading water or
    /// swimming at the depth the clips were authored at; the local avatar swims slower and cannot jump.
    /// </summary>
    public class SwimTests
    {
        private AvatarTestScene scene;
        private AvatarStyle styleBefore;
        private Mesh scratch;

        [SetUp]
        public void SetUp()
        {
            scene = new AvatarTestScene();
            styleBefore = HumanCatalog.Style;
            scratch = new Mesh();
            AvatarWater.Test = p => p.x < 0f && p.y < 0.15f;
        }

        [TearDown]
        public void TearDown()
        {
            AvatarWater.Test = null;
            scene.Dispose();
            HumanCatalog.Style = styleBefore;
            Object.Destroy(scratch);
        }

        private static IEnumerable<BodyPreset> AllPresets() => HumanCatalog.ModernPresets.Concat(HumanCatalog.ClassicPresets);

        private static AvatarPose At(float x, string hint, float speed = 0f) =>
            new AvatarPose(new Vector3(x, 0f, 0f), 0f, new Vector3(0f, 0f, speed), hint == "fly" ? MoveKind.Fly : MoveKind.Walk, hint);

        private (HumanAvatarVisual visual, HumanFigure figure) Remote(BodyPreset preset, AvatarPose pose)
        {
            HumanCatalog.Style = preset.Style;
            var remote = scene.Remote(40, "Swimmer");
            var look = AvatarLook.Default;
            look.BodyPreset = preset.Id;
            remote.SetLook(look);
            remote.SnapTo(pose);
            var visual = (HumanAvatarVisual)remote.Visual;
            Assert.That(visual.Figure, Is.Not.Null, preset.Id);
            Run(visual, pose, 2f);
            return (visual, visual.Figure);
        }

        private static void Run(HumanAvatarVisual visual, AvatarPose pose, float seconds)
        {
            for (int i = 0; i < Mathf.RoundToInt(seconds / AvatarTestScene.Dt); i++) visual.UpdatePose(pose, AvatarTestScene.Dt);
        }

        /// <summary>Lowest point and head top over <paramref name="seconds"/>, metres above the avatar's position.</summary>
        private (float lowMin, float lowMax, float headMin, float headMax) Sweep(HumanAvatarVisual visual, AvatarPose pose, float seconds)
        {
            float lowMin = float.MaxValue, lowMax = float.MinValue, headMin = float.MaxValue, headMax = float.MinValue;
            for (int i = 0; i < Mathf.RoundToInt(seconds / AvatarTestScene.Dt); i++)
            {
                visual.UpdatePose(pose, AvatarTestScene.Dt);
                float low = visual.Figure.LowestPoint(scratch), head = visual.Figure.CurrentHeadTop;
                lowMin = Mathf.Min(lowMin, low);
                lowMax = Mathf.Max(lowMax, low);
                headMin = Mathf.Min(headMin, head);
                headMax = Mathf.Max(headMax, head);
            }
            return (lowMin, lowMax, headMin, headMax);
        }

        [UnityTest]
        public IEnumerator RemoteAvatarsInTheWaterSwimWithTheBodyInTheWater()
        {
            var problems = new List<string>();
            foreach (var preset in AllPresets())
            {
                // Bots and agents report "walk" and "idle"; the position decides.
                foreach (var pose in new[] { At(-5f, "idle"), At(-5f, "walk", 2f) })
                {
                    var (visual, f) = Remote(preset, pose);
                    string what = $"{preset.Style} {preset.Id} {pose.Animation}";
                    var (lowMin, lowMax, headMin, headMax) = Sweep(visual, pose, 3.4f);
                    float tag = visual.NameTag.AnchorHeight;
                    Debug.Log($"[SwimTests] {what}: lowest {lowMin:0.000}..{lowMax:0.000}, head top {headMin:0.000}..{headMax:0.000}, tag {tag:0.000}");
                    if (f.SwimShare < 0.999f) problems.Add($"{what}: not swimming ({f.SwimShare:0.00})");
                    var dominant = f.Player.Targets.Dominant;
                    var expected = pose.Velocity.z > 0f ? HumanClip.SwimForward : HumanClip.SwimIdle;
                    if (dominant != expected) problems.Add($"{what}: plays {dominant}, not {expected}");
                    // In the water: well below the surface, the head out of it.
                    if (lowMax > -0.2f) problems.Add($"{what}: lowest point up to {lowMax:0.000} m, not in the water");
                    if (headMax < 0.05f || headMin > 0.9f) problems.Add($"{what}: head top {headMin:0.000}..{headMax:0.000} m, not at the surface");
                    if (tag < headMax + 0.1f || tag > headMax + 0.5f) problems.Add($"{what}: name tag at {tag:0.000} m, not just above the head");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
            Assert.That(problems, Is.Empty);
        }

        [UnityTest]
        public IEnumerator OnLandAndWithoutASeaNobodySwims()
        {
            var preset = HumanCatalog.ModernPresets[0];
            var (visual, f) = Remote(preset, At(5f, "walk", 2f));
            var (lowMin, _, _, _) = Sweep(visual, At(5f, "walk", 2f), 1.5f);
            Assert.That(f.SwimShare, Is.EqualTo(0f), "on land");
            Assert.That(lowMin, Is.InRange(-0.035f, 0.01f), "feet on the ground");
            Object.Destroy(visual.transform.root.gameObject);
            yield return null;

            AvatarWater.Test = null;
            (visual, f) = Remote(preset, At(-5f, "walk", 2f));
            Assert.That(f.SwimShare, Is.EqualTo(0f), "a world without a sea");
            Assert.That(f.LowestPoint(scratch), Is.InRange(-0.035f, 0.25f));
            Object.Destroy(visual.transform.root.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator FlyingOverTheWaterIsNotSunk()
        {
            foreach (var preset in new[] { HumanCatalog.ModernPresets[0], HumanCatalog.ClassicPresets[1] })
            {
                foreach (var speed in new[] { 0f, 12f })
                {
                    var pose = At(-5f, "fly", speed);
                    var (visual, f) = Remote(preset, pose);
                    var (lowMin, _, _, _) = Sweep(visual, pose, 3.4f);
                    Assert.That(f.SwimShare, Is.EqualTo(0f), $"{preset.Id} flying at {speed} m/s");
                    Assert.That(lowMin, Is.GreaterThan(0.03f), $"{preset.Id} flying at {speed} m/s over the water: above it");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator WadingInAndOutAndTakingOffGlide()
        {
            var problems = new List<string>();
            foreach (var preset in new[] { HumanCatalog.ModernPresets[0], HumanCatalog.ModernPresets[3], HumanCatalog.ClassicPresets[0] })
            {
                var (visual, f) = Remote(preset, At(1.5f, "walk", 1.5f));
                string what = $"{preset.Style} {preset.Id}";
                float fastest = 0f, x = 1.5f;
                var body = f.Animator.bodyPosition;
                void Step(AvatarPose pose, string phase)
                {
                    visual.UpdatePose(pose, AvatarTestScene.Dt);
                    var b = f.Animator.bodyPosition; // the avatar's root stays put; only the pose's position (the water test) moves
                    float d = Vector3.Distance(b, body);
                    fastest = Mathf.Max(fastest, d);
                    if (d > 0.06f) problems.Add($"{what} {phase}: the body jumps {d:0.000} m in a frame");
                    body = b;
                }
                // Walk into the sea along -X at 1.5 m/s, tread water, walk back out.
                for (int i = 0; i < 180; i++) { x -= 1.5f * AvatarTestScene.Dt; Step(At(x, "walk", 1.5f), "wading in"); }
                for (int i = 0; i < 60; i++) Step(At(x, "idle"), "treading water");
                Assert.That(f.SwimShare, Is.EqualTo(1f), what + ": swimming");
                // Take off from the water: the figure rises out of it.
                for (int i = 0; i < 90; i++) Step(At(x, "fly"), "taking off");
                Assert.That(f.LowestPoint(scratch), Is.GreaterThan(0.03f), what + ": hovering above the water");
                for (int i = 0; i < 60; i++) Step(At(x, "idle"), "dropping back in");
                for (int i = 0; i < 240; i++) { x += 1.5f * AvatarTestScene.Dt; Step(At(x, "walk", 1.5f), "wading out"); }
                Assert.That(f.SwimShare, Is.EqualTo(0f), what + ": out of the water");
                Assert.That(f.LowestPoint(scratch), Is.InRange(-0.035f, 0.05f), what + ": back on its feet");
                Debug.Log($"[SwimTests] {what}: the body at most {fastest * 1000f:0} mm per frame");
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
            Assert.That(problems.Take(10), Is.Empty);
        }

        [UnityTest]
        public IEnumerator LocalAvatarSwimsSlowlyAndTakesOffFromTheWater()
        {
            scene.Ground();
            var (avatar, input) = scene.Local(new Vector3(-5f, 0.05f, 0f));
            for (int i = 0; i < 60; i++) yield return null;
            Assert.That(avatar.IsGrounded, Is.True);
            Assert.That(avatar.IsSwimming, Is.True);
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo(AvatarAnimations.Swim));
            var figure = ((HumanAvatarVisual)avatar.Visual).Figure;
            Assert.That(figure.SwimShare, Is.EqualTo(1f));

            // Swimming is slower than walking, even with the run key.
            input.Move = Vector2.left;
            input.Run = true;
            for (int i = 0; i < 60; i++) yield return null;
            Assert.That(avatar.CurrentPose.Velocity.magnitude, Is.EqualTo(AvatarWater.SwimRunSpeed).Within(0.05f));
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo(AvatarAnimations.Swim));
            input.Run = false;
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(avatar.CurrentPose.Velocity.magnitude, Is.EqualTo(AvatarWater.SwimSpeed).Within(0.05f));
            input.Reset();
            for (int i = 0; i < 30; i++) yield return null;

            // No jumping in the water.
            float y = avatar.transform.position.y;
            input.Jump();
            for (int i = 0; i < 20; i++)
            {
                yield return null;
                Assert.That(avatar.transform.position.y, Is.EqualTo(y).Within(0.01f), "no jump in the water");
            }

            // F takes off; hovering at the surface is drawn above it.
            input.ToggleFly();
            for (int i = 0; i < 120; i++) yield return null;
            Assert.That(avatar.IsFlying, Is.True);
            Assert.That(avatar.IsSwimming, Is.False);
            Assert.That(figure.LowestPoint(scratch) + avatar.transform.position.y, Is.GreaterThan(0.03f), "hovering above the water");

            // Back in the water, then out onto the land (x ≥ 0).
            input.ToggleFly();
            for (int i = 0; i < 60; i++) yield return null;
            Assert.That(avatar.IsSwimming, Is.True);
            input.Move = Vector2.right;
            for (int i = 0; i < 900 && avatar.transform.position.x < 2f; i++) yield return null;
            Assert.That(avatar.transform.position.x, Is.GreaterThanOrEqualTo(2f), "swam back to the land");
            input.Reset();
            for (int i = 0; i < 60; i++) yield return null;
            Assert.That(avatar.IsSwimming, Is.False);
            Assert.That(avatar.CurrentPose.Animation, Is.EqualTo(AvatarAnimations.Idle));
            Assert.That(figure.SwimShare, Is.EqualTo(0f));
        }
    }
}
