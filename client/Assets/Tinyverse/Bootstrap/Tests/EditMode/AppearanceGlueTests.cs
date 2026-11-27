using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tinyverse.Avatars;
using UnityEngine;
using P = Tinyverse.Proto;
using UI = Tinyverse.UI;

namespace Tinyverse.Bootstrap.Tests
{
    public sealed class AppearanceGlueTests
    {
        string dir;

        [SetUp]
        public void SetUp() => dir = Path.Combine(Path.GetTempPath(), "tv-appearance-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        static readonly UI.AppearanceChoice Choice = new UI.AppearanceChoice
        {
            BodyPreset = "feminine",
            HairStyle = "buns",
            Skin = new Color32(0x8d, 0x5a, 0x3a, 0xff),
            Hair = new Color32(0xb8, 0x64, 0x2e, 0xff),
            Top = new Color32(0x3e, 0x9b, 0x6a, 0xff),
            Bottom = new Color32(0x25, 0x27, 0x2d, 0xff),
        };

        static void AssertColor(Color expected, P.Color actual)
        {
            Assert.AreEqual(expected.r, actual.R, 1.5f / 255f);
            Assert.AreEqual(expected.g, actual.G, 1.5f / 255f);
            Assert.AreEqual(expected.b, actual.B, 1.5f / 255f);
            Assert.AreEqual(1f, actual.A);
        }

        [Test]
        public void SavedChoice_SurvivesARestart_AndIsSentInHello()
        {
            var store = new SettingsStore(dir);
            store.Load();
            Assert.IsTrue(store.Current.appearance.IsEmpty, "nothing chosen at first");
            store.Current.appearance = AppearanceController.ToStored(Choice);
            store.Save();

            var loaded = new SettingsStore(dir).Load();
            Assert.AreEqual("feminine", loaded.appearance.bodyPreset);
            Assert.AreEqual("buns", loaded.appearance.hairStyle);
            Assert.AreEqual("#3e9b6a", loaded.appearance.top);

            var hello = ProtoConvert.ToAppearance(loaded.appearance, SettingsStore.ParseBodyColor(loaded.bodyColor));
            Assert.AreEqual("feminine", hello.BodyPreset);
            Assert.AreEqual("buns", hello.HairStyle);
            AssertColor(Choice.Skin, hello.SkinColor);
            AssertColor(Choice.Hair, hello.HairColor);
            AssertColor(Choice.Top, hello.TopColor);
            AssertColor(Choice.Bottom, hello.BottomColor);
            AssertColor(Choice.Top, hello.BodyColor);

            // What others make of it: the same look.
            var look = ProtoConvert.ToLook(hello, "someone");
            Assert.AreEqual("feminine", look.BodyPreset);
            Assert.AreEqual("buns", look.HairStyle);
            var back = AppearanceController.ToChoice(look);
            Assert.AreEqual(UI.ColorMath.ToHex(Choice.Top), UI.ColorMath.ToHex(back.Top));
            Assert.AreEqual(UI.ColorMath.ToHex(Choice.Skin), UI.ColorMath.ToHex(back.Skin));
            Assert.AreEqual(UI.ColorMath.ToHex(Choice.Bottom), UI.ColorMath.ToHex(back.Bottom));
        }

        [Test]
        public void OldSettingsFile_WithoutAppearance_ReadsAsUnchosen()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SettingsStore.FileName), "{\"version\":1,\"lastName\":\"Ann\",\"bodyColor\":\"#336699\"}");
            var s = new SettingsStore(dir).Load();
            Assert.IsNotNull(s.appearance);
            Assert.IsTrue(s.appearance.IsEmpty);
            var hello = ProtoConvert.ToAppearance(s.appearance, SettingsStore.ParseBodyColor(s.bodyColor));
            Assert.AreEqual("", hello.BodyPreset);
            Assert.IsNull(hello.TopColor);
            AssertColor(new Color32(0x33, 0x66, 0x99, 0xff), hello.BodyColor);
        }

        [Test]
        public void UnchosenPreset_IsStablePerUser_AndBothOccur()
        {
            var presets = Enumerable.Range(0, 40).Select(i => ProtoConvert.ToLook(null, "user-" + i).BodyPreset).ToList();
            Assert.That(presets, Has.Some.EqualTo(HumanCatalog.Masculine));
            Assert.That(presets, Has.Some.EqualTo(HumanCatalog.Feminine));
            Assert.AreEqual(ProtoConvert.ToLook(null, "user-7").BodyPreset, ProtoConvert.ToLook(new P.Appearance(), "user-7").BodyPreset);
            Assert.AreEqual("masculine", ProtoConvert.ToLook(new P.Appearance { BodyPreset = "masculine" }, "user-1").BodyPreset, "a chosen preset wins");
            Assert.AreEqual("robot", ProtoConvert.ToLook(new P.Appearance { BodyPreset = "robot" }, "user-1").BodyPreset, "unknown names pass through (placeholder)");
        }

        [Test]
        public void ChoiceFromALook_ResolvesDefaults()
        {
            var choice = AppearanceController.ToChoice(new AvatarLook { BodyPreset = "", HairStyle = "unknown" });
            Assert.AreEqual(HumanCatalog.DefaultPreset, choice.BodyPreset);
            Assert.AreEqual(HumanCatalog.FindPreset(HumanCatalog.DefaultPreset).DefaultHairStyle, choice.HairStyle);
            Assert.AreEqual(AvatarLook.Default.SkinColor.r, choice.Skin.r, 1e-4f);

            var look = AppearanceController.ToLook(Choice, AvatarLook.Default);
            Assert.AreEqual(Choice.Top, look.BodyColor, "the body colour follows the top for placeholder viewers");
            Assert.AreEqual(Choice.Bottom, look.BottomColor);
        }
    }
}
