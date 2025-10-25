using System;
using System.Threading.Tasks;
using UnityEngine;
using AddressableManager.Core;
using AddressableManager.Progress;

namespace AddressableManager.Facade
{
    /// <summary>
    /// Ultra-simple static API for common addressable operations
    /// Perfect for quick prototyping and simple use cases
    ///
    /// Examples:
    ///   var sprite = await Assets.Load<Sprite>("UI/Icon");
    ///   var enemy = await Assets.Spawn("Enemies/Orc", position);
    ///   Assets.Despawn("Enemies/Orc", enemy);
    /// </summary>
    public static class Assets
    {
        private static AddressablesFacade Manager => AddressablesFacade.Instance;

        #region Loading

        /// <summary>
        /// Load asset (global scope)
        /// </summary>
        public static async Task<IAssetHandle<T>> Load<T>(string address)
        {
            return await Manager.LoadGlobalAsync<T>(address);
        }

        /// <summary>
        /// Load asset with progress callback
        /// </summary>
        public static async Task<IAssetHandle<T>> Load<T>(string address, Action<ProgressInfo> onProgress)
        {
            return await Manager.LoadGlobalWithProgressAsync<T>(address, onProgress);
        }

        /// <summary>
        /// Load into session scope
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadSession<T>(string address)
        {
            return await Manager.LoadSessionAsync<T>(address);
        }

        /// <summary>
        /// Load into scene scope
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadScene<T>(string address)
        {
            return await Manager.LoadSceneAsync<T>(address);
        }

        #endregion

        #region Pooling

        /// <summary>
        /// Create object pool
        /// </summary>
        public static async Task<bool> CreatePool(string address, int preloadCount = 0, int maxSize = 100)
        {
            return await Manager.CreatePoolAsync(address, preloadCount, maxSize);
        }

        /// <summary>
        /// Spawn from pool at position
        /// </summary>
        public static GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            return Manager.Spawn(address, position, rotation, parent);
        }

        /// <summary>
        /// Spawn from pool at position (default rotation)
        /// </summary>
        public static GameObject Spawn(string address, Vector3 position, Transform parent = null)
        {
            return Manager.Spawn(address, position, parent);
        }

        /// <summary>
        /// Spawn from pool at origin
        /// </summary>
        public static GameObject Spawn(string address, Transform parent = null)
        {
            return Manager.Spawn(address, parent);
        }

        /// <summary>
        /// Return to pool
        /// </summary>
        public static void Despawn(string address, GameObject instance)
        {
            Manager.Despawn(address, instance);
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Start new gameplay session
        /// </summary>
        public static void StartSession()
        {
            Manager.StartSession();
        }

        /// <summary>
        /// End current session
        /// </summary>
        public static void EndSession()
        {
            Manager.EndSession();
        }

        #endregion

        #region Utility

        /// <summary>
        /// Get download size
        /// </summary>
        public static async Task<long> GetDownloadSize(string address)
        {
            return await Manager.GetDownloadSizeAsync(address);
        }

        /// <summary>
        /// Download dependencies
        /// </summary>
        public static async Task<bool> Download(string address, Action<ProgressInfo> onProgress = null)
        {
            return await Manager.DownloadAsync(address, onProgress);
        }

        /// <summary>
        /// Clear global cache
        /// </summary>
        public static void ClearCache()
        {
            Manager.ClearGlobalCache();
        }

        /// <summary>
        /// Clear all pools
        /// </summary>
        public static void ClearPools()
        {
            Manager.ClearAllPools();
        }

        #endregion
    }
}
