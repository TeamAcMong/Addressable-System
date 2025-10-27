using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Core;
using AddressableManager.Loaders;

namespace AddressableManager.Progress
{
    /// <summary>
    /// Extension to AssetLoader that provides progress tracking capabilities
    /// </summary>
    public static class ProgressiveAssetLoader
    {
        /// <summary>
        /// Load asset with progress tracking
        /// </summary>
        public static async Task<IAssetHandle<T>> LoadAssetWithProgressAsync<T>(
            this AssetLoader loader,
            string address,
            Action<ProgressInfo> onProgress)
        {
            var tracker = new ProgressTracker();

            if (onProgress != null)
            {
                tracker.OnProgressChanged += onProgress;
            }

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Loading {address}"));

                var operation = Addressables.LoadAssetAsync<T>(address);

                // Poll progress
                while (!operation.IsDone)
                {
                    var info = new ProgressInfo(operation.PercentComplete, $"Loading {address}");
                    tracker.UpdateProgress(info);
                    await Task.Yield();
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    tracker.Complete();
                    var handle = new AssetHandle<T>(operation);
                    return handle;
                }
                else
                {
                    Debug.LogError($"[ProgressiveLoader] Failed to load: {address}");
                    return null;
                }
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
            }
        }

        /// <summary>
        /// Download dependencies with progress tracking
        /// </summary>
        public static async Task<bool> DownloadWithProgressAsync(
            string address,
            Action<ProgressInfo> onProgress)
        {
            var tracker = new ProgressTracker();

            if (onProgress != null)
            {
                tracker.OnProgressChanged += onProgress;
            }

            try
            {
                tracker.UpdateProgress(new ProgressInfo(0f, $"Downloading {address}"));

                var operation = Addressables.DownloadDependenciesAsync(address);

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
                    await Task.Yield();
                }

                tracker.Complete();

                Addressables.Release(operation);
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
            }
        }

        /// <summary>
        /// Load multiple assets with composite progress tracking
        /// </summary>
        public static async Task<bool> LoadMultipleWithProgressAsync<T>(
            this AssetLoader loader,
            string[] addresses,
            Action<ProgressInfo> onProgress)
        {
            var compositeTracker = new CompositeProgressTracker();

            if (onProgress != null)
            {
                compositeTracker.OnProgressChanged += onProgress;
            }

            try
            {
                var tasks = new Task<IAssetHandle<T>>[addresses.Length];

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

                await Task.WhenAll(tasks);

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
