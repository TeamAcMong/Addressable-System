using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;
using System.Collections.Generic;

namespace AddressableManager.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for CompositeLayoutRuleData
    /// </summary>
    [CustomEditor(typeof(CompositeLayoutRuleData))]
    public class CompositeLayoutRuleDataInspector : UnityEditor.Editor
    {
        private CompositeLayoutRuleData _target;
        private GUIStyle _headerStyle;
        private bool _showPreview = false;

        private void OnEnable()
        {
            _target = (CompositeLayoutRuleData)target;
        }

        public override void OnInspectorGUI()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14
                };
            }

            serializedObject.Update();

            DrawHeader();
            DrawDefaultInspector();
            DrawActions();
            DrawPreview();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Composite Layout Rule Data", _headerStyle);

            // Stats
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Sources: {_target.SourceRuleData.Count}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Total Rules: {_target.GetTotalRuleCount()}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // Apply Rules button
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Apply Combined Rules", GUILayout.Height(30)))
            {
                ApplyCombinedRules();
            }
            GUI.backgroundColor = Color.white;

            // Validate button
            if (GUILayout.Button("Validate All", GUILayout.Height(30)))
            {
                ValidateAll();
            }

            EditorGUILayout.EndHorizontal();

            // Preview toggle
            _showPreview = EditorGUILayout.Foldout(_showPreview, "Preview Combined Rules", true);

            EditorGUILayout.EndVertical();
        }

        private void DrawPreview()
        {
            if (!_showPreview) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Combined Rules Preview", EditorStyles.boldLabel);

            var addressRules = _target.GetCombinedAddressRules();
            var labelRules = _target.GetCombinedLabelRules();
            var versionRules = _target.GetCombinedVersionRules();

            EditorGUILayout.LabelField($"Address Rules: {addressRules.Count}");
            EditorGUI.indentLevel++;
            foreach (var rule in addressRules)
            {
                EditorGUILayout.LabelField($"• {rule.RuleName} (Priority: {rule.Priority})", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField($"Label Rules: {labelRules.Count}");
            EditorGUI.indentLevel++;
            foreach (var rule in labelRules)
            {
                EditorGUILayout.LabelField($"• {rule.RuleName} (Priority: {rule.Priority})", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField($"Version Rules: {versionRules.Count}");
            EditorGUI.indentLevel++;
            foreach (var rule in versionRules)
            {
                EditorGUILayout.LabelField($"• {rule.RuleName} (Priority: {rule.Priority})", EditorStyles.miniLabel);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.EndVertical();
        }

        private void ApplyCombinedRules()
        {
            if (EditorUtility.DisplayDialog("Apply Combined Rules",
                "This will apply all rules from all source LayoutRuleData assets. Continue?",
                "Yes, Apply", "Cancel"))
            {
                try
                {
                    var mergedRuleData = _target.CreateMergedRuleData();
                    var processor = new LayoutRuleProcessor(mergedRuleData);

                    EditorUtility.DisplayProgressBar("Applying Rules", "Starting...", 0);

                    var result = processor.ApplyRules((progress, message) =>
                    {
                        EditorUtility.DisplayProgressBar("Applying Combined Rules", message, progress);
                    });

                    EditorUtility.ClearProgressBar();

                    // Cleanup temporary merged data
                    DestroyImmediate(mergedRuleData);

                    string resultMessage = $"Combined rules applied successfully!\n\n" +
                        $"Processed: {result.TotalAssetsProcessed} assets\n" +
                        $"Addresses: {result.AddressesApplied}\n" +
                        $"Labels: {result.LabelsApplied}\n" +
                        $"Versions: {result.VersionsApplied}";

                    if (result.Errors.Count > 0)
                    {
                        resultMessage += $"\n\nErrors: {result.Errors.Count} (see console)";
                        foreach (var error in result.Errors)
                        {
                            Debug.LogError($"[CompositeLayoutRuleData] {error}");
                        }
                    }

                    EditorUtility.DisplayDialog("Apply Complete", resultMessage, "OK");
                }
                catch (System.Exception ex)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", $"Failed to apply rules:\n{ex.Message}", "OK");
                    Debug.LogException(ex);
                }
            }
        }

        private void ValidateAll()
        {
            var (isValid, errors) = _target.Validate();

            if (isValid)
            {
                EditorUtility.DisplayDialog("Validation", "✓ All source LayoutRuleData assets are valid!", "OK");
            }
            else
            {
                string message = $"Validation failed with {errors.Count} error(s):\n\n";
                foreach (var error in errors)
                {
                    message += $"• {error}\n";
                }

                Debug.LogError($"[CompositeLayoutRuleData] Validation failed:\n{message}");
                EditorUtility.DisplayDialog("Validation Failed", message, "OK");
            }
        }
    }
}
