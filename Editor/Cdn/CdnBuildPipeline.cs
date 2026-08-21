using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>Severity of a line emitted while a build runs.</summary>
    public enum BuildLogLevel
    {
        /// <summary>Progress.</summary>
        Info,

        /// <summary>Something worth knowing that does not stop the build.</summary>
        Warning,

        /// <summary>The reason the build stopped, or detail explaining it.</summary>
        Error
    }

    /// <summary>Receives progress while a build runs. Batchmode writes to the log; the GUI appends to a view.</summary>
    public delegate void BuildLogSink(string message, BuildLogLevel level);

    /// <summary>What a completed build produced.</summary>
    public sealed class BuildOutcome
    {
        /// <summary>Addressables' own result object.</summary>
        public AddressablesPlayerBuildResult Result;

        /// <summary>True for a content update, false for a full build.</summary>
        public bool IsUpdateBuild;

        /// <summary>
        /// The content state file that must be archived as the baseline for the next update.
        /// For an update build this is the file the build CONSUMED, not one it produced — an update
        /// never writes a new state file.
        /// </summary>
        public string ContentStatePath;

        /// <summary>Where build-manifest.json was written.</summary>
        public string ManifestPath;

        /// <summary>Resolved bundle output directory.</summary>
        public string BundleDir;

        /// <summary>Resolved catalog output directory.</summary>
        public string CatalogDir;

        /// <summary>Patch cost against the previous build, or null when there was nothing to compare with.</summary>
        public PatchInfo Patch;
    }

    /// <summary>
    /// The gated build sequence, shared by the batchmode CLI and the Build tab.
    /// </summary>
    /// <remarks>
    /// This exists so the order of the gates is written down once. The order is the design — every
    /// check runs BEFORE the build, because the failure the version gate prevents cannot be detected
    /// afterwards (see BuildContentUpdate below). A second copy of that sequence in the GUI would
    /// drift from this one, and the drift would show up as a patch that built fine from a button and
    /// was refused by CI, or worse, the reverse.
    ///
    /// Nothing here calls EditorApplication.Exit or writes to the Unity console directly. Batchmode
    /// exit codes belong to CdnBuildCLI; the GUI needs neither.
    /// </remarks>
    public static class CdnBuildPipeline
    {
        /// <summary>
        /// Full content build: a new baseline, to ship alongside a player build.
        /// </summary>
        public static CdnEditorResult<BuildOutcome> BuildFull(string profileName, BuildLogSink log = null)
        {
            log = log ?? ((m, l) => { });

            var prepared = Prepare(profileName, log);
            if (prepared.IsFailure)
                return CdnEditorResult<BuildOutcome>.Failure(prepared.ErrorMessage);

            var settings = prepared.Value;

            // Captured before the build so ArchiveAfterBuild can tell a state file written by THIS
            // build from one left behind by a previous run.
            var buildStartTime = DateTime.Now;

            log("Building player content...", BuildLogLevel.Info);
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (result == null)
                return CdnEditorResult<BuildOutcome>.Failure("BuildPlayerContent returned a null result");

            if (!string.IsNullOrEmpty(result.Error))
                return CdnEditorResult<BuildOutcome>.Failure($"Build reported error: {result.Error}");

            log($"Build completed in {result.Duration:F2}s, {result.LocationCount} location(s)", BuildLogLevel.Info);

            var outcome = new BuildOutcome
            {
                Result = result,
                IsUpdateBuild = false,
                BundleDir = ResolveBundleDir(settings),
                CatalogDir = ResolveCatalogDir(settings)
            };

            // A full build DOES emit a state file. A null path here does not mean "not applicable";
            // it means CopyAndRegisterContentState threw and the exception was swallowed
            // (BuildScriptBase.cs:376-389) while the build still reported success. Losing it ends
            // delta updates for this app version, so it is a failure, not a warning.
            var archived = ContentStateManager.ArchiveAfterBuild(result, buildStartTime);
            if (archived.IsFailure)
                return CdnEditorResult<BuildOutcome>.Failure(
                    $"Build succeeded but produced no usable content state: {archived.ErrorMessage}");

            outcome.ContentStatePath = archived.Value;
            log($"Content state: {outcome.ContentStatePath}", BuildLogLevel.Info);

            var finished = Finish(outcome, log);
            return finished.IsFailure
                ? CdnEditorResult<BuildOutcome>.Failure(finished.ErrorMessage)
                : CdnEditorResult<BuildOutcome>.Success(outcome);
        }

        /// <summary>
        /// Delta content build against a previous build's content state.
        /// </summary>
        /// <remarks>
        /// THE FAILURE THE GATES PREVENT
        /// Addressables takes the catalog FILENAME from the state file's playerVersion
        /// (ContentUpdateScript.cs:659 -> BuildScriptPackedMode.cs:713) and the catalog FOLDER from
        /// the live profile (:714), and never compares them. Bump bundleVersion to 0.2.0, update
        /// against a 0.1.0 state file, and the output is catalog/0.2.0/catalog_0.1.0.bin with an
        /// empty Error and a success exit. Players on 0.1.0 poll catalog/0.1.0/... and get a
        /// permanent 404. Nothing after the build can detect this, which is why Validate runs first.
        ///
        /// THREE DISSIMILAR FAILURE SHAPES
        ///   missing state file -> LoadContentState THROWS FileNotFoundException; it opens a
        ///                         FileStream with no existence check (ContentUpdateScript.cs:616)
        ///   invalid state file -> BuildContentUpdate RETURNS NULL with no Error to read (:655-656)
        ///   wrong folder       -> nothing reports it at all
        /// </remarks>
        public static CdnEditorResult<BuildOutcome> BuildUpdate(
            string profileName,
            string contentStatePathOverride = null,
            BuildLogSink log = null)
        {
            log = log ?? ((m, l) => { });

            var prepared = Prepare(profileName, log);
            if (prepared.IsFailure)
                return CdnEditorResult<BuildOutcome>.Failure(prepared.ErrorMessage);

            var settings = prepared.Value;

            // ---- resolve the baseline ----
            string contentStatePath;
            if (!string.IsNullOrEmpty(contentStatePathOverride))
            {
                contentStatePath = contentStatePathOverride;
            }
            else
            {
                var resolved = ContentStateManager.ResolvePath();
                if (resolved.IsFailure)
                    return CdnEditorResult<BuildOutcome>.Failure($"Could not resolve the content state path: {resolved.ErrorMessage}");

                contentStatePath = resolved.Value;
            }

            log($"Content state: {contentStatePath}", BuildLogLevel.Info);

            if (!File.Exists(contentStatePath))
            {
                log("A content update needs the addressables_content_state.bin from the matching FULL build.",
                    BuildLogLevel.Error);
                log("Building a full set now would create a baseline shipped players cannot patch to.",
                    BuildLogLevel.Error);
                return CdnEditorResult<BuildOutcome>.Failure($"Content state file not found: {contentStatePath}");
            }

            // ---- gate 1: version match ----
            log("Validating the content state against the live player version...", BuildLogLevel.Info);
            var validation = ContentStateManager.Validate(contentStatePath);
            if (validation.IsFailure)
            {
                log($"Live PlayerSettings.bundleVersion is '{PlayerSettings.bundleVersion}'.", BuildLogLevel.Error);
                log("Building anyway writes the catalog into the folder named for the LIVE version while naming " +
                    "the file after the STATE version. Addressables reports success; players on the old version " +
                    "404 forever.", BuildLogLevel.Error);
                return CdnEditorResult<BuildOutcome>.Failure(validation.ErrorMessage);
            }

            log("Content state matches the current player version", BuildLogLevel.Info);

            // ---- gate 2: content update restrictions ----
            log("Checking content-update restrictions...", BuildLogLevel.Info);
            var check = ContentUpdateRestrictions.Check(settings, contentStatePath);

            if (!check.CanEvaluate)
            {
                // Unprovable is not the same as safe.
                return CdnEditorResult<BuildOutcome>.Failure(
                    $"The content-update restriction check could not run: {check.Message}");
            }

            if (!check.Passed)
            {
                if (check.Violations != null)
                {
                    foreach (var violation in check.Violations)
                    {
                        log($"  {violation.AssetPath}  [group: {violation.GroupName}]" +
                            $"{(violation.IsExplicitModification ? "  (explicitly modified)" : "  (pulled in as a dependency)")}",
                            BuildLogLevel.Error);
                    }
                }

                log("These live in StaticContent groups. Addressables does not fail on this: it logs a warning and " +
                    "reverts each entry to its PREVIOUS bundle (RevertUnchangedAssetsToPreviousAssetState.cs:183-199), " +
                    "so the patch would build, upload, and not contain the changes.", BuildLogLevel.Error);
                log("A new player build is NOT required. Use the Update Preview tab's Prepare button to move the " +
                    "changed entries into a fresh non-static group, or revert them.", BuildLogLevel.Error);

                return CdnEditorResult<BuildOutcome>.Failure($"Content-update restrictions violated: {check.Message}");
            }

            log("No content-update restriction violations", BuildLogLevel.Info);

            // ---- build ----
            log("Building content update...", BuildLogLevel.Info);
            AddressablesPlayerBuildResult result;
            try
            {
                result = ContentUpdateScript.BuildContentUpdate(settings, contentStatePath);
            }
            catch (FileNotFoundException ex)
            {
                // The File.Exists gate should make this unreachable; LoadContentState opens the
                // stream unguarded, so a file deleted in between surfaces here instead of as a result.
                return CdnEditorResult<BuildOutcome>.Failure($"Content state file disappeared during the build: {ex.Message}");
            }

            if (result == null)
            {
                return CdnEditorResult<BuildOutcome>.Failure(
                    "BuildContentUpdate returned null: Addressables rejected the content state file as invalid " +
                    "(IsCacheDataValid). This path carries no error message. Usual causes are a state file from a " +
                    $"different Unity version or a different AddressableAssetSettings, or a corrupt file: {contentStatePath}");
            }

            if (!string.IsNullOrEmpty(result.Error))
                return CdnEditorResult<BuildOutcome>.Failure($"Content update reported error: {result.Error}");

            log($"Content update completed in {result.Duration:F2}s, {result.LocationCount} location(s)", BuildLogLevel.Info);

            var outcome = new BuildOutcome
            {
                Result = result,
                IsUpdateBuild = true,
                // An update never emits a new state file: the write is gated on
                // PreviousContentState == null (BuildScriptPackedMode.cs:575) and BuildContentUpdate
                // always sets it. The baseline for the NEXT update is still this build's input.
                ContentStatePath = contentStatePath,
                BundleDir = ResolveBundleDir(settings),
                CatalogDir = ResolveCatalogDir(settings)
            };

            if (!string.IsNullOrEmpty(result.ContentStateFilePath))
            {
                log($"Unexpected: this update build reported a content state file at {result.ContentStateFilePath}. " +
                    "Addressables 2.9.1 gates that write on PreviousContentState == null, so the package may have " +
                    "changed behaviour. Check which file the next update should build against.", BuildLogLevel.Warning);
            }

            var finished = Finish(outcome, log);
            return finished.IsFailure
                ? CdnEditorResult<BuildOutcome>.Failure(finished.ErrorMessage)
                : CdnEditorResult<BuildOutcome>.Success(outcome);
        }

        // ========== shared steps ==========

        /// <summary>Compile gate, settings lookup, profile activation. Everything both builds need first.</summary>
        private static CdnEditorResult<AddressableAssetSettings> Prepare(string profileName, BuildLogSink log)
        {
            // Unity exits 0 when -executeMethod runs against a broken assembly and the method never
            // runs, so every entry point in this module checks explicitly.
            if (EditorUtility.scriptCompilationFailed)
                return CdnEditorResult<AddressableAssetSettings>.Failure("Script compilation failed; refusing to build");

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return CdnEditorResult<AddressableAssetSettings>.Failure("No AddressableAssetSettings found. Run CdnSetupCLI first.");

            log($"Activating profile '{profileName}'...", BuildLogLevel.Info);
            try
            {
                CdnProfileManager.SetActiveProfile(profileName);
            }
            catch (Exception ex)
            {
                return CdnEditorResult<AddressableAssetSettings>.Failure($"Could not activate profile '{profileName}': {ex.Message}");
            }

            // A local-only build publishes nothing, so a placeholder host cannot reach a player and
            // there is no reason to refuse the build over it.
            var placeholders = CdnBuildModes.IsLocalOnly
                ? new List<string>()
                : UnresolvedProfilePlaceholders(settings);
            if (placeholders.Count > 0)
            {
                return CdnEditorResult<AddressableAssetSettings>.Failure(
                    $"Profile '{profileName}' still contains unresolved host placeholders, and they would " +
                    $"be baked into the catalog:\n  {string.Join("\n  ", placeholders)}\n\n" +
                    "The Dev/Staging/Prod templates this package generates carry a literal \"<domain>\" " +
                    "because the real host is environment-specific - their own doc comment calls the value " +
                    "a fallback that env-var injection is meant to replace. Nothing injected it, so this " +
                    "build would have succeeded, passed verification, and shipped a player that fetches " +
                    "every bundle from a host that does not exist.\n\n" +
                    "Fix: set the profile's Remote paths to the real host (Addressables > Profiles), or " +
                    "call CdnProfileManager.InjectRemoteHostFromEnvironment with CDN_HOST set before " +
                    "building.");
            }

            return CdnEditorResult<AddressableAssetSettings>.Success(settings);
        }

        /// <summary>
        /// Profile values that still carry a <c>&lt;placeholder&gt;</c> where a real host belongs.
        /// </summary>
        /// <remarks>
        /// Refusing here is the difference between a loud failure and a silent one. The generated
        /// Dev/Staging/Prod profiles ship <c>https://cdn.&lt;domain&gt;/game/...</c>; that string is
        /// what gets baked into the catalog at build time, and nothing downstream objects to it -
        /// the build reports success, the verifier passes (it only compares output against the manifest
        /// the same build wrote), and the failure surfaces much later as content that will not load.
        ///
        /// Deliberately NOT auto-injecting from the environment here: which host a build should point
        /// at is a release decision, and a build that quietly rewrote its own target would be a worse
        /// version of the same problem. The package's job is to refuse to guess.
        /// </remarks>
        private static List<string> UnresolvedProfilePlaceholders(AddressableAssetSettings settings)
        {
            var found = new List<string>();

            foreach (string variable in new[]
                     {
                         AddressableAssetSettings.kRemoteLoadPath,
                         AddressableAssetSettings.kRemoteBuildPath,
                         SettingsContract.RemoteCatalogLoadPathVariable,
                         SettingsContract.RemoteCatalogBuildPathVariable
                     })
            {
                string raw = settings.profileSettings.GetValueByName(settings.activeProfileId, variable);
                if (string.IsNullOrEmpty(raw)) continue;

                // A '<' that is not part of an Addressables profile token: those use [square] brackets
                // and {curly} braces, so an angle bracket left in a path is always a placeholder.
                if (raw.IndexOf('<') >= 0 && raw.IndexOf('>') > raw.IndexOf('<'))
                    found.Add($"{variable} = {raw}");
            }

            return found;
        }

        /// <summary>Manifest, then verification. Both builds end the same way.</summary>
        private static CdnEditorResult<bool> Finish(BuildOutcome outcome, BuildLogSink log)
        {
            // Nothing was written to the remote output directory, so writing a manifest of it and then
            // verifying the directory against that manifest would compare two empty things and call it
            // proof. Say what was skipped instead of reporting a verification that did not happen.
            if (CdnBuildModes.IsLocalOnly)
            {
                log("Local-only build: content ships inside the player, so there is no remote output " +
                    "to manifest or verify. Set CdnSettings > Build Mode to Remote to publish to a CDN.",
                    BuildLogLevel.Info);
                return CdnEditorResult<bool>.Success(true);
            }

            log("Writing build manifest...", BuildLogLevel.Info);
            var written = BuildManifestWriter.Write(
                outcome.Result, outcome.BundleDir, outcome.CatalogDir, outcome.IsUpdateBuild);

            if (written.IsFailure)
                return CdnEditorResult<bool>.Failure($"Could not write the build manifest: {written.ErrorMessage}");

            outcome.ManifestPath = written.Value;

            var reloaded = ContentDiff.Load(outcome.ManifestPath);
            if (reloaded.IsSuccess)
                outcome.Patch = reloaded.Value.patch;

            log($"Build manifest: {outcome.ManifestPath}", BuildLogLevel.Info);

            // Verify before anything is published. This is the same check CI runs, so a build that
            // passes here cannot fail verification in the pipeline for a reason that existed locally.
            log("Verifying output...", BuildLogLevel.Info);
            var verification = CatalogVerifier.Verify(outcome.BundleDir, outcome.CatalogDir, outcome.ManifestPath);

            foreach (string warning in verification.Warnings)
                log(warning, BuildLogLevel.Warning);

            if (!verification.Passed)
            {
                foreach (string problem in verification.Problems)
                    log(problem, BuildLogLevel.Error);

                return CdnEditorResult<bool>.Failure(
                    $"Output verification found {verification.Problems.Count} problem(s); do not publish this build");
            }

            log($"{verification.BundlesChecked} bundle(s) verified against the manifest", BuildLogLevel.Info);
            return CdnEditorResult<bool>.Success(true);
        }

        internal static string ResolveBundleDir(AddressableAssetSettings settings)
        {
            return settings.profileSettings.EvaluateString(
                settings.activeProfileId,
                settings.profileSettings.GetValueByName(settings.activeProfileId, AddressableAssetSettings.kRemoteBuildPath));
        }

        internal static string ResolveCatalogDir(AddressableAssetSettings settings)
        {
            return settings.profileSettings.EvaluateString(
                settings.activeProfileId,
                settings.profileSettings.GetValueByName(settings.activeProfileId, CdnProfileManager.RemoteCatalogBuildPathVariable));
        }
    }
}
