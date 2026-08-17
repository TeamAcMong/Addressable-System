using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;
using AddressableManager.Editor.Versioning;

namespace AddressableManager.Editor.CLI
{
    /// <summary>
    /// Result data for JSON serialization in CLI commands
    /// </summary>
    [Serializable]
    internal class CLIResultData
    {
        public bool success;
        public int totalAssetsProcessed;
        public int addressesApplied;
        public int labelsApplied;
        public int versionsApplied;
        public List<string> warnings = new List<string>();
        public List<string> errors = new List<string>();
        public string timestamp;
    }

    /// <summary>
    /// Result data for JSON serialization in <see cref="AddressableCLI.DetectConflicts"/>.
    /// JsonUtility cannot serialize anonymous types (no [Serializable], and it reflects fields
    /// not the properties an anonymous type actually has) — DetectConflicts used to build its
    /// report as one and pass it straight to JsonUtility.ToJson, which silently produced "{}"
    /// regardless of how many conflicts were found. This mirrors the same fix CLIResultData
    /// already applies for ApplyRules' SaveResultToFile.
    /// </summary>
    [Serializable]
    internal class ConflictReportEntry
    {
        public string type;
        public string message;
        public List<string> affectedAssets = new List<string>();
        public string suggestion;
    }

    [Serializable]
    internal class ConflictReportData
    {
        public string timestamp;
        public int totalConflicts;
        public List<ConflictReportEntry> conflicts = new List<ConflictReportEntry>();
    }

