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
            Regex,          // Path matches regex pattern

            // Added after Contains/StartsWith/EndsWith/Exact/Regex existed and serialized asset
            // data already stored those as enum indices 0-4 - MUST stay last so old assets don't
            // silently reinterpret their saved _matchMode as a different mode
            // (HANDOFF_TO_SESSION_B.md E-PAIR-2).
            Glob            // Path matches a glob pattern (* / ** / ?), e.g. "Assets/UI/**/*.png"
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

            // Pre-compile regex if needed. Regex mode compiles the pattern AS-IS (it is already
            // a regex). Glob mode translates the "*" / "**" / "?" pattern into an equivalent
            // regex first, then compiles that - this is what lets 32 documented examples that
            // use "**" (e.g. "Assets/UI/**/*.png") work, and stops "**" from throwing on every
            // asset scanned when someone follows the docs but leaves match mode on Regex
            // (HANDOFF_TO_SESSION_B.md E-PAIR-2). Regex mode itself is unchanged - it already
            // compiles once and caches correctly for valid patterns, that path is not the bug.
            if ((_matchMode == PathMatchMode.Regex || _matchMode == PathMatchMode.Glob) && !string.IsNullOrEmpty(_pattern))
            {
                try
                {
                    var options = RegexOptions.Compiled;
                    if (!_caseSensitive)
                        options |= RegexOptions.IgnoreCase;

                    string regexPattern = _matchMode == PathMatchMode.Glob
                        ? GlobToRegexPattern(_pattern)
                        : _pattern;

                    _cachedRegex = new Regex(regexPattern, options);
                    _cachedPattern = _pattern;
                }
                catch (ArgumentException ex)
                {
                    Debug.LogError($"[PathFilter] Invalid {_matchMode} pattern '{_pattern}': {ex.Message}");
                    _cachedRegex = null;
                }
            }
        }

        /// <summary>
        /// Translate a "*" / "**" / "?" glob pattern into an equivalent anchored regex pattern.
        /// "**" (optionally followed by "/") matches zero or more path segments, including none -
        /// so "Assets/UI/**/*.png" matches both "Assets/UI/icon.png" and
        /// "Assets/UI/Nested/icon.png". A trailing "**" with no following "/" matches the rest of
        /// the path outright (e.g. "Assets/Mobile/**"). "*" matches within a single path segment
        /// (never crosses "/"), "?" matches exactly one such character.
        /// </summary>
        private static string GlobToRegexPattern(string glob)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('^');

            int i = 0;
            int len = glob.Length;
            while (i < len)
            {
                // "**/" -> zero or more whole path segments (the "/" is absorbed so a zero-segment
                // match doesn't leave a stray "//").
                if (i + 3 <= len && glob[i] == '*' && glob[i + 1] == '*' && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 3;
                    continue;
                }

                // Trailing "**" (end of pattern, no following "/") -> match anything, slashes included.
                if (i + 2 <= len && glob[i] == '*' && glob[i + 1] == '*' && (i + 2 == len || glob[i + 2] != '/'))
                {
                    sb.Append(".*");
                    i += 2;
                    continue;
                }

                char c = glob[i];
                switch (c)
                {
                    case '*':
                        sb.Append("[^/]*");
                        break;
                    case '?':
                        sb.Append("[^/]");
                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
                i++;
            }

            sb.Append('$');
            return sb.ToString();
        }

        /// <summary>
        /// Whether this filter has already complained about a wildcard pattern in a literal mode.
        /// </summary>
        [NonSerialized] private bool _warnedAboutGlobPattern;

        /// <summary>
        /// Says something when the pattern looks like a glob but the mode will not treat it as one.
        /// </summary>
        /// <remarks>
        /// <see cref="PathMatchMode.Contains"/> is the default, and in that mode a pattern like
        /// "Assets/UI/**/*.png" is compared as a LITERAL substring - so it matches nothing, ever, and
        /// the rule silently applies to zero assets while the run reports success. The package's own
        /// Quick Start walked users straight into this: it told them to type exactly that pattern and
        /// never mentioned Match Mode.
        ///
        /// The mode is not switched automatically. A serialized asset's saved mode is the user's
        /// decision and a filter that quietly redefined its own matching would be a worse bug than the
        /// one it fixes. Warning once per filter instance is enough to turn a silent no-match into
        /// something findable, without spamming a project-wide scan of thousands of assets.
        /// </remarks>
        private void WarnIfGlobPatternInLiteralMode()
        {
            if (_warnedAboutGlobPattern) return;
            if (_matchMode == PathMatchMode.Glob || _matchMode == PathMatchMode.Regex) return;
            if (_pattern.IndexOf('*') < 0 && _pattern.IndexOf('?') < 0) return;

            _warnedAboutGlobPattern = true;
            Debug.LogWarning(
                $"[PathFilter] Pattern \"{_pattern}\" contains wildcards but Match Mode is {_matchMode}, " +
                "which compares the pattern literally - so this filter will match nothing. Set Match Mode " +
                "to Glob for * / ** / ? patterns.");
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(_pattern))
                return false;

            var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            WarnIfGlobPatternInLiteralMode();

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
                case PathMatchMode.Glob:
                    // Recompile (regex, or glob-translated-to-regex) if pattern changed
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
