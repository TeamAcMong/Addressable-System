using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The settings contract, grouped by what each failure costs you rather than by rule id.
    /// </summary>
    /// <remarks>
    /// The contract has fifty-eight rules and the old tab listed them flat, sorted by id. That is
    /// the maintainer's ordering, not the user's: nothing about <c>settings.BundleTimeout</c> sitting
    /// next to <c>settings.BuildRemoteCatalog</c> tells you which one is stopping your release this
    /// afternoon. The grouping here is the first question anyone actually has.
    ///
    /// <b>The group names are claims, and each one is checkable.</b> "Fails the CI gate" is not a
    /// figure of speech: <c>CatalogVerifier.VerifySettingsContract</c> puts an unsatisfied rule into
    /// <c>Problems</c> unless it is warning-only, and <c>CatalogVerificationResult.Passed</c> is
    /// <c>Problems.Count == 0</c>. Warning-only rules land in <c>Warnings</c> and do not fail it.
    /// Those two branches are the entire basis for the split, so the headings cannot drift from
    /// what the CI gate really does without this comment becoming false.
    ///
    /// <b>Skipped is not passed.</b> In Local-only mode the remote rules short-circuit to satisfied,
    /// which would list them among the passing rules and quietly overstate what has been verified.
    /// They are detected by the marker <c>CdnBuildModes.NotApplicable</c> that the contract already
    /// puts at the front of their current-value display, and shown in their own group as unmeasured.
    /// </remarks>
    public sealed class ValidatorSection : IHubSection
    {
        /// <summary>How a rule is grouped, in the order the groups are shown.</summary>
        private enum Verdict
        {
            FailsGate = 0,
            CostsLater = 1,
            NotApplicable = 2,
            Passing = 3,
        }

        /// <summary>
        /// Throttled: evaluating the contract re-reads every rule against live project state.
        /// </summary>
        /// <remarks>
        /// Fifty-eight rules, each with its own <c>ReadCurrent</c> and <c>IsSatisfied</c> closure over
        /// the settings asset and the group list. Cheap individually, not cheap once a second forever.
        /// </remarks>
        private readonly HealthThrottle _health = new HealthThrottle();

        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Validator;

        /// <inheritdoc />
        public string Title => "Validator";

        /// <inheritdoc />
        public string Subtitle => "Every contract rule, grouped by what it costs you";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Configure;

        /// <inheritdoc />
        public SectionHealth GetHealth() => _health.Get(ComputeHealth);

        private SectionHealth ComputeHealth()
        {
            // No settings asset is a real, common state - a fresh project, or CI pointed at the
            // wrong folder - and it is NOT a pass. BuildRules() throws in that case rather than
            // returning an empty list, which is the right call: an empty checklist that reports
            // "0 problems" is the two-state lie in its purest form.
            if (AddressableAssetSettingsDefaultObject.Settings == null)
            {
                return SectionHealth.NotMeasured(
                    "No AddressableAssetSettings asset in this project, so no rule could be read.");
            }

            int fails = 0, warns = 0;

            foreach (var evaluation in Evaluate())
            {
                switch (Classify(evaluation))
                {
                    case Verdict.FailsGate:  fails++; break;
                    case Verdict.CostsLater: warns++; break;
                }
            }

            if (fails > 0)
            {
                return SectionHealth.Blocked(
                    $"{fails} to fix",
                    fails == 1
                        ? "One settings-contract rule fails, and the CI verification gate refuses a build on it."
                        : $"{fails} settings-contract rules fail, and the CI verification gate refuses a build on them.");
            }

            if (warns > 0)
                return SectionHealth.Warning($"{warns} to watch");

            return SectionHealth.Ok();
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "validator-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown()
        {
            _health.Invalidate();
            Rebuild();
        }

        // ------------------------------------------------------------------ render

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            if (AddressableAssetSettingsDefaultObject.Settings == null)
            {
                _body.Add(BuildNoSettingsState());
                return;
            }

            List<SettingsRuleEvaluation> evaluations;
            try
            {
                evaluations = Evaluate();
            }
            catch (Exception ex)
            {
                _body.Add(Note(
                    $"The contract could not be read: {ex.Message}\n\n" +
                    "Nothing below has been checked. This is not a pass."));
                Debug.LogException(ex);
                return;
            }

            var buckets = new Dictionary<Verdict, List<SettingsRuleEvaluation>>();
            foreach (Verdict v in Enum.GetValues(typeof(Verdict)))
                buckets[v] = new List<SettingsRuleEvaluation>();

            foreach (var e in evaluations)
                buckets[Classify(e)].Add(e);

            _body.Add(BuildSummary(buckets));

            AddGroup(buckets[Verdict.FailsGate],
                "Fails the CI gate",
                HealthState.Blocked,
                "The build verifier refuses a build while any of these is unsatisfied.");

            AddGroup(buckets[Verdict.CostsLater],
                "Works today, costs you later",
                HealthState.Warning,
                "These do not fail the gate. They are the ones that turn into a live incident.");

            AddGroup(buckets[Verdict.NotApplicable],
                "Not applicable in Local-only mode",
                HealthState.NotMeasured,
                "Skipped rather than checked, because this project does not publish to a CDN. " +
                "Listed separately so a skipped rule is never counted as a passing one.");

            AddGroup(buckets[Verdict.Passing],
                "Passing",
                HealthState.Ok,
                null,
                collapsedByDefault: true);

            _body.Add(BuildManualFixNote());
        }

        private void AddGroup(
            List<SettingsRuleEvaluation> rules,
            string title,
            HealthState state,
            string blurb,
            bool collapsedByDefault = false)
        {
            if (rules.Count == 0) return;

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var header = new VisualElement();
            header.AddToClassList("hub-card-header");

            var label = new Label(title);
            label.AddToClassList("hub-card-title");
            ApplyState(label, state);
            header.Add(label);

            var count = new Label(rules.Count == 1 ? "1 rule" : $"{rules.Count} rules");
            count.AddToClassList("hub-card-count");
            header.Add(count);

            card.Add(header);

            var content = new VisualElement();
            content.style.display = collapsedByDefault ? DisplayStyle.None : DisplayStyle.Flex;
            card.Add(content);

            if (collapsedByDefault)
            {
                // Passing rules are collapsed, not hidden. A validator that only ever shows problems
                // cannot answer "did it actually check the thing I care about?", which is the
                // question someone asks right after a surprise.
                header.RegisterCallback<ClickEvent>(_ =>
                    content.style.display = content.style.display == DisplayStyle.None
                        ? DisplayStyle.Flex
                        : DisplayStyle.None);
                count.text += "  (click to show)";
            }

            if (!string.IsNullOrEmpty(blurb))
            {
                var blurbLabel = new Label(blurb);
                blurbLabel.AddToClassList("hub-rule-meta");
                blurbLabel.style.paddingLeft = 8;
                blurbLabel.style.paddingRight = 8;
                blurbLabel.style.paddingTop = 5;
                content.Add(blurbLabel);
            }

            foreach (var evaluation in rules)
                content.Add(BuildRuleRow(evaluation, state));

            _body.Add(card);
        }

        private VisualElement BuildRuleRow(SettingsRuleEvaluation evaluation, HealthState state)
        {
            var rule = evaluation.Rule;

            var row = new VisualElement();
            row.AddToClassList("hub-rule");

            var dot = new VisualElement();
            dot.AddToClassList("hub-rule-dot");
            ApplyState(dot, state);
            row.Add(dot);

            var text = new VisualElement();
            text.AddToClassList("hub-rule-text");

            var headline = new Label(rule.Description);
            headline.AddToClassList("hub-rule-headline");
            text.Add(headline);

            // Current versus expected, only when they differ. Printing "found X, expected X" on a
            // passing rule is noise that makes the rows that matter harder to find.
            if (state != HealthState.Ok)
            {
                var meta = new Label($"found  {evaluation.CurrentDisplay}     expected  {rule.ExpectedDisplay}");
                meta.AddToClassList("hub-rule-meta");
                text.Add(meta);
            }

            var id = new Label(rule.IsGroupScoped ? $"{rule.Id}   ·   group “{rule.GroupName}”" : rule.Id);
            id.AddToClassList("hub-rule-id");
            text.Add(id);

            row.Add(text);

            var actions = new VisualElement();
            actions.AddToClassList("hub-rule-actions");

            if (rule.CanAutoFix)
            {
                var fix = new Button(() => RunFix(rule)) { text = "Fix" };
                fix.AddToClassList("hub-btn");
                actions.Add(fix);
            }
            else if (state != HealthState.Ok && state != HealthState.NotMeasured)
            {
                // Say WHY there is no button. A greyed-out or absent Fix reads as "the tool is
                // broken" unless it says "this one is a human call", which for these rules it is.
                var manual = new Label("manual");
                manual.AddToClassList("hub-rule-id");
                manual.tooltip = rule.IsWarningOnly
                    ? "Fixing this touches things outside the Addressables settings, so it is not automated."
                    : "There is more than one correct answer here, so the tool will not pick one for you.";
                actions.Add(manual);
            }

            row.Add(actions);
            return row;
        }

        private void RunFix(SettingsRule rule)
        {
            try
            {
                rule.Fix();
                Debug.Log($"[Validator] Applied the fix for '{rule.Id}'.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "The fix did not apply",
                    $"'{rule.Id}' could not be fixed automatically.\n\n{ex.Message}\n\n" +
                    "Nothing was changed. The Console has the full trace.",
                    "OK");
            }
            finally
            {
                // Re-read rather than assume. The whole contract is re-evaluated live, so a fix that
                // silently failed, or that satisfied one rule by breaking another, shows up here
                // immediately instead of being reported as done.
                _health.Invalidate();
                Rebuild();
            }
        }

        private VisualElement BuildSummary(Dictionary<Verdict, List<SettingsRuleEvaluation>> buckets)
        {
            var strip = new VisualElement();
            strip.AddToClassList("hub-summary");

            AddSummaryItem(strip, HealthState.Blocked,     buckets[Verdict.FailsGate].Count,     "fail the gate");
            AddSummaryItem(strip, HealthState.Warning,     buckets[Verdict.CostsLater].Count,    "to watch");
            AddSummaryItem(strip, HealthState.Ok,          buckets[Verdict.Passing].Count,       "passing");
            AddSummaryItem(strip, HealthState.NotMeasured, buckets[Verdict.NotApplicable].Count, "not applicable");

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            strip.Add(spacer);

            int fixable = 0;
            foreach (var e in buckets[Verdict.FailsGate])
                if (e.Rule.CanAutoFix) fixable++;
            foreach (var e in buckets[Verdict.CostsLater])
                if (e.Rule.CanAutoFix) fixable++;

            var recheck = new Button(Rebuild) { text = "Re-check" };
            recheck.AddToClassList("hub-btn");
            strip.Add(recheck);

            if (fixable > 0)
            {
                var fixAll = new Button(() => FixAll(buckets))
                {
                    text = fixable == 1 ? "Fix 1 safe issue" : $"Fix {fixable} safe issues",
                };
                fixAll.AddToClassList("hub-btn");
                strip.Add(fixAll);
            }

            return strip;
        }

        private static void AddSummaryItem(VisualElement parent, HealthState state, int count, string label)
        {
            var item = new VisualElement();
            item.AddToClassList("hub-summary-item");

            var dot = new VisualElement();
            dot.AddToClassList("hub-summary-dot");
            ApplyState(dot, state);
            item.Add(dot);

            var text = new Label($"{count} {label}");
            text.AddToClassList("hub-summary-label");
            item.Add(text);

            parent.Add(item);
        }

        private void FixAll(Dictionary<Verdict, List<SettingsRuleEvaluation>> buckets)
        {
            var fixable = new List<SettingsRule>();
            foreach (var e in buckets[Verdict.FailsGate])
                if (e.Rule.CanAutoFix) fixable.Add(e.Rule);
            foreach (var e in buckets[Verdict.CostsLater])
                if (e.Rule.CanAutoFix) fixable.Add(e.Rule);

            if (fixable.Count == 0) return;

            // Name what is about to change. "Fix All" against a project's build settings is not a
            // gesture anyone should make blind, and CanAutoFix already excludes the rules where the
            // right answer is a judgement call.
            var names = new System.Text.StringBuilder();
            foreach (var rule in fixable)
                names.Append("  • ").Append(rule.Id).Append('\n');

            bool go = EditorUtility.DisplayDialog(
                "Apply these fixes?",
                $"{fixable.Count} rule(s) will be written to the Addressables settings:\n\n{names}\n" +
                "Rules with more than one correct answer are not in this list and are never fixed " +
                "automatically.",
                "Apply", "Cancel");

            if (!go) return;

            int applied = 0, failed = 0;
            foreach (var rule in fixable)
            {
                try
                {
                    rule.Fix();
                    applied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Debug.LogError($"[Validator] '{rule.Id}' could not be fixed: {ex.Message}");
                    Debug.LogException(ex);
                }
            }

            // Report effect, not intent: applied is counted from calls that returned, and the
            // re-check below is what actually proves the rules now pass.
            Debug.Log($"[Validator] Fix All: {applied} applied, {failed} failed. Re-checking.");
            _health.Invalidate();
            Rebuild();
        }

        private VisualElement BuildNoSettingsState()
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var title = new Label("Nothing has been checked");
            title.AddToClassList("hub-card-title");
            ApplyState(title, HealthState.NotMeasured);
            box.Add(title);

            var body = new Label(
                "This project has no AddressableAssetSettings asset, so not one of the contract's " +
                "rules could be read. That is different from passing, and this screen will not " +
                "pretend otherwise.\n\n" +
                "Open the Addressables Groups window once to create the asset, then re-check.");
            body.AddToClassList("hub-note-text");
            box.Add(body);

            var open = new Button(() => EditorApplication.ExecuteMenuItem("Window/Asset Management/Addressables/Groups"))
            {
                text = "Open Addressables Groups",
            };
            open.AddToClassList("hub-btn");
            open.style.marginTop = 6;
            open.style.alignSelf = Align.FlexStart;
            box.Add(open);

            return box;
        }

        private static VisualElement BuildManualFixNote()
        {
            return Note(
                "Why some rules have no Fix button. An automatic fix is offered only where one answer " +
                "is unambiguously right. settings.RemoteOriginIsKnown has two — add the origin to " +
                "CdnSettings, or build against a different profile — and they mean different things, " +
                "so the tool refuses to guess. A rule that writes a field another rule writes back is " +
                "how a Fix All ends up depending on which one ran last.");
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

        // ------------------------------------------------------------------ model

        private static List<SettingsRuleEvaluation> Evaluate()
        {
            var result = new List<SettingsRuleEvaluation>();
            foreach (var rule in SettingsContract.BuildRules())
                result.Add(rule.Evaluate());
            return result;
        }

        /// <summary>
        /// Which group a rule belongs to.
        /// </summary>
        /// <remarks>
        /// The Local-only test comes FIRST, before <c>Passed</c>. Those rules short-circuit to
        /// satisfied in that mode, so testing <c>Passed</c> first would file them under "Passing"
        /// and count a rule nobody checked towards the number of rules that hold.
        /// </remarks>
        private static Verdict Classify(SettingsRuleEvaluation evaluation)
        {
            if (!string.IsNullOrEmpty(evaluation.CurrentDisplay) &&
                evaluation.CurrentDisplay.StartsWith(CdnBuildModes.NotApplicable, StringComparison.Ordinal))
            {
                return Verdict.NotApplicable;
            }

            if (evaluation.Passed) return Verdict.Passing;

            return evaluation.Rule.IsWarningOnly ? Verdict.CostsLater : Verdict.FailsGate;
        }

        private static void ApplyState(VisualElement element, HealthState state)
        {
            if (element == null) return;

            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);

            element.AddToClassList(SectionHealth.StyleClassFor(state));
        }
    }
}
