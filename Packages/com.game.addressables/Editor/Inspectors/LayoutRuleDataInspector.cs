using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;
using AddressableManager.Editor.Windows;
using System.Collections.Generic;

namespace AddressableManager.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for LayoutRuleData with quick actions and validation
    /// </summary>
    [CustomEditor(typeof(LayoutRuleData))]
    public class LayoutRuleDataInspector : UnityEditor.Editor
    {
        private LayoutRuleData _target;
        private bool _showAddressRules = true;
        private bool _showLabelRules = true;
        private bool _showVersionRules = true;
        private bool _showSettings = true;
        private List<RuleValidator.ValidationMessage> _validationMessages;
        private Vector2 _validationScrollPos;

        private GUIStyle _headerStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _warningStyle;
        private GUIStyle _infoStyle;

        private void OnEnable()
        {
            _target = (LayoutRuleData)target;
            ValidateRules();
        }

        public override void OnInspectorGUI()
        {
            InitializeStyles();

            serializedObject.Update();

            DrawHeader();
            DrawQuickActions();
            DrawValidationSection();

            EditorGUILayout.Space(10);

            DrawRuleSections();
            DrawSettingsSection();

            serializedObject.ApplyModifiedProperties();

            if (GUI.changed)
            {
                EditorUtility.SetDirty(_target);
                ValidateRules();
            }
        }

        private void InitializeStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleLeft
                };
            }

            if (_errorStyle == null)
            {
                _errorStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    normal = { textColor = new Color(1f, 0.3f, 0.3f) }
                };
            }

            if (_warningStyle == null)
            {
                _warningStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    normal = { textColor = new Color(1f, 0.8f, 0.2f) }
                };
            }

            if (_infoStyle == null)
            {
                _infoStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
                };
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Layout Rule Data", _headerStyle);

            // Description
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_description"), new GUIContent("Description"));

            // Stats
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Total Rules: {_target.TotalRuleCount}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Address: {_target.AddressRules.Count}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Label: {_target.LabelRules.Count}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Version: {_target.VersionRules.Count}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
        }

        private void DrawQuickActions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            // Apply Rules button
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Apply All Rules", GUILayout.Height(30)))
            {
                ApplyAllRules();
            }
            GUI.backgroundColor = Color.white;

            // Open Editor button
            if (GUILayout.Button("Open Rule Editor", GUILayout.Height(30)))
            {
                OpenRuleEditor();
            }

            // Open Viewer button
            if (GUILayout.Button("Open Viewer", GUILayout.Height(30)))
            {
                OpenLayoutViewer();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // Validate button
            if (GUILayout.Button("Validate Rules"))
            {
                ValidateRules();
            }

            // Detect Conflicts button
            if (GUILayout.Button("Detect Conflicts"))
            {
                DetectConflicts();
            }

            // Clear All button
            GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
            if (GUILayout.Button("Clear All Rules"))
            {
                if (EditorUtility.DisplayDialog("Clear All Rules",
                    "Are you sure you want to remove all rules? This cannot be undone.",
                    "Yes, Clear", "Cancel"))
                {
                    _target.ClearAllRules();
                    ValidateRules();
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawValidationSection()
        {
            if (_validationMessages == null || _validationMessages.Count == 0)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("✓ No validation issues", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Validation Results ({_validationMessages.Count} issues)", EditorStyles.boldLabel);

            _validationScrollPos = EditorGUILayout.BeginScrollView(_validationScrollPos, GUILayout.MaxHeight(200));

            foreach (var msg in _validationMessages)
            {
                GUIStyle style = msg.Severity == RuleValidator.ValidationSeverity.Error ? _errorStyle :
                                msg.Severity == RuleValidator.ValidationSeverity.Warning ? _warningStyle : _infoStyle;

                EditorGUILayout.BeginVertical(style);

                string icon = msg.Severity == RuleValidator.ValidationSeverity.Error ? "✖" :
                             msg.Severity == RuleValidator.ValidationSeverity.Warning ? "⚠" : "ℹ";

                EditorGUILayout.LabelField($"{icon} {msg.Message}", EditorStyles.wordWrappedLabel);

                if (!string.IsNullOrEmpty(msg.RuleName))
                {
                    EditorGUILayout.LabelField($"Rule: {msg.RuleName}", EditorStyles.miniLabel);
                }

                if (!string.IsNullOrEmpty(msg.Suggestion))
                {
                    EditorGUILayout.LabelField($"💡 {msg.Suggestion}", EditorStyles.miniLabel);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawRuleSections()
        {
            // Address Rules
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _showAddressRules = EditorGUILayout.Foldout(_showAddressRules, $"Address Rules ({_target.AddressRules.Count})", true, EditorStyles.foldoutHeader);
            if (_showAddressRules)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_addressRules"), new GUIContent("Rules"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // Label Rules
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _showLabelRules = EditorGUILayout.Foldout(_showLabelRules, $"Label Rules ({_target.LabelRules.Count})", true, EditorStyles.foldoutHeader);
            if (_showLabelRules)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_labelRules"), new GUIContent("Rules"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // Version Rules
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _showVersionRules = EditorGUILayout.Foldout(_showVersionRules, $"Version Rules ({_target.VersionRules.Count})", true, EditorStyles.foldoutHeader);
            if (_showVersionRules)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_versionRules"), new GUIContent("Rules"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawSettingsSection()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Settings", true, EditorStyles.foldoutHeader);
            if (_showSettings)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_autoApplyOnImport"), new GUIContent("Auto-Apply on Import"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_autoApplyOnModified"), new GUIContent("Auto-Apply on Modified"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_verboseLogging"), new GUIContent("Verbose Logging"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        private void ValidateRules()
        {
            _validationMessages = RuleValidator.Validate(_target);
        }

        private void ApplyAllRules()
        {
            if (EditorUtility.DisplayDialog("Apply All Rules",
                "This will apply all rules to matching assets in the project. Continue?",
                "Yes, Apply", "Cancel"))
            {
                try
                {
                    var processor = new LayoutRuleProcessor(_target);

                    EditorUtility.DisplayProgressBar("Applying Rules", "Starting...", 0);

                    var result = processor.ApplyRules((progress, message) =>
                    {
                        EditorUtility.DisplayProgressBar("Applying Rules", message, progress);
                    });

                    EditorUtility.ClearProgressBar();

                    // Show results
                    string resultMessage = $"Rules applied successfully!\n\n" +
                        $"Processed: {result.TotalAssetsProcessed} assets\n" +
                        $"Addresses: {result.AddressesApplied}\n" +
                        $"Labels: {result.LabelsApplied}\n" +
                        $"Versions: {result.VersionsApplied}";

                    if (result.Warnings.Count > 0)
                    {
                        resultMessage += $"\n\nWarnings: {result.Warnings.Count}";
                    }

                    if (result.Errors.Count > 0)
                    {
                        resultMessage += $"\n\nErrors: {result.Errors.Count}";
                        foreach (var error in result.Errors)
                        {
                            Debug.LogError($"[LayoutRuleData] {error}");
                        }
                    }

                    EditorUtility.DisplayDialog("Apply Rules Complete", resultMessage, "OK");
                }
                catch (System.Exception ex)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", $"Failed to apply rules:\n{ex.Message}", "OK");
                    Debug.LogException(ex);
                }
            }
        }

        private void DetectConflicts()
        {
            EditorUtility.DisplayProgressBar("Detecting Conflicts", "Analyzing...", 0.5f);

            var conflicts = RuleConflictDetector.DetectConflicts();

            EditorUtility.ClearProgressBar();

            if (conflicts.Count == 0)
            {
                EditorUtility.DisplayDialog("Conflict Detection", "✓ No conflicts found!", "OK");
            }
            else
            {
                string message = $"Found {conflicts.Count} conflict(s):\n\n";

                int maxShow = 5;
                for (int i = 0; i < Mathf.Min(conflicts.Count, maxShow); i++)
                {
                    var conflict = conflicts[i];
                    message += $"• {conflict.Type}: {conflict.Message}\n";
                }

                if (conflicts.Count > maxShow)
                {
                    message += $"\n... and {conflicts.Count - maxShow} more.\n";
                }

                message += "\nCheck console for full details.";

                foreach (var conflict in conflicts)
                {
                    Debug.LogWarning($"[Conflict] {conflict.Type}: {conflict.Message}\nAffected: {string.Join(", ", conflict.AffectedAssets)}");
                }

                EditorUtility.DisplayDialog("Conflicts Detected", message, "OK");
            }
        }

        private void OpenRuleEditor()
        {
            LayoutRuleEditorWindow.ShowWindow();
        }

        private void OpenLayoutViewer()
        {
            LayoutViewerWindow.ShowWindow();
        }
    }
}
