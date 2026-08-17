using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using AddressableManager.Core;
using AddressableManager.Loaders;
using AddressableManager.Pooling.Adapters;
using AddressableManager.Threading;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Pooling
{
    /// <summary>
    /// What <see cref="AddressablePoolManager.TryClearPool"/> actually did, because
    /// <see cref="AddressablePoolManager.ClearPool"/>'s <c>void</c> return made P-6's refusal
    /// invisible to the caller (discovery report P-23).
    /// </summary>
    /// <remarks>
    /// A <c>bool</c> would have collapsed "no such pool" and "refused, instances still borrowed"
    /// into one indistinguishable <c>false</c>, which is the sentinel shape repo invariant 4 exists
    /// to prevent. Each value below is a different thing for the caller to do about it.
    /// </remarks>
    public enum PoolClearOutcome
    {
        /// <summary>The pool was torn down and its template handle released.</summary>
        Cleared,

        /// <summary>No pool is registered for that address; nothing happened.</summary>
        NoSuchPool,

        /// <summary>
        /// Instances spawned from this pool are still checked out, so it was left intact (P-6).
        /// Despawn them first, or use <see cref="AddressablePoolManager.ClearAllPools"/> /
        /// <see cref="AddressablePoolManager.Dispose"/> for a teardown that reclaims them.
        /// </summary>
        RefusedInstancesBorrowed,

        /// <summary>The manager is disposed; nothing happened.</summary>
        ManagerDisposed
    }

    /// <summary>
    /// Manages object pools for Addressable assets
    /// Supports runtime factory switching for different pooling implementations
    /// </summary>
    public class AddressablePoolManager : IDisposable
    {
        /// <summary>
        /// The one default max pool size this class uses, for both <see cref="CreatePoolAsync"/> and
        /// auto-created pools (discovery report P-33).
        /// </summary>
        /// <remarks>
        /// P-33: the same concept previously had four different defaults across the package —
        /// <c>Assets.CreatePool</c> 100, <c>IPoolFactory.CreatePool</c> 100,
        /// <c>Standard.CreatePool</c> 50, and a bare literal <c>50</c> hard-coded into
        /// <see cref="RunAutoCreate"/> with no configuration hook at all, which is what silently
        /// capped every <c>Simple.Pool</c> user at 50. The literal is gone and auto-create is
        /// configurable via <see cref="EnableAutoCreatePools"/>; the values that live in
        /// <c>Runtime/API</c> and in <c>PoolConfiguration</c> are outside this file and still
        /// disagree — see "Pooling: decisions still open" in Documentation/LIFETIME_DESIGN.md.
        /// </remarks>
        public const int DefaultMaxPoolSize = 100;

        private readonly AssetLoader _loader;
        private readonly Dictionary<string, IObjectPool<GameObject>> _pools;
        // Template handles are retained alongside the pool so they can be released
        // deterministically in ClearPool / Dispose. Without this the Addressables
        // ref-count on the prefab grew by 1 per pool with no path to release it.
        //
        // P-5: this dictionary stores the SAME reference AssetLoader.LoadAssetAsync handed back —
        // the caller's half of a two-reference handle (AssetLoader.CacheHandle retains a second,
        // separate reference for its own cache). Disposing the entry here (ReleaseTemplate /
        // ClearPool / ClearAllPools) always drops exactly the pool's own reference and nothing
        // more. The prefab bundle staying resident afterward is the cache's reference doing its
        // job, not a leak — see ReleaseTemplate's docstring below before "fixing" this.
        private readonly Dictionary<string, IDisposable> _templateHandles;
        private IPoolFactory _poolFactory;
        private bool _disposed;

        // Auto-create pool settings
        private bool _autoCreatePoolsEnabled = false;
        private DynamicPoolConfig _autoCreateDefaultConfig = null;
        private int _autoCreateMaxSize = DefaultMaxPoolSize;

        // P-1: one in-flight auto-create per address, shared by every synchronous Spawn() call and
        // every SpawnAsync() call that arrives while it is still running. TaskCompletionSource<bool>
        // is plain BCL, so it can be awaited from either a UniTask<T>- or Task<T>-returning async
        // method without needing its own #if UNITASK_PRESENT branch.
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingAutoCreates =
            new Dictionary<string, TaskCompletionSource<bool>>();

        // P-30: bumped by every full teardown. RunAutoCreate captures it before it starts and
        // compares afterwards, so a creation that lands AFTER a ClearAllPools() cannot quietly
        // re-populate _pools/_templateHandles with an entry the teardown never accounted for.
        private int _teardownEpoch;

        // P-2: lazily-created DontDestroyOnLoad home for pooled instances that don't have a
        // caller-supplied poolRoot, so instances share this manager's lifetime instead of whatever
        // scene happened to be active when they were instantiated. One child transform per address
        // underneath it.
        private Transform _poolsRoot;
        private readonly Dictionary<string, Transform> _addressRoots = new Dictionary<string, Transform>();

        // P-6/P-7: which instances are currently checked out (borrowed, not sitting in a pool's own
        // free list) per address, and the reverse lookup Despawn uses to verify an instance's
        // identity before releasing it. Populated by the onGet/onRelease callbacks every pool is
        // created with below, so it stays correct regardless of which IObjectPool adapter is active.
        //
        // P-8: these two maps also need an INVALIDATION path, which they originally had none of. A
        // borrowed instance is reparented out of the DontDestroyOnLoad root by Spawn, so a scene
        // unload (or gameplay code calling Object.Destroy instead of Despawn) destroys it while both
        // maps keep the managed wrapper as a live key forever. See SweepDestroyedInstances.
        private readonly Dictionary<string, HashSet<GameObject>> _activeInstances =
            new Dictionary<string, HashSet<GameObject>>();
        private readonly Dictionary<GameObject, string> _instanceOwners =
            new Dictionary<GameObject, string>();

        // P-8/P-17: the wall clock. See PoolMaintenancePump for why the two defects it drives cannot
        // be reached from an event.
        private PoolMaintenancePump _maintenancePump;

        // P-21: the pose Spawn wants applied to the instance the very next pool.Get() hands back,
        // consumed inside the onGet callback BEFORE SetActive(true) so OnEnable never sees the
        // previous borrower's transform. Single-slot rather than a parameter because IObjectPool<T>
        // .Get() takes none and widening it would be a source break (invariant 6).
        //
        // Single-threaded is NOT the same as non-re-entrant: pool.Get() runs createFunc ->
        // Object.Instantiate -> the new instance's Awake, on this thread, inside the very call that
        // staged this slot, so a prefab that spawns from its own Awake nests one GetPlaced inside
        // another. GetPlaced therefore saves and restores this field rather than clearing it — see
        // that method.
        private PendingPlacement _pendingPlacement;

        private struct PendingPlacement
        {
            public bool Active;
            public Vector3 Position;
            public Quaternion Rotation;
            public Transform Parent;
        }

        // The delegate actually subscribed to SceneManager.sceneUnloaded. Held so Dispose can
        // unsubscribe the exact instance; see MakeWeakSceneUnloadedHandler for why it is not simply
        // OnSceneUnloaded.
        private readonly UnityAction<Scene> _sceneUnloadedHandler;

        public AddressablePoolManager(AssetLoader loader, IPoolFactory poolFactory = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _poolFactory = poolFactory ?? new UnityPoolFactory(); // Default to Unity's pool
            _pools = new Dictionary<string, IObjectPool<GameObject>>();
            _templateHandles = new Dictionary<string, IDisposable>();

            // P-8: the cheap, immediate half of tracking-map invalidation. A LoadSceneMode.Single
            // load is the single most common way a borrowed instance dies without Despawn, and this
            // fires right after those objects are destroyed.
            _sceneUnloadedHandler = MakeWeakSceneUnloadedHandler(this);
            SceneManager.sceneUnloaded += _sceneUnloadedHandler;
        }

        /// <summary>
        /// Build the <c>sceneUnloaded</c> subscription so that it does <b>not</b> keep this manager
        /// alive.
        /// </summary>
        /// <remarks>
        /// <c>SceneManager.sceneUnloaded</c> is a static event, and a plain
        /// <c>+= OnSceneUnloaded</c> stores a delegate whose target is <c>this</c> — so the static
        /// event roots the manager for the lifetime of the process unless <see cref="Dispose"/>
        /// runs. This class is explicitly documented as usable without the facade (see
        /// <c>PoolMaintenancePump</c>'s remarks), and a caller that constructs one and drops it —
        /// tests especially — would otherwise leak the manager, its <c>_templateHandles</c> (one
        /// live Addressables reference per pooled address), <c>_pools</c>, <c>_activeInstances</c>,
        /// <c>_instanceOwners</c> and the <see cref="AssetLoader"/> it was handed. Worse, the
        /// abandoned manager would keep <em>running</em>: every scene unload would sweep it and log
        /// "[PoolManager] Reclaimed N borrowed instance(s)" from an object nobody owns.
        ///
        /// <para>Holding the manager through a <see cref="WeakReference{T}"/> keeps the static
        /// event's reference non-rooting, so an undisposed manager stays collectible exactly as it
        /// was before this subscription existed. Once it has been collected the handler
        /// unsubscribes itself, so the event's invocation list does not accumulate dead entries
        /// either. <see cref="Dispose"/> remains the correct, prompt path — this only bounds the
        /// damage when it is not called.</para>
        /// </remarks>
        private static UnityAction<Scene> MakeWeakSceneUnloadedHandler(AddressablePoolManager owner)
        {
            var weak = new WeakReference<AddressablePoolManager>(owner);
            UnityAction<Scene> handler = null;

            handler = scene =>
            {
                if (weak.TryGetTarget(out var target))
                {
                    target.OnSceneUnloaded(scene);
                    return;
                }

                // Owner collected without Dispose — take the subscription with it.
                SceneManager.sceneUnloaded -= handler;
            };

            return handler;
        }

        /// <summary>
        /// Ensures the code immediately following this call runs on Unity's main thread, hopping
        /// through <see cref="UnityMainThreadDispatcher"/> when the preceding <c>await</c> resumed
        /// somewhere else first.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="AssetLoader"/>'s <c>AfterAwait</c>/<c>ReleaseOnMainThread</c> reasoning
        /// (see that class's remarks): a continuation is not guaranteed to resume on the thread that
        /// started it, and UniTask's awaiters — unlike <see cref="Task"/>'s default behaviour — do
        /// not marshal back through a captured <c>SynchronizationContext</c>. Every method in this
        /// class that touches a Unity API or the non-thread-safe <c>_pools</c>/<c>_activeInstances</c>/
        /// <c>_instanceOwners</c>/<c>_pendingAutoCreates</c>/<c>_addressRoots</c> dictionaries after an
        /// <c>await</c> calls this immediately afterward instead of proceeding inline. A no-op
        /// (already-completed task) when already on the main thread, so the common case costs nothing.
        /// </remarks>
        private static Task EnsureMainThreadAsync()
        {
            if (AddressableRuntime.IsMainThread) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                UnityMainThreadDispatcher.Enqueue(() => tcs.TrySetResult(true));
            }
            catch (Exception ex)
            {
                // Same last resort as AssetLoader.ReleaseOnMainThread: if we can't even get onto the
                // dispatcher's queue there is no safe way to hop, so let the caller continue rather
                // than hang forever — its own guards (disposed re-check, etc.) are the backstop.
                Debug.LogError($"[PoolManager] Could not marshal back to the main thread: {ex.Message}");
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }

        /// <summary>
        /// Enable automatic pool creation when Spawn() is called on non-existent pool
        /// </summary>
        /// <param name="defaultConfig">Default config for auto-created pools (null = a plain pool with <paramref name="maxSize"/>)</param>
        /// <param name="maxSize">
        /// Max size for auto-created plain pools (P-33). Ignored when <paramref name="defaultConfig"/>
        /// is supplied, because a <see cref="DynamicPoolConfig"/> carries its own
        /// <see cref="DynamicPoolConfig.MaxSize"/>.
        /// </param>
        public void EnableAutoCreatePools(DynamicPoolConfig defaultConfig = null, int maxSize = DefaultMaxPoolSize)
        {
            _autoCreatePoolsEnabled = true;
            _autoCreateDefaultConfig = defaultConfig;
            _autoCreateMaxSize = maxSize;
            Debug.Log($"[PoolManager] Auto-create pools enabled (max size {maxSize} for plain pools)");
        }

        /// <summary>
        /// Disable automatic pool creation
        /// </summary>
        public void DisableAutoCreatePools()
        {
            _autoCreatePoolsEnabled = false;
            _autoCreateDefaultConfig = null;
            _autoCreateMaxSize = DefaultMaxPoolSize;
            Debug.Log("[PoolManager] Auto-create pools disabled");
        }

        /// <summary>
        /// Check if auto-create is enabled
        /// </summary>
        public bool IsAutoCreateEnabled => _autoCreatePoolsEnabled;

        /// <summary>
        /// Switch pool factory at runtime (e.g., from Unity pool to Zenject pool).
        /// <b>Affects new pools only</b> — see remarks.
        /// </summary>
        /// <remarks>
        /// P-25: pools already created keep the adapter they were built with, and the adapters do
        /// not behave identically in every corner (P-11/P-12/P-14/P-15 narrowed that set but a
        /// third-party <see cref="IPoolFactory"/> is unconstrained). A mid-session swap therefore
        /// leaves one manager whose pools can disagree with each other through the same
        /// <see cref="IObjectPool{T}"/> interface. Whether to rebuild existing pools on a swap, or
        /// to refuse the swap once any pool exists, is recorded as an open decision in
        /// Documentation/LIFETIME_DESIGN.md — this method deliberately does neither, it just no
        /// longer claims more than it does.
        /// </remarks>
        public void SetPoolFactory(IPoolFactory factory)
        {
            if (factory == null)
            {
                Debug.LogError("[PoolManager] Cannot set null factory");
                return;
            }

            if (_pools.Count > 0)
            {
                Debug.LogWarning($"[PoolManager] Switching pool factory to {factory.GetType().Name}. " +
                    $"The {_pools.Count} pool(s) that already exist keep their current implementation — " +
                    "only pools created from now on use the new factory.");
            }
            else
            {
                Debug.Log($"[PoolManager] Switching pool factory to {factory.GetType().Name}");
            }

            _poolFactory = factory;
        }

        /// <summary>
        /// Create a dynamic pool for an addressable prefab with auto-sizing.
        /// </summary>
        /// <param name="poolRoot">
        /// Parent for created instances. Leave <c>null</c> to use this manager's own
        /// <c>DontDestroyOnLoad</c> root (recommended) — passing a scene-local transform here
        /// reproduces HANDOFF_TO_SESSION_B.md P-2: pooled instances would be destroyed by the next
        /// <c>LoadSceneMode.Single</c> load while this manager still believes they're pooled. P-16
        /// makes the pool survive the root itself being destroyed, but the instances under it do not.
        /// </param>
        /// <param name="preloadCount">
        /// How many instances to create up front. Note that <see cref="DynamicPoolConfig.InitialCapacity"/>
        /// creates nothing (P-18) — this parameter is the only thing that populates the pool.
        /// </param>
#if UNITASK_PRESENT
        public async UniTask<bool> CreateDynamicPoolAsync(
            string address,
            DynamicPoolConfig config = null,
            int preloadCount = 0,
            Transform poolRoot = null)
#else
        public async Task<bool> CreateDynamicPoolAsync(
            string address,
            DynamicPoolConfig config = null,
            int preloadCount = 0,
            Transform poolRoot = null)
#endif
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot create pool on disposed manager");
                return false;
            }

            if (_pools.ContainsKey(address))
            {
                Debug.LogWarning($"[PoolManager] Pool for {address} already exists");
                return true;
            }

            // Use default config if none provided
            config = config ?? DynamicPoolConfig.Default;

            // Validate config
            if (!config.Validate(out var error))
            {
                Debug.LogError($"[PoolManager] Invalid pool config for {address}: {error}");
                return false;
            }

            IAssetHandle<GameObject> handle = null;
            bool templateCommitted = false;

            try
            {
                // Load the prefab template first
                handle = await _loader.LoadAssetAsync<GameObject>(address);

                // The continuation above may have resumed off the main thread — hop back before
                // touching any Unity API or the _pools/_templateHandles/_addressRoots dictionaries
                // below (see EnsureMainThreadAsync's remarks).
                await EnsureMainThreadAsync();

                if (_disposed)
                {
                    Debug.LogError($"[PoolManager] Manager was disposed while loading prefab for pooling: {address}");
                    handle?.Release();
                    return false;
                }

                if (handle == null || !handle.IsValid)
                {
                    Debug.LogError($"[PoolManager] Failed to load prefab for pooling: {address}");
                    return false;
                }

                // Two concurrent direct calls for the same address can both pass the ContainsKey
                // guard above before either awaited anything; only one may win and store its
                // pool/template — the loser's template handle must be released here instead of
                // orphaned (AssetLoader.CacheHandle uses the same dispose-the-loser pattern for its
                // own dictionary). StartOrJoinAutoCreate's single-flighting is a separate path and
                // does not protect direct callers of this method.
                if (_pools.ContainsKey(address))
                {
                    Debug.LogWarning($"[PoolManager] Pool for {address} already exists " +
                        "(lost a concurrent CreateDynamicPoolAsync race); releasing the redundant load.");
                    handle.Release();
                    return true;
                }

                var prefab = handle.Asset;
                var binding = new PoolRootBinding(this, address, ResolvePoolRoot(address, poolRoot));

                // Create base pool using current factory
                var basePool = _poolFactory.CreatePool<GameObject>(
                    createFunc: () => CreateInstance(prefab, binding.Resolve()),
                    onGet: (obj) =>
                    {
                        if (obj == null) return;
                        // P-21: pose first, activation second. OnEnable must not run while the
                        // transform still holds the previous borrower's position.
                        ApplyPendingPlacement(obj);
                        obj.SetActive(true);
                        TrackActive(address, obj);
                    },
                    onRelease: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(false);
                        obj.transform.SetParent(binding.Resolve());
                        UntrackActive(address, obj);
                    },
                    onDestroy: (obj) =>
                    {
                        if (obj != null) UnityEngine.Object.Destroy(obj);
                    },
                    maxSize: config.MaxSize
                );

                // Wrap in dynamic pool
                var dynamicPool = new DynamicPool<GameObject>(
                    basePool,
                    config,
                    createFunc: () => CreateInstance(prefab, binding.Resolve()),
                    onDestroy: (obj) =>
                    {
                        if (obj != null) UnityEngine.Object.Destroy(obj);
                    },
                    poolName: address
                );

                _pools[address] = dynamicPool;
                // Retain the template handle so ClearPool / Dispose can release it (B-2 fix preserved from 2.2.0).
                _templateHandles[address] = handle;
                templateCommitted = true;
                EnsureMaintenancePump();

                // P-18: say what the numbers mean. "capacity" is the auto-resize budget; the pool is
                // empty until something preloads it.
                Debug.Log($"[PoolManager] Dynamic pool created for {address} " +
                         $"(resize budget: {config.InitialCapacity}, range: [{config.MinSize}-{config.MaxSize}], " +
                         $"instances now: {(preloadCount > 0 ? "preloading" : "0 — nothing is created until first use")})");

                // Preload instances. Routes through the pool's IResizablePool primitive (P-4) instead
                // of a Get/Release loop, so preloading doesn't fire the caller's onGet or pollute the
                // peak-active tracking that drives auto-resize (P-3/P-11).
                if (preloadCount > 0)
                {
                    // P-27: log what was achieved, not what was requested.
                    int preloaded = PreloadInto(dynamicPool, address, preloadCount);
                    Debug.Log($"[PoolManager] Preloaded {preloaded}/{preloadCount} instances for {address}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Exception creating dynamic pool for {address}: {ex}");
                // Only release the template if it never made it into _templateHandles — once it has,
                // _pools[address] depends on it staying alive and this catch must not pull it out
                // from under an otherwise-successfully-created pool.
                if (!templateCommitted) handle?.Release();

                // P-13: once the pool is committed it EXISTS and works, so this must not report
                // failure — the old code returned false for a live pool whenever preloading threw,
                // and callers then logged "auto-create failed" while the very next Spawn() succeeded.
                // PreloadInto swallows-and-logs its own failures, so reaching here after commit means
                // something outside preloading went wrong; the pool is still usable.
                return templateCommitted;
            }
        }

        /// <summary>
        /// Create a pool for an addressable prefab.
        /// Returns <c>UniTask&lt;bool&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
        /// <param name="poolRoot">
        /// Parent for created instances. Leave <c>null</c> to use this manager's own
        /// <c>DontDestroyOnLoad</c> root (recommended) — see <see cref="CreateDynamicPoolAsync"/>'s
        /// docs for why a scene-local transform here is a trap.
        /// </param>
