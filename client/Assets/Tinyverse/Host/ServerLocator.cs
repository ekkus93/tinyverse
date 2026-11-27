using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tinyverse.Host.Platform;

namespace Tinyverse.Host
{
    /// <summary>Result of <see cref="ServerLocator.Locate(string)"/>.</summary>
    public sealed class ServerLocation
    {
        internal ServerLocation(string path, string source, IReadOnlyList<string> searched, string explanation)
        {
            Path = path;
            Source = source;
            Searched = searched;
            Explanation = explanation;
        }

        public bool Found => Path != null;

        /// <summary>Full path to the binary, or null.</summary>
        public string Path { get; }

        /// <summary>Which rule found it ("explicit path", "TINYVERSE_SERVER_BIN", "StreamingAssets", "development build").</summary>
        public string Source { get; }

        /// <summary>Every path that was checked, in order.</summary>
        public IReadOnlyList<string> Searched { get; }

        /// <summary>Where it was found, or a readable list of where it was looked for.</summary>
        public string Explanation { get; }

        public override string ToString() => Explanation;
    }

    /// <summary>
    /// Finds the <c>tinyverse-server</c> binary. Order: explicit path; <c>TINYVERSE_SERVER_BIN</c>;
    /// <c>&lt;StreamingAssets&gt;/server/tinyverse-server</c>; then, in the editor or a development build only,
    /// <c>server/target/release</c> and <c>server/target/debug</c> found by walking up from the project folder.
    /// An explicit path or environment variable that points at nothing is an error rather than being skipped,
    /// so a typo never silently runs a different binary.
    /// </summary>
    public static class ServerLocator
    {
        public const string EnvironmentVariable = "TINYVERSE_SERVER_BIN";

        /// <summary>File name of the server binary on this platform.</summary>
        public static string BinaryName => ServerPlatform.Current.ServerBinaryName;

        /// <summary>Reads the directories from <c>UnityEngine.Application</c>. Call on the main thread.</summary>
        public static ServerLocation Locate(string explicitPath = null)
        {
            string streaming = UnityEngine.Application.streamingAssetsPath;
            // In the editor dataPath is <project>/Assets; in a player it is <build>/<name>_Data.
            string start = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName;
            bool development = UnityEngine.Application.isEditor || UnityEngine.Debug.isDebugBuild;
            return Locate(explicitPath, Environment.GetEnvironmentVariable(EnvironmentVariable), streaming, start, development);
        }

        /// <summary>The testable core: every input is a parameter. Null or empty inputs are skipped.</summary>
        public static ServerLocation Locate(string explicitPath, string environmentValue, string streamingAssetsPath,
            string developmentSearchStart, bool allowDevelopmentBuilds)
        {
            var searched = new List<string>();
            string name = BinaryName;

            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string p = Full(explicitPath);
                searched.Add(p);
                return File.Exists(p)
                    ? Found(p, "explicit path", searched)
                    : new ServerLocation(null, null, searched, $"The server binary given explicitly does not exist: {p}");
            }

            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                string p = Full(environmentValue);
                searched.Add(p);
                return File.Exists(p)
                    ? Found(p, EnvironmentVariable, searched)
                    : new ServerLocation(null, null, searched,
                        $"{EnvironmentVariable} is set to {p}, but there is no file there. Fix or unset it.");
            }

            if (!string.IsNullOrEmpty(streamingAssetsPath))
            {
                string p = Full(Path.Combine(streamingAssetsPath, "server", name));
                searched.Add(p);
                if (File.Exists(p)) return Found(p, "StreamingAssets", searched);
            }

            if (allowDevelopmentBuilds && !string.IsNullOrEmpty(developmentSearchStart))
            {
                var dirs = new List<string>();
                for (var d = new DirectoryInfo(Full(developmentSearchStart)); d != null; d = d.Parent)
                    dirs.Add(d.FullName);
                foreach (string profile in new[] { "release", "debug" })
                {
                    foreach (string d in dirs)
                    {
                        string p = Path.Combine(d, "server", "target", profile, name);
                        searched.Add(p);
                        if (File.Exists(p)) return Found(p, "development build", searched);
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("Could not find the Tinyverse server (").Append(name).Append("). Looked in:");
            if (searched.Count == 0) sb.Append(" nowhere (no search locations were available)");
            foreach (string s in searched) sb.Append("\n  ").Append(s);
            sb.Append("\nSet ").Append(EnvironmentVariable).Append(" to the binary's path");
            if (allowDevelopmentBuilds) sb.Append(", or build it with `cargo build --release` in server/");
            sb.Append('.');
            return new ServerLocation(null, null, searched, sb.ToString());
        }

        private static ServerLocation Found(string path, string source, List<string> searched) =>
            new ServerLocation(path, source, searched, $"Using the server at {path} ({source}).");

        private static string Full(string p) => Path.GetFullPath(p.Trim());
    }
}
