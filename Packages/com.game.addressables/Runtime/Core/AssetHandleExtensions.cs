using System.Threading.Tasks;

namespace AddressableManager.Core
{
    /// <summary>
    /// Extension methods for IAssetHandle to enable SmartAssetHandle conversion
    /// </summary>
    public static class AssetHandleExtensions
    {
        /// <summary>
        /// Convert IAssetHandle to SmartAssetHandle for automatic memory management
        ///
        /// Usage:
        ///   using var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
        ///   // Auto-released when scope exits
        /// </summary>
        /// <param name="handle">Handle to wrap</param>
        /// <param name="autoRelease">Enable auto-release on dispose (default: true)</param>
        /// <returns>SmartAssetHandle wrapper</returns>
        public static SmartAssetHandle<T> ToSmart<T>(this IAssetHandle<T> handle, bool autoRelease = true)
        {
            if (handle == null) return null;
            return new SmartAssetHandle<T>(handle, autoRelease);
        }

        /// <summary>
        /// Convert Task of IAssetHandle to Task of SmartAssetHandle
        /// This allows chaining: await loader.LoadAsync().ToSmart()
        ///
        /// Usage:
        ///   using var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
        /// </summary>
        public static async Task<SmartAssetHandle<T>> ToSmart<T>(this Task<IAssetHandle<T>> handleTask, bool autoRelease = true)
        {
            var handle = await handleTask;
            return handle?.ToSmart(autoRelease);
        }

        /// <summary>
        /// Load asset and wrap in SmartAssetHandle (extension for AssetLoader)
        ///
        /// Usage:
        ///   using var handle = await loader.LoadAssetSmartAsync<Sprite>("UI/Icon");
        ///   // Auto-released when scope exits
        /// </summary>
        public static async Task<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
            this Loaders.AssetLoader loader,
            string address,
            bool autoRelease = true)
        {
            var handle = await loader.LoadAssetAsync<T>(address);
            return handle?.ToSmart(autoRelease);
        }

        /// <summary>
        /// Load asset by AssetReference and wrap in SmartAssetHandle
        /// </summary>
        public static async Task<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
            this Loaders.AssetLoader loader,
            UnityEngine.AddressableAssets.AssetReference assetReference,
            bool autoRelease = true)
        {
            var handle = await loader.LoadAssetAsync<T>(assetReference);
            return handle?.ToSmart(autoRelease);
        }
    }
}
