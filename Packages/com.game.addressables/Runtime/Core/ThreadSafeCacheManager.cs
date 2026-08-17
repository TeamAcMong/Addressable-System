using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using AddressableManager.Threading;

namespace AddressableManager.Core
{
    /// <summary>
    /// Cache manager whose state is guarded by a <see cref="ReaderWriterLockSlim"/>, so its
    /// dictionary and bookkeeping stay consistent under concurrent access. <b>This is not the same
    /// thing as "callable from any thread"</b> — read the thread contract below before calling any
    /// of it off Unity's main thread.
    ///
    /// <para><b>THREAD CONTRACT</b> (HANDOFF_TO_SESSION_B.md C-7). Two groups:</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>Main thread only</b> — <see cref="Set"/> and <see cref="TryGet"/>.
    /// Both go through <see cref="CacheEntry{T}"/>, which reads
    /// <c>Time.realtimeSinceStartup</c> in its constructor, in <c>RecordAccess()</c> and in
    /// <c>CalculateTierScore()</c>; both also test <c>IAssetHandle&lt;T&gt;.IsValid</c>, which reads
    /// the Addressables <c>ResourceManager</c>. Neither is callable off the main thread at any
    /// price. These two now assert instead of failing deeper in — see the note on the assertion
    /// below.</description></item>
    /// <item><description><b>Any thread</b> — <see cref="Remove"/>, <see cref="Clear"/>,
    /// <see cref="Dispose"/>, <see cref="Pin"/>/<see cref="TryPin"/>,
    /// <see cref="Unpin"/>/<see cref="TryUnpin"/>, <see cref="ContainsKey"/>, <see cref="Count"/>,
    /// <see cref="CurrentSize"/>, <see cref="GetStatistics"/>, <see cref="ResetStatistics"/>. These
    /// touch no Unity API: they move dictionary entries and plain fields under the lock. The three
    /// that end up giving a handle back (<c>Remove</c>, <c>Clear</c>, <c>Dispose</c>) marshal that
    /// release onto the main thread through <c>UnityMainThreadDispatcher</c>, the same way
    /// <c>AssetLoader.ReleaseOnMainThread</c> does — so an off-thread call is safe, but the unload
    /// lands on the next main-thread <c>Update()</c> rather than before the call
    /// returns.</description></item>
    /// </list>
    ///
    /// <para><b>Why the two main-thread methods assert rather than marshal.</b> Both are
    /// synchronous and hand back a result the caller uses immediately — <see cref="TryGet"/> in
    /// particular must produce an already-retained handle through an <c>out</c> parameter. Hopping
    /// to the main thread would mean either blocking on
    /// <c>UnityMainThreadDispatcher.EnqueueAndWait</c> (which deadlocks whenever the main thread is
    /// itself waiting on the work that made this call) or turning both into async methods, which
    /// would change a public signature before 5.0.0 (repo invariant 6). So the contract is stated
    /// and enforced instead. The assertion is also a strict improvement on what was there: an
    /// off-thread <c>Set()</c>/<c>TryGet()</c> already threw — <c>TryRetain()</c> succeeded first
    /// and then <c>CacheEntry</c> threw <c>UnityException</c> on
    /// <c>get_realtimeSinceStartup</c> — leaking the reference it had just taken, from a stack that
    /// named neither this class nor the calling thread.</para>
    ///
    /// <para><b>After <see cref="Dispose"/>, the any-thread group returns neutral values rather
    /// than throwing</b> — <c>Remove</c>/<c>TryPin</c>/<c>TryUnpin</c> report <c>false</c>,
    /// <c>GetStatistics</c> an all-zero snapshot, <c>Clear</c> does nothing. Publishing an
    /// "any thread" contract invites a caller to hold this object on a worker thread, where it
    /// cannot see a <c>Dispose</c> happening on another; <c>ObjectDisposedException</c> out of a
    /// bookkeeping call that is documented as unconditionally safe is not in that contract. For the
    /// same reason the internal lock is not disposed — see <see cref="Dispose"/>. <c>Set</c> and
    /// <c>TryGet</c> keep their existing disposed behaviour (throw, and report a miss,
    /// respectively), now re-checked <em>inside</em> the lock so a <c>Set</c> racing a
    /// <c>Dispose</c> cannot insert a retained entry into a cache that has just been emptied.</para>
    ///
    /// <para>The lock earns its keep either way: <see cref="Pin"/>, <see cref="TryGet"/> and
    /// eviction all mutate the same non-atomic <see cref="CacheEntry{T}"/> fields, and the
    /// dictionary is walked while other threads insert into it.</para>
    ///
    /// <para><b>IMPORTANT — Set() can silently release your own handle:</b> if two callers
    /// <see cref="Set"/> the same key concurrently, the loser's <c>handle</c> argument is released as
    /// a rejected duplicate before <c>Set()</c> returns (see that method's doc). <c>Set()</c> stays
    /// <c>void</c>, so the only signal is <c>IsValid</c> flipping to <c>false</c> — always check it
    /// before reading or handing off a handle you just passed to <c>Set()</c>.</para>
    ///
    /// <para><b>Pinning is order-independent:</b> <see cref="Pin"/> on a key that is not cached yet
    /// is recorded and applied when that key is next stored (HANDOFF_TO_SESSION_B.md C-9). See
    /// <see cref="TryPin"/> for the version that tells you which happened.</para>
    /// </summary>
    public class ThreadSafeCacheManager<T> : IDisposable where T : class
    {
        private readonly ConcurrentDictionary<string, CacheEntry<T>> _cache;
        private readonly TieredCacheConfig _config;
        private readonly ReaderWriterLockSlim _rwLock;
        private long _currentCacheSize;

