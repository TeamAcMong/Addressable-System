using System;
using System.Threading;
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
        /// Increment reference count (prevents auto-release).
        /// Throws <see cref="ObjectDisposedException"/> when the handle already reached zero
        /// references — use the <c>TryRetain()</c> extension where that is an expected outcome.
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

    /// <summary>
    /// Atomic try-increment, on an internal interface rather than on <see cref="IAssetHandle{T}"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IAssetHandle{T}"/> is public and consumers implement it (test doubles, decorators),
    /// so a new abstract member there is a source break that repo invariant 6 defers to 5.0.0. The
    /// public entry point is the <c>TryRetain()</c> extension in <c>AssetHandleExtensions</c>, which
    /// routes every in-package handle through this interface and only falls back to the
    /// check-then-retain two-step for a foreign implementation that cannot offer the atomic form.
    /// </remarks>
    internal interface IRetainableHandle
    {
        /// <summary>
        /// Takes a reference and returns true, or returns false when the handle is already dead.
        /// The single-step form of "check IsValid, then Retain()".
        /// </summary>
        bool TryRetain();
    }

    /// <summary>
    /// Owner-side release contract, implemented by every handle a loader or cache tracks.
    /// Lets a teardown path release a handle without knowing its asset type.
    /// </summary>
    internal interface IOwnedHandle : IRetainableHandle, IDisposable
    {
        /// <summary>
        /// False once the underlying operation has been released
        /// </summary>
        bool IsAlive { get; }

        /// <summary>
        /// Hard release, ignoring the reference count. Idempotent, because teardown can reach the
        /// same handle through more than one collection.
        /// </summary>
        void ForceRelease();
    }

    /// <summary>
    /// The reference-count state every refcounted handle in this package shares.
    /// </summary>
    /// <remarks>
    /// Each owner must hold this in a plain mutable instance field and never expose it by value.
    /// <see cref="Interlocked"/> mutates the storage location it is handed, so a copy — a
    /// <c>readonly</c> field, a property, a <c>foreach</c> variable — would count references on a
    /// temporary that is then thrown away, and every count would silently read as if nothing had
    /// happened.
    /// </remarks>
    internal struct AssetReferenceCounter
    {
        // The count alone decides liveness: > 0 means the handle still holds its operation.
        //
        // A separate "released" flag alongside it cannot be flipped in the same instruction as the
        // count, and whichever order it is written in, one instant is published where the two
        // disagree — a handle reporting IsValid == true while TryRetain() refuses it, or the
        // reverse. Callers have no way to act on either. One word has no such instant.
        private int _count;

        // Arbitration only, never liveness: exactly one of Release/ForceRelease may perform the
        // underlying Addressables release, even when both reach zero concurrently.
        private int _releaseClaimed;

        public AssetReferenceCounter(int initialCount)
        {
            _count = initialCount;
            _releaseClaimed = 0;
        }

        /// <summary>
        /// Whether the underlying operation is still held.
        /// </summary>
        public bool IsAlive => Volatile.Read(ref _count) > 0;

        /// <summary>
        /// Live reference count; 0 once the last reference went away, so a released handle never
        /// reports references that no longer exist.
        /// </summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>
        /// Atomic try-increment. Returns false when there is nothing left to retain.
        /// </summary>
        /// <remarks>
        /// Testing validity and then calling Retain() is two steps and the handle can die in
        /// between; a CAS loop cannot resurrect a count that already reached zero.
        /// </remarks>
        public bool TryRetain()
        {
            while (true)
            {
                int current = Volatile.Read(ref _count);
                if (current <= 0) return false;

                if (Interlocked.CompareExchange(ref _count, current + 1, current) == current) return true;
            }
        }

        /// <summary>
        /// Give back one reference. True when this call dropped the last one, meaning the caller
        /// now owes the underlying release.
        /// </summary>
        public bool Release()
        {
            while (true)
            {
                int current = Volatile.Read(ref _count);

                // Already at zero — a redundant Release/Dispose is a no-op. Decrementing blindly
                // would drive the count negative, and every later TryRetain would still refuse
                // while ReferenceCount reported nonsense.
                if (current <= 0) return false;

                if (Interlocked.CompareExchange(ref _count, current - 1, current) != current) continue;

                return current == 1 && ClaimRelease();
            }
        }

        /// <summary>
        /// Drop the operation regardless of the count, for the owner that is tearing down.
        /// True only for the call that won the race, so the release still happens exactly once.
        /// </summary>
        public bool ForceRelease()
        {
            Interlocked.Exchange(ref _count, 0);
            return ClaimRelease();
        }

        private bool ClaimRelease()
        {
            return Interlocked.CompareExchange(ref _releaseClaimed, 1, 0) == 0;
        }
    }
}
