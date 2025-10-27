using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Monitoring;

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Extension methods for AssetLoader with automatic monitoring
    /// These wrap the standard calls with performance tracking
    /// </summary>
    public static class AssetLoaderExtensions
    {
        /// <summary>
        /// Load asset async with monitoring (use this helper for automatic tracking)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadAssetAsyncMonitored<T>(
            this AssetLoader loader,
            string address,
            string scopeName = "Unknown")
        {
            var startTime = Time.realtimeSinceStartup;
            bool fromCache = false;

            // TODO: We can't check cache directly, so we'll detect based on load time
            var handle = await loader.LoadAssetAsync<T>(address);

            if (handle != null)
            {
                var loadDuration = Time.realtimeSinceStartup - startTime;

                // If load was very fast (<1ms), it was likely from cache
                fromCache = loadDuration < 0.001f;

                // Report to monitors
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    scopeName,
                    loadDuration,
                    fromCache
                );
            }

            return handle;
        }

        /// <summary>
        /// Load asset by AssetReference with monitoring
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadAssetAsyncMonitored<T>(
            this AssetLoader loader,
            AssetReference assetReference,
            string scopeName = "Unknown")
        {
            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[AssetLoader] Invalid AssetReference");
                return null;
            }

            var address = assetReference.AssetGUID;
            return await loader.LoadAssetAsyncMonitored<T>(address, scopeName);
        }

        /// <summary>
        /// Load multiple assets by label with monitoring
        /// </summary>
        public static async Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsyncMonitored<T>(
            this AssetLoader loader,
            string label,
            string scopeName = "Unknown")
        {
            var startTime = Time.realtimeSinceStartup;

            var handles = await loader.LoadAssetsByLabelAsync<T>(label);

            if (handles != null && handles.Count > 0)
            {
                var loadDuration = Time.realtimeSinceStartup - startTime;

                // Report each asset
                foreach (var handle in handles)
                {
                    AssetMonitorBridge.ReportAssetLoaded(
                        $"{label}/*",
                        typeof(T).Name,
                        scopeName,
                        loadDuration / handles.Count, // Avg time per asset
                        false
                    );
                }
            }

            return handles;
        }

        /// <summary>
        /// Release asset handle with monitoring
        /// </summary>
        public static void ReleaseMonitored<T>(this IAssetHandle<T> handle, string address)
        {
            if (handle == null) return;

            handle.Release();

            // Report release
            if (!string.IsNullOrEmpty(address))
            {
                AssetMonitorBridge.ReportAssetReleased(address, typeof(T).Name);
            }
        }
    }
}
