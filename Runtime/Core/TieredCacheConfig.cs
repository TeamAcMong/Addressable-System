using System;

namespace AddressableManager.Core
{
    /// <summary>
    /// Configuration for tiered cache behavior
    /// </summary>
    [Serializable]
    public class TieredCacheConfig
    {
        /// <summary>
        /// Maximum total cache size in bytes (0 = unlimited)
        /// </summary>
        public long MaxCacheSizeBytes = 100 * 1024 * 1024; // 100 MB default

        /// <summary>
        /// Score threshold to promote from Cold to Warm
        /// </summary>
        public float PromoteToWarmThreshold = 5.0f;

        /// <summary>
        /// Score threshold to promote from Warm to Hot
        /// </summary>
        public float PromoteToHotThreshold = 15.0f;

        /// <summary>
        /// Score threshold to demote from Hot to Warm
        /// </summary>
        public float DemoteToWarmThreshold = 10.0f;

        /// <summary>
        /// Score threshold to demote from Warm to Cold
        /// </summary>
        public float DemoteToColdThreshold = 2.0f;

        /// <summary>
        /// How often to evaluate and adjust cache tiers (seconds)
        /// </summary>
        public float TierEvaluationInterval = 5.0f;

        /// <summary>
        /// When cache is full, evict entries with score below this threshold
        /// </summary>
        public float EvictionScoreThreshold = 1.0f;

        /// <summary>
        /// Target memory usage percentage before triggering eviction (0.0-1.0)
        /// </summary>
        public float EvictionTriggerRatio = 0.9f; // 90%

        /// <summary>
        /// Target memory usage after eviction (0.0-1.0)
        /// </summary>
        public float EvictionTargetRatio = 0.7f; // 70%

        /// <summary>
        /// Enable automatic tier adjustment based on access patterns
        /// </summary>
        public bool EnableAutoTiering = true;

        /// <summary>
        /// Enable automatic eviction when memory limit is reached
        /// </summary>
        public bool EnableAutoEviction = true;

        /// <summary>
        /// Log tier changes and evictions for debugging
        /// </summary>
        public bool LogTierOperations = false;

        /// <summary>
        /// Default balanced configuration
        /// </summary>
        public static TieredCacheConfig Default => new TieredCacheConfig
        {
            MaxCacheSizeBytes = 100 * 1024 * 1024, // 100 MB
            PromoteToWarmThreshold = 5.0f,
            PromoteToHotThreshold = 15.0f,
            DemoteToWarmThreshold = 10.0f,
            DemoteToColdThreshold = 2.0f,
            TierEvaluationInterval = 5.0f,
            EvictionScoreThreshold = 1.0f,
            EvictionTriggerRatio = 0.9f,
            EvictionTargetRatio = 0.7f,
            EnableAutoTiering = true,
            EnableAutoEviction = true,
            LogTierOperations = false
        };

        /// <summary>
        /// Aggressive configuration - keeps only frequently accessed assets
        /// Good for memory-constrained environments
        /// </summary>
        public static TieredCacheConfig Aggressive => new TieredCacheConfig
        {
            MaxCacheSizeBytes = 50 * 1024 * 1024, // 50 MB
            PromoteToWarmThreshold = 10.0f,
            PromoteToHotThreshold = 25.0f,
            DemoteToWarmThreshold = 15.0f,
            DemoteToColdThreshold = 5.0f,
            TierEvaluationInterval = 3.0f,
            EvictionScoreThreshold = 3.0f,
            EvictionTriggerRatio = 0.85f,
            EvictionTargetRatio = 0.6f,
            EnableAutoTiering = true,
            EnableAutoEviction = true,
            LogTierOperations = false
        };

        /// <summary>
        /// Lenient configuration - keeps assets longer
        /// Good for high-memory devices with stable usage patterns
        /// </summary>
        public static TieredCacheConfig Lenient => new TieredCacheConfig
        {
            MaxCacheSizeBytes = 200 * 1024 * 1024, // 200 MB
            PromoteToWarmThreshold = 2.0f,
            PromoteToHotThreshold = 8.0f,
            DemoteToWarmThreshold = 5.0f,
            DemoteToColdThreshold = 1.0f,
            TierEvaluationInterval = 10.0f,
            EvictionScoreThreshold = 0.5f,
            EvictionTriggerRatio = 0.95f,
            EvictionTargetRatio = 0.8f,
            EnableAutoTiering = true,
            EnableAutoEviction = true,
            LogTierOperations = false
        };

        /// <summary>
        /// Disabled configuration - no tiering, acts like traditional cache
        /// </summary>
        public static TieredCacheConfig Disabled => new TieredCacheConfig
        {
            MaxCacheSizeBytes = 0, // Unlimited
            EnableAutoTiering = false,
            EnableAutoEviction = false,
            LogTierOperations = false
        };

        /// <summary>
        /// Validate configuration values
        /// </summary>
        public bool Validate(out string error)
        {
            if (MaxCacheSizeBytes < 0)
            {
                error = "MaxCacheSizeBytes cannot be negative";
                return false;
            }

            if (PromoteToWarmThreshold < 0 || PromoteToHotThreshold < 0 ||
                DemoteToWarmThreshold < 0 || DemoteToColdThreshold < 0)
            {
                error = "Tier thresholds cannot be negative";
                return false;
            }

            if (PromoteToHotThreshold <= PromoteToWarmThreshold)
            {
                error = "PromoteToHotThreshold must be > PromoteToWarmThreshold";
                return false;
            }

            if (DemoteToWarmThreshold <= DemoteToColdThreshold)
            {
                error = "DemoteToWarmThreshold must be > DemoteToColdThreshold";
                return false;
            }

            if (TierEvaluationInterval <= 0)
            {
                error = "TierEvaluationInterval must be > 0";
                return false;
            }

            if (EvictionTriggerRatio < 0 || EvictionTriggerRatio > 1.0f)
            {
                error = "EvictionTriggerRatio must be between 0 and 1";
                return false;
            }

            if (EvictionTargetRatio < 0 || EvictionTargetRatio > 1.0f)
            {
                error = "EvictionTargetRatio must be between 0 and 1";
                return false;
            }

            if (EvictionTargetRatio >= EvictionTriggerRatio)
            {
                error = "EvictionTargetRatio must be < EvictionTriggerRatio";
                return false;
            }

            error = null;
            return true;
        }
    }
}
