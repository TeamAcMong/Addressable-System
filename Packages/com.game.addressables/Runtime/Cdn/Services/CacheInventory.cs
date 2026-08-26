using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using AddressableManager.Core;

namespace AddressableManager.Cdn
{
    /// <summary>Where a bundle comes from, and whether the device already has it.</summary>
    public enum BundlePresence
    {
        /// <summary>Downloaded and in the player's cache. A launch loads it without a request.</summary>
        Cached,

        /// <summary>In the catalog, not on the device. This is what a returning player downloads.</summary>
        NotFetched,

        /// <summary>Built into the app. Never cached, never downloaded — absent from the cache is correct.</summary>
        ShipsInPlayer,

        /// <summary>
        /// Remote, but the catalog gives no hash, so the question cannot be asked.
        /// </summary>
        /// <remarks>
        /// Addressables keys the cache on name AND hash; with no hash there is nothing to look up.
        /// Reported rather than guessed at: "we could not ask" and "it is not there" would otherwise
        /// be shown as the same answer, and one of them is a build configuration problem.
        /// </remarks>
        Unknown,
    }

    /// <summary>One bundle the loaded catalog names, and what the device has done with it.</summary>
    public readonly struct CachedBundleInfo
    {
        /// <summary>The cache key's name half — an internal id, not the file name.</summary>
        public readonly string BundleName;

        /// <summary>The file a player's HTTP request asks for. Readable; use it for display.</summary>
        public readonly string FileName;

        /// <summary>Content hash, the cache key's other half. Empty when the catalog carries none.</summary>
        public readonly string Hash;

        /// <summary>Download size in bytes as the catalog records it.</summary>
        public readonly long SizeBytes;

        /// <summary>How many catalog entries resolve through this bundle.</summary>
        public readonly int DependentEntryCount;

        /// <summary>Where it comes from and whether it is here.</summary>
        public readonly BundlePresence Presence;

        public CachedBundleInfo(
            string bundleName, string fileName, string hash, long sizeBytes,
            int dependentEntryCount, BundlePresence presence)
        {
            BundleName = bundleName;
            FileName = fileName;
            Hash = hash;
            SizeBytes = sizeBytes;
            DependentEntryCount = dependentEntryCount;
            Presence = presence;
        }
    }

    /// <summary>What a cache inventory found, including the part it could not name.</summary>
    public readonly struct CacheInventoryReport
    {
        /// <summary>Every bundle the loaded catalog names.</summary>
        public readonly IReadOnlyList<CachedBundleInfo> Bundles;

        /// <summary>Bytes the cache reports holding in total, or -1 when the platform does not say.</summary>
        public readonly long OccupiedBytes;

        /// <summary>Bytes of cached bundles this catalog can account for.</summary>
        public readonly long AccountedBytes;

        /// <summary>
        /// Bytes present in the cache that this catalog cannot name.
        /// </summary>
        /// <remarks>
        /// <b>The number no other view can show, and the reason this type exists.</b> Unity's cache
        /// cannot be enumerated - it answers "is the bundle named X cached?" for an X you can
        /// already name - so anything cached by an EARLIER catalog is invisible to a list built from
        /// the current one. This is the difference between what the cache says it holds and what the
        /// catalog can explain, and it is what <c>CleanObsoleteAsync</c> removes.
        ///
        /// -1 when <see cref="OccupiedBytes"/> is unavailable. Never negative otherwise: the two
        /// figures are measured by different means and a rounding disagreement must not surface as a
        /// negative quantity of bytes.
        /// </remarks>
        public readonly long UnaccountedBytes;

        public CacheInventoryReport(
            IReadOnlyList<CachedBundleInfo> bundles,
            long occupiedBytes, long accountedBytes, long unaccountedBytes)
        {
            Bundles = bundles;
            OccupiedBytes = occupiedBytes;
            AccountedBytes = accountedBytes;
            UnaccountedBytes = unaccountedBytes;
        }
    }

