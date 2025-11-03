using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets by their existing addressable address
    /// Useful for applying additional labels or versions to already-addressed assets
    /// </summary>
    [CreateAssetMenu(fileName = "AddressFilter", menuName = "Addressable Manager/Filters/Address Filter")]
    public class AddressFilter : AssetFilterBase
    {
        public enum AddressMatchMode
        {
            HasAddress,     // Asset has any address assigned
            NoAddress,      // Asset has no address assigned
            Contains,       // Address contains pattern
            StartsWith,     // Address starts with pattern
            EndsWith,       // Address ends with pattern
            Exact,          // Address exactly matches pattern
            Regex           // Address matches regex pattern
        }

        [Header("Address Filter Settings")]
        [Tooltip("Pattern to match against addresses")]
        [SerializeField] private string _pattern = "";

        [Tooltip("Match mode")]
        [SerializeField] private AddressMatchMode _matchMode = AddressMatchMode.HasAddress;

        [Tooltip("Case-sensitive matching")]
        [SerializeField] private bool _caseSensitive = false;

        private Regex _cachedRegex;
        private AddressableAssetSettings _settings;
        private Dictionary<string, string> _addressCache = new Dictionary<string, string>();

        public override void Setup()
        {
            base.Setup();

            _settings = AddressableAssetSettingsDefaultObject.Settings;

            // Pre-compile regex if needed
            if (_matchMode == AddressMatchMode.Regex && !string.IsNullOrEmpty(_pattern))
            {
                try
                {
                    var options = RegexOptions.Compiled;
                    if (!_caseSensitive)
                        options |= RegexOptions.IgnoreCase;

                    _cachedRegex = new Regex(_pattern, options);
                }
                catch (ArgumentException ex)
                {
                    Debug.LogError($"[AddressFilter] Invalid regex pattern '{_pattern}': {ex.Message}");
                    _cachedRegex = null;
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (_settings == null)
            {
                Setup();
                if (_settings == null)
                    return false;
            }

            // Get address from cache or lookup
            if (!_addressCache.TryGetValue(assetPath, out string address))
            {
                var guid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);
                address = entry?.address ?? string.Empty;
                _addressCache[assetPath] = address;
            }

            bool hasAddress = !string.IsNullOrEmpty(address);

            switch (_matchMode)
            {
                case AddressMatchMode.HasAddress:
                    return hasAddress;

                case AddressMatchMode.NoAddress:
                    return !hasAddress;

                case AddressMatchMode.Contains:
                    if (!hasAddress) return false;
                    var comp = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    return address.IndexOf(_pattern, comp) >= 0;

                case AddressMatchMode.StartsWith:
                    if (!hasAddress) return false;
                    comp = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    return address.StartsWith(_pattern, comp);

                case AddressMatchMode.EndsWith:
                    if (!hasAddress) return false;
                    comp = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    return address.EndsWith(_pattern, comp);

                case AddressMatchMode.Exact:
                    if (!hasAddress) return false;
                    comp = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    return address.Equals(_pattern, comp);

                case AddressMatchMode.Regex:
                    if (!hasAddress || _cachedRegex == null) return false;
                    return _cachedRegex.IsMatch(address);

                default:
                    return false;
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Address {_matchMode}: {_pattern}";
        }

        private void OnValidate()
        {
            _cachedRegex = null;
            _addressCache.Clear();
        }
    }
}
