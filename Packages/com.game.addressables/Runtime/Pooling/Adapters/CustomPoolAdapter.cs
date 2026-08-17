using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Custom pool implementation without Unity dependencies
    /// Can be used with any pooling library or custom implementation
    /// </summary>
    public class CustomPoolAdapter<T> : IObjectPool<T>, IResizablePool<T>, IMeasuredResizablePool<T>,
        IReclaimablePool<T> where T : class
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

        /// <summary>
        /// P-15: use-after-dispose logs an error and returns <c>null</c> — it never throws. See
        /// <see cref="IObjectPool{T}"/>'s remarks.
        /// </summary>
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

                // P-32: a third-party createFunc is free to return null (UnityEngine.Object.Instantiate
                // throws instead, so this is unreachable through AddressablePoolManager). Adding that
                // null to _activeObjects used to inflate activeCount by one forever, because
                // Release(null) early-returns before it could ever be taken back out — and that
                // inflated count feeds DynamicPool's growth controller.
                if (obj == null || PoolInstanceGuard.IsDestroyed(obj))
                {
                    Debug.LogError("[CustomPoolAdapter] createFunc produced no usable instance " +
                        "(null, or an already-destroyed UnityEngine.Object); Get() returns null " +
                        "rather than tracking a corpse as active.");
                    return null;
                }
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

        /// <summary>
        /// Destroys every pooled (inactive) instance. Instances the caller is still holding stay
        /// borrowed and keep counting as active — see <see cref="IObjectPool{T}.Clear"/>. This has
        /// always been this adapter's behaviour; P-14 made <see cref="UnityPoolAdapter{T}"/> agree
        /// with it instead of zeroing its active count out from under the borrower.
        /// </summary>
        public void Clear()
        {
            if (_disposed) return;

            // Destroy all pooled objects
            while (_pool.Count > 0)
            {
                var obj = _pool.Pop();
                _onDestroy?.Invoke(obj);
            }

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
        /// touches <see cref="_activeObjects"/> and never runs <see cref="_onGet"/> — these
        /// instances were never "gotten", so they must not count as active, unlike the old GrowPool,
        /// which called <see cref="Release"/> on an instance that had never been through
        /// <see cref="Get"/> and tripped the not-from-this-pool guard above.
        /// </summary>
        public void Prewarm(int count) => PrewarmMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.PrewarmMeasured"/>
        /// <remarks>
        /// P-12/P-27: the max-size stop moved out of the loop so the cap is reported once, up front,
        /// with the same wording <see cref="UnityPoolAdapter{T}.PrewarmMeasured"/> now uses, and so
        /// the achieved count can be returned instead of the caller logging what it asked for.
        /// </remarks>
        public int PrewarmMeasured(int count)
        {
            if (_disposed)
            {
                Debug.LogError("[CustomPoolAdapter] Cannot prewarm a disposed pool");
                return 0;
            }

            if (count <= 0) return 0;

            int room = _maxSize > 0 ? Math.Max(0, _maxSize - _pool.Count) : count;
            int toCreate = Math.Min(count, room);
            if (toCreate < count)
            {
                Debug.LogWarning($"[CustomPoolAdapter] Prewarm stopped at max size ({_maxSize}); " +
                    $"requested {count}.");
            }

            if (toCreate <= 0) return 0;

            int created = 0;
            for (int i = 0; i < toCreate; i++)
            {
                var obj = _createFunc();
                if (obj == null || PoolInstanceGuard.IsDestroyed(obj)) continue;

                _onRelease?.Invoke(obj);
                _pool.Push(obj);
                created++;
            }

            return created;
        }

        /// <summary>
        /// P-3/P-4: evict and destroy up to <paramref name="count"/> pooled (inactive) instances.
        /// Pops directly from the free stack and never touches <see cref="_activeObjects"/> — the
        /// old ShrinkPool got confused between "pooled" and "active" precisely because it routed
        /// through <see cref="Get"/>/<see cref="Release"/> instead of the stack directly.
        /// </summary>
        public void TrimExcess(int count) => TrimExcessMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.TrimExcessMeasured"/>
        public int TrimExcessMeasured(int count)
        {
            if (_disposed)
            {
                Debug.LogError("[CustomPoolAdapter] Cannot trim a disposed pool");
                return 0;
            }

            if (count <= 0) return 0;

            int toRemove = Math.Min(count, _pool.Count);
            for (int i = 0; i < toRemove; i++)
            {
                var obj = _pool.Pop();
                _onDestroy?.Invoke(obj);
            }

            return toRemove;
        }

        /// <inheritdoc cref="IReclaimablePool{T}.ForgetActive"/>
        /// <remarks>
        /// P-8. Unlike <see cref="UnityPoolAdapter{T}.ForgetActive"/> this adapter has a real
        /// membership set, so it can answer honestly: <c>false</c> means "I never handed that out,
        /// or it already came back", and no accounting changed.
        /// </remarks>
        public bool ForgetActive(T instance)
        {
            if (_disposed || instance == null) return false;

            return _activeObjects.Remove(instance);
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
