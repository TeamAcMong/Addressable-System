using UnityEditor;
using AddressableManager.Monitoring;

namespace AddressableManager.Editor.Data
{
    /// <summary>
    /// Editor implementation of IAssetMonitor
    /// Bridges Runtime events to AssetTrackerService
    /// </summary>
    [InitializeOnLoad]
    public class EditorAssetMonitor : IAssetMonitor
    {
        private readonly AssetTrackerService _tracker;

        static EditorAssetMonitor()
        {
            // Auto-register on Editor startup
            var monitor = new EditorAssetMonitor();
            AssetMonitorBridge.RegisterMonitor(monitor);

            // Clear on domain reload
            EditorApplication.playModeStateChanged += (state) =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    AssetMonitorBridge.Clear();
                    AssetMonitorBridge.RegisterMonitor(monitor);
                }
            };
        }

        public EditorAssetMonitor()
        {
            _tracker = AssetTrackerService.Instance;
        }

        public void OnAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        {
            _tracker.RegisterAssetLoad(address, typeName, scopeName, loadDuration, fromCache);

            // Also record in performance metrics
            PerformanceMetrics.Instance.RecordLoadTime(address, typeName, loadDuration, fromCache);
        }

        public void OnAssetReleased(string address, string typeName)
        {
            _tracker.RegisterAssetRelease(address, typeName);
        }

        public void OnScopeRegistered(string scopeName, bool isActive)
        {
            _tracker.RegisterScope(scopeName, isActive);
        }

        public void OnScopeStateChanged(string scopeName, bool isActive)
        {
            _tracker.UpdateScopeState(scopeName, isActive);
        }

        public void OnScopeCleared(string scopeName)
        {
            _tracker.ClearScope(scopeName);
        }
    }
}
