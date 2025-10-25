using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Data;

namespace AddressableManager.Editor.Windows
{
    /// <summary>
    /// Main Dashboard Window for Addressable Manager
    /// Real-time monitoring of assets, scopes, and performance
    /// </summary>
    public class AddressableManagerWindow : EditorWindow
    {
        private VisualElement _root;
        private AssetTrackerService _tracker;
        private PerformanceMetrics _metrics;

        // Tab pages
        private VisualElement _tabAssets;
        private VisualElement _tabPerformance;
        private VisualElement _tabScopes;
        private VisualElement _tabSettings;

        // Tab buttons
        private Button _tabAssetsBtn;
        private Button _tabPerformanceBtn;
        private Button _tabScopesBtn;
        private Button _tabSettingsBtn;

        // UI Elements - Assets Tab
        private ListView _assetsList;
        private TextField _searchField;
        private DropdownField _scopeFilter;
        private Label _assetCountLabel;

        // UI Elements - Performance Tab
        private Label _statTotalValue;
        private Label _statCacheValue;
        private Label _statMemoryValue;
        private Label _statLoadValue;
        private ListView _slowestAssetsList;
        private Button _exportBtn;

        // UI Elements - Scopes Tab
        private Dictionary<string, (Foldout foldout, Label stats, ListView list, Button cleanup)> _scopeElements;

        // UI Elements - Settings Tab
        private DropdownField _logLevelDropdown;
        private Toggle _autoRefreshToggle;
        private SliderInt _refreshIntervalSlider;
        private Toggle _simulateSlowToggle;
        private SliderInt _delaySlider;
        private Slider _failureRateSlider;

        // Footer
        private Label _lastUpdatedLabel;
        private Label _statusLabel;

        // State
        private bool _autoRefresh = true;
        private float _refreshInterval = 0.5f;
        private double _lastRefreshTime;
        private int _currentTab = 0;

        [MenuItem("Window/Addressable Manager/Dashboard %&a")]
        public static void ShowWindow()
        {
            var window = GetWindow<AddressableManagerWindow>();
            window.titleContent = new GUIContent("Addressable Manager");
            window.minSize = new Vector2(800, 600);
        }

        public void CreateGUI()
        {
            _tracker = AssetTrackerService.Instance;
            _metrics = PerformanceMetrics.Instance;

            // Load UXML
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Assets/com.game.addressables/Editor/UI/AddressableManagerWindow.uxml");

            if (visualTree != null)
            {
                _root = visualTree.CloneTree();
                rootVisualElement.Add(_root);
            }
            else
            {
                Debug.LogError("[AddressableManager] Failed to load UXML file. Creating fallback UI.");
                CreateFallbackUI();
                return;
            }

            // Load USS
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Assets/com.game.addressables/Editor/UI/Styles.uss");

            if (styleSheet != null)
            {
                _root.styleSheets.Add(styleSheet);
            }

            InitializeUIElements();
            SetupTabNavigation();
            SetupEventHandlers();
            SubscribeToEvents();

            RefreshAllData();
        }

        private void OnDestroy()
        {
            UnsubscribeFromEvents();
        }

        private void Update()
        {
            if (!_autoRefresh) return;

            if (EditorApplication.timeSinceStartup - _lastRefreshTime >= _refreshInterval)
            {
                RefreshCurrentTab();
                _lastRefreshTime = EditorApplication.timeSinceStartup;
            }
        }

        #region Initialization

