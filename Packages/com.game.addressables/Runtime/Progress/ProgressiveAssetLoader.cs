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
        /// Download dependencies with progress tracking
        /// </summary>
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

            AsyncOperationHandle operation = default;
            bool operationStarted = false;

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Downloading {address}"));

                operation = Addressables.DownloadDependenciesAsync(address);
                operationStarted = true;

                float lastProgress = 0f;
                float startTime = Time.realtimeSinceStartup;

                // Poll progress with download speed calculation
                while (!operation.IsDone)
                {
                    float currentProgress = operation.PercentComplete;
                    float deltaProgress = currentProgress - lastProgress;
                    float deltaTime = Time.realtimeSinceStartup - startTime;

                    float speed = deltaTime > 0 ? (deltaProgress / deltaTime) : 0f;
                    float eta = speed > 0 ? (1f - currentProgress) / speed : 0f;

                    var info = new ProgressInfo
                    {
                        Progress = currentProgress,
                        CurrentOperation = $"Downloading {address}",
                        DownloadSpeed = speed * 100f, // Approximate KB/s
                        EstimatedTimeRemaining = eta
                    };

                    tracker.UpdateProgress(info);

                    lastProgress = currentProgress;
#if UNITASK_PRESENT
                    await UniTask.Yield();
#else
                    await Task.Yield();
#endif
                }

                tracker.Complete();
                return operation.Status == AsyncOperationStatus.Succeeded;
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

                // Always release the download dependencies handle — the cache is owned by Addressables itself,
                // we only needed the handle for progress tracking.
                if (operationStarted && operation.IsValid())
                {
                    Addressables.Release(operation);
                }
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
