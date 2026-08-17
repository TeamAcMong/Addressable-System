using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AddressableManager.Loaders;

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
    public class TieredCache<T> : IDisposable, ITieredCache where T : class
    {
        private readonly Dictionary<AssetCacheKey, CacheEntry<T>> _cache = new Dictionary<AssetCacheKey, CacheEntry<T>>();
        private readonly TieredCacheConfig _config;

        // Shared across every TieredCache<T> a single TieredAssetLoader owns (HANDOFF_TO_SESSION_B.md
        // L-4) — the eviction gate and eviction target below read THIS, not a per-type total, so a
        // loader juggling several types is capped at one real ceiling instead of one per type.
        // Standalone callers (AdvancedAPI.CreateTieredCache<T>) get a private budget of their own —
        // see the public constructor below — so this is never null.
        private readonly CacheBudget _budget;

        private float _lastEvaluationTime;

        // This cache's own contribution to _budget, tracked in lockstep with every _budget.TryAdmit/
        // Give call below so GetStatistics() can still report a meaningful per-type figure for
        // TieredAssetLoader.GetCombinedStats() to sum (L-4's "reporting half" — see CacheBudget's
        // remarks for the "enforcement half").
        private long _currentCacheSize;
        private bool _disposed;

        // Statistics
        private int _totalAccesses;
        private int _cacheHits;
        private int _totalEvictions;
        private int _totalPromotions;
        private int _totalDemotions;

        /// <summary>
        /// Create a standalone tiered cache with its own private byte budget — the public,
        /// unshared constructor used directly by <c>AdvancedAPI.CreateTieredCache&lt;T&gt;</c> and
        /// any other external caller. A <see cref="AddressableManager.Loaders.TieredAssetLoader"/>
        /// uses the internal overload below instead, so every per-type cache it owns shares one
        /// budget rather than each getting its own (HANDOFF_TO_SESSION_B.md L-4).
        /// </summary>
        public TieredCache(TieredCacheConfig config = null)
            : this(config, null)
        {
        }

        /// <summary>
        /// Create a tiered cache that accounts against <paramref name="sharedBudget"/> instead of a
        /// private one. Internal — same-assembly callers only
        /// (<see cref="AddressableManager.Loaders.TieredAssetLoader"/>) — so this adds a new
        /// constructor overload rather than changing the public one's signature (repo invariant 6).
        /// </summary>
        internal TieredCache(TieredCacheConfig config, CacheBudget sharedBudget)
        {
            _config = config ?? TieredCacheConfig.Default;

            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid TieredCacheConfig: {error}");
            }

            _budget = sharedBudget ?? new CacheBudget(_config);
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

            // The dictionary below is keyed by (address, Type) instead of a caller-built formatted
            // string (HANDOFF_TO_SESSION_B.md L-9). Type is always typeof(T) here, so within this
            // one instance it adds nothing over the plain address — but it costs nothing either (a
            // stack struct, not an allocation), and CacheEntry<T>.Key (still the plain string, for
            // logging — see that field's doc) is unaffected.
            var cacheKey = new AssetCacheKey(key, typeof(T));

            // If entry exists and is still alive, just update access. The handle passed in is not
            // stored — release the reference we were given back unless it is the very object already
            // cached.
            if (_cache.TryGetValue(cacheKey, out var existingEntry))
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
                _budget.Give(existingEntry.EstimatedSize);
                _cache.Remove(cacheKey);
            }

            // The cache takes its own reference; a handle that is already dead is refused instead of
            // stored.
            if (!handle.TryRetain())
                return;

            // Create new entry. CacheEntry<T>.Key stays the plain string (display/log use, and
            // shared with ThreadSafeCacheManager<T> — see that field's doc) — cacheKey (the real
            // AssetCacheKey) is only ever used as this dictionary's key, not stored on the entry.
            var entry = new CacheEntry<T>(key, handle, estimatedSize);
            _cache[cacheKey] = entry;
            _currentCacheSize += estimatedSize;
            _budget.TryAdmit(estimatedSize);

            // Check if we need to evict — against the SHARED budget (L-4), not this cache's own
            // slice of it, so a loader juggling several types is capped at one real ceiling.
            if (_config.EnableAutoEviction && _budget.Max > 0)
            {
                if (_budget.UsageRatio >= _config.EvictionTriggerRatio)
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
            var cacheKey = new AssetCacheKey(key, typeof(T));

            if (_cache.TryGetValue(cacheKey, out var entry))
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
                _budget.Give(entry.EstimatedSize);
                _cache.Remove(cacheKey);
            }

            handle = null;
            return false;
        }

        /// <summary>
        /// Check if key exists in cache
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _cache.ContainsKey(new AssetCacheKey(key, typeof(T)));
        }

        /// <summary>
        /// Remove entry from cache, releasing the cache's own reference to its handle.
        /// </summary>
        public bool Remove(string key)
        {
            var cacheKey = new AssetCacheKey(key, typeof(T));
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                entry.Handle?.Release();
                _currentCacheSize -= entry.EstimatedSize;
                _budget.Give(entry.EstimatedSize);
                _cache.Remove(cacheKey);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pin an entry to prevent eviction
        /// </summary>
        public void Pin(string key)
        {
            if (_cache.TryGetValue(new AssetCacheKey(key, typeof(T)), out var entry))
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
            if (_cache.TryGetValue(new AssetCacheKey(key, typeof(T)), out var entry))
            {
                entry.IsPinned = false;
            }
        }

        /// <summary>
        /// Clear all cache entries. Teardown semantics, same as <see cref="ForceReleaseAll"/> (which
        /// this delegates to) and matching <see cref="TieredAssetLoader.ClearCache"/>'s documented
        /// memory-pressure contract (HANDOFF_TO_SESSION_B.md L-1: "TieredCache&lt;T&gt;.Clear/Dispose
        /// và TieredAssetLoader.ClearCache/Dispose: ForceRelease()").
        /// </summary>
        /// <remarks>
        /// This used to give back only the cache's own reference (a plain decrement), which reads as
        /// the milder, eviction-style release <see cref="Remove"/> and the eviction loop use — but
        /// nothing in the package ever called this method that way (grep confirms zero in-package
        /// callers), and a caller reaching it expecting "clear the cache" to actually reclaim memory
        /// would instead find a handle any other holder retained left untouched. Kept as a separate
        /// method from <see cref="ForceReleaseAll"/> rather than removed, since it is public API
        /// (invariant 1) — a caller may already depend on the name.
        /// </remarks>
        public void Clear()
        {
            ForceReleaseAll();
        }

        /// <summary>
        /// Hard-release every handle this cache holds, regardless of who else still holds a
        /// reference, and drop every entry. This is <see cref="ITieredCache.ForceReleaseAll"/> — the
        /// teardown counterpart to <see cref="Remove"/>, which only gives back the cache's own
        /// reference and leaves a handle any other holder retained untouched.
        /// </summary>
        public void ForceReleaseAll()
        {
            foreach (var entry in _cache.Values)
            {
                // Every handle this cache ever stores comes from AssetHandle{T} (see Set()'s
                // TryRetain() call), which also implements the internal owner-side IOwnedHandle
                // contract — but IAssetHandle{T} itself does not expose ForceRelease(), so a foreign
                // IAssetHandle{T} implementation (test double, decorator) falls back to a plain
                // decrement rather than throwing.
                if (entry.Handle is IOwnedHandle owned)
                {
                    owned.ForceRelease();
                }
                else
                {
                    entry.Handle?.Release();
                }
            }

            _budget.Give(_currentCacheSize);
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
        /// Perform eviction to reduce cache size.
        /// </summary>
        /// <remarks>
        /// Targets the SHARED budget's overage (HANDOFF_TO_SESSION_B.md L-4), not this cache's own
        /// slice of it — <paramref name="amountToEvict"/>-equivalent below is
        /// <c>_budget.Current - target</c>, the loader-wide amount over budget, even though the
        /// candidates evicted still only ever come from this type's own entries (a per-type cache
        /// has no visibility into siblings). This is what makes eviction reach across types in
        /// practice: <see cref="AddressableManager.Loaders.TieredAssetLoader.ForceEviction"/> and
        /// <see cref="AddressableManager.Loaders.TieredAssetLoader.EvaluateTiers"/> call every
        /// per-type cache in turn, each one re-reading the (now smaller) shared overage left by
        /// whichever cache evicted before it, so a run across all types converges on the real
        /// ceiling even though no single call touches more than one type's dictionary.
        /// </remarks>
        private void PerformEviction()
        {
            if (_budget.Max <= 0)
                return;

            long targetSize = (long)(_budget.Max * _config.EvictionTargetRatio);
            long amountToEvict = _budget.Current - targetSize;

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
            var keysToRemove = new List<AssetCacheKey>();

            foreach (var entry in candidates)
            {
                if (evictedSize >= amountToEvict)
                    break;

                // Only evict if score is below threshold or in Cold tier
                if (entry.Tier == CacheTier.Cold || entry.CalculateTierScore() < _config.EvictionScoreThreshold)
                {
                    // entry.Key is the plain string (CacheEntry<T> doesn't carry the AssetCacheKey —
                    // see that field's doc); rebuild it to match _cache's actual key type.
                    keysToRemove.Add(new AssetCacheKey(entry.Key, typeof(T)));
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
            _budget.Give(evictedSize);
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
                MaxSizeBytes = _budget.Max,
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

        /// <summary>
        /// Teardown: hard-releases every handle this cache holds, regardless of who else still
        /// holds a reference (HANDOFF_TO_SESSION_B.md L-1). Deliberately <see cref="ForceReleaseAll"/>
        /// rather than <see cref="Clear"/> — <c>Clear()</c> only gives back this cache's own
        /// reference (a decrement any other holder survives), which is correct for a caller asking
        /// this cache to empty itself while the loader stays alive, but wrong for teardown: an
        /// owner that is going away entirely must not leave a caller holding a handle this cache no
        /// longer tracks and can never invalidate again.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            ForceReleaseAll();
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
