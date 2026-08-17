using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Loaders;
using AddressableManager.Threading;
using AddressableManager.Pooling;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif
using AddressableManager.Scopes;

namespace AddressableManager.API
{
    /// <summary>
    /// Advanced API - Full control and customization
    ///
    /// Target users: Advanced developers, framework builders
    /// Features: Custom loaders, tiered caching, thread control, hybrid scopes
    /// Trade-offs: More complex, requires deep understanding
    ///
    /// Usage:
    ///   var loader = Advanced.CreateLoader("CustomScope", config);
    ///   var result = await loader.LoadAssetAsyncSafe<Sprite>("UI/Icon");
    /// </summary>
    public static class Advanced
    {
        #region Custom Loaders

        /// <summary>
        /// Create custom AssetLoader with monitoring
        /// </summary>
        public static AssetLoader CreateLoader(string scopeName)
        {
            return new AssetLoader(scopeName);
        }

        /// <summary>
        /// Create thread-safe loader for background loading
        /// </summary>
        public static ThreadSafeAssetLoader CreateThreadSafeLoader(string scopeName)
        {
            return new ThreadSafeAssetLoader(scopeName);
        }

        /// <summary>
        /// Create a loader with intelligent tiered caching (Hot/Warm/Cold + eviction).
        /// </summary>
        /// <remarks>
        /// Same tiering as the retired <c>CreateTieredLoader</c>, on the loader that also has
        /// single-flight join, the post-await thread guard, label/<c>Safe</c>/<c>Instantiate</c>
        /// loads, <c>ReleaseAsset</c>, and reachability from
        /// <c>AssetLoaderRegistry.InvalidateAll</c> after a CDN catalog update.
        ///
        /// <para>No ambiguity with <see cref="CreateLoader(string)"/> — different arity, and
        /// <paramref name="config"/> deliberately has no default value.</para>
        /// </remarks>
        public static AssetLoader CreateLoader(string scopeName, TieredCacheConfig config)
        {
            return new AssetLoader(scopeName, config);
        }

        /// <summary>
        /// Create tiered loader with intelligent caching
        /// </summary>
        // The message spells out the config argument because this is NOT a mechanical rename: the
        // `config = null` default here means "tiering ON, with TieredCacheConfig.Default", while
        // Advanced.CreateLoader(scopeName) is a pre-existing overload that means "tiering OFF" and
        // Advanced.CreateLoader(scopeName, null) throws ArgumentNullException. A caller following a
        // bare "use CreateLoader(scopeName, config)" from a one-argument call site therefore lands
        // on either a silently untiered loader or an exception. CHANGELOG.md covers this at length,
        // but the CHANGELOG is not what the compiler prints.
        [Obsolete("Use Advanced.CreateLoader(scopeName, config) — same tiering, on the loader that " +
                  "also has single-flight, Safe/Result loads, InstantiateAsync and catalog " +
                  "invalidation. NOTE: config is REQUIRED there and must not be null. If you are " +
                  "calling CreateTieredLoader(name) with no config, the equivalent is " +
                  "Advanced.CreateLoader(name, TieredCacheConfig.Default) — plain " +
                  "CreateLoader(name) is a different overload that disables tiering. " +
                  "Removed in 5.0.0.", false)]
        public static TieredAssetLoader CreateTieredLoader(string scopeName, TieredCacheConfig config = null)
        {
            return new TieredAssetLoader(scopeName, config);
        }

        #endregion

        #region Hybrid Scopes

        // Storage map (see HybridScope's class docs and Documentation/LIFETIME_DESIGN.md §5 step
        // 6 / Documentation/HANDOFF_TO_SESSION_B.md A-12 for the full picture): HybridScope is an
        // independent cache (storage "D") from the Facade's GlobalAssetScope (storage "A", reached
        // via AddressablesFacade.GetGlobalScope() / Simple.*/Standard.* global calls) and from
        // ScopeManager's own "Session" entry (storage "B", reached via
        // AddressablesFacade.GetSessionLoader() / Assets.LoadSession / Standard.LoadSession).
        // GetHybridGlobalScope()/GetHybridSessionScope() below are named to make that explicit at
        // the call site — GetGlobalScope()/GetSessionScope() used to share a name with
        // AddressablesFacade's own methods while returning a completely different cache.

