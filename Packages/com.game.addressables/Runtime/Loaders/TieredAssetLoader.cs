using System;
using System.Threading.Tasks;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Asset loader with tiered caching (Hot/Warm/Cold) — now a thin forwarder onto an
    /// <see cref="AssetLoader"/> constructed with the same <see cref="TieredCacheConfig"/>.
    /// </summary>
    /// <remarks>
    /// WHAT HAPPENED TO THIS CLASS (LIFETIME_DESIGN.md "L-7: evidence")
    ///
    /// This was a second, independent loader implementation that existed only to add tiering. Every
    /// defect fixed in it during the L-1/L-3/L-4/L-8/L-9/L-10 pass re-solved a problem
    /// <see cref="AssetLoader"/> had already solved, and the fixes still left it missing everything
    /// <see cref="AssetLoader"/> had all along. Tiering is a configuration of
    /// <see cref="AssetLoader"/> now — <c>new AssetLoader(scopeName, config)</c> — and this class
    /// forwards to one.
    ///
    /// <para><b>Forwarding is not just a compatibility courtesy; it fixes the worst bug this class
    /// had.</b> A <c>TieredAssetLoader</c> was not an <see cref="AssetLoader"/> and registered with a
    /// different registry, so <see cref="AssetLoaderRegistry.InvalidateAll"/> — the thing
    /// <c>CatalogService</c> calls after a CDN catalog update — could never reach it. Its cache went
    /// on serving handles resolved against the *previous* catalog for the rest of the session, with
    /// no code path anywhere that could have invalidated them. Because the inner loader is a plain
    /// <see cref="AssetLoader"/>, it registers in the one registry the invalidation walk uses, and
    /// every existing <c>new TieredAssetLoader(...)</c> call site is now reached after a catalog
    /// update without its author changing a line.</para>
    ///
    /// <para>What moving off this class additionally buys: single-flight join for concurrent loads
    /// of one key, the post-await disposed/thread guard, <c>LoadAssetsByLabelAsync</c>, the
    /// <c>*Safe</c>/<c>LoadResult</c> variants, <c>InstantiateAsync</c>/<c>ReleaseInstance</c>, and
    /// <c>ReleaseAsset</c> — the per-address release this class never had at all. None of those are
    /// reachable through this wrapper; they are only on <see cref="AssetLoader"/> itself.</para>
    /// </remarks>
    [Obsolete("Tiering is a configuration of AssetLoader now. Migrate:\n" +
              "\n" +
              "  BEFORE:\n" +
              "    var loader = new TieredAssetLoader(\"Battle\", TieredCacheConfig.Aggressive);\n" +
              "    var tex    = await loader.LoadAssetAsync<Texture2D>(\"Boss/Diffuse\");\n" +
              "    loader.PinAsset<Texture2D>(\"Boss/Diffuse\");\n" +
              "    var stats  = loader.GetCombinedStats();\n" +
              "    loader.Dispose();\n" +
              "\n" +
              "  AFTER:\n" +
              "    var loader = new AssetLoader(\"Battle\", TieredCacheConfig.Aggressive);\n" +
              "    var tex    = await loader.LoadAssetAsync<Texture2D>(\"Boss/Diffuse\");\n" +
              "    loader.PinAsset<Texture2D>(\"Boss/Diffuse\");\n" +
              "    var stats  = loader.GetTieredCacheStats();\n" +
              "    loader.Dispose();\n" +
              "\n" +
              "Factory form: Advanced.CreateTieredLoader(name, cfg) -> Advanced.CreateLoader(name, cfg). " +
              "The only renames are GetCombinedStats() -> GetTieredCacheStats() and " +
              "GetCacheStats<T>() -> GetTieredCacheStats<T>(); everything else is a type-name " +
              "substitution. This class forwards to an AssetLoader and keeps working, but only the " +
              "AssetLoader form gets single-flight join, the post-await thread guard, label/Safe/" +
              "Instantiate loads, ReleaseAsset, and catalog invalidation after a CDN update. " +
              "Removed in 5.0.0.", false)]
    public class TieredAssetLoader : IDisposable
    {
        /// <summary>
        /// The real loader. Registered in <see cref="AssetLoaderRegistry"/> by its constructor,
        /// which is what makes this wrapper reachable from a catalog update — see the class remarks.
        /// </summary>
        private readonly AssetLoader _inner;

        /// <summary>
        /// Create TieredAssetLoader with optional configuration
        /// </summary>
        /// <param name="scopeName">Scope name for monitoring</param>
        /// <param name="config">Tiered cache configuration (uses Default if null)</param>
        /// <remarks>
        /// The <c>null</c> → <see cref="TieredCacheConfig.Default"/> fallback is preserved from the
        /// original: this class has always meant "a loader with tiering", so it never constructs an
        /// untiered inner loader. One consequence worth knowing:
        /// <see cref="GetCacheStats{T}"/> can therefore never return <c>null</c> through this
        /// wrapper, because the inner loader's tiering is never off.
        /// </remarks>
        public TieredAssetLoader(string scopeName = "Unknown", TieredCacheConfig config = null)
        {
            _inner = new AssetLoader(scopeName, config ?? TieredCacheConfig.Default);
        }

        #region Load

        /// <summary>
        /// Load asset asynchronously by address with tiered caching. Returns
        /// <c>Task&lt;IAssetHandle&lt;T&gt;&gt;</c> in every project, UniTask installed or not.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately NOT dual-signature, and this is the one place in the package where that
        /// is correct.</b> Repo invariant 3 asks every new or changed public async API to return
        /// <c>UniTask</c> under <c>UNITASK_PRESENT</c>; invariant 6 says a shipped public member
        /// does not change before 5.0.0. Where the two collide, on an <c>[Obsolete]</c> member,
        /// invariant 6 wins — the entire promise of a warning-level deprecation is "your code keeps
        /// compiling until 5.0.0", and a return type that changes with an unrelated package's
        /// presence breaks exactly the callers the deprecation exists to carry. <c>Task</c> is what
        /// 4.1.0-pre.5 and 4.1.0-pre.6 both shipped here.
        ///
        /// <para>The identical question was already answered the same way on
        /// <c>Standard.LoadScene&lt;T&gt;</c> ("a deprecated method changing its return type would
        /// break the very callers the deprecation exists to keep compiling until 5.0.0"), whose
        /// replacements are dual while it stays <c>Task</c>. This member follows that precedent.</para>
        ///
        /// <para>Under UniTask the forwarder pays one <c>AsTask()</c> conversion. That cost is real
        /// and it is the reason to migrate, not a reason to break the signature: the replacement,
        /// <c>Advanced.CreateLoader(name, cfg)</c>, returns <c>AssetLoader</c>, whose
        /// <c>LoadAssetAsync&lt;T&gt;</c> <em>is</em> dual and hands back a <c>UniTask</c> with no
        /// conversion at all. Expression-bodied and not <c>async</c> on purpose — the forwarder adds
        /// no state machine of its own, matching <see cref="MonitoredAssetLoader"/>.</para>
        /// </remarks>
#if UNITASK_PRESENT
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(string address) where T : class
            => _inner.LoadAssetAsync<T>(address).AsTask();
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(string address) where T : class
            => _inner.LoadAssetAsync<T>(address);
#endif

        /// <summary>
        /// Load asset by AssetReference with tiered caching. Returns
        /// <c>Task&lt;IAssetHandle&lt;T&gt;&gt;</c> in every project, UniTask installed or not.
        /// </summary>
        /// <remarks>See the address overload for why this stays <c>Task</c> under UniTask.</remarks>
#if UNITASK_PRESENT
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference) where T : class
            => _inner.LoadAssetAsync<T>(assetReference).AsTask();
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference) where T : class
            => _inner.LoadAssetAsync<T>(assetReference);
