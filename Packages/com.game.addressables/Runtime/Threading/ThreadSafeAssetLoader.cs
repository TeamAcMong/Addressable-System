using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Loaders;

namespace AddressableManager.Threading
{
    /// <summary>
    /// Thread-safe wrapper for AssetLoader
    /// Automatically dispatches all operations to Unity's main thread
    ///
    /// Usage:
    ///   var threadSafeLoader = new ThreadSafeAssetLoader(scopeName);
    ///
    ///   // Can call from background thread - automatically queued to main thread
    ///   var handle = await threadSafeLoader.LoadAssetAsync<Sprite>("UI/Icon");
    /// </summary>
    public class ThreadSafeAssetLoader : IDisposable
    {
        private readonly AssetLoader _innerLoader;
        private bool _disposed;

        /// <summary>
        /// Create thread-safe asset loader
        /// </summary>
        /// <param name="scopeName">Scope name for monitoring</param>
        public ThreadSafeAssetLoader(string scopeName = "Unknown")
        {
            _innerLoader = new AssetLoader(scopeName);
        }

        /// <summary>
        /// Load asset asynchronously (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            // If already on main thread, call directly
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetAsync<T>(address);
            }

            // Otherwise queue to main thread
            var operation = new LoadOperation<T>(() => _innerLoader.LoadAssetAsync<T>(address));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                operation.Execute();
            });

            return operation.Task;
        }

        /// <summary>
        /// Load asset by AssetReference (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
                throw new ArgumentException("Invalid AssetReference", nameof(assetReference));

            // If already on main thread, call directly
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetAsync<T>(assetReference);
            }

            // Otherwise queue to main thread
            var operation = new LoadOperation<T>(() => _innerLoader.LoadAssetAsync<T>(assetReference));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                operation.Execute();
            });

            return operation.Task;
        }

        /// <summary>
        /// Load multiple assets by label (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(label))
                throw new ArgumentNullException(nameof(label));

            // If already on main thread, call directly
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetsByLabelAsync<T>(label);
            }

            // Otherwise queue to main thread
            var tcs = new TaskCompletionSource<List<IAssetHandle<T>>>();

            UnityMainThreadDispatcher.Enqueue(async () =>
            {
                try
                {
                    var result = await _innerLoader.LoadAssetsByLabelAsync<T>(label);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        /// <summary>
        /// Instantiate GameObject (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public Task<GameObject> InstantiateAsync(string address, Transform parent = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            // If already on main thread, call directly
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.InstantiateAsync(address, parent);
            }

            // Otherwise queue to main thread
            var operation = new InstantiateOperation(() => _innerLoader.InstantiateAsync(address, parent));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                operation.Execute();
            });

            return operation.Task;
        }

        /// <summary>
        /// Instantiate GameObject with position/rotation (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public Task<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            // If already on main thread, call directly
            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.InstantiateAsync(address, position, rotation, parent);
            }

            // Otherwise queue to main thread
            var operation = new InstantiateOperation(() => _innerLoader.InstantiateAsync(address, position, rotation, parent));

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                operation.Execute();
            });

            return operation.Task;
        }

        /// <summary>
        /// Clear cache (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public void ClearCache()
        {
            if (_disposed) return;

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                _innerLoader.ClearCache();
            }
            else
            {
                UnityMainThreadDispatcher.EnqueueAndWait(() => _innerLoader.ClearCache());
            }
        }

        /// <summary>
        /// Get cache statistics (thread-safe)
        /// Can be called from any thread
        /// </summary>
        public (int cachedAssets, int activeHandles) GetCacheStats()
        {
            if (_disposed)
                return (0, 0);

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.GetCacheStats();
            }
            else
            {
                var result = (0, 0);
                UnityMainThreadDispatcher.EnqueueAndWait(() => result = _innerLoader.GetCacheStats());
                return result;
            }
        }

        /// <summary>
        /// Dispose and cleanup (thread-safe)
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                _innerLoader?.Dispose();
            }
            else
            {
                UnityMainThreadDispatcher.EnqueueAndWait(() => _innerLoader?.Dispose());
            }

            _disposed = true;
        }
    }
}
