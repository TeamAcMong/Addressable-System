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
        [Tooltip("Target addressable group (leave empty for default group). '/' and '\\' are not " +
                 "valid in a group name and are normalized to '-', the same way Unity does it.")]
        [SerializeField] private string _targetGroupName;

        [Tooltip("Group template describing how this rule's group must be configured when it has to " +
                 "be created. Leave empty to inherit the DefaultGroup's schemas (the previous behaviour).")]
        [SerializeField] private AddressableAssetGroupTemplate _targetGroupTemplate;

        [Header("Filters (AND logic)")]
        [Tooltip("Asset filters - all must match for rule to apply")]
        [SerializeField] private List<AssetFilterBase> _filters = new List<AssetFilterBase>();

        [Header("Address Provider")]
        [Tooltip("Provider to generate address from matched assets")]
        [SerializeField] private AddressProviderBase _addressProvider;

        [Header("Advanced")]
        [Tooltip("Priority - higher priority rules are applied first")]
        [SerializeField] private int _priority = 0;

        [Tooltip("Skip assets that already have addresses assigned. Note this also stops the rule from " +
                 "relocating those entries into its target group.")]
        [SerializeField] private bool _skipExisting = false;

        [Tooltip("Allow this rule to move an already-addressable asset out of whatever group it is in " +
                 "and into this rule's target group. Turn OFF to protect hand-configured groups.")]
        [SerializeField] private bool _allowGroupMove = true;

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
        /// Group template applied when this rule has to CREATE its target group. Null means "copy the
        /// DefaultGroup's schemas", which is what the rule did unconditionally before.
        /// </summary>
        public AddressableAssetGroupTemplate TargetGroupTemplate
        {
            get => _targetGroupTemplate;
            set => _targetGroupTemplate = value;
        }

        /// <summary>
        /// Applies Unity's own group-name rules to <paramref name="rawName"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="AddressableAssetSettings.CreateGroup"/> pushes the requested name through
        /// FindUniqueGroupName, which rewrites '/' and '\' to '-' before creating the asset. Looking
        /// the group up afterwards by the RAW name therefore never matches what was created, and
        /// because <see cref="GetOrCreateTargetGroup"/> runs once per matched ASSET, every asset took
        /// the create branch again — and FindUniqueGroupName appends an incrementing suffix when the
        /// cleaned name is taken. A rule targeting "Icons/Small" over N assets produced
        /// "Icons-Small", "Icons-Small1", "Icons-Small2" ... : N groups, one entry each, N bundles,
        /// with the counters reporting complete success.
        ///
        /// Normalizing on THIS side keeps lookup and creation talking about the same string.
        /// </remarks>
        public static string NormalizeGroupName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            return rawName.Replace('/', '-').Replace('\\', '-');
        }

        /// <summary>
        /// Whether this rule may relocate an entry that already lives in another group.
        /// </summary>
        /// <remarks>
        /// Defaults to TRUE, which is what the rule did unconditionally before this flag existed - a
        /// rule-driven layout relies on it, and flipping the default would silently stop reorganising
        /// projects that depend on the old behaviour. It is exposed because the move is destructive in
        /// the other direction: MoveEntry also resets ReadOnly, and nothing distinguishes an entry a
        /// rule created from one a human placed and configured by hand.
        /// </remarks>
        public bool AllowGroupMove
        {
            get => _allowGroupMove;
            set => _allowGroupMove = value;
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

            // Look up by the name CreateGroup would actually produce, not the raw one - see
            // NormalizeGroupName for what a raw '/' used to cost.
            string groupName = NormalizeGroupName(_targetGroupName);

            // Find existing group
            var group = settings.FindGroup(groupName);
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
            //
            // A group template, when one is assigned, is the only place a rule author can say what
            // the group must look like. Without it the group inherits DefaultGroup's schema VALUES
            // (AddressableGroupSchemaUtility copies them via Object.Instantiate), which in a stock
            // project means PackTogether + Local paths - so a remote, label-split group that has to
            // be re-created silently comes back local and packed-together, with the entry count
            // unchanged and nothing logged.
            //
            // Order matters and is not interchangeable: ApplyToAddressableAssetGroup iterates
            // group.Schemas and applies a Preset to each, so it can only overwrite schemas that
            // ALREADY exist - it cannot attach any. The types must therefore be passed to
            // CreateGroup first. This is exactly what Unity's own Groups window does
            // (AddressableAssetsSettingsGroupTreeView: CreateGroup(..., groupTemplate.GetTypes())
            // then ApplyToAddressableAssetGroup). EnsureSchemas stays as the safety net, because a
            // template that happens to carry no BundledAssetGroupSchema would otherwise leave the
            // group unbuildable - the very failure this call site was hardened against.
            AddressableAssetGroup newGroup;
            if (_targetGroupTemplate != null)
            {
                Debug.Log($"[AddressRule] Creating new group '{groupName}' from template " +
                          $"'{_targetGroupTemplate.name}' (rule '{_ruleName}')");
                newGroup = settings.CreateGroup(groupName, false, false, true, null,
                                                _targetGroupTemplate.GetTypes());
                _targetGroupTemplate.ApplyToAddressableAssetGroup(newGroup);
                AddressableGroupSchemaUtility.EnsureSchemas(newGroup, settings);
            }
            else
            {
                // No template: the group's configuration is inherited, not declared. Say so loudly -
                // this is the branch that shipped 2803 remote assets into a player build without a
                // single warning, and the entry counts looked perfect afterwards.
                Debug.LogWarning(
                    $"[AddressRule] Rule '{_ruleName}' had to CREATE group '{groupName}', and no group " +
                    "template is assigned - its BundleMode/BuildPath/LoadPath/StaticContent are being " +
                    "copied from the DefaultGroup, not from anything this rule declares. If this group " +
                    "is meant to be remote or label-split, assign a Group Template to the rule and " +
                    "re-run; entry counts will look correct either way.");
                newGroup = settings.CreateGroup(groupName, false, false, true, null);
                AddressableGroupSchemaUtility.EnsureSchemas(newGroup, settings);
            }

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