        // volatile: read by every method on this class from threads that never took the lock (the
        // "any thread" group), and written by Dispose under it. Without volatile a reader can keep
        // observing the pre-dispose value indefinitely.
        private volatile bool _disposed;

        // Keys a caller asked to pin while nothing was cached under them (HANDOFF_TO_SESSION_B.md
        // C-9). Set() consumes an entry from here on insert, so a pin placed before the load lands
        // on the entry when it arrives instead of evaporating. Plain HashSet on purpose: every read
        // and write below happens under _rwLock, same as _cache's non-atomic entry fields.
        private readonly HashSet<string> _pendingPins = new HashSet<string>();
        private const int MaxPendingPins = 256;

        // Latch for PerformEviction's "eviction cannot reach the target" warning, so an unfixable
        // over-budget state is reported once per episode rather than on every Set().
        private bool _budgetShortfallReported;

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
        /// Enforce the main-thread half of this class's thread contract (see the class summary).
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="AddressableRuntime.IsMainThread"/> — the package's single latch,
        /// set from a <c>RuntimeInitializeOnLoadMethod</c> that always runs on the main thread, and
        /// deliberately failing open while unlatched so edit-mode tooling and EditMode tests that
        /// never trigger it are not blocked. Same primitive and same message shape as
        /// <c>TieredAssetLoader.AssertMainThread</c>, so a caller that hits one recognises the
        /// other.
        /// </remarks>
        private static void AssertMainThread(string member)
        {
            if (AddressableRuntime.IsMainThread)
                return;

            throw new InvalidOperationException(
                $"[ThreadSafeCacheManager] {member}() must be called from Unity's main thread.\n\n" +
                $"The ReaderWriterLockSlim in this class keeps its dictionary consistent across " +
                $"threads; it cannot make the Unity APIs {member}() reaches callable off the main " +
                $"thread. {member}() goes through CacheEntry (Time.realtimeSinceStartup) and " +
                $"IAssetHandle.IsValid (Addressables ResourceManager), both main-thread-only.\n" +
                $"Current thread ID: {Thread.CurrentThread.ManagedThreadId}\n" +
                $"Expected thread ID: {AddressableRuntime.MainThreadId}\n\n" +
                $"SOLUTION: marshal the call — UnityMainThreadDispatcher.Enqueue(() => ...) — or use " +
                $"Remove/Clear/Pin/Unpin/GetStatistics, which this class does support from any thread.");
        }

