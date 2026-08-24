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
    /// Duplicate-address detection must fire for two assets and stay silent for one.
    /// </summary>
    /// <remarks>
    /// <see cref="LayoutRuleProcessor"/> seeds every address already in the project before a run, so
    /// that a collision with an entry OUTSIDE the batch is visible - the on-import path processes one
    /// asset per call and could not otherwise see anything to collide with. The seed does not exclude
    /// the entries taking part in the run, so the ownership check has to: without it, every asset that
    /// already has an address collides with itself the moment it is re-imported.
    ///
    /// That defect reads as correct on a first run over fresh assets, because the seed knows nothing
    /// about them. It only appears on the second run, and a stable address provider makes every run
    /// after the first a second run. One project reported 3591 self-collisions against zero real ones.
    ///
    /// <b>These tests do not write.</b> Everything here goes through <c>PreviewRulesForAssets</c>, and
    /// the "apply, then re-import and apply again" scenario is reproduced by pointing a run at an asset
    /// that ALREADY holds the address the rule generates - which is precisely what the second run sees.
    /// Reproducing it by actually applying would mean this fixture mutating the project's shared
    /// AddressableAssetSettings, and a test that edits the environment it is measuring cannot be run
    /// twice in a row with the same meaning.
    /// </remarks>
    [TestFixture]
    public class LayoutRuleCollisionTests
    {
        /// <summary>An address provider that hands back the same string for every asset.</summary>
        /// <remarks>
        /// Test-only, and defined here rather than shipped: forcing a chosen address is the whole
        /// point of these tests, and no production provider can do it. Subclassing the real base class
        /// keeps the processor on its normal code path.
        /// </remarks>
        private sealed class FixedAddressProvider : AddressProviderBase
        {
            public string Address;

            public override string Provide(string assetPath) => Address;
        }

        private AddressableAssetSettings _settings;
        private readonly List<Object> _temporary = new List<Object>();

        private string _existingAssetPath;
        private string _existingAddress;

        [SetUp]
        public void SetUp()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;

            // Assert the precondition; never create it. Addressables itself is environment.
            Assert.IsNotNull(_settings,
                "This project has no AddressableAssetSettings asset. Open " +
                "Window > Asset Management > Addressables > Groups once to create one, then re-run.");

            FindUniquelyAddressedEntry(out _existingAssetPath, out _existingAddress);

            Assert.IsNotNull(_existingAssetPath,
                "No addressable entry in this project holds an address that no other entry also " +
                "holds. These tests need one asset whose address unambiguously belongs to it. Add " +
                "any asset to an Addressables group and give it a unique address, then re-run.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in _temporary)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }

            _temporary.Clear();
        }

        /// <summary>
        /// An asset re-claiming the address it already holds is not colliding with anything.
        /// </summary>
        /// <remarks>
        /// This IS the second run of the reported scenario. The asset is addressable, its address is
        /// the one the rule generates, and the seed has already recorded it - which is the exact state
        /// a re-import leaves behind.
        /// </remarks>
        [Test]
        public void AssetKeepingItsOwnAddress_IsNotACollision()
        {
            var rule = BuildFixedAddressRule("self-collision probe", _existingAddress, _existingAssetPath);

            // Without this the test could pass for the wrong reason: SkipExisting returns before the
            // collision check is ever reached, so a rule that skipped would report zero collisions
            // while proving nothing about the check under test.
            Assert.IsFalse(rule.SkipExisting,
                "SkipExisting must be off, or the run never reaches the duplicate-address check.");

            var processor = new LayoutRuleProcessor(BuildRuleData(rule));
            var result = processor.PreviewRulesForAssets(new List<string> { _existingAssetPath });

            Assert.IsEmpty(result.Collisions,
                $"'{_existingAssetPath}' was reported as colliding with itself over address " +
                $"'{_existingAddress}'. SeedExistingAddresses records entries taking part in this run, " +
                "so the ownership comparison in ApplyAddressRule is what keeps them from self-reporting.");

            Assert.IsFalse(result.Errors.Any(e => e.Contains("Duplicate address")),
                "No structured collision was recorded, but a duplicate-address error was still " +
                "written. The list and the prose are populated at the same site and must agree.");
        }

        /// <summary>Two different assets claiming one address is still reported.</summary>
        /// <remarks>
        /// The fix narrows the check, so this is the half that must not have been narrowed away. The
        /// address is deliberately one nothing in the project uses, so the collision is between the two
        /// assets in the batch and not with a seeded entry - otherwise a pass here would say nothing
        /// about batch-internal detection.
        /// </remarks>
        [Test]
        public void TwoAssetsClaimingOneAddress_IsStillACollision()
        {
            const string address = "layout-rule-collision-probe-address";
            Assert.IsFalse(AddressExistsInProject(address),
                $"'{address}' is already used by an entry in this project, so this test cannot tell " +
                "a batch-internal collision from one against a seeded entry. Rename it.");

            var paths = TwoDistinctEligibleAssets();

            var rule = BuildFixedAddressRule("real collision probe", address, pattern: null);
            var processor = new LayoutRuleProcessor(BuildRuleData(rule));
            var result = processor.PreviewRulesForAssets(paths);

            Assert.AreEqual(1, result.Collisions.Count,
                "Two assets given the same address must produce exactly one collision - one for the " +
                "second asset to arrive, none for the first.");

            var collision = result.Collisions[0];

            Assert.AreNotEqual(collision.FirstAsset, collision.SecondAsset,
                "A collision naming the same asset twice is the self-collision defect, not a finding.");

            Assert.AreEqual(address, collision.Address);

            Assert.IsTrue(result.Errors.Any(e => e.Contains("Duplicate address")),
                "A collision was recorded as data but no error was written for it. The CLI and the " +
                "build report read the prose; a finding only half-delivered is not delivered.");
        }

        /// <summary>Every collision names two different assets, whatever the batch.</summary>
        /// <remarks>
        /// The property the two tests above check on constructed inputs, asserted over a wide sweep
        /// instead - already-addressed assets and fresh ones together, which is the shape of a real
        /// re-import.
        ///
        /// <b>This is not the regression guard, and it was measured rather than assumed.</b> Run against
        /// the unfixed processor it PASSES, because it only reproduces the defect if some asset's
        /// existing address happens to equal its own file name, and in this project none do. The test
        /// that fails on the broken build is <see cref="AssetKeepingItsOwnAddress_IsNotACollision"/>,
        /// which constructs that condition instead of hoping for it. This one is kept for the projects
        /// where the condition is common - which is every project the rules have already run on once -
        /// but it must not be mistaken for the net.
        /// </remarks>
        [Test]
        public void NoCollisionEverNamesOneAssetTwice()
        {
            var paths = EligibleAssetPaths().Take(200).ToList();
            Assert.IsNotEmpty(paths, "No assets under Assets/ to run against.");

            var filter = ScriptableObject.CreateInstance<PathFilter>();
            filter.Pattern = "Assets/";
            filter.MatchMode = PathFilter.PathMatchMode.StartsWith;
            _temporary.Add(filter);

            var provider = ScriptableObject.CreateInstance<FileNameAddressProvider>();
            _temporary.Add(provider);

            var rule = new AddressRule
            {
                RuleName = "self-collision sweep",
                Enabled = true,
                Description = "Built by LayoutRuleCollisionTests. Never written to disk.",
            };
            rule.Filters.Add(filter);
            rule.AddressProvider = provider;

            var processor = new LayoutRuleProcessor(BuildRuleData(rule));
            var result = processor.PreviewRulesForAssets(paths);

            foreach (var collision in result.Collisions)
            {
                Assert.AreNotEqual(collision.FirstAsset, collision.SecondAsset,
                    $"'{collision.FirstAsset}' was reported as colliding with itself over " +
                    $"'{collision.Address}'.");
            }
        }

        // ------------------------------------------------------------------ fixture

        private AddressRule BuildFixedAddressRule(string name, string address, string pattern)
        {
            var provider = ScriptableObject.CreateInstance<FixedAddressProvider>();
            provider.Address = address;
            _temporary.Add(provider);

            var rule = new AddressRule
            {
                RuleName = name,
                Enabled = true,
                Description = "Built by LayoutRuleCollisionTests. Never written to disk.",
            };

            var filter = ScriptableObject.CreateInstance<PathFilter>();
            _temporary.Add(filter);

            if (string.IsNullOrEmpty(pattern))
            {
                filter.Pattern = "Assets/";
                filter.MatchMode = PathFilter.PathMatchMode.StartsWith;
            }
            else
            {
                filter.Pattern = pattern;
                filter.MatchMode = PathFilter.PathMatchMode.Exact;
            }

            rule.Filters.Add(filter);
            rule.AddressProvider = provider;
            return rule;
        }

        private LayoutRuleData BuildRuleData(AddressRule rule)
        {
            var data = ScriptableObject.CreateInstance<LayoutRuleData>();
            data.name = "CollisionProbeRules";
            data.AddAddressRule(rule);
            _temporary.Add(data);
            return data;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The first entry whose address no other entry in the project also carries.</summary>
        private void FindUniquelyAddressedEntry(out string assetPath, out string address)
        {
            assetPath = null;
            address = null;

            var counts = new Dictionary<string, int>();
            var pathOf = new Dictionary<string, string>();

            foreach (var group in _settings.groups)
            {
                if (group == null || group.entries == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    string path = AssetDatabase.GUIDToAssetPath(entry.guid);
                    if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;

                    counts.TryGetValue(entry.address, out int seen);
                    counts[entry.address] = seen + 1;
                    if (seen == 0) pathOf[entry.address] = path;
                }
            }

            foreach (var pair in counts)
            {
                if (pair.Value != 1) continue;

                address = pair.Key;
                assetPath = pathOf[pair.Key];
                return;
            }
        }

        private bool AddressExistsInProject(string address)
        {
            foreach (var group in _settings.groups)
            {
                if (group == null || group.entries == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry != null && entry.address == address) return true;
                }
            }

            return false;
        }

        private static List<string> TwoDistinctEligibleAssets()
        {
            var paths = EligibleAssetPaths().Take(2).ToList();

            Assert.AreEqual(2, paths.Count,
                "This test needs two assets under Assets/ and the project has fewer.");

            Assert.AreNotEqual(paths[0], paths[1]);
            return paths;
        }

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
    }
}
