using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Encapsulates the result of a content-update restriction check.
    /// Distinguishes three states: clean pass, violations found, or check could not evaluate.
    /// </summary>
    public class ContentUpdateCheckResult
    {
        /// <summary>
        /// True if the check passed (no violations found and was evaluated against valid cache).
        /// False if violations were found, or if the check could not be evaluated.
        /// </summary>
        public bool Passed { get; internal set; }

        /// <summary>
        /// True if the check could be evaluated against a valid cache state.
        /// False if cache was missing, invalid, or configuration was incomplete.
        /// When false, Violations is null and ErrorMessage explains why.
        /// </summary>
        public bool CanEvaluate { get; internal set; }

        /// <summary>
        /// If Passed is false and CanEvaluate is true, contains details about violations.
        /// If CanEvaluate is false, this is null.
        /// If Passed is true, this is empty.
        /// </summary>
        public List<ContentUpdateViolation> Violations { get; internal set; }

        /// <summary>
        /// Human-readable message describing the outcome.
        /// If CanEvaluate is false, explains why the check could not run.
        /// If violations exist, contains formatted list and resolution steps.
        /// </summary>
        public string Message { get; internal set; }

        public ContentUpdateCheckResult()
        {
            Passed = true;
            CanEvaluate = true;
            Violations = new List<ContentUpdateViolation>();
            Message = string.Empty;
        }
    }

    /// <summary>
    /// Represents a single entry that violates content-update restrictions.
    /// </summary>
    public struct ContentUpdateViolation
    {
        /// <summary>
        /// Full file path of the asset (e.g., "Assets/MyAsset.prefab").
        /// </summary>
        public string AssetPath { get; internal set; }

        /// <summary>
        /// Name of the group containing this asset.
        /// </summary>
        public string GroupName { get; internal set; }

        /// <summary>
        /// True if this entry was explicitly modified in the project.
        /// False if it is a dependency of an explicitly modified entry.
        /// </summary>
        public bool IsExplicitModification { get; internal set; }
    }

    /// <summary>
    /// Validates that content marked as "static" (Cannot Change Post Release) has not been modified
    /// during a content update. Violations indicate assets that were changed but will not be delivered
    /// to existing players because content updates cannot replace static content.
    ///
    /// Implements Risk R5 guard: a build-time check that prevents shipping broken content updates.
    /// </summary>
    public static class ContentUpdateRestrictions
    {
        /// <summary>
        /// Check whether the project violates content-update restrictions.
        /// </summary>
        /// <param name="settings">The AddressableAssetSettings to check.</param>
        /// <param name="contentStatePath">Full path to the addressables_content_state.bin file from a previous build.</param>
        /// <returns>
        /// A result object with three distinct outcomes:
        /// - CanEvaluate true, Passed true: no violations, check clean
        /// - CanEvaluate true, Passed false: violations found in result.Violations
        /// - CanEvaluate false: check could not run (config issue); Violations is null, Message explains why
        /// </returns>
        public static ContentUpdateCheckResult Check(
            AddressableAssetSettings settings,
            string contentStatePath)
        {
            var result = new ContentUpdateCheckResult();

            // Input validation
            if (settings == null)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message = "AddressableAssetSettings is null.";
                return result;
            }

            if (string.IsNullOrEmpty(contentStatePath))
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message = "Content state path is null or empty.";
                return result;
            }

            // Try to load the content state
            AddressablesContentState contentState = null;
            try
            {
                contentState = ContentUpdateScript.LoadContentState(contentStatePath);
            }
            catch (Exception ex)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message = $"Failed to load content state from '{contentStatePath}': {ex.Message}";
                return result;
            }

            if (contentState == null)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message = $"Content state file at '{contentStatePath}' is invalid or could not be deserialized.";
                return result;
            }

            // See EvaluateStaticContentConfiguration for why "no static groups" is not one answer.
            var staticGroups = GetStaticGroups(settings);
            if (staticGroups.Count == 0)
            {
                EvaluateStaticContentConfiguration(settings, result);
                return result;
            }

            // Check if we have any cached info to compare against
            if (contentState.cachedInfos == null || contentState.cachedInfos.Length == 0)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message =
                    "Content state file contains no cached asset information. " +
                    "This indicates either an empty previous build or a corrupted state file. " +
                    "Cannot evaluate restrictions without a baseline to compare against.";
                return result;
            }

            // Gather modified entries with their dependencies
            var modifiedEntriesMap = ContentUpdateScript.GatherModifiedEntriesWithDependencies(settings, contentStatePath);

            // If no modifications were detected, check is clean
            if (modifiedEntriesMap.Count == 0)
            {
                result.CanEvaluate = true;
                result.Passed = true;
                result.Message = "No modifications detected. Content update is safe.";
                return result;
            }

            // Build violations list
            var violations = new List<ContentUpdateViolation>();

            // Explicit modifications
            foreach (var entry in modifiedEntriesMap.Keys)
            {
                violations.Add(new ContentUpdateViolation
                {
                    AssetPath = entry.AssetPath,
                    GroupName = entry.parentGroup?.Name ?? "unknown",
                    IsExplicitModification = true
                });
            }

            // Dependencies of modified entries
            foreach (var kvp in modifiedEntriesMap)
            {
                foreach (var dependency in kvp.Value)
                {
                    violations.Add(new ContentUpdateViolation
                    {
                        AssetPath = dependency.AssetPath,
                        GroupName = dependency.parentGroup?.Name ?? "unknown",
                        IsExplicitModification = false
                    });
                }
            }

            if (violations.Count > 0)
            {
                result.CanEvaluate = true;
                result.Passed = false;
                result.Violations = violations;
                result.Message = BuildViolationMessage(violations);
            }
            else
            {
                result.CanEvaluate = true;
                result.Passed = true;
                result.Message = "No modifications detected. Content update is safe.";
            }

            return result;
        }

        /// <summary>
        /// Builds a human-readable message describing the violations.
        /// </summary>
        private static string BuildViolationMessage(List<ContentUpdateViolation> violations)
        {
            if (violations == null || violations.Count == 0)
                return string.Empty;

            var explicitCount = violations.Count(v => v.IsExplicitModification);
            var dependencyCount = violations.Count - explicitCount;

            var message = new System.Text.StringBuilder();
            message.AppendLine("Content Update Restriction Violation");
            message.AppendLine();
            message.AppendLine(
                $"{explicitCount} asset(s) marked as static have been modified. " +
                $"Content updates cannot replace these assets, so changes will not reach existing players.");
            message.AppendLine();

            if (explicitCount > 0)
            {
                message.AppendLine("Explicitly modified assets:");
                foreach (var v in violations.Where(v => v.IsExplicitModification))
                {
                    message.AppendLine($"  - {v.AssetPath} (group: {v.GroupName})");
                }
            }

            if (dependencyCount > 0)
            {
                message.AppendLine();
                message.AppendLine($"{dependencyCount} dependency(ies) pulled in by modifications:");
                foreach (var v in violations.Where(v => !v.IsExplicitModification))
                {
                    message.AppendLine($"  - {v.AssetPath} (group: {v.GroupName})");
                }
            }

            message.AppendLine();
            message.AppendLine("Resolution:");
            message.AppendLine("  1. Revert the changes to these assets, or");
            message.AppendLine("  2. Move the assets to a non-static group (one without 'Cannot Change Post Release' enabled), or");
            message.AppendLine("  3. If this is a new build baseline, update the content_state.bin file.");

            return message.ToString();
        }

        /// <summary>
        /// Decides what "no group is marked Cannot Change Post Release" means for this project, and
        /// fills <paramref name="result"/> accordingly.
        /// </summary>
        /// <remarks>
        /// It is two different situations that need opposite answers, and this used to report both as
        /// "configuration failure, check could not run":
        ///
        /// <list type="bullet">
        /// <item><b>Something ships inside the player and was not declared.</b> A group whose BuildPath
        /// resolves to a Local path is baked into the build and can never be replaced by a content
        /// update, so it must be marked Cannot Change Post Release. Leaving it unmarked is the real
        /// misconfiguration this whole check exists to catch, and the check genuinely cannot run.</item>
        /// <item><b>Nothing is immutable.</b> Every group builds remote, so there is no content a
        /// content update could break. The restriction check has nothing to guard and passes for that
        /// reason - a true answer, not a failure to produce one. Blocking here stopped a perfectly
        /// ordinary all-remote CDN layout, and the advice it gave ("if no content is static, mark a
        /// group static") contradicted itself.</item>
        /// </list>
        ///
        /// Internal rather than private so the two branches can be tested without constructing a
        /// content state file - the decision depends only on the group configuration.
        /// </remarks>
        internal static void EvaluateStaticContentConfiguration(
            AddressableAssetSettings settings, ContentUpdateCheckResult result)
        {
            var localGroups = GetLocalBuildGroups(settings);

            if (localGroups.Count > 0)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message =
                    "No group is marked Cannot Change Post Release, but these groups build to a " +
                    $"Local path and therefore ship inside the player: {string.Join(", ", localGroups)}.\n\n" +
                    "Content inside the player cannot be replaced by a content update, so changing one " +
                    "of those groups produces bundles that existing players can never receive - exactly " +
                    "what this check exists to catch, and it cannot run while the groups that need " +
                    "guarding are not declared.\n\n" +
                    "Fix: on each of those groups, enable ContentUpdateGroupSchema > Cannot Change Post " +
                    "Release. If a group was meant to be downloadable, point its BuildPath and LoadPath " +
                    "at Remote.* instead.";
                return;
            }

            result.CanEvaluate = true;
            result.Passed = true;
            result.Message =
                "No group is marked Cannot Change Post Release, and no group builds to a Local path - " +
                "every group is remote and replaceable. There is no immutable content for a content " +
                "update to break, so this check passes for that reason, not because content was " +
                "compared.\n\n" +
                "If you expected some content to ship inside the player, that is the thing to check: a " +
                "group intended to be local but pointing at Remote.* is a different bug, and the " +
                "Validator tab's group:<name>:RemotePathsConsistent rule reports it.";
        }

        /// <summary>
        /// Names of the groups whose BuildPath resolves to a Local path, i.e. that ship inside the
        /// player build and therefore cannot be replaced by a content update.
        /// </summary>
        /// <remarks>
        /// This is what separates "nothing is static because nothing needs to be" from "something
        /// needed marking and was not marked". The profile variable name is the honest signal: a group
        /// bound to Local.BuildPath is baked into the player no matter what its other schemas say.
        /// </remarks>
        private static List<string> GetLocalBuildGroups(AddressableAssetSettings settings)
        {
            var localGroups = new List<string>();
            if (settings?.groups == null)
                return localGroups;

            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
                if (bundleSchema == null)
                    continue;

                string buildPathName = bundleSchema.BuildPath?.GetName(settings);
                if (string.IsNullOrEmpty(buildPathName))
                    continue;

                if (buildPathName.StartsWith("Local.", System.StringComparison.Ordinal))
                    localGroups.Add(group.Name);
            }

            return localGroups;
        }

        /// <summary>
        /// Gets all groups in the settings that have both BundledAssetGroupSchema and ContentUpdateGroupSchema
        /// with StaticContent enabled.
        /// </summary>
        private static List<AddressableAssetGroup> GetStaticGroups(AddressableAssetSettings settings)
        {
            var staticGroups = new List<AddressableAssetGroup>();
            if (settings?.groups == null)
                return staticGroups;

            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                var contentUpdateSchema = group.GetSchema<ContentUpdateGroupSchema>();
                if (contentUpdateSchema == null)
                    continue;

                var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
                if (bundleSchema == null)
                    continue;

                if (contentUpdateSchema.StaticContent)
                    staticGroups.Add(group);
            }

            return staticGroups;
        }
    }
}
