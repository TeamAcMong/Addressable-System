namespace AddressableManager.Pooling
{
    /// <summary>
    /// An <see cref="IResizablePool{T}"/> that reports what a resize actually achieved, instead of
    /// leaving the caller to log the number it *asked* for (discovery report P-27).
    /// </summary>
    /// <remarks>
    /// Added as a further-derived interface rather than by changing
    /// <see cref="IResizablePool{T}.Prewarm"/>'s return type (repo invariant 6): both
    /// <see cref="IResizablePool{T}"/> and <see cref="IObjectPool{T}"/> are public and a third-party
    /// implementation may already exist, so changing a signature there would be a source break.
    /// Consumers probe for this interface first and fall back to
    /// <see cref="IResizablePool{T}"/> + a before/after <see cref="IObjectPool{T}.GetStats"/> delta.
    ///
    /// Why the achieved count matters and the requested count does not: a prewarm is capped by the
    /// pool's <c>maxSize</c>, and a trim is capped by how many instances are actually pooled. Logging
    /// the request as though it were the outcome is the exact "log lie" shape P-3 fixed on the
    /// non-dynamic preload path and that survived on the dynamic one, where
    /// <c>"Preloaded {preloadCount} instances"</c> was printed without ever measuring anything.
    /// </remarks>
    /// <typeparam name="T">The pooled reference type.</typeparam>
    public interface IMeasuredResizablePool<T> : IResizablePool<T> where T : class
    {
        /// <summary>
        /// Same contract as <see cref="IResizablePool{T}.Prewarm"/>, but returns how many instances
        /// were actually added to the pool — never more than <paramref name="count"/>, and less
        /// whenever <c>maxSize</c> or a failing create got in the way.
        /// </summary>
        int PrewarmMeasured(int count);

        /// <summary>
        /// Same contract as <see cref="IResizablePool{T}.TrimExcess"/>, but returns how many pooled
        /// instances were actually evicted — never more than <paramref name="count"/>, and never
        /// more than were pooled to begin with.
        /// </summary>
        int TrimExcessMeasured(int count);
    }
}
