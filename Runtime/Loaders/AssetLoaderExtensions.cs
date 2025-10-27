using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Monitoring;

namespace AddressableManager.Loaders
{
    /// <summary>
    /// [DEPRECATED] Extension methods for AssetLoader with automatic monitoring
    ///
    /// These extensions are NO LONGER NEEDED - AssetLoader now has built-in monitoring!
    ///
    /// OLD WAY (no longer required):
    ///   await loader.LoadAssetAsyncMonitored<T>(address, scopeName);
    ///
    /// NEW WAY (automatic):
    ///   await loader.LoadAssetAsync<T>(address); // Automatically monitored!
    ///
    /// These methods are kept for backward compatibility only.
    /// </summary>
    [System.Obsolete("AssetLoaderExtensions are deprecated. AssetLoader now has built-in monitoring. Use LoadAssetAsync() directly.", false)]
    public static class AssetLoaderExtensions
    {
        /// <summary>
        /// [DEPRECATED] Use loader.LoadAssetAsync() directly - monitoring is now automatic
        /// </summary>
        [System.Obsolete("Use LoadAssetAsync() directly - monitoring is now built-in", false)]
        public static async Task<IAssetHandle<T>> LoadAssetAsyncMonitored<T>(
            this AssetLoader loader,
            string address,
            string scopeName = "Unknown")
        {
            // Just forward to the standard method - it now has monitoring built-in
            return await loader.LoadAssetAsync<T>(address);
        }

        /// <summary>
        /// [DEPRECATED] Use loader.LoadAssetAsync() directly - monitoring is now automatic
        /// </summary>
        [System.Obsolete("Use LoadAssetAsync(AssetReference) directly - monitoring is now built-in", false)]
        public static async Task<IAssetHandle<T>> LoadAssetAsyncMonitored<T>(
            this AssetLoader loader,
            AssetReference assetReference,
            string scopeName = "Unknown")
        {
            // Just forward to the standard method - it now has monitoring built-in
            return await loader.LoadAssetAsync<T>(assetReference);
        }

        /// <summary>
        /// [DEPRECATED] Use loader.LoadAssetsByLabelAsync() directly - monitoring is now automatic
        /// </summary>
        [System.Obsolete("Use LoadAssetsByLabelAsync() directly - monitoring is now built-in", false)]
        public static async Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsyncMonitored<T>(
            this AssetLoader loader,
            string label,
            string scopeName = "Unknown")
        {
            // Just forward to the standard method - it now has monitoring built-in
            return await loader.LoadAssetsByLabelAsync<T>(label);
        }

        /// <summary>
        /// [DEPRECATED] Use handle.Release() directly - monitoring handles this automatically
        /// </summary>
        [System.Obsolete("Use handle.Release() directly - no need for monitored version", false)]
        public static void ReleaseMonitored<T>(this IAssetHandle<T> handle, string address)
        {
            if (handle == null) return;
            handle.Release();
        }
    }
}
