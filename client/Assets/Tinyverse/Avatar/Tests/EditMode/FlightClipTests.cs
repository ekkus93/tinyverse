using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>
    /// The flying clips (the library's swimming clips) carry the root height offset that Editor/FlightClipLevels.cs
    /// sets, which lifts them clear of the avatar's position; the other clips have none. (FlightPoseTests checks
    /// where the bodies end up.)
    /// </summary>
    public class FlightClipTests
    {
        private static AnimationClipSettings Settings(HumanClip clip)
        {
            var c = Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + clip);
            Assert.That(c, Is.Not.Null, clip.ToString());
            return AnimationUtility.GetAnimationClipSettings(c);
        }

        [Test]
        public void FlyingClipsAreLifted()
        {
            // A negative offset raises the body, in units of the body's human scale (about a metre).
            Assert.That(Settings(HumanClip.Hover).level, Is.InRange(-1.7f, -1.2f), "hovering: lifted out of the treading-water depth");
            Assert.That(Settings(HumanClip.FlyForward).level, Is.InRange(-1.3f, -0.6f), "flying forward: lifted to the middle of the capsule");
        }

        [Test]
        public void SwimmingUndoesExactlyTheClipsOffsets()
        {
            // HumanAnimationPlayer lowers the swimming figure by these (FlightClipLevels warns when they drift).
            Assert.That(HumanAnimationPlayer.HoverClipLevel, Is.EqualTo(Settings(HumanClip.Hover).level).Within(1e-5f));
            Assert.That(HumanAnimationPlayer.FlyForwardClipLevel, Is.EqualTo(Settings(HumanClip.FlyForward).level).Within(1e-5f));
        }

        [Test]
        public void SwimmingClipsShareTheFlyingAssets()
        {
            Assert.That(HumanAnimationPlayer.AssetName(HumanClip.SwimIdle), Is.EqualTo("Hover"));
            Assert.That(HumanAnimationPlayer.AssetName(HumanClip.SwimForward), Is.EqualTo("FlyForward"));
        }

        [Test]
        public void InWaterAnAvatarSwimsUnlessItFlies()
        {
            var t = HumanAnimationLogic.Choose("walk", new Vector3(0f, 0f, 1.5f), inWater: true);
            Assert.That(t.Swim, Is.EqualTo(1f).Within(1e-5f), "a bot walking in the sea swims");
            Assert.That(t.SwimForward, Is.GreaterThan(0.9f));
            t = HumanAnimationLogic.Choose("idle", Vector3.zero, inWater: true);
            Assert.That(t.SwimIdle, Is.EqualTo(1f).Within(1e-5f), "treading water");
            t = HumanAnimationLogic.Choose("fly", Vector3.zero, inWater: true);
            Assert.That(t.Swim, Is.EqualTo(0f), "flying over the water");
            Assert.That(t.Hover, Is.EqualTo(1f).Within(1e-5f));
            t = HumanAnimationLogic.Choose("swim", Vector3.zero);
            Assert.That(t.SwimIdle, Is.EqualTo(1f).Within(1e-5f), "the hint alone");
            t = HumanAnimationLogic.Choose("walk", new Vector3(0f, 0f, 1.5f));
            Assert.That(t.Swim, Is.EqualTo(0f), "on land");
        }

        [Test]
        public void GroundClipsAreNotOffset()
        {
            foreach (var clip in new[] { HumanClip.Idle, HumanClip.Walk, HumanClip.Jog, HumanClip.Sprint, HumanClip.Fall })
                Assert.That(Settings(clip).level, Is.EqualTo(0f), clip.ToString());
        }
    }
}
