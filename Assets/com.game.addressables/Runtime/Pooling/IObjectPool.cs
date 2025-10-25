using System;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Generic object pool interface - agnostic to specific pooling implementation
    /// This allows switching between Unity's ObjectPool, custom pools, or third-party solutions
    /// </summary>
    public interface IObjectPool<T> : IDisposable where T : class
    {
        /// <summary>
        /// Get an object from the pool
        /// </summary>
        T Get();

        /// <summary>
        /// Return an object to the pool
        /// </summary>
        void Release(T obj);

        /// <summary>
        /// Clear all pooled objects
        /// </summary>
        void Clear();

        /// <summary>
        /// Get current pool statistics
        /// </summary>
        (int activeCount, int pooledCount) GetStats();
    }
}
