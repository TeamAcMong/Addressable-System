using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using AddressableManager.Scopes;
using AddressableManager.Core;

namespace AddressableManager.Editor.Data
{
    /// <summary>
    /// Centralized service for tracking all addressable assets in runtime
    /// Singleton pattern for Editor-time monitoring
    /// </summary>
    public class AssetTrackerService
    {
        private static AssetTrackerService _instance;
        public static AssetTrackerService Instance => _instance ?? (_instance = new AssetTrackerService());

        // Tracked asset information
        public class TrackedAsset
        {
            public string Address { get; set; }
            public string TypeName { get; set; }
            public int ReferenceCount { get; set; }
            public long MemorySize { get; set; } // Estimated in bytes
            public string ScopeName { get; set; }
            public DateTime LoadTime { get; set; }
            public float LoadDuration { get; set; } // Seconds
            public bool IsValid { get; set; }

            // For leak detection
            public int InitialRefCount { get; set; }
            public DateTime LastRefCountChange { get; set; }
        }

        // Scope information
        public class TrackedScope
        {
            public string ScopeName { get; set; }
            public bool IsActive { get; set; }
            public List<TrackedAsset> Assets { get; set; } = new List<TrackedAsset>();
            public long TotalMemory => Assets.Sum(a => a.MemorySize);
            public int AssetCount => Assets.Count;
        }

        private readonly Dictionary<string, TrackedAsset> _trackedAssets = new();
        private readonly Dictionary<string, TrackedScope> _trackedScopes = new();
        private readonly List<string> _loadOperations = new(); // Recent operations for stats

        // Events for UI updates
        public event Action OnAssetsChanged;
        public event Action<TrackedAsset> OnAssetLoaded;
        public event Action<string> OnAssetUnloaded;

        // Performance metrics
        private float _totalLoadTime;
        private int _cacheHits;
        private int _cacheMisses;

        public IReadOnlyDictionary<string, TrackedAsset> TrackedAssets => _trackedAssets;
        public IReadOnlyDictionary<string, TrackedScope> TrackedScopes => _trackedScopes;
        public float AverageLoadTime => _loadOperations.Count > 0 ? _totalLoadTime / _loadOperations.Count : 0;
        public int CacheHits => _cacheHits;
        public int CacheMisses => _cacheMisses;
        public float CacheHitRatio => (_cacheHits + _cacheMisses) > 0 ? (float)_cacheHits / (_cacheHits + _cacheMisses) : 0;
        public long TotalMemoryUsage => _trackedAssets.Values.Sum(a => a.MemorySize);

