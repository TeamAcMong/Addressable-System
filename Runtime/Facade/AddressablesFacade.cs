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

        // Pooling
        private AddressablePoolManager _poolManager;

        public static AddressablesFacade Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[AddressablesFacade]");
                    _instance = go.AddComponent<AddressablesFacade>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

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

            // Initialize pool manager with global scope loader
            _poolManager = new AddressablePoolManager(_globalScope.Loader, new UnityPoolFactory());

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
            return await _globalScope.Loader.GetDownloadSizeAsync(address);
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
        /// Get global scope
        /// </summary>
        public GlobalAssetScope GetGlobalScope()
        {
            return _globalScope;
        }

        /// <summary>
        /// Get the session's <see cref="AssetLoader"/> (the ScopeManager entry
        /// keyed by <c>"Session"</c>). Returns null when no session is active.
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
            // Tear down in dependency order: pools own template handles loaded via the
            // global scope's loader, so dispose the pool manager first, then the scopes.
            _poolManager?.Dispose();
            _poolManager = null;

            // Session is a ScopeManager entry — clear it explicitly so its loader disposes.
            EndSession();

            // Note: GlobalAssetScope is a process-wide singleton — only dispose it if
            // this Facade is the owning instance being destroyed.
            if (_instance == this)
            {
                _globalScope?.Dispose();
                _globalScope = null;
                _instance = null;
            }
        }
    }
}