        /// <summary>
        /// Take the write lock, unless this cache has been disposed. Returns <c>false</c> — without
        /// holding the lock — when it has.
        /// </summary>
        /// <remarks>
        /// The re-check <em>inside</em> the lock is the point. Every method used to test
        /// <c>_disposed</c> before <c>EnterWriteLock()</c> and never again, which left the exact
        /// window <see cref="Dispose"/>'s own comment claims to have closed: a <see cref="Set"/>
        /// passes the check, <see cref="Dispose"/> then takes the lock, flips the flag and empties
        /// the dictionary, and <c>Set</c> finally acquires and inserts a freshly retained entry into
        /// the cache that was just cleared. Nothing releases it afterwards — every mutating method
        /// short-circuits on <c>_disposed</c> and <c>Dispose</c> has already collected its handle
        /// list. Doing both halves of the test around one acquisition is what actually closes it.
        /// </remarks>
        private bool TryEnterWriteLock()
        {
            if (_disposed) return false;

            _rwLock.EnterWriteLock();

            if (_disposed)
            {
                _rwLock.ExitWriteLock();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Take the read lock, unless this cache has been disposed. See <see cref="TryEnterWriteLock"/>.
        /// </summary>
        private bool TryEnterReadLock()
        {
            if (_disposed) return false;

            _rwLock.EnterReadLock();

            if (_disposed)
            {
                _rwLock.ExitReadLock();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Give a handle back on Unity's main thread, from a caller that may not be on it.
        /// </summary>
        /// <remarks>
        /// A release can end in <c>Addressables.Release</c> (<c>AssetHandle&lt;T&gt;</c> unloads the
        /// operation when the last reference goes), which is main-thread-only. <see cref="Remove"/>,
        /// <see cref="Clear"/> and <see cref="Dispose"/> are documented as callable from any thread
        /// and do all of their own work in plain managed state, so the release is the single thing
        /// that has to hop. Mirrors <c>AssetLoader.ReleaseOnMainThread</c>, including its last
        /// resort: if the dispatcher cannot even be reached the handle is leaked and said so, because
        /// a leaked bundle is recoverable and a ResourceManager mutated from a worker thread is not.
        /// Inline (no queue, no delay) when already on the main thread, which is the normal case.
        /// </remarks>
        private static void ReleaseOnMainThread(IAssetHandle<T> handle)
        {
            if (handle == null)
                return;

            if (AddressableRuntime.IsMainThread)
            {
                handle.Release();
                return;
            }

            try
            {
                UnityMainThreadDispatcher.Enqueue(handle.Release);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[ThreadSafeCacheManager] Could not hand a handle back to the main thread; the " +
                    $"asset will stay loaded until the next catalog reload. {ex.Message}");
            }
        }

        /// <summary>
        /// Add or update entry in cache. <b>Main thread only</b> — see the class summary's thread
        /// contract; throws <see cref="InvalidOperationException"/> otherwise. On success the cache holds its own reference
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
        ///
        /// A pin requested for <paramref name="key"/> before anything was cached under it
        /// (see <see cref="TryPin"/>) is applied to the new entry here, before the eviction check
        /// runs.
        /// </summary>
        public void Set(string key, IAssetHandle<T> handle, long estimatedSize = 0)
        {
            AssertMainThread(nameof(Set));

            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeCacheManager<T>));

            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (handle == null)
                throw new ArgumentNullException(nameof(handle));

            IAssetHandle<T> rejected = null;
            List<IAssetHandle<T>> victims = null;
            bool oversized = false;
            long evictionTarget = 0;
            var deferredLogs = new List<string>();
            var deferredWarnings = new List<string>();

            // Disposed between the check above and here: hand the caller's handle straight back
            // rather than retaining it into a cache nothing will ever drain (see TryEnterWriteLock).
            if (!TryEnterWriteLock())
            {
                throw new ObjectDisposedException(nameof(ThreadSafeCacheManager<T>));
            }

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
                        CarryPinAcrossStaleEntry(key, existingEntry);
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

                            // A pin placed before this key was ever cached applies now, on arrival
                            // (C-9). Consumed only once the insert has actually succeeded — the
                            // TryAdd failure path below hands the handle straight back, and burning
                            // the pending pin there would lose the pin for the entry that does
                            // eventually land. Still before the eviction check, so PerformEviction()
                            // sees the entry as pinned and cannot treat the very thing the caller
                            // asked to protect as a candidate in the pass its own insert triggered.
                            if (_pendingPins.Remove(key))
                            {
                                entry.IsPinned = true;
                                entry.Tier = CacheTier.Hot;

                                if (_config.LogTierOperations)
                                {
                                    deferredLogs.Add($"[ThreadSafeCacheManager] Applied a pending pin to '{key}' as it entered the cache.");
                                }
                            }

                            // Check if eviction is needed
                            if (_config.EnableAutoEviction && _config.MaxCacheSizeBytes > 0)
                            {
                                oversized = IsLargerThanEvictionTarget(estimatedSize, out evictionTarget);

                                float currentRatio = (float)_currentCacheSize / _config.MaxCacheSizeBytes;
                                if (currentRatio >= _config.EvictionTriggerRatio)
                                {
                                    victims = PerformEviction(deferredLogs, deferredWarnings);
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

            // Log outside the lock, for the same reason handles are released outside it, and for one
            // more: Unity dispatches Application.logMessageReceived synchronously on the logging
            // thread, so a subscriber that calls back into this cache (Count, GetStatistics, Remove)
            // would re-enter a lock this thread already holds under LockRecursionPolicy.NoRecursion
            // and throw LockRecursionException out of Set().
            foreach (var line in deferredLogs)
            {
                Debug.Log(line);
            }

            foreach (var line in deferredWarnings)
            {
                Debug.LogWarning(line);
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

            if (oversized)
            {
                // Logged outside the lock for the same reason handles are released outside it.
                Debug.LogWarning(
                    $"[ThreadSafeCacheManager] '{key}' is {estimatedSize / 1024}KB on its own, larger " +
                    $"than the post-eviction target of {evictionTarget / 1024}KB " +
                    $"({_config.EvictionTargetRatio:P0} of the {_config.MaxCacheSizeBytes / 1024}KB " +
                    $"budget). It was cached, but no eviction pass can bring the cache back to target " +
                    $"while it is cached — an entry is never evictable in the same pass that inserts " +
                    $"it — so until it cools to Cold every Set() will run a sweep that cannot reach " +
                    $"its target. Raise MaxCacheSizeBytes, or keep an asset this size out of this cache.");
            }
        }

        /// <summary>
        /// Is a single entry, on its own, larger than the size eviction targets? The admission half
        /// of HANDOFF_TO_SESSION_B.md C-13 — see <see cref="PerformEviction"/>'s remarks for why an
        /// entry that big puts the cache into a state eviction cannot resolve.
        /// </summary>
        private bool IsLargerThanEvictionTarget(long estimatedSize, out long targetSize)
        {
            targetSize = (long)(_config.MaxCacheSizeBytes * _config.EvictionTargetRatio);
            return estimatedSize > 0 && estimatedSize > targetSize;
        }

        /// <summary>
        /// Re-arm a pin as pending when the entry carrying it is dropped as stale (its handle died
        /// outside this cache), so the pin is not silently lost on the next load. Caller must hold
        /// the write lock.
        /// </summary>
        private void CarryPinAcrossStaleEntry(string key, CacheEntry<T> entry)
        {
            if (!entry.IsPinned)
                return;

            if (_pendingPins.Count < MaxPendingPins)
            {
                _pendingPins.Add(key);
            }
        }

        /// <summary>
        /// Try to get entry from cache. <b>Main thread only</b> — see the class summary's thread
        /// contract; throws <see cref="InvalidOperationException"/> otherwise. A wrong-thread call
        /// is a contract violation, not a cache miss, so it must not come back as <c>false</c>.
        /// On success, the returned handle carries a
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
            AssertMainThread(nameof(TryGet));

            if (_disposed)
            {
                handle = null;
                return false;
            }

            Interlocked.Increment(ref _totalAccesses);

            IAssetHandle<T> hit = null;

            // Disposed between the check above and here — report a miss rather than retaining an
            // entry out of a cache that is being emptied (see TryEnterWriteLock).
            if (!TryEnterWriteLock())
            {
                handle = null;
                return false;
            }

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
                        CarryPinAcrossStaleEntry(key, entry);
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
        /// Check if key exists. Callable from any thread — a <c>ConcurrentDictionary</c> lookup and
        /// nothing else.
        /// </summary>
        public bool ContainsKey(string key)
        {
            return _cache.ContainsKey(key);
        }

        /// <summary>
        /// Remove entry from cache, giving back the cache's own reference to its handle. Callable
        /// from any thread: the dictionary work is plain managed state under the write lock, and the
        /// release is marshalled to the main thread (see <see cref="ReleaseOnMainThread"/>) after the
        /// lock is released — never release a handle while holding the write lock.
        /// </summary>
        public bool Remove(string key)
        {
            CacheEntry<T> removed = null;
            bool found;

            // A disposed cache holds nothing, so "removed nothing" is the honest answer. Returning it
            // rather than letting EnterWriteLock throw matters because this method is documented as
            // callable from any thread, which invites a worker to hold the object across a Dispose
            // it cannot see; ObjectDisposedException out of a bookkeeping call is not in the contract.
            if (!TryEnterWriteLock()) return false;

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

            ReleaseOnMainThread(removed?.Handle);
            return found;
        }

        /// <summary>
        /// Pin an entry so eviction skips it. If nothing is cached under <paramref name="key"/> yet,
        /// the request is remembered and applied the moment that key is stored.
        /// </summary>
        /// <remarks>
        /// Callable from any thread — pure field writes under the write lock, no Unity API. Mutates
        /// <c>IsPinned</c>/<c>Tier</c>, which eviction reads under the write lock, so this takes the
        /// write lock too, not the read lock a read-only-looking call might suggest (C-11).
        ///
        /// Stays <c>void</c> because it is public API and repo invariant 6 forbids changing a public
        /// signature before 5.0.0; <see cref="TryPin"/> is the additive version that reports which of
        /// the two outcomes happened, and carries the reasoning for deferring rather than
        /// no-op'ing (HANDOFF_TO_SESSION_B.md C-9).
        /// </remarks>
        public void Pin(string key)
        {
            TryPin(key);
        }

        /// <summary>
        /// Pin an entry so eviction skips it, reporting whether a cached entry was pinned right now
        /// (<c>true</c>) or the pin was deferred until the key is next stored (<c>false</c>).
        /// Callable from any thread.
        /// </summary>
        /// <returns>
        /// <c>true</c> — an entry is cached under <paramref name="key"/> and is pinned as of this
        /// call. <c>false</c> — the pin was <em>either</em> deferred (recorded; <see cref="Set"/>
        /// will apply it on arrival) <em>or</em> refused outright (the pending list is full, so it
        /// will never be applied, and an error is logged).
        /// <b>This return value cannot tell those two apart, and they are opposites</b> — use
        /// <see cref="PinWithOutcome"/>, which returns a <see cref="CachePinOutcome"/>, when the
        /// difference matters. <c>bool</c> is kept here because this is shipped public API
        /// (invariant 6).
        /// </returns>
        /// <remarks>
        /// The old body had no <c>else</c>: pinning a key that was not cached yet did nothing, said
        /// nothing, and returned nothing, while <c>IsPinned</c> is eviction's only exemption (C-10) —
        /// so a caller who believed a critical asset was protected simply watched it get evicted.
        /// Deferring makes the "pin the critical asset, then load it" order README teaches actually
        /// work, which is worth more than a warning telling every reader of that README they are
        /// wrong. Observable three ways: this return value, <c>PendingPins</c> in
        /// <see cref="GetStatistics"/>, and a log line under <c>LogTierOperations</c> — not an
        /// unconditional warning, since pin-then-load is now a supported order.
        /// </remarks>
        public bool TryPin(string key) => PinWithOutcome(key) == CachePinOutcome.Pinned;

        /// <summary>
        /// Pin an entry so eviction skips it, reporting which of the three things actually happened.
        /// Callable from any thread.
        /// </summary>
        /// <returns>
        /// <see cref="CachePinOutcome.Pinned"/>, <see cref="CachePinOutcome.Deferred"/> or
        /// <see cref="CachePinOutcome.Refused"/> — see that enum for what each one obliges the
        /// caller to do.
        /// </returns>
        /// <remarks>
        /// Prefer this to <see cref="TryPin"/>, which collapses <c>Deferred</c> and <c>Refused</c>
        /// into one <c>false</c> even though they are opposites. <c>TryPin</c> keeps its signature
        /// because it is shipped public API (invariant 6); this is the additive version that can
        /// tell the truth. A disposed cache reports <see cref="CachePinOutcome.Refused"/>: nothing
        /// will ever load into one, so the pin can never be applied — which is exactly what
        /// <c>Refused</c> means.
        /// </remarks>
        public CachePinOutcome PinWithOutcome(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            bool pinned = false;
            bool refused = false;

            // Nothing to pin in a disposed cache, and nothing will ever load into one. See Remove().
            if (!TryEnterWriteLock()) return CachePinOutcome.Refused;

            try
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    entry.IsPinned = true;
                    entry.Tier = CacheTier.Hot;
                    pinned = true;
                }
                else if (_pendingPins.Contains(key))
                {
                    // already recorded by an earlier call — armed either way
                }
                else if (_pendingPins.Count >= MaxPendingPins)
                {
                    refused = true;
                }
                else
                {
                    _pendingPins.Add(key);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            if (refused)
            {
                // The one case where a pin is genuinely refused rather than deferred, so this is an
                // error and is not gated behind LogTierOperations: nothing later will apply it.
                Debug.LogError(
                    $"[ThreadSafeCacheManager] Pin('{key}') was refused: {MaxPendingPins} pins are " +
                    $"already waiting for keys that have never been cached. This key will NOT be " +
                    $"pinned when it loads. Pinning addresses that are never loaded is the usual cause.");
            }
            else if (_config.LogTierOperations)
            {
                Debug.Log(pinned
                    ? $"[ThreadSafeCacheManager] Pinned '{key}' (already cached)."
                    : $"[ThreadSafeCacheManager] Pin('{key}') found nothing cached under that key; the pin is now pending and will be applied when the key is stored.");
            }

            if (pinned) return CachePinOutcome.Pinned;
            if (refused) return CachePinOutcome.Refused;

            // Armed, whether this call recorded it or an earlier one already had. Set() applies it
            // when the key arrives.
            return CachePinOutcome.Deferred;
        }

        /// <summary>
        /// Unpin an entry to allow eviction, and cancel any pin still pending for that key. Callable
        /// from any thread. See <see cref="Pin"/> for why this needs the write lock, and
        /// <see cref="TryUnpin"/> for the version that reports whether anything was undone.
        /// </summary>
        /// <remarks>
        /// Cancelling the pending pin is the half that did not exist before: with <see cref="TryPin"/>
        /// now deferring, a <c>Pin(key)</c>/<c>Unpin(key)</c> pair on a key that is not cached yet
        /// would otherwise leave the pin armed and silently pin the entry when it eventually loaded.
        /// </remarks>
        public void Unpin(string key)
        {
            TryUnpin(key);
        }

        /// <summary>
        /// Unpin an entry and cancel any pin still pending for that key, reporting whether either had
        /// something to undo. Callable from any thread.
        /// </summary>
        /// <returns>
        /// <c>true</c> if a cached entry was pinned and is not any more, or a pending pin was
        /// cancelled. <c>false</c> means there was no pin of either kind under this key.
        /// </returns>
        public bool TryUnpin(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            bool unpinned = false;
            bool cancelledPending;

            // Nothing to undo in a disposed cache. See Remove().
            if (!TryEnterWriteLock()) return false;

            try
            {
                cancelledPending = _pendingPins.Remove(key);

                if (_cache.TryGetValue(key, out var entry))
                {
                    unpinned = entry.IsPinned;
                    entry.IsPinned = false;
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            if ((unpinned || cancelledPending) && _config.LogTierOperations)
            {
                Debug.Log($"[ThreadSafeCacheManager] Unpinned '{key}' (cached entry: {unpinned}, pending pin cancelled: {cancelledPending}).");
            }

            return unpinned || cancelledPending;
        }

        /// <summary>
        /// Clear all entries, giving back the cache's own reference to every handle it holds.
        /// Callable from any thread: handles are collected while the write lock is held and released
        /// only after it is released (never release a handle while holding the write lock), and the
        /// release itself is marshalled to the main thread — see <see cref="ReleaseOnMainThread"/>.
        /// Pending pins are dropped along with the entries: leaving them armed would re-pin whatever
        /// happened to load next under those keys, long after the caller emptied the cache.
        /// </summary>
        public void Clear()
        {
            List<IAssetHandle<T>> handles;

            // Dispose already cleared and released everything. See Remove().
            if (!TryEnterWriteLock()) return;

            try
            {
                handles = new List<IAssetHandle<T>>(_cache.Count);
                foreach (var entry in _cache.Values)
                {
                    handles.Add(entry.Handle);
                }

                _cache.Clear();
                Interlocked.Exchange(ref _currentCacheSize, 0);
                _pendingPins.Clear();
                _budgetShortfallReported = false;
                ResetStatistics();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            foreach (var handle in handles)
            {
                ReleaseOnMainThread(handle);
            }
        }

        /// <summary>
        /// Select eviction victims and remove their entries (must be called within the write lock).
        /// Does NOT release the victims' handles itself — releasing must never happen while the write
        /// lock is held, so the removed handles are returned for the caller to release once it has
        /// exited the lock.
        /// </summary>
        /// <remarks>
        /// RE-ENTRANCY FROM <see cref="Set"/> (HANDOFF_TO_SESSION_B.md C-13). This runs synchronously
        /// from inside <c>Set()</c>, still under the same write lock. There is no lock-recursion
        /// hazard — this method takes no lock of its own, so the <c>NoRecursion</c> policy is never
        /// re-entered — and, contrary to the original work order, the entry <c>Set()</c> is inserting
        /// is never the victim: a brand-new <see cref="CacheEntry{T}"/> is born <c>Tier = Hot</c>
        /// with <c>AccessCount = 1</c>, so its score is <c>1/(1+0) * Mathf.Log(1+1) * 100 ≈ 69.3</c>,
        /// and the gate is <c>Tier == Cold || score &lt; EvictionScoreThreshold</c> against shipped
        /// thresholds of 1.0 / 3.0 / 0.5. That ~69 is a safety margin, not a coincidence: raising
        /// <c>EvictionScoreThreshold</c> past it arms exactly the failure the work order imagined.
        ///
        /// <para>What that immunity costs, and what changed here: <c>amountToEvict</c> is measured
        /// against every byte in the cache, including bytes this pass structurally cannot reclaim —
        /// the entry just inserted, pinned entries, anything still above the threshold. The loop used
        /// to chase that number with the gate re-tested inside it, so an unreachable demand was
        /// indistinguishable from a met one: it consumed every candidate and logged "eviction
        /// complete" either way. A single insert larger than
        /// <c>MaxCacheSizeBytes * EvictionTargetRatio</c> could therefore sweep the rest of the cache
        /// away and still leave it over budget, silently, on every subsequent <c>Set()</c>. The gate
        /// is now applied once up front so the reclaimable pool can be measured before anything is
        /// drawn from it, the demand is clamped to that pool, and the residue it could not take is
        /// reported. The victim set is unchanged — the gate is per-entry and order-independent.
        /// Admission-side counterpart: <see cref="IsLargerThanEvictionTarget"/>.</para>
        /// </remarks>
        private List<IAssetHandle<T>> PerformEviction(List<string> deferredLogs, List<string> deferredWarnings)
        {
            var released = new List<IAssetHandle<T>>();

            if (_config.MaxCacheSizeBytes <= 0)
                return released;

            long targetSize = (long)(_config.MaxCacheSizeBytes * _config.EvictionTargetRatio);
            long overage = _currentCacheSize - targetSize;

            if (overage <= 0)
            {
                _budgetShortfallReported = false;
                return released;
            }

            // Candidates = the entries this pass can actually take. The eviction gate is applied
            // HERE, once, rather than inside the drain loop below, so the pool can be measured
            // before anything is drawn from it.
            var candidates = new List<CacheEntry<T>>();
            long reclaimable = 0;

            foreach (var entry in _cache.Values)
            {
                if (entry.IsPinned)
                    continue;

                float score = entry.CalculateTierScore();
                if (entry.Tier == CacheTier.Cold || score < _config.EvictionScoreThreshold)
                {
                    // Snapshot the score the entry was SELECTED on, so it is the score the entry is
                    // ORDERED on below. CalculateTierScore() reads Time.realtimeSinceStartup twice
                    // (via GetTimeSinceLastAccess and GetAge) and that clock is not frame-latched, so
                    // recomputing it inside the comparer fed List<T>.Sort a comparison whose answer
                    // drifts mid-sort: a pair compared early and re-compared late can invert, which
                    // breaks transitivity, and introsort surfaces that as
                    // "InvalidOperationException: IComparer.Compare() method returns inconsistent
                    // results" — thrown out of Set(), i.e. out of a load, with the write lock held.
                    // AssetLoader.PerformEviction already does exactly this (see its EvictionOrder
                    // comparer and CachedAsset.SortScore); this pass had not been given the fix.
                    entry.SortScore = score;
                    candidates.Add(entry);
                    reclaimable += entry.EstimatedSize;
                }
            }

            // Sort by tier (Cold first) then by the snapshotted score (low first)
            candidates.Sort(EvictionOrder);

            // The cache is `overage` bytes over target; this pass can only ever produce
            // `reclaimable`. Chase the smaller of the two, so reaching the goal and exhausting the
            // candidates stop being the same observable outcome.
            long amountToEvict = Math.Min(overage, reclaimable);

            long evictedSize = 0;
            int evictedCount = 0;

            foreach (var entry in candidates)
            {
                if (evictedSize >= amountToEvict)
                    break;

                if (_cache.TryRemove(entry.Key, out _))
                {
                    released.Add(entry.Handle);
                    evictedSize += entry.EstimatedSize;
                    evictedCount++;
                }
            }

            Interlocked.Add(ref _currentCacheSize, -evictedSize);
            Interlocked.Add(ref _totalEvictions, evictedCount);

            if (_config.LogTierOperations)
            {
                deferredLogs.Add(
                    $"[ThreadSafeCacheManager] Eviction complete: {evictedCount} entries, " +
                    $"{evictedSize / 1024}KB freed of {overage / 1024}KB over budget " +
                    $"({reclaimable / 1024}KB was reclaimable).");
            }

            ReportUnreachableBudget(targetSize, overage, evictedSize, deferredWarnings);

            return released;
        }

        /// <summary>
        /// Cold first, then lowest snapshotted score first — the eviction order. Reads
        /// <see cref="CacheEntry{T}.SortScore"/> rather than recomputing, so the comparison cannot
        /// drift with the clock mid-sort. Mirrors <c>AssetLoader.EvictionOrder</c>.
        /// </summary>
        private static readonly Comparison<CacheEntry<T>> EvictionOrder = (a, b) =>
        {
            int byTier = b.Tier.CompareTo(a.Tier); // Cold (2) first
            return byTier != 0 ? byTier : a.SortScore.CompareTo(b.SortScore);
        };

        /// <summary>
        /// Report the case where the cache is still over the eviction target after a pass — an
        /// over-budget state no further eviction can resolve, because the pass already decided the
        /// remaining bytes are not reclaimable. Latched so it reports once per over-budget episode
        /// rather than once per <see cref="Set"/>; the latch clears as soon as a pass ends back under
        /// target. Caller holds the write lock, so the latch needs no separate guard.
        /// </summary>
        private void ReportUnreachableBudget(long targetSize, long overage, long evictedSize, List<string> deferredWarnings)
        {
            if (_currentCacheSize <= targetSize)
            {
                _budgetShortfallReported = false;
                return;
            }

            if (_budgetShortfallReported)
                return;

            _budgetShortfallReported = true;

            deferredWarnings.Add(
                $"[ThreadSafeCacheManager<{typeof(T).Name}>] Eviction cannot reach its target: " +
                $"{_currentCacheSize / 1024}KB still cached against a {targetSize / 1024}KB " +
                $"post-eviction target ({overage / 1024}KB was over budget, {evictedSize / 1024}KB " +
                $"freed). The rest is held by entries no pass will take — pinned entries, and entries " +
                $"still scoring at or above EvictionScoreThreshold ({_config.EvictionScoreThreshold:F2}), " +
                $"which always includes anything inserted this frame. Until that changes, every Set() " +
                $"runs a sweep that cannot succeed. Reported once per over-budget episode.");
        }

        /// <summary>
        /// Get cache statistics. Callable from any thread — reads plain fields and counters under
        /// the read lock, no Unity API (in particular, no <c>CalculateTierScore()</c>).
        /// </summary>
        public TieredCacheStats GetStatistics()
        {
            // An all-zero snapshot is the truth about a disposed cache. See Remove().
            if (!TryEnterReadLock()) return default(TieredCacheStats);

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
                    PendingPins = _pendingPins.Count,
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
        /// Reset statistics. Callable from any thread — interlocked counter writes only.
        /// </summary>
        public void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalAccesses, 0);
            Interlocked.Exchange(ref _cacheHits, 0);
            Interlocked.Exchange(ref _totalEvictions, 0);
        }

        /// <summary>
        /// Get entry count. Callable from any thread.
        /// </summary>
        public int Count => _cache.Count;

        /// <summary>
        /// Get current cache size. Callable from any thread.
        /// </summary>
        public long CurrentSize => Interlocked.Read(ref _currentCacheSize);

        /// <summary>
        /// Teardown. Callable from any thread: the dictionary work is plain managed state under the
        /// write lock, and each handle release is marshalled to the main thread afterwards (see
        /// <see cref="ReleaseOnMainThread"/>). Deliberately does not throw on the wrong thread —
        /// a teardown path that throws just strands whatever it was tearing down.
        /// </summary>
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
                _pendingPins.Clear();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            // Release outside the lock — never release a handle while holding the write lock (C-8) —
            // and on the main thread, since a release can reach Addressables (C-7).
            foreach (var handle in handles)
            {
                ReleaseOnMainThread(handle);
            }

            // _rwLock is deliberately NOT disposed. This class publishes an "any thread" contract on
            // Remove/Clear/Pin/Unpin/GetStatistics, which is a direct invitation to hold the object
            // on a worker thread — and a worker that calls one of those the instant after Dispose
            // returns would take a lock that had just been disposed, getting ObjectDisposedException
            // out of a method documented to be unconditionally safe. Worse, disposing a
            // ReaderWriterLockSlim while any thread still holds it throws SynchronizationLockException
            // on the disposer, and the window between a racing TryEnterWriteLock acquiring and
            // releasing is exactly that. ReaderWriterLockSlim's Dispose only frees lazily-created
            // wait handles, so skipping it costs at most a couple of event handles on a contended
            // cache and buys a teardown that cannot throw on either side. The _disposed re-check
            // inside TryEnterWriteLock/TryEnterReadLock is what actually stops post-dispose mutation.
        }
    }
}
