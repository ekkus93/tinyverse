using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class UIRootTests : UITestBase
    {
        [Test]
        public void CreatesDocumentWithRuntimeThemeAndPanelSettings()
        {
            Assert.That(UI.Document, Is.Not.Null);
            Assert.That(UI.PanelSettings, Is.Not.Null);
            Assert.That(UI.Document.panelSettings, Is.SameAs(UI.PanelSettings));
            Assert.That(UI.PanelSettings.themeStyleSheet, Is.Not.Null);
            Assert.That(UI.PanelSettings.themeStyleSheet.name, Is.EqualTo("TinyverseTheme"));
            Assert.That(UI.PanelSettings, Is.Not.SameAs(UIResources.Load<PanelSettings>(UIResources.PanelSettingsAsset)),
                "a runtime copy, so scaling never dirties the asset");
            Assert.That(UI.RootElement.panel, Is.Not.Null);
            Assert.That(UI.RootElement.ClassListContains("tv-root"), Is.True);
        }

        [UnityTest]
        public IEnumerator ThemeStylesResolveOnControls()
        {
            yield return null;
            // These values only come from our theme (Theme.uss through the .tss) and the default runtime theme it imports.
            var connect = UI.Connect.ConnectButton;
            Assert.That(connect.resolvedStyle.borderTopLeftRadius, Is.EqualTo(7f).Within(0.01f), "theme radius on buttons");
            Assert.That(connect.resolvedStyle.unityFontDefinition.font, Is.Not.Null, "built-in font inherited by controls");
            Assert.That(connect.resolvedStyle.unityFontDefinition.font.name, Is.EqualTo("LegacyRuntime"));
            Assert.That(UI.RootElement.resolvedStyle.fontSize, Is.EqualTo(14f).Within(0.01f), "tv-root font size");

            // Only the default runtime theme (imported by our .tss) draws the toggle's checkmark image.
            UI.OpenSettings();
            UI.Settings.FullscreenToggle.value = true;
            yield return null;
            yield return null;
            var check = UI.Settings.FullscreenToggle.Q(className: "unity-toggle__checkmark").resolvedStyle.backgroundImage;
            Assert.That(check.texture != null || check.sprite != null || check.vectorImage != null || check.renderTexture != null,
                Is.True, "default runtime theme is applied");
        }

        [Test]
        public void StartsOnConnectScreen()
        {
            Assert.That(UI.Screen, Is.EqualTo(UIScreen.Connect));
            Assert.That(UI.Connect.IsVisible, Is.True);
            Assert.That(UI.Host.IsVisible, Is.False);
            Assert.That(UI.Hud.IsVisible, Is.False);
            Assert.That(UI.Menu.IsVisible, Is.False);
            Assert.That(UI.Settings.IsVisible, Is.False);
            Assert.That(UI.Confirm.IsVisible, Is.False);
        }

        [Test]
        public void SwitchesScreens()
        {
            var seen = new List<UIScreen>();
            UI.ScreenChanged += seen.Add;
            UI.ShowScreen(UIScreen.Host);
            Assert.That(UI.Host.IsVisible && !UI.Connect.IsVisible && !UI.Hud.IsVisible, Is.True);
            UI.ShowScreen(UIScreen.Hud);
            Assert.That(UI.Hud.IsVisible && !UI.Connect.IsVisible && !UI.Host.IsVisible, Is.True);
            UI.ShowScreen(UIScreen.None);
            Assert.That(UI.Hud.IsVisible || UI.Connect.IsVisible || UI.Host.IsVisible, Is.False);
            UI.ShowScreen(UIScreen.Connect);
            Assert.That(UI.Connect.IsVisible, Is.True);
            Assert.That(seen, Is.EqualTo(new[] { UIScreen.Host, UIScreen.Hud, UIScreen.None, UIScreen.Connect }));
        }

        [Test]
        public void HudPanelsShowAndHide()
        {
            UI.ShowScreen(UIScreen.Hud);
            Assert.That(UI.StatusBar.IsVisible, Is.True);
            Assert.That(UI.Chat.IsVisible, Is.True);
            Assert.That(UI.Hint.IsVisible, Is.True);
            Assert.That(UI.People.IsVisible, Is.False);
            Assert.That(UI.Build.IsVisible, Is.False);
            foreach (UIPanel p in new UIPanel[] { UI.People, UI.Build, UI.Chat, UI.StatusBar, UI.Hint, UI.Menu, UI.Settings })
            {
                p.Show();
                Assert.That(p.IsVisible, Is.True, p.GetType().Name);
                p.Hide();
                Assert.That(p.IsVisible, Is.False, p.GetType().Name);
            }
        }

        [Test]
        public void EscapeClosesTopmostThenOpensMenu()
        {
            UI.ShowScreen(UIScreen.Hud);
            bool modal = false;
            UI.ModalOpenChanged += m => modal = m;
            Assert.That(UI.HandleEscape(), Is.EqualTo("open-menu"));
            Assert.That(UI.Menu.IsVisible, Is.True);
            Assert.That(modal, Is.True);
            UI.OpenSettings();
            Assert.That(UI.Settings.IsVisible && !UI.Menu.IsVisible, Is.True);
            Assert.That(UI.HandleEscape(), Is.EqualTo("settings"));
            Assert.That(UI.Menu.IsVisible, Is.True, "settings opened from the menu returns to it");
            Assert.That(UI.HandleEscape(), Is.EqualTo("menu"));
            Assert.That(UI.Menu.IsVisible, Is.False);
            Assert.That(modal, Is.False);

            UI.People.Show();
            Assert.That(UI.HandleEscape(), Is.EqualTo("people"));
            Assert.That(UI.People.IsVisible, Is.False);

            UI.Confirm.Ask("Sure?", "Really", "Yes", () => Assert.Fail("escape must not confirm"));
            Assert.That(UI.HandleEscape(), Is.EqualTo("confirm"));
            Assert.That(UI.Confirm.IsVisible, Is.False);
        }

        [Test]
        public void EscapeOnFrontEndScreens()
        {
            int cancels = 0, backs = 0;
            UI.Connect.CancelRequested += () => cancels++;
            UI.Host.BackRequested += () => backs++;
            Assert.That(UI.HandleEscape(), Is.EqualTo("none"));
            UI.Connect.SetBusy(true);
            UI.HandleEscape();
            Assert.That(cancels, Is.EqualTo(1));
            UI.ShowScreen(UIScreen.Host);
            UI.HandleEscape();
            Assert.That(backs, Is.EqualTo(1));
            Assert.That(UI.Menu.IsVisible, Is.False, "no in-world menu on front-end screens");
        }

        [UnityTest]
        public IEnumerator TextInputFocusTransitions()
        {
            UI.ShowScreen(UIScreen.Hud);
            yield return null;
            var changes = new List<bool>();
            UI.TextInputFocusChanged += changes.Add;
            Assert.That(UI.IsTextInputFocused, Is.False);

            UI.OpenChat();
            yield return null;
            Assert.That(UI.IsTextInputFocused, Is.True, "chat input focused");
            Assert.That(changes, Is.EqualTo(new[] { true }));

            Assert.That(UI.HandleEscape(), Is.EqualTo("blur"));
            Assert.That(UI.IsTextInputFocused, Is.False);
            Assert.That(UI.Chat.IsInputOpen, Is.False);
            Assert.That(changes, Is.EqualTo(new[] { true, false }));

            // A non-text control does not count.
            UI.StatusBar.MenuButton.Focus();
            yield return null;
            Assert.That(UI.IsTextInputFocused, Is.False);

            // Inspector number fields count as text input.
            UI.SetBuildMode(true);
            UI.Build.SetSelection(new SelectedObjectInfo { Id = 1, Scale = Vector3.one }, editable: true);
            UI.Build.PositionFields.Field(0).Focus();
            yield return null;
            Assert.That(UI.IsTextInputFocused, Is.True);
            UI.Build.PositionFields.Field(0).Blur();
            yield return null;
            Assert.That(UI.IsTextInputFocused, Is.False);
        }

        [UnityTest]
        public IEnumerator HotkeysWorkOnlyInWorldAndNotWhileTyping()
        {
            int toggles = 0;
            UI.BuildModeToggled += () => toggles++;
            Assert.That(UI.HandleKey(Key.B), Is.False, "front-end ignores B");

            UI.ShowScreen(UIScreen.Hud);
            Assert.That(UI.HandleKey(Key.B), Is.True);
            Assert.That(toggles, Is.EqualTo(1));
            Assert.That(UI.HandleKey(Key.P), Is.True);
            Assert.That(UI.People.IsVisible, Is.True);
            UI.HandleKey(Key.P);
            Assert.That(UI.People.IsVisible, Is.False);

            UI.HandleKey(Key.Enter);
            yield return null;
            Assert.That(UI.Chat.IsInputOpen, Is.True);
            Assert.That(UI.HandleKey(Key.B), Is.False, "typing swallows nothing for the world");
            Assert.That(toggles, Is.EqualTo(1));
            UI.HandleEscape();

            UI.OpenMenu();
            Assert.That(UI.HandleKey(Key.B), Is.False, "menu open");
            UI.CloseMenu();

            UI.SetBuildMode(true);
            var tools = new List<BuildTool>();
            UI.Build.ToolSelected += tools.Add;
            UI.HandleKey(Key.Digit3);
            Assert.That(tools, Is.EqualTo(new[] { BuildTool.Move }));
            Assert.That(UI.Build.Tool, Is.EqualTo(BuildTool.Move));
        }

        [Test]
        public void BuildModeButtonsRaiseToggle()
        {
            UI.ShowScreen(UIScreen.Hud);
            int toggles = 0;
            UI.BuildModeToggled += () => toggles++;
            Click(UI.StatusBar.BuildButton);
            Assert.That(toggles, Is.EqualTo(1));
            Assert.That(UI.IsBuildMode, Is.False, "the glue decides");
            UI.SetBuildMode(true);
            Assert.That(UI.Build.IsVisible, Is.True);
            Assert.That(UI.StatusBar.BuildButton.ClassListContains("tv-toggle-button--on"), Is.True);
            Click(UI.Build.CloseButton);
            Assert.That(toggles, Is.EqualTo(2));
            UI.SetBuildMode(false);
            Assert.That(UI.Build.IsVisible, Is.False);
        }

        [Test]
        public void StatusBarButtonsOpenPeopleAndMenu()
        {
            UI.ShowScreen(UIScreen.Hud);
            Click(UI.StatusBar.PeopleButton);
            Assert.That(UI.People.IsVisible, Is.True);
            Assert.That(UI.StatusBar.PeopleButton.ClassListContains("tv-toggle-button--on"), Is.True);
            Click(UI.StatusBar.MenuButton);
            Assert.That(UI.Menu.IsVisible, Is.True);
        }

        [Test]
        public void UIScaleAppliesToPanel()
        {
            float before = UI.PanelSettings.scale;
            UI.UIScale = 1.5f;
            Assert.That(UI.PanelSettings.scale, Is.EqualTo(before * 1.5f).Within(1e-3f));
            UI.UIScale = 10f;
            Assert.That(UI.UIScale, Is.EqualTo(SettingsPanel.MaxUIScale));
            Assert.That(UI.PanelSettings.scale, Is.EqualTo(UIRoot.ComputeScale(Screen.width, Screen.height, SettingsPanel.MaxUIScale)).Within(1e-3f));
        }

        [UnityTest]
        public IEnumerator PointerOverUIReflectsPanels()
        {
            yield return null;
            // Front-end screens cover the window.
            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Assert.That(UI.IsPointerOverUIAt(center), Is.True, "connect screen");
            UI.ShowScreen(UIScreen.Hud);
            UI.Hint.Hide();
            yield return null;
            yield return null;
            Assert.That(UI.IsPointerOverUIAt(center), Is.False, "the middle of the world is free in the HUD");
            var bar = UI.StatusBar.MenuButton.worldBound.center;
            float scale = UI.PanelSettings.scale;
            var screenPos = new Vector2(bar.x * scale, Screen.height - bar.y * scale);
            Assert.That(UI.IsPointerOverUIAt(screenPos), Is.True, $"over the menu button at {screenPos}");
        }
    }
}
