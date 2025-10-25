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

        public AddressablePoolManager(AssetLoader loader, IPoolFactory poolFactory = null)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _poolFactory = poolFactory ?? new UnityPoolFactory(); // Default to Unity's pool
            _pools = new Dictionary<string, IObjectPool<GameObject>>();
        }

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
        /// </summary>
        public GameObject Spawn(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (_disposed)
            {
                Debug.LogError("[PoolManager] Cannot spawn from disposed manager");
                return null;
            }

            if (!_pools.TryGetValue(address, out var pool))
            {
                Debug.LogError($"[PoolManager] No pool found for {address}. Create pool first!");
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
