using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;
using AddressableManager.Core;
using AddressableManager.Facade;
using AddressableManager.Managers;
using AddressableManager.Pooling;
using AddressableManager.Scopes;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.API
{
    /// <summary>
    /// Standard API - Balanced control and convenience
    ///
    /// Target users: Most developers, production use
    /// Features: Scope management, pooling control, progress tracking
    /// Trade-offs: Requires understanding of scopes and lifecycle
    ///
    /// Usage:
    ///   using var handle = await Standard.LoadGlobal<Sprite>("UI/Icon");
    ///   image.sprite = handle.Asset;
    /// </summary>
    public static class Standard
    {
        private static AddressablesFacade _facade;

        private static AddressablesFacade Facade
        {
            get
            {
                if (_facade == null)
                {
                    _facade = AddressablesFacade.Instance;
                }
                return _facade;
            }
        }

        #region Scoped Loading

        /// <summary>
        /// Load asset in Global scope (persistent)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadGlobal<T>(string address)
        {
            return await Facade.LoadGlobalAsync<T>(address);
        }

        /// <summary>
        /// Load asset in Session scope (gameplay lifetime)
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadSession<T>(string address)
        {
            return await Facade.LoadSessionAsync<T>(address);
        }

        /// <summary>
        /// Load an asset into the cache scoped to <paramref name="scene"/> — the handle is
        /// released when that scene unloads. Loads an <i>asset</i>; it does not load a scene
        /// (nothing in this package does — see <c>AddressablesFacade</c>'s Scene Scope region).
        ///
        /// This is the overload to use from a MonoBehaviour:
        /// <c>Standard.LoadIntoSceneScope&lt;Material&gt;(addr, gameObject.scene)</c> binds to the
        /// scene the caller actually lives in.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public static async UniTask<IAssetHandle<T>> LoadIntoSceneScope<T>(string address, Scene scene)
#else
        public static async Task<IAssetHandle<T>> LoadIntoSceneScope<T>(string address, Scene scene)
#endif
        {
            return await Facade.LoadIntoSceneScopeAsync<T>(address, scene);
        }

        /// <summary>
        /// Load an asset into the cache scoped to <b>the currently active scene</b> — not the
        /// caller's scene. A static API has no caller GameObject to derive one from, so from an
        /// additively-loaded scene this binds your asset to a scene you do not live in, and the
        /// asset is released when that other scene unloads. Pass <c>gameObject.scene</c> to
        /// <see cref="LoadIntoSceneScope{T}(string, Scene)"/> whenever the caller has one.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
#if UNITASK_PRESENT
        public static async UniTask<IAssetHandle<T>> LoadIntoSceneScope<T>(string address)
#else
        public static async Task<IAssetHandle<T>> LoadIntoSceneScope<T>(string address)
#endif
        {
            return await Facade.LoadIntoSceneScopeAsync<T>(address);
        }

        /// <summary>
        /// Superseded by <see cref="LoadIntoSceneScope{T}(string)"/> — identical behaviour.
        /// The name reads as Unity's <c>Addressables.LoadSceneAsync</c> (one character apart)
        /// while loading an asset into a scene-scoped cache instead, and it binds to the active
        /// scene rather than the caller's. The package's own sample was written against the wrong
        /// reading of it (HANDOFF_TO_SESSION_B.md A-8).
        ///
        /// Signature intentionally left as <c>Task</c> even where the replacements are dual — a
        /// deprecated method changing its return type would break the very callers the
        /// deprecation exists to keep compiling until 5.0.0.
        /// </summary>
        // Kept until 5.0.0 per repo invariant 6.
        [Obsolete("Renamed: this loads an ASSET into a scene-scoped cache, it does not load a " +
                  "scene, and it binds to the active scene rather than the caller's. Use " +
                  "Standard.LoadIntoSceneScope<T>(address, gameObject.scene) to bind to your own " +
                  "scene, or LoadIntoSceneScope<T>(address) for the same active-scene behaviour. " +
                  "Removed in 5.0.0.", false)]
        public static async Task<IAssetHandle<T>> LoadScene<T>(string address)
        {
            return await Facade.LoadIntoSceneScopeAsync<T>(address);
        }

        /// <summary>
        /// Load asset by AssetReference
        /// </summary>
        public static async Task<IAssetHandle<T>> Load<T>(AssetReference assetReference)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.LoadAssetAsync<T>(assetReference);
        }

        /// <summary>
        /// Load with Result pattern for explicit error handling
        /// </summary>
        public static async Task<LoadResult<IAssetHandle<T>>> LoadSafe<T>(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.LoadAssetAsyncSafe<T>(address);
        }

        /// <summary>
        /// Load with SmartHandle for automatic memory management
        /// </summary>
        public static async Task<SmartAssetHandle<T>> LoadSmart<T>(string address)
        {
            var handle = await LoadGlobal<T>(address);
            return handle?.ToSmart();
        }

        #endregion

        #region Multiple Assets

        /// <summary>
        /// Load multiple assets by label
        /// </summary>
        public static async Task<List<IAssetHandle<T>>> LoadByLabel<T>(string label)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.LoadAssetsByLabelAsync<T>(label);
        }

        /// <summary>
        /// Load batch of assets by addresses
        /// </summary>
        public static async Task<Dictionary<string, IAssetHandle<T>>> LoadBatch<T>(params string[] addresses)
        {
            var results = new Dictionary<string, IAssetHandle<T>>();
            var loader = Facade.GetGlobalScope().Loader;

            foreach (var address in addresses)
            {
                var handle = await loader.LoadAssetAsync<T>(address);
                if (handle != null && handle.IsValid)
                {
                    results[address] = handle;
                }
            }

            return results;
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiate GameObject in Global scope
        /// </summary>
        public static async Task<GameObject> InstantiateGlobal(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.InstantiateAsync(address);
        }

        /// <summary>
        /// Instantiate GameObject in the Session scope.
        /// Auto-starts the session (ScopeManager-backed) on first call.
        /// </summary>
        public static async Task<GameObject> InstantiateSession(string address)
        {
            var loader = Facade.GetSessionLoader();
            if (loader == null)
            {
                Facade.StartSession();
                loader = Facade.GetSessionLoader();
            }
            return await loader.InstantiateAsync(address);
        }

        /// <summary>
        /// Instantiate with transform
        /// </summary>
        public static async Task<GameObject> Instantiate(string address, Vector3 position, Quaternion rotation)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.InstantiateAsync(address, position, rotation);
        }

        #endregion

        #region Pooling

        /// <summary>
        /// Create standard pool
        /// </summary>
        public static async Task<bool> CreatePool(string address, int preloadCount = 0, int maxSize = 50)
        {
            return await Facade.CreatePoolAsync(address, preloadCount, maxSize);
        }

        /// <summary>
        /// Create dynamic pool with auto-sizing
        /// </summary>
        public static async Task<bool> CreateDynamicPool(string address, DynamicPoolConfig config = null, int preloadCount = 0)
        {
            var poolManager = Facade.GetPoolManager();
            return await poolManager.CreateDynamicPoolAsync(address, config ?? DynamicPoolConfig.Default, preloadCount);
        }

        /// <summary>
        /// Spawn from pool
        /// </summary>
        public static GameObject Spawn(string address)
        {
            return Facade.Spawn(address);
        }

        /// <summary>
        /// Spawn from pool at position
        /// </summary>
        public static GameObject Spawn(string address, Vector3 position)
        {
            return Facade.Spawn(address, position);
        }

        /// <summary>
        /// Return to pool
        /// </summary>
        public static void Despawn(string address, GameObject instance)
        {
            Facade.Despawn(address, instance);
        }

        /// <summary>
        /// Get pool statistics
        /// </summary>
        public static (int activeCount, int pooledCount)? GetPoolStats(string address)
        {
            var poolManager = Facade.GetPoolManager();
            return poolManager.GetPoolStats(address);
        }

        #endregion

        #region Session Management

        /// <summary>
        /// Start a new session (clears previous session assets)
        /// </summary>
        public static void StartSession()
        {
            Facade.StartSession();
        }

        /// <summary>
        /// End current session (releases all session assets)
        /// </summary>
        public static void EndSession()
        {
            Facade.EndSession();
        }

        /// <summary>
        /// Check if session is active
        /// </summary>
        public static bool IsSessionActive()
        {
            return Facade.IsSessionActive();
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clear Global cache
        /// </summary>
        public static void ClearGlobalCache()
        {
            Facade.ClearGlobalCache();
        }

        /// <summary>
        /// Clear Session cache
        /// </summary>
        public static void ClearSessionCache()
        {
            Facade.ClearSessionCache();
        }

        /// <summary>
        /// Clear the cache of the scope registered in <see cref="ScopeManager"/> under
        /// <paramref name="scopeName"/>. Clears the cache only — the loader is never disposed and
        /// the scope stays usable, so the next load re-fetches. Disposing is
        /// <c>ScopeManager.ClearScope</c>/<see cref="EndSession"/>'s job, not this one.
        ///
        /// <para><b>Reach is exactly ScopeManager's directory, and no wider</b> (A-7 made that
        /// directory real by having every <c>BaseAssetScope</c> register itself): <c>"Global"</c>,
        /// each <c>SceneAssetScope</c> under its <c>Scene-{name}#h{handle}</c> id, each
        /// <c>HierarchyAssetScope</c>, each <c>HybridScope</c> under its <c>"Hybrid:"</c> id, and
        /// every manager-owned scope from <c>ScopeManager.GetOrCreateScope</c> — the Facade's
        /// <c>"Session"</c> among them. Deliberately NOT reachable: the Facade's private pool
        /// loader, registered nowhere precisely so that a bulk clear cannot yank a live pool's
        /// template prefab out from under it (see <c>AddressablesFacade._poolLoader</c>). Use
        /// <c>AddressablesFacade.ClearAllPools()</c> for pools.</para>
        ///
        /// <para>An id nothing is registered under is an error naming what IS registered, not a
        /// quiet return: a caller clearing under memory pressure must never read silence as
        /// success, which is exactly what the previous log-and-return stub gave it
        /// (HANDOFF_TO_SESSION_B.md A-10). Call <c>ScopeManager.Instance.HasScope(scopeName)</c>
        /// first to test without the error. The signature stays <c>void</c> for the same reason
        /// <c>ScopeManager.ClearScope</c>'s does (LIFETIME_DESIGN.md §8 Q4) — no return value
        /// means no sentinel to get wrong.</para>
        /// </summary>
        public static void ClearCache(string scopeName)
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                Debug.LogError("[Standard.ClearCache] Scope name is null or empty — cleared nothing.");
                return;
            }

            // GetScope, not ClearScope: ClearScope disposes and de-registers a manager-owned entry
            // (the EndSession semantics), which is a strictly larger operation than this method's
            // name promises.
            var loader = ScopeManager.Instance.GetScope(scopeName);
            if (loader == null)
            {
                var known = string.Join(", ", ScopeManager.Instance.ActiveScopes);
                Debug.LogError($"[Standard.ClearCache] No scope is registered as '{scopeName}' — " +
                                "cleared nothing. Registered scopes: " +
                                $"{(string.IsNullOrEmpty(known) ? "(none)" : known)}. Pools are not " +
                                "scopes; use AddressablesFacade.ClearAllPools() for those.");
                return;
            }

            loader.ClearCache();
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public static (int cachedAssets, int activeHandles) GetCacheStats()
        {
            return Facade.GetGlobalScope().Loader.GetCacheStats();
        }

        #endregion

        #region Preloading

        /// <summary>
        /// Preload assets without storing handles
        /// </summary>
        public static async Task PreloadAsync(params string[] addresses)
        {
            var loader = Facade.GetGlobalScope().Loader;

            foreach (var address in addresses)
            {
                var handle = await loader.LoadAssetAsync<object>(address);

                // This method keeps no handle for the caller, so the reference it was born with
                // has nowhere else to go — give it back. The loader's cache holds its own
                // reference (CacheHandle()), so the asset stays warm in cache after this Dispose()
                // decrements. Without this every call orphaned one reference, same defect as
                // Simple.Load (HANDOFF_TO_SESSION_B.md A-4, A-5).
                handle?.Dispose();
            }
        }

        /// <summary>
        /// Download dependencies for remote assets.
        /// Returns true on success, false otherwise. The signature dropped
        /// the old <c>long</c> sentinel in 2.2.0 — pair with
        /// <see cref="GetDownloadSize(string)"/> for byte counts.
        /// </summary>
        // Task 3.10. Kept until 5.0.0 per repo invariant 6.
        [Obsolete("Use CdnManager.DownloadAsync(DownloadRequest) for typed errors and cancellation. " +
                  "Removed in 5.0.0.", false)]
        public static async Task<bool> DownloadDependencies(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.DownloadDependenciesAsync(address);
        }

        /// <summary>
        /// Get download size
        /// </summary>
        // Task 3.10. Kept until 5.0.0 per repo invariant 6.
        [Obsolete("Use CdnManager.GetDownloadSizeAsync(DownloadRequest), which returns CdnResult<long> " +
                  "so zero-bytes-to-download is distinguishable from could-not-find-out. Removed in 5.0.0.", false)]
        public static async Task<long> GetDownloadSize(string address)
        {
            var loader = Facade.GetGlobalScope().Loader;
            return await loader.GetDownloadSizeAsync(address);
        }

        #endregion

        #region Validation

        /// <summary>
        /// Enable validation mode
        /// </summary>
        public static void EnableValidation(ValidationMode mode)
        {
            AssetValidator.CurrentMode = mode;
        }

        /// <summary>
        /// Disable validation
        /// </summary>
        public static void DisableValidation()
        {
            AssetValidator.CurrentMode = ValidationMode.None;
        }

        /// <summary>
        /// Get validation statistics
        /// </summary>
        public static ValidationStats GetValidationStats()
        {
            return AssetValidator.GetStatistics();
        }

        #endregion
    }
}
