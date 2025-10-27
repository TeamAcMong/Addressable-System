using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AddressableManager.Loaders;
using AddressableManager.Monitoring;

namespace AddressableManager.Managers
{
    /// <summary>
    /// Advanced scope manager for complex applications
    /// Allows multiple named scopes instead of singletons
    ///
    /// Use this when:
    /// - You need multiple sessions (PlayerSession, GameSession, etc.)
    /// - You want fine-grained control over scope lifecycle
    /// - Built-in singleton scopes are too limiting
    ///
    /// Example:
    ///   var playerLoader = ScopeManager.Instance.GetOrCreateScope("PlayerSession");
    ///   var gameLoader = ScopeManager.Instance.GetOrCreateScope("GameSession");
    ///
    ///   await playerLoader.LoadAssetAsyncMonitored<T>(address, "PlayerSession");
    /// </summary>
    public class ScopeManager
    {
        private static ScopeManager _instance;
        public static ScopeManager Instance => _instance ??= new ScopeManager();

        private readonly Dictionary<string, AssetLoader> _loaders = new Dictionary<string, AssetLoader>();

        /// <summary>
        /// Get all active scope IDs
        /// </summary>
        public IEnumerable<string> ActiveScopes => _loaders.Keys;

        /// <summary>
        /// Get or create a scope with the given ID
        /// </summary>
        public AssetLoader GetOrCreateScope(string scopeId)
        {
            if (string.IsNullOrEmpty(scopeId))
            {
                Debug.LogError("[ScopeManager] Scope ID cannot be null or empty");
                return null;
            }

            if (!_loaders.ContainsKey(scopeId))
            {
                var loader = new AssetLoader();
                _loaders[scopeId] = loader;

                Debug.Log($"[ScopeManager] Created scope: {scopeId}");

                // Report to monitoring
                AssetMonitorBridge.ReportScopeRegistered(scopeId, true);
            }

            return _loaders[scopeId];
        }

        /// <summary>
        /// Check if scope exists
        /// </summary>
        public bool HasScope(string scopeId)
        {
            return _loaders.ContainsKey(scopeId);
        }

        /// <summary>
        /// Get existing scope (returns null if doesn't exist)
        /// </summary>
        public AssetLoader GetScope(string scopeId)
        {
            _loaders.TryGetValue(scopeId, out var loader);
            return loader;
        }

        /// <summary>
        /// Clear and dispose a specific scope
        /// </summary>
        public void ClearScope(string scopeId)
        {
            if (_loaders.TryGetValue(scopeId, out var loader))
            {
                Debug.Log($"[ScopeManager] Clearing scope: {scopeId}");

                loader.ClearCache();
                loader.Dispose();
                _loaders.Remove(scopeId);

                // Report to monitoring
                AssetMonitorBridge.ReportScopeCleared(scopeId);
            }
            else
            {
                Debug.LogWarning($"[ScopeManager] Scope not found: {scopeId}");
            }
        }

        /// <summary>
        /// Clear all scopes except the specified ones
        /// </summary>
        public void ClearAllExcept(params string[] keepScopes)
        {
            var toRemove = _loaders.Keys
                .Where(k => !keepScopes.Contains(k))
                .ToList();

            foreach (var scopeId in toRemove)
            {
                ClearScope(scopeId);
            }

            Debug.Log($"[ScopeManager] Cleared {toRemove.Count} scopes, kept {keepScopes.Length}");
        }

        /// <summary>
        /// Clear all scopes except Global
        /// </summary>
        public void ClearAllExceptGlobal()
        {
            ClearAllExcept("Global");
        }

        /// <summary>
        /// Clear all scopes
        /// </summary>
        public void ClearAll()
        {
            var count = _loaders.Count;

            foreach (var loader in _loaders.Values.ToList())
            {
                loader.ClearCache();
                loader.Dispose();
            }

            foreach (var scopeId in _loaders.Keys.ToList())
            {
                AssetMonitorBridge.ReportScopeCleared(scopeId);
            }

            _loaders.Clear();

            Debug.Log($"[ScopeManager] Cleared all {count} scopes");
        }

        /// <summary>
        /// Get memory usage for a specific scope (estimated)
        /// </summary>
        public long GetScopeMemoryUsage(string scopeId)
        {
            // This is a rough estimate
            // In Dashboard, you'll see actual tracked memory
            return 0; // Placeholder - real tracking happens in AssetTrackerService
        }

        /// <summary>
        /// Get total count of active scopes
        /// </summary>
        public int ActiveScopeCount => _loaders.Count;
    }
}
