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
    ///   Simple.ReleaseAddress("UI/Icon");
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
            //
            // KNOWN EXPOSURE, and the reason this tier is called Simple. After the Dispose() below the
            // only thing keeping the returned object alive is the loader cache's reference, and the
            // caller holds no handle with which to say "I am still using this". Tier eviction cannot
            // take it (Simple always runs on GlobalAssetScope, which builds an untiered AssetLoader, so
            // _tiering is null and every eviction path returns immediately) — but a CDN catalog update
            // can: CatalogService -> AssetLoaderRegistry.InvalidateAll -> AssetLoader.InvalidateAddress
            // drops the cache's reference, and if it was the last one the object is destroyed while the
            // caller is still holding it. The symptom is a Unity "The object of type 'X' has been
            // destroyed but you are still trying to access it" on a reference that was valid a frame
            // earlier.
            //
            // Anything that outlives a catalog update must use Standard/Advanced and hold the handle;
            // that is what the handle is for. Making invalidation retain these instead would trade the
            // crash for unbounded retention across every update, which is a product decision rather
            // than a defect fix, so it is documented here instead of decided here.
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
        /// Take an instance from the pool for <paramref name="address"/>, creating the pool if this
        /// is the first call. <b>Returns null until that pool exists.</b>
        /// </summary>
        /// <remarks>
        /// This used to block the main thread on an Addressables load to honour "auto-create"
        /// (HANDOFF_TO_SESSION_B.md P-1). It no longer does, so the first call for an address starts
        /// the pool in the background and returns null; calls after it succeed. Null also means "not
        /// available" during shutdown. Both are logged where they happen.
        ///
        /// If you need an instance from the very first call, create the pool before spawning:
        /// <code>
        /// await Standard.CreatePool("Enemies/Orc", preloadCount: 10);
        /// var enemy = Simple.Pool("Enemies/Orc");   // non-null
        /// </code>
        /// </remarks>
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

        // Warn-once latch for the deprecated Release<T> below. Half of the original defect
        // (HANDOFF_TO_SESSION_B.md A-9) was the log itself: a Debug.Log that ran in shipping
        // builds, once per call, forever, reporting work that never happened. One warning per
        // process names the problem without becoming per-frame spam.
        private static bool _releaseNoOpWarned;

        /// <summary>
        /// Releases nothing. Kept only so 4.x code keeps compiling — see the <c>[Obsolete]</c>
        /// message for what to call instead.
        ///
        /// It cannot be made to work in this shape: there is no path from an asset instance back
        /// to the cache entry holding it. <see cref="Loaders.AssetLoader"/> keys its cache by
        /// <c>(address, Type)</c> with no asset→handle reverse map, and Addressables offers no
        /// reverse lookup of its own — so the address this method would need is not recoverable
        /// from its only argument. <see cref="ReleaseAddress"/> is the same operation with the one
        /// piece of information that makes it possible.
        /// </summary>
        // Kept until 5.0.0 per repo invariant 6.
        [Obsolete("Simple.Release<T>(asset) never released anything — an asset instance cannot be " +
                  "mapped back to the cache entry holding it. Use Simple.ReleaseAddress(address) " +
                  "for one address, Simple.ClearAll() for the whole Global cache, or " +
                  "Standard.LoadGlobal<T>() when you want an IAssetHandle you dispose yourself. " +
                  "Removed in 5.0.0.", false)]
        public static void Release<T>(T asset)
        {
            if (_releaseNoOpWarned) return;
            _releaseNoOpWarned = true;

            Debug.LogWarning(
                $"[Simple.Release] Released nothing for an asset of type {typeof(T).Name}: an " +
                "asset instance cannot be mapped back to the cache entry holding it. Use " +
                "Simple.ReleaseAddress(address), Simple.ClearAll(), or Standard.LoadGlobal<T>() " +
                "for a disposable handle. Logged once per process.");
        }

        /// <summary>
        /// Release everything the Global cache holds for <paramref name="address"/> — every type
        /// it was loaded as, since the cache keys by <c>(address, Type)</c>. This is the real
        /// counterpart to <see cref="Load{T}"/> that <c>Release&lt;T&gt;(asset)</c> only looked
        /// like.
        ///
        /// Eviction is unconditional, the same documented memory-pressure semantics as
        /// <see cref="ClearAll"/> and <c>AssetLoader.ClearCache</c>: an <c>IAssetHandle</c> some
        /// other caller still holds for this address (from <c>Standard.LoadGlobal</c>, say) reads
        /// <c>IsValid == false</c> afterwards. Reach for the handle APIs instead when you need
        /// refcounted release rather than eviction.
        ///
        /// Stays <c>void</c> deliberately: a <c>bool</c> would be a sentinel with two unrelated
        /// meanings (nothing cached vs. no scope) that invariant 4 forbids. Ask
        /// <see cref="IsLoaded(string)"/> before or after if you need to know.
        /// </summary>
        public static void ReleaseAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                Debug.LogError("[Simple.ReleaseAddress] Address is null or empty — released nothing.");
                return;
            }

            // Same shutdown reasoning as Destroy() above: ask HasInstance before touching
            // Instance, so a release running from another object's OnDestroy during quit cannot
            // build a fresh DontDestroyOnLoad GameObject in the middle of teardown
            // (LIFETIME_DESIGN.md §3.6). No scope means no cache means nothing to release — that
            // is an answer, not a failure, so it is not logged.
            if (!GlobalAssetScope.HasInstance) return;

            GlobalAssetScope.Instance.Loader?.ReleaseAsset(address);
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
