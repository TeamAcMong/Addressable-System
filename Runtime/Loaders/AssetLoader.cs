using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Configs;
using AddressableManager.Core;
using AddressableManager.Threading;
#if UNITY_EDITOR
using AddressableManager.Monitoring;
#endif
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Core asset loader with caching, reference counting, and lifecycle management
    /// Automatically monitors all operations in Editor for Dashboard tracking
    ///
    /// ⚠️ THREAD SAFETY WARNING:
    /// AssetLoader is NOT thread-safe and must be called from Unity's main thread only.
    /// For thread-safe loading, use ThreadSafeAssetLoader wrapper instead.
    ///
    /// Ownership: a load hands its caller one reference. When a handle is cached the cache takes a
    /// second one, so a caller disposing its handle never pulls a shared asset out from under
    /// anyone else.
    ///
    /// ReleaseAsset() and ClearCache() are eviction: they free the bundle whatever the count says,
    /// on purpose — this is a memory-pressure API, and a caller elsewhere still holding a
    /// reference (a Standard.* caller sitting on its IAssetHandle, a SmartAssetHandle) is exactly
    /// the case a plain decrement would leave untouched, defeating the point of calling it under
    /// memory pressure. (Historically this was also the *only* way to reclaim anything Simple.Load
    /// and the rest of the return-the-asset API family loaded, because they leaked the reference
    /// they were born with on every call — see HANDOFF_TO_SESSION_B.md A-4. That leak is fixed now,
    /// so it is no longer why this is unconditional, only a reason it used to be load-bearing.)
    /// A handle a caller still holds across one of them reports IsValid == false afterwards rather
    /// than pointing at a freed asset. Instantiated GameObjects are untouched by both.
    ///
    /// Dispose() is teardown: it hard-releases everything this loader tracks — including label
    /// loads, which never enter the cache at all — plus every GameObject it instantiated.
    /// <see cref="AddressableManager.Progress.ProgressiveAssetLoader.LoadAssetWithProgressAsync{T}"/>
    /// delegates to <see cref="LoadAssetAsync{T}(string)"/> rather than opening its own raw
    /// operation (HANDOFF_TO_SESSION_B.md L-2), so a handle obtained that way is tracked and
    /// reached by teardown exactly like any other handle this loader produced.
    /// </summary>
    public class AssetLoader : IDisposable
    {
        // Cache: key = (address, Type), value = the handle (owner-side interface, so an entry can
        // be released without knowing T) plus the metadata the tiering configuration needs. The
        // entry is allocated whether or not this loader is tiered — see CachedAsset — so there is
        // one code shape here, not two.
        private readonly Dictionary<AssetCacheKey, CachedAsset> _assetCache = new();

        // Ledger of every handle this loader handed out, so teardown can reach the ones the cache
        // does not hold — label loads live only here.
        private readonly List<IOwnedHandle> _activeHandles = new();

        // Loads registered before their first await, so concurrent callers for one (address, Type)
        // join a single operation instead of each building a wrapper nobody will ever release.
        /// <summary>
        /// Bumped every time this loader's cache is invalidated or cleared. A load captures it before
        /// it starts and re-checks it before caching.
        /// </summary>
        /// <remarks>
        /// A catalog update that lands WHILE a load is in flight was previously invisible to that load.
        /// InvalidateAddress sweeps _assetCache, and an in-flight load has nothing there yet; the
        /// follow-up DropCompletedInFlight deliberately leaves running loads alone; and AfterAwait
        /// checks only thread and disposal. So the operation - whose locations were resolved eagerly
        /// against the PREVIOUS catalog - completed after the sweep and inserted a pre-update handle
        /// into a cache that had just been invalidated precisely to get rid of those. Every later
        /// caller was then served the old bundle's asset, with no error and nothing stale-looking about
        /// the handle. A counter is enough: the load only needs to know THAT an invalidation happened,
        /// not which keys it touched.
        /// </remarks>
        private int _invalidationEpoch;

        private readonly Dictionary<AssetCacheKey, TaskCompletionSource<IOwnedHandle>> _inFlightLoads = new();

        // Best-effort progress source for a load currently in flight — see GetLoadProgress<T>.
        // Populated only while Addressables.LoadAssetAsync is actually running for a key, so a
        // caller can poll real percent-complete without opening a second Addressables operation
        // purely to read it (HANDOFF_TO_SESSION_B.md L-2; ProgressiveAssetLoader is the consumer).
        private readonly Dictionary<AssetCacheKey, Func<float>> _inFlightProgress = new();

        // GameObjects instantiated through this loader. Addressables tracks instances separately
        // from asset handles, so teardown has to release them explicitly.
        private readonly List<GameObject> _instances = new();

        // Handles and instances added since the ledgers were last compacted.
        private int _sinceCompaction;

        private const int CompactionInterval = 64;

        // Read by continuations that may resume on another thread, so the write in Dispose() has
        // to be published rather than kept in a register.
        private volatile bool _disposed;

        // Scope name for monitoring (Editor-only, zero overhead in builds)
        private readonly string _scopeName;

        #region Tiering state

        // null == tiering off. The single source of truth for "is this loader tiered": every
        // tiering branch in this class tests this field and nothing else. Set only by the
        // (string, TieredCacheConfig) constructor, so it cannot change after the first load —
        // see that constructor for why a settable property would be wrong.
        private readonly TieredCacheConfig _tiering;

        // Byte total across EVERY entry in _assetCache, all Types. ONE number for the whole loader,
        // adjusted in exactly one place (RemoveCacheEntry) on the way out and one place
        // (CacheHandle) on the way in. There is deliberately no second per-type total to reconcile
        // it against: HANDOFF_TO_SESSION_B.md L-4 is the bug that shape produces, and the fix is
        // not "keep both in lockstep", it is "there is only one book".
        private long _tieredBytes;

        // -1 == never evaluated. NOT initialised from Time.realtimeSinceStartup in the constructor:
        // AssetLoader is constructed off the main thread by ThreadSafeAssetLoader, and
        // Time.realtimeSinceStartup throws there. The first main-thread cache hit latches the clock
        // instead — see MaybeEvaluateTiers.
        private float _lastTierEvaluation = -1f;

        private int _tierAccesses, _tierHits, _tierEvictions, _tierPromotions, _tierDemotions;

        // Reused across evictions so a sweep allocates nothing steady-state. Only ever touched
        // inside PerformEviction, which is re-entrancy-guarded (_evicting) for exactly that reason.
        private readonly List<CachedAsset> _evictScratch = new();
        private bool _evicting;

        // Pins asked for on keys nothing is cached under yet (HANDOFF_TO_SESSION_B.md C-9).
        // CacheHandle consumes an entry from here on insert, so the "pin the critical asset, then
        // load it" order the README teaches actually pins something. Keyed by AssetCacheKey rather
        // than by the plain address TieredCache<T> uses: this loader's cache spans every Type, so
        // an address alone would pin whichever type happened to arrive first. Bounded — a caller
        // pinning addresses it never loads would otherwise grow this for the loader's lifetime.
        private readonly HashSet<AssetCacheKey> _pendingPins = new();
        private const int MaxPendingPins = 256;

        // Latch for PerformEviction's "eviction cannot reach its target" warning, so an unresolvable
        // over-budget state is reported once per episode rather than on every cached load.
        private bool _tierShortfallReported;

        #endregion

        /// <summary>
        /// The scope name this loader was constructed with — read-only mirror of the
        /// constructor's <c>scopeName</c> parameter. Exists so extension-method call sites
        /// outside this class (e.g. <see cref="AddressableManager.Progress.ProgressiveAssetLoader"/>)
        /// can report to <see cref="AddressableManager.Monitoring.AssetMonitorBridge"/> under the
        /// same scope this loader itself uses, instead of a hardcoded literal. Additive public
        /// member — no existing signature changes (invariant 1).
        /// </summary>
        public string ScopeName => _scopeName;

        // Unity's main thread. 0 until latched; ManagedThreadId is never 0, so it doubles as the
        // "not latched yet" marker without the torn reads a static int? invites.
        private static int _mainThreadId;

        /// <summary>
        /// Record Unity's main thread before any game code can run.
        /// </summary>
        /// <remarks>
        /// Latching this from the first constructor instead records whichever thread happened to
        /// build the first loader — and ThreadSafeAssetLoader, the type advertised for background
        /// use, constructs one with no thread check at all. A worker thread latched there would
        /// make every genuine main-thread call throw and every post-await guard report a violation.
        /// The constructor keeps a fallback latch for edit-mode tooling, where this never runs.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThread()
        {
            System.Threading.Volatile.Write(ref _mainThreadId, System.Threading.Thread.CurrentThread.ManagedThreadId);
        }

        /// <summary>
        /// Create AssetLoader with optional scope name for monitoring. Tiering is off: the cache is
        /// unbounded, nothing is evicted, and no asset size is ever estimated — byte-for-byte the
        /// behaviour this constructor has always had. Use
        /// <see cref="AssetLoader(string, TieredCacheConfig)"/> to turn tiering on.
        /// </summary>
        /// <param name="scopeName">Scope name for Dashboard tracking (Editor-only)</param>
        public AssetLoader(string scopeName = "Unknown")
        {
            _scopeName = scopeName;
            InitCommon();
        }

        /// <summary>
        /// Create an AssetLoader whose cache is tiered (Hot/Warm/Cold) and size-bounded by
        /// <paramref name="tiering"/> — the merged replacement for
        /// <see cref="TieredAssetLoader"/>, which was a fork of this class that never gained
        /// single-flight join, the post-await thread guard, label/Safe/Instantiate loads,
        /// per-address release, or reachability from a CDN catalog invalidation.
        /// </summary>
        /// <remarks>
        /// WHY A CONSTRUCTOR AND NOT A SETTABLE PROPERTY
        ///
        /// A <c>Tiering</c> property would let a caller flip tiering on after 200 assets are already
        /// cached with no size, no creation time and no tier. The loader would then either evict
        /// against a byte total that omits everything loaded before the flip — HANDOFF_TO_SESSION_B.md
        /// L-4's failure mode, re-created — or back-fill sizes by walking every cached prefab at an
        /// arbitrary frame. Neither is acceptable and there is no third option, so tiering has to be
        /// settled before the first load. A constructor is the only place that is structurally true.
        ///
        /// <para><paramref name="tiering"/> deliberately has NO default value. The one-argument
        /// constructor above already exists, so a defaulted second parameter would make
        /// <c>new AssetLoader("X")</c> resolve by the "fewer omitted optional parameters" tie-break
        /// — a rule no shipped API should be betting on. A required parameter means a different
        /// arity and no ambiguity on any compiler.</para>
        /// </remarks>
        /// <param name="scopeName">Scope name for Dashboard tracking (Editor-only)</param>
        /// <param name="tiering">
        /// Tiered cache configuration. Validated here, so a bad config fails at construction rather
        /// than at the first eviction — the same contract <c>TieredCache&lt;T&gt;</c>'s constructor has.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="tiering"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="tiering"/> fails validation.</exception>
        public AssetLoader(string scopeName, TieredCacheConfig tiering)
        {
            if (tiering == null) throw new ArgumentNullException(nameof(tiering));

            if (!tiering.Validate(out var error))
            {
                throw new ArgumentException($"Invalid TieredCacheConfig: {error}", nameof(tiering));
            }

            _scopeName = scopeName;
            _tiering = tiering;
            InitCommon();
        }

        /// <summary>
        /// The construction work both constructors must do, in the one place neither can skip.
        /// </summary>
        /// <remarks>
        /// <c>_scopeName</c> and <c>_tiering</c> are assigned by each constructor rather than passed
        /// through here because both are <c>readonly</c>, and C# only allows a readonly field to be
        /// written from a constructor of its own type. Keeping them readonly is worth the two lines:
        /// <c>_tiering</c> being immutable after construction is the property the remarks above turn
        /// on.
        /// </remarks>
        private void InitCommon()
        {
            System.Threading.Interlocked.CompareExchange(
                ref _mainThreadId, System.Threading.Thread.CurrentThread.ManagedThreadId, 0);

            // Registered here rather than at each of the six construction sites, because that is the
            // one place none of them can skip. A catalog update has to reach every live loader; five
            // of the six populations are otherwise unreachable. The registry holds a weak reference,
            // so this does not keep an abandoned loader alive.
            //
            // A tiering-configured loader IS an AssetLoader, so it registers here exactly like every
            // other one. That is what closes HANDOFF_TO_SESSION_B.md §8.4's "seventh source": a
            // TieredAssetLoader registered with a different registry and had no InvalidateAddresses
            // at all, so a CDN catalog update could never reach anything it had cached.
            AssetLoaderRegistry.Register(this);
        }

        /// <summary>
        /// Whether this loader's cache is tiered — i.e. whether it was built with the
        /// <see cref="AssetLoader(string, TieredCacheConfig)"/> constructor. Every tiering member
        /// below is a no-op when this is false.
        /// </summary>
        public bool TieringEnabled => _tiering != null;

        /// <summary>
        /// Whether the caller is on Unity's main thread
        /// </summary>
        private static bool IsMainThread
        {
            get
            {
                int latched = System.Threading.Volatile.Read(ref _mainThreadId);
                return latched == 0 || System.Threading.Thread.CurrentThread.ManagedThreadId == latched;
            }
        }

        /// <summary>
        /// Check if current thread is Unity's main thread
        /// Throws exception if called from background thread
        /// </summary>
        private void AssertMainThread()
        {
            if (!IsMainThread)
            {
                throw new InvalidOperationException(ThreadViolationMessage());
            }
        }

        /// <summary>
        /// The one diagnostic text for a thread violation, shared by the throwing check and the
        /// post-await guard, which cannot throw.
        /// </summary>
        private string ThreadViolationMessage()
        {
            int latched = System.Threading.Volatile.Read(ref _mainThreadId);

            return
                $"[AssetLoader] Thread safety violation detected!\n\n" +
                $"AssetLoader must be called from Unity's main thread only.\n" +
                $"Current thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}\n" +
                $"Expected thread ID: {(latched == 0 ? "unknown" : latched.ToString())}\n\n" +
                $"SOLUTION: Use ThreadSafeAssetLoader instead:\n" +
                $"  var loader = new ThreadSafeAssetLoader(\"{_scopeName}\");\n" +
                $"  var handle = await loader.LoadAssetAsync<T>(address);\n\n" +
                $"Or dispatch to main thread manually:\n" +
                $"  UnityMainThreadDispatcher.Enqueue(() => /* your code */);\n";
        }

        #region Ownership Helpers

        /// <summary>
        /// Why a resumption point refused to carry on
        /// </summary>
        private enum AwaitGuard
        {
            Ok,
            LoaderDisposed,
            WrongThread
        }

        /// <summary>
        /// Guard applied after every await.
        /// </summary>
        /// <remarks>
        /// Two things can be true after an await that were not true before it: the loader may have
        /// been disposed while the operation ran (owner GameObject destroyed), and a continuation
        /// is not guaranteed to resume on the thread that started it.
        ///
        /// It reports instead of throwing. Every await in this class sits inside a
        /// <c>catch (Exception)</c>, so a throwing guard gets swallowed and re-reported as an
        /// ordinary load failure — burying exactly the diagnosis the thread check exists to give.
        /// A returned code makes each call site name the real cause.
        /// </remarks>
        private AwaitGuard AfterAwait()
        {
            // Thread first: off the main thread nothing else this could report is actionable, and
            // reading loader state is not synchronised anyway.
            if (!IsMainThread) return AwaitGuard.WrongThread;

            return _disposed ? AwaitGuard.LoaderDisposed : AwaitGuard.Ok;
        }

        /// <summary>
        /// Same guard for a resumption point that owns an Addressables operation. Both failure
        /// modes hand the operation back — nothing downstream will wrap it, and nobody else knows
        /// it exists — but only one of them may do so inline.
        /// </summary>
        private AwaitGuard AfterAwait(AsyncOperationHandle operation)
        {
            var guard = AfterAwait();

            switch (guard)
            {
                case AwaitGuard.Ok:
                    return guard;

                case AwaitGuard.WrongThread:
                    // WrongThread means the continuation resumed off the main thread, and
                    // ResourceManager is main-thread-only: releasing here walks non-thread-safe
                    // caches and can reach Object.Destroy / AssetBundle.Unload. It would either
                    // corrupt that bookkeeping or throw into the call site's catch, which reports
                    // "exception loading asset" and buries the thread diagnosis this guard exists
                    // to give.
                    ReleaseOnMainThread(operation);
                    return guard;

                default:
                    // LoaderDisposed: AfterAwait() tested the thread first, so we are on the main
                    // thread and can hand the operation back directly.
                    if (operation.IsValid()) Addressables.Release(operation);
                    return guard;
            }
        }

        /// <summary>
        /// Hand an operation back from a thread that must not touch Addressables itself.
        /// </summary>
        private static void ReleaseOnMainThread(AsyncOperationHandle operation)
        {
            try
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (operation.IsValid()) Addressables.Release(operation);
                });
            }
            catch (Exception ex)
            {
                // Enqueue creates the dispatcher GameObject on first use, which itself needs the
                // main thread. If that fails there is no safe way to give the operation back from
                // here: leak it and say so. A leaked bundle is recoverable, a ResourceManager
                // mutated from a worker thread is not.
                Debug.LogError(
                    "[AssetLoader] Could not hand a completed operation back to the main thread; " +
                    $"it will stay loaded until the next catalog reload. {ex.Message}");
            }
        }

        /// <summary>
        /// Report a guard failure on the return-null API surface
        /// </summary>
        private void LogGuardFailure(AwaitGuard guard, string key)
        {
            if (guard == AwaitGuard.WrongThread)
            {
                // Never folded into a "failed to load" message: the load itself was fine and the
                // fix is in the caller's threading, not in the address.
                Debug.LogError(ThreadViolationMessage());
                return;
            }

            LogVerbose($"[AssetLoader] Loader was disposed while loading: {key}");
        }

        /// <summary>
        /// Describe a guard failure for the Result-returning API surface
        /// </summary>
        private LoadError DescribeGuardFailure(AwaitGuard guard, string key)
        {
            if (guard == AwaitGuard.WrongThread)
            {
                return new LoadError(
                    LoadErrorCode.ThreadSafetyViolation,
                    ThreadViolationMessage(),
                    "The operation finished but resumed on another thread. Use ThreadSafeAssetLoader, " +
                    "or await from the main thread.",
                    key);
            }

            return new LoadError(
                LoadErrorCode.LoaderDisposed,
                "Loader was disposed while the operation was running",
                "Keep the owning scope alive until the load completes",
                key);
        }

        /// <summary>
        /// Hand out a reference from the cache. Null on a miss, and on a stale entry — which is
        /// purged from both collections so the caller falls through to a real load.
        /// </summary>
        private IAssetHandle<T> TryRetainCached<T>(AssetCacheKey key)
        {
            // Counted before the lookup, so a complete miss counts as an access — TieredCache.TryGet
            // does the same (TieredCache.cs:269) and a hit rate that cannot see misses is not a hit
            // rate. Joins via TryJoinInFlight are deliberately NOT counted: there is no entry to
            // record against at join time, and inventing one would be a second book.
            if (_tiering != null) _tierAccesses++;

            if (!_assetCache.TryGetValue(key, out var entry)) return null;

            var cached = entry.Handle;

            // TryRetain is the atomic form of "test IsValid, then Retain()": no window in which
            // another owner can drop the last reference between the test and the increment.
            if (cached is IAssetHandle<T> typed && cached.TryRetain())
            {
                if (_tiering != null)
                {
                    entry.RecordAccess();
                    _tierHits++;
                    MaybeEvaluateTiers();
                }

                return typed;
            }

            // Stale. The handle is provably dead here — the only writer of _assetCache is
            // CacheHandle<T>, keyed by (address, typeof(T)), so the type test above cannot fail for
            // a live entry and TryRetain only refuses at count zero. RemoveCacheEntry's Dispose() is
            // therefore the documented no-op at count 0 (AssetReferenceCounter.Release), and its
            // IsAlive test drops the handle from _activeHandles exactly as this path always did.
            CarryPinAcrossStaleEntry(entry);
            RemoveCacheEntry(key, hard: false);
            return null;
        }

        /// <summary>
        /// The ONLY place <c>_assetCache</c> loses a single entry.
        /// </summary>
        /// <remarks>
        /// <c>_tieredBytes</c> is adjusted here and nowhere else on the removal side, so the byte
        /// total cannot drift from the dictionary's contents — there is no second place that could
        /// forget. That is the whole point: HANDOFF_TO_SESSION_B.md L-4 exists because byte
        /// accounting lived in N places and the ceiling lived in one, and a promise that every
        /// mutation site does the right thing is a to-do list, not a fix.
        ///
        /// <para>The <c>_activeHandles</c> bookkeeping at the end is the same test
        /// <c>InvalidateAddress</c> and <c>CacheHandle</c> each used to do inline — folded in here so
        /// it cannot be omitted at a seventh site later. A handle that survives the decrement stays
        /// in the teardown ledger; one that does not is dropped from it.</para>
        /// </remarks>
        /// <param name="key">The entry to drop. A key with no entry is a no-op.</param>
        /// <param name="hard">
        /// <c>true</c> — teardown/eviction-API force release, killing the asset under any other
        /// holder. <c>false</c> — give back only the cache's own reference (the refcount contract's
        /// default). Eviction always passes <c>false</c>: it must never free an asset a live holder
        /// is still using.
        /// </param>
        /// <param name="exempt">
        /// A handle that must not be released even though its entry is being dropped, for the case
        /// where the very same handle object is about to be re-inserted under this key. Its bytes
        /// are still given back, so the accounting stays exact.
        /// </param>
        private void RemoveCacheEntry(AssetCacheKey key, bool hard, IOwnedHandle exempt = null)
        {
            if (!_assetCache.TryGetValue(key, out var entry)) return;

            _assetCache.Remove(key);

            _tieredBytes -= entry.EstimatedBytes;
            if (_tieredBytes < 0) _tieredBytes = 0;   // clamp, matching CacheBudget.Give

            var handle = entry.Handle;
            if (handle == null || ReferenceEquals(handle, exempt)) return;

            if (hard) handle.ForceRelease(); else handle.Dispose();

            // Dispose() only decremented; a handle another owner still retains must stay reachable
            // for teardown even though it just left _assetCache, or its Addressables operation would
            // never be released at all.
            if (!handle.IsAlive) _activeHandles.Remove(handle);
        }

        /// <summary>
        /// Take the cache's own reference on a freshly loaded handle and start tracking it. The
        /// caller keeps the reference the handle was born with; the cache holds a second one,
        /// dropped by ReleaseAsset(), ClearCache() or teardown.
        /// </summary>
        private void CacheHandle<T>(AssetCacheKey key, AssetHandle<T> handle, int loadEpoch)
        {
            // The load started before an invalidation and finished after it, so this handle resolved
            // against a catalog that is no longer installed. Hand it to the caller that asked for it -
            // it is a perfectly valid handle onto the old bundle, and failing the call would be worse -
            // but do NOT let it into the cache, or every subsequent caller gets the pre-update asset
            // from a cache that was invalidated to prevent exactly that.
            //
            // No Retain(): the cache is not taking a reference, so the handle keeps only its birth
            // reference and dies when the caller disposes it. TrackHandle still runs, so teardown can
            // reach it either way.
            if (loadEpoch != _invalidationEpoch)
            {
                LogVerbose($"[AssetLoader] '{key}' finished across a cache invalidation; returning it to the caller without caching so the next load re-resolves against the current catalog.");
                TrackHandle(handle);
                return;
            }

            // An entry can only still sit here if it went stale between the miss and now. Drop the
            // cache's reference on it rather than leaking it behind the new one — and give its bytes
            // back, which is why this routes through RemoveCacheEntry rather than releasing inline.
            // `exempt` covers re-caching the very same handle object: releasing it would give back
            // the reference the Retain() below is about to re-take.
            RemoveCacheEntry(key, hard: false, exempt: handle);

            handle.Retain();

            // Everything between the Retain() above and the two registrations below runs inside a
            // try/finally that guarantees the handle ends up reachable. The cache's reference has
            // already been taken at this point; if anything in between threw, the handle would be in
            // NEITHER _assetCache NOR _activeHandles — unreachable from ClearCache, TearDownAll,
            // Dispose and InvalidateAll — while LoadAssetAsync's catch returned null, so the caller
            // never learns a handle exists. That is a permanent, silent leak of one Addressables
            // operation. At HEAD nothing sat in this window; the tiering merge inserted
            // EstimateAssetSize (which walks a prefab's renderers and materials) into it.
            bool registered = false;
            long bytes = 0L;
            try
            {
                // The _tiering guard on the estimator is load-bearing, not a micro-optimisation:
                // EstimateGameObjectSize walks GetComponentsInChildren<MeshFilter>/<SkinnedMeshRenderer>/
                // <Renderer> and then every texture-typed shader property of every material. Running
                // that on every prefab load in the default, untiered configuration would be a large,
                // silent, unrequested per-load cost across the whole package.
                bytes = _tiering != null ? EstimateAssetSize(handle.Asset) : 0L;

                var entry = new CachedAsset(key, handle, bytes);

                // A pin placed before this key was ever cached applies now, on arrival (C-9). BEFORE the
                // eviction check below: a freshly pinned entry must already be pinned by the time
                // PerformEviction builds its candidate list, or the very entry the caller asked to
                // protect is a candidate in the pass its own insert triggered.
                if (_tiering != null && _pendingPins.Remove(key))
                {
                    entry.IsPinned = true;
                    entry.Tier = CacheTier.Hot;

                    if (_tiering.LogTierOperations)
                    {
                        Debug.Log($"[AssetLoader] Applied a pending pin to '{key}' as it entered the cache.");
                    }
                }

                _assetCache[key] = entry;
                _tieredBytes += bytes;
                registered = true;
            }
            finally
            {
                // TrackHandle unconditionally, even on the failure path: the teardown ledger is the
                // backstop that keeps a handle releasable when the cache did not take it.
                TrackHandle(handle);

                if (!registered)
                {
                    Debug.LogError(
                        $"[AssetLoader] Failed to register '{key}' in the cache after taking a " +
                        "reference on it. The handle is tracked for teardown so it is not leaked, " +
                        "but it is not cached — the next load of this key will start a new operation.");
                }
            }

            if (_tiering != null && _tiering.EnableAutoEviction && _tiering.MaxCacheSizeBytes > 0)
            {
                WarnIfLargerThanEvictionTarget(key, bytes);

                if ((float)_tieredBytes / _tiering.MaxCacheSizeBytes >= _tiering.EvictionTriggerRatio)
                {
                    PerformEviction();
                }
            }
        }

        /// <summary>
        /// Add a handle to the teardown ledger, compacting dead entries as it grows.
        /// </summary>
        /// <remarks>
        /// Nothing prunes an entry the moment its last reference goes: a handle has no callback
        /// into the loader, and a callback would fire on whichever thread happened to drop that
        /// reference — not necessarily this one — while these collections are plain
        /// non-concurrent ones. Compacting here keeps the ledger bounded on the main thread.
        /// </remarks>
        private void TrackHandle(IOwnedHandle handle)
        {
            _activeHandles.Add(handle);

            if (++_sinceCompaction < CompactionInterval) return;

            Compact();
        }

        /// <summary>
        /// Drop ledger entries that no longer refer to anything: handles whose last reference has
        /// gone, and instances the game destroyed behind our back.
        /// </summary>
        private void Compact()
        {
            _sinceCompaction = 0;
            _activeHandles.RemoveAll(h => h == null || !h.IsAlive);
            _instances.RemoveAll(instance => instance == null);
        }

        /// <summary>
        /// Register an in-flight load. Must be called synchronously, before the first await, so a
        /// concurrent caller cannot slip through the gap between "cache miss" and "cache insert"
        /// and start a second load whose wrapper nobody would ever release.
        /// </summary>
        private TaskCompletionSource<IOwnedHandle> NewInFlight(AssetCacheKey key)
        {
            // RunContinuationsAsynchronously: joiners must not resume inline inside
            // CompleteInFlight, where they would re-register the key while it is being removed.
            var pending = new TaskCompletionSource<IOwnedHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlightLoads[key] = pending;
            return pending;
        }

        /// <summary>
        /// Publish the result to everyone waiting on this load, then de-register it. A failed load
        /// publishes null and leaves no entry behind, so the next call retries instead of replaying
        /// a cached failure.
        /// </summary>
        private void CompleteInFlight(AssetCacheKey key, TaskCompletionSource<IOwnedHandle> pending, IOwnedHandle result)
        {
            // De-register before publishing, and only our own registration — a later caller may
            // already have replaced it. Skipped off the main thread rather than racing the readers
            // of a plain Dictionary; the leftover entry is completed, so a joiner drops it.
            if (IsMainThread &&
                _inFlightLoads.TryGetValue(key, out var registered) &&
                ReferenceEquals(registered, pending))
            {
                _inFlightLoads.Remove(key);
            }

            pending.TrySetResult(result);
        }

        /// <summary>
        /// Drop a registration whose load is finished and produced nothing this caller can use.
        /// Without it the join loop could spin on an entry that its own load could not clean up.
        /// Main thread only — every call site sits behind a passed <see cref="AfterAwait()"/>.
        /// </summary>
        private void DropCompletedInFlight(AssetCacheKey key, TaskCompletionSource<IOwnedHandle> pending)
        {
            if (!pending.Task.IsCompleted) return;

            if (_inFlightLoads.TryGetValue(key, out var registered) && ReferenceEquals(registered, pending))
            {
                _inFlightLoads.Remove(key);
            }
        }

        /// <summary>
        /// Wait for a load already running for this key and claim a reference from it. Returns a
        /// null handle with <see cref="AwaitGuard.Ok"/> when there is nothing to join, meaning the
        /// caller should start its own load.
        /// </summary>
        /// <remarks>
        /// One copy for all four load entry points: they differ only in what they report and what
        /// they return, and a divergence between four hand-written copies of a refcount claim is
        /// the kind of bug that only shows up under load. It is also the single place a
        /// CancellationToken has to land when the CDN layer's tokens are threaded through — the
        /// wait below is the only unbounded one in the class.
        ///
        /// A loop, not an `if`: a failed join awaits, and during that await another caller can
        /// register a fresh load for this key. Falling straight through would overwrite their
        /// registration and start a duplicate load.
        ///
        /// It never throws — the awaited task is only ever completed with a result — so a call
        /// site outside its own try block cannot turn a Result-returning API into a throwing one.
        /// </remarks>
        private async Task<(IAssetHandle<T> handle, AwaitGuard guard)> TryJoinInFlight<T>(AssetCacheKey key)
        {
            while (_inFlightLoads.TryGetValue(key, out var pending))
            {
                var shared = await pending.Task;

                var guard = AfterAwait();
                if (guard != AwaitGuard.Ok) return (null, guard);

                // Atomic claim — the caller that started that load may already have released it.
                if (shared is IAssetHandle<T> joined && shared.TryRetain()) return (joined, AwaitGuard.Ok);

                DropCompletedInFlight(key, pending);
            }

            return (null, AwaitGuard.Ok);
        }

        #endregion

        #region Load by Address

        /// <summary>
        /// Load asset asynchronously by address.
        /// Returns <see cref="UniTask{TResult}"/> when <c>com.cysharp.unitask</c> is installed,
        /// otherwise <see cref="Task{TResult}"/>. Automatically monitored in Editor for Dashboard tracking.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(address))
            {
                Debug.LogError("[AssetLoader] Address cannot be null or empty");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            // Create cache key with type to allow different types for same address
            var cacheKey = new AssetCacheKey(address, typeof(T));

            // Check cache first
            var cached = TryRetainCached<T>(cacheKey);
            if (cached != null)
            {
                LogVerbose($"[AssetLoader] Cache hit for: {address}");

#if UNITY_EDITOR
                // Report cache hit to monitoring
                var loadDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    loadDuration,
                    true // from cache
                );
#endif

                return cached;
            }

            // Someone registered this key before their first await — join their operation instead
            // of starting a second one whose wrapper nobody would ever release.
            var (joined, joinGuard) = await TryJoinInFlight<T>(cacheKey);
            if (joinGuard != AwaitGuard.Ok)
            {
                LogGuardFailure(joinGuard, address);
                return null;
            }

            if (joined != null)
            {
                LogVerbose($"[AssetLoader] Joined in-flight load for: {address}");

#if UNITY_EDITOR
                var joinDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    joinDuration,
                    true // served by an in-flight load
                );