    /// <summary>
    /// Which bundles the running player has already downloaded.
    /// </summary>
    /// <remarks>
    /// <b>Three facts about Unity's cache shape everything here, and all three were read out of
    /// Addressables rather than assumed.</b>
    ///
    /// <list type="number">
    /// <item><b>The cache cannot be enumerated.</b> <c>Caching</c> answers whether a bundle you can
    /// name is present; <c>Caching.GetAllCachePaths</c> lists cache directories, not their contents.
    /// So an inventory can only ever cover the loaded catalog, and what an older catalog left behind
    /// is reported as <see cref="CacheInventoryReport.UnaccountedBytes"/> rather than omitted.</item>
    ///
    /// <item><b>The cache key is <c>AssetBundleRequestOptions.BundleName</c> plus the hash</b> - not
    /// the file name. Addressables builds <c>new CachedAssetBundle(m_Options.BundleName, hash)</c> at
    /// every one of its call sites. <c>CatalogReader</c> keys on the FILE name for its own work,
    /// because its question is whether a file exists on a CDN; using that key here would report
    /// every bundle as missing, which reads as a broken screen rather than a wrong lookup.</item>
    ///
    /// <item><b>Local bundles are never cached.</b> They ship inside the player, so absent from the
    /// cache is the correct answer for them - a different answer from "not downloaded yet", and kept
    /// separate.</item>
    /// </list>
    ///
    /// Main thread only, and only meaningful in play mode: outside it there is no loaded catalog,
    /// and in the Editor the cache is the machine's rather than any device's.
    /// </remarks>
    public static class CacheInventory
    {
        /// <summary>
        /// Read the loaded catalog and ask the cache about every bundle in it.
        /// </summary>
        /// <remarks>
        /// Returns a failed <see cref="LoadResult{T}"/> rather than an empty report when there is
        /// nothing to read. "No catalog is loaded" and "the catalog has no bundles" are different
        /// answers, and a screen that shows an empty list for both tells the reader the wrong one.
        /// </remarks>
        public static LoadResult<CacheInventoryReport> Snapshot()
        {
            if (!Application.isPlaying)
            {
                return LoadResult<CacheInventoryReport>.Failure(
                    LoadErrorCode.OperationFailed,
                    "Not in play mode, so no catalog is loaded and the cache being read would be " +
                    "this machine's rather than a device's.");
            }

            var locators = Addressables.ResourceLocators;
            if (locators == null)
            {
                return LoadResult<CacheInventoryReport>.Failure(
                    LoadErrorCode.OperationFailed,
                    "Addressables has no resource locators yet. Initialise Addressables first.");
            }

            var byKey = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
            int locatorCount = 0;

            foreach (var locator in locators)
            {
                if (locator == null) continue;
                locatorCount++;

                IEnumerable<UnityEngine.ResourceManagement.ResourceLocations.IResourceLocation> all;
                try
                {
                    all = locator.AllLocations;
                }
                catch (Exception ex)
                {
                    // A locator that refuses to enumerate must not take the whole inventory with it.
                    // Some locators are synthetic and have no list to give.
                    Debug.LogWarning(
                        $"[CacheInventory] Locator '{locator.LocatorId}' would not enumerate: {ex.Message}");
                    continue;
                }

                if (all == null) continue;

                foreach (var location in all)
                {
                    if (location == null) continue;

                    if (location.Data is AssetBundleRequestOptions options)
                    {
                        Record(byKey, location.InternalId, options);
                        continue;
                    }

                    // An asset location is not itself a bundle; it depends on one. Counting those
                    // dependencies is what lets a row say what it holds, and a bundle name is a hash
                    // that nobody recognises without it.
                    if (location.Dependencies == null) continue;

                    foreach (var dependency in location.Dependencies)
                    {
                        if (dependency?.Data is AssetBundleRequestOptions dependencyOptions)
                            Record(byKey, dependency.InternalId, dependencyOptions).Dependents++;
                    }
                }
            }

            if (locatorCount == 0)
            {
                return LoadResult<CacheInventoryReport>.Failure(
                    LoadErrorCode.OperationFailed,
                    "No resource locator is loaded, so there is no catalog to inventory.");
            }

            var bundles = new List<CachedBundleInfo>(byKey.Count);
            long accounted = 0;

            foreach (var pair in byKey)
            {
                var a = pair.Value;
                var presence = Presence(a);

                if (presence == BundlePresence.Cached)
                    accounted += a.Size;

                bundles.Add(new CachedBundleInfo(
                    a.BundleName, a.FileName, a.Hash, a.Size, a.Dependents, presence));
            }

            // Largest first: the decision this screen supports is about bytes, and the bundle worth
            // looking at is the big one whichever way it is sorted alphabetically.
            bundles.Sort((x, y) => y.SizeBytes.CompareTo(x.SizeBytes));

            long occupied = OccupiedBytes();
            long unaccounted = occupied < 0 ? -1 : Math.Max(0, occupied - accounted);

            return LoadResult<CacheInventoryReport>.Success(
                new CacheInventoryReport(bundles, occupied, accounted, unaccounted));
        }

