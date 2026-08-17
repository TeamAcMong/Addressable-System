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
        // Cache: key = (address, Type), value = handle (owner-side interface, so an entry can be
        // released without knowing T)
        private readonly Dictionary<AssetCacheKey, IOwnedHandle> _assetCache = new();

        // Ledger of every handle this loader handed out, so teardown can reach the ones the cache
        // does not hold — label loads live only here.
        private readonly List<IOwnedHandle> _activeHandles = new();

        // Loads registered before their first await, so concurrent callers for one (address, Type)
        // join a single operation instead of each building a wrapper nobody will ever release.
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
        /// Create AssetLoader with optional scope name for monitoring
        /// </summary>
        /// <param name="scopeName">Scope name for Dashboard tracking (Editor-only)</param>
        public AssetLoader(string scopeName = "Unknown")
        {
            _scopeName = scopeName;

            System.Threading.Interlocked.CompareExchange(
                ref _mainThreadId, System.Threading.Thread.CurrentThread.ManagedThreadId, 0);

            // Registered here rather than at each of the six construction sites, because that is the
            // one place none of them can skip. A catalog update has to reach every live loader; five
            // of the six populations are otherwise unreachable. The registry holds a weak reference,
            // so this does not keep an abandoned loader alive.
            AssetLoaderRegistry.Register(this);
        }

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
            if (!_assetCache.TryGetValue(key, out var cached)) return null;

            // TryRetain is the atomic form of "test IsValid, then Retain()": no window in which
            // another owner can drop the last reference between the test and the increment.
            if (cached is IAssetHandle<T> typed && cached.TryRetain()) return typed;

            _assetCache.Remove(key);
            if (cached != null) _activeHandles.Remove(cached);
            return null;
        }

        /// <summary>
        /// Take the cache's own reference on a freshly loaded handle and start tracking it. The
        /// caller keeps the reference the handle was born with; the cache holds a second one,
        /// dropped by ReleaseAsset(), ClearCache() or teardown.
        /// </summary>
        private void CacheHandle<T>(AssetCacheKey key, AssetHandle<T> handle)
        {
            // An entry can only still sit here if it went stale between the miss and now. Drop the
            // cache's reference on it rather than leaking it behind the new one.
            if (_assetCache.TryGetValue(key, out var previous) && !ReferenceEquals(previous, handle))
            {
                previous?.Dispose();

                // Stop tracking it only if that really was the last reference. Dispose() is a
                // decrement now, so a caller may still be holding this handle — and a live handle
                // removed from _activeHandles is unreachable from teardown, i.e. its Addressables
                // operation would never be released at all.
                if (previous != null && !previous.IsAlive) _activeHandles.Remove(previous);
            }

            handle.Retain();
            _assetCache[key] = handle;
            TrackHandle(handle);
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
                    CacheHandle(cacheKey, handle);
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
                _inFlightProgress.Remove(cacheKey);
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

            var address = assetReference.AssetGUID;
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

            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            try
            {
                var operation = assetReference.LoadAssetAsync<T>();
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

                    CacheHandle(cacheKey, handle);
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
                    CacheHandle(cacheKey, handle);
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

            var address = assetReference.AssetGUID;
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

            var inFlight = NewInFlight(cacheKey);
            IOwnedHandle loaded = null;

            // Perform actual load
            try
            {
                var operation = assetReference.LoadAssetAsync<T>();
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

                    CacheHandle(cacheKey, handle);
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

            LogVerbose($"[AssetLoader] Clearing cache ({_assetCache.Count} cached, {_activeHandles.Count} tracked)");

            foreach (var handle in _assetCache.Values)
            {
                handle?.ForceRelease();
            }

            _assetCache.Clear();

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
                // Exact address match — a prefix test also hits "Enemy_Boss" for "Enemy_".
                if (!string.Equals(kvp.Key.Address, address, StringComparison.Ordinal)) continue;

                keysToRemove ??= new List<AssetCacheKey>();
                keysToRemove.Add(kvp.Key);
            }

            if (keysToRemove == null) return;

            foreach (var key in keysToRemove)
            {
                var handle = _assetCache[key];
                _assetCache.Remove(key);
                handle?.Dispose();

                // Dispose() only decremented; a handle another owner still retains must stay
                // reachable for teardown even though it just left _assetCache, or its Addressables
                // operation would never be released at all.
                if (handle != null && !handle.IsAlive) _activeHandles.Remove(handle);
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
                // Exact address match — a prefix test also hits "Enemy_Boss" for "Enemy_".
                if (!string.Equals(kvp.Key.Address, address, StringComparison.Ordinal)) continue;

                keysToRemove ??= new List<AssetCacheKey>();
                keysToRemove.Add(kvp.Key);
            }

            if (keysToRemove == null) return;

            foreach (var key in keysToRemove)
            {
                var handle = _assetCache[key];
                _assetCache.Remove(key);
                handle?.ForceRelease();
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
            foreach (var handle in _activeHandles)
            {
                handle?.ForceRelease();
            }

            foreach (var handle in _assetCache.Values)
            {
                handle?.ForceRelease();
            }

            _assetCache.Clear();
            _activeHandles.Clear();
            _sinceCompaction = 0;

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

            return _assetCache.TryGetValue(new AssetCacheKey(address, typeof(T)), out var handle)
                   && handle != null && handle.IsAlive;
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
                if (kvp.Value != null && kvp.Value.IsAlive &&
                    string.Equals(kvp.Key.Address, address, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
