using System;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace AddressableManager.Core
{
    /// <summary>
    /// Concrete implementation of IAssetHandle with reference counting
    ///
    /// Ownership: a handle is born with one reference, owned by the caller that received it.
    /// Every further owner — the loader's cache, another caller served from that cache — takes its
    /// own reference through <see cref="TryRetain"/>. The underlying Addressables operation is
    /// released exactly once: when the last reference goes away, or when the owning loader tears
    /// down and calls <c>ForceRelease</c>.
    /// </summary>
    public class AssetHandle<T> : IAssetHandle<T>, IOwnedHandle
    {
        private AsyncOperationHandle<T> _handle;

        // Plain mutable field on purpose — see AssetReferenceCounter for why it must never be
        // copied, and never made readonly.
        private AssetReferenceCounter _references;

        public T Asset => IsValid ? _handle.Result : default;

        // The count is part of validity. Once the last reference goes, or an owner force-releases
        // during teardown or eviction, the Addressables operation can still report IsValid() for
        // an instant — and a cache gate trusting only that would hand this out again at count
        // zero. Reading the count first also makes IsValid and TryRetain agree: both read the one
        // word, so there is no state where this claims to be valid and TryRetain refuses.
        public bool IsValid => _references.IsAlive
                               && _handle.IsValid()
                               && _handle.Status == AsyncOperationStatus.Succeeded;

        public AsyncOperationStatus Status => _handle.Status;
        public float Progress => _handle.PercentComplete;
        public int ReferenceCount => _references.Count;

        bool IOwnedHandle.IsAlive => _references.IsAlive;

        public AssetHandle(AsyncOperationHandle<T> handle)
        {
            _handle = handle;
            _references = new AssetReferenceCounter(1); // The reference its receiver owns
        }

        public void Retain()
        {
            if (_references.TryRetain()) return;

            throw new ObjectDisposedException(
                nameof(AssetHandle<T>),
                "[AssetHandle] Cannot retain a handle whose reference count already reached zero");
        }

        public bool TryRetain()
        {
            return _references.TryRetain();
        }

        public void Release()
        {
            if (_references.Release()) ReleaseOperation();
        }

        public AsyncOperationHandle<T> GetHandle()
        {
            return _handle;
        }

        /// <summary>
        /// Drop this owner's reference. Identical to <see cref="Release"/>, so the documented
        /// <c>using var handle = await ...</c> pattern cannot destroy an asset that other owners
        /// still hold.
        /// </summary>
        public void Dispose()
        {
            Release();
        }

        /// <summary>
        /// Hard release, for the owning loader or cache only: drops the Addressables operation
        /// regardless of the current reference count. Idempotent, because teardown can reach the
        /// same handle through more than one collection.
        /// </summary>
        internal void ForceRelease()
        {
            if (_references.ForceRelease()) ReleaseOperation();
        }

        void IOwnedHandle.ForceRelease() => ForceRelease();

        private void ReleaseOperation()
        {
            if (_handle.IsValid())
            {
                Addressables.Release(_handle);
            }
        }
    }
}
