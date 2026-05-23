using UnityEngine;

namespace AddressableManager.Monitoring
{
    /// <summary>
    /// Helper component to initialize monitoring.
    /// Drop on a scene GameObject to signal that the Dashboard should track this play session.
    /// </summary>
    [AddComponentMenu("Addressable Manager/Monitoring Helper")]
    public class MonitoringHelper : MonoBehaviour
    {
        [Header("Monitoring Settings")]
        [Tooltip("Enable monitoring (required for Dashboard to show data)")]
        [SerializeField] private bool enableMonitoring = true;

        [Tooltip("Log monitoring status to console")]
        [SerializeField] private bool verboseLogging = false;

        /// <summary>Whether monitoring is currently enabled on this helper.</summary>
        public bool EnableMonitoring => enableMonitoring;

        private void Awake()
        {
            if (!enableMonitoring)
            {
                if (verboseLogging) Debug.Log("[MonitoringHelper] Monitoring is DISABLED");
                return;
            }

            // EditorAssetMonitor auto-registers via [InitializeOnLoad]; this component
            // is mostly a discoverable scene-level indicator of intent.
#if UNITY_EDITOR
            if (verboseLogging)
            {
                Debug.Log("[MonitoringHelper] Monitoring is ACTIVE - Dashboard will show real-time data");
            }
#else
            if (verboseLogging)
            {
                Debug.LogWarning("[MonitoringHelper] Monitoring only works in Editor. Dashboard not available in builds.");
            }
#endif
        }
    }
}
