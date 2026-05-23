using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides a constant version string for all matched assets
    /// Useful for assigning the same version to a group of assets
    /// </summary>
    [CreateAssetMenu(fileName = "ConstantVersionProvider", menuName = "Addressable Manager/Providers/Version/Constant")]
    public class ConstantVersionProvider : VersionProviderBase
    {
        [Header("Version Settings")]
        [Tooltip("Version string to apply (e.g., '1.0.0' or '2.1.0-beta.3')")]
        [SerializeField] private string _version = "1.0.0";

        /// <summary>
        /// Version string
        /// </summary>
        public string Version
        {
            get => _version;
            set => _version = value;
        }

        public override string Provide(string assetPath)
        {
            return _version;
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return $"Version: {_version}";
        }
    }
}