#endif

                return joined;
            }

            var loadEpoch = _invalidationEpoch;
            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            try
            {
                LogVerbose($"[AssetLoader] Loading asset: {address}");
                var operation = Addressables.LoadAssetAsync<T>(address);

                // See _inFlightProgress's field comment. operation is a struct that wraps a
                // reference to the shared, mutable Addressables operation state, so reading
                // .PercentComplete through this closure after progress advances reflects the same
                // live value Addressables itself would report — this does not snapshot anything.
                _inFlightProgress[cacheKey] = () => operation.PercentComplete;

                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // Editor-only overload carries address/typeName so a later Release() can
                    // report AssetMonitorBridge.ReportAssetReleased — the producer this loader is
                    // the only place that has both the context and the refcount-zero hook
                    // (HANDOFF_TO_SESSION_B.md E-CHAIN item 2). Kept out of shipping builds to
                    // match MONITORING_GUIDE.md's zero-overhead guarantee.
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif

                    // Cache the handle
                    CacheHandle(cacheKey, handle, loadEpoch);
                    loaded = handle;

                    LogVerbose($"[AssetLoader] Successfully loaded: {address}");

#if UNITY_EDITOR
                    // Report successful load to monitoring
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

                Debug.LogError($"[AssetLoader] Failed to load asset: {address}. Error: {operation.OperationException}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading asset: {address}. Error: {ex.Message}");
                return null;
            }
            finally
            {
                // Same main-thread guard CompleteInFlight applies to _inFlightLoads one line below,
                // and for the same reason: _inFlightProgress is a plain Dictionary, written on the
                // main thread at the top of this method and read from GetLoadProgress, so removing
                // from it off-thread is an unsynchronised mutation racing those readers. This runs
                // off the main thread exactly when AfterAwait's thread re-check fires — the case the
                // guard in CompleteInFlight was written for. A leftover entry is harmless: its
                // closure reads the finished operation's PercentComplete, which is 1.
                if (IsMainThread)
                {
                    _inFlightProgress.Remove(cacheKey);
                }

                CompleteInFlight(cacheKey, inFlight, loaded);
            }
        }

        /// <summary>
        /// Best-effort progress (0-1) for a load of (<paramref name="address"/>,
        /// <typeparamref name="T"/>) currently in flight through this loader's single-flight map.
        /// </summary>
        /// <remarks>
        /// The seam <see cref="AddressableManager.Progress.ProgressiveAssetLoader.LoadAssetWithProgressAsync{T}"/>
        /// polls instead of opening a second <c>Addressables.LoadAssetAsync</c> call purely to read
        /// <c>PercentComplete</c> — this loader owns single-flight now, so a second call for the
        /// same key either duplicates the load or, worse, silently joins this one with no operation
        /// of its own left to poll (HANDOFF_TO_SESSION_B.md L-2). Returns 1 when nothing is in
        /// flight for this key: a cache hit, a load that never started, or one that already
        /// finished — there is nothing left to wait for in any of those three cases, and a caller
        /// polling in a <c>while (progress &lt; 1)</c> loop should not spin on a key that will never
        /// change again.
        /// </remarks>
        internal float GetLoadProgress<T>(string address)
        {
            if (string.IsNullOrEmpty(address)) return 1f;

            var key = new AssetCacheKey(address, typeof(T));
            return _inFlightProgress.TryGetValue(key, out var read) ? read() : 1f;
        }

        #endregion

        #region Load by AssetReference

        /// <summary>
        /// Load asset by AssetReference.
        /// Automatically monitored in Editor for Dashboard tracking.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#else
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[AssetLoader] Invalid AssetReference");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var address = AssetReferenceCacheAddress(assetReference);
            var cacheKey = new AssetCacheKey(address, typeof(T));

            // Check cache
            var cached = TryRetainCached<T>(cacheKey);
            if (cached != null)
            {
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

                return cached;
            }

            var (joined, joinGuard) = await TryJoinInFlight<T>(cacheKey);
            if (joinGuard != AwaitGuard.Ok)
            {
                LogGuardFailure(joinGuard, address);
                return null;
            }

            if (joined != null)
            {
#if UNITY_EDITOR
                var joinDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    joinDuration,
                    true // served by an in-flight load
                );
