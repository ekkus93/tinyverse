using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Tinyverse.Net.Tests
{
    public class WorldAddressTests
    {
        [TestCase("192.168.1.5", "ws://192.168.1.5:7878/ws")]
        [TestCase("  192.168.1.5  ", "ws://192.168.1.5:7878/ws")]
        [TestCase("myhost:9000", "ws://myhost:9000/ws")]
        [TestCase("MyHost.Local", "ws://myhost.local:7878/ws")]
        [TestCase("localhost", "ws://localhost:7878/ws")]
        [TestCase("ws://host/ws", "ws://host:7878/ws")]
        [TestCase("ws://host", "ws://host:7878/ws")]
        [TestCase("ws://host/", "ws://host:7878/ws")]
        [TestCase("WS://host:80/ws", "ws://host:80/ws")]
        [TestCase("http://host:9000", "ws://host:9000/ws")]
        [TestCase("wss://secure.example:443/ws", "wss://secure.example:443/ws")]
        [TestCase("host:7878/custom/path", "ws://host:7878/custom/path")]
        [TestCase("[::1]", "ws://[::1]:7878/ws")]
        [TestCase("[::1]:9000", "ws://[::1]:9000/ws")]
        public void Normalizes(string input, string expected)
        {
            Assert.That(WorldAddress.TryNormalize(input, out var url, out var error), Is.True, error);
            Assert.That(url, Is.EqualTo(expected));
            Assert.That(WorldAddress.Normalize(input), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("my host")]
        [TestCase("ftp://host")]
        [TestCase("host:0")]
        [TestCase("host:70000")]
        [TestCase("host:abc")]
        [TestCase("host:")]
        [TestCase(":9000")]
        [TestCase("::1")]
        [TestCase("[::1")]
        [TestCase("user@host")]
        [TestCase("host/ws?x=1")]
        [TestCase("ho$t")]
        public void RejectsWithAClearError(string input)
        {
            Assert.That(WorldAddress.TryNormalize(input, out var url, out var error), Is.False);
            Assert.That(url, Is.Null);
            Assert.That(error, Is.Not.Empty);
            Assert.Throws<FormatException>(() => WorldAddress.Normalize(input));
        }
    }

    public class FileTokenStoreTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "tv-tokens-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        [Test]
        public void RoundTripsAcrossInstances()
        {
            var t1 = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
            var t2 = t1.AddMinutes(5);
            var store = new FileTokenStore(dir);
            store.Remember("192.168.1.5", "secret-a", "Alice", t1);
            store.Remember("ws://other:9000/ws", "secret-b", "Bob", t2);

            var reread = new FileTokenStore(dir);
            var a = reread.Get("ws://192.168.1.5:7878/ws");
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Url, Is.EqualTo("ws://192.168.1.5:7878/ws"));
            Assert.That(a.SessionToken, Is.EqualTo("secret-a"));
            Assert.That(a.DisplayName, Is.EqualTo("Alice"));
            Assert.That(a.LastConnected, Is.EqualTo(t1));
            Assert.That(reread.GetRecentWorlds().Select(r => r.DisplayName), Is.EqualTo(new[] { "Bob", "Alice" }));
        }

        [Test]
        public void EmptyTokenKeepsTheStoredOneAndForgetTokenDropsIt()
        {
            var store = new FileTokenStore(dir);
            var now = DateTimeOffset.UtcNow;
            store.Remember("h", "tok", "A", now);
            store.Remember("h", "", "B", now.AddSeconds(1));
            Assert.That(new FileTokenStore(dir).Get("h").SessionToken, Is.EqualTo("tok"));
            Assert.That(new FileTokenStore(dir).Get("h").DisplayName, Is.EqualTo("B"));

            store.ForgetToken("h");
            var r = new FileTokenStore(dir).Get("h");
            Assert.That(r.SessionToken, Is.EqualTo(""));
            Assert.That(r.DisplayName, Is.EqualTo("B"));

            store.Remove("h");
            Assert.That(new FileTokenStore(dir).Get("h"), Is.Null);
            Assert.That(new FileTokenStore(dir).GetRecentWorlds(), Is.Empty);
        }

        [Test]
        public void MissingFileIsEmpty()
        {
            var store = new FileTokenStore(dir);
            Assert.That(store.Get("h"), Is.Null);
            Assert.That(store.GetRecentWorlds(), Is.Empty);
        }

        [TestCase("{ this is not json")]
        [TestCase("")]
        [TestCase("[1,2,3]")]
        [TestCase("{\"worlds\": 5}")]
        public void CorruptFileIsEmptyAndIsReplacedOnSave(string content)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FileTokenStore.DefaultFileName), content);
            var store = new FileTokenStore(dir);
            Assert.That(store.GetRecentWorlds(), Is.Empty);
            store.Remember("h", "tok", "A", DateTimeOffset.UtcNow);
            Assert.That(new FileTokenStore(dir).Get("h").SessionToken, Is.EqualTo("tok"));
        }

        [Test]
        public void FileIsOwnerReadableOnly()
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) Assert.Ignore("POSIX permissions only.");
            var store = new FileTokenStore(dir);
            store.Remember("h", "tok", "A", DateTimeOffset.UtcNow);
            Assert.That(Mode(store.FilePath), Is.EqualTo("600"));
            store.Remember("h", "tok2", "A", DateTimeOffset.UtcNow); // replace path keeps it
            Assert.That(Mode(store.FilePath), Is.EqualTo("600"));
            Assert.That(File.Exists(store.FilePath + ".tmp"), Is.False);
        }

        static string Mode(string path)
        {
            var psi = new ProcessStartInfo("stat", $"-c %a \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return output;
            }
        }
    }
}
