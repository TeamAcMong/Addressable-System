using NUnit.Framework;
using UnityEngine;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Regression coverage for the AddressRule.GetOrCreateTargetGroup schema bug:
    /// AddressableAssetSettings.CreateGroup's schemasToCopy/types parameters are opt-in, so a
    /// group created with neither ends up with an empty schema set and silently contributes
    /// nothing to a content build. That is exactly what happened to
    /// Assets/AddressableAssetsData/AssetGroups/Scene.asset (two scene entries, m_Schemas: []).
    /// See AddressableGroupSchemaUtility for the fix these tests lock in.
    /// </summary>
    /// <remarks>
    /// Runs entirely in memory: AddressableAssetSettings.Create(..., isPersisted: false) sets
    /// AddressableAssetSettings.IsPersisted to false, which makes
    /// AddressableAssetGroup.GetSchemaAssetPath return string.Empty, which in turn makes
    /// AddressableAssetGroupSchemaSet.AddSchema skip its AssetDatabase.CreateAsset call (verified
    /// against Library/PackageCache/com.unity.addressables@8460f1c9c927/Editor/Settings
    /// /AddressableAssetSettings.cs and AddressableAssetGroup.cs). No group or schema assets are
    /// written under Assets/ by this fixture.
    /// This is an EditMode test (AddressableManager.Tests.Editor.asmdef, includePlatforms:
    /// ["Editor"]) - it needs the live Editor-only Addressables API
    /// (UnityEditor.AddressableAssets.Settings) and cannot run headless outside the Unity Test
    /// Runner / -runTests batchmode.
    /// </remarks>
    [TestFixture]
    public class AddressRuleGroupSchemaTests
    {
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _settings = AddressableAssetSettings.Create(
                "Assets/_Temp_AddressRuleGroupSchemaTests",
                "AddressRuleGroupSchemaTests.Settings",
                createDefaultGroups: true,
                isPersisted: false);
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings != null)
                UnityEngine.Object.DestroyImmediate(_settings, true);
            _settings = null;
        }

        [Test]
        public void GetOrCreateTargetGroup_NewGroupName_CreatesGroupWithBundledAssetGroupSchema()
        {
            var rule = new AddressRule { TargetGroupName = "RuleEngine_NewGroup" };

            var group = rule.GetOrCreateTargetGroup(_settings);

            Assert.IsNotNull(group,
                "GetOrCreateTargetGroup should return a group for a valid settings object");
            Assert.IsNotNull(group.GetSchema<BundledAssetGroupSchema>(),
                "A group created by the rule engine must carry a BundledAssetGroupSchema, or it " +
                "silently contributes nothing to a content build - this was the bug (CreateGroup " +
                "was called with schemasToCopy: null and no types)");
            Assert.IsNotNull(group.GetSchema<ContentUpdateGroupSchema>(),
                "A rule-engine-created group should also carry a ContentUpdateGroupSchema so it " +
                "can participate in a content update build (ContentUpdateScript.GroupFilter " +
                "requires both schemas before a group is even considered)");
        }

        [Test]
        public void GetOrCreateTargetGroup_DefaultGroupHasNoSchemas_FallsBackToFreshSchemas()
        {
            // Force the "nothing usable to copy" branch by stripping the schemas DefaultGroup
            // ("Default Local Group") normally ships with.
            var defaultGroup = _settings.DefaultGroup;
            defaultGroup.RemoveSchema(typeof(BundledAssetGroupSchema));
            defaultGroup.RemoveSchema(typeof(ContentUpdateGroupSchema));
            Assert.AreEqual(0, defaultGroup.Schemas.Count,
                "Test setup: DefaultGroup should have no schemas left to copy");

            var rule = new AddressRule { TargetGroupName = "RuleEngine_FallbackGroup" };

            var group = rule.GetOrCreateTargetGroup(_settings);

            Assert.IsNotNull(group.GetSchema<BundledAssetGroupSchema>(),
                "Even with nothing to copy from DefaultGroup, the rule engine must still fall " +
                "back to attaching a fresh BundledAssetGroupSchema");
            Assert.IsNotNull(group.GetSchema<ContentUpdateGroupSchema>(),
                "...and a fresh ContentUpdateGroupSchema alongside it");
        }
    }
}
