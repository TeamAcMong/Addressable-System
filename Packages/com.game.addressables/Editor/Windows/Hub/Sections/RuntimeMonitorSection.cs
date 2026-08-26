using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// What the running player is doing.
    /// </summary>
    /// <remarks>
    /// Built to the design's shape rather than hosting the old CDN tab: two panels side by side, the
    /// live state on the left and the transfer on the right, an outcome that stays put, and the three
    /// verbs along the bottom. The tab read the same values in a single stacked column, which put the
    /// download - the only thing on this screen that changes second to second - below a six-row table
    /// that does not.
    ///
    /// <b>Everything here is a sample, not a subscription.</b> The screen polls once a second, which is
    /// stated on it, because a fast transfer can begin and end between two samples: seventy bundles off
    /// a local server land in about a second. A reader who does not know that reads an empty Download
    /// panel as "nothing was downloaded". The Local Server request log is the reliable view and the
    /// note says so.
    ///
    /// <c>RuntimeMonitorTab</c> stays for <c>CdnManagerWindow</c> until that window retires.
    /// </remarks>
    public sealed class RuntimeMonitorSection : IHubSection, IHubSectionActions
    {
        private VisualElement _body;
        private IVisualElementScheduledItem _poll;

        /// <summary>
        /// The last action's result, kept until another action replaces it.
        /// </summary>
        /// <remarks>
        /// Static, and that is the feature. Every one of the three buttons used to write its outcome
        /// onto a label the very next refresh overwrote - within a frame, on a screen that refreshes
        /// once a second - so all three appeared to do nothing. An outcome is the only evidence a
        /// button worked; it stays until the reader acts again.
        /// </remarks>
        private static string _outcome;
        private static string _outcomeDetail;
        private static HealthState _outcomeState = HealthState.Ok;

        /// <inheritdoc />
        public string Id => HubSections.Ids.RuntimeMonitor;

        /// <inheritdoc />
        public string Title => "Runtime Monitor";

        /// <inheritdoc />
        public string Subtitle => "What the running player is doing";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Run;

        /// <inheritdoc />
        /// <remarks>
        /// Three static reads, no allocation, safe off play mode. Deliberately not "Ok" when the game
        /// is not running: there is nothing to be well or unwell, and a green dot for a screen that
        /// cannot see anything is the exact thing this window exists to stop.
        /// </remarks>
        public SectionHealth GetHealth()
        {
            if (!EditorApplication.isPlaying)
                return SectionHealth.NotMeasured("Not in play mode, so there is no session to read.");

            if (!CdnManager.IsInitialized)
                return SectionHealth.Warning("not initialised",
                    "Play mode is running but nothing has called CdnManager.InitializeAsync.");

            if (CdnDownloadMonitor.IsDownloading)
            {
                int pct = Mathf.RoundToInt(CdnDownloadMonitor.Current.Percent * 100f);
                return SectionHealth.Ok(pct + "% downloading");
            }

            return CdnManager.NetworkState == NetworkReachabilityState.Offline
                ? SectionHealth.Warning("offline", "The device reports no reachable network.")
                : SectionHealth.Ok("live");
        }

        /// <inheritdoc />
        public void PopulateHeaderActions(VisualElement container)
        {
            var attach = new Button(() => EditorApplication.ExecuteMenuItem("Window/General/Console"))
            {
                text = "Attach to player",
            };
            attach.AddToClassList("hub-btn");
            attach.tooltip =
                "Opens the Console, where a built player connects for logs. This screen reads the " +
                "Editor's own play session; a device build reports through the Console instead.";
            container.Add(attach);
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "monitor-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown()
        {
            Rebuild();

            // Scheduled on the view, so it dies with the view. A poll registered on the window would
            // outlive the section and keep reading a screen nobody is looking at.
            _poll = _body.schedule.Execute(Rebuild).Every(1000);
            _body.RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause(), TrickleDown.TrickleDown);
        }

        // ------------------------------------------------------------------ view

        private void Rebuild()
        {
            if (_body == null || _body.panel == null) return;

            // Same reason as Asset Lifetime: this refills its ScrollView on a one-second poll, and
            // a rebuild that does not restore the offset makes anything below the fold unreadable.
            var scroll = _body as ScrollView;
            var offset = scroll != null ? scroll.scrollOffset : Vector2.zero;

            _body.Clear();
            if (scroll != null) scroll.schedule.Execute(() => scroll.scrollOffset = offset);

            var panels = new VisualElement();
            panels.AddToClassList("hub-split");
            panels.Add(Wrap("hub-split-left", BuildStatePanel()));
            panels.Add(Wrap("hub-split-right", BuildDownloadPanel()));
            _body.Add(panels);

            if (_outcome != null)
                _body.Add(BuildOutcome());

            _body.Add(BuildFooter());
        }

        private static VisualElement Wrap(string cls, VisualElement child)
        {
            var host = new VisualElement();
            host.AddToClassList(cls);
            host.Add(child);
            return host;
        }

        private static VisualElement BuildStatePanel()
        {
            var card = Panel("Runtime state", out var body);

            if (!EditorApplication.isPlaying)
            {
                body.Add(Kv(HealthState.NotMeasured, "Session", "not in play mode"));
                return card;
            }

            if (!CdnManager.IsInitialized)
            {
                body.Add(Kv(HealthState.Warning, "Session", "not initialised"));
                body.Add(Kv(HealthState.NotMeasured, "Environment", "unknown until init"));
                return card;
            }

            body.Add(Kv(HealthState.Ok, "Environment", CdnManager.CurrentEnvironmentId));
            body.Add(Kv(HealthState.Ok, "Base URL", CdnManager.CurrentBaseUrl));

            var network = CdnManager.NetworkState;
            body.Add(Kv(
                network == NetworkReachabilityState.Offline ? HealthState.Warning : HealthState.Ok,
                "Network", network.ToString()));

            var cache = CdnManager.GetCacheStats();
            if (cache.IsValid)
            {
                body.Add(Kv(HealthState.Ok, "Cache used", FormatBytes(cache.OccupiedBytes)));
                body.Add(Kv(HealthState.Ok, "Cache free",
                    cache.FreeBytes >= 0 ? FormatBytes(cache.FreeBytes) : "unknown"));
                body.Add(Kv(HealthState.Ok, "Cache path", cache.Path));
            }
            else
            {
                // Distinct from "0 bytes". The platform did not report, which is not an empty cache,
                // and showing it as one would be a measurement nobody took.
                body.Add(Kv(HealthState.NotMeasured, "Cache", "not reported by this platform"));
            }

            return card;
        }

        private static VisualElement BuildDownloadPanel()
        {
            var card = Panel("Download", out var body);
            body.style.paddingTop = 10;
            body.style.paddingBottom = 10;

            if (!CdnDownloadMonitor.IsDownloading)
            {
                var idle = new Label(EditorApplication.isPlaying
                    ? "No transfer in progress."
                    : "No session.");
                idle.AddToClassList("hub-note-text");
                HubStyle.Text(idle, HealthState.NotMeasured);
                body.Add(idle);
                return card;
            }

            var p = CdnDownloadMonitor.Current;

            var head = new VisualElement();
            head.AddToClassList("hub-stat-valuerow");
            head.style.justifyContent = Justify.SpaceBetween;

            // Percent is a 0..1 FRACTION. Three places state the unit; the tab that divided it by 100
            // is why a finished download once sat at 0.01 on a 0..1 bar.
            var pct = new Label(Mathf.RoundToInt(p.Percent * 100f) + "%");
            pct.AddToClassList("hub-stat-value");
            head.Add(pct);

            var eta = new Label(p.EtaSeconds >= 0 ? $"{p.EtaSeconds:F0}s left" : "estimating");
            eta.AddToClassList("hub-stat-unit");
            head.Add(eta);
            body.Add(head);

            var track = new VisualElement();
            track.AddToClassList("hub-progress-track");

            var fill = new VisualElement();
            fill.AddToClassList("hub-progress-fill");
            fill.style.width = new StyleLength(Length.Percent(Mathf.Clamp01(p.Percent) * 100f));
            track.Add(fill);
            body.Add(track);

            string speed = p.BytesPerSecond > 1
                ? $" · {p.BytesPerSecond / (1024 * 1024):F2} MB/s"
                : string.Empty;

            var detail = new Label(p.TotalBytes > 0
                ? $"{p.DownloadedBytes / (1024f * 1024f):F2} / {p.TotalBytes / (1024f * 1024f):F2} MB{speed}"
                : $"{p.DownloadedBytes / (1024f * 1024f):F2} MB{speed}");

            detail.AddToClassList("hub-rule-id");
            detail.style.marginTop = 7;
            body.Add(detail);

            return card;
        }

        private static VisualElement BuildOutcome()
        {
            var card = new VisualElement();
            card.AddToClassList("hub-outcome");

            var text = new VisualElement();
            text.style.flexGrow = 1;
            text.style.minWidth = 0;

            var head = new Label(_outcome);
            head.AddToClassList("hub-rule-headline");
            HubStyle.Text(head, _outcomeState);
            text.Add(head);

            if (!string.IsNullOrEmpty(_outcomeDetail))
            {
                var sub = new Label(_outcomeDetail);
                sub.AddToClassList("hub-rule-meta");
                text.Add(sub);
            }

            card.Add(text);

            var hint = new Label("stays until you act again");
            hint.AddToClassList("hub-stat-caption");
            hint.style.flexShrink = 0;
            hint.style.marginLeft = 8;
            card.Add(hint);

            return card;
        }

        private VisualElement BuildFooter()
        {
            var row = new VisualElement();
            row.AddToClassList("hub-rule-actions");
            row.style.marginTop = 8;

            var note = new Label(
                "Sampled once a second. A fast transfer — seventy bundles off a local server in " +
                "about a second — can finish between two samples; the Local Server request log is " +
                "the reliable view.");
            note.AddToClassList("hub-note-text");
            note.style.flexGrow = 1;
            note.style.flexShrink = 1;
            note.style.minWidth = 0;
            row.Add(note);

            bool live = EditorApplication.isPlaying && CdnManager.IsInitialized;

            row.Add(Action("Check for update", live, CheckForUpdate,
                "Asks the CDN whether a newer catalog exists."));

            row.Add(Action("Clean obsolete", live, CleanObsolete,
                "Removes bundles superseded by a catalog update."));

            var clear = Action("Clear cache…", live, ClearCache,
                "Everything will have to be downloaded again.");
            clear.AddToClassList("hub-btn--danger");
            row.Add(clear);

            return row;
        }

        private static Button Action(string text, bool enabled, Action onClick, string tooltip)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("hub-btn");
            button.SetEnabled(enabled);
            button.tooltip = enabled
                ? tooltip
                : "Needs a running play session with the CDN layer initialised.";
            return button;
        }

        // ------------------------------------------------------------------ actions

        /// <remarks>
        /// <c>await</c>, not a boxed task and a type test. With UniTask installed these return
        /// <c>UniTask&lt;T&gt;</c> - a struct, not a <c>Task&lt;T&gt;</c> - so a type test fails, the
        /// body never runs and the button silently does nothing. That is the configuration most
        /// projects ship. <c>async void</c> is right here: a UI handler has no caller to return to,
        /// which is what the try/catch is for.
        /// </remarks>
        private async void CheckForUpdate()
        {
            if (!CdnManager.IsInitialized) return;

            SetOutcome(HealthState.NotMeasured, "Checking for updates…", null);

            try
            {
                var result = await CdnManager.CheckForUpdateAsync();

                if (result.IsFailure)
                    SetOutcome(HealthState.Blocked, "Check failed", result.ErrorMessage);
                else if (result.Value.WasOfflineFallback)
                    // Not the same as "up to date": the check never reached the server. Saying
                    // up-to-date here tells a player their game is current when nobody asked the CDN.
                    SetOutcome(HealthState.Warning, "Offline — could not check",
                        "The request never reached the CDN, so nothing was compared.");
                else if (result.Value.HasUpdate)
                    SetOutcome(HealthState.Warning,
                        result.Value.CatalogsWithUpdates.Count + " catalog(s) have updates",
                        "Checked against " + CdnManager.CurrentBaseUrl + ".");
                else
                    SetOutcome(HealthState.Ok, "Content is up to date",
                        "Checked against " + CdnManager.CurrentBaseUrl + " — catalog hash matched.");
            }
            catch (Exception ex)
            {
                SetOutcome(HealthState.Blocked, "Check threw", ex.Message);
            }

            Rebuild();
        }

        private async void CleanObsolete()
        {
            var cache = CdnManager.Cache;
            if (cache == null) return;

            SetOutcome(HealthState.NotMeasured, "Cleaning obsolete bundles…", null);

            try
            {
                var result = await cache.CleanObsoleteAsync();

                SetOutcome(
                    result.IsFailure ? HealthState.Blocked : HealthState.Ok,
                    result.IsFailure ? "Clean failed" : "Obsolete bundles removed",
                    result.IsFailure
                        ? result.ErrorMessage
                        : FormatBytes(result.Value.OccupiedBytes) + " still in cache.");
            }
            catch (Exception ex)
            {
                SetOutcome(HealthState.Blocked, "Clean threw", ex.Message);
            }

            Rebuild();
        }

        private void ClearCache()
        {
            var cache = CdnManager.Cache;
            if (cache == null) return;

            bool confirmed = EditorUtility.DisplayDialog(
                "Clear the bundle cache",
                "Everything will have to be downloaded again. Unity refuses while bundles are still " +
                "loaded, so this may report a failure rather than clearing.",
                "Clear", "Cancel");

            if (!confirmed) return;

            var result = cache.ClearAll();

            SetOutcome(
                result.IsFailure ? HealthState.Blocked : HealthState.Ok,
                result.IsFailure ? "Cache not cleared" : "Cache cleared",
                result.IsFailure ? result.ErrorMessage : null);

            Rebuild();
        }

        private static void SetOutcome(HealthState state, string headline, string detail)
        {
            _outcomeState = state;
            _outcome = headline;
            _outcomeDetail = detail;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("F1") + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024f * 1024f)).ToString("F1") + " MB";
            return (bytes / (1024f * 1024f * 1024f)).ToString("F2") + " GB";
        }

        // ------------------------------------------------------------------ chrome

        private static VisualElement Panel(string title, out VisualElement body)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var label = new Label(title);
            label.AddToClassList("hub-card-title");
            head.Add(label);
            card.Add(head);

            body = new VisualElement();
            body.style.paddingLeft = 9;
            body.style.paddingRight = 9;
            body.style.paddingTop = 6;
            body.style.paddingBottom = 6;
            card.Add(body);

            return card;
        }

        private static VisualElement Kv(HealthState state, string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-kv");

            var dot = new VisualElement();
            dot.AddToClassList("hub-summary-dot");
            HubStyle.Fill(dot, state);
            row.Add(dot);

            var k = new Label(key);
            k.AddToClassList("hub-kv-key");
            row.Add(k);

            var v = new Label(string.IsNullOrEmpty(value) ? "—" : value);
            v.AddToClassList("hub-kv-value");
            v.tooltip = value;
            HubStyle.Text(v, state == HealthState.Ok ? HealthState.Ok : state);
            row.Add(v);

            return row;
        }
    }
}