#endif

                return joined;
            }

            var loadEpoch = _invalidationEpoch;
            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            try
            {
                // Addressables.LoadAssetAsync(RuntimeKey), NOT assetReference.LoadAssetAsync().
                //
                // AssetReference.LoadAssetAsync is single-use per AssetReference INSTANCE: it stores
                // the handle in m_Operation and, on any later call while that handle is still valid,
                // logs "Attempting to load AssetReference that has already been loaded" and returns
                // default(AsyncOperationHandle<T>). The await below then hits AsyncOperationHandle.Task,
                // whose InternalOp getter throws "Attempting to use an invalid operation handle" for a
                // default handle - so the second load of the same AssetReference field surfaced to the
                // caller as a plain null with an error in the console, even though the asset was fine
                // and already cached under a different key path.
                //
                // Unity's own remark on that method points at this overload for repeated loads. It is
                // what AssetReference.LoadAssetAsync calls internally (AssetReference.cs: result =
                // Addressables.LoadAssetAsync<TObject>(RuntimeKey)), so the first load is unchanged;
                // this package tracks handles itself and never reads AssetReference.OperationHandle or
                // calls AssetReference.ReleaseAsset, so nothing depended on that stored handle.
                var operation = Addressables.LoadAssetAsync<T>(assetReference.RuntimeKey);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // See LoadAssetAsync(string) above for why this overload is Editor-only.
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif

                    CacheHandle(cacheKey, handle, loadEpoch);
                    loaded = handle;

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

                Debug.LogError($"[AssetLoader] Failed to load AssetReference. Error: {operation.OperationException}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading AssetReference: {ex.Message}");
                return null;
            }
            finally
            {
                CompleteInFlight(cacheKey, inFlight, loaded);
            }
        }

        #endregion

        #region Load Multiple by Label

        /// <summary>
        /// Load multiple assets by label.
        /// Automatically monitored in Editor for Dashboard tracking.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#else
        public async Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(label))
            {
                Debug.LogError("[AssetLoader] Label cannot be null or empty");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            try
            {
                LogVerbose($"[AssetLoader] Loading assets with label: {label}");
                var operation = Addressables.LoadAssetsAsync<T>(label, null);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, label);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handles = new List<IAssetHandle<T>>();

                    // Create a shared tracker for the list operation. Start with refcount 1 so the
                    // tracker itself keeps the underlying handle alive; we release it once after the
                    // foreach so that the surviving refcount is equal to the number of ListItemHandles.
                    var sharedTracker = new SharedListOperationTracker<T>(operation);
                    TrackHandle(sharedTracker);

                    foreach (var asset in operation.Result)
                    {
                        var wrapper = new ListItemHandle<T>(sharedTracker, asset);
                        handles.Add(wrapper);
                        TrackHandle(wrapper);
                    }

                    // Drop our extra reference; if results were empty the tracker disposes immediately
                    // and releases the underlying Addressables handle.
                    sharedTracker.Release();

                    LogVerbose($"[AssetLoader] Loaded {handles.Count} assets with label: {label}");

#if UNITY_EDITOR
                    // Report each asset loaded
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    var avgTimePerAsset = handles.Count > 0 ? loadDuration / handles.Count : loadDuration;

                    foreach (var _ in handles)
                    {
                        AssetMonitorBridge.ReportAssetLoaded(
                            $"{label}/*",
                            typeof(T).Name,
                            _scopeName,
                            avgTimePerAsset,
                            false // not from cache
                        );
                    }
#endif

                    return handles;
                }

                Debug.LogError($"[AssetLoader] Failed to load assets by label: {label}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading by label: {label}. Error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Safe Load Methods (Result Pattern)

        /// <summary>
        /// Load asset asynchronously with explicit error handling (Result pattern)
        /// Returns LoadResult with detailed error information on failure
        ///
        /// Usage:
        ///   var result = await loader.LoadAssetAsyncSafe<Sprite>("UI/Icon");
        ///   if (result.IsSuccess)
        ///   {
        ///       using var handle = result.Value;
        ///       // Use handle...
        ///   }
        ///   else
        ///   {
        ///       Debug.LogError(result.Error);
        ///   }
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<LoadResult<IAssetHandle<T>>> LoadAssetAsyncSafe<T>(string address)
#else
        public async Task<LoadResult<IAssetHandle<T>>> LoadAssetAsyncSafe<T>(string address)
#endif
        {
            // Check if disposed
            if (_disposed)
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.LoaderDisposed,
                    "Cannot load from disposed loader",
                    "Create a new loader instance or use an active scope's loader",
                    address
                );
            }

            // Validate address
            if (string.IsNullOrEmpty(address))
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.InvalidAddress,
                    "Address cannot be null or empty",
                    null,
                    address
                );
            }

            // Check thread safety
            try
            {
                AssertMainThread();
            }
            catch (InvalidOperationException ex)
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.ThreadSafetyViolation,
                    ex.Message,
                    "Use ThreadSafeAssetLoader for background thread loading",
                    address,
                    ex
                );
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            // Create cache key
            var cacheKey = new AssetCacheKey(address, typeof(T));

            // Check cache first
            var cached = TryRetainCached<T>(cacheKey);
            if (cached != null)
            {
                LogVerbose($"[AssetLoader] Cache hit for: {address}");

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

                return LoadResult<IAssetHandle<T>>.Success(cached);
            }

            // Safe to sit outside the try below: TryJoinInFlight never throws, so this cannot turn
            // a Result-returning API into a throwing one.
            var (joined, joinGuard) = await TryJoinInFlight<T>(cacheKey);
            if (joinGuard != AwaitGuard.Ok)
            {
                return LoadResult<IAssetHandle<T>>.Failure(DescribeGuardFailure(joinGuard, address));
            }

            if (joined != null)
            {
#if UNITY_EDITOR
                var joinDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    joinDuration,
                    true // served by an in-flight load
                );
