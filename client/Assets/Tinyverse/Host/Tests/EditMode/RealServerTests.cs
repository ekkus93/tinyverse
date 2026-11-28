using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Tinyverse.Host.Platform;

namespace Tinyverse.Host.Tests
{
    /// <summary>
    /// The same paths as the fake-server tests, against the real <c>tinyverse-server</c> found by the
    /// development search of <see cref="ServerLocator"/> (build it with <c>cargo build --workspace</c> in server/).
    /// Skipped, with the search explanation, when it is not built.
    /// </summary>
    public class RealServerTests : FakeServerFixture
    {
        private string _real;

        [OneTimeSetUp]
        public void FindReal()
        {
            ServerLocation l = ServerLocator.Locate(null, null, null, Directory.GetCurrentDirectory(), true);
            if (!l.Found) Assert.Ignore("Real server not built; skipping. " + l.Explanation);
            _real = l.Path;
            TestContext.Progress.WriteLine("Real server tests use " + _real);
        }

        private ServerProcess Real(ServerProcessOptions o = null) => NewServer(o, _real);

        private static string Info(int port)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            {
                c.ReceiveTimeout = 5000;
                NetworkStream s = c.GetStream();
                byte[] req = Encoding.ASCII.GetBytes("GET /info HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                s.Write(req, 0, req.Length);
                var ms = new MemoryStream();
                s.CopyTo(ms);
                string response = Encoding.UTF8.GetString(ms.ToArray());
                Assert.That(response, Does.StartWith("HTTP/1."));
                Assert.That(response, Does.Contain(" 200 "));
                return response.Substring(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
            }
        }

        [Test]
        public void InitThenRun_ThenRestartOnTheExistingWorld()
        {
            var server = Real();
            var rec = new Recorder(server);
            HostSettings settings = Settings("real", "Real Test World");
            HostStartResult r = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);

            Assert.That(r.Tls, Is.False);
            Assert.That(r.LocalUrl, Is.EqualTo($"ws://127.0.0.1:{settings.Port}/ws"));
            Assert.That(r.Port, Is.EqualTo(settings.Port));
            string token = File.ReadAllText(Path.Combine(settings.WorldDirectory, "owner.token"));
            Assert.That(token, Does.EndWith("\n"), "the real file ends with a newline");
            Assert.That(r.OwnerToken, Is.EqualTo(token.Trim()));
            Assert.That(r.OwnerToken, Does.Match("^[0-9a-f]{32,}$"));
            Assert.That(WorldDirectories.ReadName(settings.WorldDirectory), Is.EqualTo("Real Test World"));
            Assert.That(WorldDirectories.List(Temp).Single().Name, Is.EqualTo("Real Test World"));
            string info = Info(settings.Port);
            Assert.That(info, Does.Contain("\"name\":\"Real Test World\""));
            Assert.That(info, Does.Contain("\"password_required\":false"));

            server.Pump();
            Assert.That(rec.States, Is.EqualTo(new[] { HostState.Initialising, HostState.Starting, HostState.Running }));
            Assert.That(rec.AllText, Does.Not.Contain(r.OwnerToken));

            StopResult stop = Wait(server.StopAsync(TimeSpan.FromSeconds(10)), 30);
            Assert.That(stop.Graceful, Is.True, stop.Message);
            Assert.That(stop.ExitCode, Is.EqualTo(0));

            // Restart: no init (the real init would refuse), same owner token.
            HostStartResult again = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(again.OwnerToken, Is.EqualTo(r.OwnerToken));
            Assert.That(server.Launches.Count(l => l.Arguments.FirstOrDefault() == "init"), Is.EqualTo(1));
        }

        [Test]
        public void Templates_AreListedAndANewWorldStartsFromOne()
        {
            var templates = Wait(WorldTemplates.ListAsync(_real), 30);
            Assert.That(templates.First().Name, Is.EqualTo(WorldTemplates.DefaultName), "empty comes first and is the default");
            Assert.That(templates.Select(t => t.Name), Is.SupersetOf(new[] { "room", "house", "town", "island" }));
            Assert.That(templates.All(t => t.Description.Length > 0 && t.Title.Length > 0), Is.True);
            var house = templates.Single(t => t.Name == "house");
            Assert.That(house.ObjectCount, Is.GreaterThan(20));

            var server = Real();
            HostSettings settings = Settings("templated", "Templated World");
            settings.Template = "house";
            Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(WorldDirectories.ReadName(settings.WorldDirectory), Is.EqualTo("Templated World"));
            Assert.That(server.Launches.First().Arguments, Does.Contain("--template"));
            string info = Info(settings.Port);
            Assert.That(info, Does.Contain("\"name\":\"Templated World\""));
            Assert.That(Wait(server.StopAsync(TimeSpan.FromSeconds(10)), 30).Graceful, Is.True);
        }

