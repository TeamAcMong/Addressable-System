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
            }
        }

        // Stale monitor delegates from the previous Editor session would otherwise survive
        // across domain reloads (static field) and fire into disposed Editor objects.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
