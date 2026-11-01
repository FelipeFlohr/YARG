using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.WebGL;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Builds the WebGL player (batchmode).
    /// Usage: <c>-buildTarget WebGL -executeMethod Editor.WebBuild.Build [-webBuildOutput dir] [-webDiagnostics] [-webDev]</c>,
    /// the output defaults to <c>~/yarg-build-web</c>.
    /// <para>
    /// <c>-webDev</c> is a fast, incremental build for iterating (not for deploying): it skips the shader graph
    /// reimport and the Addressables clean, uses a development player with debug IL2CPP, build-time-optimized
    /// WebAssembly (<c>WasmCodeOptimization.BuildTimes</c>), the 2023 WebAssembly features and no compression
    /// (the files have no <c>.br</c> extension). Keep the same output folder and do not clean Library/Bee between
    /// builds. The player settings it changes are restored afterwards, so they never stay in ProjectSettings.asset.
    /// </para>
    /// Exit codes: 0 on success, 1 when the player build fails, 2 when the Addressables build fails.
    /// </summary>
    public static class WebBuild
    {
        private const string OUTPUT_ARG = "-webBuildOutput";
        private const string DIAGNOSTICS_ARG = "-webDiagnostics";
        private const string DEV_ARG = "-webDev";
        private const string YARG_NO_THREADS = "YARG_NO_THREADS";
        private const string ZSTRING_TEXTMESHPRO_SUPPORT = "ZSTRING_TEXTMESHPRO_SUPPORT";
        private const string MANIFEST_NAME = "web-manifest.json";

        // Folders of StreamingAssets that YargStreamingAssets.jspre does not copy into the in-memory file system:
        // the venue is not available on the web, and Addressables loads its bundles by URL.
        private static readonly string[] MANIFEST_EXCLUDED_FOLDERS = { "venue/", "aa/" };

        private static readonly string[] NATIVE_LIBRARY_EXTENSIONS = { ".dll", ".so", ".dylib", ".bundle", ".lib" };

        public static void Build()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string output = Path.Combine(home, "yarg-build-web");

            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, OUTPUT_ARG);
            if (index >= 0 && index + 1 < args.Length)
            {
                output = args[index + 1];
            }
            output = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            bool dev = args.Contains(DEV_ARG);
            DevSettingsBackup backup = null;

            try
            {
                if (!dev)
                {
                    // Only needed on a fresh Library, and it makes every shader recompile
                    MultiplayerTestBuild.ReimportShaderGraphs();
                }

                // Addressables content has to be built before the player (kept incremental in the dev build)
                if (!dev)
                {
                    AddressableAssetSettings.CleanPlayerContent();
                }
                AddressableAssetSettings.BuildPlayerContent(out var addressablesResult);
                if (!string.IsNullOrEmpty(addressablesResult.Error))
                {
                    Debug.LogError($"[WebBuild] Addressables build failed: {addressablesResult.Error}");
                    backup?.Restore();
                    EditorApplication.Exit(2);
                    return;
                }

                ConfigurePlayerSettings();

                if (dev)
                {
                    backup = ApplyDevSettings();
                }
                Debug.Log($"[WebBuild] codeOptimization: {UserBuildSettings.codeOptimization}");

                // Managed stack traces in the browser console, for finding where an exception comes from
                PlayerSettings.WebGL.exceptionSupport = args.Contains(DIAGNOSTICS_ARG)
                    ? WebGLExceptionSupport.FullWithStacktrace
                    : WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

                Directory.CreateDirectory(output);

                var options = new BuildPlayerOptions
                {
                    scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                    target = BuildTarget.WebGL,
                    targetGroup = BuildTargetGroup.WebGL,
                    locationPathName = output,
                    extraScriptingDefines = new[] { YARG_NO_THREADS },
                };
                if (dev)
                {
                    options.options |= BuildOptions.Development;
                }

                Debug.Log($"[WebBuild] building {options.scenes.Length} scenes to {options.locationPathName}");
                var summary = BuildPipeline.BuildPlayer(options).summary;
                Debug.Log($"[WebBuild] result: {summary.result}, {summary.totalSize} bytes, {summary.totalTime}");
                if (summary.result != BuildResult.Succeeded)
                {
                    backup?.Restore();
                    EditorApplication.Exit(1);
                    return;
                }

                WriteManifest(Path.Combine(output, "StreamingAssets"));
                backup?.Restore();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[WebBuild] failed: {e}");
                backup?.Restore();
                EditorApplication.Exit(1);
            }
            finally
            {
                backup?.Restore();
            }
        }

        private sealed class DevSettingsBackup
        {
            public WebGLCompressionFormat Compression;
            public bool Wasm2023;
            public Il2CppCompilerConfiguration Compiler;
            public Il2CppCodeGeneration CodeGeneration;
            public WasmCodeOptimization CodeOptimization;

            public void Restore()
            {
                PlayerSettings.WebGL.compressionFormat = Compression;
                PlayerSettings.WebGL.wasm2023 = Wasm2023;
                PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Compiler);
                PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, CodeGeneration);
                UserBuildSettings.codeOptimization = CodeOptimization;
                AssetDatabase.SaveAssets();
            }
        }

        private static DevSettingsBackup ApplyDevSettings()
        {
            var backup = new DevSettingsBackup
            {
                Compression = PlayerSettings.WebGL.compressionFormat,
                Wasm2023 = PlayerSettings.WebGL.wasm2023,
                Compiler = PlayerSettings.GetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL),
                CodeGeneration = PlayerSettings.GetIl2CppCodeGeneration(NamedBuildTarget.WebGL),
                CodeOptimization = UserBuildSettings.codeOptimization,
            };

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.wasm2023 = true;
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Debug);
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Il2CppCodeGeneration.OptimizeSize);
            UserBuildSettings.codeOptimization = WasmCodeOptimization.BuildTimes;
            return backup;
        }

        /// <summary>
        /// Applies the WebGL player settings and saves them to ProjectSettings.asset. The scripting defines
        /// matter before the build too: a batchmode run on the WebGL target compiles the scripts with them before
        /// <see cref="Build"/> is called, so on a project without them run this once from another target
        /// (<c>-executeMethod Editor.WebBuild.ConfigurePlayerSettings -quit</c>).
        /// </summary>
        public static void ConfigurePlayerSettings()
        {
            PlayerSettings.WebGL.memoryGrowthMode = WebGLMemoryGrowthMode.Geometric;
            PlayerSettings.WebGL.maximumMemorySize = 2048;
            PlayerSettings.WebGL.threadsSupport = false;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.template = "PROJECT:YARG";
            PlayerSettings.WebGL.decompressionFallback = false;

            // Same as Standalone: enables ZString's TextMeshPro extensions (TMP ships inside com.unity.ugui, so the
            // asmdef's version define for com.unity.textmeshpro never fires)
            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL, out string[] defines);
            if (!defines.Contains(ZSTRING_TEXTMESHPRO_SUPPORT))
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL,
                    defines.Append(ZSTRING_TEXTMESHPRO_SUPPORT).ToArray());
            }

            ExcludeNativePluginsFromWeb();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Native desktop libraries (BASS, Discord, SQLite, VLC, LibUSB, ...) are imported for "Any Platform" and
        /// would otherwise collide in the WebGL build, which cannot load them anyway.
        /// </summary>
        private static void ExcludeNativePluginsFromWeb()
        {
            foreach (var importer in PluginImporter.GetAllImporters())
            {
                string path = importer.assetPath;
                if (!importer.isNativePlugin || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !NATIVE_LIBRARY_EXTENSIONS.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    continue;
                }

                bool changed = false;
                if (importer.GetCompatibleWithAnyPlatform() && !importer.GetExcludeFromAnyPlatform(BuildTarget.WebGL))
                {
                    importer.SetExcludeFromAnyPlatform(BuildTarget.WebGL, true);
                    changed = true;
                }
                if (importer.GetCompatibleWithPlatform(BuildTarget.WebGL))
                {
                    importer.SetCompatibleWithPlatform(BuildTarget.WebGL, false);
                    changed = true;
                }

                if (changed)
                {
                    Debug.Log($"[WebBuild] excluding {path} from WebGL");
                    importer.SaveAndReimport();
                }
            }
        }

        /// <summary>
        /// Writes a JSON array with the path, relative to <paramref name="streamingAssets"/>, of every file the
        /// page has to copy into the in-memory file system at startup.
        /// </summary>
        private static void WriteManifest(string streamingAssets)
        {
            Directory.CreateDirectory(streamingAssets);

            var files = Directory.GetFiles(streamingAssets, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(streamingAssets, path).Replace('\\', '/'))
                .Where(path => path != MANIFEST_NAME)
                .Where(path => !MANIFEST_EXCLUDED_FOLDERS.Any(folder =>
                    path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            var json = new StringBuilder();
            json.Append('[');
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }
                json.Append("\n  ");
                AppendJsonString(json, files[i]);
            }
            json.Append(files.Count > 0 ? "\n]\n" : "]\n");

            File.WriteAllText(Path.Combine(streamingAssets, MANIFEST_NAME), json.ToString(), new UTF8Encoding(false));
            Debug.Log($"[WebBuild] wrote {MANIFEST_NAME} with {files.Count} files");
        }

        private static void AppendJsonString(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    default:
                        if (c < ' ')
                        {
                            json.Append($"\\u{(int) c:x4}");
                        }
                        else
                        {
                            json.Append(c);
                        }
                        break;
                }
            }
            json.Append('"');
        }
    }
}
