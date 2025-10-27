using System;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Factory interface for creating object pools
    /// Allows runtime switching between different pooling implementations
    /// </summary>
    public interface IPoolFactory
    {
        /// <summary>
        /// Create a new object pool
        /// </summary>
        /// <param name="createFunc">Function to create new instances</param>
        /// <param name="onGet">Action when getting from pool</param>
        /// <param name="onRelease">Action when returning to pool</param>
        /// <param name="onDestroy">Action when destroying pooled object</param>
        /// <param name="maxSize">Maximum pool size (0 = unlimited)</param>
        IObjectPool<T> CreatePool<T>(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int maxSize = 100
        ) where T : class;
    }
}
