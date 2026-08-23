using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Data;
using AddressableManager.Configs;
using AddressableManager.Managers;
using AddressableManager.Monitoring;

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
        private MemoryGraphView _memoryGraph;
        private Label _memoryGraphSummary;

        // UI Elements - Scopes Tab
        //
        // Keyed by the LIVE, instance-qualified scope id (AssetTrackerService.TrackedScopes'
        // key — e.g. "Scene-MainScene#h1234"), never a fixed category literal. Scope ids are
        // dynamic and unbounded since v4.0.0 (HANDOFF_TO_SESSION_B.md E-CHAIN item 3), so a
        // fixed 4-entry dictionary keyed by "Global"/"Session"/"Scene"/"Hierarchy" can only ever
        // show real data for Global/Hybrid scopes — every Scene/Hierarchy scope's assets would
        // silently read as 0 regardless of any other fix. Entries are created lazily in
        // RefreshScopesTab as new scope ids appear, and persist for the life of the window so a
        // Foldout's expanded/collapsed state survives across refresh ticks.
        private ScrollView _scopesScroll;
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

        // No shortcut here any more: Ctrl+Alt+A opens the hub, which is the front door now.
        // Two menu items claiming one chord is a race Unity does not arbitrate, and this project had
        // THREE of them at one point.
        [MenuItem("Window/Addressable Manager/Dashboard", priority = 210)]
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
                "Packages/com.game.addressables/Editor/UI/AddressableManagerWindow.uxml");

            if (visualTree != null)
            {
                _root = visualTree.CloneTree();
                rootVisualElement.Add(_root);

                // Task 4.8. One compact row rather than a sixth tab: this dashboard already carries
                // enough, and the CDN work has its own window. The strip answers "which environment
                // and how much cache" at a glance and hands off to CdnManagerWindow for anything more.
                rootVisualElement.Insert(0, CreateCdnStatusStrip());
            }
            else
            {
                Debug.LogError("[AddressableManager] Failed to load UXML file. Creating fallback UI.");
                CreateFallbackUI();
                return;
            }

            // Load USS
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/UI/Styles.uss");

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

            // Real choices are populated per-refresh by UpdateScopeFilterChoices() from the live
            // AssetTrackerService.TrackedScopes keys — a fixed literal list here (as before) can
            // only ever match Global/Hybrid scopes, since Scene/Hierarchy scope ids are
            // instance-qualified and unbounded (HANDOFF_TO_SESSION_B.md E-CHAIN item 3).
            _scopeFilter.choices = new List<string> { "All" };
            _scopeFilter.value = "All";

            // Performance Tab
            _statTotalValue = _root.Q<Label>("stat-total-value");
            _statCacheValue = _root.Q<Label>("stat-cache-value");
            _statMemoryValue = _root.Q<Label>("stat-memory-value");
            _statLoadValue = _root.Q<Label>("stat-load-value");
            _slowestAssetsList = _root.Q<ListView>("slowest-assets-list");
            _exportBtn = _root.Q<Button>("export-btn");

            // Initialize Memory Graph
            InitializeMemoryGraph();

            // Scopes Tab
            InitializeScopeElements();

            // Settings Tab
            _logLevelDropdown = _root.Q<DropdownField>("log-level-dropdown");
            _autoRefreshToggle = _root.Q<Toggle>("auto-refresh-toggle");
            _refreshIntervalSlider = _root.Q<SliderInt>("refresh-interval-slider");
            _simulateSlowToggle = _root.Q<Toggle>("simulate-slow-toggle");
            _delaySlider = _root.Q<SliderInt>("delay-slider");
            _failureRateSlider = _root.Q<Slider>("failure-rate-slider");

            _logLevelDropdown.choices = LogLevelChoices;
            _logLevelDropdown.value = LogLevelToChoice(DebugSettings.Instance.logLevel);

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
            _scopesScroll = _root.Q<ScrollView>("scopes-scroll");
            _scopeElements = new Dictionary<string, (Foldout, Label, ListView, Button)>();

            // The UXML ships four illustrative Foldouts (Global/Session/Scene/Hierarchy) as a
            // static preview for the UI Builder. Real scope ids are dynamic and instance-qualified
            // (HANDOFF_TO_SESSION_B.md E-CHAIN item 3) and can never match those literals, so clear
            // them out here and build the real entries dynamically in RefreshScopesTab/
            // GetOrCreateScopeFoldout instead.
            _scopesScroll?.Clear();

            var cleanupAllBtn = _root.Q<Button>("cleanup-all-btn");
            cleanupAllBtn.clicked += () =>
            {
                bool live = Application.isPlaying;
                string body = live
                    ? "Release every asset held by every tracked scope, and clear the Dashboard's rows?\n\n" +
                      "This releases real runtime handles. Anything still using those assets will break."
                    : "Clear the Dashboard's tracking rows for every scope?\n\n" +
                      "Nothing is released: there are no live scopes outside Play Mode, so this only " +
                      "resets what the Dashboard is showing.";

                if (EditorUtility.DisplayDialog("Cleanup All Scopes", body, "Yes", "Cancel"))
                {
                    foreach (var scopeId in _tracker.TrackedScopes.Keys.ToList())
                    {
                        ReleaseScope(scopeId);
                        _tracker.ClearScope(scopeId);
                    }
                    RefreshScopesTab();
                }
            };
        }

        /// <summary>
        /// Best-effort category label derived from a live scope id's own naming convention
        /// (BaseAssetScope / HybridScope / ScopeManager), used only for grouping/display next to
        /// <see cref="AssetMonitorBridge.GetDisplayName"/>'s friendly label — never as a tracker
        /// lookup key (HANDOFF_TO_SESSION_B.md E-CHAIN item 3).
        /// </summary>
        private static string GetScopeCategory(string scopeId)
        {
            if (string.IsNullOrEmpty(scopeId)) return "Unknown";
            if (scopeId == "Global") return "Global";
            if (scopeId == "Session") return "Session"; // ScopeManager's own non-Hybrid entry
            if (scopeId.StartsWith("Hybrid:Global", StringComparison.Ordinal)) return "Global";
            if (scopeId.StartsWith("Hybrid:Session", StringComparison.Ordinal)) return "Session";
            if (scopeId.StartsWith("Hybrid:", StringComparison.Ordinal)) return "Hybrid";
            if (scopeId.StartsWith("Scene-", StringComparison.Ordinal)) return "Scene";
            if (scopeId.StartsWith("Hierarchy-", StringComparison.Ordinal)) return "Hierarchy";
            return "Custom";
        }

        /// <summary>
        /// Human-readable label for one dropdown choice: "All" as-is, otherwise
        /// DisplayName + category (e.g. "MainScene (Scene)"), falling back to just the category
        /// when no distinct display name was ever reported for this id.
        /// </summary>
        private static string FormatScopeChoice(string scopeId)
        {
            if (scopeId == "All" || string.IsNullOrEmpty(scopeId)) return scopeId;

            var displayName = AssetMonitorBridge.GetDisplayName(scopeId);
            var category = GetScopeCategory(scopeId);
            return displayName == scopeId ? category : $"{displayName} ({category})";
        }

        private void InitializeMemoryGraph()
        {
            // Create memory graph visual element
            _memoryGraph = new MemoryGraphView();
            _memoryGraph.name = "memory-graph";
            _memoryGraph.style.marginTop = 10;
            _memoryGraph.style.marginBottom = 10;
            _memoryGraph.style.marginLeft = 5;
            _memoryGraph.style.marginRight = 5;

            // Create summary label
            _memoryGraphSummary = new Label("Memory Graph - No data yet");
            _memoryGraphSummary.name = "memory-graph-summary";
            _memoryGraphSummary.style.unityTextAlign = TextAnchor.MiddleCenter;
            _memoryGraphSummary.style.marginTop = 5;
            _memoryGraphSummary.style.marginBottom = 5;

            // Find performance tab and add graph
            // Try to insert after stats section or before slowest assets list
            if (_tabPerformance != null)
            {
                // Find a good container to add it to
                var container = _tabPerformance.Q<VisualElement>("performance-content");
                if (container == null)
                {
                    // Fallback: add directly to performance tab
                    container = _tabPerformance;
                }

                // Add to container
                container.Add(_memoryGraphSummary);
                container.Add(_memoryGraph);
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

            // The Settings tab's remaining four widgets were queried into fields at initialisation and
            // then never read again - no callback, no polling, no write - and the window never loaded a
            // DebugSettings asset at all. So the log level, the slow-loading simulation and the failure
            // rate were pure decoration, while the tools guide told users this tab "controls the live
            // DebugSettings instance". They write to the asset now.
            _logLevelDropdown.RegisterValueChangedCallback(evt =>
            {
                var settings = DebugSettings.Instance;
                settings.logLevel = ChoiceToLogLevel(evt.newValue);
                PersistDebugSettings(settings);
            });

            _simulateSlowToggle.RegisterValueChangedCallback(evt =>
            {
                var settings = DebugSettings.Instance;
                settings.simulateSlowLoading = evt.newValue;
                PersistDebugSettings(settings);
            });

            _delaySlider.RegisterValueChangedCallback(evt =>
            {
                var settings = DebugSettings.Instance;
                settings.simulatedDelayMs = evt.newValue;
                PersistDebugSettings(settings);
            });

            _failureRateSlider.RegisterValueChangedCallback(evt =>
            {
                var settings = DebugSettings.Instance;
                settings.simulateFailureRate = evt.newValue;
                PersistDebugSettings(settings);
            });
        }

        /// <summary>The dropdown labels, in DebugSettings.LogLevel order.</summary>
        private static readonly List<string> LogLevelChoices =
            new List<string> { "None", "Errors Only", "Warnings and Errors", "All" };

        private static string LogLevelToChoice(DebugSettings.LogLevel level)
        {
            int index = (int)level;
            return index >= 0 && index < LogLevelChoices.Count
                ? LogLevelChoices[index]
                : LogLevelChoices[LogLevelChoices.Count - 1];
        }

        private static DebugSettings.LogLevel ChoiceToLogLevel(string choice)
        {
            int index = LogLevelChoices.IndexOf(choice);
            return index >= 0 ? (DebugSettings.LogLevel)index : DebugSettings.LogLevel.WarningsAndErrors;
        }

        /// <summary>
        /// Mark the settings asset dirty so an edit made here survives a domain reload.
        /// </summary>
        /// <remarks>
        /// DebugSettings.Instance falls back to a CreateInstance when no asset exists in the project;
        /// that in-memory copy has no path, and calling SetDirty on it is meaningless rather than
        /// harmful - the checked cast keeps it from being an error either way.
        /// </remarks>
        private static void PersistDebugSettings(DebugSettings settings)
        {
            if (settings == null) return;
            if (!AssetDatabase.Contains(settings)) return;

            EditorUtility.SetDirty(settings);
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

            UpdateScopeFilterChoices();

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

        /// <summary>
        /// Repopulates the Assets-tab scope dropdown from the live
        /// <see cref="AssetTrackerService.TrackedScopes"/> keys instead of the fixed
        /// "Global"/"Session"/"Scene"/"Hierarchy" literals it used to ship with — those can only
        /// ever match Global/Hybrid scopes, since Scene/Hierarchy ids are instance-qualified
        /// (HANDOFF_TO_SESSION_B.md E-CHAIN item 3). Choices are the raw scope ids (what
        /// TrackedAsset.ScopeName actually stores, so the equality filter below keeps working
        /// unmodified); <see cref="FormatScopeChoice"/> renders the friendly label instead.
        /// </summary>
        private void UpdateScopeFilterChoices()
        {
            var choices = new List<string> { "All" };
            choices.AddRange(_tracker.TrackedScopes.Keys.OrderBy(id => id, StringComparer.Ordinal));

            // Only touch `choices` when the live scope set actually changed - reassigning it
            // every refresh tick (this runs on the auto-refresh timer) would close an open
            // dropdown popup and reset scroll position underneath the user for no reason.
            if (_scopeFilter.choices == null || !_scopeFilter.choices.SequenceEqual(choices))
            {
                _scopeFilter.choices = choices;
                _scopeFilter.formatListItemCallback = FormatScopeChoice;
                _scopeFilter.formatSelectedValueCallback = FormatScopeChoice;

                // The previously selected scope disappeared (e.g. its scene unloaded) - fall
                // back to "All" instead of leaving the field pointing at a stale value that is
                // no longer in `choices`.
                if (!choices.Contains(_scopeFilter.value))
                {
                    _scopeFilter.SetValueWithoutNotify("All");
                }
            }
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

            // Update memory graph summary
            if (_memoryGraph != null && _memoryGraphSummary != null)
            {
                _memoryGraphSummary.text = _memoryGraph.GetSummary();

                // Show peak and average stats
                var peak = _memoryGraph.GetPeakMemory() / (1024f * 1024f);
                var avg = _memoryGraph.GetAverageMemory() / (1024f * 1024f);
                if (peak > 0)
                {
                    _memoryGraphSummary.text += $" | Peak: {peak:F1} MB | Avg: {avg:F1} MB";
                }
            }
        }

        /// <summary>
        /// Rebuilds each tracked scope's stats/asset list in place — one Foldout per LIVE scope
        /// id from <see cref="AssetTrackerService.TrackedScopes"/>, created lazily the first time
        /// a given id is seen (see <see cref="GetOrCreateScopeFoldout"/>) rather than a fixed
        /// 4-entry set. This is the Dashboard's real "Scopes" surface — the one place E-CHAIN
        /// item 4's DisplayName payload actually needs to land for a user to see it
        /// (HANDOFF_TO_SESSION_B.md §4.5; BaseScopeInspector already did this correctly for the
        /// per-object Inspector, this mirrors that pattern here).
        /// </summary>
        /// <summary>
        /// Release the live loader behind a tracked scope, when there is one.
        /// </summary>
        /// <remarks>
        /// The Cleanup buttons used to call only AssetTrackerService.ClearScope, which touches nothing
        /// but the Dashboard's own bookkeeping - it flips IsValid to false, zeroes ReferenceCount and
        /// empties the row list. No AssetLoader, no IAssetScope and no Addressables handle was involved,
        /// so every asset stayed loaded while the rows vanished and the memory figure dropped to zero.
        /// The dialog meanwhile said "This will release all tracked assets". A user cleaning up to free
        /// memory got a display that agreed with them and a process that had not freed anything.
        ///
        /// ScopeManager.ClearScope does the real work, so the button now calls it. Outside Play Mode
        /// there is nothing to release and the dialog says so instead of claiming otherwise.
        /// </remarks>
        private static void ReleaseScope(string scopeId)
        {
            if (!Application.isPlaying) return;
            if (string.IsNullOrEmpty(scopeId)) return;
            if (!ScopeManager.Instance.HasScope(scopeId)) return;

            ScopeManager.Instance.ClearScope(scopeId);
        }

        private void RefreshScopesTab()
        {
            if (_scopesScroll == null) return;

            foreach (var kvp in _tracker.TrackedScopes.OrderBy(s => GetScopeCategory(s.Key))
                         .ThenBy(s => s.Value.DisplayName, StringComparer.Ordinal))
            {
                var scopeId = kvp.Key;
                var scope = kvp.Value;
                var (foldout, statsLabel, listView, cleanupBtn) = GetOrCreateScopeFoldout(scopeId);

                var category = GetScopeCategory(scopeId);
                foldout.text = scope.DisplayName == scopeId
                    ? $"{category} Scope"
                    : $"{scope.DisplayName} ({category} Scope)";

                var assets = scope.Assets;
                var memory = assets.Sum(a => a.MemorySize);

                statsLabel.text = $"Assets: {assets.Count} | Memory: {memory / (1024f * 1024f):F2} MB" +
                                   (scope.IsActive ? "" : "  |  inactive");

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

        /// <summary>
        /// Returns the cached Foldout/stats/list/cleanup group for <paramref name="scopeId"/>,
        /// building and appending it to <see cref="_scopesScroll"/> the first time this id is
        /// seen. Reusing the cached elements (rather than clearing and rebuilding the scroll view
        /// every refresh) keeps a Foldout's expanded/collapsed state stable across the
        /// auto-refresh timer.
        /// </summary>
        private (Foldout foldout, Label stats, ListView list, Button cleanup) GetOrCreateScopeFoldout(string scopeId)
        {
            if (_scopeElements.TryGetValue(scopeId, out var existing))
            {
                return existing;
            }

            var foldout = new Foldout { value = false };
            foldout.AddToClassList("scope-foldout");

            var statsLabel = new Label();
            statsLabel.AddToClassList("scope-stats");

            var listView = new ListView();
            listView.AddToClassList("scope-asset-list");

            var cleanupBtn = new Button { text = "Cleanup" };
            cleanupBtn.AddToClassList("scope-button");
            cleanupBtn.clicked += () =>
            {
                var displayName = AssetMonitorBridge.GetDisplayName(scopeId);
                bool live = Application.isPlaying && ScopeManager.Instance.HasScope(scopeId);
                string body = live
                    ? $"Release every asset held by the {displayName} scope, and clear its Dashboard rows?\n\n" +
                      "This releases real runtime handles."
                    : $"Clear the Dashboard's tracking rows for {displayName}?\n\n" +
                      "Nothing is released - this scope has no live loader right now.";

                if (EditorUtility.DisplayDialog($"Cleanup {displayName} Scope", body, "Yes", "Cancel"))
                {
                    ReleaseScope(scopeId);
                    _tracker.ClearScope(scopeId);
                    RefreshScopesTab();
                }
            };

            foldout.Add(statsLabel);
            foldout.Add(listView);
            foldout.Add(cleanupBtn);

            _scopesScroll.Add(foldout);

            var entry = (foldout, statsLabel, listView, cleanupBtn);
            _scopeElements[scopeId] = entry;
            return entry;
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

            // Friendly label ("MainScene") next to the category ("Scene") rather than the raw
            // instance-qualified scope id ("Scene-MainScene#h1234") a user has no reason to parse
            // (HANDOFF_TO_SESSION_B.md E-CHAIN item 4).
            var displayName = AssetMonitorBridge.GetDisplayName(asset.ScopeName);
            var category = GetScopeCategory(asset.ScopeName);
            var scopeLabel = displayName == asset.ScopeName ? category : $"{displayName} ({category})";
            infoLabel.text = $"{asset.TypeName} • {scopeLabel} Scope • Loaded {GetTimeSince(asset.LoadTime)} ago";
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

            // Add memory sample to graph
            if (_memoryGraph != null && _tracker != null)
            {
                var allAssets = _tracker.TrackedAssets.Values.Where(a => a.IsValid).ToList();
                var totalMemory = allAssets.Sum(a => a.MemorySize);
                var cachedMemory = allAssets.Where(a => a.InitialRefCount > 0).Sum(a => a.MemorySize);
                var activeMemory = allAssets.Where(a => a.ReferenceCount > 0).Sum(a => a.MemorySize);

                var sample = new MemoryGraphView.MemorySample
                {
                    Timestamp = DateTime.Now,
                    TotalMemory = totalMemory,
                    CachedMemory = cachedMemory,
                    ActiveMemory = activeMemory,
                    AssetCount = allAssets.Count
                };

                _memoryGraph.AddSample(sample);
            }
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
            _logLevelDropdown.value = LogLevelToChoice(DebugSettings.Instance.logLevel);
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

        /// <summary>
        /// Compact CDN status row — task 4.8.
        /// </summary>
        /// <remarks>
        /// Built in code rather than added to the window's UXML on purpose: this file's UXML is
        /// shared with the rest of the dashboard, and threading a CDN row through it would couple
        /// two features that are otherwise independent. Rebuilt whenever the window is opened, so
        /// there is no staleness to manage.
        ///
        /// Reads live state only in play mode. Outside it the runtime has nothing to report, and
        /// showing the last known values would be worse than showing none.
        /// </remarks>
        private VisualElement CreateCdnStatusStrip()
        {
            var strip = new VisualElement();
            strip.style.flexDirection = FlexDirection.Row;
            strip.style.alignItems = Align.Center;
            strip.style.paddingLeft = 6;
            strip.style.paddingRight = 6;
            strip.style.paddingTop = 3;
            strip.style.paddingBottom = 3;
            strip.style.borderBottomWidth = 1;
            strip.style.borderBottomColor = new StyleColor(new Color(0f, 0f, 0f, 0.25f));

            var label = new Label(DescribeCdnState());
            label.style.flexGrow = 1;
            label.style.overflow = Overflow.Hidden;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.tooltip = "CDN environment, catalog version and cache usage. Live values appear in play mode.";
            strip.Add(label);

            var open = new Button(() =>
                AddressableManager.Editor.Cdn.Windows.CdnManagerWindow.ShowWindow())
            {
                text = "CDN Manager"
            };
            open.style.minWidth = 100;
            strip.Add(open);

            // 1 Hz while the window is open; stopped on detach so a closed window does not keep
            // waking the editor.
            var poll = strip.schedule.Execute(() => label.text = DescribeCdnState()).Every(1000);
            strip.RegisterCallback<DetachFromPanelEvent>(_ => poll?.Pause(), TrickleDown.TrickleDown);

            return strip;
        }

        /// <summary>One line describing CDN state, or why there is none.</summary>
        private static string DescribeCdnState()
        {
            string version = UnityEditor.PlayerSettings.bundleVersion;

            if (!EditorApplication.isPlaying)
                return $"CDN  ·  app {version}  ·  not in play mode";

            if (!AddressableManager.Cdn.CdnManager.IsInitialized)
                return $"CDN  ·  app {version}  ·  not initialised";

            var cache = AddressableManager.Cdn.CdnManager.GetCacheStats();
            string cacheText = cache.IsValid
                ? $"cache {cache.OccupiedBytes / (1024 * 1024)} MB"
                : "cache not reported";

            return $"CDN  ·  {AddressableManager.Cdn.CdnManager.CurrentEnvironmentId}" +
                   $"  ·  app {version}  ·  {cacheText}" +
                   $"  ·  {AddressableManager.Cdn.CdnManager.NetworkState}";
        }

    }
}
