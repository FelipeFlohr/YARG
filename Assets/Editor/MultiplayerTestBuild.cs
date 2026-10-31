using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Builds a Linux player into its own folder for the multiplayer band tests (batchmode, see Tests/MultiplayerBand).
    /// Usage: <c>-executeMethod Editor.MultiplayerTestBuild.Build [-mpBuildOutput dir]</c>.
    /// </summary>
    public static class MultiplayerTestBuild
    {
        private const string OUTPUT_ARG = "-mpBuildOutput";
        private const string YARG_TEST_BUILD = "YARG_TEST_BUILD";

        public static void Build()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string output = Path.Combine(home, "yarg-build-mp-banda");

            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, OUTPUT_ARG);
            if (index >= 0 && index + 1 < args.Length)
            {
                output = args[index + 1];
            }
            output = Path.GetFullPath(output).TrimEnd('/');

            // That folder belongs to the regular test build
            string forbidden = Path.Combine(home, "yarg-build");
            if (output == forbidden || output.StartsWith(forbidden + "/", StringComparison.Ordinal))
            {
                Debug.LogError($"[MultiplayerTestBuild] refusing to write to {output}");
                EditorApplication.Exit(1);
                return;
            }

            try
            {
                // Addressables content has to be built before the player
                AddressableAssetSettings.CleanPlayerContent();
                AddressableAssetSettings.BuildPlayerContent(out var addressablesResult);
                if (!string.IsNullOrEmpty(addressablesResult.Error))
                {
                    Debug.LogError($"[MultiplayerTestBuild] Addressables build failed: {addressablesResult.Error}");
                    EditorApplication.Exit(2);
                    return;
                }

                Directory.CreateDirectory(output);

                var options = new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    target = BuildTarget.StandaloneLinux64,
                    locationPathName = Path.Combine(output, "YARG.x86_64"),
                    extraScriptingDefines = new[] { YARG_TEST_BUILD },
                };

                Debug.Log($"[MultiplayerTestBuild] building {options.scenes.Length} scenes to {options.locationPathName}");
                var summary = BuildPipeline.BuildPlayer(options).summary;
                Debug.Log($"[MultiplayerTestBuild] result: {summary.result}");
                EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MultiplayerTestBuild] failed: {e}");
                EditorApplication.Exit(1);
            }
        }
    }
}
