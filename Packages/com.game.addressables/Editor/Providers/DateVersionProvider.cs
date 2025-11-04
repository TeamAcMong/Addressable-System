using System;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides version based on current date/time
    /// Useful for timestamp-based versioning
    /// </summary>
    [CreateAssetMenu(fileName = "DateVersionProvider", menuName = "Addressable Manager/Providers/Version/Date")]
    public class DateVersionProvider : VersionProviderBase
    {
        public enum DateFormat
        {
            YYYYMMDD,           // 20250104
            YYYYMMDDHHMMSS,     // 20250104150530
            YYYYdotMMdotDD,     // 2025.01.04
            ISO8601,            // 2025-01-04T15:05:30Z
            UnixTimestamp,      // 1704380730
            Custom              // Custom format string
        }

        [Header("Date Version Settings")]
        [Tooltip("Date format for version string")]
        [SerializeField] private DateFormat _dateFormat = DateFormat.YYYYMMDD;

        [Tooltip("Custom format string (when format is Custom)\ne.g., 'yyyyMMdd-HHmmss'")]
        [SerializeField] private string _customFormat = "yyyyMMdd-HHmmss";

        [Tooltip("Use UTC time instead of local time")]
        [SerializeField] private bool _useUTC = true;

        [Tooltip("Prefix to add to version")]
        [SerializeField] private string _prefix = "";

        [Tooltip("Suffix to add to version")]
        [SerializeField] private string _suffix = "";

        [Tooltip("Cache version once per setup (same version for all assets)")]
        [SerializeField] private bool _cacheVersion = true;

        private string _cachedVersion;
        private bool _versionCached;

        public override void Setup()
        {
            base.Setup();

            if (_cacheVersion)
            {
                _cachedVersion = GenerateVersion();
                _versionCached = true;
            }
        }

        public override string Provide(string assetPath)
        {
            if (_cacheVersion && _versionCached)
            {
                return _cachedVersion;
            }

            return GenerateVersion();
        }

        private string GenerateVersion()
        {
            DateTime now = _useUTC ? DateTime.UtcNow : DateTime.Now;

            string version = _dateFormat switch
            {
                DateFormat.YYYYMMDD => now.ToString("yyyyMMdd"),
                DateFormat.YYYYMMDDHHMMSS => now.ToString("yyyyMMddHHmmss"),
                DateFormat.YYYYdotMMdotDD => now.ToString("yyyy.MM.dd"),
                DateFormat.ISO8601 => now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                DateFormat.UnixTimestamp => GetUnixTimestamp(now).ToString(),
                DateFormat.Custom => now.ToString(_customFormat),
                _ => now.ToString("yyyyMMdd")
            };

            return $"{_prefix}{version}{_suffix}";
        }

        private long GetUnixTimestamp(DateTime dateTime)
        {
            DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime.ToUniversalTime() - unixEpoch).TotalSeconds;
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Version: Date ({_dateFormat})";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _versionCached = false;
        }
    }
}
