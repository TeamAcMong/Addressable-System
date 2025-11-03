using System;
using System.Collections.Generic;
using UnityEngine;
using AddressableManager.Loaders;
using AddressableManager.Monitoring;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Hybrid scope pattern - supports both singleton and multiple named instances
    ///
    /// Benefits:
    /// - Start with singleton (simple)
    /// - Upgrade to named instances when needed (flexible)
    /// - No code changes required when switching modes
    /// - Backward compatible with existing singleton scopes
    ///
    /// Usage Examples:
    ///
    /// // Singleton mode (default):
    /// var loader = HybridScope.Session.Loader;
    ///
    /// // Named instance mode:
    /// var player1Loader = HybridScope.GetNamed("Session", "Player1").Loader;
    /// var player2Loader = HybridScope.GetNamed("Session", "Player2").Loader;
    ///
    /// // Clear specific instance:
    /// HybridScope.ClearNamed("Session", "Player1");
    ///
    /// // Clear all instances:
    /// HybridScope.ClearAllNamed("Session");
    /// </summary>
    public class HybridScope : IAssetScope, IDisposable
    {
        // Singleton instances
        private static HybridScope _globalInstance;
        private static HybridScope _sessionInstance;
        private static readonly Dictionary<string, HybridScope> _namedInstances = new Dictionary<string, HybridScope>();
        private static readonly object _lock = new object();

        private readonly AssetLoader _loader;
        private readonly string _scopeType;
        private readonly string _instanceName;
        private bool _disposed;

        /// <summary>
        /// Global singleton scope (persistent, DontDestroyOnLoad)
        /// </summary>
        public static HybridScope Global
        {
            get
            {
                lock (_lock)
                {
                    if (_globalInstance == null)
                    {
                        _globalInstance = new HybridScope("Global", null);
                        AssetMonitorBridge.ReportScopeRegistered("Global", true);
                    }
                    return _globalInstance;
                }
            }
        }

        /// <summary>
        /// Session singleton scope (gameplay session lifetime)
        /// </summary>
        public static HybridScope Session
        {
            get
            {
                lock (_lock)
                {
                    if (_sessionInstance == null)
                    {
                        _sessionInstance = new HybridScope("Session", null);
                        AssetMonitorBridge.ReportScopeRegistered("Session", true);
                    }
                    return _sessionInstance;
                }
            }
        }

        /// <summary>
        /// Get or create a named instance of a scope type
        /// </summary>
        /// <param name="scopeType">Type of scope (Global, Session, Custom, etc.)</param>
        /// <param name="instanceName">Unique name for this instance</param>
        public static HybridScope GetNamed(string scopeType, string instanceName)
        {
            if (string.IsNullOrEmpty(scopeType))
                throw new ArgumentNullException(nameof(scopeType));

            if (string.IsNullOrEmpty(instanceName))
                throw new ArgumentNullException(nameof(instanceName));

            lock (_lock)
            {
                string key = $"{scopeType}:{instanceName}";

                if (!_namedInstances.TryGetValue(key, out var instance))
                {
                    instance = new HybridScope(scopeType, instanceName);
                    _namedInstances[key] = instance;

                    Debug.Log($"[HybridScope] Created named instance: {key}");
                    AssetMonitorBridge.ReportScopeRegistered(key, true);
                }

                return instance;
            }
        }

        /// <summary>
        /// Check if a named instance exists
        /// </summary>
        public static bool HasNamed(string scopeType, string instanceName)
        {
            lock (_lock)
            {
                string key = $"{scopeType}:{instanceName}";
                return _namedInstances.ContainsKey(key);
            }
        }

        /// <summary>
        /// Clear specific named instance
        /// </summary>
        public static void ClearNamed(string scopeType, string instanceName)
        {
            lock (_lock)
            {
                string key = $"{scopeType}:{instanceName}";

                if (_namedInstances.TryGetValue(key, out var instance))
                {
                    instance.Dispose();
                    _namedInstances.Remove(key);

                    Debug.Log($"[HybridScope] Cleared named instance: {key}");
                    AssetMonitorBridge.ReportScopeCleared(key);
                }
            }
        }

        /// <summary>
        /// Clear all named instances of a specific scope type
        /// </summary>
        public static void ClearAllNamed(string scopeType)
        {
            lock (_lock)
            {
                var keysToRemove = new List<string>();

                foreach (var kvp in _namedInstances)
                {
                    if (kvp.Key.StartsWith(scopeType + ":"))
                    {
                        kvp.Value.Dispose();
                        keysToRemove.Add(kvp.Key);
                        AssetMonitorBridge.ReportScopeCleared(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _namedInstances.Remove(key);
                }

                Debug.Log($"[HybridScope] Cleared {keysToRemove.Count} named instances of type: {scopeType}");
            }
        }

        /// <summary>
        /// Get all named instance keys
        /// </summary>
        public static IEnumerable<string> GetAllNamedKeys()
        {
            lock (_lock)
            {
                return new List<string>(_namedInstances.Keys);
            }
        }

        /// <summary>
        /// Clear session singleton (common operation)
        /// </summary>
        public static void ClearSessionSingleton()
        {
            lock (_lock)
            {
                if (_sessionInstance != null)
                {
                    _sessionInstance.Dispose();
                    _sessionInstance = null;

                    Debug.Log("[HybridScope] Cleared Session singleton");
                    AssetMonitorBridge.ReportScopeCleared("Session");
                }
            }
        }

        /// <summary>
        /// Clear all hybrid scopes (singletons + named instances)
        /// Warning: This clears Global scope too!
        /// </summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                // Clear singletons
                _globalInstance?.Dispose();
                _globalInstance = null;

                _sessionInstance?.Dispose();
                _sessionInstance = null;

                // Clear named instances
                foreach (var kvp in _namedInstances)
                {
                    kvp.Value.Dispose();
                    AssetMonitorBridge.ReportScopeCleared(kvp.Key);
                }

                _namedInstances.Clear();

                Debug.Log("[HybridScope] Cleared all scopes (singletons + named instances)");
            }
        }

        #region Instance Members

        private HybridScope(string scopeType, string instanceName)
        {
            _scopeType = scopeType;
            _instanceName = instanceName;

            // Create loader with appropriate scope name for monitoring
            string loaderName = string.IsNullOrEmpty(instanceName)
                ? scopeType
                : $"{scopeType}:{instanceName}";

            _loader = new AssetLoader(loaderName);
        }

        /// <summary>
        /// Asset loader for this scope
        /// </summary>
        public AssetLoader Loader
        {
            get
            {
                if (_disposed)
                    throw new ObjectDisposedException(GetScopeName());

                return _loader;
            }
        }

        /// <summary>
        /// Get the full scope name (includes instance name if any)
        /// </summary>
        public string GetScopeName()
        {
            return string.IsNullOrEmpty(_instanceName)
                ? _scopeType
                : $"{_scopeType}:{_instanceName}";
        }

        /// <summary>
        /// Get scope type (Global, Session, etc.)
        /// </summary>
        public string ScopeType => _scopeType;

        /// <summary>
        /// Get instance name (null for singletons)
        /// </summary>
        public string InstanceName => _instanceName;

        /// <summary>
        /// Check if this is a singleton instance
        /// </summary>
        public bool IsSingleton => string.IsNullOrEmpty(_instanceName);

        /// <summary>
        /// Check if this is a named instance
        /// </summary>
        public bool IsNamed => !string.IsNullOrEmpty(_instanceName);

        /// <summary>
        /// Clear this scope's cache
        /// </summary>
        public void ClearCache()
        {
            if (!_disposed)
            {
                _loader.ClearCache();
            }
        }

        /// <summary>
        /// Get cache statistics for this scope
        /// </summary>
        public (int cachedAssets, int activeHandles) GetCacheStats()
        {
            if (_disposed)
                return (0, 0);

            return _loader.GetCacheStats();
        }

        public void Dispose()
        {
            if (_disposed) return;

            Debug.Log($"[HybridScope] Disposing scope: {GetScopeName()}");
            _loader?.ClearCache();
            _loader?.Dispose();
            _disposed = true;
        }

        #endregion

        #region Static Utility Methods

        /// <summary>
        /// Get total number of active hybrid scopes
        /// </summary>
        public static int GetTotalScopeCount()
        {
            lock (_lock)
            {
                int count = 0;
                if (_globalInstance != null) count++;
                if (_sessionInstance != null) count++;
                count += _namedInstances.Count;
                return count;
            }
        }

        /// <summary>
        /// Get statistics for all hybrid scopes
        /// </summary>
        public static HybridScopeStats GetGlobalStats()
        {
            lock (_lock)
            {
                var stats = new HybridScopeStats
                {
                    SingletonCount = 0,
                    NamedInstanceCount = _namedInstances.Count,
                    TotalCachedAssets = 0,
                    TotalActiveHandles = 0
                };

                // Count singletons
                if (_globalInstance != null)
                {
                    stats.SingletonCount++;
                    var (cached, active) = _globalInstance.GetCacheStats();
                    stats.TotalCachedAssets += cached;
                    stats.TotalActiveHandles += active;
                }

                if (_sessionInstance != null)
                {
                    stats.SingletonCount++;
                    var (cached, active) = _sessionInstance.GetCacheStats();
                    stats.TotalCachedAssets += cached;
                    stats.TotalActiveHandles += active;
                }

                // Count named instances
                foreach (var instance in _namedInstances.Values)
                {
                    var (cached, active) = instance.GetCacheStats();
                    stats.TotalCachedAssets += cached;
                    stats.TotalActiveHandles += active;
                }

                return stats;
            }
        }

        #endregion
    }

    /// <summary>
    /// Statistics for hybrid scope system
    /// </summary>
    public struct HybridScopeStats
    {
        public int SingletonCount;
        public int NamedInstanceCount;
        public int TotalCachedAssets;
        public int TotalActiveHandles;

        public int TotalScopes => SingletonCount + NamedInstanceCount;

        public override string ToString()
        {
            return $"HybridScopes: {TotalScopes} total ({SingletonCount} singletons, {NamedInstanceCount} named), " +
                   $"Cached: {TotalCachedAssets}, Active: {TotalActiveHandles}";
        }
    }
}
