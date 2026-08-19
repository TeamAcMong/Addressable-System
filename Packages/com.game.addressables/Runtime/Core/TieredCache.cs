using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AddressableManager.Loaders;

namespace AddressableManager.Core
{
    /// <summary>
    /// What a pin request actually did — the three outcomes <c>TryPin</c>'s <c>bool</c> cannot tell
    /// apart. Returned by <see cref="TieredCache{T}.PinWithOutcome"/> and
    /// <see cref="ThreadSafeCacheManager{T}.PinWithOutcome"/>.
    /// </summary>
    /// <remarks>
    /// <c>TryPin</c> returns <c>false</c> for both <see cref="Deferred"/> and <see cref="Refused"/>,
    /// and those are opposites: one means "protected as soon as the key arrives", the other means
    /// "this key will never be pinned". Repo invariant 4's carve-out for a two-valued return
    /// requires that the meanings never overlap; these do, and the distinction was observable only
    /// in the log. That is the same silent-pin-loss failure C-9 was raised to fix, reappearing one
    /// level up in the API rather than in the cache.
    ///
    /// <para>Additive rather than a change to <c>TryPin</c>'s signature, per invariant 6 — the same
    /// shape as <c>PoolClearOutcome</c>, which this wave introduced for the identical reason.</para>
    /// </remarks>
    public enum CachePinOutcome
    {
        /// <summary>An entry is cached under that key and is pinned as of this call.</summary>
        Pinned,

        /// <summary>
        /// Nothing is cached under that key yet. The pin is recorded and will be applied when the
        /// key is next stored — the supported "pin the critical asset, then load it" order.
        /// </summary>
        Deferred,

        /// <summary>
        /// The pin was <b>rejected</b> and will never be applied: too many pins are already waiting
        /// for keys that have never been cached. Pinning addresses that are never loaded is the
        /// usual cause. Also logged as an error.
        /// </summary>
        Refused
    }

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
    ///
    /// <para><b>Pinning is order-independent:</b> <see cref="Pin"/> on a key that is not cached yet
    /// is recorded and applied when that key is next stored, so the "pin the critical asset, then
    /// load it" order README teaches actually pins something (HANDOFF_TO_SESSION_B.md C-9). Use
    /// <see cref="TryPin"/> when you need to know which of the two happened, and
    /// <c>GetStatistics().PendingPins</c> to see how many pins are still waiting for their key.</para>
    /// </summary>
    public class TieredCache<T> : IDisposable, ITieredCache where T : class
    {
        private readonly Dictionary<AssetCacheKey, CacheEntry<T>> _cache = new Dictionary<AssetCacheKey, CacheEntry<T>>();
        private readonly TieredCacheConfig _config;

        // Byte accounting for this cache. Shareable across sibling caches via the internal ctor
        // (HANDOFF_TO_SESSION_B.md L-4) — the eviction gate and eviction target below read THIS, not
        // a bare per-instance total, so a group of caches sharing one budget is capped at one real
        // ceiling instead of one per instance.
        //
        // NOTE (L-7): no loader owns a group of these any more. AssetLoader's built-in tiering keeps
        // one byte total over its single (address, Type) dictionary and never constructs a
        // TieredCache<T>. In practice every instance today is a standalone cache from
        // Advanced.CreateTieredCache<T> with a private budget of its own; the sharing mechanism is
        // kept because the internal ctor is still part of this type's contract.
        // Standalone callers (AdvancedAPI.CreateTieredCache<T>) get a private budget of their own —
        // see the public constructor below — so this is never null.
        private readonly CacheBudget _budget;

        private float _lastEvaluationTime;

        // This cache's own contribution to _budget, tracked in lockstep with every _budget.TryAdmit/
        // Give call below so GetStatistics() can report a figure for this instance alone when the
        // budget is shared (L-4's "reporting half" — see CacheBudget's remarks for the "enforcement
        // half"). With an unshared budget the two numbers are the same.
        private long _currentCacheSize;
        private bool _disposed;

        // Keys a caller asked to pin while nothing was cached under them (HANDOFF_TO_SESSION_B.md
        // C-9). Set() consumes an entry from here on insert, so a pin placed before the load — the
        // order README:326-332 and README:416-417 both teach — lands on the entry when it arrives
        // instead of evaporating. Bounded: a caller that pins addresses it never loads would
        // otherwise grow this without limit for the lifetime of the cache.
        private readonly HashSet<string> _pendingPins = new HashSet<string>();
        private const int MaxPendingPins = 256;

