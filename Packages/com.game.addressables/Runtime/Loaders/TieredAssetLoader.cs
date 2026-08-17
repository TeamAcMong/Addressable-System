using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Core;
using AddressableManager.Threading;
#if UNITY_EDITOR
using AddressableManager.Monitoring;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Asset loader with tiered caching (Hot/Warm/Cold)
    /// Automatically manages cache based on access patterns and memory constraints
    ///
    /// Extends AssetLoader with intelligent cache management that:
    /// - Keeps frequently accessed assets in Hot tier
    /// - Demotes rarely used assets to Cold tier
    /// - Automatically evicts assets when memory limit is reached
    /// </summary>
    public class TieredAssetLoader : IDisposable
    {
        private readonly Dictionary<Type, ITieredCache> _tieredCaches = new Dictionary<Type, ITieredCache>();
        private readonly TieredCacheConfig _config;
        private readonly string _scopeName;

        // Shared by every per-type TieredCache<T> below, so the eviction gate reads one real
        // ceiling instead of _config.MaxCacheSizeBytes per type (HANDOFF_TO_SESSION_B.md L-4).
        private readonly CacheBudget _budget;

        private bool _disposed;

        /// <summary>
        /// Create TieredAssetLoader with optional configuration
        /// </summary>
        /// <param name="scopeName">Scope name for monitoring</param>
        /// <param name="config">Tiered cache configuration (uses Default if null)</param>
        public TieredAssetLoader(string scopeName = "Unknown", TieredCacheConfig config = null)
        {
            _scopeName = scopeName;
            _config = config ?? TieredCacheConfig.Default;
            _budget = new CacheBudget(_config);

            // Reaches every live instance for the periodic eviction pump and the low-memory
            // handler (HANDOFF_TO_SESSION_B.md L-3) — without this, nothing ever calls
            // EvaluateTiers()/ForceEviction() outside of Set()'s own inline trigger, so memory is
            // only ever reclaimed while still allocating, never during an idle period.
            TieredAssetLoaderRegistry.Register(this);
        }

        /// <summary>
        /// Get or create tiered cache for specific type
        /// </summary>
        private TieredCache<T> GetOrCreateCache<T>() where T : class
        {
            var type = typeof(T);
            if (!_tieredCaches.TryGetValue(type, out var cache))
            {
                var typed = new TieredCache<T>(_config, _budget);
                _tieredCaches[type] = typed;
                return typed;
            }
            return (TieredCache<T>)cache;
        }

        /// <summary>
        /// Check if current thread is Unity's main thread.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="AddressableRuntime.IsMainThread"/> instead of a private
        /// <c>static int?</c> latched by whichever thread happened to construct the first
        /// <see cref="TieredAssetLoader"/> in the process (HANDOFF_TO_SESSION_B.md L-10). That old
        /// latch never re-validated its assumption: if the first instance was ever built off the
        /// main thread — a background warm-up, a Task continuation, a test fixture — every real
        /// main-thread call would throw from then on while the actual offending thread passed
        /// silently. <see cref="AddressableRuntime"/> latches once, correctly, from a
        /// <c>RuntimeInitializeOnLoadMethod</c> hook that always runs on the main thread, and fails
        /// open (unlatched reads as "allow") for edit-mode tooling that never triggers it — the
        /// same semantics this method already documented, just backed by a latch that cannot be
        /// wrong about which thread is main.
        /// </remarks>
        private void AssertMainThread()
        {
            if (!AddressableRuntime.IsMainThread)
            {
                throw new InvalidOperationException(
                    $"[TieredAssetLoader] Thread safety violation detected!\n\n" +
                    $"TieredAssetLoader must be called from Unity's main thread only.\n" +
                    $"Current thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}\n" +
                    $"Expected thread ID: {AddressableRuntime.MainThreadId}\n\n" +
                    $"SOLUTION: Use ThreadSafeAssetLoader wrapper for background thread loading.\n"
                );
            }
        }

        /// <summary>
        /// Ensures the code immediately following this call runs on Unity's main thread, hopping
        /// through <see cref="UnityMainThreadDispatcher"/> when the preceding <c>await</c> resumed
        /// somewhere else first.
        /// </summary>
        /// <remarks>
        /// <c>await operation.Task;</c> in <see cref="LoadAssetAsync{T}(string)"/> and
        /// <see cref="LoadAssetAsync{T}(AssetReference)"/> had no post-await thread re-check at all —
        /// a continuation is not guaranteed to resume on the thread that started it, and the code
        /// immediately after touches <c>cache.Set</c>/the Addressables operation directly. A throwing
        /// <see cref="AssertMainThread"/> would at least stop that, but it would also leak
        /// <c>operation</c> — releasing it from here would itself be an off-thread Addressables call,
        /// the exact hazard being guarded against. Hopping first (mirroring
        /// <see cref="AddressableManager.Pooling.AddressablePoolManager"/>'s identical fix) means
        /// every line after the await runs on the main thread as normal, including the existing
        /// failure path's own <c>Addressables.Release(operation)</c> — nothing new needed there.
        /// A no-op (already-completed task) when already on the main thread.
        /// </remarks>
        private static Task EnsureMainThreadAsync()
        {
            if (AddressableRuntime.IsMainThread) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                UnityMainThreadDispatcher.Enqueue(() => tcs.TrySetResult(true));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TieredAssetLoader] Could not marshal back to the main thread: {ex.Message}");
                tcs.TrySetResult(true);
            }

            return tcs.Task;
        }

        #region Load by Address

        /// <summary>
        /// Load asset asynchronously by address with tiered caching
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(string address) where T : class
        {
            if (_disposed)
            {
                Debug.LogError("[TieredAssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(address))
            {
                Debug.LogError("[TieredAssetLoader] Address cannot be null or empty");
                return null;
            }

            AssertMainThread();

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var cache = GetOrCreateCache<T>();

            // The cache key used to be $"{address}_{typeof(T).Name}", built fresh on every call —
            // an allocation that bought nothing: GetOrCreateCache<T>() already splits by exact Type
            // into a separate dictionary per type, so the suffix was constant within any one cache
            // and never disambiguated anything. TieredCache<T>.Set/TryGet build the real
            // (address, Type) key internally now (HANDOFF_TO_SESSION_B.md L-9) — pass the address
            // straight through.

            // Try cache first. TryGet() already retains the reference it hands back (and drops the
            // entry internally if its handle turned out to be dead), so a successful result here is
            // always a live, owned handle.
            if (cache.TryGet(address, out var cachedHandle))
            {
                Debug.Log($"[TieredAssetLoader] Cache hit for: {address}");

#if UNITY_EDITOR
                var loadDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    loadDuration,
                    true // from cache
                );
#endif

                return cachedHandle;
            }

            // Load from Addressables
            try
            {
                Debug.Log($"[TieredAssetLoader] Loading asset: {address}");
                var operation = Addressables.LoadAssetAsync<T>(address);
                await operation.Task;

                // The continuation above may have resumed off the main thread — hop back before
                // touching the cache or the operation below (see EnsureMainThreadAsync's remarks).
                await EnsureMainThreadAsync();

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // Editor-only monitored overload — see AssetLoader.cs for the identical
                    // pattern. Without it, ReleaseOperation() has no address/type to report and
                    // AssetMonitorBridge.ReportAssetReleased never fires for handles this loader
                    // produces, even though ReportAssetLoaded below does fire on the load side
                    // (HANDOFF_TO_SESSION_B.md E-CHAIN item 2).
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif

                    // Estimate size for cache management
                    long estimatedSize = EstimateAssetSize(operation.Result);

                    // Add to tiered cache. If a concurrent load for the same key already won and
                    // populated the cache first, Set() releases our duplicate handle instead of
                    // storing it (see Set()'s XML doc) — serve the entry it already holds instead of
                    // handing back a handle we no longer own.
                    cache.Set(address, handle, estimatedSize);

                    try
                    {
                        // Nothing to add to a ledger here any more — cache.Set() above is this
                        // loader's only tracking, and TieredCache<T>.ForceReleaseAll() (via
                        // ClearCache()/Dispose() below) already reaches every handle it holds
                        // (HANDOFF_TO_SESSION_B.md L-1). A separate _activeHandles list used to
                        // duplicate that tracking for no reason — this loader, unlike AssetLoader,
                        // has no label-load path that lives outside the per-type caches.

                        Debug.Log($"[TieredAssetLoader] Successfully loaded: {address}");

#if UNITY_EDITOR
                        var loadDuration = Time.realtimeSinceStartup - startTime;
                        AssetMonitorBridge.ReportAssetLoaded(
                            address,
                            typeof(T).Name,
                            _scopeName,
                            loadDuration,
                            false // not from cache
                        );
#endif

                        if (!handle.IsValid && cache.TryGet(address, out var canonical))
                        {
                            return canonical;
                        }

                        return handle;
                    }
                    catch
                    {
                        // Anything thrown here means the outer catch below swallows it and returns
                        // null — this method's own caller never receives `handle` and therefore can
                        // never Release() it. If Set() above stored `handle` (the common case), the
                        // object now carries two references: the cache's own (from TryRetain) and
                        // this call's original one that was meant to be handed to our caller. Release
                        // exactly that second one here so the entry is left exactly as if this call
                        // had never happened beyond caching it — refcount 1, owned solely by the
                        // cache, nothing orphaned. (If Set() instead rejected `handle` as a duplicate,
                        // it is already dead and this Release() is a documented no-op at count 0 —
                        // see AssetReferenceCounter.Release — so this is safe either way without
                        // needing to know which case happened.)
                        handle.Release();
                        throw;
                    }
                }
                else
                {
                    Debug.LogError($"[TieredAssetLoader] Failed to load asset: {address}. Error: {operation.OperationException}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TieredAssetLoader] Exception loading asset: {address}. Error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Load by AssetReference

        /// <summary>
        /// Load asset by AssetReference with tiered caching
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference) where T : class
        {
            if (_disposed)
            {
                Debug.LogError("[TieredAssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[TieredAssetLoader] Invalid AssetReference");
                return null;
            }

            AssertMainThread();

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var cache = GetOrCreateCache<T>();
            var address = assetReference.AssetGUID;

            // See LoadAssetAsync(string) above: the cache key is (address, Type), built internally
            // by TieredCache<T> now — no more redundant $"{address}_{typeof(T).Name}" string per
            // call (HANDOFF_TO_SESSION_B.md L-9).

            // Check cache. TryGet() already retains the reference it hands back (and drops the entry
            // internally if its handle turned out to be dead), so a successful result here is always
            // a live, owned handle.
            if (cache.TryGet(address, out var cachedHandle))
            {
#if UNITY_EDITOR
                var loadDuration = Time.realtimeSinceStartup - startTime;
                AssetMonitorBridge.ReportAssetLoaded(
                    address,
                    typeof(T).Name,
                    _scopeName,
                    loadDuration,
                    true
                );
#endif

                return cachedHandle;
            }

            // Load from Addressables
            try
            {
                var operation = assetReference.LoadAssetAsync<T>();
                await operation.Task;

                // See LoadAssetAsync(string) above for why this hop is needed after every await in
                // this class.
                await EnsureMainThreadAsync();

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    // See LoadAssetAsync(string) above for why this overload is Editor-only.
#if UNITY_EDITOR
                    var handle = new AssetHandle<T>(operation, address, typeof(T).Name);
#else
                    var handle = new AssetHandle<T>(operation);
#endif
                    long estimatedSize = EstimateAssetSize(operation.Result);

                    // See the address overload above: Set() may release this handle as a duplicate if
                    // a concurrent load for the same key already populated the cache.
                    cache.Set(address, handle, estimatedSize);

                    try
                    {
                        // See the address overload above: cache.Set() is this loader's only
                        // tracking now — no separate _activeHandles ledger (HANDOFF_TO_SESSION_B.md
                        // L-1).

#if UNITY_EDITOR
                        var loadDuration = Time.realtimeSinceStartup - startTime;
                        AssetMonitorBridge.ReportAssetLoaded(
                            address,
                            typeof(T).Name,
                            _scopeName,
                            loadDuration,
                            false
                        );
#endif

                        if (!handle.IsValid && cache.TryGet(address, out var canonical))
                        {
                            return canonical;
                        }

                        return handle;
                    }
                    catch
                    {
                        // See the address overload above for why this Release() is correct and safe
                        // regardless of whether Set() stored or rejected `handle`.
                        handle.Release();
                        throw;
                    }
                }
                else
                {
                    Debug.LogError($"[TieredAssetLoader] Failed to load AssetReference. Error: {operation.OperationException}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TieredAssetLoader] Exception loading AssetReference: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Pin an asset to prevent it from being evicted
        /// </summary>
        public void PinAsset<T>(string address) where T : class
        {
            var cache = GetOrCreateCache<T>();
            cache.Pin(address);
        }

        /// <summary>
        /// Unpin an asset to allow eviction
        /// </summary>
        public void UnpinAsset<T>(string address) where T : class
        {
            var cache = GetOrCreateCache<T>();
            cache.Unpin(address);
        }

        /// <summary>
        /// Get tiered cache statistics for a specific type
        /// </summary>
        public TieredCacheStats? GetCacheStats<T>() where T : class
        {
            var type = typeof(T);
            if (_tieredCaches.TryGetValue(type, out var cache))
            {
                return cache.GetStatistics();
            }
            return null;
        }

        /// <summary>
        /// Get combined cache statistics across all types.
        /// </summary>
        /// <remarks>
        /// <c>MaxSizeBytes</c> here was always the one real ceiling (<c>_config.MaxCacheSizeBytes</c>,
        /// now surfaced via <see cref="CacheBudget.Max"/>) — the bug this reported honestly but did
        /// not explain was that nothing enforced it: each per-type cache gated eviction against its
        /// own local total, so <c>TotalSizeBytes</c> below could legitimately sit at several times
        /// <c>MaxSizeBytes</c> with every individual cache reporting itself comfortably under 90%
        /// full (HANDOFF_TO_SESSION_B.md L-4). Now that every <see cref="TieredCache{T}"/> this
        /// loader owns shares <see cref="_budget"/> and gates against it, this combined figure and
        /// the enforced ceiling are finally the same number.
        /// </remarks>
        public TieredCacheStats GetCombinedStats()
        {
            var combined = new TieredCacheStats
            {
                MaxSizeBytes = _budget.Max
            };

            foreach (var cache in _tieredCaches.Values)
            {
                var stats = cache.GetStatistics();
                combined.TotalEntries += stats.TotalEntries;
                combined.HotEntries += stats.HotEntries;
                combined.WarmEntries += stats.WarmEntries;
                combined.ColdEntries += stats.ColdEntries;
                combined.PinnedEntries += stats.PinnedEntries;
                combined.TotalSizeBytes += stats.TotalSizeBytes;
                combined.TotalAccesses += stats.TotalAccesses;
                combined.CacheHits += stats.CacheHits;
                combined.TotalEvictions += stats.TotalEvictions;
                combined.TotalPromotions += stats.TotalPromotions;
                combined.TotalDemotions += stats.TotalDemotions;
            }

            combined.HitRate = combined.TotalAccesses > 0 ? (float)combined.CacheHits / combined.TotalAccesses : 0f;

            return combined;
        }

        /// <summary>
        /// Force tier evaluation for all caches
        /// </summary>
        public void EvaluateTiers()
        {
            AssertMainThread();

            foreach (var cache in _tieredCaches.Values)
            {
                cache.ForceEvaluateTiers();
            }
        }

        /// <summary>
        /// Force eviction for all caches
        /// </summary>
        public void ForceEviction()
        {
            AssertMainThread();

            foreach (var cache in _tieredCaches.Values)
            {
                cache.ForceEviction();
            }
        }

        /// <summary>
        /// Clear all caches, hard-releasing every handle they hold regardless of who else still
        /// holds a reference.
        /// </summary>
        /// <remarks>
        /// Unconditional on purpose, matching <see cref="AssetLoader.ClearCache"/>'s documented
        /// memory-pressure semantics — this used to be a documentation lie
        /// (HANDOFF_TO_SESSION_B.md L-1): every per-type <see cref="TieredCache{T}"/> is disposed
        /// below, and <see cref="TieredCache{T}.Dispose"/> now calls
        /// <see cref="TieredCache{T}.ForceReleaseAll"/> (not <see cref="TieredCache{T}.Clear"/>), so
        /// a caller still holding a handle across this call sees <c>IsValid == false</c> afterwards
        /// rather than the asset quietly staying resident because nothing this loader owned was
        /// ever actually released.
        /// </remarks>
        public void ClearCache()
        {
            Debug.Log($"[TieredAssetLoader] Clearing all caches");

            foreach (var cache in _tieredCaches.Values)
            {
                cache.Dispose();
            }

            _tieredCaches.Clear();
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Estimate asset memory size for cache management — drives <c>_currentCacheSize</c>, the
        /// eviction trigger gate and every byte figure in <see cref="TieredCacheStats"/>
        /// (HANDOFF_TO_SESSION_B.md L-5).
        /// </summary>
        /// <remarks>
        /// Measured first via <see cref="UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong"/>,
        /// which accounts for real format/mip/representation instead of the per-type formulas below —
        /// a 2048x2048 ASTC 6x6 texture is ~0.9MB actual vs. 16MB from the RGBA32-no-mips formula,
        /// roughly 18x off, and in the other direction an RGBA32 texture *with* mips was undercounted
        /// by the old formula's missing +33%.
        ///
        /// <para><b>Guarded, not trusted unconditionally:</b> <c>GetRuntimeMemorySizeLong</c> is
        /// documented to return 0 in non-development builds on some platforms. This implementation
        /// could not be verified against a development-stripped build of the target platform in this
        /// environment — that confirmation is still owed (see L-5's "(a)" requirement) — so rather
        /// than assume either behavior, a &lt;= 0 result falls back to the same heuristic this method
        /// used before, instead of letting eviction silently stop. That fallback is still the
        /// documented-inaccurate formula, kept only as the honest "no better number available"
        /// answer, not as a fix in its own right — HANDOFF_TO_SESSION_B.md tracks the eventual real
        /// answer as W4-07 (ingesting <c>Library/com.unity.addressables/buildReports</c>).</para>
        ///
        /// <para><b>GameObject is a special case, not a whole-object measurement:</b>
        /// <c>GetRuntimeMemorySizeLong</c> on a GameObject reports only the native GameObject shell
        /// (roughly a fixed, small overhead) — unlike <c>Texture2D</c>/<c>Mesh</c>/<c>AudioClip</c>,
        /// where the object being measured *is* the resource, it does not walk the object graph to
        /// add up the meshes/materials/textures the prefab's components reference. Trusting that
        /// number directly (the earlier version of this method did, via the generic branch above)
        /// would report a few hundred bytes for a prefab that drags in a 40MB mesh — strictly worse
        /// than the flat heuristic it replaced, because it looks like a real measurement instead of
        /// an admitted guess. <see cref="EstimateGameObjectSize"/> walks the referenced render data
        /// explicitly instead.</para>
        /// </remarks>
        private long EstimateAssetSize(object asset)
        {
            if (asset == null) return 0;

            if (asset is GameObject go)
            {
                long aggregated = EstimateGameObjectSize(go);
                if (aggregated > 0) return aggregated;

                // Nothing measurable was found (e.g. an empty prefab with no renderers) — fall
                // through to the flat heuristic below rather than report 0 and let this entry look
                // free to the eviction gate.
            }
            else if (asset is UnityEngine.Object unityObject)
            {
                long measured = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(unityObject);
                if (measured > 0) return measured;

                // Falls through to the heuristic below — see the guard note above.
            }

            return asset switch
            {
                // (long) cast on the first operand, not because width*height*4 can overflow int here
                // (Unity's max texture edge is 16384, so 16384*16384*4 = 1,073,741,824 < int.MaxValue)
                // but so this stays correct if that edge ever grows.
                Texture2D texture => (long)texture.width * texture.height * 4, // RGBA32, no mips/format — heuristic only
                AudioClip audio => (long)audio.samples * audio.channels * 2, // 16-bit PCM estimate
                Mesh mesh => (long)mesh.vertexCount * 32, // rough estimate
                GameObject => 4096, // flat estimate — same number regardless of what the prefab contains
                ScriptableObject => 1024,
                _ => 1024
            };
        }

        /// <summary>
        /// Aggregate a GameObject's real memory footprint by walking the meshes, materials and
        /// material-referenced textures its components actually use, instead of trusting a
        /// single-instance <c>GetRuntimeMemorySizeLong(go)</c> call that only covers the native
        /// GameObject shell (see the remarks on <see cref="EstimateAssetSize"/>).
        /// </summary>
        /// <remarks>
        /// Deduplicates via asset identity (a <see cref="HashSet{T}"/> of
        /// <see cref="UnityEngine.Object"/>) so a material or texture shared by several renderers on
        /// the same prefab — the common case — is only counted once, matching how it is actually
        /// resident in memory. Best-effort: any exception walking a component (e.g. a custom shader
        /// that misbehaves under <c>Shader.GetPropertyCount</c>) is caught and logged rather than
        /// letting a single malformed asset break caching for every other entry.
        /// </remarks>
        private long EstimateGameObjectSize(GameObject go)
        {
            long total = 0;

            try
            {
                var seen = new HashSet<UnityEngine.Object>();

                long AddAsset(UnityEngine.Object obj)
                {
                    if (obj == null || !seen.Add(obj)) return 0;
                    long size = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(obj);
                    return size > 0 ? size : 0;
                }

                // The native GameObject shell itself (components, transform, etc.) — small, but
                // still real overhead the walk below doesn't otherwise account for.
                total += AddAsset(go);

                foreach (var meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    total += AddAsset(meshFilter.sharedMesh);
                }

                foreach (var skinned in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    total += AddAsset(skinned.sharedMesh);
                }

                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    if (materials == null) continue;

                    foreach (var material in materials)
                    {
                        if (material == null) continue;
                        total += AddAsset(material);

                        // The bulk of a prefab's real memory usually lives in the textures its
                        // materials reference, not the material asset itself — walk every
                        // texture-typed shader property instead of guessing at "_MainTex".
                        var shader = material.shader;
                        if (shader == null) continue;

                        int propertyCount = shader.GetPropertyCount();
                        for (int i = 0; i < propertyCount; i++)
                        {
                            if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                                continue;

                            var texture = material.GetTexture(shader.GetPropertyName(i));
                            total += AddAsset(texture);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TieredAssetLoader] Error walking GameObject '{go.name}' for size " +
                    $"estimation; using partial result. Error: {ex.Message}");
            }

            return total;
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Teardown: hard-releases every asset this loader ever cached.
        /// </summary>
        /// <remarks>
        /// Flips <see cref="_disposed"/> first, before doing anything else
        /// (HANDOFF_TO_SESSION_B.md L-1) — the previous order set it only after
        /// <see cref="ClearCache"/> returned, so a re-entrant call reached from inside teardown (a
        /// handle's release callback, a nested Dispose) saw <c>_disposed == false</c> and treated
        /// this loader as still live. <see cref="AssetLoader.Dispose"/> already flips first for the
        /// same reason — this now matches it.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            Debug.Log("[TieredAssetLoader] Disposing loader and releasing all assets");
            ClearCache();

            TieredAssetLoaderRegistry.Unregister(this);
        }

        #endregion
    }
}
