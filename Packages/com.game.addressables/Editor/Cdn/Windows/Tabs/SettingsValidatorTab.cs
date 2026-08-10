using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Design doc §5.9/§5.10 "Settings Validator" tab: renders the <see cref="SettingsContract"/>
    /// (design doc §9) as a pass/fail checklist with a Fix All button.
    /// </summary>
    /// <remarks>
    /// This class is a thin view: it calls <see cref="SettingsContract.BuildRules()"/>,
    /// <see cref="SettingsRule.Evaluate"/> and <see cref="SettingsRule.Fix"/> and renders the results.
    /// It does not decide what a passing value looks like - that is entirely SettingsContract's job
    /// (repo Invariant 2), so this tab and CatalogVerifier (CI, task 1.6) can never drift apart.
    /// </remarks>
    public sealed class SettingsValidatorTab : ICdnManagerTab
    {
        public string TabName => "Validator";

        private HelpBox _summaryBox;
        private VisualElement _rowsContainer;
        private Button _fixAllButton;
        private Button _recheckButton;
        private Button _copyReportButton;

        private List<SettingsRuleEvaluation> _currentEvaluations;

        public VisualElement CreateView()
        {
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/SettingsValidatorTab.uxml");

            VisualElement root;
            if (visualTree != null)
            {
                root = visualTree.CloneTree();
            }
            else
            {
                Debug.LogError("[SettingsValidatorTab] Failed to load SettingsValidatorTab.uxml. Creating fallback UI.");
                root = CreateFallbackUI();
                return root;
            }

            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/SettingsValidatorTab.uss");
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            _summaryBox = root.Q<HelpBox>("validator-summary");
            _rowsContainer = root.Q<VisualElement>("validator-rows");
            _fixAllButton = root.Q<Button>("validator-fix-all-btn");
            _recheckButton = root.Q<Button>("validator-recheck-btn");
            _copyReportButton = root.Q<Button>("validator-copy-btn");

            _fixAllButton.clicked += FixAll;
            _recheckButton.clicked += Refresh;
            _copyReportButton.clicked += CopyReport;

            return root;
        }

        public void OnShown() => Refresh();

        /// <summary>
        /// Rebuilds the rule list from live project state and re-renders every row. This is what makes
        /// deliberately reverting one setting show up red again: nothing here is cached across calls -
        /// Re-check (and simply switching back to this tab) always re-reads the project from scratch.
        /// </summary>
        private void Refresh()
        {
            _rowsContainer.Clear();

            IReadOnlyList<SettingsRule> rules;
            try
            {
                rules = SettingsContract.BuildRules();
            }
            catch (InvalidOperationException ex)
            {
                _currentEvaluations = null;
                _summaryBox.messageType = HelpBoxMessageType.Error;
                _summaryBox.text = ex.Message;
                _fixAllButton.text = "Fix All";
                _fixAllButton.SetEnabled(false);
                return;
            }

            _currentEvaluations = rules.Select(r => r.Evaluate()).ToList();

            var projectEvaluations = _currentEvaluations.Where(e => !e.Rule.IsGroupScoped).ToList();
            var groupEvaluations = _currentEvaluations.Where(e => e.Rule.IsGroupScoped).ToList();

            if (projectEvaluations.Count > 0)
            {
                _rowsContainer.Add(BuildSectionHeader("Addressable Asset Settings"));
                foreach (var evaluation in projectEvaluations)
                    _rowsContainer.Add(BuildRuleRow(evaluation));
            }

            if (groupEvaluations.Count > 0)
            {
                int groupCount = groupEvaluations.Select(e => e.Rule.GroupName).Distinct().Count();
                _rowsContainer.Add(BuildSectionHeader($"Group Schemas - {groupCount} group{(groupCount == 1 ? "" : "s")}"));
                foreach (var evaluation in groupEvaluations)
                    _rowsContainer.Add(BuildRuleRow(evaluation));
            }

            int failCount = _currentEvaluations.Count(e => !e.Passed && !e.Rule.IsWarningOnly);
            int warnCount = _currentEvaluations.Count(e => !e.Passed && e.Rule.IsWarningOnly);
            int fixableCount = _currentEvaluations.Count(e => !e.Passed && e.Rule.CanAutoFix);

            if (failCount == 0 && warnCount == 0)
            {
                _summaryBox.messageType = HelpBoxMessageType.Info;
                _summaryBox.text = $"All {_currentEvaluations.Count} checks pass.";
            }
            else
            {
                _summaryBox.messageType = failCount > 0 ? HelpBoxMessageType.Error : HelpBoxMessageType.Warning;
                _summaryBox.text = BuildSummaryText(failCount, warnCount, fixableCount);
            }

            _fixAllButton.text = $"Fix All ({fixableCount})";
            _fixAllButton.SetEnabled(fixableCount > 0);
        }

        private void FixAll()
        {
            if (_currentEvaluations == null) return;

            int fixedCount = 0;
            foreach (var evaluation in _currentEvaluations)
            {
                if (evaluation.Passed || !evaluation.Rule.CanAutoFix) continue;
                evaluation.Rule.Fix();
                fixedCount++;
            }

            if (fixedCount > 0)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[CdnManager] Settings Validator: Fix All wrote {fixedCount} value(s).");
            }

            Refresh();
        }

        private void CopyReport()
        {
            if (_currentEvaluations == null) return;

            string report = SettingsContract.FormatReport(_currentEvaluations.Select(e => e.Rule));
            EditorGUIUtility.systemCopyBuffer = report;
            Debug.Log("[CdnManager] Settings Validator report copied to clipboard.");
        }

        private static VisualElement BuildSectionHeader(string text)
        {
            var header = new Label(text);
            header.AddToClassList("cdn-section-header");
            return header;
        }

        private static VisualElement BuildRuleRow(SettingsRuleEvaluation evaluation)
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-rule-row");
            row.tooltip = evaluation.Rule.Description;

            var icon = new Image { image = GetStatusIcon(evaluation) };
            icon.AddToClassList("cdn-rule-icon");
            row.Add(icon);

            var key = new Label(BuildRowLabel(evaluation.Rule));
            key.AddToClassList("cdn-rule-key");
            row.Add(key);

            var value = new Label(BuildRowValue(evaluation));
            value.AddToClassList("cdn-rule-value");
            if (!evaluation.Passed)
                value.AddToClassList("cdn-rule-value-changed");
            row.Add(value);

            if (!evaluation.Passed && evaluation.Rule.Fix == null)
            {
                var manualTag = new Label("manual");
                manualTag.AddToClassList("cdn-rule-manual-tag");
                row.Add(manualTag);
            }

            return row;
        }

        private static string BuildRowLabel(SettingsRule rule)
        {
            string field = ShortFieldName(rule.Id);
            return rule.IsGroupScoped ? $"{field} · {rule.GroupName}" : field;
        }

        private static string ShortFieldName(string ruleId)
        {
            int lastSeparator = ruleId.LastIndexOfAny(new[] { '.', ':' });
            return lastSeparator >= 0 ? ruleId.Substring(lastSeparator + 1) : ruleId;
        }

        private static string BuildRowValue(SettingsRuleEvaluation evaluation) =>
            evaluation.Passed
                ? evaluation.CurrentDisplay
                : $"{evaluation.CurrentDisplay} → {evaluation.Rule.ExpectedDisplay}";

        private static string BuildSummaryText(int failCount, int warnCount, int fixableCount)
        {
            string violations = $"{failCount} violation{(failCount == 1 ? "" : "s")}";
            string warnings = warnCount > 0 ? $", {warnCount} warning{(warnCount == 1 ? "" : "s")}" : "";
            string fixNote = fixableCount > 0
                ? $" Fix All writes the expected value for {fixableCount} of them; the rest need a manual decision (see each row's tooltip)."
                : " None of these have a safe automatic fix - see each row's tooltip.";
            return violations + warnings + "." + fixNote;
        }

        /// <summary>Icons per design doc §5.11: real Image elements fed by EditorGUIUtility.IconContent, not CSS pseudo-elements.</summary>
        private static Texture2D GetStatusIcon(SettingsRuleEvaluation evaluation)
        {
            string iconName = evaluation.Passed ? "TestPassed" : evaluation.Rule.IsWarningOnly ? "Warning" : "TestFailed";
            return EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
        }

        private VisualElement CreateFallbackUI()
        {
            var fallback = new VisualElement();
            fallback.style.flexGrow = 1;
            fallback.style.alignItems = Align.Center;
            fallback.style.justifyContent = Justify.Center;

            var label = new Label("Failed to load UI. Check that Editor/Cdn/UI/SettingsValidatorTab.uxml exists.");
            label.style.color = Color.red;
            fallback.Add(label);

            return fallback;
        }
    }
}
