using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Windows
{
    /// <summary>
    /// Window for viewing and validating addressable layout
    /// Shows all addressable groups, addresses, labels, and conflicts
    /// </summary>
    public class LayoutViewerWindow : EditorWindow
    {
        private AddressableAssetSettings _settings;
        private Vector2 _leftScrollPos;
        private Vector2 _rightScrollPos;

        private AddressableAssetGroup _selectedGroup;
        private List<RuleConflictDetector.Conflict> _conflicts;
        private string _searchFilter = "";

        private GUIStyle _headerStyle;
        private GUIStyle _groupStyle;
        private GUIStyle _errorStyle;
        private GUIStyle _warningStyle;
        private bool _stylesInitialized;

        private bool _showOnlyConflicts = false;
        private bool _autoRefresh = true;
        private double _lastRefreshTime;

        [MenuItem("Window/Addressable Manager/Layout Viewer", priority = 202)]
        public static void ShowWindow()
        {
            var window = GetWindow<LayoutViewerWindow>();
            window.titleContent = new GUIContent("Layout Viewer");
            window.minSize = new Vector2(900, 500);
            window.Show();
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            RefreshConflicts();
        }

        private void Update()
        {
            if (_autoRefresh && EditorApplication.timeSinceStartup - _lastRefreshTime > 2.0)
            {
                RefreshConflicts();
                _lastRefreshTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnGUI()
        {
            InitializeStyles();

            if (_settings == null)
            {
                EditorGUILayout.HelpBox("Addressable Asset Settings not found. Please initialize Addressables first.", MessageType.Error);
                return;
            }

            DrawToolbar();
            DrawSummary();

            EditorGUILayout.BeginHorizontal();

            // Left panel: Groups
            DrawGroupPanel();

            // Right panel: Validation/Conflicts
            DrawValidationPanel();

            EditorGUILayout.EndHorizontal();
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            _groupStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontSize = 11
            };

            _errorStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal = { textColor = new Color(1f, 0.3f, 0.3f) }
            };

            _warningStyle = new GUIStyle(EditorStyles.helpBox)
            {
                normal = { textColor = new Color(1f, 0.8f, 0.2f) }
            };

            _stylesInitialized = true;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                RefreshConflicts();
            }

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh", EditorStyles.toolbarButton, GUILayout.Width(100));

            GUILayout.Space(10);

            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(200));

            GUILayout.FlexibleSpace();

            _showOnlyConflicts = GUILayout.Toggle(_showOnlyConflicts, "Conflicts Only", EditorStyles.toolbarButton, GUILayout.Width(100));

            if (GUILayout.Button("Export Report", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                ExportReport();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSummary()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField("Summary", _headerStyle);

            EditorGUILayout.BeginHorizontal();

            // Total stats
            int totalGroups = _settings.groups.Count;
            int totalEntries = _settings.groups.Sum(g => g?.entries?.Count ?? 0);
            int totalLabels = _settings.GetLabels().Count;

            EditorGUILayout.LabelField($"Groups: {totalGroups}", GUILayout.Width(100));
            EditorGUILayout.LabelField($"Entries: {totalEntries}", GUILayout.Width(100));
            EditorGUILayout.LabelField($"Labels: {totalLabels}", GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            // Conflict stats
            if (_conflicts != null)
            {
                int errors = _conflicts.Count(c => c.Type == RuleConflictDetector.ConflictType.DuplicateAddress ||
                                                     c.Type == RuleConflictDetector.ConflictType.EmptyAddress);
                int warnings = _conflicts.Count - errors;

                if (errors > 0)
                {
                    GUI.color = new Color(1f, 0.3f, 0.3f);
                    EditorGUILayout.LabelField($"✖ {errors} Error(s)", GUILayout.Width(100));
                    GUI.color = Color.white;
                }

                if (warnings > 0)
                {
                    GUI.color = new Color(1f, 0.8f, 0.2f);
                    EditorGUILayout.LabelField($"⚠ {warnings} Warning(s)", GUILayout.Width(120));
                    GUI.color = Color.white;
                }

                if (errors == 0 && warnings == 0)
                {
                    GUI.color = new Color(0.3f, 0.8f, 0.3f);
                    EditorGUILayout.LabelField("✓ No Issues", GUILayout.Width(100));
                    GUI.color = Color.white;
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawGroupPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(450));

            EditorGUILayout.LabelField("Addressable Groups", _headerStyle);

            _leftScrollPos = EditorGUILayout.BeginScrollView(_leftScrollPos, GUI.skin.box);

            foreach (var group in _settings.groups)
            {
                if (group == null) continue;

                DrawGroup(group);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawGroup(AddressableAssetGroup group)
        {
            bool hasConflicts = _conflicts != null && _conflicts.Any(c => c.AffectedAssets.Any(path =>
            {
                var guid = AssetDatabase.AssetPathToGUID(path);
                return group.entries.Any(e => e.guid == guid);
            }));

            if (_showOnlyConflicts && !hasConflicts)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Group header
            EditorGUILayout.BeginHorizontal();

            string groupLabel = $"{group.Name} ({group.entries.Count} entries)";
            if (hasConflicts)
            {
                GUI.color = new Color(1f, 0.5f, 0.5f);
                groupLabel += " ⚠";
            }

            bool isExpanded = EditorGUILayout.Foldout(_selectedGroup == group, groupLabel, true, _groupStyle);

            GUI.color = Color.white;

            EditorGUILayout.EndHorizontal();

            if (isExpanded)
            {
                _selectedGroup = group;

                EditorGUI.indentLevel++;

                // Show entries
                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;

                    // Filter by search
                    if (!string.IsNullOrEmpty(_searchFilter) &&
                        !entry.address.ToLower().Contains(_searchFilter.ToLower()))
                    {
                        continue;
                    }

                    DrawEntry(entry);
                }

                EditorGUI.indentLevel--;
            }
            else if (_selectedGroup == group)
            {
                _selectedGroup = null;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        private void DrawEntry(AddressableAssetEntry entry)
        {
            EditorGUILayout.BeginHorizontal();

            // Address
            EditorGUILayout.LabelField(entry.address, GUILayout.MinWidth(200));

            // Labels
            if (entry.labels.Count > 0)
            {
                EditorGUILayout.LabelField($"[{string.Join(", ", entry.labels)}]", EditorStyles.miniLabel, GUILayout.Width(150));
            }

            // Ping button
            if (GUILayout.Button("→", GUILayout.Width(25)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(entry.guid));
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawValidationPanel()
        {
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.MinWidth(400));

            EditorGUILayout.LabelField("Validation & Conflicts", _headerStyle);

            if (_conflicts == null || _conflicts.Count == 0)
            {
                EditorGUILayout.HelpBox("✓ No conflicts detected!", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            _rightScrollPos = EditorGUILayout.BeginScrollView(_rightScrollPos);

            // Group conflicts by type
            var grouped = _conflicts.GroupBy(c => c.Type);

            foreach (var group in grouped)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.LabelField($"{group.Key} ({group.Count()})", EditorStyles.boldLabel);

                foreach (var conflict in group)
                {
                    DrawConflict(conflict);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawConflict(RuleConflictDetector.Conflict conflict)
        {
            var style = conflict.Type == RuleConflictDetector.ConflictType.DuplicateAddress ||
                        conflict.Type == RuleConflictDetector.ConflictType.EmptyAddress
                        ? _errorStyle : _warningStyle;

            EditorGUILayout.BeginVertical(style);

            EditorGUILayout.LabelField(conflict.Message, EditorStyles.wordWrappedLabel);

            if (conflict.AffectedAssets.Count > 0)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Affected Assets ({conflict.AffectedAssets.Count}):", EditorStyles.miniLabel);

                int maxShow = 5;
                for (int i = 0; i < Mathf.Min(conflict.AffectedAssets.Count, maxShow); i++)
                {
                    EditorGUILayout.BeginHorizontal();

                    EditorGUILayout.LabelField($"• {conflict.AffectedAssets[i]}", EditorStyles.miniLabel);

                    if (GUILayout.Button("→", GUILayout.Width(25)))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<Object>(conflict.AffectedAssets[i]);
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                if (conflict.AffectedAssets.Count > maxShow)
                {
                    EditorGUILayout.LabelField($"... and {conflict.AffectedAssets.Count - maxShow} more", EditorStyles.miniLabel);
                }

                EditorGUI.indentLevel--;
            }

            if (!string.IsNullOrEmpty(conflict.Suggestion))
            {
                EditorGUILayout.LabelField($"💡 {conflict.Suggestion}", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(3);
        }

        private void RefreshConflicts()
        {
            if (_settings == null) return;

            _conflicts = RuleConflictDetector.DetectConflicts(_settings);
            _lastRefreshTime = EditorApplication.timeSinceStartup;
        }

        private void ExportReport()
        {
            var path = EditorUtility.SaveFilePanel("Export Validation Report", "", "addressable_validation_report.csv", "csv");

            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("Type,Message,Affected Assets,Suggestion");

                foreach (var conflict in _conflicts)
                {
                    csv.AppendLine($"\"{conflict.Type}\",\"{conflict.Message}\",\"{string.Join("; ", conflict.AffectedAssets)}\",\"{conflict.Suggestion}\"");
                }

                System.IO.File.WriteAllText(path, csv.ToString());

                EditorUtility.DisplayDialog("Export Complete", $"Report exported to:\n{path}", "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Export Failed", $"Failed to export report:\n{ex.Message}", "OK");
            }
        }
    }
}