        private void InitializeUIElements()
        {
            // Tab pages
            _tabAssets = _root.Q<VisualElement>("tab-assets");
            _tabPerformance = _root.Q<VisualElement>("tab-performance");
            _tabScopes = _root.Q<VisualElement>("tab-scopes");
            _tabSettings = _root.Q<VisualElement>("tab-settings");

            // Tab buttons
            _tabAssetsBtn = _root.Q<Button>("tab-assets-btn");
            _tabPerformanceBtn = _root.Q<Button>("tab-performance-btn");
            _tabScopesBtn = _root.Q<Button>("tab-scopes-btn");
            _tabSettingsBtn = _root.Q<Button>("tab-settings-btn");

            // Header
            var refreshBtn = _root.Q<Button>("refresh-btn");
            refreshBtn.clicked += RefreshAllData;

            // Assets Tab
            _assetsList = _root.Q<ListView>("assets-list");
            _searchField = _root.Q<TextField>("search-field");
            _scopeFilter = _root.Q<DropdownField>("scope-filter");
            _assetCountLabel = _root.Q<Label>("asset-count-label");

            _scopeFilter.choices = new List<string> { "All", "Global", "Session", "Scene", "Hierarchy" };
            _scopeFilter.value = "All";

            // Performance Tab
            _statTotalValue = _root.Q<Label>("stat-total-value");
            _statCacheValue = _root.Q<Label>("stat-cache-value");
            _statMemoryValue = _root.Q<Label>("stat-memory-value");
            _statLoadValue = _root.Q<Label>("stat-load-value");
            _slowestAssetsList = _root.Q<ListView>("slowest-assets-list");
            _exportBtn = _root.Q<Button>("export-btn");

            // Scopes Tab
            InitializeScopeElements();

            // Settings Tab
            _logLevelDropdown = _root.Q<DropdownField>("log-level-dropdown");
            _autoRefreshToggle = _root.Q<Toggle>("auto-refresh-toggle");
            _refreshIntervalSlider = _root.Q<SliderInt>("refresh-interval-slider");
            _simulateSlowToggle = _root.Q<Toggle>("simulate-slow-toggle");
            _delaySlider = _root.Q<SliderInt>("delay-slider");
            _failureRateSlider = _root.Q<Slider>("failure-rate-slider");

            _logLevelDropdown.choices = new List<string> { "None", "Errors Only", "Warnings and Errors", "All" };
            _logLevelDropdown.value = "Warnings and Errors";

            var resetSettingsBtn = _root.Q<Button>("reset-settings-btn");
            var resetStatsBtn = _root.Q<Button>("reset-stats-btn");
            resetSettingsBtn.clicked += ResetSettings;
            resetStatsBtn.clicked += ResetStatistics;

            // Footer
            _lastUpdatedLabel = _root.Q<Label>("last-updated-label");
            _statusLabel = _root.Q<Label>("status-label");

            UpdateStatusLabel();
        }

        private void InitializeScopeElements()
        {
            _scopeElements = new Dictionary<string, (Foldout, Label, ListView, Button)>
            {
                ["Global"] = (
                    _root.Q<Foldout>("scope-global"),
                    _root.Q<Label>("scope-global-stats"),
                    _root.Q<ListView>("scope-global-list"),
                    _root.Q<Button>("scope-global-cleanup")
                ),
                ["Session"] = (
                    _root.Q<Foldout>("scope-session"),
                    _root.Q<Label>("scope-session-stats"),
                    _root.Q<ListView>("scope-session-list"),
                    _root.Q<Button>("scope-session-cleanup")
                ),
                ["Scene"] = (
                    _root.Q<Foldout>("scope-scene"),
                    _root.Q<Label>("scope-scene-stats"),
                    _root.Q<ListView>("scope-scene-list"),
                    _root.Q<Button>("scope-scene-cleanup")
                ),
                ["Hierarchy"] = (
                    _root.Q<Foldout>("scope-hierarchy"),
                    _root.Q<Label>("scope-hierarchy-stats"),
                    _root.Q<ListView>("scope-hierarchy-list"),
                    _root.Q<Button>("scope-hierarchy-cleanup")
                )
            };

            var cleanupAllBtn = _root.Q<Button>("cleanup-all-btn");
            cleanupAllBtn.clicked += () =>
            {
                if (EditorUtility.DisplayDialog("Cleanup All Scopes",
                    "Are you sure you want to cleanup all scopes? This will release all tracked assets.",
                    "Yes", "Cancel"))
                {
                    foreach (var scope in _scopeElements.Keys)
                    {
                        _tracker.ClearScope(scope);
                    }
                    RefreshScopesTab();
                }
            };

            // Setup cleanup buttons for each scope
            foreach (var kvp in _scopeElements)
            {
                var scopeName = kvp.Key;
                var cleanupBtn = kvp.Value.cleanup;
                cleanupBtn.clicked += () =>
                {
                    if (EditorUtility.DisplayDialog($"Cleanup {scopeName} Scope",
                        $"Are you sure you want to cleanup the {scopeName} scope?",
                        "Yes", "Cancel"))
                    {
                        _tracker.ClearScope(scopeName);
                        RefreshScopesTab();
                    }
                };
            }
        }

