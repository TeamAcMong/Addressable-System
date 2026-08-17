using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Dynamic object pool that automatically grows and shrinks based on usage patterns
    /// Wraps an IObjectPool implementation and adds auto-sizing behavior
    /// </summary>
    /// <remarks>
    /// P-17: shrinking is a two-step state machine (arm, then — once
    /// <see cref="DynamicPoolConfig.ShrinkDelaySeconds"/> has elapsed — fire), and it used to be
    /// reachable only from <see cref="Release"/>. That made the one case shrinking exists for
    /// unreachable: a pool whose whole wave has been despawned and which is then left alone gets no
    /// further <c>Release</c> calls, so the armed shrink never fired and the instances stayed
    /// resident for the rest of the session. <see cref="EvaluateAutoResize"/> is the missing
    /// clock-driven entry point; <c>AddressablePoolManager</c> pumps it (see
    /// <c>PoolMaintenancePump</c>), and any host that would rather drive it from its own update loop
    /// can call it directly.
    /// </remarks>
    public class DynamicPool<T> : IObjectPool<T>, IResizablePool<T>, IMeasuredResizablePool<T>,
        IReclaimablePool<T>, IDisposable where T : class
    {
        private readonly IObjectPool<T> _innerPool;
        private readonly DynamicPoolConfig _config;
        private readonly string _poolName;

        // Usage tracking
        private int _currentCapacity;
        private int _peakActiveCount;
        private float _lastShrinkCheckTime;
        private bool _shrinkPending;
        private bool _disposed;

        // Callbacks for dynamic creation/destruction. Both are validated non-null by the constructor
        // and neither is invoked directly any more: growth and shrinkage go through the inner pool's
        // own createFunc/onDestroy via IResizablePool (P-4), which is the only way to keep that
        // pool's accounting straight. They are kept as part of the public constructor contract.
        //
        // P-28: the `Stack<T> _excessInstances` that used to sit here is gone. It was declared,
        // cleared in Clear(), and drained in Dispose() — and never pushed to, by anything, ever. The
        // drain loop was unreachable code that made this class look like it held shrink overflow
        // (instances taken out of the inner pool but not yet destroyed) when it never has: shrinkage
        // destroys through the inner pool immediately, in ShrinkPool.
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onDestroy;

        public DynamicPool(
            IObjectPool<T> innerPool,
            DynamicPoolConfig config,
            Func<T> createFunc,
            Action<T> onDestroy,
            string poolName = "DynamicPool")
        {
            _innerPool = innerPool ?? throw new ArgumentNullException(nameof(innerPool));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
            _onDestroy = onDestroy ?? throw new ArgumentNullException(nameof(onDestroy));
            _poolName = poolName;

            // Validate config
            if (!_config.Validate(out var error))
            {
                throw new ArgumentException($"Invalid DynamicPoolConfig: {error}");
            }

            _currentCapacity = _config.InitialCapacity;
            _peakActiveCount = 0;
            _lastShrinkCheckTime = Time.realtimeSinceStartup;

            if (_config.LogResizeOperations)
            {
                // P-18: "capacity" here is the auto-resize controller's budget, not a population.
                // Nothing has been instantiated at this point and the free list is empty; say so,
                // because the old wording ("Created with capacity 10") read as though ten instances
                // existed.
                Debug.Log($"[DynamicPool:{_poolName}] Created with a resize budget of {_currentCapacity} " +
                         $"(min: {_config.MinSize}, max: {_config.MaxSize}). No instances exist yet — " +
                         "capacity is the denominator the grow/shrink thresholds are measured against; " +
                         "use preloadCount / Prewarm to actually populate the pool.");
            }
        }

        /// <summary>
        /// P-15: use-after-dispose logs an error and returns <c>null</c>. It used to throw
        /// <see cref="ObjectDisposedException"/> from here while <see cref="Release"/> two methods
        /// down merely warned, and while both shipped adapters returned <c>null</c> through the very
        /// same <see cref="IObjectPool{T}"/> reference. See <see cref="IObjectPool{T}"/>'s remarks
        /// for why "log and return neutral" is the contract the package settled on.
        /// </summary>
        public T Get()
        {
            if (_disposed)
            {
                Debug.LogError($"[DynamicPool:{_poolName}] Cannot get from a disposed pool");
                return null;
            }

            var item = _innerPool.Get();

            // Check if we need to grow
            if (_config.EnableAutoResize)
            {
                CheckForGrowth();
            }

            return item;
        }

        public void Release(T item)
        {
            if (_disposed)
            {
                Debug.LogWarning($"[DynamicPool:{_poolName}] Cannot release to disposed pool");
                return;
            }

            if (item == null)
            {
                Debug.LogWarning($"[DynamicPool:{_poolName}] Cannot release null item");
                return;
            }

            _innerPool.Release(item);

            // Check if we need to shrink
            if (_config.EnableAutoResize)
            {
                CheckForShrinkage();
            }
        }

        /// <inheritdoc cref="IObjectPool{T}.Clear"/>
        public void Clear()
        {
            if (_disposed) return;

            _innerPool.Clear();
            _currentCapacity = _config.InitialCapacity;
            _peakActiveCount = 0;
            _shrinkPending = false;
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            if (_disposed) return (0, 0);
            return _innerPool.GetStats();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _innerPool?.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Clock-driven half of the auto-resize state machine (P-17). Safe and cheap to call every
        /// frame or every few seconds: it reads the inner pool's stats and does arithmetic, and the
        /// <see cref="DynamicPoolConfig.ShrinkDelaySeconds"/> gate inside
        /// <see cref="CheckForShrinkage"/> is what decides whether anything is actually destroyed.
        /// </summary>
        /// <remarks>
        /// Only shrinkage is driven from here. Growth is demand-driven by construction — it can only
        /// ever be needed as a consequence of a <see cref="Get"/>, and <see cref="Get"/> already
        /// checks — whereas shrinkage is *absence*-driven, and absence produces no calls at all.
        /// That asymmetry is the whole bug: the state machine needed two <see cref="Release"/> calls
        /// at least <see cref="DynamicPoolConfig.ShrinkDelaySeconds"/> apart, so a pool that went
        /// quiet — the definition of "should shrink" — never got the second one.
        /// </remarks>
        public void EvaluateAutoResize()
        {
            if (_disposed || !_config.EnableAutoResize) return;

            CheckForShrinkage();
        }

        /// <summary>
        /// Check if pool should grow based on usage
        /// </summary>
        private void CheckForGrowth()
        {
            var stats = _innerPool.GetStats();
            var activeCount = stats.activeCount;

            // Track peak usage
            if (activeCount > _peakActiveCount)
            {
                _peakActiveCount = activeCount;
            }

            // Calculate usage ratio
            float usageRatio = _currentCapacity > 0 ? (float)activeCount / _currentCapacity : 0f;

            // Grow if usage exceeds threshold and we haven't hit max size
            if (usageRatio >= _config.GrowThreshold && _currentCapacity < _config.MaxSize)
            {
                int growAmount = Mathf.CeilToInt(_currentCapacity * _config.GrowFactor);
                growAmount = Mathf.Max(1, growAmount); // Grow by at least 1

                int newCapacity = Mathf.Min(_currentCapacity + growAmount, _config.MaxSize);
                int actualGrowth = newCapacity - _currentCapacity;

                if (actualGrowth > 0)
                {
                    GrowPool(actualGrowth);
                    _currentCapacity = newCapacity;

                    if (_config.LogResizeOperations)
                    {
                        Debug.Log($"[DynamicPool:{_poolName}] Grew pool by {actualGrowth} " +
                                 $"(new capacity: {_currentCapacity}, usage: {usageRatio:P0})");
                    }

                    // Reset shrink tracking after growth
                    _shrinkPending = false;
                    _lastShrinkCheckTime = Time.realtimeSinceStartup;
                }
            }
        }

        /// <summary>
        /// Check if pool should shrink based on sustained low usage
        /// </summary>
        private void CheckForShrinkage()
        {
            var stats = _innerPool.GetStats();
            var activeCount = stats.activeCount;
            var pooledCount = stats.pooledCount;

            // Calculate usage ratio
            float usageRatio = _currentCapacity > 0 ? (float)activeCount / _currentCapacity : 0f;

            // Check if usage is below shrink threshold
            if (usageRatio < _config.ShrinkThreshold && _currentCapacity > _config.MinSize)
            {
                float timeSinceLastCheck = Time.realtimeSinceStartup - _lastShrinkCheckTime;

                // Start tracking for potential shrink
                if (!_shrinkPending)
                {
                    _shrinkPending = true;
                    _lastShrinkCheckTime = Time.realtimeSinceStartup;
                }
                // If usage has been low for long enough, perform shrink
                else if (timeSinceLastCheck >= _config.ShrinkDelaySeconds)
                {
                    // Calculate shrink amount based on excess pooled capacity
                    int targetCapacity = Mathf.Max(
                        _config.MinSize,
                        Mathf.CeilToInt(_peakActiveCount * 1.5f) // Keep 50% buffer above peak
                    );

                    if (targetCapacity < _currentCapacity)
                    {
                        // CeilToInt to match CheckForGrowth's GrowFactor arithmetic; the Max(1, ...)
                        // below is what actually guarantees progress on a small gap either way, so
                        // this is a consistency change, not a rate change. The rate problem P-17
                        // describes ("one instance per 30 seconds") is fixed by EvaluateAutoResize
                        // being reachable at all, not by this line.
                        //
                        // P-20 is why ShrinkFactor can no longer be 0 here: a zero factor produced 0
                        // and then silently shrank by the Max(1, ...) floor anyway, so "never shrink"
                        // was inexpressible. Validate() now rejects it and points at
                        // EnableAutoResize = false, which is the honest way to say that.
                        int shrinkAmount = Mathf.CeilToInt((_currentCapacity - targetCapacity) * _config.ShrinkFactor);
                        shrinkAmount = Mathf.Max(1, shrinkAmount); // Shrink by at least 1

                        int newCapacity = Mathf.Max(_currentCapacity - shrinkAmount, _config.MinSize);
                        int actualShrink = _currentCapacity - newCapacity;

                        if (actualShrink > 0 && pooledCount > 0)
                        {
                            int destroyed = ShrinkPool(actualShrink);
                            _currentCapacity = newCapacity;

                            if (_config.LogResizeOperations)
                            {
                                Debug.Log($"[DynamicPool:{_poolName}] Shrunk pool by {actualShrink} " +
                                         $"(destroyed {destroyed} pooled instance(s), new capacity: " +
                                         $"{_currentCapacity}, usage was: {usageRatio:P0})");
                            }
                        }
                    }

                    // Reset tracking
                    _shrinkPending = false;
                    _lastShrinkCheckTime = Time.realtimeSinceStartup;
                    _peakActiveCount = activeCount; // Reset peak after shrink
                }
            }
            else
            {
                // Usage is above threshold, reset shrink tracking
                if (_shrinkPending)
                {
                    _shrinkPending = false;
                    _lastShrinkCheckTime = Time.realtimeSinceStartup;
                }
            }
        }

        /// <summary>
        /// Grow pool by creating and pre-populating instances (HANDOFF_TO_SESSION_B.md P-4).
        /// Returns how many instances were actually added (P-27).
        /// </summary>
        /// <remarks>
        /// Prefers <see cref="IMeasuredResizablePool{T}.PrewarmMeasured"/>, then
        /// <see cref="IResizablePool{T}.Prewarm"/> plus a stats delta, on the inner pool — each
        /// shipped adapter implements those to keep its own active/pooled accounting correct. Falls
        /// back to a Get-then-Release-N loop (never <c>_innerPool.Release(_createFunc())</c> on an
        /// instance that never went through <c>Get()</c> — that was the old bug, and it corrupts
        /// <see cref="Adapters.CustomPoolAdapter{T}"/> outright) for a third-party
        /// <see cref="IObjectPool{T}"/> that predates <see cref="IResizablePool{T}"/> — this is the
        /// same pattern P-3 uses for the non-dynamic preload path, and it is safe for any conforming
        /// <see cref="IObjectPool{T}"/> because it only ever calls the base Get/Release contract.
        /// </remarks>
        private int GrowPool(int amount)
        {
            if (amount <= 0) return 0;

            if (_innerPool is IMeasuredResizablePool<T> measured)
            {
                return measured.PrewarmMeasured(amount);
            }

            if (_innerPool is IResizablePool<T> resizable)
            {
                int before = _innerPool.GetStats().pooledCount;
                resizable.Prewarm(amount);
                return Mathf.Max(0, _innerPool.GetStats().pooledCount - before);
            }

            // Real Get()-then-Release() pairs through the inner pool's own contract: Get() lets the
            // inner pool run its own createFunc/onGet bookkeeping, and the matching Release() below
            // is never called on an instance that pool didn't hand out itself. Calling
            // _createFunc() directly here and pushing the result straight into _innerPool.Release()
            // (the old bug) releases an object the inner pool never Get()'d — CustomPoolAdapter's
            // Release() rejects that outright (it isn't in _activeObjects) and just leaks the
            // instance forever, and any other third-party IObjectPool<T> with equivalent bookkeeping
            // would do the same.
            int pooledBefore = _innerPool.GetStats().pooledCount;
            var created = new List<T>(amount);
            try
            {
                for (int i = 0; i < amount; i++)
                {
                    var item = _innerPool.Get();
                    if (item != null) created.Add(item);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DynamicPool:{_poolName}] Error creating instance during growth: {ex.Message}");
            }
            finally
            {
                // P-13's shape, on the fallback path: whatever was already borrowed has to go back
                // even when the loop above died partway, or those instances stay "active" forever.
                foreach (var item in created)
                {
                    _innerPool.Release(item);
                }
            }

            return Mathf.Max(0, _innerPool.GetStats().pooledCount - pooledBefore);
        }

        /// <summary>
        /// Shrink pool by removing excess pooled instances (HANDOFF_TO_SESSION_B.md P-4). Returns
        /// how many instances were actually evicted (P-27).
        /// </summary>
        /// <remarks>
        /// Prefers <see cref="IMeasuredResizablePool{T}.TrimExcessMeasured"/> /
        /// <see cref="IResizablePool{T}.TrimExcess"/> for the same reason as <see cref="GrowPool"/>.
        /// Unlike growth, there is no safe generic fallback here: the old "Get() then destroy without
        /// Release()" loop is exactly the bug that drove <c>activeCount</c> negative, and there is no
        /// way to evict a specific pooled instance through the base <see cref="IObjectPool{T}"/>
        /// contract alone without either activating it (Get) or destroying real capacity accounting.
        /// A third-party pool that doesn't implement <see cref="IResizablePool{T}"/> simply doesn't
        /// shrink — reported stats staying slightly too large is strictly better than
        /// <c>activeCount</c> going negative.
        /// </remarks>
        private int ShrinkPool(int amount)
        {
            if (amount <= 0) return 0;

            var stats = _innerPool.GetStats();
            int availableToRemove = Mathf.Min(amount, stats.pooledCount);
            if (availableToRemove <= 0) return 0;

            if (_innerPool is IMeasuredResizablePool<T> measured)
            {
                return measured.TrimExcessMeasured(availableToRemove);
            }

            if (_innerPool is IResizablePool<T> resizable)
            {
                resizable.TrimExcess(availableToRemove);
                return Mathf.Max(0, stats.pooledCount - _innerPool.GetStats().pooledCount);
            }

            Debug.LogWarning($"[DynamicPool:{_poolName}] Inner pool does not implement " +
                $"{nameof(IResizablePool<T>)}; skipping shrink rather than corrupting its " +
                "active/pooled accounting (see HANDOFF_TO_SESSION_B.md P-4).");
            return 0;
        }

        /// <summary>
        /// P-3/P-4: pre-populate <paramref name="count"/> instances without disturbing
        /// <see cref="_peakActiveCount"/> or <see cref="_currentCapacity"/> — routes through the same
        /// primitive <see cref="GrowPool"/> uses instead of a Get/Release loop, so a preload call
        /// can never be mistaken for real usage by the auto-resize heuristics.
        /// </summary>
        public void Prewarm(int count) => PrewarmMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.PrewarmMeasured"/>
        public int PrewarmMeasured(int count)
        {
            // P-15: no ObjectDisposedException — see Get().
            if (_disposed)
            {
                Debug.LogError($"[DynamicPool:{_poolName}] Cannot prewarm a disposed pool");
                return 0;
            }

            return GrowPool(count);
        }

        /// <summary>
        /// P-4: evict up to <paramref name="count"/> pooled instances via the same primitive
        /// <see cref="ShrinkPool"/> uses.
        /// </summary>
        /// <remarks>
        /// P-18: this is a direct order from the caller and is deliberately NOT floored by
        /// <see cref="DynamicPoolConfig.MinSize"/>. <c>MinSize</c> constrains the auto-resize
        /// controller's own capacity budget (see <see cref="CheckForShrinkage"/>); it has never been
        /// a guaranteed minimum population, and an explicit trim can empty the free list completely.
        /// </remarks>
        public void TrimExcess(int count) => TrimExcessMeasured(count);

        /// <inheritdoc cref="IMeasuredResizablePool{T}.TrimExcessMeasured"/>
        public int TrimExcessMeasured(int count)
        {
            if (_disposed)
            {
                Debug.LogError($"[DynamicPool:{_poolName}] Cannot trim a disposed pool");
                return 0;
            }

            return ShrinkPool(count);
        }

        /// <inheritdoc cref="IReclaimablePool{T}.ForgetActive"/>
        /// <remarks>
        /// P-8: forwards to the inner pool, because that is where the active accounting this pool's
        /// grow/shrink heuristics read from actually lives — a borrowed instance destroyed out in
        /// the world inflates <c>GetStats().activeCount</c>, which is
        /// <see cref="CheckForGrowth"/>'s numerator and <see cref="CheckForShrinkage"/>'s, so
        /// leaving it uncorrected pins the pool at <see cref="DynamicPoolConfig.MaxSize"/> forever.
        /// Also clamps <see cref="_peakActiveCount"/>, which would otherwise keep the shrink target
        /// permanently inflated by the phantom.
        /// </remarks>
        public bool ForgetActive(T instance)
        {
            if (_disposed || instance == null) return false;

            if (!(_innerPool is IReclaimablePool<T> reclaimable)) return false;

            if (!reclaimable.ForgetActive(instance)) return false;

            if (_peakActiveCount > 0) _peakActiveCount--;
            return true;
        }

        /// <summary>
        /// Get current dynamic pool statistics
        /// </summary>
        public DynamicPoolStats GetDynamicStats()
        {
            var baseStats = _innerPool.GetStats();
            return new DynamicPoolStats
            {
                ActiveCount = baseStats.activeCount,
                PooledCount = baseStats.pooledCount,
                CurrentCapacity = _currentCapacity,
                PeakActiveCount = _peakActiveCount,
                MinCapacity = _config.MinSize,
                MaxCapacity = _config.MaxSize,
                IsShrinkPending = _shrinkPending,
                TimeSinceShrinkCheck = Time.realtimeSinceStartup - _lastShrinkCheckTime
            };
        }

        /// <summary>
        /// Force immediate resize to target capacity
        /// </summary>
        public void ResizeTo(int targetCapacity)
        {
            if (_disposed)
            {
                Debug.LogError($"[DynamicPool:{_poolName}] Cannot resize a disposed pool");
                return;
            }

            targetCapacity = Mathf.Clamp(targetCapacity, _config.MinSize, _config.MaxSize);

            int delta = targetCapacity - _currentCapacity;

            if (delta > 0)
            {
                int added = GrowPool(delta);
                if (_config.LogResizeOperations)
                {
                    Debug.Log($"[DynamicPool:{_poolName}] Manual grow to capacity {targetCapacity} " +
                        $"(pooled {added} of the {delta} requested)");
                }
            }
            else if (delta < 0)
            {
                int destroyed = ShrinkPool(-delta);
                if (_config.LogResizeOperations)
                {
                    Debug.Log($"[DynamicPool:{_poolName}] Manual shrink to capacity {targetCapacity} " +
                        $"(destroyed {destroyed} of the {-delta} requested)");
                }
            }

            _currentCapacity = targetCapacity;
            _shrinkPending = false;
            _lastShrinkCheckTime = Time.realtimeSinceStartup;
        }
    }

    /// <summary>
    /// Extended statistics for dynamic pools
    /// </summary>
    /// <remarks>
    /// P-18: <see cref="CurrentCapacity"/> is the auto-resize controller's budget, NOT a count of
    /// instances that exist. A freshly created pool reports <c>CurrentCapacity == 10</c> and
    /// <c>TotalCount == 0</c>, and that is correct — see <see cref="DynamicPoolConfig.InitialCapacity"/>.
    /// <see cref="UsageRatio"/> is therefore "how loaded the controller thinks the pool is", not
    /// "what fraction of existing instances are checked out"; for the real population use
    /// <see cref="ActiveCount"/> / <see cref="PooledCount"/> / <see cref="TotalCount"/>, which come
    /// straight from the inner pool.
    /// </remarks>
    public struct DynamicPoolStats
    {
        public int ActiveCount;
        public int PooledCount;

        /// <summary>
        /// The auto-resize budget, not a population count (P-18).
        /// </summary>
        public int CurrentCapacity;

        public int PeakActiveCount;
        public int MinCapacity;
        public int MaxCapacity;
        public bool IsShrinkPending;
        public float TimeSinceShrinkCheck;

        public float UsageRatio => CurrentCapacity > 0 ? (float)ActiveCount / CurrentCapacity : 0f;
        public int TotalCount => ActiveCount + PooledCount;

        public override string ToString()
        {
            return $"Active: {ActiveCount}/{CurrentCapacity} ({UsageRatio:P0}), " +
                   $"Pooled: {PooledCount}, Peak: {PeakActiveCount}, " +
                   $"Range: [{MinCapacity}-{MaxCapacity}]";
        }
    }
}