        // Latch for PerformEviction's "this cache alone cannot reach the budget target" warning, so
        // an unfixable over-budget state is reported once per episode rather than on every Set().
        private bool _budgetShortfallReported;

        // Re-entrancy guard for PerformEviction. Its release loop reaches Addressables.Release ->
        // destroyed objects -> game code -> a load -> Set() -> PerformEviction again. A nested pass
        // would select from a dictionary the outer pass is mid-way through draining and give the same
        // bytes back to the budget twice. Matches AssetLoader.PerformEviction's _evicting guard.
        private bool _evicting;

        // Statistics
        private int _totalAccesses;
        private int _cacheHits;
        private int _totalEvictions;
        private int _totalPromotions;
        private int _totalDemotions;

        /// <summary>
        /// Create a standalone tiered cache with its own private byte budget — the public,
        /// unshared constructor used directly by <c>AdvancedAPI.CreateTieredCache&lt;T&gt;</c> and
        /// any other external caller.
        /// </summary>
        /// <remarks>
        /// This is the only constructor with callers today. Tiering inside a loader is a
        /// configuration of <c>AssetLoader</c> now and does not use this type at all, so a
        /// standalone cache has no siblings to share a budget with — which is correct, because L-4
        /// was only ever about the several caches one loader used to own.
        /// </remarks>
        public TieredCache(TieredCacheConfig config = null)
            : this(config, null)
        {
        }

        /// <summary>
        /// Create a tiered cache that accounts against <paramref name="sharedBudget"/> instead of a
        /// private one. Internal — same-assembly callers only — so this adds a new constructor
        /// overload rather than changing the public one's signature (repo invariant 6).
        /// </summary>
        /// <remarks>
        /// No caller today. Its original one was the loader that owned a cache per <c>Type</c>; that
        /// loader is now a forwarder onto <c>AssetLoader</c>, whose tiering keeps a single byte total
        /// over a single dictionary and needs no shared budget. Kept because it is the seam that
        /// makes budget sharing possible at all, and removing it would force the public ctor to
        /// change shape.
        /// </remarks>
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
        /// it — only the access time is bumped, and <paramref name="handle"/> is left completely
        /// untouched: not stored, not retained, and <b>not released</b>.
        ///
        /// <para><b>The ownership rule is uniform on every path: the caller keeps its own reference
        /// and releases it when done.</b> This method used to release the caller's handle on the
        /// duplicate path, which gave one void method two opposite contracts — and a caller that did
        /// the documented thing then over-released. That was invisible for a singly-owned handle
        /// (Release at zero is a no-op) and unloaded the asset out from under AssetLoader in the
        /// normal case, where the loader's cache holds a second reference. It also could not be
        /// detected: after the release the count was still 1, so <c>IsValid</c> read true.</para>
        ///
        /// If the existing entry's handle has died without going through this cache (e.g.
        /// force-released by its owning loader), it is treated the same way <see cref="TryGet"/>
        /// treats it — as a stale/missing entry — so the incoming handle replaces it instead of being
        /// rejected into a zombie slot.
        ///
        /// A pin requested for <paramref name="key"/> before anything was cached under it
        /// (see <see cref="TryPin"/>) is applied to the new entry here, before the eviction check
        /// below runs.
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

