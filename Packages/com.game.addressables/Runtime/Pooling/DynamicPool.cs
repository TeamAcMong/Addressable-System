using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Dynamic object pool that automatically grows and shrinks based on usage patterns
    /// Wraps an IObjectPool implementation and adds auto-sizing behavior
    /// </summary>
    public class DynamicPool<T> : IObjectPool<T>, IDisposable where T : class
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

        // Callbacks for dynamic creation/destruction
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onDestroy;

        // Track excess instances for shrinking
        private readonly Stack<T> _excessInstances = new Stack<T>();

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
                Debug.Log($"[DynamicPool:{_poolName}] Created with capacity {_currentCapacity} " +
                         $"(min: {_config.MinSize}, max: {_config.MaxSize})");
            }
        }

        public T Get()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DynamicPool<T>));

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

        public void Clear()
        {
            _innerPool.Clear();
            _excessInstances.Clear();
            _currentCapacity = _config.InitialCapacity;
            _peakActiveCount = 0;
            _shrinkPending = false;
        }

        public (int activeCount, int pooledCount) GetStats()
        {
            return _innerPool.GetStats();
        }

        public void Dispose()
        {
            if (_disposed) return;

            // Clear excess instances
            while (_excessInstances.Count > 0)
            {
                var item = _excessInstances.Pop();
                _onDestroy?.Invoke(item);
            }

            _innerPool?.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Check if pool should grow based on usage
        /// </summary>
        private void CheckForGrowth()
        {
            var stats = _innerPool.GetStats();
            var activeCount = stats.activeCount;
            var totalCount = activeCount + stats.pooledCount;

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
                        int shrinkAmount = Mathf.FloorToInt((_currentCapacity - targetCapacity) * _config.ShrinkFactor);
                        shrinkAmount = Mathf.Max(1, shrinkAmount); // Shrink by at least 1

                        int newCapacity = Mathf.Max(_currentCapacity - shrinkAmount, _config.MinSize);
                        int actualShrink = _currentCapacity - newCapacity;

                        if (actualShrink > 0 && pooledCount > 0)
                        {
                            ShrinkPool(actualShrink);
                            _currentCapacity = newCapacity;

                            if (_config.LogResizeOperations)
                            {
                                Debug.Log($"[DynamicPool:{_poolName}] Shrunk pool by {actualShrink} " +
                                         $"(new capacity: {_currentCapacity}, usage was: {usageRatio:P0})");
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
        /// Grow pool by creating and pre-populating instances
        /// </summary>
        private void GrowPool(int amount)
        {
            for (int i = 0; i < amount; i++)
            {
                try
                {
                    var item = _createFunc();
                    if (item != null)
                    {
                        _innerPool.Release(item);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DynamicPool:{_poolName}] Error creating instance during growth: {ex.Message}");
                    break;
                }
            }
        }

        /// <summary>
        /// Shrink pool by removing excess pooled instances
        /// </summary>
        private void ShrinkPool(int amount)
        {
            var stats = _innerPool.GetStats();
            int availableToRemove = Mathf.Min(amount, stats.pooledCount);

            for (int i = 0; i < availableToRemove; i++)
            {
                try
                {
                    // Get from pool and destroy instead of releasing back
                    var item = _innerPool.Get();
                    if (item != null)
                    {
                        _onDestroy?.Invoke(item);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DynamicPool:{_poolName}] Error destroying instance during shrink: {ex.Message}");
                    break;
                }
            }
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
            targetCapacity = Mathf.Clamp(targetCapacity, _config.MinSize, _config.MaxSize);

            int delta = targetCapacity - _currentCapacity;

            if (delta > 0)
            {
                GrowPool(delta);
                if (_config.LogResizeOperations)
                {
                    Debug.Log($"[DynamicPool:{_poolName}] Manual grow to capacity {targetCapacity}");
                }
            }
            else if (delta < 0)
            {
                ShrinkPool(-delta);
                if (_config.LogResizeOperations)
                {
                    Debug.Log($"[DynamicPool:{_poolName}] Manual shrink to capacity {targetCapacity}");
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
    public struct DynamicPoolStats
    {
        public int ActiveCount;
        public int PooledCount;
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
