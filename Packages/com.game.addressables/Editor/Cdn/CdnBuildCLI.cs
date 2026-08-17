using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Batchmode entry points for content builds and output verification — tasks 1.1, 1.2, 1.6.
    /// </summary>
    /// <remarks>
    /// A thin shell. The gated build sequence lives in <see cref="CdnBuildPipeline"/> so the Build
    /// tab runs exactly the same steps in exactly the same order; what belongs here is only what is
    /// specific to running headless — command line arguments, console logging, and exit codes.
    ///
    /// Output verification is <see cref="CatalogVerifier"/>'s job and the pipeline calls it before
    /// reporting success, so a build that passes locally cannot fail CI verification for a reason
    /// that was already visible on the build machine.
    ///
    /// Usage:
    ///   Unity -batchmode -quit -nographics -projectPath &lt;repo&gt; -logFile build.log \
    ///     -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent -cdnProfile Local
    ///   ... .BuildContentUpdate -cdnProfile Local [-contentStatePath &lt;path&gt;]
    ///   ... .VerifyOutput       -cdnProfile Local [-manifest &lt;path&gt;]
    ///
    /// Exit codes:
    ///   0 = success
    ///   1 = refused, failed, or verification found problems
    ///   2 = unhandled exception (logged with stack trace)
    /// </remarks>
    public static class CdnBuildCLI
    {
        /// <summary>Full content build — task 1.1.</summary>
        public static void BuildContent()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");

            try
            {
                Log("=== CDN Build CLI - Full Content Build (task 1.1) ===");
                Log($"Target profile: {profileName}");
                Log("");

                // Same gate VerifyOutput already carried. Unity exits 0 from -executeMethod even when
                // the assembly failed to compile, so exit status alone cannot tell a CI step that the
                // build it just "completed" was produced by the code in the commit.
                if (EditorUtility.scriptCompilationFailed)
                {
                    LogError("FAILURE: Script compilation failed before the build started");
                    EditorApplication.Exit(1);
                    return;
                }

                var outcome = CdnBuildPipeline.BuildFull(profileName, Sink);
                if (outcome.IsFailure)
                {
                    LogError($"FAILURE: {outcome.ErrorMessage}");
                    EditorApplication.Exit(1);
                    return;
                }

                ReportOutcome(outcome.Value, profileName);

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

        /// <summary>Delta content update — task 1.2.</summary>
        public static void BuildContentUpdate()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");
            string contentStateOverride = GetArg(args, "contentStatePath", null);
            var started = DateTime.Now;

            try
            {
                Log("=== CDN Build CLI - Content Update (task 1.2) ===");
                Log($"Target profile: {profileName}");
                Log("");

                // See BuildContent. A delta build is the worse of the two to get wrong: it writes a
                // new content_state.bin, so a run against a half-compiled editor poisons the baseline
                // every later update diffs against.
                if (EditorUtility.scriptCompilationFailed)
                {
                    LogError("FAILURE: Script compilation failed before the content update started");
                    EditorApplication.Exit(1);
                    return;
                }

                var outcome = CdnBuildPipeline.BuildUpdate(profileName, contentStateOverride, Sink);
                if (outcome.IsFailure)
                {
                    LogError($"FAILURE: {outcome.ErrorMessage}");
                    EditorApplication.Exit(1);
                    return;
                }

                ReportOutcome(outcome.Value, profileName);

                Log("");
                Log($"✓ SUCCESS: Content update complete and verified (elapsed {(DateTime.Now - started).TotalSeconds:F1}s)");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during content update: {ex.Message}");
                LogError(ex.StackTrace);
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Verify a build's output against its manifest and the settings contract — task 1.6.
        /// </summary>
        /// <remarks>
        /// Standalone so CI can verify an artifact it did not build in the same step: unpack the
        /// archived output plus its manifest, point this at them, and get a non-zero exit if
        /// anything a player needs is missing or altered.
        /// </remarks>
        public static void VerifyOutput()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");

            // -manifest is the spelling infrastructure §7 uses; -manifestPath is this module's.
            // A CI step that silently verified the default path instead of the one it was handed
            // would pass while checking the wrong artifact.
            string manifestPath = GetArg(args, "manifestPath", null) ?? GetArg(args, "manifest", null);

            try
            {
                Log("=== CDN Build CLI - Output Verification (task 1.6) ===");
                Log($"Target profile: {profileName}");
                Log("");

                if (EditorUtility.scriptCompilationFailed)
                {
                    LogError("FAILURE: Script compilation failed before verification started");
                    EditorApplication.Exit(1);
                    return;
                }

                try
                {
                    CdnProfileManager.SetActiveProfile(profileName);
                }
                catch (Exception ex)
                {
                    LogError($"FAILURE: Could not activate profile: {ex.Message}");
                    EditorApplication.Exit(1);
                    return;
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    LogError("FAILURE: No AddressableAssetSettings found");
                    EditorApplication.Exit(1);
                    return;
                }

                string bundleDir = CdnBuildPipeline.ResolveBundleDir(settings);
                string catalogDir = CdnBuildPipeline.ResolveCatalogDir(settings);

                Log($"Bundles:  {bundleDir}");
                Log($"Catalog:  {catalogDir}");
                Log("");

                var verification = CatalogVerifier.Verify(bundleDir, catalogDir, manifestPath);

                foreach (string warning in verification.Warnings)
                    LogWarning($"  {warning}");

                if (!verification.Passed)
                {
                    foreach (string problem in verification.Problems)
                        LogError($"  {problem}");

                    LogError("");
                    LogError($"FAILURE: {verification.Problems.Count} problem(s) found. Do NOT upload this output.");
                    EditorApplication.Exit(1);
                    return;
                }

                Log($"✓ {verification.BundlesChecked} bundle(s) verified against the manifest");
                Log("✓ Catalog and hash file present and unmodified");
                Log("✓ Settings contract holds");
                if (verification.Warnings.Count > 0)
                    Log($"  ({verification.Warnings.Count} warning(s) above, none blocking)");

                Log("");
                Log("✓ SUCCESS: Output is consistent with its manifest and safe to publish");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during verification: {ex.Message}");
                LogError(ex.StackTrace);
                EditorApplication.Exit(2);
            }
        }

        // ========== private implementation ==========

        /// <summary>Routes pipeline progress into the batchmode log.</summary>
        private static void Sink(string message, BuildLogLevel level)
        {
            switch (level)
            {
                case BuildLogLevel.Error:
                    LogError($"  {message}");
                    break;
                case BuildLogLevel.Warning:
                    LogWarning($"  {message}");
                    break;
                default:
                    Log(message);
                    break;
            }
        }

        private static void ReportOutcome(BuildOutcome outcome, string profileName)
        {
            Log("");

            if (outcome.Patch != null && outcome.Patch.available)
            {
                var patch = outcome.Patch;
                Log($"Patch vs the previous build ({patch.comparedToGitSha}, {patch.comparedToBuildDate}):");
                Log($"  {patch.newBundleCount} new, {patch.changedBundleCount} changed, " +
                    $"{patch.removedBundleCount} removed, {patch.unchangedBundleCount} unchanged" +
                    $"{(patch.catalogChanged ? ", catalog changed" : "")}");
                Log($"  A player on that build downloads {FormatBytes(patch.patchSizeBytes)} ({patch.patchSizeBytes:N0} B)");
            }
            else
            {
                Log("No previous manifest to compare against, so no patch size for this build.");
            }

            Log("");
            Log($"Content state to archive: {outcome.ContentStatePath}");
            if (!string.IsNullOrEmpty(outcome.ContentStatePath) && File.Exists(outcome.ContentStatePath))
            {
                var info = new FileInfo(outcome.ContentStatePath);
                Log($"  Size: {FormatBytes(info.Length)}, last written {info.LastWriteTime:O}");
            }

            if (outcome.IsUpdateBuild)
            {
                // An update consumes a state file and does not produce one, so the baseline for the
                // next update is still this build's input. Said explicitly because archiving the
                // wrong file is silent until the next patch cannot be built.
                Log("  Unchanged by this update - CI must archive THIS file as the next baseline.");
            }

            Log("");
            ReportResolvedPaths(profileName);
        }

        /// <summary>
        /// Print the expanded profile paths, so a log shows where the output actually went rather
        /// than which variables were configured.
        /// </summary>
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
            Log($"  Catalog Build Path: {Evaluate(settings, profileId, CdnProfileManager.RemoteCatalogBuildPathVariable)}");
            Log($"  Catalog Load Path:  {Evaluate(settings, profileId, CdnProfileManager.RemoteCatalogLoadPathVariable)}");
            Log($"  Bundle Build Path:  {Evaluate(settings, profileId, AddressableAssetSettings.kRemoteBuildPath)}");
            Log($"  Bundle Load Path:   {Evaluate(settings, profileId, AddressableAssetSettings.kRemoteLoadPath)}");
        }

        private static string Evaluate(AddressableAssetSettings settings, string profileId, string variableName)
        {
            return settings.profileSettings.EvaluateString(
                profileId,
                settings.profileSettings.GetValueByName(profileId, variableName));
        }

        private static Dictionary<string, string> ParseCommandLineArgs()
        {
            var args = new Dictionary<string, string>();
            var cmdArgs = Environment.GetCommandLineArgs();

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].StartsWith("-") && i + 1 < cmdArgs.Length)
                {
                    args[cmdArgs[i].TrimStart('-')] = cmdArgs[i + 1];
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

            if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
            if (bytes >= mb) return $"{bytes / (double)mb:F2} MB";
            if (bytes >= kb) return $"{bytes / (double)kb:F2} KB";
            return $"{bytes} B";
        }

        private static void Log(string message) => Debug.Log($"[CdnBuildCLI] {message}");
        private static void LogWarning(string message) => Debug.LogWarning($"[CdnBuildCLI] {message}");
        private static void LogError(string message) => Debug.LogError($"[CdnBuildCLI] {message}");
    }
}
