namespace AddressableManager.Pooling
{
    /// <summary>
    /// Optional capability for an <see cref="IObjectPool{T}"/> implementation that can be told an
    /// instance it handed out has ceased to exist outside the pool's control, so it can drop that
    /// instance from its own active accounting WITHOUT recycling the corpse (discovery report P-8).
    /// </summary>
    /// <remarks>
    /// This is not <see cref="IObjectPool{T}.Release"/>. <c>Release</c> means "the borrower is done
    /// with this instance, put it back on the free list"; a destroyed <c>GameObject</c> pushed back
    /// onto a free list is a corpse the next <c>Get()</c> would hand to a caller.
    /// <see cref="ForgetActive"/> means "this instance is gone; stop counting it as borrowed and
    /// never speak of it again".
    ///
    /// Why a pool needs to be told at all: <c>Spawn(address, pos, rot, someSceneTransform)</c>
    /// reparents a borrowed instance out from under the manager's <c>DontDestroyOnLoad</c> root
    /// (P-2 only ever protected instances sitting in the free list), so a
    /// <c>LoadSceneMode.Single</c> load — or gameplay code calling <c>Object.Destroy</c> directly
    /// instead of <c>Despawn</c> — destroys it while every pool in the chain still counts it as
    /// active. That inflated active count then feeds <c>DynamicPool.CheckForGrowth</c>, so the pool
    /// grows to <c>MaxSize</c> and can never shrink again: bad numbers feeding the controller that
    /// produced them.
    ///
    /// Added as its own interface rather than as members on <see cref="IObjectPool{T}"/> for repo
    /// invariant 6 — see <see cref="IResizablePool{T}"/>'s remarks for the same reasoning.
    /// </remarks>
    /// <typeparam name="T">The pooled reference type.</typeparam>
    public interface IReclaimablePool<T> where T : class
    {
        /// <summary>
        /// Drop <paramref name="instance"/> from this pool's active accounting without releasing it
        /// back onto the free list and without destroying it (the caller has established that it is
        /// already gone). Returns <c>true</c> when the pool's books changed as a result.
        /// </summary>
        /// <remarks>
        /// Must be idempotent-safe in the sense that calling it for an instance the pool does not
        /// consider active must not corrupt the counts. Implementations that cannot distinguish the
        /// two cases must document that, and callers must therefore call it exactly once per lost
        /// instance — which is what <c>AddressablePoolManager</c> guarantees by removing the
        /// instance from its own tracking maps in the same step.
        /// </remarks>
        bool ForgetActive(T instance);
    }
}
