using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
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
            var tracker = new ProgressTracker();

            if (onProgress != null)
            {
                tracker.OnProgressChanged += onProgress;
            }

            AsyncOperationHandle<T> operation = default;
            bool operationStarted = false;
            bool succeeded = false;

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Loading {address}"));

                operation = Addressables.LoadAssetAsync<T>(address);
                operationStarted = true;

                // Poll progress
                while (!operation.IsDone)
                {
                    var info = new ProgressInfo(operation.PercentComplete, $"Loading {address}");
                    tracker.UpdateProgress(info);
#if UNITASK_PRESENT
                    await UniTask.Yield();
#else
                    await Task.Yield();
#endif
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    tracker.Complete();
                    succeeded = true;
                    return new AssetHandle<T>(operation);
                }

                Debug.LogError($"[ProgressiveLoader] Failed to load: {address}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProgressiveLoader] Exception: {ex.Message}");
                return null;
            }
            finally
            {
                if (onProgress != null)
                {
                    tracker.OnProgressChanged -= onProgress;
                }

                // Release the underlying Addressables handle if we never wrapped it in an AssetHandle.
                // AssetHandle takes ownership and releases on Dispose / refcount<=0; without it the handle leaks.
                if (operationStarted && !succeeded && operation.IsValid())
                {
                    Addressables.Release(operation);
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
        /// Load multiple assets with composite progress tracking
        /// </summary>
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
            var compositeTracker = new CompositeProgressTracker();

            if (onProgress != null)
            {
                compositeTracker.OnProgressChanged += onProgress;
            }

            try
            {
#if UNITASK_PRESENT
                var tasks = new UniTask<IAssetHandle<T>>[addresses.Length];
#else
                var tasks = new Task<IAssetHandle<T>>[addresses.Length];
#endif

                for (int i = 0; i < addresses.Length; i++)
                {
                    var childTracker = new ProgressTracker();
                    compositeTracker.AddTracker(childTracker, weight: 1f);

                    string address = addresses[i];
                    tasks[i] = loader.LoadAssetWithProgressAsync<T>(
                        address,
                        info => childTracker.UpdateProgress(info)
                    );
                }

#if UNITASK_PRESENT
                await UniTask.WhenAll(tasks);
#else
                await Task.WhenAll(tasks);
#endif

                compositeTracker.Complete();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProgressiveLoader] Batch load exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (onProgress != null)
                {
                    compositeTracker.OnProgressChanged -= onProgress;
                }
            }
        }
    }
}