        /// <summary>
        /// Get Global hybrid scope (singleton) — storage "D". Distinct from
        /// <see cref="AddressableManager.Facade.AddressablesFacade.GetGlobalScope"/> (storage "A");
        /// see this region's storage-map note.
        /// </summary>
        public static HybridScope GetHybridGlobalScope()
        {
            return HybridScope.Global;
        }

        /// <summary>
        /// Get Session hybrid scope (singleton) — storage "D". Distinct from the
        /// <c>AddressablesFacade</c>-owned <c>"Session"</c> entry (storage "B") returned by
        /// <see cref="AddressableManager.Facade.AddressablesFacade.GetSessionLoader"/>; see this
        /// region's storage-map note.
        /// </summary>
        public static HybridScope GetHybridSessionScope()
        {
            return HybridScope.Session;
        }

        /// <summary>
        /// Superseded by <see cref="GetHybridGlobalScope"/> — identical behavior, renamed so the
        /// call site doesn't read as <see cref="AddressableManager.Facade.AddressablesFacade.GetGlobalScope"/>'s
        /// storage. The two used to share this name while returning independent caches that both
        /// self-reported to monitoring as "Global" (HANDOFF_TO_SESSION_B.md A-12).
        /// </summary>
        [Obsolete("Use GetHybridGlobalScope() instead — identical behavior, renamed so the call site " +
                  "can't be misread as AddressablesFacade.GetGlobalScope()'s storage. Removed in 5.0.0.", false)]
        public static HybridScope GetGlobalScope()
        {
            return HybridScope.Global;
        }

        /// <summary>
        /// Superseded by <see cref="GetHybridSessionScope"/> — identical behavior, renamed so the
        /// call site doesn't read as ScopeManager's <c>"Session"</c> entry
        /// (HANDOFF_TO_SESSION_B.md A-12).
        /// </summary>
        [Obsolete("Use GetHybridSessionScope() instead — identical behavior, renamed so the call " +
                  "site can't be misread as ScopeManager's \"Session\" entry. Removed in 5.0.0.", false)]
        public static HybridScope GetSessionScope()
        {
            return HybridScope.Session;
        }

        /// <summary>
        /// Create or get named scope instance
        /// </summary>
        public static HybridScope GetNamedScope(string scopeType, string instanceName)
        {
            return HybridScope.GetNamed(scopeType, instanceName);
        }

        /// <summary>
        /// Check if named scope exists
        /// </summary>
        public static bool HasNamedScope(string scopeType, string instanceName)
        {
            return HybridScope.HasNamed(scopeType, instanceName);
        }

        /// <summary>
        /// Clear specific named scope
        /// </summary>
        public static void ClearNamedScope(string scopeType, string instanceName)
        {
            HybridScope.ClearNamed(scopeType, instanceName);
        }

        /// <summary>
        /// Clear all named scopes of a type
        /// </summary>
        public static void ClearAllNamedScopes(string scopeType)
        {
            HybridScope.ClearAllNamed(scopeType);
        }

        /// <summary>
        /// Get hybrid scope statistics
        /// </summary>
        public static HybridScopeStats GetHybridScopeStats()
        {
            return HybridScope.GetGlobalStats();
        }

        #endregion

        #region Advanced Pooling

        /// <summary>
        /// Create pool manager with custom factory
        /// </summary>
        public static AddressablePoolManager CreatePoolManager(AssetLoader loader, IPoolFactory factory = null)
        {
            return new AddressablePoolManager(loader, factory);
        }

        /// <summary>
        /// Create dynamic pool with full configuration
        /// </summary>
        public static async Task<bool> CreateDynamicPool(
            AddressablePoolManager poolManager,
            string address,
            DynamicPoolConfig config,
            int preloadCount = 0,
            Transform poolRoot = null)
        {
            return await poolManager.CreateDynamicPoolAsync(address, config, preloadCount, poolRoot);
        }

        /// <summary>
        /// Get dynamic pool statistics
        /// </summary>
        public static DynamicPoolStats? GetDynamicPoolStats(AddressablePoolManager poolManager, string address)
        {
            return poolManager.GetDynamicPoolStats(address);
        }

