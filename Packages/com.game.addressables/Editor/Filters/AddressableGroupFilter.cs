using System.Collections.Generic;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets by which addressable group they belong to
    /// </summary>
    [CreateAssetMenu(fileName = "AddressableGroupFilter", menuName = "Addressable Manager/Filters/Addressable Group Filter")]
    public class AddressableGroupFilter : AssetFilterBase
    {
        [Header("Group Filter Settings")]
        [Tooltip("Target group names (comma-separated for multiple)")]
        [SerializeField] private string _groupNames = "Default Local Group";

        [Tooltip("Match assets in ANY of the groups (OR logic)")]
        [SerializeField] private bool _matchAny = true;

        [Tooltip("Include assets not in any group")]
        [SerializeField] private bool _includeUngrouped = false;

        private AddressableAssetSettings _settings;
        private HashSet<string> _targetGroupNames;
        private Dictionary<string, string> _assetGroupCache = new Dictionary<string, string>();

        public override void Setup()
        {
            base.Setup();

            _settings = AddressableAssetSettingsDefaultObject.Settings;

            // Parse group names
            _targetGroupNames = new HashSet<string>();
            if (!string.IsNullOrEmpty(_groupNames))
            {
                var parts = _groupNames.Split(new[] { ',', ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    _targetGroupNames.Add(part.Trim());
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (_settings == null || _targetGroupNames == null)
            {
                Setup();
                if (_settings == null)
                    return false;
            }

            // Get group name from cache or lookup
            if (!_assetGroupCache.TryGetValue(assetPath, out string groupName))
            {
                var guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);
                groupName = entry?.parentGroup?.Name ?? string.Empty;
                _assetGroupCache[assetPath] = groupName;
            }

            bool isUngrouped = string.IsNullOrEmpty(groupName);

            if (isUngrouped)
            {
                return _includeUngrouped;
            }

            if (_matchAny)
            {
                // OR logic - match if in any target group
                return _targetGroupNames.Contains(groupName);
            }
            else
            {
                // For groups, AND logic doesn't make sense (asset can only be in one group)
                // So we just check membership
                return _targetGroupNames.Contains(groupName);
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Group: {_groupNames}";
        }

        private void OnValidate()
        {
            _targetGroupNames = null;
            _assetGroupCache.Clear();
        }
    }
}
