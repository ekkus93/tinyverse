using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Tinyverse.Host.Tests
{
    public class ServerProcessTests : FakeServerFixture
    {
        private static string Marker(HostSettings s) => Path.Combine(s.WorldDirectory, "shutdown.marker");

        [Test]
        public void InitThenRun_OnFreshDirectory_ReturnsUrlAddressesAndToken()
        {
            var server = NewServer();
            HostSettings settings = Settings("fresh", "Phil's \"big\" $HOME world \\ `x`");
            Assert.That(WorldDirectories.IsWorld(settings.WorldDirectory), Is.False);

            HostStartResult r = Wait(server.StartAsync(settings));
            Track(server.ProcessId);

            Assert.That(server.State, Is.EqualTo(HostState.Running));
            Assert.That(r.LocalUrl, Is.EqualTo($"ws://127.0.0.1:{settings.Port}/ws"));
            Assert.That(r.Port, Is.EqualTo(settings.Port));
            string token = File.ReadAllText(Path.Combine(settings.WorldDirectory, "owner.token")).Trim();
            Assert.That(r.OwnerToken, Is.EqualTo(token));
            Assert.That(token, Does.StartWith("tvo_"));
            Assert.That(r.ToString(), Does.Not.Contain(token));
            Assert.That(server.Current, Is.SameAs(r));
            // The name survives the trip through the command line byte for byte.
            Assert.That(WorldDirectories.ReadName(settings.WorldDirectory), Is.EqualTo(settings.WorldName));
            Assert.That(File.ReadAllText(Path.Combine(settings.WorldDirectory, "world.toml")), Does.Contain("password_set = false"));

            Assert.That(r.LanUrls.Count, Is.EqualTo(r.LanAddresses.Count));
            for (int i = 0; i < r.LanAddresses.Count; i++)
            {
                IPAddress ip = IPAddress.Parse(r.LanAddresses[i]);
                Assert.That(ip.AddressFamily, Is.EqualTo(System.Net.Sockets.AddressFamily.InterNetwork));
                Assert.That(IPAddress.IsLoopback(ip), Is.False);
                Assert.That(r.LanUrls[i], Is.EqualTo($"ws://{r.LanAddresses[i]}:{settings.Port}/ws"));
            }
            TestContext.WriteLine("LAN: " + string.Join(", ", r.LanUrls));

            var rec = new Recorder(server);
            server.Pump();
            Assert.That(rec.States, Is.EqualTo(new[] { HostState.Initialising, HostState.Starting, HostState.Running }));
            Assert.That(rec.Lines.Any(l => l.Contains("Created world")), Is.True);
            Assert.That(rec.Lines.Any(l => l.Contains("listening on ws://")), Is.True);
        }

        [Test]
        public void ExistingWorld_RunsWithoutInit()
        {
            HostSettings settings = Settings("existing");
            var first = NewServer();
            Wait(first.StartAsync(settings));
            Track(first.ProcessId);
            Assert.That(Wait(first.StopAsync(TimeSpan.FromSeconds(5))).Graceful, Is.True);
            string tokenBefore = File.ReadAllText(Path.Combine(settings.WorldDirectory, "owner.token"));

            // The fake init refuses an existing world, so a start that ran init again would fail.
            var server = NewServer();
            var rec = new Recorder(server);
            HostStartResult r = Wait(server.StartAsync(settings));
            Track(server.ProcessId);
            server.Pump();
            Assert.That(rec.States, Is.EqualTo(new[] { HostState.Starting, HostState.Running }));
            Assert.That(rec.Lines.Any(l => l.Contains("Creating a new world")), Is.False);
            Assert.That(r.OwnerToken, Is.EqualTo(tokenBefore.Trim()));
        }

        [Test]
        public void ReadyLineOnStdout_IsAlsoRecognised()
        {
            var server = NewServer(Options("FAKE_READY_STREAM", "stdout"));
            HostStartResult r = Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            Assert.That(r.LocalUrl, Does.StartWith("ws://127.0.0.1:"));
        }

        [Test]
        public void SlowStart_StaysStartingUntilReady()
        {
            var server = NewServer(Options("FAKE_START_DELAY", "1.5"));
            var task = server.StartAsync(Settings());
            Assert.That(WaitUntil(() => server.State == HostState.Starting, TimeSpan.FromSeconds(5)), Is.True);
            Track(server.ProcessId);
            Thread.Sleep(500);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(server.State, Is.EqualTo(HostState.Starting));
            Wait(task);
            Assert.That(server.State, Is.EqualTo(HostState.Running));
        }

        [Test]
        public void PortInUse_FailsWithReadableMessage()
        {
            var server = NewServer(Options("FAKE_MODE", "port_in_use"));
            var rec = new Recorder(server);
            HostSettings settings = Settings();
            var e = WaitFault(server.StartAsync(settings)) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.PortInUse));
            Assert.That(e.Message, Does.Contain($"Port {settings.Port} is already in use"));
            Assert.That(e.Message, Does.Contain("Address already in use"));
            Assert.That(e.ExitCode, Is.EqualTo(1));
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
            Assert.That(server.LastError, Is.EqualTo(e.Message));
            server.Pump();
            Assert.That(rec.Failures, Is.EqualTo(new[] { e.Message }));
            Assert.That(rec.States.Last(), Is.EqualTo(HostState.Failed));
            Assert.That(rec.Order.IndexOf("failed"), Is.EqualTo(rec.Order.IndexOf("state:Failed") - 1));
        }

        [Test]
        public void ExitBeforeReady_ReportsExitCodeAndOutput()
        {
            var server = NewServer(Options("FAKE_MODE", "exit_early"));
            var e = WaitFault(server.StartAsync(Settings())) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.ExitedBeforeReady));
            Assert.That(e.Message, Does.Contain("exit code 1"));
            Assert.That(e.Message, Does.Contain("world.db is locked"));
            Assert.That(e.RecentOutput.Any(l => l.Contains("world.db is locked")), Is.True);
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
        }

        [Test]
        public void NeverReady_TimesOutAndStopsTheProcess()
        {
            ServerProcessOptions o = Options("FAKE_MODE", "never_ready");
            o.ReadyTimeout = TimeSpan.FromSeconds(1.5);
            var server = NewServer(o);
            HostSettings settings = Settings();
            var task = server.StartAsync(settings);
            Assert.That(WaitUntil(() => server.ProcessId.HasValue && server.State == HostState.Starting, TimeSpan.FromSeconds(5)), Is.True);
            int pid = server.ProcessId.Value;
            Track(pid);
            var e = WaitFault(task) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.ReadyTimeout));
            Assert.That(e.Message, Does.Contain("did not report that it was ready within 1.5 seconds"));
            Assert.That(e.Message, Does.Contain("loading world"));
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(3)), Is.True, "timed-out server still running");
        }

        [Test]
        public void GracefulStop_WritesMarker()
        {
            var server = NewServer();
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            int pid = server.ProcessId.Value;
            Track(pid);
            server.Pump(); // nobody listening yet: drains the start-up events
            var rec = new Recorder(server);

            StopResult r = Wait(server.StopAsync(TimeSpan.FromSeconds(5)));
            Assert.That(r.WasRunning, Is.True);
            Assert.That(r.Graceful, Is.True);
            Assert.That(r.Killed, Is.False);
            Assert.That(r.ExitCode, Is.EqualTo(0));
            Assert.That(File.ReadAllText(Marker(settings)).Trim(), Is.EqualTo("graceful"));
            Assert.That(server.State, Is.EqualTo(HostState.Stopped));
            Assert.That(server.Current, Is.Null);
            Assert.That(IsAlive(pid), Is.False);

            server.Pump();
            Assert.That(rec.States, Is.EqualTo(new[] { HostState.Stopping, HostState.Stopped }));
            Assert.That(rec.Lines.Any(l => l.Contains("flushing world")), Is.True);

            // Stopping again is harmless.
            Assert.That(server.Stop(TimeSpan.FromSeconds(1)).WasRunning, Is.False);
        }

        [Test]
        public void IgnoredSigterm_EndsInHardKillAfterTimeout()
        {
            var server = NewServer(Options("FAKE_MODE", "ignore_term"));
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            int pid = server.ProcessId.Value;
            Track(pid);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            StopResult r = server.Stop(TimeSpan.FromSeconds(1));
            Assert.That(sw.Elapsed.TotalSeconds, Is.GreaterThanOrEqualTo(0.9));
            Assert.That(r.Killed, Is.True);
            Assert.That(r.Graceful, Is.False);
            Assert.That(r.Message, Does.Contain("killed"));
            Assert.That(File.Exists(Marker(settings)), Is.False);
            Assert.That(server.State, Is.EqualTo(HostState.Stopped));
            Assert.That(IsAlive(pid), Is.False);
            Assert.That(server.GetRecentLines().Any(l => l.StartsWith("[host]") && l.Contains("killed")), Is.True);
        }

        [Test]
        public void UnexpectedExit_WhileRunning_IsReported()
        {
            var server = NewServer(Options("FAKE_CRASH_AFTER", "1"));
            var rec = new Recorder(server);
            Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            Assert.That(WaitUntil(() => server.State == HostState.Failed, TimeSpan.FromSeconds(6)), Is.True);
            Assert.That(server.LastError, Does.Contain("stopped unexpectedly (exit code 3)"));
            Assert.That(server.LastError, Does.Contain("panicked"));
            Assert.That(server.ProcessId, Is.Null);
            server.Pump();
            Assert.That(rec.States.Last(), Is.EqualTo(HostState.Failed));
            Assert.That(rec.Failures.Single(), Is.EqualTo(server.LastError));

            // It can be started again afterwards.
            Assert.That(server.Stop(TimeSpan.FromSeconds(1)).WasRunning, Is.False);
        }

        [Test]
        public void ChattyOutput_RingIsBoundedAndInOrder()
        {
            ServerProcessOptions o = Options("FAKE_CHATTY", "5000");
            o.LogCapacity = 100;
            var server = NewServer(o);
            var rec = new Recorder(server);
            Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            Assert.That(WaitUntil(() => server.GetRecentLines().Any(l => l == "chatty 5000"), TimeSpan.FromSeconds(10)), Is.True);

            IReadOnlyList<string> recent = server.GetRecentLines();
            Assert.That(recent.Count, Is.LessThanOrEqualTo(100));
            AssertChattyAscending(recent);

            server.Pump();
            Assert.That(rec.Lines.Count, Is.LessThanOrEqualTo(101));
            Assert.That(rec.Lines[0], Does.Contain("earlier lines were skipped"));
            AssertChattyAscending(rec.Lines);
            Assert.That(rec.Lines, Does.Contain("chatty 5000"));
        }

        private static void AssertChattyAscending(IEnumerable<string> lines)
        {
            int last = 0;
            foreach (string l in lines)
            {
                if (!l.StartsWith("chatty ")) continue;
                int n = int.Parse(l.Substring(7));
                Assert.That(n, Is.EqualTo(last == 0 ? n : last + 1), "out of order or gap after " + last);
                last = n;
            }
            Assert.That(last, Is.GreaterThan(0));
        }

        [Test]
        public void NoEventsBeforePump_ThenAllOnMainThreadInOrder()
        {
            var server = NewServer();
            var rec = new Recorder(server);
            Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            Thread.Sleep(300);
            Assert.That(rec.Order, Is.Empty, "an event was raised before Pump");

            server.Pump();
            Assert.That(rec.Order, Is.Not.Empty);
            Assert.That(rec.Threads, Is.EqualTo(new HashSet<int> { MainThreadId }));
            int init = rec.Order.IndexOf("state:Initialising");
            int starting = rec.Order.IndexOf("state:Starting");
            int running = rec.Order.IndexOf("state:Running");
            Assert.That(init, Is.LessThan(starting));
            Assert.That(starting, Is.LessThan(running));
            int readyLine = rec.Lines.FindIndex(l => l.Contains("listening on"));
            Assert.That(readyLine, Is.GreaterThanOrEqualTo(0));

        }

        [Test]
        public void Dispose_StopsTheProcessGracefully()
        {
            var server = NewServer();
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            int pid = server.ProcessId.Value;
            Track(pid);
            server.Dispose();
            Assert.That(IsAlive(pid), Is.False);
            Assert.That(File.Exists(Marker(settings)), Is.True);
            Assert.Throws<ObjectDisposedException>(() => server.StartAsync(settings));
        }

        [Test]
        public void Secrets_NeverAppearInLogsOrMessages()
        {
            const string password = "hunter2-very-secret";
            var server = NewServer(Options("FAKE_LEAK", "1"));
            var rec = new Recorder(server);
            HostSettings settings = Settings(password: password);
            HostStartResult r = Wait(server.StartAsync(settings));
            Track(server.ProcessId);
            Assert.That(WaitUntil(() => server.GetRecentLines().Any(l => l.Contains("raw ")), TimeSpan.FromSeconds(5)), Is.True);
            string token = r.OwnerToken;
            Assert.That(token, Is.Not.Null.And.Not.Empty);
            Assert.That(File.ReadAllText(Path.Combine(settings.WorldDirectory, "world.toml")), Does.Contain("password_set = true"));
            server.Pump();
            Wait(server.StopAsync(TimeSpan.FromSeconds(5)));
            server.Pump();

            // The password is never on a command line: only init/password get it, through the environment.
            foreach (var launch in server.Launches)
            {
                Assert.That(launch.Arguments, Has.None.Contains(password), string.Join(" ", launch.Arguments));
                Assert.That(launch.PasswordInEnvironment, Is.EqualTo(launch.Arguments[0] == "init"));
            }
            Assert.That(server.Launches.Select(l => l.Arguments.Contains("run") ? "run" : l.Arguments[0]),
                Is.EqualTo(new[] { "init", "run" }));
            Assert.That(File.ReadAllText(Path.Combine(settings.WorldDirectory, "run.env")).Trim(), Is.EqualTo("unset"));

            string all = rec.AllText + string.Join("\n", server.GetRecentLines()) + settings + r;
            Assert.That(all, Does.Not.Contain(password));
            Assert.That(all, Does.Not.Contain(token));
            Assert.That(all, Does.Contain("[redacted]"), "the fake leaked secrets, so something should be masked");

            // A password change on an existing world, and a failure with the password in its output.
            var again = NewServer(Options("FAKE_LEAK", "1"));
            var rec2 = new Recorder(again);
            Wait(again.StartAsync(Settings(password: "another-secret-pw")));
            Track(again.ProcessId);
            again.Pump();
            Assert.That(rec2.AllText, Does.Not.Contain("another-secret-pw"));
            var pwLaunch = again.Launches.First();
            Assert.That(pwLaunch.Arguments, Is.EqualTo(new[] { "password", Path.GetFullPath(settings.WorldDirectory), "--set-from-env" }));
            Assert.That(pwLaunch.PasswordInEnvironment, Is.True);
            Assert.That(again.Launches.Last().PasswordInEnvironment, Is.False);

            var failing = NewServer(Options("FAKE_LEAK", "1", "FAKE_INIT_FAIL", "1"));
            var e = WaitFault(failing.StartAsync(Settings("other", password: password))) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.InitFailed));
            Assert.That(e.Message, Does.Contain("Permission denied"));
            Assert.That(e.Message + string.Join("\n", e.RecentOutput) + failing.LastError, Does.Not.Contain(password));
        }

        [Test]
        public void InitFailure_SurfacesStderr()
        {
            var server = NewServer(Options("FAKE_INIT_FAIL", "1"));
            var e = WaitFault(server.StartAsync(Settings())) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.InitFailed));
            Assert.That(e.Message, Does.Contain("Could not create the world (exit code 1)"));
            Assert.That(e.Message, Does.Contain("Permission denied"));
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
        }

        [Test]
        public void PasswordOnExistingWorld_IsSetAndCleared()
        {
            HostSettings settings = Settings("pw");
            var a = NewServer();
            Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            a.Stop(TimeSpan.FromSeconds(5));
            string toml = Path.Combine(settings.WorldDirectory, "world.toml");
            Assert.That(File.ReadAllText(toml), Does.Contain("password_set = false"));

            settings.Password = "new-password";
            Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            a.Stop(TimeSpan.FromSeconds(5));
            Assert.That(File.ReadAllText(toml), Does.Contain("password_set = true"));

            settings.Password = "";
            Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            a.Stop(TimeSpan.FromSeconds(5));
            Assert.That(File.ReadAllText(toml), Does.Contain("password_set = false"));
        }

        [Test]
        public void Cancel_DuringStart_StopsAndThrowsCancelled()
        {
            var server = NewServer(Options("FAKE_START_DELAY", "5"));
            HostSettings settings = Settings();
            var cts = new CancellationTokenSource();
            var task = server.StartAsync(settings, cts.Token);
            Assert.That(WaitUntil(() => server.ProcessId.HasValue && server.State == HostState.Starting, TimeSpan.FromSeconds(5)), Is.True);
            int pid = server.ProcessId.Value;
            Track(pid);
            cts.Cancel();
            Assert.That(WaitFault(task), Is.InstanceOf<OperationCanceledException>());
            Assert.That(server.State, Is.EqualTo(HostState.Stopped));
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(3)), Is.True);
        }

        [Test]
        public void Stop_DuringStart_EndsStopped()
        {
            var server = NewServer(Options("FAKE_START_DELAY", "5"));
            var task = server.StartAsync(Settings());
            Assert.That(WaitUntil(() => server.ProcessId.HasValue && server.State == HostState.Starting, TimeSpan.FromSeconds(5)), Is.True);
            int pid = server.ProcessId.Value;
            Track(pid);
            StopResult r = server.Stop(TimeSpan.FromSeconds(5));
            Assert.That(r.WasRunning, Is.True);
            Assert.That(WaitFault(task), Is.InstanceOf<OperationCanceledException>());
            Assert.That(server.State, Is.EqualTo(HostState.Stopped));
            Assert.That(IsAlive(pid), Is.False);
        }

        [Test]
        public void SecondStart_WhileRunning_Throws()
        {
            var server = NewServer();
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            Track(server.ProcessId);
            Assert.Throws<InvalidOperationException>(() => server.StartAsync(settings));
        }

        [Test]
        public void MissingBinary_FailsToLaunch()
        {
            var server = NewServer(binary: Path.Combine(Temp, "no-such-server"));
            var e = WaitFault(server.StartAsync(Settings())) as HostException;
            Assert.That(e, Is.Not.Null);
            Assert.That(e.Kind, Is.EqualTo(HostErrorKind.LaunchFailed));
            Assert.That(e.Message, Does.Contain("no-such-server"));
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
        }

        [Test]
        public void InvalidSettings_AreRejected()
        {
            var server = NewServer();
            HostSettings s = Settings();
            s.Port = 70000;
            var e = WaitFault(server.StartAsync(s)) as HostException;
            Assert.That(e?.Kind, Is.EqualTo(HostErrorKind.InvalidSettings));
            Assert.That(server.State, Is.EqualTo(HostState.Failed));
        }

        [Test]
        public void PasswordVariableOfTheClient_IsNeverInherited()
        {
            string before = Environment.GetEnvironmentVariable("TINYVERSE_PASSWORD");
            Environment.SetEnvironmentVariable("TINYVERSE_PASSWORD", "inherited-from-client");
            try
            {
                var server = NewServer();
                HostSettings settings = Settings();
                Wait(server.StartAsync(settings));
                Track(server.ProcessId);
                // init did not pick up a password nobody asked for, and run never saw the variable.
                Assert.That(File.ReadAllText(Path.Combine(settings.WorldDirectory, "world.toml")), Does.Contain("password_set = false"));
                Assert.That(File.ReadAllText(Path.Combine(settings.WorldDirectory, "run.env")).Trim(), Is.EqualTo("unset"));
                int? watchdog = server.WatchdogProcessId;
                Assert.That(watchdog, Is.Not.Null);
                Track(watchdog);
                Assert.That(File.ReadAllText("/proc/" + watchdog + "/environ"), Does.Not.Contain("TINYVERSE_PASSWORD"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("TINYVERSE_PASSWORD", before);
            }
        }

        [Test]
        public void Tls_IsReportedAndCanBeDisabled()
        {
            HostSettings settings = Settings("tls");
            var a = NewServer();
            Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            a.Stop(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(settings.WorldDirectory, "cert.pem"), "fake");
            File.WriteAllText(Path.Combine(settings.WorldDirectory, "key.pem"), "fake");

            HostStartResult r = Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            Assert.That(r.Tls, Is.True);
            Assert.That(r.LocalUrl, Is.EqualTo($"wss://127.0.0.1:{settings.Port}/ws"));
            Assert.That(r.LanUrls, Has.All.StartWith("wss://"));
            Assert.That(a.Launches.Last().Arguments, Has.No.Member("--no-tls"));
            a.Stop(TimeSpan.FromSeconds(5));

            settings.DisableTls = true;
            r = Wait(a.StartAsync(settings));
            Track(a.ProcessId);
            Assert.That(r.Tls, Is.False);
            Assert.That(r.LocalUrl, Is.EqualTo($"ws://127.0.0.1:{settings.Port}/ws"));
            Assert.That(a.Launches.Last().Arguments, Has.Member("--no-tls"));
        }

        [Test]
        public void BindToLoopback_HasNoLanAddresses()
        {
            var server = NewServer();
            HostSettings s = Settings();
            s.BindAddress = "127.0.0.1";
            HostStartResult r = Wait(server.StartAsync(s));
            Track(server.ProcessId);
            Assert.That(r.LocalUrl, Is.EqualTo($"ws://127.0.0.1:{s.Port}/ws"));
            Assert.That(r.LanAddresses, Is.Empty);
        }
    }
}
