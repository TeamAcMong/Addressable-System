using System;
using System.Collections.Generic;
using UnityEngine;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Rule for automatically assigning labels to assets
    /// Uses filters to match assets and providers to generate labels
    /// </summary>
    [Serializable]
    public class LabelRule
    {
        [Header("Rule Configuration")]
        [Tooltip("Name of this rule for identification")]
        [SerializeField] private string _ruleName = "New Label Rule";

        [Tooltip("Enable/disable this rule")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Description of what this rule does")]
        [SerializeField] [TextArea(2, 3)] private string _description;

        [Header("Filters (AND logic)")]
        [Tooltip("Asset filters - all must match for rule to apply")]
        [SerializeField] private List<AssetFilterBase> _filters = new List<AssetFilterBase>();

        [Header("Label Provider")]
        [Tooltip("Provider to generate labels from matched assets")]
        [SerializeField] private LabelProviderBase _labelProvider;

        [Header("Advanced")]
        [Tooltip("Priority - higher priority rules are applied first")]
        [SerializeField] private int _priority = 0;

        [Tooltip("Append to existing labels instead of replacing")]
        [SerializeField] private bool _appendToExisting = true;

        /// <summary>
        /// Rule name
        /// </summary>
        public string RuleName
        {
            get => _ruleName;
            set => _ruleName = value;
        }

        /// <summary>
        /// Is this rule enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Rule description
        /// </summary>
        public string Description
        {
            get => _description;
            set => _description = value;
        }

        /// <summary>
        /// Asset filters (AND logic)
        /// </summary>
        public List<AssetFilterBase> Filters => _filters;

        /// <summary>
        /// Label provider
        /// </summary>
        public LabelProviderBase LabelProvider
        {
            get => _labelProvider;
            set => _labelProvider = value;
        }

        /// <summary>
        /// Rule priority
        /// </summary>
        public int Priority
        {
            get => _priority;
            set => _priority = value;
        }

        /// <summary>
        /// Append to existing labels
        /// </summary>
        public bool AppendToExisting
        {
            get => _appendToExisting;
            set => _appendToExisting = value;
        }

        /// <summary>
        /// Check if an asset matches this rule
        /// </summary>
        public bool IsMatch(string assetPath)
        {
            if (!_enabled)
                return false;

            if (_filters == null || _filters.Count == 0)
                return false;

            // AND logic over ENABLED filters. AssetFilterBase.IsMatch() returning true for a
            // disabled filter is correct in isolation - a disabled filter must not veto the
            // filters that ARE active. But ANDing only those "always true" disabled results
            // together silently turns a rule into match-all the moment every one of its
            // filters gets unchecked, and this rule runs against the (bounded but still
            // project-wide) Assets/ scan in LayoutRuleProcessor.ProcessLabelRules. Require at
            // least one ENABLED filter to actually constrain the match; a rule with every
            // filter disabled has no active constraint left and must not match anything
            // (HANDOFF_TO_SESSION_B.md E-PAIR-1 — same defect as AddressRule.IsMatch). Do not
            // "fix" this by flipping AssetFilterBase.IsMatch's disabled-return-value instead -
            // that would silently invert every already-configured filter across the project.
            bool hasEnabledFilter = false;
            foreach (var filter in _filters)
            {
                if (filter == null || !filter.IsMatch(assetPath))
                    return false;

                if (filter.Enabled)
                    hasEnabledFilter = true;
            }

            return hasEnabledFilter;
        }

        /// <summary>
        /// Generate labels for an asset
        /// </summary>
        public List<string> GenerateLabels(string assetPath)
        {
            if (_labelProvider == null)
            {
                Debug.LogWarning($"[LabelRule] {_ruleName}: No label provider assigned");
                return new List<string>();
            }

            return _labelProvider.Provide(assetPath);
        }

        /// <summary>
        /// Validate this rule
        /// </summary>
        public (bool isValid, List<string> errors) Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(_ruleName))
            {
                errors.Add("Rule name cannot be empty");
            }

            if (_filters == null || _filters.Count == 0)
            {
                errors.Add($"Rule '{_ruleName}': Must have at least one filter");
            }
            else
            {
                // Check for null filters
                for (int i = 0; i < _filters.Count; i++)
                {
                    if (_filters[i] == null)
                    {
                        errors.Add($"Rule '{_ruleName}': Filter at index {i} is null");
                    }
                }
            }

            if (_labelProvider == null)
            {
                errors.Add($"Rule '{_ruleName}': Label provider is not assigned");
            }

            return (errors.Count == 0, errors);
        }

        /// <summary>
        /// Setup all components (call on main thread before worker thread usage)
        /// </summary>
        public void Setup()
        {
            if (_filters != null)
            {
                foreach (var filter in _filters)
                {
                    filter?.Setup();
                }
            }

            _labelProvider?.Setup();
        }
    }
}
