using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Layout rules, with a dry run in front of the apply.
    /// </summary>
    /// <remarks>
    /// The rule system had no preview at all. The only way to learn what "Apply All Rules" would do
    /// to a project's Addressables layout was to let it do it — on a shared project, that is an edit
    /// to every teammate's settings asset, discovered afterwards, with no undo that covers the whole
    /// run.
    ///
    /// So the primary action here is <b>Preview</b>, and Apply is reachable only past it. The preview
    /// runs the identical code path with the processor's dry-run flag set, so it cannot describe a
    /// run that will not happen: a second "what would occur" implementation is exactly how a preview
    /// drifts from the thing it previews.
    /// </remarks>
    public sealed class RulesSection : IHubSection, IHubHostAware, IHubSectionActions
    {
        /// <summary>
        /// Three seconds between rule-set lookups.
        /// </summary>
        /// <remarks>
        /// GetHealth used to call FindRuleData on every tick, and FindRuleData is
        /// <c>AssetDatabase.FindAssets("t:LayoutRuleData")</c> - a project-wide search, once a second,
        /// for the lifetime of the window.
        /// </remarks>
        private readonly HealthThrottle _health = new HealthThrottle();

        private IHubHost _host;
        private VisualElement _body;
        private LayoutRuleProcessor.ProcessResult _preview;
        private LayoutRuleData _ruleData;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Rules;

        /// <inheritdoc />
        public string Title => "Layout Rules";

        /// <inheritdoc />
        public string Subtitle => "What gets an address, a label and a group";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Author;

        /// <inheritdoc />
        public void Bind(IHubHost host) => _host = host;

        /// <inheritdoc />
        public SectionHealth GetHealth() => _health.Get(ComputeHealth);

        private SectionHealth ComputeHealth()
        {
            if (AddressableAssetSettingsDefaultObject.Settings == null)
                return SectionHealth.NotMeasured("No AddressableAssetSettings asset, so rules cannot be evaluated.");

            var data = FindRuleData();
            if (data == null)
                return SectionHealth.NotMeasured("No LayoutRuleData asset in this project.");

            // The rule set is validated - cheap, no asset scan - but NOT previewed. A preview walks
            // every asset in the project, and the rail asks for health once a second; running that on
            // a timer would make the window unusable on a real project. The health here is therefore
            // about the rule set's own consistency, and the preview is an explicit action.
            var (valid, errors) = data.Validate();
            if (!valid)
            {
                return SectionHealth.Blocked(
                    errors.Count == 1 ? "1 invalid rule" : $"{errors.Count} invalid rules",
                    "The rule set fails validation, so a run would apply nothing at all.");
            }

            if (_preview == null)
                return SectionHealth.NotMeasured("No preview has been run, so nothing is known about what the rules would change.");

            if (_preview.Errors.Count > 0)
            {
                return SectionHealth.Blocked(
                    _preview.Errors.Count == 1 ? "1 collision" : $"{_preview.Errors.Count} collisions",
                    "The last preview found a duplicate address, which would make an asset unreachable at runtime.");
            }

            if (_preview.Warnings.Count > 0)
                return SectionHealth.Warning($"{_preview.Warnings.Count} to read");

            return _preview.Planned.Count == 0
                ? SectionHealth.Ok()
                : SectionHealth.Warning($"{_preview.Planned.Count} pending");
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "rules-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown()
        {
            // Opening the screen is the one moment the user is definitely looking, so spend the
            // lookup then rather than making them wait out the throttle.
            _health.Invalidate();
            Rebuild();
        }

        // ------------------------------------------------------------------

        /// <inheritdoc />
        /// <remarks>
        /// Disabled rather than hidden when there is no rule set. A control that vanishes leaves the
        /// reader wondering whether the feature exists at all; a disabled one with a tooltip says what
        /// is missing and how to get it.
        /// </remarks>
        public void PopulateHeaderActions(VisualElement container)
        {
            var data = FindRuleData();

            var import = new Button(() => ImportRules(data)) { text = "Import\u2026" };
            import.AddToClassList("hub-btn");
            import.SetEnabled(data != null);
            import.tooltip = data != null
                ? "Merge rules from a JSON file into this rule set."
                : "No LayoutRuleData asset in the project to import into.";
            container.Add(import);

            var export = new Button(() => ExportRules(data)) { text = "Export\u2026" };
            export.AddToClassList("hub-btn");
            export.SetEnabled(data != null);
            export.tooltip = data != null
                ? "Write this rule set to a JSON file."
                : "No LayoutRuleData asset in the project to export.";
            container.Add(export);
        }

        /// <summary>Merge a JSON rule file into the project's rule set.</summary>
        /// <remarks>
        /// Merge, never replace. Replacing would silently discard every rule the file does not mention,
        /// and a file picker gives the reader no way to see that list before it goes.
        /// </remarks>
        private void ImportRules(LayoutRuleData data)
        {
            if (data == null) return;

            string path = EditorUtility.OpenFilePanel("Import layout rules", Application.dataPath, "json");
            if (string.IsNullOrEmpty(path)) return;

            if (!RuleSerializer.ImportFromJson(data, path, mergeMode: true))
            {
                EditorUtility.DisplayDialog(
                    "Import failed",
                    "Nothing was imported from:\n\n" + path +
                    "\n\nThe Console has the reason.",
                    "OK");
                return;
            }

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Rebuild();
        }

        private void ExportRules(LayoutRuleData data)
        {
            if (data == null) return;

            string path = EditorUtility.SaveFilePanel(
                "Export layout rules", Application.dataPath, data.name + ".json", "json");
            if (string.IsNullOrEmpty(path)) return;

            if (!RuleSerializer.ExportToJson(data, path))
            {
                EditorUtility.DisplayDialog(
                    "Export failed",
                    "Nothing was written to:\n\n" + path +
                    "\n\nThe Console has the reason.",
                    "OK");
            }
        }

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            if (AddressableAssetSettingsDefaultObject.Settings == null)
            {
                _body.Add(Note("This project has no AddressableAssetSettings asset, so rules have nothing to write to."));
                return;
            }

            _ruleData = FindRuleData();
            if (_ruleData == null)
            {
                _body.Add(BuildNoRulesState());
                return;
            }

            // Two panes, as designed. The rule list and the dry run answer each other - "this rule
            // matched nothing" and "26 matched nothing" are the same fact from two directions - and
            // stacking them meant scrolling away from one to read the other on exactly the screen
            // where they need to be compared.
            var split = new VisualElement();
            split.AddToClassList("hub-split");

            var left = new VisualElement();
            left.AddToClassList("hub-split-left");
            left.Add(BuildRuleList());
            split.Add(left);

            var right = new VisualElement();
            right.AddToClassList("hub-split-right");
            right.Add(BuildPreviewPanel());
            split.Add(right);

            _body.Add(split);
        }

        private VisualElement BuildRuleList()
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label(_ruleData.name);
            title.AddToClassList("hub-card-title");
            head.Add(title);

            int total = _ruleData.AddressRules.Count + _ruleData.LabelRules.Count + _ruleData.VersionRules.Count;
            var count = new Label($"{total} rule(s)");
            count.AddToClassList("hub-card-count");
            head.Add(count);

            card.Add(head);

            AddRuleRows(card, "Address", _ruleData.AddressRules.Count, _ruleData.AddressRules.ConvertAll(r => (r.RuleName, r.Enabled)));
            AddRuleRows(card, "Label", _ruleData.LabelRules.Count, _ruleData.LabelRules.ConvertAll(r => (r.RuleName, r.Enabled)));
            AddRuleRows(card, "Version", _ruleData.VersionRules.Count, _ruleData.VersionRules.ConvertAll(r => (r.RuleName, r.Enabled)));

            var select = new Button(() =>
            {
                Selection.activeObject = _ruleData;
                EditorGUIUtility.PingObject(_ruleData);
            })
            { text = "Edit rules" };
            select.AddToClassList("hub-btn");
            select.style.alignSelf = Align.FlexStart;
            select.style.marginLeft = 8;
            select.style.marginTop = 5;
            select.style.marginBottom = 6;
            card.Add(select);

            return card;
        }

        private static void AddRuleRows(
            VisualElement card, string kind, int count, List<(string Name, bool Enabled)> rules)
        {
            if (count == 0) return;

            foreach (var rule in rules)
            {
                var row = new VisualElement();
                row.AddToClassList("hub-prow");

                var kindLabel = new Label(kind);
                kindLabel.AddToClassList("hub-pcol");
                kindLabel.style.width = 60;
                kindLabel.style.flexShrink = 0;
                row.Add(kindLabel);

                var name = new Label(string.IsNullOrEmpty(rule.Name) ? "(unnamed)" : rule.Name);
                name.AddToClassList("hub-pcol");
                name.style.flexGrow = 1;
                row.Add(name);

                // A disabled rule is shown, not hidden. "Why is this asset not being addressed" is
                // most often answered by a rule that is switched off, and a list that omits them
                // cannot answer it.
                var state = new Label(rule.Enabled ? string.Empty : "disabled");
                state.AddToClassList("hub-pcol");
                state.style.width = 70;
                state.style.flexShrink = 0;
                if (!rule.Enabled) ApplyText(state, HealthState.Warning);
                row.Add(state);

                card.Add(row);
            }
        }

        private VisualElement BuildPreviewPanel()
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label(_preview == null
                ? "Nothing has been previewed"
                : "Dry run — nothing has been written");
            title.AddToClassList("hub-card-title");
            if (_preview == null) ApplyText(title, HealthState.NotMeasured);
            head.Add(title);

            card.Add(head);

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 6;
            body.style.paddingBottom = 7;

            if (_preview == null)
            {
                var blurb = new Label(
                    "A preview walks every asset in the project and works out exactly what the rules " +
                    "would write, without writing any of it. It is not run automatically because on a " +
                    "large project it is not cheap — and a check that runs on a timer is one nobody " +
                    "reads.");
                blurb.AddToClassList("hub-rule-meta");
                body.Add(blurb);
            }
            else
            {
                body.Add(SummaryRow("Rules run", $"{RuleTotal()}"));
                body.Add(SummaryRow("Assets examined", $"{_preview.TotalAssetsProcessed}"));
                body.Add(SummaryRow("Would change", $"{_preview.Planned.Count}",
                    _preview.Planned.Count > 0 ? HealthState.Warning : HealthState.Ok));

                foreach (var kind in new[]
                {
                    LayoutRuleProcessor.ChangeKind.CreateEntry,
                    LayoutRuleProcessor.ChangeKind.MoveGroup,
                    LayoutRuleProcessor.ChangeKind.Address,
                    LayoutRuleProcessor.ChangeKind.AddLabel,
                    LayoutRuleProcessor.ChangeKind.RemoveLabel,
                })
                {
                    int n = _preview.Planned.FindAll(c => c.Kind == kind).Count;
                    if (n > 0) body.Add(SummaryRow("  " + Describe(kind), n.ToString()));
                }

                foreach (var collision in _preview.Collisions)
                    body.Add(BuildCollisionCard(collision));

                // Anything in Errors that is NOT a collision still has to be shown; the collisions
                // above are the subset that can be acted on, not the whole list.
                if (_preview.Errors.Count > _preview.Collisions.Count)
                    body.Add(BuildMessageList("Other errors", _preview.Errors, HealthState.Blocked));

                if (_preview.Warnings.Count > 0)
                    body.Add(BuildMessageList("Worth reading", _preview.Warnings, HealthState.Warning));

                if (_preview.Planned.Count > 0)
                    body.Add(BuildPlannedList());
            }

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.FlexEnd;
            actions.style.marginTop = 8;

            var preview = new Button(RunPreview) { text = _preview == null ? "Preview" : "Re-run preview" };
            preview.AddToClassList("hub-btn");
            actions.Add(preview);

            // Apply is reachable only past a preview, and only when the preview found something to
            // do and nothing that would break. Enabling it before anything has been checked is what
            // made this an all-or-nothing button before.
            bool canApply = _preview != null && _preview.Errors.Count == 0 && _preview.Planned.Count > 0;
            var apply = new Button(RunApply)
            {
                text = _preview == null
                    ? "Apply"
                    : $"Apply {_preview.Planned.Count} change(s)",
            };
            apply.AddToClassList("hub-btn");
            apply.SetEnabled(canApply);
            apply.tooltip = canApply
                ? "Writes the changes listed above to the Addressables settings."
                : _preview == null
                    ? "Run a preview first."
                    : _preview.Errors.Count > 0
                        ? "The preview found a duplicate address. Resolve it before applying."
                        : "The preview found nothing to change.";
            actions.Add(apply);

            body.Add(actions);
            card.Add(body);
            return card;
        }

        private VisualElement BuildPlannedList()
        {
            var box = new VisualElement();
            box.style.marginTop = 7;

            var head = new Label("What would change");
            head.AddToClassList("hub-card-title");
            box.Add(head);

            // Capped, and the cap is stated. A silent truncation reads as "that is all of them",
            // which is how a list of 12 shown out of 4000 becomes a wrong decision.
            const int cap = 40;
            int shown = 0;

            foreach (var change in _preview.Planned)
            {
                if (shown++ >= cap) break;

                var row = new VisualElement();
                row.AddToClassList("hub-prow");

                var kind = new Label(Describe(change.Kind));
                kind.AddToClassList("hub-pcol");
                kind.style.width = 92;
                kind.style.flexShrink = 0;
                row.Add(kind);

                var path = new Label(change.AssetPath);
                path.AddToClassList("hub-pcol");
                path.style.flexGrow = 1;
                path.tooltip = change.AssetPath;
                row.Add(path);

                var delta = new Label($"{change.From}  →  {change.To}");
                delta.AddToClassList("hub-pcol");
                delta.style.width = 240;
                delta.style.flexShrink = 0;
                delta.tooltip = $"{change.From}  →  {change.To}   ({change.RuleName})";
                row.Add(delta);

                box.Add(row);
            }

            if (_preview.Planned.Count > cap)
            {
                var more = new Label(
                    $"{_preview.Planned.Count - cap} further change(s) are not listed here. " +
                    "Apply writes all of them, not just the ones shown.");
                more.AddToClassList("hub-rule-meta");
                box.Add(more);
            }

            return box;
        }

        /// <summary>One duplicate address, with the two assets and something to do about it.</summary>
        /// <remarks>
        /// The design's rule for a finding is symptom, cause, action. A collision reported as a
        /// sentence in a list has the first two and not the third: the reader is told two assets
        /// collide and then has to go and find them by hand, on the screen that already knows exactly
        /// where they are.
        /// </remarks>
        internal static VisualElement BuildCollisionCard(LayoutRuleProcessor.AddressCollision collision)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");
            card.style.marginTop = 7;

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label($"Two assets would claim \u201c{collision.Address}\u201d");
            title.AddToClassList("hub-card-title");
            ApplyText(title, HealthState.Blocked);
            head.Add(title);
            card.Add(head);

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 6;
            body.style.paddingBottom = 8;

            var why = new Label(
                $"Both resolve through '{collision.RuleName}'. Addressables returns one location for " +
                "a key, so the other asset could never be loaded at runtime.");
            why.AddToClassList("hub-rule-meta");
            body.Add(why);

            body.Add(AssetLine(collision.FirstAsset));
            body.Add(AssetLine(collision.SecondAsset));

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 8;

            var ping = new Button(() => PingBoth(collision)) { text = "Ping both" };
            ping.AddToClassList("hub-btn");
            ping.style.marginLeft = 0;
            ping.tooltip = "Selects both assets in the Project window.";
            actions.Add(ping);

            var narrow = new Button(() => SelectRuleAsset()) { text = "Narrow the rule" };
            narrow.AddToClassList("hub-btn");
            narrow.tooltip = "Opens the rule set so the filter can be tightened.";
            actions.Add(narrow);

            body.Add(actions);
            card.Add(body);
            return card;
        }

        private static VisualElement AssetLine(string path)
        {
            var line = new Label("← " + path);
            line.AddToClassList("hub-rule-id");
            line.style.marginTop = 3;
            line.tooltip = path;
            return line;
        }

        private static void PingBoth(LayoutRuleProcessor.AddressCollision collision)
        {
            var objects = new List<UnityEngine.Object>();

            foreach (var path in new[] { collision.FirstAsset, collision.SecondAsset })
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) objects.Add(asset);
            }

            if (objects.Count == 0)
            {
                // Both paths came from a run that just walked them, so failing to load is worth a
                // word rather than a silent no-op on a button the user just pressed.
                Debug.LogWarning($"[Layout Rules] Neither '{collision.FirstAsset}' nor " +
                                 $"'{collision.SecondAsset}' could be loaded.");
                return;
            }

            Selection.objects = objects.ToArray();
            EditorGUIUtility.PingObject(objects[0]);
        }

        private static void SelectRuleAsset()
        {
            var data = FindRuleData();
            if (data == null) return;

            Selection.activeObject = data;
            EditorGUIUtility.PingObject(data);
        }

        private static VisualElement BuildMessageList(string title, List<string> messages, HealthState state)
        {
            var box = new VisualElement();
            box.style.marginTop = 7;

            var head = new Label($"{title} ({messages.Count})");
            head.AddToClassList("hub-card-title");
            ApplyText(head, state);
            box.Add(head);

            const int cap = 12;
            for (int i = 0; i < messages.Count && i < cap; i++)
            {
                var line = new Label("• " + messages[i]);
                line.AddToClassList("hub-rule-meta");
                box.Add(line);
            }

            if (messages.Count > cap)
            {
                var more = new Label($"…and {messages.Count - cap} more. The Console has all of them.");
                more.AddToClassList("hub-rule-id");
                box.Add(more);
            }

            return box;
        }

        private void RunPreview()
        {
            try
            {
                var processor = new LayoutRuleProcessor(_ruleData);
                _preview = processor.PreviewRules();

                Debug.Log($"[Layout Rules] Preview: {_preview.Planned.Count} change(s) would be written, " +
                          $"{_preview.Errors.Count} collision(s), {_preview.Warnings.Count} warning(s). " +
                          "Nothing was written.");

                // The rail's badge is derived from this preview, so it has to change the moment the
                // preview does - otherwise the badge keeps reporting the previous run for a few
                // seconds after the user watched this one finish.
                _health.Invalidate();
            }
            catch (Exception ex)
            {
                _preview = null;
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("The preview failed",
                    $"{ex.GetType().Name}: {ex.Message}\n\nNothing was written. The Console has the trace.",
                    "OK");
            }
            finally
            {
                Rebuild();
            }
        }

        private void RunApply()
        {
            if (_preview == null) return;

            bool go = EditorUtility.DisplayDialog(
                "Apply these changes?",
                $"{_preview.Planned.Count} change(s) will be written to the Addressables settings " +
                "asset, which is shared with everyone on this branch.\n\n" +
                "This is the run you just previewed.",
                "Apply", "Cancel");

            if (!go) return;

            try
            {
                var processor = new LayoutRuleProcessor(_ruleData);
                var result = processor.ApplyRules();

                // Report what the APPLY did, not what the preview predicted. If the two disagree,
                // that disagreement is the interesting fact and it belongs in the log rather than
                // being smoothed over by echoing the preview's numbers back.
                Debug.Log($"[Layout Rules] Applied: {result.AddressesApplied} address(es), " +
                          $"{result.LabelsApplied} label(s), {result.VersionsApplied} version(s). " +
                          $"Preview had predicted {_preview.Planned.Count} change(s).");

                // The APPLY's numbers, not the preview's. Where the two disagree that disagreement
                // is the interesting fact, and a history that echoed the prediction back would erase
                // exactly the evidence someone would want a week later.
                HubHistory.Record(
                    result.Errors.Count > 0 ? HistoryKind.Warning : HistoryKind.Ok,
                    "Layout rules applied",
                    $"{result.AddressesApplied} addr · {result.LabelsApplied} label");

                if (result.Errors.Count > 0)
                {
                    EditorUtility.DisplayDialog("Applied with errors",
                        $"{result.Errors.Count} error(s) occurred. The Console has them.", "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("The run failed", $"{ex.GetType().Name}: {ex.Message}", "OK");
            }
            finally
            {
                // Re-preview rather than clear. After an apply the honest state is "here is what is
                // still outstanding", and an empty panel would look like the run had not happened.
                RunPreview();
            }
        }

        private VisualElement BuildNoRulesState()
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var title = new Label("No layout rules in this project");
            title.AddToClassList("hub-card-title");
            box.Add(title);

            var body = new Label(
                "Rules give assets their addresses, labels and groups automatically as they are " +
                "imported, so nobody has to name them by hand. Without any, this stage does nothing " +
                "— which is a valid way to run a project, not a problem to fix.");
            body.AddToClassList("hub-note-text");
            box.Add(body);

            var create = new Button(() =>
                EditorApplication.ExecuteMenuItem("Assets/Create/Addressable Manager/Layout Rule Data"))
            { text = "Create a rule set" };
            create.AddToClassList("hub-btn");
            create.style.marginTop = 6;
            create.style.alignSelf = Align.FlexStart;
            box.Add(create);

            return box;
        }

        // ------------------------------------------------------------------ helpers

        private int RuleTotal() =>
            _ruleData.AddressRules.Count + _ruleData.LabelRules.Count + _ruleData.VersionRules.Count;

        private static string Describe(LayoutRuleProcessor.ChangeKind kind)
        {
            switch (kind)
            {
                case LayoutRuleProcessor.ChangeKind.CreateEntry:  return "make addressable";
                case LayoutRuleProcessor.ChangeKind.MoveGroup:    return "move group";
                case LayoutRuleProcessor.ChangeKind.Address:      return "address";
                case LayoutRuleProcessor.ChangeKind.AddLabel:     return "add label";
                case LayoutRuleProcessor.ChangeKind.RemoveLabel:  return "remove label";
                default:                                          return kind.ToString();
            }
        }

        private static VisualElement SummaryRow(string key, string value, HealthState? state = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingTop = 1;
            row.style.paddingBottom = 1;

            var k = new Label(key);
            k.AddToClassList("hub-pcol");
            k.style.width = 150;
            k.style.flexShrink = 0;
            row.Add(k);

            var v = new Label(value);
            v.AddToClassList("hub-pcol");
            if (state.HasValue) ApplyText(v, state.Value);
            row.Add(v);

            return row;
        }

        internal static LayoutRuleData FindRuleData()
        {
            var guids = AssetDatabase.FindAssets("t:LayoutRuleData");
            foreach (var guid in guids)
            {
                var data = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(AssetDatabase.GUIDToAssetPath(guid));
                if (data != null) return data;
            }

            return null;
        }

        private static VisualElement Note(string text)
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var label = new Label(text);
            label.AddToClassList("hub-note-text");
            box.Add(label);

            return box;
        }

        /// <summary>Colour a label by health state.</summary>
        /// <remarks>
        /// Delegates. This used to apply the dot classes and then clear style.backgroundColor inline
        /// to undo the half of them that does not belong on text - five sections carried a copy of
        /// that, and the copies had already drifted. HubStyle has the distinction instead.
        /// </remarks>
        private static void ApplyText(VisualElement element, HealthState state) =>
            HubStyle.Text(element, state);
    }
}
