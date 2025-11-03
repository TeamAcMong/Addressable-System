using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets by file extension
    /// </summary>
    [CreateAssetMenu(fileName = "ExtensionFilter", menuName = "Addressable Manager/Filters/Extension Filter")]
    public class ExtensionFilter : AssetFilterBase
    {
        [Header("Extension Filter Settings")]
        [Tooltip("File extensions to match (with or without dot, comma-separated for multiple)")]
        [SerializeField] private string _extensions = ".prefab";

        [Tooltip("Match if asset has ANY of the extensions (OR logic)")]
        [SerializeField] private bool _matchAny = true;

        private HashSet<string> _normalizedExtensions;

        /// <summary>
        /// Extensions to match
        /// </summary>
        public string Extensions
        {
            get => _extensions;
            set
            {
                _extensions = value;
                _normalizedExtensions = null; // Invalidate cache
            }
        }

        /// <summary>
        /// Match any extension (OR logic) vs match all (AND logic)
        /// </summary>
        public bool MatchAny
        {
            get => _matchAny;
            set => _matchAny = value;
        }

        public override void Setup()
        {
            base.Setup();

            // Parse and normalize extensions
            _normalizedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(_extensions))
            {
                var parts = _extensions.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var normalized = part.Trim();
                    // Ensure it starts with a dot
                    if (!normalized.StartsWith("."))
                        normalized = "." + normalized;

                    _normalizedExtensions.Add(normalized.ToLower());
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (_normalizedExtensions == null || _normalizedExtensions.Count == 0)
            {
                Setup();
            }

            if (_normalizedExtensions == null || _normalizedExtensions.Count == 0)
                return false;

            string ext = Path.GetExtension(assetPath).ToLower();

            if (_matchAny)
            {
                // OR logic - match if asset has any of the extensions
                return _normalizedExtensions.Contains(ext);
            }
            else
            {
                // AND logic - for single extension this is the same as OR
                // For multiple, it would need to match all (which doesn't make sense for file extensions)
                // So we just check if it's in the set
                return _normalizedExtensions.Contains(ext);
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            if (_normalizedExtensions != null && _normalizedExtensions.Count > 0)
            {
                return $"Extension: {string.Join(", ", _normalizedExtensions)}";
            }

            return $"Extension: {_extensions}";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _normalizedExtensions = null;
        }
    }
}
