using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Facade;
using AddressableManager.Pooling;

namespace AddressableManager.API
{
    /// <summary>
    /// Standard API - Balanced control and convenience
    ///
    /// Target users: Most developers, production use
    /// Features: Scope management, pooling control, progress tracking
    /// Trade-offs: Requires understanding of scopes and lifecycle
    ///
    /// Usage:
    ///   using var handle = await Standard.LoadGlobal<Sprite>("UI/Icon");
    ///   image.sprite = handle.Asset;
    /// </summary>
    public static class Standard
    {
        private static AddressablesFacade _facade;

        private static AddressablesFacade Facade
        {
            get
            {
                if (_facade == null)
                {
                    _facade = AddressablesFacade.Instance;
                }
                return _facade;
            }
        }

        #region Scoped Loading

        /// <summary>
        /// Load asset in Global scope (persistent)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadGlobal<T>(string address)
        {
            return await Facade.LoadGlobal<T>(address);
        }

        /// <summary>
        /// Load asset in Session scope (gameplay lifetime)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadSession<T>(string address)
        {
            return await Facade.LoadSession<T>(address);
        }

        /// <summary>
        /// Load asset in Scene scope (current scene lifetime)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadScene<T>(string address)
        {
            return await Facade.LoadScene<T>(address);
        }

        /// <summary>
        /// Load asset by AssetReference
        /// </summary>
        public static async Task<IAssetHandle<T>> Load<T>(AssetReference assetReference)
        {
            return await Facade.LoadGlobal<T>(assetReference);
        }

        /// <summary>
        /// Load with Result pattern for explicit error handling
        /// </summary>
        public static async Task<LoadResult<IAssetHandle<T>>> LoadSafe<T>(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.LoadAssetAsyncSafe<T>(address);
        }

        /// <summary>
        /// Load with SmartHandle for automatic memory management
        /// </summary>
        public static async Task<SmartAssetHandle<T>> LoadSmart<T>(string address)
        {
            var handle = await LoadGlobal<T>(address);
            return handle?.ToSmart();
        }

        #endregion

        #region Multiple Assets

        /// <summary>
        /// Load multiple assets by label
        /// </summary>
        public static async Task<List<IAssetHandle<T>>> LoadByLabel<T>(string label)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.LoadAssetsByLabelAsync<T>(label);
        }

        /// <summary>
        /// Load batch of assets by addresses
        /// </summary>
        public static async Task<Dictionary<string, IAssetHandle<T>>> LoadBatch<T>(params string[] addresses)
        {
            var results = new Dictionary<string, IAssetHandle<T>>();
            var loader = Facade.GetGlobalScope().Loader;

            foreach (var address in addresses)
            {
                var handle = await loader.LoadAssetAsync<T>(address);
                if (handle != null && handle.IsValid)
                {
                    results[address] = handle;
                }
            }

            return results;
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiate GameObject in Global scope
        /// </summary>
        public static async Task<GameObject> InstantiateGlobal(string address)
        {
            return await Facade.InstantiateGlobal(address);
        }

        /// <summary>
        /// Instantiate GameObject in Session scope
        /// </summary>
        public static async Task<GameObject> InstantiateSession(string address)
        {
            return await Facade.InstantiateSession(address);
        }

        /// <summary>
        /// Instantiate with transform
        /// </summary>
        public static async Task<GameObject> Instantiate(string address, Vector3 position, Quaternion rotation)
        {
            return await Facade.InstantiateGlobal(address, position, rotation);
        }

        #endregion

        #region Pooling

        /// <summary>
        /// Create standard pool
        /// </summary>
        public static async Task<bool> CreatePool(string address, int preloadCount = 0, int maxSize = 50)
        {
            return await Facade.CreatePool(address, preloadCount, maxSize);
        }

        /// <summary>
        /// Create dynamic pool with auto-sizing
        /// </summary>
        public static async Task<bool> CreateDynamicPool(string address, DynamicPoolConfig config = null, int preloadCount = 0)
        {
            var poolManager = Facade.GetPoolManager();
            return await poolManager.CreateDynamicPoolAsync(address, config ?? DynamicPoolConfig.Default, preloadCount);
        }

        /// <summary>
        /// Spawn from pool
        /// </summary>
        public static GameObject Spawn(string address)
        {
            return Facade.Spawn(address);
        }

        /// <summary>
        /// Spawn from pool at position
        /// </summary>
        public static GameObject Spawn(string address, Vector3 position)
        {
            return Facade.Spawn(address, position);
        }

        /// <summary>
        /// Return to pool
        /// </summary>
        public static void Despawn(string address, GameObject instance)
        {
            Facade.Despawn(address, instance);
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        public static (int activeCount, int pooledCount)? GetPoolStats(string address)
        {
            var poolManager = Facade.GetPoolManager();
            return poolManager.GetPoolStats(address);
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Start a new session (clears previous session assets)
        /// </summary>
        public static void StartSession()
        {
            Facade.StartSession();
        }

        /// <summary>
        /// End current session (releases all session assets)
        /// </summary>
        public static void EndSession()
        {
            Facade.EndSession();
        }

        /// <summary>
        /// Check if session is active
        /// </summary>
        public static bool IsSessionActive()
        {
            return Facade.IsSessionActive();
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clear Global cache
        /// </summary>
        public static void ClearGlobalCache()
        {
            Facade.ClearGlobalCache();
        }

        /// <summary>
        /// Clear Session cache
        /// </summary>
        public static void ClearSessionCache()
        {
            Facade.ClearSessionCache();
        }

        /// <summary>
        /// Clear specific scope cache
        /// </summary>
        public static void ClearCache(string scopeName)
        {
            // Implementation depends on scope manager
            Debug.Log($"[Standard] Clearing cache for scope: {scopeName}");
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public static (int cachedAssets, int activeHandles) GetCacheStats()
        {
            return Facade.GetGlobalScope().Loader.GetCacheStats();
        }

        #endregion

        #region Preloading

        /// <summary>
        /// Preload assets without storing handles
        /// </summary>
        public static async Task PreloadAsync(params string[] addresses)
        {
            var loader = Facade.GetGlobalScope().Loader;

            foreach (var address in addresses)
            {
                await loader.LoadAssetAsync<object>(address);
            }
        }

        /// <summary>
        /// Download dependencies for remote assets
        /// </summary>
        public static async Task<long> DownloadDependencies(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.DownloadDependenciesAsync(address);
        }

        /// <summary>
        /// Get download size
        /// </summary>
        public static async Task<long> GetDownloadSize(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.GetDownloadSizeAsync(address);
        }

        #endregion

        #region Validation

        /// <summary>
        /// Enable validation mode
        /// </summary>
        public static void EnableValidation(ValidationMode mode)
        {
            AssetValidator.CurrentMode = mode;
        }

        /// <summary>
        /// Disable validation
        /// </summary>
        public static void DisableValidation()
        {
            AssetValidator.CurrentMode = ValidationMode.None;
        }

        /// <summary>
        /// Get validation statistics
        /// </summary>
        public static ValidationStats GetValidationStats()
        {
            return AssetValidator.GetStatistics();
        }

        #endregion
    }
}
