using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using AddressableManager.Editor.Cdn;
using AddressableManager.Editor.Tools;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// CLI entry point for Phase 0 project setup: ensure CDN profiles exist, activate a profile,
    /// apply fixable settings contract rules, repair groups missing schemas, and save assets.
    /// This is a load-bearing step - it must run before any subsequent phases.
    /// </summary>
    /// <remarks>
    /// Ordering is critical:
    /// 1. CdnProfileManager.EnsureProfilesExist() must run FIRST because it creates the dedicated
    ///    catalog profile variables (Remote.CatalogBuildPath, Remote.CatalogLoadPath) that
    ///    SettingsContract's rules need to bind to.
    /// 2. Then apply all fixable rules from SettingsContract.
    /// 3. Then repair groups missing schemas.
    /// 4. Then save assets so the changes persist.
    /// </remarks>
    public static class CdnSetupCLI
    {
        /// <summary>
        /// Apply Phase 0 project setup: profiles, settings contract rules, schema repair, and asset save.
        ///
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.Cdn.CdnSetupCLI.ApplyPhaseZeroSetup -profile Local
        ///
        /// Exit codes:
        ///   0 = success, all rules pass
        ///   1 = some rule still fails (either a fixable rule that failed to fix, or manual-only rules that cannot be auto-fixed)
        ///   2 = exception during execution (logged with stack trace)
        /// </summary>
        public static void ApplyPhaseZeroSetup()
        {
            var args = ParseCommandLineArgs();
            string profileName = GetArg(args, "profile", "Local");

            try
            {
                Log("CDN Setup CLI - Phase 0 Project Setup");
                Log($"Target profile: {profileName}");
                Log("");

                // ========== BEFORE STATE ==========
                Log("=== BEFORE STATE ===");
                var beforeRules = SettingsContract.BuildRules();
                ReportRules(beforeRules);
                Log("");

                // ========== STEP 1: PROFILE MANAGER - MUST RUN FIRST ==========
                Log("Step 1: Ensuring CDN profiles and catalog variables exist...");
                CdnProfileManager.EnsureProfilesExist();
                Log("✓ Profiles and catalog variables ensured");
                Log("");

                // ========== STEP 2: ACTIVATE PROFILE ==========
                Log($"Step 2: Activating profile '{profileName}'...");
                CdnProfileManager.SetActiveProfile(profileName);
                Log($"✓ Profile '{profileName}' activated");
                Log("");

                // ========== STEP 3: APPLY FIXABLE RULES ==========
                Log("Step 3: Applying fixable settings contract rules...");
                var allRules = SettingsContract.BuildRules();
                int fixedCount = 0;

                foreach (var rule in allRules)
                {
                    if (!rule.IsSatisfied() && rule.CanAutoFix)
                    {
                        Log($"  Fixing: {rule.Id}");
                        rule.Fix();
                        fixedCount++;
                    }
                }

                Log($"✓ Applied {fixedCount} fix(es)");
                Log("");

                // ========== STEP 4: REPAIR MISSING SCHEMAS ==========
                Log("Step 4: Repairing groups with missing BundledAssetGroupSchema...");
                try
                {
                    AddressableGroupSchemaRepair.RepairGroupsMissingSchemas();
                    Log("✓ Group schema repair completed");
                }
                catch (Exception schemaEx)
                {
                    LogWarning($"Schema repair raised an exception: {schemaEx.Message}");
                    LogWarning("Continuing with setup (schema repair may have partially succeeded)");
                }
                Log("");

                // ========== STEP 5: SAVE ASSETS ==========
                Log("Step 5: Persisting asset changes to disk...");
                AssetDatabase.SaveAssets();
                Log("✓ Assets saved");
                Log("");

                // ========== AFTER STATE ==========
                Log("=== AFTER STATE ===");
                var afterRules = SettingsContract.BuildRules();
                ReportRules(afterRules);
                Log("");

                // ========== FINAL VERDICT ==========
                var failingRules = afterRules.Where(r => !r.IsSatisfied()).ToList();

                if (failingRules.Count == 0)
                {
                    Log("✓ SUCCESS: All rules pass");
                    EditorApplication.Exit(0);
                    return;
                }

                // Distinguish fixable failures (bugs) from manual-only failures (expected)
                var fixableStillFailing = failingRules.Where(r => r.CanAutoFix).ToList();
                var manualOnlyFailing = failingRules.Where(r => !r.CanAutoFix && !r.IsWarningOnly).ToList();
                var warningsOnly = failingRules.Where(r => r.IsWarningOnly).ToList();

                if (fixableStillFailing.Count > 0)
                {
                    LogError($"FAILURE: {fixableStillFailing.Count} fixable rule(s) still failing after auto-fix attempt (bug in fix logic):");
                    foreach (var rule in fixableStillFailing)
                    {
                        LogError($"  - {rule.Id}: {rule.Description}");
                        LogError($"    Expected: {rule.ExpectedDisplay}, Found: {rule.ReadCurrent()}");
                    }
                }

                if (manualOnlyFailing.Count > 0)
                {
                    LogWarning($"MANUAL REQUIRED: {manualOnlyFailing.Count} manual rule(s) require human intervention:");
                    foreach (var rule in manualOnlyFailing)
                    {
                        LogWarning($"  - {rule.Id}: {rule.Description}");
                        LogWarning($"    Expected: {rule.ExpectedDisplay}, Found: {rule.ReadCurrent()}");
                    }
                }

                if (warningsOnly.Count > 0)
                {
                    Log($"Note: {warningsOnly.Count} warning(s) are present (expected):");
                    foreach (var rule in warningsOnly)
                    {
                        Log($"  - {rule.Id}: {rule.Description}");
                    }
                }

                EditorApplication.Exit(1);
            }
            catch (Exception ex)
            {
                LogError($"Exception during Phase 0 setup: {ex.Message}");
                LogError($"Stack trace: {ex.StackTrace}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Format a point-in-time snapshot of all rules: their current values, expected values, and pass/fail.
        /// </summary>
        private static void ReportRules(IEnumerable<SettingsRule> rules)
        {
            var evaluations = rules.Select(r => r.Evaluate()).ToList();
            int passed = evaluations.Count(e => e.Passed);
            int failed = evaluations.Count(e => !e.Passed && !e.Rule.IsWarningOnly);
            int warnings = evaluations.Count(e => !e.Passed && e.Rule.IsWarningOnly);

            Log($"Summary: {passed} passed, {failed} failed, {warnings} warning(s) of {evaluations.Count} rules");
            Log("");

            foreach (var e in evaluations)
            {
                if (e.Passed)
                {
                    Log($"  ✓ {e.Rule.Id} = {e.CurrentDisplay}");
                }
                else
                {
                    string tag = e.Rule.IsWarningOnly ? "⚠" : "✖";
                    string fixNote = e.Rule.CanAutoFix ? string.Empty : " [manual]";
                    Log($"  {tag} {e.Rule.Id}");
                    Log($"      Expected: {e.Rule.ExpectedDisplay}");
                    Log($"      Found:    {e.CurrentDisplay}{fixNote}");
                }
            }
        }

        #region Helpers

        /// <summary>
        /// Parse command-line arguments in Unity's standard format: -key value -key2 value2
        /// </summary>
        private static Dictionary<string, string> ParseCommandLineArgs()
        {
            var args = new Dictionary<string, string>();
            var cmdArgs = Environment.GetCommandLineArgs();

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].StartsWith("-") && i + 1 < cmdArgs.Length)
                {
                    string key = cmdArgs[i].TrimStart('-');
                    string value = cmdArgs[i + 1];
                    args[key] = value;
                }
            }

            return args;
        }

        /// <summary>
        /// Retrieve a string argument, or return the default value if not present.
        /// </summary>
        private static string GetArg(Dictionary<string, string> args, string key, string defaultValue)
        {
            return args.ContainsKey(key) ? args[key] : defaultValue;
        }

        /// <summary>
        /// Log an informational message prefixed with the class name.
        /// </summary>
        private static void Log(string message)
        {
            Debug.Log($"[CdnSetupCLI] {message}");
        }

        /// <summary>
        /// Log a warning message prefixed with the class name.
        /// </summary>
        private static void LogWarning(string message)
        {
            Debug.LogWarning($"[CdnSetupCLI] {message}");
        }

        /// <summary>
        /// Log an error message prefixed with the class name.
        /// </summary>
        private static void LogError(string message)
        {
            Debug.LogError($"[CdnSetupCLI] {message}");
        }

        #endregion
    }
}
