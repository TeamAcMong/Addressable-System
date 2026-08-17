using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Every live <see cref="TieredAssetLoader"/>, so a periodic pump and a low-memory handler can
    /// reach all of them.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS (HANDOFF_TO_SESSION_B.md L-3)
    ///
    /// Before this, nothing in the package ever called <see cref="TieredAssetLoader.EvaluateTiers"/>
    /// or <see cref="TieredAssetLoader.ForceEviction"/> except two <c>Advanced.*</c> entry points
    /// with zero call sites anywhere in the repo. The only eviction that ever ran was the inline
    /// trigger inside <see cref="Core.TieredCache{T}.Set"/> — which only fires while a cache is
    /// still actively receiving new entries. A gameplay phase that loads a burst of assets and then
    /// goes quiet (a level finished streaming, a menu the player is idling on, the middle of a boss
    /// fight) is exactly when a Cold tier should be reclaimed, and exactly when nothing was running
    /// to do it.
    ///
    /// This registry is what <c>AddressablesFacade</c>'s periodic pump and
    /// <see cref="Application.lowMemory"/> handler walk. Mirrors
    /// <see cref="AssetLoaderRegistry"/>'s shape for the same reasons that type documents: loaders
    /// are constructed in more than one place (<c>Advanced.CreateTieredLoader</c> hands one straight
    /// to the caller with no other owner), so a registry is the only way to reach them all, and it
    /// must not be the thing keeping them alive.
    ///
    /// WEAK REFERENCES, NOT STRONG
    ///
    /// A registry of live objects that keeps them alive is a leak with a nice name — same rule as
    /// <see cref="AssetLoaderRegistry"/>. Entries are weak and pruned lazily as the list is walked.
    /// </remarks>
    internal static class TieredAssetLoaderRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<WeakReference<TieredAssetLoader>> Loaders =
            new List<WeakReference<TieredAssetLoader>>();

        /// <summary>Record a loader. Called from the constructor.</summary>
        internal static void Register(TieredAssetLoader loader)
        {
            if (loader == null) return;

            lock (Gate)
            {
                Loaders.Add(new WeakReference<TieredAssetLoader>(loader));
            }
        }

        /// <summary>Forget a loader. Called from <see cref="TieredAssetLoader.Dispose"/>.</summary>
        internal static void Unregister(TieredAssetLoader loader)
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

        private static TieredAssetLoader[] Snapshot()
        {
            lock (Gate)
            {
                Prune();

                var live = new List<TieredAssetLoader>(Loaders.Count);
                foreach (var reference in Loaders)
                {
                    if (reference.TryGetTarget(out var loader) && loader != null) live.Add(loader);
                }

                return live.ToArray();
            }
        }

        /// <summary>
        /// Periodic drain: evaluate tiers, then evict, on every live loader. A no-op call on a
        /// settled cache costs one dictionary walk per type — cheap enough to run on an interval
        /// gate rather than needing its own scheduling story.
        /// </summary>
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
                    Debug.LogWarning($"[TieredAssetLoaderRegistry] A loader threw during the periodic " +
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
                    Debug.LogWarning($"[TieredAssetLoaderRegistry] A loader threw during the low-memory " +
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
