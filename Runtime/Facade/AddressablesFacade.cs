using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
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

        // Periodic drain for every live tiering-configured AssetLoader (HANDOFF_TO_SESSION_B.md
        // L-3) — nothing else in the package ever calls EvaluateTiers()/ForceEviction() outside the
        // insert path's own inline trigger, which only fires while a cache is still actively
        // growing. This Facade already has Unity lifetime (DontDestroyOnLoad), so it is the owner
        // the design names rather than the loader itself (no Unity lifetime, no unsubscribe path)
        // or a MonoBehaviour per loader.
        //
        // Walks AssetLoaderRegistry, not the retired TieredAssetLoaderRegistry: tiering is a
        // configuration of AssetLoader now, and an untiered loader's EvaluateTiers()/ForceEviction()
        // are no-ops, so pumping every registered loader is correct and costs a virtual call each.
        private const float TieredCachePumpInterval = 5f; // seconds
        private float _tieredCachePumpElapsed;

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

            // See the field comment on TieredCachePumpInterval — the other half of L-3, a low-memory
            // sweep across every live tiering-configured AssetLoader. This is the case the periodic
            // pump above is too slow for: iOS gives the process one warning before killing it.
            Application.lowMemory -= OnTieredCacheLowMemory;
            Application.lowMemory += OnTieredCacheLowMemory;

            Debug.Log("[AddressablesFacade] Initialized");
        }

        private void Update()
        {
            _tieredCachePumpElapsed += Time.unscaledDeltaTime;
            if (_tieredCachePumpElapsed < TieredCachePumpInterval) return;

            _tieredCachePumpElapsed = 0f;
            AssetLoaderRegistry.PumpAll();
        }

        private void OnTieredCacheLowMemory()
        {
            Debug.LogWarning("[AddressablesFacade] Application.lowMemory — forcing eviction on every live tiering-configured AssetLoader.");
            AssetLoaderRegistry.ForceEvictionAll();
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

        // Nothing in this region loads a Unity scene. "Scene scope" is a cache whose lifetime is
        // tied to a scene's — the asset dies when that scene unloads. Grep the package for
        // Addressables.LoadSceneAsync / SceneInstance / LoadSceneMode and you get zero hits
        // outside of two doc comments: there is no scene-loading capability here at all
        // (HANDOFF_TO_SESSION_B.md A-8).

        /// <summary>
        /// Get or create the scope bound to <paramref name="scene"/>. Prefer this over the
        /// parameterless overload from anything that lives in a specific scene:
        /// <c>GetOrCreateSceneScope(gameObject.scene)</c>.
        /// </summary>
        public SceneAssetScope GetOrCreateSceneScope(Scene scene)
        {
            _sceneScope = SceneAssetScope.GetOrCreate(scene);
            return _sceneScope;
        }

        /// <summary>
        /// Get or create the scope bound to <b>the currently active scene</b> — not the caller's
        /// scene. A static entry point has no caller GameObject to derive one from, so this is a
        /// choice the caller has to make: from a MonoBehaviour in an additively-loaded scene, call
        /// <see cref="GetOrCreateSceneScope(Scene)"/> with <c>gameObject.scene</c> instead, or the
        /// scope you get back belongs to whichever scene happens to be active and your assets are
        /// released when <i>that</i> scene unloads (A-8).
        /// </summary>
        public SceneAssetScope GetOrCreateSceneScope()
        {
            // Identical to SceneAssetScope.GetOrCreate()'s own body; spelled out here so the
            // active-scene choice is visible at the one call site the API surface funnels through.
            return GetOrCreateSceneScope(SceneManager.GetActiveScene());
        }

        /// <summary>
        /// Load an asset into the cache scoped to <paramref name="scene"/> — released when that
        /// scene unloads. Loads an <i>asset</i>; it does not load a scene.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadIntoSceneScopeAsync<T>(string address, Scene scene)
#else
        public async Task<IAssetHandle<T>> LoadIntoSceneScopeAsync<T>(string address, Scene scene)
#endif
        {
            var scope = GetOrCreateSceneScope(scene);
            return await scope.Loader.LoadAssetAsync<T>(address);
        }

        /// <summary>
        /// Load an asset into the cache scoped to <b>the currently active scene</b>. Loads an
        /// <i>asset</i>; it does not load a scene. See
        /// <see cref="GetOrCreateSceneScope()"/> for why "the caller's scene" cannot be the
        /// default here — pass <c>gameObject.scene</c> to
        /// <see cref="LoadIntoSceneScopeAsync{T}(string, Scene)"/> when the caller lives in a
        /// scene other than the active one.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadIntoSceneScopeAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadIntoSceneScopeAsync<T>(string address)
#endif
        {
            return await LoadIntoSceneScopeAsync<T>(address, SceneManager.GetActiveScene());
        }

        /// <summary>
        /// Superseded by <see cref="LoadIntoSceneScopeAsync{T}(string)"/> — identical behaviour.
        /// The name is one character away from Unity's own <c>Addressables.LoadSceneAsync</c>
        /// while doing something completely different (it loads an asset into a scene-scoped
        /// cache), which is exactly the confusion the package's own sample fell into
        /// (<c>Assets.LoadScene&lt;Material&gt;("Scene/SpecialMaterial")</c>).
        ///
        /// Not marked <c>[Obsolete]</c>: <c>Assets.LoadScene&lt;T&gt;</c> still forwards here and
        /// renaming that surface is the other half of A-8, left for whoever owns
        /// <c>Runtime/Facade/Assets.cs</c> and <c>Samples~</c>. Deprecating this before that lands
        /// would only emit warnings inside the package.
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<IAssetHandle<T>> LoadSceneAsync<T>(string address)
#else
        public async Task<IAssetHandle<T>> LoadSceneAsync<T>(string address)
#endif
        {
            return await LoadIntoSceneScopeAsync<T>(address);
        }

        #endregion

        #region Pooling Operations

        /// <summary>
        /// True once <see cref="Initialize"/> has built the pool manager.
        /// </summary>
        /// <remarks>
        /// Initialize() returns early — leaving <c>_poolManager</c> null — when GlobalAssetScope is
        /// unavailable, which is the normal state during shutdown. Every member below went through
        /// that field unguarded, so Assets.Spawn / Assets.Despawn / Assets.CreatePool threw
        /// NullReferenceException from a teardown path rather than declining politely
        /// (HANDOFF_TO_SESSION_B.md P-31). SimpleAPI.Pool already guarded for this; the Facade did
        /// not, and Assets goes straight here.
        ///
        /// Each member now returns its neutral value and says why, which is the same
        /// "use-after-teardown logs and returns neutral, never throws" contract the pooling layer
        /// settled on in P-15.
        /// </remarks>
        private bool PoolsReady(string operation)
        {
            if (_poolManager != null) return true;

            Debug.LogWarning($"[AddressablesFacade] {operation} ignored: the pool manager does not " +
                             "exist. Initialize() has not run, or it returned early because the " +
                             "global scope was already gone (normal during shutdown).");
            return false;
        }

        /// <summary>
        /// Create object pool
        /// </summary>
#if UNITASK_PRESENT
        public async UniTask<bool> CreatePoolAsync(string address, int preloadCount = 0, int maxSize = 100)
#else
        public async Task<bool> CreatePoolAsync(string address, int preloadCount = 0, int maxSize = 100)
#endif
        {
            if (!PoolsReady(nameof(CreatePoolAsync))) return false;
            return await _poolManager.CreatePoolAsync(address, preloadCount, maxSize);
        }

        /// <summary>
        /// Spawn from pool
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (!PoolsReady(nameof(Spawn))) return null;
            return _poolManager.Spawn(address, position, rotation, parent);
        }

        /// <summary>
        /// Spawn at position (default rotation)
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Transform parent = null)
        {
            if (!PoolsReady(nameof(Spawn))) return null;
            return _poolManager.Spawn(address, position, parent);
        }

        /// <summary>
        /// Spawn at origin
        /// </summary>
        public GameObject Spawn(string address, Transform parent = null)
        {
            if (!PoolsReady(nameof(Spawn))) return null;
            return _poolManager.Spawn(address, parent);
        }

        /// <summary>
        /// Despawn to pool
        /// </summary>
        public void Despawn(string address, GameObject instance)
        {
            if (!PoolsReady(nameof(Despawn))) return;
            _poolManager.Despawn(address, instance);
        }

        /// <summary>
        /// Switch pool factory (e.g., to custom implementation)
        /// </summary>
        public void SetPoolFactory(IPoolFactory factory)
        {
            if (!PoolsReady(nameof(SetPoolFactory))) return;
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
                // Unconditional: a no-op if Initialize() never subscribed (the early-return path
                // above), and paired with the += in Initialize() either way.
                Application.lowMemory -= OnTieredCacheLowMemory;

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
