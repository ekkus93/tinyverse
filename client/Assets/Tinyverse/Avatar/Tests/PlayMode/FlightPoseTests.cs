using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>
    /// Where the drawn body is, relative to the avatar's position (its feet), in every clip of every body of both
    /// styles: hovering floats just above it, flying forward keeps every limb above it, the ground clips keep the
    /// feet on it, and taking off, flying and landing move the body without dipping or popping. The flying clips are
    /// the library's swimming clips, lifted by Editor/FlightClipLevels.cs; without that the hovering figure was
    /// drawn waist-deep in the ground.
    /// </summary>
    public class FlightPoseTests
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
        }

        [TearDown]
        public void TearDown()
        {
            scene.Dispose();
            HumanCatalog.Style = styleBefore;
            Object.Destroy(scratch);
        }

        private static IEnumerable<BodyPreset> AllPresets() => HumanCatalog.ModernPresets.Concat(HumanCatalog.ClassicPresets);

        private static AvatarPose Pose(string hint, float speed = 0f) =>
            new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, speed), hint == "fly" ? MoveKind.Fly : MoveKind.Walk, hint);

        private static readonly AvatarPose Standing = Pose("idle");
        private static readonly AvatarPose Hovering = Pose("fly");
        private static readonly AvatarPose FlyingFast = Pose("fly", 16f);

        /// <summary>A remote avatar of <paramref name="preset"/> at the origin, settled into <paramref name="pose"/>.</summary>
        private (HumanAvatarVisual visual, HumanFigure figure) Avatar(BodyPreset preset, AvatarPose pose, float settle = 2f)
        {
            HumanCatalog.Style = preset.Style;
            var remote = scene.Remote(30, "Flyer");
            var look = AvatarLook.Default;
            look.BodyPreset = preset.Id;
            remote.SetLook(look);
            remote.SnapTo(pose);
            var visual = (HumanAvatarVisual)remote.Visual;
            Assert.That(visual.Figure, Is.Not.Null, preset.Id);
            Assert.That(visual.Figure.Preset, Is.SameAs(preset));
            Run(visual, pose, settle);
            return (visual, visual.Figure);
        }

        private static void Run(HumanAvatarVisual visual, AvatarPose pose, float seconds)
        {
            int frames = Mathf.RoundToInt(seconds / AvatarTestScene.Dt);
            for (int i = 0; i < frames; i++) visual.UpdatePose(pose, AvatarTestScene.Dt);
        }

        /// <summary>The lowest and highest of the body's lowest point, sampled every frame for <paramref name="seconds"/>.</summary>
        private (float min, float max) Sweep(HumanAvatarVisual visual, AvatarPose pose, float seconds)
        {
            float min = float.MaxValue, max = float.MinValue;
            int frames = Mathf.RoundToInt(seconds / AvatarTestScene.Dt);
            for (int i = 0; i < frames; i++)
            {
                visual.UpdatePose(pose, AvatarTestScene.Dt);
                float low = visual.Figure.LowestPoint(scratch);
                min = Mathf.Min(min, low);
                max = Mathf.Max(max, low);
            }
            return (min, max);
        }

        private static Transform Bone(HumanFigure f, HumanBodyBones b) => f.Animator.GetBoneTransform(b);

        private static float ClipLength(HumanClip clip) => Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + HumanAnimationPlayer.AssetName(clip)).length;

        [UnityTest]
        public IEnumerator HoveringFloatsJustAboveThePosition()
        {
            var problems = new List<string>();
            foreach (var preset in AllPresets())
            {
                var (visual, f) = Avatar(preset, Hovering);
                var (min, max) = Sweep(visual, Hovering, ClipLength(HumanClip.Hover) + 0.2f);
                string what = $"{preset.Style} {preset.Id}";
                Debug.Log($"[FlightPoseTests] {what} hovering: lowest point {min:0.000} .. {max:0.000} m");
                if (min < 0.03f) problems.Add($"{what}: lowest point {min:0.000} m, at or below the avatar's position");
                if (max > 0.3f) problems.Add($"{what}: lowest point up to {max:0.000} m, floating too high");
                var lean = Bone(f, HumanBodyBones.Head).position - Bone(f, HumanBodyBones.Hips).position;
                if (Vector3.Angle(lean, Vector3.up) > 25f) problems.Add($"{what}: not upright ({Vector3.Angle(lean, Vector3.up):0} degrees)");
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
            Assert.That(problems, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FlyingForwardKeepsEveryLimbAboveThePosition()
        {
            var problems = new List<string>();
            foreach (var preset in AllPresets())
            {
                foreach (var speed in new[] { 5f, 16f })
                {
                    var pose = Pose("fly", speed);
                    var (visual, f) = Avatar(preset, pose);
                    // At the slowest playback rate one loop takes length / 0.7.
                    var (min, max) = Sweep(visual, pose, ClipLength(HumanClip.FlyForward) / 0.7f + 0.2f);
                    string what = $"{preset.Style} {preset.Id} at {speed} m/s";
                    float centre = f.Animator.bodyPosition.y - visual.transform.position.y;
                    Debug.Log($"[FlightPoseTests] {what} flying: lowest point {min:0.000} .. {max:0.000} m, centre of mass {centre:0.000} m");
                    if (min < 0.03f) problems.Add($"{what}: lowest point {min:0.000} m, at or below the avatar's position");
                    // Lying in the middle of the avatar's 1.8 m capsule, not above it.
                    if (centre < 0.6f || centre > 1.2f) problems.Add($"{what}: centre of mass {centre:0.000} m above the position");
                    var lean = Bone(f, HumanBodyBones.Head).position - Bone(f, HumanBodyBones.Hips).position;
                    if (Vector3.Angle(lean, Vector3.up) < 50f) problems.Add($"{what}: not lying forward ({Vector3.Angle(lean, Vector3.up):0} degrees from upright)");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
            Assert.That(problems, Is.Empty);
        }

        [UnityTest]
        public IEnumerator GroundClipsKeepTheFeetOnTheGround()
        {
            var problems = new List<string>();
            var clips = new[] { ("idle", 0f, HumanClip.Idle), ("walk", 1.2f, HumanClip.Walk), ("walk", 4f, HumanClip.Jog), ("run", 8f, HumanClip.Sprint) };
            foreach (var preset in AllPresets())
            {
                foreach (var (hint, speed, clip) in clips)
                {
                    var pose = Pose(hint, speed);
                    var (visual, _) = Avatar(preset, pose);
                    var (min, _) = Sweep(visual, pose, ClipLength(clip) * 1.6f);
                    string what = $"{preset.Style} {preset.Id} {clip}";
                    // The soles touch the ground (the meshes sink a centimetre or two into it, as authored) and no
                    // further.
                    if (min < -0.035f || min > 0.01f) problems.Add($"{what}: lowest point {min:0.000} m, not on the ground");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
            Assert.That(problems, Is.Empty);
        }

        /// <summary>
        /// Runs <paramref name="pose"/> for <paramref name="seconds"/>, its speed changing from
        /// <paramref name="fromSpeed"/> at the local avatar's flying acceleration (24 m/s², 30 braking), checking every
        /// frame that the lowest point is at least <paramref name="floor"/> and that the body (its centre of mass)
        /// moves by less than <see cref="BodyStep"/>, so it glides rather than pops. The lowest point itself may move
        /// faster, with the clip's own strokes.
        /// </summary>
        private void Transition(HumanAvatarVisual visual, AvatarPose pose, float seconds, float floor, string what, List<string> problems, float fromSpeed = 0f)
        {
            var f = visual.Figure;
            var body = f.Animator.bodyPosition;
            float fastest = 0f, lowest = float.MaxValue;
            float target = pose.Velocity.z, speed = fromSpeed;
            int frames = Mathf.RoundToInt(seconds / AvatarTestScene.Dt);
            for (int i = 0; i < frames; i++)
            {
                speed = Mathf.MoveTowards(speed, target, (target > speed ? 24f : 30f) * AvatarTestScene.Dt);
                pose.Velocity = new Vector3(0f, 0f, speed);
                visual.UpdatePose(pose, AvatarTestScene.Dt);
                float low = f.LowestPoint(scratch);
                lowest = Mathf.Min(lowest, low);
                var b = f.Animator.bodyPosition;
                fastest = Mathf.Max(fastest, Vector3.Distance(b, body));
                body = b;
                if (low < floor) { problems.Add($"{what} frame {i}: lowest point {low:0.000} m, below {floor:0.000}"); break; }
            }
            Debug.Log($"[FlightPoseTests] {what}: lowest point {lowest:0.000} m, the body at most {fastest * 1000f:0} mm per frame");
            if (fastest > BodyStep) problems.Add($"{what}: the body moves {fastest:0.000} m in one frame");
        }

        /// <summary>
        /// The most the body may move in one frame (at 60 a second) while the clip changes: 2.4 m/s, a quick glide
        /// (lifting it by a metre at once, as the unlifted hovering clip did, moves it 0.12 m in the first frame).
        /// </summary>
        private const float BodyStep = 0.04f;

        [UnityTest]
        public IEnumerator TakingOffFlyingAndLandingDoNotDipOrPop()
        {
            var problems = new List<string>();
            foreach (var preset in AllPresets())
            {
                var (visual, f) = Avatar(preset, Standing);
                float ground = f.LowestPoint(scratch);
                string what = $"{preset.Style} {preset.Id}";
                // F on the ground: the figure rises from its standing height, never below it.
                Transition(visual, Hovering, 1.5f, ground - 0.005f, what + " standing → hovering", problems);
                // Hovering to flying forward and back: always above the position.
                Transition(visual, FlyingFast, 1.5f, 0f, what + " hovering → flying", problems);
                Transition(visual, Hovering, 1.5f, 0f, what + " flying → hovering", problems, FlyingFast.Velocity.z);
                // Landing: back down to the standing height, not through the ground.
                Transition(visual, Standing, 1.5f, ground - 0.005f, what + " hovering → standing", problems);
                // Walking then taking off.
                Run(visual, Pose("walk", 1.2f), 1f);
                Transition(visual, Hovering, 1.5f, -0.035f, what + " walking → hovering", problems);
                Object.Destroy(visual.transform.root.gameObject);
                yield return null;
            }
            Assert.That(problems, Is.Empty);
        }

        [UnityTest]
        public IEnumerator NameTagStaysAboveTheHeadWhileFlying()
        {
            foreach (var preset in AllPresets())
            {
                foreach (var pose in new[] { Hovering, FlyingFast })
                {
                    // Standing, the top of the head (or hair) is Height; the head bone sits that much lower.
                    var (visual, f) = Avatar(preset, Standing);
                    float crown = f.Height - (Bone(f, HumanBodyBones.Head).position.y - visual.transform.position.y);
                    float standingTag = visual.NameTag.AnchorHeight;
                    // From standing into the flight pose and through its loop: the tag stays clear above the head.
                    float lowestGap = float.MaxValue, highestTag = standingTag;
                    int frames = Mathf.RoundToInt((1.5f + ClipLength(HumanClip.Hover)) / AvatarTestScene.Dt);
                    for (int i = 0; i < frames; i++)
                    {
                        visual.UpdatePose(pose, AvatarTestScene.Dt);
                        float top = Bone(f, HumanBodyBones.Head).position.y - visual.transform.position.y + crown;
                        float tag = visual.NameTag.transform.localPosition.y;
                        lowestGap = Mathf.Min(lowestGap, tag - top);
                        highestTag = Mathf.Max(highestTag, tag);
                    }
                    string what = $"{preset.Style} {preset.Id} {(pose.Velocity.z > 0f ? "flying" : "hovering")}";
                    Debug.Log($"[FlightPoseTests] {what}: the tag at least {lowestGap:0.000} m above the head, at most {highestTag - standingTag:0.000} m higher than standing");
                    Assert.That(lowestGap, Is.GreaterThan(0.1f), $"{what}: the tag clear above the head");
                    Assert.That(highestTag - standingTag, Is.LessThan(0.3f), $"{what}: the tag not far above the head");
                    Object.Destroy(visual.transform.root.gameObject);
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator LocalAvatarTakingOffFromTheGroundRises()
        {
            scene.Ground();
            foreach (var preset in new[] { HumanCatalog.ModernPresets[0], HumanCatalog.ClassicPresets[1] })
            {
                HumanCatalog.Style = preset.Style;
                var (avatar, input) = scene.Local(new Vector3(0f, 0.05f, 0f));
                var look = AvatarLook.Default;
                look.BodyPreset = preset.Id;
                avatar.SetLook(look);
                for (int i = 0; i < 120; i++) yield return null;
                Assert.That(avatar.IsGrounded, Is.True);
                var figure = ((HumanAvatarVisual)avatar.Visual).Figure;
                float WorldLowest() => figure.LowestPoint(scratch) + avatar.transform.position.y;
                float standing = WorldLowest();
                Assert.That(standing, Is.InRange(-0.06f, 0.03f), $"{preset.Id}: standing on the ground");

                input.ToggleFly();
                float last = standing;
                for (int i = 0; i < 90; i++)
                {
                    yield return null;
                    float low = WorldLowest();
                    Assert.That(low, Is.GreaterThanOrEqualTo(standing - 0.005f), $"{preset.Id} frame {i} after F: the body does not sink into the ground");
                    Assert.That(Mathf.Abs(low - last), Is.LessThan(0.03f), $"{preset.Id} frame {i} after F: no pop");
                    last = low;
                }
                Assert.That(avatar.IsFlying, Is.True);
                Assert.That(last, Is.GreaterThan(0.03f), $"{preset.Id}: hovering clear of the ground");
                Object.Destroy(avatar.gameObject);
                yield return null;
            }
        }
    }
}
