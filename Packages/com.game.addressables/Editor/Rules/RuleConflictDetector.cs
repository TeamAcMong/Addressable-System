using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Detects conflicts in addressable rules such as duplicate addresses
    /// </summary>
    public static class RuleConflictDetector
    {
        public enum ConflictType
        {
            DuplicateAddress,           // Same address assigned to multiple assets
            InvalidAddressCharacters,   // Address contains invalid characters
            EmptyAddress,               // Empty or whitespace-only address
            CircularDependency,         // Asset depends on itself (circular reference)
            MissingReference,           // Asset references missing object
            GroupConflict              // Asset in wrong group for its dependencies
        }

        public class Conflict
        {
            public ConflictType Type;
            public string Message;
            public List<string> AffectedAssets = new List<string>();
            public string Suggestion;

            public Conflict(ConflictType type, string message, string suggestion = "")
            {
                Type = type;
                Message = message;
                Suggestion = suggestion;
            }
        }

        /// <summary>
        /// Detect all conflicts in addressable settings
        /// </summary>
        public static List<Conflict> DetectConflicts(AddressableAssetSettings settings = null)
        {
            var conflicts = new List<Conflict>();

            if (settings == null)
            {
                settings = AddressableAssetSettingsDefaultObject.Settings;
            }

            if (settings == null)
            {
                return conflicts;
            }

            // Detect duplicate addresses
            DetectDuplicateAddresses(settings, conflicts);

            // Detect invalid address characters
            DetectInvalidAddresses(settings, conflicts);

            // Detect empty addresses
            DetectEmptyAddresses(settings, conflicts);

            return conflicts;
        }

        /// <summary>
        /// Detect potential conflicts that would result from applying rules
        /// </summary>
        public static List<Conflict> PreviewRuleConflicts(LayoutRuleData ruleData, List<string> assetPaths)
        {
            var conflicts = new List<Conflict>();

            if (ruleData == null || assetPaths == null)
                return conflicts;

            // Track addresses that would be generated
            var addressMap = new Dictionary<string, List<string>>(); // address -> asset paths

            // Setup rules
            foreach (var rule in ruleData.AddressRules)
            {
                rule?.Setup();
            }

            // Simulate address generation
            foreach (var assetPath in assetPaths)
            {
                foreach (var rule in ruleData.AddressRules)
                {
                    if (rule != null && rule.Enabled && rule.IsMatch(assetPath))
                    {
                        string address = rule.GenerateAddress(assetPath);
                        if (!string.IsNullOrEmpty(address))
                        {
                            if (!addressMap.ContainsKey(address))
                            {
                                addressMap[address] = new List<string>();
                            }
                            addressMap[address].Add(assetPath);
                        }
                        break; // Use first matching rule
                    }
                }
            }

            // Check for duplicate addresses
            foreach (var kvp in addressMap)
            {
                if (kvp.Value.Count > 1)
                {
                    var conflict = new Conflict(
                        ConflictType.DuplicateAddress,
                        $"Duplicate address would be created: '{kvp.Key}'",
                        "Adjust rules to generate unique addresses for each asset");

                    conflict.AffectedAssets.AddRange(kvp.Value);
                    conflicts.Add(conflict);
                }
            }

            return conflicts;
        }

        private static void DetectDuplicateAddresses(AddressableAssetSettings settings, List<Conflict> conflicts)
        {
            var addressMap = new Dictionary<string, List<AddressableAssetEntry>>();

            // Collect all entries grouped by address
            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address))
                        continue;

                    if (!addressMap.ContainsKey(entry.address))
                    {
                        addressMap[entry.address] = new List<AddressableAssetEntry>();
                    }
                    addressMap[entry.address].Add(entry);
                }
            }

            // Find duplicates
            foreach (var kvp in addressMap)
            {
                if (kvp.Value.Count > 1)
                {
                    var conflict = new Conflict(
                        ConflictType.DuplicateAddress,
                        $"Duplicate address found: '{kvp.Key}' is assigned to {kvp.Value.Count} assets",
                        "Each addressable asset should have a unique address");

                    foreach (var entry in kvp.Value)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                        conflict.AffectedAssets.Add(path);
                    }

                    conflicts.Add(conflict);
                }
            }
        }

        private static void DetectInvalidAddresses(AddressableAssetSettings settings, List<Conflict> conflicts)
        {
            // Characters that might cause issues in addresses
            char[] invalidChars = { '<', '>', ':', '"', '\\', '|', '?', '*' };

            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address))
                        continue;

                    // Check for invalid characters
                    if (entry.address.IndexOfAny(invalidChars) >= 0)
                    {
                        var conflict = new Conflict(
                            ConflictType.InvalidAddressCharacters,
                            $"Address contains invalid characters: '{entry.address}'",
                            "Avoid using special characters like <, >, :, \", \\, |, ?, *");

                        string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                        conflict.AffectedAssets.Add(path);
                        conflicts.Add(conflict);
                    }

                    // Check for leading/trailing whitespace
                    if (entry.address != entry.address.Trim())
                    {
                        var conflict = new Conflict(
                            ConflictType.InvalidAddressCharacters,
                            $"Address has leading or trailing whitespace: '{entry.address}'",
                            "Remove whitespace from address start/end");

                        string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                        conflict.AffectedAssets.Add(path);
                        conflicts.Add(conflict);
                    }
                }
            }
        }

        private static void DetectEmptyAddresses(AddressableAssetSettings settings, List<Conflict> conflicts)
        {
            foreach (var group in settings.groups)
            {
                if (group == null)
                    continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(entry.address))
                    {
                        var conflict = new Conflict(
                            ConflictType.EmptyAddress,
                            "Asset has empty or whitespace-only address",
                            "Assign a valid address or remove from addressables");

                        string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                        conflict.AffectedAssets.Add(path);
                        conflicts.Add(conflict);
                    }
                }
            }
        }

        /// <summary>
        /// Get a summary string of conflicts
        /// </summary>
        public static string GetConflictSummary(List<Conflict> conflicts)
        {
            if (conflicts.Count == 0)
                return "✓ No conflicts found";

            var groups = conflicts.GroupBy(c => c.Type);
            var summary = string.Join(", ", groups.Select(g => $"{g.Count()} {g.Key}"));
            return $"{conflicts.Count} total conflicts: {summary}";
        }
    }
}
