using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatars.Tests
{
    public class HumanAvatarTests
    {
        private AvatarTestScene scene;

        [SetUp]
        public void SetUp() => scene = new AvatarTestScene();

        [TearDown]
        public void TearDown() => scene.Dispose();

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

        private static Transform Bone(HumanFigure figure, string name) =>
            figure.GetComponentsInChildren<Transform>().First(t => t.name == name);

        [UnityTest]
        public IEnumerator PresetsSelectHumanModelsAndUnknownFallsBack()
        {
            var remote = scene.Remote(1, "Pat");
            var visual = (HumanAvatarVisual)remote.Visual;
            Assert.That(visual.Figure, Is.Not.Null, "an empty preset is the default human");
            Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.DefaultPreset));
            Assert.That(visual.Placeholder, Is.Null);

            remote.SetLook(Look("feminine"));
            yield return null;
            Assert.That(visual.Preset.Id, Is.EqualTo(HumanCatalog.Feminine));
            Assert.That(visual.Figure.Animator, Is.Not.Null);
            Assert.That(visual.Figure.Animator.isHuman, Is.True);
            Assert.That(visual.Figure.Renderers.Count(), Is.GreaterThanOrEqualTo(6), "clothes, head, eyes, eyebrows, hair");
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
        }

        [UnityTest]
        public IEnumerator ColoursAndHairFollowTheLook()
        {
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
            AssertColor(look.SkinColor, f.ColorOf(HumanPart.Skin));
            AssertColor(look.HairColor, f.ColorOf(HumanPart.Hair));
            AssertColor(look.TopColor, f.ColorOf(HumanPart.Top));
            AssertColor(look.BottomColor, f.ColorOf(HumanPart.Bottom));
            AssertColor(HumanFigure.ShoeColor, f.ColorOf(HumanPart.Shoes));
            Assert.That(f.HairModels, Is.EqualTo(new[] { "Hair_Buns" }));

            // The top's material is what the sleeves and the shirt draw with.
            var top = f.Renderers.Where(r => r.name.Contains("_Body") || r.name.Contains("_Arms"))
                .SelectMany(r => r.sharedMaterials).Where(m => (m.GetColor("_BaseColor") - look.TopColor).maxColorComponent < 2e-3f && (look.TopColor - m.GetColor("_BaseColor")).maxColorComponent < 2e-3f);
            Assert.That(top.Count(), Is.EqualTo(2));

            look.HairStyle = "bald";
            remote.SetLook(look);
            Assert.That(f.HairModels, Is.Empty);
            look.HairStyle = "beard";
            look.TopColor = Color.white;
            remote.SetLook(look);
            Assert.That(f, Is.SameAs(visual.Figure), "colours and hair change in place");
            Assert.That(f.HairModels, Is.EqualTo(new[] { "Hair_SimpleParted", "Hair_Beard" }));
            AssertColor(Color.white, f.ColorOf(HumanPart.Top));

            // Unset colours are the defaults, never black.
            remote.SetLook(new AvatarLook { BodyPreset = "feminine" });
            AssertColor(AvatarLook.Default.SkinColor, f.ColorOf(HumanPart.Skin));
        }

        [UnityTest]
        public IEnumerator StandsOnTheGroundFacingForward()
        {
            foreach (var preset in new[] { "masculine", "feminine" })
            {
                var remote = scene.Remote(3, "Sam");
                remote.SetLook(Look(preset));
                remote.SnapTo(new AvatarPose(new Vector3(5f, 0f, 2f), 90f));
                for (int i = 0; i < 10; i++)
                {
                    remote.Visual.UpdatePose(remote.CurrentPose, AvatarTestScene.Dt);
                    yield return null;
                }
                var f = ((HumanAvatarVisual)remote.Visual).Figure;
                float lowest = Mathf.Min(Bone(f, "ball_l").position.y, Bone(f, "ball_r").position.y);
                Assert.That(lowest, Is.InRange(-0.02f, 0.12f), $"{preset}: feet on the ground");
                Assert.That(f.Height, Is.InRange(1.6f, 1.95f), $"{preset}: a person's height");
                Assert.That(Bone(f, "Head").position.y, Is.InRange(1.4f, 1.8f), $"{preset}: head height");

                // Yaw 90 faces +X: the left foot is on the avatar's left (+Z side seen from above is left when facing +X).
                var right = remote.transform.right;
                float side = Vector3.Dot(Bone(f, "foot_l").position - remote.transform.position, right);
                Assert.That(side, Is.LessThan(0f), $"{preset}: left foot on the left, so the figure faces forward");
                Object.Destroy(remote.gameObject);
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator AnimationFollowsSpeedAndMode()
        {
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
            var hipsIdle = Bone(visual.Figure, "pelvis").localPosition;
            Assert.That(Play("walk", 1.2f), Is.EqualTo(HumanClip.Walk));
            Assert.That(Play("walk", 4f), Is.EqualTo(HumanClip.Jog));
            Assert.That(Play("run", 8f), Is.EqualTo(HumanClip.Sprint));
            Assert.That(Play("fly", 0f), Is.EqualTo(HumanClip.Hover));
            Assert.That(Play("fly", 14f), Is.EqualTo(HumanClip.FlyForward));
            Assert.That(Play("fall", 0f), Is.EqualTo(HumanClip.Fall));
            yield return null;

            // The pose really changes over time (the skeleton is animated, not frozen).
            var thigh = Bone(visual.Figure, "thigh_l");
            var pose2 = new AvatarPose(Vector3.zero, 0f, new Vector3(0f, 0f, 4f), MoveKind.Walk, "walk");
            visual.UpdatePose(pose2, AvatarTestScene.Dt);
            var a = thigh.localRotation;
            for (int i = 0; i < 8; i++) visual.UpdatePose(pose2, AvatarTestScene.Dt);
            Assert.That(Quaternion.Angle(a, thigh.localRotation), Is.GreaterThan(5f), "legs swing while jogging");
            Assert.That(hipsIdle, Is.Not.EqualTo(Vector3.zero));
        }

        [UnityTest]
        public IEnumerator HidesForFirstPerson()
        {
            var remote = scene.Remote(5, "Ghost");
            var visual = (HumanAvatarVisual)remote.Visual;
            visual.SetVisible(false);
            Assert.That(visual.Figure.Renderers.All(r => !r.enabled), Is.True);
            remote.SetLook(Look("feminine", "long"));
            yield return null;
            Assert.That(visual.Figure.Renderers.All(r => !r.enabled), Is.True, "a new figure and hair stay hidden");
            visual.SetVisible(true);
            Assert.That(visual.Figure.Renderers.All(r => r.enabled), Is.True);
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
