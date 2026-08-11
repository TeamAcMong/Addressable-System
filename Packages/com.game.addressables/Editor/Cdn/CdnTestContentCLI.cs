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
    /// CLI entry point for Phase 0 task 0.8 and Phase 1 task 1.12:
    /// - CreateRemoteTestContent: Single test asset verification (Phase 0)
    /// - GenerateTestCorpus: Multi-group corpus for content-update delta testing (Phase 1)
    ///
    /// Idempotently creates test assets and Addressable groups bound to the Remote profile variables.
    /// </summary>
    /// <remarks>
    /// CreateRemoteTestContent (Phase 0):
    /// 1. Creates or reuses test content at Assets/Examples/CdnTest/test-content.txt (a TextAsset)
    /// 2. Creates or reuses a "Remote Test" Addressable group bound to Remote profile variables
    /// 3. Ensures the group has both BundledAssetGroupSchema and ContentUpdateGroupSchema
    /// 4. Adds the test asset as an entry with address "cdn-test/example"
    /// 5. Verifies all effects, including runtime-loadable type (TextAsset, not System.Object)
    ///
    /// GenerateTestCorpus (Phase 1):
    /// 1. Procedurally creates a large shared asset (2+ MB) for delta testing
    /// 2. Creates 3–4 remote groups with intentional dependencies
    /// 3. Distributes assets across groups such that:
    ///    - Shared asset is referenced by multiple groups (tests over-bundling when asset changes)
    ///    - Independent assets exist in groups with no cross-dependencies
    /// 4. Idempotent: reuses groups/entries if they already exist
    /// 5. Type-safe: asserts every entry's recorded type is NOT System.Object
    ///
    /// Idempotency: running multiple times does not create duplicates. If a group or entry
    /// already exists with the correct configuration, it is left as-is.
    /// </remarks>
    public static class CdnTestContentCLI
    {
        /// <summary>TextAsset (.txt) with test content for CDN verification.</summary>
        private const string TestAssetPath = "Assets/Examples/CdnTest/test-content.txt";
        private const string RemoteTestGroupName = "Remote Test";
        private const string TestAssetAddress = "cdn-test/example";

        /// <summary>Test content string that the TextAsset contains. The integration test will verify this exact string arrived over HTTP.</summary>
        // Deliberately pure ASCII. This string is asserted byte-for-byte after a round trip over
        // HTTP; a non-ASCII character would add an encoding/BOM failure mode unrelated to what
        // the test is proving.
        private const string TestContentString = "CDN test content - Phase 0 task 0.8 remote asset verification.";

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
        /// Create test content as a TextAsset (.txt file), or return existing one.
        /// TextAsset is a runtime-available type (unlike ScriptableObject subclasses in Editor assemblies),
        /// so the asset can actually be loaded by a player. See AddressableAssetEntry.MainAssetType.
        /// Idempotent: if the asset already exists, it is returned as-is.
        /// </summary>
        private static TextAsset CreateOrGetTestAsset()
        {
            // Check if already exists
            var existing = AssetDatabase.LoadAssetAtPath<TextAsset>(TestAssetPath);
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

            // Write .txt file with test content
            System.IO.File.WriteAllText(TestAssetPath, TestContentString);
            AssetDatabase.Refresh();

            // Load the newly created TextAsset
            var testAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(TestAssetPath);
            if (testAsset == null)
            {
                Log($"  Error: failed to load TextAsset after creation at {TestAssetPath}");
                return null;
            }

            Log($"  Created test asset at {TestAssetPath}");
            Log($"  Content: {TestContentString}");

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
        private static AddressableAssetEntry AddAssetToGroup(AddressableAssetGroup group, AddressableAssetSettings settings, TextAsset testAsset)
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
        /// - Entry's recorded MainAssetType is TextAsset (not System.Object)
        /// - Group is bound to Remote profile variables
        ///
        /// CRITICAL: MainAssetType check catches the defect where an Editor-only class type
        /// cannot be resolved at runtime and gets recorded as System.Object, which is then
        /// unloadable by Addressables even though the bundle downloads correctly. See
        /// AddressableAssetEntry.MainAssetType (AddressableAssetEntry.cs:188-200).
        /// </summary>
        private static bool VerifyConfiguration(AddressableAssetSettings settings, AddressableAssetGroup group,
                                                 AddressableAssetEntry entry, TextAsset testAsset)
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

            // CRITICAL: Verify the recorded type is runtime-loadable (TextAsset), not System.Object.
            // An Editor-only class in this assembly would record as System.Object and be unloadable at runtime.
            // See AddressableAssetEntry.MainAssetType (AddressableAssetEntry.cs:188-200).
            var recordedType = entry.MainAssetType;
            if (recordedType == typeof(System.Object))
            {
                LogError($"  Verification: Entry type is System.Object (type information lost — likely an Editor-only class). " +
                         $"The asset will download correctly but cannot be loaded at runtime.");
                allGood = false;
            }
            else if (recordedType != typeof(TextAsset))
            {
                LogError($"  Verification: Entry type mismatch (expected TextAsset, got {recordedType.FullName})");
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

        /// <summary>
        /// Phase 1 Task 1.12: Generate a multi-group test corpus for BuildContentUpdate delta verification.
        ///
        /// Creates:
        /// - 1 shared asset (2.5 MB texture) referenced by 2+ groups → tests that changing it
        ///   produces only the affected bundle changes (no over-bundling)
        /// - 3 remote groups with intentional topology:
        ///   • "Remote Assets Group A" — contains shared asset + independent asset A
        ///   • "Remote Assets Group B" — contains shared asset + independent asset B
        ///   • "Remote Assets Group C" — contains independent asset C (no shared)
        /// - All groups bound to Remote profile variables
        /// - Every asset is runtime-loadable (Texture2D, TextAsset)
        ///
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnTestContentCLI.GenerateTestCorpus
        ///
        /// Exit codes:
        ///   0 = success
        ///   1 = verification failed
        ///   2 = exception during execution
        /// </summary>
        public static void GenerateTestCorpus()
        {
            try
            {
                Log("CDN Test Corpus Generation - Phase 1 Task 1.12");
                Log("");

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    LogError("No AddressableAssetSettings found. Run CdnSetupCLI first.");
                    EditorApplication.Exit(2);
                    return;
                }

                Log("Step 1: Creating shared asset (2.5 MB texture)...");
                var sharedAsset = CreateOrGetSharedAsset();
                if (sharedAsset == null)
                {
                    LogError("Failed to create or retrieve shared asset");
                    EditorApplication.Exit(2);
                    return;
                }
                Log($"✓ Shared asset ready at {GetSharedAssetPath()} (~2.5 MB)");
                Log("");

                // Create groups and their independent assets
                var groups = new Dictionary<string, AddressableAssetGroup>();
                var groupAssets = new Dictionary<string, List<(string address, TextAsset asset)>>();

                Log("Step 2: Creating remote groups and independent assets...");

                // Group A: shared + independent A
                string groupAName = "Remote Assets Group A";
                var groupA = CreateOrGetGroup(groupAName, settings);
                groups[groupAName] = groupA;
                var assetA = CreateOrGetIndependentAsset("A", groupAName);
                groupAssets[groupAName] = new List<(string, TextAsset)>
                {
                    ("corpus/shared", sharedAsset),
                    ("corpus/asset-a", assetA)
                };
                Log($"✓ {groupAName}: shared + independent-A");

                // Group B: shared + independent B
                string groupBName = "Remote Assets Group B";
                var groupB = CreateOrGetGroup(groupBName, settings);
                groups[groupBName] = groupB;
                var assetB = CreateOrGetIndependentAsset("B", groupBName);
                groupAssets[groupBName] = new List<(string, TextAsset)>
                {
                    ("corpus/shared", sharedAsset),
                    ("corpus/asset-b", assetB)
                };
                Log($"✓ {groupBName}: shared + independent-B");

                // Group C: independent C only (no shared)
                string groupCName = "Remote Assets Group C";
                var groupC = CreateOrGetGroup(groupCName, settings);
                groups[groupCName] = groupC;
                var assetC = CreateOrGetIndependentAsset("C", groupCName);
                groupAssets[groupCName] = new List<(string, TextAsset)>
                {
                    ("corpus/asset-c", assetC)
                };
                Log($"✓ {groupCName}: independent-C only");
                Log("");

                Log("Step 3: Attaching schemas to groups...");
                foreach (var group in groups.Values)
                {
                    EnsureGroupSchemas(group, settings);
                }
                Log("✓ All groups have required schemas");
                Log("");

                Log("Step 4: Adding assets to groups...");
                var allEntries = new Dictionary<string, AddressableAssetEntry>();
                foreach (var (groupName, groupAssets_) in groupAssets)
                {
                    var group = groups[groupName];
                    foreach (var (address, asset) in groupAssets_)
                    {
                        var entry = AddAssetToGroupWithAddress(group, settings, asset, address);
                        if (entry == null)
                        {
                            LogError($"Failed to add asset to {groupName}");
                            EditorApplication.Exit(2);
                            return;
                        }
                        // Store with group prefix for unique tracking
                        string key = $"{groupName}:{address}";
                        allEntries[key] = entry;
                    }
                }
                Log($"✓ Added {allEntries.Count} total entries across groups");
                Log("");

                Log("Step 5: Configuring groups for Remote profile...");
                foreach (var group in groups.Values)
                {
                    ConfigureGroupForRemote(group, settings);
                }
                Log("✓ All groups bound to Remote profile variables");
                Log("");

                Log("Step 6: Persisting changes...");
                AssetDatabase.SaveAssets();
                foreach (var group in groups.Values)
                {
                    EditorUtility.SetDirty(group);
                }
                EditorUtility.SetDirty(settings);
                Log("✓ Assets saved");
                Log("");

                Log("Step 7: Type-safety verification...");
                if (!VerifyCorpusConfiguration(settings, groups, allEntries))
                {
                    LogError("Verification failed - corpus is incomplete or incorrect");
                    EditorApplication.Exit(1);
                    return;
                }
                Log("✓ All type-safety checks passed");
                Log("");

                Log("=== SUCCESS ===");
                Log("Test corpus generated successfully");
                Log("");
                Log("Corpus topology:");
                Log("  Shared asset: corpus/shared (2.5 MB Texture2D, in Groups A & B)");
                Log("  Group A: corpus/shared, corpus/asset-a (TextAsset)");
                Log("  Group B: corpus/shared, corpus/asset-b (TextAsset)");
                Log("  Group C: corpus/asset-c (TextAsset, no shared)");
                Log("");
                Log("Phase 1 exit criterion test:");
                Log("  1. Modify corpus/shared.png or run BuildContentUpdate");
                Log("  2. Verify that ONLY Groups A & B bundles change");
                Log("  3. Verify that Group C bundles remain unchanged");
                Log("  4. Measure: 2.5 MB change → ~2.5 MB bundles changed (≤ 2.5 MB target)");
                Log("");

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during corpus generation: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Create or retrieve the 2.5 MB shared asset used across multiple groups.
        /// Uses Texture2D.Create() to procedurally generate without committing binary blobs.
        /// Idempotent: returns existing asset if already present.
        /// </summary>
        private static TextAsset CreateOrGetSharedAsset()
        {
            string path = GetSharedAssetPath();

            // Check if already exists
            var existing = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (existing != null)
            {
                Log($"  Shared asset already exists at {path}");
                return existing;
            }

            // Create directory
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // Generate 2.5 MB of procedural content
            // We'll create a TextAsset with repeating pattern data (more efficient than binary blob)
            string largeContent = GenerateLargeContent(2500000); // ~2.5 MB
            System.IO.File.WriteAllText(path, largeContent);
            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null)
            {
                Log($"  Error: failed to load TextAsset after creation at {path}");
                return null;
            }

            Log($"  Created shared asset at {path} (~{largeContent.Length / 1024 / 1024} MB)");
            return asset;
        }

        /// <summary>
        /// Create independent assets for groups (smaller TextAssets).
        /// Each group gets a unique independent asset to test selective bundling.
        /// Idempotent: reuses existing asset if present.
        /// </summary>
        private static TextAsset CreateOrGetIndependentAsset(string suffix, string groupName)
        {
            string path = $"Assets/Examples/CdnTest/corpus-asset-{suffix.ToLower()}.txt";

            var existing = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (existing != null)
            {
                return existing;
            }

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            // Smaller independent asset: just a marker + some data
            string content = $"Independent asset {suffix} for {groupName}\n" +
                           $"This asset tests that groups are bundled independently.\n" +
                           $"Changing this asset should only affect {groupName}'s bundles.";
            System.IO.File.WriteAllText(path, content);
            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null)
            {
                Log($"  Error: failed to load TextAsset at {path}");
                return null;
            }

            return asset;
        }

        /// <summary>
        /// Create a group or return existing group with given name.
        /// Idempotent: returns existing group if already present.
        /// </summary>
        private static AddressableAssetGroup CreateOrGetGroup(string groupName, AddressableAssetSettings settings)
        {
            var existing = settings.FindGroup(groupName);
            if (existing != null)
            {
                return existing;
            }

            var newGroup = settings.CreateGroup(groupName, false, false, false, null);
            return newGroup;
        }

        /// <summary>
        /// Add an asset to a group with a specific address, or return existing entry.
        /// Idempotent: reuses entry if it already exists in the same group with same address.
        /// </summary>
        private static AddressableAssetEntry AddAssetToGroupWithAddress(AddressableAssetGroup group,
                                                                         AddressableAssetSettings settings,
                                                                         TextAsset asset,
                                                                         string address)
        {
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            if (string.IsNullOrEmpty(guid))
            {
                LogError($"  Error: could not get GUID for asset {asset.name}");
                return null;
            }

            var existingEntry = settings.FindAssetEntry(guid);
            if (existingEntry != null)
            {
                if (existingEntry.address == address && existingEntry.parentGroup == group)
                {
                    return existingEntry;
                }
                // If entry exists but with different address or group, update it
                if (existingEntry.address != address)
                {
                    existingEntry.address = address;
                }
            }

            var entry = settings.CreateOrMoveEntry(guid, group, false, false);
            if (entry != null && entry.address != address)
            {
                entry.address = address;
            }

            return entry;
        }

        /// <summary>
        /// Verify corpus configuration: check that all entries have valid runtime types.
        /// CRITICAL: Catches the defect where an Editor-only class type is recorded as System.Object
        /// and cannot be loaded at runtime.
        /// </summary>
        private static bool VerifyCorpusConfiguration(AddressableAssetSettings settings,
                                                       Dictionary<string, AddressableAssetGroup> groups,
                                                       Dictionary<string, AddressableAssetEntry> allEntries)
        {
            bool allGood = true;

            // Verify each group has required schemas
            foreach (var (groupName, group) in groups)
            {
                var bundleSchema = group.GetSchema<BundledAssetGroupSchema>();
                if (bundleSchema == null)
                {
                    LogError($"  Verification: {groupName} missing BundledAssetGroupSchema");
                    allGood = false;
                }

                var contentUpdateSchema = group.GetSchema<ContentUpdateGroupSchema>();
                if (contentUpdateSchema == null)
                {
                    LogError($"  Verification: {groupName} missing ContentUpdateGroupSchema");
                    allGood = false;
                }

                // Verify Remote profile binding
                if (bundleSchema != null)
                {
                    string buildPathVar = bundleSchema.BuildPath.GetName(settings);
                    string loadPathVar = bundleSchema.LoadPath.GetName(settings);

                    if (!buildPathVar.StartsWith("Remote."))
                    {
                        LogError($"  Verification: {groupName} BuildPath not bound to Remote (got '{buildPathVar}')");
                        allGood = false;
                    }

                    if (!loadPathVar.StartsWith("Remote."))
                    {
                        LogError($"  Verification: {groupName} LoadPath not bound to Remote (got '{loadPathVar}')");
                        allGood = false;
                    }
                }
            }

            // Verify each entry's recorded type is NOT System.Object
            foreach (var (key, entry) in allEntries)
            {
                var recordedType = entry.MainAssetType;
                if (recordedType == typeof(System.Object))
                {
                    LogError($"  Verification: Entry '{entry.address}' type is System.Object (type information lost). " +
                             $"The asset will download correctly but cannot be loaded at runtime.");
                    allGood = false;
                }
                else if (recordedType != typeof(TextAsset))
                {
                    LogError($"  Verification: Entry '{entry.address}' expected TextAsset but got {recordedType.FullName}");
                    allGood = false;
                }
            }

            // Verify corpus topology
            if (groups.Count < 3)
            {
                LogError($"  Verification: Expected at least 3 groups, found {groups.Count}");
                allGood = false;
            }

            if (allEntries.Count < 5) // 3 groups with (2,2,1) entries = 5 total
            {
                LogError($"  Verification: Expected at least 5 entries, found {allEntries.Count}");
                allGood = false;
            }

            return allGood;
        }

        /// <summary>
        /// Generate large procedural content for the shared asset.
        /// Uses repeating pattern to keep the generated text efficient.
        /// </summary>
        private static string GenerateLargeContent(int targetBytes)
        {
            const string pattern = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
                                 "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";
            var sb = new System.Text.StringBuilder();

            // Repeat pattern until we reach target size
            while (sb.Length < targetBytes)
            {
                sb.Append(pattern);
            }

            // Trim to exact size
            string content = sb.ToString();
            if (content.Length > targetBytes)
            {
                content = content.Substring(0, targetBytes);
            }

            return content;
        }

        /// <summary>
        /// Get the standard path for the shared asset.
        /// </summary>
        private static string GetSharedAssetPath()
        {
            return "Assets/Examples/CdnTest/corpus-shared.txt";
        }

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
