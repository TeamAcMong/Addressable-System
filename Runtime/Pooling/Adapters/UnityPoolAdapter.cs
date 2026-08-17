using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace AddressableManager.Pooling.Adapters
{
    /// <summary>
    /// Adapter for Unity's built-in ObjectPool (Unity 2021+)
    /// </summary>
    public class UnityPoolAdapter<T> : IObjectPool<T>, IResizablePool<T>, IMeasuredResizablePool<T>,
        IReclaimablePool<T> where T : class
    {
        private readonly ObjectPool<T> _unityPool;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onDestroy;

        // P-12: the cap as this adapter understands it — int.MaxValue when the caller asked for
        // "unlimited" (maxSize <= 0, P-7's shared convention). Kept because Prewarm has to honour it
        // and UnityEngine.Pool.ObjectPool<T> does not expose the value it was constructed with.
        private readonly int _maxSize;

        private bool _disposed;

        // P-2: set by SafeOnGet (called synchronously inside _unityPool.Get()) whenever the popped
        // instance turns out to be a Unity "fake null". Get() loops on this instead of handing the
        // caller a corpse.
        private bool _lastGetWasDestroyed;

        // P-4/P-11: while non-zero, SafeOnGet must not forward to the caller's onGet — the popped
        // instance is about to be destroyed by TrimExcess, or is being cycled through by Prewarm to
        // seed the free list. Neither is a real hand-out, and the caller's onGet is where
        // AddressablePoolManager does SetActive(true) + TrackActive.
        //
        // A DEPTH COUNTER, not a bool. Prewarm holds the suppression across N pops, and every pop
        // can reach createFunc -> Object.Instantiate -> the new instance's Awake, i.e. arbitrary
        // game code on this thread inside the suppressed region. If that code reaches Prewarm or
        // TrimExcessMeasured on this same adapter, a bool's `finally` cleared the OUTER region's
        // suppression halfway through: the outer loop's remaining pops then fired the caller's onGet
        // on instances it was about to Release(), re-creating the exact P-11 defect the flag exists
        // to fix. Incrementing and decrementing means only the outermost region's exit re-enables
        // onGet. See SafeOnGet for the one case this deliberately does not fix.
        private int _suppressOnGetDepth;

        // P-4: UnityEngine.Pool.ObjectPool<T> has no public way to decrement CountAll, so a
        // TrimExcess pop (which the pool internally treats as "became active", same as any Get())
        // is never balanced by a Release(). Track the correction separately and subtract it back out
        // in GetStats() instead of letting CountActive climb by one per trimmed instance forever.
        // The same correction applies to a stale/destroyed instance SafeOnGet discovers and Get()'s
        // retry loop discards (below), and to a borrowed instance destroyed out in the world that
        // ForgetActive is told about (P-8): every one of those pops is unbalanced in exactly the
        // same way.
        //
        // P-14: this deliberately goes NEGATIVE in Clear(). ObjectPool.Clear() zeroes CountAll while
        // the caller may still be holding N instances, which would otherwise make those N vanish
        // from CountActive; carrying -N here keeps reporting them until they come back. See Clear().
        private int _trimmedCount;

        // P-26: the Mathf.Max(0, ...) below is exact under normal use, so it only ever engages once
        // the accounting has ALREADY gone wrong. Say so, once, instead of silently reporting 0.
        private bool _clampReported;

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
            _maxSize = maxSize > 0 ? maxSize : int.MaxValue;

            _unityPool = new ObjectPool<T>(
                createFunc,
                SafeOnGet,
                onRelease,
                onDestroy,
                collectionCheck: true,
                defaultCapacity: 10,
                maxSize: _maxSize
            );
        }

        /// <summary>
        /// P-15: use-after-dispose logs an error and returns <c>null</c> — it never throws. See
        /// <see cref="IObjectPool{T}"/>'s remarks for why the whole package settled on that shape.
        /// </summary>
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

        /// <summary>
        /// Destroys every pooled (inactive) instance. Instances the caller is still holding stay
        /// borrowed and keep counting as active — see <see cref="IObjectPool{T}.Clear"/>.
        /// </summary>
        /// <remarks>
        /// P-14: <c>UnityEngine.Pool.ObjectPool&lt;T&gt;.Clear()</c> resets its own <c>CountAll</c> to
        /// zero, which silently reclassifies every outstanding borrow as "never existed" —
        /// <c>GetStats()</c> would report <c>(0, 0)</c> with three instances live on screen, while
        /// <see cref="CustomPoolAdapter{T}"/> in the same situation reports <c>(3, 0)</c>. Fold the
        /// outstanding count into the standing correction so both adapters answer the same question
        /// the same way, and so a later <c>Release()</c> of one of those instances brings the number
        /// down by one instead of driving the raw count negative behind the clamp.
        ///
        /// Why a single negative offset is exactly right and not an approximation: after
        /// <c>Clear()</c>, <c>CountAll</c> is 0 and <c>Release()</c> never increments it (only
        /// <c>Get()</c> does, and only when it has to construct), so the raw
        /// <c>CountActive = CountAll - CountInactive</c> tracks <c>trueActive - N</c> continuously
        /// from here on, for both later releases and later gets. Subtracting <c>-N</c> in
        /// <see cref="GetStats"/> cancels it exactly.
        /// </remarks>
        public void Clear()
        {
            if (_disposed) return;

            int outstanding = Mathf.Max(0, _unityPool.CountActive - _trimmedCount);
            _unityPool.Clear();
            _trimmedCount = -outstanding;
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            if (_disposed) return (0, 0);

            int raw = _unityPool.CountActive - _trimmedCount;

            // P-26: under normal use the correction is exact, so a negative here is never "a small
            // rounding artefact" — it means an instance was released that this pool never handed
            // out, or released twice, i.e. the class of bug P-4 fixed has recurred. Clamping is
            // still the right thing to report (a negative population is meaningless), but doing it
            // silently is what let the original drift hide. Once per pool, not once per frame.
            if (raw < 0 && !_clampReported)
            {
                _clampReported = true;
                Debug.LogWarning("[UnityPoolAdapter] Active-instance accounting has drifted negative " +
                    $"(ObjectPool.CountActive={_unityPool.CountActive}, correction={_trimmedCount}); " +
                    "reporting 0 active. Something released an instance this pool never handed out, " +
                    "or released one twice. Reported once per pool.");
            }

            return (Mathf.Max(0, raw), _unityPool.CountInactive);
        }

        public void Dispose()
        {
            if (_disposed) return;

            Clear();
            _disposed = true;
        }

        /// <summary>
        /// P-3/P-4/P-11/P-12: seed <paramref name="count"/> instances into the pool without ever
        /// reporting them as active and without ever running the caller's <c>onGet</c>.
        /// </summary>
        public void Prewarm(int count) => PrewarmMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.PrewarmMeasured"/>
        /// <remarks>
        /// Three separate defects meet in this method and each fix is load-bearing:
        ///
        /// P-11 — it used to call the public <see cref="Get"/> N times with <c>_suppressOnGetDepth</c>
        /// false, so the caller's <c>onGet</c> ran: <c>AddressablePoolManager</c>'s callback does
        /// <c>SetActive(true)</c> + <c>TrackActive</c>, and the matching <c>Release</c> does
        /// <c>SetActive(false)</c>. Preloading 20 enemies therefore ran <c>OnEnable</c>+
        /// <c>OnDisable</c> on all 20 during pool creation — spawn VFX, 20 phantom WaveManager
        /// registrations, audio — while <see cref="CustomPoolAdapter{T}"/>, through the same
        /// <see cref="IResizablePool{T}"/> call, ran none of it. <c>_suppressOnGetDepth</c> existed for
        /// exactly this and was only wired into <see cref="TrimExcessMeasured"/>.
        ///
        /// P-12 — <c>UnityEngine.Pool.ObjectPool.Get()</c> has no cap, so <c>Prewarm(200)</c> on a
        /// <c>maxSize: 100</c> pool used to instantiate 200 and let <c>Release()</c> destroy the 100
        /// that overflowed: a frame hitch plus 100 full Awake/OnEnable/OnDisable/OnDestroy cycles,
        /// silently. <see cref="CustomPoolAdapter{T}"/> stopped at the cap and said so.
        ///
        /// P-13 — there was no <c>try</c>/<c>finally</c>. A <c>createFunc</c> that threw partway
        /// (destroyed template, OOM) left every instance already popped stranded: active,
        /// <c>SetActive(true)</c>, and tracked forever, which then permanently blocks
        /// <c>ClearPool</c> (P-8's symptom).
        ///
        /// The pop count is <c>CountInactive + toCreate</c>, not <c>toCreate</c>: <c>Get()</c> drains
        /// the free list before it constructs anything, so popping only <c>toCreate</c> times on a
        /// pool that already holds instances would recycle them and add nothing.
        /// <see cref="CustomPoolAdapter{T}.Prewarm"/> adds <c>count</c> NEW instances; this makes
        /// both adapters mean the same thing by that argument.
        /// </remarks>
        public int PrewarmMeasured(int count)
        {
            if (_disposed)
            {
                Debug.LogError("[UnityPoolAdapter] Cannot prewarm a disposed pool");
                return 0;
            }

            if (count <= 0) return 0;

            int existing = _unityPool.CountInactive;

            int room = _maxSize == int.MaxValue ? count : Mathf.Max(0, _maxSize - existing);
            int toCreate = Mathf.Min(count, room);
            if (toCreate < count)
            {
                Debug.LogWarning($"[UnityPoolAdapter] Prewarm stopped at max size ({_maxSize}); " +
                    $"requested {count}.");
            }

            if (toCreate <= 0) return 0;

            int pops = existing + toCreate;
            var held = new List<T>(pops);

            _suppressOnGetDepth++;
            try
            {
                for (int i = 0; i < pops; i++)
                {
                    var item = Get();
                    if (item != null) held.Add(item);
                }
            }
            finally
            {
                _suppressOnGetDepth--;
                foreach (var item in held)
                {
                    Release(item);
                }
            }

            return Mathf.Max(0, _unityPool.CountInactive - existing);
        }

        /// <summary>
        /// P-4: evict and destroy up to <paramref name="count"/> pooled (inactive) instances without
        /// corrupting <see cref="GetStats"/>.
        /// </summary>
        public void TrimExcess(int count) => TrimExcessMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.TrimExcessMeasured"/>
        /// <remarks>
        /// Pops raw from the inner pool (never retries on a destroyed instance the way
        /// <see cref="Get"/> does — a shrink must only ever remove existing capacity, never
        /// manufacture a fresh instance via <c>createFunc</c>) and suppresses the caller's onGet for
        /// the duration, because these instances are being destroyed, not handed out as active.
        /// </remarks>
        public int TrimExcessMeasured(int count)
        {
            if (_disposed)
            {
                Debug.LogError("[UnityPoolAdapter] Cannot trim a disposed pool");
                return 0;
            }

            if (count <= 0) return 0;

            int toRemove = Mathf.Min(count, _unityPool.CountInactive);
            int removed = 0;

            for (int i = 0; i < toRemove; i++)
            {
                T item;
                _suppressOnGetDepth++;
                try
                {
                    item = _unityPool.Get();
                }
                finally
                {
                    _suppressOnGetDepth--;
                }

                if (PoolInstanceGuard.IsDestroyed(item))
                {
                    // SafeOnGet already booked this unbalanced pop into _trimmedCount — the slot is
                    // gone from the free list either way, so it counts toward the trim, but it must
                    // not be corrected for twice.
                    removed++;
                    continue;
                }

                _onDestroy?.Invoke(item);

                // Unity's pool now believes this slot is active (it was popped, and we deliberately
                // never Release() it back). Correct GetStats() for that instead of letting it drift.
                _trimmedCount++;
                removed++;
            }

            return removed;
        }

        /// <inheritdoc cref="IReclaimablePool{T}.ForgetActive"/>
        /// <remarks>
        /// P-8. <c>UnityEngine.Pool.ObjectPool&lt;T&gt;</c> exposes no way to decrement
        /// <c>CountAll</c>, and it has no membership set to consult, so this cannot tell whether the
        /// instance really was one of ours — it books the same standing correction
        /// <see cref="TrimExcessMeasured"/> uses and returns <c>true</c>. That is safe only because
        /// the sole caller (<c>AddressablePoolManager</c>) calls it exactly once per lost instance,
        /// under the authority of its own <c>_instanceOwners</c> map, in the same step that removes
        /// the instance from that map. Do not call it speculatively: an extra call under-reports
        /// <c>activeCount</c> and eventually trips the P-26 clamp warning in <see cref="GetStats"/>.
        /// </remarks>
        public bool ForgetActive(T instance)
        {
            if (_disposed || instance == null) return false;

            _trimmedCount++;
            return true;
        }

        private void SafeOnGet(T obj)
        {
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
                //
                // Booked here rather than at the call sites so it is booked exactly once no matter
                // which of the three pop paths (Get / Prewarm / TrimExcess) found the corpse.
                _trimmedCount++;

                if (_suppressOnGetDepth == 0)
                {
                    Debug.LogWarning("[UnityPoolAdapter] Discarding a pooled instance that was " +
                        "destroyed externally (e.g. by a scene unload); a replacement will be created.");
                }

                return;
            }

            _lastGetWasDestroyed = false;

            // P-4 (TrimExcess) / P-11 (Prewarm): neither is a real hand-out.
            //
            // KNOWN RESIDUAL, deliberately not fixed here. Suppression is scoped to a REGION of
            // time, not to a specific pop, so a genuine Get() that nests inside a Prewarm/TrimExcess
            // region — reachable only when a pooled prefab's Awake spawns from the same address on
            // the same adapter — is suppressed too, and its caller receives an instance that never
            // got SetActive(true)/TrackActive. Fixing that needs per-pop scoping, which
            // UnityEngine.Pool.ObjectPool<T> gives no seam for: it invokes createFunc (and therefore
            // Awake, and therefore the nested Get) BEFORE actionOnGet for the outer pop, so a
            // "consume on first callback" token would be consumed by the nested pop, not the outer
            // one. Recorded in Documentation/LIFETIME_DESIGN.md rather than papered over.
            if (_suppressOnGetDepth > 0) return;

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
