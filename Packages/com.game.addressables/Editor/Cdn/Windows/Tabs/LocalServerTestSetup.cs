using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Cdn.Windows
{
    /// <summary>
    /// The controls that let the local server be made to fail on purpose, and that create content to
    /// fail against.
    /// </summary>
    /// <remarks>
    /// <b>The machinery already existed and nothing could reach it.</b> <see cref="ServerFaults"/> can
    /// return any status for any path, drop connections, throttle to a byte rate and add latency; it
    /// was called from exactly one place in the package, <c>CdnFaultInjectionTests</c>.
    /// <see cref="CdnTestContentCLI"/> can create remote test content and a whole corpus, and had no
    /// menu item, so both were reachable only from <c>-executeMethod</c> in batchmode. A capability
    /// nobody can press is a capability the team does not have.
    ///
    /// <b>Faults are ONE global state, not a stack, and this is drawn to say so.</b>
    /// <c>InjectStatus</c> and <c>InjectConnectionDrop</c> share the same path filter and the same
    /// request budget - setting one overwrites the other - and the <c>IDisposable</c> every injector
    /// returns calls <c>Clear()</c>, which removes ALL of them rather than the one it came from. A
    /// panel offering several independently-removable faults would be describing an engine that does
    /// not exist, so this offers one fault at a time and one way to remove it.
    ///
    /// <b>The banner is the important part.</b> The state is static and survives domain reloads, so a
    /// 503 switched on and forgotten makes everything afterwards fail with nothing on screen
    /// explaining why - and the next person debugs a CDN over a switch someone flipped ten minutes
    /// ago. While a fault is active this says so, in a colour that is hard to read past, on the screen
    /// that owns the server.
    /// </remarks>
    internal sealed class LocalServerTestSetup
    {
        private VisualElement _root;
        private VisualElement _banner;
        private Label _bannerText;

        private EnumField _kindField;
        private IntegerField _statusField;
        private TextField _pathField;
        private IntegerField _countField;
        private IntegerField _throttleField;
        private IntegerField _latencyField;

        private IVisualElementScheduledItem _poll;

        /// <summary>Which failure to inject. One at a time, because the engine holds one.</summary>
        private enum FaultKind
        {
            /// <summary>Return an HTTP status instead of the file.</summary>
            HttpStatus,

            /// <summary>Accept the connection and drop it mid-response.</summary>
            ConnectionDrop,

            /// <summary>Serve correctly, slowly.</summary>
            Throttle,

            /// <summary>Serve correctly, late.</summary>
            Latency,
        }

        public VisualElement Build()
        {
            _root = new VisualElement();
            _root.style.marginTop = 8;

            _root.Add(BuildBanner());
            _root.Add(BuildContentBlock());
            _root.Add(BuildFaultBlock());

            // The banner has to be right even when the fault was set by something else - a test, a
            // previous session, the CLI - so it is polled rather than only updated on click.
            _poll = _root.schedule.Execute(RefreshBanner).Every(500);
            _root.RegisterCallback<DetachFromPanelEvent>(_ => _poll?.Pause(), TrickleDown.TrickleDown);

            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            RefreshBanner();
            return _root;
        }

        // ------------------------------------------------------------------ banner

        private VisualElement BuildBanner()
        {
            _banner = new VisualElement();
            _banner.style.flexDirection = FlexDirection.Row;
            _banner.style.alignItems = Align.Center;
            _banner.style.paddingLeft = 10;
            _banner.style.paddingRight = 10;
            _banner.style.paddingTop = 7;
            _banner.style.paddingBottom = 7;
            _banner.style.marginBottom = 8;
            _banner.style.borderTopLeftRadius = 3;
            _banner.style.borderTopRightRadius = 3;
            _banner.style.borderBottomLeftRadius = 3;
            _banner.style.borderBottomRightRadius = 3;

            _bannerText = new Label();
            _bannerText.style.flexGrow = 1;
            _bannerText.style.flexShrink = 1;
            _bannerText.style.minWidth = 0;
            _bannerText.style.whiteSpace = WhiteSpace.Normal;
            _banner.Add(_bannerText);

            var clear = new Button(ClearFaults) { text = "Clear all faults" };
            clear.AddToClassList("cdn-btn");
            clear.style.flexShrink = 0;
            _banner.Add(clear);

            return _banner;
        }

        private void RefreshBanner()
        {
            if (_banner == null) return;

            bool active = ServerFaults.IsActive;

            // Hidden when nothing is wrong. A permanent "no faults active" strip is a line people
            // stop reading, which is the one thing this must not become.
            _banner.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;

            if (!active) return;

            _banner.style.backgroundColor = new StyleColor(new Color(0.32f, 0.22f, 0.22f));
            _bannerText.text =
                "The local server is failing on purpose. Every request it serves is affected until " +
                "this is cleared — including requests from a play session that has nothing to do " +
                "with what you were testing. This survives domain reloads.";
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            // Leaving play mode ends the session the fault was set up for. Carrying it into the next
            // one is how a forgotten 503 becomes an afternoon.
            if (change != PlayModeStateChange.EnteredEditMode) return;
            if (!ServerFaults.IsActive) return;

            ServerFaults.Clear();
            Debug.Log("[LocalServer] Cleared injected faults on leaving play mode.");
            RefreshBanner();
        }

        // ------------------------------------------------------------------ test content

        private VisualElement BuildContentBlock()
        {
            var box = Section("Test content",
                "Creates addressable content aimed at the remote group, so there is something to " +
                "serve and something to fail. Both of these existed only as batchmode entry points.");

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginTop = 6;

            row.Add(Action("Create test asset", CdnTestContentCLI.CreateRemoteTestContent,
                "One TextAsset with a known string, in the remote group. The integration tests " +
                "assert that exact string arrived over HTTP."));

            row.Add(Action("Generate corpus", CdnTestContentCLI.GenerateTestCorpus,
                "A larger spread of assets across groups — enough for a download to take long " +
                "enough to watch."));

            box.Add(row);
            return box;
        }

        private static Button Action(string text, Action onClick, string tooltip)
        {
            var button = new Button(() =>
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    // These write to the Addressables settings. A failure half-way through leaves the
                    // project in a state the reader needs told about, not a silent no-op.
                    Debug.LogError($"[LocalServer] {text} failed: {ex.Message}");
                    EditorUtility.DisplayDialog(text, ex.Message, "OK");
                }
            })
            { text = text };

            button.AddToClassList("cdn-btn");
            button.tooltip = tooltip;
            button.style.marginRight = 6;
            return button;
        }

        // ------------------------------------------------------------------ faults

        private VisualElement BuildFaultBlock()
        {
            var box = Section("Make it fail",
                "One fault at a time. The injectors share a single path filter and request budget, " +
                "and clearing removes all of them — so this offers one, rather than describing an " +
                "engine that does not exist.");

            _kindField = new EnumField("Fault", FaultKind.HttpStatus);
            _kindField.RegisterValueChangedCallback(_ => RefreshFieldVisibility());
            box.Add(_kindField);

            _statusField = new IntegerField("Status") { value = 503 };
            _statusField.tooltip = "503 for a flaky origin, 404 for a missing bundle, 401 for auth.";
            box.Add(_statusField);

            _pathField = new TextField("Only paths containing") { value = "/catalog/" };
            _pathField.tooltip = "Leave empty to affect every request. '/bundles/' or '/catalog/' " +
                                 "targets one half of the traffic.";
            box.Add(_pathField);

            _countField = new IntegerField("For this many requests") { value = 1 };
            _countField.tooltip = "The retry policy is the thing most worth testing: fail once and a " +
                                  "correct client still succeeds. 0 or less means every request.";
            box.Add(_countField);

            _throttleField = new IntegerField("KB per second") { value = 64 };
            box.Add(_throttleField);

            _latencyField = new IntegerField("Milliseconds") { value = 400 };
            box.Add(_latencyField);

            var apply = new Button(ApplyFault) { text = "Inject" };
            apply.AddToClassList("cdn-btn");
            apply.style.marginTop = 8;
            apply.style.alignSelf = Align.FlexStart;
            box.Add(apply);

            RefreshFieldVisibility();
            return box;
        }

        private void RefreshFieldVisibility()
        {
            var kind = (FaultKind)_kindField.value;

            Show(_statusField, kind == FaultKind.HttpStatus);
            Show(_pathField, kind == FaultKind.HttpStatus || kind == FaultKind.ConnectionDrop);
            Show(_countField, kind == FaultKind.HttpStatus || kind == FaultKind.ConnectionDrop);
            Show(_throttleField, kind == FaultKind.Throttle);
            Show(_latencyField, kind == FaultKind.Latency);
        }

        private static void Show(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        private void ApplyFault()
        {
            var kind = (FaultKind)_kindField.value;
            string path = string.IsNullOrWhiteSpace(_pathField.value) ? null : _pathField.value.Trim();
            int count = _countField.value <= 0 ? int.MaxValue : _countField.value;

            // The IDisposable each injector returns is deliberately discarded. Disposing any one of
            // them calls Clear(), which removes every fault - so holding them would offer a per-fault
            // teardown the engine cannot honour. "Clear all faults" is the only honest removal.
            switch (kind)
            {
                case FaultKind.HttpStatus:
                    if (_statusField.value < 100 || _statusField.value > 599)
                    {
                        EditorUtility.DisplayDialog(
                            "Not an HTTP status",
                            $"{_statusField.value} is not a status code. Use 4xx or 5xx.",
                            "OK");
                        return;
                    }

                    ServerFaults.InjectStatus(_statusField.value, path, count);
                    break;

                case FaultKind.ConnectionDrop:
                    ServerFaults.InjectConnectionDrop(path, count);
                    break;

                case FaultKind.Throttle:
                    if (_throttleField.value <= 0)
                    {
                        EditorUtility.DisplayDialog(
                            "Throttle must be positive",
                            "A rate of zero would stop the server rather than slow it.",
                            "OK");
                        return;
                    }

                    ServerFaults.Throttle(_throttleField.value * 1024L);
                    break;

                case FaultKind.Latency:
                    ServerFaults.InjectLatency(Math.Max(0, _latencyField.value));
                    break;
            }

            RefreshBanner();
        }

        private void ClearFaults()
        {
            ServerFaults.Clear();
            Debug.Log("[LocalServer] Injected faults cleared.");
            RefreshBanner();
        }

        // ------------------------------------------------------------------ chrome

        private static VisualElement Section(string title, string blurb)
        {
            var box = new VisualElement();
            box.style.marginBottom = 10;
            box.style.paddingLeft = 10;
            box.style.paddingRight = 10;
            box.style.paddingTop = 8;
            box.style.paddingBottom = 10;
            box.style.borderTopWidth = 1;
            box.style.borderBottomWidth = 1;
            box.style.borderLeftWidth = 1;
            box.style.borderRightWidth = 1;
            box.style.borderTopLeftRadius = 3;
            box.style.borderTopRightRadius = 3;
            box.style.borderBottomLeftRadius = 3;
            box.style.borderBottomRightRadius = 3;

            var heading = new Label(title);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            box.Add(heading);

            var note = new Label(blurb);
            note.style.fontSize = 10;
            note.style.opacity = 0.7f;
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 2;
            box.Add(note);

            return box;
        }
    }
}
