using NUnit.Framework;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Covers what "no group is marked Cannot Change Post Release" is allowed to mean.
    /// </summary>
    /// <remarks>
    /// The Update Preview tab used to report a hard "the restriction check could not run ...
    /// configuration failure" for every project with no static groups, whatever the reason. That
    /// blocked an ordinary all-remote CDN layout, which has nothing immutable and therefore nothing
    /// the restriction check could possibly guard - and the advice it printed contradicted itself:
    /// "if no content is static, ensure at least one group has StaticContent enabled".
    ///
    /// The distinguishing fact is whether anything ships inside the player. These tests pin both
    /// branches so the two cannot collapse back into one answer.
    ///
    /// In-memory only: AddressableAssetSettings.Create(..., isPersisted: false) makes
    /// AddressableAssetGroupSchemaSet.AddSchema skip its AssetDatabase.CreateAsset call, so nothing is
    /// written under Assets/. Same fixture shape as AddressRuleGroupSchemaTests.
    /// </remarks>
    [TestFixture]
    public class ContentUpdateRestrictionsTests
    {
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = AddressableAssetSettings.Create(
                "Assets/_Temp_ContentUpdateRestrictionsTests",
                "ContentUpdateRestrictionsTests.Settings",
                createDefaultGroups: false,
                isPersisted: false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
                UnityEngine.Object.DestroyImmediate(_settings, true);
            _settings = null;
        }

        private AddressableAssetGroup AddGroup(string name, string buildPathVariable)
        {
            var group = _settings.CreateGroup(
                name, false, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));

            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
            Assert.IsNotNull(bundleSchema, "fixture: the group should have a BundledAssetGroupSchema");
            bundleSchema.BuildPath.SetVariableByName(_settings, buildPathVariable);

            // Every group in this fixture is explicitly NOT static; that is the state under test.
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;

            return group;
        }

        [Test]
        public void NoStaticGroups_AllRemote_PassesInsteadOfRefusingToEvaluate()
        {
            AddGroup("RemoteA", AddressableAssetSettings.kRemoteBuildPath);
            AddGroup("RemoteB", AddressableAssetSettings.kRemoteBuildPath);

            var result = new ContentUpdateCheckResult();
            ContentUpdateRestrictions.EvaluateStaticContentConfiguration(_settings, result);

            Assert.IsTrue(result.CanEvaluate,
                "Every group is remote, so nothing is immutable and there is nothing for the " +
                "restriction check to guard. Refusing to evaluate blocks a legitimate all-remote " +
                "layout - which is exactly what the Update Preview tab used to do.");
            Assert.IsTrue(result.Passed, "A check with nothing to guard passes.");
            StringAssert.Contains("not because content was compared", result.Message,
                "A pass with nothing to guard must not read like a pass that compared content. This " +
                "phrase is the difference between 'safe' and 'nothing was checked', and it is the " +
                "whole reason this branch reports rather than staying silent.");
        }

        [Test]
        public void NoStaticGroups_WithLocalGroup_RefusesToEvaluateAndNamesTheGroup()
        {
            AddGroup("RemoteA", AddressableAssetSettings.kRemoteBuildPath);
            AddGroup("ShipsInPlayer", AddressableAssetSettings.kLocalBuildPath);

            var result = new ContentUpdateCheckResult();
            ContentUpdateRestrictions.EvaluateStaticContentConfiguration(_settings, result);

            Assert.IsFalse(result.CanEvaluate,
                "A group that builds Local ships inside the player and can never be replaced by a " +
                "content update, so leaving it unmarked is the misconfiguration this check exists " +
                "to catch - it genuinely cannot run.");
            Assert.IsFalse(result.Passed);
            StringAssert.Contains("ShipsInPlayer", result.Message,
                "Naming the offending group is the whole value of the message; without it the user " +
                "has to guess which group needs Cannot Change Post Release.");
            StringAssert.DoesNotContain("RemoteA", result.Message,
                "A remote group is not the problem and must not be named as one.");
        }

        [Test]
        public void NoGroupsAtAll_PassesRatherThanBlocking()
        {
            var result = new ContentUpdateCheckResult();
            ContentUpdateRestrictions.EvaluateStaticContentConfiguration(_settings, result);

            Assert.IsTrue(result.CanEvaluate,
                "No groups means no local content, so the same reasoning as the all-remote case " +
                "applies: there is nothing to guard.");
            Assert.IsTrue(result.Passed);
        }
    }
}
