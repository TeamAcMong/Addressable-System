using System;
using System.Collections.Generic;
using UnityEngine;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Rule for automatically assigning versions to assets
    /// Uses filters to match assets and providers to generate versions
    /// </summary>
    [Serializable]
    public class VersionRule
    {
        [Header("Rule Configuration")]
        [Tooltip("Name of this rule for identification")]
        [SerializeField] private string _ruleName = "New Version Rule";

        [Tooltip("Enable/disable this rule")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Description of what this rule does")]
        [SerializeField] [TextArea(2, 3)] private string _description;

        [Header("Filters (AND logic)")]
        [Tooltip("Asset filters - all must match for rule to apply")]
        [SerializeField] private List<AssetFilterBase> _filters = new List<AssetFilterBase>();

        [Header("Version Provider")]
        [Tooltip("Provider to generate version from matched assets")]
        [SerializeField] private VersionProviderBase _versionProvider;

        [Header("Advanced")]
        [Tooltip("Priority - higher priority rules are applied first")]
        [SerializeField] private int _priority = 0;

        [Tooltip("Skip assets that already have versions assigned")]
        [SerializeField] private bool _skipExisting = false;

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
        /// Version provider
        /// </summary>
        public VersionProviderBase VersionProvider
        {
            get => _versionProvider;
            set => _versionProvider = value;
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
        /// Skip assets with existing versions
        /// </summary>
        public bool SkipExisting
        {
            get => _skipExisting;
            set => _skipExisting = value;
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
            // project-wide) Assets/ scan in LayoutRuleProcessor.ProcessVersionRules. Require at
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
        /// Generate version for an asset
        /// </summary>
        public string GenerateVersion(string assetPath)
        {
            if (_versionProvider == null)
            {
                Debug.LogWarning($"[VersionRule] {_ruleName}: No version provider assigned");
                return null;
            }

            return _versionProvider.Provide(assetPath);
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

            if (_versionProvider == null)
            {
                errors.Add($"Rule '{_ruleName}': Version provider is not assigned");
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

            _versionProvider?.Setup();
        }
    }
}
