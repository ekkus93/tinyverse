using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class AppearancePanelTests : UITestBase
    {
        AppearancePanel panel;
        readonly List<AppearanceChoice> changed = new List<AppearanceChoice>();
        readonly List<AppearanceChoice> saved = new List<AppearanceChoice>();
        readonly List<AppearanceChoice> cancelled = new List<AppearanceChoice>();
        int openRequests;

        static readonly AppearanceChoice Start = new AppearanceChoice
        {
            BodyPreset = "masculine",
            HairStyle = "short",
            Skin = AppearancePanel.SkinTones[2],
            Hair = AppearancePanel.HairColors[1],
            Top = AppearancePanel.ClothColors[4],
            Bottom = AppearancePanel.ClothColors[3],
        };

        [UnitySetUp]
        public IEnumerator Attach()
        {
            changed.Clear();
            saved.Clear();
            cancelled.Clear();
            openRequests = 0;
            panel = AppearancePanel.Attach(UI);
            panel.SetOptions(
                new[] { new AppearanceOption("masculine", "Masculine"), new AppearanceOption("feminine", "Feminine") },
                new[] { new AppearanceOption("short", "Short"), new AppearanceOption("long", "Long"), new AppearanceOption("bald", "Bald") });
            panel.Changed += changed.Add;
            panel.Saved += saved.Add;
            panel.Cancelled += cancelled.Add;
            panel.OpenRequested += () => openRequests++;
            UI.ShowScreen(UIScreen.Hud);
            yield return null;
        }

        [UnityTest]
        public IEnumerator MenuButtonRequestsTheEditor()
        {
            Assert.That(panel.IsVisible, Is.False);
            UI.OpenMenu();
            yield return null;
            Assert.That(panel.MenuButton.panel, Is.Not.Null, "the button is in the menu");
            Assert.That(UI.Menu.Root.Q<Button>("menuAppearance"), Is.SameAs(panel.MenuButton));
            Click(panel.MenuButton);
            Assert.That(openRequests, Is.EqualTo(1));
            Assert.That(UI.Menu.IsVisible, Is.False, "the menu closes");
        }

        [UnityTest]
        public IEnumerator EditsPreviewLiveAndDoneSaves()
        {
            panel.Open(Start);
            yield return null;
            Assert.That(Shown(panel.Root), Is.True);
            Assert.That(panel.BodyOptionCount, Is.EqualTo(2));
            Assert.That(panel.HairOptionCount, Is.EqualTo(3));

            panel.PickBody("feminine");
            panel.PickHairStyle("long");
            panel.PickSkin(7);
            panel.PickHairColor(5);
            panel.PickTop(10);
            panel.PickBottom(0);
            Assert.That(changed.Count, Is.EqualTo(6), "every pick previews");
            var last = changed[changed.Count - 1];
            Assert.That(last.BodyPreset, Is.EqualTo("feminine"));
            Assert.That(last.HairStyle, Is.EqualTo("long"));
            Assert.That(last.Skin, Is.EqualTo(AppearancePanel.SkinTones[7]));
            Assert.That(last.Hair, Is.EqualTo(AppearancePanel.HairColors[5]));
            Assert.That(last.Top, Is.EqualTo(AppearancePanel.ClothColors[10]));
            Assert.That(last.Bottom, Is.EqualTo(AppearancePanel.ClothColors[0]));
            Assert.That(saved, Is.Empty, "nothing is kept before Done");

            Click(UIUtil.Require<Button>(panel.Root, "appearanceDone"));
            Assert.That(panel.IsVisible, Is.False);
            Assert.That(saved.Count, Is.EqualTo(1));
            Assert.That(saved[0].BodyPreset, Is.EqualTo("feminine"));
            Assert.That(saved[0].Top, Is.EqualTo(AppearancePanel.ClothColors[10]));
        }

        [UnityTest]
        public IEnumerator CancelAndEscapeRestoreTheOriginal()
        {
            panel.Open(Start);
            panel.PickBody("feminine");
            Click(UIUtil.Require<Button>(panel.Root, "appearanceCancel"));
            Assert.That(cancelled.Count, Is.EqualTo(1));
            Assert.That(cancelled[0].BodyPreset, Is.EqualTo("masculine"));
            Assert.That(panel.Choice.BodyPreset, Is.EqualTo("masculine"));

            panel.Open(Start);
            panel.PickTop(1);
            yield return null;
            UI.HandleEscape(); // opens the menu, which cancels the editor
            Assert.That(panel.IsVisible, Is.False);
            Assert.That(cancelled.Count, Is.EqualTo(2));

            panel.Open(Start);
            UI.ShowScreen(UIScreen.Connect); // leaving the world
            Assert.That(panel.IsVisible, Is.False);
            Assert.That(cancelled.Count, Is.EqualTo(3));
            Assert.That(saved, Is.Empty);
        }

        [UnityTest]
        public IEnumerator HexFieldSetsACustomColour()
        {
            panel.Open(Start);
            yield return null;
            var field = panel.TopHexField;
            Assert.That(field.value, Is.EqualTo(ColorMath.ToHex(Start.Top)));
            field.value = "#123456";
            PressKey(field, KeyCode.Return);
            Assert.That(ColorMath.ToHex(panel.Choice.Top), Is.EqualTo("#123456"));
            Assert.That(changed.Count, Is.EqualTo(1));
            field.value = "nonsense";
            PressKey(field, KeyCode.Return);
            Assert.That(ColorMath.ToHex(panel.Choice.Top), Is.EqualTo("#123456"), "invalid text is ignored");
            Assert.That(field.value, Is.EqualTo("#123456"));
        }

        [UnityTest]
        public IEnumerator FitsOnA720pScreen()
        {
            panel.Open(Start);
            for (int i = 0; i < 3; i++) yield return null;
            var bounds = panel.Root.worldBound;
            var root = UI.RootElement.worldBound;
            Assert.That(bounds.width, Is.GreaterThan(100f));
            Assert.That(bounds.xMax, Is.LessThanOrEqualTo(root.xMax + 0.5f));
            Assert.That(bounds.yMax, Is.LessThanOrEqualTo(root.yMax + 0.5f));
            Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(root.yMin - 0.5f));
        }
    }
}
