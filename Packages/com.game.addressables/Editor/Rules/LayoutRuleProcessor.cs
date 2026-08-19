using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Processes layout rules and applies them to addressable assets
    /// </summary>
    public class LayoutRuleProcessor
    {
        /// <summary>
        /// Result of rule processing
        /// </summary>
        public class ProcessResult
        {
            public int TotalAssetsProcessed;
            public int AddressesApplied;
            public int LabelsApplied;
            public int VersionsApplied;
            public List<string> Warnings = new List<string>();
            public List<string> Errors = new List<string>();
            public bool Success => Errors.Count == 0;
        }

        private readonly LayoutRuleData _ruleData;
        private readonly AddressableAssetSettings _settings;
        private bool _verboseLogging;

        /// <summary>
        /// True when this run changed at least one entry label, including a removal.
        /// </summary>
        /// <remarks>
        /// The save block keys off the applied-counters, but a run can legitimately dirty group assets
        /// without moving any of them: stripping a stale "version:" label increments nothing. Saving on
        /// the counters alone would leave those removals sitting dirty-but-unsaved until something else
        /// happened to trigger a save.
        /// </remarks>
        private bool _labelsTouched;

        /// <summary>
        /// address -> the asset paths a run assigned it to, used to catch duplicates before they ship.
        /// </summary>
        /// <remarks>
        /// Two entries sharing one address makes one of the assets permanently unreachable: Addressables
        /// resolves a single-asset load to one location and the other is simply never returned. Nothing
        /// else on the write path catches it - AddressableAssetEntry.SetAddress accepts any duplicate
        /// (it only rejects '[' and ']'), RuleValidator checks duplicate rule NAMES rather than
        /// generated addresses, and RuleConflictDetector is only ever reached from read-only surfaces
        /// (the CLI's detect command, the inspector button, the Layout Viewer). So the apply path had no
        /// duplicate check at all and reported full success either way.
        /// </remarks>
        private readonly Dictionary<string, string> _addressOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        public LayoutRuleProcessor(LayoutRuleData ruleData)
        {
            _ruleData = ruleData ?? throw new ArgumentNullException(nameof(ruleData));
            _settings = AddressableAssetSettingsDefaultObject.Settings;

            if (_settings == null)
            {
                throw new InvalidOperationException("AddressableAssetSettings not found. Please initialize Addressables first.");
            }

            _verboseLogging = ruleData.VerboseLogging;
        }

        /// <summary>
        /// Apply all rules to all assets in the project
        /// </summary>
        public ProcessResult ApplyRules(Action<float, string> progressCallback = null)
        {
            var result = new ProcessResult();
            _labelsTouched = false;
            _addressOwners.Clear();

            try
            {
                // Validate rules first
                var (isValid, validationErrors) = _ruleData.Validate();
                if (!isValid)
                {
                    result.Errors.AddRange(validationErrors);
                    return result;
                }

                Log("Starting rule processing...");

                // Setup all filters and providers
                SetupRules();

                // Get all assets in the project
                progressCallback?.Invoke(0.1f, "Finding assets...");
                var allAssetPaths = GetAllAssetPaths();
                Log($"Found {allAssetPaths.Count} total assets to process");

                // Process address rules
                progressCallback?.Invoke(0.2f, "Processing address rules...");
                ProcessAddressRules(allAssetPaths, result, progressCallback);

                // Process label rules
                progressCallback?.Invoke(0.6f, "Processing label rules...");
                ProcessLabelRules(allAssetPaths, result, progressCallback);

                // Process version rules (placeholder for now - full implementation in Phase 4)
                progressCallback?.Invoke(0.9f, "Processing version rules...");
                ProcessVersionRules(allAssetPaths, result, progressCallback);

                // Save changes - only if something was actually applied. A rule set that
                // validates but matches nothing is the everyday case (not an error), and
                // dirtying + saving AddressableAssetSettings.asset on every no-op run - which,
                // with AutoApplyOnImport on, means every asset import in the project - produces
                // spurious diffs/merge conflicts in a shared project for no actual change
                // (HANDOFF_TO_SESSION_B.md §4.5 "Also").
                progressCallback?.Invoke(0.95f, "Saving changes...");
                if (result.AddressesApplied > 0 || result.LabelsApplied > 0 || result.VersionsApplied > 0 || _labelsTouched)
                {
                    FlushLabelEvent();
                    EditorUtility.SetDirty(_settings);
                    AssetDatabase.SaveAssets();
                }

                progressCallback?.Invoke(1.0f, "Complete!");
                Log($"Rule processing complete. Processed {result.TotalAssetsProcessed} assets.");
                Log($"Applied: {result.AddressesApplied} addresses, {result.LabelsApplied} labels, {result.VersionsApplied} versions");

                if (result.Warnings.Count > 0)
                {
                    Log($"Warnings: {result.Warnings.Count}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception during rule processing: {ex.Message}");
                Debug.LogException(ex);
            }

            return result;
        }

        /// <summary>
        /// Apply rules to specific asset paths
        /// </summary>
        public ProcessResult ApplyRulesToAssets(List<string> assetPaths, Action<float, string> progressCallback = null)
        {
            var result = new ProcessResult();
            _labelsTouched = false;
            _addressOwners.Clear();

            try
            {
                // Validate rules
                var (isValid, validationErrors) = _ruleData.Validate();
                if (!isValid)
                {
                    result.Errors.AddRange(validationErrors);
                    return result;
                }

                SetupRules();

                // Process rules
                ProcessAddressRules(assetPaths, result, progressCallback);
                ProcessLabelRules(assetPaths, result, progressCallback);
                ProcessVersionRules(assetPaths, result, progressCallback);

                // Save - only if something was actually applied (see ApplyRules() above).
                if (result.AddressesApplied > 0 || result.LabelsApplied > 0 || result.VersionsApplied > 0 || _labelsTouched)
                {
                    FlushLabelEvent();
                    EditorUtility.SetDirty(_settings);
                    AssetDatabase.SaveAssets();
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception: {ex.Message}");
                Debug.LogException(ex);
            }

            return result;
        }

        private void SetupRules()
        {
            foreach (var rule in _ruleData.AddressRules)
            {
                rule?.Setup();
            }

            foreach (var rule in _ruleData.LabelRules)
            {
                rule?.Setup();
            }

            foreach (var rule in _ruleData.VersionRules)
            {
                rule?.Setup();
            }
        }

        // Rules are applied by ApplyRules() to every path GetAllAssetPaths() returns, and a rule
        // whose filters are all individually disabled used to become match-all (see
        // AddressRule.IsMatch fix). FindAssets("") with no folder scope searches the ENTIRE
        // project database - Packages/ included - so that combination could rewrite addresses
        // across package boundaries. Bound the scan to "Assets" and exclude
        // Assets/AddressableAssetsData so rule application can never touch Addressables' own
        // settings/group assets or anything outside the project's own asset tree
        // (HANDOFF_TO_SESSION_B.md E-PAIR-1).
        private const string ExcludedAddressableDataPrefix = "Assets/AddressableAssetsData/";

        private List<string> GetAllAssetPaths()
        {
            var paths = new List<string>();

            // Get all asset GUIDs, scoped to the project's own Assets folder only.
            var allGuids = AssetDatabase.FindAssets("", new[] { "Assets" });
            foreach (var guid in allGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
                    continue;

                if (path.StartsWith(ExcludedAddressableDataPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                paths.Add(path);
            }

            return paths;
        }

        private void ProcessAddressRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            if (_ruleData.AddressRules == null || _ruleData.AddressRules.Count == 0)
            {
                Log("No address rules to process");
                return;
            }

            // Sort rules by priority (higher first)
            var sortedRules = _ruleData.AddressRules
                .Where(r => r != null && r.Enabled)
.ToList();

            // Priority is the default ordering, but CompositeLayoutRuleData can ask for its source
            // order to survive (see LayoutRuleData.PreserveRuleOrder). Sorting unconditionally here is
            // what made that setting inert.
            if (!_ruleData.PreserveRuleOrder)
            {
                sortedRules = sortedRules.OrderByDescending(r => r.Priority).ToList();
            }

            Log($"Processing {sortedRules.Count} address rules");

            int processed = 0;
            int total = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                processed++;
                if (processed % 100 == 0)
                {
                    float progress = 0.2f + (0.4f * (processed / (float)total));
                    progressCallback?.Invoke(progress, $"Processing addresses ({processed}/{total})...");
                }

                // Find first matching rule
                AddressRule matchedRule = null;
                foreach (var rule in sortedRules)
                {
                    if (rule.IsMatch(assetPath))
                    {
                        matchedRule = rule;
                        break; // Use first matching rule (highest priority)
                    }
                }

                if (matchedRule != null)
                {
                    ApplyAddressRule(assetPath, matchedRule, result);
                }
            }
        }

        private void ApplyAddressRule(string assetPath, AddressRule rule, ProcessResult result)
        {
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);

                // Skip if already has address and rule says skip existing
                if (rule.SkipExisting && entry != null && !string.IsNullOrEmpty(entry.address))
                {
                    return;
                }

                // Generate address
                string address = rule.GenerateAddress(assetPath);
                if (string.IsNullOrEmpty(address))
                {
                    result.Warnings.Add($"Rule '{rule.RuleName}' generated empty address for: {assetPath}");
                    return;
                }

                // Get or create target group
                var targetGroup = rule.GetOrCreateTargetGroup(_settings);
                if (targetGroup == null)
                {
                    result.Errors.Add($"Failed to get/create target group for rule '{rule.RuleName}'");
                    return;
                }

                // Create or update entry.
                //
                // postEvent:true, not false. AddressableScenesManager subscribes to
                // OnModificationGlobal and exists to enforce Unity's invariant that a scene cannot be
                // both addressable and enabled in EditorBuildSettings - with postEvent:false it never
                // runs, so a scene made addressable by a rule stays in the build list and ships twice.
                // The per-entry Groups-window rebuild that made postEvent:false the right call for
                // labels does not apply at the same volume here: only entries actually created or
                // moved reach this line, not every label on every entry.
                if (entry == null)
                {
                    entry = _settings.CreateOrMoveEntry(guid, targetGroup, false, true);
                }
                else if (entry.parentGroup != targetGroup)
                {
                    // Relocation of an entry this rule did not create. It is legitimate when a rule owns
                    // the layout, and destructive when someone placed the asset by hand - MoveEntry also
                    // resets ReadOnly. Nothing distinguishes the two cases, and the only guard is
                    // SkipExisting, which defaults to false and whose tooltip talks only about addresses.
                    // Until there is an explicit opt-in, the move still happens (changing that silently
                    // would break layouts that rely on it) but it stops being invisible.
                    string previousGroup = entry.parentGroup != null ? entry.parentGroup.Name : "(none)";

                    if (!rule.AllowGroupMove)
                    {
                        result.Warnings.Add(
                            $"Rule '{rule.RuleName}' left '{assetPath}' in group '{previousGroup}' " +
                            $"instead of moving it to '{targetGroup.Name}': the rule has 'Allow Group " +
                            "Move' turned off. The address was still applied.");
                    }
                    else
                    {
                        result.Warnings.Add(
                            $"Rule '{rule.RuleName}' moved '{assetPath}' from group '{previousGroup}' to " +
                            $"'{targetGroup.Name}'. If that group was configured by hand, turn off the " +
                            "rule's 'Allow Group Move' to keep rules from relocating entries they did " +
                            "not create.");

                        _settings.MoveEntry(entry, targetGroup, false, true);
                    }
                }

                if (_addressOwners.TryGetValue(address, out var firstOwner))
                {
                    // Reported, not silently skipped: which of the two assets "should" own the address
                    // is a human call, and writing it anyway at least keeps the run's behaviour
                    // unchanged for anyone already depending on it. What must not happen is the run
                    // finishing green.
                    result.Errors.Add(
                        $"Duplicate address '{address}': generated for both '{firstOwner}' and " +
                        $"'{assetPath}' by rule '{rule.RuleName}'. Two entries sharing one address makes " +
                        "one of the assets unreachable at runtime - narrow the rule's filters or use an " +
                        "address provider that includes more of the path.");
                }
                else
                {
                    _addressOwners[address] = assetPath;
                }

                if (entry != null)
                {
                    entry.SetAddress(address, false);
                    result.AddressesApplied++;
                    result.TotalAssetsProcessed++;
                    LogVerbose($"Applied address '{address}' to {assetPath}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error applying address rule to {assetPath}: {ex.Message}");
            }
        }

        private void ProcessLabelRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            if (_ruleData.LabelRules == null || _ruleData.LabelRules.Count == 0)
            {
                Log("No label rules to process");
                return;
            }

            var sortedRules = _ruleData.LabelRules
                .Where(r => r != null && r.Enabled)
.ToList();

            // Priority is the default ordering, but CompositeLayoutRuleData can ask for its source
            // order to survive (see LayoutRuleData.PreserveRuleOrder). Sorting unconditionally here is
            // what made that setting inert.
            if (!_ruleData.PreserveRuleOrder)
            {
                sortedRules = sortedRules.OrderByDescending(r => r.Priority).ToList();
            }

            Log($"Processing {sortedRules.Count} label rules");

            int processed = 0;
            int total = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                processed++;
                if (processed % 100 == 0)
                {
                    float progress = 0.6f + (0.3f * (processed / (float)total));
                    progressCallback?.Invoke(progress, $"Processing labels ({processed}/{total})...");
                }

                // Collect labels from all matching rules.
                //
                // LabelRule.AppendToExisting is honoured here. It was serialized, exported, imported and
                // drawn in the Layout Rule Editor, but nothing ever read it: labels were only ever
                // added, so turning it off changed nothing at all. "Replace" is a per-asset decision -
                // if ANY matching rule asks to replace, the entry's rule-owned labels are rebuilt from
                // scratch instead of accumulated.
                var labelsToApply = new HashSet<string>(StringComparer.Ordinal);
                bool replaceExisting = false;

                foreach (var rule in sortedRules)
                {
                    if (rule.IsMatch(assetPath))
                    {
                        if (!rule.AppendToExisting) replaceExisting = true;

                        var labels = rule.GenerateLabels(assetPath);
                        if (labels != null)
                        {
                            foreach (var label in labels)
                            {
                                if (!string.IsNullOrEmpty(label))
                                {
                                    labelsToApply.Add(label);
                                }
                            }
                        }
                    }
                }

                if (labelsToApply.Count > 0 || replaceExisting)
                {
                    ApplyLabels(assetPath, labelsToApply.ToList(), result, replaceExisting);
                }
            }
        }

        /// <param name="replaceExisting">
        /// When true, labels this run did not ask for are stripped from the entry first. "version:"
        /// labels are deliberately exempt: they are owned by the version-rule path, and a label rule
        /// clobbering them would silently break version queries for an entry that no version rule even
        /// matched.
        /// </param>
        private void ApplyLabels(string assetPath, List<string> labels, ProcessResult result, bool replaceExisting = false)
        {
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);

                if (entry == null)
                {
                    // Asset not in addressables - skip labels
                    return;
                }

                if (replaceExisting)
                {
                    var wanted = new HashSet<string>(labels, StringComparer.Ordinal);
                    var stale = entry.labels
                        .Where(l => !wanted.Contains(l) && !l.StartsWith("version:", StringComparison.Ordinal))
                        .ToList();

                    foreach (var oldLabel in stale)
                    {
                        if (entry.SetLabel(oldLabel, false, false, false))
                        {
                            _labelsTouched = true;
                            LogVerbose($"Removed label '{oldLabel}' from {assetPath} (rule replaces instead of appends)");
                        }
                    }
                }

                foreach (var label in labels)
                {
                    // entry.SetLabel is the only mutator that dirties the owning GROUP asset, and the
                    // group asset - not AddressableAssetSettings.asset - is where an entry's labels are
                    // serialized (m_SerializedLabels). Writing through entry.labels instead skipped
                    // SetDirty entirely, so the labels never reached disk even when the in-memory write
                    // took; on Addressables 2.9+ the property returns a COPY (m_Labels.ToHashSet()) and
                    // the write does not even take. Same call, both problems.
                    //
                    // force:true registers the label on the settings object, so the manual AddLabel this
                    // used to do first is now redundant. postEvent:false is deliberate: each event makes
                    // the Groups window rebuild its whole entry tree, which across thousands of entries
                    // in one synchronous loop is a hard editor stall. One BatchModification event is
                    // fired after the run instead. Persistence does not depend on postEvent -
                    // AddressableAssetGroup.SetDirty gates EditorUtility.SetDirty on groupModified.
                    //
                    // The counter now follows SetLabel's return value rather than being incremented
                    // unconditionally. An unconditional counter is what let this report "3591 labels
                    // applied" while none of them stuck.
                    if (entry.SetLabel(label, true, true, false))
                    {
                        result.LabelsApplied++;
                        _labelsTouched = true;
                        LogVerbose($"Applied label '{label}' to {assetPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error applying labels to {assetPath}: {ex.Message}");
            }
        }

        private void ProcessVersionRules(List<string> assetPaths, ProcessResult result, Action<float, string> progressCallback)
        {
            if (_ruleData.VersionRules == null || _ruleData.VersionRules.Count == 0)
            {
                Log("No version rules to process");
                return;
            }

            // Sort rules by priority (higher first)
            var sortedRules = _ruleData.VersionRules
                .Where(r => r != null && r.Enabled)
.ToList();

            // Priority is the default ordering, but CompositeLayoutRuleData can ask for its source
            // order to survive (see LayoutRuleData.PreserveRuleOrder). Sorting unconditionally here is
            // what made that setting inert.
            if (!_ruleData.PreserveRuleOrder)
            {
                sortedRules = sortedRules.OrderByDescending(r => r.Priority).ToList();
            }

            Log($"Processing {sortedRules.Count} version rules");

            int processed = 0;
            int total = assetPaths.Count;

            foreach (var assetPath in assetPaths)
            {
                processed++;
                if (processed % 100 == 0)
                {
                    float progress = 0.9f + (0.1f * (processed / (float)total));
                    progressCallback?.Invoke(progress, $"Processing versions ({processed}/{total})...");
                }

                // Find first matching rule
                VersionRule matchedRule = null;
                foreach (var rule in sortedRules)
                {
                    if (rule.IsMatch(assetPath))
                    {
                        matchedRule = rule;
                        break; // Use first matching rule (highest priority)
                    }
                }

                if (matchedRule != null)
                {
                    ApplyVersionRule(assetPath, matchedRule, result);
                }
            }
        }

        private void ApplyVersionRule(string assetPath, VersionRule rule, ProcessResult result)
        {
            try
            {
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                var entry = _settings.FindAssetEntry(guid);

                // Skip if not in addressables
                if (entry == null)
                {
                    return;
                }

                // Skip if already has version and rule says skip existing
                bool hasVersion = entry.labels.Any(l => l.StartsWith("version:"));
                if (rule.SkipExisting && hasVersion)
                {
                    return;
                }

                // Generate version
                string version = rule.GenerateVersion(assetPath);
                if (string.IsNullOrEmpty(version))
                {
                    result.Warnings.Add($"Rule '{rule.RuleName}' generated empty version for: {assetPath}");
                    return;
                }

                // Format version as label: "version:1.0.0"
                string versionLabel = $"version:{version}";

                // Remove existing version labels. This removal has to go through SetLabel for the
                // same reason the additions below do - and it is the easiest of the three to miss.
                // Fixing only the additions would make the writes start landing while the removals
                // stayed dead, so every version bump would leave the previous "version:x.y.z" label
                // attached and a load-by-version query would return assets of several versions at once.
                var existingVersionLabels = entry.labels.Where(l => l.StartsWith("version:")).ToList();
                foreach (var oldLabel in existingVersionLabels)
                {
                    if (oldLabel == versionLabel) continue;   // already correct, leave it alone
                    if (entry.SetLabel(oldLabel, false, false, false))
                    {
                        _labelsTouched = true;
                    }
                }

                // Add version label to entry (force:true registers it on the settings object).
                if (entry.SetLabel(versionLabel, true, true, false))
                {
                    result.VersionsApplied++;
                    _labelsTouched = true;
                    LogVerbose($"Applied version '{version}' to {assetPath}");
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Error applying version rule to {assetPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires exactly one settings-level modification event for a run's worth of label writes.
        /// </summary>
        /// <remarks>
        /// Every individual SetLabel is issued with postEvent:false, which keeps the Groups window from
        /// rebuilding its entry tree once per entry. Something still has to tell the window and the
        /// settings locator that the label tables moved, or the current session keeps serving a stale
        /// key->entry index and a load-by-label in Play Mode misses the assets that were just labeled.
        /// </remarks>
        private void FlushLabelEvent()
        {
            if (!_labelsTouched) return;

            _settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            _labelsTouched = false;
        }

        private void Log(string message)
        {
            Debug.Log($"[LayoutRuleProcessor] {message}");
        }

        private void LogVerbose(string message)
        {
            if (_verboseLogging)
            {
                Debug.Log($"[LayoutRuleProcessor] {message}");
            }
        }
    }
}
