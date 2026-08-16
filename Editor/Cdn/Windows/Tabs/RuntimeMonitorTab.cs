using System;
using System.Text;
using AddressableManager.Cdn;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Live CDN state during play mode — task 4.6.
    /// </summary>
    /// <remarks>
    /// Everything here is read from the running <see cref="CdnManager"/>, so outside play mode there
    /// is nothing to show and the tab says so rather than displaying stale or invented values. That
    /// is the whole point of the tab: the other three answer questions about the project, this one
    /// answers questions about the session in front of you.
    ///
    /// POLLED, NOT PUSHED
    /// The runtime has no change events to subscribe to, and adding them would put an editor concern
    /// into shipping code. A 1 Hz poll while the tab is visible is cheap and stops when the view is
    /// detached — the DetachFromPanelEvent callback is what makes that reliable, since a tab is
    /// rebuilt from scratch every time it becomes active and there is no teardown hook from the shell.
    /// </remarks>
    public sealed class RuntimeMonitorTab : ICdnManagerTab
    {
        public string TabName => "Runtime Monitor";

        private HelpBox _summary;
        private VisualElement _state;
        private Label _downloadLabel;
        private ProgressBar _downloadBar;
        private Button _checkButton;
        private Button _cleanButton;
        private Button _clearButton;
        private Button _refreshButton;

        private IVisualElementScheduledItem _poll;

        public VisualElement CreateView()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/RuntimeMonitorTab.uxml");

            if (tree == null)
            {
                Debug.LogError("[RuntimeMonitorTab] Failed to load RuntimeMonitorTab.uxml.");
                var fallback = new VisualElement();
                fallback.Add(new Label("Failed to load UI."));
                return fallback;
            }

            var root = tree.CloneTree();

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/RuntimeMonitorTab.uss");
            if (uss != null) root.styleSheets.Add(uss);

            _summary = root.Q<HelpBox>("monitor-summary");
            _state = root.Q<VisualElement>("monitor-state");
            _downloadLabel = root.Q<Label>("monitor-download-label");
            _downloadBar = root.Q<ProgressBar>("monitor-download-bar");
            _checkButton = root.Q<Button>("monitor-check-btn");
            _cleanButton = root.Q<Button>("monitor-clean-btn");
            _clearButton = root.Q<Button>("monitor-clear-btn");
            _refreshButton = root.Q<Button>("monitor-refresh-btn");

            _refreshButton.clicked += Refresh;
            _checkButton.clicked += CheckForUpdate;
            _cleanButton.clicked += CleanObsolete;
            _clearButton.clicked += ClearCache;

            // Poll while visible. Stopped on detach so a tab that is no longer shown does not keep
            // waking the editor.
            _poll = root.schedule.Execute(Refresh).Every(1000);
            root.RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause(), TrickleDown.TrickleDown);

            return root;
        }

        public void OnShown() => Refresh();

        private void Refresh()
        {
            _state.Clear();

            if (!EditorApplication.isPlaying)
            {
                Set(HelpBoxMessageType.Info,
                    "Not in play mode. This tab reads the live CdnManager, so there is nothing to " +
                    "report until the game is running.");
                SetActionsEnabled(false);
                SetDownloadIdle("No session");
                return;
            }

            if (!CdnManager.IsInitialized)
            {
                Set(HelpBoxMessageType.Warning,
                    "Play mode is running but the CDN layer has not initialised. Either the game has " +
                    "not called CdnManager.InitializeAsync yet, or it failed — check the console.");
                SetActionsEnabled(false);
                SetDownloadIdle("Not initialised");
                return;
            }

            Set(HelpBoxMessageType.Info, "CDN layer is live.");
            SetActionsEnabled(true);

            AddRow("Environment", CdnManager.CurrentEnvironmentId, false);
            AddRow("Base URL", CdnManager.CurrentBaseUrl, false);

            var network = CdnManager.NetworkState;
            AddRow("Network", network.ToString(), network == NetworkReachabilityState.Offline);

            var cache = CdnManager.GetCacheStats();
            if (cache.IsValid)
            {
                AddRow("Cache used", FormatBytes(cache.OccupiedBytes), false);
                AddRow("Cache free", cache.FreeBytes >= 0 ? FormatBytes(cache.FreeBytes) : "unknown", false);
                AddRow("Cache path", cache.Path, false);
            }
            else
            {
                // Distinct from "0 bytes": the platform did not report, which is not the same as an
                // empty cache and must not be shown as one.
                AddRow("Cache", "not reported by this platform", false);
            }

            SetDownloadIdle("No download in progress");
        }

        /// <summary>Check for a newer catalog and report the outcome on the label.</summary>
        /// <remarks>
        /// `await`, not a boxed task and a type test.
        ///
        /// The previous version handed the returned task to `object` and matched it with
        /// `is Task&lt;CdnResult&lt;CatalogUpdateInfo&gt;&gt;`, on the reasoning that boxing kept the file
        /// free of the UNITASK_PRESENT conditional. It kept the file free of the conditional by
        /// being wrong under it: with UniTask installed the method returns UniTask&lt;T&gt;, a struct
        /// that is not a Task&lt;T&gt;, so the match failed, the body never ran, and the button did
        /// nothing at all. Silently — no exception, no log, just a label that never changed. That
        /// is the configuration every project with UniTask ships, which is most of them.
        ///
        /// `await` needs no conditional because both are awaitable and both yield the same
        /// CdnResult. The dual signature lives in CdnManager where repo Invariant 3 puts it; a
        /// caller does not have to care, which was the point of the invariant.
        ///
        /// async void is deliberate: this is a UI event handler, there is no caller to return to,
        /// and the try/catch is what an unobserved async void would otherwise cost.
        /// </remarks>
        private async void CheckForUpdate()
        {
            if (!CdnManager.IsInitialized) return;

            _downloadLabel.text = "Checking for updates...";

            try
            {
                var result = await CdnManager.CheckForUpdateAsync();

                // The view is rebuilt on every tab switch, so the element awaited on may already be
                // detached. Writing to it would not throw, it would just be invisible — checking is
                // how a stale write stays out of a live tab.
                if (_downloadLabel == null || _downloadLabel.panel == null) return;

                _downloadLabel.text = result.IsFailure
                    ? $"Check failed: {result.ErrorMessage}"
                    : result.Value.WasOfflineFallback
                        // Not the same as "up to date": the check never reached the server. Saying
                        // "up to date" here is how a player is told their game is current when
                        // nobody asked the CDN.
                        ? "Offline — could not check"
                        : result.Value.HasUpdate
                            ? $"{result.Value.CatalogsWithUpdates.Count} catalog(s) have updates"
                            : "Content is up to date";

                Refresh();
            }
            catch (Exception ex)
            {
                if (_downloadLabel != null && _downloadLabel.panel != null)
                    _downloadLabel.text = $"Check threw: {ex.Message}";
            }
        }

        /// <summary>Remove bundles superseded by a catalog update, and report what happened.</summary>
        /// <remarks>
        /// Awaited rather than fired and forgotten. The previous version started the clean, queued a
        /// Refresh on the next delayCall, and returned — so the panel refreshed before the clean had
        /// done anything and reported the cache size it had a moment ago. It also discarded the
        /// result, so a clean that failed looked identical to one that worked.
        /// </remarks>
        private async void CleanObsolete()
        {
            var cache = CdnManager.Cache;
            if (cache == null) return;

            _downloadLabel.text = "Cleaning obsolete bundles...";

            try
            {
                var result = await cache.CleanObsoleteAsync();

                if (_downloadLabel == null || _downloadLabel.panel == null) return;

                _downloadLabel.text = result.IsFailure
                    ? $"Clean failed: {result.ErrorMessage}"
                    : $"Obsolete bundles removed — {FormatBytes(result.Value.OccupiedBytes)} still in cache";

                Refresh();
            }
            catch (Exception ex)
            {
                if (_downloadLabel != null && _downloadLabel.panel != null)
                    _downloadLabel.text = $"Clean threw: {ex.Message}";
            }
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
            _downloadLabel.text = result.IsFailure ? result.ErrorMessage : "Cache cleared";
            Refresh();
        }

        // ========== helpers ==========

        private void Set(HelpBoxMessageType type, string message)
        {
            _summary.messageType = type;
            _summary.text = message;
        }

        private void SetActionsEnabled(bool enabled)
        {
            _checkButton.SetEnabled(enabled);
            _cleanButton.SetEnabled(enabled);
            _clearButton.SetEnabled(enabled);
        }

        private void SetDownloadIdle(string text)
        {
            _downloadLabel.text = text;
            _downloadBar.value = 0;
        }

        private void AddRow(string key, string value, bool isBad)
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-state-row");

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("cdn-state-key");
            row.Add(keyLabel);

            var valueLabel = new Label(string.IsNullOrEmpty(value) ? "—" : value);
            valueLabel.AddToClassList("cdn-state-value");
            valueLabel.AddToClassList(isBad ? "cdn-state-value-bad" : "cdn-state-value-good");
            valueLabel.tooltip = value;
            row.Add(valueLabel);

            _state.Add(row);
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "unknown";
            if (bytes < 1024) return $"{bytes} B";

            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:F0} KB";

            double mb = kb / 1024.0;
            return mb < 1024 ? $"{mb:F1} MB" : $"{mb / 1024.0:F2} GB";
        }
    }
}
