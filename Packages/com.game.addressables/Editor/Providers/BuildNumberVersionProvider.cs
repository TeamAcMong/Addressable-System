using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides version based on Unity build number or custom version string
    /// Uses PlayerSettings.bundleVersion or custom source
    /// </summary>
    [CreateAssetMenu(fileName = "BuildNumberVersionProvider", menuName = "Addressable Manager/Providers/Version/Build Number")]
    public class BuildNumberVersionProvider : VersionProviderBase
    {
        public enum VersionSource
        {
            BundleVersion,      // PlayerSettings.bundleVersion (e.g., "1.0.0")
            BuildNumber,        // PlayerSettings.Android.bundleVersionCode / iOS.buildNumber
            Combined,           // BundleVersion + BuildNumber (e.g., "1.0.0.123")
            Custom              // Custom version string
        }

        [Header("Build Number Settings")]
        [Tooltip("Source for version string")]
        [SerializeField] private VersionSource _versionSource = VersionSource.BundleVersion;

        [Tooltip("Custom version string (used when source is Custom)")]
        [SerializeField] private string _customVersion = "1.0.0";

        [Tooltip("Prefix to add to version")]
        [SerializeField] private string _prefix = "";

        [Tooltip("Suffix to add to version")]
        [SerializeField] private string _suffix = "";

        [Tooltip("Platform to use for build number (when applicable)")]
        [SerializeField] private RuntimePlatform _platform = RuntimePlatform.Android;

        public override string Provide(string assetPath)
        {
            string version = _versionSource switch
            {
                VersionSource.BundleVersion => GetBundleVersion(),
                VersionSource.BuildNumber => GetBuildNumber(),
                VersionSource.Combined => GetCombinedVersion(),
                VersionSource.Custom => _customVersion,
                _ => _customVersion
            };

            if (string.IsNullOrEmpty(version))
            {
                Debug.LogWarning($"[BuildNumberVersionProvider] Failed to get version from {_versionSource}, using custom: {_customVersion}");
                version = _customVersion;
            }

            return $"{_prefix}{version}{_suffix}";
        }

        private string GetBundleVersion()
        {
            return PlayerSettings.bundleVersion;
        }

        private string GetBuildNumber()
        {
            // Platform-specific build numbers
            if (_platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.Android)
            {
                return PlayerSettings.Android.bundleVersionCode.ToString();
            }
            else if (_platform == RuntimePlatform.IPhonePlayer || Application.platform == RuntimePlatform.IPhonePlayer)
            {
                return PlayerSettings.iOS.buildNumber;
            }
            else
            {
                // Generic fallback
                return PlayerSettings.Android.bundleVersionCode.ToString();
            }
        }

        private string GetCombinedVersion()
        {
            string bundle = GetBundleVersion();
            string buildNum = GetBuildNumber();

            if (string.IsNullOrEmpty(bundle))
            {
                return buildNum;
            }

            if (string.IsNullOrEmpty(buildNum))
            {
                return bundle;
            }

            return $"{bundle}.{buildNum}";
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Version: {_versionSource}";
        }
    }
}
