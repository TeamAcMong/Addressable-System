using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Custom pool implementation without Unity dependencies
    /// Can be used with any pooling library or custom implementation
    /// </summary>
    public class CustomPoolAdapter<T> : IObjectPool<T>, IResizablePool<T> where T : class
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

            T obj = null;

            // P-2: walk past any pooled entries that were destroyed out from under us (e.g. by a
            // scene unload) instead of handing one to the caller. Discarded entries are simply never
            // added to _activeObjects — there is nothing to "untrack" for an instance that was never
            // tracked as active in the first place.
            while (obj == null && _pool.Count > 0)
            {
                var candidate = _pool.Pop();
                if (PoolInstanceGuard.IsDestroyed(candidate))
                {
                    Debug.LogWarning("[CustomPoolAdapter] Discarding a pooled instance that was " +
                        "destroyed externally (e.g. by a scene unload); a replacement will be created.");
                    continue;
                }
                obj = candidate;
            }

            if (obj == null)
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

        /// <summary>
        /// P-3/P-4: seed <paramref name="count"/> instances directly into the free stack. Never
        /// touches <see cref="_activeObjects"/> — these instances were never "gotten", so they must
        /// not count as active, unlike the old GrowPool, which called <see cref="Release"/> on an
        /// instance that had never been through <see cref="Get"/> and tripped the exact
        /// not-from-this-pool guard above.
        /// </summary>
        public void Prewarm(int count)
        {
            if (_disposed || count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                if (_maxSize > 0 && _pool.Count >= _maxSize)
                {
                    Debug.LogWarning($"[CustomPoolAdapter] Prewarm stopped at max size ({_maxSize}); " +
                        $"requested {count}.");
                    break;
                }

                var obj = _createFunc();
                if (obj == null) continue;

                _onRelease?.Invoke(obj);
                _pool.Push(obj);
            }
        }

        /// <summary>
        /// P-3/P-4: evict and destroy up to <paramref name="count"/> pooled (inactive) instances.
        /// Pops directly from the free stack and never touches <see cref="_activeObjects"/> — the
        /// old ShrinkPool got confused between "pooled" and "active" precisely because it routed
        /// through <see cref="Get"/>/<see cref="Release"/> instead of the stack directly.
        /// </summary>
        public void TrimExcess(int count)
        {
            if (_disposed || count <= 0) return;

            int toRemove = Math.Min(count, _pool.Count);
            for (int i = 0; i < toRemove; i++)
            {
                var obj = _pool.Pop();
                _onDestroy?.Invoke(obj);
            }
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
