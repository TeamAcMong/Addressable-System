using System;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Base class for address providers
    /// Providers generate addresses from asset paths
    /// </summary>
    [Serializable]
    public abstract class AddressProviderBase : ScriptableObject
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
        /// Generate an address for an asset
        /// This may be called from worker threads, so avoid Unity API calls
        /// </summary>
        /// <param name="assetPath">Path to the asset</param>
        /// <returns>Generated address</returns>
        public abstract string Provide(string assetPath);

        /// <summary>
        /// Get a human-readable name for this provider
        /// </summary>
        public virtual string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return GetType().Name.Replace("Provider", "").Replace("Address", "");
        }
    }
}
