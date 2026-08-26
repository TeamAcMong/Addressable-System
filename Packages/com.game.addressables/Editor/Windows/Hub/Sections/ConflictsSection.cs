using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Rules that disagree about the same asset.
    /// </summary>
    /// <remarks>
    /// Its own screen, as the design has it, rather than a block at the bottom of Layout Rules. Two
    /// assets resolving to one address is not a detail of authoring rules - it is the one outcome of
    /// the rule system that makes an asset unreachable at runtime, and it survives being scrolled past
    /// far too easily when it is the last card on a screen about something else.
    ///
    /// <b>Nothing is scanned until asked.</b> Finding collisions means running the rule set over every
    /// eligible asset, which is far past what <see cref="GetHealth"/> may cost - the rail calls that
    /// once a second for every section. So the health here is <see cref="HealthState.NotMeasured"/>
    /// until a scan has run, and says so. "We have not looked" and "we looked and found nothing" are
    /// different answers and this window does not blur them.
    ///
    /// The scan is the same dry run Layout Rules offers, on the same code path, so the two screens can
    /// never disagree about what a run would do.
    /// </remarks>
    public sealed class ConflictsSection : IHubSection, IHubSectionActions
    {
        /// <summary>
        /// The last scan's result, kept across view rebuilds.
        /// </summary>
        /// <remarks>
        /// Static because sections are constructed fresh on every navigation: an instance field would
        /// make walking away from the screen and back again look like the scan never happened, and the
        /// rail's badge would drop back to "not measured" while the answer was still on the disk it
        /// came from.
        /// </remarks>
        private static List<LayoutRuleProcessor.AddressCollision> _lastScan;
        private static string _lastScanFailure;
        private static double _lastScanAt;
        private static int _lastScanSetCount;

        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Conflicts;

        /// <inheritdoc />
        public string Title => "Conflicts";

        /// <inheritdoc />
        public string Subtitle => "Rules that disagree about the same asset";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Author;

        /// <inheritdoc />
        public SectionHealth GetHealth()
        {
            if (_lastScanFailure != null)
                return SectionHealth.NotMeasured(_lastScanFailure);

            if (_lastScan == null)
                return SectionHealth.NotMeasured("Nothing has scanned for collisions yet.");

            if (_lastScan.Count == 0)
                return SectionHealth.Ok("no collisions");

            return SectionHealth.Blocked(
                _lastScan.Count == 1 ? "1 collision" : $"{_lastScan.Count} collisions",
                "Two assets resolve to one address, so one of them cannot be loaded at runtime.");
        }

        /// <inheritdoc />
        public void PopulateHeaderActions(VisualElement container)
        {
            var scan = new Button(Scan) { text = "Scan for conflicts" };
            scan.AddToClassList("hub-btn");
            scan.tooltip = "Runs the rule set over every eligible asset without writing anything.";
            container.Add(scan);
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "conflicts-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown() => Rebuild();

        private void Scan()
        {
            var all = RulesSection.FindAllRuleData();

            if (all.Count == 0)
            {
                _lastScan = null;
                _lastScanFailure = "No LayoutRuleData asset in the project, so there are no rules to " +
                                   "disagree with each other.";
                Rebuild();
                return;
            }

            try
            {
                // Every rule set, not the one Layout Rules happens to be showing. Two sets can both
                // address the same asset, and that collision belongs to neither of them alone - a
                // scan of one would report it clean while the build fails.
                var collisions = new List<LayoutRuleProcessor.AddressCollision>();

                foreach (var data in all)
                    collisions.AddRange(new LayoutRuleProcessor(data).PreviewRules().Collisions);

                _lastScan = collisions;
                _lastScanFailure = null;
                _lastScanAt = EditorApplication.timeSinceStartup;
                _lastScanSetCount = all.Count;
            }
            catch (Exception ex)
            {
                // Recorded as "not measured", never as "clean". A scan that threw has found nothing
                // and proved nothing, and those are not the same as finding nothing.
                _lastScan = null;
                _lastScanFailure = "The scan failed to run: " + ex.Message;
                Debug.LogWarning($"[AddressableManagerHub] Conflict scan threw: {ex}");
            }

            Rebuild();
        }

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            if (_lastScanFailure != null)
            {
                _body.Add(Note(_lastScanFailure, HealthState.NotMeasured));
                return;
            }

            if (_lastScan == null)
            {
                _body.Add(Note(
                    "Nothing has been scanned yet. Finding collisions means running the rule set over " +
                    "every eligible asset, which is too slow to do on a timer - so this screen waits to " +
                    "be asked rather than showing a number it has not measured.",
                    HealthState.NotMeasured));

                var run = new Button(Scan) { text = "Scan for conflicts" };
                run.AddToClassList("hub-btn");
                run.style.marginTop = 8;
                run.style.marginLeft = 0;
                run.style.alignSelf = Align.FlexStart;
                _body.Add(run);
                return;
            }

            if (_lastScan.Count == 0)
            {
                _body.Add(Note(
                    "No two assets resolve to the same address across " + _lastScanSetCount +
                    " rule set(s). Scanned " + Ago(_lastScanAt) +
                    " - re-scan after changing a rule or adding assets.",
                    HealthState.Ok));
                return;
            }

            _body.Add(Note(
                (_lastScan.Count == 1
                    ? "One address is claimed by two assets."
                    : _lastScan.Count + " addresses are each claimed by two assets.") +
                " Addressables returns one location per key, so the other asset could never be loaded " +
                "at runtime. Scanned " + Ago(_lastScanAt) + ".",
                HealthState.Blocked));

            foreach (var collision in _lastScan)
                _body.Add(RulesSection.BuildCollisionCard(collision));
        }

        private static string Ago(double at)
        {
            double seconds = EditorApplication.timeSinceStartup - at;
            if (seconds < 60) return "just now";
            if (seconds < 3600) return (int)(seconds / 60) + " min ago";
            return (int)(seconds / 3600) + " h ago";
        }

        private static VisualElement Note(string text, HealthState state)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var label = new Label(text);
            label.AddToClassList("hub-note-text");
            label.style.paddingLeft = 9;
            label.style.paddingRight = 9;
            label.style.paddingTop = 8;
            label.style.paddingBottom = 8;
            HubStyle.Text(label, state);
            card.Add(label);

            return card;
        }
    }
}
