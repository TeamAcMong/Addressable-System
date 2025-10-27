using UnityEngine;

namespace AddressableManager.Monitoring
{
    /// <summary>
    /// Helper component to initialize monitoring
    /// Add this to a GameObject in your scene to enable Dashboard monitoring
    /// </summary>
    [AddComponentMenu("Addressable Manager/Monitoring Helper")]
    public class MonitoringHelper : MonoBehaviour
    {
        [Header("Monitoring Settings")]
        [Tooltip("Enable monitoring (required for Dashboard to show data)")]
        [SerializeField] private bool enableMonitoring = true;

        [Tooltip("Log monitoring status to console")]
        [SerializeField] private bool verboseLogging = false;

        private void Awake()
        {
            if (enableMonitoring)
            {
                // In Editor, the EditorAssetMonitor will auto-register via [InitializeOnLoad]
                // This component just serves as a visual indicator that monitoring is active

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
            else
            {
                if (verboseLogging)
                {
                    Debug.Log("[MonitoringHelper] Monitoring is DISABLED");
                }
            }
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            // Update helpbox based on settings
            if (enableMonitoring)
            {
                // Green light - monitoring active
            }
            else
            {
                // Yellow warning - monitoring disabled
            }
#endif
        }

#if UNITY_EDITOR
        [UnityEditor.CustomEditor(typeof(MonitoringHelper))]
        private class MonitoringHelperEditor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                var helper = (MonitoringHelper)target;

                UnityEditor.EditorGUILayout.HelpBox(
                    "This component enables real-time monitoring for the Addressable Manager Dashboard.\n\n" +
                    "When enabled, the Dashboard (Window → Addressable Manager → Dashboard) will show:\n" +
                    "• All loaded assets\n" +
                    "• Memory usage per scope\n" +
                    "• Load times and performance metrics\n" +
                    "• Reference counts\n\n" +
                    "Monitoring only works in Editor Play Mode.",
                    helper.enableMonitoring ? UnityEditor.MessageType.Info : UnityEditor.MessageType.Warning
                );

                DrawDefaultInspector();

                if (Application.isPlaying && helper.enableMonitoring)
                {
                    UnityEditor.EditorGUILayout.Space();
                    if (GUILayout.Button("Open Dashboard", GUILayout.Height(30)))
                    {
                        UnityEditor.EditorApplication.ExecuteMenuItem("Window/Addressable Manager/Dashboard");
                    }
                }
            }
        }
#endif
    }
}
