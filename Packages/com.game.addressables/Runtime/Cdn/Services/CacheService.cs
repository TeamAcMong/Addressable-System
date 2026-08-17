using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Cdn
{
    /// <summary>
    /// What the bundle cache is currently holding.
    /// </summary>
    public readonly struct CacheStats
    {
        /// <summary>Bytes the cache is using.</summary>
        public readonly long OccupiedBytes;

        /// <summary>Bytes still available to it, or -1 when the platform does not report it.</summary>
        public readonly long FreeBytes;

        /// <summary>Where the cache lives on disk.</summary>
        public readonly string Path;

        /// <summary>Whether the numbers could be read at all.</summary>
        public readonly bool IsValid;

        /// <summary>Create stats.</summary>
        public CacheStats(long occupiedBytes, long freeBytes, string path, bool isValid)
        {
            OccupiedBytes = occupiedBytes;
            FreeBytes = freeBytes;
            Path = path ?? string.Empty;
            IsValid = isValid;
        }

        /// <summary>Unavailable, e.g. before Caching is ready.</summary>
        public static CacheStats Unavailable => new CacheStats(0, -1, string.Empty, false);

        /// <inheritdoc />
        public override string ToString() => IsValid
            ? $"{OccupiedBytes / (1024 * 1024)} MB used, " +
              $"{(FreeBytes >= 0 ? (FreeBytes / (1024 * 1024)) + " MB free" : "free space unknown")}"
            : "cache stats unavailable";
    }

    /// <summary>
    /// Manages the Addressables bundle cache — tasks 4.1, 4.2, 4.3.
    /// </summary>
    /// <remarks>
    /// WHY 4.2 MATTERS MORE THAN IT SOUNDS
    /// A content update leaves the superseded bundles on disk. Nothing removes them on its own, so
    /// after twenty updates a player is carrying twenty generations of content. That is the usual
    /// answer to "why is this game 8 GB". CleanObsoleteAsync is cheap and should run after every
    /// applied update, which is why CdnManager calls it rather than leaving it to the integrator to
    /// remember.
    /// </remarks>
    public class CacheService
    {
        /// <summary>Raised when occupied bytes cross the configured budget — task 4.3.</summary>
        /// <remarks>
        /// An event rather than a built-in prompt: only the game knows whether now is a reasonable
        /// moment to interrupt a player, and a package that puts up its own dialog mid-match would
        /// be wrong more often than right.
        /// </remarks>
        public event Action<CacheStats> BudgetExceeded;

        private readonly long _budgetBytes;
        private bool _budgetAlreadyReported;

        /// <summary>Create a cache service.</summary>
        /// <param name="budgetBytes">Occupied-bytes threshold for <see cref="BudgetExceeded"/>. 0 disables it.</param>
        public CacheService(long budgetBytes = 0)
        {
            _budgetBytes = budgetBytes;
        }

        /// <summary>Current cache usage — task 4.1.</summary>
        public CacheStats GetStats()
        {
            try
            {
                if (!Caching.ready)
                    return CacheStats.Unavailable;

                var cache = Caching.defaultCache;
                if (!cache.valid)
                    return CacheStats.Unavailable;

                var stats = new CacheStats(cache.spaceOccupied, cache.spaceFree, cache.path, true);
                CheckBudget(stats);
                return stats;
            }
            catch (Exception ex)
            {
                // Caching throws on platforms without a writable cache. Unavailable is the honest
                // answer; zero would read as an empty cache.
                Debug.LogWarning($"[CdnCacheService] Could not read cache stats: {ex.Message}");
                return CacheStats.Unavailable;
            }
        }

#if UNITASK_PRESENT
        /// <summary>Remove bundles superseded by a newer catalog — task 4.2.</summary>
        public async UniTask<CdnResult<CacheStats>> CleanObsoleteAsync(
            IEnumerable<string> catalogIds = null, CancellationToken cancellationToken = default)
#else
        /// <summary>Remove bundles superseded by a newer catalog — task 4.2.</summary>
        public async Task<CdnResult<CacheStats>> CleanObsoleteAsync(
            IEnumerable<string> catalogIds = null, CancellationToken cancellationToken = default)
#endif
        {
            var before = GetStats();

            AsyncOperationHandle<bool> handle;
            try
            {
                // null preserves every currently loaded catalog and removes what none of them
                // reference — which is exactly "the previous generation of bundles".
                handle = Addressables.CleanBundleCache(catalogIds);
            }
            catch (Exception ex)
            {
                return CdnResult<CacheStats>.Failure(
                    CdnErrorCode.Unknown, $"CleanBundleCache threw: {ex.Message}", exception: ex);
            }

            try
            {
#if UNITASK_PRESENT
                await handle.ToUniTask(cancellationToken: cancellationToken);
#else
                await handle.Task;
#endif
            }
            catch (OperationCanceledException)
            {
                SafeRelease(handle);
                return CdnResult<CacheStats>.Cancelled("Cancelled while cleaning the bundle cache");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<CacheStats>.Failure(
                    CdnErrorCode.Unknown, $"Cleaning the bundle cache failed: {ex.Message}", exception: ex);
            }

            bool ok = handle.Status == AsyncOperationStatus.Succeeded && handle.Result;
            SafeRelease(handle);

            if (!ok)
            {
                return CdnResult<CacheStats>.Failure(
                    CdnErrorCode.Unknown, "Addressables reported that the cache clean did not complete",
                    hint: "Obsolete bundles are still on disk. Not fatal — they waste space rather than " +
                          "breaking anything — but it will keep growing if it recurs.");
            }

            var after = GetStats();

            if (before.IsValid && after.IsValid)
            {
                long freed = before.OccupiedBytes - after.OccupiedBytes;
                Debug.Log($"[CdnCacheService] Cleaned obsolete bundles: freed {freed / (1024 * 1024)} MB " +
                          $"({before.OccupiedBytes / (1024 * 1024)} -> {after.OccupiedBytes / (1024 * 1024)} MB)");
            }

            return CdnResult<CacheStats>.Success(after);
        }

#if UNITASK_PRESENT
        /// <summary>Evict the cached bundles for specific keys — task 4.1.</summary>
        public async UniTask<CdnResult<bool>> ClearAsync(
            IEnumerable<object> keys, CancellationToken cancellationToken = default)
#else
        /// <summary>Evict the cached bundles for specific keys — task 4.1.</summary>
        public async Task<CdnResult<bool>> ClearAsync(
            IEnumerable<object> keys, CancellationToken cancellationToken = default)
#endif
        {
            if (keys == null)
                return CdnResult<bool>.Failure(CdnErrorCode.Unknown, "No keys given to clear");

            AsyncOperationHandle<bool> handle;
            try
            {
                handle = Addressables.ClearDependencyCacheAsync((System.Collections.IEnumerable)keys, false);
            }
            catch (Exception ex)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown, $"ClearDependencyCacheAsync threw: {ex.Message}", exception: ex);
            }

            try
            {
#if UNITASK_PRESENT
                await handle.ToUniTask(cancellationToken: cancellationToken);
#else
                await handle.Task;
#endif
            }
            catch (OperationCanceledException)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Cancelled("Cancelled while clearing cached dependencies");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown, $"Clearing cached dependencies failed: {ex.Message}", exception: ex);
            }

            bool ok = handle.Status == AsyncOperationStatus.Succeeded && handle.Result;
            SafeRelease(handle);

            return ok
                ? CdnResult<bool>.Success(true)
                : CdnResult<bool>.Failure(CdnErrorCode.Unknown, "The cached dependencies could not be cleared");
        }

        /// <summary>
        /// Empty the whole bundle cache — task 4.1.
        /// </summary>
        /// <remarks>
        /// Everything must be re-downloaded afterwards, so this is a support action, not something
        /// to call on a hunch. Fails rather than reporting success when Unity refuses, which it does
        /// while any bundle is still loaded.
        /// </remarks>
        public CdnResult<CacheStats> ClearAll()
        {
            try
            {
                if (!Caching.ClearCache())
                {
                    return CdnResult<CacheStats>.Failure(
                        CdnErrorCode.Unknown,
                        "Unity refused to clear the cache",
                        hint: "This happens while bundles are still loaded. Release loaded content first — " +
                              "clearing the cache under a live bundle would be worse than refusing.");
                }

                _budgetAlreadyReported = false;
                return CdnResult<CacheStats>.Success(GetStats());
            }
            catch (Exception ex)
            {
                return CdnResult<CacheStats>.Failure(
                    CdnErrorCode.Unknown, $"Clearing the cache threw: {ex.Message}", exception: ex);
            }
        }

        /// <summary>
        /// Raise <see cref="BudgetExceeded"/> at most once per crossing — task 4.3.
        /// </summary>
        /// <remarks>
        /// Latched deliberately. GetStats is called from UI that polls, so an unlatched event would
        /// fire several times a second for as long as the cache stayed over budget, and any prompt
        /// wired to it would be unusable. It re-arms once usage drops back under.
        /// </remarks>
        private void CheckBudget(CacheStats stats)
        {
            if (_budgetBytes <= 0 || !stats.IsValid) return;

            if (stats.OccupiedBytes > _budgetBytes)
            {
                if (_budgetAlreadyReported) return;

                _budgetAlreadyReported = true;
                BudgetExceeded?.Invoke(stats);
            }
            else
            {
                _budgetAlreadyReported = false;
            }
        }

        private static void SafeRelease<T>(AsyncOperationHandle<T> handle)
        {
            try
            {
                if (handle.IsValid()) Addressables.Release(handle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CdnCacheService] Failed to release a handle: {ex.Message}");
            }
        }
    }
}
