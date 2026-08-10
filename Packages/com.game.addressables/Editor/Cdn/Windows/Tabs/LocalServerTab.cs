using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Design doc §5.9/§5.10 "Local Server" tab (task 0.10): start/stop a <see cref="LocalContentServer"/>,
    /// edit its port, and show a live request log. Surfacing
    /// <see cref="LocalContentServer.RequestEventArgs.PathMatchesInfraPolicy"/> prominently is the whole
    /// point of this tab - catching a Cache-Control mismatch here is cheaper than catching it in
    /// production (risk R3, infra §3's cache policy matrix).
    /// </summary>
    /// <remarks>
    /// <c>_server</c> is <see cref="LocalContentServerMenu.Instance"/> - the same shared instance the
    /// "Tools ▸ Addressable Manager ▸ Start/Stop Local Content Server" menu drives, not a private server
    /// this tab owns. That makes this tab one of potentially several observers of a resource whose
    /// lifetime it does not control:
    ///
    /// <list type="bullet">
    /// <item>Every visible control is re-derived from the shared instance's *live* state on every
    /// refresh - <see cref="LocalContentServer.IsRunning"/>, <see cref="LocalContentServer.ActivePort"/>,
    /// <see cref="LocalContentServer.ServerDataPath"/> - never cached in a field on this class. So
    /// <see cref="OnShown"/> attaching to a server the Tools menu (or a previous window session) already
    /// started shows the real running state, the real port and the real serving path immediately, not a
    /// "not running" / default-port assumption.</item>
    /// <item>This tab must never stop or dispose the shared server as a side effect of its own lifecycle.
    /// <see cref="OnViewDetached"/> (tab switch or window close) unsubscribes from
    /// <see cref="LocalContentServer.RequestReceived"/> and the drain tick only - it never calls
    /// <c>_server.Stop()</c>. The only thing that stops the shared server from this tab is the user
    /// explicitly clicking "Stop Server" (<see cref="OnServerToggleChanged"/>), exactly mirroring the
    /// menu's own Stop item - and doing so stops it for every other observer too, which is the correct,
    /// expected behaviour for a shared resource.</item>
    /// </list>
    /// </remarks>
    public sealed class LocalServerTab : ICdnManagerTab
    {
        public string TabName => "Server";

        /// <summary>
        /// Hard cap on retained log rows. A long content-update test can generate thousands of range
        /// requests for a single large bundle; without a cap, both the backing list and (if a naive
        /// non-virtualized layout were used) the visual tree would grow without bound for the life of the
        /// window. <see cref="ListView"/> already bounds the *visual element* count to roughly what is on
        /// screen (it recycles rows via makeItem/bindItem instead of creating one element per entry) - this
        /// cap bounds the *data* side the same way, so neither memory nor the cost of a re-bind grows with
        /// session length. 500 keeps a generous scrollback (the newest requests, which is what this tab's
        /// diagnostic purpose actually needs) while staying trivially cheap to re-sort/trim every frame.
        /// </summary>
        private const int MaxLogEntries = 500;

        // Shared, not owned - see the class remarks. LocalContentServerMenu.Instance lazily creates the
        // one server the whole editor session uses, the same instance "Tools ▸ Addressable Manager ▸
        // Start/Stop Local Content Server" drives.
        private readonly LocalContentServer _server = LocalContentServerMenu.Instance;

        // The only thread-safe hand-off point in this file. LocalContentServer raises RequestReceived on
        // an HttpListener thread-pool thread and deliberately makes no Unity API calls itself (see its
        // class doc) - so OnRequestReceived below must not touch UIElements/EditorGUIUtility/etc. either.
        // It only enqueues; DrainPendingEvents (polled from EditorApplication.update, main thread) is
        // where queued events become UI. Everything downstream of the drain is single-threaded.
        private readonly ConcurrentQueue<LocalContentServer.RequestEventArgs> _pendingEvents =
            new ConcurrentQueue<LocalContentServer.RequestEventArgs>();

        // Long-lived log data, owned by this class (not the view) so switching tabs and back does not
        // lose history. Newest entry first - see DrainPendingEvents.
        private readonly List<LogRow> _logEntries = new List<LogRow>();

        private int _unmatchedTotal;
        private Texture2D _warningIcon;

        private HelpBox _statusBox;
        private TextField _portField;
        private ToolbarToggle _serverToggle;
        private ListView _logList;
        private Label _logCountLabel;
        private Button _clearLogButton;

        public VisualElement CreateView()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/LocalServerTab.uxml");

            VisualElement root;
            if (visualTree != null)
            {
                root = visualTree.CloneTree();
            }
            else
            {
                Debug.LogError("[LocalServerTab] Failed to load LocalServerTab.uxml. Creating fallback UI.");
                root = CreateFallbackUI();
                return root;
            }

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/LocalServerTab.uss");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            _statusBox = root.Q<HelpBox>("server-status");
            _portField = root.Q<TextField>("server-port-field");
            _logList = root.Q<ListView>("server-log-list");
            _logCountLabel = root.Q<Label>("server-log-count");
            _clearLogButton = root.Q<Button>("server-clear-log-btn");

            // Built in code, not declared in LocalServerTab.uxml - mirrors CdnManagerWindow.BuildTabStrip's
            // own ToolbarToggle construction exactly, the only proven usage of this control in this package.
            var toolbar = root.Q<Toolbar>("server-toolbar");
            _serverToggle = new ToolbarToggle { name = "server-toggle", text = "Start Server" };
            _serverToggle.AddToClassList("cdn-server-toggle");
            toolbar.Add(_serverToggle);

            _warningIcon = EditorGUIUtility.IconContent("Warning")?.image as Texture2D;

            _serverToggle.RegisterValueChangedCallback(OnServerToggleChanged);
            _clearLogButton.clicked += ClearLog;

            ConfigureLogList();

            // Per ICdnManagerTab's own remarks: long-lived state (the shared server, the pending-event
            // queue) lives in a holder that outlives the view, and this tab only listens for
            // RequestReceived / polls the drain tick while its view is actually attached - stop LISTENING
            // when the view is torn down, whether that is a tab switch or the window closing, rather than
            // relying on a teardown hook from the shell (CdnManagerWindow has none). See OnViewDetached for
            // why this must not also stop the server itself.
            root.RegisterCallback<DetachFromPanelEvent>(OnViewDetached, TrickleDown.TrickleDown);

            return root;
        }

        public void OnShown()
        {
            if (_logList == null) return; // fallback UI - nothing to wire up

            // Defensive -= before += (same idiom as AddressableAutoProcessor.cs elsewhere in this
            // package): guards against a double subscription if OnShown were ever invoked twice without an
            // intervening detach, at zero cost in the normal case.
            _server.RequestReceived -= OnRequestReceived;
            _server.RequestReceived += OnRequestReceived;
            EditorApplication.update -= DrainPendingEvents;
            EditorApplication.update += DrainPendingEvents;

            RefreshServerState();
            RefreshLogView();
        }

        /// <summary>
        /// Stops this tab from listening - never stops or disposes <c>_server</c>. <c>_server</c> is
        /// shared (<see cref="LocalContentServerMenu.Instance"/>), so tearing down this tab's view (a tab
        /// switch, or the window closing) must not affect whether the server is running for the Tools
        /// menu or any other observer. Only <see cref="OnServerToggleChanged"/>'s explicit "Stop Server"
        /// path is allowed to call <c>_server.Stop()</c>.
        /// </summary>
        private void OnViewDetached(DetachFromPanelEvent evt)
        {
            _server.RequestReceived -= OnRequestReceived;
            EditorApplication.update -= DrainPendingEvents;
        }

        private void OnRequestReceived(object sender, LocalContentServer.RequestEventArgs e)
        {
            _pendingEvents.Enqueue(e);
        }

        /// <summary>
        /// Runs on the main thread every editor tick. Batches however many requests arrived since the last
        /// tick into a single log/UI update instead of one per request - the difference between one
        /// ListView.Rebuild() per frame and one per request matters once a content download is firing
        /// dozens of range requests within a few milliseconds of each other.
        /// </summary>
        private void DrainPendingEvents()
        {
            if (_pendingEvents.IsEmpty) return;

            bool any = false;
            while (_pendingEvents.TryDequeue(out var e))
            {
                _logEntries.Insert(0, BuildLogRow(e)); // newest first - no auto-scroll tug-of-war with the user
                if (!e.PathMatchesInfraPolicy) _unmatchedTotal++;
                any = true;
            }

            if (!any) return;

            if (_logEntries.Count > MaxLogEntries)
                _logEntries.RemoveRange(MaxLogEntries, _logEntries.Count - MaxLogEntries);

            RefreshLogView();
            RefreshServerState(); // unmatched total feeds the status banner
        }

        private void OnServerToggleChanged(ChangeEvent<bool> evt)
        {
            if (evt.newValue)
            {
                if (!TryParsePort(_portField.value, out int port))
                {
                    Debug.LogError($"[LocalServerTab] '{_portField.value}' is not a valid port (1-65535).");
                    RefreshServerState(); // snaps the toggle back off - it never actually started
                    return;
                }

                _server.Start(port);
            }
            else
            {
                _server.Stop();
            }

            RefreshServerState();
        }

        private void ClearLog()
        {
            _logEntries.Clear();
            _unmatchedTotal = 0;
            RefreshLogView();
            RefreshServerState();
        }

        /// <summary>
        /// Re-derives every visible control from the shared server's *live* state - never a value cached
        /// on this class - so this correctly handles both a failed Start() (bad port, port already in use,
        /// access denied - LocalContentServer logs the reason; snaps the toggle back to "stopped" instead
        /// of showing it stuck "on" for a server that never started) and attaching to a server some other
        /// observer already had running (the Tools menu, or this same tab in an earlier window session) -
        /// the port field picks up <see cref="LocalContentServer.ActivePort"/> instead of showing whatever
        /// default or stale value it last displayed.
        /// </summary>
        private void RefreshServerState()
        {
            bool running = _server.IsRunning;
            _serverToggle.SetValueWithoutNotify(running);
            _serverToggle.text = running ? "Stop Server" : "Start Server";
            _portField.SetEnabled(!running);

            if (!running)
            {
                _statusBox.messageType = HelpBoxMessageType.Warning;
                _statusBox.text = "Server is not running.";
                return;
            }

            // Only overwritten while running - while stopped this must not clobber a port the user is
            // mid-typing for the next Start().
            _portField.SetValueWithoutNotify(_server.ActivePort.ToString());

            if (_unmatchedTotal > 0)
            {
                _statusBox.messageType = HelpBoxMessageType.Warning;
                _statusBox.text = $"Serving {_server.ServerDataPath} on http://localhost:{_server.ActivePort}. " +
                    $"{_unmatchedTotal} request{(_unmatchedTotal == 1 ? "" : "s")} did not match the infra §3 " +
                    "cache policy matrix (highlighted below).";
            }
            else
            {
                _statusBox.messageType = HelpBoxMessageType.Info;
                _statusBox.text = $"Serving {_server.ServerDataPath} on http://localhost:{_server.ActivePort}.";
            }
        }

        private void RefreshLogView()
        {
            _logList.itemsSource = _logEntries;
            _logList.Rebuild();
            _logCountLabel.text = _logEntries.Count >= MaxLogEntries
                ? $"{_logEntries.Count} requests (newest {MaxLogEntries} kept)"
                : $"{_logEntries.Count} request{(_logEntries.Count == 1 ? "" : "s")}";
        }

        private void ConfigureLogList()
        {
            _logList.itemsSource = _logEntries;
            _logList.makeItem = MakeLogRow;
            _logList.bindItem = BindLogRow;
            _logList.fixedItemHeight = 20;
            _logList.selectionType = SelectionType.None; // a log, not a picker
            _logList.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
        }

        private VisualElement MakeLogRow()
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-log-row");

            var icon = new Image { name = "log-icon" };
            icon.AddToClassList("cdn-log-icon");
            row.Add(icon);

            var time = new Label { name = "log-time" };
            time.AddToClassList("cdn-log-time");
            row.Add(time);

            var method = new Label { name = "log-method" };
            method.AddToClassList("cdn-log-method");
            row.Add(method);

            var path = new Label { name = "log-path" };
            path.AddToClassList("cdn-log-path");
            row.Add(path);

            var status = new Label { name = "log-status" };
            status.AddToClassList("cdn-log-status");
            row.Add(status);

            var cacheControl = new Label { name = "log-cache-control" };
            cacheControl.AddToClassList("cdn-log-cache-control");
            row.Add(cacheControl);

            var size = new Label { name = "log-size" };
            size.AddToClassList("cdn-log-size");
            row.Add(size);

            var tag = new Label("unmatched") { name = "log-tag-unmatched" };
            tag.AddToClassList("cdn-log-tag-unmatched");
            row.Add(tag);

            return row;
        }

        private void BindLogRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _logEntries.Count) return;
            var entry = _logEntries[index];

            element.EnableInClassList("cdn-log-row-unmatched", !entry.MatchesPolicy);

            var icon = element.Q<Image>("log-icon");
            icon.image = entry.MatchesPolicy ? null : _warningIcon; // slot always reserved - only the texture toggles, so columns stay aligned

            element.Q<Label>("log-time").text = entry.Time;
            element.Q<Label>("log-method").text = entry.Method;
            element.Q<Label>("log-path").text = entry.Path;

            var statusLabel = element.Q<Label>("log-status");
            statusLabel.text = entry.Status;
            statusLabel.EnableInClassList("cdn-log-status-error", entry.IsErrorStatus);

            element.Q<Label>("log-cache-control").text = entry.CacheControl;
            element.Q<Label>("log-size").text = entry.Size;

            element.Q<Label>("log-tag-unmatched").style.display =
                entry.MatchesPolicy ? DisplayStyle.None : DisplayStyle.Flex;

            element.tooltip = entry.MatchesPolicy
                ? $"{entry.Method} {entry.Path}\nStatus: {entry.Status}    Cache-Control: {entry.CacheControl}    Size: {entry.Size}"
                : $"{entry.Method} {entry.Path}\nStatus: {entry.Status}    Cache-Control: {entry.CacheControl}    Size: {entry.Size}" +
                  "\n\nNo infra §3 cache policy rule matched this path - served no-store as the safe default (risk R3).";
        }

        private static LogRow BuildLogRow(LocalContentServer.RequestEventArgs e)
        {
            return new LogRow(
                time: e.Timestamp.ToString("HH:mm:ss"),
                method: string.IsNullOrEmpty(e.Method) ? "-" : e.Method,
                path: string.IsNullOrEmpty(e.Path) ? "-" : e.Path,
                status: e.StatusCode.ToString(),
                cacheControl: string.IsNullOrEmpty(e.CacheControl) ? "-" : e.CacheControl,
                size: FormatBytes(e.BytesSent),
                matchesPolicy: e.PathMatchesInfraPolicy,
                isErrorStatus: e.StatusCode >= 400);
        }

        private static bool TryParsePort(string text, out int port) =>
            int.TryParse(text, out port) && port > 0 && port <= 65535;

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";

            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024d && unitIndex < units.Length - 1)
            {
                value /= 1024d;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{value:F0} {units[unitIndex]}" : $"{value:F1} {units[unitIndex]}";
        }

        private VisualElement CreateFallbackUI()
        {
            var fallback = new VisualElement();
            fallback.style.flexGrow = 1;
            fallback.style.alignItems = Align.Center;
            fallback.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check that Editor/Cdn/UI/LocalServerTab.uxml exists.");
            label.style.color = Color.red;
            fallback.Add(label);

            return fallback;
        }

        /// <summary>One pre-formatted, display-ready log row. Built once per event in <see cref="BuildLogRow"/>
        /// rather than re-formatted on every bind, since a ListView re-invokes bindItem every time a
        /// recycled row scrolls onto a different index.</summary>
        private readonly struct LogRow
        {
            public readonly string Time;
            public readonly string Method;
            public readonly string Path;
            public readonly string Status;
            public readonly string CacheControl;
            public readonly string Size;
            public readonly bool MatchesPolicy;
            public readonly bool IsErrorStatus;

            public LogRow(string time, string method, string path, string status, string cacheControl,
                string size, bool matchesPolicy, bool isErrorStatus)
            {
                Time = time;
                Method = method;
                Path = path;
                Status = status;
                CacheControl = cacheControl;
                Size = size;
                MatchesPolicy = matchesPolicy;
                IsErrorStatus = isErrorStatus;
            }
        }
    }
}
