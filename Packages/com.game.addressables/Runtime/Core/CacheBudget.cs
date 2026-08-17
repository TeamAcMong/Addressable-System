namespace AddressableManager.Core
{
    /// <summary>
    /// Byte accounting shared by every <see cref="TieredCache{T}"/> a single
    /// <see cref="AddressableManager.Loaders.TieredAssetLoader"/> owns — one instance per loader,
    /// not one per type.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS (HANDOFF_TO_SESSION_B.md L-4)
    ///
    /// Before this type, each per-type <c>TieredCache&lt;T&gt;</c> tracked its own
    /// <c>_currentCacheSize</c> and gated eviction against <c>_config.MaxCacheSizeBytes</c> in
    /// isolation. Every type got the same <see cref="Core.TieredCacheConfig"/> object, so every
    /// cache believed it alone owned the full budget — a loader juggling Texture2D, GameObject,
    /// AudioClip, Mesh and Sprite caches could sit at 6x its configured ceiling with every
    /// individual cache honestly reporting itself under 20% full and never evicting anything.
    ///
    /// This type moves the "how much is in use, and against what ceiling" question out of each
    /// per-type cache and into one place all of them share, so <c>Set()</c>'s eviction gate and
    /// <c>PerformEviction()</c>'s target size are computed against the loader's real total rather
    /// than one type's slice of it.
    ///
    /// <see cref="TieredCache{T}"/> keeps its own local byte count alongside this (see that type's
    /// <c>_currentCacheSize</c>) purely so <see cref="TieredCache{T}.GetStatistics"/> can still
    /// report a per-type figure for <see cref="AddressableManager.Loaders.TieredAssetLoader.GetCombinedStats"/>
    /// to sum — this class is the enforcement half, that field is the reporting half, and every
    /// mutation site touches both in lockstep.
    ///
    /// Reads <see cref="TieredCacheConfig.MaxCacheSizeBytes"/> live from the shared config rather
    /// than snapshotting it at construction — the field is public and mutable, and every existing
    /// call site in <see cref="TieredCache{T}"/> already read it live before this type existed, so
    /// this preserves that behaviour instead of quietly freezing it.
    /// </remarks>
    internal sealed class CacheBudget
    {
        private readonly TieredCacheConfig _config;
        private long _current;

        public CacheBudget(TieredCacheConfig config)
        {
            _config = config;
        }

        /// <summary>Total bytes currently admitted across every cache sharing this budget.</summary>
        public long Current => _current;

        /// <summary>The shared ceiling (0 = unlimited), read live from the config every call.</summary>
        public long Max => _config?.MaxCacheSizeBytes ?? 0;

        /// <summary>Fraction of <see cref="Max"/> currently in use; 0 when unlimited.</summary>
        public float UsageRatio => Max > 0 ? (float)_current / Max : 0f;

        /// <summary>
        /// Record an admission of <paramref name="amount"/> bytes. Always succeeds — this budget
        /// does not refuse an insert, it only tracks how full it now is; the caller (a cache's
        /// eviction gate) decides what to do with the result. Returns whether the budget is still
        /// within <see cref="Max"/> after admitting, so a caller that wants to skip an immediate
        /// eviction check on the common case can do so cheaply.
        /// </summary>
        public bool TryAdmit(long amount)
        {
            if (amount == 0) return true;

            _current += amount;
            return Max <= 0 || _current <= Max;
        }

        /// <summary>Give back <paramref name="amount"/> bytes previously admitted. Clamped at 0 so a
        /// bookkeeping mismatch can never drive the total negative.</summary>
        public void Give(long amount)
        {
            if (amount == 0) return;

            _current -= amount;
            if (_current < 0) _current = 0;
        }
    }
}
