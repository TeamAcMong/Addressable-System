using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Core
{
    /// <summary>
    /// Tiered cache system with Hot/Warm/Cold tiers.
    /// Automatically manages cache based on access patterns and memory constraints.
    /// Not thread-safe — intended for single (main) thread use; see <see cref="ThreadSafeCacheManager{T}"/>
    /// for a multi-thread-safe equivalent.
    ///
    /// <para><b>IMPORTANT — Set() can silently release your own handle:</b> calling <see cref="Set"/>
    /// for a key that already has a live entry releases the <c>handle</c> argument passed in as a
    /// rejected duplicate (see that method's doc). <c>Set()</c> stays <c>void</c>, so the only
    /// signal is <c>IsValid</c> flipping to <c>false</c> — always check it before reading or handing
    /// off a handle you just passed to <c>Set()</c>.</para>
    /// </summary>
    public class TieredCache<T> : IDisposable where T : class
    {
        private readonly Dictionary<string, CacheEntry<T>> _cache = new Dictionary<string, CacheEntry<T>>();
        private readonly TieredCacheConfig _config;
        private float _lastEvaluationTime;
        private long _currentCacheSize;
        private bool _disposed;

        // Statistics
        private int _totalAccesses;
        private int _cacheHits;
        private int _totalEvictions;
        private int _totalPromotions;
        private int _totalDemotions;

        public TieredCache(TieredCacheConfig config = null)
        {
            _config = config ?? TieredCacheConfig.Default;

            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid TieredCacheConfig: {error}");
            }

            _lastEvaluationTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Add or update entry in cache. On success the cache holds its own reference to
        /// <paramref name="handle"/> (taken via <c>TryRetain()</c>), independent of the caller's own
        /// reference — the caller must still <c>Release()</c>/<c>Dispose()</c> its copy as usual.
        ///
        /// If <paramref name="handle"/> is already dead, it is refused rather than stored: a dead
        /// entry could never be served back out by <see cref="TryGet"/> anyway.
        ///
        /// If <paramref name="key"/> already has a <em>live</em> entry, this call does not replace
        /// it — only the access time is bumped. The handle passed in this call is not stored, so
        /// unless it is the exact object already cached, this call releases the reference it was
        /// given back to the caller (i.e. it does not adopt it and does not leak it); the caller
        /// must not use that handle as if the cache had taken ownership of it.
        ///
        /// <para><b>IMPORTANT:</b> because this call can release the caller's own
        /// <paramref name="handle"/> reference as that "losing" duplicate (e.g. two concurrent loads
        /// for the same key), <paramref name="handle"/> may already be invalid by the time this call
        /// returns — check <c>IsValid</c> before reading or handing off <paramref name="handle"/>
        /// afterwards. When <c>_config.LogTierOperations</c> is enabled, every such rejection is
        /// logged so the loss is observable even without checking <c>IsValid</c>.</para>
        ///
        /// If the existing entry's handle has died without going through this cache (e.g.
        /// force-released by its owning loader), it is treated the same way <see cref="TryGet"/>
        /// treats it — as a stale/missing entry — so the incoming handle replaces it instead of being
        /// rejected into a zombie slot.
        /// </summary>
        public void Set(string key, IAssetHandle<T> handle, long estimatedSize = 0)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TieredCache<T>));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            // If entry exists and is still alive, just update access. The handle passed in is not
            // stored — release the reference we were given back unless it is the very object already
            // cached.
            if (_cache.TryGetValue(key, out var existingEntry))
            {
                if (existingEntry.Handle.IsValid)
                {
                    existingEntry.RecordAccess();
                    if (!ReferenceEquals(existingEntry.Handle, handle))
                    {
                        if (_config.LogTierOperations)
                        {
                            Debug.LogWarning($"[TieredCache] Set() rejected a duplicate handle for already-cached key '{key}'; the caller's handle was released and is no longer valid.");
                        }
                        handle.Release();
                    }
                    return;
                }

                // Stale entry whose handle died outside this cache — mirror TryGet()'s handling:
                // drop the zombie slot instead of rejecting the caller's freshly loaded handle into
                // it (which would silently unload the very asset that was just loaded).
                _currentCacheSize -= existingEntry.EstimatedSize;
                _cache.Remove(key);
            }

            // The cache takes its own reference; a handle that is already dead is refused instead of
            // stored.
            if (!handle.TryRetain())
                return;

            // Create new entry
            var entry = new CacheEntry<T>(key, handle, estimatedSize);
            _cache[key] = entry;
            _currentCacheSize += estimatedSize;

            // Check if we need to evict
            if (_config.EnableAutoEviction && _config.MaxCacheSizeBytes > 0)
            {
                float currentRatio = (float)_currentCacheSize / _config.MaxCacheSizeBytes;
                if (currentRatio >= _config.EvictionTriggerRatio)
                {
                    PerformEviction();
                }
            }
        }

        /// <summary>
        /// Try to get entry from cache. On success, the returned handle carries a reference the
        /// caller now owns and must <c>Release()</c>/<c>Dispose()</c> — <c>TryRetain()</c> is the
        /// validity test, so a handle that died without going through this cache (e.g. force-released
        /// by its owning loader) is treated as a miss and its entry is dropped.
        /// </summary>
        public bool TryGet(string key, out IAssetHandle<T> handle)
        {
            if (_disposed)
            {
                handle = null;
                return false;
            }

            _totalAccesses++;

            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.Handle.TryRetain())
                {
                    entry.RecordAccess();
                    handle = entry.Handle;
                    _cacheHits++;

                    // Periodic tier evaluation
                    if (_config.EnableAutoTiering)
                    {
                        float timeSinceEvaluation = Time.realtimeSinceStartup - _lastEvaluationTime;
                        if (timeSinceEvaluation >= _config.TierEvaluationInterval)
                        {
                            EvaluateAndAdjustTiers();
                            _lastEvaluationTime = Time.realtimeSinceStartup;
                        }
                    }

                    return true;
                }

                // Entry's handle is already dead (its last reference went away outside this cache).
                // Nothing left to release here — drop the stale entry and report a miss.
                _currentCacheSize -= entry.EstimatedSize;
                _cache.Remove(key);
            }

            handle = null;
            return false;
        }

        /// <summary>
        /// Check if key exists in cache
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _cache.ContainsKey(key);
        }

        /// <summary>
        /// Remove entry from cache, releasing the cache's own reference to its handle.
        /// </summary>
        public bool Remove(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.Handle?.Release();
                _currentCacheSize -= entry.EstimatedSize;
                _cache.Remove(key);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pin an entry to prevent eviction
        /// </summary>
        public void Pin(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.IsPinned = true;
                entry.Tier = CacheTier.Hot;
            }
        }

        /// <summary>
        /// Unpin an entry to allow eviction
        /// </summary>
        public void Unpin(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.IsPinned = false;
            }
        }

        /// <summary>
        /// Clear all cache entries, releasing the cache's own reference to every handle it holds.
        /// </summary>
        public void Clear()
        {
            foreach (var entry in _cache.Values)
            {
                entry.Handle?.Release();
            }

            _cache.Clear();
            _currentCacheSize = 0;
            ResetStatistics();
        }

        /// <summary>
        /// Evaluate all entries and adjust their tiers based on access patterns
        /// </summary>
        private void EvaluateAndAdjustTiers()
        {
            foreach (var entry in _cache.Values)
            {
                if (entry.IsPinned)
                    continue; // Skip pinned entries

                float score = entry.CalculateTierScore();
                CacheTier oldTier = entry.Tier;
                CacheTier newTier = oldTier;

                // Determine new tier based on score
                if (score >= _config.PromoteToHotThreshold)
                {
                    newTier = CacheTier.Hot;
                }
                else if (score >= _config.PromoteToWarmThreshold)
                {
                    newTier = CacheTier.Warm;
                }
                else if (score <= _config.DemoteToColdThreshold)
                {
                    newTier = CacheTier.Cold;
                }
                else if (oldTier == CacheTier.Hot && score < _config.DemoteToWarmThreshold)
                {
                    newTier = CacheTier.Warm;
                }

                // Apply tier change
                if (newTier != oldTier)
                {
                    entry.Tier = newTier;

                    if (newTier < oldTier) // Promotion (Hot=0, Cold=2)
                    {
                        _totalPromotions++;
                        if (_config.LogTierOperations)
                        {
                            Debug.Log($"[TieredCache] Promoted {entry.Key}: {oldTier} → {newTier} (score: {score:F2})");
                        }
                    }
                    else // Demotion
                    {
                        _totalDemotions++;
                        if (_config.LogTierOperations)
                        {
                            Debug.Log($"[TieredCache] Demoted {entry.Key}: {oldTier} → {newTier} (score: {score:F2})");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Perform eviction to reduce cache size
        /// </summary>
        private void PerformEviction()
        {
            if (_config.MaxCacheSizeBytes <= 0)
                return;

            long targetSize = (long)(_config.MaxCacheSizeBytes * _config.EvictionTargetRatio);
            long amountToEvict = _currentCacheSize - targetSize;

            if (amountToEvict <= 0)
                return;

            // Get candidates for eviction (Cold tier first, then by score)
            var candidates = _cache.Values
                .Where(e => !e.IsPinned)
                .OrderByDescending(e => e.Tier) // Cold first
                .ThenBy(e => e.CalculateTierScore()) // Lowest score first
                .ToList();

            long evictedSize = 0;
            int evictedCount = 0;
            var keysToRemove = new List<string>();

            foreach (var entry in candidates)
            {
                if (evictedSize >= amountToEvict)
                    break;

                // Only evict if score is below threshold or in Cold tier
                if (entry.Tier == CacheTier.Cold || entry.CalculateTierScore() < _config.EvictionScoreThreshold)
                {
                    keysToRemove.Add(entry.Key);
                    evictedSize += entry.EstimatedSize;
                    evictedCount++;

                    if (_config.LogTierOperations)
                    {
                        Debug.Log($"[TieredCache] Evicted {entry.Key} from {entry.Tier} tier (score: {entry.CalculateTierScore():F2})");
                    }
                }
            }

            // Remove evicted entries, releasing the cache's own reference to each handle
            foreach (var key in keysToRemove)
            {
                var entry = _cache[key];
                entry.Handle?.Release();
                _cache.Remove(key);
            }

            _currentCacheSize -= evictedSize;
            _totalEvictions += evictedCount;

            if (_config.LogTierOperations)
            {
                Debug.Log($"[TieredCache] Eviction complete: {evictedCount} entries, {evictedSize / 1024}KB freed");
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public TieredCacheStats GetStatistics()
        {
            var hotCount = _cache.Values.Count(e => e.Tier == CacheTier.Hot);
            var warmCount = _cache.Values.Count(e => e.Tier == CacheTier.Warm);
            var coldCount = _cache.Values.Count(e => e.Tier == CacheTier.Cold);
            var pinnedCount = _cache.Values.Count(e => e.IsPinned);

            return new TieredCacheStats
            {
                TotalEntries = _cache.Count,
                HotEntries = hotCount,
                WarmEntries = warmCount,
                ColdEntries = coldCount,
                PinnedEntries = pinnedCount,
                TotalSizeBytes = _currentCacheSize,
                MaxSizeBytes = _config.MaxCacheSizeBytes,
                TotalAccesses = _totalAccesses,
                CacheHits = _cacheHits,
                HitRate = _totalAccesses > 0 ? (float)_cacheHits / _totalAccesses : 0f,
                TotalEvictions = _totalEvictions,
                TotalPromotions = _totalPromotions,
                TotalDemotions = _totalDemotions
            };
        }

        /// <summary>
        /// Get entries by tier (internal use only)
        /// </summary>
        internal IEnumerable<CacheEntry<T>> GetEntriesByTier(CacheTier tier)
        {
            return _cache.Values.Where(e => e.Tier == tier);
        }

        /// <summary>
        /// Reset statistics counters
        /// </summary>
        public void ResetStatistics()
        {
            _totalAccesses = 0;
            _cacheHits = 0;
            _totalEvictions = 0;
            _totalPromotions = 0;
            _totalDemotions = 0;
        }

        /// <summary>
        /// Force immediate tier evaluation
        /// </summary>
        public void ForceEvaluateTiers()
        {
            if (_config.EnableAutoTiering)
            {
                EvaluateAndAdjustTiers();
                _lastEvaluationTime = Time.realtimeSinceStartup;
            }
        }

        /// <summary>
        /// Force immediate eviction to target size
        /// </summary>
        public void ForceEviction()
        {
            if (_config.EnableAutoEviction)
            {
                PerformEviction();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Statistics for tiered cache
    /// </summary>
    public struct TieredCacheStats
    {
        public int TotalEntries;
        public int HotEntries;
        public int WarmEntries;
        public int ColdEntries;
        public int PinnedEntries;
        public long TotalSizeBytes;
        public long MaxSizeBytes;
        public int TotalAccesses;
        public int CacheHits;
        public float HitRate;
        public int TotalEvictions;
        public int TotalPromotions;
        public int TotalDemotions;

        public float UsageRatio => MaxSizeBytes > 0 ? (float)TotalSizeBytes / MaxSizeBytes : 0f;

        public override string ToString()
        {
            return $"Cache: {TotalEntries} entries (Hot:{HotEntries}, Warm:{WarmEntries}, Cold:{ColdEntries}), " +
                   $"Size: {TotalSizeBytes / 1024}KB / {MaxSizeBytes / 1024}KB ({UsageRatio:P0}), " +
                   $"Hit Rate: {HitRate:P1}, " +
                   $"Evictions: {TotalEvictions}, Promotions: {TotalPromotions}, Demotions: {TotalDemotions}";
        }
    }
}
