using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Core;
#if UNITY_EDITOR
using AddressableManager.Monitoring;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Asset loader with tiered caching (Hot/Warm/Cold)
    /// Automatically manages cache based on access patterns and memory constraints
    ///
    /// Extends AssetLoader with intelligent cache management that:
    /// - Keeps frequently accessed assets in Hot tier
    /// - Demotes rarely used assets to Cold tier
    /// - Automatically evicts assets when memory limit is reached
    /// </summary>
    public class TieredAssetLoader : IDisposable
    {
        private readonly Dictionary<Type, object> _tieredCaches = new Dictionary<Type, object>();
        private readonly TieredCacheConfig _config;
        private readonly string _scopeName;
        private readonly List<IDisposable> _activeHandles = new List<IDisposable>();
        private bool _disposed;

        // Main thread ID for thread safety checks
        private static int? _mainThreadId;

        /// <summary>
        /// Create TieredAssetLoader with optional configuration
        /// </summary>
        /// <param name="scopeName">Scope name for monitoring</param>
        /// <param name="config">Tiered cache configuration (uses Default if null)</param>
        public TieredAssetLoader(string scopeName = "Unknown", TieredCacheConfig config = null)
        {
            _scopeName = scopeName;
            _config = config ?? TieredCacheConfig.Default;

            // Capture main thread ID on first creation
            if (_mainThreadId == null)
            {
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            }
        }

        /// <summary>
        /// Get or create tiered cache for specific type
        /// </summary>
        private TieredCache<T> GetOrCreateCache<T>() where T : class
        {
            var type = typeof(T);
            if (!_tieredCaches.TryGetValue(type, out var cache))
            {
                cache = new TieredCache<T>(_config);
                _tieredCaches[type] = cache;
            }
            return (TieredCache<T>)cache;
        }

        /// <summary>
        /// Check if current thread is Unity's main thread
        /// </summary>
        private void AssertMainThread()
        {
            if (_mainThreadId.HasValue && System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId.Value)
            {
                throw new InvalidOperationException(
                    $"[TieredAssetLoader] Thread safety violation detected!\n\n" +
                    $"TieredAssetLoader must be called from Unity's main thread only.\n" +
                    $"Current thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}\n" +
                    $"Expected thread ID: {_mainThreadId.Value}\n\n" +
                    $"SOLUTION: Use ThreadSafeAssetLoader wrapper for background thread loading.\n"
                );
            }
        }

        #region Load by Address

        /// <summary>
        /// Load asset asynchronously by address with tiered caching
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
        {
            if (_disposed)
            {
                Debug.LogError("[TieredAssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(address))
            {
                Debug.LogError("[TieredAssetLoader] Address cannot be null or empty");
                return null;
            }

            AssertMainThread();

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var cache = GetOrCreateCache<T>();
            string cacheKey = $"{address}_{typeof(T).Name}";

            // Try cache first
            if (cache.TryGet(cacheKey, out var cachedHandle))
            {
                if (cachedHandle.IsValid)
                {
                    Debug.Log($"[TieredAssetLoader] Cache hit for: {address}");
                    cachedHandle.Retain();

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        true // from cache
                    );
#endif

                    return cachedHandle;
                }
                else
                {
                    // Remove invalid cached handle
                    cache.Remove(cacheKey);
                }
            }

            // Load from Addressables
            try
            {
                Debug.Log($"[TieredAssetLoader] Loading asset: {address}");
                var operation = Addressables.LoadAssetAsync<T>(address);
                await operation.Task;

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handle = new AssetHandle<T>(operation);

                    // Estimate size for cache management
                    long estimatedSize = EstimateAssetSize(operation.Result);

                    // Add to tiered cache
                    cache.Set(cacheKey, handle, estimatedSize);
                    _activeHandles.Add(handle);

                    Debug.Log($"[TieredAssetLoader] Successfully loaded: {address}");

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        false // not from cache
                    );
#endif

                    return handle;
                }
                else
                {
                    Debug.LogError($"[TieredAssetLoader] Failed to load asset: {address}. Error: {operation.OperationException}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TieredAssetLoader] Exception loading asset: {address}. Error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Load by AssetReference

        /// <summary>
        /// Load asset by AssetReference with tiered caching
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
        {
            if (_disposed)
            {
                Debug.LogError("[TieredAssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[TieredAssetLoader] Invalid AssetReference");
                return null;
            }

            AssertMainThread();

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var cache = GetOrCreateCache<T>();
            var address = assetReference.AssetGUID;
            string cacheKey = $"{address}_{typeof(T).Name}";

            // Check cache
            if (cache.TryGet(cacheKey, out var cachedHandle))
            {
                if (cachedHandle.IsValid)
                {
                    cachedHandle.Retain();

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        true
                    );
#endif

                    return cachedHandle;
                }
                else
                {
                    cache.Remove(cacheKey);
                }
            }

            // Load from Addressables
            try
            {
                var operation = assetReference.LoadAssetAsync<T>();
                await operation.Task;

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handle = new AssetHandle<T>(operation);
                    long estimatedSize = EstimateAssetSize(operation.Result);

                    cache.Set(cacheKey, handle, estimatedSize);
                    _activeHandles.Add(handle);

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        false
                    );
#endif

                    return handle;
                }
                else
                {
                    Debug.LogError($"[TieredAssetLoader] Failed to load AssetReference. Error: {operation.OperationException}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TieredAssetLoader] Exception loading AssetReference: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Pin an asset to prevent it from being evicted
        /// </summary>
        public void PinAsset<T>(string address) where T : class
        {
            var cache = GetOrCreateCache<T>();
            string cacheKey = $"{address}_{typeof(T).Name}";
            cache.Pin(cacheKey);
        }

        /// <summary>
        /// Unpin an asset to allow eviction
        /// </summary>
        public void UnpinAsset<T>(string address) where T : class
        {
            var cache = GetOrCreateCache<T>();
            string cacheKey = $"{address}_{typeof(T).Name}";
            cache.Unpin(cacheKey);
        }

        /// <summary>
        /// Get tiered cache statistics for a specific type
        /// </summary>
        public TieredCacheStats? GetCacheStats<T>() where T : class
        {
            var type = typeof(T);
            if (_tieredCaches.TryGetValue(type, out var cache))
            {
                return ((TieredCache<T>)cache).GetStatistics();
            }
            return null;
        }

        /// <summary>
        /// Get combined cache statistics across all types
        /// </summary>
        public TieredCacheStats GetCombinedStats()
        {
            var combined = new TieredCacheStats
            {
                MaxSizeBytes = _config.MaxCacheSizeBytes
            };

            foreach (var cache in _tieredCaches.Values)
            {
                var method = cache.GetType().GetMethod("GetStatistics");
                if (method != null)
                {
                    var stats = (TieredCacheStats)method.Invoke(cache, null);
                    combined.TotalEntries += stats.TotalEntries;
                    combined.HotEntries += stats.HotEntries;
                    combined.WarmEntries += stats.WarmEntries;
                    combined.ColdEntries += stats.ColdEntries;
                    combined.PinnedEntries += stats.PinnedEntries;
                    combined.TotalSizeBytes += stats.TotalSizeBytes;
                    combined.TotalAccesses += stats.TotalAccesses;
                    combined.CacheHits += stats.CacheHits;
                    combined.TotalEvictions += stats.TotalEvictions;
                    combined.TotalPromotions += stats.TotalPromotions;
                    combined.TotalDemotions += stats.TotalDemotions;
                }
            }

            combined.HitRate = combined.TotalAccesses > 0 ? (float)combined.CacheHits / combined.TotalAccesses : 0f;

            return combined;
        }

        /// <summary>
        /// Force tier evaluation for all caches
        /// </summary>
        public void EvaluateTiers()
        {
            foreach (var cache in _tieredCaches.Values)
            {
                var method = cache.GetType().GetMethod("ForceEvaluateTiers");
                method?.Invoke(cache, null);
            }
        }

        /// <summary>
        /// Force eviction for all caches
        /// </summary>
        public void ForceEviction()
        {
            foreach (var cache in _tieredCaches.Values)
            {
                var method = cache.GetType().GetMethod("ForceEviction");
                method?.Invoke(cache, null);
            }
        }

        /// <summary>
        /// Clear all caches
        /// </summary>
        public void ClearCache()
        {
            Debug.Log($"[TieredAssetLoader] Clearing all caches");

            foreach (var cache in _tieredCaches.Values)
            {
                if (cache is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _tieredCaches.Clear();
            _activeHandles.Clear();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Estimate asset memory size for cache management
        /// </summary>
        private long EstimateAssetSize(object asset)
        {
            if (asset == null) return 0;

            // Rough estimates based on asset type
            return asset switch
            {
                Texture2D texture => texture.width * texture.height * 4, // 4 bytes per pixel (RGBA)
                AudioClip audio => (long)(audio.samples * audio.channels * 2), // 16-bit audio
                Mesh mesh => mesh.vertexCount * 32, // Rough estimate
                GameObject go => 4096, // Base estimate for prefab
                ScriptableObject => 1024, // Small data objects
                _ => 1024 // Default estimate
            };
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;

            Debug.Log("[TieredAssetLoader] Disposing loader and releasing all assets");
            ClearCache();
            _disposed = true;
        }

        #endregion
    }
}
