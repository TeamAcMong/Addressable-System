using System;
using UnityEngine;

namespace AddressableManager.Pooling
{
    /// <summary>
    /// The clock behind <see cref="AddressablePoolManager.RunMaintenance"/> (discovery report P-8 and
    /// P-17). One of these is created lazily by a manager the first time it owns something that needs
    /// wall-clock time to make progress, and destroyed with the manager.
    /// </summary>
    /// <remarks>
    /// <para>Two defects need a tick, and neither can be reached from an event:</para>
    ///
    /// <para><b>P-17 (auto-shrink).</b> <c>DynamicPool</c>'s shrink is armed on one
    /// <c>Release</c> and fires on a later one at least <c>ShrinkDelaySeconds</c> afterwards. A pool
    /// that has gone idle — the entire reason shrinking exists — produces no later <c>Release</c>,
    /// so the armed shrink never fired and a spent 40-instance wave stayed resident for the rest of
    /// the session.</para>
    ///
    /// <para><b>P-8 (tracking rot).</b> A borrowed instance destroyed by a scene unload or by
    /// gameplay code calling <c>Object.Destroy</c> instead of <c>Despawn</c> leaves a dead entry in
    /// the manager's <c>_activeInstances</c>/<c>_instanceOwners</c> maps and a phantom active count
    /// in the owning pool. <c>sceneUnloaded</c> catches the common case immediately; this catches the
    /// rest.</para>
    ///
    /// <para>Deliberately <b>not</b> an <c>Update</c> on the existing <c>[Pools]</c> root: that root
    /// only exists when at least one pool used the default parent, and pools created with a
    /// caller-supplied <c>poolRoot</c> need maintenance just as much. It is also deliberately its own
    /// pump rather than a call added to <c>AddressablesFacade</c>'s 5s tiered-cache pump (L-3) —
    /// <c>AddressablePoolManager</c> is constructible and usable without the facade, and a
    /// maintenance loop that only runs for facade users is the inert-feature shape all over again.
    /// If the facade should own both pumps instead, that is noted as an open decision in
    /// <c>Documentation/LIFETIME_DESIGN.md</c>.</para>
    /// </remarks>
    internal sealed class PoolMaintenancePump : MonoBehaviour
    {
        /// <summary>
        /// Seconds between maintenance passes. A pass is a handful of dictionary walks and one
        /// <c>GetStats</c> per pool, so this is cheap; it is intentionally much shorter than
        /// <see cref="DynamicPoolConfig.ShrinkDelaySeconds"/> (30s by default) because the pump is
        /// only the clock — the delay gate inside <c>DynamicPool.CheckForShrinkage</c> is what
        /// decides whether anything is destroyed.
        /// </summary>
        internal const float IntervalSeconds = 1f;

        /// <summary>
        /// The manager this pump drives, held <b>weakly</b>.
        /// </summary>
        /// <remarks>
        /// This is a <c>DontDestroyOnLoad</c> MonoBehaviour, so Unity's scene graph roots it for the
        /// whole session. A strong reference here would make it root its owner too: an
        /// <c>AddressablePoolManager</c> that is never disposed — which this class's own remarks
        /// invite, since a manager is "constructible and usable without the facade" — would be kept
        /// alive for the process, along with every template handle, pool and tracking map it holds,
        /// and would keep having <see cref="AddressablePoolManager.RunMaintenance"/> called on it
        /// once a second forever. Weak here means an abandoned manager is collectible, and this pump
        /// destroys itself the moment it notices the manager has gone.
        /// </remarks>
        private WeakReference<AddressablePoolManager> _owner;
        private float _elapsed;

        internal static PoolMaintenancePump Create(AddressablePoolManager owner)
        {
            var go = new GameObject("[PoolMaintenance]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;

            var pump = go.AddComponent<PoolMaintenancePump>();
            pump._owner = new WeakReference<AddressablePoolManager>(owner);
            return pump;
        }

        internal void Shutdown()
        {
            _owner = null;
            if (this != null) Destroy(gameObject);
        }

        private void Update()
        {
            if (_owner == null) return;

            if (!_owner.TryGetTarget(out var owner))
            {
                // Manager collected without Dispose. Nothing left to maintain, and nothing else will
                // ever clean this GameObject up.
                Shutdown();
                return;
            }

            // unscaledDeltaTime, matching AddressablesFacade's own pump: a paused game
            // (Time.timeScale == 0) still holds memory, and maintenance is not gameplay.
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < IntervalSeconds) return;

            _elapsed = 0f;
            owner.RunMaintenance();
        }
    }
}
