using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;

namespace CI
{
    public static class Build
    {
        private const string TurboJpegApkEntry = "lib/arm64-v8a/libturbojpeg.so";
        private const string SqliteApkEntry = "lib/arm64-v8a/libe_sqlite3.so";

        public static void PerformAndroid()
        {
            string projectPath = Directory.GetCurrentDirectory().Replace("\\", "/");
            string outputDir = Path.Combine(projectPath, "build/Android");
            Directory.CreateDirectory(outputDir);

            string outputFile = Path.Combine(outputDir, "QuestX-local.apk");

            // Ensure Android is the active target so package asmdefs pick correct platform
            EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.Android,
                BuildTarget.Android
            );

            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

            ConfigureRestoredSqlitePlugin();

            // Preload OpenXR settings to avoid "Please build again" error in batch mode
            // This is a known Unity issue where OpenXR settings aren't loaded in batch mode
            try
            {
                // Force load XR settings for Android
                var buildTargetGroup = BuildTargetGroup.Android;
                var xrSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(
                    buildTargetGroup
                );

                if (xrSettings != null && xrSettings.Manager != null)
                {
                    Debug.Log("OpenXR settings loaded successfully");

                    // Try to initialize the loaders to ensure settings are fully loaded
                    var managers = xrSettings.Manager;
                    if (managers != null)
                    {
                        Debug.Log(
                            $"XR Manager found with {managers.activeLoaders.Count} active loaders"
                        );
                    }
                }
                else
                {
                    Debug.LogWarning(
                        "OpenXR settings or Manager is null. Build may fail with 'Please build again' error."
                    );
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to preload OpenXR settings: {e.Message}");
            }

            // Force asset database to refresh to ensure all settings are loaded
            UnityEditor.AssetDatabase.Refresh();

            try
            {
                var target = NamedBuildTarget.Android;
                var defines = PlayerSettings.GetScriptingDefineSymbols(target) ?? string.Empty;
                string[] needed = new[]
                {
                    "OVRPLUGIN_ANDROID",
                    "USING_XR_SDK_OPENXR",
                    "OCULUS_OPENXR",
                    "META_XR",
                };
                foreach (var d in needed)
                {
                    if (
                        !defines
                            .Split(';')
                            .Any(x => string.Equals(x.Trim(), d, StringComparison.Ordinal))
                    )
                    {
                        defines = string.IsNullOrEmpty(defines) ? d : defines + ";" + d;
                    }
                }
                PlayerSettings.SetScriptingDefineSymbols(target, defines);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Define setup skipped: {e.Message}");
            }

            bool development = !string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable("QUESTX_DEVELOPMENT_BUILD")
            );

            string version = Environment.GetEnvironmentVariable("QUESTX_VERSION");
            string versionCodeStr = Environment.GetEnvironmentVariable("QUESTX_VERSION_CODE");
            if (!string.IsNullOrEmpty(version))
            {
                PlayerSettings.bundleVersion = version;
            }
            if (int.TryParse(versionCodeStr, out int versionCode))
            {
                PlayerSettings.Android.bundleVersionCode = versionCode;
            }

            string[] scenes = EditorBuildSettings
                .scenes.Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                throw new Exception("No enabled scenes in Build Settings.");
            }

            var buildOptions = BuildOptions.None;
            if (development)
            {
                buildOptions |= BuildOptions.Development;
            }

            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputFile,
                target = BuildTarget.Android,
                options = buildOptions,
            };

            BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new Exception(
                    $"Android build failed: {report.summary.result} (errors: {report.summary.totalErrors})"
                );
            }

            if (!File.Exists(outputFile))
            {
                throw new Exception($"Expected APK not found at {outputFile}");
            }

            VerifyNativeDependencies(outputFile);

            Debug.Log($"SUCCESS: APK built at {outputFile}");

            // Force Unity to exit cleanly after successful build
            // This prevents hanging on async operations in batch mode
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Configures the Android SQLite native library restored by NuGetForUnity. The
        /// package lives under Assets/Packages instead of Assets/Plugins, so Unity does
        /// not infer Android compatibility from its directory name.
        /// </summary>
        private static void ConfigureRestoredSqlitePlugin()
        {
            string[] candidates = Directory.GetFiles(
                Application.dataPath,
                "libe_sqlite3.so",
                SearchOption.AllDirectories
            );
            string nativePath = candidates.FirstOrDefault(path =>
                path.Replace('\\', '/').Contains("/runtimes/android-arm64/native/")
            );

            if (string.IsNullOrEmpty(nativePath))
            {
                throw new Exception(
                    "NuGet restore did not provide the Android ARM64 SQLite native library."
                );
            }

            string assetPath = "Assets" + nativePath.Substring(Application.dataPath.Length)
                .Replace('\\', '/');
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            }
            if (importer == null)
            {
                throw new Exception($"Could not configure SQLite plugin importer at '{assetPath}'.");
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
            importer.SetPlatformData(BuildTarget.Android, "CPU", "ARM64");
            importer.isPreloaded = true;
            importer.SaveAndReimport();
            Debug.Log($"Configured restored SQLite Android plugin at {assetPath}");
        }

        /// <summary>
        /// Verifies native libraries required only at runtime are actually packaged in
        /// the APK. C# compilation cannot detect a missing P/Invoke target, so without
        /// this check the build can succeed even though the app will fail on the headset
        /// with <see cref="DllNotFoundException"/>.
        /// </summary>
        private static void VerifyNativeDependencies(string apkPath)
        {
            using var apkStream = File.OpenRead(apkPath);
            using var archive = new ZipArchive(apkStream, ZipArchiveMode.Read);
            string[] requiredEntries = { TurboJpegApkEntry, SqliteApkEntry };
            foreach (string requiredEntry in requiredEntries)
            {
                ZipArchiveEntry nativeLibrary = archive.GetEntry(requiredEntry);
                if (nativeLibrary == null || nativeLibrary.Length == 0)
                {
                    throw new Exception(
                        $"Android build is missing required native library '{requiredEntry}'."
                    );
                }

                Debug.Log(
                    $"Verified native dependency {requiredEntry} ({nativeLibrary.Length} bytes)"
                );
            }
        }
    }
}
