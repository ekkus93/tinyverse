using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tinyverse.Host.Internal
{
    /// <summary>
    /// One child process, started from a dedicated thread that lives exactly as long as the child.
    /// That matters on Linux: PR_SET_PDEATHSIG fires when the *thread* that forked the child exits, and Mono
    /// forks on the calling thread, so starting from a pool thread could get the server killed at random.
    /// stdout and stderr are each read line by line on their own thread and handed to <c>onLine</c>.
    /// </summary>
    internal sealed class ChildProcess
    {
        private readonly TaskCompletionSource<int> _exited =
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private Process _process;

        private ChildProcess() { }

        public int Pid { get; private set; }

        /// <summary>Completes with the exit code once the process has exited and its output is drained.</summary>
        public Task<int> Exited => _exited.Task;

        public bool HasExited => _exited.Task.IsCompleted;

        /// <summary>Starts the process. The task faults if it cannot be started.</summary>
        public static Task<ChildProcess> Launch(string fileName, IList<string> args, string workingDirectory,
            Action<string> onLine, string threadName, IDictionary<string, string> environment = null)
        {
            var started = new TaskCompletionSource<ChildProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = new ChildProcess();
            var thread = new Thread(() => child.Supervise(fileName, args, workingDirectory, onLine, environment, started))
            {
                IsBackground = true,
                Name = threadName,
            };
            thread.Start();
            return started.Task;
        }

        private void Supervise(string fileName, IList<string> args, string workingDirectory, Action<string> onLine,
            IDictionary<string, string> environment, TaskCompletionSource<ChildProcess> started)
        {
            Process p;
            try
            {
                var psi = new ProcessStartInfo(fileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
                foreach (string a in args) psi.ArgumentList.Add(a);
                psi.Environment["NO_COLOR"] = "1";
                if (environment != null)
                    foreach (KeyValuePair<string, string> kv in environment) psi.Environment[kv.Key] = kv.Value;
                p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Process.Start returned no process");
            }
            catch (Exception e)
            {
                started.TrySetException(e);
                _exited.TrySetResult(-1);
                return;
            }

            _process = p;
            Pid = p.Id;
            Thread outReader = StartReader(p.StandardOutput, onLine, Pid + "-out");
            Thread errReader = StartReader(p.StandardError, onLine, Pid + "-err");
            started.TrySetResult(this);

            int code = -1;
            try
            {
                p.WaitForExit();
                code = p.ExitCode;
            }
            catch (Exception)
            {
                // Treat as exited; nothing more can be learned.
            }

            // A grandchild that inherited the pipes could hold them open; do not wait for ever.
            outReader.Join(2000);
            errReader.Join(2000);
            _exited.TrySetResult(code);
            try { p.Dispose(); } catch (Exception) { /* ignore */ }
        }

        private static Thread StartReader(StreamReader reader, Action<string> onLine, string name)
        {
            var t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try { onLine(line); }
                        catch (Exception) { /* a broken handler must not stop the drain */ }
                    }
                }
                catch (Exception)
                {
                    // Stream closed underneath us.
                }
            })
            {
                IsBackground = true,
                Name = "tinyverse-server-" + name,
            };
            t.Start();
            return t;
        }

        /// <summary>SIGKILL (Mono's Process.Kill). Safe to call after exit.</summary>
        public void Kill()
        {
            if (HasExited) return;
            try
            {
                Process p = _process;
                if (p != null && !p.HasExited) p.Kill();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }
}
