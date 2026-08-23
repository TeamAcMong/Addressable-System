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
    /// Deliberately not a dashboard of everything. It answers one question - where does the pipeline
    /// stop - and hands off. Anything that would need its own controls belongs in the section that
    /// owns it, reachable from the rail.
    ///
    /// This first cut renders the pipeline and the honest state of what the shell can cheaply know.
    /// The per-stage probes land with their sections (build order steps 2-6); until a stage has one,
    /// it reports <see cref="HealthState.NotMeasured"/> with a reason rather than a green tick.
    /// </remarks>
    public sealed class OverviewSection : IHubSection
    {
        /// <inheritdoc />
        public string Id => HubSections.Ids.Overview;

        /// <inheritdoc />
        public string Title => "Overview";

        /// <inheritdoc />
        public string Subtitle => "Where content stands, and what is stopping it";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Configure;

        /// <summary>
        /// The overview never carries a badge of its own.
        /// </summary>
        /// <remarks>
        /// It summarises the other sections, so giving it a state would double-count whatever they
        /// already report and put a second, competing verdict on the rail.
        /// </remarks>
        public SectionHealth GetHealth() => SectionHealth.Ok();

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            var root = new ScrollView { name = "overview-root" };
            root.style.flexGrow = 1;

            var page = new VisualElement();
            page.style.paddingLeft = 10;
            page.style.paddingRight = 10;
            page.style.paddingTop = 10;
            page.style.paddingBottom = 10;
            root.Add(page);

            page.Add(BuildModeCard());
            page.Add(BuildStageList());
            page.Add(BuildHonestyNote());

            return root;
        }

        /// <inheritdoc />
        public void OnShown() { }

        // ------------------------------------------------------------------

        private static VisualElement BuildModeCard()
        {
            var card = Card();

            bool localOnly = CdnBuildModes.IsLocalOnly;

            var title = new Label(localOnly
                ? "This project ships all content inside the player"
                : "This project publishes content to a CDN");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            var body = new Label(localOnly
                ? "Build Mode is Local-only, so the remote half of the pipeline is skipped rather " +
                  "than reported as failing. Publish and Run show nothing to check, which is the " +
                  "honest answer and not a passing grade."
                : "Content is built to ServerData/, uploaded, and fetched at runtime. Every stage " +
                  "below has to be sound for a player to receive an update.");
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.marginTop = 3;
            body.style.opacity = 0.75f;
            card.Add(body);

            return card;
        }

        private static VisualElement BuildStageList()
        {
            var card = Card();

            var heading = new Label("The pipeline");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 4;
            card.Add(heading);

            foreach (var stage in PipelineStages.All)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;

                var name = new Label(PipelineStages.Label(stage));
                name.style.width = 90;
                name.style.flexShrink = 0;
                row.Add(name);

                var note = new Label(StageNote(stage));
                note.style.flexGrow = 1;
                note.style.opacity = 0.7f;
                note.style.whiteSpace = WhiteSpace.Normal;
                row.Add(note);

                card.Add(row);
            }

            return card;
        }

        private static string StageNote(PipelineStage stage)
        {
            switch (stage)
            {
                case PipelineStage.Configure:
                    return "Settings and profiles agree, and the origin you build against is one the runtime knows.";
                case PipelineStage.Author:
                    return "Rules give assets addresses without two of them claiming the same one.";
                case PipelineStage.Build:
                    return "Bundles and a catalog exist for the active profile, with a content state to patch from.";
                case PipelineStage.Publish:
                    return "The built output is somewhere a player can actually reach.";
                case PipelineStage.Run:
                    return "The player fetches what you published, and gives it back when it is done.";
                default:
                    return string.Empty;
            }
        }

        private static VisualElement BuildHonestyNote()
        {
            var card = Card();

            var title = new Label("A hollow dot is not a pass");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            var body = new Label(
                "A filled dot means this window measured the stage. A hollow one means it did not — " +
                "no asset to read, nothing running, or a check that has not been built yet. The two " +
                "are drawn differently on purpose: rendering them the same is how a CDN nobody had " +
                "ever reached read as healthy.");
            body.style.whiteSpace = WhiteSpace.Normal;
            body.style.marginTop = 3;
            body.style.opacity = 0.75f;
            card.Add(body);

            return card;
        }

        private static VisualElement Card()
        {
            var card = new VisualElement();
            card.style.marginBottom = 8;
            card.style.paddingLeft = 8;
            card.style.paddingRight = 8;
            card.style.paddingTop = 6;
            card.style.paddingBottom = 6;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            card.style.borderTopLeftRadius = 3;
            card.style.borderTopRightRadius = 3;
            card.style.borderBottomLeftRadius = 3;
            card.style.borderBottomRightRadius = 3;
            return card;
        }
    }
}
