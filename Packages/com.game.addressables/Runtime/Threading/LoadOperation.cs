using System;
using System.Threading.Tasks;
using AddressableManager.Core;

namespace AddressableManager.Threading
{
    /// <summary>
    /// Represents a queued load operation for thread-safe loading
    /// </summary>
    internal class LoadOperation<T>
    {
        private readonly TaskCompletionSource<IAssetHandle<T>> _tcs;
        private readonly Func<Task<IAssetHandle<T>>> _loadFunc;

        public Task<IAssetHandle<T>> Task => _tcs.Task;

        public LoadOperation(Func<Task<IAssetHandle<T>>> loadFunc)
        {
            _loadFunc = loadFunc ?? throw new ArgumentNullException(nameof(loadFunc));
            _tcs = new TaskCompletionSource<IAssetHandle<T>>();
        }

        /// <summary>
        /// Execute the load operation (must be called from main thread)
        /// </summary>
        public async void Execute()
        {
            try
            {
                var result = await _loadFunc();
                _tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.SetException(ex);
            }
        }

        /// <summary>
        /// Cancel the operation
        /// </summary>
        public void Cancel()
        {
            _tcs.SetCanceled();
        }
    }

    /// <summary>
    /// Represents a queued instantiate operation for thread-safe loading
    /// </summary>
    internal class InstantiateOperation
    {
        private readonly TaskCompletionSource<UnityEngine.GameObject> _tcs;
        private readonly Func<Task<UnityEngine.GameObject>> _instantiateFunc;

        public Task<UnityEngine.GameObject> Task => _tcs.Task;

        public InstantiateOperation(Func<Task<UnityEngine.GameObject>> instantiateFunc)
        {
            _instantiateFunc = instantiateFunc ?? throw new ArgumentNullException(nameof(instantiateFunc));
            _tcs = new TaskCompletionSource<UnityEngine.GameObject>();
        }

        /// <summary>
        /// Execute the instantiate operation (must be called from main thread)
        /// </summary>
        public async void Execute()
        {
            try
            {
                var result = await _instantiateFunc();
                _tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.SetException(ex);
            }
        }

        /// <summary>
        /// Cancel the operation
        /// </summary>
        public void Cancel()
        {
            _tcs.SetCanceled();
        }
    }
}
