namespace AddressableManager.Core
{
    /// <summary>
    /// Cache tier levels for tiered caching strategy
    /// </summary>
    public enum CacheTier
    {
        /// <summary>
        /// Hot cache - Recently accessed, keep in memory
        /// Highest priority, fastest access
        /// </summary>
        Hot = 0,

        /// <summary>
        /// Warm cache - Moderately accessed, keep but lower priority
        /// Medium priority, may be demoted to Cold if not accessed
        /// </summary>
        Warm = 1,

        /// <summary>
        /// Cold cache - Rarely accessed, candidate for eviction
        /// Lowest priority, first to be evicted when memory pressure
        /// </summary>
        Cold = 2
    }
}
