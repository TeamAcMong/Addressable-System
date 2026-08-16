using UnityEngine;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#else
using System.Threading.Tasks;
#endif

namespace AddressableManager.Core
{
    /// <summary>
    /// Extension methods for IAssetHandle to enable SmartAssetHandle conversion
    /// </summary>
    public static class AssetHandleExtensions
    {
        /// <summary>
        /// Atomic try-increment: takes a reference and returns true, or returns false when the
        /// handle is already dead. The single-step form of "check IsValid, then Retain()", which
        /// cannot be written safely by hand — the handle can die between the two statements, and
        /// Retain() throws on a dead handle.
        ///
        /// Usage:
        ///   if (handle.TryRetain()) { /* the reference is yours; Release() it */ }
        /// </summary>
        public static bool TryRetain<T>(this IAssetHandle<T> handle)
        {
            if (handle == null) return false;

            // Every handle this package produces takes the atomic path.
            if (handle is IRetainableHandle retainable) return retainable.TryRetain();

            // A foreign IAssetHandle implementation has only the two-step form to offer. Its
            // Retain() may throw on a dead handle (that is the documented contract), so the
            // race this method exists to close is downgraded to a caught exception rather than
            // an escaping one.
            try
            {
                if (!handle.IsValid) return false;

                handle.Retain();
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AssetHandle] TryRetain fell back to Retain() and it failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Convert IAssetHandle to SmartAssetHandle for automatic memory management
        ///
        /// Usage:
        ///   using var handle = (await loader.LoadAssetAsync<Sprite>("UI/Icon")).ToSmart();
        ///   // Auto-released when scope exits
        ///
        /// With autoRelease (the default) the wrapper takes over the caller's reference rather
        /// than adding one, so the handle passed in must not be released separately afterwards.
        /// With autoRelease:false the wrapper takes nothing and the caller keeps releasing the
        /// handle itself.
        /// </summary>
        /// <param name="handle">Handle to wrap</param>
        /// <param name="autoRelease">Take over the caller's reference and release it on dispose (default: true)</param>
        /// <returns>SmartAssetHandle wrapper</returns>
        public static SmartAssetHandle<T> ToSmart<T>(this IAssetHandle<T> handle, bool autoRelease = true)
        {
            if (handle == null) return null;
            return new SmartAssetHandle<T>(handle, autoRelease);
        }

        // The awaitable-taking overloads below must match whatever AssetLoader returns. UniTask<T>
        // has no conversion to Task<T>, so a Task-only ToSmart makes the documented
        // `await loader.LoadAssetAsync<T>(...).ToSmart()` a compile error in every project that
        // installs UniTask — the branch no compile gate here can see.

        /// <summary>
        /// Convert the loader's awaitable of IAssetHandle to an awaitable of SmartAssetHandle.
        /// This allows chaining: await loader.LoadAssetAsync().ToSmart()
        ///
        /// Usage:
        ///   using var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
        /// </summary>
#if UNITASK_PRESENT
        public static async UniTask<SmartAssetHandle<T>> ToSmart<T>(this UniTask<IAssetHandle<T>> handleTask, bool autoRelease = true)
#else
        public static async Task<SmartAssetHandle<T>> ToSmart<T>(this Task<IAssetHandle<T>> handleTask, bool autoRelease = true)
#endif
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
#if UNITASK_PRESENT
        public static async UniTask<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
#else
        public static async Task<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
#endif
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
#if UNITASK_PRESENT
        public static async UniTask<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
#else
        public static async Task<SmartAssetHandle<T>> LoadAssetSmartAsync<T>(
#endif
            this Loaders.AssetLoader loader,
            UnityEngine.AddressableAssets.AssetReference assetReference,
            bool autoRelease = true)
        {
            var handle = await loader.LoadAssetAsync<T>(assetReference);
            return handle?.ToSmart(autoRelease);
        }
    }
}
