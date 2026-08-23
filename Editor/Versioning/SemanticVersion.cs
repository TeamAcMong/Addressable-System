using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AddressableManager.Editor.Versioning
{
    /// <summary>
    /// Represents a semantic version (Major.Minor.Patch-PreRelease+BuildMetadata)
    /// Follows semver 2.0.0 specification
    /// </summary>
    [Serializable]
    public class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        [SerializeField] private int _major;
        [SerializeField] private int _minor;
        [SerializeField] private int _patch;
        [SerializeField] private string _preRelease;
        [SerializeField] private string _buildMetadata;

        private static readonly Regex VersionRegex = new Regex(
            @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)" +
            @"(?:-(?<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)" +
            @"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
            @"(?:\+(?<buildmetadata>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
            RegexOptions.Compiled);

        public int Major => _major;
        public int Minor => _minor;
        public int Patch => _patch;
        public string PreRelease => _preRelease;
        public string BuildMetadata => _buildMetadata;

        public bool IsPreRelease => !string.IsNullOrEmpty(_preRelease);

        public SemanticVersion(int major, int minor, int patch, string preRelease = null, string buildMetadata = null)
        {
            if (major < 0) throw new ArgumentException("Major version must be >= 0", nameof(major));
            if (minor < 0) throw new ArgumentException("Minor version must be >= 0", nameof(minor));
            if (patch < 0) throw new ArgumentException("Patch version must be >= 0", nameof(patch));

            _major = major;
            _minor = minor;
            _patch = patch;
            _preRelease = preRelease ?? string.Empty;
            _buildMetadata = buildMetadata ?? string.Empty;
        }

        /// <summary>
        /// Parse a semantic version string
        /// </summary>
        public static bool TryParse(string versionString, out SemanticVersion version)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(versionString))
                return false;

            var match = VersionRegex.Match(versionString.Trim());
            if (!match.Success)
                return false;

            int major = int.Parse(match.Groups["major"].Value);
            int minor = int.Parse(match.Groups["minor"].Value);
            int patch = int.Parse(match.Groups["patch"].Value);
            string preRelease = match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null;
            string buildMetadata = match.Groups["buildmetadata"].Success ? match.Groups["buildmetadata"].Value : null;

            version = new SemanticVersion(major, minor, patch, preRelease, buildMetadata);
            return true;
        }

        /// <summary>
        /// Parse a version string, accepting the near-semver shapes real version sources emit.
        /// </summary>
        /// <remarks>
        /// <see cref="TryParse"/> is strict semver, and strictness in the wrong place made the version
        /// filter useless: the shipped providers write labels that strict semver rejects, and a
        /// rejected label was treated as "unversioned", which with the default
        /// <c>ExcludeUnversioned = false</c> means <b>matches everything</b>. A filter advertised as
        /// scoping a run to a version range quietly scoped it to the whole project.
        ///
        /// Two shapes are genuinely ordered versions that merely fail the grammar, and both are
        /// normalised here:
        /// <list type="bullet">
        /// <item><c>1.0</c> - Unity's stock <c>PlayerSettings.bundleVersion</c>, so this is the
        /// DEFAULT output of BuildNumberVersionProvider. Read as <c>1.0.0</c>.</item>
        /// <item><c>1.0.0.123</c> - the four-component "version.build" form
        /// BuildNumberVersionProvider writes in Combined mode. The fourth component is build metadata
        /// in semver terms, so it is read as <c>1.0.0+123</c>: it does not affect ordering, which is
        /// the correct semantics for a build number.</item>
        /// </list>
        ///
        /// What is deliberately NOT accepted: a git hash (<c>a1b2c3d</c>) or a date stamp
        /// (<c>20260822</c>). Those are identifiers, not ordered versions, and inventing an ordering
        /// for them would make a range filter silently answer a question it cannot answer. The caller
        /// is expected to report them rather than guess - see
        /// <c>LayoutRuleProcessor.PassesVersionFilter</c>.
        /// </remarks>
        public static bool TryParseLenient(string versionString, out SemanticVersion version)
        {
            if (TryParse(versionString, out version))
                return true;

            version = null;
            if (string.IsNullOrWhiteSpace(versionString))
                return false;

            string trimmed = versionString.Trim();

            // "1.0" -> "1.0.0"
            var twoPart = Regex.Match(trimmed, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)$");
            if (twoPart.Success)
                return TryParse(twoPart.Groups[1].Value + "." + twoPart.Groups[2].Value + ".0", out version);

            // "1.0.0.123" -> "1.0.0+123"
            var fourPart = Regex.Match(
                trimmed, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)\.([0-9a-zA-Z-]+)$");
            if (fourPart.Success)
                return TryParse(
                    $"{fourPart.Groups[1].Value}.{fourPart.Groups[2].Value}.{fourPart.Groups[3].Value}" +
                    $"+{fourPart.Groups[4].Value}", out version);

            return false;
        }

        /// <summary>
        /// Parse a semantic version string (throws on failure)
        /// </summary>
        public static SemanticVersion Parse(string versionString)
        {
            if (!TryParse(versionString, out var version))
            {
                throw new FormatException($"Invalid semantic version: {versionString}");
            }
            return version;
        }

        public override string ToString()
        {
            string version = $"{_major}.{_minor}.{_patch}";

            if (!string.IsNullOrEmpty(_preRelease))
                version += $"-{_preRelease}";

            if (!string.IsNullOrEmpty(_buildMetadata))
                version += $"+{_buildMetadata}";

            return version;
        }

        #region Comparison

        public int CompareTo(SemanticVersion other)
        {
            if (other == null) return 1;

            // Compare major.minor.patch
            int result = _major.CompareTo(other._major);
            if (result != 0) return result;

            result = _minor.CompareTo(other._minor);
            if (result != 0) return result;

            result = _patch.CompareTo(other._patch);
            if (result != 0) return result;

            // Pre-release versions have lower precedence than normal versions
            if (string.IsNullOrEmpty(_preRelease) && !string.IsNullOrEmpty(other._preRelease))
                return 1; // This version is greater (no pre-release)

            if (!string.IsNullOrEmpty(_preRelease) && string.IsNullOrEmpty(other._preRelease))
                return -1; // This version is less (has pre-release)

            // Both have pre-release or both don't
            if (!string.IsNullOrEmpty(_preRelease) && !string.IsNullOrEmpty(other._preRelease))
            {
                return ComparePreRelease(_preRelease, other._preRelease);
            }

            return 0;
        }

        private static int ComparePreRelease(string a, string b)
        {
            var aParts = a.Split('.');
            var bParts = b.Split('.');

            int minLength = Math.Min(aParts.Length, bParts.Length);

            for (int i = 0; i < minLength; i++)
            {
                bool aIsNumeric = int.TryParse(aParts[i], out int aNum);
                bool bIsNumeric = int.TryParse(bParts[i], out int bNum);

                if (aIsNumeric && bIsNumeric)
                {
                    int result = aNum.CompareTo(bNum);
                    if (result != 0) return result;
                }
                else if (aIsNumeric)
                {
                    return -1; // Numeric identifiers have lower precedence
                }
                else if (bIsNumeric)
                {
                    return 1;
                }
                else
                {
                    int result = string.CompareOrdinal(aParts[i], bParts[i]);
                    if (result != 0) return result;
                }
            }

            return aParts.Length.CompareTo(bParts.Length);
        }

        public bool Equals(SemanticVersion other)
        {
            if (other == null) return false;
            return _major == other._major &&
                   _minor == other._minor &&
                   _patch == other._patch &&
                   _preRelease == other._preRelease;
            // Build metadata is NOT included in equality comparison per semver spec
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as SemanticVersion);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + _major.GetHashCode();
                hash = hash * 23 + _minor.GetHashCode();
                hash = hash * 23 + _patch.GetHashCode();
                hash = hash * 23 + (_preRelease?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public static bool operator ==(SemanticVersion a, SemanticVersion b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            return a.Equals(b);
        }

        public static bool operator !=(SemanticVersion a, SemanticVersion b)
        {
            return !(a == b);
        }

        public static bool operator <(SemanticVersion a, SemanticVersion b)
        {
            return a?.CompareTo(b) < 0;
        }

        public static bool operator >(SemanticVersion a, SemanticVersion b)
        {
            return a?.CompareTo(b) > 0;
        }

        public static bool operator <=(SemanticVersion a, SemanticVersion b)
        {
            return a?.CompareTo(b) <= 0;
        }

        public static bool operator >=(SemanticVersion a, SemanticVersion b)
        {
            return a?.CompareTo(b) >= 0;
        }

        #endregion
    }
}
