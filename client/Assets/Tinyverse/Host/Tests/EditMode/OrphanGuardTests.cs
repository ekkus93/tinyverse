using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Tinyverse.Host.Platform;

namespace Tinyverse.Host.Tests
{
    /// <summary>
    /// The editor cannot be crashed from a test, so these check each guard with a stand-in:
    /// the watchdog watches a throwaway <c>sleep</c> process instead of the editor, and the parent-death
    /// signal is checked by launching through the same wrapper from a thread that then exits.
    /// </summary>
    public class OrphanGuardTests : FakeServerFixture
    {
        [Test]
        public void ParentDeathWrapper_SendsSigterm_WhenParentIsKilled()
        {
            var platform = ServerPlatform.Current as PosixServerPlatform;
            if (platform?.Setpriv == null) Assert.Ignore("setpriv with --pdeathsig is not installed");

            string dir = Path.Combine(Temp, "pdeath-parent");
            using (var init = Process.Start(new ProcessStartInfo(FakeServer) { UseShellExecute = false, ArgumentList = { "init", dir }, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                init.StandardOutput.ReadToEnd();
                init.WaitForExit(5000);
                Assert.That(init.ExitCode, Is.EqualTo(0));
            }
            string file = FakeServer;
            var args = new List<string> { "run", dir, "--port", FreePort().ToString() };
            Assert.That(platform.TryWrapForParentDeath(ref file, args), Is.True);

            // A stand-in client: a shell that launches the wrapped server exactly as ServerProcess would, then
            // gets SIGKILLed — the same thing the kernel sees when the game crashes.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("\"$@\" 2>/dev/null & echo $!; wait");
            psi.ArgumentList.Add("fake-client");
            psi.ArgumentList.Add(file);
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process parent = Process.Start(psi);
            TrackExtra(parent);
            int serverPid = int.Parse(parent.StandardOutput.ReadLine().Trim());
            Track(serverPid);
            string pidFile = Path.Combine(dir, "run.pid");
            Assert.That(WaitUntil(() => File.Exists(pidFile), TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(File.ReadAllText(pidFile).Trim(), Is.EqualTo(serverPid.ToString()), "setpriv must exec in place");
            Thread.Sleep(300);
            Assert.That(File.Exists(Path.Combine(dir, "shutdown.marker")), Is.False);

            parent.Kill();
            Assert.That(WaitUntil(() => File.Exists(Path.Combine(dir, "shutdown.marker")), TimeSpan.FromSeconds(5)), Is.True,
                "the server did not get SIGTERM when its parent died");
            Assert.That(WaitGone(serverPid, TimeSpan.FromSeconds(3)), Is.True);
        }

        private Process StartFakeClient()
        {
            var p = Process.Start(new ProcessStartInfo("/bin/sleep", "600") { UseShellExecute = false });
            TrackExtra(p);
            return p;
        }

        [Test]
        public void Watchdog_StopsServerGracefully_WhenClientDies()
        {
            Process client = StartFakeClient();
            ServerProcessOptions o = Options();
            o.OrphanGuard = OrphanGuard.Watchdog;
            o.WatchdogClientPidOverride = client.Id;
            var server = NewServer(o);
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            int pid = server.ProcessId.Value;
            Track(pid);
            Track(server.WatchdogProcessId);
            Assert.That(server.WatchdogProcessId, Is.Not.Null);

            Thread.Sleep(1500);
            Assert.That(IsAlive(pid), Is.True, "the watchdog fired while the client was alive");
            client.Kill();
            string marker = Path.Combine(settings.WorldDirectory, "shutdown.marker");
            Assert.That(WaitUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5)), Is.True, "no graceful shutdown");
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(3)), Is.True);
        }

        [Test]
        public void Watchdog_KillsHard_WhenServerIgnoresSigterm()
        {
            Process client = StartFakeClient();
            ServerProcessOptions o = Options("FAKE_MODE", "ignore_term");
            o.OrphanGuard = OrphanGuard.Watchdog;
            o.WatchdogClientPidOverride = client.Id;
            o.WatchdogKillAfterSeconds = 1;
            var server = NewServer(o);
            HostSettings settings = Settings();
            Wait(server.StartAsync(settings));
            int pid = server.ProcessId.Value;
            Track(pid);
            Track(server.WatchdogProcessId);

            client.Kill();
            Assert.That(WaitGone(pid, TimeSpan.FromSeconds(8)), Is.True, "the watchdog did not kill the server");
            Assert.That(File.Exists(Path.Combine(settings.WorldDirectory, "shutdown.marker")), Is.False);
        }

