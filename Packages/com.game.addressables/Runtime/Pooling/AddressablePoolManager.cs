using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
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
    /// Manages object pools for Addressable assets
    /// Supports runtime factory switching for different pooling implementations
    /// </summary>
    public class AddressablePoolManager : IDisposable
    {
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

        // P-1: one in-flight auto-create per address, shared by every synchronous Spawn() call and
        // every SpawnAsync() call that arrives while it is still running. TaskCompletionSource<bool>
        // is plain BCL, so it can be awaited from either a UniTask<T>- or Task<T>-returning async
        // method without needing its own #if UNITASK_PRESENT branch.
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingAutoCreates =
            new Dictionary<string, TaskCompletionSource<bool>>();

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
        private readonly Dictionary<string, HashSet<GameObject>> _activeInstances =
            new Dictionary<string, HashSet<GameObject>>();
        private readonly Dictionary<GameObject, string> _instanceOwners =
            new Dictionary<GameObject, string>();

        public AddressablePoolManager(AssetLoader loader, IPoolFactory poolFactory = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _poolFactory = poolFactory ?? new UnityPoolFactory(); // Default to Unity's pool
            _pools = new Dictionary<string, IObjectPool<GameObject>>();
            _templateHandles = new Dictionary<string, IDisposable>();
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
        /// <param name="defaultConfig">Default config for auto-created pools (null = use static config)</param>
        public void EnableAutoCreatePools(DynamicPoolConfig defaultConfig = null)
        {
            _autoCreatePoolsEnabled = true;
            _autoCreateDefaultConfig = defaultConfig;
            Debug.Log("[PoolManager] Auto-create pools enabled");
        }

        /// <summary>
        /// Disable automatic pool creation
        /// </summary>
        public void DisableAutoCreatePools()
        {
            _autoCreatePoolsEnabled = false;
            _autoCreateDefaultConfig = null;
            Debug.Log("[PoolManager] Auto-create pools disabled");
        }

        /// <summary>
        /// Check if auto-create is enabled
        /// </summary>
        public bool IsAutoCreateEnabled => _autoCreatePoolsEnabled;

        /// <summary>
        /// Switch pool factory at runtime (e.g., from Unity pool to Zenject pool)
        /// </summary>
        public void SetPoolFactory(IPoolFactory factory)
        {
            if (factory == null)
            {
                Debug.LogError("[PoolManager] Cannot set null factory");
                return;
            }

            Debug.Log($"[PoolManager] Switching pool factory to {factory.GetType().Name}");
            _poolFactory = factory;

            // Note: Existing pools won't be affected, only new pools will use new factory
        }

        /// <summary>
        /// Create a dynamic pool for an addressable prefab with auto-sizing.
        /// </summary>
        /// <param name="poolRoot">
        /// Parent for created instances. Leave <c>null</c> to use this manager's own
        /// <c>DontDestroyOnLoad</c> root (recommended) — passing a scene-local transform here
        /// reproduces HANDOFF_TO_SESSION_B.md P-2: pooled instances would be destroyed by the next
        /// <c>LoadSceneMode.Single</c> load while this manager still believes they're pooled.
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
                var effectiveRoot = ResolvePoolRoot(address, poolRoot);

                // Create base pool using current factory
                var basePool = _poolFactory.CreatePool<GameObject>(
                    createFunc: () => CreateInstance(prefab, effectiveRoot),
                    onGet: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(true);
                        TrackActive(address, obj);
                    },
                    onRelease: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(false);
                        obj.transform.SetParent(effectiveRoot);
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
                    createFunc: () => CreateInstance(prefab, effectiveRoot),
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
                Debug.Log($"[PoolManager] Dynamic pool created for {address} " +
                         $"(capacity: {config.InitialCapacity}, range: [{config.MinSize}-{config.MaxSize}])");

                // Preload instances. Routes through DynamicPool.Prewarm (P-4's primitive) instead of a
                // Get/Release loop, so preloading doesn't pollute the peak-active tracking that drives
                // auto-resize (P-3).
                if (preloadCount > 0)
                {
                    dynamicPool.Prewarm(preloadCount);
                    Debug.Log($"[PoolManager] Preloaded {preloadCount} instances for {address}");
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
                return false;
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
            int maxSize = 100,
            Transform poolRoot = null)
#else
        public async Task<bool> CreatePoolAsync(
            string address,
            int preloadCount = 0,
            int maxSize = 100,
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
                var effectiveRoot = ResolvePoolRoot(address, poolRoot);

                // Create pool using current factory
                var pool = _poolFactory.CreatePool<GameObject>(
                    createFunc: () => CreateInstance(prefab, effectiveRoot),
                    onGet: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(true);
                        TrackActive(address, obj);
                    },
                    onRelease: (obj) =>
                    {
                        if (obj == null) return;
                        obj.SetActive(false);
                        obj.transform.SetParent(effectiveRoot);
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
                Debug.Log($"[PoolManager] Pool created for {address} with max size {maxSize}");

                // Preload instances. Get() the full N first, THEN Release() all N — every Get() here
                // slides past the free list (which starts empty) and calls createFunc, instead of the
                // old Get/Release-per-iteration loop where every Get() after the first just popped the
                // one instance the previous iteration had already returned (P-3).
                if (preloadCount > 0)
                {
                    var preloaded = new List<GameObject>(preloadCount);
                    for (int i = 0; i < preloadCount; i++)
                    {
                        var instance = pool.Get();
                        if (instance != null) preloaded.Add(instance);
                    }
                    foreach (var instance in preloaded)
                    {
                        pool.Release(instance);
                    }
                    Debug.Log($"[PoolManager] Preloaded {preloaded.Count}/{preloadCount} instances for {address}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoolManager] Exception creating pool for {address}: {ex}");
                if (!templateCommitted) handle?.Release();
                return false;
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

            var instance = pool.Get();
            if (instance != null)
            {
                instance.transform.SetPositionAndRotation(position, rotation);
                if (parent != null) instance.transform.SetParent(parent);
            }

            return instance;
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

            var instance = pool.Get();
            if (instance != null)
            {
                instance.transform.SetPositionAndRotation(position, rotation);
                if (parent != null) instance.transform.SetParent(parent);
            }

            return instance;
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
            try
            {
                created = _autoCreateDefaultConfig != null
                    ? await CreateDynamicPoolAsync(address, _autoCreateDefaultConfig, preloadCount: 0)
                    : await CreatePoolAsync(address, preloadCount: 0, maxSize: 50);

                // CreateDynamicPoolAsync/CreatePoolAsync already hop back to the main thread
                // internally before returning, but awaiting their result is itself one more
                // resumption point that is not guaranteed to land on the same thread. Guard it too
                // before the finally below mutates _pendingAutoCreates — the same dictionary
                // Spawn()/SpawnAsync() read and write unsynchronized on the main thread.
                await EnsureMainThreadAsync();

                if (!created)
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
        /// </remarks>
        public void Despawn(string address, GameObject instance)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot despawn on disposed manager");
                return;
            }

            if (instance == null)
            {
                Debug.LogWarning("[PoolManager] Cannot despawn null instance");
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
                // forever, permanently blocking ClearPool for it (P-6).
                UntrackInstanceEverywhere(instance);
                UnityEngine.Object.Destroy(instance);
                return;
            }

            pool.Release(instance);
        }

        /// <summary>
        /// Removes <paramref name="instance"/> from tracking under whichever address it is
        /// currently filed under, regardless of the address a caller asked to despawn it into. Used
        /// only on the reject-and-destroy paths in <see cref="Despawn"/>, where the instance is about
        /// to stop existing either way.
        /// </summary>
        private void UntrackInstanceEverywhere(GameObject instance)
        {
            if (instance == null) return;
            if (_instanceOwners.TryGetValue(instance, out var owner))
            {
                UntrackActive(owner, instance);
            }
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
        /// Get dynamic pool statistics (if pool is dynamic)
        /// Returns null if pool doesn't exist or is not a dynamic pool
        /// </summary>
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
        /// Force a dynamic pool to resize to target capacity
        /// No effect on non-dynamic pools
        /// </summary>
        public void ResizePool(string address, int targetCapacity)
        {
            if (_pools.TryGetValue(address, out var pool))
            {
                if (pool is DynamicPool<GameObject> dynamicPool)
                {
                    dynamicPool.ResizeTo(targetCapacity);
                }
                else
                {
                    Debug.LogWarning($"[PoolManager] Pool {address} is not a dynamic pool, cannot resize");
                }
            }
            else
            {
                Debug.LogWarning($"[PoolManager] Pool {address} not found");
            }
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
        /// Clear a specific pool. Refuses when instances spawned from it are still borrowed (P-6) —
        /// use <see cref="ClearAllPools"/> or <see cref="Dispose"/> for a teardown that should
        /// forcibly reclaim outstanding instances instead.
        /// </summary>
        public void ClearPool(string address)
        {
            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogWarning($"[PoolManager] ClearPool: no pool found for {address}");
                return;
            }

            if (_activeInstances.TryGetValue(address, out var active) && active.Count > 0)
            {
                Debug.LogWarning($"[PoolManager] ClearPool refused for {address}: {active.Count} " +
                    "instance(s) are still borrowed (not yet Despawn()'d). Despawn them first, or " +
                    "use ClearAllPools()/Dispose() to force a full teardown that destroys " +
                    "outstanding instances along with the pool.");
                return;
            }

            Debug.Log($"[PoolManager] Clearing pool: {address}");
            pool.Clear();
            pool.Dispose();
            _pools.Remove(address);
            _activeInstances.Remove(address);

            ReleaseTemplate(address);
        }

        /// <summary>
        /// Clear all pools. Unlike <see cref="ClearPool"/>, this is a real teardown: any instance
        /// still borrowed from any pool is destroyed as part of it (P-6) — accounted for and
        /// reclaimed rather than orphaned pointing at a template whose reference is about to be
        /// released out from under it.
        /// </summary>
        public void ClearAllPools()
        {
            Debug.Log($"[PoolManager] Clearing all pools ({_pools.Count})");

            // P-6: account for every borrowed instance BEFORE releasing anything it depends on.
            foreach (var active in _activeInstances.Values)
            {
                foreach (var instance in active)
                {
                    if (instance != null) UnityEngine.Object.Destroy(instance);
                }
            }
            _activeInstances.Clear();
            _instanceOwners.Clear();

            foreach (var pool in _pools.Values)
            {
                pool?.Clear();
                pool?.Dispose();
            }
            _pools.Clear();

            // Template release comes last, and only after every instance that depended on it —
            // borrowed or still pooled — has been accounted for above and by pool.Clear().
            foreach (var template in _templateHandles.Values)
            {
                template?.Dispose();
            }
            _templateHandles.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;

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
        /// lets a pool recreated for the same address after <see cref="ClearPool"/> load instantly
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
            if (instance == null) return;

            if (_activeInstances.TryGetValue(address, out var set))
            {
                set.Remove(instance);
            }
            _instanceOwners.Remove(instance);
        }
    }
}
