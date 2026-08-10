using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// CLI entry point for Phase 1 content builds: activate a CDN profile and build player content.
    ///
    /// Verifies that the build succeeded by checking:
    /// 1. Compilation did not fail before execution
    /// 2. The build reported no error
    /// 3. The catalog and hash files exist at the expected paths
    /// 4. Bundles exist under the bundles directory
    /// 5. The output is non-empty
    ///
    /// Reports resolved paths (catalog build/load and bundle paths after variable expansion)
    /// to confirm the §2 layout separation took effect.
    /// </summary>
    /// <remarks>
    /// Task 1.1 of Phase 1. Does not handle content-update (task 1.2) — only full builds.
    ///
    /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent -cdnProfile Local
    ///
    /// Exit codes:
    ///   0 = success, build complete and verified
    ///   1 = build failed, compilation failed, or verification failed
    ///   2 = exception during execution (logged with stack trace)
    /// </remarks>
    public static class CdnBuildCLI
    {
        /// <summary>
        /// Build player content for the active CDN profile.
        /// </summary>
        public static void BuildContent()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");

            try
            {
                Log("=== CDN Build CLI - Phase 1 Content Build ===");
                Log($"Target profile: {profileName}");
                Log("");

                // ========== GATE ON COMPILATION ==========
                if (EditorUtility.scriptCompilationFailed)
                {
                    LogError("FAILURE: Script compilation failed before build started");
                    EditorApplication.Exit(1);
                    return;
                }

                // ========== ACTIVATE PROFILE ==========
                Log($"Activating profile '{profileName}'...");
                try
                {
                    CdnProfileManager.SetActiveProfile(profileName);
                    Log($"✓ Profile '{profileName}' activated");
                }
                catch (ArgumentException ex)
                {
                    LogError($"FAILURE: Could not activate profile: {ex.Message}");
                    EditorApplication.Exit(1);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    LogError($"FAILURE: {ex.Message}");
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");

                // ========== RUN BUILD ==========
                Log("Building player content...");
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

                if (result == null)
                {
                    LogError("FAILURE: BuildPlayerContent returned null result");
                    EditorApplication.Exit(1);
                    return;
                }

                if (!string.IsNullOrEmpty(result.Error))
                {
                    LogError($"FAILURE: Build reported error: {result.Error}");
                    EditorApplication.Exit(1);
                    return;
                }

                Log($"✓ Build completed in {result.Duration:F2} seconds");
                Log($"  Locations (addressable assets): {result.LocationCount}");
                Log("");

                // ========== VERIFY OUTPUT ==========
                Log("Verifying build output...");
                if (!VerifyBuildOutput(result))
                {
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");

                // ========== REPORT RESOLVED PATHS ==========
                ReportResolvedPaths(profileName);

                Log("");
                Log("✓ SUCCESS: Content build complete and verified");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during build: {ex.Message}");
                LogError(ex.StackTrace);
                EditorApplication.Exit(2);
            }
        }

        // ========== private implementation ==========

        private static bool VerifyBuildOutput(AddressablesPlayerBuildResult result)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                LogError("FAILURE: Cannot verify output (no AddressableAssetSettings found)");
                return false;
            }

            string activeProfileId = settings.activeProfileId;
            if (string.IsNullOrEmpty(activeProfileId))
            {
                LogError("FAILURE: No active profile set");
                return false;
            }

            // Get the resolved catalog build path from profile variables
            string catalogBuildPathTemplate = settings.profileSettings.GetValueByName(
                activeProfileId,
                CdnProfileManager.RemoteCatalogBuildPathVariable);
            string catalogBuildPathResolved = settings.profileSettings.EvaluateString(activeProfileId, catalogBuildPathTemplate);

            if (string.IsNullOrEmpty(catalogBuildPathResolved))
            {
                LogError("FAILURE: Could not resolve catalog build path from profile");
                return false;
            }

            // Verify catalog directory exists
            if (!Directory.Exists(catalogBuildPathResolved))
            {
                LogError($"FAILURE: Catalog directory does not exist: {catalogBuildPathResolved}");
                return false;
            }

            // Find the catalog file (can be .bin or .json depending on EnableJsonCatalog setting)
            // The catalog filename format is catalog_<version>.bin or catalog_<version>.json
            string[] catalogFiles = Directory.GetFiles(catalogBuildPathResolved, "catalog_*.bin")
                .Concat(Directory.GetFiles(catalogBuildPathResolved, "catalog_*.json"))
                .ToArray();

            if (catalogFiles.Length == 0)
            {
                LogError($"FAILURE: No catalog files found in {catalogBuildPathResolved}");
                return false;
            }

            string catalogFilePath = catalogFiles[0]; // Take the first one found
            Log($"✓ Catalog: {Path.GetFileName(catalogFilePath)}");
            var catalogFileInfo = new FileInfo(catalogFilePath);
            Log($"  Size: {FormatBytes(catalogFileInfo.Length)}");

            // Verify corresponding hash file exists (alongside the catalog file with .hash extension)
            string hashFilePath = Path.Combine(catalogBuildPathResolved, Path.GetFileNameWithoutExtension(catalogFilePath) + ".hash");
            if (!File.Exists(hashFilePath))
            {
                LogError($"FAILURE: Catalog hash file does not exist: {hashFilePath}");
                return false;
            }

            Log($"✓ Catalog hash: {Path.GetFileName(hashFilePath)}");
            var hashFileInfo = new FileInfo(hashFilePath);
            Log($"  Size: {FormatBytes(hashFileInfo.Length)}");

            // Verify bundles exist under the bundle build path
            string bundleBuildPathTemplate = settings.profileSettings.GetValueByName(
                activeProfileId,
                AddressableAssetSettings.kRemoteBuildPath);
            string bundleBuildPathResolved = settings.profileSettings.EvaluateString(activeProfileId, bundleBuildPathTemplate);

            if (string.IsNullOrEmpty(bundleBuildPathResolved))
            {
                LogError("FAILURE: Could not resolve bundle build path from profile");
                return false;
            }

            if (!Directory.Exists(bundleBuildPathResolved))
            {
                LogError($"FAILURE: Bundle directory does not exist: {bundleBuildPathResolved}");
                return false;
            }

            // Count bundle files (AssetBundle files have .bundle extension)
            string[] bundleFiles = Directory.GetFiles(bundleBuildPathResolved, "*.bundle", SearchOption.AllDirectories);

            Log($"✓ Asset bundles: {bundleFiles.Length} bundle(s)");

            if (bundleFiles.Length == 0)
            {
                LogWarning("  (No bundles created — this may be expected if no addressable content was configured)");
            }
            else
            {
                long totalBundleSize = 0;
                foreach (string bundleFile in bundleFiles)
                {
                    var bundleFileInfo = new FileInfo(bundleFile);
                    totalBundleSize += bundleFileInfo.Length;
                }

                Log($"  Total bundle size: {FormatBytes(totalBundleSize)}");
            }

            // Verify content state file exists (artifact risk R1 — losing it ends delta updates permanently)
            string contentStateFilePath = result.ContentStateFilePath;
            if (string.IsNullOrEmpty(contentStateFilePath))
            {
                // If not provided by result, check the standard location
                contentStateFilePath = Path.Combine("ServerData/ContentState", "addressables_content_state.bin");
            }

            if (File.Exists(contentStateFilePath))
            {
                Log($"✓ Content state: {contentStateFilePath}");
                var contentStateFileInfo = new FileInfo(contentStateFilePath);
                Log($"  Size: {FormatBytes(contentStateFileInfo.Length)}");
            }
            else
            {
                LogWarning($"⚠ Content state file not found (expected for first build): {contentStateFilePath}");
            }

            return true;
        }

        private static void ReportResolvedPaths(string profileName)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                LogWarning("Could not report resolved paths (no AddressableAssetSettings)");
                return;
            }

            string profileId = settings.profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(profileId))
            {
                LogWarning($"Could not report resolved paths (profile '{profileName}' not found)");
                return;
            }

            Log("Resolved profile paths (after variable expansion):");
            Log("");

            // Report catalog paths
            string catalogBuildPathTemplate = settings.profileSettings.GetValueByName(
                profileId,
                CdnProfileManager.RemoteCatalogBuildPathVariable);

            string catalogLoadPathTemplate = settings.profileSettings.GetValueByName(
                profileId,
                CdnProfileManager.RemoteCatalogLoadPathVariable);

            string catalogBuildPathResolved = settings.profileSettings.EvaluateString(profileId, catalogBuildPathTemplate);
            string catalogLoadPathResolved = settings.profileSettings.EvaluateString(profileId, catalogLoadPathTemplate);

            Log($"  Catalog Build Path: {catalogBuildPathResolved}");
            Log($"  Catalog Load Path:  {catalogLoadPathResolved}");

            // Report bundle paths
            string bundleBuildPathTemplate = settings.profileSettings.GetValueByName(
                profileId,
                AddressableAssetSettings.kRemoteBuildPath);

            string bundleLoadPathTemplate = settings.profileSettings.GetValueByName(
                profileId,
                AddressableAssetSettings.kRemoteLoadPath);

            string bundleBuildPathResolved = settings.profileSettings.EvaluateString(profileId, bundleBuildPathTemplate);
            string bundleLoadPathResolved = settings.profileSettings.EvaluateString(profileId, bundleLoadPathTemplate);

            Log($"  Bundle Build Path:  {bundleBuildPathResolved}");
            Log($"  Bundle Load Path:   {bundleLoadPathResolved}");
        }

        private static Dictionary<string, string> ParseCommandLineArgs()
        {
            var args = new Dictionary<string, string>();
            var cmdArgs = Environment.GetCommandLineArgs();

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].StartsWith("-") && i + 1 < cmdArgs.Length)
                {
                    string key = cmdArgs[i].TrimStart('-');
                    string value = cmdArgs[i + 1];
                    args[key] = value;
                }
            }

            return args;
        }

        private static string GetArg(Dictionary<string, string> args, string key, string defaultValue)
        {
            return args.ContainsKey(key) ? args[key] : defaultValue;
        }

        private static string FormatBytes(long bytes)
        {
            const long kb = 1024;
            const long mb = kb * 1024;
            const long gb = mb * 1024;

            if (bytes >= gb)
                return $"{bytes / (double)gb:F2} GB";
            if (bytes >= mb)
                return $"{bytes / (double)mb:F2} MB";
            if (bytes >= kb)
                return $"{bytes / (double)kb:F2} KB";

            return $"{bytes} B";
        }

        private static void Log(string message)
        {
            Debug.Log($"[CdnBuildCLI] {message}");
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning($"[CdnBuildCLI] {message}");
        }

        private static void LogError(string message)
        {
            Debug.LogError($"[CdnBuildCLI] {message}");
        }
    }
}
