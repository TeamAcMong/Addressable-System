using System;
using System.Collections.Generic;
using UnityEngine;
using AddressableManager.Loaders;
using AddressableManager.Managers;
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
    ///
    /// Storage identity (see Documentation/LIFETIME_DESIGN.md §5 step 6 and
    /// Documentation/HANDOFF_TO_SESSION_B.md A-12 for the full map): this is storage "D" — a
    /// third, independent scope mechanism next to <see cref="GlobalAssetScope"/>/<c>SceneAssetScope</c>
    /// (storage A/C) and <see cref="ScopeManager"/>'s own <c>"Session"</c> entry (storage B).
    /// <c>HybridScope.Global</c>/<c>.Session</c> share NO state with
    /// <see cref="AddressableManager.Facade.AddressablesFacade.GetGlobalScope"/> or
    /// <c>AddressablesFacade.GetSessionLoader()</c> despite the identical names — each instance
    /// reports to monitoring and to <see cref="ScopeManager"/>'s directory under a
    /// <c>"Hybrid:"</c>-prefixed id (<c>"Hybrid:Global"</c>, <c>"Hybrid:Session"</c>,
    /// <c>"Hybrid:{type}:{name}"</c>) precisely so it never collides with storage A/B/C's own
    /// channels on the dashboard.
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
                    // Treat a disposed instance as absent, not just a null one — belt-and-braces
                    // for anyone who called Dispose() directly on the singleton (Deactivate() no
                    // longer does; see its docs) instead of going through ClearAll(). Without this
                    // the next Global access would hand back the disposed husk and every Loader
                    // read off it would throw ObjectDisposedException forever (A-6b).
                    if (_globalInstance == null || _globalInstance._disposed)
                    {
                        _globalInstance = new HybridScope("Global", null);
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
                    // See Global's comment — same disposed-as-absent guard.
                    if (_sessionInstance == null || _sessionInstance._disposed)
                    {
                        _sessionInstance = new HybridScope("Session", null);
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

                // See Global's comment — treat a disposed entry as absent rather than handing
                // back a husk. ClearNamed() removes the dictionary entry on Dispose, but a direct
                // Dispose() call on the returned instance would not (A-6b).
                if (!_namedInstances.TryGetValue(key, out var instance) || instance._disposed)
                {
                    instance = new HybridScope(scopeType, instanceName);
                    _namedInstances[key] = instance;

                    Debug.Log($"[HybridScope] Created named instance: {key}");
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
                    // Dispose() reports to monitoring / unregisters from ScopeManager's directory
                    // itself now (using the "Hybrid:" id) — no separate report here.
                    instance.Dispose();
                    _namedInstances.Remove(key);

                    Debug.Log($"[HybridScope] Cleared named instance: {key}");
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
                        // Dispose() reports to monitoring / the directory itself — see ClearNamed.
                        kvp.Value.Dispose();
                        keysToRemove.Add(kvp.Key);
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
                    // Dispose() reports to monitoring / the directory itself — see ClearNamed.
                    _sessionInstance.Dispose();
                    _sessionInstance = null;

                    Debug.Log("[HybridScope] Cleared Session singleton");
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
                // Clear singletons. Dispose() reports to monitoring / the directory itself now.
                _globalInstance?.Dispose();
                _globalInstance = null;

                _sessionInstance?.Dispose();
                _sessionInstance = null;

                // Clear named instances
                foreach (var kvp in _namedInstances)
                {
                    kvp.Value.Dispose();
                }

                _namedInstances.Clear();

                Debug.Log("[HybridScope] Cleared all scopes (singletons + named instances)");
            }
        }

        // Reset the three static stores on domain reload (Editor) and at the start of a fresh
        // SubsystemRegistration in a build — the same job ScopeManager.ResetOnLoad already does
        // for its own statics (Managers/ScopeManager.cs). Without this hook, with domain reload
        // disabled (Enter Play Mode Options), _globalInstance/_sessionInstance/_namedInstances and
        // every AssetLoader + handle they hold survive into the next Play session
        // (HANDOFF_TO_SESSION_B.md A-6a). ClearAll() already disposes and nulls both singletons
        // and clears the named-instance dictionary, so this is mostly delegation — but the
        // explicit re-null after a caught exception is required: a throw partway through ClearAll
        // must not leave a static pointing at a half-disposed scope.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            try
            {
                ClearAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HybridScope] Reset failed: {ex}");
            }

            lock (_lock)
            {
                _globalInstance = null;
                _sessionInstance = null;
                _namedInstances.Clear();
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

            // Self-report once, here, rather than at each of the three call sites (Global,
            // Session, GetNamed) that used to do it separately — so every construction path
            // reports under the same "Hybrid:"-prefixed id (see the class docs' storage-identity
            // note / A-12). Registered with ScopeManager's directory as a foreign (not
            // manager-owned) entry: ClearAll()/ClearAllExcept() can reach this loader's cache, but
            // only this scope's own Dispose() may remove the entry or dispose the loader — the
            // one-owner rule (LIFETIME_DESIGN.md §1, §5 step 6).
            AssetMonitorBridge.ReportScopeRegistered(DirectoryId, true);
            ScopeManager.Instance.RegisterExternal(DirectoryId, _loader, this);
        }

        // "Hybrid:" prefixed so this scope's self-report never collides with the like-named
        // storage GlobalAssetScope / ScopeManager's own "Session" entry report under their own,
        // unprefixed ids — despite GetScopeName() returning the same "Global"/"Session"/"{type}:
        // {name}" text those use for their (unrelated) storages. See the class docs.
        private string DirectoryId => "Hybrid:" + GetScopeName();

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
        /// Name identifier for this scope (IAssetScope requirement)
        /// </summary>
        public string ScopeName => GetScopeName();

        /// <summary>
        /// Whether this scope is active (IAssetScope requirement)
        /// </summary>
        public bool IsActive => !_disposed;

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
        /// Activate this scope (IAssetScope requirement)
        /// For HybridScope, this is always active unless disposed
        /// </summary>
        public void Activate()
        {
            // HybridScope is always active when created
            // No-op for compatibility
        }

        /// <summary>
        /// Deactivate this scope (IAssetScope requirement) — clears its cache, same as
        /// <see cref="BaseAssetScope.Deactivate"/>. Used to alias <see cref="Dispose"/>, which left
        /// the owning static (<see cref="Global"/>/<see cref="Session"/>/a <c>GetNamed</c> entry)
        /// pointing at a disposed instance: the next access threw <see cref="ObjectDisposedException"/>
        /// forever instead of returning a usable scope (HANDOFF_TO_SESSION_B.md A-6b). This scope
        /// stays usable after Deactivate() — the next <see cref="Loader"/> access re-populates the
        /// cache exactly as it would for a freshly-created scope.
        /// </summary>
        public void Deactivate()
        {
            ClearCache();
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

            // Single source of truth for both reports now — every caller that used to report
            // these itself (Global/Session/GetNamed's creation, ClearNamed/ClearAllNamed/
            // ClearSessionSingleton/ClearAll's teardown) relies on this happening here instead.
            AssetMonitorBridge.ReportScopeCleared(DirectoryId);
            // Pass _loader so ScopeManager only removes the entry if it's still ours — see
            // BaseAssetScope.Dispose's identical comment (LIFETIME_DESIGN.md §1a).
            ScopeManager.Instance.UnregisterExternal(DirectoryId, _loader);

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
