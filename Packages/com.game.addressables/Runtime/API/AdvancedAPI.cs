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
    ///   var loader = Advanced.CreateTieredLoader("CustomScope", config);
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
        /// Create tiered loader with intelligent caching
        /// </summary>
        public static TieredAssetLoader CreateTieredLoader(string scopeName, TieredCacheConfig config = null)
        {
            return new TieredAssetLoader(scopeName, config);
        }

        #endregion

        #region Hybrid Scopes

        /// <summary>
        /// Get Global hybrid scope (singleton)
        /// </summary>
        public static HybridScope GetGlobalScope()
        {
            return HybridScope.Global;
        }

        /// <summary>
        /// Get Session hybrid scope (singleton)
        /// </summary>
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

        /// <summary>
        /// Pin asset in tiered loader to prevent eviction
        /// </summary>
        public static void PinAsset<T>(TieredAssetLoader loader, string address) where T : class
        {
            loader.PinAsset<T>(address);
        }

        /// <summary>
        /// Unpin asset in tiered loader
        /// </summary>
        public static void UnpinAsset<T>(TieredAssetLoader loader, string address) where T : class
        {
            loader.UnpinAsset<T>(address);
        }

        /// <summary>
        /// Force tier evaluation for all caches
        /// </summary>
        public static void EvaluateTiers(TieredAssetLoader loader)
        {
            loader.EvaluateTiers();
        }

        /// <summary>
        /// Force cache eviction
        /// </summary>
        public static void ForceEviction(TieredAssetLoader loader)
        {
            loader.ForceEviction();
        }

        /// <summary>
        /// Get tiered cache statistics
        /// </summary>
        public static TieredCacheStats GetTieredCacheStats<T>(TieredAssetLoader loader) where T : class
        {
            return loader.GetCacheStats<T>() ?? default;
        }

        /// <summary>
        /// Get combined cache statistics
        /// </summary>
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
        /// Load asset from background thread
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadFromBackgroundThread<T>(ThreadSafeAssetLoader loader, string address)
        {
            return await Task.Run(() => loader.LoadAssetAsync<T>(address));
        }

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
