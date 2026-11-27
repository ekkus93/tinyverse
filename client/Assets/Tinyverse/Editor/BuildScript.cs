using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Tinyverse.Editor
{
    /// <summary>
    /// Command-line builds. Call with
    /// <c>-batchmode -quit -projectPath client -executeMethod Tinyverse.Editor.BuildScript.BuildLinux</c>.
    /// The server binary (<c>server/target/release/tinyverse-server</c>, else <c>debug</c>) is copied into the
    /// player's <c>StreamingAssets/server/</c>, where <c>ServerLocator</c> looks in a packaged build.
    /// </summary>
    public static class BuildScript
    {
        public const string LinuxOutput = "Builds/Linux/tinyverse.x86_64";
        public const string ServerBinaryName = "tinyverse-server";
        /// <summary>Overrides the output path (used by scripts/package-linux.sh).</summary>
        public const string OutputEnvironmentVariable = "TINYVERSE_BUILD_OUTPUT";

        public static void BuildLinux()
        {
            int code = 1;
            try
            {
                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                if (scenes.Length == 0)
                {
                    throw new InvalidOperationException("No enabled scenes in build settings.");
                }

                string server = FindServerBinary();
                Debug.Log($"[BuildScript] bundling server {server}");

                string output = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
                if (string.IsNullOrEmpty(output)) output = LinuxOutput;
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = output,
                    target = BuildTarget.StandaloneLinux64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log($"[BuildScript] {summary.result}: {summary.outputPath}, {summary.totalSize} bytes, " +
                          $"{summary.totalErrors} errors, {summary.totalWarnings} warnings, {summary.totalTime}");
                code = summary.result == BuildResult.Succeeded ? 0 : 1;
                if (code == 0) CopyServer(server, output);
            }
            catch (Exception e)
            {
                Debug.LogError("[BuildScript] " + e.Message);
                Debug.LogException(e);
                code = 1;
            }

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
            else if (code != 0)
            {
                throw new BuildFailedException("Linux build failed; see the log.");
            }
        }

        /// <summary>The release server if built, else the debug one; throws a clear error when neither exists.</summary>
        public static string FindServerBinary()
        {
            string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string release = Path.Combine(repo, "server", "target", "release", ServerBinaryName);
            string debug = Path.Combine(repo, "server", "target", "debug", ServerBinaryName);
            if (File.Exists(release)) return release;
            if (File.Exists(debug)) return debug;
            throw new FileNotFoundException(
                $"No server binary to bundle: neither {release} nor {debug} exists. Run `cargo build --release` in server/ first.");
        }

        static void CopyServer(string server, string playerPath)
        {
            string data = Path.Combine(Path.GetDirectoryName(playerPath), Path.GetFileNameWithoutExtension(playerPath) + "_Data");
            string dir = Path.Combine(data, "StreamingAssets", "server");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, ServerBinaryName);
            File.Copy(server, target, true);
            // File.Copy keeps the permission bits on Linux with Mono, but make sure the binary is executable.
            try
            {
                var p = System.Diagnostics.Process.Start("chmod", $"755 \"{target}\"");
                p?.WaitForExit(5000);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BuildScript] chmod failed: " + e.Message);
            }
            Debug.Log($"[BuildScript] server copied to {target}");
        }
    }
}
