using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressableManager.Configs
{
    /// <summary>
    /// Configuration for preloading addressable assets
    /// No more hardcoded addresses in code!
    /// </summary>
    [CreateAssetMenu(fileName = "PreloadConfig", menuName = "Addressable Manager/Preload Configuration", order = 1)]
    public class AddressablePreloadConfig : ScriptableObject
    {
        [System.Serializable]
        public class PreloadEntry
        {
            [Tooltip("Asset to preload (drag from Addressables Groups)")]
            public AssetReference assetReference;

            [Tooltip("Manual address (alternative to AssetReference)")]
            public string address;

            [Tooltip("Which scope to load this asset into")]
            public AssetScopeType scope = AssetScopeType.Global;

            [Tooltip("Load this asset automatically on startup")]
            public bool loadOnStartup = true;

            [Tooltip("Priority (lower = loaded first)")]
            [Range(0, 100)]
            public int priority = 50;

            [Tooltip("Optional label for debugging")]
            public string label;

            /// <summary>
            /// Get the address to load (prefer AssetReference over manual address)
            /// </summary>
            public string GetAddress()
            {
                if (assetReference != null && assetReference.RuntimeKeyIsValid())
                {
                    return assetReference.AssetGUID;
                }
                return address;
            }

            /// <summary>
            /// Check if this entry is valid
            /// </summary>
            public bool IsValid()
            {
                if (assetReference != null && assetReference.RuntimeKeyIsValid())
                    return true;

                return !string.IsNullOrEmpty(address);
            }
        }

        [Header("Preload Settings")]
        [Tooltip("List of assets to preload")]
        public List<PreloadEntry> preloadAssets = new List<PreloadEntry>();

        [Header("Validation")]
        [Tooltip("Validate all addresses exist before building")]
        public bool validateOnBuild = true;

        [Tooltip("Fail build if validation fails")]
        public bool failBuildOnError = false;

        [Header("Loading Behavior")]
        [Tooltip("Load assets in parallel or sequentially")]
        public bool loadInParallel = true;

        [Tooltip("Maximum concurrent loads (if parallel)")]
        [Range(1, 20)]
        public int maxConcurrentLoads = 5;

        /// <summary>
        /// Get all entries for a specific scope
        /// </summary>
        public List<PreloadEntry> GetEntriesForScope(AssetScopeType scope)
        {
            var result = new List<PreloadEntry>();

            foreach (var entry in preloadAssets)
            {
                if (entry.scope == scope && entry.IsValid())
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// Get startup assets sorted by priority
        /// </summary>
        public List<PreloadEntry> GetStartupAssets()
        {
            var result = new List<PreloadEntry>();

            foreach (var entry in preloadAssets)
            {
                if (entry.loadOnStartup && entry.IsValid())
                {
                    result.Add(entry);
                }
            }

            // Sort by priority (lower first)
            result.Sort((a, b) => a.priority.CompareTo(b.priority));

            return result;
        }

        /// <summary>
        /// Validate all entries
        /// </summary>
        public (bool success, List<string> errors) Validate()
        {
            var errors = new List<string>();

            for (int i = 0; i < preloadAssets.Count; i++)
            {
                var entry = preloadAssets[i];

                if (!entry.IsValid())
                {
                    errors.Add($"Entry {i}: No valid address or AssetReference set");
                }

                // Check for duplicate addresses
                for (int j = i + 1; j < preloadAssets.Count; j++)
                {
                    if (entry.GetAddress() == preloadAssets[j].GetAddress())
                    {
                        errors.Add($"Entry {i} and {j}: Duplicate address '{entry.GetAddress()}'");
                    }
                }
            }

            return (errors.Count == 0, errors);
        }

        #region Editor Helpers

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Auto-sort by priority when editing
            if (preloadAssets != null && preloadAssets.Count > 1)
            {
                // Don't actually sort here as it would reorder the list in inspector
                // Just validate
                var (success, errors) = Validate();
                if (!success && errors.Count > 0)
                {
                    Debug.LogWarning($"[PreloadConfig] Validation warnings:\n{string.Join("\n", errors)}", this);
                }
            }
        }
#endif

        #endregion
    }
}
