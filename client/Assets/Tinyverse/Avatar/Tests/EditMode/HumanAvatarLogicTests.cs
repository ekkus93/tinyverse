using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Tinyverse.Avatars.Tests
{
    public class HumanCatalogTests
    {
        [TestCase("", HumanCatalog.Masculine)]
        [TestCase(null, HumanCatalog.Masculine)]
        [TestCase("masculine", HumanCatalog.Masculine)]
        [TestCase("Feminine", HumanCatalog.Feminine)]
        [TestCase(" female ", HumanCatalog.Feminine)]
        [TestCase("man", HumanCatalog.Masculine)]
        public void PresetNamesResolve(string name, string expected)
        {
            Assert.That(HumanCatalog.FindPreset(name)?.Id, Is.EqualTo(expected));
        }

        [TestCase("robot")]
        [TestCase("masculine2")]
        public void UnknownPresetIsNull(string name)
        {
            Assert.That(HumanCatalog.FindPreset(name), Is.Null, "unknown presets draw the placeholder");
        }

        [TestCase("masculine_heavy", AvatarStyle.Modern, "masculine_heavy")]
        [TestCase("Heavy", AvatarStyle.Modern, "masculine_heavy")]
        [TestCase("feminine_slim", AvatarStyle.Modern, "feminine_slim")]
        [TestCase("slim", AvatarStyle.Modern, "feminine_slim")]
        [TestCase("masculine_heavy", AvatarStyle.Classic, "masculine")]
        [TestCase("feminine_slim", AvatarStyle.Classic, "feminine")]
        [TestCase("female", AvatarStyle.Classic, "feminine")]
        [TestCase("", AvatarStyle.Classic, "masculine")]
        public void PresetsResolvePerStyle(string name, AvatarStyle style, string expected)
        {
            var p = HumanCatalog.FindPreset(name, style);
            Assert.That(p?.Id, Is.EqualTo(expected));
            Assert.That(p.Style, Is.EqualTo(style));
        }

        [Test]
        public void ModernIsTheDefaultStyleAndOffersMoreBuilds()
        {
            Assert.That(HumanCatalog.ParseStyle(new string[0]), Is.EqualTo(AvatarStyle.Modern));
            Assert.That(HumanCatalog.PresetsOf(AvatarStyle.Modern).Select(p => p.Id),
                Is.SupersetOf(new[] { HumanCatalog.Masculine, HumanCatalog.Feminine, HumanCatalog.MasculineHeavy, HumanCatalog.FeminineSlim }));
            Assert.That(HumanCatalog.PresetsOf(AvatarStyle.Classic).Select(p => p.Id), Is.EqualTo(new[] { HumanCatalog.Masculine, HumanCatalog.Feminine }));
            Assert.That(HumanCatalog.PresetsOf(AvatarStyle.Modern).All(p => p.Style == AvatarStyle.Modern), Is.True);
            Assert.That(HumanCatalog.FindPreset("robot", AvatarStyle.Modern), Is.Null);
            Assert.That(HumanCatalog.FindPreset("robot", AvatarStyle.Classic), Is.Null);
        }

        [TestCase(new[] { "tinyverse", "--avatar-style=classic" }, AvatarStyle.Classic)]
        [TestCase(new[] { "tinyverse", "--avatar-style", "Classic" }, AvatarStyle.Classic)]
        [TestCase(new[] { "tinyverse", "--avatar-style=modern" }, AvatarStyle.Modern)]
        [TestCase(new[] { "tinyverse", "--avatar-style=nonsense" }, AvatarStyle.Modern)]
        [TestCase(new[] { "tinyverse", "--avatar-style" }, AvatarStyle.Modern)]
        [TestCase(new[] { "tinyverse", "--connect", "ws://x", "--avatar-style=classic", "--name", "A" }, AvatarStyle.Classic)]
        [TestCase(new[] { "classic" }, AvatarStyle.Modern)]
        public void StyleComesFromTheCommandLine(string[] args, AvatarStyle expected)
        {
            Assert.That(HumanCatalog.ParseStyle(args), Is.EqualTo(expected));
        }

        [Test]
        public void ModernHairIsOneFittedModelPerPresetAndStyle()
        {
            var slim = HumanCatalog.FindPreset(HumanCatalog.FeminineSlim, AvatarStyle.Modern);
            Assert.That(HumanCatalog.FindHairStyle("", slim).Id, Is.EqualTo("long"));
            Assert.That(HumanCatalog.FindHairStyle("bald", slim).ModelsFor(slim), Is.EqualTo(new[] { "Hair_feminine_slim_bald" }), "eyebrows stay on a bald head");
            Assert.That(HumanCatalog.FindHairStyle("beard", slim).ModelsFor(slim), Is.EqualTo(new[] { "Hair_feminine_slim_beard" }));
            Assert.That(slim.Gender, Is.EqualTo(HumanCatalog.Feminine));
            Assert.That(slim.SkinMaterial, Is.EqualTo("Skin_feminine_slim"));
            Assert.That(slim.TopMaterial, Is.EqualTo("Top_feminine_slim"));
            Assert.That(slim.BottomMaterial, Is.EqualTo("Bottom_feminine_slim"));
        }

        [Test]
        public void HairStylesResolveWithPresetDefaults()
        {
            var masc = HumanCatalog.FindPreset(HumanCatalog.Masculine, AvatarStyle.Classic);
            var fem = HumanCatalog.FindPreset(HumanCatalog.Feminine, AvatarStyle.Classic);
            Assert.That(HumanCatalog.FindHairStyle("LONG", masc).Id, Is.EqualTo("long"));
            Assert.That(HumanCatalog.FindHairStyle("", masc).Id, Is.EqualTo(masc.DefaultHairStyle));
            Assert.That(HumanCatalog.FindHairStyle("mohawk", fem).Id, Is.EqualTo(fem.DefaultHairStyle));
            Assert.That(HumanCatalog.FindHairStyle("bald", fem).ModelsFor(fem), Is.Empty);
            Assert.That(HumanCatalog.FindHairStyle("buzzed", fem).ModelsFor(fem), Is.EqualTo(new[] { "Hair_BuzzedFemale" }));
            Assert.That(HumanCatalog.FindHairStyle("buzzed", masc).ModelsFor(masc), Is.EqualTo(new[] { "Hair_Buzzed" }));
            Assert.That(HumanCatalog.HairStyles.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(HumanCatalog.Presets.Count, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void UnsetColoursTakeDefaultsAndTopFollowsBody()
        {
            var d = AvatarLook.Default;
            var unset = HumanCatalog.ColorsFor(new AvatarLook());
            Assert.That(unset.Skin, Is.EqualTo(Opaque(d.SkinColor)));
            Assert.That(unset.Hair, Is.EqualTo(Opaque(d.HairColor)));
            Assert.That(unset.Top, Is.EqualTo(Opaque(d.TopColor)));
            Assert.That(unset.Bottom, Is.EqualTo(Opaque(d.BottomColor)));

            var bodyOnly = HumanCatalog.ColorsFor(new AvatarLook { BodyColor = new Color(0.1f, 0.2f, 0.3f, 0.5f) });
            Assert.That(bodyOnly.Top, Is.EqualTo(new Color(0.1f, 0.2f, 0.3f, 1f)), "the body colour dresses the top when no top is set");

            var look = new AvatarLook
            {
                BodyColor = Color.red,
                SkinColor = new Color(0.3f, 0.2f, 0.1f, 1f),
                HairColor = Color.white, // white is a real colour, not "unset"
                TopColor = new Color(0f, 0f, 0f, 1f), // opaque black is set
                BottomColor = new Color(0.5f, 0.5f, 0.9f, 0f),
            };
            var c = HumanCatalog.ColorsFor(look);
            Assert.That(c.Skin, Is.EqualTo(look.SkinColor));
            Assert.That(c.Hair, Is.EqualTo(Color.white));
            Assert.That(c.Top, Is.EqualTo(Color.black));
            Assert.That(c.Bottom, Is.EqualTo(new Color(0.5f, 0.5f, 0.9f, 1f)));
        }

        private static Color Opaque(Color c)
        {
            c.a = 1f;
            return c;
        }
    }

    public class HumanAnimationLogicTests
    {
        private static void AssertNormalised(HumanAnimationTargets t)
        {
            float sum = 0f;
            for (int i = 0; i < HumanAnimationTargets.ClipCount; i++)
            {
                Assert.That(t[(HumanClip)i], Is.InRange(0f, 1f));
                sum += t[(HumanClip)i];
            }
            Assert.That(sum, Is.EqualTo(1f).Within(1e-4f), t.ToString());
        }

        [TestCase("idle", 0f, HumanClip.Idle)]
        [TestCase("", 0f, HumanClip.Idle)]
        [TestCase(null, 0f, HumanClip.Idle)]
        [TestCase("walk", 1.2f, HumanClip.Walk)]
        [TestCase("walk", 4f, HumanClip.Jog)]
        [TestCase("run", 8f, HumanClip.Sprint)]
        [TestCase("fly", 0f, HumanClip.Hover)]
        [TestCase("fly", 0.5f, HumanClip.Hover)]
        [TestCase("fly", 12f, HumanClip.FlyForward)]
        [TestCase("fall", 0f, HumanClip.Fall)]
        [TestCase("fall", 6f, HumanClip.Fall)]
        [TestCase("dance", 0f, HumanClip.Idle)]
        [TestCase("idle", 4f, HumanClip.Jog)] // a remote avatar's stale hint: its velocity decides
        public void ChoosesTheClipForTheState(string hint, float speed, HumanClip expected)
        {
            var t = HumanAnimationLogic.Choose(hint, new Vector3(0f, 0f, speed));
            Assert.That(t.Dominant, Is.EqualTo(expected), t.ToString());
            AssertNormalised(t);
        }

        [Test]
        public void VerticalSpeedAloneDoesNotWalk()
        {
            var t = HumanAnimationLogic.Choose("walk", new Vector3(0f, -3f, 0f));
            Assert.That(t.Dominant, Is.EqualTo(HumanClip.Idle));
        }

        [Test]
        public void BlendsSmoothlyWithSpeed()
        {
            float lastWalkPlusJog = -1f;
            HumanAnimationTargets prev = default;
            for (float s = 0f; s <= 10f; s += 0.05f)
            {
                var t = HumanAnimationLogic.Choose("walk", new Vector3(s * 0.6f, 0f, s * 0.8f));
                AssertNormalised(t);
                if (s > 0f)
                {
                    Assert.That(Mathf.Abs(t.Idle - prev.Idle), Is.LessThan(0.1f), $"at {s}");
                    Assert.That(Mathf.Abs(t.Walk - prev.Walk), Is.LessThan(0.1f), $"at {s}");
                    Assert.That(Mathf.Abs(t.Jog - prev.Jog), Is.LessThan(0.1f), $"at {s}");
                    Assert.That(Mathf.Abs(t.Sprint - prev.Sprint), Is.LessThan(0.1f), $"at {s}");
                }
                prev = t;
                lastWalkPlusJog = t.Walk + t.Jog;
            }
            Assert.That(lastWalkPlusJog, Is.EqualTo(0f));
        }

        [Test]
        public void StrideRatesFollowGroundSpeed()
        {
            var jog = HumanAnimationLogic.Choose("walk", new Vector3(0f, 0f, 4f));
            Assert.That(jog.JogRate, Is.EqualTo(4f / HumanAnimationLogic.JogClipSpeed).Within(1e-3f));
            var sprint = HumanAnimationLogic.Choose("run", new Vector3(0f, 0f, 8f));
            Assert.That(sprint.SprintRate, Is.EqualTo(8f / HumanAnimationLogic.SprintClipSpeed).Within(1e-3f));
            var fast = HumanAnimationLogic.Choose("run", new Vector3(0f, 0f, 40f));
            Assert.That(fast.SprintRate, Is.LessThanOrEqualTo(1.35f), "capped");
        }

        [Test]
        public void BadVelocityIsIdle()
        {
            var t = HumanAnimationLogic.Choose("walk", new Vector3(float.NaN, 0f, float.PositiveInfinity));
            Assert.That(t.Dominant, Is.EqualTo(HumanClip.Idle));
            AssertNormalised(t);
        }
    }

    public class NameTagLayoutTests
    {
        private static Rect R(float x, float y, float w = 100f, float h = 20f) => new Rect(x, y, w, h);

        [Test]
        public void SeparateTagsStayPut()
        {
            var shifts = NameTagLayout.Resolve(new List<Rect> { R(0, 0), R(200, 0), R(0, 100) });
            Assert.That(shifts, Is.EqualTo(new[] { 0f, 0f, 0f }));
        }

        [Test]
        public void FartherTagMovesAboveTheNearerOne()
        {
            var shifts = NameTagLayout.Resolve(new List<Rect> { R(0, 100), R(30, 105) }, gap: 2f);
            Assert.That(shifts[0], Is.EqualTo(0f), "the nearest never moves");
            Assert.That(105f + shifts[1], Is.EqualTo(100f + 20f + 2f).Within(1e-4f));
        }

        [Test]
        public void AFarTagBelowANearOneAlsoMovesUpOverIt()
        {
            var shifts = NameTagLayout.Resolve(new List<Rect> { R(0, 100), R(10, 95) }, gap: 2f);
            Assert.That(95f + shifts[1], Is.GreaterThanOrEqualTo(122f - 1e-4f));
        }

        [Test]
        public void ACrowdStacksWithoutOverlap()
        {
            var rects = new List<Rect>();
            for (int i = 0; i < 8; i++) rects.Add(R(i * 7f, 200f + (i % 3) * 4f, 90f, 18f));
            var shifts = NameTagLayout.Resolve(rects, gap: 2f);
            for (int i = 0; i < rects.Count; i++)
            {
                Assert.That(shifts[i], Is.GreaterThanOrEqualTo(0f));
                var a = new Rect(rects[i].x, rects[i].y + shifts[i], rects[i].width, rects[i].height);
                for (int j = 0; j < i; j++)
                {
                    var b = new Rect(rects[j].x, rects[j].y + shifts[j], rects[j].width, rects[j].height);
                    Assert.That(a.Overlaps(b), Is.False, $"{i} overlaps {j}");
                }
            }
        }

        [Test]
        public void SideBySideTagsAreLeftAlone()
        {
            var shifts = NameTagLayout.Resolve(new List<Rect> { R(0, 0), R(101, 3) });
            Assert.That(shifts, Is.EqualTo(new[] { 0f, 0f }));
        }
    }
}
