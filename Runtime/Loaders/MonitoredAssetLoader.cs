using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// AssetLoader wrapper that simply forwards every call to an inner AssetLoader
    /// constructed with the given scope name.
    ///
    /// Historically this class added a second monitoring layer on top of AssetLoader;
    /// since AssetLoader gained built-in monitoring in 2.1.0 those calls would double-report,
    /// so this wrapper is now a thin forwarder kept for source compatibility.
    /// </summary>
    public class MonitoredAssetLoader : IDisposable
    {
        private readonly AssetLoader _loader;

        public MonitoredAssetLoader(string scopeName = "Unknown")
        {
            _loader = new AssetLoader(scopeName);
        }

        /// <summary>
        /// Load asset asynchronously by address.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#endif
            => _loader.LoadAssetAsync<T>(address);

        /// <summary>
        /// Load asset by AssetReference
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#endif
            => _loader.LoadAssetAsync<T>(assetReference);

        /// <summary>
        /// Load multiple assets by label
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#else
        public Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#endif
            => _loader.LoadAssetsByLabelAsync<T>(label);

        /// <summary>
        /// Release asset handle
        /// </summary>
        public void Release<T>(IAssetHandle<T> handle, string address = null)
        {
            if (handle == null) return;
            handle.Release();
        }

        /// <summary>
        /// Clear all cached assets
        /// </summary>
        public void ClearCache() => _loader.ClearCache();

        public void Dispose() => _loader?.Dispose();

        /// <summary>
        /// Get the underlying AssetLoader (for advanced usage)
        /// </summary>
        public AssetLoader UnderlyingLoader => _loader;
    }
}
