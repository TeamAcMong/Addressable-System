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
    /// Tasks 1.1 (BuildContent, full builds) and 1.2 (BuildContentUpdate, delta builds).
    ///
    /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent -cdnProfile Local
    ///        Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContentUpdate -cdnProfile Local
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

                // ========== WRITE BUILD MANIFEST (task 1.7) ==========
                if (!WriteBuildManifest(result, isUpdateBuild: false))
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

        /// <summary>
        /// Build a delta content update against the content state file left by a previous build.
        /// </summary>
        /// <remarks>
        /// Task 1.2. Every gate below runs BEFORE the build, because the failure this method
        /// exists to prevent is invisible after the fact.
        ///
        /// THE FAILURE BEING PREVENTED
        /// Addressables reads the update's version from two unrelated places and never
        /// reconciles them:
        ///   - the catalog FILENAME comes from the state file's playerVersion
        ///     (ContentUpdateScript.cs:659 → BuildScriptPackedMode.cs:713)
        ///   - the catalog FOLDER comes from the active profile, evaluated at build time
        ///     against the live PlayerSettings.bundleVersion (BuildScriptPackedMode.cs:714)
        /// Bump bundleVersion to 0.2.0, build an update against a 0.1.0 state file, and the
        /// output is catalog/0.2.0/catalog_0.1.0.bin — with an empty result.Error and exit 0.
        /// Players on 0.1.0 poll catalog/0.1.0/catalog_0.1.0.hash and get a permanent 404.
        /// The patch is not slow or broken; it is invisible. ContentStateManager.Validate is
        /// the only thing that detects it, which is why it is called before the build here.
        ///
        /// THREE DISTINCT API FAILURE SHAPES, none of which look alike:
        ///   - state file missing   → LoadContentState THROWS FileNotFoundException; it opens
        ///                            a FileStream with no existence check and no try/catch
        ///                            (ContentUpdateScript.cs:616). Only IsNullOrEmpty is guarded.
        ///   - state file invalid   → BuildContentUpdate RETURNS NULL with no Error to read
        ///                            (ContentUpdateScript.cs:655-656).
        ///   - built to wrong folder→ nothing reports it; see above.
        ///
        /// CONTENT STATE IS NOT RE-EMITTED BY AN UPDATE BUILD
        /// BuildContentUpdate sets context.PreviousContentState, and the block that writes a
        /// new state file is gated on PreviousContentState == null
        /// (BuildScriptPackedMode.cs:575). So result.ContentStateFilePath is always null after
        /// an update build, and ContentStateManager.ArchiveAfterBuild — which requires it —
        /// must not be called here. The INPUT state file is carried forward as the artifact
        /// instead. (Worse, on a full build a copy failure is swallowed by
        /// BuildScriptBase.cs:376-389, so a null path there means "silently failed", not "n/a".)
        ///
        /// Usage: Unity -batchmode -quit -nographics -projectPath &lt;repo&gt; \
        ///          -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContentUpdate \
        ///          -cdnProfile Local [-contentStatePath &lt;path&gt;] -logFile update.log
        /// </remarks>
        public static void BuildContentUpdate()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");
            string contentStateOverride = GetArg(args, "contentStatePath", null);
            var buildStartTime = DateTime.Now;

            try
            {
                Log("=== CDN Build CLI - Phase 1 Content Update (task 1.2) ===");
                Log($"Target profile: {profileName}");
                Log("");

                // ========== GATE ON COMPILATION ==========
                // Unity exits 0 even when the assembly did not compile and this method never
                // ran, so every CLI in this module gates on it explicitly.
                if (EditorUtility.scriptCompilationFailed)
                {
                    LogError("FAILURE: Script compilation failed before build started");
                    EditorApplication.Exit(1);
                    return;
                }

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    LogError("FAILURE: No AddressableAssetSettings found. Run CdnSetupCLI first.");
                    EditorApplication.Exit(1);
                    return;
                }

                // ========== ACTIVATE PROFILE ==========
                // Must happen before resolving the state path: ResolvePath evaluates
                // ContentStateBuildPath against the ACTIVE profile's variables.
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

                // ========== RESOLVE CONTENT STATE PATH ==========
                string contentStatePath;
                if (!string.IsNullOrEmpty(contentStateOverride))
                {
                    contentStatePath = contentStateOverride;
                    Log($"Content state path (from -contentStatePath): {contentStatePath}");
                }
                else
                {
                    var resolved = ContentStateManager.ResolvePath();
                    if (resolved.IsFailure)
                    {
                        LogError($"FAILURE: Could not resolve content state path: {resolved.ErrorMessage}");
                        EditorApplication.Exit(1);
                        return;
                    }

                    contentStatePath = resolved.Value;
                    Log($"Content state path (resolved from profile): {contentStatePath}");
                }

                // ========== FAIL LOUDLY ON A MISSING STATE FILE ==========
                // Phase 1 §1.2 is explicit that a missing state file must not degrade into a
                // silent full build. Checked here rather than letting LoadContentState throw,
                // so the operator gets a remediation hint instead of a stack trace.
                if (!File.Exists(contentStatePath))
                {
                    LogError($"FAILURE: Content state file not found: {contentStatePath}");
                    LogError("  A content update requires the addressables_content_state.bin produced by the");
                    LogError("  matching FULL build. Without it there is no baseline, and Addressables cannot");
                    LogError("  compute a delta. This is NOT recoverable by building a full player content set");
                    LogError("  now — that would produce a new baseline that shipped players cannot patch to.");
                    LogError("  Restore the archived state file for this app version, or ship a new player build.");
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");

                // ========== GATE 1: VERSION MATCH (the invisible-patch blocker) ==========
                Log("Validating content state against the live player version...");
                var validation = ContentStateManager.Validate(contentStatePath);
                if (validation.IsFailure)
                {
                    LogError($"FAILURE: Content state validation refused the build: {validation.ErrorMessage}");
                    LogError($"  Live PlayerSettings.bundleVersion = '{PlayerSettings.bundleVersion}'.");
                    LogError("  Building anyway would write the catalog into the folder named after the LIVE");
                    LogError("  version while naming the file after the STATE version, and Addressables would");
                    LogError("  report success. Players on the old version would 404 forever.");
                    EditorApplication.Exit(1);
                    return;
                }

                Log("✓ Content state matches the current player version");
                Log("");

                // ========== GATE 2: CONTENT UPDATE RESTRICTIONS (task 1.4) ==========
                Log("Checking content-update restrictions...");
                var check = ContentUpdateRestrictions.Check(settings, contentStatePath);

                if (!check.CanEvaluate)
                {
                    // Could not evaluate is a failure, not a pass. An unprovable build is not a safe one.
                    LogError($"FAILURE: Content-update restriction check could not run: {check.Message}");
                    EditorApplication.Exit(1);
                    return;
                }

                if (!check.Passed)
                {
                    LogError($"FAILURE: Content-update restrictions violated: {check.Message}");
                    if (check.Violations != null)
                    {
                        LogError($"  {check.Violations.Count} offending entr{(check.Violations.Count == 1 ? "y" : "ies")}:");
                        foreach (var violation in check.Violations)
                        {
                            LogError($"    {violation.AssetPath}  [group: {violation.GroupName}]" +
                                     $"{(violation.IsExplicitModification ? "  (explicitly modified)" : "  (pulled in as a dependency)")}");
                        }
                    }
                    LogError("");
                    LogError("  These assets live in groups marked StaticContent, which means \"not rebuilt by a");
                    LogError("  content update\". Addressables does NOT fail on this: it logs a warning and reverts");
                    LogError("  each changed entry to point at its PREVIOUS bundle");
                    LogError("  (RevertUnchangedAssetsToPreviousAssetState.cs:183-199). The patch would build, exit 0,");
                    LogError("  upload cleanly — and not contain these changes. That is why this is a hard failure");
                    LogError("  here rather than a warning nobody reads in a CI log.");
                    LogError("");
                    LogError("  A new player build is NOT required. The fix is the \"Prepare for Content Update\"");
                    LogError("  step: move the changed entries into a fresh non-static group via");
                    LogError("  ContentUpdateScript.CreateContentUpdateGroup (ContentUpdateScript.cs:1100), so the");
                    LogError("  update rebuilds them into a new bundle players will actually download.");
                    LogError("  Run it from the Update Preview tab, review which entries move, and commit the group");
                    LogError("  change — it edits AddressableAssetSettings and belongs in a reviewed diff, not in a");
                    LogError("  CI job that restructures your groups on its own.");
                    LogError("");
                    LogError("  Reverting the offending assets is the other valid resolution.");
                    EditorApplication.Exit(1);
                    return;
                }

                Log("✓ No content-update restriction violations");
                Log("");

                // ========== RUN UPDATE BUILD ==========
                Log("Building content update...");
                AddressablesPlayerBuildResult result;
                try
                {
                    result = ContentUpdateScript.BuildContentUpdate(settings, contentStatePath);
                }
                catch (FileNotFoundException ex)
                {
                    // Defence in depth: the File.Exists gate above should make this unreachable,
                    // but LoadContentState opens the stream unguarded, so a file deleted between
                    // the two calls surfaces here rather than as a result.
                    LogError($"FAILURE: Content state file disappeared during the build: {ex.Message}");
                    EditorApplication.Exit(1);
                    return;
                }

                if (result == null)
                {
                    // IsCacheDataValid rejected the state file. There is no Error string to read
                    // on this path — null IS the whole error report.
                    LogError("FAILURE: BuildContentUpdate returned null.");
                    LogError("  Addressables rejected the content state file as invalid (IsCacheDataValid);");
                    LogError("  this path carries no error message. Usual causes: the file was produced by a");
                    LogError("  different Unity version, by a different AddressableAssetSettings, or is corrupt.");
                    LogError($"  State file: {contentStatePath}");
                    EditorApplication.Exit(1);
                    return;
                }

                if (!string.IsNullOrEmpty(result.Error))
                {
                    LogError($"FAILURE: Content update reported error: {result.Error}");
                    EditorApplication.Exit(1);
                    return;
                }

                Log($"✓ Content update completed in {result.Duration:F2} seconds");
                Log($"  Locations (addressable assets): {result.LocationCount}");
                Log("");

                // ========== VERIFY OUTPUT ==========
                Log("Verifying build output...");
                if (!VerifyBuildOutput(result, isUpdateBuild: true))
                {
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");

                // ========== VERIFY THE CATALOG LANDED WHERE PLAYERS WILL LOOK ==========
                // Gate 1 makes a mismatch impossible in theory. This proves it on disk, because
                // the whole point of this method is that the theory failing is undetectable.
                if (!VerifyCatalogMatchesPlayerVersion())
                {
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");

                // ========== CARRY THE INPUT STATE FILE FORWARD ==========
                // An update build does not emit a new state file (see remarks), so the baseline
                // for the NEXT update is still this build's input.
                if (!string.IsNullOrEmpty(result.ContentStateFilePath))
                {
                    LogWarning($"Unexpected: this update build reported a content state file at " +
                               $"{result.ContentStateFilePath}. Addressables 2.9.1 gates that write on " +
                               "PreviousContentState == null, so this suggests the package changed behaviour. " +
                               "Verify which file the next update should build against.");
                }

                Log($"✓ Content state to archive (unchanged, carried forward): {contentStatePath}");
                Log($"  Size: {FormatBytes(new FileInfo(contentStatePath).Length)}");
                Log($"  Last written: {new FileInfo(contentStatePath).LastWriteTime:O}");
                Log("  CI must archive THIS file as the baseline for the next update build.");

                Log("");

                // ========== WRITE BUILD MANIFEST (task 1.7) ==========
                if (!WriteBuildManifest(result, isUpdateBuild: true))
                {
                    EditorApplication.Exit(1);
                    return;
                }

                Log("");
                ReportResolvedPaths(profileName);

                Log("");
                Log($"✓ SUCCESS: Content update complete and verified (elapsed {(DateTime.Now - buildStartTime).TotalSeconds:F1}s)");
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
        /// Runs standalone so CI can verify an artifact it did not build in the same step: unpack
        /// the archived output plus its manifest, point this at them, and get a non-zero exit if
        /// anything a player needs is missing or altered.
        ///
        /// Usage: Unity -batchmode -quit -nographics -projectPath &lt;repo&gt; \
        ///          -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.VerifyOutput \
        ///          -cdnProfile Local [-manifestPath &lt;path&gt;] -logFile verify.log
        /// </remarks>
        public static void VerifyOutput()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "cdnProfile", "Local");

            // -manifest is accepted as well as -manifestPath: infrastructure §7 writes the CI step
            // with -manifest, and a CI job that silently verifies the default path instead of the
            // one it was pointed at would pass while checking the wrong artifact.
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

                string profileId = settings.activeProfileId;
                string bundleDir = settings.profileSettings.EvaluateString(
                    profileId,
                    settings.profileSettings.GetValueByName(profileId, AddressableAssetSettings.kRemoteBuildPath));
                string catalogDir = settings.profileSettings.EvaluateString(
                    profileId,
                    settings.profileSettings.GetValueByName(profileId, CdnProfileManager.RemoteCatalogBuildPathVariable));

                Log($"Bundles:  {bundleDir}");
                Log($"Catalog:  {catalogDir}");
                Log("");

                var verification = CatalogVerifier.Verify(bundleDir, catalogDir, manifestPath);

                foreach (string warning in verification.Warnings)
                {
                    LogWarning($"  {warning}");
                }

                if (!verification.Passed)
                {
                    foreach (string problem in verification.Problems)
                    {
                        LogError($"  {problem}");
                    }

                    LogError("");
                    LogError($"FAILURE: {verification.Problems.Count} problem(s) found. Do NOT upload this output.");
                    EditorApplication.Exit(1);
                    return;
                }

                Log($"✓ {verification.BundlesChecked} bundle(s) verified against the manifest");
                Log($"✓ Catalog and hash file present and unmodified");
                Log($"✓ Settings contract holds");
                if (verification.Warnings.Count > 0)
                {
                    Log($"  ({verification.Warnings.Count} warning(s) above, none blocking)");
                }

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

        /// <summary>
        /// Write build-manifest.json for the build that just finished — task 1.7.
        /// </summary>
        /// <remarks>
        /// Treated as part of the build, not a nice-to-have: CI uses the manifest to decide what to
        /// upload and to verify integrity afterwards, so a build whose manifest is missing or
        /// unparseable must not report success.
        /// </remarks>
        private static bool WriteBuildManifest(AddressablesPlayerBuildResult result, bool isUpdateBuild)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                LogError("FAILURE: Cannot write build manifest (no AddressableAssetSettings found)");
                return false;
            }

            string profileId = settings.activeProfileId;

            string bundleDir = settings.profileSettings.EvaluateString(
                profileId,
                settings.profileSettings.GetValueByName(profileId, AddressableAssetSettings.kRemoteBuildPath));

            string catalogDir = settings.profileSettings.EvaluateString(
                profileId,
                settings.profileSettings.GetValueByName(profileId, CdnProfileManager.RemoteCatalogBuildPathVariable));

            Log("Writing build manifest...");
            var written = BuildManifestWriter.Write(result, bundleDir, catalogDir, isUpdateBuild);
            if (written.IsFailure)
            {
                LogError($"FAILURE: Could not write build manifest: {written.ErrorMessage}");
                return false;
            }

            var manifestFile = new FileInfo(written.Value);
            Log($"✓ Build manifest: {written.Value}");
            Log($"  Size: {FormatBytes(manifestFile.Length)}");
            Log("  Not under the per-platform folders, so a CI sync of ServerData/<platform>/ will");
            Log("  not publish it. Archive it privately as the build's audit trail.");
            return true;
        }

        /// <summary>
        /// Assert that the catalog produced by this build is named for the version players
        /// are actually polling for, and that it sits in the folder named for that same version.
        /// </summary>
        /// <remarks>
        /// This is the on-disk proof for the split-version failure described on
        /// BuildContentUpdate. A build that trips it has already reported success.
        /// </remarks>
        private static bool VerifyCatalogMatchesPlayerVersion()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                LogError("FAILURE: Cannot verify catalog version (no AddressableAssetSettings found)");
                return false;
            }

            string expectedVersion = PlayerSettings.bundleVersion;
            Log($"Verifying the catalog is addressed to player version '{expectedVersion}'...");

            string catalogBuildPathTemplate = settings.profileSettings.GetValueByName(
                settings.activeProfileId,
                CdnProfileManager.RemoteCatalogBuildPathVariable);
            string catalogDir = settings.profileSettings.EvaluateString(settings.activeProfileId, catalogBuildPathTemplate);

            if (string.IsNullOrEmpty(catalogDir) || !Directory.Exists(catalogDir))
            {
                LogError($"FAILURE: Catalog directory does not exist after build: {catalogDir}");
                return false;
            }

            string[] catalogFiles = Directory.GetFiles(catalogDir, "catalog_*.bin")
                .Concat(Directory.GetFiles(catalogDir, "catalog_*.json"))
                .Select(Path.GetFileName)
                .ToArray();

            if (catalogFiles.Length == 0)
            {
                LogError($"FAILURE: No catalog file in {catalogDir}");
                return false;
            }

            string expectedBin = $"catalog_{expectedVersion}.bin";
            string expectedJson = $"catalog_{expectedVersion}.json";

            if (!catalogFiles.Any(f => f == expectedBin || f == expectedJson))
            {
                LogError($"FAILURE: The catalog in {catalogDir} is not addressed to player version '{expectedVersion}'.");
                LogError($"  Expected: {expectedBin} (or .json)");
                LogError($"  Found:    {string.Join(", ", catalogFiles)}");
                LogError("  The catalog filename comes from the content state file's playerVersion while the");
                LogError("  folder comes from the live profile. They have diverged, so shipped players polling");
                LogError("  their own version's path will 404. Do NOT upload this build.");
                return false;
            }

            Log($"✓ Catalog {expectedBin.Replace(".bin", "")} is in the folder players on '{expectedVersion}' will poll");
            return true;
        }

        private static bool VerifyBuildOutput(AddressablesPlayerBuildResult result, bool isUpdateBuild = false)
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

            // An update build never emits a state file (BuildScriptPackedMode.cs:575 gates that
            // write on PreviousContentState == null), so checking for one here would always
            // warn. BuildContentUpdate carries the input file forward and reports it instead.
            if (isUpdateBuild)
            {
                return true;
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