        private AssetTrackerService()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// Register an asset load
        /// </summary>
        public void RegisterAssetLoad(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        {
            var cacheKey = $"{address}_{typeName}";

            if (fromCache)
            {
                _cacheHits++;
                if (_trackedAssets.TryGetValue(cacheKey, out var existing))
                {
                    existing.ReferenceCount++;
                    existing.LastRefCountChange = DateTime.Now;
                }
            }
            else
            {
                _cacheMisses++;
                var asset = new TrackedAsset
                {
                    Address = address,
                    TypeName = typeName,
                    ReferenceCount = 1,
                    MemorySize = EstimateMemorySize(typeName),
                    ScopeName = scopeName,
                    LoadTime = DateTime.Now,
                    LoadDuration = loadDuration,
                    IsValid = true,
                    InitialRefCount = 1,
                    LastRefCountChange = DateTime.Now
                };

                _trackedAssets[cacheKey] = asset;
                _totalLoadTime += loadDuration;
                _loadOperations.Add(cacheKey);

                OnAssetLoaded?.Invoke(asset);
            }

            // Update scope tracking
            EnsureScopeExists(scopeName);
            UpdateScopeAssets(scopeName);

            OnAssetsChanged?.Invoke();
        }

        /// <summary>
        /// Register an asset release
        /// </summary>
        public void RegisterAssetRelease(string address, string typeName)
        {
            var cacheKey = $"{address}_{typeName}";

            if (_trackedAssets.TryGetValue(cacheKey, out var asset))
            {
                asset.ReferenceCount--;
                asset.LastRefCountChange = DateTime.Now;

                if (asset.ReferenceCount <= 0)
                {
                    asset.IsValid = false;
                    OnAssetUnloaded?.Invoke(cacheKey);
                }

                OnAssetsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Register a scope
        /// </summary>
        public void RegisterScope(string scopeName, bool isActive)
        {
            if (!_trackedScopes.ContainsKey(scopeName))
            {
                _trackedScopes[scopeName] = new TrackedScope
                {
                    ScopeName = scopeName,
                    IsActive = isActive
                };
            }
            else
            {
                _trackedScopes[scopeName].IsActive = isActive;
            }

            OnAssetsChanged?.Invoke();
        }

        /// <summary>
        /// Update scope active state
        /// </summary>
        public void UpdateScopeState(string scopeName, bool isActive)
        {
            if (_trackedScopes.TryGetValue(scopeName, out var scope))
            {
                scope.IsActive = isActive;
                OnAssetsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Clear a specific scope
        /// </summary>
        public void ClearScope(string scopeName)
        {
            if (_trackedScopes.TryGetValue(scopeName, out var scope))
            {
                // Mark all assets in this scope as invalid
                foreach (var asset in scope.Assets.ToList())
                {
                    var cacheKey = $"{asset.Address}_{asset.TypeName}";
                    if (_trackedAssets.TryGetValue(cacheKey, out var trackedAsset))
                    {
                        trackedAsset.IsValid = false;
                        trackedAsset.ReferenceCount = 0;
                    }
                }

                scope.Assets.Clear();
                OnAssetsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Detect potential memory leaks
        /// Assets that have been loaded for more than the specified time without ref count changes
        /// </summary>
        public List<TrackedAsset> DetectPotentialLeaks(int minutesThreshold = 5)
        {
            var threshold = DateTime.Now.AddMinutes(-minutesThreshold);

            return _trackedAssets.Values
                .Where(a => a.IsValid &&
                           a.ReferenceCount > 0 &&
                           a.LastRefCountChange < threshold &&
                           a.ReferenceCount == a.InitialRefCount)
                .ToList();
        }

        /// <summary>
        /// Get assets by scope
        /// </summary>
        public List<TrackedAsset> GetAssetsByScope(string scopeName)
        {
            if (_trackedScopes.TryGetValue(scopeName, out var scope))
            {
                return scope.Assets;
            }
            return new List<TrackedAsset>();
        }

        /// <summary>
        /// Clear all tracking data
        /// </summary>
        public void Clear()
        {
            _trackedAssets.Clear();
            _trackedScopes.Clear();
            _loadOperations.Clear();
            _totalLoadTime = 0;
            _cacheHits = 0;
            _cacheMisses = 0;
            OnAssetsChanged?.Invoke();
        }

        /// <summary>
        /// Reset statistics
        /// </summary>
        public void ResetStats()
        {
            _totalLoadTime = 0;
            _cacheHits = 0;
            _cacheMisses = 0;
            _loadOperations.Clear();
            OnAssetsChanged?.Invoke();
        }

        private void EnsureScopeExists(string scopeName)
        {
            if (!_trackedScopes.ContainsKey(scopeName))
            {
                _trackedScopes[scopeName] = new TrackedScope
                {
                    ScopeName = scopeName,
                    IsActive = true
                };
            }
        }

        private void UpdateScopeAssets(string scopeName)
        {
            if (_trackedScopes.TryGetValue(scopeName, out var scope))
            {
                scope.Assets = _trackedAssets.Values
                    .Where(a => a.ScopeName == scopeName && a.IsValid)
                    .ToList();
            }
        }

        private long EstimateMemorySize(string typeName)
        {
            // Rough estimates based on common Unity types
            // In production, you could use Profiler.GetRuntimeMemorySizeLong if available
            switch (typeName)
            {
                case "Texture2D":
                    return 1024 * 1024; // 1MB average
                case "AudioClip":
                    return 512 * 1024; // 512KB average
                case "GameObject":
                case "Prefab":
                    return 256 * 1024; // 256KB average
                case "Material":
                    return 64 * 1024; // 64KB average
                case "Mesh":
                    return 128 * 1024; // 128KB average
                case "ScriptableObject":
                    return 32 * 1024; // 32KB average
                default:
                    return 100 * 1024; // 100KB default
            }
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                // Clear tracking data when exiting play mode
                Clear();
            }
        }
    }
}
