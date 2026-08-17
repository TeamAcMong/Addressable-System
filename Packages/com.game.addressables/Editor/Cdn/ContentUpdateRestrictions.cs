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

            // Check if there is any static content configuration at all
            var staticGroups = GetStaticGroups(settings);
            if (staticGroups.Count == 0)
            {
                result.CanEvaluate = false;
                result.Passed = false;
                result.Message =
                    "No groups with static content (Cannot Change Post Release) detected. " +
                    "This is a configuration failure — the check is meant to guard against accidental " +
                    "modifications to content that cannot be updated. If no content is static, " +
                    "ensure that at least one group has the ContentUpdateGroupSchema with StaticContent enabled.";
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