#if UNITASK_PRESENT
        public async UniTask<bool> CreatePoolAsync(
            string address,
            int preloadCount = 0,
            int maxSize = DefaultMaxPoolSize,
            Transform poolRoot = null)
#else
        public async Task<bool> CreatePoolAsync(
            string address,
            int preloadCount = 0,
            int maxSize = DefaultMaxPoolSize,
            Transform poolRoot = null)
#endif
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot create pool on disposed manager");
                return false;
            }

            if (_pools.ContainsKey(address))
            {
                Debug.LogWarning($"[PoolManager] Pool for {address} already exists");
                return true;
            }

            IAssetHandle<GameObject> handle = null;
            bool templateCommitted = false;

            try
            {
                // Load the prefab template first
                handle = await _loader.LoadAssetAsync<GameObject>(address);

                // See CreateDynamicPoolAsync's identical hop for why this is needed after every
                // await in this class.
                await EnsureMainThreadAsync();

                if (_disposed)
                {
                    Debug.LogError($"[PoolManager] Manager was disposed while loading prefab for pooling: {address}");
                    handle?.Release();
                    return false;
                }

                if (handle == null || !handle.IsValid)
                {
                    Debug.LogError($"[PoolManager] Failed to load prefab for pooling: {address}");
                    return false;
                }

                // See CreateDynamicPoolAsync's identical guard: two concurrent direct calls for the
                // same address can both pass the ContainsKey check above before either awaited
                // anything.
                if (_pools.ContainsKey(address))
                {
                    Debug.LogWarning($"[PoolManager] Pool for {address} already exists " +
                        "(lost a concurrent CreatePoolAsync race); releasing the redundant load.");
                    handle.Release();
                    return true;
                }

                var prefab = handle.Asset;
                var binding = new PoolRootBinding(this, address, ResolvePoolRoot(address, poolRoot));

                // Create pool using current factory
                var pool = _poolFactory.CreatePool<GameObject>(
                    createFunc: () => CreateInstance(prefab, binding.Resolve()),
                    onGet: (obj) =>
                    {
                        if (obj == null) return;
                        ApplyPendingPlacement(obj); // P-21
                        obj.SetActive(true);
                        TrackActive(address, obj);
                    },
                    onRelease: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(false);
                        obj.transform.SetParent(binding.Resolve());
                        UntrackActive(address, obj);
                    },
                    onDestroy: (obj) =>
                    {
                        if (obj != null) UnityEngine.Object.Destroy(obj);
                    },
                    maxSize: maxSize
                );

                _pools[address] = pool;
                _templateHandles[address] = handle;
                templateCommitted = true;
                EnsureMaintenancePump();
                Debug.Log($"[PoolManager] Pool created for {address} with max size {maxSize}");

                // P-3 (create the count that was asked for), P-11 (through the same primitive the
                // dynamic path uses, so `preloadCount` means ONE thing across both create methods and
                // both adapters — it used to mean three different things), P-27 (log the achieved
                // count, not the requested one: over-cap instances are destroyed on release).
                if (preloadCount > 0)
                {
                    int preloaded = PreloadInto(pool, address, preloadCount);
                    Debug.Log($"[PoolManager] Preloaded {preloaded}/{preloadCount} instances for {address}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Exception creating pool for {address}: {ex}");
                if (!templateCommitted) handle?.Release();

                // P-13: see CreateDynamicPoolAsync — a committed pool is a real pool.
                return templateCommitted;
            }
        }

        /// <summary>
        /// Spawn object from pool. If auto-create is enabled and the pool doesn't exist yet, queues
        /// asynchronous creation and returns <c>null</c> for THIS call — it never blocks.
        /// </summary>
        /// <remarks>
        /// P-1: this used to block the calling thread on the Addressables load underneath pool
        /// creation (<c>Task.GetAwaiter().GetResult()</c> hangs main-thread loads forever;
        /// <c>UniTask</c>'s awaiter throws immediately instead). <c>Spawn()</c> is a synchronous API
        /// and cannot honestly wait for an async load, so it no longer tries to: a pool miss with
        /// auto-create enabled starts (or joins) a single in-flight creation for that address and
        /// returns <c>null</c> immediately. <c>null</c> here always means exactly one thing — "no
        /// instance is available synchronously, right now" — never "and it also failed": a failure
        /// is logged separately, asynchronously, once it actually happens, and never rides back
        /// through this call's already-returned value (repo invariant 4). Use
        /// <see cref="SpawnAsync(string, UnityEngine.Vector3, UnityEngine.Quaternion, UnityEngine.Transform)"/>
        /// for the honest await, or pre-create the pool with <see cref="CreatePoolAsync"/> /
        /// <see cref="CreateDynamicPoolAsync"/> before the first <c>Spawn()</c> call.
        ///
        /// P-21: position, rotation and parent are applied BEFORE the instance is activated, so
        /// <c>OnEnable</c> and any <c>playOnAwake</c> particle system see the real spawn pose rather
        /// than wherever the previous borrower was despawned.
        /// </remarks>
        public GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot spawn from disposed manager");
                return null;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                if (!_autoCreatePoolsEnabled)
                {
                    Debug.LogError($"[PoolManager] No pool found for {address}. Create pool first or enable auto-create!");
                    return null;
                }

                bool alreadyPending = _pendingAutoCreates.ContainsKey(address);
                StartOrJoinAutoCreate(address);

                if (!alreadyPending)
                {
                    Debug.LogWarning($"[PoolManager] Pool for {address} does not exist yet; queued " +
                        $"asynchronous creation. This Spawn() call returns null — await " +
                        $"SpawnAsync(\"{address}\", ...) for the first instance, or pre-create the " +
                        $"pool with CreatePoolAsync/CreateDynamicPoolAsync before calling Spawn().");
                }

                return null;
            }

            return GetPlaced(pool, position, rotation, parent);
        }

        /// <summary>
        /// Spawn at position (default rotation)
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Transform parent = null)
        {
            return Spawn(address, position, Quaternion.identity, parent);
        }

        /// <summary>
        /// Spawn at origin
        /// </summary>
        public GameObject Spawn(string address, Transform parent = null)
        {
            return Spawn(address, Vector3.zero, Quaternion.identity, parent);
        }

        /// <summary>
        /// The honest, awaitable counterpart to <see cref="Spawn(string, Vector3, Quaternion, Transform)"/>
        /// (P-1). If the pool doesn't exist yet and auto-create is enabled, this actually waits for
        /// creation to finish (joining an in-flight creation started by a prior <c>Spawn()</c> call
        /// rather than starting a second, redundant one for the same address) before returning the
        /// spawned instance. Returns <c>null</c> only once the attempt has fully resolved — creation
        /// disabled, creation failed, or the manager was disposed mid-await — and that failure is
        /// always logged at the point it happens, same as every other load failure in this package.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<GameObject> SpawnAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#else
        public async Task<GameObject> SpawnAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#endif
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot spawn from disposed manager");
                return null;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                if (!_autoCreatePoolsEnabled)
                {
                    Debug.LogError($"[PoolManager] No pool found for {address}. Create pool first or enable auto-create!");
                    return null;
                }

                bool created = await StartOrJoinAutoCreate(address).Task;

                // The continuation above may have resumed off the main thread — hop back before
                // touching _pools/_disposed or any Unity API below (see EnsureMainThreadAsync's
                // remarks).
                await EnsureMainThreadAsync();

                if (_disposed)
                {
                    Debug.LogError("[PoolManager] Manager was disposed while awaiting auto-create");
                    return null;
                }

                if (!created)
                {
                    // Already logged by RunAutoCreate / CreatePoolAsync / CreateDynamicPoolAsync.
                    return null;
                }

                if (!_pools.TryGetValue(address, out pool))
                {
                    Debug.LogError($"[PoolManager] Pool was created but not found in dictionary: {address}");
                    return null;
                }
            }

            return GetPlaced(pool, position, rotation, parent);
        }

        /// <summary>
        /// SpawnAsync at position (default rotation)
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<GameObject> SpawnAsync(string address, Vector3 position, Transform parent = null)
#else
        public Task<GameObject> SpawnAsync(string address, Vector3 position, Transform parent = null)
