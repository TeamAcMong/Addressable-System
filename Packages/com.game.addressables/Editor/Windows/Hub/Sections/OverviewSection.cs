using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The screen a fresh window opens on: how far content gets, and what stops it.
    /// </summary>
    /// <remarks>
    /// <b>It owns no findings of its own.</b> Everything on the "what needs you" list comes from
    /// asking each section for its <see cref="IHubSection.GetHealth"/>, which is the only way this
    /// screen stays true as sections gain real probes. A hand-maintained list of "things that can be
    /// wrong" would drift from the sections within one release, and this package has paid for that
    /// shape of mistake more than once.
    ///
    /// Deliberately not a dashboard of everything: it answers where the pipeline stops and hands
    /// off. Anything needing its own controls belongs in the section that owns it.
    /// </remarks>
    public sealed class OverviewSection : IHubSection, IHubHostAware
    {
        private IHubHost _host;
        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Overview;

        /// <inheritdoc />
        public string Title => "Overview";

        /// <inheritdoc />
        public string Subtitle => "Where content stands, and what is stopping it";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Configure;

        /// <inheritdoc />
        public void Bind(IHubHost host) => _host = host;

        /// <summary>
        /// The overview never carries a badge of its own: it summarises the other sections, so a
        /// state here would double-count what they already report and put a second, competing
        /// verdict on the rail.
        /// </summary>
        public SectionHealth GetHealth() => SectionHealth.Ok();

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "overview-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown() => Rebuild();

        // ------------------------------------------------------------------

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            var health = CollectHealth();

            _body.Add(BuildPipelineStrip(health));
            _body.Add(BuildStatCards(health));
            _body.Add(BuildTodoList(health));
            _body.Add(BuildHonestyNote());
        }

        /// <summary>Every section's current verdict, in rail order.</summary>
        private List<KeyValuePair<IHubSection, SectionHealth>> CollectHealth()
        {
            var result = new List<KeyValuePair<IHubSection, SectionHealth>>();
            if (_host == null) return result;

            foreach (var section in _host.Sections)
            {
                if (section == this) continue;
                result.Add(new KeyValuePair<IHubSection, SectionHealth>(section, section.GetHealth()));
            }

            return result;
        }

        // ---------------------------------------------------------------- pipeline

        /// <summary>
        /// The pipeline, horizontally, with the connector between stages coloured by whether content
        /// actually gets that far.
        /// </summary>
        /// <remarks>
        /// The connector is the whole point of drawing this rather than listing five words. A run of
        /// solid links that goes dead at one node says "it stops here" faster than any sentence, and
        /// it is the same information the rail carries, in the shape a person reads first.
        /// </remarks>
        private VisualElement BuildPipelineStrip(List<KeyValuePair<IHubSection, SectionHealth>> health)
        {
            var worst = WorstByStage(health);

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var header = new VisualElement();
            header.AddToClassList("hub-card-header");
            var title = new Label("How far content gets right now");
            title.AddToClassList("hub-card-title");
            header.Add(title);
            card.Add(header);

            var strip = new VisualElement();
            strip.AddToClassList("hub-flow");

            // A stage is "reached" while everything before it is clear. The first blocked stage ends
            // the run, and every link after it is drawn dead - which is exactly what happens to the
            // content.
            bool reachable = true;

            for (int i = 0; i < PipelineStages.All.Length; i++)
            {
                var stage = PipelineStages.All[i];
                var state = worst.TryGetValue(stage, out var s) ? s : HealthState.NotMeasured;

                var cell = new VisualElement();
                cell.AddToClassList("hub-flow-cell");

                var nodeRow = new VisualElement();
                nodeRow.AddToClassList("hub-flow-noderow");

                var left = new VisualElement();
                left.AddToClassList("hub-flow-link");
                if (i == 0) left.style.opacity = 0;
                else if (!reachable) left.AddToClassList("hub-flow-link--dead");
                nodeRow.Add(left);

                var node = new VisualElement();
                node.AddToClassList("hub-flow-node");
                ApplyState(node, state);
                nodeRow.Add(node);

                bool blocksHere = state == HealthState.Blocked;
                if (blocksHere) reachable = false;

                var right = new VisualElement();
                right.AddToClassList("hub-flow-link");
                if (i == PipelineStages.All.Length - 1) right.style.opacity = 0;
                else if (!reachable) right.AddToClassList("hub-flow-link--dead");
                nodeRow.Add(right);

                cell.Add(nodeRow);

                var label = new Label(PipelineStages.Label(stage));
                label.AddToClassList("hub-flow-label");
                cell.Add(label);

                var note = new Label(StageNote(stage, state, worst.ContainsKey(stage)));
                note.AddToClassList("hub-flow-note");
                ApplyState(note, state);
                cell.Add(note);

                strip.Add(cell);
            }

            card.Add(strip);
            return card;
        }

        private static string StageNote(PipelineStage stage, HealthState state, bool hasSections)
        {
            if (!hasSections) return "not built yet";

            switch (state)
            {
                case HealthState.Ok:      return "clear";
                case HealthState.Warning: return "check it";
                case HealthState.Blocked: return "stops here";
                default:                  return "not measured";
            }
        }

        private static Dictionary<PipelineStage, HealthState> WorstByStage(
            List<KeyValuePair<IHubSection, SectionHealth>> health)
        {
            var worst = new Dictionary<PipelineStage, HealthState>();

            foreach (var pair in health)
            {
                var stage = pair.Key.Stage;
                if (!worst.TryGetValue(stage, out var current))
                    current = HealthState.Ok;

                worst[stage] = SectionHealth.Worse(current, pair.Value.State);
            }

            return worst;
        }

        // ---------------------------------------------------------------- stat cards

        private VisualElement BuildStatCards(List<KeyValuePair<IHubSection, SectionHealth>> health)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-stats");

            int blocked = 0, warned = 0, unmeasured = 0;
            foreach (var pair in health)
            {
                switch (pair.Value.State)
                {
                    case HealthState.Blocked:     blocked++; break;
                    case HealthState.Warning:     warned++; break;
                    case HealthState.NotMeasured: unmeasured++; break;
                }
            }

            row.Add(StatCard("NEEDS ACTION",
                (blocked + warned).ToString(),
                blocked + warned == 1 ? "item" : "items",
                blocked > 0 ? HealthState.Blocked : warned > 0 ? HealthState.Warning : HealthState.Ok,
                $"{health.Count} screen(s) report a verdict"));

            row.Add(StatCard("NOT MEASURED",
                unmeasured.ToString(),
                unmeasured == 1 ? "screen" : "screens",
                unmeasured > 0 ? HealthState.NotMeasured : HealthState.Ok,
                unmeasured > 0 ? "Nothing here can vouch for these" : "Every screen reported"));

            row.Add(BuildOutputCard());
            row.Add(BuildModeCard());

            return row;
        }

        /// <summary>
        /// What is on disk from the last build, read from the build manifest.
        /// </summary>
        /// <remarks>
        /// Reads the artifact rather than asking a service, because the artifact is what a CDN would
        /// actually be given. No manifest is <see cref="HealthState.NotMeasured"/>, never zero: "no
        /// build has run" and "a build produced nothing" are different facts and only one of them is
        /// a problem.
        /// </remarks>
        private static VisualElement BuildOutputCard()
        {
            try
            {
                string path = System.IO.Path.Combine(
                    BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName);

                if (!System.IO.File.Exists(path))
                {
                    return StatCard("BUILT CONTENT", "—", "no manifest", HealthState.NotMeasured,
                        "No build has written one in this workspace");
                }

                var info = new System.IO.FileInfo(path);
                var age = System.DateTime.Now - info.LastWriteTime;
                string ageText = age.TotalHours >= 1
                    ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalMinutes}m ago";

                return StatCard("BUILT CONTENT", "✓", "manifest", HealthState.Ok, $"Written {ageText}");
            }
            catch (System.Exception ex)
            {
                return StatCard("BUILT CONTENT", "—", "unreadable", HealthState.NotMeasured, ex.Message);
            }
        }

        private static VisualElement BuildModeCard()
        {
            bool localOnly = CdnBuildModes.IsLocalOnly;

            return StatCard(
                "DELIVERY",
                localOnly ? "In-app" : "CDN",
                localOnly ? "local-only" : "remote",
                HealthState.Ok,
                localOnly
                    ? "The remote half of the pipeline is skipped, not failing"
                    : "Content is published and fetched at runtime");
        }

        private static VisualElement StatCard(
            string caption, string value, string unit, HealthState state, string footnote)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-stat");

            var cap = new Label(caption);
            cap.AddToClassList("hub-stat-caption");
            card.Add(cap);

            var valueRow = new VisualElement();
            valueRow.AddToClassList("hub-stat-valuerow");

            var big = new Label(value);
            big.AddToClassList("hub-stat-value");
            ApplyState(big, state);
            valueRow.Add(big);

            var unitLabel = new Label(unit);
            unitLabel.AddToClassList("hub-stat-unit");
            valueRow.Add(unitLabel);

            card.Add(valueRow);

            var foot = new Label(footnote);
            foot.AddToClassList("hub-stat-foot");
            card.Add(foot);

            return card;
        }

        // ---------------------------------------------------------------- what needs you

        private VisualElement BuildTodoList(List<KeyValuePair<IHubSection, SectionHealth>> health)
        {
            var items = new List<KeyValuePair<IHubSection, SectionHealth>>();
            foreach (var pair in health)
                if (pair.Value.State != HealthState.Ok) items.Add(pair);

            // Worst first, and within that, earliest in the pipeline first - fixing the earliest
            // blocker is what unblocks the most.
            items.Sort((a, b) =>
            {
                int byState = StateOrder(b.Value.State).CompareTo(StateOrder(a.Value.State));
                return byState != 0 ? byState : a.Key.Stage.CompareTo(b.Key.Stage);
            });

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var header = new VisualElement();
            header.AddToClassList("hub-card-header");

            var title = new Label("What needs you");
            title.AddToClassList("hub-card-title");
            header.Add(title);

            var count = new Label(items.Count == 0
                ? "nothing outstanding"
                : "ordered by what unblocks the most");
            count.AddToClassList("hub-card-count");
            header.Add(count);

            card.Add(header);

            if (items.Count == 0)
            {
                var clear = new Label(
                    "Every screen that reports a verdict is clear. That is not the same as everything " +
                    "being fine — check the NOT MEASURED count above for what nobody has vouched for.");
                clear.AddToClassList("hub-rule-meta");
                clear.style.paddingLeft = 8;
                clear.style.paddingRight = 8;
                clear.style.paddingTop = 7;
                clear.style.paddingBottom = 7;
                card.Add(clear);
                return card;
            }

            foreach (var pair in items)
                card.Add(BuildTodoRow(pair.Key, pair.Value));

            return card;
        }

        private VisualElement BuildTodoRow(IHubSection section, SectionHealth health)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-rule");

            var bar = new VisualElement();
            bar.AddToClassList("hub-todo-bar");
            ApplyState(bar, health.State);
            row.Add(bar);

            var text = new VisualElement();
            text.AddToClassList("hub-rule-text");

            var headline = new Label(string.IsNullOrEmpty(health.Reason)
                ? $"{section.Title}: {health.Badge}"
                : health.Reason);
            headline.AddToClassList("hub-rule-headline");
            text.Add(headline);

            var where = new Label($"{PipelineStages.Label(section.Stage)}  ›  {section.Title}");
            where.AddToClassList("hub-rule-id");
            text.Add(where);

            row.Add(text);

            // What this is holding up. The most useful column on the screen and the one the old
            // tooling never had: a list of problems with no ordering leaves the reader to guess
            // which one to start with.
            var blocks = new Label(BlocksText(section.Stage, health.State));
            blocks.AddToClassList("hub-todo-blocks");
            row.Add(blocks);

            var actions = new VisualElement();
            actions.AddToClassList("hub-rule-actions");

            string id = section.Id;
            var open = new Button(() => _host?.Navigate(id)) { text = "Open" };
            open.AddToClassList("hub-btn");
            actions.Add(open);

            row.Add(actions);
            return row;
        }

        private static string BlocksText(PipelineStage stage, HealthState state)
        {
            if (state != HealthState.Blocked) return string.Empty;

            for (int i = 0; i < PipelineStages.All.Length; i++)
            {
                if (PipelineStages.All[i] != stage) continue;
                if (i + 1 >= PipelineStages.All.Length) return "blocks nothing after it";
                return $"blocks {PipelineStages.Label(PipelineStages.All[i + 1])}";
            }

            return string.Empty;
        }

        private static int StateOrder(HealthState s)
        {
            switch (s)
            {
                case HealthState.Blocked:     return 3;
                case HealthState.Warning:     return 2;
                case HealthState.NotMeasured: return 1;
                default:                      return 0;
            }
        }

        // ---------------------------------------------------------------- footer

        private static VisualElement BuildHonestyNote()
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var label = new Label(
                "A hollow node is not a pass. A filled node means this window measured the stage; a " +
                "hollow one means it did not — no asset to read, nothing running, or a check that has " +
                "not been built yet. The two are drawn differently on purpose: rendering them the same " +
                "is how a CDN nobody had ever reached read as healthy.");
            label.AddToClassList("hub-note-text");
            box.Add(label);

            return box;
        }

        private static void ApplyState(VisualElement element, HealthState state)
        {
            if (element == null) return;

            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);

            element.AddToClassList(SectionHealth.StyleClassFor(state));
        }
    }
}