        [Test]
        public void Watchdog_ExitsWhenServerIsStoppedNormally()
        {
            var server = NewServer(); // Auto: parent-death signal and watchdog
            Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            int? watchdog = server.WatchdogProcessId;
            Assert.That(watchdog, Is.Not.Null);
            Track(watchdog);
            Assert.That(server.Stop(TimeSpan.FromSeconds(5)).Graceful, Is.True);
            Assert.That(WaitGone(watchdog.Value, TimeSpan.FromSeconds(3)), Is.True);
        }

        [Test]
        public void Auto_UsesParentDeathSignalWhenSetprivIsAvailable()
        {
            var platform = ServerPlatform.Current as PosixServerPlatform;
            if (platform?.Setpriv == null) Assert.Ignore("setpriv with --pdeathsig is not installed");
            var server = NewServer();
            Wait(server.StartAsync(Settings()));
            Track(server.ProcessId);
            Assert.That(server.GetRecentLines().Any(l => l.Contains("pdeathsig is not available")), Is.False);
        }

        [Test]
        public void ParentDeathSignal_StopsServerGracefully_WhenLaunchingThreadDies()
        {
            var platform = ServerPlatform.Current as PosixServerPlatform;
            if (platform?.Setpriv == null) Assert.Ignore("setpriv with --pdeathsig is not installed");

            string dir = Path.Combine(Temp, "pdeath");
            using (var init = Process.Start(new ProcessStartInfo(FakeServer) { UseShellExecute = false, ArgumentList = { "init", dir }, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                init.StandardOutput.ReadToEnd();
                init.WaitForExit(5000);
                Assert.That(init.ExitCode, Is.EqualTo(0));
            }

            string file = FakeServer;
            var args = new List<string> { "run", dir, "--port", FreePort().ToString() };
            Assert.That(platform.TryWrapForParentDeath(ref file, args), Is.True);
            Assert.That(file, Is.EqualTo(platform.Setpriv));

            Process child = null;
            var t = new Thread(() =>
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string a in args) psi.ArgumentList.Add(a);
                child = Process.Start(psi);
                // setpriv must have called prctl before this thread goes, or there is nothing to fire.
                WaitUntil(() => File.Exists(Path.Combine(dir, "run.pid")), TimeSpan.FromSeconds(5));
            });
            string pidFile = Path.Combine(dir, "run.pid");
            try
            {
                t.Start();
                t.Join();
                Assert.That(child, Is.Not.Null);
                Track(child.Id);
                // The launching thread is gone. This shows (a) Mono forks on the calling thread, so ServerProcess
                // must launch from a thread that outlives the server, and (b) the wrapper works under the editor.
                string marker = Path.Combine(dir, "shutdown.marker");
                Assert.That(WaitUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5)), Is.True,
                    "the server did not get SIGTERM when its launching thread exited");
                Assert.That(File.ReadAllText(pidFile).Trim(), Is.EqualTo(child.Id.ToString()), "setpriv must exec in place");
                Assert.That(WaitGone(child.Id, TimeSpan.FromSeconds(3)), Is.True);
            }
            finally
            {
                try { if (child != null && !child.HasExited) child.Kill(); } catch (Exception) { }
            }
        }

        [Test]
        public void ServerSurvivesBusyThreadPool()
        {
            // Mono forks on the calling thread; if the server were started from a pool thread the parent-death
            // signal could fire when that thread retires. The dedicated supervisor thread prevents that.
            var server = NewServer();
            Wait(server.StartAsync(Settings()));
            int pid = server.ProcessId.Value;
            Track(pid);
            var done = new CountdownEvent(64);
            for (int i = 0; i < 64; i++) ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(20); done.Signal(); });
            done.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(1000);
            Assert.That(IsAlive(pid), Is.True);
            Assert.That(server.State, Is.EqualTo(HostState.Running));
        }
    }
}