        /// <summary>
        /// Manually resize dynamic pool
        /// </summary>
        public static void ResizePool(AddressablePoolManager poolManager, string address, int targetCapacity)
        {
            poolManager.ResizePool(address, targetCapacity);
        }

        /// <summary>
        /// Enable auto-create pools with custom config
        /// </summary>
        public static void EnableAutoCreatePools(AddressablePoolManager poolManager, DynamicPoolConfig defaultConfig = null)
        {
            poolManager.EnableAutoCreatePools(defaultConfig);
        }

        #endregion

        #region Tiered Caching

        /// <summary>
        /// Create tiered cache with custom configuration
        /// </summary>
        public static TieredCache<T> CreateTieredCache<T>(TieredCacheConfig config = null) where T : class
        {
            return new TieredCache<T>(config);
        }

        /// <summary>
        /// Create thread-safe cache manager
        /// </summary>
        public static ThreadSafeCacheManager<T> CreateThreadSafeCache<T>(TieredCacheConfig config = null) where T : class
        {
            return new ThreadSafeCacheManager<T>(config);
        }

        // The AssetLoader-typed members below are the live surface; the TieredAssetLoader-typed
        // ones after them are their [Obsolete] predecessors, kept until 5.0.0 (repo invariant 6).
        // Every member whose signature names TieredAssetLoader must itself carry [Obsolete] —
        // otherwise it emits CS0618 from inside the package, in a user's console, at a call site
        // they cannot fix. The compile gate cannot catch that: it passes -nowarn:0618.

        /// <summary>
        /// Pin asset in a tiering-configured loader to prevent eviction. Pinning before the asset
        /// is loaded works — the request is remembered and applied when the key arrives.
        /// </summary>
        public static void PinAsset<T>(AssetLoader loader, string address)
        {
            loader.PinAsset<T>(address);
        }

        /// <summary>
        /// Unpin asset, and cancel a pin still waiting for its key.
        /// </summary>
        public static void UnpinAsset<T>(AssetLoader loader, string address)
        {
            loader.UnpinAsset<T>(address);
        }

        /// <summary>
        /// Force tier evaluation across every cached entry, of every Type.
        /// </summary>
        public static void EvaluateTiers(AssetLoader loader)
        {
            loader.EvaluateTiers();
        }

        /// <summary>
        /// Force an eviction pass, ranking candidates of every Type together.
        /// </summary>
        public static void ForceEviction(AssetLoader loader)
        {
            loader.ForceEviction();
        }

        /// <summary>
        /// Get tiered cache statistics restricted to entries cached as <typeparamref name="T"/>.
        /// </summary>
        /// <remarks>
        /// An all-zero struct means either "tiering is off on this loader" or "nothing of this type
        /// is cached"; <see cref="AssetLoader.TieringEnabled"/> tells them apart. Call
        /// <see cref="AssetLoader.GetTieredCacheStats{T}()"/> directly if you want the nullable form.
        /// </remarks>
        public static TieredCacheStats GetTieredCacheStats<T>(AssetLoader loader)
        {
            return loader.GetTieredCacheStats<T>() ?? default;
        }

        /// <summary>
        /// Get cache statistics across every cached entry, of every Type.
        /// </summary>
        public static TieredCacheStats GetCombinedCacheStats(AssetLoader loader)
        {
            return loader.GetTieredCacheStats();
        }

        /// <summary>
        /// Pin asset in tiered loader to prevent eviction
        /// </summary>
        [Obsolete("Use Advanced.PinAsset<T>(AssetLoader, string) with a loader built by " +
                  "Advanced.CreateLoader(scopeName, config). Removed in 5.0.0.", false)]
        public static void PinAsset<T>(TieredAssetLoader loader, string address) where T : class
        {
            loader.PinAsset<T>(address);
        }

        /// <summary>
        /// Unpin asset in tiered loader
        /// </summary>
        [Obsolete("Use Advanced.UnpinAsset<T>(AssetLoader, string) with a loader built by " +
                  "Advanced.CreateLoader(scopeName, config). Removed in 5.0.0.", false)]
        public static void UnpinAsset<T>(TieredAssetLoader loader, string address) where T : class
        {
            loader.UnpinAsset<T>(address);
        }