#endif

                return LoadResult<IAssetHandle<T>>.Success(joined);
            }

            var loadEpoch = _invalidationEpoch;
            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            // Perform actual load
            try
            {
                LogVerbose($"[AssetLoader] Loading asset: {address}");
                var operation = Addressables.LoadAssetAsync<T>(address);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    return LoadResult<IAssetHandle<T>>.Failure(DescribeGuardFailure(guard, address));
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // See LoadAssetAsync(string) above for why this overload is Editor-only.
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif

                    // Cache the handle
                    CacheHandle(cacheKey, handle, loadEpoch);
                    loaded = handle;

                    LogVerbose($"[AssetLoader] Successfully loaded: {address}");

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

                    return LoadResult<IAssetHandle<T>>.Success(handle);
                }
                else
                {
                    // Operation failed - determine error code
                    var errorCode = DetermineErrorCode(operation);
                    var errorMsg = operation.OperationException?.Message ?? "Load operation failed";

                    // The failure result carries no handle, so nothing downstream can release the
                    // operation. The non-Safe twin does this too; leaving it out here leaked the
                    // failed operation on every unhappy path.
                    if (operation.IsValid()) Addressables.Release(operation);

                    return LoadResult<IAssetHandle<T>>.Failure(
                        errorCode,
                        $"Failed to load asset: {address}. {errorMsg}",
                        null,
                        address,
                        operation.OperationException
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading asset: {address}. Error: {ex.Message}");

                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.OperationFailed,
                    $"Exception loading asset: {address}",
                    null,
                    address,
                    ex
                );
            }
            finally
            {
                CompleteInFlight(cacheKey, inFlight, loaded);
            }
        }

        /// <summary>
        /// Load asset by AssetReference with explicit error handling (Result pattern)
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<LoadResult<IAssetHandle<T>>> LoadAssetAsyncSafe<T>(AssetReference assetReference)
#else
        public async Task<LoadResult<IAssetHandle<T>>> LoadAssetAsyncSafe<T>(AssetReference assetReference)
#endif
        {
            // Check if disposed
            if (_disposed)
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.LoaderDisposed,
                    "Cannot load from disposed loader",
                    "Create a new loader instance or use an active scope's loader",
                    assetReference?.AssetGUID
                );
            }

            // Validate AssetReference
            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.InvalidAssetReference,
                    "Invalid AssetReference - null or runtime key not valid",
                    null,
                    assetReference?.AssetGUID
                );
            }

            // Check thread safety
            try
            {
                AssertMainThread();
            }
            catch (InvalidOperationException ex)
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.ThreadSafetyViolation,
                    ex.Message,
                    "Use ThreadSafeAssetLoader for background thread loading",
                    assetReference.AssetGUID,
                    ex
                );
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var address = AssetReferenceCacheAddress(assetReference);
            var cacheKey = new AssetCacheKey(address, typeof(T));

            // Check cache
            var cached = TryRetainCached<T>(cacheKey);
            if (cached != null)
            {
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

                return LoadResult<IAssetHandle<T>>.Success(cached);
            }

            var (joined, joinGuard) = await TryJoinInFlight<T>(cacheKey);
            if (joinGuard != AwaitGuard.Ok)
            {
                return LoadResult<IAssetHandle<T>>.Failure(DescribeGuardFailure(joinGuard, address));
            }

            if (joined != null)
            {
#if UNITY_EDITOR
                var joinDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    joinDuration,
                    true // served by an in-flight load
                );
#endif

                return LoadResult<IAssetHandle<T>>.Success(joined);
            }

            var loadEpoch = _invalidationEpoch;
            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            // Perform actual load
            try
            {
                // Addressables.LoadAssetAsync(RuntimeKey), NOT assetReference.LoadAssetAsync().
                //
                // AssetReference.LoadAssetAsync is single-use per AssetReference INSTANCE: it stores
                // the handle in m_Operation and, on any later call while that handle is still valid,
                // logs "Attempting to load AssetReference that has already been loaded" and returns
                // default(AsyncOperationHandle<T>). The await below then hits AsyncOperationHandle.Task,
                // whose InternalOp getter throws "Attempting to use an invalid operation handle" for a
                // default handle - so the second load of the same AssetReference field surfaced to the
                // caller as a plain null with an error in the console, even though the asset was fine
                // and already cached under a different key path.
                //
                // Unity's own remark on that method points at this overload for repeated loads. It is
                // what AssetReference.LoadAssetAsync calls internally (AssetReference.cs: result =
                // Addressables.LoadAssetAsync<TObject>(RuntimeKey)), so the first load is unchanged;
                // this package tracks handles itself and never reads AssetReference.OperationHandle or
                // calls AssetReference.ReleaseAsset, so nothing depended on that stored handle.
                var operation = Addressables.LoadAssetAsync<T>(assetReference.RuntimeKey);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    return LoadResult<IAssetHandle<T>>.Failure(DescribeGuardFailure(guard, address));
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // See LoadAssetAsync(string) above for why this overload is Editor-only.
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif

                    CacheHandle(cacheKey, handle, loadEpoch);
                    loaded = handle;

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

                    return LoadResult<IAssetHandle<T>>.Success(handle);
                }
                else
                {
                    var errorCode = DetermineErrorCode(operation);
                    var errorMsg = operation.OperationException?.Message ?? "Load operation failed";

                    // Nothing downstream can release a failure result's operation.
                    if (operation.IsValid()) Addressables.Release(operation);

                    return LoadResult<IAssetHandle<T>>.Failure(
                        errorCode,
                        $"Failed to load AssetReference. {errorMsg}",
                        null,
                        address,
                        operation.OperationException
                    );
                }
            }
            catch (Exception ex)
            {
                return LoadResult<IAssetHandle<T>>.Failure(
                    LoadErrorCode.OperationFailed,
                    $"Exception loading AssetReference",
                    null,
                    address,
                    ex
                );
            }
            finally
            {
                CompleteInFlight(cacheKey, inFlight, loaded);
            }
        }

        /// <summary>
        /// Load multiple assets by label with explicit error handling (Result pattern)
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<LoadResult<List<IAssetHandle<T>>>> LoadAssetsByLabelAsyncSafe<T>(string label)
#else
        public async Task<LoadResult<List<IAssetHandle<T>>>> LoadAssetsByLabelAsyncSafe<T>(string label)
