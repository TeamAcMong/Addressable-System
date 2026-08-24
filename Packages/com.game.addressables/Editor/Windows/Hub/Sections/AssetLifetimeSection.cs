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
    /// <b>Leaks.</b> A scope whose owning object has been destroyed while its loader is still
    /// holding assets is memory nobody is going to give back, and it is reported here as such.
    /// <c>ScopeManager</c> keeps a WEAK reference to each foreign owner for exactly this - weak by
    /// requirement, so the directory can never be the reason an owner stays alive.
    ///
    /// The check is the PAIR, not either half. A dead owner holding nothing is an untidy
    /// registration, not an expensive one; a live owner holding a great deal is a game doing its job.
    /// And a destroyed <c>UnityEngine.Object</c> is not null to the CLR - only Unity's overloaded
    /// <c>==</c> knows - so the weak reference is unwrapped and asked Unity's question rather than
    /// tested for null, which would report a destroyed MonoBehaviour as alive for exactly as long as
    /// the leak was worth catching.
    ///
    /// Per-handle reference counts are still not available (<c>IOwnedHandle</c> exposes
    /// <c>IsAlive</c> and nothing else), so this reports leaks at scope granularity rather than
    /// naming which holder failed to release. That is the useful half.
    /// </remarks>
    public sealed class AssetLifetimeSection : IHubSection, IHubSectionActions
    {
        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.AssetLifetime;

        /// <inheritdoc />
        public string Title => "Asset Lifetime";

        /// <inheritdoc />
        public string Subtitle => "What is loaded, which scope holds it, and what leaked";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Run;

        /// <inheritdoc />
        public SectionHealth GetHealth()
        {
            if (!EditorApplication.isPlaying)
                return SectionHealth.NotMeasured("Not in play mode, so nothing is loaded to inspect.");

            // CachedAssetCount, not SnapshotLoadedAssets().Count: this runs on the rail's timer and
            // the snapshot allocates one row per cached asset. Counting is a dictionary read.
            var scopes = ScopeManager.Instance.SnapshotScopes();
            if (scopes.Count == 0)
                return SectionHealth.NotMeasured("No scopes are registered in this play session.");

            int assets = 0, leaked = 0, leakedAssets = 0;
            foreach (var scope in scopes)
            {
                assets += scope.HeldAssetCount;
                if (!scope.IsLeaked) continue;

                leaked++;
                leakedAssets += scope.HeldAssetCount;
            }

            if (leaked > 0)
            {
                return SectionHealth.Warning(
                    leaked == 1 ? "1 leaked scope" : $"{leaked} leaked scopes",
                    $"{leakedAssets} asset(s) are held by {leaked} scope(s) whose owner no longer " +
                    "exists. Nothing is going to release them this session.");
            }

            return SectionHealth.Ok($"{assets} held");
        }

        /// <summary>Look again, and let go of what nobody is holding.</summary>
        public void PopulateHeaderActions(VisualElement container)
        {
            var snapshot = new Button(Rebuild) { text = "Snapshot" };
            snapshot.AddToClassList("hub-btn");
            snapshot.tooltip = "Re-reads every live scope. The list is a moment in time, not a live view.";
            container.Add(snapshot);

            // Counted from a snapshot taken here, so the number on the button and the rows under it
            // come from one reading. Absent when there is nothing to release: an action offering to fix
            // zero things reads as an action that did not work.
            int leaked = 0;
            foreach (var scope in ReadScopes())
                if (scope.Info.IsLeaked) leaked++;

            if (leaked == 0) return;

            var release = new Button(() => ReleaseLeaked(leaked))
            {
                text = leaked == 1 ? "Release leaked (1)" : "Release leaked (" + leaked + ")",
            };
            release.AddToClassList("hub-btn");
            release.tooltip =
                "Clears every scope whose owner is gone. Assets held only by those scopes are freed.";
            container.Add(release);
        }

        /// <summary>Clear every scope whose owner has been destroyed.</summary>
        /// <remarks>
        /// Confirmed first, and the dialog names the scopes rather than saying "some". This frees assets
        /// that something still running may be reading through a handle it took out before its owner
        /// died. A leaked scope is a bug being reported, not a state to silently repair.
        ///
        /// The list is re-read inside rather than captured when the button was drawn: play mode can end
        /// between the two, taking every scope with it.
        /// </remarks>
        private void ReleaseLeaked(int expected)
        {
            var leaked = new List<string>();
            foreach (var scope in ReadScopes())
                if (scope.Info.IsLeaked) leaked.Add(scope.Id);

            if (leaked.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Nothing to release",
                    "There were " + expected + " leaked scope(s) when this screen was drawn, and none " +
                    "now. Play mode probably ended.",
                    "OK");
                Rebuild();
                return;
            }

            bool go = EditorUtility.DisplayDialog(
                "Release leaked scopes",
                "Clear " + leaked.Count + " scope(s) whose owner has been destroyed?\n\n" +
                string.Join("\n", leaked) +
                "\n\nAssets held only by these scopes are released. A handle taken out of one " +
                "before its owner died becomes invalid.",
                "Release", "Cancel");

            if (!go) return;

            foreach (string id in leaked)
                ScopeManager.Instance.ClearScope(id);

            Rebuild();
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

            _body.Add(BuildLeakHeadline(scopes));
            _body.Add(BuildSummary(scopes));
            _body.Add(BuildListHeading());

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

            // The snapshot button is in the section header — see PopulateHeaderActions.
        }

        private sealed class ScopeRow
        {
            public string Id;
            public List<LoadedAssetInfo> Assets;
            public long Bytes;
            public int Dead;
            public bool BytesKnown;
            public ScopeInfo Info;
        }

        private static List<ScopeRow> ReadScopes()
        {
            var rows = new List<ScopeRow>();

            foreach (var info in ScopeManager.Instance.SnapshotScopes())
            {
                var loader = ScopeManager.Instance.GetScope(info.ScopeId);
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
                    Id = info.ScopeId,
                    Assets = assets,
                    Bytes = bytes,
                    Dead = dead,
                    BytesKnown = bytesKnown,
                    Info = info,
                });
            }

            // Leaked scopes first, then by how much they are holding. The ordering IS the finding:
            // a list sorted only by size buries a small leak under a large, legitimate scene scope.
            rows.Sort((a, b) =>
            {
                if (a.Info.IsLeaked != b.Info.IsLeaked) return a.Info.IsLeaked ? -1 : 1;
                return b.Assets.Count.CompareTo(a.Assets.Count);
            });
            return rows;
        }

        private static VisualElement BuildSummary(List<ScopeRow> scopes)
        {
            int assets = 0, dead = 0, leaked = 0, leakedAssets = 0;
            long leakedBytes = 0;
            bool bytesKnown = false;

            foreach (var scope in scopes)
            {
                assets += scope.Assets.Count;
                dead += scope.Dead;
                if (scope.BytesKnown) bytesKnown = true;

                if (!scope.Info.IsLeaked) continue;
                leaked++;
                leakedAssets += scope.Assets.Count;
                leakedBytes += scope.Bytes;
            }

            var row = new VisualElement();
            row.AddToClassList("hub-stats");

            row.Add(Stat("LIVE SCOPES", scopes.Count.ToString(), string.Empty,
                HealthState.Ok, "Registered right now"));

            row.Add(Stat("ASSETS HELD", assets.ToString(), string.Empty,
                HealthState.Ok, "Across every live scope"));

            row.Add(Stat("LEAKED SCOPES", leaked.ToString(), string.Empty,
                leaked > 0 ? HealthState.Warning : HealthState.Ok,
                leaked > 0
                    ? $"Holding {leakedAssets} asset(s) nobody will release"
                    : "Every scope still has an owner"));

            // Bytes only when the loader actually measured them. An untiered loader never computes
            // a size, and printing 0 MB next to a hundred megabytes of resident content would be a
            // number that is worse than no number.
            row.Add(bytesKnown
                ? Stat("HELD BY LEAKS", FormatBytes(leakedBytes), string.Empty,
                    leakedBytes > 0 ? HealthState.Warning : HealthState.Ok,
                    "Estimated, from the tier accounting")
                : Stat("HELD BY LEAKS", "—", string.Empty, HealthState.NotMeasured,
                    "These loaders are not tiered, so no size was computed"));

            var wrapper = new VisualElement();
            wrapper.Add(row);

            if (dead > 0)
            {
                var note = new Label(
                    $"{dead} cached entr(ies) point at a released handle. That is the shape a " +
                    "release-ordering bug takes, and it is shown rather than filtered out.");
                note.AddToClassList("hub-note-text");
                note.style.marginBottom = 8;
                wrapper.Add(note);
            }

            return wrapper;
        }

        /// <summary>One headline figure. Same shape as the Overview and Update Preview cards.</summary>
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
            if (scope.Info.IsLeaked) ApplyText(title, HealthState.Warning);
            head.Add(title);

            var owner = new Label(DescribeOwner(scope.Info));
            owner.AddToClassList("hub-card-count");
            owner.style.marginRight = 10;
            if (scope.Info.IsLeaked) ApplyText(owner, HealthState.Warning);
            head.Add(owner);

            var count = new Label(scope.BytesKnown
                ? $"{scope.Assets.Count} held  ·  {FormatBytes(scope.Bytes)}"
                : $"{scope.Assets.Count} held  ·  size not measured");
            count.AddToClassList("hub-card-count");
            count.tooltip = scope.BytesKnown
                ? "Estimated resident bytes, as tracked for tier eviction."
                : "This loader is not tiered, so it never computes byte sizes. Not the same as zero.";
            head.Add(count);

            card.Add(head);

            if (scope.Info.IsLeaked)
            {
                var why = new Label(
                    $"{scope.Info.OwnerTypeName ?? "The owner"} " +
                    (scope.Info.OwnerState == ScopeOwnerState.Destroyed
                        ? "was destroyed"
                        : "was garbage collected") +
                    $" while this scope still holds {scope.Assets.Count} asset(s). Nothing is going " +
                    "to release them for the rest of this session.");
                why.AddToClassList("hub-rule-meta");
                why.style.paddingLeft = 8;
                why.style.paddingRight = 8;
                why.style.paddingTop = 6;
                card.Add(why);
            }

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

        /// <summary>
        /// The design leads this screen with the leak, not with the totals.
        /// </summary>
        /// <remarks>
        /// A count of live scopes is a fact; a handle that outlived its owner is a bug, and putting it
        /// behind four cards of arithmetic makes the reader go and find it. Absent entirely when
        /// nothing has leaked - a headline reading "0 leaked" every session teaches people to skip
        /// past the place the real one will appear.
        /// </remarks>
        private static VisualElement BuildLeakHeadline(List<ScopeRow> scopes)
        {
            int handles = 0;
            foreach (var scope in scopes)
                if (scope.Info.IsLeaked) handles += scope.Assets.Count;

            if (handles == 0) return new VisualElement();

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label(handles == 1
                ? "1 handle outlived the object that took it"
                : handles + " handles outlived the object that took them");
            title.AddToClassList("hub-card-title");
            ApplyText(title, HealthState.Warning);
            head.Add(title);
            card.Add(head);

            var body = new Label(
                "Their owning GameObject is destroyed and the reference count never reached zero, so " +
                "the bundle behind each one stays resident for the rest of the session. Unity's " +
                "profiler can show you the memory; only this package knows who was supposed to give " +
                "it back.");

            body.AddToClassList("hub-note-text");
            body.style.paddingLeft = 9;
            body.style.paddingRight = 9;
            body.style.paddingTop = 6;
            body.style.paddingBottom = 8;
            card.Add(body);

            return card;
        }

        /// <summary>The heading over the scope list, stamped with when the sample was taken.</summary>
        /// <remarks>
        /// The stamp is the point. Every row below it is a sample rather than a live reading, and a
        /// list that does not say when it was taken gets read as current however old it is.
        /// </remarks>
        private static VisualElement BuildListHeading()
        {
            var row = new VisualElement();
            row.AddToClassList("hub-card-header");
            row.style.marginTop = 4;

            var title = new Label("Live handles by owning scope");
            title.AddToClassList("hub-card-title");
            row.Add(title);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            var stamp = new Label(
                "sampled " + System.DateTime.Now.ToString("HH:mm:ss") +
                (EditorApplication.isPlaying ? " \u00b7 play mode" : " \u00b7 edit mode"));

            stamp.AddToClassList("hub-card-count");
            row.Add(stamp);

            return row;
        }

        private static VisualElement BuildLimitsNote()
        {
            return Note(
                "Not a memory profiler. Unity's is better at bytes; this answers the one "
                + "question it cannot: which scope is still holding this, and who took the "
                + "reference. Sizes are the bundle's, shown so a leak can be ranked - not to "
                + "be added up against the Profiler's numbers." + "\n\n" +
                "A leak here means the object that created a scope is gone while its loader still " +
                "holds assets. Both halves matter: a dead owner holding nothing is untidy, a live " +
                "owner holding a lot is a game doing its job.\n\n" +
                "Reported per scope, not per handle. Naming which holder failed to release needs a " +
                "reference count on the handle, and IOwnedHandle exposes IsAlive and nothing else — " +
                "so that half is still not measured, and this screen does not guess at it.");
        }

        // ------------------------------------------------------------------ helpers

        private static string DescribeOwner(ScopeInfo info)
        {
            switch (info.OwnerState)
            {
                case ScopeOwnerState.ManagerOwned: return "created by ScopeManager";
                case ScopeOwnerState.Destroyed:    return $"{info.OwnerTypeName ?? "owner"} — destroyed";
                case ScopeOwnerState.Collected:    return $"{info.OwnerTypeName ?? "owner"} — collected";
                case ScopeOwnerState.Alive:        return info.OwnerTypeName ?? "owned externally";
                default:                           return "owner unknown";
            }
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
