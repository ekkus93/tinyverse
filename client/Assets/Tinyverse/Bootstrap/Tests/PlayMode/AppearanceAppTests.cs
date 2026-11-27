using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using Tinyverse.Avatars;
using Tinyverse.UI;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>The appearance editor through the whole App against the real server.</summary>
    public sealed class AppearanceAppTests
    {
        ServerFixture server;
        App app;
        Driver driver;
        string profile;

        [SetUp]
        public void SetUp()
        {
            Binaries.Require();
            profile = Path.Combine(Path.GetTempPath(), "tv-profile-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        void CreateApp()
        {
            app = new App(new CommandLine { ProfileDirectory = profile });
            driver = new GameObject("App driver").AddComponent<Driver>();
            driver.OnUpdate = dt => app?.Tick(dt);
            driver.OnLateUpdate = () => app?.LateTick();
        }

        void DisposeApp()
        {
            app?.Dispose();
            app = null;
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
        }

        [TearDown]
        public void TearDown()
        {
            DisposeApp();
            server?.Dispose();
            server?.AssertNothingLeft();
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (Exception) { }
        }

        IEnumerator InWorld() =>
            Wait.For(() => app.State == AppState.InWorld && app.Session != null && app.Session.SnapshotComplete && app.Session.LocalAvatar != null, 15f, "the world");

        static void Click(VisualElement root, string name)
        {
            var b = root.Q<Button>(name);
            Assert.IsNotNull(b, name);
            Submit(b);
        }

        static void Submit(VisualElement b)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        [UnityTest]
        public IEnumerator Edit_Preview_Done_IsSentAndKeptForTheNextJoin()
        {
            server = new ServerFixture();
            yield return server.Start();
            CreateApp();
            app.Connect(server.Url, "Ann", "");
            yield return InWorld();
            ulong annId = app.Session.OwnAvatarId;

            P.Appearance seen = null;
            using (var observer = new Observer())
            {
                observer.Conn.AvatarUpdated += (m, _) => { if (m.Avatar.Id == annId) seen = m.Avatar.Appearance; };
                yield return observer.Join(server.Url, "Watcher", "");

                var panel = app.Appearance.Panel;
                app.Appearance.Open();
                yield return null;
                Assert.IsTrue(panel.IsVisible);
                Assert.IsFalse(app.Session.LocalAvatar.InputEnabled, "the avatar stands still while editing");

                Click(panel.Root, "appearanceBody-feminine");
                Click(panel.Root, "appearanceHair-buns");
                Submit(panel.Root.Q("appearanceTop")[10]);
                Submit(panel.Root.Q("appearanceSkin")[6]);
                var look = app.Session.LocalAvatar.Look;
                Assert.AreEqual("feminine", look.BodyPreset, "live preview");
                Assert.AreEqual("buns", look.HairStyle);
                Assert.AreEqual(HumanCatalog.Feminine, ((HumanAvatarVisual)app.Session.LocalAvatar.Visual).Preset.Id);
                Assert.IsNull(seen, "nothing sent before Done");

                Click(panel.Root, "appearanceDone");
                yield return Wait.For(() => seen != null, 5f, "the AvatarUpdated broadcast");
                Assert.AreEqual("feminine", seen.BodyPreset);
                Assert.AreEqual("buns", seen.HairStyle);
                Assert.AreEqual(AppearancePanel.ClothColors[10].r, seen.TopColor.R, 1e-3f);
                Assert.AreEqual(AppearancePanel.SkinTones[6].g, seen.SkinColor.G, 1e-3f);
                yield return null;
                Assert.IsTrue(app.Session.LocalAvatar.InputEnabled, "free to move again");
            }
            StringAssert.Contains("\"bodyPreset\": \"feminine\"", File.ReadAllText(Path.Combine(profile, SettingsStore.FileName)));

            // Cancel restores the look.
            app.Appearance.Open();
            Click(app.Appearance.Panel.Root, "appearanceBody-masculine");
            Assert.AreEqual("masculine", app.Session.LocalAvatar.Look.BodyPreset);
            Click(app.Appearance.Panel.Root, "appearanceCancel");
            Assert.AreEqual("feminine", app.Session.LocalAvatar.Look.BodyPreset);

            // A new start of the client joins with the stored look.
            DisposeApp();
            yield return null;
            CreateApp();
            app.Connect(server.Url, "Ann", "");
            yield return InWorld();
            Assert.AreEqual("feminine", app.Session.LocalAvatar.Look.BodyPreset);
            Assert.AreEqual("buns", app.Session.LocalAvatar.Look.HairStyle);
        }
    }
}