        [Test]
        public void GracefulStop_ExitsZeroWithinTimeout()
        {
            var server = Real();
            Wait(server.StartAsync(Settings()), 60);
            int pid = server.ProcessId.Value;
            Track(pid);
            var sw = Stopwatch.StartNew();
            StopResult stop = server.Stop(TimeSpan.FromSeconds(10));
            Assert.That(stop.Graceful, Is.True, stop.Message);
            Assert.That(stop.Killed, Is.False);
            Assert.That(stop.ExitCode, Is.EqualTo(0));
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(10));
            Assert.That(IsAlive(pid), Is.False);
            Assert.That(server.GetRecentLines().Any(l => l.Contains("stopped")), Is.True);
            TestContext.WriteLine($"stop took {sw.ElapsedMilliseconds} ms");
        }

        [Test]
        public void SecondServerOnTheSamePort_IsReportedAsPortInUse()
        {
            var first = Real();
            HostSettings a = Settings("a");
            Wait(first.StartAsync(a), 60);
            Track(first.ProcessId);
            Assert.That(PortCheck.IsAvailable(a.Port), Is.False);

            var second = Real();
            HostSettings b = Settings("b");
            b.Port = a.Port;
            var e = WaitFault(second.StartAsync(b), 60) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.PortInUse), e.Message);
            Assert.That(e.Message, Does.Contain($"Port {a.Port} is already in use"));
            Assert.That(second.State, Is.EqualTo(HostState.Failed));
            TestContext.WriteLine(e.Message);
        }

        [Test]
        public void PasswordThroughTheEnvironment_IsRequiredAfterwards()
        {
            const string password = "real-secret-pw-42";
            var server = Real();
            var rec = new Recorder(server);
            HostSettings settings = Settings(password: password);
            Wait(server.StartAsync(settings), 60);
            int pid = server.ProcessId.Value;
            Track(pid);
            Assert.That(Info(settings.Port), Does.Contain("\"password_required\":true"));

            // Neither the command line nor the environment of the running server holds it.
            string cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            Assert.That(cmdline, Does.Contain(" run "));
            Assert.That(cmdline, Does.Not.Contain(password));
            Assert.That(File.ReadAllText($"/proc/{pid}/environ"), Does.Not.Contain("TINYVERSE_PASSWORD"));
            Assert.That(server.Launches.SelectMany(l => l.Arguments), Has.None.Contains(password));
            server.Stop(TimeSpan.FromSeconds(10));

            // Clear it, then set another one through password --set-from-env.
            settings.Password = "";
            Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(Info(settings.Port), Does.Contain("\"password_required\":false"));
            server.Stop(TimeSpan.FromSeconds(10));

            settings.Password = "second-secret-pw";
            Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(Info(settings.Port), Does.Contain("\"password_required\":true"));
            server.Pump();
            Assert.That(rec.AllText, Does.Not.Contain(password).And.Not.Contain("second-secret-pw"));
            Assert.That(rec.Lines.Any(l => l.Contains("join password set")), Is.True);
        }

        [Test]
        public void SelfSignedTls_IsServedAsWss_AndCanBeDisabled()
        {
            var server = Real();
            HostSettings settings = Settings("tls");
            Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            server.Stop(TimeSpan.FromSeconds(10));
            using (var p = Process.Start(new ProcessStartInfo(_real) { UseShellExecute = false, ArgumentList = { "tls", "self-signed", settings.WorldDirectory }, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                Assert.That(p.ExitCode, Is.EqualTo(0));
            }

            HostStartResult r = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(r.Tls, Is.True);
            Assert.That(r.LocalUrl, Is.EqualTo($"wss://127.0.0.1:{settings.Port}/ws"));
            server.Stop(TimeSpan.FromSeconds(10));

            settings.DisableTls = true;
            r = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(r.Tls, Is.False);
            Assert.That(Info(settings.Port), Does.Contain("\"name\""));
        }

        [Test]
        public void CreateCertificate_MakesOneOnce_AndReportsItsPin()
        {
            var server = Real();
            HostSettings settings = Settings("tls-auto");
            settings.CreateCertificate = true;
            HostStartResult r = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(r.Tls, Is.True);
            Assert.That(File.Exists(Path.Combine(settings.WorldDirectory, "cert.pem")), Is.True);
            string printed;
            using (var p = Process.Start(new ProcessStartInfo(_real) { UseShellExecute = false, ArgumentList = { "tls", "fingerprint", settings.WorldDirectory }, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                printed = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                Assert.That(p.ExitCode, Is.EqualTo(0));
            }
            Assert.That(printed, Does.StartWith("tls certificate sha256=" + r.CertificateSha256));
            Assert.That(r.LocalJoinUrl, Is.EqualTo(r.LocalUrl + "#sha256=" + r.CertificateSha256));
            string first = r.CertificateSha256;
            server.Stop(TimeSpan.FromSeconds(10));

            r = Wait(server.StartAsync(settings), 60);
            Track(server.ProcessId);
            Assert.That(r.CertificateSha256, Is.EqualTo(first), "the certificate is kept across restarts");
            server.Stop(TimeSpan.FromSeconds(10));
        }

        [Test]
        public void ParentDeath_RealServerShutsDownByItself()
        {
            var platform = ServerPlatform.Current as PosixServerPlatform;
            if (platform?.Setpriv == null) Assert.Ignore("setpriv with --pdeathsig is not installed");

            // Create the world through ServerProcess, then stop it.
            var host = Real();
            HostSettings settings = Settings("orphan");
            Wait(host.StartAsync(settings), 60);
            Track(host.ProcessId);
            host.Stop(TimeSpan.FromSeconds(10));

            // 1. stderr to a file: the server gets SIGTERM when its parent is SIGKILLed and runs its clean shutdown.
            string log = Path.Combine(Temp, "orphan.log");
            int pid = LaunchUnderStandInParent(settings, "\"$@\" 2>\"$LOG\" >/dev/null & echo $!; wait", log, out Process parent);
            Assert.That(WaitUntil(() => File.Exists(log) && File.ReadAllText(log).Contains("listening on"), TimeSpan.FromSeconds(30)), Is.True);
            parent.Kill();
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(10)), Is.True, "the real server outlived its parent");
            string text = File.ReadAllText(log);
            Assert.That(text, Does.Contain("shutting down"));
            Assert.That(text, Does.Contain("stopped"), "clean shutdown did not finish:\n" + text);

            // 2. stdout and stderr to pipes whose reader is gone: the server must neither die of it nor hang.
            string fifo = Path.Combine(Temp, "err.fifo");
            using (var mk = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo", "\"" + fifo + "\"") { UseShellExecute = false }))
                mk.WaitForExit(5000);
            pid = LaunchUnderStandInParent(settings,
                "head -n 1 \"$LOG\" >/dev/null & \"$@\" 2>\"$LOG\" | head -n 1 >/dev/null & echo $!; wait", fifo, out parent, pipeline: true);
            Thread.Sleep(3000);
            Assert.That(IsAlive(pid), Is.True, "the server died when its log pipes closed");
            Assert.That(Info(settings.Port), Does.Contain("\"name\""));
            parent.Kill();
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(10)), Is.True, "the real server outlived its parent");

            // The world still opens normally afterwards.
            Wait(host.StartAsync(settings), 60);
            Track(host.ProcessId);
            Assert.That(host.Stop(TimeSpan.FromSeconds(10)).ExitCode, Is.EqualTo(0));
        }

        /// <summary>
        /// Runs <c>setpriv --pdeathsig TERM -- server run</c> as a child of a shell (the stand-in client) and returns
        /// the server's pid. For a pipeline, <c>$!</c> is the last command, so the server is found through /proc.
        /// </summary>
        private int LaunchUnderStandInParent(HostSettings settings, string script, string logPath, out Process parent, bool pipeline = false)
        {
            string file = _real;
            var args = new List<string> { "run", settings.WorldDirectory, "--port", settings.Port.ToString() };
            Assert.That(((PosixServerPlatform)ServerPlatform.Current).TryWrapForParentDeath(ref file, args), Is.True);
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["LOG"] = logPath;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("stand-in-client");
            psi.ArgumentList.Add(file);
            foreach (string a in args) psi.ArgumentList.Add(a);
            parent = Process.Start(psi);
            TrackExtra(parent);
            int reported = int.Parse(parent.StandardOutput.ReadLine().Trim());
            int pid = reported;
            if (pipeline)
            {
                string dir = settings.WorldDirectory;
                Assert.That(WaitUntil(() => (pid = FindServer(dir)) > 0, TimeSpan.FromSeconds(10)), Is.True, "server not found");
                Track(reported);
            }
            Track(pid);
            Assert.That(WaitUntil(() => PortCheck.IsAvailable(settings.Port) == false, TimeSpan.FromSeconds(30)), Is.True, "server did not listen");
            return pid;
        }

        private int FindServer(string dir)
        {
            foreach (string p in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(p), out int pid)) continue;
                try
                {
                    string[] argv = File.ReadAllText(Path.Combine(p, "cmdline")).Split('\0');
                    if (argv.Length > 2 && argv[0] == _real && argv[1] == "run" && argv[2] == dir && IsAlive(pid)) return pid;
                }
                catch (Exception) { }
            }
            return 0;
        }
    }
}
