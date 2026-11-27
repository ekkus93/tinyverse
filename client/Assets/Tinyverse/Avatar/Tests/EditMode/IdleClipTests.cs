using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    /// <summary>The standing idle clip (written by Editor/IdleClipAuthor.cs) loops without a seam.</summary>
    public class IdleClipTests
    {
        private static AnimationClip Idle()
        {
            var clip = Resources.Load<AnimationClip>(HumanCatalog.AnimationsFolder + HumanClip.Idle);
            Assert.That(clip, Is.Not.Null);
            return clip;
        }

        [Test]
        public void IsALoopingHumanoidClipWithFootGoals()
        {
            var clip = Idle();
            Assert.That(clip.humanMotion, Is.True);
            Assert.That(clip.isLooping, Is.True);
            Assert.That(clip.length, Is.InRange(4f, 12f), "slow breathing and weight shift");
            var names = AnimationUtility.GetCurveBindings(clip).Select(b => b.propertyName).ToList();
            foreach (var n in new[] { "RootT.y", "RootQ.w", "LeftFootT.x", "RightFootQ.w", "Left Arm Down-Up", "LeftHand.Index.1 Stretched" })
                Assert.That(names, Does.Contain(n));
        }

        [Test]
        public void FirstAndLastPoseMatch()
        {
            var clip = Idle();
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var c = AnimationUtility.GetEditorCurve(clip, b);
                var first = c.keys[0];
                var last = c.keys[c.length - 1];
                Assert.That(last.time, Is.EqualTo(clip.length).Within(1e-4f), b.propertyName);
                Assert.That(last.value, Is.EqualTo(first.value).Within(1e-4f), $"{b.propertyName}: value at the seam");
                Assert.That(last.outTangent, Is.EqualTo(first.inTangent).Within(2e-3f), $"{b.propertyName}: speed at the seam");
            }
        }

        [Test]
        public void MovesOnlyALittle()
        {
            // A calm idle: no muscle moves by more than a small amount over the loop, and the body barely travels.
            var clip = Idle();
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var c = AnimationUtility.GetEditorCurve(clip, b);
                float range = c.keys.Max(k => k.value) - c.keys.Min(k => k.value);
                float limit = b.propertyName.StartsWith("RootT") ? 0.04f : b.propertyName.Contains("Foot") && b.propertyName.Contains("T.") ? 0.04f : 0.1f;
                Assert.That(range, Is.LessThanOrEqualTo(limit), $"{b.propertyName} moves {range:0.000}");
            }
        }
    }
}
