using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressableManager.Editor.Filters;
using AddressableManager.Editor.Providers;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Tests
{
    /// <summary>
    /// The safety net under <see cref="LayoutRuleProcessor.PreviewRules"/>: a preview must not write.
    /// </summary>
    /// <remarks>
    /// The dry-run flag guards seven separate mutation sites inside LayoutRuleProcessor - two entry
    /// operations, one address write and four label writes - plus the save block. Reviewing that I
    /// found all seven is not evidence that I did, and the failure mode is the worst kind: a control
    /// labelled "preview" that silently rewrites every teammate's Addressables settings.
    ///
    /// So this asserts the property rather than the implementation. It snapshots the address, group
    /// and label set of EVERY entry in the project, runs a preview, and re-reads. Anything that moved
    /// fails, whichever write site let it through - including one added later by someone who never
    /// read this file.
    ///
    /// <b>The rule set is built here, in memory, and that is deliberate.</b> An earlier version read
    /// a LayoutRuleData asset out of the project and called <c>Assert.Ignore</c> when it found none -
    /// which is what happened: the suite went green with all three of these skipped, so the guard on
    /// the riskiest change in the release was never executed. A rule set is the test's INPUT, not its
    /// environment; constructing an input is not the same as a [SetUp] repairing a precondition, and
    /// a test that only runs on a correctly-configured project is a test that reports nothing on
    /// every other one.
    ///
    /// The rule is deliberately broad - every asset under Assets/ - so the preview has a great deal
    /// it WOULD do. A dry run that changes nothing because it matched nothing proves nothing.
    /// </remarks>
    [TestFixture]
    public class LayoutRuleDryRunTests
    {
        private AddressableAssetSettings _settings;
        private LayoutRuleData _ruleData;
        private readonly List<Object> _temporary = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;

            // Assert the precondition; never create it. Addressables itself IS environment - a test
            // that conjures the settings asset would be testing a project nobody has.
            Assert.IsNotNull(_settings,
                "This project has no AddressableAssetSettings asset. Open " +
                "Window > Asset Management > Addressables > Groups once to create one, then re-run.");

            _ruleData = BuildBroadRuleSet();
        }

        [TearDown]
        public void TearDown()
        {
            // These were never written to disk, but they are ScriptableObjects and Unity will leak
            // them across the run otherwise - and a leaked filter that survives into another fixture
            // is the sort of cross-test coupling that makes a suite order-dependent.
            foreach (var obj in _temporary)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }

            _temporary.Clear();
            _ruleData = null;
        }

        /// <summary>A preview must leave every entry's address, group and labels exactly as they were.</summary>
        [Test]
        public void PreviewRules_WritesNothing()
        {
            var before = Snapshot();

            var processor = new LayoutRuleProcessor(_ruleData);
            var result = processor.PreviewRules();

            var after = Snapshot();

            Assert.IsTrue(result.WasDryRun, "PreviewRules must mark its result as a dry run.");
            AssertUnchanged(before, after);
        }

        /// <summary>The same guarantee on the incremental path the asset postprocessor uses.</summary>
        /// <remarks>
        /// A separate test because it is a separate entry point: ApplyRulesToAssets has its own
        /// prelude and its own save block, and guarding one of the two would leave the path that runs
        /// on every asset import unprotected - which is the one that would do the damage.
        /// </remarks>
        [Test]
        public void PreviewRulesForAssets_WritesNothing()
        {
            var paths = EligibleAssetPaths().Take(50).ToList();
            Assert.IsNotEmpty(paths,
                "No assets under Assets/ to preview against. This test needs something to look at.");

            var before = Snapshot();

            var processor = new LayoutRuleProcessor(_ruleData);
            var result = processor.PreviewRulesForAssets(paths);

            var after = Snapshot();

            Assert.IsTrue(result.WasDryRun, "PreviewRulesForAssets must mark its result as a dry run.");
            AssertUnchanged(before, after);
        }

        /// <summary>
        /// The preview must actually have had something to do, or the two tests above are vacuous.
        /// </summary>
        /// <remarks>
        /// This is the guard against the failure this file already had once: a green result that
        /// proved nothing because the run never reached a write site. If the broad rule set stops
        /// planning anything, that is a signal the fixture has rotted - not a pass.
        /// </remarks>
        [Test]
        public void PreviewRules_ActuallyPlansSomething()
        {
            var processor = new LayoutRuleProcessor(_ruleData);
            var result = processor.PreviewRules();

            Assert.Greater(result.Planned.Count, 0,
                "A rule matching everything under Assets/ planned no changes at all. Either the " +
                "project has no eligible assets, or the preview is not reaching the write sites - " +
                "and in both cases the other tests in this fixture are proving nothing.");
        }

        /// <summary>Every planned change must describe a real difference, not a re-write of the same value.</summary>
        [Test]
        public void PlannedChanges_AreRealDifferences()
        {
            var processor = new LayoutRuleProcessor(_ruleData);
            var result = processor.PreviewRules();

            foreach (var change in result.Planned)
            {
                Assert.AreNotEqual(change.From, change.To,
                    $"Planned {change.Kind} for '{change.AssetPath}' has From == To, so it is not a change.");

                Assert.IsFalse(string.IsNullOrEmpty(change.AssetPath),
                    "A planned change with no asset path cannot be acted on.");
            }
        }

        // ------------------------------------------------------------------ fixture

        /// <summary>A rule set that matches everything under Assets/ and gives each asset an address.</summary>
        private LayoutRuleData BuildBroadRuleSet()
        {
            var filter = ScriptableObject.CreateInstance<PathFilter>();
            filter.Pattern = "Assets/";
            filter.MatchMode = PathFilter.PathMatchMode.StartsWith;
            _temporary.Add(filter);

            var provider = ScriptableObject.CreateInstance<FileNameAddressProvider>();
            _temporary.Add(provider);

            var rule = new AddressRule
            {
                RuleName = "dry-run probe",
                Enabled = true,
                Description = "Built by LayoutRuleDryRunTests. Never written to disk.",
            };
            rule.Filters.Add(filter);
            rule.AddressProvider = provider;

            var data = ScriptableObject.CreateInstance<LayoutRuleData>();
            data.name = "DryRunProbeRules";
            data.AddAddressRule(rule);
            _temporary.Add(data);

            return data;
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<string> EligibleAssetPaths()
        {
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                yield return path;
            }
        }

        /// <summary>guid → "group|address|sorted labels" for every entry in the project.</summary>
        private Dictionary<string, string> Snapshot()
        {
            var map = new Dictionary<string, string>();

            foreach (var group in _settings.groups)
            {
                if (group == null || group.entries == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null) continue;

                    // Labels are sorted so an ordering difference is not read as a change; every
                    // other field is compared verbatim.
                    var labels = entry.labels != null ? entry.labels.ToList() : new List<string>();
                    labels.Sort(System.StringComparer.Ordinal);

                    map[entry.guid] = $"{group.Name}|{entry.address}|{string.Join(",", labels)}";
                }
            }

            return map;
        }

        private static void AssertUnchanged(
            Dictionary<string, string> before, Dictionary<string, string> after)
        {
            Assert.AreEqual(before.Count, after.Count,
                "A dry run changed how many addressable entries exist. It must create and remove none.");

            foreach (var pair in before)
            {
                Assert.IsTrue(after.TryGetValue(pair.Key, out var now),
                    $"Entry {pair.Key} disappeared during a dry run.");

                Assert.AreEqual(pair.Value, now,
                    $"A dry run modified entry {pair.Key}.\n" +
                    $"  before: {pair.Value}\n" +
                    $"  after:  {now}\n" +
                    "One of LayoutRuleProcessor's mutation sites is not guarded by _dryRun.");
            }
        }
    }
}
