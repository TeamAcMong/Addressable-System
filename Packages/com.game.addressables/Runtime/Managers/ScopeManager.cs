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
    ///
    /// Also the package-wide scope directory (see Documentation/LIFETIME_DESIGN.md §5 step 6 and
    /// Documentation/HANDOFF_TO_SESSION_B.md A-7/A-12 for the reasoning). Two kinds of entry share
    /// one dictionary:
    ///
    ///   - Manager-owned — built by <see cref="GetOrCreateScope"/> (today: only the Facade's
    ///     <c>"Session"</c> entry, storage "B"). This manager created it, so this manager may
    ///     dispose it: <see cref="ClearScope"/>/<see cref="ClearAll"/>/<see cref="ClearAllExcept"/>
    ///     dispose-and-remove it.
    ///   - Foreign / scope-owned — registered by a scope this manager did NOT create
    ///     (<c>BaseAssetScope</c> for Global/Scene/Hierarchy = storage A/C; <see cref="AddressableManager.Scopes.HybridScope"/>
    ///     under its <c>"Hybrid:"</c>-prefixed ids = storage D) via <see cref="RegisterExternal"/>,
    ///     purely so it becomes visible here (<see cref="GetScope"/>/<see cref="HasScope"/>/
    ///     <see cref="ActiveScopes"/>) and reachable by bulk cache-clear operations. This manager
    ///     may only <c>ClearCache()</c> it — disposing another owner's loader out from under it
    ///     violates the one-owner rule (LIFETIME_DESIGN.md §1). Only the owning scope's own
    ///     teardown, via <see cref="UnregisterExternal"/>, removes the entry.
    ///
    /// <see cref="IsManagerOwned"/> lets a caller ask which kind an entry is before acting.
    /// </summary>
    public class ScopeManager
    {
        private static ScopeManager _instance;
        public static ScopeManager Instance => _instance ??= new ScopeManager();

        /// <summary>
        /// Reserved: the id <see cref="AddressableManager.Scopes.GlobalAssetScope"/> registers
        /// itself under as a foreign entry. <see cref="GetOrCreateScope"/> refuses to hand this id
        /// out as a manager-owned scope (see its doc comment) so a project cannot accidentally
        /// alias, or race, the built-in Global singleton's directory entry
        /// (HANDOFF_TO_SESSION_B.md §4.3, A-7 review finding).
        /// </summary>
        internal const string ReservedGlobalScopeId = "Global";

        /// <summary>One directory entry: a loader plus who may dispose it.</summary>
        private readonly struct Registration
        {
            public readonly AssetLoader Loader;

            /// <summary>True only for loaders <see cref="GetOrCreateScope"/> itself built.</summary>
            public readonly bool ManagerOwned;

            /// <summary>
            /// For foreign entries only: the owning scope's type name, so a caller trying to
            /// <see cref="ClearScope"/> a foreign entry gets told who actually owns it instead of
            /// just "not mine". Null for manager-owned entries.
            /// </summary>
            public readonly string OwnerTypeName;

            public Registration(AssetLoader loader, bool managerOwned, string ownerTypeName = null)
            {
                Loader = loader;
                ManagerOwned = managerOwned;
                OwnerTypeName = ownerTypeName;
            }
        }

        private readonly Dictionary<string, Registration> _loaders = new Dictionary<string, Registration>();

        /// <summary>
        /// Get all active scope IDs — manager-owned and foreign alike.
        /// </summary>
        public IEnumerable<string> ActiveScopes => _loaders.Keys;

        /// <summary>
        /// Get or create a manager-owned scope with the given ID. The manager built this loader,
        /// so the manager may dispose it — <see cref="ClearScope"/>/<see cref="ClearAll"/> will.
        /// </summary>
        /// <remarks>
        /// <c>"Global"</c> is reserved (<see cref="ReservedGlobalScopeId"/>) and always refused:
        /// <see cref="AddressableManager.Scopes.GlobalAssetScope"/> registers its own loader under
        /// that exact id as a *foreign* entry (<see cref="RegisterExternal"/>). Before this guard,
        /// calling <c>GetOrCreateScope("Global")</c> either raced GlobalAssetScope's registration
        /// (whichever ran first silently blocked or aliased the other — HANDOFF_TO_SESSION_B.md
        /// §4.3, A-7 review finding) or, if GlobalAssetScope had already registered, silently
        /// handed back GlobalAssetScope's own loader instead of an independent manager-owned
        /// cache. Use <c>GlobalAssetScope.Instance.Loader</c> for the real Global cache; pick a
        /// different id (e.g. <c>"AppGlobal"</c>) for your own named scope.
        /// </remarks>
        public AssetLoader GetOrCreateScope(string scopeId)
        {
            if (string.IsNullOrEmpty(scopeId))
            {
                Debug.LogError("[ScopeManager] Scope ID cannot be null or empty");
                return null;
            }

            if (scopeId == ReservedGlobalScopeId)
            {
                Debug.LogError($"[ScopeManager] '{ReservedGlobalScopeId}' is reserved for " +
                                "GlobalAssetScope (registered automatically as a foreign entry) " +
                                "and cannot be created as a manager-owned scope. Use " +
                                "GlobalAssetScope.Instance.Loader for the real Global cache, or " +
                                "pick a different id for your own named scope.");
                return null;
            }

            if (!_loaders.TryGetValue(scopeId, out var reg))
            {
                // Pass scope ID to AssetLoader for automatic monitoring
                var loader = new AssetLoader(scopeId);
                reg = new Registration(loader, managerOwned: true);
                _loaders[scopeId] = reg;

                Debug.Log($"[ScopeManager] Created scope: {scopeId}");

                // Report to monitoring
                AssetMonitorBridge.ReportScopeRegistered(scopeId, true);
            }

            return reg.Loader;
        }

        /// <summary>
        /// Register a loader this manager did not build — a scope (<c>BaseAssetScope</c>,
        /// <see cref="AddressableManager.Scopes.HybridScope"/>) making itself visible in the
        /// directory so <see cref="GetScope"/>/<see cref="HasScope"/>/<see cref="ActiveScopes"/>
        /// can find it and bulk operations can reach its cache. This manager may never dispose
        /// what it did not create — see the class docs' ownership table — so this entry is tagged
        /// foreign (<see cref="Registration.ManagerOwned"/> false) and only
        /// <see cref="UnregisterExternal"/>, called from the owning scope's own teardown, removes
        /// it.
        ///
        /// Identity-guarded (LIFETIME_DESIGN.md §1a): overwriting an existing foreign entry is
        /// only allowed when <paramref name="loader"/> IS the loader already registered under
        /// <paramref name="scopeId"/> (an owner re-registering itself, harmless). A *different*
        /// loader claiming an id another live foreign owner still holds is refused and logged
        /// instead of silently replacing the directory's view of the first owner's cache — this is
        /// reachable through ordinary misuse, not just malice: a duplicate <c>[GlobalAssetScope]</c>
        /// rejected in <c>Awake()</c> but still touched before its deferred <c>Destroy()</c> runs,
        /// or two <c>HierarchyAssetScope</c>/<c>SceneAssetScope</c> instances sharing a
        /// caller-chosen <c>customScopeId</c> while briefly alive together across a respawn
        /// (HANDOFF_TO_SESSION_B.md §4.3 review finding).
        /// </summary>
        internal void RegisterExternal(string scopeId, AssetLoader loader, object owner)
        {
            if (string.IsNullOrEmpty(scopeId) || loader == null) return;

            if (_loaders.TryGetValue(scopeId, out var existing))
            {
                // A manager-owned entry already sitting under this exact id (e.g. someone called
                // GetOrCreateScope(scopeId) directly before this scope registered) must not be
                // silently overwritten — that would orphan its loader with nothing left to dispose
                // it (the dictionary slot is this manager's only reference).
                if (existing.ManagerOwned)
                {
                    Debug.LogError($"[ScopeManager] '{scopeId}' is already a manager-owned entry " +
                                    $"(created via GetOrCreateScope) — {owner?.GetType().Name ?? "a scope"} " +
                                    $"cannot register under the same id without orphaning it. Use a " +
                                    $"different scope id.");
                    return;
                }

                // A different foreign owner already holds this id. Silently overwriting it would
                // corrupt the directory: ClearAll/ClearAllExcept/the dashboard would from this
                // point on only ever see the NEW owner, while the first owner's loader keeps
                // living and caching — invisible, and permanently unreachable by bulk operations.
                if (!ReferenceEquals(existing.Loader, loader))
                {
                    Debug.LogError($"[ScopeManager] '{scopeId}' is already registered by a " +
                                    $"different foreign owner ({existing.OwnerTypeName ?? "unknown"}) " +
                                    $"— {owner?.GetType().Name ?? "a scope"} cannot register under " +
                                    $"the same id without silently orphaning the existing owner's " +
                                    $"directory visibility. Use a different scope id.");
                    return;
                }
                // Else: the same loader re-registering under the same id (an owner rebuilding
                // itself after its own properly-paired Dispose()+UnregisterExternal, e.g.
                // GlobalAssetScope's recovery getter) — refresh the entry below, not a collision.
            }

            _loaders[scopeId] = new Registration(loader, managerOwned: false, owner?.GetType().Name);
        }

        /// <summary>
        /// Companion to <see cref="RegisterExternal"/>, called from the owning scope's own
        /// <c>Dispose()</c> with the exact loader it registered. Removes the directory entry only
        /// — never touches the loader itself, which the caller already owns and is disposing on
        /// its own.
        ///
        /// Identity-guarded (LIFETIME_DESIGN.md §1a, mirroring
        /// <c>InputManager.UninstallGlobals</c>'s <c>ReferenceEquals</c> pattern): only removes the
        /// entry if it is still <paramref name="loader"/> sitting there. A no-op if the id is
        /// unregistered, manager-owned, or — the case this guard exists for — currently holds a
        /// *different* foreign owner's loader (e.g. this scope's own <see cref="RegisterExternal"/>
        /// call was refused earlier because another live owner already held the id; disposing must
        /// not then delete that other owner's entry out from under it).
        /// </summary>
        internal void UnregisterExternal(string scopeId, AssetLoader loader)
        {
            if (string.IsNullOrEmpty(scopeId) || loader == null) return;
            if (_loaders.TryGetValue(scopeId, out var reg) &&
                !reg.ManagerOwned &&
                ReferenceEquals(reg.Loader, loader))
            {
                _loaders.Remove(scopeId);
            }
        }

        /// <summary>
        /// Check if scope exists — manager-owned or foreign.
        /// </summary>
        public bool HasScope(string scopeId)
        {
            return _loaders.ContainsKey(scopeId);
        }

        /// <summary>
        /// Get existing scope (returns null if doesn't exist) — manager-owned or foreign.
        /// </summary>
        public AssetLoader GetScope(string scopeId)
        {
            return _loaders.TryGetValue(scopeId, out var reg) ? reg.Loader : null;
        }

        /// <summary>
        /// True if this manager built (and may dispose) the entry under <paramref name="scopeId"/>;
        /// false if it is a foreign/scope-owned entry or does not exist. Lets a caller ask before
        /// acting instead of finding out the hard way that <see cref="ClearScope"/> only cleared a
        /// cache rather than disposing the loader.
        /// </summary>
        public bool IsManagerOwned(string scopeId)
        {
            return _loaders.TryGetValue(scopeId, out var reg) && reg.ManagerOwned;
        }

        /// <summary>
        /// Clear a specific scope. For a manager-owned entry this disposes the loader and removes
        /// it — the pre-existing <c>"Session"</c> / EndSession semantics. For a foreign entry
        /// (registered via <see cref="RegisterExternal"/>) this only clears its cache: disposing a
        /// loader another object still owns would violate the one-owner rule
        /// (LIFETIME_DESIGN.md §1) out from under whoever holds that scope. Check
        /// <see cref="IsManagerOwned"/> first if the distinction matters to the caller.
        /// </summary>
        public void ClearScope(string scopeId)
        {
            if (_loaders.TryGetValue(scopeId, out var reg))
            {
                if (reg.ManagerOwned)
                {
                    Debug.Log($"[ScopeManager] Clearing scope: {scopeId}");

                    reg.Loader.ClearCache();
                    reg.Loader.Dispose();
                    _loaders.Remove(scopeId);

                    AssetMonitorBridge.ReportScopeCleared(scopeId);
                }
                else
                {
                    var owner = string.IsNullOrEmpty(reg.OwnerTypeName) ? "its own scope" : reg.OwnerTypeName;
                    Debug.LogError($"[ScopeManager] '{scopeId}' is owned by {owner}, not by " +
                                    $"ScopeManager — clearing its cache instead of disposing the " +
                                    $"loader. Dispose the owning scope itself (or call ClearCache() " +
                                    $"on this loader directly) if you meant to release it entirely.");

                    reg.Loader.ClearCache();
                }
            }
            else
            {
                Debug.LogWarning($"[ScopeManager] Scope not found: {scopeId}");
            }
        }

        /// <summary>
        /// Clear all scopes except the specified ones. Manager-owned entries not in
        /// <paramref name="keepScopes"/> are disposed and removed; foreign entries are never
        /// removed by this manager (only their owner's own teardown does that) but their cache is
        /// still cleared — the bulk-clear caller gets the memory back even though the directory
        /// entry stays until its owner unregisters.
        /// </summary>
        public void ClearAllExcept(params string[] keepScopes)
        {
            var toClear = _loaders.Keys
                .Where(k => !keepScopes.Contains(k))
                .ToList();

            int disposed = 0;
            foreach (var scopeId in toClear)
            {
                var reg = _loaders[scopeId];
                reg.Loader.ClearCache();

                if (reg.ManagerOwned)
                {
                    reg.Loader.Dispose();
                    _loaders.Remove(scopeId);
                    AssetMonitorBridge.ReportScopeCleared(scopeId);
                    disposed++;
                }
            }

            Debug.Log($"[ScopeManager] Cleared {toClear.Count} scopes ({disposed} disposed, " +
                      $"{toClear.Count - disposed} foreign caches cleared), kept {keepScopes.Length}");
        }

        /// <summary>
        /// Clear all scopes except Global. Now does what its name says — <c>"Global"</c> only
        /// became a real directory entry once <c>BaseAssetScope</c> started registering itself
        /// (HANDOFF_TO_SESSION_B.md A-7). Before that this was identical to <see cref="ClearAll"/>
        /// in every reachable state, because nothing had ever registered under that key.
        /// </summary>
        public void ClearAllExceptGlobal()
        {
            ClearAllExcept("Global");
        }

        /// <summary>
        /// Clear every scope. Manager-owned entries are disposed and removed; foreign entries have
        /// their cache cleared but the directory entry stays — see <see cref="ClearAllExcept"/>.
        /// </summary>
        public void ClearAll()
        {
            var all = _loaders.Keys.ToList();
            int disposed = 0;

            foreach (var scopeId in all)
            {
                var reg = _loaders[scopeId];
                reg.Loader.ClearCache();

                if (reg.ManagerOwned)
                {
                    reg.Loader.Dispose();
                    _loaders.Remove(scopeId);
                    AssetMonitorBridge.ReportScopeCleared(scopeId);
                    disposed++;
                }
            }

            Debug.Log($"[ScopeManager] Cleared {all.Count} scopes ({disposed} disposed, " +
                      $"{all.Count - disposed} foreign caches cleared)");
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
        /// Get total count of active scopes — manager-owned and foreign alike.
        /// </summary>
        public int ActiveScopeCount => _loaders.Count;

        // Reset the static singleton on domain reload (Editor) and at the start of a fresh
        // SubsystemRegistration in a build. Without this the AssetLoader instances from
        // a previous Play session — along with whatever live handles they were holding —
        // would survive into the next session and leak. Discarding _instance drops every
        // registration, manager-owned and foreign alike, regardless of how far ClearAll() below
        // gets — the previous session's owning scopes are gone with the domain anyway, so there is
        // nothing left to notify.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            if (_instance != null)
            {
                try
                {
                    _instance.ClearAll();
                }
                catch (Exception ex)
                {
                    // Was a silent catch { } — a partial teardown here left live Addressables
                    // handles with no diagnostic at all. UniTask's rule for the same situation:
                    // drain first, discard unconditionally, but make the discard's failure
                    // visible.
                    Debug.LogError($"[ScopeManager] Reset failed with {_instance._loaders.Count} " +
                                    $"scope(s) still registered (their AssetLoader handles were not " +
                                    $"released): {ex}");
                }
            }
            _instance = null;
        }
    }
}
