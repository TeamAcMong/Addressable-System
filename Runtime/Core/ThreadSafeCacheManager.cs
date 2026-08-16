using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace AddressableManager.Core
{
    /// <summary>
    /// Thread-safe cache manager guarded by a <see cref="ReaderWriterLockSlim"/>.
    /// Can be safely accessed from any thread.
    ///
    /// <para><b>IMPORTANT — Set() can silently release your own handle:</b> if two callers
    /// <see cref="Set"/> the same key concurrently, the loser's <c>handle</c> argument is released as
    /// a rejected duplicate before <c>Set()</c> returns (see that method's doc). <c>Set()</c> stays
    /// <c>void</c>, so the only signal is <c>IsValid</c> flipping to <c>false</c> — always check it
    /// before reading or handing off a handle you just passed to <c>Set()</c>.</para>
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
        /// Add or update entry in cache (thread-safe). On success the cache holds its own reference
        /// to <paramref name="handle"/> (taken via <c>TryRetain()</c>), independent of the caller's
        /// own reference — the caller must still <c>Release()</c>/<c>Dispose()</c> its copy as usual.
        ///
        /// If <paramref name="handle"/> is already dead, it is refused rather than stored: a dead
        /// entry could never be served back out by <see cref="TryGet"/> anyway.
        ///
        /// If <paramref name="key"/> already has a <em>live</em> entry, this call does not replace
        /// it — only the access time is bumped. The handle passed in this call is not stored, so
        /// unless it is the exact object already cached, this call releases the reference it was
        /// given back to the caller (i.e. it does not adopt it and does not leak it).
        ///
        /// <para><b>IMPORTANT:</b> because this call can release the caller's own
        /// <paramref name="handle"/> reference as that "losing" duplicate, <paramref name="handle"/>
        /// may already be invalid by the time this call returns — check <c>IsValid</c> before reading
        /// or handing off <paramref name="handle"/> afterwards. Two threads racing to
        /// <c>Set()</c> the same key is exactly the scenario this class exists for, so this is not a
        /// corner case. When <c>_config.LogTierOperations</c> is enabled, every such rejection is
        /// logged so the loss is observable even without checking <c>IsValid</c>.</para>
        ///
        /// If the existing entry's handle has died without going through this cache (e.g.
        /// force-released by its owning loader), it is treated the same way <see cref="TryGet"/>
        /// treats it — as a stale/missing entry — so the incoming handle replaces it instead of being
        /// rejected into a zombie slot.
        ///
        /// Handles are never released while the write lock is held — any handle this call needs to
        /// give back (a rejected duplicate, or eviction victims) is released only after the lock is
        /// released.
        /// </summary>
        public void Set(string key, IAssetHandle<T> handle, long estimatedSize = 0)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeCacheManager<T>));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            IAssetHandle<T> rejected = null;
            List<IAssetHandle<T>> victims = null;

            _rwLock.EnterWriteLock();
            try
            {
                // Update existing or add new
                if (_cache.TryGetValue(key, out var existingEntry) && existingEntry.Handle.IsValid)
                {
                    existingEntry.RecordAccess();
                    if (!ReferenceEquals(existingEntry.Handle, handle))
                    {
                        rejected = handle;
                    }
                }
                else
                {
                    if (existingEntry != null)
                    {
                        // Stale entry whose handle died outside this cache — mirror TryGet()'s
                        // handling: drop the zombie slot instead of rejecting the caller's freshly
                        // loaded handle into it (which would silently unload the very asset that was
                        // just loaded).
                        if (_cache.TryRemove(key, out _))
                        {
                            Interlocked.Add(ref _currentCacheSize, -existingEntry.EstimatedSize);
                        }
                    }

                    if (handle.TryRetain())
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
                                    victims = PerformEviction();
                                }
                            }
                        }
                        else
                        {
                            // Lost the race to another writer for this key (should not happen while
                            // we hold the write lock, but stay defensive) — give the reference back.
                            rejected = handle;
                        }
                    }
                    // else: handle was already dead: refused, nothing was retained, nothing to
                    // release.
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            // Release outside the lock — never release a handle while holding the write lock.
            if (rejected != null)
            {
                if (_config.LogTierOperations)
                {
                    Debug.LogWarning($"[ThreadSafeCacheManager] Set() rejected a duplicate handle for already-cached key '{key}'; the caller's handle was released and is no longer valid.");
                }
                rejected.Release();
            }

            if (victims != null)
            {
                foreach (var victim in victims)
                {
                    victim?.Release();
                }
            }
        }

        /// <summary>
        /// Try to get entry from cache (thread-safe). On success, the returned handle carries a
        /// reference the caller now owns and must <c>Release()</c>/<c>Dispose()</c> —
        /// <c>TryRetain()</c> is the validity test, so a handle that died without going through this
        /// cache (e.g. force-released by its owning loader) is treated as a miss and its entry is
        /// dropped.
        ///
        /// Held under the <em>write</em> lock, not a shared read lock: a hit calls
        /// <c>entry.RecordAccess()</c>, which mutates <c>AccessCount</c>/<c>LastAccessTime</c> with
        /// plain (non-atomic) field writes — the same fields eviction reads via
        /// <c>CalculateTierScore()</c>. A shared read lock would let concurrent <c>TryGet()</c> calls
        /// race on those writes against each other and against eviction (C-11).
        /// </summary>
        public bool TryGet(string key, out IAssetHandle<T> handle)
        {
            if (_disposed)
            {
                handle = null;
                return false;
            }

            Interlocked.Increment(ref _totalAccesses);

            IAssetHandle<T> hit = null;

            _rwLock.EnterWriteLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (entry.Handle.TryRetain())
                    {
                        entry.RecordAccess();
                        hit = entry.Handle;
                    }
                    else
                    {
                        // Entry's handle is already dead (its last reference went away outside this
                        // cache). Nothing left to release here — drop the stale entry and report a
                        // miss. Already holding the write lock, so this is atomic with the lookup
                        // above: no other writer can have raced in between.
                        if (_cache.TryRemove(key, out _))
                        {
                            Interlocked.Add(ref _currentCacheSize, -entry.EstimatedSize);
                        }
                    }
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            if (hit != null)
            {
                Interlocked.Increment(ref _cacheHits);
                handle = hit;
                return true;
            }

            handle = null;
            return false;
        }

        /// <summary>
        /// Check if key exists (thread-safe)
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _cache.ContainsKey(key);
        }

        /// <summary>
        /// Remove entry from cache (thread-safe), releasing the cache's own reference to its handle
        /// after the write lock is released — never release a handle while holding the write lock.
        /// </summary>
        public bool Remove(string key)
        {
            CacheEntry<T> removed = null;
            bool found;

            _rwLock.EnterWriteLock();
            try
            {
                found = _cache.TryRemove(key, out removed);
                if (found)
                {
                    Interlocked.Add(ref _currentCacheSize, -removed.EstimatedSize);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            removed?.Handle?.Release();
            return found;
        }

        /// <summary>
        /// Pin entry to prevent eviction (thread-safe). Mutates <c>IsPinned</c>/<c>Tier</c>, which
        /// eviction reads under the write lock — so this takes the write lock too, not the read lock
        /// a read-only-looking call might suggest.
        /// </summary>
        public void Pin(string key)
        {
            _rwLock.EnterWriteLock();
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
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Unpin entry (thread-safe). See <see cref="Pin"/> for why this needs the write lock.
        /// </summary>
        public void Unpin(string key)
        {
            _rwLock.EnterWriteLock();
            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.IsPinned = false;
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Clear all entries (thread-safe), releasing the cache's own reference to every handle it
        /// holds. Handles are collected while the write lock is held and released only after it is
        /// released — never release a handle while holding the write lock.
        /// </summary>
        public void Clear()
        {
            List<IAssetHandle<T>> handles;

            _rwLock.EnterWriteLock();
            try
            {
                handles = new List<IAssetHandle<T>>(_cache.Count);
                foreach (var entry in _cache.Values)
                {
                    handles.Add(entry.Handle);
                }

                _cache.Clear();
                Interlocked.Exchange(ref _currentCacheSize, 0);
                ResetStatistics();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            foreach (var handle in handles)
            {
                handle?.Release();
            }
        }

        /// <summary>
        /// Select eviction victims and remove their entries (must be called within the write lock).
        /// Does NOT release the victims' handles itself — releasing must never happen while the write
        /// lock is held, so the removed handles are returned for the caller to release once it has
        /// exited the lock.
        /// </summary>
        private List<IAssetHandle<T>> PerformEviction()
        {
            var released = new List<IAssetHandle<T>>();

            if (_config.MaxCacheSizeBytes <= 0)
                return released;

            long targetSize = (long)(_config.MaxCacheSizeBytes * _config.EvictionTargetRatio);
            long amountToEvict = _currentCacheSize - targetSize;

            if (amountToEvict <= 0)
                return released;

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
                        released.Add(entry.Handle);
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

            return released;
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

            // _disposed must flip to true in the SAME write-lock acquisition that clears the
            // dictionary — not via a separate call to Clear() followed by a second, later
            // EnterWriteLock() just to set the flag. Between those two lock acquisitions the lock is
            // free and _disposed is still false, so a concurrent Set() could retain and insert a
            // brand-new entry into the just-cleared cache; nothing would ever release it afterwards
            // because every mutating method short-circuits on _disposed. Doing both under one lock
            // hold closes that window.
            List<IAssetHandle<T>> handles;

            _rwLock.EnterWriteLock();
            try
            {
                if (_disposed) return; // another thread won the race to dispose first

                _disposed = true;

                handles = new List<IAssetHandle<T>>(_cache.Count);
                foreach (var entry in _cache.Values)
                {
                    handles.Add(entry.Handle);
                }

                _cache.Clear();
                Interlocked.Exchange(ref _currentCacheSize, 0);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            // Release outside the lock — never release a handle while holding the write lock (C-8).
            foreach (var handle in handles)
            {
                handle?.Release();
            }

            _rwLock.Dispose();
        }
    }
}
