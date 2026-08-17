using System;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// Generic object pool interface - agnostic to specific pooling implementation
    /// This allows switching between Unity's ObjectPool, custom pools, or third-party solutions
    /// </summary>
    /// <remarks>
    /// <para><b>Use after <see cref="IDisposable.Dispose"/> (discovery report P-15).</b> Every
    /// implementation in this package logs an error (or a warning, for the no-op methods) and
    /// returns a neutral value: <see cref="Get"/> returns <c>null</c>, <see cref="Release"/> and
    /// <see cref="Clear"/> do nothing, <see cref="GetStats"/> returns <c>(0, 0)</c>. None of them
    /// throw. Implementations MUST NOT throw <see cref="ObjectDisposedException"/> here: pools are
    /// torn down during application/scene shutdown, exactly when the last frame of gameplay code is
    /// still running, and the same call through the same interface used to throw on
    /// <c>DynamicPool</c> while returning <c>null</c> on both adapters — a divergence a caller had
    /// no way to see coming from the interface alone.</para>
    ///
    /// <para><b>Third-party implementations.</b> Two optional capabilities are declared as separate
    /// derived interfaces rather than as members here, because adding an abstract member to this
    /// interface would be a source break (repo invariant 6): <see cref="IResizablePool{T}"/> (plus
    /// <see cref="IMeasuredResizablePool{T}"/>) for prewarm/trim, and
    /// <see cref="IReclaimablePool{T}"/> for being told that a borrowed instance has been destroyed
    /// out in the world. <c>AddressablePoolManager</c> and <see cref="DynamicPool{T}"/> probe for
    /// them and degrade to a documented weaker behaviour when they are absent.</para>
    /// </remarks>
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
        /// Destroy every pooled (inactive) instance.
        /// </summary>
        /// <remarks>
        /// P-14: instances the caller currently holds are NOT affected and MUST keep counting toward
        /// <see cref="GetStats"/>'s <c>activeCount</c> until they are released. Clearing is about the
        /// free list; a borrow that is still on screen is still a borrow. (Unity's own
        /// <c>ObjectPool.Clear()</c> zeroes its <c>CountAll</c>, which reports the opposite;
        /// <see cref="Adapters.UnityPoolAdapter{T}.Clear"/> compensates for that so both shipped
        /// adapters answer alike.)
        /// </remarks>
        void Clear();

        /// <summary>
        /// Get current pool statistics: how many instances are currently borrowed, and how many are
        /// sitting in the free list.
        /// </summary>
        (int activeCount, int pooledCount) GetStats();
    }
}
