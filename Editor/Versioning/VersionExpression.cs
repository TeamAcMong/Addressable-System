using System;
using System.Text.RegularExpressions;

namespace AddressableManager.Editor.Versioning
{
    /// <summary>
    /// Represents a version range expression
    /// Supports Unity-style version range expressions:
    /// - [1.3,3.4.1] - Inclusive bounds
    /// - (1.3.0,3.4) - Exclusive bounds
    /// - 2.1.0-preview.7 - Minimum version (inclusive)
    /// - [1.0.0,) - 1.0.0 or higher
    /// - (,2.0.0] - Up to 2.0.0
    /// </summary>
    public class VersionExpression
    {
        public SemanticVersion MinVersion { get; private set; }
        public SemanticVersion MaxVersion { get; private set; }
        public bool MinInclusive { get; private set; }
        public bool MaxInclusive { get; private set; }

        private static readonly Regex RangeRegex = new Regex(
            @"^(?<minBracket>[\[\(])(?<min>[^,\]\)]*),(?<max>[^,\]\)]*)(?<maxBracket>[\]\)])$",
            RegexOptions.Compiled);

        private VersionExpression(SemanticVersion minVersion, SemanticVersion maxVersion,
            bool minInclusive, bool maxInclusive)
        {
            MinVersion = minVersion;
            MaxVersion = maxVersion;
            MinInclusive = minInclusive;
            MaxInclusive = maxInclusive;
        }

        /// <summary>
        /// Parse a version expression string
        /// </summary>
        public static bool TryParse(string expression, out VersionExpression versionExpression)
        {
            versionExpression = null;

            if (string.IsNullOrWhiteSpace(expression))
                return false;

            expression = expression.Trim();

            // Check if it's a range expression [min,max] or (min,max)
            var match = RangeRegex.Match(expression);
            if (match.Success)
            {
                return ParseRange(match, out versionExpression);
            }

            // Not a range - treat as single version (minimum, inclusive)
            if (!SemanticVersion.TryParse(expression, out var version))
                return false;

            versionExpression = new VersionExpression(version, null, true, false);
            return true;
        }

        private static bool ParseRange(Match match, out VersionExpression versionExpression)
        {
            versionExpression = null;

            string minBracket = match.Groups["minBracket"].Value;
            string maxBracket = match.Groups["maxBracket"].Value;
            string minStr = match.Groups["min"].Value.Trim();
            string maxStr = match.Groups["max"].Value.Trim();

            bool minInclusive = minBracket == "[";
            bool maxInclusive = maxBracket == "]";

            // Parse min version (empty means no lower bound)
            SemanticVersion minVersion = null;
            if (!string.IsNullOrEmpty(minStr))
            {
                if (!SemanticVersion.TryParse(minStr, out minVersion))
                    return false;
            }

            // Parse max version (empty means no upper bound)
            SemanticVersion maxVersion = null;
            if (!string.IsNullOrEmpty(maxStr))
            {
                if (!SemanticVersion.TryParse(maxStr, out maxVersion))
                    return false;
            }

            // Validate range
            if (minVersion != null && maxVersion != null)
            {
                if (minVersion > maxVersion)
                    return false; // Invalid range
            }

            versionExpression = new VersionExpression(minVersion, maxVersion, minInclusive, maxInclusive);
            return true;
        }

        /// <summary>
        /// Check if a version matches this expression
        /// </summary>
        public bool IsMatch(SemanticVersion version)
        {
            if (version == null)
                return false;

            // Check minimum bound
            if (MinVersion != null)
            {
                int minComparison = version.CompareTo(MinVersion);
                if (MinInclusive)
                {
                    if (minComparison < 0) return false; // version < min
                }
                else
                {
                    if (minComparison <= 0) return false; // version <= min
                }
            }

            // Check maximum bound
            if (MaxVersion != null)
            {
                int maxComparison = version.CompareTo(MaxVersion);
                if (MaxInclusive)
                {
                    if (maxComparison > 0) return false; // version > max
                }
                else
                {
                    if (maxComparison >= 0) return false; // version >= max
                }
            }

            return true;
        }

        public override string ToString()
        {
            if (MaxVersion == null && MinVersion != null && MinInclusive)
            {
                // Single version
                return MinVersion.ToString();
            }

            string minBracket = MinInclusive ? "[" : "(";
            string maxBracket = MaxInclusive ? "]" : ")";
            string minStr = MinVersion?.ToString() ?? "";
            string maxStr = MaxVersion?.ToString() ?? "";

            return $"{minBracket}{minStr},{maxStr}{maxBracket}";
        }

        /// <summary>
        /// Create an expression matching any version >= specified version
        /// </summary>
        public static VersionExpression AtLeast(SemanticVersion version)
        {
            return new VersionExpression(version, null, true, false);
        }

        /// <summary>
        /// Create an expression matching versions in range [min, max]
        /// </summary>
        public static VersionExpression InRange(SemanticVersion min, SemanticVersion max,
            bool minInclusive = true, bool maxInclusive = true)
        {
            if (min != null && max != null && min > max)
            {
                throw new ArgumentException("Min version cannot be greater than max version");
            }

            return new VersionExpression(min, max, minInclusive, maxInclusive);
        }

        /// <summary>
        /// Create an expression matching exact version
        /// </summary>
        public static VersionExpression Exactly(SemanticVersion version)
        {
            return new VersionExpression(version, version, true, true);
        }
    }
}
