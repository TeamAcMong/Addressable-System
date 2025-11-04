using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;

namespace AddressableManager.Editor.Windows
{
    /// <summary>
    /// Editor window for creating and managing layout rules
    /// </summary>
    public class LayoutRuleEditorWindow : EditorWindow
    {
        private LayoutRuleData _selectedRuleData;
        private Vector2 _leftScrollPos;
        private Vector2 _centerScrollPos;
        private Vector2 _rightScrollPos;

        private int _selectedRuleIndex = -1;
        private RuleType _selectedRuleType = RuleType.Address;

        private enum RuleType
        {
            Address,
            Label,
            Version
        }

        private GUIStyle _headerStyle;
        private GUIStyle _selectedItemStyle;
        private GUIStyle _normalItemStyle;
        private bool _stylesInitialized;

        // Preview data
        private List<PreviewItem> _previewItems = new List<PreviewItem>();
        private bool _previewNeedsRefresh = true;
        private int _previewLimit = 50;
        private int _totalMatchCount = 0;
        private bool _isGeneratingPreview = false;

        private class PreviewItem
        {
            public string AssetPath;
            public string GeneratedValue; // Address, Label, or Version
            public bool IsValid;
            public string ErrorMessage;
        }

        [MenuItem("Window/Addressable Manager/Layout Rule Editor", priority = 201)]
        public static void ShowWindow()
        {
            var window = GetWindow<LayoutRuleEditorWindow>();
            window.titleContent = new GUIContent("Layout Rule Editor");
            window.minSize = new Vector2(1000, 600);
            window.Show();
        }

        private void OnEnable()
        {
            // Try to find a LayoutRuleData in the project
            if (_selectedRuleData == null)
            {
                var guids = AssetDatabase.FindAssets("t:LayoutRuleData");
                if (guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _selectedRuleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(path);
                }
            }
        }

        private void OnGUI()
        {
            InitializeStyles();

            DrawToolbar();

            if (_selectedRuleData == null)
            {
                DrawNoRuleDataSelected();
                return;
            }

            EditorGUILayout.BeginHorizontal();

            // Left panel: Rule list
            DrawRuleListPanel();

            // Center panel: Rule configuration
            DrawRuleConfigPanel();

            // Right panel: Preview
            DrawPreviewPanel();

            EditorGUILayout.EndHorizontal();
        }

        private void InitializeStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft
            };

