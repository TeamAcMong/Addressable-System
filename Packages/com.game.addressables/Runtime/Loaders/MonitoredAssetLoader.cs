using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Monitoring;

namespace AddressableManager.Loaders
{
    /// <summary>
    /// AssetLoader wrapper with monitoring support
    /// Use this instead of AssetLoader for automatic tracking
    /// </summary>
    public class MonitoredAssetLoader : IDisposable
    {
        private readonly AssetLoader _loader;
        private readonly string _scopeName;

        public MonitoredAssetLoader(string scopeName = "Unknown")
        {
            _loader = new AssetLoader();
            _scopeName = scopeName;
        }

        /// <summary>
        /// Load asset asynchronously by address
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
        {
            var startTime = Time.realtimeSinceStartup;

            // Load asset
            var handle = await _loader.LoadAssetAsync<T>(address);

            if (handle != null)
            {
                var loadDuration = Time.realtimeSinceStartup - startTime;

                // If load was very fast (<1ms), it was likely from cache
                bool fromCache = loadDuration < 0.001f;

                // Report to monitors
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    loadDuration,
                    fromCache
                );
            }

            return handle;
        }

        /// <summary>
        /// Load asset by AssetReference
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
        {
            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[MonitoredAssetLoader] Invalid AssetReference");
                return null;
            }

            var address = assetReference.AssetGUID;
            return await LoadAssetAsync<T>(address);
        }

        /// <summary>
        /// Load multiple assets by label
        /// </summary>
        public async Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
        {
            var startTime = Time.realtimeSinceStartup;

            var handles = await _loader.LoadAssetsByLabelAsync<T>(label);

            if (handles != null && handles.Count > 0)
            {
                var loadDuration = Time.realtimeSinceStartup - startTime;

                // Report each asset
                foreach (var handle in handles)
                {
                    AssetMonitorBridge.ReportAssetLoaded(
                        $"{label}/*",
                        typeof(T).Name,
                        _scopeName,
                        loadDuration / handles.Count, // Avg time per asset
                        false
                    );
                }
            }

            return handles;
        }

        /// <summary>
        /// Release asset handle
        /// </summary>
        public void Release<T>(IAssetHandle<T> handle, string address = null)
        {
            if (handle == null) return;

            handle.Release();

            // Report release
            if (!string.IsNullOrEmpty(address))
            {
                AssetMonitorBridge.ReportAssetReleased(address, typeof(T).Name);
            }
        }

        /// <summary>
        /// Clear all cached assets
        /// </summary>
        public void ClearCache()
        {
            _loader.ClearCache();
        }

        public void Dispose()
        {
            _loader?.Dispose();
        }

        /// <summary>
        /// Get the underlying AssetLoader (for advanced usage)
        /// </summary>
        public AssetLoader UnderlyingLoader => _loader;
    }
}