        // ------------------------------------------------------------------ internals

        private sealed class Accumulator
        {
            public string BundleName;
            public string FileName;
            public string Hash;
            public long Size;
            public int Dependents;
            public bool IsRemote;
        }

        private static Accumulator Record(
            IDictionary<string, Accumulator> byKey,
            string internalId,
            AssetBundleRequestOptions options)
        {
            string name = options.BundleName ?? string.Empty;

            // Keyed on the cache key, not the file name, because two catalog entries pointing at one
            // bundle must accumulate into one row - and the cache key is what makes them the same
            // bundle as far as the thing being measured is concerned.
            string key = name + "|" + (options.Hash ?? string.Empty);

            if (!byKey.TryGetValue(key, out var a))
            {
                a = new Accumulator
                {
                    BundleName = name,
                    FileName = FileNameFromInternalId(internalId),
                    Hash = options.Hash ?? string.Empty,
                    Size = options.BundleSize,
                    IsRemote = IsRemote(internalId),
                };

                byKey[key] = a;
            }

            return a;
        }

        private static BundlePresence Presence(Accumulator a)
        {
            if (!a.IsRemote) return BundlePresence.ShipsInPlayer;

#if ENABLE_CACHING
            if (string.IsNullOrEmpty(a.Hash)) return BundlePresence.Unknown;

            var hash = Hash128.Parse(a.Hash);
            if (!hash.isValid) return BundlePresence.Unknown;

            // The exact call Addressables makes before deciding to download. Matching it is the whole
            // point: any other formulation answers a question the loader does not ask.
            return Caching.IsVersionCached(new CachedAssetBundle(a.BundleName, hash))
                ? BundlePresence.Cached
                : BundlePresence.NotFetched;
#else
            // Caching compiled out. Not "nothing is cached" - nothing can be asked.
            return BundlePresence.Unknown;
#endif
        }

        private static long OccupiedBytes()
        {
#if ENABLE_CACHING
            try
            {
                if (!Caching.ready) return -1;

                var cache = Caching.defaultCache;
                return cache.valid ? cache.spaceOccupied : -1;
            }
            catch (Exception)
            {
                // -1, never 0. An unreadable cache and an empty one are different, and this figure
                // is subtracted from - reporting 0 would turn every cached byte into "unaccounted".
                return -1;
            }
#else
            return -1;
#endif
        }

        /// <summary>Remote content is cached; content inside the player is not.</summary>
        /// <remarks>
        /// Decided by the id's scheme, which is what Addressables itself uses to choose between a
        /// web request and a file read.
        /// </remarks>
        private static bool IsRemote(string internalId)
        {
            if (string.IsNullOrEmpty(internalId)) return false;

            return internalId.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || internalId.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static string FileNameFromInternalId(string internalId)
        {
            if (string.IsNullOrEmpty(internalId)) return string.Empty;

            int cut = internalId.LastIndexOfAny(new[] { '/', '\\' });
            string tail = cut >= 0 ? internalId.Substring(cut + 1) : internalId;

            int query = tail.IndexOf('?');
            return query >= 0 ? tail.Substring(0, query) : tail;
        }
    }
}