            // If the entry exists and is still alive, just record the access. The handle passed in is
            // NOT stored - and it is NOT released either.
            //
            // It used to be released here, which gave Set() two opposite ownership outcomes behind one
            // void return: on the fresh-key path below the cache takes its own reference via TryRetain
            // and the caller keeps the birth reference, while on this path the caller's reference was
            // spent. A caller doing the documented thing - Set(...) then Release() when it is done -
            // therefore released a reference it no longer owned. Release() at zero is a no-op, so this
            // was invisible for a handle with one owner; for the normal case, where AssetLoader's cache
            // holds a second reference, the extra Release decremented the LOADER's reference and
            // Addressables unloaded an asset the loader still had listed as cached.
            //
            // A void method can only express one contract, so it expresses the uniform one: the caller
            // always keeps its own reference, on every path.
            if (_cache.TryGetValue(cacheKey, out var existingEntry))
            {
                if (existingEntry.Handle.IsValid)
                {
                    existingEntry.RecordAccess();
                    if (!ReferenceEquals(existingEntry.Handle, handle) && _config.LogTierOperations)
                    {
                        Debug.LogWarning(
                            $"[TieredCache] Set() ignored a duplicate handle for already-cached key '{key}'. " +
                            "The cached handle was kept; your handle is untouched and is still yours to release.");
                    }
                    return;
                }

                // Stale entry whose handle died outside this cache — mirror TryGet()'s handling:
                // drop the zombie slot instead of rejecting the caller's freshly loaded handle into
                // it (which would silently unload the very asset that was just loaded).
                CarryPinAcrossStaleEntry(key, existingEntry);
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

            // A pin placed before this key was ever cached applies now, on arrival (C-9). Do this
            // BEFORE the eviction check below: a freshly pinned entry must already be pinned by the
            // time PerformEviction() builds its candidate list, or the very entry the caller asked
            // to protect is a candidate in the pass its own insert triggered.
            if (_pendingPins.Remove(key))
            {
                entry.IsPinned = true;
                entry.Tier = CacheTier.Hot;

                if (_config.LogTierOperations)
                {
                    Debug.Log($"[TieredCache] Applied a pending pin to '{key}' as it entered the cache.");
                }
            }

            _cache[cacheKey] = entry;
            _currentCacheSize += estimatedSize;
            _budget.TryAdmit(estimatedSize);

            // Check if we need to evict — against the SHARED budget (L-4), not this cache's own
            // slice of it, so a loader juggling several types is capped at one real ceiling.
            if (_config.EnableAutoEviction && _budget.Max > 0)
            {
                WarnIfLargerThanEvictionTarget(key, estimatedSize);

                if (_budget.UsageRatio >= _config.EvictionTriggerRatio)
                {
                    PerformEviction();
                }
            }
        }

