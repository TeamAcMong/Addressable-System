namespace AddressableManager.Pooling
{
    /// <summary>
    /// Optional capability for an <see cref="IObjectPool{T}"/> implementation that can pre-populate
    /// or evict a specific number of pooled instances as one atomic step, keeping its own
    /// active/pooled accounting correct while doing so.
    /// </summary>
    /// <remarks>
    /// Added as a derived interface rather than new members on <see cref="IObjectPool{T}"/> itself
    /// (repo invariant 6): <see cref="IObjectPool{T}"/> is public and a third-party
    /// <see cref="IPoolFactory"/> may already implement it, so adding an abstract member there would
    /// be a source break. <see cref="DynamicPool{T}"/> checks for this interface on whatever inner
    /// pool it was handed and falls back to a slower-but-still-correct path when it is absent — see
    /// <c>DynamicPool.GrowPool</c> / <c>DynamicPool.ShrinkPool</c>.
    ///
    /// Why "pre-populate N" and "evict N" need to be their own operations instead of N calls to
    /// <see cref="IObjectPool{T}.Get"/> / <see cref="IObjectPool{T}.Release"/> in a loop
    /// (HANDOFF_TO_SESSION_B.md P-3/P-4): a naive Get-then-immediately-Release loop is
    /// indistinguishable, from the inner pool's point of view, from a caller borrowing and
    /// returning N instances — some adapters cannot represent "this instance was never actually
    /// active" any other way, and a naive Get-then-destroy-without-Release loop (the old
    /// <c>ShrinkPool</c>) permanently inflates the inner pool's active count because it never
    /// balances the Get with a matching Release.
    /// </remarks>
    /// <typeparam name="T">The pooled reference type.</typeparam>
    public interface IResizablePool<T> where T : class
    {
        /// <summary>
        /// Create and pool up to <paramref name="count"/> instances up front, without ever treating
        /// any of them as "active" — <see cref="IObjectPool{T}.GetStats"/>'s <c>activeCount</c> must
        /// read the same before and after a call to this method.
        /// </summary>
        void Prewarm(int count);

        /// <summary>
        /// Evict and destroy up to <paramref name="count"/> currently-pooled (inactive) instances.
        /// Never touches an instance the caller currently holds, and must not change
        /// <see cref="IObjectPool{T}.GetStats"/>'s <c>activeCount</c>.
        /// </summary>
        void TrimExcess(int count);
    }
}
