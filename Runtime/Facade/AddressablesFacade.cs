using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Loaders;
using AddressableManager.Core;
using AddressableManager.Managers;
using AddressableManager.Pooling;
using AddressableManager.Pooling.Adapters;
using AddressableManager.Progress;
using AddressableManager.Scopes;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Facade
{
    /// <summary>
    /// High-level facade providing unified access to all addressable systems
    /// Simplifies common operations and hides complexity
    /// </summary>
    public class AddressablesFacade : MonoBehaviour
    {
        private static AddressablesFacade _instance;

        // Built-in singleton scope used as the package's default cache root.
        private GlobalAssetScope _globalScope;

        // Session is no longer a dedicated class — it's a named entry in ScopeManager.
        // The Facade keeps the existing StartSession/EndSession/LoadSessionAsync ergonomic
        // by routing through ScopeManager.Instance.GetOrCreateScope(SessionScopeId).
        private const string SessionScopeId = "Session";
        private AssetLoader _sessionLoader;

        // Last scene scope the Facade was asked to resolve, cached for reuse.
        private SceneAssetScope _sceneScope;

        // Pooling. _poolLoader is a dedicated loader owned and disposed by this Facade — NOT
        // GlobalAssetScope's loader. Sharing GlobalAssetScope's loader here used to mean every
        // template handle CreatePoolAsync/CreateDynamicPoolAsync cached (_templateHandles) lived
        // in the exact same AssetLoader that Simple.ClearAll/Standard.ClearGlobalCache/
        // ScopeManager.ClearAll(Except)'s new "Global" foreign entry (A-7) can ClearCache() —
        // which force-releases unconditionally. Any of those bulk-clears would silently yank the
        // prefab reference out from under a live pool's create closures with the pool manager
        // never notified (HANDOFF_TO_SESSION_B.md §4.3 review finding). A dedicated loader that
        // nothing else ever registers or clears removes the collision entirely.
        private AssetLoader _poolLoader;
        private AddressablePoolManager _poolManager;

        public static AddressablesFacade Instance
        {
            get
            {
                // Do not build a new DontDestroyOnLoad GameObject once the process is shutting
                // down — mirrors GlobalAssetScope.Instance's guard (LIFETIME_DESIGN.md §3.6). This
                // is the single most-used entry point in the package: nearly every Simple.*/
                // Standard.* call funnels through it, so any of them called from another object's
                // OnDestroy during quit (a pooled object's own teardown, chief among them) would
                // otherwise resurrect the Facade mid-teardown, which Unity reports as a leaked
                // GameObject. Callers that may run during quit should check HasInstance first.
                if (_instance == null && !AddressableRuntime.IsShuttingDown)
                {
                    var go = new GameObject("[AddressablesFacade]");
                    _instance = go.AddComponent<AddressablesFacade>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>
        /// True when an instance already exists and may be used right now — the non-sentinel way
        /// to ask "would Instance hand me something real" without risking the side effect of
        /// building one. Mirrors <see cref="GlobalAssetScope.HasInstance"/> (LIFETIME_DESIGN.md
        /// §3.6).
        /// </summary>
        public static bool HasInstance => _instance != null && !AddressableRuntime.IsShuttingDown;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            Initialize();
        }

        private void Initialize()
        {
            // Initialize global scope
            _globalScope = GlobalAssetScope.Instance;
            if (_globalScope == null)
            {
                // Only reachable if the process starts shutting down in the narrow window between
                // this Facade's own Instance getter guard passing and this call — GlobalAssetScope
                // .Instance is guarded the very same way. Tolerate it rather than NullReferencing:
                // there is nothing useful to finish initializing this late anyway
                // (HANDOFF_TO_SESSION_B.md §4.3 review finding).
                Debug.LogWarning("[AddressablesFacade] GlobalAssetScope unavailable (process is " +
                                  "shutting down) — skipping pool manager setup.");
                return;
            }

            // Pool manager gets its own dedicated loader — see the field comment on _poolLoader
            // for why it must not share GlobalAssetScope's.
            _poolLoader = new AssetLoader("Pool");
            _poolManager = new AddressablePoolManager(_poolLoader, new UnityPoolFactory());

            Debug.Log("[AddressablesFacade] Initialized");
        }

        #region Global Scope Operations

        /// <summary>
        /// Load asset into global scope (persistent).
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadGlobalAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadGlobalAsync<T>(string address)
#endif
        {
            return await _globalScope.Loader.LoadAssetAsync<T>(address);
        }

        /// <summary>
        /// Load with progress tracking
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadGlobalWithProgressAsync<T>(string address, Action<ProgressInfo> onProgress)
#else
        public async Task<IAssetHandle<T>> LoadGlobalWithProgressAsync<T>(string address, Action<ProgressInfo> onProgress)
#endif
        {
            return await _globalScope.Loader.LoadAssetWithProgressAsync<T>(address, onProgress);
        }

        #endregion

        #region Session Scope Operations

        /// <summary>
        /// Start a new session. Idempotent — calling it twice returns the same
        /// ScopeManager-backed loader (use <see cref="EndSession"/> first to
        /// drop the existing session).
        /// </summary>
        public void StartSession()
        {
            _sessionLoader = ScopeManager.Instance.GetOrCreateScope(SessionScopeId);
            Debug.Log("[AddressablesFacade] Session started (ScopeManager-backed)");
        }

        /// <summary>
        /// End the current session — releases every asset loaded into the
        /// <c>Session</c> ScopeManager entry.
        /// </summary>
        public void EndSession()
        {
            if (ScopeManager.Instance.HasScope(SessionScopeId))
            {
                ScopeManager.Instance.ClearScope(SessionScopeId);
                Debug.Log("[AddressablesFacade] Session ended");
            }
            _sessionLoader = null;
        }

        /// <summary>
        /// Load asset into the session scope. Auto-starts the session if not
        /// already active so call sites don't have to remember to call
        /// <see cref="StartSession"/> first.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadSessionAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadSessionAsync<T>(string address)
#endif
        {
            if (_sessionLoader == null)
            {
                _sessionLoader = ScopeManager.Instance.GetOrCreateScope(SessionScopeId);
            }
            return await _sessionLoader.LoadAssetAsync<T>(address);
        }

        #endregion

        #region Scene Scope Operations

        /// <summary>
        /// Get or create scene scope for current scene
        /// </summary>
        public SceneAssetScope GetOrCreateSceneScope()
        {
            _sceneScope = SceneAssetScope.GetOrCreate();
            return _sceneScope;
        }

        /// <summary>
        /// Load asset into scene scope
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadSceneAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadSceneAsync<T>(string address)
#endif
        {
            var scope = GetOrCreateSceneScope();
            return await scope.Loader.LoadAssetAsync<T>(address);
        }

        #endregion

        #region Pooling Operations

        /// <summary>
        /// Create object pool
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<bool> CreatePoolAsync(string address, int preloadCount = 0, int maxSize = 100)
#else
        public async Task<bool> CreatePoolAsync(string address, int preloadCount = 0, int maxSize = 100)
#endif
        {
            return await _poolManager.CreatePoolAsync(address, preloadCount, maxSize);
        }

        /// <summary>
        /// Spawn from pool
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            return _poolManager.Spawn(address, position, rotation, parent);
        }

        /// <summary>
        /// Spawn at position (default rotation)
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Transform parent = null)
        {
            return _poolManager.Spawn(address, position, parent);
        }

        /// <summary>
        /// Spawn at origin
        /// </summary>
        public GameObject Spawn(string address, Transform parent = null)
        {
            return _poolManager.Spawn(address, parent);
        }

        /// <summary>
        /// Despawn to pool
        /// </summary>
        public void Despawn(string address, GameObject instance)
        {
            _poolManager.Despawn(address, instance);
        }

        /// <summary>
        /// Switch pool factory (e.g., to custom implementation)
        /// </summary>
        public void SetPoolFactory(IPoolFactory factory)
        {
            _poolManager.SetPoolFactory(factory);
        }

        /// <summary>
        /// Get statistics for a specific pool.
        /// </summary>
        public (int activeCount, int pooledCount)? GetPoolStats(string address)
            => _poolManager?.GetPoolStats(address);

        /// <summary>
        /// Clear a single pool by address (releases template handle + destroys pooled instances).
        /// </summary>
        public void ClearPool(string address) => _poolManager?.ClearPool(address);

        #endregion

        #region Accessors

        /// <summary>
        /// Direct access to the global scope's loader for advanced operations
        /// (e.g. <c>ReleaseInstance</c>, custom monitoring).
        /// </summary>
        public AssetLoader GlobalLoader => _globalScope?.Loader;

        #endregion

        #region Utility

        /// <summary>
        /// Get download size for address
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<long> GetDownloadSizeAsync(string address)
#else
        public async Task<long> GetDownloadSizeAsync(string address)
#endif
        {
            // Deliberate forward within the deprecated download surface (task 3.10): this facade
            // method is itself part of what CdnManager.GetDownloadSizeAsync replaces, and both go
            // away together in 5.0.0. Suppressed so consumers of the package do not see a warning
            // pointing at package-internal code they cannot change.
#pragma warning disable CS0618
            return await _globalScope.Loader.GetDownloadSizeAsync(address);
#pragma warning restore CS0618
        }

        /// <summary>
        /// Download dependencies with progress
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<bool> DownloadAsync(string address, Action<ProgressInfo> onProgress = null)
#else
        public async Task<bool> DownloadAsync(string address, Action<ProgressInfo> onProgress = null)
#endif
        {
            return await ProgressiveAssetLoader.DownloadWithProgressAsync(address, onProgress);
        }

        /// <summary>
        /// Clear all global cache
        /// </summary>
        public void ClearGlobalCache()
        {
            _globalScope?.Loader?.ClearCache();
        }

        /// <summary>
        /// Clear all pools
        /// </summary>
        public void ClearAllPools()
        {
            _poolManager?.ClearAllPools();
        }

        /// <summary>
        /// Get pool manager for advanced operations
        /// </summary>
        public AddressablePoolManager GetPoolManager()
        {
            return _poolManager;
        }

        /// <summary>
        /// Get global scope — storage "A" in the package's storage map (see
        /// <see cref="GlobalAssetScope"/>'s class docs and Documentation/LIFETIME_DESIGN.md §5
        /// step 6 / Documentation/HANDOFF_TO_SESSION_B.md A-12). NOT the same cache as
        /// <c>HybridScope.Global</c> (storage "D", reached through <c>Advanced.GetHybridGlobalScope()</c>)
        /// despite the similar name.
        /// </summary>
        public GlobalAssetScope GetGlobalScope()
        {
            return _globalScope;
        }

        /// <summary>
        /// Get the session's <see cref="AssetLoader"/> (the ScopeManager entry
        /// keyed by <c>"Session"</c> — storage "B" in the package's storage map; see
        /// <see cref="GlobalAssetScope"/>'s class docs). Returns null when no session is active.
        /// NOT the same cache as <c>HybridScope.Session</c> (storage "D", reached through
        /// <c>Advanced.GetHybridSessionScope()</c>) despite the similar name.
        /// Replaces the pre-4.0 <c>GetSessionScope()</c> method that returned
        /// the now-removed <c>SessionAssetScope</c>.
        /// </summary>
        public AssetLoader GetSessionLoader()
        {
            return _sessionLoader ?? ScopeManager.Instance.GetScope(SessionScopeId);
        }

        /// <summary>
        /// True when a session loader has been created (via
        /// <see cref="StartSession"/> or <see cref="LoadSessionAsync{T}"/>).
        /// </summary>
        public bool IsSessionActive()
        {
            return _sessionLoader != null || ScopeManager.Instance.HasScope(SessionScopeId);
        }

        /// <summary>
        /// Clear the session loader's cache without ending the session.
        /// </summary>
        public void ClearSessionCache()
        {
            (_sessionLoader ?? ScopeManager.Instance.GetScope(SessionScopeId))?.ClearCache();
        }

        #endregion

        private void OnDestroy()
        {
            // A duplicate Facade rejected in Awake (`_instance != this`) never ran
            // Initialize(): _poolManager is null (the Dispose() calls below would be
            // no-ops for it regardless), and it must not touch process-wide singleton
            // state (ScopeManager's "Session" entry, GlobalAssetScope) that the real,
            // live Facade still owns. So EVERYTHING that follows is gated on being the
            // owning instance — nothing outside this guard may touch static/singleton
            // state (HANDOFF_TO_SESSION_B.md A-1).
            if (_instance == this)
            {
                // Tear down in dependency order: pools own template handles loaded via
                // the pool loader, so dispose the pool manager (which releases its own
                // handles) before disposing the loader itself, then the session.
                _poolManager?.Dispose();
                _poolManager = null;
                _poolLoader?.Dispose();
                _poolLoader = null;

                // Session is a ScopeManager entry — clear it explicitly so its loader disposes.
                EndSession();

                // GlobalAssetScope is a separate, process-wide singleton with its own
                // GameObject and DontDestroyOnLoad lifetime — it is consumed directly
                // by other systems too (SimpleAPI.cs, GlobalAssetScopeInspector.cs),
                // not only through this Facade. The Facade is a *consumer* of it, not
                // its owner: calling _globalScope.Dispose() here nulls GlobalAssetScope's
                // internal _scope without destroying its GameObject, and nothing ever
                // rebuilds it (GlobalAssetScope.Instance only rebuilds when its own
                // static _instance is null, which only happens from GlobalAssetScope's
                // own OnDestroy) — so every Simple.* call and every
                // Facade.GetGlobalScope().Loader NullReferences for the rest of the
                // process after the first Facade teardown. Only GlobalAssetScope's own
                // OnDestroy may dispose it; we just drop our reference
                // (see HANDOFF_TO_SESSION_B.md A-2, option (b)).
                _globalScope = null;
                _instance = null;
            }
        }
    }
}
