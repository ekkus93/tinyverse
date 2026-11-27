using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Tinyverse.Avatars;
using Tinyverse.Building;
using Tinyverse.Net;
using Tinyverse.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;
using P = Tinyverse.Proto;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>Paths to the Rust binaries built by <c>cargo build --workspace</c>.</summary>
    static class Binaries
    {
        public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        public static string Find(string name)
        {
            foreach (var profile in new[] { "debug", "release" })
            {
                string p = Path.Combine(RepoRoot, "server", "target", profile, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string Server => Find("tinyverse-server");
        public static string Cli => Find("tv-cli");

        /// <summary>Skips the test with a clear message when the binaries have not been built.</summary>
        public static void Require()
        {
            if (Server == null || Cli == null)
                Assert.Ignore("tinyverse-server or tv-cli is not built: run `cargo build --workspace` in server/ to run the real-server tests.");
        }

        public static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    /// <summary>A child process whose output lines are collected.</summary>
    sealed class Child : IDisposable
    {
        readonly Process process;
        readonly List<string> lines = new List<string>();
        readonly object gate = new object();

        public Child(string file, IEnumerable<string> args, string label, bool stdin = false)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin,
                CreateNoWindow = true,
            };
            psi.Arguments = string.Join(" ", args.Select(Quote));
            Label = label;
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Add(e.Data);
            process.ErrorDataReceived += (_, e) => Add(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string Label { get; }
        public int Pid => process.Id;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;

        static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\'', '#' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

        void Add(string line)
        {
            if (line == null) return;
            lock (gate) lines.Add(line);
        }

        public List<string> Lines
        {
            get { lock (gate) return new List<string>(lines); }
        }

        public string Output => string.Join("\n", Lines);

        public void WriteLine(string line)
        {
            process.StandardInput.WriteLine(line);
            process.StandardInput.Flush();
        }

        public bool WaitForExit(int ms) => process.WaitForExit(ms);

        /// <summary>SIGTERM, then SIGKILL after <paramref name="ms"/>.</summary>
        public void Terminate(int ms = 10000)
        {
            if (process.HasExited) return;
            try
            {
                using (var kill = Process.Start(new ProcessStartInfo("kill", "-TERM " + process.Id) { UseShellExecute = false }))
                    kill?.WaitForExit(2000);
            }
            catch (Exception) { /* fall through to Kill */ }
            if (!process.WaitForExit(ms))
            {
                try { process.Kill(); } catch (Exception) { }
                process.WaitForExit(2000);
            }
        }

        public void Dispose()
        {
            Terminate(3000);
            process.Dispose();
        }
    }

    /// <summary>A real tinyverse-server in a temporary world, plus tv-cli participants. Leaves nothing running.</summary>
    sealed class ServerFixture : IDisposable
    {
        readonly List<Child> clis = new List<Child>();
        Child server;

        public string WorldDir { get; }
        public string Url { get; private set; }
        public int Port { get; private set; }
        public string OwnerToken => File.ReadAllText(Path.Combine(WorldDir, "owner.token")).Trim();
        public Child Process => server;

        public ServerFixture(string worldName = "Test World", string password = null)
        {
            Binaries.Require();
            WorldDir = Path.Combine(Path.GetTempPath(), "tv-it-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var args = new List<string> { "init", WorldDir, "--name", worldName };
            if (password != null) { args.Add("--password"); args.Add(password); }
            using (var init = new Child(Binaries.Server, args, "init"))
            {
                Assert.IsTrue(init.WaitForExit(20000), "init finished");
                Assert.AreEqual(0, init.ExitCode, init.Output);
            }
        }

        /// <summary>Starts <c>run</c> (port 0 = any) and waits for its "listening on" line.</summary>
        public IEnumerator Start(int port = 0)
        {
            server = new Child(Binaries.Server, new[] { "run", WorldDir, "--port", port.ToString(), "--bind", "127.0.0.1" }, "server");
            pids.Add(server.Pid);
            var re = new Regex(@"listening on (wss?://[^\s]+)");
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                var m = server.Lines.Select(l => re.Match(l)).FirstOrDefault(x => x.Success);
                if (m != null)
                {
                    Url = m.Groups[1].Value;
                    Port = new Uri(Url).Port;
                    yield break;
                }
                if (server.HasExited) Assert.Fail("the server exited: " + server.Output);
                yield return null;
            }
            Assert.Fail("the server did not print its listening line: " + server.Output);
        }

        /// <summary>SIGTERM and wait: the server flushes the world and exits.</summary>
        public void Stop()
        {
            server?.Terminate();
        }

        /// <summary>Starts a long-running tv-cli command (bot, connect).</summary>
        public Child Cli(string name, params string[] args)
        {
            var all = new List<string> { "--url", Url, "--name", name };
            all.AddRange(args);
            var c = new Child(Binaries.Cli, all, "tv-cli " + string.Join(" ", args), stdin: true);
            clis.Add(c);
            pids.Add(c.Pid);
            return c;
        }

        /// <summary>Runs a short tv-cli command to completion off the main thread.</summary>
        public Task<(int code, string output)> CliRun(params string[] args)
        {
            var all = new List<string> { "--url", Url };
            all.AddRange(args);
            return Task.Run(() =>
            {
                using (var c = new Child(Binaries.Cli, all, "tv-cli"))
                {
                    if (!c.WaitForExit(20000)) return (-1, "timed out: " + c.Output);
                    c.WaitForExit(1000); // drain output
                    return (c.ExitCode, c.Output);
                }
            });
        }

        public static ulong ParseObjectId(string output)
        {
            var m = Regex.Match(output, @"object (\d+)");
            Assert.IsTrue(m.Success, "no object id in: " + output);
            return ulong.Parse(m.Groups[1].Value);
        }

        readonly List<int> pids = new List<int>();

        /// <summary>After Dispose: fails if any server or tv-cli process this fixture started is still alive.</summary>
        public void AssertNothingLeft()
        {
            foreach (int pid in pids)
            {
                bool alive;
                try { alive = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { alive = false; }
                Assert.IsFalse(alive, $"process {pid} is still running");
            }
        }

        public void Dispose()
        {
            foreach (var c in clis)
            {
                try { c.Dispose(); } catch (Exception e) { Debug.LogException(e); }
            }
            clis.Clear();
            server?.Dispose();
            try { if (Directory.Exists(WorldDir)) Directory.Delete(WorldDir, true); } catch (Exception) { }
        }
    }

    /// <summary>Ticks a session (Update) and reports (LateUpdate) as the Bootstrap does.</summary>
    [DefaultExecutionOrder(-100)]
    sealed class Driver : MonoBehaviour
    {
        public Action<float> OnUpdate;
        public Action OnLateUpdate;
        void Update() => OnUpdate?.Invoke(Time.deltaTime);
        void LateUpdate() => OnLateUpdate?.Invoke();
    }

    static class Wait
    {
        public static IEnumerator For(Func<bool> condition, float seconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) Assert.Fail("timed out waiting for " + what);
                yield return null;
            }
        }

        public static IEnumerator Seconds(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        public static IEnumerator Task(Task t, float seconds, string what)
        {
            yield return For(() => t.IsCompleted, seconds, what);
            if (t.IsFaulted) throw t.Exception.InnerException ?? t.Exception;
        }
    }

    /// <summary>A real WorldSession with its own UI and camera, driven like the app drives it.</summary>
    sealed class SessionHarness : IDisposable
    {
        public UIRoot UI;
        public Camera Camera;
        public WorldSession Session;
        public readonly ScriptedAvatarInput Input = new ScriptedAvatarInput();
        public readonly ScriptedBuildInput BuildInput = new ScriptedBuildInput { HasPointer = false };
        GameObject camGo;
        Driver driver;
        public string EndReason;
        public bool Kicked;
        public bool Ended;

        public IEnumerator Join(string url, string name, string token = "")
        {
            if (UI == null)
            {
                UI = UIRoot.Create();
                UI.HotkeysEnabled = false;
                camGo = new GameObject("Test Camera") { tag = "MainCamera" };
                Camera = camGo.AddComponent<Camera>();
                driver = camGo.AddComponent<Driver>();
            }
            var conn = new TinyverseConnection(new ConnectionSettings { ReconnectBaseDelay = TimeSpan.FromMilliseconds(500), ReconnectMaxDelay = TimeSpan.FromSeconds(2), MaxReconnectAttempts = 20 });
            var task = conn.ConnectAsync(new ConnectOptions { Url = url, DisplayName = name, Token = token ?? "" });
            yield return Wait.Task(task, 15f, "Welcome");
            Ended = false;
            Session = new WorldSession(conn, UI, Camera, new SessionOptions { Input = Input, BuildInput = BuildInput });
            Session.Ended += (r, k) => { Ended = true; EndReason = r; Kicked = k; };
            UI.ShowScreen(UIScreen.Hud);
            driver.OnUpdate = dt => Session?.Tick(dt);
            driver.OnLateUpdate = () => Session?.LateTick();
            yield return Wait.For(() => Session.SnapshotComplete, 15f, "the snapshot");
        }

        public void Leave()
        {
            Session?.Dispose();
            Session = null;
        }

        public void Dispose()
        {
            Leave();
            if (UI != null) UnityEngine.Object.Destroy(UI.gameObject);
            if (camGo != null) UnityEngine.Object.Destroy(camGo);
        }
    }
}
