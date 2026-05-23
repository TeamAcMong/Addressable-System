using UnityEditor;
using UnityEngine;
using AddressableManager.Monitoring;

namespace AddressableManager.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for <see cref="MonitoringHelper"/>. Lives in the editor assembly so
    /// the runtime DLL does not have to ship a CustomEditor type.
    /// </summary>
    [CustomEditor(typeof(MonitoringHelper))]
    internal class MonitoringHelperInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var helper = (MonitoringHelper)target;

            EditorGUILayout.HelpBox(
                "This component enables real-time monitoring for the Addressable Manager Dashboard.\n\n" +
                "When enabled, the Dashboard (Window → Addressable Manager → Dashboard) will show:\n" +
                "• All loaded assets\n" +
                "• Memory usage per scope\n" +
                "• Load times and performance metrics\n" +
                "• Reference counts\n\n" +
                "Monitoring only works in Editor Play Mode.",
                helper.EnableMonitoring ? MessageType.Info : MessageType.Warning
            );

            DrawDefaultInspector();

            if (Application.isPlaying && helper.EnableMonitoring)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Open Dashboard", GUILayout.Height(30)))
                {
                    EditorApplication.ExecuteMenuItem("Window/Addressable Manager/Dashboard");
                }
            }
        }
    }
}
