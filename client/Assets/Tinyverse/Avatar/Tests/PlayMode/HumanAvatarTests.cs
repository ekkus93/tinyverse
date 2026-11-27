using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    public class HumanAvatarTests
    {
        private static readonly AvatarStyle[] Styles = { AvatarStyle.Modern, AvatarStyle.Classic };

        private AvatarTestScene scene;
        private AvatarStyle styleBefore;

        [SetUp]
        public void SetUp()
        {
            scene = new AvatarTestScene();
            styleBefore = HumanCatalog.Style;
        }

        [TearDown]
        public void TearDown()
        {
            scene.Dispose();
            HumanCatalog.Style = styleBefore;
        }

        private static AvatarLook Look(string preset, string hair = "")
        {
            var look = AvatarLook.Default;
            look.BodyPreset = preset;
            look.HairStyle = hair;
            return look;
        }

        private static void AssertColor(Color expected, Color actual, string what = "")
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(2e-3f), what);
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(2e-3f), what);
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(2e-3f), what);
        }

        private static Transform Bone(HumanFigure figure, HumanBodyBones bone) => figure.Animator.GetBoneTransform(bone);

        [UnityTest]
        public IEnumerator PresetsSelectHumanModelsAndUnknownFallsBack()
        {
            foreach (var style in Styles)
            {
                HumanCatalog.Style = style;
                var remote = scene.Remote(1, "Pat");
                var visual = (HumanAvatarVisual)remote.Visual;
                Assert.That(visual.Figure, Is.Not.Null, $"{style}: an empty preset is the default human");
                Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.DefaultPreset));
                Assert.That(visual.Preset.Style, Is.EqualTo(style));
                Assert.That(visual.Placeholder, Is.Null);

                remote.SetLook(Look("feminine"));
                yield return null;
                Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.Feminine));
                Assert.That(visual.Figure.Animator, Is.Not.Null);
                Assert.That(visual.Figure.Animator.isHuman, Is.True);
                int parts = style == AvatarStyle.Modern ? 4 : 6; // modern: body, top, bottom, hair; classic: more pieces
                Assert.That(visual.Figure.Renderers.Count(), Is.GreaterThanOrEqualTo(parts), $"{style}: clothes, head, hair");
                Assert.That(visual.GetComponentsInChildren<HumanFigure>().Length, Is.EqualTo(1));

                remote.SetLook(Look("masculine"));
                yield return null;
                Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.Masculine));
                Assert.That(visual.GetComponentsInChildren<HumanFigure>().Length, Is.EqualTo(1), "the old figure is gone");

                remote.SetLook(Look("dragon"));
                yield return null;
                Assert.That(visual.Figure, Is.Null);
                Assert.That(visual.Placeholder, Is.Not.Null, "unknown preset: placeholder");
                Assert.That(visual.GetComponentsInChildren<HumanFigure>().Length, Is.EqualTo(0));

                remote.SetLook(Look("female"));
                yield return null;
                Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.Feminine), "alias");
                Assert.That(visual.Placeholder, Is.Null);
                Assert.That(visual.NameTag.Text, Is.EqualTo("Pat"), "the tag survives a swap");
                Assert.That(visual.NameTag.AnchorHeight, Is.EqualTo(visual.Height + 0.22f).Within(1e-3f));

                // A modern-only build draws the classic figure of the same gender in the classic style.
                remote.SetLook(Look(HumanCatalog.MasculineHeavy));
                yield return null;
                Assert.That(visual.Figure, Is.Not.Null);
                Assert.That(visual.Preset.Id, Is.EqualTo(style == AvatarStyle.Modern ? HumanCatalog.MasculineHeavy : HumanCatalog.Masculine));
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator EveryModernPresetAndHairStyleBuilds()
        {
            HumanCatalog.Style = AvatarStyle.Modern;
            var remote = scene.Remote(6, "Model");
            var visual = (HumanAvatarVisual)remote.Visual;
            foreach (var preset in HumanCatalog.ModernPresets)
            {
                foreach (var hair in HumanCatalog.HairStyles)
                {
                    remote.SetLook(Look(preset.Id, hair.Id));
                    var f = visual.Figure;
                    Assert.That(f, Is.Not.Null, $"{preset.Id}");
                    Assert.That(f.Preset, Is.SameAs(preset));
                    Assert.That(f.Hair, Is.SameAs(hair));
                    Assert.That(f.HairModels, Is.EqualTo(new[] { $"Hair_{preset.Id}_{hair.Id}" }));
                    var renderers = f.Renderers.ToList();
                    Assert.That(renderers.Count, Is.EqualTo(4), $"{preset.Id} {hair.Id}: one skinned mesh per material");
                    var mats = renderers.SelectMany(r => r.sharedMaterials).Distinct().ToList();
                    Assert.That(mats.Count, Is.LessThanOrEqualTo(4), $"{preset.Id} {hair.Id}: materials");
                    Assert.That(mats.All(m => m != null && m.shader.name == "Universal Render Pipeline/Lit"), Is.True, "plain URP Lit");
                    int tris = renderers.Sum(r => Triangles(((SkinnedMeshRenderer)r).sharedMesh));
                    Assert.That(tris, Is.LessThanOrEqualTo(15000), $"{preset.Id} {hair.Id}: {tris} triangles");
                    Assert.That(f.Height, Is.InRange(1.55f, 1.95f), $"{preset.Id} {hair.Id}: height");
                }
                yield return null;
            }
        }

        private static int Triangles(Mesh m)
        {
            long n = 0;
            for (int s = 0; s < m.subMeshCount; s++) n += m.GetIndexCount(s);
            return (int)(n / 3);
        }

        [UnityTest]
        public IEnumerator ColoursAndHairFollowTheLook()
        {
            foreach (var style in Styles)
            {
                HumanCatalog.Style = style;
                var remote = scene.Remote(2, "Dee");
                var visual = (HumanAvatarVisual)remote.Visual;
                var look = Look("feminine", "buns");
                look.SkinColor = new Color(0.4f, 0.3f, 0.2f);
                look.HairColor = new Color(0.9f, 0.1f, 0.1f);
                look.TopColor = new Color(0.1f, 0.8f, 0.3f);
                look.BottomColor = new Color(0.2f, 0.2f, 0.9f);
                remote.SetLook(look);
                yield return null;
                var f = visual.Figure;
                AssertColor(look.SkinColor, f.ColorOf(HumanPart.Skin), $"{style} skin");
                AssertColor(look.HairColor, f.ColorOf(HumanPart.Hair), $"{style} hair");
                AssertColor(look.TopColor, f.ColorOf(HumanPart.Top), $"{style} top");
                AssertColor(look.BottomColor, f.ColorOf(HumanPart.Bottom), $"{style} bottom");
                if (style == AvatarStyle.Classic)
                {
                    AssertColor(HumanFigure.ShoeColor, f.ColorOf(HumanPart.Shoes));
                    Assert.That(f.HairModels, Is.EqualTo(new[] { "Hair_Buns" }));
                    // The top's material is what the sleeves and the shirt draw with.
                    var top = f.Renderers.Where(r => r.name.Contains("_Body") || r.name.Contains("_Arms"))
                        .SelectMany(r => r.sharedMaterials).Where(m => (m.GetColor("_BaseColor") - look.TopColor).maxColorComponent < 2e-3f && (look.TopColor - m.GetColor("_BaseColor")).maxColorComponent < 2e-3f);
                    Assert.That(top.Count(), Is.EqualTo(2));
                }
                else
                {
                    // The shoes share the trousers' material; their texels are dark, so they read as dark shoes.
                    AssertColor(look.BottomColor, f.ColorOf(HumanPart.Shoes));
                    Assert.That(f.HairModels, Is.EqualTo(new[] { "Hair_feminine_buns" }));
                    var byName = f.Renderers.ToDictionary(r => r.name, r => r.sharedMaterial);
                    AssertColor(look.TopColor, byName["Top"].GetColor("_BaseColor"), "the top mesh");
                    AssertColor(look.BottomColor, byName["Bottom"].GetColor("_BaseColor"), "the trousers mesh");
                    AssertColor(look.SkinColor, byName["Body"].GetColor("_BaseColor"), "the body mesh");
                    AssertColor(look.HairColor, byName["Hair"].GetColor("_BaseColor"), "the hair mesh");
                    Assert.That(byName["Body"].IsKeywordEnabled("_EMISSION"), Is.True, "the eyes are drawn by emission");
                }

                look.HairStyle = "bald";
                remote.SetLook(look);
                if (style == AvatarStyle.Classic) Assert.That(f.HairModels, Is.Empty);
                else Assert.That(f.HairModels, Is.EqualTo(new[] { "Hair_feminine_bald" }), "eyebrows and eyelashes stay");
                look.HairStyle = "beard";
                look.TopColor = Color.white;
                remote.SetLook(look);
                Assert.That(f, Is.SameAs(visual.Figure), "colours and hair change in place");
                Assert.That(f.HairModels, Is.EqualTo(style == AvatarStyle.Classic ? new[] { "Hair_SimpleParted", "Hair_Beard" } : new[] { "Hair_feminine_beard" }));
                AssertColor(Color.white, f.ColorOf(HumanPart.Top));
                AssertColor(look.HairColor, f.ColorOf(HumanPart.Hair), "a new hair style keeps the colour");

                // Unset colours are the defaults, never black.
                remote.SetLook(new AvatarLook { BodyPreset = "feminine" });
                AssertColor(AvatarLook.Default.SkinColor, f.ColorOf(HumanPart.Skin));
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator StandsOnTheGroundFacingForward()
        {
            foreach (var style in Styles)
            {
                HumanCatalog.Style = style;
                foreach (var preset in HumanCatalog.Presets)
                {
                    var remote = scene.Remote(3, "Sam");
                    remote.SetLook(Look(preset.Id));
                    remote.SnapTo(new AvatarPose(new Vector3(5f, 0f, 2f), 90f));
                    for (int i = 0; i < 10; i++)
                    {
                        remote.Visual.UpdatePose(remote.CurrentPose, AvatarTestScene.Dt);
                        yield return null;
                    }
                    var f = ((HumanAvatarVisual)remote.Visual).Figure;
                    string what = $"{style} {preset.Id}";
                    float lowest = Mathf.Min(Bone(f, HumanBodyBones.LeftToes).position.y, Bone(f, HumanBodyBones.RightToes).position.y);
                    Assert.That(lowest, Is.InRange(-0.02f, 0.12f), $"{what}: feet on the ground");
                    Assert.That(f.Height, Is.InRange(1.6f, 1.95f), $"{what}: a person's height");
                    Assert.That(Bone(f, HumanBodyBones.Head).position.y, Is.InRange(1.4f, 1.8f), $"{what}: head height");

                    // Yaw 90 faces +X: the left foot is on the avatar's left.
                    var right = remote.transform.right;
                    var forward = remote.transform.forward;
                    float side = Vector3.Dot(Bone(f, HumanBodyBones.LeftFoot).position - remote.transform.position, right);
                    Assert.That(side, Is.LessThan(0f), $"{what}: left foot on the left, so the figure faces forward");
                    var toes = Bone(f, HumanBodyBones.LeftToes).position - Bone(f, HumanBodyBones.LeftFoot).position;
                    Assert.That(Vector3.Dot(toes, forward), Is.GreaterThan(0f), $"{what}: toes point forward");
                    Object.Destroy(remote.gameObject);
                    yield return null;
                }
            }
        }

        [UnityTest]
        public IEnumerator ModernFeetStayOnTheGroundWhileWalking()
        {
            HumanCatalog.Style = AvatarStyle.Modern;
            foreach (var preset in HumanCatalog.ModernPresets)
            {
                var remote = scene.Remote(7, "Walker");
                remote.SetLook(Look(preset.Id));
                var visual = (HumanAvatarVisual)remote.Visual;
                var f = visual.Figure;
                foreach (var speed in new[] { 0f, 1.2f, 4f, 8f })
                {
                    var pose = new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, speed), MoveKind.Walk, speed > 6f ? "run" : speed > 0f ? "walk" : "idle");
                    float min = float.MaxValue, max = float.MinValue;
                    for (int i = 0; i < 120; i++)
                    {
                        visual.UpdatePose(pose, AvatarTestScene.Dt);
                        if (i < 30) continue;
                        float low = Mathf.Min(Bone(f, HumanBodyBones.LeftToes).position.y, Bone(f, HumanBodyBones.RightToes).position.y);
                        min = Mathf.Min(min, low);
                        max = Mathf.Max(max, low);
                    }
                    Assert.That(min, Is.InRange(-0.04f, 0.1f), $"{preset.Id} at {speed} m/s: the lower foot touches the ground");
                    Assert.That(max, Is.LessThan(0.35f), $"{preset.Id} at {speed} m/s: never both feet in the air for long");
                }
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator AnimationFollowsSpeedAndMode()
        {
            foreach (var style in Styles)
            {
                HumanCatalog.Style = style;
                var remote = scene.Remote(4, "Run");
                var visual = (HumanAvatarVisual)remote.Visual;
                var player = visual.Figure.Player;
                Assert.That(player.IsValid, Is.True);

                HumanClip Play(string hint, float speed)
                {
                    var pose = new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, speed), MoveKind.Walk, hint);
                    for (int i = 0; i < 90; i++) visual.UpdatePose(pose, AvatarTestScene.Dt);
                    int best = 0;
                    for (int i = 1; i < player.Weights.Count; i++) if (player.Weights[i] > player.Weights[best]) best = i;
                    return (HumanClip)best;
                }

                Assert.That(Play("idle", 0f), Is.EqualTo(HumanClip.Idle));
                var hipsIdle = Bone(visual.Figure, HumanBodyBones.Hips).localPosition;
                Assert.That(Play("walk", 1.2f), Is.EqualTo(HumanClip.Walk));
                Assert.That(Play("walk", 4f), Is.EqualTo(HumanClip.Jog));
                Assert.That(Play("run", 8f), Is.EqualTo(HumanClip.Sprint));
                Assert.That(Play("fly", 0f), Is.EqualTo(HumanClip.Hover));
                Assert.That(Play("fly", 14f), Is.EqualTo(HumanClip.FlyForward));
                Assert.That(Play("fall", 0f), Is.EqualTo(HumanClip.Fall));
                yield return null;

                // The pose really changes over time (the skeleton is animated, not frozen).
                var thigh = Bone(visual.Figure, HumanBodyBones.LeftUpperLeg);
                var pose2 = new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, 4f), MoveKind.Walk, "walk");
                visual.UpdatePose(pose2, AvatarTestScene.Dt);
                var a = thigh.localRotation;
                for (int i = 0; i < 8; i++) visual.UpdatePose(pose2, AvatarTestScene.Dt);
                Assert.That(Quaternion.Angle(a, thigh.localRotation), Is.GreaterThan(5f), $"{style}: legs swing while jogging");
                Assert.That(hipsIdle, Is.Not.EqualTo(Vector3.zero));
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator HidesForFirstPerson()
        {
            foreach (var style in Styles)
            {
                HumanCatalog.Style = style;
                var remote = scene.Remote(5, "Ghost");
                var visual = (HumanAvatarVisual)remote.Visual;
                visual.SetVisible(false);
                Assert.That(visual.Figure.Renderers.All(r => !r.enabled), Is.True);
                remote.SetLook(Look("feminine", "long"));
                yield return null;
                Assert.That(visual.Figure.Renderers.All(r => !r.enabled), Is.True, $"{style}: a new figure and hair stay hidden");
                visual.SetVisible(true);
                Assert.That(visual.Figure.Renderers.All(r => r.enabled), Is.True);
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator CloseTagsAreSeparatedOnScreen()
        {
            var cam = scene.Track(new GameObject("TestCamera").AddComponent<Camera>());
            cam.transform.position = new Vector3(0f, 1.7f, -6f);
            cam.transform.rotation = Quaternion.identity;
            NameTag.ViewCamera = cam;
            var near = scene.Remote(10, "Alexandra Near");
            var far = scene.Remote(11, "Bartholomew Far");
            near.SnapTo(new AvatarPose(new Vector3(0f, 0f, 0f), 0f));
            far.SnapTo(new AvatarPose(new Vector3(0.3f, 0f, 1f), 0f));
            for (int i = 0; i < 30; i++) yield return null;

            var a = near.Visual.NameTag;
            var b = far.Visual.NameTag;
            Assert.That(a.IsShown && b.IsShown, Is.True);
            Assert.That(a.TryGetScreenRect(cam, out _, out _), Is.True);
            Assert.That(a.LayoutShiftPixels, Is.EqualTo(0f), "the nearest stays");
            Assert.That(b.LayoutShiftPixels, Is.GreaterThan(5f), "the farther one is lifted");
            var ra = ScreenRect(cam, a);
            var rb = ScreenRect(cam, b);
            Assert.That(ra.Overlaps(rb), Is.False, $"{ra} vs {rb}");

            // Apart again: the lift eases back to nothing.
            far.SnapTo(new AvatarPose(new Vector3(4f, 0f, 1f), 0f));
            for (int i = 0; i < 60; i++) yield return null;
            Assert.That(b.LayoutShiftPixels, Is.LessThan(0.5f));
        }

        private static Rect ScreenRect(Camera cam, NameTag tag)
        {
            var b = tag.NameText.GetComponent<MeshRenderer>().bounds;
            var min = cam.WorldToScreenPoint(b.min);
            var max = cam.WorldToScreenPoint(b.max);
            return Rect.MinMaxRect(Mathf.Min(min.x, max.x), Mathf.Min(min.y, max.y), Mathf.Max(min.x, max.x), Mathf.Max(min.y, max.y));
        }
    }
}
