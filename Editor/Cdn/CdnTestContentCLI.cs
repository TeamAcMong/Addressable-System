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

        /// <summary>
        /// Batchmode entry point. Runs the work, then ends the Editor with its exit code.
        /// </summary>
        /// <remarks>
        /// The exit lives here and NOT in the work, so the same work can be run from a button. It
        /// used to live inside: pressing "Create test asset" on the Local Server screen closed Unity,
        /// and read as a crash because nothing in the Editor announces its own exit.
        ///
        /// The guard is belt as well as braces. Even reached from a live session by an automation
        /// bridge or a stray -executeMethod, this refuses rather than taking the window down.
        /// </remarks>
        public static void CreateRemoteTestContent()
        {
            int code = CreateRemoteTestContentCore();

            if (!Application.isBatchMode)
            {
                Log($"Finished with code {code}. Not exiting - this is not batchmode.");
                return;
            }

            EditorApplication.Exit(code);
        }

        /// <summary>Batchmode entry point for the corpus. See <see cref="CreateRemoteTestContent"/>.</summary>
        public static void GenerateTestCorpus()
        {
            int code = GenerateTestCorpusCore();

            if (!Application.isBatchMode)
            {
                Log($"Finished with code {code}. Not exiting - this is not batchmode.");
                return;
            }

            EditorApplication.Exit(code);
        }

        internal static int CreateRemoteTestContentCore()

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
                    return 2;
                }

                Log("Step 1: Creating test content...");
                var testAsset = CreateOrGetTestAsset();
                if (testAsset == null)
                {
                    LogError("Failed to create or retrieve test asset");
                    return 2;
                }
                Log($"✓ Test asset ready at {TestAssetPath}");
                Log("");

                Log("Step 2: Creating Remote Test group...");
                var group = CreateOrGetRemoteTestGroup(settings);
                if (group == null)
                {
                    LogError("Failed to create or retrieve Remote Test group");
                    return 2;
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
                    return 2;
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
                    return 1;
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

                return 0;
            }
            catch (Exception ex)
            {
                LogError($"Exception during test content creation: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                return 2;
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

            // The corpus groups must be NON-static, set explicitly rather than left to the schema
            // default. StaticContent = true means "these bundles are not rebuilt by a content
            // update"; changing an asset in such a group is a restriction violation, which
            // CdnBuildCLI.BuildContentUpdate treats as a hard failure per Phase 1 §1.4. With every
            // corpus group static there is no reachable state: change something and the build is
            // refused, change nothing and there is no delta to measure. The corpus exists to be
            // changed, so it is dynamic.
            //
            // This is a TEST corpus, not a model of shipping configuration. Real shipped content is
            // normally static, and a change there is resolved by moving the modified entries into a
            // fresh group via ContentUpdateScript.CreateContentUpdateGroup (ContentUpdateScript.cs:1100)
            // — the "Prepare for Content Update" step — not by rebuilding the player.
            var contentUpdateSchema = group.GetSchema<ContentUpdateGroupSchema>();
            if (contentUpdateSchema == null)
            {
                LogWarning("  ContentUpdateGroupSchema not found on group - cannot set StaticContent");
            }
            else
            {
                contentUpdateSchema.StaticContent = false;
                EditorUtility.SetDirty(contentUpdateSchema);
            }

            Log($"  Configured bundle paths: BuildPath=[Remote.BuildPath], LoadPath=[Remote.LoadPath], StaticContent=false");
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
        internal static int GenerateTestCorpusCore()
        {
            try
            {
                Log("CDN Test Corpus Generation - Phase 1 Task 1.12");
                Log("");

                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    LogError("No AddressableAssetSettings found. Run CdnSetupCLI first.");
                    return 2;
                }

                Log("Step 1: Creating the shared payload texture (~2 MB, incompressible)...");
                var sharedTexture = CreateOrGetSharedTexture();
                if (sharedTexture == null)
                {
                    LogError("Failed to create or retrieve the shared texture");
                    return 2;
                }
                Log($"✓ Shared texture ready at {GetSharedTexturePath()}");
                Log("");

                var groups = new Dictionary<string, AddressableAssetGroup>();
                var groupAssets = new Dictionary<string, List<(string address, UnityEngine.Object asset)>>();

                Log("Step 2: Creating remote groups and their assets...");

                // The shared payload is an entry in its OWN group. Groups A and B do not contain
                // it; they contain materials that REFERENCE it. That is the only way one asset can
                // be shared by two groups in Addressables — an AddressableAssetEntry belongs to
                // exactly one group, so adding the same asset to A and then B silently MOVES it out
                // of A. The previous version of this generator did exactly that and produced a
                // corpus with no shared dependency at all.
                string groupSharedName = "Remote Corpus Shared";
                var groupShared = CreateOrGetGroup(groupSharedName, settings);
                groups[groupSharedName] = groupShared;
                groupAssets[groupSharedName] = new List<(string, UnityEngine.Object)>
                {
                    ("corpus/shared-texture", sharedTexture)
                };
                Log($"✓ {groupSharedName}: shared-texture (the ~2 MB payload)");

                // Group A: a material referencing the shared texture, plus an independent asset
                string groupAName = "Remote Assets Group A";
                var groupA = CreateOrGetGroup(groupAName, settings);
                groups[groupAName] = groupA;
                var materialA = CreateOrGetMaterial("A", sharedTexture);
                var assetA = CreateOrGetIndependentAsset("A", groupAName);
                if (materialA == null || assetA == null)
                {
                    LogError($"Failed to create assets for {groupAName}");
                    return 2;
                }
                groupAssets[groupAName] = new List<(string, UnityEngine.Object)>
                {
                    ("corpus/mat-a", materialA),
                    ("corpus/asset-a", assetA)
                };
                Log($"✓ {groupAName}: mat-a (→ shared-texture) + independent-A");

                // Group B: same shape as A, so a shared-texture change must hit both A and B
                string groupBName = "Remote Assets Group B";
                var groupB = CreateOrGetGroup(groupBName, settings);
                groups[groupBName] = groupB;
                var materialB = CreateOrGetMaterial("B", sharedTexture);
                var assetB = CreateOrGetIndependentAsset("B", groupBName);
                if (materialB == null || assetB == null)
                {
                    LogError($"Failed to create assets for {groupBName}");
                    return 2;
                }
                groupAssets[groupBName] = new List<(string, UnityEngine.Object)>
                {
                    ("corpus/mat-b", materialB),
                    ("corpus/asset-b", assetB)
                };
                Log($"✓ {groupBName}: mat-b (→ shared-texture) + independent-B");

                // Group C: the control. Nothing here depends on the shared texture, so its bundle
                // must stay byte-identical when the shared texture changes.
                string groupCName = "Remote Assets Group C";
                var groupC = CreateOrGetGroup(groupCName, settings);
                groups[groupCName] = groupC;
                var assetC = CreateOrGetIndependentAsset("C", groupCName);
                if (assetC == null)
                {
                    LogError($"Failed to create assets for {groupCName}");
                    return 2;
                }
                groupAssets[groupCName] = new List<(string, UnityEngine.Object)>
                {
                    ("corpus/asset-c", assetC)
                };
                Log($"✓ {groupCName}: independent-C only (control group)");
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
                            return 2;
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
                    return 1;
                }
                Log("✓ All type-safety checks passed");
                Log("");

                Log("=== SUCCESS ===");
                Log("Test corpus generated successfully");
                Log("");
                Log("Corpus topology (all groups Remote + StaticContent=false):");
                Log($"  Remote Corpus Shared   : corpus/shared-texture  ({SharedTextureSize}x{SharedTextureSize} RGBA32 uncompressed, ~2 MB, incompressible)");
                Log("  Remote Assets Group A  : corpus/mat-a  → shared-texture, corpus/asset-a");
                Log("  Remote Assets Group B  : corpus/mat-b  → shared-texture, corpus/asset-b");
                Log("  Remote Assets Group C  : corpus/asset-c  (control: no dependency on shared)");
                Log("");
                Log("The shared payload is an entry of its OWN group and a DEPENDENCY of A and B.");
                Log("An AddressableAssetEntry belongs to exactly one group, so a shared asset cannot");
                Log("be an entry of two groups — adding it twice moves it, it does not duplicate.");
                Log("");
                Log("Phase 1 exit criterion — how to measure it:");
                Log("  1. Full build, snapshot ServerData/<platform>/bundles");
                Log("  2. Change corpus/shared-texture (any pixel), run BuildContentUpdate");
                Log("  3. Expect: the shared group's bundle changes; group C's bundle byte-identical");
                Log("  4. Measure changed+new bundle bytes (what a player re-downloads, not size deltas)");
                Log($"     against the target: a ~2 MB change should stay under 2.5 MB");
                Log("");

                return 0;
            }
            catch (Exception ex)
            {
                LogError($"Exception during corpus generation: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                return 2;
            }
        }

        /// <summary>
        /// Side length of the shared payload texture. 720 x 720 x RGBA32 = 2,073,600 bytes,
        /// just under 2 MB, which is the size the Phase 1 exit criterion is written against
        /// ("a 2 MB asset change produces <= 2.5 MB of changed bundles").
        /// </summary>
        private const int SharedTextureSize = 720;

        /// <summary>
        /// Create or retrieve the shared payload texture: ~2 MB that survives into the bundle
        /// at full size.
        /// </summary>
        /// <remarks>
        /// TWO PROPERTIES MATTER HERE, AND THEY PULL AGAINST EACH OTHER.
        ///
        /// Deterministic — the same bytes on every machine and every run, so a rebuild with no
        /// source change produces byte-identical bundles and the diff harness can tell "rebuilt"
        /// from "actually changed".
        ///
        /// Incompressible — the criterion is measured in BUNDLE bytes, not source bytes. The
        /// previous shared asset was 2.5 MB of a repeated lorem-ipsum string, which LZ4 crushed
        /// into a 13.9 KB bundle: a ~180x ratio that made a 2 MB delta impossible to produce.
        /// Pseudo-random bytes do not compress, so the payload keeps its size on the wire.
        ///
        /// The texture importer is forced to Uncompressed with no mipmaps, because DXT/BC would
        /// otherwise shrink RGBA32 by 4x and reintroduce the same problem in a different layer.
        ///
        /// Idempotent: returns the existing asset if present.
        /// </remarks>
        private static Texture2D CreateOrGetSharedTexture()
        {
            string path = GetSharedTexturePath();

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
            {
                Log($"  Shared texture already exists at {path}");
                return existing;
            }

            string dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            int byteCount = SharedTextureSize * SharedTextureSize * 4;
            byte[] pixels = GenerateIncompressibleBytes(byteCount);

            var texture = new Texture2D(SharedTextureSize, SharedTextureSize, TextureFormat.RGBA32, false);
            try
            {
                texture.LoadRawTextureData(pixels);
                texture.Apply();
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            // Keep the payload at full size in the bundle. Without this the importer applies
            // platform texture compression and the ~2 MB becomes ~0.5 MB.
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = 8192;
                importer.SaveAndReimport();
            }
            else
            {
                LogError($"  Error: no TextureImporter for {path}; the payload may be compressed in the bundle");
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (asset == null)
            {
                LogError($"  Error: failed to load Texture2D after creation at {path}");
                return null;
            }

            long onDisk = new System.IO.FileInfo(path).Length;
            Log($"  Created shared texture at {path} " +
                $"({SharedTextureSize}x{SharedTextureSize} RGBA32, {byteCount:N0} B raw, {onDisk:N0} B as PNG)");
            return asset;
        }

        /// <summary>
        /// Create or retrieve a material that references the shared texture.
        /// This is what makes the shared payload a real cross-group dependency.
        /// </summary>
        private static Material CreateOrGetMaterial(string suffix, Texture2D sharedTexture)
        {
            string path = $"Assets/Examples/CdnTest/corpus-mat-{suffix.ToLower()}.mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                return existing;
            }

            // Unlit/Texture is a built-in always-present shader with a _MainTex slot. The corpus
            // is never rendered — only the asset reference matters — so shader choice is
            // irrelevant beyond having a texture property.
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                LogError("  Error: neither Unlit/Texture nor Sprites/Default could be found");
                return null;
            }

            var material = new Material(shader) { mainTexture = sharedTexture };
            AssetDatabase.CreateAsset(material, path);

            var asset = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (asset == null)
            {
                LogError($"  Error: failed to load Material after creation at {path}");
                return null;
            }

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
                                                                         UnityEngine.Object asset,
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
            }

            // ---- Topology, checked against the settings rather than against our own bookkeeping ----
            //
            // The previous version counted entries in a dictionary keyed by "group:address", so the
            // shared asset appeared under both "Group A:corpus/shared" and "Group B:corpus/shared"
            // and the count came to 5. On disk there were only 4 entries: an AddressableAssetEntry
            // lives in exactly one group, so adding the shared asset to B had MOVED it out of A.
            // The bookkeeping hid the very defect the check existed to catch. These assertions ask
            // AddressableAssetSettings where each entry actually is.

            if (groups.Count < 4)
            {
                LogError($"  Verification: Expected at least 4 groups (Shared, A, B, C), found {groups.Count}");
                allGood = false;
            }

            var expectedPlacement = new Dictionary<string, string>
            {
                { "corpus/shared-texture", "Remote Corpus Shared" },
                { "corpus/mat-a", "Remote Assets Group A" },
                { "corpus/asset-a", "Remote Assets Group A" },
                { "corpus/mat-b", "Remote Assets Group B" },
                { "corpus/asset-b", "Remote Assets Group B" },
                { "corpus/asset-c", "Remote Assets Group C" }
            };

            foreach (var (address, expectedGroup) in expectedPlacement)
            {
                AddressableAssetEntry found = null;
                foreach (var group in settings.groups)
                {
                    if (group == null) continue;
                    foreach (var entry in group.entries)
                    {
                        if (entry != null && entry.address == address)
                        {
                            found = entry;
                            break;
                        }
                    }
                    if (found != null) break;
                }

                if (found == null)
                {
                    LogError($"  Verification: no entry addressed '{address}' exists in any group");
                    allGood = false;
                }
                else if (found.parentGroup == null || found.parentGroup.Name != expectedGroup)
                {
                    LogError($"  Verification: '{address}' is in group '{found.parentGroup?.Name ?? "<none>"}', " +
                             $"expected '{expectedGroup}'");
                    allGood = false;
                }
            }

            // The point of the corpus: the shared texture must be a real dependency of the two
            // materials. If this breaks, changing the texture stops affecting A and B and the
            // shared-asset scenario silently degrades into three unrelated groups.
            if (!VerifyMaterialReferencesSharedTexture("corpus-mat-a"))
            {
                allGood = false;
            }

            if (!VerifyMaterialReferencesSharedTexture("corpus-mat-b"))
            {
                allGood = false;
            }

            return allGood;
        }

        /// <summary>
        /// Assert that a corpus material actually depends on the shared texture, as Unity records
        /// dependencies — not merely that we set the property a moment ago.
        /// </summary>
        private static bool VerifyMaterialReferencesSharedTexture(string materialFileName)
        {
            string materialPath = $"Assets/Examples/CdnTest/{materialFileName}.mat";
            string texturePath = GetSharedTexturePath();

            if (!System.IO.File.Exists(materialPath))
            {
                LogError($"  Verification: material not found at {materialPath}");
                return false;
            }

            string[] dependencies = AssetDatabase.GetDependencies(materialPath, true);
            foreach (string dependency in dependencies)
            {
                if (dependency == texturePath)
                {
                    return true;
                }
            }

            LogError($"  Verification: {materialPath} does not depend on {texturePath}. " +
                     "The shared-dependency topology is not in place, so a texture change would not " +
                     "propagate to this group's bundle.");
            return false;
        }

        /// <summary>
        /// Generate <paramref name="byteCount"/> bytes that are deterministic across machines
        /// and runs, but do not compress.
        /// </summary>
        /// <remarks>
        /// Hand-rolled xorshift32 rather than System.Random on purpose: System.Random's algorithm
        /// is an implementation detail and has changed between .NET Framework and .NET Core, so
        /// the same seed is not guaranteed to give the same stream on a different runtime. This
        /// corpus is compared byte-for-byte across builds and potentially across machines, so the
        /// generator has to be pinned, not merely seeded.
        ///
        /// The output is high-entropy, so LZ4 (Addressables' default bundle compression) leaves it
        /// essentially unchanged — which is the whole point; see CreateOrGetSharedTexture.
        /// </remarks>
        private static byte[] GenerateIncompressibleBytes(int byteCount)
        {
            const uint seed = 0x5EED1234;
            var bytes = new byte[byteCount];
            uint state = seed;

            for (int i = 0; i < byteCount; i++)
            {
                // xorshift32 (Marsaglia). Fixed shifts, fixed seed, no library dependency.
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                bytes[i] = (byte)(state & 0xFF);
            }

            return bytes;
        }

        /// <summary>
        /// Get the standard path for the shared payload texture.
        /// </summary>
        private static string GetSharedTexturePath()
        {
            return "Assets/Examples/CdnTest/corpus-shared-texture.png";
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
