using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using AddressableManager.Core;
using AddressableManager.Loaders;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Progress
{
    /// <summary>
    /// Extension to AssetLoader that provides progress tracking capabilities
    /// </summary>
    public static class ProgressiveAssetLoader
    {
        /// <summary>
        /// Load asset with progress tracking.
        /// Returns <c>UniTask&lt;IAssetHandle&lt;T&gt;&gt;</c> when UniTask is installed, otherwise <c>Task</c>.
        /// </summary>
        /// <remarks>
        /// HANDOFF_TO_SESSION_B.md L-2: this used to open its own <c>Addressables.LoadAssetAsync</c>
        /// call, bypassing <paramref name="loader"/> entirely. The handle it returned looked like it
        /// belonged to whatever scope <paramref name="loader"/> represented but was invisible to
        /// that loader's cache, its teardown ledger, its single-flight map, and
        /// <c>AssetLoader.InvalidateAddresses</c> — so after a catalog update it silently kept
        /// serving the old bundle forever, through the one loading path the rest of the package
        /// could never reach.
        ///
        /// Now this delegates straight to <see cref="AssetLoader.LoadAssetAsync{T}(string)"/>, so
        /// the handle it returns is the loader's own — cached, ledgered, single-flighted, reachable
        /// by <c>ClearCache()</c>/<c>Dispose()</c>/<c>InvalidateAddresses</c> exactly like any other
        /// handle that loader produces, and already reported to <c>AssetMonitorBridge</c> internally
        /// (no separate reporting needed here any more).
        ///
        /// Progress is read from <see cref="AssetLoader.GetLoadProgress{T}"/> instead of a second
        /// operation's own <c>PercentComplete</c> — the loader owns single-flight now, so opening a
        /// second operation here would either start a duplicate load or, on a race, silently join
        /// the first one with no operation of its own left to poll.
        /// </remarks>
#if UNITASK_PRESENT
        public static async UniTask<IAssetHandle<T>> LoadAssetWithProgressAsync<T>(
            this AssetLoader loader,
            string address,
            Action<ProgressInfo> onProgress)
#else
        public static async Task<IAssetHandle<T>> LoadAssetWithProgressAsync<T>(
            this AssetLoader loader,
            string address,
            Action<ProgressInfo> onProgress)
#endif
        {
            if (loader == null)
            {
                Debug.LogError("[ProgressiveLoader] loader is null");
                return null;
            }

            var tracker = new ProgressTracker();

            if (onProgress != null)
            {
                tracker.OnProgressChanged += onProgress;
            }

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Loading {address}"));

#if UNITASK_PRESENT
                // Preserve(): the polling loop below reads .Status without consuming the task, and
                // the final `await loadTask` consumes it afterwards — a plain UniTask<T> can only
                // be awaited once.
                var loadTask = loader.LoadAssetAsync<T>(address).Preserve();

                while (loadTask.Status == UniTaskStatus.Pending)
                {
                    tracker.UpdateProgress(new ProgressInfo(loader.GetLoadProgress<T>(address), $"Loading {address}"));
                    await UniTask.Yield();
                }
#else
                var loadTask = loader.LoadAssetAsync<T>(address);

                while (!loadTask.IsCompleted)
                {
                    tracker.UpdateProgress(new ProgressInfo(loader.GetLoadProgress<T>(address), $"Loading {address}"));
                    await Task.Yield();
                }
#endif

                var handle = await loadTask;

                // loader.LoadAssetAsync<T> already logged the specific reason (invalid address,
                // disposed loader, an Addressables failure) — nothing more to add here.
                if (handle == null) return null;

                tracker.Complete();
                return handle;
            }
            finally
            {
                if (onProgress != null)
                {
                    tracker.OnProgressChanged -= onProgress;
                }
            }
        }

        /// <summary>
        /// Download dependencies with progress tracking — task 3.9.
        /// </summary>
        /// <remarks>
        /// Same signature as before; the implementation now runs on
        /// <see cref="AddressableManager.Cdn.DownloadService"/>.
        ///
        /// WHAT WAS WRONG WITH THE OLD MATH
        /// It computed <c>speed = deltaProgress / deltaTime</c> where deltaProgress is a FRACTION of
        /// the operation, then multiplied by 100 and labelled the result KB/s. That number had no
        /// unit at all — it was percent-per-second scaled by an arbitrary constant, and a 10 MB and
        /// a 10 GB download reported the same "speed". deltaTime was measured from a startTime that
        /// was never reset, so it grew for the whole download and the reported rate decayed toward
        /// zero regardless of what the network was doing. ETA inherited both faults.
        /// <c>BytesDownloaded</c> and <c>TotalBytes</c> existed on ProgressInfo and were never
        /// populated, so any UI trying to show real sizes had nothing to show.
        ///
        /// Now speed is bytes per second smoothed with an EMA, ETA comes from real remaining bytes,
        /// and both byte fields are filled from Addressables' own DownloadStatus.
        ///
        /// ETA WHEN UNKNOWN
        /// DownloadProgress reports -1 for an unknown ETA. ProgressInfo has no way to say "unknown",
        /// so this maps -1 to 0 — the pre-existing meaning of the field. A caller that needs the
        /// distinction should use DownloadService directly.
        /// </remarks>
#if UNITASK_PRESENT
        public static async UniTask<bool> DownloadWithProgressAsync(
            string address,
            Action<ProgressInfo> onProgress)
#else
        public static async Task<bool> DownloadWithProgressAsync(
            string address,
            Action<ProgressInfo> onProgress)
#endif
        {
            var tracker = new ProgressTracker();

            if (onProgress != null)
            {
                tracker.OnProgressChanged += onProgress;
            }

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Downloading {address}"));

                // Falls back to a standalone service when the CDN layer has not been initialised, so
                // this keeps working in projects that never adopted it.
                var service = AddressableManager.Cdn.CdnManager.IsInitialized
                    ? null
                    : new AddressableManager.Cdn.DownloadService(
                        new AddressableManager.Cdn.NetworkPolicy(AddressableManager.Cdn.DownloadPolicy.Default));

                var request = AddressableManager.Cdn.DownloadRequest.For(address);
                var progress = new Progress<AddressableManager.Cdn.DownloadProgress>(p =>
                {
                    tracker.UpdateProgress(new ProgressInfo
                    {
                        Progress = p.Percent,
                        CurrentOperation = $"Downloading {address}",
                        BytesDownloaded = p.DownloadedBytes,
                        TotalBytes = p.TotalBytes,
                        DownloadSpeed = (float)(p.BytesPerSecond / 1024.0),
                        EstimatedTimeRemaining = p.EtaSeconds >= 0 ? (float)p.EtaSeconds : 0f
                    });
                });

                var result = service != null
                    ? await service.DownloadAsync(request, progress)
                    : await AddressableManager.Cdn.CdnManager.DownloadAsync(request, progress);

                if (result.IsFailure)
                {
                    // ProgressTracker has no failure state — only UpdateProgress, Complete and
                    // Reset — so the last reported progress is left standing, which is accurate for
                    // a download that stopped part-way. Complete() is deliberately not called: it
                    // would tell every subscriber the download succeeded.
                    Debug.LogError($"[ProgressiveAssetLoader] Download failed for {address}: {result.Error}");
                    return false;
                }

                tracker.Complete();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProgressiveLoader] Download exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (onProgress != null)
                {
                    tracker.OnProgressChanged -= onProgress;
                }

                // No handle to release any more: DownloadService owns the operation and releases it
                // on every path, including cancellation.
            }
        }

        /// <summary>
        /// Load multiple assets with composite progress tracking, returning every live handle to
        /// the caller — the caller owns each one and must <c>Release()</c>/<c>Dispose()</c> it.
        /// </summary>
        /// <remarks>
        /// HANDOFF_TO_SESSION_B.md L-2: added alongside — not replacing — the <c>bool</c>-returning
        /// <see cref="LoadMultipleWithProgressAsync{T}"/> below, per repo invariant 6 (no reshaping
        /// an existing public signature). That method's <c>Task.WhenAll(tasks)</c> used to discard
        /// every <c>tasks[i].Result</c>: ten addresses meant ten bundles nothing could ever release.
        /// This is the escape hatch for a caller that actually wants the handles.
        ///
        /// A batch where every address succeeds returns <c>Success</c> with the full list. A batch
        /// where any address fails returns <c>Failure</c> naming which ones — and gives back this
        /// call's own reference to every handle that DID succeed first, since the caller receiving
        /// a <c>Failure</c> has no way to release them. That is not a leak: every one of those loads
        /// went through <paramref name="loader"/>'s own <c>LoadAssetAsync</c>
        /// (via <see cref="LoadAssetWithProgressAsync{T}"/>), so the loader's cache still holds its
        /// own reference and the asset stays reachable through the loader exactly like any other
        /// cached entry.
        /// </remarks>
