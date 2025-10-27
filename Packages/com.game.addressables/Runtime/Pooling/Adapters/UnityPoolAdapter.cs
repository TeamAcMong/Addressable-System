using System;
using UnityEngine;
using UnityEngine.Pool;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Adapter for Unity's built-in ObjectPool (Unity 2021+)
    /// </summary>
    public class UnityPoolAdapter<T> : IObjectPool<T> where T : class
    {
        private readonly ObjectPool<T> _unityPool;
        private bool _disposed;

        public UnityPoolAdapter(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100)
        {
            _unityPool = new ObjectPool<T>(
                createFunc,
                onGet,
                onRelease,
                onDestroy,
                collectionCheck: true,
                defaultCapacity: 10,
                maxSize: maxSize
            );
        }

        public T Get()
        {
            if (_disposed)
            {
                Debug.LogError("[UnityPoolAdapter] Cannot get from disposed pool");
                return null;
            }

            return _unityPool.Get();
        }

        public void Release(T obj)
        {
            if (_disposed)
            {
                Debug.LogError("[UnityPoolAdapter] Cannot release to disposed pool");
                return;
            }

            if (obj == null)
            {
                Debug.LogWarning("[UnityPoolAdapter] Cannot release null object");
                return;
            }

            _unityPool.Release(obj);
        }

        public void Clear()
        {
            if (_disposed) return;
            _unityPool.Clear();
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            if (_disposed) return (0, 0);
            return (_unityPool.CountActive, _unityPool.CountInactive);
        }

        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Factory for creating Unity pool adapters
    /// </summary>
    public class UnityPoolFactory : IPoolFactory
    {
        public IObjectPool<T> CreatePool<T>(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100) where T : class
        {
            return new UnityPoolAdapter<T>(createFunc, onGet, onRelease, onDestroy, maxSize);
        }
    }
}
