using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressableManager.Core
{
    /// <summary>
    /// Runtime validation for asset loading operations
    /// Helps catch errors early during development
    /// </summary>
    public static class AssetValidator
    {
        private static ValidationMode _currentMode = ValidationMode.None;
        private static readonly Dictionary<string, int> _loadCounts = new Dictionary<string, int>();
        private static readonly Dictionary<string, HashSet<int>> _referenceTracking = new Dictionary<string, HashSet<int>>();
        private static readonly object _lock = new object();

        // Thresholds
        private const int DUPLICATE_LOAD_THRESHOLD = 3;
        private const int HIGH_REFCOUNT_THRESHOLD = 10;
        private const long MAX_CACHE_SIZE_WARNING = 500 * 1024 * 1024; // 500 MB

        /// <summary>
        /// Current validation mode
        /// </summary>
        public static ValidationMode CurrentMode
        {
            get => _currentMode;
            set => _currentMode = value;
        }

        /// <summary>
        /// Check if a specific validation is enabled
        /// </summary>
        public static bool IsEnabled(ValidationMode mode)
        {
            return (_currentMode & mode) == mode;
        }

        /// <summary>
        /// Enable specific validation mode
        /// </summary>
        public static void Enable(ValidationMode mode)
        {
            _currentMode |= mode;
            LogVerbose($"Enabled validation mode: {mode}");
        }

        /// <summary>
        /// Disable specific validation mode
        /// </summary>
        public static void Disable(ValidationMode mode)
        {
            _currentMode &= ~mode;
            LogVerbose($"Disabled validation mode: {mode}");
        }

        #region Address Validation

        /// <summary>
        /// Validate asset address before loading
        /// Returns true if valid, false if invalid
        /// </summary>
        public static bool ValidateAddress(string address, out string error)
        {
            error = null;

            if (!IsEnabled(ValidationMode.ValidateAddresses))
                return true;

            if (string.IsNullOrEmpty(address))
            {
                error = "Address cannot be null or empty";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            // Check for common mistakes
            if (address.Contains("\\"))
            {
                error = "Address contains backslashes - use forward slashes";
                Debug.LogWarning($"[AssetValidator] {error}: {address}");
            }

            if (address.StartsWith("/") || address.EndsWith("/"))
            {
                error = "Address should not start or end with slash";
                Debug.LogWarning($"[AssetValidator] {error}: {address}");
            }

            LogVerbose($"Validated address: {address}");
            return true;
        }

        #endregion

        #region AssetReference Validation

        /// <summary>
        /// Validate AssetReference before loading
        /// </summary>
        public static bool ValidateAssetReference(AssetReference assetReference, out string error)
        {
            error = null;

            if (!IsEnabled(ValidationMode.ValidateAssetReferences))
                return true;

            if (assetReference == null)
            {
                error = "AssetReference is null";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            if (!assetReference.RuntimeKeyIsValid())
            {
                error = "AssetReference runtime key is not valid";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            LogVerbose($"Validated AssetReference: {assetReference.AssetGUID}");
            return true;
        }

        #endregion

        #region Duplicate Load Tracking

        /// <summary>
        /// Record a load operation for duplicate detection
        /// </summary>
        public static void RecordLoad(string address)
        {
            if (!IsEnabled(ValidationMode.CheckDuplicateLoads))
                return;

            lock (_lock)
            {
                if (!_loadCounts.ContainsKey(address))
                {
                    _loadCounts[address] = 0;
                }

                _loadCounts[address]++;

                if (_loadCounts[address] >= DUPLICATE_LOAD_THRESHOLD)
                {
                    Debug.LogWarning($"[AssetValidator] Potential duplicate loading detected: {address} " +
                                   $"has been loaded {_loadCounts[address]} times. " +
                                   $"Consider caching this asset.");
                }
            }

            LogVerbose($"Recorded load: {address} (count: {_loadCounts[address]})");
        }

        /// <summary>
        /// Get load count for address
        /// </summary>
        public static int GetLoadCount(string address)
        {
            lock (_lock)
            {
                return _loadCounts.TryGetValue(address, out var count) ? count : 0;
            }
        }

        /// <summary>
        /// Reset load tracking
        /// </summary>
        public static void ResetLoadTracking()
        {
            lock (_lock)
            {
                _loadCounts.Clear();
            }
            LogVerbose("Reset load tracking");
        }

        #endregion

        #region Reference Counting Validation

        /// <summary>
        /// Track reference count changes
        /// </summary>
        public static void TrackReferenceChange(string address, int refCount, bool isRetain)
        {
            if (!IsEnabled(ValidationMode.CheckReferenceBalance))
                return;

            lock (_lock)
            {
                if (!_referenceTracking.ContainsKey(address))
                {
                    _referenceTracking[address] = new HashSet<int>();
                }

                _referenceTracking[address].Add(refCount);

                // Check for potential issues
                if (refCount > HIGH_REFCOUNT_THRESHOLD)
                {
                    Debug.LogWarning($"[AssetValidator] High reference count detected: {address} " +
                                   $"has ref count {refCount}. This might indicate a memory leak.");
                }

                if (refCount < 0)
                {
                    Debug.LogError($"[AssetValidator] Invalid reference count: {address} " +
                                 $"has ref count {refCount}. Release() called more than Retain()!");
                }
            }

            LogVerbose($"Tracked reference change: {address}, count: {refCount}, {'+'}{(isRetain ? "retain" : "release")}");
        }

        /// <summary>
        /// Get reference tracking info for address
        /// </summary>
        public static HashSet<int> GetReferenceHistory(string address)
        {
            lock (_lock)
            {
                return _referenceTracking.TryGetValue(address, out var history)
                    ? new HashSet<int>(history)
                    : new HashSet<int>();
            }
        }

        #endregion

        #region Memory Leak Detection

        /// <summary>
        /// Check for potential memory leaks
        /// </summary>
        public static void CheckMemoryLeaks<T>(Dictionary<string, T> cache) where T : class
        {
            if (!IsEnabled(ValidationMode.CheckMemoryLeaks))
                return;

            lock (_lock)
            {
                var suspiciousAssets = cache
                    .Where(kvp =>
                    {
                        if (kvp.Value is IAssetHandle<object> handle)
                        {
                            return handle.ReferenceCount > HIGH_REFCOUNT_THRESHOLD;
                        }
                        return false;
                    })
                    .ToList();

                if (suspiciousAssets.Any())
                {
                    Debug.LogWarning($"[AssetValidator] Potential memory leaks detected: " +
                                   $"{suspiciousAssets.Count} assets with high reference counts");

                    foreach (var kvp in suspiciousAssets)
                    {
                        if (kvp.Value is IAssetHandle<object> handle)
                        {
                            Debug.LogWarning($"  - {kvp.Key}: RefCount = {handle.ReferenceCount}");
                        }
                    }
                }
            }

            LogVerbose($"Memory leak check complete: {cache.Count} assets checked");
        }

        #endregion

        #region Cache Validation

        /// <summary>
        /// Validate cache size against limits
        /// </summary>
        public static void ValidateCacheSize(long currentSize, long maxSize)
        {
            if (!IsEnabled(ValidationMode.ValidateCacheLimits))
                return;

            if (currentSize > MAX_CACHE_SIZE_WARNING)
            {
                Debug.LogWarning($"[AssetValidator] Cache size is very large: {currentSize / (1024 * 1024)}MB. " +
                               $"Consider clearing unused assets.");
            }

            if (maxSize > 0 && currentSize > maxSize)
            {
                Debug.LogError($"[AssetValidator] Cache size ({currentSize / (1024 * 1024)}MB) " +
                             $"exceeds maximum ({maxSize / (1024 * 1024)}MB)!");
            }

            float usagePercent = maxSize > 0 ? (float)currentSize / maxSize * 100f : 0f;
            LogVerbose($"Cache size: {currentSize / (1024 * 1024)}MB / {maxSize / (1024 * 1024)}MB ({usagePercent:F1}%)");
        }

        #endregion

        #region Pool Validation

        /// <summary>
        /// Validate pool configuration
        /// </summary>
        public static bool ValidatePoolConfig(int preloadCount, int maxSize, out string error)
        {
            error = null;

            if (!IsEnabled(ValidationMode.ValidatePoolConfigs))
                return true;

            if (preloadCount < 0)
            {
                error = "Preload count cannot be negative";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            if (maxSize <= 0)
            {
                error = "Max pool size must be > 0";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            if (preloadCount > maxSize)
            {
                error = $"Preload count ({preloadCount}) exceeds max size ({maxSize})";
                Debug.LogError($"[AssetValidator] {error}");
                return false;
            }

            if (maxSize > 1000)
            {
                Debug.LogWarning($"[AssetValidator] Pool max size is very large: {maxSize}. " +
                               $"This might use excessive memory.");
            }

            LogVerbose($"Validated pool config: preload={preloadCount}, max={maxSize}");
            return true;
        }

        #endregion

        #region Thread Safety

        /// <summary>
        /// Check if current thread is main thread
        /// </summary>
        private static int? _mainThreadId;

        public static bool CheckThreadSafety(out string error)
        {
            error = null;

            if (!IsEnabled(ValidationMode.CheckThreadSafety))
                return true;

            // Capture main thread ID on first call
            if (_mainThreadId == null)
            {
                _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                return true;
            }

            bool isMainThread = System.Threading.Thread.CurrentThread.ManagedThreadId == _mainThreadId;

            if (!isMainThread)
            {
                error = $"Thread safety violation: Called from thread {System.Threading.Thread.CurrentThread.ManagedThreadId}, " +
                       $"expected main thread {_mainThreadId}";
                Debug.LogWarning($"[AssetValidator] {error}");
                return false;
            }

            LogVerbose("Thread safety check passed");
            return true;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Get validation statistics
        /// </summary>
        public static ValidationStats GetStatistics()
        {
            lock (_lock)
            {
                return new ValidationStats
                {
                    TotalTrackedAddresses = _loadCounts.Count,
                    TotalLoads = _loadCounts.Values.Sum(),
                    DuplicateLoadAddresses = _loadCounts.Count(kvp => kvp.Value >= DUPLICATE_LOAD_THRESHOLD),
                    TrackedReferences = _referenceTracking.Count,
                    CurrentMode = _currentMode
                };
            }
        }

        /// <summary>
        /// Reset all validation data
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _loadCounts.Clear();
                _referenceTracking.Clear();
            }

            LogVerbose("Reset all validation data");
        }

        #endregion

        #region Helpers

        private static void LogVerbose(string message)
        {
            if (IsEnabled(ValidationMode.VerboseLogging))
            {
                Debug.Log($"[AssetValidator] {message}");
            }
        }

        #endregion
    }

    /// <summary>
    /// Validation statistics
    /// </summary>
    public struct ValidationStats
    {
        public int TotalTrackedAddresses;
        public int TotalLoads;
        public int DuplicateLoadAddresses;
        public int TrackedReferences;
        public ValidationMode CurrentMode;

        public override string ToString()
        {
            return $"ValidationStats: {TotalTrackedAddresses} addresses tracked, {TotalLoads} total loads, " +
                   $"{DuplicateLoadAddresses} duplicate load warnings, {TrackedReferences} references tracked, " +
                   $"Mode: {CurrentMode}";
        }
    }
}