#if UNITASK_PRESENT
        public static async UniTask<LoadResult<List<IAssetHandle<T>>>> LoadMultipleWithProgressAsyncSafe<T>(
            this AssetLoader loader,
            string[] addresses,
            Action<ProgressInfo> onProgress)
#else
        public static async Task<LoadResult<List<IAssetHandle<T>>>> LoadMultipleWithProgressAsyncSafe<T>(
            this AssetLoader loader,
            string[] addresses,
            Action<ProgressInfo> onProgress)
#endif
        {
            if (loader == null)
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.LoaderDisposed, "loader is null");
            }

            if (addresses == null || addresses.Length == 0)
            {
                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.InvalidAddress, "addresses cannot be null or empty");
            }

            var compositeTracker = new CompositeProgressTracker();

            if (onProgress != null)
            {
                compositeTracker.OnProgressChanged += onProgress;
            }

            // Declared outside the try so the catch below can still reach every task that DID
            // complete successfully if WhenAll itself throws (propagating a fault from one of the
            // per-address tasks) — a variable scoped inside the try is invisible to its own catch.
#if UNITASK_PRESENT
            UniTask<IAssetHandle<T>>[] tasks = null;
#else
            Task<IAssetHandle<T>>[] tasks = null;
#endif

            try
            {
#if UNITASK_PRESENT
                tasks = new UniTask<IAssetHandle<T>>[addresses.Length];
#else
                tasks = new Task<IAssetHandle<T>>[addresses.Length];
#endif

                for (int i = 0; i < addresses.Length; i++)
                {
                    var childTracker = new ProgressTracker();
                    compositeTracker.AddTracker(childTracker, weight: 1f);

                    string address = addresses[i];
#if UNITASK_PRESENT
                    // Preserve(): the catch block below may need to read each task's result a
                    // second time (WhenAll already consumed it once) — same reason
                    // LoadAssetWithProgressAsync preserves its own inner task above.
                    tasks[i] = loader.LoadAssetWithProgressAsync<T>(
                        address,
                        info => childTracker.UpdateProgress(info)
                    ).Preserve();
#else
                    tasks[i] = loader.LoadAssetWithProgressAsync<T>(
                        address,
                        info => childTracker.UpdateProgress(info)
                    );
#endif
                }

#if UNITASK_PRESENT
                var results = await UniTask.WhenAll(tasks);
#else
                var results = await Task.WhenAll(tasks);
#endif

                var handles = new List<IAssetHandle<T>>(addresses.Length);
                List<string> failedAddresses = null;

                for (int i = 0; i < results.Length; i++)
                {
                    if (results[i] != null)
                    {
                        handles.Add(results[i]);
                    }
                    else
                    {
                        (failedAddresses ??= new List<string>()).Add(addresses[i]);
                    }
                }

                if (failedAddresses != null)
                {
                    // See this method's remarks: give back this call's own share of every handle
                    // that DID succeed. loader's cache still holds its own reference to each.
                    foreach (var handle in handles)
                    {
                        handle?.Dispose();
                    }

                    return LoadResult<List<IAssetHandle<T>>>.Failure(
                        LoadErrorCode.OperationFailed,
                        $"{failedAddresses.Count}/{addresses.Length} addresses failed to load: " +
                        string.Join(", ", failedAddresses),
                        "Check individual addresses with AssetLoader.LoadAssetAsyncSafe for a detailed error code",
                        string.Join(",", failedAddresses)
                    );
                }

                compositeTracker.Complete();
                return LoadResult<List<IAssetHandle<T>>>.Success(handles);
            }
            catch (Exception ex)
            {
                // WhenAll waits for every task to finish (successfully or not) before propagating a
                // fault, so every entry in `tasks` is already complete here — release whatever any
                // sibling task DID succeed at obtaining before this call returns, instead of leaving
                // it referenced only by a local array nobody outside this catch can ever reach again.
                if (tasks != null)
                {
                    foreach (var t in tasks)
                    {
#if UNITASK_PRESENT
                        if (t.Status == UniTaskStatus.Succeeded)
                        {
                            try
                            {
                                t.GetAwaiter().GetResult()?.Dispose();
                            }
                            catch (Exception disposeEx)
                            {
                                Debug.LogWarning($"[ProgressiveAssetLoader] Failed to release a handle " +
                                    $"from a partially-completed batch load: {disposeEx.Message}");
                            }
                        }
#else
                        if (t != null && t.Status == TaskStatus.RanToCompletion)
                        {
                            t.Result?.Dispose();
                        }
#endif
                    }
                }

                return LoadResult<List<IAssetHandle<T>>>.Failure(
                    LoadErrorCode.OperationFailed,
                    "Exception during batch load",
                    null,
                    string.Join(",", addresses),
                    ex
                );
            }
            finally
            {
                if (onProgress != null)
                {
                    compositeTracker.OnProgressChanged -= onProgress;
                }
            }
        }

        /// <summary>
        /// Load multiple assets with composite progress tracking. Returns whether every address
        /// succeeded.
        /// </summary>
        /// <remarks>
        /// HANDOFF_TO_SESSION_B.md L-2: this used to open its own Addressables operation per
        /// address (bypassing <paramref name="loader"/> entirely — see
        /// <see cref="LoadAssetWithProgressAsync{T}"/>'s remarks) and then discard every handle
        /// <c>Task.WhenAll</c> resolved — ten addresses meant ten bundles pinned for the rest of the
        /// process with no API able to reach them. It also always returned <c>true</c> regardless
        /// of whether any address actually failed.
        ///
        /// Both are fixed by delegating to <see cref="LoadMultipleWithProgressAsyncSafe{T}"/>: every
        /// load now goes through <paramref name="loader"/>'s own single-flight/cache, and the
        /// returned <c>bool</c> reflects whether every address actually succeeded. This method's own
        /// <c>bool</c> signature stays exactly as it was (repo invariant 6) — it has no way to hand
        /// individual handles back to its caller, so on success it releases this call's own share of
        /// each one immediately, leaving every asset owned solely by <paramref name="loader"/>'s
        /// cache rather than pinned by a reference nobody will ever give back. A caller that wants
        /// the actual handles should call <see cref="LoadMultipleWithProgressAsyncSafe{T}"/> instead.
        /// </remarks>
#if UNITASK_PRESENT
        public static async UniTask<bool> LoadMultipleWithProgressAsync<T>(
            this AssetLoader loader,
            string[] addresses,
            Action<ProgressInfo> onProgress)
#else
        public static async Task<bool> LoadMultipleWithProgressAsync<T>(
            this AssetLoader loader,
            string[] addresses,
            Action<ProgressInfo> onProgress)
#endif
        {
            var result = await LoadMultipleWithProgressAsyncSafe<T>(loader, addresses, onProgress);

            if (result.IsSuccess)
            {
                foreach (var handle in result.Value)
                {
                    handle?.Dispose();
                }
            }

            return result.IsSuccess;
        }
    }
}
