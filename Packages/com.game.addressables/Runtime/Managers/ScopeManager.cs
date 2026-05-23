using System;
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
    ///   await playerLoader.LoadAssetAsync<T>(address);
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
                // Pass scope ID to AssetLoader for automatic monitoring
                var loader = new AssetLoader(scopeId);
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
        /// Get memory usage for a specific scope.
        /// Not yet implemented at runtime — the live numbers live in the Editor Dashboard
        /// (AssetTrackerService); kept here as a forward-compatible signature.
        /// </summary>
        [Obsolete("Runtime memory tracking is not implemented yet. Always returns 0. The Editor Dashboard has live numbers.", false)]
        public long GetScopeMemoryUsage(string scopeId)
        {
            return 0;
        }

        /// <summary>
        /// Get total count of active scopes
        /// </summary>
        public int ActiveScopeCount => _loaders.Count;

        // Reset the static singleton on domain reload (Editor) and at the start of a fresh
        // SubsystemRegistration in a build. Without this the AssetLoader instances from
        // a previous Play session — along with whatever live handles they were holding —
        // would survive into the next session and leak.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            if (_instance != null)
            {
                try { _instance.ClearAll(); }
                catch { /* ignore — singleton state is being torn down */ }
            }
            _instance = null;
        }
    }
}
