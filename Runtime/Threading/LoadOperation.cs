using System;
using System.Threading.Tasks;
using AddressableManager.Core;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Threading
{
    /// <summary>
    /// Represents a queued load operation for thread-safe loading.
    /// Internal completion source switches between
    /// <see cref="TaskCompletionSource{TResult}"/> and
    /// <see cref="Cysharp.Threading.Tasks.UniTaskCompletionSource{T}"/>
    /// depending on whether <c>com.cysharp.unitask</c> is installed.
    /// </summary>
    internal class LoadOperation<T>
    {
#if UNITASK_PRESENT
        private readonly UniTaskCompletionSource<IAssetHandle<T>> _tcs;
        private readonly Func<UniTask<IAssetHandle<T>>> _loadFunc;

        public UniTask<IAssetHandle<T>> Task => _tcs.Task;

        public LoadOperation(Func<UniTask<IAssetHandle<T>>> loadFunc)
        {
            _loadFunc = loadFunc ?? throw new ArgumentNullException(nameof(loadFunc));
            _tcs = new UniTaskCompletionSource<IAssetHandle<T>>();
        }

        public async void Execute()
        {
            try
            {
                var result = await _loadFunc();
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Cancel() => _tcs.TrySetCanceled();
#else
        private readonly TaskCompletionSource<IAssetHandle<T>> _tcs;
        private readonly Func<Task<IAssetHandle<T>>> _loadFunc;

        public Task<IAssetHandle<T>> Task => _tcs.Task;

        public LoadOperation(Func<Task<IAssetHandle<T>>> loadFunc)
        {
            _loadFunc = loadFunc ?? throw new ArgumentNullException(nameof(loadFunc));
            _tcs = new TaskCompletionSource<IAssetHandle<T>>();
        }

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

        public void Cancel() => _tcs.SetCanceled();
#endif
    }

    /// <summary>
    /// Represents a queued instantiate operation for thread-safe loading.
    /// Inner completion source mirrors <see cref="LoadOperation{T}"/>.
    /// </summary>
    internal class InstantiateOperation
    {
#if UNITASK_PRESENT
        private readonly UniTaskCompletionSource<UnityEngine.GameObject> _tcs;
        private readonly Func<UniTask<UnityEngine.GameObject>> _instantiateFunc;

        public UniTask<UnityEngine.GameObject> Task => _tcs.Task;

        public InstantiateOperation(Func<UniTask<UnityEngine.GameObject>> instantiateFunc)
        {
            _instantiateFunc = instantiateFunc ?? throw new ArgumentNullException(nameof(instantiateFunc));
            _tcs = new UniTaskCompletionSource<UnityEngine.GameObject>();
        }

        public async void Execute()
        {
            try
            {
                var result = await _instantiateFunc();
                _tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }

        public void Cancel() => _tcs.TrySetCanceled();
#else
        private readonly TaskCompletionSource<UnityEngine.GameObject> _tcs;
        private readonly Func<Task<UnityEngine.GameObject>> _instantiateFunc;

        public Task<UnityEngine.GameObject> Task => _tcs.Task;

        public InstantiateOperation(Func<Task<UnityEngine.GameObject>> instantiateFunc)
        {
            _instantiateFunc = instantiateFunc ?? throw new ArgumentNullException(nameof(instantiateFunc));
            _tcs = new TaskCompletionSource<UnityEngine.GameObject>();
        }

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

        public void Cancel() => _tcs.SetCanceled();
#endif
    }
}
