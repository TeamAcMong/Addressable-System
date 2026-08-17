using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Every live <see cref="AssetLoader"/>, so a catalog update — and the periodic tier/eviction
    /// pump — can reach all of them.
    /// </summary>
    /// <remarks>
    /// TWO DUTIES, ONE LIST
    ///
    /// <see cref="InvalidateAll"/> answers "a catalog update landed, whose caches are stale".
    /// <see cref="PumpAll"/>/<see cref="ForceEvictionAll"/> answer "who has tiering that needs
    /// draining". Those used to be two registries over two loader types, because
    /// <c>TieredAssetLoader</c> was a fork of <see cref="AssetLoader"/> that shared no reachability
    /// mechanism with it (LIFETIME_DESIGN.md "L-7: evidence", row L-3). Since tiering became a
    /// configuration of <see cref="AssetLoader"/> there is one population, so there is one list —
    /// and the catalog-update hole that fork had is closed by construction rather than by a second
    /// invalidation path that would have to be kept in step with this one.
    ///
    /// WHY THIS EXISTS
    ///
    /// After a catalog update, a handle a loader cached still wraps an operation resolved against
    /// the previous catalog. Nothing about it looks stale — the operation is valid and Succeeded —
    /// so the cache keeps serving it for the rest of the session. Addressables cannot invalidate it,
    /// because Addressables does not know these caches exist.
    ///
    /// <see cref="AssetLoader.InvalidateAddresses"/> fixes one loader. The problem was finding them:
    /// loaders are constructed in six places and only one of those populations was enumerable.
    ///
    ///   ScopeManager.GetOrCreateScope     tracked in a dictionary        reachable
    ///   BaseAssetScope                    Global / Scene / Hierarchy     NOT
    ///   HybridScope                       private instance fields        NOT
    ///   Advanced.CreateLoader             handed to the caller           NOT
    ///   MonitoredAssetLoader              wraps one privately            NOT
    ///   ThreadSafeAssetLoader             wraps one privately            NOT
    ///
    /// Invalidating only the first left the other five serving pre-update content indefinitely —
    /// and the first entry in the unreachable list is the default path a game takes.
    ///
    /// WEAK REFERENCES, NOT STRONG
    ///
    /// A registry of live objects that keeps them alive is a leak with a nice name. Loaders that are
    /// dropped without Dispose() — which is most of what Advanced.CreateLoader hands out — must stay
    /// collectable, so entries are weak and dead ones are pruned whenever the list is walked.
    ///
    /// THREAD SAFETY
    ///
    /// Registration is locked because construction is not confined to the main thread:
    /// ThreadSafeAssetLoader builds its inner loader wherever it is created (ThreadSafeAssetLoader.cs:34).
    /// Invalidation is main-thread only, which is not a restriction so much as a fact —
    /// AssetLoader.InvalidateAddresses asserts it (AssetLoader.cs:1685), and every caller reaches
    /// here after awaiting an Addressables operation.
    ///
    /// That also settles whether a ThreadSafeAssetLoader's inner loader is safe to touch: it is.
    /// That wrapper holds no lock of its own — it runs on the main thread directly and marshals
    /// everything else through UnityMainThreadDispatcher (ThreadSafeAssetLoader.cs:199-203), so all
    /// mutation of the inner loader already happens on the main thread. Walking it from here, on
    /// that same thread, races with nothing. The hazard flagged during design was calling
    /// InvalidateAddresses *through* the wrapper from a background thread; this does not do that.
    /// </remarks>
    internal static class AssetLoaderRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<WeakReference<AssetLoader>> Loaders =
            new List<WeakReference<AssetLoader>>();

        /// <summary>Record a loader. Called from the constructor; safe from any thread.</summary>
        internal static void Register(AssetLoader loader)
        {
            if (loader == null) return;

            lock (Gate)
            {
                Loaders.Add(new WeakReference<AssetLoader>(loader));
            }
        }

        /// <summary>
        /// Forget a loader. Called from <see cref="AssetLoader.Dispose"/> after its main-thread
        /// guard, so a Dispose that refused to run leaves the loader registered — it is still live.
        /// </summary>
        internal static void Unregister(AssetLoader loader)
        {
            if (loader == null) return;

            lock (Gate)
            {
                for (int i = Loaders.Count - 1; i >= 0; i--)
                {
                    if (!Loaders[i].TryGetTarget(out var candidate) || ReferenceEquals(candidate, loader))
                        Loaders.RemoveAt(i);
                }
            }
        }

        /// <summary>How many loaders are still alive. Diagnostics and tests; prunes as it counts.</summary>
        internal static int LiveCount
        {
            get
            {
                lock (Gate)
                {
                    Prune();
                    return Loaders.Count;
                }
            }
        }

        /// <summary>
        /// Give back every loader's cached reference to the given keys.
        /// </summary>
        /// <remarks>
        /// Returns how many loaders were reached, so a caller can log a number instead of asserting
        /// a silence. One loader throwing must not stop the rest: a partially invalidated set is bad,
        /// but it is strictly better than stopping at the first failure and leaving the remainder
        /// stale, and the exception is reported rather than swallowed.
        /// </remarks>
        internal static int InvalidateAll(IEnumerable<string> addresses)
        {
            if (addresses == null) return 0;

            // Materialised once: the list is walked per loader, and the caller's sequence may well
            // be a LINQ chain over a locator that would be re-enumerated each time.
            var keys = addresses as IList<string> ?? new List<string>(addresses);
            if (keys.Count == 0) return 0;

            // Outside the lock — see Snapshot()'s remarks. InvalidateAddresses can end with a scope
            // disposing itself, which calls back into Unregister; taking Gate again from inside it
            // would deadlock on a non-reentrant lock.
            int reached = 0;
            foreach (var loader in Snapshot())
            {
                try
                {
                    loader.InvalidateAddresses(keys);
                    reached++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AssetLoaderRegistry] A loader could not be invalidated after " +
                                     $"a catalog update: {ex.Message}. Assets it cached before the " +
                                     $"update may still be served from the previous catalog.");
                }
            }

            return reached;
        }

        /// <summary>
        /// Every live loader, copied out under <see cref="Gate"/> and returned with the lock
        /// released.
        /// </summary>
        /// <remarks>
        /// THE LOCK MUST NOT BE HELD ACROSS THE WALK. Every caller below re-enters loader code that
        /// can call back into <see cref="Unregister"/> — <see cref="InvalidateAll"/> through a scope
        /// disposing itself, <see cref="PumpAll"/>/<see cref="ForceEvictionAll"/> through an
        /// eviction that force-releases the last handle to an asset whose destruction tears down a
        /// scope. Taking <see cref="Gate"/> again from inside that walk deadlocks a non-reentrant
        /// lock, and <c>Monitor</c> only makes it survivable by accident of being reentrant on the
        /// same thread. Snapshotting also keeps the lock off the main thread for the duration of
        /// the walk, which for the pump is every 5 seconds forever.
        /// </remarks>
        private static AssetLoader[] Snapshot()
        {
            lock (Gate)
            {
                Prune();

                var live = new List<AssetLoader>(Loaders.Count);
                foreach (var reference in Loaders)
                {
                    if (reference.TryGetTarget(out var loader) && loader != null) live.Add(loader);
                }

                return live.ToArray();
            }
        }

        /// <summary>
        /// Periodic drain: evaluate tiers, then evict, on every live loader.
        /// </summary>
        /// <remarks>
        /// WHY THIS LIVES HERE NOW (L-3, and L-7's resolution)
        ///
        /// This body came from <c>TieredAssetLoaderRegistry.PumpAll</c>, which existed only because
        /// <c>TieredAssetLoader</c> was a fork that shared no reachability mechanism with
        /// <see cref="AssetLoader"/>. Tiering is a configuration of <see cref="AssetLoader"/> now,
        /// so the loaders that need pumping are already in this registry and the duplicate registry
        /// is gone.
        ///
        /// A loader built without tiering makes both calls no-ops
        /// (<see cref="AssetLoader.EvaluateTiers"/>/<see cref="AssetLoader.ForceEviction"/> return
        /// immediately when tiering is off), so an untiered loader costs one virtual call per pump
        /// and touches nothing. That is what makes it safe to walk *every* loader here rather than
        /// keeping a separate list of the tiered ones — a second list being exactly the kind of
        /// second book L-4 was about.
        ///
        /// One loader throwing must not stop the rest, matching <see cref="InvalidateAll"/>.
        /// </remarks>
        internal static void PumpAll()
        {
            foreach (var loader in Snapshot())
            {
                try
                {
                    loader.EvaluateTiers();
                    loader.ForceEviction();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AssetLoaderRegistry] A loader threw during the periodic " +
                                      $"tier/eviction pump: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// <see cref="Application.lowMemory"/> handler: force eviction only, on every live loader.
        /// No tier evaluation here — under real memory pressure the goal is reclaiming bytes as fast
        /// as possible, not re-scoring access patterns first.
        /// </summary>
        internal static void ForceEvictionAll()
        {
            foreach (var loader in Snapshot())
            {
                try
                {
                    loader.ForceEviction();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AssetLoaderRegistry] A loader threw during the low-memory " +
                                      $"eviction sweep: {ex.Message}");
                }
            }
        }

        /// <summary>Drop collected entries. Caller holds <see cref="Gate"/>.</summary>
        private static void Prune()
        {
            for (int i = Loaders.Count - 1; i >= 0; i--)
            {
                if (!Loaders[i].TryGetTarget(out _)) Loaders.RemoveAt(i);
            }
        }
    }
}
