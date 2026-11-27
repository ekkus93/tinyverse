using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tinyverse.Host;
using Tinyverse.Net;
using Tinyverse.UI;
using UnityEngine;
using UnityEngine.TestTools;
using P = Tinyverse.Proto;
using HostSettings = Tinyverse.Host.HostSettings;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>The whole App (front end, connecting, kick, hosting) against the real server.</summary>
    public sealed class AppTests
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

        [TearDown]
        public void TearDown()
        {
            app?.Dispose();
            app = null;
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
            server?.Dispose();
            server?.AssertNothingLeft();
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (Exception) { }
        }

        IEnumerator InWorld() =>
            Wait.For(() => app.State == AppState.InWorld && app.Session != null && app.Session.SnapshotComplete, 15f, "the world");

        [UnityTest]
        public IEnumerator Kick_ReturnsToTheConnectScreenWithTheReason()
        {
            server = new ServerFixture();
            yield return server.Start();
            CreateApp();
            Assert.AreEqual(UIScreen.Connect, app.UI.Screen);
            app.Connect(server.Url, "Kickee", "");
            yield return InWorld();
            Assert.AreEqual(UIScreen.Hud, app.UI.Screen);

            using (var owner = new Observer())
            {
                yield return owner.Join(server.Url, "Owner", server.OwnerToken);
                var kick = owner.Conn.RequestAsync(new P.ClientMessage
                {
                    Admin = new P.Admin { Kick = new P.Admin.Types.Kick { UserId = app.Session.UserId, Reason = "testing kicks" } },
                });
                yield return Wait.Task(kick, 5f, "the kick");
            }
            yield return Wait.For(() => app.State == AppState.FrontEnd, 5f, "the front end");
            Assert.AreEqual(UIScreen.Connect, app.UI.Screen);
            StringAssert.Contains("testing kicks", app.UI.Connect.ErrorText);
            Assert.IsNull(app.Session);
        }

        [UnityTest]
        public IEnumerator StoredToken_IsUsed_AndAnInvalidOneIsForgottenAndRetried()
        {
            server = new ServerFixture();
            yield return server.Start();
            string url = WorldAddress.Normalize(server.Url);
            new FileTokenStore(profile).Remember(url, "not-a-real-token", "Returner", DateTimeOffset.Now);
            CreateApp();
            Assert.AreEqual(url, app.UI.Connect.Address, "the last world is pre-filled");
            Assert.AreEqual("Returner", app.UI.Connect.DisplayName);
            Assert.AreEqual(1, app.UI.Connect.RecentWorlds.Count);

            app.Connect(server.Url, "Returner", "");
            yield return InWorld();
            string token = new FileTokenStore(profile).Get(url).SessionToken;
            Assert.IsNotEmpty(token);
            Assert.AreNotEqual("not-a-real-token", token, "a fresh token replaced the refused one");
            string userId = app.Session.UserId;

            app.Leave();
            Assert.AreEqual(AppState.FrontEnd, app.State);
            Assert.AreEqual("Test World", app.UI.Connect.RecentWorlds[0].Name, "recent worlds show the world's name");
            app.Connect(server.Url, "Returner", "");
            yield return InWorld();
            Assert.AreEqual(userId, app.Session.UserId, "the stored token brings back the same user");
        }

        [UnityTest]
        public IEnumerator Refusals_ShowAsReadableErrors_AndCancelWorks()
        {
            server = new ServerFixture(password: "sesame");
            yield return server.Start();
            CreateApp();
            app.Connect(server.Url, "Guest", "wrong");
            yield return Wait.For(() => app.State == AppState.FrontEnd && app.UI.Connect.ErrorText != null, 10f, "the refusal");
            Assert.AreEqual(ErrorText.Describe(P.ErrorCode.AuthFailed), app.UI.Connect.ErrorText.Split('(')[0].Trim());
            Assert.IsFalse(app.UI.Connect.IsBusy);

            app.Connect("not a valid address", "Guest", "");
            Assert.That(app.UI.Connect.ErrorText, Does.Contain("space"));

            // Cancel while the connection hangs (a port where nothing answers the WebSocket handshake).
            var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            silent.Start();
            try
            {
                int port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
                app.Connect("127.0.0.1:" + port, "Guest", "");
                Assert.AreEqual(AppState.Connecting, app.State);
                Assert.IsTrue(app.UI.Connect.IsBusy);
                yield return Wait.Seconds(0.3f);
                app.UI.Connect.RequestCancel();
                yield return Wait.For(() => app.State == AppState.FrontEnd, 5f, "the cancel");
                Assert.IsFalse(app.UI.Connect.IsBusy);
            }
            finally
            {
                silent.Stop();
            }

            app.Connect(server.Url, "Guest", "sesame");
            yield return InWorld();
        }

        [UnityTest]
        public IEnumerator Hosting_ThroughTheApp_JoinsAsOwner_AndQuitStopsTheServer()
        {
            string world = Path.Combine(profile, "worlds", "app-hosted");
            int port = Binaries.FreePort();
            CreateApp();
            app.HostFromCommandLine(world, port);
            yield return Wait.For(() => app.State == AppState.InWorld && app.Session != null && app.Session.SnapshotComplete, 60f, "hosting and joining");
            Assert.AreEqual(P.Role.Owner, app.Session.Role, "joined with the owner token");
            Assert.IsTrue(app.Hosting.IsRunning);
            Assert.IsNotEmpty(app.UI.Host.ShareAddress);
            Assert.IsTrue(app.UI.Host.LogLines.Any(l => l.Contains("listening on")), "server log in the Host screen");
            Assert.IsTrue(app.UI.People.IsLocalOwner, "owner actions offered");
            int? pid = app.Hosting.Process.ProcessId;
            Assert.IsNotNull(pid);

            // Settings apply live and persist.
            var s = app.UI.Settings.Settings;
            s.FieldOfView = 95f;
            s.InvertY = true;
            s.MouseSensitivity = 2f;
            s.UIScale = 1.25f;
            s.MasterVolume = 0.4f;
            app.ApplySettings(s);
            Assert.AreEqual(95f, app.Camera.fieldOfView);
            Assert.IsTrue(app.Session.Orbit.InvertY);
            Assert.AreEqual(0.4f, app.Session.Orbit.OrbitSensitivity, 1e-5f);
            Assert.AreEqual(1.25f, app.UI.UIScale);
            Assert.AreEqual(0.4f, AudioListener.volume, 1e-5f);

            // Leaving a hosted world asks; "keep hosting" (cancel) leaves the server running.
            app.RequestDisconnect();
            Assert.IsTrue(app.UI.Confirm.IsVisible, "asked whether to stop hosting");
            app.UI.Confirm.Cancel();
            Assert.AreEqual(AppState.FrontEnd, app.State);
            Assert.IsTrue(app.Hosting.IsRunning, "still hosting");
            StringAssert.Contains(port.ToString(), app.UI.Connect.Address, "the local address is pre-filled to rejoin");
            app.UI.Connect.Submit();
            yield return InWorld();
            Assert.AreEqual(P.Role.Owner, app.Session.Role, "rejoined as owner from the Connect screen");

            app.Shutdown(); // what Application.quitting does
            Assert.AreEqual(Tinyverse.Host.HostState.Stopped, app.Hosting.Process == null ? Tinyverse.Host.HostState.Stopped : app.Hosting.Process.State);
            Assert.IsFalse(ProcessAlive(pid.Value), "the server process is gone");
            Assert.IsTrue(File.Exists(Path.Combine(world, "world.toml")));
        }

        static bool ProcessAlive(int pid)
        {
            try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>HostingController with the real binary: start, join, build, graceful stop, persistence.</summary>
    public sealed class HostingTests
    {
        HostingController hosting;
        Driver driver;
        SessionHarness h;
        string dir;

        [SetUp]
        public void SetUp()
        {
            Binaries.Require();
            dir = Path.Combine(Path.GetTempPath(), "tv-host-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            hosting = new HostingController { ServerBinaryOverride = Binaries.Server };
            driver = new GameObject("Hosting driver").AddComponent<Driver>();
            driver.OnUpdate = _ => hosting.Tick();
        }

        [TearDown]
        public void TearDown()
        {
            h?.Dispose();
            hosting?.Dispose();
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }

        [UnityTest]
        public IEnumerator StartJoinBuildStop_AndTheObjectsPersist()
        {
            int port = Binaries.FreePort();
            var settings = new HostSettings
            {
                WorldDirectory = Path.Combine(dir, "my-world"), WorldName = "Hosted World", Port = port, BindAddress = "127.0.0.1",
            };
            var logs = new System.Collections.Generic.List<string>();
            hosting.LogLine += logs.Add;
            var start = hosting.StartAsync(settings);
            yield return Wait.Task(start, 60f, "the hosted server");
            var result = start.Result;
            Assert.IsNotNull(result.OwnerToken);
            Assert.AreEqual(port, result.Port);
            Assert.IsTrue(hosting.IsRunning);

            h = new SessionHarness();
            yield return h.Join(result.LocalUrl, "Host", result.OwnerToken);
            Assert.AreEqual(P.Role.Owner, h.Session.Role);
            Assert.AreEqual("Hosted World", h.Session.WorldName);
            int token = h.Session.Build.PlaceInFrontOfAvatar();
            Assert.AreNotEqual(0, token);
            yield return Wait.For(() => h.Session.Objects.Count == 1, 5f, "the object");
            ulong id = h.Session.Objects.All.First().Id;
            h.Leave();

            var stop = hosting.StopAsync();
            yield return Wait.Task(stop, 20f, "the stop");
            Assert.IsTrue(stop.Result.Graceful, stop.Result.Message);
            Assert.IsFalse(hosting.IsActive);

            // Again: the object was saved.
            settings.Password = null;
            var again = hosting.StartAsync(settings);
            yield return Wait.Task(again, 60f, "the restart");
            yield return h.Join(again.Result.LocalUrl, "Host", again.Result.OwnerToken);
            Assert.IsTrue(h.Session.Objects.Contains(id), "the object persisted across a graceful stop");
            h.Leave();
            var stop2 = hosting.StopAsync();
            yield return Wait.Task(stop2, 20f, "the second stop");
            Assert.IsTrue(stop2.Result.Graceful);
            Assert.IsTrue(logs.Any(l => l.Contains("listening on")), string.Join("\n", logs));
        }

        [UnityTest]
        public IEnumerator PortInUse_IsAReadableError()
        {
            var busy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            busy.Start();
            try
            {
                int port = ((System.Net.IPEndPoint)busy.LocalEndpoint).Port;
                var start = hosting.StartAsync(new HostSettings { WorldDirectory = Path.Combine(dir, "w"), Port = port, BindAddress = "127.0.0.1" });
                yield return Wait.For(() => start.IsCompleted, 10f, "the refusal");
                Assert.IsTrue(start.IsFaulted);
                var e = start.Exception.InnerException as HostException;
                Assert.IsNotNull(e);
                Assert.AreEqual(HostErrorKind.PortInUse, e.Kind);
                StringAssert.Contains("already in use", e.Message);
            }
            finally
            {
                busy.Stop();
            }
        }
    }
}
