using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Tinyverse.Net;
using Tinyverse.UI;
using UnityEngine;
using UnityEngine.TestTools;
using ConnectionState = Tinyverse.Net.ConnectionState;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>
    /// <c>wss://</c> with the real server's self-signed certificate: the connection with a correct pin, a wrong
    /// pin and no pin, and the App's trust prompt (accept, refuse, changed certificate).
    /// </summary>
    public sealed class TlsTests
    {
        ServerFixture server;
        App app;
        Driver driver;
        string profile;
        TinyverseConnection conn;

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
            conn?.Dispose();
            conn = null;
            app?.Dispose();
            app = null;
            if (driver != null) UnityEngine.Object.Destroy(driver.gameObject);
            server?.Dispose();
            server?.AssertNothingLeft();
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch (Exception) { }
        }

        IEnumerator InWorld() =>
            Wait.For(() => app.State == AppState.InWorld && app.Session != null && app.Session.SnapshotComplete, 15f, "the world");

        IEnumerator Asked(string title) =>
            Wait.For(() => app.UI.Confirm.IsVisible && app.UI.Confirm.Title == title, 15f, "the question \"" + title + "\"");

        /// <summary>Connects with the real transport and returns the task (its exception, if it fails).</summary>
        Task<P.Welcome> Join(string url)
        {
            conn?.Dispose();
            conn = new TinyverseConnection(new ConnectionSettings { MaxReconnectAttempts = 0 });
            return conn.ConnectAsync(new ConnectOptions { Url = url, DisplayName = "TLS probe" });
        }

        static Exception Failure(Task t) => t.Exception?.GetBaseException();

        [UnityTest]
        public IEnumerator Connection_PinnedJoins_WrongPinAndNoPinAreRefusedWithTheFingerprint()
        {
            server = new ServerFixture(tls: true);
            yield return server.Start();
            StringAssert.StartsWith("wss://127.0.0.1:", server.Url);
            string pin = server.Pin();

            // The right pin: joined over TLS, though no CA vouches for the certificate.
            var ok = Join(server.Url + "#sha256=" + pin);
            yield return Wait.Task(ok, 15f, "the pinned join");
            Assert.AreEqual(ConnectionState.Connected, conn.State);
            Assert.IsNotEmpty(ok.Result.SessionToken);
            Assert.Greater(conn.Send(new P.ClientMessage { Chat = new P.Chat { Channel = P.ChatChannel.World, Text = "hello over tls" } }), 0u);
            conn.Close();

            // Upper case with colons is the same pin.
            var colons = Join(server.Url + "#SHA256=" + CertificatePin.Colons(pin));
            yield return Wait.Task(colons, 15f, "the join with a colon-separated pin");

            // A wrong pin: refused before anything is sent, naming both fingerprints.
            string wrong = new string('0', 64);
            var refused = Join(server.Url + "#sha256=" + wrong);
            yield return Wait.For(() => refused.IsCompleted, 15f, "the refusal");
            var e = Failure(refused) as TinyverseCertificateException;
            Assert.IsNotNull(e, "expected a certificate refusal, got " + Failure(refused));
            Assert.AreEqual(CertificateProblem.PinMismatch, e.Problem);
            Assert.AreEqual(pin, e.Fingerprint);
            Assert.AreEqual(wrong, e.ExpectedPin);
            Assert.AreEqual("127.0.0.1:" + server.Port, e.HostPort);
            Assert.AreEqual(ConnectionState.Disconnected, conn.State);

            // No pin: the system does not trust a self-signed certificate; the fingerprint comes back.
            var untrusted = Join(server.Url);
            yield return Wait.For(() => untrusted.IsCompleted, 15f, "the refusal");
            e = Failure(untrusted) as TinyverseCertificateException;
            Assert.IsNotNull(e, "expected a certificate refusal, got " + Failure(untrusted));
            Assert.AreEqual(CertificateProblem.Untrusted, e.Problem);
            Assert.AreEqual(pin, e.Fingerprint);
            Assert.IsNull(e.ExpectedPin);
            StringAssert.Contains(CertificatePin.Colons(pin), e.Message);

            // wss:// to a plain server: a readable handshake failure, not a certificate question.
            using (var plain = new ServerFixture("Plain World"))
            {
                yield return plain.Start();
                var mismatch = Join("wss://127.0.0.1:" + plain.Port + "/ws");
                yield return Wait.For(() => mismatch.IsCompleted, 15f, "the failure");
                Assert.IsInstanceOf<TinyverseConnectionException>(Failure(mismatch), Failure(mismatch)?.ToString());
                StringAssert.Contains("ws://", Failure(mismatch).Message);
                conn.Dispose();
                conn = null;
            }
        }

        [UnityTest]
        public IEnumerator App_AsksToTrustAnUnknownCertificate_RefuseThenAccept_ThenRemembers()
        {
            server = new ServerFixture(tls: true);
            yield return server.Start();
            string pin = server.Pin();
            CreateApp();

            // Refuse: not joined, nothing remembered.
            app.Connect(server.Url, "Guest", "");
            yield return Asked("Trust this world?");
            StringAssert.Contains("not known", app.UI.Confirm.Message);
            StringAssert.Contains(CertificatePin.Colons(pin), app.UI.Confirm.Message, "the prompt shows the fingerprint");
            StringAssert.Contains("127.0.0.1:" + server.Port, app.UI.Confirm.Message);
            Assert.AreEqual(AppState.FrontEnd, app.State);
            app.UI.Confirm.Cancel();
            Assert.AreEqual(AppState.FrontEnd, app.State);
            StringAssert.Contains("did not trust", app.UI.Connect.ErrorText);
            Assert.IsNull(app.RememberedPin(server.Url));

            // Accept: joined, and the pin is remembered for host:port.
            app.Connect(server.Url, "Guest", "");
            yield return Asked("Trust this world?");
            app.UI.Confirm.Confirm();
            yield return InWorld();
            Assert.AreEqual(pin, app.RememberedPin(server.Url));
            string settings = File.ReadAllText(Path.Combine(profile, SettingsStore.FileName));
            StringAssert.Contains(pin, settings, "kept in settings.json");

            // Next time: straight in, no question.
            app.Leave();
            app.Connect(server.Url, "Guest", "");
            yield return InWorld();
            Assert.IsFalse(app.UI.Confirm.IsVisible);
            Assert.AreEqual(1, app.UI.Connect.RecentWorlds.Count, "one world, whether pinned or not");
        }

        [UnityTest]
        public IEnumerator App_PinnedAddress_JoinsWithoutAQuestion_AndAWrongPinIsAnError()
        {
            server = new ServerFixture(tls: true);
            yield return server.Start();
            CreateApp();

            app.Connect(server.Url + "#sha256=" + new string('a', 64), "Guest", "");
            yield return Wait.For(() => app.State == AppState.FrontEnd && app.UI.Connect.ErrorText != null, 15f, "the refusal");
            Assert.IsFalse(app.UI.Confirm.IsVisible, "a pasted pin is not second-guessed");
            StringAssert.Contains("does not match the fingerprint in the address", app.UI.Connect.ErrorText);
            Assert.IsNull(app.RememberedPin(server.Url));

            app.Connect(server.PinnedUrl, "Guest", "");
            yield return InWorld();
            Assert.IsFalse(app.UI.Confirm.IsVisible);
            Assert.AreEqual(server.Pin(), app.RememberedPin(server.Url), "a pinned join is remembered");
        }

        [UnityTest]
        public IEnumerator App_ChangedCertificate_IsRefused_UntilTheOldOneIsForgotten()
        {
            server = new ServerFixture(tls: true);
            yield return server.Start();
            int port = server.Port;
            string oldPin = server.Pin();
            CreateApp();
            app.Connect(server.PinnedUrl, "Guest", "");
            yield return InWorld();
            app.Leave();

            // The host makes a new certificate and restarts on the same port.
            server.Stop();
            server.NewCertificate();
            string newPin = server.Pin();
            Assert.AreNotEqual(oldPin, newPin);
            yield return server.Start(port);

            app.Connect(server.Url, "Guest", "");
            yield return Asked("Certificate changed");
            StringAssert.Contains(CertificatePin.Colons(oldPin), app.UI.Confirm.Message);
            StringAssert.Contains(CertificatePin.Colons(newPin), app.UI.Confirm.Message);
            app.UI.Confirm.Cancel();
            Assert.AreEqual(AppState.FrontEnd, app.State);
            StringAssert.Contains("has changed", app.UI.Connect.ErrorText);
            Assert.AreEqual(oldPin, app.RememberedPin(server.Url), "refusing keeps the old pin");

            // Forget the old one: then it is an unknown certificate, asked about as usual.
            app.Connect(server.Url, "Guest", "");
            yield return Asked("Certificate changed");
            app.UI.Confirm.Confirm();
            yield return Asked("Trust this world?");
            StringAssert.Contains(CertificatePin.Colons(newPin), app.UI.Confirm.Message);
            app.UI.Confirm.Confirm();
            yield return InWorld();
            Assert.AreEqual(newPin, app.RememberedPin(server.Url));
        }
    }
}
