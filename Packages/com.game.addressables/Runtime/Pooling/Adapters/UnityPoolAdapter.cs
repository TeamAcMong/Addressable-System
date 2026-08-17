using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Adapter for Unity's built-in ObjectPool (Unity 2021+)
    /// </summary>
    public class UnityPoolAdapter<T> : IObjectPool<T>, IResizablePool<T> where T : class
    {
        private readonly ObjectPool<T> _unityPool;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onDestroy;
        private bool _disposed;

        // P-2: set by SafeOnGet (called synchronously inside _unityPool.Get()) whenever the popped
        // instance turns out to be a Unity "fake null". Get() loops on this instead of handing the
        // caller a corpse.
        private bool _lastGetWasDestroyed;

        // P-4/TrimExcess: while true, SafeOnGet must not forward to the caller's onGet — the popped
        // instance is about to be destroyed by TrimExcess, never handed out as "active".
        private bool _suppressOnGet;

        // P-4: UnityEngine.Pool.ObjectPool<T> has no public way to decrement CountAll, so a
        // TrimExcess pop (which the pool internally treats as "became active", same as any Get())
        // is never balanced by a Release(). Track the correction separately and subtract it back out
        // in GetStats() instead of letting CountActive climb by one per trimmed instance forever.
        // The same correction applies to a stale/destroyed instance SafeOnGet discovers and Get()'s
        // retry loop discards (below): that pop is unbalanced in exactly the same way, just
        // triggered by an externally-destroyed instance instead of a deliberate trim.
        private int _trimmedCount;

        public UnityPoolAdapter(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100)
        {
            _onGet = onGet;
            _onDestroy = onDestroy;

            // P-7: one documented meaning for maxSize <= 0 across both adapters — "unlimited",
            // matching CustomPoolAdapter's existing behavior and PoolConfiguration's own tooltip
            // ("Maximum pool size (0 = unlimited)"). UnityEngine.Pool.ObjectPool<T>'s constructor
            // does not accept a non-positive maxSize, so translate before handing it off rather than
            // let a caller-supplied 0 throw here.
            int effectiveMaxSize = maxSize > 0 ? maxSize : int.MaxValue;

            _unityPool = new ObjectPool<T>(
                createFunc,
                SafeOnGet,
                onRelease,
                onDestroy,
                collectionCheck: true,
                defaultCapacity: 10,
                maxSize: effectiveMaxSize
            );
        }

        public T Get()
        {
            if (_disposed)
            {
                Debug.LogError("[UnityPoolAdapter] Cannot get from disposed pool");
                return null;
            }

            // P-2: retry until we have an instance that survived. Bounded by the inactive list's
            // size — once it is drained of stale entries, _unityPool.Get() falls through to
            // createFunc(), which is always fresh.
            T obj;
            do
            {
                obj = _unityPool.Get();
            } while (_lastGetWasDestroyed);

            return obj;
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
            // ObjectPool.Clear() resets its own CountAll to 0, so any outstanding TrimExcess
            // correction is moot afterward — reset alongside it rather than let it go stale.
            _trimmedCount = 0;
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            if (_disposed) return (0, 0);
            int active = Mathf.Max(0, _unityPool.CountActive - _trimmedCount);
            return (active, _unityPool.CountInactive);
        }

        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _disposed = true;
        }

        /// <summary>
        /// P-3/P-4: seed <paramref name="count"/> instances into the pool without ever reporting
        /// them as active. Implemented as Get-then-Release-N (not N individual Get/Release pairs) —
        /// that is the only way to make <c>ObjectPool&lt;T&gt;.CountAll</c> increase without going
        /// through <c>createFunc</c> directly and bypassing this adapter's own bookkeeping/guards.
        /// </summary>
        public void Prewarm(int count)
        {
            if (_disposed || count <= 0) return;

            var created = new List<T>(count);
            for (int i = 0; i < count; i++)
            {
                var item = Get();
                if (item != null) created.Add(item);
            }

            foreach (var item in created)
            {
                Release(item);
            }
        }

        /// <summary>
        /// P-4: evict and destroy up to <paramref name="count"/> pooled (inactive) instances without
        /// corrupting <see cref="GetStats"/>. Pops raw from the inner pool (never retries on a
        /// destroyed instance the way <see cref="Get"/> does — a shrink must only ever remove
        /// existing capacity, never manufacture a fresh instance via <c>createFunc</c>) and
        /// suppresses the caller's onGet for the duration, because these instances are being
        /// destroyed, not handed out as active.
        /// </summary>
        public void TrimExcess(int count)
        {
            if (_disposed || count <= 0) return;

            int toRemove = Mathf.Min(count, _unityPool.CountInactive);
            for (int i = 0; i < toRemove; i++)
            {
                T item;
                _suppressOnGet = true;
                try
                {
                    item = _unityPool.Get();
                }
                finally
                {
                    _suppressOnGet = false;
                }

                if (!PoolInstanceGuard.IsDestroyed(item))
                {
                    _onDestroy?.Invoke(item);
                }

                // Unity's pool now believes this slot is active (it was popped, and we deliberately
                // never Release() it back). Correct GetStats() for that instead of letting it drift.
                _trimmedCount++;
            }
        }

        private void SafeOnGet(T obj)
        {
            if (_suppressOnGet)
            {
                _lastGetWasDestroyed = PoolInstanceGuard.IsDestroyed(obj);
                return;
            }

            if (PoolInstanceGuard.IsDestroyed(obj))
            {
                _lastGetWasDestroyed = true;

                // This pop is the same unbalanced-Get shape TrimExcess corrects for: Unity's pool
                // now believes this slot is active (it came off the inactive stack via Get()), and
                // Get()'s caller-facing retry loop (above) discards it and tries again instead of
                // ever pairing it with a Release(). Without this correction CountActive drifts
                // upward by one per stale instance discovered, unboundedly, and never resets except
                // via Clear() — GetStats().activeCount would silently inflate forever across scene
                // reloads instead of reporting real usage.
                _trimmedCount++;

                Debug.LogWarning("[UnityPoolAdapter] Discarding a pooled instance that was " +
                    "destroyed externally (e.g. by a scene unload); a replacement will be created.");
                return;
            }

            _lastGetWasDestroyed = false;
            _onGet?.Invoke(obj);
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
