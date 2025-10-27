using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Loaders;
using AddressableManager.Core;
using AddressableManager.Pooling;
using AddressableManager.Pooling.Adapters;
using AddressableManager.Progress;
using AddressableManager.Scopes;

namespace AddressableManager.Facade
{
    /// <summary>
    /// High-level facade providing unified access to all addressable systems
    /// Simplifies common operations and hides complexity
    /// </summary>
    public class AddressablesFacade : MonoBehaviour
    {
        private static AddressablesFacade _instance;

        // Scopes
        private GlobalAssetScope _globalScope;
        private SessionAssetScope _sessionScope;
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
        /// Load asset into global scope (persistent)
        /// </summary>
        public async Task<IAssetHandle<T>> LoadGlobalAsync<T>(string address)
        {
            return await _globalScope.Loader.LoadAssetAsync<T>(address);
        }

        /// <summary>
        /// Load with progress tracking
        /// </summary>
        public async Task<IAssetHandle<T>> LoadGlobalWithProgressAsync<T>(string address, Action<ProgressInfo> onProgress)
        {
            return await _globalScope.Loader.LoadAssetWithProgressAsync<T>(address, onProgress);
        }

        #endregion

        #region Session Scope Operations

        /// <summary>
        /// Start new session
        /// </summary>
        public void StartSession()
        {
            _sessionScope = SessionAssetScope.StartSession();
            Debug.Log("[AddressablesFacade] Session started");
        }

        /// <summary>
        /// End current session
        /// </summary>
        public void EndSession()
        {
            SessionAssetScope.EndSession();
            _sessionScope = null;
            Debug.Log("[AddressablesFacade] Session ended");
        }

        /// <summary>
        /// Load asset into session scope
        /// </summary>
        public async Task<IAssetHandle<T>> LoadSessionAsync<T>(string address)
        {
            if (_sessionScope == null)
            {
                Debug.LogError("[AddressablesFacade] No active session. Call StartSession() first!");
                return null;
            }

            return await _sessionScope.Loader.LoadAssetAsync<T>(address);
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
        public async Task<IAssetHandle<T>> LoadSceneAsync<T>(string address)
        {
            var scope = GetOrCreateSceneScope();
            return await scope.Loader.LoadAssetAsync<T>(address);
        }

        #endregion

        #region Pooling Operations

        /// <summary>
        /// Create object pool
        /// </summary>
        public async Task<bool> CreatePoolAsync(string address, int preloadCount = 0, int maxSize = 100)
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

        #endregion

        #region Utility

        /// <summary>
        /// Get download size for address
        /// </summary>
        public async Task<long> GetDownloadSizeAsync(string address)
        {
            return await _globalScope.Loader.GetDownloadSizeAsync(address);
        }

        /// <summary>
        /// Download dependencies with progress
        /// </summary>
        public async Task<bool> DownloadAsync(string address, Action<ProgressInfo> onProgress = null)
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

        #endregion

        private void OnDestroy()
        {
            _poolManager?.Dispose();

            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
