using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// Start-up options, for automation and for people. Unknown arguments (Unity's own, such as
    /// <c>-logFile</c> or <c>-screen-width</c>) are ignored.
    /// <code>
    /// --connect &lt;address&gt; [--name &lt;n&gt;] [--password &lt;pw&gt;]   connect at start-up
    /// --host &lt;world-dir&gt; [--port &lt;p&gt;]                     host that world and join it
    /// --screenshot-dir &lt;dir&gt; [--screenshot-every &lt;s&gt;]     write PNG screenshots
    /// --screenshot-tour                                     also capture each screen and panel once
    /// --quit-after &lt;seconds&gt;                               quit after that long
    /// --demo-walk                                           walk a small figure in the world
    /// --fresh-profile &lt;dir&gt;                                use this instead of persistentDataPath
    /// </code>
    /// </summary>
    public sealed class CommandLine
    {
        public string ConnectAddress;
        public string Name;
        public string Password;
        public string HostWorldDirectory;
        public int? Port;
        public string ScreenshotDirectory;
        public float ScreenshotEvery;
        public bool ScreenshotTour;
        public float QuitAfter;
        public bool DemoWalk;
        public string ProfileDirectory;

        /// <summary>Problems with the arguments, as sentences; the rest are still applied.</summary>
        public readonly List<string> Errors = new List<string>();

        public static CommandLine FromEnvironment() => Parse(Environment.GetCommandLineArgs(), skipFirst: true);

        public static CommandLine Parse(IReadOnlyList<string> args, bool skipFirst = false)
        {
            var c = new CommandLine();
            if (args == null) return c;
            for (int i = skipFirst ? 1 : 0; i < args.Count; i++)
            {
                string a = args[i] ?? "";
                string value = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 2)
                {
                    value = a.Substring(eq + 1);
                    a = a.Substring(0, eq);
                }

                string Next()
                {
                    if (value != null) return value;
                    if (i + 1 < args.Count && !(args[i + 1] ?? "").StartsWith("--", StringComparison.Ordinal)) return args[++i];
                    c.Errors.Add($"{a} needs a value.");
                    return null;
                }

                switch (a)
                {
                    case "--connect": c.ConnectAddress = Next(); break;
                    case "--name": c.Name = Next(); break;
                    case "--password": c.Password = Next(); break;
                    case "--host": c.HostWorldDirectory = Next(); break;
                    case "--port":
                        {
                            string v = Next();
                            if (v == null) break;
                            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p >= 1 && p <= 65535) c.Port = p;
                            else c.Errors.Add($"--port: \"{v}\" is not a port number (1 to 65535).");
                            break;
                        }
                    case "--screenshot-dir": c.ScreenshotDirectory = Next(); break;
                    case "--screenshot-every": c.ScreenshotEvery = Seconds(a, Next(), c); break;
                    case "--screenshot-tour": c.ScreenshotTour = true; break;
                    case "--quit-after": c.QuitAfter = Seconds(a, Next(), c); break;
                    case "--demo-walk": c.DemoWalk = true; break;
                    case "--fresh-profile": c.ProfileDirectory = Next(); break;
                }
            }
            if (c.ConnectAddress != null && c.HostWorldDirectory != null)
                c.Errors.Add("--connect and --host cannot be used together; hosting wins.");
            return c;
        }

        static float Seconds(string option, string v, CommandLine c)
        {
            if (v == null) return 0f;
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float s) && s > 0f && !float.IsInfinity(s)) return s;
            c.Errors.Add($"{option}: \"{v}\" is not a positive number of seconds.");
            return 0f;
        }
    }
}
