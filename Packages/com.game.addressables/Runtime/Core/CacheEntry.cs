using System;
using UnityEngine;

namespace AddressableManager.Core
{
    /// <summary>
    /// Cache entry with metadata for tiered caching
    /// Tracks access patterns and tier information
    /// </summary>
    internal class CacheEntry<T> where T : class
    {
        /// <summary>
        /// The cached asset handle
        /// </summary>
        public IAssetHandle<T> Handle { get; }

        /// <summary>
        /// Current cache tier
        /// </summary>
        public CacheTier Tier { get; set; }

        /// <summary>
        /// Number of times this entry has been accessed
        /// </summary>
        public int AccessCount { get; private set; }

        /// <summary>
        /// Last access time (Time.realtimeSinceStartup)
        /// </summary>
        public float LastAccessTime { get; private set; }

        /// <summary>
        /// Time when entry was first cached
        /// </summary>
        public float CreationTime { get; }

        /// <summary>
        /// Estimated memory size in bytes
        /// </summary>
        public long EstimatedSize { get; }

        /// <summary>
        /// Cache key for this entry
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Whether this entry is pinned (cannot be evicted)
        /// </summary>
        public bool IsPinned { get; set; }

        public CacheEntry(string key, IAssetHandle<T> handle, long estimatedSize)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            EstimatedSize = estimatedSize;

            CreationTime = Time.realtimeSinceStartup;
            LastAccessTime = CreationTime;
            AccessCount = 1; // First access is the load itself
            Tier = CacheTier.Hot; // Start in Hot tier
            IsPinned = false;
        }

        /// <summary>
        /// Record an access to this entry
        /// Updates access count and last access time
        /// </summary>
        public void RecordAccess()
        {
            AccessCount++;
            LastAccessTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Get age of this entry in seconds
        /// </summary>
        public float GetAge()
        {
            return Time.realtimeSinceStartup - CreationTime;
        }

        /// <summary>
        /// Get time since last access in seconds
        /// </summary>
        public float GetTimeSinceLastAccess()
        {
            return Time.realtimeSinceStartup - LastAccessTime;
        }

        /// <summary>
        /// Calculate access frequency (accesses per second)
        /// </summary>
        public float GetAccessFrequency()
        {
            float age = GetAge();
            return age > 0 ? AccessCount / age : AccessCount;
        }

        /// <summary>
        /// Calculate a score for tier determination
        /// Higher score = should be in higher tier (Hot)
        /// </summary>
        public float CalculateTierScore()
        {
            if (IsPinned)
                return float.MaxValue; // Pinned entries always stay Hot

            float timeSinceAccess = GetTimeSinceLastAccess();
            float frequency = GetAccessFrequency();

            // Score formula: higher frequency and recent access = higher score
            // Recency weight: newer accesses are more valuable
            float recencyWeight = 1.0f / (1.0f + timeSinceAccess);
            float frequencyWeight = Mathf.Log(1.0f + frequency);

            return recencyWeight * frequencyWeight * 100f;
        }

        public override string ToString()
        {
            return $"[{Tier}] {Key} - Accesses: {AccessCount}, " +
                   $"Frequency: {GetAccessFrequency():F2}/s, " +
                   $"Last access: {GetTimeSinceLastAccess():F1}s ago, " +
                   $"Size: {EstimatedSize / 1024}KB";
        }
    }
}
