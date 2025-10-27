using System;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressableManager.Core
{
    /// <summary>
    /// Wrapper interface for AsyncOperationHandle with reference counting and lifecycle management
    /// </summary>
    /// <typeparam name="T">Type of asset being handled</typeparam>
    public interface IAssetHandle<T> : IDisposable
    {
        /// <summary>
        /// The loaded asset instance
        /// </summary>
        T Asset { get; }

        /// <summary>
        /// Whether the asset is loaded and valid
        /// </summary>
        bool IsValid { get; }

        /// <summary>
        /// Current loading status
        /// </summary>
        AsyncOperationStatus Status { get; }

        /// <summary>
        /// Loading progress (0-1)
        /// </summary>
        float Progress { get; }

        /// <summary>
        /// Reference count for this handle
        /// </summary>
        int ReferenceCount { get; }

        /// <summary>
        /// Increment reference count (prevents auto-release)
        /// </summary>
        void Retain();

        /// <summary>
        /// Decrement reference count (auto-releases when reaches 0)
        /// </summary>
        void Release();

        /// <summary>
        /// Get the underlying AsyncOperationHandle
        /// </summary>
        AsyncOperationHandle<T> GetHandle();
    }
}
