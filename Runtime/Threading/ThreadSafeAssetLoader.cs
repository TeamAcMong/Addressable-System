using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using AddressableManager.Core;
using AddressableManager.Loaders;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Threading
{
    /// <summary>
    /// Thread-safe wrapper for <see cref="AssetLoader"/>.
    /// Dispatches every operation to Unity's main thread.
    ///
    /// All public async signatures switch between <c>Task&lt;T&gt;</c> and
    /// <c>UniTask&lt;T&gt;</c> automatically based on whether
    /// <c>com.cysharp.unitask</c> is installed (the <c>UNITASK_PRESENT</c>
    /// versionDefine on the Runtime asmdef).
    ///
    /// Usage:
    ///   var loader = new ThreadSafeAssetLoader(scopeName);
    ///   var handle = await loader.LoadAssetAsync&lt;Sprite&gt;("UI/Icon");
    /// </summary>
    public class ThreadSafeAssetLoader : IDisposable
    {
        private readonly AssetLoader _innerLoader;
        private bool _disposed;

        public ThreadSafeAssetLoader(string scopeName = "Unknown")
        {
            _innerLoader = new AssetLoader(scopeName);
        }

        /// <summary>
        /// Load asset asynchronously (thread-safe). Can be called from any thread.
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
#endif
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetAsync<T>(address);
            }

            var operation = new LoadOperation<T>(() => _innerLoader.LoadAssetAsync<T>(address));
            UnityMainThreadDispatcher.Enqueue(() => operation.Execute());
            return operation.Task;
        }

        /// <summary>
        /// Load asset by AssetReference (thread-safe).
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#else
        public Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
#endif
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
                throw new ArgumentException("Invalid AssetReference", nameof(assetReference));

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetAsync<T>(assetReference);
            }

            var operation = new LoadOperation<T>(() => _innerLoader.LoadAssetAsync<T>(assetReference));
            UnityMainThreadDispatcher.Enqueue(() => operation.Execute());
            return operation.Task;
        }

        /// <summary>
        /// Load multiple assets by label (thread-safe).
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#else
        public Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
#endif
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(label))
                throw new ArgumentNullException(nameof(label));

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.LoadAssetsByLabelAsync<T>(label);
            }

#if UNITASK_PRESENT
            var tcs = new UniTaskCompletionSource<List<IAssetHandle<T>>>();
            UnityMainThreadDispatcher.Enqueue(async () =>
            {
                try
                {
                    var result = await _innerLoader.LoadAssetsByLabelAsync<T>(label);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
#else
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
#endif
        }

        /// <summary>
        /// Instantiate GameObject (thread-safe).
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<GameObject> InstantiateAsync(string address, Transform parent = null)
#else
        public Task<GameObject> InstantiateAsync(string address, Transform parent = null)
#endif
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.InstantiateAsync(address, parent);
            }

            var operation = new InstantiateOperation(() => _innerLoader.InstantiateAsync(address, parent));
            UnityMainThreadDispatcher.Enqueue(() => operation.Execute());
            return operation.Task;
        }

        /// <summary>
        /// Instantiate GameObject with position/rotation (thread-safe).
        /// </summary>
#if UNITASK_PRESENT
        public UniTask<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#else
        public Task<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
#endif
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ThreadSafeAssetLoader));

            if (string.IsNullOrEmpty(address))
                throw new ArgumentNullException(nameof(address));

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.InstantiateAsync(address, position, rotation, parent);
            }

            var operation = new InstantiateOperation(() => _innerLoader.InstantiateAsync(address, position, rotation, parent));
            UnityMainThreadDispatcher.Enqueue(() => operation.Execute());
            return operation.Task;
        }

        /// <summary>
        /// Clear cache (thread-safe).
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
        /// Get cache statistics (thread-safe).
        /// </summary>
        public (int cachedAssets, int activeHandles) GetCacheStats()
        {
            if (_disposed)
                return (0, 0);

            if (UnityMainThreadDispatcher.IsMainThread)
            {
                return _innerLoader.GetCacheStats();
            }

            var result = (0, 0);
            UnityMainThreadDispatcher.EnqueueAndWait(() => result = _innerLoader.GetCacheStats());
            return result;
        }

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
