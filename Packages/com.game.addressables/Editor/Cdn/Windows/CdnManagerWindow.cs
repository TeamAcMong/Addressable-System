using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn.Windows.Tabs;

namespace AddressableManager.Editor.Cdn.Windows
{
    /// <summary>
    /// A single tab hosted by <see cref="CdnManagerWindow"/>. Design doc §5.9 - every tab is a thin
    /// view over the same services and Editor classes the CDN CLI drives; no business logic lives here.
    /// </summary>
    public interface ICdnManagerTab
    {
        /// <summary>Label shown on the tab strip, e.g. "Validator".</summary>
        string TabName { get; }

        /// <summary>
        /// Builds this tab's content. Called fresh every time the tab becomes active (including
        /// switching back to it), so a tab never needs its own "did my data go stale" tracking - it
        /// just reads live state each time it is built. A tab that owns something long-lived (a running
        /// server, a polling loop) should own that state in a separate object that outlives the
        /// VisualElement, and register a TrickleDown DetachFromPanelEvent callback on its root to stop
        /// listening when the view is torn down - not rely on a teardown hook from the shell.
        /// </summary>
        VisualElement CreateView();

        /// <summary>Called right after <see cref="CreateView"/>, once the view is attached under the shell. Use this to run the first refresh.</summary>
        void OnShown();
    }

    /// <summary>
    /// Tabbed shell window for the CDN subsystem (design doc §5.9-§5.10, task 0.9). Six tabs are planned
    /// across Phases 0-5 (Validator, Local Server, Update Preview, Build, Catalog Inspector, Runtime
    /// Monitor); this task wires up the shell and registers Validator only.
    /// </summary>
    public class CdnManagerWindow : EditorWindow
    {
        // NOT "Tools/..." - every window in this package lives under "Window/Addressable Manager/..."
        // (see AddressableManagerWindow.cs, LayoutRuleEditorWindow.cs, LayoutViewerWindow.cs).
        // "Tools/Addressable Manager/..." is reserved for non-window actions (see
        // AddressableRuleMenuItems.cs, LocalContentServerMenu in LocalContentServer.cs).
        //
        // This is the ONLY registration of this menu item. Do not add a second [MenuItem] for
        // "Window/Addressable Manager/CDN Manager" anywhere else in the package - this repo already has
        // an accidental double-registration of "Window/Addressable Manager/Layout Rule Editor"
        // (LayoutRuleEditorWindow.cs and AddressableRuleMenuItems.cs both declare it); don't add a third
        // instance of that pattern, here or elsewhere.
        [MenuItem("Window/Addressable Manager/CDN Manager")]
        public static void ShowWindow()
        {
            var window = GetWindow<CdnManagerWindow>();
            window.titleContent = new GUIContent("CDN Manager");
            // Design doc §5.10/task 5.10 requires the window to work resized down to 560px - start
            // there instead of growing into a minimum that later needs shrinking.
            window.minSize = new Vector2(560, 400);
            window.Show();
        }

        private readonly List<ICdnManagerTab> _tabs = new List<ICdnManagerTab>();
        private readonly List<ToolbarToggle> _tabToggles = new List<ToolbarToggle>();

        private Toolbar _tabStrip;
        private VisualElement _tabBody;
        private Label _headerContext;
        private int _activeTabIndex = -1;

        public void CreateGUI()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/CdnManagerWindow.uxml");

            VisualElement root;
            if (visualTree != null)
            {
                root = visualTree.CloneTree();
            }
            else
            {
                Debug.LogError("[CdnManagerWindow] Failed to load CdnManagerWindow.uxml. Creating fallback UI.");
                root = CreateFallbackUI();
            }

            rootVisualElement.Add(root);

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/CdnManagerWindow.uss");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            _tabStrip = root.Q<Toolbar>("cdn-tab-strip");
            _tabBody = root.Q<VisualElement>("cdn-tab-body");
            _headerContext = root.Q<Label>("cdn-header-context");

            if (_headerContext != null)
                _headerContext.text = EditorUserBuildSettings.activeBuildTarget.ToString();

            RegisterTabs();
            BuildTabStrip();

            if (_tabs.Count > 0)
                SelectTab(0);
        }

        /// <summary>
        /// Tab registration point. Tasks 0.9-0.10 register Validator and Local Server. Append new tabs
        /// here as later tasks land - do not replace this method's contents wholesale, and do not
        /// register any tab anywhere else:
        ///   task 4.6   Runtime Monitor tab
        ///   task 5.9   Catalog Inspector tab
        ///
        /// The list itself lives in <see cref="CreateTabs"/> so the batchmode smoke check
        /// (CdnTabProbeCLI) exercises exactly the tabs this window shows. A second hand-written list
        /// would drift, and the tab it forgot would be the one nobody ever probed.
        /// </summary>
        private void RegisterTabs()
        {
            _tabs.Clear();
            _tabs.AddRange(CreateTabs());
        }

        /// <summary>
        /// The registered tabs, in display order. Single source of truth — see <see cref="RegisterTabs"/>.
        /// </summary>
        internal static List<ICdnManagerTab> CreateTabs()
        {
            return new List<ICdnManagerTab>
            {
                new SettingsValidatorTab(),
                new LocalServerTab(),
                new UpdatePreviewTab(),
                new BuildTab(),
                new RuntimeMonitorTab()
            };
        }

        private void BuildTabStrip()
        {
            _tabStrip.Clear();
            _tabToggles.Clear();

            for (int i = 0; i < _tabs.Count; i++)
            {
                int index = i; // capture for the closure below
                var toggle = new ToolbarToggle
                {
                    text = _tabs[i].TabName,
                    name = $"cdn-tab-{index}"
                };
                toggle.AddToClassList("cdn-tab-toggle");
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue)
                    {
                        SelectTab(index);
                    }
                    else if (_activeTabIndex == index)
                    {
                        // Exactly one tab must stay active - undo an attempt to toggle the active one off.
                        toggle.SetValueWithoutNotify(true);
                    }
                });

                _tabStrip.Add(toggle);
                _tabToggles.Add(toggle);
            }
        }

        private void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;

            _activeTabIndex = index;
            for (int i = 0; i < _tabToggles.Count; i++)
                _tabToggles[i].SetValueWithoutNotify(i == index);

            _tabBody.Clear();
            var view = _tabs[index].CreateView();
            _tabBody.Add(view);
            _tabs[index].OnShown();
        }

        private VisualElement CreateFallbackUI()
        {
            var fallback = new VisualElement();
            fallback.style.flexGrow = 1;
            fallback.style.alignItems = Align.Center;
            fallback.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check that Editor/Cdn/UI/CdnManagerWindow.uxml exists.");
            label.style.color = Color.red;
            fallback.Add(label);

            return fallback;
        }
    }
}
