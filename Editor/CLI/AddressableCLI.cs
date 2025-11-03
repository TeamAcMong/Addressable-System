using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.CLI
{
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
        /// Set version expression for filtering (placeholder for Phase 4)
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.SetVersionExpression -layoutRuleAssetPath "Assets/Rules/Main.asset" -versionExpression "[1.0.0,2.0.0)"
        /// </summary>
        public static void SetVersionExpression()
        {
            var args = ParseCommandLineArgs();

            string layoutRuleAssetPath = GetArg(args, "layoutRuleAssetPath", "");
            string versionExpression = GetArg(args, "versionExpression", "");

            Log($"SetVersionExpression called (Phase 4 - Coming Soon)");
            Log($"  LayoutRuleData: {layoutRuleAssetPath}");
            Log($"  Version Expression: {versionExpression}");

            // Will be implemented in Phase 4
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Detect conflicts in addressable layout
        /// Usage: Unity -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.DetectConflicts -reportFilePath "conflicts.json"
        /// </summary>
        public static void DetectConflicts()
        {
            var args = ParseCommandLineArgs();
            string reportFilePath = GetArg(args, "reportFilePath", "conflicts.json");

            try
            {
                var conflicts = RuleConflictDetector.DetectConflicts();

                var report = new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    totalConflicts = conflicts.Count,
                    conflicts = conflicts.Select(c => new
                    {
                        type = c.Type.ToString(),
                        message = c.Message,
                        affectedAssets = c.AffectedAssets,
                        suggestion = c.Suggestion
                    })
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

        #region Helpers

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
            var resultData = new
            {
                success = result.Success,
                totalAssetsProcessed = result.TotalAssetsProcessed,
                addressesApplied = result.AddressesApplied,
                labelsApplied = result.LabelsApplied,
                versionsApplied = result.VersionsApplied,
                warnings = result.Warnings,
                errors = result.Errors,
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
