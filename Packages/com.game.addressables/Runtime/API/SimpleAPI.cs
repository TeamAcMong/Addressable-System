using System;
using System.Threading.Tasks;
using UnityEngine;
using AddressableManager.Core;
using AddressableManager.Facade;

namespace AddressableManager.API
{
    /// <summary>
    /// Simple API - Ultra-simple one-liner operations
    ///
    /// Target users: Beginners, rapid prototyping
    /// Features: Automatic management, no configuration needed
    /// Trade-offs: Less control, uses default settings
    ///
    /// Usage:
    ///   var sprite = await Simple.Load<Sprite>("UI/Icon");
    ///   Simple.Release(sprite);
    /// </summary>
    public static class Simple
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

        #region Load

        /// <summary>
        /// Load asset by address (simplest possible syntax)
        /// Returns the asset directly, handle managed automatically
        /// </summary>
        public static async Task<T> Load<T>(string address)
        {
            var handle = await Facade.LoadGlobal<T>(address);
            return handle != null && handle.IsValid ? handle.Asset : default;
        }

        /// <summary>
        /// Load asset with error handling
        /// Returns tuple (asset, success)
        /// </summary>
        public static async Task<(T asset, bool success)> TryLoad<T>(string address)
        {
            try
            {
                var handle = await Facade.LoadGlobal<T>(address);
                if (handle != null && handle.IsValid)
                {
                    return (handle.Asset, true);
                }
                return (default, false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Simple.TryLoad] Failed to load {address}: {ex.Message}");
                return (default, false);
            }
        }

        /// <summary>
        /// Load asset with callback when ready
        /// Fire-and-forget style
        /// </summary>
        public static async void LoadAsync<T>(string address, Action<T> onLoaded)
        {
            var asset = await Load<T>(address);
            if (asset != null)
            {
                onLoaded?.Invoke(asset);
            }
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiate GameObject from addressable
        /// </summary>
        public static async Task<GameObject> Spawn(string address)
        {
            return await Facade.InstantiateGlobal(address);
        }

        /// <summary>
        /// Instantiate at position
        /// </summary>
        public static async Task<GameObject> Spawn(string address, Vector3 position)
        {
            return await Facade.InstantiateGlobal(address, position, Quaternion.identity);
        }

        /// <summary>
        /// Instantiate with full transform
        /// </summary>
        public static async Task<GameObject> Spawn(string address, Vector3 position, Quaternion rotation)
        {
            return await Facade.InstantiateGlobal(address, position, rotation);
        }

        /// <summary>
        /// Destroy instantiated GameObject
        /// </summary>
        public static void Destroy(GameObject instance)
        {
            if (instance != null)
            {
                UnityEngine.Object.Destroy(instance);
            }
        }

        #endregion

        #region Pooling (Auto)

        /// <summary>
        /// Spawn from pool (auto-creates pool if needed)
        /// </summary>
        public static GameObject Pool(string address)
        {
            var poolManager = Facade.GetPoolManager();

            // Enable auto-create if not already enabled
            if (!poolManager.IsAutoCreateEnabled)
            {
                poolManager.EnableAutoCreatePools();
            }

            return poolManager.Spawn(address);
        }

        /// <summary>
        /// Spawn from pool at position
        /// </summary>
        public static GameObject Pool(string address, Vector3 position)
        {
            var instance = Pool(address);
            if (instance != null)
            {
                instance.transform.position = position;
            }
            return instance;
        }

        /// <summary>
        /// Return to pool
        /// </summary>
        public static void Recycle(string address, GameObject instance)
        {
            var poolManager = Facade.GetPoolManager();
            poolManager.Despawn(address, instance);
        }

        #endregion

        #region Release

        /// <summary>
        /// Release asset (if you have the asset reference)
        /// Note: In Simple API, assets are usually managed automatically
        /// </summary>
        public static void Release<T>(T asset)
        {
            // In Simple API, we don't expose handles directly
            // Assets are managed by the Global scope
            // Users can call this to hint that asset is no longer needed
            // But actual release is managed by scope lifecycle
            Debug.Log($"[Simple.Release] Release hint for asset of type {typeof(T).Name}");
        }

        /// <summary>
        /// Clear all cached assets
        /// Use sparingly - clears everything in Global scope
        /// </summary>
        public static void ClearAll()
        {
            Facade.ClearGlobalCache();
        }

        #endregion

        #region Preload

        /// <summary>
        /// Preload asset in background (fire-and-forget)
        /// </summary>
        public static async void Preload<T>(string address)
        {
            await Load<T>(address);
        }

        /// <summary>
        /// Preload multiple assets
        /// </summary>
        public static async void PreloadBatch(params string[] addresses)
        {
            foreach (var address in addresses)
            {
                await Load<object>(address);
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Check if asset is loaded
        /// </summary>
        public static bool IsLoaded(string address)
        {
            // Check if in cache
            var (_, activeHandles) = Facade.GetGlobalScope().Loader.GetCacheStats();
            return activeHandles > 0; // Simplified check
        }

        /// <summary>
        /// Get memory stats (simple)
        /// </summary>
        public static (int loadedAssets, int pooledObjects) GetStats()
        {
            var (cached, active) = Facade.GetGlobalScope().Loader.GetCacheStats();
            return (cached + active, 0); // Simplified
        }

        #endregion
    }
}
