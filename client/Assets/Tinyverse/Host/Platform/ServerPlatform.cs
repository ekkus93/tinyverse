using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Tinyverse.Host.Platform
{
    /// <summary>
    /// The platform-specific parts of hosting: graceful termination, orphan guards and the binary name.
    /// Linux is implemented; macOS shares the POSIX parts (signals, watchdog) but has no parent-death signal;
    /// Windows needs its own implementation (e.g. a console control event or a stdin shutdown command).
    /// </summary>
    internal abstract class ServerPlatform
    {
        private static ServerPlatform _current;

        public static ServerPlatform Current => _current ?? (_current = Detect());

        private static ServerPlatform Detect()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new PosixServerPlatform(true);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new PosixServerPlatform(false);
            return new FallbackServerPlatform();
        }

        public abstract string ServerBinaryName { get; }

        /// <summary>Asks the process to shut down cleanly (SIGTERM on POSIX). False with a reason if not possible.</summary>
        public abstract bool TryRequestStop(int pid, out string error);

        /// <summary>
        /// Rewrites the command so the child dies with the thread that launches it, if the platform supports that.
        /// Returns false (and leaves the command alone) if not available.
        /// </summary>
        public abstract bool TryWrapForParentDeath(ref string fileName, List<string> args);

        /// <summary>Starts a process that stops <paramref name="serverPid"/> once <paramref name="clientPid"/> is gone. Null if unsupported.</summary>
        public abstract Process StartWatchdog(int clientPid, int serverPid, int killAfterSeconds);

        /// <summary>True if a process with this pid exists (a zombie counts as gone where detectable).</summary>
        public abstract bool IsAlive(int pid);
    }

    internal sealed class PosixServerPlatform : ServerPlatform
    {
        private const int SIGTERM = 15;
        private static readonly string[] SetprivCandidates = { "/usr/bin/setpriv", "/bin/setpriv" };
        private readonly bool _linux;
        private string _setpriv;
        private bool _setprivProbed;
        private bool _libcKillBroken;

        public PosixServerPlatform(bool linux) { _linux = linux; }

        public override string ServerBinaryName => "tinyverse-server";

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int sys_kill(int pid, int sig);

        public override bool TryRequestStop(int pid, out string error)
        {
            error = null;
            if (!_libcKillBroken)
            {
                try
                {
                    if (sys_kill(pid, SIGTERM) == 0) return true;
                    error = $"kill({pid}, SIGTERM) failed with errno {Marshal.GetLastWin32Error()}";
                    return false;
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
                {
                    _libcKillBroken = true; // fall through to /bin/kill
                }
            }

            try
            {
                var psi = new ProcessStartInfo(File.Exists("/bin/kill") ? "/bin/kill" : "kill")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-TERM");
                psi.ArgumentList.Add(pid.ToString());
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.HasExited && p.ExitCode == 0) return true;
                    error = "kill -TERM failed: " + err.Trim();
                    return false;
                }
            }
            catch (Exception e)
            {
                error = "kill -TERM could not be run: " + e.Message;
                return false;
            }
        }

        /// <summary>The setpriv binary if it supports --pdeathsig (util-linux 2.33+), probed once.</summary>
        internal string Setpriv
        {
            get
            {
                if (_setprivProbed) return _setpriv;
                _setprivProbed = true;
                if (!_linux) return null;
                foreach (string candidate in SetprivCandidates)
                {
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        var psi = new ProcessStartInfo(candidate)
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                        };
                        psi.ArgumentList.Add("--help");
                        using (var p = Process.Start(psi))
                        {
                            var errTask = p.StandardError.ReadToEndAsync();
                            string help = p.StandardOutput.ReadToEnd() + errTask.Result;
                            p.WaitForExit(5000);
                            if (help.Contains("--pdeathsig")) return _setpriv = candidate;
                        }
                    }
                    catch (Exception)
                    {
                        // try the next candidate
                    }
                }
                return null;
            }
        }

        public override bool TryWrapForParentDeath(ref string fileName, List<string> args)
        {
            string setpriv = Setpriv;
            if (setpriv == null) return false;
            // setpriv sets PR_SET_PDEATHSIG and execs the server in place, so the pid stays the server's.
            args.InsertRange(0, new[] { "--pdeathsig", "TERM", "--", fileName });
            fileName = setpriv;
            return true;
        }

        // $1 client pid, $2 server pid, $3 seconds between SIGTERM and SIGKILL.
        private const string WatchdogScript =
            "c=$1; s=$2; g=$3\n" +
            "while kill -0 \"$c\" 2>/dev/null; do\n" +
            "  kill -0 \"$s\" 2>/dev/null || exit 0\n" +
            "  sleep 1\n" +
            "done\n" +
            "kill -TERM \"$s\" 2>/dev/null || exit 0\n" +
            "i=0\n" +
            "while kill -0 \"$s\" 2>/dev/null; do\n" +
            "  [ \"$i\" -ge \"$g\" ] && { kill -KILL \"$s\" 2>/dev/null; exit 0; }\n" +
            "  sleep 1; i=$((i+1))\n" +
            "done\n";

        public override Process StartWatchdog(int clientPid, int serverPid, int killAfterSeconds)
        {
            if (!File.Exists("/bin/sh")) return null;
            var psi = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false, CreateNoWindow = true,
                // Redirected so the watchdog never writes into the client's terminal or log.
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(WatchdogScript);
            psi.ArgumentList.Add("tinyverse-host-watchdog");
            psi.ArgumentList.Add(clientPid.ToString());
            psi.ArgumentList.Add(serverPid.ToString());
            psi.ArgumentList.Add(Math.Max(0, killAfterSeconds).ToString());
            return Process.Start(psi);
        }

        public override bool IsAlive(int pid)
        {
            if (_linux)
            {
                string status = "/proc/" + pid + "/status";
                try
                {
                    if (!File.Exists(status)) return false;
                    foreach (string line in File.ReadAllLines(status))
                        if (line.StartsWith("State:", StringComparison.Ordinal))
                            return !line.Contains("Z (zombie)") && !line.Contains("X (dead)");
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return true; }
            }
            try { return sys_kill(pid, 0) == 0; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Windows and anything else: no graceful stop, no orphan guard yet.</summary>
    internal sealed class FallbackServerPlatform : ServerPlatform
    {
        public override string ServerBinaryName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tinyverse-server.exe" : "tinyverse-server";

        public override bool TryRequestStop(int pid, out string error)
        {
            error = "graceful shutdown is not implemented on this platform yet";
            return false;
        }

        public override bool TryWrapForParentDeath(ref string fileName, List<string> args) => false;

        public override Process StartWatchdog(int clientPid, int serverPid, int killAfterSeconds) => null;

        public override bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (Exception) { return false; }
        }
    }
}
