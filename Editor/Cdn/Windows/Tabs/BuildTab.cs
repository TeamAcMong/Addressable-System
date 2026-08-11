using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Design doc §5.9 "Build" tab — task 1.10, and the surface for task 1.11's patch diff.
    /// Lets QA and producers run a content build, and see what it will cost players, without a
    /// programmer or a terminal.
    /// </summary>
    /// <remarks>
    /// Every button here goes through <see cref="CdnBuildPipeline"/>, the same entry point
    /// CdnBuildCLI uses in batchmode. The gate order — compile, state file present, version match,
    /// content-update restrictions, then build, manifest, verify — is written down once, there. A
    /// build started from this tab and one started from CI therefore cannot disagree about what is
    /// allowed, which matters because the gates exist to prevent a patch that looks fine and is not.
    ///
    /// THE LOG IS NOT LIVE
    /// AddressableAssetSettings.BuildPlayerContent is synchronous and blocks the Editor's main
    /// thread, so no UI can repaint while it runs. Lines are collected through the pipeline's sink
    /// and rendered when it returns. Calling it a live log would be a lie; the header says "Build
    /// log" and the tab shows a "running..." line before it blocks.
    ///
    /// THE PLATFORM IS SHOWN, NOT PICKED
    /// The design doc asks for a platform picker. Switching the active build target reimports the
    /// whole project and can take many minutes, which is not a thing to trigger from a dropdown
    /// someone is browsing. The active target is displayed and the button opens Build Settings,
    /// where the switch is expected and warned about.
    /// </remarks>
    public sealed class BuildTab : ICdnManagerTab
    {
        public string TabName => "Build";

        private static readonly string[] ProfileNames =
        {
            CdnProfileManager.ProfileNames.Local,
            CdnProfileManager.ProfileNames.Dev,
            CdnProfileManager.ProfileNames.Staging,
            CdnProfileManager.ProfileNames.Prod
        };

        private Label _platformLabel;
        private DropdownField _profileDropdown;
        private VisualElement _stateContainer;
        private HelpBox _summaryBox;
        private VisualElement _patchContainer;
        private VisualElement _logContainer;
        private ScrollView _logScroll;
        private Button _buildFullButton;
        private Button _buildUpdateButton;
        private Button _verifyButton;
        private Button _refreshButton;
        private Button _switchPlatformButton;

        private readonly List<(string message, BuildLogLevel level)> _pendingLog =
            new List<(string, BuildLogLevel)>();

        public VisualElement CreateView()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/BuildTab.uxml");

            if (visualTree == null)
            {
                Debug.LogError("[BuildTab] Failed to load BuildTab.uxml. Creating fallback UI.");
                return CreateFallbackUI();
            }

            var root = visualTree.CloneTree();

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/BuildTab.uss");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            _platformLabel = root.Q<Label>("build-platform-label");
            _profileDropdown = root.Q<DropdownField>("build-profile-dropdown");
            _stateContainer = root.Q<VisualElement>("build-state");
            _summaryBox = root.Q<HelpBox>("build-summary");
            _patchContainer = root.Q<VisualElement>("build-patch");
            _logContainer = root.Q<VisualElement>("build-log");
            _logScroll = root.Q<ScrollView>("build-log-scroll");
            _buildFullButton = root.Q<Button>("build-full-btn");
            _buildUpdateButton = root.Q<Button>("build-update-btn");
            _verifyButton = root.Q<Button>("build-verify-btn");
            _refreshButton = root.Q<Button>("build-refresh-btn");
            _switchPlatformButton = root.Q<Button>("build-switch-platform-btn");

            _profileDropdown.choices = ProfileNames.ToList();
            _profileDropdown.value = ResolveActiveProfileName();

            _buildFullButton.clicked += () => RunBuild(isUpdate: false);
            _buildUpdateButton.clicked += () => RunBuild(isUpdate: true);
            _verifyButton.clicked += RunVerify;
            _refreshButton.clicked += Refresh;
            _switchPlatformButton.clicked += () =>
                EditorApplication.ExecuteMenuItem("File/Build Settings...");

            return root;
        }

        public void OnShown() => Refresh();

        /// <summary>Re-reads build state from disk. Nothing is cached between calls.</summary>
        private void Refresh()
        {
            _stateContainer.Clear();
            _patchContainer.Clear();

            _platformLabel.text = $"Platform: {EditorUserBuildSettings.activeBuildTarget}";

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                SetSummary(HelpBoxMessageType.Error, "No AddressableAssetSettings in this project. Run CdnSetupCLI first.");
                SetBuildButtonsEnabled(false, false);
                return;
            }

            AddStateRow("App version", PlayerSettings.bundleVersion, false);

            // ---- content state ----
            bool canUpdate = false;
            var resolved = ContentStateManager.ResolvePath();
            if (resolved.IsFailure)
            {
                AddStateRow("Content state", resolved.ErrorMessage, true);
            }
            else if (!File.Exists(resolved.Value))
            {
                AddStateRow("Content state", "missing - no baseline to patch against", true);
            }
            else
            {
                var info = new FileInfo(resolved.Value);
                AddStateRow("Content state", resolved.Value, false);
                AddStateRow("  built", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), false);

                var validation = ContentStateManager.Validate(resolved.Value);
                if (validation.IsFailure)
                {
                    AddStateRow("  version", $"does not match {PlayerSettings.bundleVersion}", true);
                }
                else
                {
                    AddStateRow("  version", $"matches {PlayerSettings.bundleVersion}", false);
                    canUpdate = true;
                }
            }

            // ---- last build output ----
            string manifestPath = Path.Combine(BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName);
            var manifest = ContentDiff.Load(manifestPath);
            if (manifest.IsFailure)
            {
                AddStateRow("Last build", "none in this workspace", false);
                RenderPatch(null);
                SetSummary(HelpBoxMessageType.Info,
                    "No build output here yet. Build Full produces a new baseline; Build Update needs one already " +
                    "shipped to players.");
            }
            else
            {
                var m = manifest.Value;
                AddStateRow("Last build", $"{m.buildType}, {m.buildDate}, {m.bundles?.Count ?? 0} bundle(s)", false);
                AddStateRow("  git", string.IsNullOrEmpty(m.gitSha) ? "(unknown)" : m.gitSha, false);
                RenderPatch(m.patch);

                if (canUpdate)
                {
                    SetSummary(HelpBoxMessageType.Info,
                        "Ready. Build Update patches the shipped version; Build Full replaces the baseline and can " +
                        "only be published alongside a new player build.");
                }
                else
                {
                    SetSummary(HelpBoxMessageType.Warning,
                        "Update builds are unavailable: there is no usable content state for this app version. " +
                        "Restore the archived one, or run a full build if this version has not shipped.");
                }
            }

            SetBuildButtonsEnabled(true, canUpdate);
        }

        private void RenderPatch(PatchInfo patch)
        {
            if (patch == null || !patch.available)
            {
                var none = new Label("No previous build to compare against, so there is no patch size yet.");
                none.AddToClassList("cdn-patch-detail");
                _patchContainer.Add(none);
                return;
            }

            var headline = new Label(
                $"A player on the previous build downloads {FormatBytes(patch.patchSizeBytes)} " +
                $"({patch.patchSizeBytes:N0} B)");
            headline.AddToClassList("cdn-patch-headline");
            _patchContainer.Add(headline);

            var detail = new Label(
                $"{patch.newBundleCount} new, {patch.changedBundleCount} changed, {patch.removedBundleCount} removed, " +
                $"{patch.unchangedBundleCount} unchanged{(patch.catalogChanged ? ", catalog changed" : "")}   " +
                $"vs {patch.comparedToGitSha} ({patch.comparedToBuildDate})");
            detail.AddToClassList("cdn-patch-detail");
            _patchContainer.Add(detail);

            // Full sizes of changed and new bundles, not size deltas: bundles are fetched whole, so
            // a rebuild with identical size but different content still costs its full size.
            var note = new Label(
                "Counts what actually crosses the wire: full size of new and changed bundles, plus the catalog " +
                "pair when it changed. Removed bundles cost nothing.");
            note.AddToClassList("cdn-patch-detail");
            _patchContainer.Add(note);
        }

        private void RunBuild(bool isUpdate)
        {
            string profile = _profileDropdown.value;

            if (!EditorUtility.DisplayDialog(
                    isUpdate ? "Build content update" : "Build full content",
                    isUpdate
                        ? $"Build a delta update against the current content state, using profile \"{profile}\"?\n\n" +
                          "The Editor will be unresponsive until it finishes."
                        : $"Build a full content set using profile \"{profile}\"?\n\n" +
                          "This produces a NEW baseline. Publishing it without a matching player build strands " +
                          "everyone already on the old one.\n\nThe Editor will be unresponsive until it finishes.",
                    isUpdate ? "Build update" : "Build full",
                    "Cancel"))
            {
                return;
            }

            _pendingLog.Clear();
            ClearLog();
            AppendLogLine(isUpdate ? "Running content update..." : "Running full content build...", BuildLogLevel.Info);

            // The build blocks the main thread, so nothing painted here appears until it returns.
            // Lines are buffered and flushed afterwards rather than pretending to stream.
            CdnEditorResult<BuildOutcome> outcome;
            try
            {
                outcome = isUpdate
                    ? CdnBuildPipeline.BuildUpdate(profile, null, Collect)
                    : CdnBuildPipeline.BuildFull(profile, Collect);
            }
            catch (Exception ex)
            {
                FlushLog();
                AppendLogLine($"Unhandled exception: {ex.Message}", BuildLogLevel.Error);
                SetSummary(HelpBoxMessageType.Error, $"Build threw {ex.GetType().Name}: {ex.Message}");
                Debug.LogException(ex);
                return;
            }

            FlushLog();

            if (outcome.IsFailure)
            {
                AppendLogLine(outcome.ErrorMessage, BuildLogLevel.Error);
                SetSummary(HelpBoxMessageType.Error, outcome.ErrorMessage);
                // Refresh rebuilds the state panel only; the log container is left alone so the
                // reason the build failed stays on screen.
                Refresh();
                return;
            }

            AppendLogLine("Build complete and verified.", BuildLogLevel.Info);
            Refresh();
        }

        private void RunVerify()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            ClearLog();
            _pendingLog.Clear();

            string bundleDir = CdnBuildPipeline.ResolveBundleDir(settings);
            string catalogDir = CdnBuildPipeline.ResolveCatalogDir(settings);
            var verification = CatalogVerifier.Verify(bundleDir, catalogDir);

            foreach (string warning in verification.Warnings)
                AppendLogLine(warning, BuildLogLevel.Warning);

            foreach (string problem in verification.Problems)
                AppendLogLine(problem, BuildLogLevel.Error);

            if (verification.Passed)
            {
                AppendLogLine($"{verification.BundlesChecked} bundle(s) verified against the manifest.", BuildLogLevel.Info);
                SetSummary(HelpBoxMessageType.Info, "Output is consistent with its manifest and safe to publish.");
            }
            else
            {
                SetSummary(HelpBoxMessageType.Error,
                    $"{verification.Problems.Count} problem(s) found. Do not upload this output.");
            }
        }

        // ========== log plumbing ==========

        private void Collect(string message, BuildLogLevel level) => _pendingLog.Add((message, level));

        private void FlushLog()
        {
            foreach (var (message, level) in _pendingLog)
                AppendLogLine(message, level);
        }

        private void ClearLog() => _logContainer.Clear();

        private void AppendLogLine(string message, BuildLogLevel level)
        {
            var line = new Label(message);
            line.AddToClassList("cdn-log-line");
            if (level == BuildLogLevel.Warning) line.AddToClassList("cdn-log-line-warning");
            if (level == BuildLogLevel.Error) line.AddToClassList("cdn-log-line-error");
            _logContainer.Add(line);

            if (_logScroll != null)
                _logScroll.scrollOffset = new Vector2(0, float.MaxValue);
        }

        // ========== small helpers ==========

        private string ResolveActiveProfileName()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return ProfileNames[0];

            string name = settings.profileSettings.GetProfileName(settings.activeProfileId);
            return ProfileNames.Contains(name) ? name : ProfileNames[0];
        }

        private void SetBuildButtonsEnabled(bool full, bool update)
        {
            _buildFullButton.SetEnabled(full);
            _buildUpdateButton.SetEnabled(update);
            _verifyButton.SetEnabled(full);
        }

        private void SetSummary(HelpBoxMessageType type, string message)
        {
            _summaryBox.messageType = type;
            _summaryBox.text = message;
        }

        private void AddStateRow(string key, string value, bool isBad)
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-state-row");

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("cdn-state-key");
            row.Add(keyLabel);

            var valueLabel = new Label(value);
            valueLabel.AddToClassList("cdn-state-value");
            if (isBad) valueLabel.AddToClassList("cdn-state-value-bad");
            valueLabel.tooltip = value;
            row.Add(valueLabel);

            _stateContainer.Add(row);
        }

        private static string FormatBytes(long bytes)
        {
            const long kb = 1024;
            const long mb = kb * 1024;
            const long gb = mb * 1024;

            if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
            if (bytes >= mb) return $"{bytes / (double)mb:F2} MB";
            if (bytes >= kb) return $"{bytes / (double)kb:F2} KB";
            return $"{bytes} B";
        }

        private VisualElement CreateFallbackUI()
        {
            var fallback = new VisualElement();
            fallback.style.flexGrow = 1;
            fallback.style.alignItems = Align.Center;
            fallback.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check that Editor/Cdn/UI/BuildTab.uxml exists.");
            label.style.color = Color.red;
            fallback.Add(label);

            return fallback;
        }
    }
}