#endif

        #endregion

        #region Cache Management

        /// <summary>
        /// Pin an asset to prevent it from being evicted. Pinning before the asset is loaded now
        /// works: the request is remembered and applied when the key arrives.
        /// </summary>
        public void PinAsset<T>(string address) where T : class => _inner.PinAsset<T>(address);

        /// <summary>
        /// Unpin an asset to allow eviction. Also cancels a pin that is still waiting for its key.
        /// </summary>
        public void UnpinAsset<T>(string address) where T : class => _inner.UnpinAsset<T>(address);

        /// <summary>
        /// Get tiered cache statistics for a specific type.
        /// </summary>
        /// <remarks>
        /// <b>Two semantic shifts, both deliberate.</b>
        ///
        /// <para><i>Nullability.</i> This used to return <c>null</c> until the first load of
        /// <typeparamref name="T"/> created a per-type cache, then a zeroed struct forever after.
        /// There is no per-type cache object to test for existence any more. The rule now is the
        /// inner loader's: <c>null</c> iff tiering is off — which, through this wrapper, never
        /// happens, because the constructor always supplies a config. So this returns a struct
        /// always, all-zero when nothing of <typeparamref name="T"/> is cached. Code that treated
        /// <c>null</c> as "nothing loaded yet" should test <c>TotalEntries == 0</c> instead.</para>
        ///
        /// <para><i>Which counters are per-type.</i> <c>TotalEntries</c>/<c>HotEntries</c>/
        /// <c>WarmEntries</c>/<c>ColdEntries</c>/<c>PinnedEntries</c>/<c>PendingPins</c>/
        /// <c>TotalSizeBytes</c> remain exact per-<typeparamref name="T"/> figures — cache entries
        /// carry their Type. <c>TotalAccesses</c>/<c>CacheHits</c>/<c>HitRate</c>/
        /// <c>TotalEvictions</c>/<c>TotalPromotions</c>/<c>TotalDemotions</c> are now loader-wide:
        /// there is one counter set per loader rather than one per Type, and keeping them per-Type
        /// would mean a <c>Dictionary&lt;Type, counters&gt;</c> — a second book, i.e. L-4's shape
        /// rebuilt for statistics. These are diagnostics, not lifetime.</para>
        /// </remarks>
        public TieredCacheStats? GetCacheStats<T>() where T : class => _inner.GetTieredCacheStats<T>();

        /// <summary>
        /// Get combined cache statistics across all types.
        /// </summary>
        /// <remarks>
        /// <c>TotalSizeBytes</c> and <c>MaxSizeBytes</c> are now the same two numbers eviction
        /// actually gates on, rather than a sum over per-type caches compared against a ceiling
        /// nothing enforced (L-4). The merged loader keeps one byte total over one dictionary, so
        /// the reported figure and the enforced figure cannot drift apart — there is no second book
        /// to reconcile.
        /// </remarks>
        public TieredCacheStats GetCombinedStats() => _inner.GetTieredCacheStats();

        /// <summary>
        /// Force tier evaluation across every cached entry.
        /// </summary>
        public void EvaluateTiers() => _inner.EvaluateTiers();

        /// <summary>
        /// Force an eviction pass.
        /// </summary>
        /// <remarks>
        /// Eviction now ranks candidates of every Type together in one pass. It used to walk each
        /// per-type cache in turn, where a cache could only ever evict its own entries and relied on
        /// round-robin over siblings to converge — the structural half of L-4.
        /// </remarks>
        public void ForceEviction() => _inner.ForceEviction();

        /// <summary>
        /// Clear the cache, hard-releasing every handle it holds regardless of who else still holds
        /// a reference.
        /// </summary>
        /// <remarks>
        /// Semantics are unchanged: both this and <see cref="AssetLoader.ClearCache"/> force-release
        /// every cached handle, so a caller still holding one across this call sees
        /// <c>IsValid == false</c> afterwards.
        /// </remarks>
        public void ClearCache() => _inner.ClearCache();

        #endregion

        #region Dispose

        /// <summary>
        /// Teardown: hard-releases every asset this loader ever cached, and unregisters it.
        /// </summary>
        /// <remarks>
        /// The <c>_disposed = true</c>-before-teardown ordering that this class used to implement
        /// itself is preserved inside <see cref="AssetLoader.Dispose"/>, which flips first for the
        /// same reason: a re-entrant call reached from inside teardown (a handle's release callback,
        /// a nested Dispose) must not see the loader as still live. Idempotence likewise lives
        /// there, so this needs no <c>_disposed</c> flag of its own.
        /// </remarks>
        public void Dispose() => _inner.Dispose();

        #endregion
    }
}
