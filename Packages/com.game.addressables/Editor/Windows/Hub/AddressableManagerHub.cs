using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The package's single Editor window: one shell, one rail, every screen inside it.
    /// </summary>
    /// <remarks>
    /// Replaces an arrangement of four windows, ten tabs and twenty-one menu items spread across
    /// four root menus, four of which were registered twice with the same path.
    ///
    /// <b>The rail is not a menu.</b> It is <see cref="PipelineStage"/> - Configure, Author, Build,
    /// Publish, Run - and each stage's colour is that stage's real health right now. So the window
    /// answers "how far does my content get, and what stops it" before anything is clicked. The old
    /// arrangement grouped by subsystem, which required the user to know which module owned a
    /// feature in order to find it, and made every real task cross two or three windows.
    ///
    /// The shell owns navigation, the rail, the section header and the status bar. It owns no
    /// business logic: every section is a thin view over the same services the CLI drives, exactly
    /// as the tab contract it replaces required.
    /// </remarks>
    public sealed class AddressableManagerHub : EditorWindow, IHubHost
    {
        private const string UxmlPath =
            "Packages/com.game.addressables/Editor/Windows/Hub/UI/AddressableManagerHub.uxml";
        private const string UssPath =
            "Packages/com.game.addressables/Editor/Windows/Hub/UI/AddressableManagerHub.uss";

        /// <summary>
        /// The section to show, remembered across domain reloads but not across Editor sessions.
        /// </summary>
        /// <remarks>
        /// <see cref="SessionState"/> rather than <see cref="EditorPrefs"/> on purpose: which screen
        /// you were on is worth surviving a script recompile - losing it mid-task is the single most
        /// irritating thing an Editor window does - but it is not worth restoring a week later, when
        /// it would reopen on a screen whose context is long gone.
        /// </remarks>
        private const string ActiveSectionKey = "AddressableManager.Hub.ActiveSection";

        /// <summary>How often the rail re-derives health while the window is open.</summary>
        /// <remarks>
        /// One second. Health probes are contractually cheap (see <see cref="IHubSection.GetHealth"/>),
        /// and this is the same cadence the Runtime Monitor already polls at.
        /// </remarks>
        private const double HealthRefreshSeconds = 1.0;

        private readonly List<IHubSection> _sections = new List<IHubSection>();
        private readonly Dictionary<string, VisualElement> _sectionRows =
            new Dictionary<string, VisualElement>(StringComparer.Ordinal);
        private readonly Dictionary<PipelineStage, VisualElement> _stageDots =
            new Dictionary<PipelineStage, VisualElement>();
        private readonly Dictionary<PipelineStage, Label> _stageBadges =
            new Dictionary<PipelineStage, Label>();

        /// <summary>
        /// Every connector segment on the rail, in top-to-bottom order, tagged with the stage it
        /// sits under.
        /// </summary>
        /// <remarks>
        /// A list rather than a lookup because the order IS the meaning: segments up to the first
        /// blocked stage are drawn live, everything after it is drawn dead. That is the same fact
        /// the overview's horizontal strip shows, in the shape the rail can carry.
        /// </remarks>
        private readonly List<KeyValuePair<PipelineStage, VisualElement>> _railLines =
            new List<KeyValuePair<PipelineStage, VisualElement>>();

        private VisualElement _railStages;
        private VisualElement _sectionBody;
        private VisualElement _sectionActions;
        private VisualElement _blocker;
        private VisualElement _statusDot;
        private Label _headerContext;
        private Label _sectionTitle;
        private Label _sectionSubtitle;
        private Label _blockerTitle;
        private Label _blockerBody;
        private Label _statusText;
        private Label _statusContext;

        private HubPalette _palette;
        private string _activeSectionId;
        private double _nextHealthRefresh;

        /// <summary>Open the window, restoring whichever section was last shown.</summary>
        [MenuItem("Window/Addressable Manager/Open %&a", priority = 100)]
        public static void Open() => Open(null);

        /// <summary>
        /// Open the window at a particular section.
        /// </summary>
        /// <param name="sectionId">
        /// A value from <see cref="HubSections.Ids"/>, or null to restore the remembered one.
        /// An id that does not match a registered section falls back to the first section and logs -
        /// silently landing somewhere else would make a broken deep link indistinguishable from a
        /// working one.
        /// </param>
        public static void Open(string sectionId)
        {
            var window = GetWindow<AddressableManagerHub>();
            window.titleContent = new GUIContent("Addressable Manager");

            // The design's rail is 196px and the content pane needs room for a two-column screen;
            // 620 is where that stops being cramped. Deliberately not larger: an Editor window with
            // a big minimum is one that cannot be docked usefully in a side column.
            window.minSize = new Vector2(620, 420);

            if (!string.IsNullOrEmpty(sectionId))
                window.RequestSection(sectionId);

            window.Show();
        }

        /// <summary>Open straight to the configuration checklist.</summary>
        /// <remarks>
        /// Deep links exist so the menu can stay small without hiding anything. Every one of them
        /// lands on a screen that is also reachable from the rail - the menu is the shortcut, the
        /// window is the home, and nothing lives only in a menu.
        /// </remarks>
        [MenuItem("Window/Addressable Manager/Validate Setup", priority = 101)]
        public static void OpenValidator() => Open(HubSections.Ids.Validator);

        /// <summary>Open straight to profile conformance.</summary>
        [MenuItem("Window/Addressable Manager/Profiles", priority = 102)]
        public static void OpenProfiles() => Open(HubSections.Ids.Profiles);

        /// <summary>Open straight to the content build screen.</summary>
        [MenuItem("Window/Addressable Manager/Build Content", priority = 103)]
        public static void OpenBuild() => Open(HubSections.Ids.Build);

        /// <summary>Navigate, whether or not the UI has been built yet.</summary>
        private void RequestSection(string sectionId)
        {
            SessionState.SetString(ActiveSectionKey, sectionId);
            if (_railStages != null)
                ShowSection(sectionId);
        }

        private void CreateGUI()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (tree == null)
            {
                ShowLoadFailure();
                return;
            }

            var root = tree.CloneTree();
            rootVisualElement.Add(root);

            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (sheet != null)
                root.styleSheets.Add(sheet);
            else
                Debug.LogWarning($"[AddressableManagerHub] Stylesheet missing at {UssPath}. " +
                                 "The window works but will be unstyled.");

            _railStages      = root.Q<VisualElement>("hub-rail-stages");
            _sectionBody     = root.Q<VisualElement>("hub-section-body");
            _sectionActions  = root.Q<VisualElement>("hub-section-actions");
            _blocker         = root.Q<VisualElement>("hub-rail-blocker");
            _statusDot       = root.Q<VisualElement>("hub-status-dot");
            _headerContext   = root.Q<Label>("hub-header-context");
            _sectionTitle    = root.Q<Label>("hub-section-title");
            _sectionSubtitle = root.Q<Label>("hub-section-subtitle");
            _blockerTitle    = root.Q<Label>("hub-rail-blocker-title");
            _blockerBody     = root.Q<Label>("hub-rail-blocker-body");
            _statusText      = root.Q<Label>("hub-status-text");
            _statusContext   = root.Q<Label>("hub-status-context");

            // Every name above must exist. A Q<> miss returns null and the handler that uses it
            // no-ops in silence - the exact way this package has shipped dead UI before, twice.
            if (_railStages == null || _sectionBody == null)
            {
                ShowLoadFailure();
                return;
            }

            _sections.Clear();
            _sections.AddRange(HubSections.Create());

            // Bind before anything is shown. The overview reads every other section's verdict, so
            // an unbound one would render an empty "nothing outstanding" - the most confident
            // possible way to say nothing was checked.
            foreach (var section in _sections)
            {
                if (section is IHubHostAware aware)
                    aware.Bind(this);
            }

            BuildRail();
            UpdateContext();

            string remembered = SessionState.GetString(ActiveSectionKey, null);
            ShowSection(!string.IsNullOrEmpty(remembered) ? remembered : HubSections.Ids.Overview);

            RefreshHealth();

            // The palette overlays the whole window, so it is attached to the cloned root rather
            // than to the content pane - it has to be able to dim the rail too.
            _palette = new HubPalette(this, root);

            // TrickleDown: the shortcut has to be seen before a focused TextField swallows the key.
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnShortcut, TrickleDown.TrickleDown);

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        /// <summary>Ctrl+K / Cmd+K opens the palette.</summary>
        private void OnShortcut(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.K) return;
            if (!evt.ctrlKey && !evt.commandKey) return;
            if (_palette == null) return;

            if (_palette.IsOpen) _palette.Close();
            else _palette.Open();

            evt.StopPropagation();
        }

        private void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextHealthRefresh) return;

            _nextHealthRefresh = now + HealthRefreshSeconds;
            RefreshHealth();
        }

        // ---------------------------------------------------------------- rail

        private void BuildRail()
        {
            _railStages.Clear();
            _sectionRows.Clear();
            _stageDots.Clear();
            _stageBadges.Clear();
            _railLines.Clear();

            foreach (var stage in PipelineStages.All)
            {
                var sections = _sections.FindAll(s => s.Stage == stage);

                // A stage with no sections is not drawn. Drawing an empty heading would advertise a
                // step of the pipeline the tool cannot actually show anything about.
                if (sections.Count == 0) continue;

                var header = new VisualElement { name = $"hub-stage-{stage}" };
                header.AddToClassList("hub-stage");

                // The gutter carries the dot AND the connector segment below it. Every row on the
                // rail contributes one segment, so the segments stack into a single line running the
                // height of the rail - which is what makes the rail read as a pipeline rather than
                // as a list of headings.
                var gutter = new VisualElement();
                gutter.AddToClassList("hub-gutter");

                var dot = new VisualElement();
                dot.AddToClassList("hub-stage-dot");
                gutter.Add(dot);
                _stageDots[stage] = dot;

                var line = new VisualElement();
                line.AddToClassList("hub-gutter-line");
                gutter.Add(line);
                _railLines.Add(new KeyValuePair<PipelineStage, VisualElement>(stage, line));

                header.Add(gutter);

                var label = new Label(PipelineStages.Label(stage).ToUpperInvariant());
                label.AddToClassList("hub-stage-label");
                header.Add(label);

                var badge = new Label(string.Empty);
                badge.AddToClassList("hub-stage-badge");
                header.Add(badge);
                _stageBadges[stage] = badge;

                _railStages.Add(header);

                foreach (var section in sections)
                    _railStages.Add(BuildSectionRow(section, stage));
            }
        }

        private VisualElement BuildSectionRow(IHubSection section, PipelineStage stage)
        {
            var row = new VisualElement { name = $"hub-row-{section.Id}" };
            row.AddToClassList("hub-section-row");

            var gutter = new VisualElement();
            gutter.AddToClassList("hub-gutter");

            var line = new VisualElement();
            line.AddToClassList("hub-gutter-line");
            gutter.Add(line);
            _railLines.Add(new KeyValuePair<PipelineStage, VisualElement>(stage, line));

            row.Add(gutter);

            var label = new Label(section.Title);
            label.AddToClassList("hub-section-row-label");
            row.Add(label);

            var dot = new VisualElement { name = "row-dot" };
            dot.AddToClassList("hub-section-row-dot");
            row.Add(dot);

            string id = section.Id;
            row.RegisterCallback<ClickEvent>(_ => ShowSection(id));

            _sectionRows[section.Id] = row;
            return row;
        }

        // ---------------------------------------------------------------- IHubHost

        /// <inheritdoc />
        public IReadOnlyList<IHubSection> Sections => _sections;

        /// <inheritdoc />
        public void Navigate(string sectionId) => ShowSection(sectionId);

        // ---------------------------------------------------------------- navigation

        private void ShowSection(string sectionId)
        {
            var section = _sections.Find(s => string.Equals(s.Id, sectionId, StringComparison.Ordinal));

            if (section == null)
            {
                // Loud, not silent. A deep link that quietly lands on the wrong screen is
                // indistinguishable from one that works, and this is how a renamed id gets shipped.
                Debug.LogWarning(
                    $"[AddressableManagerHub] No section with id '{sectionId}'. " +
                    $"Showing '{_sections[0].Id}' instead. Valid ids are on HubSections.Ids.");
                section = _sections[0];
            }

            _activeSectionId = section.Id;
            SessionState.SetString(ActiveSectionKey, _activeSectionId);

            foreach (var pair in _sectionRows)
            {
                if (pair.Key == _activeSectionId)
                    pair.Value.AddToClassList("hub-section-row--active");
                else
                    pair.Value.RemoveFromClassList("hub-section-row--active");
            }

            _sectionTitle.text = section.Title;
            _sectionSubtitle.text = section.Subtitle;
            _sectionActions.Clear();

            _sectionBody.Clear();

            // A section that throws while building must not take the shell with it: the rail has to
            // stay usable so the user can navigate away from the broken screen rather than closing
            // and reopening the window.
            try
            {
                var view = section.CreateView();
                if (view != null)
                {
                    view.style.flexGrow = 1;
                    _sectionBody.Add(view);
                }

                section.OnShown();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _sectionBody.Add(BuildSectionFailure(section, ex));
            }
        }

        private static VisualElement BuildSectionFailure(IHubSection section, Exception ex)
        {
            var box = new VisualElement();
            box.style.flexGrow = 1;
            box.style.paddingLeft = 12;
            box.style.paddingRight = 12;
            box.style.paddingTop = 12;

            var title = new Label($"'{section.Title}' could not be shown.");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            box.Add(title);

            var detail = new Label($"{ex.GetType().Name}: {ex.Message}\n\n" +
                                   "The full stack trace is in the Console. The rest of the window " +
                                   "still works — pick another screen on the left.");
            detail.style.whiteSpace = WhiteSpace.Normal;
            detail.style.marginTop = 6;
            box.Add(detail);

            return box;
        }

        // ---------------------------------------------------------------- health

        private void RefreshHealth()
        {
            if (_railStages == null) return;

            var worstByStage = new Dictionary<PipelineStage, HealthState>();
            var badgeByStage = new Dictionary<PipelineStage, string>();
            var blockedReasonByStage = new Dictionary<PipelineStage, string>();

            foreach (var section in _sections)
            {
                var health = section.GetHealth();

                if (_sectionRows.TryGetValue(section.Id, out var row))
                {
                    var dot = row.Q<VisualElement>("row-dot");
                    if (dot != null)
                    {
                        ApplyState(dot, health.State);

                        // An Ok section carries no dot at all: a rail speckled with green is noise,
                        // and the eye should be drawn only to what is not fine.
                        dot.style.display = health.State == HealthState.Ok
                            ? DisplayStyle.None
                            : DisplayStyle.Flex;
                    }

                    row.tooltip = string.IsNullOrEmpty(health.Reason) ? section.Subtitle : health.Reason;
                }

                var stage = section.Stage;
                if (!worstByStage.TryGetValue(stage, out var current))
                    current = HealthState.Ok;

                var worse = SectionHealth.Worse(current, health.State);
                worstByStage[stage] = worse;

                if (worse == health.State && !string.IsNullOrEmpty(health.Badge))
                    badgeByStage[stage] = health.Badge;

                if (health.State == HealthState.Blocked && !blockedReasonByStage.ContainsKey(stage))
                {
                    blockedReasonByStage[stage] = string.IsNullOrEmpty(health.Reason)
                        ? $"{section.Title} is blocking it."
                        : health.Reason;
                }
            }

            // The EARLIEST blocked stage, walked in pipeline order rather than in whatever order
            // sections happen to be registered in. Those two orders agree today, which is exactly why
            // this was worth fixing before they stop agreeing: taking the first blocked section out
            // of the registration list would, after one reordering, report a Publish failure as the
            // thing stopping content that never got past Configure.
            PipelineStage? firstBlocked = null;
            string firstBlockedReason = null;

            foreach (var stage in PipelineStages.All)
            {
                if (!worstByStage.TryGetValue(stage, out var state) || state != HealthState.Blocked)
                    continue;

                firstBlocked = stage;
                blockedReasonByStage.TryGetValue(stage, out firstBlockedReason);
                break;
            }

            foreach (var pair in _stageDots)
            {
                var state = worstByStage.TryGetValue(pair.Key, out var s) ? s : HealthState.NotMeasured;
                ApplyState(pair.Value, state);

                if (_stageBadges.TryGetValue(pair.Key, out var badge))
                {
                    badge.text = badgeByStage.TryGetValue(pair.Key, out var b) ? b : string.Empty;
                    ApplyState(badge, state);
                }
            }

            UpdateRailConnector(firstBlocked);
            UpdateBlocker(firstBlocked, firstBlockedReason);
            UpdateStatus(firstBlocked, firstBlockedReason);
        }

        /// <summary>
        /// Draw the rail's connector live down to the first blocked stage, and dead after it.
        /// </summary>
        /// <remarks>
        /// Content does not reach past a blocked stage, so neither should the line that represents
        /// it. Leaving the whole rail solid would say the pipeline runs end to end while the badge
        /// three rows up says it does not - two claims in one control, and the reader has to work
        /// out which to believe.
        /// </remarks>
        private void UpdateRailConnector(PipelineStage? blockedStage)
        {
            bool dead = false;

            foreach (var pair in _railLines)
            {
                if (blockedStage.HasValue && pair.Key == blockedStage.Value)
                    dead = true;

                if (dead)
                    pair.Value.AddToClassList("hub-gutter-line--dead");
                else
                    pair.Value.RemoveFromClassList("hub-gutter-line--dead");
            }
        }

        private void UpdateBlocker(PipelineStage? blockedStage, string reason)
        {
            if (_blocker == null) return;

            if (blockedStage == null)
            {
                _blocker.style.display = DisplayStyle.None;
                return;
            }

            _blocker.style.display = DisplayStyle.Flex;
            _blockerTitle.text = $"Pipeline stops at {PipelineStages.Label(blockedStage.Value)}";
            ApplyState(_blockerTitle, HealthState.Blocked);
            _blockerBody.text = reason;
        }

        private void UpdateStatus(PipelineStage? blockedStage, string reason)
        {
            if (_statusText == null) return;

            if (blockedStage == null)
            {
                _statusText.text = "Ready";
                ApplyState(_statusDot, HealthState.Ok);
            }
            else
            {
                _statusText.text = $"{PipelineStages.Label(blockedStage.Value)} is blocked — {reason}";
                ApplyState(_statusDot, HealthState.Blocked);
            }
        }

        private static void ApplyState(VisualElement element, HealthState state)
        {
            if (element == null) return;

            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);

            element.AddToClassList(SectionHealth.StyleClassFor(state));
        }

        // ---------------------------------------------------------------- chrome

        private void UpdateContext()
        {
            if (_headerContext == null) return;

            string target = EditorUserBuildSettings.activeBuildTarget.ToString();
            string profile = ActiveProfileName();
            string mode = Cdn.CdnBuildModes.IsLocalOnly ? "Local-only" : "Remote";

            // The three facts that change what every other screen means. They were spread across
            // three windows before, which is how a build went out against the wrong profile.
            _headerContext.text = $"{profile}   ·   {target}   ·   {mode}";

            if (_statusContext != null)
                _statusContext.text = $"{profile} · {target}";
        }

        /// <summary>The active Addressables profile, or an honest placeholder.</summary>
        private static string ActiveProfileName()
        {
            var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return "no settings";

            string name = settings.profileSettings.GetProfileName(settings.activeProfileId);
            return string.IsNullOrEmpty(name) ? "no profile" : name;
        }

        private void ShowLoadFailure()
        {
            rootVisualElement.Clear();

            var box = new VisualElement();
            box.style.flexGrow = 1;
            box.style.paddingLeft = 12;
            box.style.paddingTop = 12;

            var label = new Label(
                $"Failed to load the window layout from {UxmlPath}.\n\n" +
                "This usually means the package was imported without its Editor/Windows/Hub/UI " +
                "folder, or a .meta file was lost in a merge.");
            label.style.whiteSpace = WhiteSpace.Normal;
            box.Add(label);

            rootVisualElement.Add(box);
        }
    }
}
