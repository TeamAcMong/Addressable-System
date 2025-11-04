using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Main container for all addressable layout rules
    /// Stores address, label, and version rules for automatic asset configuration
    /// </summary>
    [CreateAssetMenu(fileName = "LayoutRuleData", menuName = "Addressable Manager/Layout Rule Data", order = 1)]
    public class LayoutRuleData : ScriptableObject
    {
        [Header("Configuration")]
        [Tooltip("Unique identifier for this rule data")]
        [SerializeField] private string _ruleDataId = System.Guid.NewGuid().ToString();

        [Tooltip("Description of this rule set")]
        [SerializeField] [TextArea(2, 4)] private string _description;

        [Header("Rules")]
        [Tooltip("Rules for assigning addresses to assets")]
        [SerializeField] private List<AddressRule> _addressRules = new List<AddressRule>();

        [Tooltip("Rules for assigning labels to assets")]
        [SerializeField] private List<LabelRule> _labelRules = new List<LabelRule>();

        [Tooltip("Rules for assigning versions to assets")]
        [SerializeField] private List<VersionRule> _versionRules = new List<VersionRule>();

        [Header("Settings")]
        [Tooltip("Auto-apply rules when assets are imported")]
        [SerializeField] private bool _autoApplyOnImport = true;

        [Tooltip("Auto-apply rules when this asset is modified")]
        [SerializeField] private bool _autoApplyOnModified = true;

        [Tooltip("Show detailed logs when applying rules")]
        [SerializeField] private bool _verboseLogging = false;

        [Header("Version Filtering")]
        [Tooltip("Version expression for filtering assets (e.g., '[1.0.0,2.0.0)')\nLeave empty to include all versions")]
        [SerializeField] private string _versionExpression = "";

        [Tooltip("Exclude assets without version labels when expression is set")]
        [SerializeField] private bool _excludeUnversioned = false;

        /// <summary>
        /// Unique identifier for this rule data
        /// </summary>
        public string RuleDataId => _ruleDataId;

        /// <summary>
        /// Description of this rule set
        /// </summary>
        public string Description
        {
            get => _description;
            set => _description = value;
        }

        /// <summary>
        /// All address rules
        /// </summary>
        public List<AddressRule> AddressRules => _addressRules;

        /// <summary>
        /// All label rules
        /// </summary>
        public List<LabelRule> LabelRules => _labelRules;

        /// <summary>
        /// All version rules
        /// </summary>
        public List<VersionRule> VersionRules => _versionRules;

        /// <summary>
        /// Auto-apply on asset import
        /// </summary>
        public bool AutoApplyOnImport
        {
            get => _autoApplyOnImport;
            set => _autoApplyOnImport = value;
        }

        /// <summary>
        /// Auto-apply when rule data is modified
        /// </summary>
        public bool AutoApplyOnModified
        {
            get => _autoApplyOnModified;
            set => _autoApplyOnModified = value;
        }

        /// <summary>
        /// Enable verbose logging
        /// </summary>
        public bool VerboseLogging
        {
            get => _verboseLogging;
            set => _verboseLogging = value;
        }

        /// <summary>
        /// Version expression for filtering assets
        /// </summary>
        public string VersionExpression
        {
            get => _versionExpression;
            set => _versionExpression = value;
        }

        /// <summary>
        /// Exclude unversioned assets when version expression is set
        /// </summary>
        public bool ExcludeUnversioned
        {
            get => _excludeUnversioned;
            set => _excludeUnversioned = value;
        }

        /// <summary>
        /// Get total number of rules
        /// </summary>
        public int TotalRuleCount => _addressRules.Count + _labelRules.Count + _versionRules.Count;

        /// <summary>
        /// Add a new address rule
        /// </summary>
        public void AddAddressRule(AddressRule rule)
        {
            if (rule == null)
            {
                Debug.LogWarning("[LayoutRuleData] Cannot add null address rule");
                return;
            }

            _addressRules.Add(rule);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Remove an address rule
        /// </summary>
        public bool RemoveAddressRule(AddressRule rule)
        {
            bool removed = _addressRules.Remove(rule);
            if (removed)
            {
                EditorUtility.SetDirty(this);
            }
            return removed;
        }

        /// <summary>
        /// Add a new label rule
        /// </summary>
        public void AddLabelRule(LabelRule rule)
        {
            if (rule == null)
            {
                Debug.LogWarning("[LayoutRuleData] Cannot add null label rule");
                return;
            }

            _labelRules.Add(rule);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Remove a label rule
        /// </summary>
        public bool RemoveLabelRule(LabelRule rule)
        {
            bool removed = _labelRules.Remove(rule);
            if (removed)
            {
                EditorUtility.SetDirty(this);
            }
            return removed;
        }

        /// <summary>
        /// Add a new version rule
        /// </summary>
        public void AddVersionRule(VersionRule rule)
        {
            if (rule == null)
            {
                Debug.LogWarning("[LayoutRuleData] Cannot add null version rule");
                return;
            }

            _versionRules.Add(rule);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Remove a version rule
        /// </summary>
        public bool RemoveVersionRule(VersionRule rule)
        {
            bool removed = _versionRules.Remove(rule);
            if (removed)
            {
                EditorUtility.SetDirty(this);
            }
            return removed;
        }

        /// <summary>
        /// Clear all rules
        /// </summary>
        public void ClearAllRules()
        {
            _addressRules.Clear();
            _labelRules.Clear();
            _versionRules.Clear();
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Validate all rules
        /// </summary>
        public (bool isValid, List<string> errors) Validate()
        {
            var errors = new List<string>();

            // Validate address rules
            for (int i = 0; i < _addressRules.Count; i++)
            {
                var rule = _addressRules[i];
                if (rule == null)
                {
                    errors.Add($"Address rule at index {i} is null");
                    continue;
                }

                var (isValid, ruleErrors) = rule.Validate();
                if (!isValid)
                {
                    errors.AddRange(ruleErrors);
                }
            }

            // Validate label rules
            for (int i = 0; i < _labelRules.Count; i++)
            {
                var rule = _labelRules[i];
                if (rule == null)
                {
                    errors.Add($"Label rule at index {i} is null");
                    continue;
                }

                var (isValid, ruleErrors) = rule.Validate();
                if (!isValid)
                {
                    errors.AddRange(ruleErrors);
                }
            }

            // Validate version rules
            for (int i = 0; i < _versionRules.Count; i++)
            {
                var rule = _versionRules[i];
                if (rule == null)
                {
                    errors.Add($"Version rule at index {i} is null");
                    continue;
                }

                var (isValid, ruleErrors) = rule.Validate();
                if (!isValid)
                {
                    errors.AddRange(ruleErrors);
                }
            }

            return (errors.Count == 0, errors);
        }

        private void OnValidate()
        {
            // Ensure rule data ID is not empty
            if (string.IsNullOrEmpty(_ruleDataId))
            {
                _ruleDataId = System.Guid.NewGuid().ToString();
            }
        }
    }
}
