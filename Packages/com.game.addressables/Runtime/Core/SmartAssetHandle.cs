using System;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Threading;

namespace AddressableManager.Core
{
    /// <summary>
    /// Smart wrapper for IAssetHandle with automatic memory management
    /// Automatically releases when disposed or garbage collected
    ///
    /// Usage with 'using' statement (recommended):
    ///   using var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
    ///   // Auto-released when scope exits
    ///
    /// Usage with manual disposal:
    ///   var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
    ///   // ... use handle ...
    ///   handle.Dispose(); // Explicit release
    ///
    /// Benefits:
    /// - No memory leaks from forgotten Release() calls
    /// - C# using pattern support
    /// - Still supports manual Retain/Release if needed
    ///
    /// Ownership: the wrapper gives back exactly the references it owns, no more.
    /// - autoRelease (the default) takes over the reference the inner handle was handed to its
    ///   receiver with — it does not add one, so the original must not be released separately.
    /// - autoRelease:false wraps without taking anything: the caller keeps owning the handle it
    ///   passed in and still has to release it.
    /// Every Retain() through the wrapper adds one the wrapper owns either way, and Dispose()
    /// gives back everything it still owns.
    /// </summary>
    public class SmartAssetHandle<T> : IAssetHandle<T>, IRetainableHandle
    {
        private IAssetHandle<T> _innerHandle;
        private bool _disposed;

        // References on the inner handle that this wrapper still owes back. This is the single
        // record of what the wrapper owns: a separate auto-release flag consulted only by Dispose()
        // made autoRelease:false drop owned references on the floor with nobody left holding them.
        private int _ownedReferences;

        /// <summary>
        /// Create smart handle wrapper
        /// </summary>
        /// <param name="innerHandle">Handle to wrap</param>
        /// <param name="autoRelease">Take over the caller's reference and release it on dispose (default: true)</param>
        public SmartAssetHandle(IAssetHandle<T> innerHandle, bool autoRelease = true)
        {
            _innerHandle = innerHandle ?? throw new ArgumentNullException(nameof(innerHandle));
            _ownedReferences = autoRelease ? 1 : 0;
        }

        #region IAssetHandle Implementation

        public T Asset => _innerHandle != null ? _innerHandle.Asset : default;

        // A consumed wrapper is never valid, even if the inner handle survives because another
        // owner still holds a reference to it.
        public bool IsValid => !_disposed && (_innerHandle?.IsValid ?? false);

        public AsyncOperationStatus Status => _innerHandle?.Status ?? AsyncOperationStatus.None;

        public float Progress => _innerHandle?.Progress ?? 0f;

        public int ReferenceCount => _innerHandle?.ReferenceCount ?? 0;

        public void Retain()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(SmartAssetHandle<T>),
                    "[SmartAssetHandle] Cannot retain a wrapper that already gave its references back");
            }

            // Count it only once the inner handle really granted it, so a throwing Retain() cannot
            // leave the wrapper owing a reference it never took.
            _innerHandle.Retain();
            _ownedReferences++;
        }

        public bool TryRetain()
        {
            if (_disposed) return false;

            if (_innerHandle == null || !_innerHandle.TryRetain()) return false;

            _ownedReferences++;
            return true;
        }

        public void Release()
        {
            if (_disposed)
            {
                Debug.LogWarning("[SmartAssetHandle] Handle already disposed");
                return;
            }

            if (_ownedReferences <= 0)
            {
                // A wrapper built with autoRelease:false, or one that already handed its
                // references back, owns nothing. Releasing here would spend the *caller's*
                // reference and free the asset under whoever is still using it.
                Debug.LogWarning("[SmartAssetHandle] Wrapper owns no reference to release");
                return;
            }

            _ownedReferences--;
            _innerHandle?.Release();

            // Mark consumed once the wrapper owes nothing: without this, a Release() followed by
            // the Dispose() of the enclosing `using` decrements twice for one reference.
            if (_ownedReferences <= 0) MarkConsumed();
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
        /// Give back every reference this wrapper owns. A wrapper that owns none — built with
        /// autoRelease:false, or already unwrapped — releases nothing.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            // Give back the reference it took over on construction, plus one for each Retain()
            // that went through it. Count down before each release and catch per iteration: a
            // single throwing Release must not abandon the references still owed behind it,
            // because MarkConsumed below erases the wrapper's record of them.
            while (_ownedReferences > 0 && _innerHandle != null)
            {
                _ownedReferences--;

                try
                {
                    _innerHandle.Release();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SmartAssetHandle] Error releasing handle: {ex.Message}");
                }
            }

            MarkConsumed();
        }

        /// <summary>
        /// Finalizer - reports a forgotten disposal
        /// </summary>
        ~SmartAssetHandle()
        {
            // Only worth acting on when the wrapper actually took references with it; a
            // non-owning wrapper going undisposed costs nothing.
            if (_disposed || _ownedReferences <= 0) return;

            Debug.LogWarning(
                $"[SmartAssetHandle] Handle was not properly disposed! " +
                $"Consider using 'using' statement for automatic disposal.\n" +
                $"Asset type: {typeof(T).Name}"
            );

            // Never release inline. Release() is a real decrement now, so from here it would
            // routinely be the call that reaches Addressables — and ResourceManager is
            // main-thread-only, so releasing on the finalizer thread corrupts its bookkeeping
            // instead of recovering the leak.
            var owed = _ownedReferences;
            var inner = _innerHandle;

            try
            {
                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    for (int i = 0; i < owed; i++)
                    {
                        inner?.Release();
                    }
                });
            }
            catch (Exception ex)
            {
                // Enqueue creates its dispatcher on first use, which needs the main thread. If it
                // cannot, the references stay held until the owning loader tears down — a leak,
                // which is recoverable, unlike a ResourceManager mutated off-thread.
                Debug.LogWarning($"[SmartAssetHandle] Could not recover the leaked references: {ex.Message}");
            }
        }

        /// <summary>
        /// Mark the wrapper as owing nothing further. Idempotent.
        /// </summary>
        private void MarkConsumed()
        {
            _innerHandle = null;
            _ownedReferences = 0;
            _disposed = true;

            // Nothing left for the finalizer to warn about
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Hand every reference this wrapper owns back to the caller, for manual memory management.
        /// Dispose() then releases nothing, and the caller owes the inner handle that many
        /// Release() calls.
        /// </summary>
        public void DisableAutoRelease()
        {
            // Not a flag Dispose() consults: leaving the references recorded here while refusing
            // to give them back is how the wrapper used to discard them silently.
            _ownedReferences = 0;
        }

        /// <summary>
        /// Hand the caller's reference to the wrapper again, so Dispose() releases it. The
        /// caller must not release the inner handle itself afterwards.
        /// </summary>
        public void EnableAutoRelease()
        {
            if (_disposed)
            {
                Debug.LogWarning("[SmartAssetHandle] Cannot re-arm a wrapper that gave its references back");
                return;
            }

            if (_ownedReferences <= 0) _ownedReferences = 1;
        }

        /// <summary>
        /// Get the inner handle (unwrap).
        /// Transfers every reference this wrapper owns to the caller, who must release them.
        /// </summary>
        public IAssetHandle<T> Unwrap()
        {
            // Consume the wrapper as part of the transfer. Leaving it live over references it no
            // longer owns lets a later Release()/Dispose() spend them a second time.
            var inner = _innerHandle;
            MarkConsumed();
            return inner;
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
