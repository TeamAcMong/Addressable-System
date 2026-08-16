using System;
using System.Threading.Tasks;
using UnityEngine;
using AddressableManager.Core;
using AddressableManager.Facade;
using AddressableManager.Scopes;

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
            // GlobalAssetScope.Instance legitimately returns null once the process is shutting
            // down and no instance exists (LIFETIME_DESIGN.md §3.6) — checking the getter's own
            // return is enough here (unlike a HasInstance pre-check, which would also refuse the
            // very first call ever made, before any instance has been built). default is already
            // this method's documented failure value (invariant 4) — no new sentinel.
            var scope = GlobalAssetScope.Instance;
            if (scope == null) return default;

            var handle = await scope.Loader.LoadAssetAsync<T>(address);
            if (handle == null || !handle.IsValid)
            {
                handle?.Dispose();
                return default;
            }

            // Simple hands back the raw asset, not the handle, so this call's own reference
            // (the one the handle was born with) has nowhere else to go. Give it back here —
            // the loader's cache took its own reference in CacheHandle(), so the asset stays
            // alive and reachable for the next Load() even after this Dispose() decrements.
            // Without this every call orphaned one reference (HANDOFF_TO_SESSION_B.md A-4).
            var asset = handle.Asset;
            handle.Dispose();
            return asset;
        }

        /// <summary>
        /// Load asset with error handling
        /// Returns tuple (asset, success)
        /// </summary>
        public static async Task<(T asset, bool success)> TryLoad<T>(string address)
        {
            try
            {
                // See Load<T>'s comment: check the getter's own return rather than a HasInstance
                // pre-check, so the very first call ever made still builds the instance.
                var scope = GlobalAssetScope.Instance;
                if (scope == null) return (default, false);

                var handle = await scope.Loader.LoadAssetAsync<T>(address);
                if (handle != null && handle.IsValid)
                {
                    // Same reference hand-back as Load<T> — see its comment (A-4).
                    var asset = handle.Asset;
                    handle.Dispose();
                    return (asset, true);
                }
                handle?.Dispose();
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
            // See Load<T>'s comment (§3.6): check the getter's own return, not a HasInstance
            // pre-check — this must still build the instance on an ordinary first call.
            var scope = GlobalAssetScope.Instance;
            if (scope == null) return null;
            return await scope.Loader.InstantiateAsync(address);
        }

        /// <summary>
        /// Instantiate at position
        /// </summary>
        public static async Task<GameObject> Spawn(string address, Vector3 position)
        {
            var scope = GlobalAssetScope.Instance;
            if (scope == null) return null;
            return await scope.Loader.InstantiateAsync(address, position, Quaternion.identity);
        }

        /// <summary>
        /// Instantiate with full transform
        /// </summary>
        public static async Task<GameObject> Spawn(string address, Vector3 position, Quaternion rotation)
        {
            var scope = GlobalAssetScope.Instance;
            if (scope == null) return null;
            return await scope.Loader.InstantiateAsync(address, position, rotation);
        }

        /// <summary>
        /// Destroy instantiated GameObject
        /// </summary>
        public static void Destroy(GameObject instance)
        {
            if (instance == null) return;

            // Spawn() goes through scope.Loader.InstantiateAsync(), which tracks the instance so
            // Addressables' own instance refcount balances. Object.Destroy() never tells
            // Addressables anything, so it left a permanently-retained bundle reference behind
            // (HANDOFF_TO_SESSION_B.md A-3). Route through the same loader Spawn() used instead.
            //
            // GlobalAssetScope.HasInstance is asked BEFORE touching Instance: this method can run
            // from another object's OnDestroy during application quit / Play-mode exit (that is
            // exactly what pooled-instance cleanup looks like), and Instance must not build a
            // fresh DontDestroyOnLoad GameObject in the middle of teardown just to hand back a
            // reference to a process that is going away anyway (LIFETIME_DESIGN.md §3.6). No scope
            // ever existed → nothing to give back, fall through to plain Destroy. Scope alive →
            // release through it, same as Spawn() used. Shutting down → plain Destroy.
            //
            // Fallback is also required outside of shutdown: ReleaseInstance() returns false for a
            // GameObject Addressables never owned (e.g. one handed out by Simple.Pool), and that
            // one still needs destroying.
            if (GlobalAssetScope.HasInstance &&
                GlobalAssetScope.Instance.Loader.ReleaseInstance(instance))
            {
                return;
            }

            UnityEngine.Object.Destroy(instance);
        }

        #endregion

        #region Pooling (Auto)

        /// <summary>
        /// Spawn from pool (auto-creates pool if needed)
        /// </summary>
        public static GameObject Pool(string address)
        {
            // AddressablesFacade.Instance now legitimately returns null during shutdown
            // (LIFETIME_DESIGN.md §3.6) — check the getter's own return, not a HasInstance
            // pre-check, so an ordinary first call still builds the Facade.
            var poolManager = AddressablesFacade.Instance?.GetPoolManager();
            if (poolManager == null) return null;

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
            AddressablesFacade.Instance?.GetPoolManager()?.Despawn(address, instance);
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
            AddressablesFacade.Instance?.ClearGlobalCache();
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
        /// Check if this address is loaded (cached under any type).
        /// The cache keys by (address, Type) (see <see cref="Loaders.AssetLoader"/>), so an
        /// address loaded as two different types has two independent entries — prefer
        /// <see cref="IsLoaded{T}"/> when the type is known, since that is the exact cache key
        /// <see cref="Load{T}"/> would hit.
        /// </summary>
        public static bool IsLoaded(string address)
        {
            // Facade can now legitimately return null during shutdown (LIFETIME_DESIGN.md §3.6) —
            // "not loaded" is already this method's honest answer in that state.
            return Facade?.GetGlobalScope()?.Loader?.IsCached(address) ?? false;
        }

        /// <summary>
        /// Check if this address is loaded (cached) as type <typeparamref name="T"/> — an exact
        /// match of the (address, Type) cache key <see cref="Load{T}"/> uses.
        /// </summary>
        public static bool IsLoaded<T>(string address)
        {
            return Facade?.GetGlobalScope()?.Loader?.IsCached<T>(address) ?? false;
        }

        /// <summary>
        /// Get memory stats (simple)
        /// </summary>
        public static (int loadedAssets, int pooledObjects) GetStats()
        {
            // GetCacheStats() is (cachedAssets, activeHandles): every cached handle also lives in
            // activeHandles (CacheHandle() tracks it there too), so activeHandles is already the
            // superset and adding cachedAssets on top double-counted every one of them
            // (HANDOFF_TO_SESSION_B.md A-13).
            //
            // Facade can now legitimately return null during shutdown (LIFETIME_DESIGN.md §3.6) —
            // (0, 0) is the honest "nothing to report" answer in that state, not a sentinel.
            var facade = Facade;
            if (facade == null) return (0, 0);

            var (_, activeHandles) = facade.GetGlobalScope()?.Loader?.GetCacheStats() ?? (0, 0);
            var (_, pooledObjects) = facade.GetPoolManager()?.GetTotalStats() ?? (0, 0);

            return (activeHandles, pooledObjects);
        }

        #endregion
    }
}