        private void SetupTabNavigation()
        {
            _tabAssetsBtn.clicked += () => SwitchTab(0);
            _tabPerformanceBtn.clicked += () => SwitchTab(1);
            _tabScopesBtn.clicked += () => SwitchTab(2);
            _tabSettingsBtn.clicked += () => SwitchTab(3);

            SwitchTab(0); // Start with Assets tab
        }

        private void SetupEventHandlers()
        {
            _searchField.RegisterValueChangedCallback(evt => RefreshAssetsTab());
            _scopeFilter.RegisterValueChangedCallback(evt => RefreshAssetsTab());

            _exportBtn.clicked += ExportPerformanceReport;

            _autoRefreshToggle.RegisterValueChangedCallback(evt =>
            {
                _autoRefresh = evt.newValue;
            });

            _refreshIntervalSlider.RegisterValueChangedCallback(evt =>
            {
                _refreshInterval = evt.newValue / 1000f; // Convert ms to seconds
            });
        }

        private void SubscribeToEvents()
        {
            _tracker.OnAssetsChanged += OnTrackerDataChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void UnsubscribeFromEvents()
        {
            if (_tracker != null)
            {
                _tracker.OnAssetsChanged -= OnTrackerDataChanged;
            }
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        #endregion

        #region Tab Navigation

        private void SwitchTab(int tabIndex)
        {
            _currentTab = tabIndex;

            // Hide all tabs
            _tabAssets.style.display = DisplayStyle.None;
            _tabPerformance.style.display = DisplayStyle.None;
            _tabScopes.style.display = DisplayStyle.None;
            _tabSettings.style.display = DisplayStyle.None;

            // Remove active class from all buttons
            _tabAssetsBtn.RemoveFromClassList("tab-button-active");
            _tabPerformanceBtn.RemoveFromClassList("tab-button-active");
            _tabScopesBtn.RemoveFromClassList("tab-button-active");
            _tabSettingsBtn.RemoveFromClassList("tab-button-active");

            // Show selected tab and mark button as active
            switch (tabIndex)
            {
                case 0:
                    _tabAssets.style.display = DisplayStyle.Flex;
                    _tabAssetsBtn.AddToClassList("tab-button-active");
                    RefreshAssetsTab();
                    break;
                case 1:
                    _tabPerformance.style.display = DisplayStyle.Flex;
                    _tabPerformanceBtn.AddToClassList("tab-button-active");
                    RefreshPerformanceTab();
                    break;
                case 2:
                    _tabScopes.style.display = DisplayStyle.Flex;
                    _tabScopesBtn.AddToClassList("tab-button-active");
                    RefreshScopesTab();
                    break;
                case 3:
                    _tabSettings.style.display = DisplayStyle.Flex;
                    _tabSettingsBtn.AddToClassList("tab-button-active");
                    break;
            }
        }

        #endregion

        #region Data Refresh

        private void RefreshAllData()
        {
            RefreshCurrentTab();
            UpdateLastUpdatedLabel();
        }

        private void RefreshCurrentTab()
        {
            switch (_currentTab)
            {
                case 0:
                    RefreshAssetsTab();
                    break;
                case 1:
                    RefreshPerformanceTab();
                    break;
                case 2:
                    RefreshScopesTab();
                    break;
            }
        }

        private void RefreshAssetsTab()
        {
            var allAssets = _tracker.TrackedAssets.Values
                .Where(a => a.IsValid)
                .ToList();

            // Apply filters
            var searchTerm = _searchField.value?.ToLower() ?? "";
            var scopeFilterValue = _scopeFilter.value;

            var filteredAssets = allAssets
                .Where(a =>
                {
                    // Search filter
                    if (!string.IsNullOrEmpty(searchTerm) &&
                        !a.Address.ToLower().Contains(searchTerm) &&
                        !a.TypeName.ToLower().Contains(searchTerm))
                    {
                        return false;
                    }

                    // Scope filter
                    if (scopeFilterValue != "All" && a.ScopeName != scopeFilterValue)
                    {
                        return false;
                    }

                    return true;
                })
                .OrderByDescending(a => a.LoadTime)
                .ToList();

            _assetCountLabel.text = $"{filteredAssets.Count} asset{(filteredAssets.Count != 1 ? "s" : "")} loaded";

            _assetsList.itemsSource = filteredAssets;
            _assetsList.makeItem = MakeAssetListItem;
            _assetsList.bindItem = BindAssetListItem;
            _assetsList.fixedItemHeight = 60;
            _assetsList.Rebuild();
        }

        private void RefreshPerformanceTab()
        {
            var summary = _metrics.GetSummary();

            _statTotalValue.text = summary.TotalAssets.ToString();
            _statCacheValue.text = $"{summary.CacheHitRatio * 100:F1}%";
            _statMemoryValue.text = $"{summary.TotalMemory / (1024f * 1024f):F2} MB";
            _statLoadValue.text = $"{summary.AverageLoadTime * 1000:F0} ms";

            // Slowest assets
            _slowestAssetsList.itemsSource = summary.SlowestAssets;
            _slowestAssetsList.makeItem = MakeSlowestAssetItem;
            _slowestAssetsList.bindItem = BindSlowestAssetItem;
            _slowestAssetsList.fixedItemHeight = 40;
            _slowestAssetsList.Rebuild();

            // TODO: Implement memory graph rendering
        }

        private void RefreshScopesTab()
        {
            foreach (var kvp in _scopeElements)
            {
                var scopeName = kvp.Key;
                var (foldout, statsLabel, listView, cleanupBtn) = kvp.Value;

                var assets = _tracker.GetAssetsByScope(scopeName);
                var memory = assets.Sum(a => a.MemorySize);

                statsLabel.text = $"Assets: {assets.Count} | Memory: {memory / (1024f * 1024f):F2} MB";

                listView.itemsSource = assets;
                listView.makeItem = () => new Label();
                listView.bindItem = (element, index) =>
                {
                    var label = element as Label;
                    var asset = assets[index];
                    label.text = $"{asset.Address} ({asset.TypeName}) - Refs: {asset.ReferenceCount}";
                };
                listView.fixedItemHeight = 25;
                listView.Rebuild();
            }
        }

        #endregion

        #region UI Item Factories

        private VisualElement MakeAssetListItem()
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Row;
            container.style.justifyContent = Justify.SpaceBetween;
            container.style.paddingTop = 5;
            container.style.paddingBottom = 5;
            container.style.paddingLeft = 5;
            container.style.paddingRight = 5;

            var leftSection = new VisualElement();
            leftSection.style.flexGrow = 1;

            var addressLabel = new Label();
            addressLabel.name = "address-label";
            addressLabel.style.fontSize = 12;
            addressLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            leftSection.Add(addressLabel);

            var infoLabel = new Label();
            infoLabel.name = "info-label";
            infoLabel.style.fontSize = 10;
            infoLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            leftSection.Add(infoLabel);

            var rightSection = new VisualElement();
            rightSection.style.alignItems = Align.FlexEnd;

            var refsLabel = new Label();
            refsLabel.name = "refs-label";
            refsLabel.style.fontSize = 11;
            rightSection.Add(refsLabel);

            var memoryLabel = new Label();
            memoryLabel.name = "memory-label";
            memoryLabel.style.fontSize = 10;
            memoryLabel.style.color = new Color(0.3f, 0.8f, 1f);
            rightSection.Add(memoryLabel);

            container.Add(leftSection);
            container.Add(rightSection);

            return container;
        }

        private void BindAssetListItem(VisualElement element, int index)
        {
            var assets = _assetsList.itemsSource as List<AssetTrackerService.TrackedAsset>;
            if (assets == null || index >= assets.Count) return;

            var asset = assets[index];

            var addressLabel = element.Q<Label>("address-label");
            var infoLabel = element.Q<Label>("info-label");
            var refsLabel = element.Q<Label>("refs-label");
            var memoryLabel = element.Q<Label>("memory-label");

            addressLabel.text = asset.Address;
            infoLabel.text = $"{asset.TypeName} • {asset.ScopeName} Scope • Loaded {GetTimeSince(asset.LoadTime)} ago";
            refsLabel.text = $"Refs: {asset.ReferenceCount}";
            memoryLabel.text = $"{asset.MemorySize / 1024f:F0} KB";
        }

        private VisualElement MakeSlowestAssetItem()
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Row;
            container.style.justifyContent = Justify.SpaceBetween;

            var addressLabel = new Label();
            addressLabel.name = "address";
            addressLabel.style.flexGrow = 1;
            container.Add(addressLabel);

            var timeLabel = new Label();
            timeLabel.name = "time";
            timeLabel.style.color = new Color(1f, 0.6f, 0.3f);
            container.Add(timeLabel);

            return container;
        }