        /// <summary>
        /// Force tier evaluation for all caches
        /// </summary>
        [Obsolete("Use Advanced.EvaluateTiers(AssetLoader). Removed in 5.0.0.", false)]
        public static void EvaluateTiers(TieredAssetLoader loader)
        {
            loader.EvaluateTiers();
        }

        /// <summary>
        /// Force cache eviction
        /// </summary>
        [Obsolete("Use Advanced.ForceEviction(AssetLoader). Removed in 5.0.0.", false)]
        public static void ForceEviction(TieredAssetLoader loader)
        {
            loader.ForceEviction();
        }

        /// <summary>
        /// Get tiered cache statistics
        /// </summary>
        [Obsolete("Use Advanced.GetTieredCacheStats<T>(AssetLoader). Removed in 5.0.0.", false)]
        public static TieredCacheStats GetTieredCacheStats<T>(TieredAssetLoader loader) where T : class
        {
            return loader.GetCacheStats<T>() ?? default;
        }

        /// <summary>
        /// Get combined cache statistics
        /// </summary>
        [Obsolete("Use Advanced.GetCombinedCacheStats(AssetLoader). Removed in 5.0.0.", false)]
        public static TieredCacheStats GetCombinedCacheStats(TieredAssetLoader loader)
        {
            return loader.GetCombinedStats();
        }

        #endregion

        #region Result Pattern & Error Handling

        /// <summary>
        /// Load with full Result pattern
        /// </summary>
        public static async Task<LoadResult<IAssetHandle<T>>> LoadWithResult<T>(AssetLoader loader, string address)
        {
            return await loader.LoadAssetAsyncSafe<T>(address);
        }

        /// <summary>
        /// Load by AssetReference with Result pattern
        /// </summary>
        public static async Task<LoadResult<IAssetHandle<T>>> LoadWithResult<T>(AssetLoader loader, AssetReference assetReference)
        {
            return await loader.LoadAssetAsyncSafe<T>(assetReference);
        }

        /// <summary>
        /// Load multiple by label with Result pattern
        /// </summary>
        public static async Task<LoadResult<List<IAssetHandle<T>>>> LoadByLabelWithResult<T>(AssetLoader loader, string label)
        {
            return await loader.LoadAssetsByLabelAsyncSafe<T>(label);
        }

        #endregion

        #region Smart Handles

        /// <summary>
        /// Convert handle to SmartHandle
        /// </summary>
        public static SmartAssetHandle<T> ToSmart<T>(IAssetHandle<T> handle, bool autoRelease = true)
        {
            return handle?.ToSmart(autoRelease);
        }

        /// <summary>
        /// Load directly as SmartHandle
        /// </summary>
        public static async Task<SmartAssetHandle<T>> LoadSmart<T>(AssetLoader loader, string address, bool autoRelease = true)
        {
            return await loader.LoadAssetSmartAsync<T>(address, autoRelease);
        }

        #endregion

        #region Threading

        /// <summary>
        /// Check if current thread is Unity main thread
        /// </summary>
        public static bool IsMainThread()
        {
            return UnityMainThreadDispatcher.IsMainThread;
        }

        /// <summary>
        /// Enqueue action to main thread
        /// </summary>
        public static void EnqueueMainThread(Action action)
        {
            UnityMainThreadDispatcher.Enqueue(action);
        }

        /// <summary>
        /// Enqueue and wait for completion
        /// </summary>
        public static void EnqueueAndWait(Action action)
        {
            UnityMainThreadDispatcher.EnqueueAndWait(action);
        }

        /// <summary>
        /// Load asset from a background thread. Internally hops to the thread pool
        /// before calling the loader; the loader itself bounces back to the main thread
        /// before touching Addressables.
        /// </summary>
#if UNITASK_PRESENT
        public static async UniTask<IAssetHandle<T>> LoadFromBackgroundThread<T>(ThreadSafeAssetLoader loader, string address)
        {
            return await UniTask.RunOnThreadPool(() => loader.LoadAssetAsync<T>(address));
        }
#else
        public static async Task<IAssetHandle<T>> LoadFromBackgroundThread<T>(ThreadSafeAssetLoader loader, string address)
        {
            return await Task.Run(() => loader.LoadAssetAsync<T>(address));
        }
#endif