    /// <summary>
    /// CLI commands for CI/CD integration
    /// Can be called from command line with Unity batch mode
    /// </summary>
    public static class AddressableCLI
    {
        /// <summary>
        /// Apply layout rules
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules -layoutRuleAssetPath "Assets/Rules/Main.asset" -validateOnly false -warningAsError true -resultFilePath "build_log.json"
        /// </summary>
        public static void ApplyRules()
        {
            // Gate: do not report on an assembly that did not build
            if (EditorUtility.scriptCompilationFailed)
            {
                LogError("FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            var args = ParseCommandLineArgs();

            string layoutRuleAssetPath = GetArg(args, "layoutRuleAssetPath", "");
            bool validateOnly = GetArg(args, "validateOnly", false);
            bool warningAsError = GetArg(args, "warningAsError", false);
            string resultFilePath = GetArg(args, "resultFilePath", "");

            if (string.IsNullOrEmpty(layoutRuleAssetPath))
            {
                LogError("Missing required argument: -layoutRuleAssetPath");
                EditorApplication.Exit(2);
                return;
            }

            var ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(layoutRuleAssetPath);
            if (ruleData == null)
            {
                LogError($"LayoutRuleData not found at path: {layoutRuleAssetPath}");
                EditorApplication.Exit(2);
                return;
            }

            try
            {
                // Validate first
                var validationMessages = RuleValidator.Validate(ruleData);
                bool hasErrors = validationMessages.Any(m => m.Severity == RuleValidator.ValidationSeverity.Error);
                bool hasWarnings = validationMessages.Any(m => m.Severity == RuleValidator.ValidationSeverity.Warning);

                if (hasErrors || (warningAsError && hasWarnings))
                {
                    LogError("Validation failed:");
                    foreach (var msg in validationMessages)
                    {
                        if (msg.Severity == RuleValidator.ValidationSeverity.Error || warningAsError)
                        {
                            LogError($"  [{msg.Severity}] {msg.Message}");
                        }
                    }
                    EditorApplication.Exit(1);
                    return;
                }

                if (validateOnly)
                {
                    Log("Validation passed (validate-only mode)");
                    EditorApplication.Exit(0);
                    return;
                }

                // Apply rules
                var processor = new LayoutRuleProcessor(ruleData);
                var result = processor.ApplyRules();

                // Save result
                if (!string.IsNullOrEmpty(resultFilePath))
                {
                    SaveResultToFile(result, resultFilePath);
                }

                // Log summary
                Log($"Rules applied successfully:");
                Log($"  Processed: {result.TotalAssetsProcessed} assets");
                Log($"  Addresses: {result.AddressesApplied}");
                Log($"  Labels: {result.LabelsApplied}");
                Log($"  Versions: {result.VersionsApplied}");

                if (result.Errors.Count > 0)
                {
                    LogError($"  Errors: {result.Errors.Count}");
                    foreach (var error in result.Errors)
                    {
                        LogError($"    {error}");
                    }
                    EditorApplication.Exit(1);
                    return;
                }

                // Check processor warnings if -warningAsError flag is set
                if (warningAsError && result.Warnings.Count > 0)
                {
                    LogError($"  Warnings (treated as errors): {result.Warnings.Count}");
                    foreach (var warning in result.Warnings)
                    {
                        LogError($"    {warning}");
                    }
                    EditorApplication.Exit(1);
                    return;
                }

                if (result.Warnings.Count > 0)
                {
                    LogWarning($"  Warnings: {result.Warnings.Count}");
                    foreach (var warning in result.Warnings)
                    {
                        LogWarning($"    {warning}");
                    }
                }

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during rule application: {ex.Message}");
                LogError(ex.StackTrace);
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Validate layout rules without applying them
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ValidateLayoutRules -layoutRuleAssetPath "Assets/Rules/Main.asset" -errorLogFilePath "validation_errors.txt"
        /// </summary>
        public static void ValidateLayoutRules()
        {
            // Gate: do not report on an assembly that did not build
            if (EditorUtility.scriptCompilationFailed)
            {
                LogError("FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            var args = ParseCommandLineArgs();

            string layoutRuleAssetPath = GetArg(args, "layoutRuleAssetPath", "");
            string errorLogFilePath = GetArg(args, "errorLogFilePath", "");

            if (string.IsNullOrEmpty(layoutRuleAssetPath))
            {
                LogError("Missing required argument: -layoutRuleAssetPath");
                EditorApplication.Exit(2);
                return;
            }

            var ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(layoutRuleAssetPath);
            if (ruleData == null)
            {
                LogError($"LayoutRuleData not found at path: {layoutRuleAssetPath}");
                EditorApplication.Exit(2);
                return;
            }

            try
            {
                var messages = RuleValidator.Validate(ruleData);

                if (messages.Count == 0)
                {
                    Log("✓ Validation passed - no issues found");
                    EditorApplication.Exit(0);
                    return;
                }

                // Log all messages
                bool hasErrors = false;
                var errorLog = new System.Text.StringBuilder();

                foreach (var msg in messages)
                {
                    string line = $"[{msg.Severity}] {msg.Message}";
                    if (!string.IsNullOrEmpty(msg.RuleName))
                    {
                        line += $" (Rule: {msg.RuleName})";
                    }

                    if (msg.Severity == RuleValidator.ValidationSeverity.Error)
                    {
                        LogError(line);
                        hasErrors = true;
                    }
                    else
                    {
                        LogWarning(line);
                    }

                    errorLog.AppendLine(line);
                }

                // Save to file if specified
                if (!string.IsNullOrEmpty(errorLogFilePath))
                {
                    File.WriteAllText(errorLogFilePath, errorLog.ToString());
                    Log($"Error log saved to: {errorLogFilePath}");
                }

                EditorApplication.Exit(hasErrors ? 1 : 0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during validation: {ex.Message}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Set version expression for filtering
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.SetVersionExpression -layoutRuleAssetPath "Assets/Rules/Main.asset" -versionExpression "[1.0.0,2.0.0)" -excludeUnversioned true
        /// </summary>
        public static void SetVersionExpression()
        {
            // Gate: do not report on an assembly that did not build
            if (EditorUtility.scriptCompilationFailed)
            {
                LogError("FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            var args = ParseCommandLineArgs();

            string layoutRuleAssetPath = GetArg(args, "layoutRuleAssetPath", "");
            string versionExpression = GetArg(args, "versionExpression", "");
            bool excludeUnversioned = GetArg(args, "excludeUnversioned", false);

            if (string.IsNullOrEmpty(layoutRuleAssetPath))
            {
                LogError("Missing required argument: -layoutRuleAssetPath");
                EditorApplication.Exit(2);
                return;
            }

            var ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(layoutRuleAssetPath);
            if (ruleData == null)
            {
                LogError($"LayoutRuleData not found at path: {layoutRuleAssetPath}");
                EditorApplication.Exit(2);
                return;
            }

            try
            {
                // Validate version expression if provided
                if (!string.IsNullOrEmpty(versionExpression))
                {
                    if (!VersionExpression.TryParse(versionExpression, out var parsedExpression))
                    {
                        LogError($"Invalid version expression: {versionExpression}");
                        LogError("Valid formats: [1.0.0,2.0.0), (1.0.0,2.0.0], [1.0.0,2.0.0], 1.0.0, >=1.0.0, >1.0.0, <=2.0.0, <2.0.0");
                        EditorApplication.Exit(1);
                        return;
                    }
                    Log($"✓ Version expression validated: {versionExpression}");
                }

                // Update properties
                ruleData.VersionExpression = versionExpression;
                ruleData.ExcludeUnversioned = excludeUnversioned;

                // Save changes
                EditorUtility.SetDirty(ruleData);
                AssetDatabase.SaveAssets();

                Log($"✓ Version expression updated successfully");
                Log($"  LayoutRuleData: {layoutRuleAssetPath}");
                Log($"  Version Expression: {(string.IsNullOrEmpty(versionExpression) ? "(none)" : versionExpression)}");
                Log($"  Exclude Unversioned: {excludeUnversioned}");

                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                LogError($"Exception during version expression update: {ex.Message}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Detect conflicts in addressable layout
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.DetectConflicts -reportFilePath "conflicts.json"
        /// </summary>
        public static void DetectConflicts()
        {
            // Gate: do not report on an assembly that did not build
            if (EditorUtility.scriptCompilationFailed)
            {
                LogError("FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            var args = ParseCommandLineArgs();
            string reportFilePath = GetArg(args, "reportFilePath", "conflicts.json");

            try
            {
                var conflicts = RuleConflictDetector.DetectConflicts();

                var report = new ConflictReportData
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    totalConflicts = conflicts.Count,
                    conflicts = conflicts.Select(c => new ConflictReportEntry
                    {
                        type = c.Type.ToString(),
                        message = c.Message,
                        affectedAssets = c.AffectedAssets,
                        suggestion = c.Suggestion
                    }).ToList()
                };

                string json = JsonUtility.ToJson(report, true);
                File.WriteAllText(reportFilePath, json);

                if (conflicts.Count == 0)
                {
                    Log("✓ No conflicts detected");
                    EditorApplication.Exit(0);
                }
                else
                {
                    LogWarning($"⚠ Detected {conflicts.Count} conflict(s) - see {reportFilePath}");
                    EditorApplication.Exit(1);
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception during conflict detection: {ex.Message}");
                EditorApplication.Exit(2);
            }
        }

        /// <summary>
        /// Import rules from a JSON template file
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ImportRules -layoutRuleAssetPath "Assets/Rules/Main.asset" -importFilePath "Packages/com.game.addressables/Editor/Templates/VersionedAssetsRules.json" -mergeMode true
        /// </summary>
        public static void ImportRules()
        {
            // Gate: do not report on an assembly that did not build
            if (EditorUtility.scriptCompilationFailed)
            {
                LogError("FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            var args = ParseCommandLineArgs();

            string layoutRuleAssetPath = GetArg(args, "layoutRuleAssetPath", "");
            string importFilePath = GetArg(args, "importFilePath", "");
            bool mergeMode = GetArg(args, "mergeMode", false);

            if (string.IsNullOrEmpty(layoutRuleAssetPath))
            {
                LogError("Missing required argument: -layoutRuleAssetPath");
                EditorApplication.Exit(2);
                return;
            }

            if (string.IsNullOrEmpty(importFilePath))
            {
                LogError("Missing required argument: -importFilePath");
                EditorApplication.Exit(2);
                return;
            }

            if (!File.Exists(importFilePath))
            {
                LogError($"Import file not found: {importFilePath}");
                EditorApplication.Exit(2);
                return;
            }

            var ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(layoutRuleAssetPath);
            if (ruleData == null)
            {
                LogError($"LayoutRuleData not found at path: {layoutRuleAssetPath}");
                EditorApplication.Exit(2);
                return;
            }

            try
            {
                bool success = RuleSerializer.ImportFromJson(ruleData, importFilePath, mergeMode);

                if (success)
                {
                    Log($"✓ Rules imported successfully from: {importFilePath}");
                    Log($"  Target: {layoutRuleAssetPath}");
                    Log($"  Merge mode: {mergeMode}");

                    EditorUtility.SetDirty(ruleData);
                    AssetDatabase.SaveAssets();

                    EditorApplication.Exit(0);
                }
                else
                {
                    LogError($"Failed to import rules from: {importFilePath}");
                    EditorApplication.Exit(1);
                }
            }
            catch (Exception ex)
            {
                LogError($"Exception during rule import: {ex.Message}");
                EditorApplication.Exit(2);
            }
        }

        #region Helpers

        private static Dictionary<string, string> ParseCommandLineArgs()
        {
            var args = new Dictionary<string, string>();
            var cmdArgs = Environment.GetCommandLineArgs();

            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].StartsWith("-"))
                {
                    string key = cmdArgs[i].TrimStart('-');

                    // Check if next argument exists and is not another flag
                    if (i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith("-"))
                    {
                        string value = cmdArgs[i + 1];
                        args[key] = value;
                        i++; // Skip the value argument
                    }
                    else
                    {
                        // Valueless flag defaults to "true"
                        args[key] = "true";
                    }
                }
            }

            return args;
        }

        private static string GetArg(Dictionary<string, string> args, string key, string defaultValue)
        {
            return args.ContainsKey(key) ? args[key] : defaultValue;
        }

        private static bool GetArg(Dictionary<string, string> args, string key, bool defaultValue)
        {
            if (!args.ContainsKey(key)) return defaultValue;
            return args[key].ToLower() == "true" || args[key] == "1";
        }

        private static void SaveResultToFile(LayoutRuleProcessor.ProcessResult result, string filePath)
        {
            var resultData = new CLIResultData
            {
                success = result.Success,
                totalAssetsProcessed = result.TotalAssetsProcessed,
                addressesApplied = result.AddressesApplied,
                labelsApplied = result.LabelsApplied,
                versionsApplied = result.VersionsApplied,
                warnings = new List<string>(result.Warnings),
                errors = new List<string>(result.Errors),
                timestamp = DateTime.UtcNow.ToString("o")
            };

            string json = JsonUtility.ToJson(resultData, true);
            File.WriteAllText(filePath, json);
            Log($"Result saved to: {filePath}");
        }

        private static void Log(string message)
        {
            Debug.Log($"[AddressableCLI] {message}");
        }

        private static void LogWarning(string message)
        {
            Debug.LogWarning($"[AddressableCLI] {message}");
        }

        private static void LogError(string message)
        {
            Debug.LogError($"[AddressableCLI] {message}");
        }

        #endregion
    }
}
