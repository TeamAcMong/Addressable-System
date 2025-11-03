using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets by their path using string matching or regex
    /// </summary>
    [CreateAssetMenu(fileName = "PathFilter", menuName = "Addressable Manager/Filters/Path Filter")]
    public class PathFilter : AssetFilterBase
    {
        public enum PathMatchMode
        {
            Contains,       // Path contains the pattern
            StartsWith,     // Path starts with the pattern
            EndsWith,       // Path ends with the pattern
            Exact,          // Path exactly matches the pattern
            Regex           // Path matches regex pattern
        }

        [Header("Path Filter Settings")]
        [Tooltip("Pattern to match against asset paths")]
        [SerializeField] private string _pattern = "Assets/";

        [Tooltip("Match mode")]
        [SerializeField] private PathMatchMode _matchMode = PathMatchMode.Contains;

        [Tooltip("Case-sensitive matching")]
        [SerializeField] private bool _caseSensitive = false;

        private Regex _cachedRegex;
        private string _cachedPattern;

        /// <summary>
        /// Pattern to match
        /// </summary>
        public string Pattern
        {
            get => _pattern;
            set
            {
                _pattern = value;
                _cachedRegex = null; // Invalidate cache
            }
        }

        /// <summary>
        /// Match mode
        /// </summary>
        public PathMatchMode MatchMode
        {
            get => _matchMode;
            set
            {
                _matchMode = value;
                _cachedRegex = null; // Invalidate cache
            }
        }

        /// <summary>
        /// Case sensitive matching
        /// </summary>
        public bool CaseSensitive
        {
            get => _caseSensitive;
            set
            {
                _caseSensitive = value;
                _cachedRegex = null; // Invalidate cache
            }
        }

        public override void Setup()
        {
            base.Setup();

            // Pre-compile regex if needed
            if (_matchMode == PathMatchMode.Regex && !string.IsNullOrEmpty(_pattern))
            {
                try
                {
                    var options = RegexOptions.Compiled;
                    if (!_caseSensitive)
                        options |= RegexOptions.IgnoreCase;

                    _cachedRegex = new Regex(_pattern, options);
                    _cachedPattern = _pattern;
                }
                catch (ArgumentException ex)
                {
                    Debug.LogError($"[PathFilter] Invalid regex pattern '{_pattern}': {ex.Message}");
                    _cachedRegex = null;
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(_pattern))
                return false;

            var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            switch (_matchMode)
            {
                case PathMatchMode.Contains:
                    return assetPath.IndexOf(_pattern, comparison) >= 0;

                case PathMatchMode.StartsWith:
                    return assetPath.StartsWith(_pattern, comparison);

                case PathMatchMode.EndsWith:
                    return assetPath.EndsWith(_pattern, comparison);

                case PathMatchMode.Exact:
                    return assetPath.Equals(_pattern, comparison);

                case PathMatchMode.Regex:
                    // Recompile regex if pattern changed
                    if (_cachedRegex == null || _cachedPattern != _pattern)
                    {
                        Setup();
                    }

                    if (_cachedRegex == null)
                        return false;

                    return _cachedRegex.IsMatch(assetPath);

                default:
                    return false;
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            string modeStr = _matchMode.ToString();
            return $"Path {modeStr}: {_pattern}";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _cachedRegex = null;
        }
    }
}
