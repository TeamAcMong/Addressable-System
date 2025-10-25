using System.Collections.Generic;

namespace AddressableManager.Monitoring
{
    /// <summary>
    /// Bridge between Runtime and Editor for asset monitoring
    /// Runtime code calls this, Editor registers listeners
    /// </summary>
    public static class AssetMonitorBridge
    {
        private static readonly List<IAssetMonitor> _monitors = new List<IAssetMonitor>();

        /// <summary>
        /// Register a monitor (called by Editor code)
        /// </summary>
        public static void RegisterMonitor(IAssetMonitor monitor)
        {
            if (monitor != null && !_monitors.Contains(monitor))
            {
                _monitors.Add(monitor);
            }
        }

        /// <summary>
        /// Unregister a monitor
        /// </summary>
        public static void UnregisterMonitor(IAssetMonitor monitor)
        {
            _monitors.Remove(monitor);
        }

        /// <summary>
        /// Report asset load (called by Runtime code)
        /// </summary>
        public static void ReportAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        {
            foreach (var monitor in _monitors)
            {
                monitor.OnAssetLoaded(address, typeName, scopeName, loadDuration, fromCache);
            }
        }

        /// <summary>
        /// Report asset release (called by Runtime code)
        /// </summary>
        public static void ReportAssetReleased(string address, string typeName)
        {
            foreach (var monitor in _monitors)
            {
                monitor.OnAssetReleased(address, typeName);
            }
        }

        /// <summary>
        /// Report scope registration (called by Runtime code)
        /// </summary>
        public static void ReportScopeRegistered(string scopeName, bool isActive)
        {
            foreach (var monitor in _monitors)
            {
                monitor.OnScopeRegistered(scopeName, isActive);
            }
        }

        /// <summary>
        /// Report scope state change (called by Runtime code)
        /// </summary>
        public static void ReportScopeStateChanged(string scopeName, bool isActive)
        {
            foreach (var monitor in _monitors)
            {
                monitor.OnScopeStateChanged(scopeName, isActive);
            }
        }

        /// <summary>
        /// Report scope cleared (called by Runtime code)
        /// </summary>
        public static void ReportScopeCleared(string scopeName)
        {
            foreach (var monitor in _monitors)
            {
                monitor.OnScopeCleared(scopeName);
            }
        }

        /// <summary>
        /// Clear all monitors (called on domain reload)
        /// </summary>
        public static void Clear()
        {
            _monitors.Clear();
        }
    }
}