        #endregion

        #region Validation

        /// <summary>
        /// Set validation mode with full control
        /// </summary>
        public static void SetValidationMode(ValidationMode mode)
        {
            AssetValidator.CurrentMode = mode;
        }

        /// <summary>
        /// Enable specific validation flags
        /// </summary>
        public static void EnableValidation(ValidationMode mode)
        {
            AssetValidator.Enable(mode);
        }

        /// <summary>
        /// Disable specific validation flags
        /// </summary>
        public static void DisableValidation(ValidationMode mode)
        {
            AssetValidator.Disable(mode);
        }

        /// <summary>
        /// Check if validation mode is enabled
        /// </summary>
        public static bool IsValidationEnabled(ValidationMode mode)
        {
            return AssetValidator.IsEnabled(mode);
        }

        /// <summary>
        /// Validate address manually
        /// </summary>
        public static bool ValidateAddress(string address, out string error)
        {
            return AssetValidator.ValidateAddress(address, out error);
        }

        /// <summary>
        /// Validate AssetReference manually
        /// </summary>
        public static bool ValidateAssetReference(AssetReference assetReference, out string error)
        {
            return AssetValidator.ValidateAssetReference(assetReference, out error);
        }

        /// <summary>
        /// Get load count for address
        /// </summary>
        public static int GetLoadCount(string address)
        {
            return AssetValidator.GetLoadCount(address);
        }

        /// <summary>
        /// Get validation statistics
        /// </summary>
        public static ValidationStats GetValidationStats()
        {
            return AssetValidator.GetStatistics();
        }

        /// <summary>
        /// Reset validation tracking
        /// </summary>
        public static void ResetValidation()
        {
            AssetValidator.Reset();
        }

        #endregion

        #region Custom Configurations

        /// <summary>
        /// Create custom tiered cache config
        /// </summary>
        public static TieredCacheConfig CreateCacheConfig(
            long maxSizeBytes = 100 * 1024 * 1024,
            float promoteToHotThreshold = 15.0f,
            float evictionTriggerRatio = 0.9f,
            bool enableAutoTiering = true,
            bool enableAutoEviction = true)
        {
            return new TieredCacheConfig
            {
                MaxCacheSizeBytes = maxSizeBytes,
                PromoteToHotThreshold = promoteToHotThreshold,
                EvictionTriggerRatio = evictionTriggerRatio,
                EnableAutoTiering = enableAutoTiering,
                EnableAutoEviction = enableAutoEviction
            };
        }

        /// <summary>
        /// Create custom dynamic pool config
        /// </summary>
        public static DynamicPoolConfig CreatePoolConfig(
            int initialCapacity = 10,
            int minSize = 5,
            int maxSize = 100,
            float growThreshold = 0.8f,
            float shrinkThreshold = 0.3f,
            bool enableAutoResize = true)
        {
            return new DynamicPoolConfig
            {
                InitialCapacity = initialCapacity,
                MinSize = minSize,
                MaxSize = maxSize,
                GrowThreshold = growThreshold,
                ShrinkThreshold = shrinkThreshold,
                EnableAutoResize = enableAutoResize
            };
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Get comprehensive system statistics
        /// </summary>
        public static SystemDiagnostics GetSystemDiagnostics()
        {
            return new SystemDiagnostics
            {
                HybridScopes = HybridScope.GetGlobalStats(),
                Validation = AssetValidator.GetStatistics(),
                IsMainThread = UnityMainThreadDispatcher.IsMainThread
            };
        }

        #endregion
    }

    /// <summary>
    /// Comprehensive system diagnostics
    /// </summary>
    public struct SystemDiagnostics
    {
        public HybridScopeStats HybridScopes;
        public ValidationStats Validation;
        public bool IsMainThread;

        public override string ToString()
        {
            return $"SystemDiagnostics:\n" +
                   $"  {HybridScopes}\n" +
                   $"  {Validation}\n" +
                   $"  Main Thread: {IsMainThread}";
        }
    }
}