#endif
        {
            // Check if disposed
            if (_disposed)
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.LoaderDisposed,
                    "Cannot load from disposed loader",
                    "Create a new loader instance or use an active scope's loader",
                    label
                );
            }

            // Validate label
            if (string.IsNullOrEmpty(label))
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.InvalidLabel,
                    "Label cannot be null or empty",
                    null,
                    label
                );
            }

            // Check thread safety
            try
            {
                AssertMainThread();
            }
            catch (InvalidOperationException ex)
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.ThreadSafetyViolation,
                    ex.Message,
                    "Use ThreadSafeAssetLoader for background thread loading",
                    label,
                    ex
                );
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            // Perform actual load
            try
            {
                LogVerbose($"[AssetLoader] Loading assets with label: {label}");
                var operation = Addressables.LoadAssetsAsync<T>(label, null);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    return LoadResult<List<IAssetHandle<T>>>.Failure(DescribeGuardFailure(guard, label));
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handles = new List<IAssetHandle<T>>();

                    // Create a shared tracker for the list operation
                    var sharedTracker = new SharedListOperationTracker<T>(operation);
                    TrackHandle(sharedTracker);

                    // For each loaded asset, create a wrapper handle
                    foreach (var asset in operation.Result)
                    {
                        var wrapper = new ListItemHandle<T>(sharedTracker, asset);
                        handles.Add(wrapper);
                        TrackHandle(wrapper);
                    }

                    // Drop our extra reference; if results were empty the tracker disposes
                    // immediately and releases the underlying Addressables handle. Without this the
                    // tracker never reaches 0 even when the caller releases every item correctly.
                    sharedTracker.Release();

                    LogVerbose($"[AssetLoader] Loaded {handles.Count} assets with label: {label}");

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    var avgTimePerAsset = handles.Count > 0 ? loadDuration / handles.Count : loadDuration;

                    foreach (var handle in handles)
                    {
                        AssetMonitorBridge.ReportAssetLoaded(
                            $"{label}/*",
                            typeof(T).Name,
                            _scopeName,
                            avgTimePerAsset,
                            false // not from cache
                        );
                    }
#endif

                    return LoadResult<List<IAssetHandle<T>>>.Success(handles);
                }
                else
                {
                    var errorCode = DetermineErrorCode(operation);
                    var errorMsg = operation.OperationException?.Message ?? "Load operation failed";

                    // Nothing downstream can release a failure result's operation.
                    if (operation.IsValid()) Addressables.Release(operation);

                    return LoadResult<List<IAssetHandle<T>>>.Failure(
                        errorCode,
                        $"Failed to load assets by label: {label}. {errorMsg}",
                        null,
                        label,
                        operation.OperationException
                    );
                }
            }
            catch (Exception ex)
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.OperationFailed,
                    $"Exception loading by label: {label}",
                    null,
                    label,
                    ex
                );
            }
        }

        /// <summary>
        /// Classify an exception message into a specific error code.
        /// This static method extracts the classification logic for testability.
        /// The substring matching is a known flaw that will be
        /// replaced with status-code based classification in Phase 3.
        /// </summary>
        public static LoadErrorCode ClassifyErrorMessage(string exceptionMessage)
        {
            if (exceptionMessage == null)
                return LoadErrorCode.OperationFailed;

            var exceptionMsg = exceptionMessage.ToLower();

            // Check for specific error patterns
            if (exceptionMsg.Contains("not found") || exceptionMsg.Contains("no location"))
                return LoadErrorCode.AssetNotFound;

            if (exceptionMsg.Contains("invalid key") || exceptionMsg.Contains("invalid address"))
                return LoadErrorCode.InvalidAddress;

            if (exceptionMsg.Contains("type") && exceptionMsg.Contains("mismatch"))
                return LoadErrorCode.TypeMismatch;

            if (exceptionMsg.Contains("network") || exceptionMsg.Contains("connection") || exceptionMsg.Contains("download"))
                return LoadErrorCode.NetworkError;

            return LoadErrorCode.OperationFailed;
        }

        /// <summary>
        /// Determine specific error code from operation exception
        /// </summary>
        private LoadErrorCode DetermineErrorCode<T>(AsyncOperationHandle<T> operation)
        {
            return ClassifyErrorMessage(operation.OperationException?.Message);
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiate a GameObject from addressable.
        /// The instance is tracked so teardown can release it; release it earlier with
        /// <see cref="ReleaseInstance"/>.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<GameObject> InstantiateAsync(string address, Transform parent = null)
#else
        public async Task<GameObject> InstantiateAsync(string address, Transform parent = null)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot instantiate from disposed loader");
                return null;
            }

            try
            {
                var operation = Addressables.InstantiateAsync(address, parent);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    TrackInstance(operation.Result);
                    return operation.Result;
                }

                Debug.LogError($"[AssetLoader] Failed to instantiate: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception instantiating: {address}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Instantiate with position and rotation
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#else
        public async Task<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot instantiate from disposed loader");
                return null;
            }

            try
            {
                var operation = Addressables.InstantiateAsync(address, position, rotation, parent);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    TrackInstance(operation.Result);
                    return operation.Result;
                }

                Debug.LogError($"[AssetLoader] Failed to instantiate: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception instantiating: {address}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Add an instance to the teardown ledger, dropping entries the game already destroyed.
        /// </summary>
        /// <remarks>
        /// Addressables gives no destruction callback, so an instance the game destroys with
        /// Object.Destroy leaves a fake-null entry here. Without this sweep the list only ever
        /// grows, for the whole lifetime of the loader.
        /// </remarks>
        private void TrackInstance(GameObject instance)
        {
            _instances.Add(instance);

            if (++_sinceCompaction < CompactionInterval) return;

            Compact();
        }

        /// <summary>
        /// Release instantiated GameObject
        /// </summary>
        public bool ReleaseInstance(GameObject instance)
        {
            // Null check first: releasing nothing is a legitimate no-op on any thread, and making
            // it throw would have been a new failure mode for callers that never had one.
            if (instance == null) return false;

            AssertMainThread();

            try
            {
                // Stop tracking first: ReleaseInstance destroys the GameObject, and a destroyed
                // instance no longer compares equal to the entry we stored.
                _instances.Remove(instance);
                return Addressables.ReleaseInstance(instance);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Error releasing instance: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Release every GameObject this loader instantiated. Instances that scene teardown already
        /// destroyed are skipped.
        /// </summary>
        private void ReleaseTrackedInstances()
        {
            foreach (var instance in _instances)
            {
                if (instance == null) continue;

                try
                {
                    Addressables.ReleaseInstance(instance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AssetLoader] Error releasing instance: {ex.Message}");
                }
            }

            _instances.Clear();
        }

        #endregion

        #region Preload & Download

        /// <summary>
        /// Preload/Download asset without loading it into memory.
        /// Returns true on success, false otherwise.
        /// </summary>
        // Task 3.10. Kept until 5.0.0 per repo invariant 6.
        //
        // Returns bool, so a caller cannot tell "the CDN was unreachable" from "the download
        // failed" from "there was nothing to download". CdnManager.DownloadAsync returns a
        // CdnResult carrying the error code, the HTTP status and whether retrying can help.
        [Obsolete("Use CdnManager.DownloadAsync(DownloadRequest) — it reports why a download failed " +
                  "and whether a retry can succeed, which a bool cannot. Removed in 5.0.0.", false)]
#if UNITASK_PRESENT
        public async UniTask<bool> DownloadDependenciesAsync(string address)
#else
        public async Task<bool> DownloadDependenciesAsync(string address)
#endif
        {
            AssertMainThread();

            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot download from disposed loader");
                return false;
            }

            try
            {
                var operation = Addressables.DownloadDependenciesAsync(address);
                await operation.Task;

                // The guard releases the operation itself, so the unconditional release below
                // still runs exactly once.
                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return false;
                }

                bool succeeded = operation.Status == AsyncOperationStatus.Succeeded;

                if (!succeeded)
                {
                    Debug.LogError($"[AssetLoader] Failed to download dependencies: {address}");
                }

                if (operation.IsValid())
                {
                    Addressables.Release(operation);
                }

                return succeeded;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception downloading dependencies: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get download size for address
        /// </summary>
        // Task 3.10. Kept until 5.0.0 per repo invariant 6.
        //
        // THIS IS THE BUG repo invariant 4 EXISTS FOR: it returns 0 both when everything is
        // already cached and when the size could not be determined at all. A caller that reads 0
        // as "nothing to download" will skip a download it needed to do.
        [Obsolete("Use CdnManager.GetDownloadSizeAsync(DownloadRequest), which returns CdnResult<long> " +
                  "so zero-bytes-to-download is distinguishable from could-not-find-out. Removed in 5.0.0.", false)]
#if UNITASK_PRESENT
        public async UniTask<long> GetDownloadSizeAsync(string address)
#else
        public async Task<long> GetDownloadSizeAsync(string address)
#endif
        {
            AssertMainThread();

            try
            {
                var operation = Addressables.GetDownloadSizeAsync(address);
                await operation.Task;

                var guard = AfterAwait(operation);
                if (guard != AwaitGuard.Ok)
                {
                    LogGuardFailure(guard, address);
                    return 0;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    long size = operation.Result;
                    Addressables.Release(operation);
                    return size;
                }

                Debug.LogError($"[AssetLoader] Failed to get download size: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception getting download size: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Evict everything this loader has cached, freeing the bundles behind it. Label loads and
        /// instantiated GameObjects are untouched — this is a memory-pressure API, not teardown.
        /// For teardown use <see cref="Dispose"/>.
        /// </summary>
        /// <remarks>
        /// The eviction is unconditional rather than a decrement of the cache's own reference — on
        /// purpose: this is a memory-pressure API, and a caller elsewhere still holding its own
        /// reference (a Standard.* caller sitting on its IAssetHandle, a SmartAssetHandle) is
        /// exactly the case a plain decrement would leave untouched, which would defeat the point
        /// of calling this under memory pressure.
        ///
        /// (Historical note: Simple.Load and the rest of the return-the-asset API family used to
        /// hand back <c>handle.Asset</c> without ever giving back the reference they were born
        /// with, so a count-respecting ClearCache would additionally have found every one of their
        /// entries still "held" and freed nothing at all — see HANDOFF_TO_SESSION_B.md A-4. That
        /// leak is fixed now, so it is no longer why this method forces the release, only a reason
        /// it used to be the *only* way those particular entries could ever come back.)
        ///
        /// A handle a caller is still holding across this reports IsValid == false afterwards
        /// (the counter is what IsValid reads), so the failure mode is a visibly dead handle, not
        /// a live-looking one pointing at a freed asset.
        /// </remarks>
        public void ClearCache()
        {
            AssertMainThread();

            // Same reasoning as InvalidateAddresses: a load in flight right now resolved against the
            // state the caller just asked to discard, so it must not land in the emptied cache.
            _invalidationEpoch++;

            LogVerbose($"[AssetLoader] Clearing cache ({_assetCache.Count} cached, {_activeHandles.Count} tracked)");

            // Snapshot, then release. ForceRelease reaches Addressables.Release, which can destroy
            // objects, which can run game code, which can re-enter this loader — the same chain
            // PerformEviction's _evicting guard exists for. Iterating _assetCache.Values live made
            // that a "Collection was modified" throw out of a teardown path, and the re-entrant
            // mutation is easy to reach from here: during this loop every already-released entry is
            // stale, so a re-entrant LoadAssetAsync hits TryRetainCached -> TryRetain() fails ->
            // RemoveCacheEntry(hard: false) -> _assetCache.Remove(), mutating the dictionary the
            // foreach is walking. Clearing the dictionary BEFORE releasing also means a re-entrant
            // caller sees an empty cache rather than entries that are already dead.
            var entries = new CachedAsset[_assetCache.Count];
            _assetCache.Values.CopyTo(entries, 0);

            _assetCache.Clear();
            _tieredBytes = 0;

            // Emptying the cache drops the tiering bookkeeping with it, matching
            // TieredCache.ForceReleaseAll (which TieredAssetLoader.ClearCache reaches through
            // Dispose): leaving armed pins behind would re-pin whatever happened to be loaded next
            // under those keys, long after the caller believed the cache was gone. Reset with the
            // rest of the state, before the releases below, so a re-entrant load lands in a
            // fully-emptied loader rather than one this call is halfway through clearing.
            ResetTieringState();

            foreach (var entry in entries)
            {
                entry.Handle?.ForceRelease();
            }

            // Registrations whose load already finished are a second cache: a joiner still claims a
            // reference from them. Leaving them would serve the assets this call just evicted.
            DropCompletedInFlight();

            // Prune what that killed. The ledger itself is not cleared: label loads live only
            // there, never in _assetCache, and clearing it outright is what used to put them
            // beyond the reach of Dispose().
            Compact();
        }

        /// <summary>
        /// Drop every in-flight registration whose load has already finished. Loads still running
        /// are left alone: their owner completes and de-registers them, and dropping one here would
        /// let a later caller start a duplicate load of the same key.
        /// </summary>
        private void DropCompletedInFlight()
        {
            List<AssetCacheKey> completed = null;

            foreach (var kvp in _inFlightLoads)
            {
                if (!kvp.Value.Task.IsCompleted) continue;

                completed ??= new List<AssetCacheKey>();
                completed.Add(kvp.Key);
            }

            if (completed == null) return;

            foreach (var key in completed)
            {
                _inFlightLoads.Remove(key);
            }
        }

        /// <summary>
        /// The cache address for an <see cref="AssetReference"/>: its <c>RuntimeKey</c>, not its
        /// <c>AssetGUID</c>.
        /// </summary>
        /// <remarks>
        /// <c>AssetGUID</c> identifies the containing ASSET; <c>RuntimeKey</c> identifies what will
        /// actually be loaded. For a sub-object reference — an <c>AssetReferenceSprite</c> pointing at
        /// one sprite inside a multi-sprite texture or atlas — every reference into the same texture
        /// shares one guid but carries a distinct <c>RuntimeKey</c> of the form
        /// <c>"{guid}[{subObjectName}]"</c> (UnityEngine.AddressableAssets.AssetReference.RuntimeKey).
        ///
        /// Keying the cache by guid therefore collapses every sub-object of one asset onto a single
        /// entry, and the second caller to ask for a different sprite silently receives the first
        /// one — same type, valid handle, no error anywhere. Keying by RuntimeKey keeps them apart.
        ///
        /// Callers that invalidate or release by bare guid/address still reach these entries via
        /// <see cref="AssetCacheKey.MatchesAddress"/>.
        /// </remarks>
        internal static string AssetReferenceCacheAddress(AssetReference assetReference)
        {
            if (assetReference == null) return null;

            // RuntimeKey is object-typed and is the bare guid string when no sub-object is set, so
            // this stays byte-identical to the old key for every non-sub-object reference.
            var runtimeKey = assetReference.RuntimeKey?.ToString();
            return string.IsNullOrEmpty(runtimeKey) ? assetReference.AssetGUID : runtimeKey;
        }

        /// <summary>
        /// Drop the cache's entries for a set of addresses — not whatever their reference count is,
        /// only the cache's own — and drop any finished registration for them so the next load goes
        /// back to Addressables.
        /// </summary>
        /// <remarks>
        /// The seam a catalog update needs: after CatalogService applies one, every handle this
        /// loader cached still wraps an operation resolved against the *previous* catalog, and
        /// nothing about a stale handle looks stale — the operation is valid and Succeeded, so
        /// TryRetainCached keeps serving it forever. CdnManager calls this with the changed keys
        /// ApplyUpdateAsync returns — except UpdateCatalogs never says which address actually
        /// changed, so in practice that is every key in the new locator. A hard release on that
        /// scale would free bundles out from under whatever is still live mid live-op, so this only
        /// gives back the cache's own reference; a caller still holding one keeps its asset valid on
        /// the old bundle until it releases on its own.
        /// </remarks>
        internal void InvalidateAddresses(IEnumerable<string> addresses)
        {
            // Bumped before the sweep, not after: a load that completes between the bump and the last
            // key being removed must also be treated as stale.
            _invalidationEpoch++;

            AssertMainThread();

            if (addresses == null) return;

            foreach (var address in addresses)
            {
                InvalidateAddress(address);
            }

            DropCompletedInFlight();
            Compact();
        }

        /// <summary>
        /// Give back the cache's reference to every cached type stored under one address, leaving
        /// whatever any other holder retained untouched.
        /// </summary>
        /// <remarks>
        /// Same key match as <see cref="EvictAddress"/>, different release: <see cref="ReleaseAsset"/>
        /// needs a hard release because it is an eviction API — a caller elsewhere still holding a
        /// reference to what it evicts is exactly the case an unconditional release is for (see
        /// <see cref="ClearCache"/>'s remarks). A catalog invalidation is the opposite case —
        /// CacheHandle() is the only reference this path is entitled to, so Dispose() (a decrement)
        /// is all it takes back. A
        /// handle that is still alive after that decrement stays out of _assetCache (so the next
        /// load hits the updated catalog) but stays in _activeHandles (so teardown can still reach
        /// it) — the same displaced-entry bookkeeping CacheHandle() does when it retires a stale one.
        /// </remarks>
        private void InvalidateAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return;

            List<AssetCacheKey> keysToRemove = null;

            foreach (var kvp in _assetCache)
            {
                // Exact address match, plus sub-objects of it — a bare prefix test would also hit
                // "Enemy_Boss" for "Enemy_", so AssetCacheKey.MatchesAddress requires the bracket.
                if (!kvp.Key.MatchesAddress(address)) continue;

                keysToRemove ??= new List<AssetCacheKey>();
                keysToRemove.Add(kvp.Key);
            }

            if (keysToRemove == null) return;

            foreach (var key in keysToRemove)
            {
                // Because the tier metadata lives IN the entry being removed, invalidation drops the
                // handle and its bytes/tier/pin state in one indivisible step. There is no separate
                // metadata map that could be left holding a row for an address with no handle.
                //
                // A pin, though, must outlive the entry that carried it. Invalidation is not the
                // caller withdrawing its pin — it is this loader dropping a now-stale entry so the
                // next load re-fetches against the updated catalog — so re-arming the pin as pending
                // is what makes the reloaded entry come back protected. Without this, a CDN catalog
                // update (CatalogService -> AssetLoaderRegistry.InvalidateAll -> here) silently
                // unpinned every pinned asset: the entry left with IsPinned == true and nothing in
                // _pendingPins, so the next load re-cached it as an ordinary eviction candidate and
                // BOTH observability channels (GetTieredCacheStats().PinnedEntries and
                // .PendingPins) read zero. Same re-arm the two stale-drop sites already do
                // (TryRetainCached here, TieredCache/ThreadSafeCacheManager in their Set/TryGet).
                if (_assetCache.TryGetValue(key, out var invalidated))
                {
                    CarryPinAcrossStaleEntry(invalidated);
                }

                RemoveCacheEntry(key, hard: false);
            }
        }

        /// <summary>
        /// Evict every cached type stored under one address, hard-releasing each regardless of who
        /// still holds a reference. Backs <see cref="ReleaseAsset"/> only — see its remarks, and
        /// <see cref="InvalidateAddress"/>, for why the catalog-invalidation path cannot share this.
        /// </summary>
        private void EvictAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return;

            List<AssetCacheKey> keysToRemove = null;

            foreach (var kvp in _assetCache)
            {
                // Exact address match, plus sub-objects of it — a bare prefix test would also hit
                // "Enemy_Boss" for "Enemy_", so AssetCacheKey.MatchesAddress requires the bracket.
                if (!kvp.Key.MatchesAddress(address)) continue;

                keysToRemove ??= new List<AssetCacheKey>();
                keysToRemove.Add(kvp.Key);
            }

            if (keysToRemove == null) return;

            foreach (var key in keysToRemove)
            {
                RemoveCacheEntry(key, hard: true);
            }
        }

        /// <summary>
        /// Teardown: hard-release every handle this loader ever handed out, regardless of who still
        /// holds a reference, and release every instantiated GameObject with it.
        /// </summary>
        private void TearDownAll()
        {
            LogVerbose($"[AssetLoader] Tearing down ({_assetCache.Count} cached, {_activeHandles.Count} tracked)");

            // Release everything tracked, not just the cache: label loads never enter _assetCache,
            // so clearing only that leaks the whole label's bundles. ForceRelease is idempotent, so
            // the overlap between the two collections is safe.
            //
            // Snapshot both collections and empty them BEFORE releasing anything, for the reason
            // spelled out in ClearCache: ForceRelease can run game code that re-enters this loader,
            // and re-entrant TrackHandle adds to _activeHandles while re-entrant RemoveCacheEntry
            // removes from _assetCache — either one throws "Collection was modified" out of a live
            // foreach. Anything a re-entrant caller manages to add after this point is its own
            // handle in its own ledger, and Dispose's _disposed flag stops it being cached at all.
            var tracked = _activeHandles.ToArray();
            var entries = new CachedAsset[_assetCache.Count];
            _assetCache.Values.CopyTo(entries, 0);

            _assetCache.Clear();
            _tieredBytes = 0;
            ResetTieringState();

            _activeHandles.Clear();
            _sinceCompaction = 0;

            foreach (var handle in tracked)
            {
                handle?.ForceRelease();
            }

            foreach (var entry in entries)
            {
                entry.Handle?.ForceRelease();
            }

            // Instances are owned by Addressables per instance, not by our reference count.
            ReleaseTrackedInstances();
        }

        /// <summary>
        /// Release specific asset by address (all types stored for that address).
        /// The singular form of <see cref="ClearCache"/>, with the same unconditional eviction —
        /// see its remarks for why a decrement cannot free anything here.
        /// </summary>
        public void ReleaseAsset(string address)
        {
            AssertMainThread();

            if (string.IsNullOrEmpty(address)) return;

            EvictAddress(address);
            Compact();
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public (int cachedAssets, int activeHandles) GetCacheStats()
        {
            AssertMainThread();

            return (_assetCache.Count, _activeHandles.Count);
        }

        /// <summary>
        /// Cache probe: whether this loader currently has a live, cached handle for
        /// (<paramref name="address"/>, <typeparamref name="T"/>) — the exact key
        /// <see cref="LoadAssetAsync{T}(string)"/> would hit. Read-only: does not retain a
        /// reference and does not affect the reference count.
        /// </summary>
        internal bool IsCached<T>(string address)
        {
            AssertMainThread();

            if (string.IsNullOrEmpty(address)) return false;

            return _assetCache.TryGetValue(new AssetCacheKey(address, typeof(T)), out var entry)
                   && entry?.Handle != null && entry.Handle.IsAlive;
        }

        /// <summary>
        /// Cache probe across every type cached under one address. The cache keys by
        /// (address, Type) (see <see cref="AssetCacheKey"/>), so an address loaded as two
        /// different types is two independent entries — this is a broader, type-erased check;
        /// prefer <see cref="IsCached{T}"/> when the type is known.
        /// </summary>
        internal bool IsCached(string address)
        {
            AssertMainThread();

            if (string.IsNullOrEmpty(address)) return false;

            foreach (var kvp in _assetCache)
            {
                // Same match as the eviction paths, deliberately. ReleaseAsset(address) evicts
                // sub-object entries of that address, so a probe that answered false for them would
                // make the ordinary "if (IsCached(a)) ReleaseAsset(a)" shape skip real cleanup.
                if (kvp.Value?.Handle != null && kvp.Value.Handle.IsAlive &&
                    kvp.Key.MatchesAddress(address))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Tiering

        /// <summary>
        /// Pin an asset so eviction skips it. If nothing is cached under
        /// (<paramref name="address"/>, <typeparamref name="T"/>) yet, the request is
        /// <em>remembered</em> and applied the moment that key is stored, so pinning before the load
        /// works exactly as the README teaches (HANDOFF_TO_SESSION_B.md C-9). Pins still waiting for
        /// their key are reported as <c>PendingPins</c> by <see cref="GetTieredCacheStats()"/>.
        /// </summary>
        /// <remarks>
        /// A pin on a loader built without tiering cannot protect anything — nothing evicts — so it
        /// warns rather than silently doing nothing, which is the exact shape of the bug C-9
        /// describes.
        /// </remarks>
        public void PinAsset<T>(string address)
        {
            AssertMainThread();

            if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));

            if (_tiering == null)
            {
                Debug.LogWarning(
                    $"[AssetLoader] PinAsset<{typeof(T).Name}>('{address}') did nothing: this loader " +
                    $"was built without tiering, so nothing is ever evicted and there is nothing to " +
                    $"pin against. Construct it as new AssetLoader(scopeName, config) if you need " +
                    $"pinning.");
                return;
            }

            var key = new AssetCacheKey(address, typeof(T));

            if (_assetCache.TryGetValue(key, out var entry))
            {
                entry.IsPinned = true;
                entry.Tier = CacheTier.Hot;

                if (_tiering.LogTierOperations)
                {
                    Debug.Log($"[AssetLoader] Pinned '{key}' (already cached).");
                }

                return;
            }

            RecordPendingPin(key);
        }

        /// <summary>
        /// Unpin an asset to allow eviction, and cancel any pin still pending for that key.
        /// </summary>
        /// <remarks>
        /// Cancelling the pending pin is not optional: with <see cref="PinAsset{T}"/> deferring, a
        /// Pin/Unpin pair on a key that is not cached yet would otherwise leave the pin armed and
        /// silently pin the entry when it eventually loaded.
        /// </remarks>
        public void UnpinAsset<T>(string address)
        {
            AssertMainThread();

            if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));
            if (_tiering == null) return;

            var key = new AssetCacheKey(address, typeof(T));

            bool cancelledPending = _pendingPins.Remove(key);
            bool unpinned = false;

            if (_assetCache.TryGetValue(key, out var entry))
            {
                unpinned = entry.IsPinned;
                entry.IsPinned = false;
            }

            if ((unpinned || cancelledPending) && _tiering.LogTierOperations)
            {
                Debug.Log($"[AssetLoader] Unpinned '{key}' (cached entry: {unpinned}, " +
                          $"pending pin cancelled: {cancelledPending}).");
            }
        }

        /// <summary>
        /// Force an immediate tier re-evaluation across every cached entry, of every Type. No-op on
        /// an untiered loader or when <see cref="TieredCacheConfig.EnableAutoTiering"/> is off.
        /// </summary>
        public void EvaluateTiers()
        {
            AssertMainThread();

            if (_tiering == null || !_tiering.EnableAutoTiering) return;

            EvaluateAndAdjustTiers();
            _lastTierEvaluation = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Force an immediate eviction pass. No-op on an untiered loader or when
        /// <see cref="TieredCacheConfig.EnableAutoEviction"/> is off.
        /// </summary>
        /// <remarks>
        /// Eviction gives back only the cache's own reference. An asset another owner is still
        /// holding stays valid; this is not <see cref="ClearCache"/>.
        /// </remarks>
        public void ForceEviction()
        {
            AssertMainThread();

            if (_tiering == null || !_tiering.EnableAutoEviction) return;

            PerformEviction();
        }

        /// <summary>
        /// Tiering statistics across every cached entry of every Type. Named
        /// <c>GetTieredCacheStats</c> rather than <c>GetCacheStats</c> because
        /// <see cref="GetCacheStats"/> already exists with a different return type and C# cannot
        /// overload on that.
        /// </summary>
        /// <remarks>
        /// On an untiered loader the entry counts are real (the cache still has entries) while every
        /// byte and tier figure reads 0 — nothing was ever measured or tiered. Use
        /// <see cref="TieringEnabled"/> to tell the two apart.
        /// </remarks>
        public TieredCacheStats GetTieredCacheStats()
        {
            AssertMainThread();

            return BuildTieredStats(null);
        }

        /// <summary>
        /// Tiering statistics restricted to entries cached as <typeparamref name="T"/>, or
        /// <c>null</c> when this loader has tiering disabled.
        /// </summary>
        /// <remarks>
        /// <c>null</c> means exactly one thing — tiering is off — so the answer is deterministic and
        /// needs no extra bookkeeping. (The old <c>TieredAssetLoader.GetCacheStats&lt;T&gt;</c>
        /// returned <c>null</c> until the first load of <c>T</c> created a per-type cache; there is
        /// no per-type object to test for existence any more, and an all-zero struct for "tiered but
        /// nothing of this type cached" is the honest answer.)
        ///
        /// <para><c>TotalEntries</c>/<c>HotEntries</c>/<c>WarmEntries</c>/<c>ColdEntries</c>/
        /// <c>PinnedEntries</c>/<c>PendingPins</c>/<c>TotalSizeBytes</c> are exact per-<c>T</c>
        /// figures — the entries carry their Type. <c>TotalAccesses</c>/<c>CacheHits</c>/
        /// <c>HitRate</c>/<c>TotalEvictions</c>/<c>TotalPromotions</c>/<c>TotalDemotions</c> are
        /// loader-wide: there is one counter set per loader now, and keeping them per-Type would
        /// mean a Dictionary&lt;Type, counters&gt; — a second book, i.e. L-4's shape rebuilt for
        /// statistics. These are diagnostics, not lifetime.</para>
        /// </remarks>
        public TieredCacheStats? GetTieredCacheStats<T>()
        {
            AssertMainThread();

            if (_tiering == null) return null;

            return BuildTieredStats(typeof(T));
        }

        /// <summary>
        /// Build a stats snapshot over every entry, or only those whose key Type is
        /// <paramref name="filter"/>.
        /// </summary>
        private TieredCacheStats BuildTieredStats(Type filter)
        {
            int total = 0, hot = 0, warm = 0, cold = 0, pinned = 0;
            long bytes = 0;

            foreach (var entry in _assetCache.Values)
            {
                if (filter != null && entry.Key.Type != filter) continue;

                total++;
                bytes += entry.EstimatedBytes;
                if (entry.IsPinned) pinned++;

                switch (entry.Tier)
                {
                    case CacheTier.Hot: hot++; break;
                    case CacheTier.Warm: warm++; break;
                    default: cold++; break;
                }
            }

            int pending = 0;
            if (filter == null)
            {
                pending = _pendingPins.Count;
            }
            else
            {
                foreach (var key in _pendingPins)
                {
                    if (key.Type == filter) pending++;
                }
            }

            return new TieredCacheStats
            {
                TotalEntries = total,
                HotEntries = hot,
                WarmEntries = warm,
                ColdEntries = cold,
                PinnedEntries = pinned,
                PendingPins = pending,

                // Loader-wide reports the number eviction actually gates on, not a re-derived sum:
                // if the two could ever disagree the enforced one is the one worth seeing.
                TotalSizeBytes = filter == null ? _tieredBytes : bytes,
                MaxSizeBytes = _tiering?.MaxCacheSizeBytes ?? 0,

                TotalAccesses = _tierAccesses,
                CacheHits = _tierHits,
                HitRate = _tierAccesses > 0 ? (float)_tierHits / _tierAccesses : 0f,
                TotalEvictions = _tierEvictions,
                TotalPromotions = _tierPromotions,
                TotalDemotions = _tierDemotions
            };
        }

        /// <summary>
        /// Remember a pin for a key that is not cached yet, for <see cref="CacheHandle{T}"/> to apply
        /// on arrival. Bounded — see <see cref="MaxPendingPins"/>.
        /// </summary>
        private void RecordPendingPin(AssetCacheKey key)
        {
            if (_pendingPins.Contains(key)) return;

            if (_pendingPins.Count >= MaxPendingPins)
            {
                // The one case where a pin is genuinely refused rather than deferred, so this is an
                // error and is not gated behind LogTierOperations: nothing later will apply it.
                Debug.LogError(
                    $"[AssetLoader] Pin('{key}') was refused: {MaxPendingPins} pins are already " +
                    $"waiting for keys that have never been cached. This key will NOT be pinned when " +
                    $"it loads. Pinning addresses that are never loaded is the usual cause — check " +
                    $"the addresses being passed to PinAsset.");
                return;
            }

            _pendingPins.Add(key);

            if (_tiering.LogTierOperations)
            {
                Debug.Log($"[AssetLoader] Pin('{key}') found nothing cached under that key; the pin " +
                          $"is now pending and will be applied when the key is stored.");
            }
        }

        /// <summary>
        /// Re-arm a pin as pending when the entry carrying it is dropped as stale (its handle died
        /// outside this cache). Without this, a pinned entry force-released by another owner comes
        /// back unpinned on the next load with no trace.
        /// </summary>
        private void CarryPinAcrossStaleEntry(CachedAsset entry)
        {
            if (_tiering == null || entry == null || !entry.IsPinned) return;

            if (_pendingPins.Count < MaxPendingPins)
            {
                _pendingPins.Add(entry.Key);
            }
        }

        /// <summary>
        /// Drop everything that only made sense for the entries just emptied out of the cache.
        /// </summary>
        private void ResetTieringState()
        {
            _pendingPins.Clear();
            _tierShortfallReported = false;

            _tierAccesses = 0;
            _tierHits = 0;
            _tierEvictions = 0;
            _tierPromotions = 0;
            _tierDemotions = 0;
        }

        /// <summary>
        /// Interval-gated tier evaluation, run from the cache-hit path.
        /// </summary>
        /// <remarks>
        /// The <c>_lastTierEvaluation &gt;= 0f</c> test is what makes the <c>-1f</c> sentinel work:
        /// the first main-thread cache hit evaluates immediately and latches the clock, so
        /// <c>Time.realtimeSinceStartup</c> is never read from a constructor — which is where
        /// <c>TieredCache&lt;T&gt;</c> still reads it (TieredCache.cs:95) and where it throws for a
        /// loader constructed off the main thread by <c>ThreadSafeAssetLoader</c>.
        /// </remarks>
        private void MaybeEvaluateTiers()
        {
            if (_tiering == null || !_tiering.EnableAutoTiering) return;

            // Safe: every caller has already passed AssertMainThread() or AfterAwait().
            float now = Time.realtimeSinceStartup;

            if (_lastTierEvaluation >= 0f &&
                now - _lastTierEvaluation < _tiering.TierEvaluationInterval)
            {
                return;
            }

            EvaluateAndAdjustTiers();
            _lastTierEvaluation = now;
        }

        /// <summary>
        /// Evaluate every cached entry, of every Type, and adjust its tier from its access pattern.
        /// Thresholds and the promote/demote ladder are the ones <c>TieredCache&lt;T&gt;</c> uses
        /// (TieredCache.cs:558-610), applied over one dictionary instead of one per Type.
        /// </summary>
        private void EvaluateAndAdjustTiers()
        {
            foreach (var entry in _assetCache.Values)
            {
                if (entry.IsPinned)
                    continue; // Skip pinned entries

                float score = entry.CalculateTierScore();
                CacheTier oldTier = entry.Tier;
                CacheTier newTier = oldTier;

                // Determine new tier based on score
                if (score >= _tiering.PromoteToHotThreshold)
                {
                    newTier = CacheTier.Hot;
                }
                else if (score >= _tiering.PromoteToWarmThreshold)
                {
                    newTier = CacheTier.Warm;
                }
                else if (score <= _tiering.DemoteToColdThreshold)
                {
                    newTier = CacheTier.Cold;
                }
                else if (oldTier == CacheTier.Hot && score < _tiering.DemoteToWarmThreshold)
                {
                    newTier = CacheTier.Warm;
                }

                // Apply tier change
                if (newTier != oldTier)
                {
                    entry.Tier = newTier;

                    if (newTier < oldTier) // Promotion (Hot=0, Cold=2)
                    {
                        _tierPromotions++;
                        if (_tiering.LogTierOperations)
                        {
                            Debug.Log($"[AssetLoader] Promoted {entry.Key}: {oldTier} → {newTier} (score: {score:F2})");
                        }
                    }
                    else // Demotion
                    {
                        _tierDemotions++;
                        if (_tiering.LogTierOperations)
                        {
                            Debug.Log($"[AssetLoader] Demoted {entry.Key}: {oldTier} → {newTier} (score: {score:F2})");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Cold first, then lowest score first — the eviction order.
        /// </summary>
        /// <remarks>
        /// <c>CacheTier</c> is <c>Hot = 0, Warm = 1, Cold = 2</c>, so comparing <c>b</c> to <c>a</c>
        /// sorts the tier descending and puts Cold at the front, reproducing
        /// <c>OrderByDescending(e =&gt; e.Tier).ThenBy(e =&gt; e.CalculateTierScore())</c> without the
        /// LINQ. <c>SortScore</c> is read rather than recomputed so the score each entry was
        /// <em>selected</em> on is the score it is <em>ordered</em> on — recomputing mid-sort would
        /// feed <see cref="List{T}.Sort"/> a comparison whose answer drifts with the clock.
        /// </remarks>
        private static readonly Comparison<CachedAsset> EvictionOrder = (a, b) =>
        {
            int byTier = b.Tier.CompareTo(a.Tier);
            return byTier != 0 ? byTier : a.SortScore.CompareTo(b.SortScore);
        };

        /// <summary>
        /// Evict cold/low-scoring entries until the cache is back at
        /// <see cref="TieredCacheConfig.EvictionTargetRatio"/> of its ceiling.
        /// </summary>
        /// <remarks>
        /// EVICTION SPANS EVERY TYPE. This is the one behavioural difference from
        /// <c>TieredCache&lt;T&gt;.PerformEviction</c> (TieredCache.cs:656-735), and it is the point
        /// of the merge. A per-type cache admits in its own remarks that its candidates "still only
        /// ever come from this type's own entries", and relied on
        /// <c>TieredAssetLoader.ForceEviction</c>'s round-robin over sibling caches to converge on
        /// the real ceiling. With one dictionary the candidate set is every entry of every Type,
        /// ranked together, in one pass — the round-robin convergence argument disappears along with
        /// the thing that needed it.
        ///
        /// <para>NO LINQ. <c>_assetCache</c> is walked once to score and select, the reusable
        /// <c>_evictScratch</c> list is sorted in place, then drained. The LINQ original allocated a
        /// list plus three iterators on every pass, on the main thread, from inside a load.</para>
        ///
        /// <para>RELEASE, NOT FORCE-RELEASE. Removal is <c>RemoveCacheEntry(key, hard: false)</c> —
        /// a decrement, matching <c>entry.Handle?.Release()</c> at TieredCache.cs:719. Eviction must
        /// never free an asset under a live holder; that is reserved for <see cref="ClearCache"/>,
        /// <see cref="ReleaseAsset"/> and teardown.</para>
        ///
        /// <para>RE-ENTRANCY. <c>RemoveCacheEntry</c> can reach <c>Addressables.Release</c>, which can
        /// destroy objects, which can run game code, which can start a load that calls
        /// <see cref="CacheHandle{T}"/> and triggers a nested pass. <c>_evictScratch</c> is a shared
        /// field, so a nested pass would clear the list the outer one is mid-way through draining.
        /// The guard makes the nested call a no-op instead; the outer pass is already evicting.</para>
        ///
        /// <para>An entry inserted this frame is structurally immune: it is born Hot with
        /// <c>AccessCount = 1</c>, so its score is <c>1/(1+0) * Mathf.Log(1+1) * 100 ≈ 69.3</c>, far
        /// above every shipped <c>EvictionScoreThreshold</c>. That is what stops a load from evicting
        /// the handle it just produced — a safety margin, not a coincidence.</para>
        /// </remarks>
        private void PerformEviction()
        {
            long max = _tiering.MaxCacheSizeBytes;
            if (max <= 0) return;

            if (_evicting) return;

            long targetSize = (long)(max * _tiering.EvictionTargetRatio);
            long overage = _tieredBytes - targetSize;

            if (overage <= 0)
            {
                _tierShortfallReported = false;
                return;
            }

            _evicting = true;

            try
            {
                _evictScratch.Clear();

                // Candidates = the entries this pass can actually take. The gate is applied HERE,
                // once, instead of inside the drain loop, so the pool can be measured before
                // anything is drawn from it. Same victim set either way — the gate is per-entry and
                // independent of iteration order — but the demand below can now be honest about what
                // is reachable.
                long reclaimable = 0;
                foreach (var entry in _assetCache.Values)
                {
                    if (entry.IsPinned) continue;

                    float score = entry.CalculateTierScore();
                    if (entry.Tier != CacheTier.Cold && score >= _tiering.EvictionScoreThreshold) continue;

                    entry.SortScore = score;
                    _evictScratch.Add(entry);
                    reclaimable += entry.EstimatedBytes;
                }

                _evictScratch.Sort(EvictionOrder);

                // The cache wants `overage` bytes back; this pass can only ever produce
                // `reclaimable`. Chase the smaller of the two, so reaching the goal and exhausting
                // the candidates are no longer indistinguishable outcomes.
                long amountToEvict = Math.Min(overage, reclaimable);

                long evictedSize = 0;
                int evictedCount = 0;

                for (int i = 0; i < _evictScratch.Count; i++)
                {
                    if (evictedSize >= amountToEvict) break;

                    var entry = _evictScratch[i];

                    // Only book what this pass actually reclaims. The _evicting guard stops a nested
                    // PASS, but it does not stop re-entrant ClearCache/ReleaseAsset/InvalidateAddress
                    // (reached through Addressables.Release -> destroyed objects -> game code) from
                    // removing entries this list still holds stale references to. RemoveCacheEntry is
                    // a documented no-op for a key that is already gone, so counting before calling
                    // it inflated _tierEvictions, the "eviction complete" line and the evictedSize
                    // ReportUnreachableBudget compares against target — the three diagnostics added
                    // so that an unreachable eviction target stops being invisible.
                    if (!_assetCache.TryGetValue(entry.Key, out var live) || !ReferenceEquals(live, entry))
                    {
                        continue;
                    }

                    evictedSize += entry.EstimatedBytes;
                    evictedCount++;

                    if (_tiering.LogTierOperations)
                    {
                        Debug.Log($"[AssetLoader] Evicted {entry.Key} from {entry.Tier} tier " +
                                  $"(score: {entry.SortScore:F2})");
                    }

                    // Adjusts _tieredBytes itself — that is the chokepoint, and it is why nothing
                    // here subtracts evictedSize a second time.
                    RemoveCacheEntry(entry.Key, hard: false);
                }

                _evictScratch.Clear();
                _tierEvictions += evictedCount;

                if (_tiering.LogTierOperations)
                {
                    Debug.Log(
                        $"[AssetLoader] Eviction complete: {evictedCount} entries, {evictedSize / 1024}KB " +
                        $"freed of {overage / 1024}KB over budget ({reclaimable / 1024}KB was reclaimable).");
                }

                ReportUnreachableBudget(targetSize, overage, evictedSize);
            }
            finally
            {
                _evicting = false;
            }
        }

        /// <summary>
        /// Warn when a single entry is, on its own, larger than the size eviction targets — the
        /// admission half of HANDOFF_TO_SESSION_B.md C-13.
        /// </summary>
        /// <remarks>
        /// An entry bigger than <c>MaxCacheSizeBytes * EvictionTargetRatio</c> puts the cache into a
        /// state eviction cannot resolve while it stays cached: the trigger fires on every subsequent
        /// insert and each pass sweeps toward a target it can never reach. Deliberately not gated
        /// behind <c>LogTierOperations</c> — the entry is still cached, so nothing else will ever
        /// surface it.
        /// </remarks>
        private void WarnIfLargerThanEvictionTarget(AssetCacheKey key, long estimatedSize)
        {
            if (estimatedSize <= 0) return;

            long targetSize = (long)(_tiering.MaxCacheSizeBytes * _tiering.EvictionTargetRatio);
            if (estimatedSize <= targetSize) return;

            Debug.LogWarning(
                $"[AssetLoader] '{key}' is {estimatedSize / 1024}KB on its own, larger than the " +
                $"post-eviction target of {targetSize / 1024}KB ({_tiering.EvictionTargetRatio:P0} of " +
                $"the {_tiering.MaxCacheSizeBytes / 1024}KB budget). It was cached, but no eviction " +
                $"pass can bring the cache back to target while it is cached — an entry is never " +
                $"evictable in the same pass that inserts it, and until this one cools to Cold every " +
                $"load will run a sweep that cannot reach its target. Raise MaxCacheSizeBytes, or " +
                $"keep an asset this size out of the tiered cache.");
        }

        /// <summary>
        /// Report an over-budget state that no further eviction can resolve.
        /// </summary>
        /// <remarks>
        /// Under the old per-type design this test had to be careful: a per-type cache routinely
        /// finished a pass with the shared budget still over, because a sibling cache was expected to
        /// chip at the remainder next, so warning on that would fire on healthy runs. There are no
        /// siblings now — this pass saw every entry of every Type — so "still over target after a
        /// pass" means exactly what it says and there is nobody else who could fix it. Latched, so it
        /// reports once per over-budget episode rather than once per load; the latch clears as soon
        /// as a pass ends back under target.
        /// </remarks>
        private void ReportUnreachableBudget(long targetSize, long overage, long evictedSize)
        {
            if (_tieredBytes <= targetSize)
            {
                _tierShortfallReported = false;
                return;
            }

            if (_tierShortfallReported) return;

            _tierShortfallReported = true;

            Debug.LogWarning(
                $"[AssetLoader:{_scopeName}] Eviction cannot reach its target: the cache still holds " +
                $"{_tieredBytes / 1024}KB against a {targetSize / 1024}KB post-eviction target " +
                $"({overage / 1024}KB was over budget, {evictedSize / 1024}KB freed). The rest is held " +
                $"by entries no pass will take — pinned entries, and entries still scoring at or above " +
                $"EvictionScoreThreshold ({_tiering.EvictionScoreThreshold:F2}), which always includes " +
                $"anything inserted this frame. Until that changes, every load runs a sweep that " +
                $"cannot succeed. Reported once per over-budget episode.");
        }

        /// <summary>
        /// Estimate asset memory size for cache management — drives <c>_tieredBytes</c>, the eviction
        /// trigger gate and every byte figure in <see cref="TieredCacheStats"/>
        /// (HANDOFF_TO_SESSION_B.md L-5).
        /// </summary>
        /// <remarks>
        /// Measured first via <see cref="UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong"/>,
        /// which accounts for real format/mip/representation instead of the per-type formulas below —
        /// a 2048x2048 ASTC 6x6 texture is ~0.9MB actual vs. 16MB from the RGBA32-no-mips formula,
        /// roughly 18x off, and in the other direction an RGBA32 texture *with* mips was undercounted
        /// by the old formula's missing +33%.
        ///
        /// <para><b>Guarded, not trusted unconditionally:</b> <c>GetRuntimeMemorySizeLong</c> is
        /// documented to return 0 in non-development builds on some platforms. This implementation
        /// could not be verified against a development-stripped build of the target platform in this
        /// environment — that confirmation is still owed (see L-5's "(a)" requirement) — so rather
        /// than assume either behavior, a &lt;= 0 result falls back to the same heuristic this method
        /// used before, instead of letting eviction silently stop. That fallback is still the
        /// documented-inaccurate formula, kept only as the honest "no better number available"
        /// answer, not as a fix in its own right — HANDOFF_TO_SESSION_B.md tracks the eventual real
        /// answer as W4-07 (ingesting <c>Library/com.unity.addressables/buildReports</c>).</para>
        ///
        /// <para><b>GameObject is a special case, not a whole-object measurement:</b>
        /// <c>GetRuntimeMemorySizeLong</c> on a GameObject reports only the native GameObject shell
        /// (roughly a fixed, small overhead) — unlike <c>Texture2D</c>/<c>Mesh</c>/<c>AudioClip</c>,
        /// where the object being measured *is* the resource, it does not walk the object graph to
        /// add up the meshes/materials/textures the prefab's components reference. Trusting that
        /// number directly (the earlier version of this method did, via the generic branch above)
        /// would report a few hundred bytes for a prefab that drags in a 40MB mesh — strictly worse
        /// than the flat heuristic it replaced, because it looks like a real measurement instead of
        /// an admitted guess. <see cref="EstimateGameObjectSize"/> walks the referenced render data
        /// explicitly instead.</para>
        ///
        /// <para>Only ever called behind a <c>_tiering != null</c> test — see
        /// <see cref="CacheHandle{T}"/>.</para>
        /// </remarks>
        private long EstimateAssetSize(object asset)
        {
            if (asset == null) return 0;

            if (asset is GameObject go)
            {
                long aggregated = EstimateGameObjectSize(go);
                if (aggregated > 0) return aggregated;

                // Nothing measurable was found (e.g. an empty prefab with no renderers) — fall
                // through to the flat heuristic below rather than report 0 and let this entry look
                // free to the eviction gate.
            }
            else if (asset is UnityEngine.Object unityObject)
            {
                long measured = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(unityObject);
                if (measured > 0) return measured;

                // Falls through to the heuristic below — see the guard note above.
            }

            return asset switch
            {
                // (long) cast on the first operand, not because width*height*4 can overflow int here
                // (Unity's max texture edge is 16384, so 16384*16384*4 = 1,073,741,824 < int.MaxValue)
                // but so this stays correct if that edge ever grows.
                Texture2D texture => (long)texture.width * texture.height * 4, // RGBA32, no mips/format — heuristic only
                AudioClip audio => (long)audio.samples * audio.channels * 2, // 16-bit PCM estimate
                Mesh mesh => (long)mesh.vertexCount * 32, // rough estimate
                GameObject => 4096, // flat estimate — same number regardless of what the prefab contains
                ScriptableObject => 1024,
                _ => 1024
            };
        }

        /// <summary>
        /// Aggregate a GameObject's real memory footprint by walking the meshes, materials and
        /// material-referenced textures its components actually use, instead of trusting a
        /// single-instance <c>GetRuntimeMemorySizeLong(go)</c> call that only covers the native
        /// GameObject shell (see the remarks on <see cref="EstimateAssetSize"/>).
        /// </summary>
        /// <remarks>
        /// Deduplicates via asset identity (a <see cref="HashSet{T}"/> of
        /// <see cref="UnityEngine.Object"/>) so a material or texture shared by several renderers on
        /// the same prefab — the common case — is only counted once, matching how it is actually
        /// resident in memory. Best-effort: any exception walking a component (e.g. a custom shader
        /// that misbehaves under <c>Shader.GetPropertyCount</c>) is caught and logged rather than
        /// letting a single malformed asset break caching for every other entry.
        /// </remarks>
        private long EstimateGameObjectSize(GameObject go)
        {
            long total = 0;

            try
            {
                var seen = new HashSet<UnityEngine.Object>();

                long AddAsset(UnityEngine.Object obj)
                {
                    if (obj == null || !seen.Add(obj)) return 0;
                    long size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(obj);
                    return size > 0 ? size : 0;
                }

                // The native GameObject shell itself (components, transform, etc.) — small, but
                // still real overhead the walk below doesn't otherwise account for.
                total += AddAsset(go);

                foreach (var meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    total += AddAsset(meshFilter.sharedMesh);
                }

                foreach (var skinned in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    total += AddAsset(skinned.sharedMesh);
                }

                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    if (materials == null) continue;

                    foreach (var material in materials)
                    {
                        if (material == null) continue;
                        total += AddAsset(material);

                        // The bulk of a prefab's real memory usually lives in the textures its
                        // materials reference, not the material asset itself — walk every
                        // texture-typed shader property instead of guessing at "_MainTex".
                        var shader = material.shader;
                        if (shader == null) continue;

                        int propertyCount = shader.GetPropertyCount();
                        for (int i = 0; i < propertyCount; i++)
                        {
                            if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                                continue;

                            var texture = material.GetTexture(shader.GetPropertyName(i));
                            total += AddAsset(texture);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AssetLoader] Error walking GameObject '{go.name}' for size " +
                    $"estimation; using partial result. Error: {ex.Message}");
            }

            return total;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;

            // Report instead of throwing: a throwing Dispose breaks the IDisposable contract and
            // abandons teardown half-done inside a `using` or a `finally`. But it must not carry
            // on either — everything below mutates plain non-concurrent collections shared with
            // the main thread and calls Addressables, so an unguarded off-thread teardown is a
            // torn Dictionary plus a mutated ResourceManager, with nothing said about it.
            // ThreadSafeAssetLoader.Dispose() dispatches for exactly this reason.
            if (!IsMainThread)
            {
                Debug.LogError(ThreadViolationMessage());
                return;
            }

            LogVerbose("[AssetLoader] Disposing loader and releasing all assets");

            // Flip first: a load still in flight must not write into a loader that is going away.
            _disposed = true;

            // Wake everyone joined to a pending load — those tasks would otherwise never complete
            // now that this loader will not finish them.
            foreach (var pending in _inFlightLoads.Values)
            {
                pending.TrySetResult(null);
            }

            _inFlightLoads.Clear();

            TearDownAll();

            // Last, and below the main-thread guard above on purpose. A Dispose that refused to run
            // left this loader alive, so it must stay registered — a catalog update still needs to
            // reach it. Only a loader that actually tore down is forgotten here. Dropping it early
            // would also be harmless to correctness but wrong in the other direction: the registry
            // holds weak references, so the cost of a late unregister is one pruned slot, while an
            // early one is a live cache nothing can invalidate.
            AssetLoaderRegistry.Unregister(this);
        }

        #endregion

        // Gate verbose informational logs behind DebugSettings.logLevel so shipping
        // builds don't burn GC on string-interpolation for every cache hit.
        private static void LogVerbose(string message)
        {
            if (DebugSettings.IsVerbose)
            {
                Debug.Log(message);
            }
        }
    }

    /// <summary>
    /// Cache and in-flight key. Address alone is not enough — the same address can be loaded as
    /// several types — and <c>typeof(T).Name</c> is not enough either: it drops the namespace, so
    /// two same-short-named types from one address collide on a single entry.
    /// </summary>
    internal readonly struct AssetCacheKey : IEquatable<AssetCacheKey>
    {
        public readonly string Address;
        public readonly Type Type;

        public AssetCacheKey(string address, Type type)
        {
            Address = address;
            Type = type;
        }

        /// <summary>
        /// True when this key belongs to <paramref name="address"/> — either because it IS that
        /// address, or because it is a sub-object of it.
        /// </summary>
        /// <remarks>
        /// An <c>AssetReference</c> load keys by <c>RuntimeKey</c>, which for a sub-object reference
        /// is <c>"{guid}[{subObjectName}]"</c> rather than the bare guid (see
        /// <see cref="AssetLoader.AssetReferenceCacheAddress"/>). Catalog invalidation and
        /// <c>ReleaseAsset</c> both arrive holding the bare guid/address, so an exact-equality test
        /// alone would walk straight past every sub-object entry and leave it serving content from
        /// the pre-update catalog.
        ///
        /// The bracket is required rather than treated as a plain prefix: a prefix test would also
        /// match "Enemy_Boss" for "Enemy_", which is the very thing the exact-match comment on the
        /// eviction loops exists to prevent.
        /// </remarks>
        public bool MatchesAddress(string address)
        {
            if (Address == null || string.IsNullOrEmpty(address)) return false;
            if (string.Equals(Address, address, StringComparison.Ordinal)) return true;

            // "{address}[...]" — sub-object of this asset.
            return Address.Length > address.Length + 1
                && Address[address.Length] == '['
                && Address[Address.Length - 1] == ']'
                && string.CompareOrdinal(Address, 0, address, 0, address.Length) == 0;
        }

        public bool Equals(AssetCacheKey other)
        {
            return Type == other.Type && string.Equals(Address, other.Address, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is AssetCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Address?.GetHashCode() ?? 0) * 397) ^ (Type?.GetHashCode() ?? 0);
            }
        }

        public override string ToString()
        {
            return $"{Address} ({Type?.FullName})";
        }
    }

    /// <summary>
    /// One row of <see cref="AssetLoader"/>'s cache: the handle, plus the metadata the tiering
    /// configuration attaches to it.
    /// </summary>
    /// <remarks>
    /// NON-GENERIC ON PURPOSE, AND WHY <c>CacheEntry&lt;T&gt;</c> CANNOT SERVE HERE.
    /// <c>_assetCache</c> is keyed by (address, Type) and stores <see cref="IOwnedHandle"/> precisely
    /// so an entry can be released without knowing <c>T</c>.
    /// <see cref="AddressableManager.Core.CacheEntry{T}"/> is generic and holds an
    /// <c>IAssetHandle&lt;T&gt;</c>, so it could only be the value of this non-generic dictionary by
    /// boxing into <c>object</c> and casting back — the reflection-adjacent shape L-8 just removed.
    /// <c>CacheEntry&lt;T&gt;</c> also stays in live use by
    /// <see cref="AddressableManager.Core.ThreadSafeCacheManager{T}"/>, so it is untouched.
    ///
    /// <para>Allocated for every cached asset whether or not the loader is tiered. One small object
    /// per entry, against a dictionary that already allocates a bucket per entry — worth it to keep
    /// one code shape on the read/insert/remove paths instead of two. On an untiered loader
    /// <see cref="EstimatedBytes"/> is 0 and no tier field is ever read.</para>
    ///
    /// <para>Every method body below is copied from
    /// <see cref="AddressableManager.Core.CacheEntry{T}"/> (CacheEntry.cs:76-125) so tier behaviour
    /// is provably unchanged rather than re-derived. Do not "improve" the score formula here without
    /// changing it there too.</para>
    /// </remarks>
    internal sealed class CachedAsset
    {
        /// <summary>
        /// The key this entry is filed under. Not a second book: it is the same value the dictionary
        /// is keyed by, written once at construction and never mutated. It exists so an eviction
        /// sweep can sort entries and still know which key to remove, without the "rebuild the key
        /// from a display string" step the per-type cache needed (TieredCache.cs:705).
        /// </summary>
        public readonly AssetCacheKey Key;

        /// <summary>The cached handle. The cache holds its own reference on it.</summary>
        public readonly IOwnedHandle Handle;

        /// <summary>Estimated resident bytes; 0 when the loader is not tiered.</summary>
        public readonly long EstimatedBytes;

        /// <summary>Time when the entry was first cached (<c>Time.realtimeSinceStartup</c>).</summary>
        public readonly float CreationTime;

        /// <summary>Current cache tier. Starts Hot, matching CacheEntry.cs:68.</summary>
        public CacheTier Tier;

        /// <summary>Accesses so far. Starts at 1 — the load itself — matching CacheEntry.cs:67.</summary>
        public int AccessCount;

        /// <summary>Last access time (<c>Time.realtimeSinceStartup</c>).</summary>
        public float LastAccessTime;

        /// <summary>Whether eviction must skip this entry.</summary>
        public bool IsPinned;

        /// <summary>
        /// Scratch, written only by the eviction sweep just before it sorts, so selection and
        /// ordering use the same score. Meaningless outside <c>PerformEviction</c>.
        /// </summary>
        public float SortScore;

        public CachedAsset(AssetCacheKey key, IOwnedHandle handle, long estimatedBytes)
        {
            Key = key;
            Handle = handle;
            EstimatedBytes = estimatedBytes;

            // Main thread only — the sole construction site is AssetLoader.CacheHandle, which is
            // reached only after a passed AfterAwait(). AssetLoader's own constructors deliberately
            // never read the clock; see _lastTierEvaluation.
            CreationTime = Time.realtimeSinceStartup;
            LastAccessTime = CreationTime;
            AccessCount = 1; // First access is the load itself
            Tier = CacheTier.Hot; // Start in Hot tier
            IsPinned = false;
        }

        /// <summary>
        /// Record an access to this entry
        /// Updates access count and last access time
        /// </summary>
        public void RecordAccess()
        {
            AccessCount++;
            LastAccessTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Get age of this entry in seconds
        /// </summary>
        public float GetAge()
        {
            return Time.realtimeSinceStartup - CreationTime;
        }

        /// <summary>
        /// Get time since last access in seconds
        /// </summary>
        public float GetTimeSinceLastAccess()
        {
            return Time.realtimeSinceStartup - LastAccessTime;
        }

        /// <summary>
        /// Calculate access frequency (accesses per second)
        /// </summary>
        public float GetAccessFrequency()
        {
            float age = GetAge();
            return age > 0 ? AccessCount / age : AccessCount;
        }

        /// <summary>
        /// Calculate a score for tier determination
        /// Higher score = should be in higher tier (Hot)
        /// </summary>
        public float CalculateTierScore()
        {
            if (IsPinned)
                return float.MaxValue; // Pinned entries always stay Hot

            float timeSinceAccess = GetTimeSinceLastAccess();
            float frequency = GetAccessFrequency();

            // Score formula: higher frequency and recent access = higher score
            // Recency weight: newer accesses are more valuable
            float recencyWeight = 1.0f / (1.0f + timeSinceAccess);
            float frequencyWeight = Mathf.Log(1.0f + frequency);

            return recencyWeight * frequencyWeight * 100f;
        }

        public override string ToString()
        {
            return $"[{Tier}] {Key} - Accesses: {AccessCount}, " +
                   $"Frequency: {GetAccessFrequency():F2}/s, " +
                   $"Last access: {GetTimeSinceLastAccess():F1}s ago, " +
                   $"Size: {EstimatedBytes / 1024}KB";
        }
    }

    /// <summary>
    /// Tracks a shared list operation for reference counting
    /// </summary>
    internal class SharedListOperationTracker<T> : IOwnedHandle
    {
        private readonly AsyncOperationHandle<System.Collections.Generic.IList<T>> _listHandle;

        // Plain mutable field on purpose — see AssetReferenceCounter.
        private AssetReferenceCounter _references;

        public bool IsValid => _references.IsAlive
                               && _listHandle.IsValid()
                               && _listHandle.Status == AsyncOperationStatus.Succeeded;

        public AsyncOperationStatus Status => _listHandle.Status;
        public bool IsAlive => _references.IsAlive;

        public SharedListOperationTracker(AsyncOperationHandle<System.Collections.Generic.IList<T>> listHandle)
        {
            _listHandle = listHandle;
            // Start with 1 reference — caller (AssetLoader.LoadAssetsByLabelAsync) drops it
            // once it has wrapped all items in ListItemHandle. Empty-result label loads then
            // release immediately instead of leaking the list handle.
            _references = new AssetReferenceCounter(1);
        }

        public void Retain()
        {
            if (_references.TryRetain()) return;

            throw new ObjectDisposedException(
                nameof(SharedListOperationTracker<T>),
                "[SharedListOperationTracker] Cannot retain a tracker that already reached zero references");
        }

        public bool TryRetain()
        {
            return _references.TryRetain();
        }

        public void Release()
        {
            if (_references.Release()) ReleaseOperation();
        }

        public void Dispose()
        {
            Release();
        }

        public void ForceRelease()
        {
            if (_references.ForceRelease()) ReleaseOperation();
        }

        private void ReleaseOperation()
        {
            if (_listHandle.IsValid())
            {
                Addressables.Release(_listHandle);
            }
        }
    }

    /// <summary>
    /// Handle for individual items from a list operation
    /// </summary>
    internal class ListItemHandle<T> : IAssetHandle<T>, IOwnedHandle
    {
        private readonly SharedListOperationTracker<T> _tracker;
        private readonly T _asset;

        // Plain mutable field on purpose — see AssetReferenceCounter.
        private AssetReferenceCounter _references;

        public T Asset => _asset;

        // Mirrors AssetHandle<T>: a released item handle is never valid, even while sibling items
        // keep the shared list operation alive.
        public bool IsValid => _references.IsAlive && _tracker.IsValid && _asset != null;

        public AsyncOperationStatus Status => _tracker.Status;
        public float Progress => 1f; // Already loaded
        public int ReferenceCount => _references.Count;
        public bool IsAlive => _references.IsAlive;

        public ListItemHandle(SharedListOperationTracker<T> tracker, T asset)
        {
            _tracker = tracker;
            _asset = asset;
            _references = new AssetReferenceCounter(1);
            _tracker.Retain(); // Increment shared tracker
        }

        public void Retain()
        {
            if (_references.TryRetain()) return;

            throw new ObjectDisposedException(
                nameof(ListItemHandle<T>),
                "[ListItemHandle] Cannot retain a handle whose reference count already reached zero");
        }

        public bool TryRetain()
        {
            return _references.TryRetain();
        }

        public void Release()
        {
            if (_references.Release()) ReleaseTracker();
        }

        public AsyncOperationHandle<T> GetHandle()
        {
            // Cannot convert IList handle to single item handle
            Debug.LogWarning("[ListItemHandle] GetHandle() not supported for list-loaded items");
            return default;
        }

        public void Dispose()
        {
            Release();
        }

        public void ForceRelease()
        {
            if (_references.ForceRelease()) ReleaseTracker();
        }

        private void ReleaseTracker()
        {
            _tracker.Release(); // Decrement shared tracker
        }
    }
}
