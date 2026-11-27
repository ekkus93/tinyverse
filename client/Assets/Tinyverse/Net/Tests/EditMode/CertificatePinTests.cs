using System;
using System.Text;
using NUnit.Framework;

namespace Tinyverse.Net.Tests
{
    public class CertificatePinTests
    {
        // SHA-256 of "abc", as in the Rust tv-tls tests.
        const string Hex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

        [Test]
        public void HashesAndFormatsLikeTheServer()
        {
            Assert.That(CertificatePin.Sha256Hex(Encoding.ASCII.GetBytes("abc")), Is.EqualTo(Hex));
            string colons = CertificatePin.Colons(Hex);
            Assert.That(colons, Does.StartWith("BA:78:16:BF:"));
            Assert.That(colons.Length, Is.EqualTo(95));
        }

        [TestCase(Hex)]
        [TestCase("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
        [TestCase("sha256=" + Hex)]
        [TestCase("SHA256=" + Hex)]
        [TestCase("BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD")]
        public void ParsesEveryWrittenForm(string text)
        {
            Assert.That(CertificatePin.TryParse(text, out var hex), Is.True);
            Assert.That(hex, Is.EqualTo(Hex));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("abc")]
        [TestCase("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015")]
        [TestCase("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015adff")]
        [TestCase("ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
        public void RejectsWhatIsNotAPin(string text)
        {
            Assert.That(CertificatePin.TryParse(text, out _), Is.False);
        }

        [Test]
        public void SplitsUrls()
        {
            Assert.That(CertificatePin.TrySplit("wss://h:1/ws", out var b, out var p, out var e), Is.True, e);
            Assert.That(b, Is.EqualTo("wss://h:1/ws"));
            Assert.That(p, Is.Null);

            Assert.That(CertificatePin.TrySplit("wss://10.0.0.2:7878/ws#sha256=" + Hex.ToUpperInvariant(), out b, out p, out e), Is.True, e);
            Assert.That(b, Is.EqualTo("wss://10.0.0.2:7878/ws"));
            Assert.That(p, Is.EqualTo(Hex));

            Assert.That(CertificatePin.TrySplit("ws://h/ws#sha256=" + Hex, out _, out _, out e), Is.False);
            Assert.That(e, Does.Contain("wss://"));
            Assert.That(CertificatePin.TrySplit("wss://h/ws#top", out _, out _, out e), Is.False);
            Assert.That(e, Does.Contain("#sha256="));
            Assert.That(CertificatePin.TrySplit("wss://h/ws#sha256=12", out _, out _, out e), Is.False);
            Assert.That(e, Does.Contain("64 hex digits"));

            string pinned = CertificatePin.WithPin("wss://h:1/ws#old", Hex);
            Assert.That(pinned, Is.EqualTo("wss://h:1/ws#sha256=" + Hex));
            Assert.That(CertificatePin.PinOf(pinned), Is.EqualTo(Hex));
            Assert.That(CertificatePin.Strip(pinned), Is.EqualTo("wss://h:1/ws"));
            Assert.That(CertificatePin.PinOf("wss://h:1/ws"), Is.Null);
        }

        [Test]
        public void HostPortKeys()
        {
            Assert.That(CertificatePin.HostPortKey("wss://192.168.1.20:7878/ws#sha256=" + Hex), Is.EqualTo("192.168.1.20:7878"));
            Assert.That(CertificatePin.HostPortKey("wss://MyHost:9000/ws"), Is.EqualTo("myhost:9000"));
            Assert.That(CertificatePin.HostPortKey("wss://[::1]:7878/ws"), Is.EqualTo("[::1]:7878"));
            Assert.That(CertificatePin.HostPortKey("not a url"), Is.Null);
        }

        [TestCase("wss://192.168.1.20:7878/ws#sha256=" + Hex, "wss://192.168.1.20:7878/ws#sha256=" + Hex)]
        [TestCase("192.168.1.20#sha256=" + Hex, "wss://192.168.1.20:7878/ws#sha256=" + Hex)]
        [TestCase("192.168.1.20:9000#SHA256=BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD",
            "wss://192.168.1.20:9000/ws#sha256=" + Hex)]
        [TestCase("https://host/ws#sha256=" + Hex, "wss://host:7878/ws#sha256=" + Hex)]
        // Copied from the Host screen, where the address carries zero-width break hints.
        [TestCase("wss://​192.168.1.20:7878/​ws#​sha256=​" + Hex, "wss://192.168.1.20:7878/ws#sha256=" + Hex)]
        public void WorldAddressesKeepPins(string input, string expected)
        {
            Assert.That(WorldAddress.TryNormalize(input, out var url, out var error), Is.True, error);
            Assert.That(url, Is.EqualTo(expected));
        }

        [TestCase("ws://host#sha256=" + Hex, "wss://")]
        [TestCase("host#top", "#sha256=")]
        [TestCase("host#sha256=1234", "64 hex digits")]
        public void WorldAddressesRefuseBadPins(string input, string needle)
        {
            Assert.That(WorldAddress.TryNormalize(input, out _, out var error), Is.False);
            Assert.That(error, Does.Contain(needle));
        }

        [Test]
        public void ConnectRefusesABadPinBeforeConnecting()
        {
            var conn = new TinyverseConnection(new ConnectionSettings { HandlerExceptionLogger = _ => { } });
            var e = Assert.Throws<ArgumentException>(() =>
                conn.ConnectAsync(new ConnectOptions { Url = "ws://127.0.0.1:1/ws#sha256=" + Hex }).GetAwaiter().GetResult());
            Assert.That(e.Message, Does.Contain("wss://"));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Disconnected));
        }

        [Test]
        public void CertificateExceptionsSayWhatWasPresented()
        {
            var untrusted = new TinyverseCertificateException(CertificateProblem.Untrusted, "h:1", Hex, null, "RemoteCertificateChainErrors");
            Assert.That(untrusted.Message, Does.Contain("not trusted").And.Contain("BA:78:16:BF"));
            var changed = new TinyverseCertificateException(CertificateProblem.PinMismatch, "h:1", Hex, new string('0', 64), null);
            Assert.That(changed.Message, Does.Contain("does not match").And.Contain("00:00"));
            Assert.That(changed.ExpectedPin, Is.EqualTo(new string('0', 64)));
        }
    }
}