        private void BindSlowestAssetItem(VisualElement element, int index)
        {
            var items = _slowestAssetsList.itemsSource as List<(string address, string typeName, float avgTime)>;
            if (items == null || index >= items.Count) return;

            var item = items[index];

            var addressLabel = element.Q<Label>("address");
            var timeLabel = element.Q<Label>("time");

            addressLabel.text = $"{item.address} ({item.typeName})";
            timeLabel.text = $"{item.avgTime * 1000:F0} ms";
        }

        #endregion

        #region Helpers

        private string GetTimeSince(DateTime time)
        {
            var span = DateTime.Now - time;

            if (span.TotalSeconds < 60)
                return $"{span.TotalSeconds:F0}s";
            if (span.TotalMinutes < 60)
                return $"{span.TotalMinutes:F0}m";
            return $"{span.TotalHours:F0}h";
        }

        private void UpdateLastUpdatedLabel()
        {
            _lastUpdatedLabel.text = $"Last Updated: {DateTime.Now:HH:mm:ss}";
        }

        private void UpdateStatusLabel()
        {
            if (EditorApplication.isPlaying)
            {
                _statusLabel.text = "Play Mode - Tracking Active";
                _statusLabel.style.color = new Color(0.3f, 1f, 0.3f);
            }
            else
            {
                _statusLabel.text = "Not in Play Mode";
                _statusLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            }
        }

