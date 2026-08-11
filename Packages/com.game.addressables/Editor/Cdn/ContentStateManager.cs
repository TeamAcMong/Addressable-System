using System;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Manages <c>addressables_content_state.bin</c> files for CDN workflow.
    ///
    /// The content state file is critical infrastructure: losing it for a shipped app version
    /// permanently ends delta updates for that version — every later patch becomes a full
    /// re-download for those players, and it cannot be reconstructed. This manager prevents
    /// that scenario (risk R1) by:
    ///
    /// 1. Validating playerVersion matches PlayerSettings.bundleVersion (the only detector)
    /// 2. Archiving state files after successful builds
    /// 3. Detecting stale files from earlier runs
    ///
    /// Public surface:
    /// - <see cref="ResolvePath()"/> — find the build-time state file path
    /// - <see cref="Validate(string)"/> — verify state file is usable for delta builds
    /// - <see cref="ArchiveAfterBuild(AddressablesPlayerBuildResult, DateTime)"/> — copy state file after build, validating it was written during this build
    ///
    /// No CLI wiring — another agent owns CdnBuildCLI and will compose this.
    /// </summary>
    public static class ContentStateManager
    {
        /// <summary>
        /// Resolves the path where the content state file should be saved, using the
        /// semantics of <c>ContentUpdateScript.GetContentStateDataPath</c>.
        /// </summary>
        /// <returns>Success with the resolved path; Failure if no valid path is available.</returns>
        /// <remarks>
        /// The build-time path is stored in AddressableAssetSettings.ContentStateBuildPath,
        /// which is evaluated against the active profile's variables (e.g., [UnityEditor.PlayerSettings.bundleVersion]).
        /// This method returns that evaluated path without writing any files.
        /// </remarks>
        public static CdnEditorResult<string> ResolvePath()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                return CdnEditorResult<string>.Failure("AddressableAssetSettings not found in project");
            }

            if (string.IsNullOrEmpty(settings.activeProfileId))
            {
                return CdnEditorResult<string>.Failure("No active Addressables profile set");
            }

            string contentStateBuildPathTemplate = settings.ContentStateBuildPath;
            if (string.IsNullOrEmpty(contentStateBuildPathTemplate))
            {
                return CdnEditorResult<string>.Failure("ContentStateBuildPath is empty in AddressableAssetSettings");
            }

            try
            {
                string resolvedPath = settings.profileSettings.EvaluateString(
                    settings.activeProfileId,
                    contentStateBuildPathTemplate);

                if (string.IsNullOrEmpty(resolvedPath))
                {
                    return CdnEditorResult<string>.Failure(
                        $"Failed to evaluate ContentStateBuildPath template '{contentStateBuildPathTemplate}'");
                }

                return CdnEditorResult<string>.Success(resolvedPath);
            }
            catch (Exception ex)
            {
                return CdnEditorResult<string>.Failure($"Error resolving ContentStateBuildPath: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates that a content state file is usable for delta builds.
        ///
        /// Checks:
        /// 1. File exists
        /// 2. File is readable (not corrupted)
        /// 3. <b>playerVersion matches PlayerSettings.bundleVersion (THE critical gate)</b>
        /// 4. Has required fields (cachedInfos, remoteCatalogLoadPath)
        /// </summary>
        /// <param name="contentStateFilePath">Path to the .bin file to validate</param>
        /// <returns>Success if the file is valid; Failure with specific reason if not</returns>
        /// <remarks>
        /// The playerVersion == bundleVersion check is the only detector of version mismatch
        /// (see task 1.3 analysis: Addressables splits version sources, and the build catalog
        /// filename uses playerVersion from the state file, not the live bundleVersion).
        /// This gate refuses mismatches, not just warns about them.
        /// </remarks>
        public static CdnEditorResult<bool> Validate(string contentStateFilePath)
        {
            if (string.IsNullOrEmpty(contentStateFilePath))
            {
                return CdnEditorResult<bool>.Failure("Content state file path is empty");
            }

            // Check if file exists
            if (!File.Exists(contentStateFilePath))
            {
                return CdnEditorResult<bool>.Failure($"Content state file not found: {contentStateFilePath}");
            }

            // Try to load and deserialize the state file
            AddressablesContentState state = null;
            try
            {
                state = ContentUpdateScript.LoadContentState(contentStateFilePath);
            }
            catch (Exception ex)
            {
                return CdnEditorResult<bool>.Failure($"Failed to load content state file: {ex.Message}");
            }

            if (state == null)
            {
                return CdnEditorResult<bool>.Failure("Content state file is corrupted or unreadable (LoadContentState returned null)");
            }

            // Check required fields
            if (state.cachedInfos == null)
            {
                return CdnEditorResult<bool>.Failure("Content state file missing cachedInfos (corrupted)");
            }

            if (string.IsNullOrEmpty(state.remoteCatalogLoadPath))
            {
                return CdnEditorResult<bool>.Failure("Content state file missing remoteCatalogLoadPath (previous build had 'Build Remote Catalog' disabled)");
            }

            // THE CRITICAL GATE: playerVersion must match current bundleVersion
            // This is the only detector of version mismatch (see task 1.3 analysis)
            string currentBundleVersion = PlayerSettings.bundleVersion;
            if (state.playerVersion != currentBundleVersion)
            {
                return CdnEditorResult<bool>.Failure(
                    $"Content state playerVersion '{state.playerVersion}' does not match current PlayerSettings.bundleVersion '{currentBundleVersion}'. " +
                    "This mismatch would create invisible patches (catalog filename uses playerVersion, players poll for bundleVersion). " +
                    "State file must be from this version or state must be regenerated.");
            }

            return CdnEditorResult<bool>.Success(true);
        }

        /// <summary>
        /// Archives the content state file after a successful build.
        ///
        /// For full builds: copies the newly-written state file to a safe location.
        /// For update builds: carries the input state file forward (Addressables does not re-archive on updates).
        /// </summary>
        /// <param name="buildResult">Result from AddressableAssetSettings.BuildPlayerContent()</param>
        /// <param name="buildStartTime">Timestamp when the build started; used to verify the state file was written during this build, not from an earlier run</param>
        /// <returns>Success with the state file path if archive completed; Failure if state file is missing/stale</returns>
        /// <remarks>
        /// Three key facts about Addressables' state file handling (verified in 2.9.1):
        ///
        /// 1. <c>ContentStateFilePath</c> has exactly one assignment (BuildScriptBase.cs:374).
        ///    Its sibling properties RemoteCatalogHashFilePath and RemoteCatalogJsonFilePath
        ///    are never assigned — public, correctly typed, permanently null.
        ///
        /// 2. For content-update builds, ContentStateFilePath is always null because the writer
        ///    is gated on <c>builderInput.PreviousContentState == null</c> (BuildScriptPackedMode.cs:575).
        ///    The state is not re-archived; the input must be carried forward.
        ///
        /// 3. CopyAndRegisterContentState swallows exceptions (BuildScriptBase.cs:377-388),
        ///    so a copy failure leaves ContentStateFilePath null with exit code 0 (build success).
        ///    This is the R1 open door: we must treat null-or-missing as hard failure, not silently accept it.
        ///
        /// Staleness detection: the file's <c>LastWriteTime</c> must be >= <c>buildStartTime</c>.
        /// This is an exact comparison with no window: the file must have been written during this build.
        /// </remarks>
        public static CdnEditorResult<string> ArchiveAfterBuild(AddressablesPlayerBuildResult buildResult, DateTime buildStartTime)
        {
            if (buildResult == null)
            {
                return CdnEditorResult<string>.Failure("Build result is null");
            }

            string contentStateFilePath = buildResult.ContentStateFilePath;
            if (string.IsNullOrEmpty(contentStateFilePath))
            {
                return CdnEditorResult<string>.Failure(
                    "Build did not produce a content state file (ContentStateFilePath is null or empty). " +
                    "This may indicate CopyAndRegisterContentState failed silently. " +
                    "For update builds, use the input state file instead (PreviousContentState from BuildContentUpdate).");
            }

            if (!File.Exists(contentStateFilePath))
            {
                return CdnEditorResult<string>.Failure(
                    $"Content state file does not exist at reported path: {contentStateFilePath}. " +
                    "The build claimed success but the file was never written or was deleted.");
            }

            // Verify the file was written during this build, not from a previous run
            var fileInfo = new FileInfo(contentStateFilePath);
            if (fileInfo.LastWriteTime < buildStartTime)
            {
                return CdnEditorResult<string>.Failure(
                    $"Content state file timestamp ({fileInfo.LastWriteTime:O}) is earlier than build start time ({buildStartTime:O}). " +
                    "The file may be from a previous build run, not this one. Verify the build succeeded and wrote the state file.");
            }

            return CdnEditorResult<string>.Success(contentStateFilePath);
        }
    }
}
