using System;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Shared logic for attaching schemas to an Addressable group that has none.
    /// Used by <see cref="AddressRule.GetOrCreateTargetGroup"/> (auto-created groups) and by the
    /// "Tools/Addressable Manager/Repair Groups Missing Schemas" menu command (already-broken
    /// groups such as Assets/AddressableAssetsData/AssetGroups/Scene.asset, which shipped with
    /// two scene entries and an empty m_SchemaSet).
    /// </summary>
    public static class AddressableGroupSchemaUtility
    {
        /// <summary>
        /// Fallback schema types applied when there is no existing group to copy configuration
        /// from.
        ///
        /// - <see cref="BundledAssetGroupSchema"/> is required for a group to contribute
        ///   anything to a content build at all; a group without it is silently skipped.
        /// - <see cref="ContentUpdateGroupSchema"/> is what
        ///   AddressableAssetSettings.CreateDefaultGroup() itself pairs BundledAssetGroupSchema
        ///   with when bootstrapping a project's default group, and it is what every other
        ///   in-package group-creation call site pairs it with too (see
        ///   ContentUpdateScript.CreateContentUpdateGroup, AddressableAssetUtility
        ///   .ConvertAssetBundlesToAddressables, CheckBundleDupeDependencies's "Duplicate Asset
        ///   Isolation" group - all verified against the pinned Addressables 2.9.1 source under
        ///   Library/PackageCache/com.unity.addressables@8460f1c9c927/Editor). More concretely,
        ///   ContentUpdateScript.GroupFilter requires BOTH BundledAssetGroupSchema
        ///   (IncludeInBuild) and ContentUpdateGroupSchema to be present before a group is even
        ///   considered during "Check for Content Update Restrictions" / an update build - a
        ///   group missing ContentUpdateGroupSchema can never participate in a content update,
        ///   even though a plain content build would still succeed without it.
        /// </summary>
        public static readonly Type[] RequiredSchemaTypes =
        {
            typeof(BundledAssetGroupSchema),
            typeof(ContentUpdateGroupSchema)
        };

        /// <summary>
        /// Adds schemas to <paramref name="group"/> if it currently has none. Never touches a
        /// group that already carries at least one schema - this is a "fill the gap" helper, not
        /// a normalizer.
        /// </summary>
        /// <remarks>
        /// Prefers cloning the schema list already configured on <see
        /// cref="AddressableAssetSettings.DefaultGroup"/>. This mirrors the Addressables Groups
        /// window's own "Duplicate" and "New Group > &lt;template&gt;" commands, which both
        /// create groups by copying an existing schema list
        /// (AddressableAssetsSettingsGroupTreeView.CreateNewGroup/DuplicateGroup) rather than
        /// starting from class defaults - so the new group inherits this project's actual
        /// build/load path configuration (and whatever the Cdn settings contract has already
        /// normalized on DefaultGroup) instead of blank ProfileValueReferences.
        /// Falls back to fresh, default-valued <see cref="RequiredSchemaTypes"/> only when
        /// DefaultGroup itself has no schemas to copy - matching how
        /// AddressableAssetSettings.CreateDefaultGroup bootstraps a brand new project - or when
        /// <paramref name="group"/> IS DefaultGroup (nothing to copy from itself).
        /// </remarks>
        public static void EnsureSchemas(AddressableAssetGroup group, AddressableAssetSettings settings)
        {
            if (group == null || settings == null)
                return;

            if (group.Schemas != null && group.Schemas.Count > 0)
                return;

            var defaultGroup = settings.DefaultGroup;
            var schemasToCopy = defaultGroup != null && defaultGroup != group && defaultGroup.Schemas.Count > 0
                ? defaultGroup.Schemas
                : null;

            if (schemasToCopy != null)
            {
                foreach (var schema in schemasToCopy)
                    group.AddSchema(schema, false);
                return;
            }

            foreach (var type in RequiredSchemaTypes)
            {
                if (group.GetSchema(type) == null)
                    group.AddSchema(type);
            }
        }
    }
}
