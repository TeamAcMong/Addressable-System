using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace AddressableManager.Core
{
    /// <summary>
    /// Thread-safe cache manager using lock-free data structures
    /// Can be safely accessed from any thread
    /// </summary>
    public class ThreadSafeCacheManager<T> : IDisposable where T : class
    {
        private readonly ConcurrentDictionary<string, CacheEntry<T>> _cache;
        private readonly TieredCacheConfig _config;
        private readonly ReaderWriterLockSlim _rwLock;
        private long _currentCacheSize;
        private bool _disposed;

        // Statistics (thread-safe)
        private int _totalAccesses;
        private int _cacheHits;
        private int _totalEvictions;

        public ThreadSafeCacheManager(TieredCacheConfig config = null)
        {
            _config = config ?? TieredCacheConfig.Default;
            _cache = new ConcurrentDictionary<string, CacheEntry<T>>();
            _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _currentCacheSize = 0;
        }

        /// <summary>
        /// Add or update entry in cache (thread-safe)
        /// </summary>
        public void Set(string key, IAssetHandle<T> handle, long estimatedSize = 0)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeCacheManager<T>));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            _rwLock.EnterWriteLock();
            try
            {
                // Update existing or add new
                if (_cache.TryGetValue(key, out var existingEntry))
                {
                    existingEntry.RecordAccess();
                }
                else
                {
                    var entry = new CacheEntry<T>(key, handle, estimatedSize);
                    if (_cache.TryAdd(key, entry))
                    {
                        Interlocked.Add(ref _currentCacheSize, estimatedSize);

                        // Check if eviction is needed
                        if (_config.EnableAutoEviction && _config.MaxCacheSizeBytes > 0)
                        {
                            float currentRatio = (float)_currentCacheSize / _config.MaxCacheSizeBytes;
                            if (currentRatio >= _config.EvictionTriggerRatio)
                            {
                                PerformEviction();
                            }
                        }
                    }
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Try to get entry from cache (thread-safe)
        /// </summary>
        public bool TryGet(string key, out IAssetHandle<T> handle)
        {
            if (_disposed)
            {
                handle = null;
                return false;
            }

            Interlocked.Increment(ref _totalAccesses);

            _rwLock.EnterReadLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.RecordAccess();
                    handle = entry.Handle;
                    Interlocked.Increment(ref _cacheHits);
                    return true;
                }

                handle = null;
                return false;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Check if key exists (thread-safe)
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _cache.ContainsKey(key);
        }

        /// <summary>
        /// Remove entry from cache (thread-safe)
        /// </summary>
        public bool Remove(string key)
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (_cache.TryRemove(key, out var entry))
                {
                    Interlocked.Add(ref _currentCacheSize, -entry.EstimatedSize);
                    return true;
                }
                return false;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Pin entry to prevent eviction (thread-safe)
        /// </summary>
        public void Pin(string key)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.IsPinned = true;
                    entry.Tier = CacheTier.Hot;
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Unpin entry (thread-safe)
        /// </summary>
        public void Unpin(string key)
        {
            _rwLock.EnterReadLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.IsPinned = false;
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Clear all entries (thread-safe)
        /// </summary>
        public void Clear()
        {
            _rwLock.EnterWriteLock();
            try
            {
                _cache.Clear();
                Interlocked.Exchange(ref _currentCacheSize, 0);
                ResetStatistics();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Perform eviction (must be called within write lock)
        /// </summary>
        private void PerformEviction()
        {
            if (_config.MaxCacheSizeBytes <= 0)
                return;

            long targetSize = (long)(_config.MaxCacheSizeBytes * _config.EvictionTargetRatio);
            long amountToEvict = _currentCacheSize - targetSize;

            if (amountToEvict <= 0)
                return;

            var candidates = new List<CacheEntry<T>>();

            // Collect eviction candidates
            foreach (var entry in _cache.Values)
            {
                if (!entry.IsPinned)
                {
                    candidates.Add(entry);
                }
            }

            // Sort by tier (Cold first) then by score
            candidates.Sort((a, b) =>
            {
                int tierCompare = b.Tier.CompareTo(a.Tier); // Cold (2) first
                if (tierCompare != 0) return tierCompare;
                return a.CalculateTierScore().CompareTo(b.CalculateTierScore()); // Low score first
            });

            long evictedSize = 0;
            int evictedCount = 0;

            foreach (var entry in candidates)
            {
                if (evictedSize >= amountToEvict)
                    break;

                if (entry.Tier == CacheTier.Cold || entry.CalculateTierScore() < _config.EvictionScoreThreshold)
                {
                    if (_cache.TryRemove(entry.Key, out _))
                    {
                        entry.Handle?.Release();
                        evictedSize += entry.EstimatedSize;
                        evictedCount++;
                    }
                }
            }

            Interlocked.Add(ref _currentCacheSize, -evictedSize);
            Interlocked.Add(ref _totalEvictions, evictedCount);

            if (_config.LogTierOperations)
            {
                Debug.Log($"[ThreadSafeCacheManager] Eviction complete: {evictedCount} entries, {evictedSize / 1024}KB freed");
            }
        }

        /// <summary>
        /// Get cache statistics (thread-safe)
        /// </summary>
        public TieredCacheStats GetStatistics()
        {
            _rwLock.EnterReadLock();
            try
            {
                int hotCount = 0, warmCount = 0, coldCount = 0, pinnedCount = 0;

                foreach (var entry in _cache.Values)
                {
                    if (entry.IsPinned) pinnedCount++;

                    switch (entry.Tier)
                    {
                        case CacheTier.Hot: hotCount++; break;
                        case CacheTier.Warm: warmCount++; break;
                        case CacheTier.Cold: coldCount++; break;
                    }
                }

                int totalAccesses = Interlocked.CompareExchange(ref _totalAccesses, 0, 0);
                int cacheHits = Interlocked.CompareExchange(ref _cacheHits, 0, 0);

                return new TieredCacheStats
                {
                    TotalEntries = _cache.Count,
                    HotEntries = hotCount,
                    WarmEntries = warmCount,
                    ColdEntries = coldCount,
                    PinnedEntries = pinnedCount,
                    TotalSizeBytes = _currentCacheSize,
                    MaxSizeBytes = _config.MaxCacheSizeBytes,
                    TotalAccesses = totalAccesses,
                    CacheHits = cacheHits,
                    HitRate = totalAccesses > 0 ? (float)cacheHits / totalAccesses : 0f,
                    TotalEvictions = Interlocked.CompareExchange(ref _totalEvictions, 0, 0),
                    TotalPromotions = 0,
                    TotalDemotions = 0
                };
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Reset statistics
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalAccesses, 0);
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _totalEvictions, 0);
        }

        /// <summary>
        /// Get entry count (thread-safe)
        /// </summary>
        public int Count => _cache.Count;

        /// <summary>
        /// Get current cache size (thread-safe)
        /// </summary>
        public long CurrentSize => Interlocked.Read(ref _currentCacheSize);

        public void Dispose()
        {
            if (_disposed) return;

            _rwLock.EnterWriteLock();
            try
            {
                _cache.Clear();
                _disposed = true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                _rwLock.Dispose();
            }
        }
    }
}
