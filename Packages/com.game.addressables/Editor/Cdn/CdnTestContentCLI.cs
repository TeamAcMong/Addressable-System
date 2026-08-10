using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// CLI entry point for Phase 0 task 0.8: create a remote test group and test content.
    /// Idempotently creates test assets and an Addressable group bound to the Remote profile variables.
    /// </summary>
    /// <remarks>
    /// This CLI:
    /// 1. Creates or reuses test content at Assets/Examples/CdnTest/TestAsset.asset
    /// 2. Creates or reuses a "Remote Test" Addressable group bound to Remote profile variables
    /// 3. Ensures the group has both BundledAssetGroupSchema and ContentUpdateGroupSchema
    /// 4. Adds the test asset as an entry with address "cdn-test/example"
    /// 5. Verifies all effects before reporting success
    ///
    /// Idempotency: running multiple times does not create duplicates. If the group or entry
    /// already exists with the correct configuration, they are left as-is.
    /// </remarks>
    public static class CdnTestContentCLI
    {
        private const string TestAssetPath = "Assets/Examples/CdnTest/TestAsset.asset";
        private const string RemoteTestGroupName = "Remote Test";
        private const string TestAssetAddress = "cdn-test/example";

        /// <summary>
        /// Create remote test group and test content.
        ///
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnTestContentCLI.CreateRemoteTestContent
        ///
        /// Exit codes:
        ///   0 = success
        ///   1 = verification failed (group/entry exists but not properly configured)
        ///   2 = exception during execution
        /// </summary>
        public static void CreateRemoteTestContent()
        {
            try
            {
                Log("CDN Test Content Creation - Phase 0 Task 0.8");
                Log("");

                // Get settings
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    LogError("No AddressableAssetSettings found. Run CdnSetupCLI first.");
                    EditorApplication.Exit(2);
                    return;
                }

                Log("Step 1: Creating test content...");
                var testAsset = CreateOrGetTestAsset();
                if (testAsset == null)
                {
                    LogError("Failed to create or retrieve test asset");
                    EditorApplication.Exit(2);
                    return;
                }
                Log($"✓ Test asset ready at {TestAssetPath}");
                Log("");

                Log("Step 2: Creating Remote Test group...");
                var group = CreateOrGetRemoteTestGroup(settings);
                if (group == null)
                {
                    LogError("Failed to create or retrieve Remote Test group");
                    EditorApplication.Exit(2);
                    return;
                }
                Log($"✓ Group '{RemoteTestGroupName}' ready");
                Log("");

                Log("Step 3: Ensuring group has required schemas...");
                EnsureGroupSchemas(group, settings);
                Log("✓ Group schemas verified");
                Log("");

                Log("Step 4: Adding test asset as addressable entry...");
                var entry = AddAssetToGroup(group, settings, testAsset);
                if (entry == null)
                {
                    LogError("Failed to add test asset to group");
                    EditorApplication.Exit(2);
                    return;
                }
                Log($"✓ Entry '{TestAssetAddress}' ready");
                Log("");

                Log("Step 5: Configuring group for Remote profile...");
                ConfigureGroupForRemote(group, settings);
                Log("✓ Group configured for Remote profile variables");
                Log("");

                Log("Step 6: Persisting changes...");
                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(settings);
                EditorUtility.SetDirty(group);
                Log("✓ Assets saved");
                Log("");

                Log("Step 7: Verifying configuration...");
                if (!VerifyConfiguration(settings, group, entry, testAsset))
                {
                    LogError("Verification failed - configuration is incomplete or incorrect");
                    EditorApplication.Exit(1);
                    return;
                }
                Log("✓ All verifications passed");
                Log("");

                Log("=== SUCCESS ===");
                Log($"Test asset address: {TestAssetAddress}");
                Log($"Group name: {RemoteTestGroupName}");
                Log($"Asset path: {TestAssetPath}");
                Log($"Profile variables: Remote.BuildPath, Remote.LoadPath (bundles); Remote.CatalogBuildPath, Remote.CatalogLoadPath (catalog)");
                Log("");
                Log("The test asset is now available for download from the remote server when using the Local profile.");

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during test content creation: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Create test content as a simple ScriptableObject, or return existing one.
        /// Idempotent: if the asset already exists, it is returned as-is.
        /// </summary>
        private static ScriptableObject CreateOrGetTestAsset()
        {
            // Check if already exists
            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(TestAssetPath);
            if (existing != null)
            {
                Log($"  Test asset already exists at {TestAssetPath}");
                return existing;
            }

            // Create directory if needed
            string dir = System.IO.Path.GetDirectoryName(TestAssetPath);
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                Log($"  Created directory: {dir}");
            }

            // Create a simple test ScriptableObject
            var testAsset = ScriptableObject.CreateInstance<TestAssetData>();
            testAsset.name = "TestAsset";
            testAsset.Description = "Test asset for CDN remote verification (Phase 0 task 0.8)";

            AssetDatabase.CreateAsset(testAsset, TestAssetPath);
            AssetDatabase.SaveAssets();
            Log($"  Created test asset at {TestAssetPath}");

            return testAsset;
        }

        /// <summary>
        /// Find or create the "Remote Test" Addressable group.
        /// Does NOT attach schemas or configure profile paths - those are separate steps.
        /// </summary>
        private static AddressableAssetGroup CreateOrGetRemoteTestGroup(AddressableAssetSettings settings)
        {
            // Try to find existing group
            var existing = settings.FindGroup(RemoteTestGroupName);
            if (existing != null)
            {
                Log($"  Group '{RemoteTestGroupName}' already exists");
                return existing;
            }

            // Create new group using the Addressables API
            // Parameters to CreateGroup:
            // - groupName: "Remote Test"
            // - isDefaultGroup: false (this is not the default group)
            // - setAsActive: false (don't change the active group)
            // - readOnly: false (group is editable)
            // - schemasToCopy: null (we'll attach schemas separately via EnsureSchemas)
            var newGroup = settings.CreateGroup(RemoteTestGroupName, false, false, false, null);
            Log($"  Created group '{RemoteTestGroupName}'");

            return newGroup;
        }

        /// <summary>
        /// Ensure the group has both required schemas for content updates.
        /// Uses the standard utility to avoid code duplication.
        /// </summary>
        private static void EnsureGroupSchemas(AddressableAssetGroup group, AddressableAssetSettings settings)
        {
            if (group.Schemas != null && group.Schemas.Count >= 2)
            {
                // Check if we have both required types
                bool hasBundled = group.GetSchema<BundledAssetGroupSchema>() != null;
                bool hasContentUpdate = group.GetSchema<ContentUpdateGroupSchema>() != null;

                if (hasBundled && hasContentUpdate)
                {
                    Log($"  Group already has both required schemas");
                    return;
                }
            }

            // Use the standard utility to attach schemas
            AddressableGroupSchemaUtility.EnsureSchemas(group, settings);
            Log($"  Attached/verified required schemas (BundledAssetGroupSchema, ContentUpdateGroupSchema)");
        }

        /// <summary>
        /// Add test asset to the group as an addressable entry, or return existing entry.
        /// Idempotent: if the entry already exists with the same address, it is returned as-is.
        /// Uses CreateOrMoveEntry which is idempotent: calling it for an entry already in that group
        /// moves it to itself without side effects. See AddressableAssetSettings.cs:2541.
        /// </summary>
        private static AddressableAssetEntry AddAssetToGroup(AddressableAssetGroup group, AddressableAssetSettings settings, ScriptableObject testAsset)
        {
            string guid = AssetDatabase.AssetPathToGUID(TestAssetPath);
            if (string.IsNullOrEmpty(guid))
            {
                Log($"  Error: could not get GUID for {TestAssetPath}");
                return null;
            }

            // Try to find existing entry globally. See AddressableAssetSettings.cs:2330
            var existingEntry = settings.FindAssetEntry(guid);
            if (existingEntry != null)
            {
                if (existingEntry.address == TestAssetAddress && existingEntry.parentGroup == group)
                {
                    Log($"  Entry with address '{TestAssetAddress}' already exists in group");
                    return existingEntry;
                }
                else if (existingEntry.address != TestAssetAddress)
                {
                    Log($"  Found existing entry but address mismatch: '{existingEntry.address}' vs '{TestAssetAddress}'");
                    Log($"  Updating address...");
                    existingEntry.address = TestAssetAddress;
                }
            }

            // CreateOrMoveEntry is idempotent: calling it for an entry already in the target group
            // moves it to itself without side effects. See AddressableAssetSettings.cs:2541
            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            if (entry != null)
            {
                if (entry.address != TestAssetAddress)
                {
                    entry.address = TestAssetAddress;
                }
                Log($"  Entry with address '{TestAssetAddress}' ready");
            }

            return entry;
        }

        /// <summary>
        /// Configure the group to use Remote profile variables for bundle paths.
        /// Sets both the BundledAssetGroupSchema's BuildPath/LoadPath to the Remote variables.
        /// </summary>
        private static void ConfigureGroupForRemote(AddressableAssetGroup group, AddressableAssetSettings settings)
        {
            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
            if (bundleSchema == null)
            {
                LogWarning("  BundledAssetGroupSchema not found on group - cannot configure paths");
                return;
            }

            // Set bundle paths to use Remote profile variables (not Local)
            // Remote.BuildPath and Remote.LoadPath are the standard bundle path variables
            bundleSchema.BuildPath.SetVariableByName(settings, "Remote.BuildPath");
            bundleSchema.LoadPath.SetVariableByName(settings, "Remote.LoadPath");

            Log($"  Configured bundle paths: BuildPath=[Remote.BuildPath], LoadPath=[Remote.LoadPath]");
        }

        /// <summary>
        /// Verify that the group and entry are properly configured.
        /// Checks:
        /// - Group exists with correct name
        /// - Group has both required schemas
        /// - Entry exists with correct address
        /// - Entry points to the test asset
        /// - Group is bound to Remote profile variables
        /// </summary>
        private static bool VerifyConfiguration(AddressableAssetSettings settings, AddressableAssetGroup group,
                                                 AddressableAssetEntry entry, ScriptableObject testAsset)
        {
            bool allGood = true;

            // Verify group exists and is in settings
            if (!settings.groups.Contains(group))
            {
                LogError("  Verification: Group not found in settings.groups");
                allGood = false;
            }

            // Verify group name
            if (group.Name != RemoteTestGroupName)
            {
                LogError($"  Verification: Group name mismatch (expected '{RemoteTestGroupName}', got '{group.Name}')");
                allGood = false;
            }

            // Verify both required schemas exist
            var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
            if (bundleSchema == null)
            {
                LogError("  Verification: BundledAssetGroupSchema missing");
                allGood = false;
            }

            var contentUpdateSchema = group.GetSchema<ContentUpdateGroupSchema>();
            if (contentUpdateSchema == null)
            {
                LogError("  Verification: ContentUpdateGroupSchema missing");
                allGood = false;
            }

            // Verify entry address
            if (entry.address != TestAssetAddress)
            {
                LogError($"  Verification: Entry address mismatch (expected '{TestAssetAddress}', got '{entry.address}')");
                allGood = false;
            }

            // Verify entry references the test asset
            string expectedGuid = AssetDatabase.AssetPathToGUID(TestAssetPath);
            if (entry.guid != expectedGuid)
            {
                LogError($"  Verification: Entry GUID mismatch (expected '{expectedGuid}', got '{entry.guid}')");
                allGood = false;
            }

            // Verify group is bound to Remote profile variables (not Local)
            if (bundleSchema != null)
            {
                string buildPathVar = bundleSchema.BuildPath.GetName(settings);
                string loadPathVar = bundleSchema.LoadPath.GetName(settings);

                if (!buildPathVar.StartsWith("Remote."))
                {
                    LogError($"  Verification: BuildPath not bound to Remote variable (got '{buildPathVar}')");
                    allGood = false;
                }

                if (!loadPathVar.StartsWith("Remote."))
                {
                    LogError($"  Verification: LoadPath not bound to Remote variable (got '{loadPathVar}')");
                    allGood = false;
                }
            }

            return allGood;
        }

        #region Test Asset Definition

        /// <summary>
        /// Minimal test ScriptableObject for CDN testing.
        /// </summary>
        private class TestAssetData : ScriptableObject
        {
            [SerializeField]
            public string Description = "";
        }

        #endregion

        #region Helpers

        private static void Log(string message)
        {
            Debug.Log($"[CdnTestContentCLI] {message}");
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning($"[CdnTestContentCLI] {message}");
        }

        private static void LogError(string message)
        {
            Debug.LogError($"[CdnTestContentCLI] {message}");
        }

        #endregion
    }
}
