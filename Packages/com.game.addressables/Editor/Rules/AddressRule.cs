using System;
using System.Collections.Generic;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Rule for automatically assigning addresses to assets
    /// Uses filters to match assets and providers to generate addresses
    /// </summary>
    [Serializable]
    public class AddressRule
    {
        [Header("Rule Configuration")]
        [Tooltip("Name of this rule for identification")]
        [SerializeField] private string _ruleName = "New Address Rule";

        [Tooltip("Enable/disable this rule")]
        [SerializeField] private bool _enabled = true;

        [Tooltip("Description of what this rule does")]
        [SerializeField] [TextArea(2, 3)] private string _description;

        [Header("Target Group")]
        [Tooltip("Target addressable group (leave empty for default group)")]
        [SerializeField] private string _targetGroupName;

        [Header("Filters (AND logic)")]
        [Tooltip("Asset filters - all must match for rule to apply")]
        [SerializeField] private List<AssetFilterBase> _filters = new List<AssetFilterBase>();

        [Header("Address Provider")]
        [Tooltip("Provider to generate address from matched assets")]
        [SerializeField] private AddressProviderBase _addressProvider;

        [Header("Advanced")]
        [Tooltip("Priority - higher priority rules are applied first")]
        [SerializeField] private int _priority = 0;

        [Tooltip("Skip assets that already have addresses assigned")]
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
        /// Target group name
        /// </summary>
        public string TargetGroupName
        {
            get => _targetGroupName;
            set => _targetGroupName = value;
        }

        /// <summary>
        /// Asset filters (AND logic)
        /// </summary>
        public List<AssetFilterBase> Filters => _filters;

        /// <summary>
        /// Address provider
        /// </summary>
        public AddressProviderBase AddressProvider
        {
            get => _addressProvider;
            set => _addressProvider = value;
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
        /// Skip assets with existing addresses
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
            // filters gets unchecked, and this rule runs against an unbounded project-wide
            // scan (see GetAllAssetPaths()). Require at least one ENABLED filter to actually
            // constrain the match; a rule with every filter disabled has no active constraint
            // left and must not match anything (HANDOFF_TO_SESSION_B.md E-PAIR-1). Do not
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
        /// Generate address for an asset
        /// </summary>
        public string GenerateAddress(string assetPath)
        {
            if (_addressProvider == null)
            {
                Debug.LogWarning($"[AddressRule] {_ruleName}: No address provider assigned");
                return null;
            }

            return _addressProvider.Provide(assetPath);
        }

        /// <summary>
        /// Get or create target group
        /// </summary>
        public AddressableAssetGroup GetOrCreateTargetGroup(AddressableAssetSettings settings)
        {
            if (settings == null)
            {
                Debug.LogError("[AddressRule] AddressableAssetSettings is null");
                return null;
            }

            // Use default group if no target specified
            if (string.IsNullOrEmpty(_targetGroupName))
            {
                return settings.DefaultGroup;
            }

            // Find existing group
            var group = settings.FindGroup(_targetGroupName);
            if (group != null)
                return group;

            // Create new group.
            // CreateGroup's schemasToCopy/types parameters are opt-in: passing null for
            // schemasToCopy with no types (as this call used to) creates a group with an EMPTY
            // schema set. Without a BundledAssetGroupSchema that group contributes nothing to a
            // content build - silently (see Assets/AddressableAssetsData/AssetGroups/Scene.asset,
            // which shipped exactly this way: two scene entries under
            // m_SchemaSet: m_Schemas: []).
            // AddressableGroupSchemaUtility.EnsureSchemas attaches the schemas a normal group
            // needs right after creation; see its doc comment for why DefaultGroup's own schemas
            // are preferred over fresh default-valued ones.
            Debug.Log($"[AddressRule] Creating new group: {_targetGroupName}");
            var newGroup = settings.CreateGroup(_targetGroupName, false, false, true, null);
            AddressableGroupSchemaUtility.EnsureSchemas(newGroup, settings);
            return newGroup;
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

            if (_addressProvider == null)
            {
                errors.Add($"Rule '{_ruleName}': Address provider is not assigned");
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

            _addressProvider?.Setup();
        }
    }
}
