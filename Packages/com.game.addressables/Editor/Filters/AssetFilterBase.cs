using System;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Base class for all asset filters
    /// Filters determine which assets a rule should apply to
    /// </summary>
    [Serializable]
    public abstract class AssetFilterBase : ScriptableObject
    {
        [Header("Filter Configuration")]
        [Tooltip("Enable/disable this filter")]
        [SerializeField] protected bool _enabled = true;

        [Tooltip("Invert the filter logic (match when it would NOT match)")]
        [SerializeField] protected bool _invert = false;

        [Tooltip("Description of this filter")]
        [SerializeField] [TextArea(1, 2)] protected string _description;

        /// <summary>
        /// Is this filter enabled
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Invert filter logic
        /// </summary>
        public bool Invert
        {
            get => _invert;
            set => _invert = value;
        }

        /// <summary>
        /// Filter description
        /// </summary>
        public string Description
        {
            get => _description;
            set => _description = value;
        }

        /// <summary>
        /// Check if an asset matches this filter
        /// This is the public interface that handles inversion
        /// </summary>
        public bool IsMatch(string assetPath)
        {
            if (!_enabled)
                return true; // Disabled filters always match (don't filter out)

            bool match = IsMatchInternal(assetPath);
            return _invert ? !match : match;
        }

        /// <summary>
        /// Setup the filter (called on main thread before worker thread usage)
        /// Override this to perform any Unity API calls or caching
        /// </summary>
        public virtual void Setup()
        {
            // Default: no setup needed
        }

        /// <summary>
        /// Internal match logic - implement this in derived classes
        /// This will be called from worker threads, so avoid Unity API calls
        /// </summary>
        protected abstract bool IsMatchInternal(string assetPath);

        /// <summary>
        /// Get a human-readable description of what this filter does
        /// </summary>
        public virtual string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return GetType().Name.Replace("Filter", "");
        }
    }
}
