using System;

namespace AddressableManager.Core
{
    /// <summary>
    /// Type-erased surface of <see cref="TieredCache{T}"/>. Lets a caller that holds one
    /// per-asset-type cache per <c>Type</c> (e.g. <c>TieredAssetLoader._tieredCaches</c>) dispatch
    /// across all of them without reflection.
    /// </summary>
    /// <remarks>
    /// Replaces three <c>cache.GetType().GetMethod(...).Invoke(...)</c> call sites that had no other
    /// static reference to <see cref="TieredCache{T}.GetStatistics"/>,
    /// <see cref="TieredCache{T}.ForceEvaluateTiers"/> or <see cref="TieredCache{T}.ForceEviction"/>
    /// anywhere in the package — exactly what managed code stripping and IL2CPP's generic-sharing
    /// analysis treat as unreachable and remove, at which point <c>GetMethod</c> silently returns
    /// null and eviction stops with no exception and no log line
    /// (HANDOFF_TO_SESSION_B.md L-8).
    /// </remarks>
    internal interface ITieredCache : IDisposable
    {
        /// <inheritdoc cref="TieredCache{T}.GetStatistics"/>
        TieredCacheStats GetStatistics();

        /// <inheritdoc cref="TieredCache{T}.ForceEvaluateTiers"/>
        void ForceEvaluateTiers();

        /// <inheritdoc cref="TieredCache{T}.ForceEviction"/>
        void ForceEviction();

        /// <summary>
        /// Hard-release every handle this cache holds, regardless of who else still holds a
        /// reference, and drop every entry. The teardown counterpart to
        /// <see cref="TieredCache{T}.Clear"/>, which only gives back the cache's own reference and
        /// leaves a handle any other holder retained untouched.
        /// </summary>
        void ForceReleaseAll();
    }
}
