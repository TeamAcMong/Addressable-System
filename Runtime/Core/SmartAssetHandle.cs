using System;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressableManager.Core
{
    /// <summary>
    /// Smart wrapper for IAssetHandle with automatic memory management
    /// Automatically releases when disposed or garbage collected
    ///
    /// Usage with 'using' statement (recommended):
    ///   using var handle = loader.LoadAsync<Sprite>("UI/Icon").ToSmart();
    ///   // Auto-released when scope exits
    ///
    /// Usage with manual disposal:
    ///   var handle = loader.LoadAsync<Sprite>("UI/Icon").ToSmart();
    ///   // ... use handle ...
    ///   handle.Dispose(); // Explicit release
    ///
    /// Benefits:
    /// - No memory leaks from forgotten Release() calls
    /// - C# using pattern support
    /// - Still supports manual Retain/Release if needed
    /// </summary>
    public class SmartAssetHandle<T> : IAssetHandle<T>
    {
        private IAssetHandle<T> _innerHandle;
        private bool _autoReleaseEnabled;
        private bool _disposed;

        /// <summary>
        /// Create smart handle wrapper
        /// </summary>
        /// <param name="innerHandle">Handle to wrap</param>
        /// <param name="autoRelease">Enable auto-release on dispose (default: true)</param>
        public SmartAssetHandle(IAssetHandle<T> innerHandle, bool autoRelease = true)
        {
            _innerHandle = innerHandle ?? throw new ArgumentNullException(nameof(innerHandle));
            _autoReleaseEnabled = autoRelease;
        }

        #region IAssetHandle Implementation

        public T Asset => _innerHandle != null ? _innerHandle.Asset : default;

        public bool IsValid => _innerHandle?.IsValid ?? false;

        public AsyncOperationStatus Status => _innerHandle?.Status ?? AsyncOperationStatus.None;

        public float Progress => _innerHandle?.Progress ?? 0f;

        public int ReferenceCount => _innerHandle?.ReferenceCount ?? 0;

        public void Retain()
        {
            if (_disposed)
            {
                Debug.LogWarning("[SmartAssetHandle] Cannot retain disposed handle");
                return;
            }

            _innerHandle?.Retain();
        }

        public void Release()
        {
            if (_disposed)
            {
                Debug.LogWarning("[SmartAssetHandle] Handle already disposed");
                return;
            }

            _innerHandle?.Release();
        }

        public AsyncOperationHandle<T> GetHandle()
        {
            if (_disposed)
            {
                Debug.LogWarning("[SmartAssetHandle] Cannot get handle from disposed wrapper");
                return default;
            }

            return _innerHandle?.GetHandle() ?? default;
        }

        #endregion

        #region Automatic Memory Management

        /// <summary>
        /// Dispose and auto-release if enabled
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            if (_autoReleaseEnabled && _innerHandle != null)
            {
                try
                {
                    _innerHandle.Release();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SmartAssetHandle] Error releasing handle: {ex.Message}");
                }
            }

            _innerHandle = null;
            _disposed = true;

            // Suppress finalizer since we're disposing properly
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer - auto-release when garbage collected
        /// This is a safety net for forgotten disposals
        /// </summary>
        ~SmartAssetHandle()
        {
            if (!_disposed && _autoReleaseEnabled)
            {
                Debug.LogWarning(
                    $"[SmartAssetHandle] Handle was not properly disposed! " +
                    $"Consider using 'using' statement for automatic disposal.\n" +
                    $"Asset type: {typeof(T).Name}, Valid: {IsValid}"
                );

                // Try to release on finalizer thread
                // Note: This might not always work reliably
                try
                {
                    _innerHandle?.Release();
                }
                catch
                {
                    // Silently fail - we're on finalizer thread
                }
            }
        }

        /// <summary>
        /// Disable auto-release (for manual memory management)
        /// </summary>
        public void DisableAutoRelease()
        {
            _autoReleaseEnabled = false;
        }

        /// <summary>
        /// Enable auto-release
        /// </summary>
        public void EnableAutoRelease()
        {
            _autoReleaseEnabled = true;
        }

        /// <summary>
        /// Get the inner handle (unwrap)
        /// Warning: Caller is responsible for memory management after unwrapping
        /// </summary>
        public IAssetHandle<T> Unwrap()
        {
            _autoReleaseEnabled = false; // Disable auto-release when unwrapping
            return _innerHandle;
        }

        #endregion

        #region Implicit Conversions

        /// <summary>
        /// Implicit conversion to asset
        /// </summary>
        public static implicit operator T(SmartAssetHandle<T> handle)
        {
            return handle != null ? handle.Asset : default;
        }

        /// <summary>
        /// Implicit conversion to bool (for null checks)
        /// </summary>
        public static implicit operator bool(SmartAssetHandle<T> handle)
        {
            return handle?.IsValid ?? false;
        }

        #endregion
    }
}
