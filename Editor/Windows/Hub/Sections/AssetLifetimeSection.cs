using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Managers;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// What is loaded, and which scope is holding it.
    /// </summary>
    /// <remarks>
    /// This is the one screen in the package that answers something Unity's own tooling cannot.
    /// Unity's profiler is better at bytes and always will be; what it does not know is what a
    /// <i>scope</i> is, because scopes are this package's idea and the reference counts live in its
    /// loaders. "The scene unloaded — so why is this bundle still resident?" is answerable only from
    /// in here.
    ///
    /// <b>What this does not do yet, and why it says so.</b> The decision record calls for leak
    /// detection: a handle whose owning object was destroyed while its reference count never reached
    /// zero. Two things are missing for that, and neither is guessable —
    /// <c>ScopeManager.Registration</c> keeps the owner's type NAME rather than a weak reference to
    /// the owner, so "was it destroyed" cannot be asked; and <c>IOwnedHandle</c> exposes
    /// <c>IsAlive</c> but no reference count, so "how many holders remain" cannot be answered either.
    /// Both are small additions to code that governs asset lifetime, which is precisely the code not
    /// to change casually. Until they land, this screen reports the inventory it can actually see and
    /// says plainly that the leak column is not measured — rather than showing a plausible,
    /// fabricated one.
    /// </remarks>
    public sealed class AssetLifetimeSection : IHubSection
    {
        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.AssetLifetime;

        /// <inheritdoc />
        public string Title => "Asset Lifetime";

        /// <inheritdoc />
        public string Subtitle => "What is loaded, and which scope is holding it";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Run;

        /// <inheritdoc />
        public SectionHealth GetHealth()
        {
            if (!EditorApplication.isPlaying)
                return SectionHealth.NotMeasured("Not in play mode, so nothing is loaded to inspect.");

            // CachedAssetCount, not SnapshotLoadedAssets().Count: this runs on the rail's timer and
            // the snapshot allocates one row per cached asset. Counting is a dictionary read.
            int scopes = 0, assets = 0;
            foreach (var scopeId in ScopeManager.Instance.ActiveScopes)
            {
                scopes++;
                var loader = ScopeManager.Instance.GetScope(scopeId);
                if (loader != null) assets += loader.CachedAssetCount;
            }

            if (scopes == 0)
                return SectionHealth.NotMeasured("No scopes are registered in this play session.");

            return SectionHealth.Ok($"{assets} held");
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "lifetime-root" };
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

            if (!EditorApplication.isPlaying)
            {
                _body.Add(BuildNotPlayingState());
                return;
            }

            var scopes = ReadScopes();

            _body.Add(BuildSummary(scopes));

            if (scopes.Count == 0)
            {
                _body.Add(Note(
                    "No scopes are registered. Either nothing has loaded through this package yet, or " +
                    "the game is loading assets through Addressables directly, which this screen " +
                    "cannot see."));
            }
            else
            {
                foreach (var scope in scopes)
                    _body.Add(BuildScopeCard(scope));
            }

            _body.Add(BuildLimitsNote());

            var refresh = new Button(Rebuild) { text = "Take a snapshot" };
            refresh.AddToClassList("hub-btn");
            refresh.style.alignSelf = Align.FlexStart;
            _body.Add(refresh);
        }

        private sealed class ScopeRow
        {
            public string Id;
            public List<LoadedAssetInfo> Assets;
            public long Bytes;
            public int Dead;
            public bool BytesKnown;
        }

        private static List<ScopeRow> ReadScopes()
        {
            var rows = new List<ScopeRow>();

            foreach (var scopeId in ScopeManager.Instance.ActiveScopes)
            {
                var loader = ScopeManager.Instance.GetScope(scopeId);
                if (loader == null) continue;

                var assets = loader.SnapshotLoadedAssets();

                long bytes = 0;
                int dead = 0;
                bool bytesKnown = false;

                foreach (var asset in assets)
                {
                    bytes += asset.EstimatedBytes;
                    if (asset.EstimatedBytes > 0) bytesKnown = true;
                    if (!asset.IsAlive) dead++;
                }

                rows.Add(new ScopeRow
                {
                    Id = scopeId,
                    Assets = assets,
                    Bytes = bytes,
                    Dead = dead,
                    BytesKnown = bytesKnown,
                });
            }

            rows.Sort((a, b) => b.Assets.Count.CompareTo(a.Assets.Count));
            return rows;
        }

        private static VisualElement BuildSummary(List<ScopeRow> scopes)
        {
            int assets = 0, dead = 0;
            foreach (var scope in scopes)
            {
                assets += scope.Assets.Count;
                dead += scope.Dead;
            }

            var strip = new VisualElement();
            strip.AddToClassList("hub-summary");

            strip.Add(SummaryItem(HealthState.Ok, $"{scopes.Count} scope(s)"));
            strip.Add(SummaryItem(HealthState.Ok, $"{assets} asset(s) held"));

            if (dead > 0)
            {
                strip.Add(SummaryItem(HealthState.Warning,
                    $"{dead} released handle(s) still cached"));
            }

            // The column the decision record asked for, reported honestly as absent rather than
            // silently omitted. A missing column reads as "no leaks"; this reads as what it is.
            strip.Add(SummaryItem(HealthState.NotMeasured, "leaks not measured"));

            return strip;
        }

        private static VisualElement SummaryItem(HealthState state, string text)
        {
            var item = new VisualElement();
            item.AddToClassList("hub-summary-item");

            var dot = new VisualElement();
            dot.AddToClassList("hub-summary-dot");
            foreach (var cls in SectionHealth.AllStyleClasses) dot.RemoveFromClassList(cls);
            dot.AddToClassList(SectionHealth.StyleClassFor(state));
            item.Add(dot);

            var label = new Label(text);
            label.AddToClassList("hub-summary-label");
            item.Add(label);

            return item;
        }

        private static VisualElement BuildScopeCard(ScopeRow scope)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label(scope.Id);
            title.AddToClassList("hub-card-title");
            head.Add(title);

            var count = new Label(scope.BytesKnown
                ? $"{scope.Assets.Count} held  ·  {FormatBytes(scope.Bytes)}"
                : $"{scope.Assets.Count} held  ·  size not measured");
            count.AddToClassList("hub-card-count");
            count.tooltip = scope.BytesKnown
                ? "Estimated resident bytes, as tracked for tier eviction."
                : "This loader is not tiered, so it never computes byte sizes. Not the same as zero.";
            head.Add(count);

            card.Add(head);

            const int cap = 60;
            int shown = 0;

            foreach (var asset in scope.Assets)
            {
                if (shown++ >= cap) break;

                var row = new VisualElement();
                row.AddToClassList("hub-prow");

                var address = new Label(asset.Address);
                address.AddToClassList("hub-pcol");
                address.style.flexGrow = 1;
                address.tooltip = asset.Address;
                if (!asset.IsAlive) ApplyText(address, HealthState.Warning);
                row.Add(address);

                var type = new Label(asset.TypeName);
                type.AddToClassList("hub-pcol");
                type.style.width = 96;
                type.style.flexShrink = 0;
                row.Add(type);

                var state = new Label(asset.IsAlive ? string.Empty : "released");
                state.AddToClassList("hub-pcol");
                state.style.width = 70;
                state.style.flexShrink = 0;
                if (!asset.IsAlive) ApplyText(state, HealthState.Warning);
                state.tooltip = asset.IsAlive
                    ? string.Empty
                    : "The Addressables operation behind this entry has been released, but the entry " +
                      "is still in the cache. That is the shape a release-ordering bug takes.";
                row.Add(state);

                var size = new Label(asset.EstimatedBytes > 0 ? FormatBytes(asset.EstimatedBytes) : "—");
                size.AddToClassList("hub-pcol");
                size.style.width = 74;
                size.style.flexShrink = 0;
                size.style.unityTextAlign = TextAnchor.MiddleRight;
                row.Add(size);

                card.Add(row);
            }

            if (scope.Assets.Count > cap)
            {
                var more = new Label($"{scope.Assets.Count - cap} further entr(ies) are not listed.");
                more.AddToClassList("hub-rule-meta");
                more.style.paddingLeft = 8;
                more.style.paddingBottom = 6;
                card.Add(more);
            }

            return card;
        }

        private static VisualElement BuildNotPlayingState()
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var title = new Label("Nothing is loaded");
            title.AddToClassList("hub-card-title");
            foreach (var cls in SectionHealth.AllStyleClasses) title.RemoveFromClassList(cls);
            title.AddToClassList(SectionHealth.StyleClassFor(HealthState.NotMeasured));
            box.Add(title);

            var body = new Label(
                "Asset lifetime is a play-mode question: outside it there are no loaders, no scopes " +
                "and nothing held. Enter play mode and come back.\n\n" +
                "This screen is not a memory profiler — Unity's is better at bytes. It answers the one " +
                "thing Unity cannot: which scope is holding a given asset alive.");
            body.AddToClassList("hub-note-text");
            box.Add(body);

            return box;
        }

        private static VisualElement BuildLimitsNote()
        {
            return Note(
                "Leak detection is not implemented yet, and this screen will not fake it. Finding a " +
                "handle whose owner was destroyed needs two things the runtime does not expose today: " +
                "a weak reference to the owning object (ScopeManager keeps only its type name) and a " +
                "reference count on the handle (IOwnedHandle exposes IsAlive and nothing else). Both " +
                "are changes to the code that governs asset lifetime, which is not code to change " +
                "casually.\n\n" +
                "What is here is real: every entry each live scope is holding, read from the loader " +
                "itself.");
        }

        // ------------------------------------------------------------------ helpers

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