        #endregion

        #region Event Handlers

        private void OnTrackerDataChanged()
        {
            RefreshCurrentTab();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            UpdateStatusLabel();

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // Start performance metric collection
                EditorApplication.update += CollectMetrics;
            }
            else if (state == PlayModeStateChange.ExitingPlayMode)
            {
                EditorApplication.update -= CollectMetrics;
            }
        }

        private void CollectMetrics()
        {
            _metrics.RecordSnapshot();
        }

        #endregion

        #region Actions

        private void ExportPerformanceReport()
        {
            var path = EditorUtility.SaveFilePanel("Export Performance Report", "", "addressable_report.csv", "csv");

            if (!string.IsNullOrEmpty(path))
            {
                var csv = _metrics.ExportToCSV();
                System.IO.File.WriteAllText(path, csv);
                EditorUtility.DisplayDialog("Export Complete", $"Report exported to:\n{path}", "OK");
            }
        }

        private void ResetSettings()
        {
            _logLevelDropdown.value = "Warnings and Errors";
            _autoRefreshToggle.value = true;
            _refreshIntervalSlider.value = 500;
            _simulateSlowToggle.value = false;
            _delaySlider.value = 500;
            _failureRateSlider.value = 0;

            _autoRefresh = true;
            _refreshInterval = 0.5f;
        }

        private void ResetStatistics()
        {
            if (EditorUtility.DisplayDialog("Reset Statistics",
                "Are you sure you want to reset all performance statistics?",
                "Yes", "Cancel"))
            {
                _tracker.ResetStats();
                _metrics.Clear();
                RefreshAllData();
            }
        }

        #endregion

        #region Fallback UI

        private void CreateFallbackUI()
        {
            _root = new VisualElement();
            _root.style.flexGrow = 1;
            _root.style.alignItems = Align.Center;
            _root.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check UXML file path.");
            label.style.fontSize = 14;
            label.style.color = Color.red;

            _root.Add(label);
            rootVisualElement.Add(_root);
        }

        #endregion
    }
}
