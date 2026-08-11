using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Design doc §5.9 "Update Preview" tab — task 1.9. Answers, before a build cycle is spent,
    /// whether a content update can be built and what would block it.
    /// </summary>
    /// <remarks>
    /// This is the user interface for task 1.4, and the place the "Prepare for Content Update" step
    /// lives (see CDN_HANDOFF.html §10 for why it lives here and not in CI).
    ///
    /// WHAT IT IS FOR
    /// CdnBuildCLI.BuildContentUpdate refuses to build when an asset in a StaticContent group has
    /// changed. That refusal is correct — Addressables itself only logs a warning and then reverts
    /// the entry to its previous bundle (RevertUnchangedAssetsToPreviousAssetState.cs:183-199), so
    /// the patch would ship without the change in it. But a CI failure is a poor place to learn
    /// this. This tab gives the same answer without a build, names every offending asset, and
    /// offers the actual remedy.
    ///
    /// THE REMEDY IS NOT A PLAYER REBUILD
    /// Moving the changed entries into a fresh non-static group makes the update rebuild them into
    /// a new bundle that players will download. That is what
    /// ContentUpdateScript.CreateContentUpdateGroup does, and what Unity's own
    /// ContentUpdatePreviewWindow does with its Apply button.
    ///
    /// WHY THE BUTTON IS HERE RATHER THAN IN THE CLI
    /// It edits AddressableAssetSettings and creates a group. That belongs in a diff someone
    /// reviews, not in a CI job that restructures the project on its own.
    ///
    /// TWO SOURCES, ON PURPOSE
    /// The verdict and the violation list come from ContentUpdateRestrictions.Check, so this tab and
    /// the CLI can never disagree about whether a build is allowed. The entry objects the prepare
    /// action needs come from ContentUpdateScript.GatherModifiedEntriesWithDependencies, because
    /// Check reports paths and names rather than AddressableAssetEntry instances. The two are
    /// cross-checked and a mismatch is surfaced rather than silently reconciled.
    /// </remarks>
    public sealed class UpdatePreviewTab : ICdnManagerTab
    {
        public string TabName => "Update Preview";

        private HelpBox _summaryBox;
        private VisualElement _stateContainer;
        private VisualElement _rowsContainer;
        private Button _prepareButton;
        private Button _recheckButton;
        private Button _copyButton;

        private string _contentStatePath;
        private ContentUpdateCheckResult _lastCheck;
        private readonly List<EntryRow> _rows = new List<EntryRow>();

        private sealed class EntryRow
        {
            public string AssetPath;
            public string GroupName;
            public bool IsExplicit;
            public Toggle Toggle;
        }

        public VisualElement CreateView()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/UpdatePreviewTab.uxml");

            if (visualTree == null)
            {
                Debug.LogError("[UpdatePreviewTab] Failed to load UpdatePreviewTab.uxml. Creating fallback UI.");
                return CreateFallbackUI();
            }

            var root = visualTree.CloneTree();

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/UpdatePreviewTab.uss");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            _summaryBox = root.Q<HelpBox>("update-preview-summary");
            _stateContainer = root.Q<VisualElement>("update-preview-state");
            _rowsContainer = root.Q<VisualElement>("update-preview-rows");
            _prepareButton = root.Q<Button>("update-preview-prepare-btn");
            _recheckButton = root.Q<Button>("update-preview-check-btn");
            _copyButton = root.Q<Button>("update-preview-copy-btn");

            _prepareButton.clicked += PrepareContentUpdate;
            _recheckButton.clicked += Refresh;
            _copyButton.clicked += CopyReport;

            return root;
        }

        public void OnShown() => Refresh();

        /// <summary>
        /// Re-reads everything from live project state. Nothing is cached across calls, so switching
        /// away and back, or reverting an asset, is reflected immediately.
        /// </summary>
        private void Refresh()
        {
            _rowsContainer.Clear();
            _stateContainer.Clear();
            _rows.Clear();
            _lastCheck = null;
            _prepareButton.SetEnabled(false);

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                SetSummary(HelpBoxMessageType.Error, "No AddressableAssetSettings in this project. Run CdnSetupCLI first.");
                return;
            }

            // --- content state -------------------------------------------------------------------
            var resolved = ContentStateManager.ResolvePath();
            if (resolved.IsFailure)
            {
                SetSummary(HelpBoxMessageType.Error, $"Cannot resolve the content state path: {resolved.ErrorMessage}");
                return;
            }

            _contentStatePath = resolved.Value;
            AddStateRow("Content state", _contentStatePath, isBad: false);

            if (!System.IO.File.Exists(_contentStatePath))
            {
                AddStateRow("Status", "missing", isBad: true);
                SetSummary(HelpBoxMessageType.Error,
                    "No content state file, so no update can be built against this version. It is produced by the " +
                    "full build that shipped to players; restore it from the CI archive for this app version.");
                RenderEmptyNote("Nothing to preview without a baseline.");
                return;
            }

            // The version gate, shown here rather than discovered at build time. A mismatch means the
            // catalog would be written under the live version's folder while carrying the state
            // file's version in its name, which players never request.
            var validation = ContentStateManager.Validate(_contentStatePath);
            if (validation.IsFailure)
            {
                AddStateRow("Player version", PlayerSettings.bundleVersion, isBad: true);
                AddStateRow("Status", "version mismatch", isBad: true);
                SetSummary(HelpBoxMessageType.Error, validation.ErrorMessage);
                RenderEmptyNote("Resolve the version mismatch before previewing changes.");
                return;
            }

            AddStateRow("Player version", PlayerSettings.bundleVersion, isBad: false);
            AddStateRow("Status", "matches the content state", isBad: false);

            // --- restriction check ---------------------------------------------------------------
            _lastCheck = ContentUpdateRestrictions.Check(settings, _contentStatePath);

            if (!_lastCheck.CanEvaluate)
            {
                SetSummary(HelpBoxMessageType.Error,
                    $"The restriction check could not run, so nothing here proves the update is safe: {_lastCheck.Message}");
                RenderEmptyNote("Check could not be evaluated.");
                return;
            }

            if (_lastCheck.Passed)
            {
                SetSummary(HelpBoxMessageType.Info,
                    "No static-content violations. A content update can be built from this state.");
                RenderEmptyNote("No changed assets in StaticContent groups.");
                return;
            }

            var violations = _lastCheck.Violations ?? new List<ContentUpdateViolation>();
            int explicitCount = violations.Count(v => v.IsExplicitModification);

            SetSummary(HelpBoxMessageType.Error,
                $"{violations.Count} entr{(violations.Count == 1 ? "y" : "ies")} in StaticContent groups changed " +
                $"({explicitCount} modified directly, {violations.Count - explicitCount} pulled in as dependencies). " +
                "A build would succeed and quietly omit these changes: Addressables reverts them to their previous " +
                "bundles and only logs a warning. Move them to a new group, or revert them.");

            RenderViolations(violations);
            _prepareButton.SetEnabled(true);
        }

        private void RenderViolations(List<ContentUpdateViolation> violations)
        {
            var byGroup = violations
                .GroupBy(v => v.GroupName ?? "(no group)")
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var group in byGroup)
            {
                var header = new Label($"{group.Key} - {group.Count()} entr{(group.Count() == 1 ? "y" : "ies")}");
                header.AddToClassList("cdn-section-header");
                _rowsContainer.Add(header);

                foreach (var violation in group.OrderBy(v => v.AssetPath, StringComparer.Ordinal))
                    _rowsContainer.Add(BuildEntryRow(violation));
            }
        }

        private VisualElement BuildEntryRow(ContentUpdateViolation violation)
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-entry-row");

            var toggle = new Toggle { value = true };
            toggle.AddToClassList("cdn-entry-toggle");
            toggle.tooltip = "Include this entry when preparing the content update";
            row.Add(toggle);

            // Glyph first, tint second: colour alone would not survive a high-contrast theme or a
            // colourblind reader.
            var icon = new Image
            {
                image = EditorGUIUtility.IconContent(violation.IsExplicitModification ? "console.erroricon" : "console.warnicon").image
            };
            icon.AddToClassList("cdn-entry-icon");
            row.Add(icon);

            var path = new Label(violation.AssetPath);
            path.AddToClassList("cdn-entry-path");
            path.AddToClassList("cdn-entry-path-blocking");
            path.tooltip = violation.AssetPath;
            row.Add(path);

            var kind = new Label(violation.IsExplicitModification ? "modified" : "dependency");
            kind.AddToClassList("cdn-entry-kind");
            kind.tooltip = violation.IsExplicitModification
                ? "This asset was changed directly"
                : "This asset did not change, but something it depends on did, so its bundle is affected too";
            row.Add(kind);

            var groupLabel = new Label(violation.GroupName);
            groupLabel.AddToClassList("cdn-entry-group");
            row.Add(groupLabel);

            _rows.Add(new EntryRow
            {
                AssetPath = violation.AssetPath,
                GroupName = violation.GroupName,
                IsExplicit = violation.IsExplicitModification,
                Toggle = toggle
            });

            return row;
        }

        /// <summary>
        /// Move the selected entries into a fresh non-static group so the next update actually
        /// rebuilds them — the "Prepare for Content Update" step.
        /// </summary>
        private void PrepareContentUpdate()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || string.IsNullOrEmpty(_contentStatePath))
            {
                return;
            }

            var selectedPaths = new HashSet<string>(
                _rows.Where(r => r.Toggle.value).Select(r => r.AssetPath),
                StringComparer.Ordinal);

            if (selectedPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Prepare content update", "No entries are selected.", "OK");
                return;
            }

            // Entry objects, which the violation records do not carry. Sourced from Addressables so
            // the set moved is exactly the set Addressables considers modified.
            Dictionary<AddressableAssetEntry, List<AddressableAssetEntry>> modified;
            try
            {
                modified = ContentUpdateScript.GatherModifiedEntriesWithDependencies(settings, _contentStatePath);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Prepare content update",
                    $"Could not gather modified entries:\n\n{ex.Message}", "OK");
                return;
            }

            var allEntries = new HashSet<AddressableAssetEntry>();
            foreach (var pair in modified)
            {
                allEntries.Add(pair.Key);
                foreach (var dependency in pair.Value)
                    allEntries.Add(dependency);
            }

            // Cross-check the two sources before mutating anything. If they disagree, the safe move
            // is to say so rather than move a set the user was never shown.
            if (allEntries.Count != _rows.Count)
            {
                Debug.LogWarning(
                    $"[UpdatePreviewTab] ContentUpdateRestrictions reported {_rows.Count} entr(ies) but " +
                    $"GatherModifiedEntriesWithDependencies returned {allEntries.Count}. Re-checking before you " +
                    "prepare is advised; the list above may be stale.");
            }

            var toMove = allEntries
                .Where(e => e != null && selectedPaths.Contains(e.AssetPath))
                .ToList();

            if (toMove.Count == 0)
            {
                EditorUtility.DisplayDialog("Prepare content update",
                    "None of the selected entries could be resolved. Re-check and try again.", "OK");
                return;
            }

            string groupName = $"Content Update {PlayerSettings.bundleVersion}";
            bool confirmed = EditorUtility.DisplayDialog(
                "Prepare content update",
                $"Move {toMove.Count} entr{(toMove.Count == 1 ? "y" : "ies")} into a new group \"{groupName}\"?\n\n" +
                "The new group is remote and non-static, so the next content update rebuilds these assets into a " +
                "new bundle that players will download.\n\n" +
                "This edits AddressableAssetSettings and creates a group asset. Commit the change - do not leave it " +
                "only on the build machine.",
                "Move entries",
                "Cancel");

            if (!confirmed)
            {
                return;
            }

            // Clear the restriction highlight before moving, as Unity's own ContentUpdatePreviewWindow
            // does (ContentUpdatePreviewWindow.cs:311-319) — but on the ENTRIES, not the groups.
            // AddressableAssetGroup.FlaggedDuringContentUpdateRestriction is { get; internal set; }
            // (AddressableAssetGroup.cs:56), so Unity's own window can assign it and code outside the
            // package cannot; doing it their way is a compile error here. The entry-level flag is a
            // plain public field (AddressableAssetEntry.cs:59), and the group flag is derived from it:
            // RefreshEntriesCache recomputes the group from its entries (AddressableAssetGroup.cs:58-69),
            // and GatherExplicitModifiedEntries resets it at the start of every gather
            // (ContentUpdateScript.cs:759). So clearing the entries is both sufficient and the only
            // option available.
            foreach (var entry in toMove)
            {
                entry.FlaggedDuringContentUpdateRestriction = false;
            }

            ContentUpdateScript.CreateContentUpdateGroup(settings, toMove, groupName);
            AssetDatabase.SaveAssets();

            Debug.Log($"[UpdatePreviewTab] Moved {toMove.Count} entr(ies) into \"{groupName}\". " +
                      "Commit the modified AddressableAssetSettings and the new group asset.");

            Refresh();
        }

        private void CopyReport()
        {
            var report = new StringBuilder();
            report.AppendLine("CDN content update preview");
            report.AppendLine($"  player version : {PlayerSettings.bundleVersion}");
            report.AppendLine($"  content state  : {_contentStatePath ?? "(unresolved)"}");

            if (_lastCheck == null)
            {
                report.AppendLine("  result         : not evaluated");
            }
            else if (!_lastCheck.CanEvaluate)
            {
                report.AppendLine($"  result         : could not evaluate - {_lastCheck.Message}");
            }
            else if (_lastCheck.Passed)
            {
                report.AppendLine("  result         : no violations; an update can be built");
            }
            else
            {
                report.AppendLine($"  result         : {_rows.Count} blocking entr(ies)");
                report.AppendLine();
                foreach (var row in _rows)
                    report.AppendLine($"    [{(row.IsExplicit ? "modified  " : "dependency")}] {row.AssetPath}  ({row.GroupName})");
            }

            EditorGUIUtility.systemCopyBuffer = report.ToString();
            Debug.Log("[UpdatePreviewTab] Report copied to the clipboard.");
        }

        // ========== small helpers ==========

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
            valueLabel.AddToClassList(isBad ? "cdn-state-value-bad" : "cdn-state-value-good");
            valueLabel.tooltip = value;
            row.Add(valueLabel);

            _stateContainer.Add(row);
        }

        private void RenderEmptyNote(string message)
        {
            var note = new Label(message);
            note.AddToClassList("cdn-empty-note");
            _rowsContainer.Add(note);
        }

        private VisualElement CreateFallbackUI()
        {
            var fallback = new VisualElement();
            fallback.style.flexGrow = 1;
            fallback.style.alignItems = Align.Center;
            fallback.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check that Editor/Cdn/UI/UpdatePreviewTab.uxml exists.");
            label.style.color = Color.red;
            fallback.Add(label);

            return fallback;
        }
    }
}
