using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Custom pool implementation without Unity dependencies
    /// Can be used with any pooling library or custom implementation
    /// </summary>
    public class CustomPoolAdapter<T> : IObjectPool<T> where T : class
    {
        private readonly Stack<T> _pool;
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly Action<T> _onDestroy;
        private readonly int _maxSize;
        private readonly HashSet<T> _activeObjects;

        private bool _disposed;

        public CustomPoolAdapter(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100)
        {
            _createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
            _onGet = onGet;
            _onRelease = onRelease;
            _onDestroy = onDestroy;
            _maxSize = maxSize;

            _pool = new Stack<T>();
            _activeObjects = new HashSet<T>();
        }

        public T Get()
        {
            if (_disposed)
            {
                Debug.LogError("[CustomPoolAdapter] Cannot get from disposed pool");
                return null;
            }

            T obj;

            if (_pool.Count > 0)
            {
                obj = _pool.Pop();
            }
            else
            {
                obj = _createFunc();
            }

            _activeObjects.Add(obj);
            _onGet?.Invoke(obj);

            return obj;
        }

        public void Release(T obj)
        {
            if (_disposed)
            {
                Debug.LogError("[CustomPoolAdapter] Cannot release to disposed pool");
                return;
            }

            if (obj == null)
            {
                Debug.LogWarning("[CustomPoolAdapter] Cannot release null object");
                return;
            }

            // Check if object is actually from this pool
            if (!_activeObjects.Remove(obj))
            {
                Debug.LogWarning("[CustomPoolAdapter] Attempting to release object that wasn't from this pool");
                return;
            }

            _onRelease?.Invoke(obj);

            // Respect max size
            if (_maxSize <= 0 || _pool.Count < _maxSize)
            {
                _pool.Push(obj);
            }
            else
            {
                // Pool is full, destroy the object
                _onDestroy?.Invoke(obj);
            }
        }

        public void Clear()
        {
            if (_disposed) return;

            // Destroy all pooled objects
            while (_pool.Count > 0)
            {
                var obj = _pool.Pop();
                _onDestroy?.Invoke(obj);
            }

            // Note: We don't clear active objects as they're still in use
            _pool.Clear();
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            if (_disposed) return (0, 0);
            return (_activeObjects.Count, _pool.Count);
        }

        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _activeObjects.Clear();
            _disposed = true;
        }
    }

    /// <summary>
    /// Factory for creating custom pool adapters
    /// </summary>
    public class CustomPoolFactory : IPoolFactory
    {
        public IObjectPool<T> CreatePool<T>(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100) where T : class
        {
            return new CustomPoolAdapter<T>(createFunc, onGet, onRelease, onDestroy, maxSize);
        }
    }
}
