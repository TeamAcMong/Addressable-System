using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Which bundles this device has already downloaded.
    /// </summary>
    /// <remarks>
    /// Before this screen the tool could report how much the cache held and nothing about what was
    /// in it: a total in bytes on the Runtime Monitor, and no way to ask which bundles those bytes
    /// were.
    ///
    /// The shape follows from what <see cref="CacheInventory"/> can honestly answer. Bundles are
    /// grouped by what the answer MEANS - here, still to come, or shipped inside the player - rather
    /// than by name, because "not downloaded" and "never will be" are different facts and a single
    /// list makes the reader work out which is which. The split bar is sized by BYTES: "340 bundles"
    /// is a number, "2.0 GB over a phone connection" is a decision.
    ///
    /// The third segment is the one no other view can show. Unity's cache cannot be enumerated, so
    /// content left by an earlier catalog has no name to list - it can only be measured, as the
    /// difference between what the cache says it holds and what this catalog explains. Omitting it
    /// would make the screen look complete and be wrong, and it is exactly what Clean obsolete
    /// deletes.
    /// </remarks>
    public sealed class CacheSection : IHubSection, IHubSectionActions, IHubRailLabel
    {
        private ScrollView _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Cache;

        /// <inheritdoc />
        public string Title => "Downloaded Content";

        /// <inheritdoc />
        /// <remarks>The rail is 196px wide; "Downloaded Content" would wrap.</remarks>
        public string RailLabel => "Cache";

        /// <inheritdoc />
        public string Subtitle => "What this device has already fetched, and what is still to come";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Run;

        /// <inheritdoc />
        /// <remarks>
        /// Two static reads. Deliberately never <see cref="HealthState.Ok"/> outside play mode: there
        /// is no catalog to inventory and the cache being read would be this machine's rather than a
        /// device's, so there is nothing to be well about.
        /// </remarks>
        public SectionHealth GetHealth()
        {
            if (!EditorApplication.isPlaying)
                return SectionHealth.NotMeasured("Not in play mode, so no catalog is loaded.");

            return SectionHealth.NotMeasured("Press Re-scan to read the cache.");
        }

        /// <inheritdoc />
        public void PopulateHeaderActions(VisualElement container)
        {
            var rescan = new Button(Rebuild) { text = "Re-scan" };
            rescan.AddToClassList("hub-btn");
            rescan.tooltip = "Reads the loaded catalog and asks the cache about every bundle in it.";
            container.Add(rescan);
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "cache-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown() => Rebuild();

        // ------------------------------------------------------------------ view

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            var result = CacheInventory.Snapshot();

            if (!result.IsSuccess)
            {
                _body.Add(Note(result.Error.Message, HealthState.NotMeasured));
                return;
            }

            var report = result.Value;

            _body.Add(BuildSplit(report));
            _body.Add(BuildUnaccountedNote(report));

            AddGroup(report, BundlePresence.Cached, "On this device", HealthState.Ok,
                "Cached under the player's storage. A fresh launch loads these without a request.");

            AddGroup(report, BundlePresence.NotFetched, "Still to fetch", HealthState.NotMeasured,
                "In the catalog, not on the device. This is what a returning player downloads.");

            AddGroup(report, BundlePresence.ShipsInPlayer, "Ships in the player", HealthState.NotMeasured,
                "Built into the app. Never cached, never downloaded — absent here is correct.");

            AddGroup(report, BundlePresence.Unknown, "Could not be asked", HealthState.Warning,
                "The catalog carries no hash for these, and the cache is keyed by name AND hash. " +
                "Not an empty answer — no question could be put.");

            _body.Add(BuildFooter());
        }

        /// <summary>How much of this catalog is already here, sized by bytes.</summary>
        private static VisualElement BuildSplit(CacheInventoryReport report)
        {
            long cached = 0, pending = 0;
            foreach (var b in report.Bundles)
            {
                if (b.Presence == BundlePresence.Cached) cached += b.SizeBytes;
                else if (b.Presence == BundlePresence.NotFetched) pending += b.SizeBytes;
            }

            long other = report.UnaccountedBytes > 0 ? report.UnaccountedBytes : 0;
            long total = cached + pending + other;

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label("How much of this catalog is already here");
            title.AddToClassList("hub-card-title");
            head.Add(title);
            card.Add(head);

            var body = new VisualElement();
            body.style.paddingLeft = 9;
            body.style.paddingRight = 9;
            body.style.paddingTop = 8;
            body.style.paddingBottom = 9;

            var bar = new VisualElement();
            bar.AddToClassList("hub-splitbar");

            // A bar with nothing in it would render as an empty groove that looks like a bug. When
            // there is nothing to divide, the note below carries the whole message instead.
            if (total > 0)
            {
                bar.Add(Segment("hub-splitbar-local", cached, total));
                bar.Add(Segment("hub-splitbar-none", pending, total));
                bar.Add(Segment("hub-splitbar-remote", other, total));
                body.Add(bar);
            }

            var legend = new VisualElement();
            legend.AddToClassList("hub-splitlegend");
            legend.Add(LegendItem("hub-splitbar-local", "On this device", cached));
            legend.Add(LegendItem("hub-splitbar-none", "Still to fetch", pending));

            legend.Add(report.UnaccountedBytes < 0
                ? LegendItem("hub-splitbar-remote", "Other versions", -1)
                : LegendItem("hub-splitbar-remote", "Other versions", other));

            body.Add(legend);
            card.Add(body);
            return card;
        }

        private static VisualElement Segment(string cls, long bytes, long total)
        {
            var seg = new VisualElement();
            seg.AddToClassList(cls);
            seg.style.width = new StyleLength(Length.Percent(total <= 0 ? 0 : 100f * bytes / total));
            return seg;
        }

        private static VisualElement LegendItem(string swatchClass, string label, long bytes)
        {
            var item = new VisualElement();
            item.AddToClassList("hub-splitlegend-item");

            var swatch = new VisualElement();
            swatch.AddToClassList("hub-splitlegend-swatch");
            swatch.AddToClassList(swatchClass);
            item.Add(swatch);

            var text = new Label(label);
            text.AddToClassList("hub-splitlegend-label");
            item.Add(text);

            // A dash, not "0 B". The platform declining to report and the cache being empty are
            // different, and this figure is a subtraction - printing 0 would turn every cached byte
            // into "unaccounted".
            var value = new Label(bytes < 0 ? "—" : FormatBytes(bytes));
            value.AddToClassList("hub-rule-id");
            value.style.marginLeft = 6;
            item.Add(value);

            return item;
        }

        /// <summary>The limit, said out loud rather than left to be discovered.</summary>
        private static VisualElement BuildUnaccountedNote(CacheInventoryReport report)
        {
            if (report.UnaccountedBytes < 0)
            {
                return Note(
                    "This platform does not report how much the cache holds, so the share of it this " +
                    "catalog cannot account for is unknown. The groups below are still exact — they " +
                    "come from asking about each bundle by name.",
                    HealthState.NotMeasured);
            }

            if (report.UnaccountedBytes == 0)
                return new VisualElement();

            return Note(
                $"{FormatBytes(report.UnaccountedBytes)} in the cache belongs to no bundle this " +
                "catalog names. Unity can tell you whether a bundle you name is cached; it cannot " +
                "tell you what is in the cache — so content left by an earlier catalog has no name " +
                "to show here. That figure is what Clean obsolete removes.",
                HealthState.Warning);
        }

        private void AddGroup(
            CacheInventoryReport report, BundlePresence presence,
            string title, HealthState state, string blurb)
        {
            var rows = new List<CachedBundleInfo>();
            long bytes = 0;

            foreach (var b in report.Bundles)
            {
                if (b.Presence != presence) continue;
                rows.Add(b);
                bytes += b.SizeBytes;
            }

            // A group with nothing in it is not drawn. "0 bundles could not be asked" is a heading
            // that teaches people to skip past the place a real one will appear.
            if (rows.Count == 0) return;

            var card = new VisualElement();
            card.AddToClassList("hub-card");
            card.style.marginTop = 8;

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var dot = new VisualElement();
            dot.AddToClassList("hub-rule-dot");
            HubStyle.Fill(dot, state);
            head.Add(dot);

            var label = new Label(title);
            label.AddToClassList("hub-card-title");
            head.Add(label);

            var count = new Label(presence == BundlePresence.ShipsInPlayer
                ? $"{rows.Count} bundle(s)"
                : $"{rows.Count} bundle(s) · {FormatBytes(bytes)}");

            count.AddToClassList("hub-card-count");
            head.Add(count);
            card.Add(head);

            var note = new Label(blurb);
            note.AddToClassList("hub-note-text");
            note.style.paddingLeft = 9;
            note.style.paddingRight = 9;
            note.style.paddingTop = 5;
            card.Add(note);

            foreach (var b in rows)
                card.Add(BuildRow(b));

            _body.Add(card);
        }

        private VisualElement BuildRow(CachedBundleInfo bundle)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-rule");

            var name = new Label(string.IsNullOrEmpty(bundle.FileName) ? bundle.BundleName : bundle.FileName);
            name.AddToClassList("hub-rule-id");
            name.style.flexGrow = 1;
            name.style.flexShrink = 1;
            name.style.minWidth = 0;
            name.style.overflow = Overflow.Hidden;
            name.style.textOverflow = TextOverflow.Ellipsis;
            name.style.whiteSpace = WhiteSpace.NoWrap;

            // A bundle name is a hash. Without the full id and the cache key there is no way to
            // correlate a row with a log line or a file on a CDN.
            name.tooltip = $"cache key: {bundle.BundleName}\nhash: {bundle.Hash}";
            row.Add(name);

            // What it holds, because the name says nothing about that.
            var holds = new Label($"{bundle.DependentEntryCount} address(es)");
            holds.AddToClassList("hub-rule-meta");
            holds.style.flexShrink = 0;
            holds.style.width = 104;
            holds.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(holds);

            var size = new Label(bundle.Presence == BundlePresence.ShipsInPlayer
                ? "in the build"
                : FormatBytes(bundle.SizeBytes));

            size.AddToClassList("hub-rule-id");
            size.style.flexShrink = 0;
            size.style.width = 88;
            size.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(size);

            if (bundle.Presence == BundlePresence.Cached)
            {
                var evict = new Button(() => Evict(bundle)) { text = "Evict" };
                evict.AddToClassList("hub-btn");
                evict.AddToClassList("hub-btn--danger");
                evict.style.flexShrink = 0;
                evict.tooltip = "Removes every cached version of this bundle from the device.";
                row.Add(evict);
            }

            return row;
        }

        /// <summary>
        /// Drop one bundle from the device's cache.
        /// </summary>
        /// <remarks>
        /// Confirmed, and the dialog names the bundle and its size. This is not tidying: the running
        /// player may be holding a handle into this bundle right now, and the next load of anything
        /// inside it becomes a download. A reader who presses it should know both.
        /// </remarks>
        private void Evict(CachedBundleInfo bundle)
        {
            string display = string.IsNullOrEmpty(bundle.FileName) ? bundle.BundleName : bundle.FileName;

            bool go = EditorUtility.DisplayDialog(
                "Evict from the cache",
                $"Remove {display} ({FormatBytes(bundle.SizeBytes)}) from this device's cache?\n\n" +
                $"{bundle.DependentEntryCount} address(es) resolve through it. Anything already " +
                "loaded stays loaded; the next load of anything in this bundle becomes a download.",
                "Evict", "Cancel");

            if (!go) return;

#if ENABLE_CACHING
            // ClearAllCachedVersions, not ClearCachedVersion: an older version of the same bundle is
            // exactly the content nothing can name, and leaving it behind would move the bytes from
            // one group into the unaccounted figure rather than freeing them.
            if (!Caching.ClearAllCachedVersions(bundle.BundleName))
            {
                EditorUtility.DisplayDialog(
                    "Nothing was evicted",
                    $"Unity refused to clear {display}. It does that while a bundle is still loaded — " +
                    "leave play mode and try again.",
                    "OK");
            }
#endif

            Rebuild();
        }

        private static VisualElement BuildFooter()
        {
            var row = new VisualElement();
            row.AddToClassList("hub-rule-actions");
            row.style.marginTop = 8;

            var note = new Label(
                "Read from the loaded catalog and the player's cache. Outside play mode there is no " +
                "catalog, and the Editor's cache is this machine's rather than any device's.");

            note.AddToClassList("hub-note-text");
            note.style.flexGrow = 1;
            note.style.flexShrink = 1;
            note.style.minWidth = 0;
            row.Add(note);

            var stamp = new Label("sampled " + System.DateTime.Now.ToString("HH:mm:ss"));
            stamp.AddToClassList("hub-card-count");
            stamp.style.flexShrink = 0;
            row.Add(stamp);

            return row;
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

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024f:F0} KB";
            return $"{bytes} B";
        }
    }
}
