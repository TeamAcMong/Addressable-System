using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressableManager.Configs
{
    /// <summary>
    /// Configuration for object pools
    /// Define all your pools in one place!
    /// </summary>
    [CreateAssetMenu(fileName = "PoolConfig", menuName = "Addressable Manager/Pool Configuration", order = 2)]
    public class PoolConfiguration : ScriptableObject
    {
        [System.Serializable]
        public class PoolSettings
        {
            [Tooltip("Prefab to pool (must be a GameObject)")]
            public AssetReference prefabReference;

            [Tooltip("Manual address (alternative to AssetReference)")]
            public string address;

            [Header("Pool Settings")]
            [Tooltip("Number of instances to preload on pool creation")]
            [Range(0, 100)]
            public int preloadCount = 10;

            [Tooltip("Maximum pool size (0 = unlimited)")]
            [Range(0, 1000)]
            public int maxSize = 100;

            [Tooltip("Create this pool automatically on startup")]
            public bool autoCreate = true;

            [Header("Advanced")]
            [Tooltip("Parent transform for pooled objects")]
            public Transform poolRoot;

            [Tooltip("Auto-destroy instances when pool is full")]
            public bool destroyOnFull = false;

            [Tooltip("Optional label for debugging")]
            public string label;

            /// <summary>
            /// Get the address to use
            /// </summary>
            public string GetAddress()
            {
                if (prefabReference != null && prefabReference.RuntimeKeyIsValid())
                {
                    return prefabReference.AssetGUID;
                }
                return address;
            }

            /// <summary>
            /// Check if settings are valid
            /// </summary>
            public bool IsValid()
            {
                if (prefabReference != null && prefabReference.RuntimeKeyIsValid())
                    return true;

                return !string.IsNullOrEmpty(address);
            }
        }

        [Header("Pool Configurations")]
        [Tooltip("List of all pools to create")]
        public List<PoolSettings> pools = new List<PoolSettings>();

        [Header("Global Pool Settings")]
        [Tooltip("Default max pool size for new pools")]
        public int defaultMaxSize = 50;

        [Tooltip("Default preload count for new pools")]
        public int defaultPreloadCount = 5;

        [Tooltip("Create all pools on startup")]
        public bool createAllOnStartup = true;

        [Tooltip("Cleanup pools on scene unload")]
        public bool cleanupOnSceneUnload = true;

        /// <summary>
        /// Get all pools marked for auto-creation
        /// </summary>
        public List<PoolSettings> GetAutoCreatePools()
        {
            var result = new List<PoolSettings>();

            foreach (var pool in pools)
            {
                if (pool.autoCreate && pool.IsValid())
                {
                    result.Add(pool);
                }
            }

            return result;
        }

        /// <summary>
        /// Get pool settings by address
        /// </summary>
        public PoolSettings GetPoolByAddress(string address)
        {
            foreach (var pool in pools)
            {
                if (pool.GetAddress() == address)
                {
                    return pool;
                }
            }

            return null;
        }

        /// <summary>
        /// Validate all pool settings
        /// </summary>
        public (bool success, List<string> errors) Validate()
        {
            var errors = new List<string>();

            for (int i = 0; i < pools.Count; i++)
            {
                var pool = pools[i];

                if (!pool.IsValid())
                {
                    errors.Add($"Pool {i}: No valid address or prefab reference set");
                }

                if (pool.maxSize > 0 && pool.preloadCount > pool.maxSize)
                {
                    errors.Add($"Pool {i}: Preload count ({pool.preloadCount}) exceeds max size ({pool.maxSize})");
                }

                // Check for duplicates
                for (int j = i + 1; j < pools.Count; j++)
                {
                    if (pool.GetAddress() == pools[j].GetAddress())
                    {
                        errors.Add($"Pool {i} and {j}: Duplicate address '{pool.GetAddress()}'");
                    }
                }
            }

            return (errors.Count == 0, errors);
        }

        #region Editor Helpers

#if UNITY_EDITOR
        private void OnValidate()
        {
            var (success, errors) = Validate();
            if (!success && errors.Count > 0)
            {
                Debug.LogWarning($"[PoolConfig] Validation warnings:\n{string.Join("\n", errors)}", this);
            }
        }
#endif

        #endregion
    }
}
