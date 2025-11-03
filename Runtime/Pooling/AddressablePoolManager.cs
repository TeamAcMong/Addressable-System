using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Loaders;
using AddressableManager.Pooling.Adapters;

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
        private IPoolFactory _poolFactory;
        private bool _disposed;

        // Auto-create pool settings
        private bool _autoCreatePoolsEnabled = false;
        private DynamicPoolConfig _autoCreateDefaultConfig = null;

        public AddressablePoolManager(AssetLoader loader, IPoolFactory poolFactory = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _poolFactory = poolFactory ?? new UnityPoolFactory(); // Default to Unity's pool
            _pools = new Dictionary<string, IObjectPool<GameObject>>();
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
        /// Create a dynamic pool for an addressable prefab with auto-sizing
        /// </summary>
        public async Task<bool> CreateDynamicPoolAsync(
            string address,
            DynamicPoolConfig config = null,
            int preloadCount = 0,
            Transform poolRoot = null)
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

            // Load the prefab template first
            var handle = await _loader.LoadAssetAsync<GameObject>(address);
            if (handle == null || !handle.IsValid)
            {
                Debug.LogError($"[PoolManager] Failed to load prefab for pooling: {address}");
                return false;
            }

            var prefab = handle.Asset;

            // Create base pool using current factory
            var basePool = _poolFactory.CreatePool<GameObject>(
                createFunc: () => CreateInstance(prefab, poolRoot),
                onGet: (obj) => obj.SetActive(true),
                onRelease: (obj) =>
                {
                    if (obj != null)
                    {
                        obj.SetActive(false);
                        if (poolRoot != null) obj.transform.SetParent(poolRoot);
                    }
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
                createFunc: () => CreateInstance(prefab, poolRoot),
                onDestroy: (obj) =>
                {
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                },
                poolName: address
            );

            _pools[address] = dynamicPool;
            Debug.Log($"[PoolManager] Dynamic pool created for {address} " +
                     $"(capacity: {config.InitialCapacity}, range: [{config.MinSize}-{config.MaxSize}])");

            // Preload instances
            if (preloadCount > 0)
            {
                Debug.Log($"[PoolManager] Preloading {preloadCount} instances for {address}");
                for (int i = 0; i < preloadCount; i++)
                {
                    var instance = dynamicPool.Get();
                    dynamicPool.Release(instance);
                }
            }

            return true;
        }

        /// <summary>
        /// Create a pool for an addressable prefab
        /// </summary>
        public async Task<bool> CreatePoolAsync(
            string address,
            int preloadCount = 0,
            int maxSize = 100,
            Transform poolRoot = null)
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

            // Load the prefab template first
            var handle = await _loader.LoadAssetAsync<GameObject>(address);
            if (handle == null || !handle.IsValid)
            {
                Debug.LogError($"[PoolManager] Failed to load prefab for pooling: {address}");
                return false;
            }

            var prefab = handle.Asset;

            // Create pool using current factory
            var pool = _poolFactory.CreatePool<GameObject>(
                createFunc: () => CreateInstance(prefab, poolRoot),
                onGet: (obj) => obj.SetActive(true),
                onRelease: (obj) =>
                {
                    if (obj != null)
                    {
                        obj.SetActive(false);
                        if (poolRoot != null) obj.transform.SetParent(poolRoot);
                    }
                },
                onDestroy: (obj) =>
                {
                    if (obj != null) UnityEngine.Object.Destroy(obj);
                },
                maxSize: maxSize
            );

            _pools[address] = pool;
            Debug.Log($"[PoolManager] Pool created for {address} with max size {maxSize}");

            // Preload instances
            if (preloadCount > 0)
            {
                Debug.Log($"[PoolManager] Preloading {preloadCount} instances for {address}");
                for (int i = 0; i < preloadCount; i++)
                {
                    var instance = pool.Get();
                    pool.Release(instance);
                }
            }

            return true;
        }

        /// <summary>
        /// Spawn object from pool
        /// If auto-create is enabled and pool doesn't exist, creates it automatically
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot spawn from disposed manager");
                return null;
            }

            // Check if pool exists
            if (!_pools.TryGetValue(address, out var pool))
            {
                // Auto-create pool if enabled
                if (_autoCreatePoolsEnabled)
                {
                    Debug.LogWarning($"[PoolManager] Auto-creating pool for {address}");

                    // Create pool synchronously (blocking)
                    var task = _autoCreateDefaultConfig != null
                        ? CreateDynamicPoolAsync(address, _autoCreateDefaultConfig, preloadCount: 0)
                        : CreatePoolAsync(address, preloadCount: 0, maxSize: 50);

                    task.Wait(); // Block until pool is created

                    if (!task.Result)
                    {
                        Debug.LogError($"[PoolManager] Failed to auto-create pool for {address}");
                        return null;
                    }

                    // Get the newly created pool
                    if (!_pools.TryGetValue(address, out pool))
                    {
                        Debug.LogError($"[PoolManager] Pool was created but not found in dictionary: {address}");
                        return null;
                    }
                }
                else
                {
                    Debug.LogError($"[PoolManager] No pool found for {address}. Create pool first or enable auto-create!");
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
        /// Return object to pool
        /// </summary>
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
                UnityEngine.Object.Destroy(instance);
                return;
            }

            pool.Release(instance);
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
        /// Clear specific pool
        /// </summary>
        public void ClearPool(string address)
        {
            if (_pools.TryGetValue(address, out var pool))
            {
                Debug.Log($"[PoolManager] Clearing pool: {address}");
                pool.Clear();
                pool.Dispose();
                _pools.Remove(address);
            }
        }

        /// <summary>
        /// Clear all pools
        /// </summary>
        public void ClearAllPools()
        {
            Debug.Log($"[PoolManager] Clearing all pools ({_pools.Count})");

            foreach (var pool in _pools.Values)
            {
                pool?.Clear();
                pool?.Dispose();
            }

            _pools.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;

            ClearAllPools();
            _disposed = true;
        }

        private GameObject CreateInstance(GameObject prefab, Transform parent)
        {
            var instance = UnityEngine.Object.Instantiate(prefab, parent);
            instance.SetActive(false);
            return instance;
        }
    }
}
