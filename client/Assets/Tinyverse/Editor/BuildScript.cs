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
    /// </summary>
    public static class BuildScript
    {
        public const string LinuxOutput = "Builds/Linux/tinyverse.x86_64";

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

                Directory.CreateDirectory(Path.GetDirectoryName(LinuxOutput));
                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = LinuxOutput,
                    target = BuildTarget.StandaloneLinux64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log($"[BuildScript] {summary.result}: {summary.outputPath}, {summary.totalSize} bytes, " +
                          $"{summary.totalErrors} errors, {summary.totalWarnings} warnings, {summary.totalTime}");
                code = summary.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception e)
            {
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
    }
}
