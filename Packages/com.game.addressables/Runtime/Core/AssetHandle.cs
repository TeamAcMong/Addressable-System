using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressableManager.Core
{
    /// <summary>
    /// Concrete implementation of IAssetHandle with reference counting
    /// </summary>
    public class AssetHandle<T> : IAssetHandle<T>
    {
        private AsyncOperationHandle<T> _handle;
        private int _referenceCount;
        private bool _disposed;

        public T Asset => IsValid ? _handle.Result : default;
        public bool IsValid => _handle.IsValid() && _handle.Status == AsyncOperationStatus.Succeeded;
        public AsyncOperationStatus Status => _handle.Status;
        public float Progress => _handle.PercentComplete;
        public int ReferenceCount => _referenceCount;

        public AssetHandle(AsyncOperationHandle<T> handle)
        {
            _handle = handle;
            _referenceCount = 1; // Start with 1 reference
        }

        public void Retain()
        {
            if (_disposed)
            {
                Debug.LogWarning("[AssetHandle] Cannot retain a disposed handle");
                return;
            }

            _referenceCount++;
        }

        public void Release()
        {
            if (_disposed)
            {
                Debug.LogWarning("[AssetHandle] Handle already disposed");
                return;
            }

            _referenceCount--;

            if (_referenceCount <= 0)
            {
                Dispose();
            }
        }

        public AsyncOperationHandle<T> GetHandle()
        {
            return _handle;
        }

        public void Dispose()
        {
            if (_disposed) return;

            if (_handle.IsValid())
            {
                Addressables.Release(_handle);
            }

            _disposed = true;
            _referenceCount = 0;
        }
    }
}
