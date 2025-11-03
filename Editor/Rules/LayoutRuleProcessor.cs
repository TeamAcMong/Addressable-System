using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Processes layout rules and applies them to addressable assets
    /// </summary>
    public class LayoutRuleProcessor
    {
        /// <summary>
        /// Result of rule processing
        /// </summary>
        public class ProcessResult
        {
            public int TotalAssetsProcessed;
            public int AddressesApplied;
            public int LabelsApplied;
            public int VersionsApplied;
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
            public bool Success => Errors.Count == 0;
        }

        private readonly LayoutRuleData _ruleData;
        private readonly AddressableAssetSettings _settings;
        private bool _verboseLogging;

        public LayoutRuleProcessor(LayoutRuleData ruleData)
        {
            _ruleData = ruleData ?? throw new ArgumentNullException(nameof(ruleData));
            _settings = AddressableAssetSettingsDefaultObject.Settings;

            if (_settings == null)
            {
                throw new InvalidOperationException("AddressableAssetSettings not found. Please initialize Addressables first.");
            }

            _verboseLogging = ruleData.VerboseLogging;
        }

        /// <summary>
        /// Apply all rules to all assets in the project
        /// </summary>
        public ProcessResult ApplyRules(Action<float, string> progressCallback = null)
        {
            var result = new ProcessResult();

            try
            {
                // Validate rules first
                var (isValid, validationErrors) = _ruleData.Validate();
                if (!isValid)
                {
                    result.Errors.AddRange(validationErrors);
                    return result;
                }

                Log("Starting rule processing...");

                // Setup all filters and providers
                SetupRules();

                // Get all assets in the project
                progressCallback?.Invoke(0.1f, "Finding assets...");
                var allAssetPaths = GetAllAssetPaths();
                Log($"Found {allAssetPaths.Count} total assets to process");

                // Process address rules
                progressCallback?.Invoke(0.2f, "Processing address rules...");
                ProcessAddressRules(allAssetPaths, result, progressCallback);

                // Process label rules
                progressCallback?.Invoke(0.6f, "Processing label rules...");
                ProcessLabelRules(allAssetPaths, result, progressCallback);

                // Process version rules (placeholder for now - full implementation in Phase 4)
                progressCallback?.Invoke(0.9f, "Processing version rules...");
                ProcessVersionRules(allAssetPaths, result, progressCallback);

                // Save changes
                progressCallback?.Invoke(0.95f, "Saving changes...");
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();

                progressCallback?.Invoke(1.0f, "Complete!");
                Log($"Rule processing complete. Processed {result.TotalAssetsProcessed} assets.");
                Log($"Applied: {result.AddressesApplied} addresses, {result.LabelsApplied} labels, {result.VersionsApplied} versions");

                if (result.Warnings.Count > 0)
                {
                    Log($"Warnings: {result.Warnings.Count}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception during rule processing: {ex.Message}");
                Debug.LogException(ex);
            }

            return result;
        }

        /// <summary>
        /// Apply rules to specific asset paths
        /// </summary>
        public ProcessResult ApplyRulesToAssets(List<string> assetPaths, Action<float, string> progressCallback = null)
        {
            var result = new ProcessResult();

            try
            {
                // Validate rules
                var (isValid, validationErrors) = _ruleData.Validate();
                if (!isValid)
                {
                    result.Errors.AddRange(validationErrors);
                    return result;
                }

                SetupRules();

                // Process rules
                ProcessAddressRules(assetPaths, result, progressCallback);
                ProcessLabelRules(assetPaths, result, progressCallback);
                ProcessVersionRules(assetPaths, result, progressCallback);

                // Save
                EditorUtility.SetDirty(_settings);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception: {ex.Message}");
                Debug.LogException(ex);
            }

            return result;
        }

        private void SetupRules()
        {
            foreach (var rule in _ruleData.AddressRules)
            {
                rule?.Setup();
            }

            foreach (var rule in _ruleData.LabelRules)
            {
                rule?.Setup();
            }

            foreach (var rule in _ruleData.VersionRules)
            {
                rule?.Setup();
            }
        }

        private List<string> GetAllAssetPaths()
        {
            var paths = new List<string>();

            // Get all asset GUIDs
            var allGuids = AssetDatabase.FindAssets("");
            foreach (var guid in allGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private void ProcessAddressRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            if (_ruleData.AddressRules == null || _ruleData.AddressRules.Count == 0)
            {
                Log("No address rules to process");
                return;
            }

            // Sort rules by priority (higher first)
            var sortedRules = _ruleData.AddressRules
                .Where(r => r != null && r.Enabled)
                .OrderByDescending(r => r.Priority)
                .ToList();

            Log($"Processing {sortedRules.Count} address rules");

            int processed = 0;
            int total = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                processed++;
                if (processed % 100 == 0)
                {
                    float progress = 0.2f + (0.4f * (processed / (float)total));
                    progressCallback?.Invoke(progress, $"Processing addresses ({processed}/{total})...");
                }

                // Find first matching rule
                AddressRule matchedRule = null;
                foreach (var rule in sortedRules)
                {
                    if (rule.IsMatch(assetPath))
                    {
                        matchedRule = rule;
                        break; // Use first matching rule (highest priority)
                    }
                }

                if (matchedRule != null)
                {
                    ApplyAddressRule(assetPath, matchedRule, result);
                }
            }
        }

        private void ApplyAddressRule(string assetPath, AddressRule rule, ProcessResult result)
        {
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);

                // Skip if already has address and rule says skip existing
                if (rule.SkipExisting && entry != null && !string.IsNullOrEmpty(entry.address))
                {
                    return;
                }

                // Generate address
                string address = rule.GenerateAddress(assetPath);
                if (string.IsNullOrEmpty(address))
                {
                    result.Warnings.Add($"Rule '{rule.RuleName}' generated empty address for: {assetPath}");
                    return;
                }

                // Get or create target group
                var targetGroup = rule.GetOrCreateTargetGroup(_settings);
                if (targetGroup == null)
                {
                    result.Errors.Add($"Failed to get/create target group for rule '{rule.RuleName}'");
                    return;
                }

                // Create or update entry
                if (entry == null)
                {
                    entry = _settings.CreateOrMoveEntry(guid, targetGroup, false, false);
                }
                else if (entry.parentGroup != targetGroup)
                {
                    _settings.MoveEntry(entry, targetGroup, false, false);
                }

                if (entry != null)
                {
                    entry.SetAddress(address, false);
                    result.AddressesApplied++;
                    result.TotalAssetsProcessed++;
                    LogVerbose($"Applied address '{address}' to {assetPath}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error applying address rule to {assetPath}: {ex.Message}");
            }
        }

        private void ProcessLabelRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            if (_ruleData.LabelRules == null || _ruleData.LabelRules.Count == 0)
            {
                Log("No label rules to process");
                return;
            }

            var sortedRules = _ruleData.LabelRules
                .Where(r => r != null && r.Enabled)
                .OrderByDescending(r => r.Priority)
                .ToList();

            Log($"Processing {sortedRules.Count} label rules");

            int processed = 0;
            int total = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                processed++;
                if (processed % 100 == 0)
                {
                    float progress = 0.6f + (0.3f * (processed / (float)total));
                    progressCallback?.Invoke(progress, $"Processing labels ({processed}/{total})...");
                }

                // Collect labels from all matching rules
                var labelsToApply = new HashSet<string>();

                foreach (var rule in sortedRules)
                {
                    if (rule.IsMatch(assetPath))
                    {
                        var labels = rule.GenerateLabels(assetPath);
                        if (labels != null)
                        {
                            foreach (var label in labels)
                            {
                                if (!string.IsNullOrEmpty(label))
                                {
                                    labelsToApply.Add(label);
                                }
                            }
                        }
                    }
                }

                if (labelsToApply.Count > 0)
                {
                    ApplyLabels(assetPath, labelsToApply.ToList(), result);
                }
            }
        }

        private void ApplyLabels(string assetPath, List<string> labels, ProcessResult result)
        {
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);

                if (entry == null)
                {
                    // Asset not in addressables - skip labels
                    return;
                }

                foreach (var label in labels)
                {
                    // Add label to settings if it doesn't exist
                    if (!_settings.GetLabels().Contains(label))
                    {
                        _settings.AddLabel(label, false);
                    }

                    // Add label to entry
                    if (!entry.labels.Contains(label))
                    {
                        entry.labels.Add(label);
                        result.LabelsApplied++;
                        LogVerbose($"Applied label '{label}' to {assetPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error applying labels to {assetPath}: {ex.Message}");
            }
        }

        private void ProcessVersionRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            // Placeholder for Phase 4 - Version Management
            // For now, just log that version rules are not yet implemented
            if (_ruleData.VersionRules != null && _ruleData.VersionRules.Count > 0)
            {
                result.Warnings.Add("Version rules are not yet implemented (coming in v3.4.0)");
            }
        }

        private void Log(string message)
        {
            Debug.Log($"[LayoutRuleProcessor] {message}");
        }

        private void LogVerbose(string message)
        {
            if (_verboseLogging)
            {
                Debug.Log($"[LayoutRuleProcessor] {message}");
            }
        }
    }
}