        /// <summary>
        /// Warn when a single entry is, on its own, larger than the size eviction targets — the
        /// admission half of HANDOFF_TO_SESSION_B.md C-13.
        /// </summary>
        /// <remarks>
        /// <see cref="PerformEviction"/> cannot take the entry <see cref="Set"/> is inserting: a
        /// brand-new <see cref="CacheEntry{T}"/> is born <c>Tier = Hot</c> with
        /// <c>AccessCount = 1</c>, so its score is <c>1/(1+0) * Mathf.Log(1+1) * 100 ≈ 69.3</c> —
        /// far above every shipped <c>EvictionScoreThreshold</c> — and the eviction gate is
        /// <c>Tier == Cold || score &lt; threshold</c>. That immunity is deliberate and is what
        /// stops a load from evicting the handle it just produced. But it also means an entry
        /// bigger than <c>Max * EvictionTargetRatio</c> puts the budget into a state eviction
        /// cannot resolve while it stays cached: the trigger fires on every subsequent
        /// <see cref="Set"/> — in this cache and, because the budget is shared (L-4), in every
        /// sibling cache too — and each of those passes sweeps its own entries toward a target it
        /// can never reach. Today the only trace of that is a debug-level "eviction complete" line
        /// with a number nobody compares to anything. This is the missing signal, and it is
        /// deliberately not gated behind <c>LogTierOperations</c>: the entry is still cached, so
        /// nothing else will ever surface it.
        /// </remarks>
        private void WarnIfLargerThanEvictionTarget(string key, long estimatedSize)
        {
            if (estimatedSize <= 0)
                return;

            long targetSize = (long)(_budget.Max * _config.EvictionTargetRatio);
            if (estimatedSize <= targetSize)
                return;

            Debug.LogWarning(
                $"[TieredCache] '{key}' is {estimatedSize / 1024}KB on its own, larger than the " +
                $"post-eviction target of {targetSize / 1024}KB ({_config.EvictionTargetRatio:P0} of " +
                $"the {_budget.Max / 1024}KB shared budget). It was cached, but no eviction pass can " +
                $"bring the budget back to target while it is cached — an entry is never evictable in " +
                $"the same pass that inserts it, and until this one cools to Cold every Set() will " +
                $"run a sweep that cannot reach its target. Raise MaxCacheSizeBytes, or keep an asset " +
                $"this size out of the tiered cache.");
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
                CarryPinAcrossStaleEntry(key, entry);
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
        /// Pin an entry so eviction skips it. If nothing is cached under <paramref name="key"/>
        /// yet, the request is <em>remembered</em> and applied the moment that key is stored, so
        /// pinning before the load works exactly as documented.
        /// </summary>
        /// <remarks>
        /// This method stays <c>void</c> — it is public API and repo invariant 6 forbids changing a
        /// public signature before 5.0.0 — so it cannot report which of the two happened. Callers
        /// who need to know should use <see cref="TryPin"/>, the additive overload that returns it.
        /// See <see cref="TryPin"/> for why deferral (rather than a bare warning) is the fix for
        /// HANDOFF_TO_SESSION_B.md C-9.
        /// </remarks>
        public void Pin(string key)
        {
            TryPin(key);
        }

        /// <summary>
        /// Pin an entry so eviction skips it, reporting whether a cached entry was pinned right now
        /// (<c>true</c>) or the pin was deferred until the key is next stored (<c>false</c>).
        /// Additive companion to <see cref="Pin"/>; the two never disagree about what they do, only
        /// about whether they tell you.
        /// </summary>
        /// <returns>
        /// <c>true</c> — an entry is cached under <paramref name="key"/> and is pinned as of this
        /// call. <c>false</c> — the pin was <em>either</em> deferred (recorded; <see cref="Set"/>
        /// will apply it when the key arrives) <em>or</em> refused outright (the pending list is
        /// full, so it will never be applied, and an error is logged).
        /// <b>This return value cannot tell those two apart, and they are opposites</b> — use
        /// <see cref="PinWithOutcome"/>, which returns a <see cref="CachePinOutcome"/>, when the
        /// difference matters. <c>bool</c> is kept here because this is shipped public API
        /// (invariant 6).
        /// </returns>
        /// <remarks>
        /// WHY DEFERRAL AND NOT JUST A WARNING (HANDOFF_TO_SESSION_B.md C-9)
        ///
        /// The old body had no <c>else</c>: pinning a key that was not cached yet did nothing at
        /// all, and README teaches exactly that order twice (<c>README.md:326-332</c> pins
        /// <c>"UI/CoreIcon"</c> before any load, <c>README.md:416-417</c> pins straight after
        /// <c>Advanced.CreateTieredLoader</c> on a loader holding nothing). The caller believed a
        /// critical asset was protected, <c>GetStatistics().PinnedEntries</c> read 0, and — since
        /// <c>IsPinned</c> is eviction's only exemption (C-10) — the asset was evicted like any
        /// other.
        ///
        /// A warning alone would not have fixed the documented path: the whole public route into
        /// this method was <c>void</c> end to end, and the files on it could not grow a return value
        /// before 5.0.0 either. (That route ran through the tiered loader, which is a forwarder onto
        /// <c>AssetLoader</c> now; <c>AssetLoader.PinAsset</c> defers pins the same way, for the same
        /// reason.) Deferring makes the order README already teaches correct,
        /// which is better than making both README examples wrong and shouting about it. The
        /// deferral is still observable three ways: this return value, the
        /// <c>PendingPins</c> figure in <see cref="GetStatistics"/>, and a log line under
        /// <c>LogTierOperations</c>. It is deliberately <em>not</em> an unconditional warning —
        /// pin-then-load is now a supported order, and warning on the supported path is noise.
        /// </remarks>
        public bool TryPin(string key) => PinWithOutcome(key) == CachePinOutcome.Pinned;

        /// <summary>
        /// Unpin an entry to allow eviction, and cancel any pin still pending for that key.
        /// </summary>
        /// <remarks>
        /// Cancelling the pending pin is the half that did not exist before: with
        /// <see cref="TryPin"/> now deferring, a <c>Pin(key)</c> / <c>Unpin(key)</c> pair on a key
        /// that is not cached yet would otherwise leave the pin armed and silently pin the entry
        /// when it eventually loaded — a worse version of the bug C-9 describes, in the opposite
        /// direction. Stays <c>void</c> for the same invariant-6 reason as <see cref="Pin"/>; see
        /// <see cref="TryUnpin"/> for the answer.
        /// </remarks>
        public void Unpin(string key)
        {
            TryUnpin(key);
        }

        /// <summary>
        /// Unpin an entry and cancel any pin still pending for that key, reporting whether either
        /// of those actually had something to undo. Additive companion to <see cref="Unpin"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if a cached entry was pinned and is not any more, or a pending pin was
        /// cancelled. <c>false</c> means there was no pin of either kind under this key — the call
        /// changed nothing.
        /// </returns>
        public bool TryUnpin(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            bool cancelledPending = _pendingPins.Remove(key);
            bool unpinned = false;

            if (_cache.TryGetValue(new AssetCacheKey(key, typeof(T)), out var entry))
            {
                unpinned = entry.IsPinned;
                entry.IsPinned = false;
            }

            if ((unpinned || cancelledPending) && _config.LogTierOperations)
            {
                Debug.Log($"[TieredCache] Unpinned '{key}' (cached entry: {unpinned}, pending pin cancelled: {cancelledPending}).");
            }

            return unpinned || cancelledPending;
        }

        /// <summary>
        /// Pin an entry so eviction skips it, reporting which of the three things actually happened.
        /// </summary>
        /// <returns>
        /// <see cref="CachePinOutcome.Pinned"/>, <see cref="CachePinOutcome.Deferred"/> or
        /// <see cref="CachePinOutcome.Refused"/> — see that enum for what each one obliges the
        /// caller to do.
        /// </returns>
        /// <remarks>
        /// Prefer this to <see cref="TryPin"/>. <c>TryPin</c> collapses <c>Deferred</c> and
        /// <c>Refused</c> into one <c>false</c>, and they are opposites: the first means the asset
        /// will be protected the moment it loads, the second means it never will be. <c>TryPin</c>
        /// keeps its signature because it is shipped public API (invariant 6); this is the additive
        /// version that can tell the truth.
        /// </remarks>
        public CachePinOutcome PinWithOutcome(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (_cache.TryGetValue(new AssetCacheKey(key, typeof(T)), out var entry))
            {
                entry.IsPinned = true;
                entry.Tier = CacheTier.Hot;

                if (_config.LogTierOperations)
                {
                    Debug.Log($"[TieredCache] Pinned '{key}' (already cached).");
                }

                return CachePinOutcome.Pinned;
            }

            return RecordPendingPin(key);
        }

        /// <summary>
        /// Remember a pin for a key that is not cached yet, for <see cref="Set"/> to apply on
        /// arrival. Bounded — see <see cref="MaxPendingPins"/>.
        /// </summary>
        /// <returns>
        /// <see cref="CachePinOutcome.Deferred"/> if the pin is now armed (including when an earlier
        /// call already armed it), <see cref="CachePinOutcome.Refused"/> if the pending list is full
        /// and this pin will never be applied.
        /// </returns>
        private CachePinOutcome RecordPendingPin(string key)
        {
            if (_pendingPins.Contains(key))
                return CachePinOutcome.Deferred;

            if (_pendingPins.Count >= MaxPendingPins)
            {
                // The one case where a pin is genuinely refused rather than deferred, so this is an
                // error and is not gated behind LogTierOperations: nothing later will apply it.
                Debug.LogError(
                    $"[TieredCache] Pin('{key}') was refused: {MaxPendingPins} pins are already " +
                    $"waiting for keys that have never been cached. This key will NOT be pinned when " +
                    $"it loads. Pinning addresses that are never loaded is the usual cause — check " +
                    $"the addresses being passed to PinAsset.");
                return CachePinOutcome.Refused;
            }

            _pendingPins.Add(key);

            if (_config.LogTierOperations)
            {
                Debug.Log($"[TieredCache] Pin('{key}') found nothing cached under that key; the pin is now pending and will be applied when the key is stored.");
            }

            return CachePinOutcome.Deferred;
        }

        /// <summary>
        /// Re-arm a pin as pending when the entry carrying it is dropped as stale (its handle died
        /// outside this cache). Without this, a pinned entry that gets force-released by its owning
        /// loader comes back unpinned on the next load and the pin is lost with no trace — the same
        /// silent loss C-9 is about, reached by a different route.
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
        /// Clear all cache entries. Teardown semantics, same as <see cref="ForceReleaseAll"/> (which
        /// this delegates to) and matching
        /// <see cref="AddressableManager.Loaders.AssetLoader.ClearCache"/>'s documented
        /// memory-pressure contract (HANDOFF_TO_SESSION_B.md L-1, which specifies
        /// <c>ForceRelease()</c> for <c>TieredCache&lt;T&gt;.Clear</c>/<c>Dispose</c> and for
        /// <c>ClearCache</c>/<c>Dispose</c>).
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
        /// <remarks>
        /// The dictionary is snapshotted and emptied BEFORE anything is released. A release ends in
        /// <c>Addressables.Release</c>, which can destroy objects, which can run game code, which can
        /// re-enter this cache (<see cref="Remove"/>, <see cref="Set"/>, a nested
        /// <c>ForceReleaseAll</c>) — and mutating <c>_cache</c> while a <c>foreach</c> walks
        /// <c>_cache.Values</c> throws <c>InvalidOperationException: Collection was modified</c> out
        /// of a teardown path. Emptying first also means a re-entrant caller sees an empty cache
        /// rather than entries whose handles are already dead.
        /// </remarks>
        public void ForceReleaseAll()
        {
            var entries = new CacheEntry<T>[_cache.Count];
            _cache.Values.CopyTo(entries, 0);

            _budget.Give(_currentCacheSize);
            _cache.Clear();
            _currentCacheSize = 0;

            // Pending pins are dropped with everything else: this is "empty the cache", and leaving
            // armed pins behind would re-pin whatever happened to be loaded next under those keys,
            // long after the caller believed the cache was gone. Reset with the rest of the state,
            // before the releases below, so a re-entrant Set() lands in a fully-emptied cache rather
            // than one this method is still halfway through clearing.
            _pendingPins.Clear();
            _budgetShortfallReported = false;

            ResetStatistics();

            foreach (var entry in entries)
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
        /// Targets the budget's overage (HANDOFF_TO_SESSION_B.md L-4), not a per-instance slice of
        /// it — <paramref name="amountToEvict"/>-equivalent below is <c>_budget.Current - target</c>,
        /// while the candidates evicted still only ever come from this cache's own entries (a cache
        /// has no visibility into siblings sharing its budget).
        ///
        /// <para>WHAT CHANGED (L-7). That gap used to be closed from outside: a loader owning one
        /// cache per <c>Type</c> walked every sibling in turn, each re-reading the smaller overage
        /// left by the last, so a full round-robin converged on the real ceiling even though no
        /// single call crossed a type boundary. No such walker exists any more — tiering moved into
        /// <c>AssetLoader</c>, which ranks candidates of every <c>Type</c> together in one pass over
        /// one dictionary and never uses this type. For the standalone caches that remain, the
        /// budget is private and unshared, so "the overage" and "this cache's overage" are the same
        /// number and the question does not arise. If you construct several caches over one shared
        /// budget via the internal ctor, you own the round-robin: a single call here can only
        /// reclaim what this instance holds.</para>
        ///
        /// <para>RE-ENTRANCY FROM <see cref="Set"/> (HANDOFF_TO_SESSION_B.md C-13). This runs
        /// synchronously from inside <c>Set()</c>, which is itself reached from the middle of a load
        /// (<c>TieredAssetLoader.LoadAssetAsync</c> → <c>cache.Set</c>). That re-entrancy is real,
        /// but it does <b>not</b> put the handle of the load in progress at risk, and the work order
        /// that said it did was wrong. (The named caller is historical — that loader forwards to
        /// <c>AssetLoader</c> now — but any caller of <see cref="Set"/> reaches this the same way.) A brand-new <see cref="CacheEntry{T}"/> is born
        /// <c>Tier = Hot</c> with <c>AccessCount = 1</c>, so <c>CalculateTierScore()</c> sees
        /// <c>timeSinceAccess = 0</c> (recency 1) and <c>age = 0</c> (frequency
        /// <c>Mathf.Log(2) ≈ 0.693</c>) and returns <c>≈ 69.3</c>; the gate below is
        /// <c>Tier == Cold || score &lt; EvictionScoreThreshold</c>, and every shipped threshold is
        /// 1.0 / 3.0 / 0.5. Neither arm matches, so the entry survives the pass its own insert
        /// triggered. <b>That ~69 is a safety margin, not a coincidence:</b> anyone raising
        /// <c>EvictionScoreThreshold</c> past it is arming exactly the failure the work order
        /// imagined.</para>
        ///
        /// <para>WHAT THAT IMMUNITY ACTUALLY COSTS, AND WHAT CHANGED HERE. The demand
        /// (<c>_budget.Current - targetSize</c>) counts every byte in the budget, including bytes
        /// this pass structurally cannot reclaim: the entry just inserted, pinned entries, and
        /// anything still scoring above the threshold. The drain loop used to chase that number
        /// while re-testing the gate inside the loop, so when the demand was unreachable it simply
        /// consumed every candidate and logged "eviction complete" with a figure nobody compares to
        /// the target — a single insert larger than <c>Max * EvictionTargetRatio</c> could sweep the
        /// rest of the cache away and still leave the budget over, silently, on every subsequent
        /// <c>Set()</c>. The pass now applies the gate <em>once, up front</em>, measures the pool it
        /// is actually drawing from, clamps the demand to it, and reports the residue it could not
        /// take. Which entries get evicted is unchanged (the gate is per-entry and
        /// order-independent); what changed is that the number the loop chases is now one it can
        /// reach, and the case where it cannot is no longer silent. Admission-side counterpart:
        /// <see cref="WarnIfLargerThanEvictionTarget"/>.</para>
        /// </remarks>
        private void PerformEviction()
        {
            // RE-ENTRANCY GUARD. PerformEvictionCore's release loop reaches Addressables.Release ->
            // destroyed objects -> game code -> a load -> Set() -> here again. A nested pass would
            // select victims from a dictionary the outer pass is still draining, and would give the
            // same bytes back to the shared budget twice. The outer pass is already evicting, so the
            // nested call is a no-op. Same guard, same reason, as AssetLoader.PerformEviction.
            if (_evicting)
                return;

            _evicting = true;
            try
            {
                PerformEvictionCore();
            }
            finally
            {
                _evicting = false;
            }
        }

        /// <inheritdoc cref="PerformEviction"/>
        private void PerformEvictionCore()
        {
            if (_budget.Max <= 0)
                return;

            long targetSize = (long)(_budget.Max * _config.EvictionTargetRatio);
            long overage = _budget.Current - targetSize;

            if (overage <= 0)
            {
                _budgetShortfallReported = false;
                return;
            }

            // Candidates = the entries this pass can actually take. The eviction gate is applied
            // HERE, once, instead of inside the drain loop below, so the pool can be measured
            // before anything is drawn from it. Same victim set either way — the gate is per-entry
            // and independent of iteration order — but the demand below can now be honest about
            // what is reachable.
            var candidates = _cache.Values
                .Where(e => !e.IsPinned &&
                            (e.Tier == CacheTier.Cold ||
                             e.CalculateTierScore() < _config.EvictionScoreThreshold))
                .OrderByDescending(e => e.Tier) // Cold first
                .ThenBy(e => e.CalculateTierScore()) // Lowest score first
                .ToList();

            long reclaimable = 0;
            foreach (var candidate in candidates)
            {
                reclaimable += candidate.EstimatedSize;
            }

            // The budget wants `overage` bytes back; this pass can only ever produce `reclaimable`.
            // Chase the smaller of the two, so reaching the goal and exhausting the candidates are
            // no longer indistinguishable outcomes.
            long amountToEvict = Math.Min(overage, reclaimable);

            long evictedSize = 0;
            int evictedCount = 0;
            var keysToRemove = new List<AssetCacheKey>();

            foreach (var entry in candidates)
            {
                if (evictedSize >= amountToEvict)
                    break;

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

            // Remove evicted entries, releasing the cache's own reference to each handle.
            //
            // RE-ENTRANCY. Release() reaches Addressables.Release, which can destroy objects, which
            // can run game code, which can call back into this cache — Remove, Clear/ForceReleaseAll,
            // Dispose, a TryGet that drops a stale entry, or a nested PerformEviction. Any of those
            // can take a key this loop has not reached yet, so:
            //   * the entry is looked up with TryGetValue, not the bare _cache[key] indexer, which
            //     threw KeyNotFoundException out of Set() and out of the load that triggered it;
            //   * the entry is removed from the dictionary BEFORE its handle is released, so a
            //     re-entrant pass cannot see and re-release it; and
            //   * bytes and counts are booked from what this loop actually removed, not from what it
            //     hoped to, so a nested pass that already gave the same bytes back cannot make the
            //     outer pass double-count them into _currentCacheSize and _budget.Give.
            long actuallyEvicted = 0;
            int actuallyEvictedCount = 0;

            foreach (var key in keysToRemove)
            {
                if (!_cache.TryGetValue(key, out var entry)) continue;

                _cache.Remove(key);
                actuallyEvicted += entry.EstimatedSize;
                actuallyEvictedCount++;

                entry.Handle?.Release();
            }

            evictedSize = actuallyEvicted;
            evictedCount = actuallyEvictedCount;

            _currentCacheSize -= evictedSize;
            _budget.Give(evictedSize);
            _totalEvictions += evictedCount;

            if (_config.LogTierOperations)
            {
                Debug.Log(
                    $"[TieredCache] Eviction complete: {evictedCount} entries, {evictedSize / 1024}KB " +
                    $"freed of {overage / 1024}KB over budget ({reclaimable / 1024}KB was reclaimable).");
            }

            ReportUnreachableBudget(targetSize, overage, evictedSize);
        }

        /// <summary>
        /// Report the case where this cache's own remaining bytes still exceed the whole eviction
        /// target after a pass — an over-budget state no further eviction can resolve.
        /// </summary>
        /// <remarks>
        /// The test is deliberately <c>_currentCacheSize &gt; targetSize</c> (this cache's own
        /// slice) rather than the budget's overage. When the budget is shared, a cache routinely
        /// finishes a pass with it still over — that was the normal, designed path back when a
        /// loader walked every sibling in turn and each chipped at the remainder — and warning about
        /// it would fire on healthy runs. (Standalone caches, which is all of them today, have a
        /// private budget where the two figures coincide.) When
        /// <em>this</em> cache alone is over the entire target after refusing to take those bytes,
        /// no sibling can fix it: the bytes are here, and this pass already decided they are not
        /// reclaimable. Latched so it reports once per over-budget episode instead of once per
        /// <c>Set()</c>; the latch clears as soon as a pass ends back under target.
        /// </remarks>
        private void ReportUnreachableBudget(long targetSize, long overage, long evictedSize)
        {
            if (_currentCacheSize <= targetSize)
            {
                _budgetShortfallReported = false;
                return;
            }

            if (_budgetShortfallReported)
                return;

            _budgetShortfallReported = true;

            Debug.LogWarning(
                $"[TieredCache<{typeof(T).Name}>] Eviction cannot reach its target: this cache still " +
                $"holds {_currentCacheSize / 1024}KB against a {targetSize / 1024}KB post-eviction " +
                $"target ({overage / 1024}KB was over budget, {evictedSize / 1024}KB freed). The rest " +
                $"is held by entries no pass will take — pinned entries, and entries still scoring at " +
                $"or above EvictionScoreThreshold ({_config.EvictionScoreThreshold:F2}), which always " +
                $"includes anything inserted this frame. Until that changes, every Set() runs a sweep " +
                $"that cannot succeed. Reported once per over-budget episode.");
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
                PendingPins = _pendingPins.Count,
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
        /// holds a reference (HANDOFF_TO_SESSION_B.md L-1).
        /// </summary>
        /// <remarks>
        /// <b><see cref="Clear"/> and <see cref="ForceReleaseAll"/> are the same operation</b> —
        /// <c>Clear()</c> is <c>=&gt; ForceReleaseAll();</c>. Both hard-release. Calling
        /// <see cref="ForceReleaseAll"/> here rather than <see cref="Clear"/> is a readability
        /// choice about naming the intent at the call site, not a semantic one, and picking between
        /// them for refcount reasons is meaningless.
        ///
        /// <para>This block used to claim the opposite: that <c>Clear()</c> "only gives back this
        /// cache's own reference (a decrement any other holder survives)" and that
        /// <c>ForceReleaseAll</c> was chosen over it for teardown. That was true of an older
        /// <c>Clear()</c> and was left behind when it was redirected — see <see cref="Clear"/>'s own
        /// remarks for why it was changed. A reader choosing between the two on that basis got the
        /// exact opposite of the truth about the refcount contract, which is why it is corrected
        /// rather than deleted.</para>
        ///
        /// <para>The hard release itself is the right teardown semantic and is unchanged: an owner
        /// going away entirely must not leave a caller holding a handle this cache no longer tracks
        /// and can never invalidate again. <see cref="Remove"/> remains the decrement.</para>
        /// </remarks>
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

        /// <summary>
        /// Pins asked for on keys that are not cached yet, waiting to be applied when the key
        /// arrives (HANDOFF_TO_SESSION_B.md C-9). Additive field: a non-zero value here is the
        /// difference between "pinning did nothing" and "pinning has not happened yet", which
        /// <see cref="PinnedEntries"/> alone could never tell apart — it read 0 in both cases,
        /// which is how a lost pin used to go unnoticed. A number that never falls to 0 means pins
        /// were placed on addresses that are never loaded.
        /// </summary>
        public int PendingPins;

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
                   $"Pinned: {PinnedEntries} (+{PendingPins} pending), " +
                   $"Size: {TotalSizeBytes / 1024}KB / {MaxSizeBytes / 1024}KB ({UsageRatio:P0}), " +
                   $"Hit Rate: {HitRate:P1}, " +
                   $"Evictions: {TotalEvictions}, Promotions: {TotalPromotions}, Demotions: {TotalDemotions}";
        }
    }
}
