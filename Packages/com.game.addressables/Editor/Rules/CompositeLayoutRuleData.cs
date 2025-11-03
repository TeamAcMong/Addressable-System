using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Combines multiple LayoutRuleData assets into a single unified rule set
    /// Useful for modular rule organization
    /// </summary>
    [CreateAssetMenu(fileName = "CompositeLayoutRuleData", menuName = "Addressable Manager/Composite Layout Rule Data", order = 2)]
    public class CompositeLayoutRuleData : ScriptableObject
    {
        [Header("Source Rule Data")]
        [Tooltip("List of LayoutRuleData assets to combine")]
        [SerializeField] private List<LayoutRuleData> _sourceRuleData = new List<LayoutRuleData>();

        [Header("Settings")]
        [Tooltip("Priority order for rule application (first = highest priority)")]
        [SerializeField] private bool _respectSourceOrder = true;

        [Tooltip("Deduplicate rules with same name")]
        [SerializeField] private bool _deduplicateByName = true;

        /// <summary>
        /// Source rule data assets
        /// </summary>
        public List<LayoutRuleData> SourceRuleData => _sourceRuleData;

        /// <summary>
        /// Get all address rules from all sources combined
        /// </summary>
        public List<AddressRule> GetCombinedAddressRules()
        {
            var rules = new List<AddressRule>();
            var seenNames = new HashSet<string>();

            foreach (var source in _sourceRuleData)
            {
                if (source == null) continue;

                foreach (var rule in source.AddressRules)
                {
                    if (rule == null) continue;

                    if (_deduplicateByName && seenNames.Contains(rule.RuleName))
                    {
                        Debug.LogWarning($"[CompositeLayoutRuleData] Skipping duplicate address rule: {rule.RuleName}");
                        continue;
                    }

                    rules.Add(rule);
                    seenNames.Add(rule.RuleName);
                }
            }

            // Sort by priority if not respecting source order
            if (!_respectSourceOrder)
            {
                rules = rules.OrderByDescending(r => r.Priority).ToList();
            }

            return rules;
        }

        /// <summary>
        /// Get all label rules from all sources combined
        /// </summary>
        public List<LabelRule> GetCombinedLabelRules()
        {
            var rules = new List<LabelRule>();
            var seenNames = new HashSet<string>();

            foreach (var source in _sourceRuleData)
            {
                if (source == null) continue;

                foreach (var rule in source.LabelRules)
                {
                    if (rule == null) continue;

                    if (_deduplicateByName && seenNames.Contains(rule.RuleName))
                    {
                        Debug.LogWarning($"[CompositeLayoutRuleData] Skipping duplicate label rule: {rule.RuleName}");
                        continue;
                    }

                    rules.Add(rule);
                    seenNames.Add(rule.RuleName);
                }
            }

            if (!_respectSourceOrder)
            {
                rules = rules.OrderByDescending(r => r.Priority).ToList();
            }

            return rules;
        }

        /// <summary>
        /// Get all version rules from all sources combined
        /// </summary>
        public List<VersionRule> GetCombinedVersionRules()
        {
            var rules = new List<VersionRule>();
            var seenNames = new HashSet<string>();

            foreach (var source in _sourceRuleData)
            {
                if (source == null) continue;

                foreach (var rule in source.VersionRules)
                {
                    if (rule == null) continue;

                    if (_deduplicateByName && seenNames.Contains(rule.RuleName))
                    {
                        Debug.LogWarning($"[CompositeLayoutRuleData] Skipping duplicate version rule: {rule.RuleName}");
                        continue;
                    }

                    rules.Add(rule);
                    seenNames.Add(rule.RuleName);
                }
            }

            if (!_respectSourceOrder)
            {
                rules = rules.OrderByDescending(r => r.Priority).ToList();
            }

            return rules;
        }

        /// <summary>
        /// Create a temporary merged LayoutRuleData for processing
        /// </summary>
        public LayoutRuleData CreateMergedRuleData()
        {
            var merged = CreateInstance<LayoutRuleData>();
            merged.name = $"{name} (Merged)";

            // Add all rules
            foreach (var rule in GetCombinedAddressRules())
            {
                merged.AddAddressRule(rule);
            }

            foreach (var rule in GetCombinedLabelRules())
            {
                merged.AddLabelRule(rule);
            }

            foreach (var rule in GetCombinedVersionRules())
            {
                merged.AddVersionRule(rule);
            }

            // Use settings from first source
            if (_sourceRuleData.Count > 0 && _sourceRuleData[0] != null)
            {
                merged.AutoApplyOnImport = _sourceRuleData[0].AutoApplyOnImport;
                merged.AutoApplyOnModified = _sourceRuleData[0].AutoApplyOnModified;
                merged.VerboseLogging = _sourceRuleData[0].VerboseLogging;
            }

            return merged;
        }

        /// <summary>
        /// Get total rule count across all sources
        /// </summary>
        public int GetTotalRuleCount()
        {
            return GetCombinedAddressRules().Count +
                   GetCombinedLabelRules().Count +
                   GetCombinedVersionRules().Count;
        }

        /// <summary>
        /// Validate all source rule data
        /// </summary>
        public (bool isValid, List<string> errors) Validate()
        {
            var errors = new List<string>();

            if (_sourceRuleData == null || _sourceRuleData.Count == 0)
            {
                errors.Add("No source LayoutRuleData assets assigned");
                return (false, errors);
            }

            int nullCount = _sourceRuleData.Count(r => r == null);
            if (nullCount > 0)
            {
                errors.Add($"{nullCount} source(s) are null");
            }

            // Validate each source
            foreach (var source in _sourceRuleData)
            {
                if (source == null) continue;

                var (isValid, sourceErrors) = source.Validate();
                if (!isValid)
                {
                    errors.Add($"Source '{source.name}' has validation errors:");
                    errors.AddRange(sourceErrors.Select(e => $"  {e}"));
                }
            }

            return (errors.Count == 0, errors);
        }
    }
}
