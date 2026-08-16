using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Monitoring
{
    /// <summary>
    /// Bridge between Runtime and Editor for asset monitoring.
    /// Runtime code calls these reporters, Editor code (or tests) registers listeners.
    ///
    /// All public methods are thread-safe — callers from background tasks or
    /// async continuations don't need to marshal back to the main thread to report.
    /// </summary>
    public static class AssetMonitorBridge
    {
        private static readonly object _lock = new object();
        private static IAssetMonitor[] _monitors = System.Array.Empty<IAssetMonitor>();

        // scopeId -> friendly label (BaseAssetScope.DisplayName). Kept here, next to _monitors,
        // rather than added as a new IAssetMonitor member: IAssetMonitor is public and consumers
        // implement it directly (MONITORING_GUIDE.md's "Custom monitors" section), so adding a
        // required method there would be a breaking signature change for every existing
        // implementer (invariant 1). A plain lookup lets Editor code (AssetTrackerService) pull
        // the label on demand instead, with zero impact on IAssetMonitor's contract
        // (HANDOFF_TO_SESSION_B.md E-CHAIN item 4).
        private static readonly Dictionary<string, string> _displayNames = new Dictionary<string, string>();

        /// <summary>
        /// Register a monitor (called by Editor code).
        /// </summary>
        public static void RegisterMonitor(IAssetMonitor monitor)
        {
            if (monitor == null) return;

            lock (_lock)
            {
                foreach (var existing in _monitors)
                {
                    if (existing == monitor) return;
                }

                var next = new IAssetMonitor[_monitors.Length + 1];
                System.Array.Copy(_monitors, next, _monitors.Length);
                next[_monitors.Length] = monitor;
                _monitors = next;
            }
        }

        /// <summary>
        /// Unregister a monitor.
        /// </summary>
        public static void UnregisterMonitor(IAssetMonitor monitor)
        {
            if (monitor == null) return;

            lock (_lock)
            {
                int index = -1;
                for (int i = 0; i < _monitors.Length; i++)
                {
                    if (_monitors[i] == monitor) { index = i; break; }
                }
                if (index < 0) return;

                var next = new IAssetMonitor[_monitors.Length - 1];
                if (index > 0) System.Array.Copy(_monitors, 0, next, 0, index);
                if (index < _monitors.Length - 1) System.Array.Copy(_monitors, index + 1, next, index, _monitors.Length - index - 1);
                _monitors = next;
            }
        }

        public static void ReportAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        {
            var snapshot = _monitors;
            foreach (var monitor in snapshot)
            {
                monitor.OnAssetLoaded(address, typeName, scopeName, loadDuration, fromCache);
            }
        }

        public static void ReportAssetReleased(string address, string typeName)
        {
            var snapshot = _monitors;
            foreach (var monitor in snapshot)
            {
                monitor.OnAssetReleased(address, typeName);
            }
        }

        public static void ReportScopeRegistered(string scopeName, bool isActive)
        {
            var snapshot = _monitors;
            foreach (var monitor in snapshot)
            {
                monitor.OnScopeRegistered(scopeName, isActive);
            }
        }

        /// <summary>
        /// Record the friendly label for a scope id, so Editor code can show it without every
        /// <see cref="IAssetMonitor"/> implementer needing to carry it through the event stream.
        /// Called once, from <see cref="AddressableManager.Scopes.BaseAssetScope"/>'s constructor,
        /// beside the existing <see cref="ReportScopeRegistered"/> call for the same id.
        /// </summary>
        public static void ReportScopeDisplayName(string scopeId, string displayName)
        {
            if (string.IsNullOrEmpty(scopeId)) return;

            lock (_lock)
            {
                _displayNames[scopeId] = displayName;
            }
        }

        /// <summary>
        /// The friendly label registered for <paramref name="scopeId"/> via
        /// <see cref="ReportScopeDisplayName"/>, or <paramref name="scopeId"/> itself if none was
        /// ever recorded (e.g. a scope built before this pairing existed, or an id that was never
        /// a real scope) — never null, per invariant 4: this either answers with a real label or
        /// with the id the caller already had, not a sentinel that could be confused with "no
        /// scope by this id exists".
        /// </summary>
        public static string GetDisplayName(string scopeId)
        {
            if (string.IsNullOrEmpty(scopeId)) return scopeId;

            lock (_lock)
            {
                return _displayNames.TryGetValue(scopeId, out var name) ? name : scopeId;
            }
        }

        public static void ReportScopeStateChanged(string scopeName, bool isActive)
        {
            var snapshot = _monitors;
            foreach (var monitor in snapshot)
            {
                monitor.OnScopeStateChanged(scopeName, isActive);
            }
        }

        public static void ReportScopeCleared(string scopeName)
        {
            var snapshot = _monitors;
            foreach (var monitor in snapshot)
            {
                monitor.OnScopeCleared(scopeName);
            }
        }

        /// <summary>
        /// Clear all monitors (called on domain reload).
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _monitors = System.Array.Empty<IAssetMonitor>();
                _displayNames.Clear();
            }
        }

        // Stale monitor delegates from the previous Editor session would otherwise survive
        // across domain reloads (static field) and fire into disposed Editor objects.
        //
        // This unconditionally empties _monitors on every fresh Play session, including the
        // legitimate registration EditorAssetMonitor's [InitializeOnLoad] constructor just made
        // moments earlier in the same domain load (SubsystemRegistration always fires after
        // InitializeOnLoad). That is intentional, not a bug to "fix" by skipping the clear here:
        // the correct fix is on the registration side, which must re-register *after* this runs
        // rather than assume its earlier registration survives. See
        // EditorAssetMonitor.OnPlayModeStateChanged (Editor/Data/EditorAssetMonitor.cs), which
        // re-registers on PlayModeStateChange.EnteredPlayMode — guaranteed to fire after every
        // RuntimeInitializeOnLoadMethod callback, this one included — for exactly this reason
        // (HANDOFF_TO_SESSION_B.md E-CHAIN item 1).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
