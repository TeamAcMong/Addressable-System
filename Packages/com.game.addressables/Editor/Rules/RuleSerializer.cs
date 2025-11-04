using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Serializes and deserializes layout rules to/from JSON format
    /// Used for import/export and rule templates
    /// </summary>
    public static class RuleSerializer
    {
        [Serializable]
        private class RuleDataExport
        {
            public string version = "1.0";
            public string description;
            public bool autoApplyOnImport;
            public bool autoApplyOnModified;
            public bool verboseLogging;
            public string versionExpression;
            public bool excludeUnversioned;
            public List<AddressRuleExport> addressRules = new List<AddressRuleExport>();
            public List<LabelRuleExport> labelRules = new List<LabelRuleExport>();
            public List<VersionRuleExport> versionRules = new List<VersionRuleExport>();
        }

        [Serializable]
        private class AddressRuleExport
        {
            public string ruleName;
            public string description;
            public bool enabled = true;
            public int priority;
            public bool skipExisting;
            public string targetGroupName;
            public List<FilterExport> filters = new List<FilterExport>();
            public string addressProviderType;
            public string addressProviderPath;
        }

        [Serializable]
        private class LabelRuleExport
        {
            public string ruleName;
            public string description;
            public bool enabled = true;
            public int priority;
            public bool appendToExisting = true;
            public List<FilterExport> filters = new List<FilterExport>();
            public string labelProviderType;
            public string labelProviderPath;
        }

        [Serializable]
        private class VersionRuleExport
        {
            public string ruleName;
            public string description;
            public bool enabled = true;
            public int priority;
            public bool skipExisting;
            public List<FilterExport> filters = new List<FilterExport>();
            public string versionProviderType;
            public string versionProviderPath;
        }

        [Serializable]
        private class FilterExport
        {
            public string filterType;
            public string filterAssetPath;
        }

        /// <summary>
        /// Export LayoutRuleData to JSON file
        /// </summary>
        public static bool ExportToJson(LayoutRuleData ruleData, string filePath)
        {
            if (ruleData == null)
            {
                Debug.LogError("[RuleSerializer] Cannot export null rule data");
                return false;
            }

            try
            {
                var export = new RuleDataExport
                {
                    description = ruleData.Description,
                    autoApplyOnImport = ruleData.AutoApplyOnImport,
                    autoApplyOnModified = ruleData.AutoApplyOnModified,
                    verboseLogging = ruleData.VerboseLogging,
                    versionExpression = ruleData.VersionExpression,
                    excludeUnversioned = ruleData.ExcludeUnversioned
                };

                // Export address rules
                foreach (var rule in ruleData.AddressRules)
                {
                    if (rule == null) continue;

                    var ruleExport = new AddressRuleExport
                    {
                        ruleName = rule.RuleName,
                        description = rule.Description,
                        enabled = rule.Enabled,
                        priority = rule.Priority,
                        skipExisting = rule.SkipExisting,
                        targetGroupName = rule.TargetGroupName,
                        addressProviderType = rule.AddressProvider?.GetType().Name ?? "",
                        addressProviderPath = rule.AddressProvider != null ? AssetDatabase.GetAssetPath(rule.AddressProvider) : ""
                    };

                    foreach (var filter in rule.Filters)
                    {
                        if (filter == null) continue;
                        ruleExport.filters.Add(new FilterExport
                        {
                            filterType = filter.GetType().Name,
                            filterAssetPath = AssetDatabase.GetAssetPath(filter)
                        });
                    }

                    export.addressRules.Add(ruleExport);
                }

                // Export label rules
                foreach (var rule in ruleData.LabelRules)
                {
                    if (rule == null) continue;

                    var ruleExport = new LabelRuleExport
                    {
                        ruleName = rule.RuleName,
                        description = rule.Description,
                        enabled = rule.Enabled,
                        priority = rule.Priority,
                        appendToExisting = rule.AppendToExisting,
                        labelProviderType = rule.LabelProvider?.GetType().Name ?? "",
                        labelProviderPath = rule.LabelProvider != null ? AssetDatabase.GetAssetPath(rule.LabelProvider) : ""
                    };

                    foreach (var filter in rule.Filters)
                    {
                        if (filter == null) continue;
                        ruleExport.filters.Add(new FilterExport
                        {
                            filterType = filter.GetType().Name,
                            filterAssetPath = AssetDatabase.GetAssetPath(filter)
                        });
                    }

                    export.labelRules.Add(ruleExport);
                }

                // Export version rules
                foreach (var rule in ruleData.VersionRules)
                {
                    if (rule == null) continue;

                    var ruleExport = new VersionRuleExport
                    {
                        ruleName = rule.RuleName,
                        description = rule.Description,
                        enabled = rule.Enabled,
                        priority = rule.Priority,
                        skipExisting = rule.SkipExisting,
                        versionProviderType = rule.VersionProvider?.GetType().Name ?? "",
                        versionProviderPath = rule.VersionProvider != null ? AssetDatabase.GetAssetPath(rule.VersionProvider) : ""
                    };

                    foreach (var filter in rule.Filters)
                    {
                        if (filter == null) continue;
                        ruleExport.filters.Add(new FilterExport
                        {
                            filterType = filter.GetType().Name,
                            filterAssetPath = AssetDatabase.GetAssetPath(filter)
                        });
                    }

                    export.versionRules.Add(ruleExport);
                }

                // Write to file
                string json = JsonUtility.ToJson(export, true);
                File.WriteAllText(filePath, json);

                Debug.Log($"[RuleSerializer] Exported {export.addressRules.Count} address rules, {export.labelRules.Count} label rules, {export.versionRules.Count} version rules to {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuleSerializer] Failed to export rules: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Import rules from JSON file into existing LayoutRuleData
        /// </summary>
        public static bool ImportFromJson(LayoutRuleData ruleData, string filePath, bool mergeMode = false)
        {
            if (ruleData == null)
            {
                Debug.LogError("[RuleSerializer] Cannot import into null rule data");
                return false;
            }

            if (!File.Exists(filePath))
            {
                Debug.LogError($"[RuleSerializer] Import file not found: {filePath}");
                return false;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var import = JsonUtility.FromJson<RuleDataExport>(json);

                if (import == null)
                {
                    Debug.LogError("[RuleSerializer] Failed to parse JSON");
                    return false;
                }

                // Clear existing rules if not merging
                if (!mergeMode)
                {
                    ruleData.ClearAllRules();
                }

                // Import settings (only if not merging)
                if (!mergeMode)
                {
                    ruleData.Description = import.description;
                    ruleData.AutoApplyOnImport = import.autoApplyOnImport;
                    ruleData.AutoApplyOnModified = import.autoApplyOnModified;
                    ruleData.VerboseLogging = import.verboseLogging;
                    ruleData.VersionExpression = import.versionExpression;
                    ruleData.ExcludeUnversioned = import.excludeUnversioned;
                }

                int successCount = 0;
                int failCount = 0;

                // Import address rules
                foreach (var ruleImport in import.addressRules)
                {
                    try
                    {
                        var rule = new AddressRule
                        {
                            RuleName = ruleImport.ruleName,
                            Description = ruleImport.description,
                            Enabled = ruleImport.enabled,
                            Priority = ruleImport.priority,
                            SkipExisting = ruleImport.skipExisting,
                            TargetGroupName = ruleImport.targetGroupName
                        };

                        // Load filters
                        foreach (var filterExport in ruleImport.filters)
                        {
                            if (!string.IsNullOrEmpty(filterExport.filterAssetPath))
                            {
                                var filter = AssetDatabase.LoadAssetAtPath<AssetFilterBase>(filterExport.filterAssetPath);
                                if (filter != null)
                                {
                                    rule.Filters.Add(filter);
                                }
                                else
                                {
                                    Debug.LogWarning($"[RuleSerializer] Filter not found: {filterExport.filterAssetPath}");
                                }
                            }
                        }

                        // Load address provider
                        if (!string.IsNullOrEmpty(ruleImport.addressProviderPath))
                        {
                            rule.AddressProvider = AssetDatabase.LoadAssetAtPath<AddressProviderBase>(ruleImport.addressProviderPath);
                            if (rule.AddressProvider == null)
                            {
                                Debug.LogWarning($"[RuleSerializer] Address provider not found: {ruleImport.addressProviderPath}");
                            }
                        }

                        ruleData.AddAddressRule(rule);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RuleSerializer] Failed to import address rule '{ruleImport.ruleName}': {ex.Message}");
                        failCount++;
                    }
                }

                // Import label rules
                foreach (var ruleImport in import.labelRules)
                {
                    try
                    {
                        var rule = new LabelRule
                        {
                            RuleName = ruleImport.ruleName,
                            Description = ruleImport.description,
                            Enabled = ruleImport.enabled,
                            Priority = ruleImport.priority,
                            AppendToExisting = ruleImport.appendToExisting
                        };

                        // Load filters
                        foreach (var filterExport in ruleImport.filters)
                        {
                            if (!string.IsNullOrEmpty(filterExport.filterAssetPath))
                            {
                                var filter = AssetDatabase.LoadAssetAtPath<AssetFilterBase>(filterExport.filterAssetPath);
                                if (filter != null)
                                {
                                    rule.Filters.Add(filter);
                                }
                            }
                        }

                        // Load label provider
                        if (!string.IsNullOrEmpty(ruleImport.labelProviderPath))
                        {
                            rule.LabelProvider = AssetDatabase.LoadAssetAtPath<LabelProviderBase>(ruleImport.labelProviderPath);
                        }

                        ruleData.AddLabelRule(rule);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RuleSerializer] Failed to import label rule '{ruleImport.ruleName}': {ex.Message}");
                        failCount++;
                    }
                }

                // Import version rules
                foreach (var ruleImport in import.versionRules)
                {
                    try
                    {
                        var rule = new VersionRule
                        {
                            RuleName = ruleImport.ruleName,
                            Description = ruleImport.description,
                            Enabled = ruleImport.enabled,
                            Priority = ruleImport.priority,
                            SkipExisting = ruleImport.skipExisting
                        };

                        // Load filters
                        foreach (var filterExport in ruleImport.filters)
                        {
                            if (!string.IsNullOrEmpty(filterExport.filterAssetPath))
                            {
                                var filter = AssetDatabase.LoadAssetAtPath<AssetFilterBase>(filterExport.filterAssetPath);
                                if (filter != null)
                                {
                                    rule.Filters.Add(filter);
                                }
                            }
                        }

                        // Load version provider
                        if (!string.IsNullOrEmpty(ruleImport.versionProviderPath))
                        {
                            rule.VersionProvider = AssetDatabase.LoadAssetAtPath<VersionProviderBase>(ruleImport.versionProviderPath);
                        }

                        ruleData.AddVersionRule(rule);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RuleSerializer] Failed to import version rule '{ruleImport.ruleName}': {ex.Message}");
                        failCount++;
                    }
                }

                EditorUtility.SetDirty(ruleData);
                AssetDatabase.SaveAssets();

                Debug.Log($"[RuleSerializer] Import complete. Success: {successCount}, Failed: {failCount}");
                return failCount == 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RuleSerializer] Failed to import rules: {ex.Message}");
                return false;
            }
        }
    }
}
