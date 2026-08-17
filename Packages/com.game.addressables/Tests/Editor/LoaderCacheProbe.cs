using System.Collections.Generic;
using System.Reflection;
using AddressableManager.Core;
using AddressableManager.Loaders;
using NUnit.Framework;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// Plants entries in an <see cref="AssetLoader"/>'s cache without an Addressables load, so the
    /// paths that only run on a populated cache — eviction, ClearCache, invalidation — can be tested
    /// in EditMode.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NEEDED, AND WHY IT IS ACCEPTABLE.
    ///
    /// <see cref="AssetLoader"/>'s only insert path is its private <c>CacheHandle&lt;T&gt;</c>,
    /// reachable only from a completed Addressables operation. An EditMode fixture has no built
    /// content, so the choice is between leaving the merged loader's cache paths untested — the
    /// paths every tiered loader now runs, since the fork was retired — or planting entries and
    /// then driving the behaviour through the real public API. This does the latter: the only
    /// reflection is the two field lookups below, and every test that uses it triggers the code
    /// under test through <c>ForceEviction()</c>/<c>ClearCache()</c> rather than by invoking a
    /// private method.
    ///
    /// <see cref="CachedAsset"/>, <see cref="AssetCacheKey"/> and <see cref="IOwnedHandle"/> are all
    /// internal and already visible here via <c>InternalsVisibleTo</c>, so the entries planted are
    /// the real entry type holding a handle that satisfies the real owner-side contract — not a
    /// parallel structure that could drift from what production stores.
    ///
    /// FRAGILITY IS DELIBERATELY LOUD. Both field lookups assert with a message naming what to
    /// update. A rename should fail here with an explanation, not with a NullReferenceException.
    /// </remarks>
    internal static class LoaderCacheProbe
    {
        /// <summary>
        /// The owner-side handle an <see cref="AssetLoader"/> cache entry holds, with no Addressables
        /// operation behind it. Records which release it was given, because the difference between
        /// <c>Dispose()</c> (a decrement, what eviction owes) and <c>ForceRelease()</c> (a hard
        /// release, reserved for teardown) is the refcount contract's central distinction and the
        /// one a test has to be able to see.
        /// </summary>
        internal sealed class FakeOwnedHandle : IOwnedHandle
        {
            private int _count = 1;

            public int Releases { get; private set; }
            public int ForceReleases { get; private set; }
            public bool IsAlive => _count > 0;

            public bool TryRetain()
            {
                if (_count <= 0) return false;
                _count++;
                return true;
            }

            public void ForceRelease()
            {
                ForceReleases++;
                _count = 0;
            }

            public void Dispose()
            {
                Releases++;
                if (_count > 0) _count--;
            }
        }

        internal static Dictionary<AssetCacheKey, CachedAsset> CacheOf(AssetLoader loader)
        {
            var field = typeof(AssetLoader)
                .GetField("_assetCache", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field,
                "AssetLoader._assetCache is the single (address, Type) dictionary the L-7 merge is " +
                "built around. If it was renamed, update LoaderCacheProbe.");

            return (Dictionary<AssetCacheKey, CachedAsset>)field.GetValue(loader);
        }

        /// <summary>
        /// Set the loader's one byte total. Planting an entry does not go through the insert path
        /// that maintains it, so the two have to be set in step by hand for the eviction gate to
        /// see a realistic cache.
        /// </summary>
        internal static void SetTieredBytes(AssetLoader loader, long bytes)
        {
            var field = typeof(AssetLoader)
                .GetField("_tieredBytes", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field,
                "AssetLoader._tieredBytes is the one byte total the merge keeps — adjusted in " +
                "RemoveCacheEntry on the way out and CacheHandle on the way in, with no second book " +
                "to reconcile. If it was renamed, update LoaderCacheProbe.");

            field.SetValue(loader, bytes);
        }

        /// <summary>
        /// Plant one entry under <c>(address, typeof(object))</c> and hand back the handle it holds.
        /// </summary>
        internal static FakeOwnedHandle Plant(
            AssetLoader loader, string address, long bytes, CacheTier tier)
        {
            var handle = new FakeOwnedHandle();
            var key = new AssetCacheKey(address, typeof(object));

            CacheOf(loader)[key] = new CachedAsset(key, handle, bytes) { Tier = tier };

            return handle;
        }

        internal static bool Contains(AssetLoader loader, string address) =>
            CacheOf(loader).ContainsKey(new AssetCacheKey(address, typeof(object)));

        internal static CachedAsset Entry(AssetLoader loader, string address) =>
            CacheOf(loader)[new AssetCacheKey(address, typeof(object))];
    }
}
