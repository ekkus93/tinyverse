using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using Tinyverse.Host.Internal;

namespace Tinyverse.Host.Tests
{
    public class ServerLocatorTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "tinyverse-locator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        private string Touch(params string[] parts)
        {
            string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, "#!/bin/sh\n");
            return p;
        }

        private string Dir(params string[] parts)
        {
            string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(p);
            return p;
        }

        [Test]
        public void Order_ExplicitThenEnvThenStreamingAssetsThenDevelopment()
        {
            string bin = ServerLocator.BinaryName;
            string explicitPath = Touch("explicit", bin);
            string env = Touch("env", bin);
            string streaming = Dir("game", "StreamingAssets");
            string packaged = Touch("game", "StreamingAssets", "server", bin);
            string project = Dir("repo", "client");
            string release = Touch("repo", "server", "target", "release", bin);

            Assert.That(ServerLocator.Locate(explicitPath, env, streaming, project, true).Path, Is.EqualTo(explicitPath));
            ServerLocation l = ServerLocator.Locate(null, env, streaming, project, true);
            Assert.That(l.Path, Is.EqualTo(env));
            Assert.That(l.Source, Is.EqualTo(ServerLocator.EnvironmentVariable));
            Assert.That(ServerLocator.Locate(null, null, streaming, project, true).Path, Is.EqualTo(packaged));
            File.Delete(packaged);
            l = ServerLocator.Locate(null, "", streaming, project, true);
            Assert.That(l.Path, Is.EqualTo(release));
            Assert.That(l.Source, Is.EqualTo("development build"));
            Assert.That(l.Found, Is.True);
            Assert.That(l.Explanation, Does.Contain(release));
        }

        [Test]
        public void Development_WalksUpAndPrefersReleaseOverDebug()
        {
            string bin = ServerLocator.BinaryName;
            string start = Dir("a", "b", "client");
            string debug = Touch("a", "b", "server", "target", "debug", bin);
            Assert.That(ServerLocator.Locate(null, null, null, start, true).Path, Is.EqualTo(debug));
            string release = Touch("a", "server", "target", "release", bin);
            Assert.That(ServerLocator.Locate(null, null, null, start, true).Path, Is.EqualTo(release));
        }

        [Test]
        public void Development_IsSkippedInReleaseBuilds()
        {
            string start = Dir("repo", "client");
            Touch("repo", "server", "target", "release", ServerLocator.BinaryName);
            ServerLocation l = ServerLocator.Locate(null, null, null, start, false);
            Assert.That(l.Found, Is.False);
            Assert.That(l.Searched, Is.Empty);
        }

        [Test]
        public void ExplicitOrEnvPointingAtNothing_IsAnErrorNotAFallback()
        {
            string streaming = Dir("sa");
            Touch("sa", "server", ServerLocator.BinaryName);
            string missing = Path.Combine(_root, "nope", "tinyverse-server");

            ServerLocation l = ServerLocator.Locate(missing, null, streaming, null, false);
            Assert.That(l.Found, Is.False);
            Assert.That(l.Explanation, Does.Contain(missing).And.Contain("explicitly"));

            l = ServerLocator.Locate(null, missing, streaming, null, false);
            Assert.That(l.Found, Is.False);
            Assert.That(l.Explanation, Does.Contain(ServerLocator.EnvironmentVariable).And.Contain(missing));
        }

        [Test]
        public void NotFound_ExplainsEveryPlaceItLooked()
        {
            string streaming = Dir("game", "StreamingAssets");
            string start = Dir("x", "client");
            ServerLocation l = ServerLocator.Locate(null, null, streaming, start, true);
            Assert.That(l.Found, Is.False);
            Assert.That(l.Path, Is.Null);
            Assert.That(l.Searched.Count, Is.GreaterThan(3));
            Assert.That(l.Searched[0], Is.EqualTo(Path.Combine(streaming, "server", ServerLocator.BinaryName)));
            foreach (string s in l.Searched) Assert.That(l.Explanation, Does.Contain(s));
            Assert.That(l.Explanation, Does.Contain(ServerLocator.EnvironmentVariable));
            Assert.That(l.Explanation, Does.Contain("cargo build --release"));
        }

        [Test]
        public void UnityOverload_Runs()
        {
            ServerLocation l = ServerLocator.Locate();
            Assert.That(l.Explanation, Is.Not.Empty);
            TestContext.WriteLine(l.Explanation);
        }
    }

    public class WorldDirectoriesTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "tinyverse-worlds-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch (Exception) { }
        }

        [TestCase("name = \"My World\"", "My World")]
        [TestCase("  name=\"Tight\"  ", "Tight")]
        [TestCase("name = 'Literal \\n kept'", "Literal \\n kept")]
        [TestCase("name = \"Esc \\\"q\\\" \\\\ \\u00e9\" # comment", "Esc \"q\" \\ \u00e9")]
        [TestCase("# name = \"commented\"\nname = \"Real\"", "Real")]
        [TestCase("description = \"x\"\n\nname = \"Second\"\r\n", "Second")]
        [TestCase("[spawn]\nname = \"not root\"", null)]
        [TestCase("title = \"x\"", null)]
        [TestCase("name = \"unterminated", null)]
        [TestCase("name = \"\"", null)]
        [TestCase("\uFEFFname = \"Bom\"", "Bom")]
        [TestCase("\"name\" = \"Quoted key\"", "Quoted key")]
        public void ParseName(string toml, string expected)
        {
            Assert.That(WorldDirectories.ParseName(toml), Is.EqualTo(expected));
        }

        [Test]
        public void List_FindsWorldsWithNamesSorted()
        {
            Directory.CreateDirectory(Path.Combine(_root, "b"));
            File.WriteAllText(Path.Combine(_root, "b", "world.toml"), "name = \"Zebra\"\n");
            Directory.CreateDirectory(Path.Combine(_root, "a"));
            File.WriteAllText(Path.Combine(_root, "a", "world.toml"), "name = \"apple\"\n[env]\n");
            Directory.CreateDirectory(Path.Combine(_root, "nameless"));
            File.WriteAllText(Path.Combine(_root, "nameless", "world.toml"), "max_users = 16\n");
            Directory.CreateDirectory(Path.Combine(_root, "not-a-world"));
            File.WriteAllText(Path.Combine(_root, "stray.toml"), "name = \"file\"");

            var worlds = WorldDirectories.List(_root);
            Assert.That(worlds.Select(w => w.Name), Is.EqualTo(new[] { "apple", "nameless", "Zebra" }));
            Assert.That(worlds[1].NameFromFile, Is.False);
            Assert.That(worlds[0].NameFromFile, Is.True);
            Assert.That(worlds[0].Directory, Is.EqualTo(Path.Combine(_root, "a")));
            Assert.That(WorldDirectories.List(Path.Combine(_root, "missing")), Is.Empty);
            Assert.That(WorldDirectories.IsWorld(Path.Combine(_root, "a")), Is.True);
            Assert.That(WorldDirectories.IsWorld(Path.Combine(_root, "not-a-world")), Is.False);
        }

        [TestCase("My World!", "my-world")]
        [TestCase("../../etc/passwd", "etc-passwd")]
        [TestCase("", "world")]
        [TestCase(null, "world")]
        [TestCase("   ", "world")]
        [TestCase("日本", "world")]
        [TestCase("CON", "con-world")]
        [TestCase("Café Über", "cafe-uber")]
        [TestCase("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
        [TestCase("--Lots   of---gaps--", "lots-of-gaps")]
        [TestCase(".hidden", "hidden")]
        public void SafeFolderName(string input, string expected)
        {
            Assert.That(WorldDirectories.SafeFolderName(input), Is.EqualTo(expected));
        }

        [Test]
        public void SafeFolderName_IsBounded()
        {
            string n = WorldDirectories.SafeFolderName(new string('x', 500));
            Assert.That(n.Length, Is.EqualTo(64));
            n = WorldDirectories.SafeFolderName(new string('a', 63) + " b");
            Assert.That(n.Length, Is.LessThanOrEqualTo(64));
            Assert.That(n, Does.Not.EndWith("-"));
        }

        [Test]
        public void NewWorldDirectory_AvoidsExistingFolders()
        {
            Assert.That(WorldDirectories.NewWorldDirectory(_root, "My World"), Is.EqualTo(Path.Combine(_root, "my-world")));
            Directory.CreateDirectory(Path.Combine(_root, "my-world"));
            Directory.CreateDirectory(Path.Combine(_root, "my-world-2"));
            Assert.That(WorldDirectories.NewWorldDirectory(_root, "My World"), Is.EqualTo(Path.Combine(_root, "my-world-3")));
            Assert.That(Directory.Exists(Path.Combine(_root, "my-world-3")), Is.False, "must not create anything");
        }

        [Test]
        public void DefaultRoot_IsWorldsUnderBase()
        {
            Assert.That(WorldDirectories.DefaultRoot(_root), Is.EqualTo(Path.Combine(_root, "worlds")));
        }
    }

    public class PortCheckTests
    {
        [Test]
        public void FreePort_IsAvailable()
        {
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Assert.That(PortCheck.Check(port), Is.Null);
            Assert.That(PortCheck.IsAvailable(port, "127.0.0.1"), Is.True);
        }

        [Test]
        public void ListeningPort_IsNotAvailable()
        {
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            try
            {
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                Assert.That(PortCheck.IsAvailable(port), Is.False);
                Assert.That(PortCheck.Check(port), Does.Contain($"Port {port} is already in use"));
            }
            finally
            {
                l.Stop();
            }
        }

        [Test]
        public void LoopbackListener_BlocksWildcard()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                Assert.That(PortCheck.IsAvailable(port, "0.0.0.0"), Is.False);
            }
            finally
            {
                l.Stop();
            }
        }

        [Test]
        public void BadInput_IsExplained()
        {
            Assert.That(PortCheck.Check(0), Does.Contain("between 1 and 65535"));
            Assert.That(PortCheck.Check(70000), Does.Contain("between 1 and 65535"));
            Assert.That(PortCheck.Check(7878, "not-an-ip"), Does.Contain("not an IP address"));
            Assert.That(PortCheck.Check(7878, "203.0.113.7"), Does.Contain("not an address of this computer"));
        }
    }

    public class ParsingTests
    {
        [TestCase("2026-11-26T10:00:00Z INFO tinyverse_server: listening on ws://0.0.0.0:7878/ws", "0.0.0.0", 7878)]
        [TestCase("listening on ws://127.0.0.1:9000/ws", "127.0.0.1", 9000)]
        [TestCase("INFO listening on ws://[::]:7878/ws (world: x)", "[::]", 7878)]
        [TestCase("listening on ws://my-host:1/ws", "my-host", 1)]
        public void ReadyLine_Parses(string line, string host, int port)
        {
            Assert.That(ReadyLine.TryParse(line, out string h, out int p), Is.True);
            Assert.That(h, Is.EqualTo(host));
            Assert.That(p, Is.EqualTo(port));
        }

        [TestCase("listening on ws://0.0.0.0:99999/ws")]
        [TestCase("listening on http://0.0.0.0:7878/")]
        [TestCase("not listening yet")]
        [TestCase(null)]
        public void ReadyLine_RejectsOthers(string line)
        {
            Assert.That(ReadyLine.TryParse(line, out _, out _), Is.False);
        }

        [Test]
        public void Redactor_MasksKnownAndLabelledSecrets()
        {
            var r = new Redactor();
            r.Add("hunter2");
            r.Add("tvo_abc");
            Assert.That(r.Apply("pw hunter2 and tvo_abc!"), Is.EqualTo("pw [redacted] and [redacted]!"));
            Assert.That(r.Apply("owner token: zzzz"), Is.EqualTo("owner token: [redacted]"));
            Assert.That(r.Apply("password=abc"), Is.EqualTo("password=[redacted]"));
            Assert.That(r.Apply("token a1b2c3d4e5f6g7h8i9"), Is.EqualTo("token [redacted]"));
            Assert.That(r.Apply("use token create to make one"), Is.EqualTo("use token create to make one"));
            Assert.That(r.Apply("\u001b[32mINFO\u001b[0m ok"), Is.EqualTo("INFO ok"));
        }

        [Test]
        public void LineRing_KeepsTheLastLines()
        {
            var ring = new LineRing(3);
            for (int i = 1; i <= 5; i++) ring.Add(i.ToString());
            Assert.That(ring.Tail(10), Is.EqualTo(new[] { "3", "4", "5" }));
            Assert.That(ring.Tail(2), Is.EqualTo(new[] { "4", "5" }));
            Assert.That(ring.Count, Is.EqualTo(3));
        }
    }
}
