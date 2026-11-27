using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tinyverse.Host.Platform;

namespace Tinyverse.Host.Tests
{
    /// <summary>
    /// Base for tests that drive <see cref="ServerProcess"/> against the fake server script. Every test gets a
    /// fresh temp folder; teardown disposes every server and asserts that no process it started is left alive.
    /// </summary>
    public abstract class FakeServerFixture
    {
        protected const string FakeRelativePath = "Assets/Tinyverse/Host/Tests/EditMode/FakeServer/tinyverse-server";

        protected string FakeServer;
        protected string Temp;
        private readonly List<ServerProcess> _servers = new List<ServerProcess>();
        private readonly List<int> _pids = new List<int>();
        private readonly List<Process> _extra = new List<Process>();
        protected int MainThreadId;

        [OneTimeSetUp]
        public void FindFake()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                Assert.Ignore("The fake server and process tests are Linux-only for now.");
            FakeServer = Path.GetFullPath(FakeRelativePath);
            Assert.That(File.Exists(FakeServer), "fake server missing at " + FakeServer);
            // Git stores the mode bit, but make sure anyway (e.g. after a checkout on a filesystem without it).
            using (var p = Process.Start(new ProcessStartInfo("/bin/chmod", "+x \"" + FakeServer + "\"") { UseShellExecute = false }))
                p.WaitForExit(5000);
        }

        [SetUp]
        public void MakeTemp()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            Temp = Path.Combine(Path.GetTempPath(), "tinyverse-host-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Temp);
        }

        [TearDown]
        public void CleanUp()
        {
            foreach (ServerProcess s in _servers)
            {
                int? pid = s.ProcessId;
                if (pid.HasValue) _pids.Add(pid.Value);
                int? w = s.WatchdogProcessId;
                if (w.HasValue) _pids.Add(w.Value);
                s.Dispose();
            }
            foreach (Process p in _extra)
            {
                try { if (!p.HasExited) p.Kill(); p.WaitForExit(2000); p.Dispose(); } catch (Exception) { }
            }
            _servers.Clear();
            _extra.Clear();

            var alive = new List<int>();
            foreach (int pid in _pids)
                if (!WaitGone(pid, TimeSpan.FromSeconds(3))) alive.Add(pid);
            _pids.Clear();
            try { Directory.Delete(Temp, true); } catch (Exception) { }
            Assert.That(alive, Is.Empty, "processes left behind: " + string.Join(", ", alive));
        }

        protected ServerProcess NewServer(ServerProcessOptions options = null, string binary = null)
        {
            var s = new ServerProcess(binary ?? FakeServer, options ?? Options());
            _servers.Add(s);
            return s;
        }

        protected static ServerProcessOptions Options(params string[] env)
        {
            var o = new ServerProcessOptions
            {
                ReadyTimeout = TimeSpan.FromSeconds(10),
                InitTimeout = TimeSpan.FromSeconds(10),
                StopTimeout = TimeSpan.FromSeconds(5),
            };
            for (int i = 0; i + 1 < env.Length; i += 2) o.Environment[env[i]] = env[i + 1];
            return o;
        }

        protected HostSettings Settings(string folder = "world", string name = "Test World", string password = null) =>
            new HostSettings
            {
                WorldDirectory = Path.Combine(Temp, folder),
                WorldName = name,
                Description = "A world for tests",
                Port = FreePort(),
                Password = password,
            };

        /// <summary>Remembers a pid so teardown checks it is gone.</summary>
        protected void Track(int? pid)
        {
            if (pid.HasValue) _pids.Add(pid.Value);
        }

        protected void TrackExtra(Process p) => _extra.Add(p);

        protected static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        protected static bool IsAlive(int pid) => ServerPlatform.Current.IsAlive(pid);

        protected static bool WaitGone(int pid, TimeSpan timeout) => WaitUntil(() => !IsAlive(pid), timeout);

        protected static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return condition();
        }

        /// <summary>Blocks on a task. Safe because the library never needs the main thread to make progress.</summary>
        protected static T Wait<T>(Task<T> task, double seconds = 20)
        {
            Assert.That(task.Wait(TimeSpan.FromSeconds(seconds)) || task.IsCompleted, "task did not finish in " + seconds + " s");
            return task.Result;
        }

        /// <summary>Waits for the task to fail and returns the inner exception.</summary>
        protected static Exception WaitFault(Task task, double seconds = 20)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(seconds));
            }
            catch (AggregateException e)
            {
                return e.InnerException;
            }
            Assert.Fail(task.IsCompleted ? "task succeeded but should have failed" : "task did not finish in " + seconds + " s");
            return null;
        }

        /// <summary>Collects events raised by Pump and the threads they were raised on.</summary>
        protected sealed class Recorder
        {
            public readonly List<string> Lines = new List<string>();
            public readonly List<HostState> States = new List<HostState>();
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> Order = new List<string>();
            public readonly HashSet<int> Threads = new HashSet<int>();

            public Recorder(ServerProcess s)
            {
                s.LogLine += l => { Threads.Add(Thread.CurrentThread.ManagedThreadId); Lines.Add(l); Order.Add("log"); };
                s.StateChanged += st => { Threads.Add(Thread.CurrentThread.ManagedThreadId); States.Add(st); Order.Add("state:" + st); };
                s.Failed += m => { Threads.Add(Thread.CurrentThread.ManagedThreadId); Failures.Add(m); Order.Add("failed"); };
            }

            public string AllText => string.Join("\n", Lines) + "\n" + string.Join("\n", Failures);
        }
    }
}
