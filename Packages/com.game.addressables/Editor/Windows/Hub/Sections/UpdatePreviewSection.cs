using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// What a returning player would download: the diff between two build manifests.
    /// </summary>
    /// <remarks>
    /// One number decides whether a patch is acceptable, and it is not the size of the build. A
    /// player installing fresh downloads everything; a player who already has the previous release
    /// downloads only what changed. That second figure is what a release is judged on, and it was
    /// previously reachable only by reading two JSON files by hand.
    ///
    /// The comparison is between two MANIFESTS - artifacts written by builds - not between a build
    /// and itself. A diff computed by scanning the current output and then checking it against that
    /// same output is circular and proves nothing, which is the shape the build verification used to
    /// have.
    /// </remarks>
    public sealed class UpdatePreviewSection : IHubSection, IHubHostAware, IHubSectionActions
    {
        /// <summary>Where the archived previous manifest is looked for, relative to the project.</summary>
        /// <remarks>
        /// A convention rather than a setting, for now, and stated on screen so nobody has to guess.
        /// CI archives the manifest of the release it shipped; comparing against it is the only way
        /// to know what a patch costs before publishing one.
        /// </remarks>
        private const string PreviousManifestPath = "ServerData/previous-build-manifest.json";

        private readonly HealthThrottle _health = new HealthThrottle();
        private IHubHost _host;
        private VisualElement _body;
        private ContentDiffResult _diff;
        private string _diffError;

        /// <inheritdoc />
        public string Id => HubSections.Ids.UpdatePreview;

        /// <inheritdoc />
        public string Title => "Update Preview";

        /// <inheritdoc />
        public string Subtitle => "What a returning player would download";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Build;

        /// <inheritdoc />
        public void Bind(IHubHost host) => _host = host;

        /// <inheritdoc />
        public SectionHealth GetHealth() => _health.Get(ComputeHealth);

        private SectionHealth ComputeHealth()
        {
            if (CdnBuildModes.IsLocalOnly)
                return SectionHealth.NotMeasured("Build Mode is Local-only; there is nothing to patch.");

            if (!System.IO.File.Exists(CurrentManifestPath))
                return SectionHealth.NotMeasured("No build manifest in ServerData/, so no build has run here.");

            if (!System.IO.File.Exists(PreviousManifestPath))
            {
                return SectionHealth.NotMeasured(
                    "No archived manifest from the previous release, so a patch size cannot be computed.");
            }

            if (_diff == null)
                return SectionHealth.NotMeasured("The two manifests have not been compared yet.");

            return SectionHealth.Ok(FormatBytes(_diff.PatchSizeBytes));
        }

        /// <inheritdoc />
        public void PopulateHeaderActions(VisualElement container)
        {
            var compare = new Button(Compare) { text = "Re-compare" };
            compare.AddToClassList("hub-btn");
            container.Add(compare);
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "update-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown()
        {
            _health.Invalidate();
            Compare();
        }

        // ------------------------------------------------------------------

        private void Compare()
        {
            _diff = null;
            _diffError = null;

            if (System.IO.File.Exists(CurrentManifestPath) && System.IO.File.Exists(PreviousManifestPath))
            {
                var result = ContentDiff.CompareFiles(PreviousManifestPath, CurrentManifestPath);
                if (result.IsFailure) _diffError = result.ErrorMessage;
                else _diff = result.Value;
            }

            _health.Invalidate();
            Rebuild();
        }

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            if (CdnBuildModes.IsLocalOnly)
            {
                _body.Add(Note(
                    "Build Mode is Local-only. All content ships inside the player, so there is no " +
                    "patch for anyone to download. Skipped, not failing."));
                return;
            }

            if (!System.IO.File.Exists(CurrentManifestPath))
            {
                _body.Add(BuildMissingState(
                    "No build has run in this workspace",
                    $"There is no manifest at {CurrentManifestPath}, so there is nothing to compare. " +
                    "Build content first.",
                    "Go to Build", HubSections.Ids.Build));
                return;
            }

            if (!System.IO.File.Exists(PreviousManifestPath))
            {
                _body.Add(BuildMissingState(
                    "Nothing to compare against",
                    $"A patch size is the difference between two releases, so it needs the manifest " +
                    $"of the one already live. Put it at {PreviousManifestPath}.\n\n" +
                    "On CI that is one copy step at the end of a successful publish: archive " +
                    $"{CurrentManifestPath} under that name so the next build can measure against it. " +
                    "Without it this screen can say what you built, but not what it costs anyone.",
                    null, null));
                return;
            }

            if (_diffError != null)
            {
                _body.Add(Note($"The two manifests could not be compared: {_diffError}\n\n" +
                               "Nothing below has been measured."));
                return;
            }

            _body.Add(BuildStats());
            _body.Add(BuildRows());
            _body.Add(Note(
                $"{FormatBytes(_diff.PatchSizeBytes)} is what a returning player downloads. Someone " +
                "installing fresh downloads every bundle in the build — that is a different number, " +
                "and it is not the one a patch is judged on."));

            // Re-compare is in the section header — see PopulateHeaderActions.
        }

        private VisualElement BuildStats()
        {
            var row = new VisualElement();
            row.AddToClassList("hub-stats");

            row.Add(Stat("PATCH SIZE", FormatBytes(_diff.PatchSizeBytes), string.Empty,
                HealthState.Ok, "What a returning player downloads"));

            row.Add(Stat("CHANGED", _diff.ChangedBundles.Count.ToString(), "bundles",
                HealthState.Ok, "Contents differ from the live release"));

            row.Add(Stat("NEW", _diff.NewBundles.Count.ToString(), "bundles",
                HealthState.Ok, "Did not exist before"));

            row.Add(Stat("NOW UNUSED", _diff.RemovedBundles.Count.ToString(), "bundles",
                _diff.RemovedBundles.Count > 0 ? HealthState.Warning : HealthState.Ok,
                "Gone from the build — leave them on the CDN for players still on the old release"));

            return row;
        }

        private VisualElement BuildRows()
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label("Changed since the live release");
            title.AddToClassList("hub-card-title");
            head.Add(title);

            var count = new Label(_diff.CatalogChanged
                ? $"{_diff.UnchangedCount} unchanged  ·  catalog changed"
                : $"{_diff.UnchangedCount} unchanged  ·  catalog identical");
            count.AddToClassList("hub-card-count");
            head.Add(count);

            card.Add(head);

            const int cap = 60;
            int shown = 0;

            foreach (var b in _diff.NewBundles)
            {
                if (shown++ >= cap) break;
                card.Add(DiffRow("+", b.fileName, "new", b.sizeBytes, HealthState.Ok));
            }

            foreach (var c in _diff.ChangedBundles)
            {
                if (shown++ >= cap) break;
                long delta = c.CurrentSizeBytes - c.PreviousSizeBytes;
                string note = delta == 0 ? "contents changed" : (delta > 0 ? "grew" : "shrank");
                card.Add(DiffRow("~", c.FileName, note, c.CurrentSizeBytes, HealthState.Warning));
            }

            foreach (var b in _diff.RemovedBundles)
            {
                if (shown++ >= cap) break;
                card.Add(DiffRow("-", b.fileName, "no longer referenced", b.sizeBytes, HealthState.NotMeasured));
            }

            int total = _diff.NewBundles.Count + _diff.ChangedBundles.Count + _diff.RemovedBundles.Count;
            if (total > cap)
            {
                // Never a silent truncation. A list that stops at sixty with no note reads as "that
                // is all of them", which is how a four-hundred-bundle patch gets approved as sixty.
                var more = new Label($"{total - cap} further change(s) are not listed. The patch size " +
                                     "above counts all of them.");
                more.AddToClassList("hub-rule-meta");
                more.style.paddingLeft = 8;
                more.style.paddingBottom = 7;
                card.Add(more);
            }

            if (total == 0)
            {
                var same = new Label(
                    "Nothing changed. A returning player downloads nothing, and publishing this build " +
                    "would be a no-op for them.");
                same.AddToClassList("hub-rule-meta");
                same.style.paddingLeft = 8;
                same.style.paddingTop = 7;
                same.style.paddingBottom = 7;
                card.Add(same);
            }

            return card;
        }

        private static VisualElement DiffRow(
            string sign, string name, string reason, long bytes, HealthState state)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-prow");

            var mark = new Label(sign);
            mark.AddToClassList("hub-pcol");
            mark.style.width = 18;
            mark.style.flexShrink = 0;
            mark.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyText(mark, state);
            row.Add(mark);

            var file = new Label(name);
            file.AddToClassList("hub-pcol");
            file.style.flexGrow = 1;
            file.tooltip = name;
            row.Add(file);

            var why = new Label(reason);
            why.AddToClassList("hub-pcol");
            why.style.width = 150;
            why.style.flexShrink = 0;
            row.Add(why);

            var size = new Label(FormatBytes(bytes));
            size.AddToClassList("hub-pcol");
            size.style.width = 80;
            size.style.flexShrink = 0;
            size.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(size);

            return row;
        }

        private VisualElement BuildMissingState(string title, string body, string cta, string target)
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var head = new Label(title);
            head.AddToClassList("hub-card-title");
            ApplyText(head, HealthState.NotMeasured);
            box.Add(head);

            var text = new Label(body);
            text.AddToClassList("hub-note-text");
            box.Add(text);

            if (!string.IsNullOrEmpty(cta))
            {
                var button = new Button(() => _host?.Navigate(target)) { text = cta };
                button.AddToClassList("hub-btn");
                button.style.marginTop = 6;
                button.style.marginLeft = 0;
                button.style.alignSelf = Align.FlexStart;
                box.Add(button);
            }

            return box;
        }

        // ------------------------------------------------------------------ helpers

        private static string CurrentManifestPath => System.IO.Path.Combine(
            BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName);

        private static VisualElement Stat(
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
            ApplyText(big, state);
            valueRow.Add(big);

            if (!string.IsNullOrEmpty(unit))
            {
                var unitLabel = new Label(unit);
                unitLabel.AddToClassList("hub-stat-unit");
                valueRow.Add(unitLabel);
            }

            card.Add(valueRow);

            var foot = new Label(footnote);
            foot.AddToClassList("hub-stat-foot");
            card.Add(foot);

            return card;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:F0} KB";
            return $"{bytes} B";
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

        private static void ApplyText(VisualElement element, HealthState state)
        {
            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);

            element.AddToClassList(SectionHealth.StyleClassFor(state));
            element.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0));
        }
    }
}