#endif
        {
            return SpawnAsync(address, position, Quaternion.identity, parent);
        }

        /// <summary>
        /// SpawnAsync at origin
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<GameObject> SpawnAsync(string address, Transform parent = null)
#else
        public Task<GameObject> SpawnAsync(string address, Transform parent = null)
#endif
        {
            return SpawnAsync(address, Vector3.zero, Quaternion.identity, parent);
        }

        /// <summary>
        /// P-21: the one place a borrowed instance is taken out of a pool and posed. The pose is
        /// staged before <see cref="IObjectPool{T}.Get"/> so the manager's own <c>onGet</c> callback
        /// can apply it ahead of <c>SetActive(true)</c>; the second call below is the fallback for a
        /// third-party <see cref="IObjectPool{T}"/> that never invokes <c>onGet</c> at all, and is a
        /// no-op when the callback already consumed it.
        /// </summary>
        private GameObject GetPlaced(IObjectPool<GameObject> pool, Vector3 position, Quaternion rotation, Transform parent)
        {
            // Save and restore rather than stage-and-clear. pool.Get() runs createFunc BEFORE
            // actionOnGet, createFunc is Object.Instantiate, and that runs the new instance's Awake
            // synchronously — so a prefab whose Awake calls Spawn re-enters this method. With a
            // blanket "clear on exit" the nested call wiped the OUTER call's staged pose: the outer
            // onGet then found Active == false, did nothing, and SetActive(true) fired OnEnable at
            // the prefab's default transform, silently discarding the outer Spawn's
            // position/rotation/parent — precisely the defect P-21 exists to prevent. Restoring the
            // previous value keeps the outer pose armed for the outer onGet, and still guarantees a
            // staged pose never outlives the outermost call (where `previous` is default).
            var previous = _pendingPlacement;

            _pendingPlacement = new PendingPlacement
            {
                Active = true,
                Position = position,
                Rotation = rotation,
                Parent = parent
            };

            try
            {
                var instance = pool.Get();
                if (instance != null) ApplyPendingPlacement(instance);
                return instance;
            }
            finally
            {
                // Never let THIS call's staged pose survive it — a throwing Get() would otherwise
                // teleport whatever the next Get() returns, from anywhere in the codebase.
                _pendingPlacement = previous;
            }
        }

        private void ApplyPendingPlacement(GameObject obj)
        {
            if (!_pendingPlacement.Active || obj == null) return;

            _pendingPlacement.Active = false;

            // Parent first, then the world pose: SetParent's default worldPositionStays keeps the
            // world transform, so the two orders are equivalent, and doing it this way means the
            // instance is never momentarily at the right place under the wrong parent.
            if (_pendingPlacement.Parent != null) obj.transform.SetParent(_pendingPlacement.Parent);
            obj.transform.SetPositionAndRotation(_pendingPlacement.Position, _pendingPlacement.Rotation);
        }

        /// <summary>
        /// Starts auto-create for <paramref name="address"/> if nothing is already in flight for it,
        /// or hands back the in-flight attempt's own completion source if there is one (P-1). Every
        /// caller — <c>Spawn()</c>'s fire-and-forget path and every concurrent <c>SpawnAsync()</c>
        /// call — shares exactly one underlying <c>CreatePoolAsync</c>/<c>CreateDynamicPoolAsync</c>
        /// call per address, so two Spawns for the same not-yet-pooled address never race each other
        /// into creating (and silently orphaning one of) two separate pools.
        /// </summary>
        private TaskCompletionSource<bool> StartOrJoinAutoCreate(string address)
        {
            if (_pendingAutoCreates.TryGetValue(address, out var existing))
            {
                return existing;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAutoCreates[address] = tcs;
            RunAutoCreate(address, tcs);
            return tcs;
        }

        // async void is intentional and safe here: this is the fire-and-forget half of P-1's
        // single-flight creation, kicked off from the synchronous Spawn(). It never needs its own
        // #if UNITASK_PRESENT branch (it returns void either way) — the CreateDynamicPoolAsync /
        // CreatePoolAsync call it awaits is already dual-signature, and `await` on either shape
        // compiles identically here.
        private async void RunAutoCreate(string address, TaskCompletionSource<bool> tcs)
        {
            bool created = false;

            // P-30: remember which "world" this creation belongs to. ClearAllPools bumps the epoch.
            int epoch = _teardownEpoch;

            try
            {
                created = _autoCreateDefaultConfig != null
                    ? await CreateDynamicPoolAsync(address, _autoCreateDefaultConfig, preloadCount: 0)
                    : await CreatePoolAsync(address, preloadCount: 0, maxSize: _autoCreateMaxSize);

                // CreateDynamicPoolAsync/CreatePoolAsync already hop back to the main thread
                // internally before returning, but awaiting their result is itself one more
                // resumption point that is not guaranteed to land on the same thread. Guard it too
                // before the finally below mutates _pendingAutoCreates — the same dictionary
                // Spawn()/SpawnAsync() read and write unsynchronized on the main thread.
                await EnsureMainThreadAsync();

                // P-30: a teardown ran while this was in flight. Dispose() is already safe (the
                // _disposed re-check inside the create methods catches it), but ClearAllPools() on a
                // LIVE manager was not: the pool and template handle this call just committed would
                // land after the teardown finished, leaving _pools and _templateHandles each holding
                // an entry that teardown never accounted for and that nothing would ever release.
                if (created && !_disposed && epoch != _teardownEpoch)
                {
                    Debug.LogWarning($"[PoolManager] Auto-create for {address} finished after a full " +
                        "teardown (ClearAllPools) started; discarding the pool it created so the " +
                        "teardown stays complete. Spawn again to build a fresh one.");

                    if (_pools.TryGetValue(address, out var strayPool))
                    {
                        TearDownPool(address, strayPool, destroyBorrowed: true);
                    }
                    created = false;
                }

                if (!created && !_disposed)
                {
                    Debug.LogError($"[PoolManager] Auto-create failed for {address}; Spawn()/SpawnAsync() " +
                        "will keep returning null for it until CreatePoolAsync/CreateDynamicPoolAsync succeeds.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Auto-create threw for {address}: {ex}");
            }
            finally
            {
                _pendingAutoCreates.Remove(address);
                tcs.TrySetResult(created);
            }
        }

        /// <summary>
        /// Return object to pool
        /// </summary>
        /// <remarks>
        /// P-7: verifies the instance actually belongs to the pool named by <paramref name="address"/>
        /// (via the tracking <see cref="TrackActive"/>/<see cref="UntrackActive"/> maintain) before
        /// calling <c>Release</c>, instead of hoping the underlying adapter happens to reject a
        /// mismatched or already-despawned instance — <c>UnityPoolAdapter</c> silently adopts a
        /// stranger's instance while <c>CustomPoolAdapter</c> warns-and-refuses, and only the latter
        /// catches a double-despawn. Both "wrong pool" and "already despawned" now behave
        /// identically regardless of adapter: log a warning and destroy the instance instead of
        /// releasing it.
        ///
        /// P-8: an instance that was destroyed outside this method is now untracked here instead of
        /// bailing out at the null guard. That guard used to be a trap door — Unity equality reports
        /// a destroyed <c>GameObject</c> as <c>null</c>, so the one call that could have cleaned the
        /// entry up returned before doing so, and the address could never be cleared again.
        ///
        /// P-9: the wrong-pool branch now also corrects the REAL owner's active count. Destroying a
        /// live instance out of another pool without telling that pool leaves it counting a phantom
        /// forever, which feeds the same auto-resize corruption P-8 describes. Whether destroying is
        /// even the right policy (versus routing to the real owner, or refusing outright) is an open
        /// product decision — see Documentation/LIFETIME_DESIGN.md.
        /// </remarks>
        public void Despawn(string address, GameObject instance)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot despawn on disposed manager");
                return;
            }

            if (ReferenceEquals(instance, null))
            {
                Debug.LogWarning("[PoolManager] Cannot despawn null instance");
                return;
            }

            // Unity equality, not reference equality: true for a GameObject whose native side is
            // already gone. That is a DIFFERENT situation from the genuine null above, and it is one
            // this manager still has books to settle for (P-8).
            if (instance == null)
            {
                if (ReclaimLostInstance(instance))
                {
                    Debug.LogWarning($"[PoolManager] The instance passed to Despawn(\"{address}\", ...) " +
                        "had already been destroyed elsewhere (a scene unload, or a direct " +
                        "Object.Destroy call instead of Despawn). Removed it from the borrowed set " +
                        "and corrected its pool's active count. Prefer Despawn over Object.Destroy: " +
                        "the pool cannot reuse an instance it never got back.");
                }
                else
                {
                    Debug.LogWarning($"[PoolManager] Cannot despawn an already-destroyed instance " +
                        $"into '{address}': this manager has no record of it.");
                }
                return;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] No pool found for {address}, destroying instance instead");
                UntrackInstanceEverywhere(instance);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            if (!_instanceOwners.TryGetValue(instance, out var ownerAddress) || ownerAddress != address)
            {
                string reason = ownerAddress != null
                    ? $"it belongs to pool '{ownerAddress}'"
                    : "it was never spawned by this manager, or has already been despawned once";
                Debug.LogWarning($"[PoolManager] Refusing to despawn instance into pool '{address}': " +
                    $"{reason}. Destroying it instead of risking a wrong-pool or double-release.");
                // The instance may still be tracked as active under ITS real owner (a different
                // address than the one this call passed) — untrack it there too, or that pool's
                // _activeInstances set would keep a stale entry pointing at a destroyed instance
                // forever, permanently blocking ClearPool for it (P-6). P-9: this also tells the
                // real owner's adapter to forget it, so that pool's activeCount comes back down
                // instead of counting a destroyed instance for the rest of the session.
                UntrackInstanceEverywhere(instance);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            pool.Release(instance);
        }

        /// <summary>
        /// Removes <paramref name="instance"/> from tracking under whichever address it is
        /// currently filed under, regardless of the address a caller asked to despawn it into, and
        /// tells that address's pool to drop it from its own active accounting (P-9). Used only on
        /// the reject-and-destroy paths in <see cref="Despawn"/>, where the instance is about to
        /// stop existing either way.
        /// </summary>
        private void UntrackInstanceEverywhere(GameObject instance)
        {
            if (ReferenceEquals(instance, null)) return;
            ReclaimLostInstance(instance);
        }

        /// <summary>
        /// Drops one instance out of this manager's tracking maps AND out of its owning pool's
        /// active accounting, without releasing it back onto a free list. Returns <c>false</c> when
        /// the instance was not tracked at all.
        /// </summary>
        /// <remarks>
        /// P-8/P-9. The pool half matters as much as the map half: an adapter that keeps counting a
        /// destroyed instance as active feeds <c>DynamicPool.CheckForGrowth</c> a ratio that only
        /// ever climbs, so the pool grows to <see cref="DynamicPoolConfig.MaxSize"/> and
        /// <c>CheckForShrinkage</c> can never fire again. A pool whose adapter does not implement
        /// <see cref="IReclaimablePool{T}"/> simply keeps the phantom — the manager's own maps are
        /// still cleaned, so <see cref="TryClearPool"/> stops being permanently blocked either way.
        /// </remarks>
        private bool ReclaimLostInstance(GameObject instance)
        {
            if (ReferenceEquals(instance, null)) return false;
            if (!_instanceOwners.TryGetValue(instance, out var owner)) return false;

            UntrackActive(owner, instance);

            if (_pools.TryGetValue(owner, out var pool) && pool is IReclaimablePool<GameObject> reclaimable)
            {
                reclaimable.ForgetActive(instance);
            }

            return true;
        }

        /// <summary>
        /// Removes every borrowed instance that has been destroyed out in the world from this
        /// manager's tracking maps and from its pool's active count, and returns how many were
        /// reclaimed (discovery report P-8).
        /// </summary>
        /// <remarks>
        /// <para>Called automatically on <see cref="SceneManager.sceneUnloaded"/>, on every
        /// <see cref="PoolMaintenancePump"/> tick, and immediately before
        /// <see cref="TryClearPool"/> decides whether to refuse. Public because a host that knows it
        /// just destroyed a batch of pooled objects can settle the books immediately instead of
        /// waiting for the next tick.</para>
        ///
        /// <para>Why this is needed at all: P-2 gave <i>pooled</i> instances a
        /// <c>DontDestroyOnLoad</c> root and taught both adapters to skip destroyed free-list
        /// entries, but <see cref="Spawn(string, Vector3, Quaternion, Transform)"/> explicitly
        /// reparents a <i>borrowed</i> instance to the caller's transform, and borrowed instances had
        /// no guard at all. The consequences were all permanent: <c>Despawn</c> bailed at its null
        /// guard so the entry could never be removed; <c>ClearPool</c> refused forever and the
        /// template handle was never released; and both dictionaries grew without bound across
        /// scene loads, because a destroyed <c>GameObject</c> is still a perfectly valid dictionary
        /// key (the managed wrapper keeps its instance id, so <c>GetHashCode</c> keeps working).</para>
        /// </remarks>
        public int SweepDestroyedInstances()
        {
            if (_disposed || _instanceOwners.Count == 0) return 0;

            List<GameObject> dead = null;
            foreach (var pair in _instanceOwners)
            {
                // Unity equality on a concrete GameObject: true once the native object is gone.
                if (pair.Key == null)
                {
                    if (dead == null) dead = new List<GameObject>();
                    dead.Add(pair.Key);
                }
            }

            if (dead == null) return 0;

            foreach (var instance in dead)
            {
                ReclaimLostInstance(instance);
            }

            Debug.LogWarning($"[PoolManager] Reclaimed {dead.Count} borrowed instance(s) that were " +
                "destroyed without going through Despawn(). Their pools' active counts have been " +
                "corrected. If this happens every scene load, something is calling Object.Destroy " +
                "on pooled objects (or letting a scene take them) instead of despawning them.");

            return dead.Count;
        }

        /// <summary>
        /// Advances every dynamic pool's auto-resize state machine (discovery report P-17). Cheap;
        /// see <see cref="DynamicPool{T}.EvaluateAutoResize"/> for what it can and cannot do.
        /// </summary>
        public void EvaluateAutoResize()
        {
            if (_disposed) return;

            foreach (var pool in _pools.Values)
            {
                if (pool is DynamicPool<GameObject> dynamicPool)
                {
                    dynamicPool.EvaluateAutoResize();
                }
            }
        }

        /// <summary>
        /// One maintenance pass: reclaim instances destroyed out from under their pools (P-8), then
        /// let idle pools shrink (P-17). Driven by <see cref="PoolMaintenancePump"/>; safe to call
        /// by hand from a host that would rather own the clock itself.
        /// </summary>
        public void RunMaintenance()
        {
            if (_disposed) return;

            SweepDestroyedInstances();
            EvaluateAutoResize();
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (_disposed) return;

            SweepDestroyedInstances();
        }

        private void EnsureMaintenancePump()
        {
            if (_disposed || _maintenancePump != null) return;

            // An AddressablePoolManager can be constructed by editor tooling; spawning a scene
            // object there would dirty the open scene and the Update would never run anyway.
            if (!Application.isPlaying) return;

            _maintenancePump = PoolMaintenancePump.Create(this);
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        public (int activeCount, int pooledCount)? GetPoolStats(string address)
        {
            if (_pools.TryGetValue(address, out var pool))
            {
                return pool.GetStats();
            }

            return null;
        }

        /// <summary>
        /// Sum of <see cref="GetPoolStats"/> across every pool this manager currently knows about.
        /// Consumers that only need a total (e.g. a dashboard tile) don't have to enumerate
        /// addresses themselves — there is no public way to do that today.
        /// </summary>
        public (int totalActive, int totalPooled) GetTotalStats()
        {
            int totalActive = 0;
            int totalPooled = 0;

            foreach (var pool in _pools.Values)
            {
                var (active, pooled) = pool.GetStats();
                totalActive += active;
                totalPooled += pooled;
            }

            return (totalActive, totalPooled);
        }

        /// <summary>
        /// Get dynamic pool statistics. Returns <c>null</c> if the pool doesn't exist or is not a
        /// dynamic pool.
        /// </summary>
        /// <remarks>
        /// P-24: <c>null</c> for a non-dynamic pool is deliberate rather than an oversight, and it is
        /// not the sentinel invariant 4 forbids — every field of <see cref="DynamicPoolStats"/>
        /// except <c>ActiveCount</c>/<c>PooledCount</c> (capacity budget, peak, min/max, shrink
        /// timers) describes machinery a plain pool does not have, so synthesising them would report
        /// numbers that mean nothing. Use <see cref="GetPoolStats"/> for the two figures that are
        /// real for every pool, and <see cref="IsDynamicPool"/> to tell the cases apart up front.
        /// </remarks>
        public DynamicPoolStats? GetDynamicPoolStats(string address)
        {
            if (_pools.TryGetValue(address, out var pool))
            {
                if (pool is DynamicPool<GameObject> dynamicPool)
                {
                    return dynamicPool.GetDynamicStats();
                }
            }

            return null;
        }

        /// <summary>
        /// Resize a pool towards <paramref name="targetCapacity"/>.
        /// </summary>
        /// <remarks>
        /// P-24: this used to refuse outright for anything that wasn't a <see cref="DynamicPool{T}"/>
        /// ("Pool X is not a dynamic pool, cannot resize"), even though both shipped adapters gained
        /// <see cref="IResizablePool{T}"/> in P-4 and could have done the work. They now do.
        ///
        /// The two meanings of <paramref name="targetCapacity"/> are necessarily different and both
        /// are honest: on a dynamic pool it is the auto-resize <i>budget</i>
        /// (<see cref="DynamicPool{T}.ResizeTo"/>, clamped to the config's min/max, which is what
        /// <c>DynamicPoolStats.CurrentCapacity</c> reports); on a plain pool there is no budget, so
        /// it is the target <i>total instance count</i> (borrowed + pooled). Shrinking can only
        /// evict instances that are actually in the free list, so a plain pool with more borrowed
        /// instances than the target lands above it — the log says what was achieved.
        /// </remarks>
        public void ResizePool(string address, int targetCapacity)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot resize on disposed manager");
                return;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] Pool {address} not found");
                return;
            }

            if (pool is DynamicPool<GameObject> dynamicPool)
            {
                dynamicPool.ResizeTo(targetCapacity);
                return;
            }

            if (!(pool is IResizablePool<GameObject>))
            {
                Debug.LogWarning($"[PoolManager] Pool {address} is neither a DynamicPool nor an " +
                    $"{nameof(IResizablePool<GameObject>)}, so it cannot be resized. Both pool " +
                    "adapters shipped with this package implement it; a custom IPoolFactory can too.");
                return;
            }

            var (active, pooled) = pool.GetStats();
            int total = active + pooled;
            int delta = targetCapacity - total;

            if (delta > 0)
            {
                int added = PreloadInto(pool, address, delta);
                Debug.Log($"[PoolManager] Resized pool {address} towards {targetCapacity}: " +
                    $"pooled {added} of the {delta} additional instance(s) requested.");
            }
            else if (delta < 0)
            {
                int removed = TrimFrom(pool, address, -delta);
                Debug.Log($"[PoolManager] Resized pool {address} towards {targetCapacity}: " +
                    $"destroyed {removed} of the {-delta} excess instance(s) " +
                    $"({active} still borrowed and therefore untouchable).");
            }
        }

        /// <summary>
        /// Create and pool <paramref name="count"/> additional instances for an existing pool,
        /// returning how many were actually added. The after-creation counterpart to
        /// <c>preloadCount</c>, which had no public equivalent at all (P-24).
        /// </summary>
        public int PrewarmPool(string address, int count)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot prewarm on disposed manager");
                return 0;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] Pool {address} not found");
                return 0;
            }

            return PreloadInto(pool, address, count);
        }

        /// <summary>
        /// Destroy up to <paramref name="count"/> pooled (never borrowed) instances of an existing
        /// pool, returning how many were actually destroyed (P-24).
        /// </summary>
        public int TrimPool(string address, int count)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot trim on disposed manager");
                return 0;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] Pool {address} not found");
                return 0;
            }

            return TrimFrom(pool, address, count);
        }

        /// <summary>
        /// Check if pool is a dynamic pool
        /// </summary>
        public bool IsDynamicPool(string address)
        {
            if (_pools.TryGetValue(address, out var pool))
            {
                return pool is DynamicPool<GameObject>;
            }
            return false;
        }

        /// <summary>
        /// Clear a specific pool. Prefer <see cref="TryClearPool"/>, which reports what happened —
        /// this overload discards that answer, and "refused, instances still borrowed" is a normal
        /// outcome, not an exceptional one.
        /// </summary>
        public void ClearPool(string address)
        {
            TryClearPool(address);
        }

        /// <summary>
        /// Clear a specific pool and say what happened. Refuses when instances spawned from it are
        /// still borrowed (P-6) — use <see cref="ClearAllPools"/> or <see cref="Dispose"/> for a
        /// teardown that should forcibly reclaim outstanding instances instead.
        /// </summary>
        /// <remarks>
        /// P-23: <see cref="ClearPool"/> returns <c>void</c>, so shutdown code that loops it over a
        /// list of addresses had no way to learn that every single call refused — memory was never
        /// freed and the calling code read as correct. That mattered far more than it looks, because
        /// P-8 made refusal the <i>normal</i> outcome for any address one of whose instances had ever
        /// been destroyed outside <c>Despawn</c>. Both halves are fixed: the sweep below removes the
        /// dead entries that caused the false refusal, and this method reports the real one.
        /// </remarks>
        public PoolClearOutcome TryClearPool(string address)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot clear a pool on a disposed manager");
                return PoolClearOutcome.ManagerDisposed;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] ClearPool: no pool found for {address}");
                return PoolClearOutcome.NoSuchPool;
            }

            // P-8: settle the books BEFORE deciding to refuse. Without this, one instance destroyed
            // by a scene unload three levels ago blocks this address for the rest of the session.
            SweepDestroyedInstances();

            if (_activeInstances.TryGetValue(address, out var active) && active.Count > 0)
            {
                Debug.LogWarning($"[PoolManager] ClearPool refused for {address}: {active.Count} " +
                    "instance(s) are still borrowed (not yet Despawn()'d). Despawn them first, or " +
                    "use ClearAllPools()/Dispose() to force a full teardown that destroys " +
                    "outstanding instances along with the pool.");
                return PoolClearOutcome.RefusedInstancesBorrowed;
            }

            Debug.Log($"[PoolManager] Clearing pool: {address}");
            TearDownPool(address, pool, destroyBorrowed: false);
            return PoolClearOutcome.Cleared;
        }

        /// <summary>
        /// Clear all pools. Unlike <see cref="TryClearPool"/>, this is a real teardown: any instance
        /// still borrowed from any pool is destroyed as part of it (P-6) — accounted for and
        /// reclaimed rather than orphaned pointing at a template whose reference is about to be
        /// released out from under it.
        /// </summary>
        public void ClearAllPools()
        {
            Debug.Log($"[PoolManager] Clearing all pools ({_pools.Count})");

            // P-30: everything created from here on belongs to a different world than the one being
            // torn down. An auto-create already in flight checks this against the epoch it captured
            // and discards its result rather than re-populating _pools behind the teardown's back.
            _teardownEpoch++;

            // ToList-equivalent: TearDownPool mutates _pools.
            var addresses = new List<string>(_pools.Keys);
            foreach (var address in addresses)
            {
                _pools.TryGetValue(address, out var pool);
                TearDownPool(address, pool, destroyBorrowed: true);
            }

            // Anything the per-address teardown could not see: tracking filed under an address whose
            // pool had already been removed, and templates/roots with no surviving pool entry.
            foreach (var set in _activeInstances.Values)
            {
                foreach (var instance in set)
                {
                    if (instance != null) UnityEngine.Object.Destroy(instance);
                }
            }
            _activeInstances.Clear();
            _instanceOwners.Clear();
            _pools.Clear();

            foreach (var template in _templateHandles.Values)
            {
                template?.Dispose();
            }
            _templateHandles.Clear();

            // P-29: the per-address "[Pools]/<address>" holders are ours; they do not survive a
            // teardown either.
            foreach (var root in _addressRoots.Values)
            {
                if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            }
            _addressRoots.Clear();
        }

        /// <summary>
        /// Tears one address all the way down: borrowed instances (optionally), tracking, the pool
        /// itself, the template reference, and the per-address root GameObject.
        /// </summary>
        /// <remarks>
        /// P-29: destroying the address root is the part that used to be missing. <c>ClearPool</c> and
        /// <c>ClearAllPools</c> removed the pool but left <c>[Pools]/&lt;address&gt;</c> standing and
        /// left its entry in <c>_addressRoots</c>, so a create/clear cycle across many addresses
        /// accumulated empty GameObjects and dictionary entries that only <see cref="Dispose"/> ever
        /// cleaned up. Safe because nothing live is parented under it at this point: the refusal in
        /// <see cref="TryClearPool"/> guarantees no borrowed instance remains, and the teardown path
        /// destroys them first.
        /// </remarks>
        private void TearDownPool(string address, IObjectPool<GameObject> pool, bool destroyBorrowed)
        {
            if (_activeInstances.TryGetValue(address, out var borrowed))
            {
                foreach (var instance in borrowed)
                {
                    if (destroyBorrowed && instance != null) UnityEngine.Object.Destroy(instance);
                    _instanceOwners.Remove(instance);
                }
                _activeInstances.Remove(address);
            }

            pool?.Clear();
            pool?.Dispose();
            _pools.Remove(address);

            ReleaseTemplate(address);
            DestroyAddressRoot(address);
        }

        private void DestroyAddressRoot(string address)
        {
            if (!_addressRoots.TryGetValue(address, out var root)) return;

            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            _addressRoots.Remove(address);
        }

        public void Dispose()
        {
            if (_disposed) return;

            SceneManager.sceneUnloaded -= _sceneUnloadedHandler;

            if (_maintenancePump != null) _maintenancePump.Shutdown();
            _maintenancePump = null;

            ClearAllPools();
            _disposed = true;

            if (_poolsRoot != null)
            {
                UnityEngine.Object.Destroy(_poolsRoot.gameObject);
                _poolsRoot = null;
            }
            _addressRoots.Clear();
        }

        /// <summary>
        /// P-5: releases only the reference this manager itself is holding on the template handle —
        /// the one <c>AssetLoader.LoadAssetAsync</c> handed back when the pool was created.
        /// </summary>
        /// <remarks>
        /// <c>AssetLoader.CacheHandle</c> retains a SECOND, independent reference for its own cache
        /// the moment the load completes (see its docstring), so disposing this one always leaves
        /// the prefab bundle resident in the loader's cache — by design, not by leak. That is what
        /// lets a pool recreated for the same address after <see cref="TryClearPool"/> load instantly
        /// instead of re-downloading/re-decompressing the prefab. If a caller actually wants the
        /// prefab evicted from memory, that is <c>AssetLoader.ClearCache()</c>'s job, not this
        /// manager's — this class only ever owns the one reference it was handed, never the cache's.
        /// (An earlier draft of this fix considered retaining a second reference here to release
        /// unconditionally; that would silently leak permanently instead, because it would still
        /// only ever release ONE of the two references the handle carries. See
        /// HANDOFF_TO_SESSION_B.md P-5.)
        /// </remarks>
        private void ReleaseTemplate(string address)
        {
            if (_templateHandles.TryGetValue(address, out var template))
            {
                template?.Dispose();
                _templateHandles.Remove(address);
            }
        }

        /// <summary>
        /// Adds <paramref name="count"/> instances to <paramref name="pool"/>'s free list and returns
        /// how many actually landed there (P-27).
        /// </summary>
        /// <remarks>
        /// Probes for the strongest capability the pool has, in order: measured prewarm, plain
        /// prewarm plus a stats delta, and finally — for a third-party <see cref="IObjectPool{T}"/>
        /// that predates <see cref="IResizablePool{T}"/> — Get the full N first and THEN Release all
        /// N (P-3's shape: a Get/Release pair per iteration just pops back the instance the previous
        /// iteration returned, and creates one instance total).
        ///
        /// P-13: failures are logged and swallowed here so a partial preload cannot make pool
        /// creation report failure for a pool that exists and works. The count that comes back is
        /// measured after the fact either way, so a partial result is reported as a partial result.
        /// </remarks>
        private int PreloadInto(IObjectPool<GameObject> pool, string address, int count)
        {
            if (pool == null || count <= 0) return 0;

            int before = pool.GetStats().pooledCount;

            try
            {
                if (pool is IMeasuredResizablePool<GameObject> measured)
                {
                    return measured.PrewarmMeasured(count);
                }

                if (pool is IResizablePool<GameObject> resizable)
                {
                    resizable.Prewarm(count);
                }
                else
                {
                    var borrowed = new List<GameObject>(count);
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var instance = pool.Get();
                            if (instance != null) borrowed.Add(instance);
                        }
                    }
                    finally
                    {
                        foreach (var instance in borrowed) pool.Release(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Preloading '{address}' stopped early: {ex}");
            }

            return Mathf.Max(0, pool.GetStats().pooledCount - before);
        }

        /// <summary>
        /// Destroys up to <paramref name="count"/> pooled instances and returns how many actually
        /// went (P-27). Never touches a borrowed instance.
        /// </summary>
        private int TrimFrom(IObjectPool<GameObject> pool, string address, int count)
        {
            if (pool == null || count <= 0) return 0;

            int before = pool.GetStats().pooledCount;

            try
            {
                if (pool is IMeasuredResizablePool<GameObject> measured)
                {
                    return measured.TrimExcessMeasured(count);
                }

                if (pool is IResizablePool<GameObject> resizable)
                {
                    resizable.TrimExcess(count);
                }
                else
                {
                    Debug.LogWarning($"[PoolManager] Pool {address} does not implement " +
                        $"{nameof(IResizablePool<GameObject>)}; there is no way to evict a specific " +
                        "pooled instance through IObjectPool<T> alone without corrupting its " +
                        "active/pooled accounting, so nothing was trimmed (see P-4).");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Trimming '{address}' stopped early: {ex}");
            }

            return Mathf.Max(0, before - pool.GetStats().pooledCount);
        }

        private GameObject CreateInstance(GameObject prefab, Transform parent)
        {
            var instance = UnityEngine.Object.Instantiate(prefab, parent);
            instance.SetActive(false);
            return instance;
        }

        /// <summary>
        /// Resolves the parent instances are created under when the caller didn't supply one
        /// (P-2). Lazily builds a <c>DontDestroyOnLoad</c> "[Pools]" root the first time any pool
        /// needs it, with one child transform per address, so pooled instances share this manager's
        /// lifetime instead of whichever scene happened to be active at creation time. A
        /// caller-supplied <paramref name="poolRoot"/> is always honored as-is instead.
        /// </summary>
        private Transform ResolvePoolRoot(string address, Transform poolRoot)
        {
            if (poolRoot != null) return poolRoot;

            if (_poolsRoot == null)
            {
                var rootGo = new GameObject("[Pools]");
                UnityEngine.Object.DontDestroyOnLoad(rootGo);
                _poolsRoot = rootGo.transform;
            }

            if (!_addressRoots.TryGetValue(address, out var addressRoot) || addressRoot == null)
            {
                var addressGo = new GameObject(address);
                addressGo.transform.SetParent(_poolsRoot, false);
                addressRoot = addressGo.transform;
                _addressRoots[address] = addressRoot;
            }

            return addressRoot;
        }

        /// <summary>
        /// The parent a pool's instances live under, re-resolved on demand (discovery report P-16).
        /// </summary>
        /// <remarks>
        /// The root used to be a plain captured local shared by a pool's <c>createFunc</c> and its
        /// <c>onRelease</c> closure, dereferenced unguarded as
        /// <c>obj.transform.SetParent(effectiveRoot)</c>. P-2 documented the caller-supplied-poolRoot
        /// trap for pooled <i>instances</i>, but the closure itself was never hardened:
        /// <c>CreatePoolAsync("X", poolRoot: sceneTransform)</c> followed by a
        /// <c>LoadSceneMode.Single</c> load leaves the pool alive (the manager lives on the
        /// <c>DontDestroyOnLoad</c> facade) with a destroyed <c>Transform</c>, and the next
        /// <c>Despawn</c> throws <see cref="MissingReferenceException"/> from inside the adapter —
        /// past the manager's own <c>if (obj == null)</c> guard, which is checking the wrong object.
        ///
        /// Holding it in one shared object rather than duplicating the check in each closure means
        /// <c>createFunc</c> and <c>onRelease</c> can never disagree about where instances belong.
        /// </remarks>
        private sealed class PoolRootBinding
        {
            private readonly AddressablePoolManager _owner;
            private readonly string _address;
            private Transform _root;
            private bool _warned;

            public PoolRootBinding(AddressablePoolManager owner, string address, Transform root)
            {
                _owner = owner;
                _address = address;
                _root = root;
            }

            public Transform Resolve()
            {
                if (_root != null) return _root;

                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"[PoolManager] The pool root for '{_address}' was destroyed " +
                        "(most likely by a scene unload taking a caller-supplied poolRoot with it). " +
                        "Falling back to this manager's own DontDestroyOnLoad root for every " +
                        "instance from now on. Pass poolRoot: null at creation to avoid this " +
                        "entirely — see CreatePoolAsync's docs.");
                }

                // Passing null forces the DontDestroyOnLoad path, which also rebuilds the
                // per-address holder if that was what got destroyed.
                _root = _owner.ResolvePoolRoot(_address, null);
                return _root;
            }
        }

        private void TrackActive(string address, GameObject instance)
        {
            if (instance == null) return;

            if (!_activeInstances.TryGetValue(address, out var set))
            {
                set = new HashSet<GameObject>();
                _activeInstances[address] = set;
            }
            set.Add(instance);
            _instanceOwners[instance] = address;
        }

        private void UntrackActive(string address, GameObject instance)
        {
            // ReferenceEquals, not Unity equality: a destroyed instance is precisely the one that
            // most needs removing from these maps (P-8), and `instance == null` would skip it.
            if (ReferenceEquals(instance, null)) return;

            if (_activeInstances.TryGetValue(address, out var set))
            {
                set.Remove(instance);
            }
            _instanceOwners.Remove(instance);
        }
    }
}
