using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class ToastTests : UITestBase
    {
        [UnityTest]
        public IEnumerator ToastsStackAndExpire()
        {
            UI.Toasts.Show("Saved", ToastKind.Info, 0.2f);
            UI.Toasts.Show("Slow down", ToastKind.Warning, 0.2f);
            UI.Toasts.Show("Permission denied", ToastKind.Error, 30f);
            Assert.That(UI.Toasts.Count, Is.EqualTo(3));
            Assert.That(UI.Toasts.Kinds, Is.EqualTo(new[] { ToastKind.Info, ToastKind.Warning, ToastKind.Error }));
            Assert.That(UI.Toasts.Root.childCount, Is.EqualTo(3));
            Assert.That(UI.Toasts.Root[2].ClassListContains("tv-toast--error"), Is.True);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(UI.Toasts.Messages, Is.EqualTo(new[] { "Permission denied" }));
            UI.Toasts.Clear();
            Assert.That(UI.Toasts.Count, Is.EqualTo(0));
        }

        [Test]
        public void StackIsBounded()
        {
            for (int i = 0; i < Toasts.MaxVisible + 3; i++) UI.Toasts.Info("t" + i);
            Assert.That(UI.Toasts.Count, Is.EqualTo(Toasts.MaxVisible));
            Assert.That(UI.Toasts.Messages[0], Is.EqualTo("t3"));
        }

        [Test]
        public void ToastsShowOnEveryScreen()
        {
            UI.ShowScreen(UIScreen.Hud);
            UI.Toasts.Error("x");
            UI.ShowScreen(UIScreen.Connect);
            Assert.That(UI.Toasts.Count, Is.EqualTo(1));
        }
    }

    public class MenuAndSettingsTests : UITestBase
    {
        [SetUp]
        public void ShowHud() => UI.ShowScreen(UIScreen.Hud);

        [Test]
        public void MenuButtonsRaiseEvents()
        {
            var got = new List<string>();
            UI.Menu.ResumeRequested += () => got.Add("resume");
            UI.Menu.SettingsRequested += () => got.Add("settings");
            UI.Menu.DisconnectRequested += () => got.Add("disconnect");
            UI.Menu.QuitRequested += () => got.Add("quit");

            UI.OpenMenu();
            Click(UI.Menu.Root.Q<Button>("menuResume"));
            Assert.That(UI.Menu.IsVisible, Is.False, "resume closes");
            UI.OpenMenu();
            Click(UI.Menu.Root.Q<Button>("menuSettings"));
            Assert.That(UI.Settings.IsVisible, Is.True);
            UI.Settings.Close();
            Click(UI.Menu.Root.Q<Button>("menuDisconnect"));
            Click(UI.Menu.Root.Q<Button>("menuQuit"));
            Assert.That(got, Is.EqualTo(new[] { "resume", "settings", "disconnect", "quit" }));
        }

        [Test]
        public void SettingsRoundTrip()
        {
            UI.Settings.SetQualityLevels(new[] { "Low", "Medium", "High" });
            var s = new ClientSettings
            {
                MouseSensitivity = 2.5f, InvertY = true, FieldOfView = 80f, QualityIndex = 2,
                Fullscreen = true, MasterVolume = 0.3f, UIScale = 1.25f,
            };
            int changes = 0;
            UI.Settings.SettingsChanged += _ => changes++;
            UI.Settings.SetSettings(s);
            Assert.That(changes, Is.EqualTo(0), "SetSettings is silent");
            var back = UI.Settings.Settings;
            Assert.That(back.MouseSensitivity, Is.EqualTo(2.5f));
            Assert.That(back.InvertY, Is.True);
            Assert.That(back.FieldOfView, Is.EqualTo(80f));
            Assert.That(back.QualityIndex, Is.EqualTo(2));
            Assert.That(back.Fullscreen, Is.True);
            Assert.That(back.MasterVolume, Is.EqualTo(0.3f));
            Assert.That(back.UIScale, Is.EqualTo(1.25f));
            Assert.That(UI.Settings.QualityDropdown.value, Is.EqualTo("High"));
            Assert.That(UI.Settings.InvertYToggle.value, Is.True);
            Assert.That(UI.Settings.FovSlider.value, Is.EqualTo(80f));
        }

        [Test]
        public void EveryControlRaisesChanged()
        {
            UI.Settings.SetQualityLevels(new[] { "Low", "High" });
            UI.Settings.SetSettings(ClientSettings.Default);
            var got = new List<ClientSettings>();
            UI.Settings.SettingsChanged += got.Add;

            Change(UI.Settings.SensitivitySlider, 3f);
            Assert.That(got[^1].MouseSensitivity, Is.EqualTo(3f));
            Change(UI.Settings.InvertYToggle, true);
            Assert.That(got[^1].InvertY, Is.True);
            Change(UI.Settings.FovSlider, 95.4f);
            Assert.That(got[^1].FieldOfView, Is.EqualTo(95f));
            Change(UI.Settings.QualityDropdown, "High");
            Assert.That(got[^1].QualityIndex, Is.EqualTo(1));
            Change(UI.Settings.FullscreenToggle, true);
            Assert.That(got[^1].Fullscreen, Is.True);
            Change(UI.Settings.VolumeSlider, 0.5f);
            Assert.That(got[^1].MasterVolume, Is.EqualTo(0.5f));
            Change(UI.Settings.UIScaleSlider, 1.2f);
            Assert.That(got[^1].UIScale, Is.EqualTo(1.2f).Within(1e-4f));
            Assert.That(UI.UIScale, Is.EqualTo(1.2f).Within(1e-4f), "UI scale applies at once");
            Assert.That(got.Count, Is.EqualTo(7));
            Assert.That(got[^1].InvertY && got[^1].Fullscreen, Is.True, "earlier changes are kept");
        }

        [Test]
        public void OutOfRangeValuesAreClamped()
        {
            UI.Settings.SetSettings(new ClientSettings { MouseSensitivity = 99f, FieldOfView = 10f, MasterVolume = 2f, UIScale = 0f, QualityIndex = 9 });
            var s = UI.Settings.Settings;
            Assert.That(s.MouseSensitivity, Is.EqualTo(SettingsPanel.MaxSensitivity));
            Assert.That(s.FieldOfView, Is.EqualTo(SettingsPanel.MinFov));
            Assert.That(s.MasterVolume, Is.EqualTo(1f));
            Assert.That(s.UIScale, Is.EqualTo(1f));
            Assert.That(s.QualityIndex, Is.EqualTo(0));
        }

        [Test]
        public void KeyBindingsAreListed()
        {
            Assert.That(UI.Settings.KeyBindingCount, Is.EqualTo(SettingsPanel.DefaultKeyBindings.Count));
            UI.Settings.SetKeyBindings(new[] { ("Q", "Quack") });
            Assert.That(UI.Settings.KeyBindingCount, Is.EqualTo(1));
        }

        [Test]
        public void DoneClosesAndRaisesClosed()
        {
            int closed = 0;
            UI.Settings.Closed += () => closed++;
            UI.OpenSettings();
            Click(UI.Settings.Root.Q<Button>("settingsDone"));
            Assert.That(UI.Settings.IsVisible, Is.False);
            Assert.That(closed, Is.EqualTo(1));
        }
    }
}
