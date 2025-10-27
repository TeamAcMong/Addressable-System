using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Editor.Data
{
    /// <summary>
    /// Collects and analyzes performance metrics for addressable operations
    /// </summary>
    public class PerformanceMetrics
    {
        private static PerformanceMetrics _instance;
        public static PerformanceMetrics Instance => _instance ?? (_instance = new PerformanceMetrics());

        // Time-series data for graphing
        public class MetricSnapshot
        {
            public DateTime Timestamp { get; set; }
            public long TotalMemory { get; set; }
            public int ActiveAssets { get; set; }
            public float AverageLoadTime { get; set; }
            public float CacheHitRatio { get; set; }
        }

        public class LoadTimeEntry
        {
            public string Address { get; set; }
            public string TypeName { get; set; }
            public float LoadTime { get; set; }
            public DateTime Timestamp { get; set; }
            public bool FromCache { get; set; }
        }

        private readonly List<MetricSnapshot> _snapshots = new();
        private readonly List<LoadTimeEntry> _loadTimeHistory = new();
        private readonly Dictionary<string, List<float>> _loadTimesByAddress = new();

        private const int MaxSnapshots = 1000; // Keep last 1000 snapshots (~16 minutes at 1s interval)
        private const int MaxLoadHistory = 500; // Keep last 500 load operations

        public IReadOnlyList<MetricSnapshot> Snapshots => _snapshots;
        public IReadOnlyList<LoadTimeEntry> LoadTimeHistory => _loadTimeHistory;

        /// <summary>
        /// Record a metric snapshot
        /// </summary>
        public void RecordSnapshot()
        {
            var tracker = AssetTrackerService.Instance;

            var snapshot = new MetricSnapshot
            {
                Timestamp = DateTime.Now,
                TotalMemory = tracker.TotalMemoryUsage,
                ActiveAssets = tracker.TrackedAssets.Count(kv => kv.Value.IsValid),
                AverageLoadTime = tracker.AverageLoadTime,
                CacheHitRatio = tracker.CacheHitRatio
            };

            _snapshots.Add(snapshot);

            // Keep only recent snapshots
            if (_snapshots.Count > MaxSnapshots)
            {
                _snapshots.RemoveAt(0);
            }
        }

        /// <summary>
        /// Record a load time entry
        /// </summary>
        public void RecordLoadTime(string address, string typeName, float loadTime, bool fromCache)
        {
            var entry = new LoadTimeEntry
            {
                Address = address,
                TypeName = typeName,
                LoadTime = loadTime,
                Timestamp = DateTime.Now,
                FromCache = fromCache
            };

            _loadTimeHistory.Add(entry);

            // Track by address for analysis
            var key = $"{address}_{typeName}";
            if (!_loadTimesByAddress.ContainsKey(key))
            {
                _loadTimesByAddress[key] = new List<float>();
            }
            _loadTimesByAddress[key].Add(loadTime);

            // Keep only recent entries
            if (_loadTimeHistory.Count > MaxLoadHistory)
            {
                _loadTimeHistory.RemoveAt(0);
            }
        }

        /// <summary>
        /// Get average load time for a specific asset
        /// </summary>
        public float GetAverageLoadTime(string address, string typeName)
        {
            var key = $"{address}_{typeName}";
            if (_loadTimesByAddress.TryGetValue(key, out var times) && times.Count > 0)
            {
                return times.Average();
            }
            return 0f;
        }

        /// <summary>
        /// Get slowest loading assets
        /// </summary>
        public List<(string address, string typeName, float avgTime)> GetSlowestAssets(int count = 10)
        {
            return _loadTimesByAddress
                .Select(kv =>
                {
                    var parts = kv.Key.Split('_');
                    var address = parts.Length > 1 ? string.Join("_", parts.Take(parts.Length - 1)) : kv.Key;
                    var typeName = parts.Length > 1 ? parts[parts.Length - 1] : "Unknown";
                    var avgTime = kv.Value.Average();
                    return (address, typeName, avgTime);
                })
                .OrderByDescending(x => x.avgTime)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Get memory usage trend (increasing, decreasing, stable)
        /// </summary>
        public MemoryTrend GetMemoryTrend(int sampleCount = 30)
        {
            if (_snapshots.Count < sampleCount)
                return MemoryTrend.Insufficient_Data;

            var recentSnapshots = _snapshots.TakeLast(sampleCount).ToList();
            var firstHalf = recentSnapshots.Take(sampleCount / 2).Average(s => s.TotalMemory);
            var secondHalf = recentSnapshots.Skip(sampleCount / 2).Average(s => s.TotalMemory);

            var changePercent = ((secondHalf - firstHalf) / firstHalf) * 100;

            if (Math.Abs(changePercent) < 5) // Less than 5% change
                return MemoryTrend.Stable;
            else if (changePercent > 0)
                return MemoryTrend.Increasing;
            else
                return MemoryTrend.Decreasing;
        }

        /// <summary>
        /// Get performance summary
        /// </summary>
        public PerformanceSummary GetSummary()
        {
            var tracker = AssetTrackerService.Instance;

            return new PerformanceSummary
            {
                TotalAssets = tracker.TrackedAssets.Count,
                ActiveAssets = tracker.TrackedAssets.Count(kv => kv.Value.IsValid),
                TotalMemory = tracker.TotalMemoryUsage,
                AverageLoadTime = tracker.AverageLoadTime,
                CacheHitRatio = tracker.CacheHitRatio,
                TotalCacheHits = tracker.CacheHits,
                TotalCacheMisses = tracker.CacheMisses,
                MemoryTrend = GetMemoryTrend(),
                SlowestAssets = GetSlowestAssets(5)
            };
        }

        /// <summary>
        /// Clear all metrics
        /// </summary>
        public void Clear()
        {
            _snapshots.Clear();
            _loadTimeHistory.Clear();
            _loadTimesByAddress.Clear();
        }

        /// <summary>
        /// Export data to CSV
        /// </summary>
        public string ExportToCSV()
        {
            var csv = "Timestamp,Total Memory (MB),Active Assets,Avg Load Time (s),Cache Hit Ratio\n";

            foreach (var snapshot in _snapshots)
            {
                csv += $"{snapshot.Timestamp:yyyy-MM-dd HH:mm:ss}," +
                       $"{snapshot.TotalMemory / (1024f * 1024f):F2}," +
                       $"{snapshot.ActiveAssets}," +
                       $"{snapshot.AverageLoadTime:F3}," +
                       $"{snapshot.CacheHitRatio:F2}\n";
            }

            return csv;
        }

        public enum MemoryTrend
        {
            Insufficient_Data,
            Stable,
            Increasing,
            Decreasing
        }

        public class PerformanceSummary
        {
            public int TotalAssets { get; set; }
            public int ActiveAssets { get; set; }
            public long TotalMemory { get; set; }
            public float AverageLoadTime { get; set; }
            public float CacheHitRatio { get; set; }
            public int TotalCacheHits { get; set; }
            public int TotalCacheMisses { get; set; }
            public MemoryTrend MemoryTrend { get; set; }
            public List<(string address, string typeName, float avgTime)> SlowestAssets { get; set; }
        }
    }
}
