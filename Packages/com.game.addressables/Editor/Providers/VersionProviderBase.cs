using System;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Base class for version providers
    /// Providers generate version strings from asset paths
    /// </summary>
    [Serializable]
    public abstract class VersionProviderBase : ScriptableObject
    {
        [Header("Provider Configuration")]
        [Tooltip("Description of what this provider does")]
        [SerializeField] [TextArea(1, 2)] protected string _description;

        /// <summary>
        /// Provider description
        /// </summary>
        public string Description
        {
            get => _description;
            set => _description = value;
        }

        /// <summary>
        /// Setup the provider (called on main thread before worker thread usage)
        /// Override this to perform any Unity API calls or caching
        /// </summary>
        public virtual void Setup()
        {
            // Default: no setup needed
        }

        /// <summary>
        /// Generate a version string for an asset
        /// This may be called from worker threads, so avoid Unity API calls
        /// Should follow semantic versioning: Major.Minor.Patch[-PreRelease]
        /// </summary>
        /// <param name="assetPath">Path to the asset</param>
        /// <returns>Generated version string</returns>
        public abstract string Provide(string assetPath);

        /// <summary>
        /// Get a human-readable name for this provider
        /// </summary>
        public virtual string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return GetType().Name.Replace("Provider", "").Replace("Version", "");
        }
    }
}