            _selectedItemStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(2, 2, new Color(0.3f, 0.5f, 0.8f, 0.3f)) }
            };

            _normalItemStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(2, 2, new Color(0.2f, 0.2f, 0.2f, 0.1f)) }
            };

            _stylesInitialized = true;
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;

            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Rule Data selector
            EditorGUILayout.LabelField("Rule Data:", GUILayout.Width(70));
            var newRuleData = (LayoutRuleData)EditorGUILayout.ObjectField(_selectedRuleData, typeof(LayoutRuleData), false, GUILayout.Width(200));
            if (newRuleData != _selectedRuleData)
            {
                _selectedRuleData = newRuleData;
                _selectedRuleIndex = -1;
            }

            GUILayout.FlexibleSpace();

            // Quick actions
            if (GUILayout.Button("Validate", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ValidateRules();
            }

            if (GUILayout.Button("Apply All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ApplyAllRules();
            }

            if (GUILayout.Button("Import", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                ImportRules();
            }

            if (GUILayout.Button("Export", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                ExportRules();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawNoRuleDataSelected()
        {
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.Width(400));
            EditorGUILayout.HelpBox("No LayoutRuleData selected.\n\nSelect an existing one or create a new one to get started.", MessageType.Info);

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Create New LayoutRuleData", GUILayout.Height(30)))
            {
                CreateNewRuleData();
            }

            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void DrawRuleListPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(250));

            // Rule type tabs
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_selectedRuleType == RuleType.Address, "Address", EditorStyles.toolbarButton))
                _selectedRuleType = RuleType.Address;
            if (GUILayout.Toggle(_selectedRuleType == RuleType.Label, "Label", EditorStyles.toolbarButton))
                _selectedRuleType = RuleType.Label;
            if (GUILayout.Toggle(_selectedRuleType == RuleType.Version, "Version", EditorStyles.toolbarButton))
                _selectedRuleType = RuleType.Version;
            EditorGUILayout.EndHorizontal();

            // Rule list
            _leftScrollPos = EditorGUILayout.BeginScrollView(_leftScrollPos, GUI.skin.box);

            switch (_selectedRuleType)
            {
                case RuleType.Address:
                    DrawAddressRuleList();
                    break;
                case RuleType.Label:
                    DrawLabelRuleList();
                    break;
                case RuleType.Version:
                    DrawVersionRuleList();
                    break;
            }

            EditorGUILayout.EndScrollView();

            // Add/Remove buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Rule"))
            {
                AddNewRule();
            }
            GUI.enabled = _selectedRuleIndex >= 0;
            if (GUILayout.Button("- Remove"))
            {
                RemoveSelectedRule();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawAddressRuleList()
        {
            for (int i = 0; i < _selectedRuleData.AddressRules.Count; i++)
            {
                var rule = _selectedRuleData.AddressRules[i];
                if (rule == null) continue;

                var style = i == _selectedRuleIndex ? _selectedItemStyle : _normalItemStyle;

                EditorGUILayout.BeginVertical(style);
                if (GUILayout.Button(rule.RuleName, EditorStyles.label, GUILayout.Height(30)))
                {
                    if (_selectedRuleIndex != i)
                    {
                        _selectedRuleIndex = i;
                        _previewNeedsRefresh = true;
                    }
                }

                // Mini info
                EditorGUILayout.LabelField($"Filters: {rule.Filters.Count} | Priority: {rule.Priority}", EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }

        private void DrawLabelRuleList()
        {
            for (int i = 0; i < _selectedRuleData.LabelRules.Count; i++)
            {
                var rule = _selectedRuleData.LabelRules[i];
                if (rule == null) continue;

                var style = i == _selectedRuleIndex ? _selectedItemStyle : _normalItemStyle;

                EditorGUILayout.BeginVertical(style);
                if (GUILayout.Button(rule.RuleName, EditorStyles.label, GUILayout.Height(30)))
                {
                    if (_selectedRuleIndex != i)
                    {
                        _selectedRuleIndex = i;
                        _previewNeedsRefresh = true;
                    }
                }

                EditorGUILayout.LabelField($"Filters: {rule.Filters.Count} | Priority: {rule.Priority}", EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }

        private void DrawVersionRuleList()
        {
            for (int i = 0; i < _selectedRuleData.VersionRules.Count; i++)
            {
                var rule = _selectedRuleData.VersionRules[i];
                if (rule == null) continue;

                var style = i == _selectedRuleIndex ? _selectedItemStyle : _normalItemStyle;

                EditorGUILayout.BeginVertical(style);
                if (GUILayout.Button(rule.RuleName, EditorStyles.label, GUILayout.Height(30)))
                {
                    if (_selectedRuleIndex != i)
                    {
                        _selectedRuleIndex = i;
                        _previewNeedsRefresh = true;
                    }
                }

                EditorGUILayout.LabelField($"Filters: {rule.Filters.Count} | Priority: {rule.Priority}", EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
        }

        private void DrawRuleConfigPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.MinWidth(400));

            if (_selectedRuleIndex < 0)
            {
                EditorGUILayout.HelpBox("Select a rule from the list to edit it.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            _centerScrollPos = EditorGUILayout.BeginScrollView(_centerScrollPos);

            switch (_selectedRuleType)
            {
                case RuleType.Address:
                    DrawAddressRuleConfig();
                    break;
                case RuleType.Label:
                    DrawLabelRuleConfig();
                    break;
                case RuleType.Version:
                    DrawVersionRuleConfig();
                    break;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawAddressRuleConfig()
        {
            if (_selectedRuleIndex >= _selectedRuleData.AddressRules.Count) return;

            var rule = _selectedRuleData.AddressRules[_selectedRuleIndex];
            if (rule == null) return;

            EditorGUILayout.LabelField("Address Rule Configuration", _headerStyle);
            EditorGUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            rule.RuleName = EditorGUILayout.TextField("Rule Name", rule.RuleName);
            rule.Enabled = EditorGUILayout.Toggle("Enabled", rule.Enabled);
            rule.Description = EditorGUILayout.TextArea(rule.Description, GUILayout.Height(40));
            rule.TargetGroupName = EditorGUILayout.TextField("Target Group", rule.TargetGroupName);
            rule.Priority = EditorGUILayout.IntField("Priority", rule.Priority);
            rule.SkipExisting = EditorGUILayout.Toggle("Skip Existing", rule.SkipExisting);

            EditorGUILayout.Space(10);

            // Filters
            EditorGUILayout.LabelField("Filters (AND logic)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("All filters must match for the rule to apply", MessageType.None);

            for (int i = 0; i < rule.Filters.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                rule.Filters[i] = (AssetFilterBase)EditorGUILayout.ObjectField($"Filter {i + 1}", rule.Filters[i], typeof(AssetFilterBase), false);
                if (GUILayout.Button("-", GUILayout.Width(25)))
                {
                    rule.Filters.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Filter"))
            {
                rule.Filters.Add(null);
            }

            EditorGUILayout.Space(10);

            // Provider
            EditorGUILayout.LabelField("Address Provider", EditorStyles.boldLabel);
            rule.AddressProvider = (AddressProviderBase)EditorGUILayout.ObjectField("Provider", rule.AddressProvider, typeof(AddressProviderBase), false);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_selectedRuleData);
            }
        }

        private void DrawLabelRuleConfig()
        {
            if (_selectedRuleIndex >= _selectedRuleData.LabelRules.Count) return;

            var rule = _selectedRuleData.LabelRules[_selectedRuleIndex];
            if (rule == null) return;

            EditorGUILayout.LabelField("Label Rule Configuration", _headerStyle);
            EditorGUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            rule.RuleName = EditorGUILayout.TextField("Rule Name", rule.RuleName);
            rule.Enabled = EditorGUILayout.Toggle("Enabled", rule.Enabled);
            rule.Description = EditorGUILayout.TextArea(rule.Description, GUILayout.Height(40));
            rule.Priority = EditorGUILayout.IntField("Priority", rule.Priority);
            rule.AppendToExisting = EditorGUILayout.Toggle("Append to Existing", rule.AppendToExisting);

            EditorGUILayout.Space(10);

            // Filters
            EditorGUILayout.LabelField("Filters (AND logic)", EditorStyles.boldLabel);

            for (int i = 0; i < rule.Filters.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                rule.Filters[i] = (AssetFilterBase)EditorGUILayout.ObjectField($"Filter {i + 1}", rule.Filters[i], typeof(AssetFilterBase), false);
                if (GUILayout.Button("-", GUILayout.Width(25)))
                {
                    rule.Filters.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Filter"))
            {
                rule.Filters.Add(null);
            }

            EditorGUILayout.Space(10);

            // Provider
            EditorGUILayout.LabelField("Label Provider", EditorStyles.boldLabel);
            rule.LabelProvider = (LabelProviderBase)EditorGUILayout.ObjectField("Provider", rule.LabelProvider, typeof(LabelProviderBase), false);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_selectedRuleData);
            }
        }

        private void DrawVersionRuleConfig()
        {
            if (_selectedRuleIndex >= _selectedRuleData.VersionRules.Count) return;

            var rule = _selectedRuleData.VersionRules[_selectedRuleIndex];
            if (rule == null) return;

            EditorGUILayout.LabelField("Version Rule Configuration", _headerStyle);
            EditorGUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            rule.RuleName = EditorGUILayout.TextField("Rule Name", rule.RuleName);
            rule.Enabled = EditorGUILayout.Toggle("Enabled", rule.Enabled);
            rule.Description = EditorGUILayout.TextArea(rule.Description, GUILayout.Height(40));
            rule.Priority = EditorGUILayout.IntField("Priority", rule.Priority);
            rule.SkipExisting = EditorGUILayout.Toggle("Skip Existing", rule.SkipExisting);

            EditorGUILayout.Space(10);

            // Filters
            EditorGUILayout.LabelField("Filters (AND logic)", EditorStyles.boldLabel);

            for (int i = 0; i < rule.Filters.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                rule.Filters[i] = (AssetFilterBase)EditorGUILayout.ObjectField($"Filter {i + 1}", rule.Filters[i], typeof(AssetFilterBase), false);
                if (GUILayout.Button("-", GUILayout.Width(25)))
                {
                    rule.Filters.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Filter"))
            {
                rule.Filters.Add(null);
            }

            EditorGUILayout.Space(10);

            // Provider
            EditorGUILayout.LabelField("Version Provider", EditorStyles.boldLabel);
            rule.VersionProvider = (VersionProviderBase)EditorGUILayout.ObjectField("Provider", rule.VersionProvider, typeof(VersionProviderBase), false);

            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_selectedRuleData);
            }
        }

        private void DrawPreviewPanel()
        {
            EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(350));

            EditorGUILayout.LabelField("Preview", _headerStyle);

            if (_selectedRuleIndex < 0)
            {
                EditorGUILayout.HelpBox("Select a rule to see preview of matched assets", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // Preview controls
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Preview Limit:", GUILayout.Width(90));
            _previewLimit = EditorGUILayout.IntSlider(_previewLimit, 10, 200);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(_isGeneratingPreview ? "Generating..." : "Refresh Preview", GUILayout.Height(25)))
            {
                if (!_isGeneratingPreview)
                {
                    GeneratePreview();
                }
            }

            EditorGUILayout.Space(5);

            // Statistics
            if (_totalMatchCount > 0)
            {
                string stats = $"Showing {_previewItems.Count} of {_totalMatchCount} matched assets";
                EditorGUILayout.LabelField(stats, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(3);

            // Preview list
            _rightScrollPos = EditorGUILayout.BeginScrollView(_rightScrollPos);

            if (_previewNeedsRefresh)
            {
                EditorGUILayout.HelpBox("Click 'Refresh Preview' to see matched assets", MessageType.Info);
            }
            else if (_isGeneratingPreview)
            {
                EditorGUILayout.HelpBox("Generating preview...", MessageType.Info);
            }
            else if (_previewItems.Count == 0)
            {
                EditorGUILayout.HelpBox("No assets match this rule", MessageType.Warning);
            }
            else
            {
                foreach (var item in _previewItems)
                {
                    DrawPreviewItem(item);
                    EditorGUILayout.Space(2);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawPreviewItem(PreviewItem item)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Asset path (clickable)
            if (GUILayout.Button(System.IO.Path.GetFileName(item.AssetPath), EditorStyles.label))
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.AssetPath);
                if (obj != null)
                {
                    EditorGUIUtility.PingObject(obj);
                    Selection.activeObject = obj;
                }
            }

            EditorGUILayout.LabelField(item.AssetPath, EditorStyles.miniLabel);

            // Generated value
            if (item.IsValid)
            {
                string label = _selectedRuleType switch
                {
                    RuleType.Address => "Address:",
                    RuleType.Label => "Labels:",
                    RuleType.Version => "Version:",
                    _ => "Value:"
                };

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(label, GUILayout.Width(60));
                EditorGUILayout.SelectableLabel(item.GeneratedValue, EditorStyles.miniLabel, GUILayout.Height(15));
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox($"Error: {item.ErrorMessage}", MessageType.Error);
            }

            EditorGUILayout.EndVertical();
        }

        private void GeneratePreview()
        {
            if (_selectedRuleIndex < 0 || _selectedRuleData == null) return;

            _isGeneratingPreview = true;
            _previewItems.Clear();
            _totalMatchCount = 0;

            try
            {
                // Get the selected rule
                object selectedRule = null;
                switch (_selectedRuleType)
                {
                    case RuleType.Address:
                        if (_selectedRuleIndex < _selectedRuleData.AddressRules.Count)
                            selectedRule = _selectedRuleData.AddressRules[_selectedRuleIndex];
                        break;
                    case RuleType.Label:
                        if (_selectedRuleIndex < _selectedRuleData.LabelRules.Count)
                            selectedRule = _selectedRuleData.LabelRules[_selectedRuleIndex];
                        break;
                    case RuleType.Version:
                        if (_selectedRuleIndex < _selectedRuleData.VersionRules.Count)
                            selectedRule = _selectedRuleData.VersionRules[_selectedRuleIndex];
                        break;
                }

                if (selectedRule == null)
                {
                    _isGeneratingPreview = false;
                    _previewNeedsRefresh = false;
                    return;
                }

                // Setup the rule
                switch (selectedRule)
                {
                    case AddressRule ar:
                        ar.Setup();
                        GenerateAddressPreview(ar);
                        break;
                    case LabelRule lr:
                        lr.Setup();
                        GenerateLabelPreview(lr);
                        break;
                    case VersionRule vr:
                        vr.Setup();
                        GenerateVersionPreview(vr);
                        break;
                }

                _previewNeedsRefresh = false;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error generating preview: {ex.Message}");
            }
            finally
            {
                _isGeneratingPreview = false;
                Repaint();
            }
        }

        private void GenerateAddressPreview(AddressRule rule)
        {
            var allAssets = GetAllAssetPaths();
            int matchCount = 0;

            foreach (var assetPath in allAssets)
            {
                if (rule.IsMatch(assetPath))
                {
                    matchCount++;

                    if (_previewItems.Count < _previewLimit)
                    {
                        try
                        {
                            string address = rule.GenerateAddress(assetPath);
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = address,
                                IsValid = !string.IsNullOrEmpty(address),
                                ErrorMessage = string.IsNullOrEmpty(address) ? "Empty address generated" : ""
                            });
                        }
                        catch (System.Exception ex)
                        {
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = "",
                                IsValid = false,
                                ErrorMessage = ex.Message
                            });
                        }
                    }
                }
            }

            _totalMatchCount = matchCount;
        }

        private void GenerateLabelPreview(LabelRule rule)
        {
            var allAssets = GetAllAssetPaths();
            int matchCount = 0;

            foreach (var assetPath in allAssets)
            {
                if (rule.IsMatch(assetPath))
                {
                    matchCount++;

                    if (_previewItems.Count < _previewLimit)
                    {
                        try
                        {
                            var labels = rule.GenerateLabels(assetPath);
                            string labelsStr = labels != null ? string.Join(", ", labels) : "";
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = labelsStr,
                                IsValid = labels != null && labels.Count > 0,
                                ErrorMessage = (labels == null || labels.Count == 0) ? "No labels generated" : ""
                            });
                        }
                        catch (System.Exception ex)
                        {
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = "",
                                IsValid = false,
                                ErrorMessage = ex.Message
                            });
                        }
                    }
                }
            }

            _totalMatchCount = matchCount;
        }

        private void GenerateVersionPreview(VersionRule rule)
        {
            var allAssets = GetAllAssetPaths();
            int matchCount = 0;

            foreach (var assetPath in allAssets)
            {
                if (rule.IsMatch(assetPath))
                {
                    matchCount++;

                    if (_previewItems.Count < _previewLimit)
                    {
                        try
                        {
                            string version = rule.GenerateVersion(assetPath);
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = $"version:{version}",
                                IsValid = !string.IsNullOrEmpty(version),
                                ErrorMessage = string.IsNullOrEmpty(version) ? "Empty version generated" : ""
                            });
                        }
                        catch (System.Exception ex)
                        {
                            _previewItems.Add(new PreviewItem
                            {
                                AssetPath = assetPath,
                                GeneratedValue = "",
                                IsValid = false,
                                ErrorMessage = ex.Message
                            });
                        }
                    }
                }
            }

            _totalMatchCount = matchCount;
        }

        private List<string> GetAllAssetPaths()
        {
            var paths = new List<string>();
            var allGuids = AssetDatabase.FindAssets("");

            foreach (var guid in allGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private void AddNewRule()
        {
            switch (_selectedRuleType)
            {
                case RuleType.Address:
                    var addressRule = new AddressRule();
                    addressRule.RuleName = $"Address Rule {_selectedRuleData.AddressRules.Count + 1}";
                    _selectedRuleData.AddAddressRule(addressRule);
                    _selectedRuleIndex = _selectedRuleData.AddressRules.Count - 1;
                    break;

                case RuleType.Label:
                    var labelRule = new LabelRule();
                    labelRule.RuleName = $"Label Rule {_selectedRuleData.LabelRules.Count + 1}";
                    _selectedRuleData.AddLabelRule(labelRule);
                    _selectedRuleIndex = _selectedRuleData.LabelRules.Count - 1;
                    break;

                case RuleType.Version:
                    var versionRule = new VersionRule();
                    versionRule.RuleName = $"Version Rule {_selectedRuleData.VersionRules.Count + 1}";
                    _selectedRuleData.AddVersionRule(versionRule);
                    _selectedRuleIndex = _selectedRuleData.VersionRules.Count - 1;
                    break;
            }

            EditorUtility.SetDirty(_selectedRuleData);
        }

        private void RemoveSelectedRule()
        {
            if (_selectedRuleIndex < 0) return;

            switch (_selectedRuleType)
            {
                case RuleType.Address:
                    if (_selectedRuleIndex < _selectedRuleData.AddressRules.Count)
                    {
                        _selectedRuleData.RemoveAddressRule(_selectedRuleData.AddressRules[_selectedRuleIndex]);
                    }
                    break;

                case RuleType.Label:
                    if (_selectedRuleIndex < _selectedRuleData.LabelRules.Count)
                    {
                        _selectedRuleData.RemoveLabelRule(_selectedRuleData.LabelRules[_selectedRuleIndex]);
                    }
                    break;

                case RuleType.Version:
                    if (_selectedRuleIndex < _selectedRuleData.VersionRules.Count)
                    {
                        _selectedRuleData.RemoveVersionRule(_selectedRuleData.VersionRules[_selectedRuleIndex]);
                    }
                    break;
            }

            _selectedRuleIndex = -1;
            EditorUtility.SetDirty(_selectedRuleData);
        }

        private void CreateNewRuleData()
        {
            var asset = ScriptableObject.CreateInstance<LayoutRuleData>();
            ProjectWindowUtil.CreateAsset(asset, "NewLayoutRuleData.asset");
        }

        private void ValidateRules()
        {
            if (_selectedRuleData == null) return;

            var messages = RuleValidator.Validate(_selectedRuleData);
            var summary = RuleValidator.GetValidationSummary(messages);

            if (messages.Count == 0)
            {
                EditorUtility.DisplayDialog("Validation", "✓ No validation issues found!", "OK");
            }
            else
            {
                string message = $"{summary}\n\n";
                foreach (var msg in messages.Take(10))
                {
                    message += $"• [{msg.Severity}] {msg.Message}\n";
                }
                if (messages.Count > 10)
                {
                    message += $"\n... and {messages.Count - 10} more (check console)";
                }

                foreach (var msg in messages)
                {
                    Debug.LogWarning($"[Validation] {msg.Severity}: {msg.Message}");
                }

                EditorUtility.DisplayDialog("Validation Results", message, "OK");
            }
        }

        private void ApplyAllRules()
        {
            if (_selectedRuleData == null) return;

            if (EditorUtility.DisplayDialog("Apply All Rules",
                "This will apply all rules in this LayoutRuleData. Continue?",
                "Yes", "Cancel"))
            {
                try
                {
                    var processor = new LayoutRuleProcessor(_selectedRuleData);
                    var result = processor.ApplyRules((progress, message) =>
                    {
                        EditorUtility.DisplayProgressBar("Applying Rules", message, progress);
                    });

                    EditorUtility.ClearProgressBar();

                    string message = $"Rules applied!\n\n" +
                        $"Processed: {result.TotalAssetsProcessed}\n" +
                        $"Addresses: {result.AddressesApplied}\n" +
                        $"Labels: {result.LabelsApplied}\n" +
                        $"Errors: {result.Errors.Count}";

                    EditorUtility.DisplayDialog("Complete", message, "OK");
                }
                catch (System.Exception ex)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Error", ex.Message, "OK");
                }
            }
        }

        private void ImportRules()
        {
            if (_selectedRuleData == null)
            {
                EditorUtility.DisplayDialog("Import Rules", "Please select a LayoutRuleData first", "OK");
                return;
            }

            string path = EditorUtility.OpenFilePanel("Import Rules", Application.dataPath, "json");
            if (string.IsNullOrEmpty(path))
            {
                return; // User cancelled
            }

            bool mergeMode = EditorUtility.DisplayDialog("Import Mode",
                "How would you like to import the rules?\n\n" +
                "Replace: Remove all existing rules and import new ones\n" +
                "Merge: Keep existing rules and add imported rules",
                "Merge", "Replace");

            bool success = RuleSerializer.ImportFromJson(_selectedRuleData, path, mergeMode);

            if (success)
            {
                EditorUtility.DisplayDialog("Import Complete",
                    $"Rules imported successfully from:\n{System.IO.Path.GetFileName(path)}\n\n" +
                    $"Mode: {(mergeMode ? "Merge" : "Replace")}\n" +
                    $"Total rules: {_selectedRuleData.TotalRuleCount}",
                    "OK");

                _selectedRuleIndex = -1;
                _previewNeedsRefresh = true;
                Repaint();
            }
            else
            {
                EditorUtility.DisplayDialog("Import Failed",
                    "Failed to import rules. Check the console for details.",
                    "OK");
            }
        }

        private void ExportRules()
        {
            if (_selectedRuleData == null)
            {
                EditorUtility.DisplayDialog("Export Rules", "Please select a LayoutRuleData first", "OK");
                return;
            }

            string defaultName = string.IsNullOrEmpty(_selectedRuleData.Description)
                ? "AddressableRules"
                : _selectedRuleData.Description.Replace(" ", "_");

            string path = EditorUtility.SaveFilePanel("Export Rules", Application.dataPath, defaultName, "json");
            if (string.IsNullOrEmpty(path))
            {
                return; // User cancelled
            }

            bool success = RuleSerializer.ExportToJson(_selectedRuleData, path);

            if (success)
            {
                EditorUtility.DisplayDialog("Export Complete",
                    $"Rules exported successfully to:\n{System.IO.Path.GetFileName(path)}\n\n" +
                    $"Address rules: {_selectedRuleData.AddressRules.Count}\n" +
                    $"Label rules: {_selectedRuleData.LabelRules.Count}\n" +
                    $"Version rules: {_selectedRuleData.VersionRules.Count}",
                    "OK");

                // Offer to reveal in explorer
                if (EditorUtility.DisplayDialog("Show in Explorer",
                    "Would you like to reveal the exported file?",
                    "Yes", "No"))
                {
                    EditorUtility.RevealInFinder(path);
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Export Failed",
                    "Failed to export rules. Check the console for details.",
                    "OK");
            }
        }
    }
}
