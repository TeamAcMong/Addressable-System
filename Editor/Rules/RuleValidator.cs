using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Editor.Rules
{
    /// <summary>
    /// Validates layout rules for common issues
    /// </summary>
    public static class RuleValidator
    {
        public enum ValidationSeverity
        {
            Info,
            Warning,
            Error
        }

        public class ValidationMessage
        {
            public ValidationSeverity Severity;
            public string Message;
            public string RuleName;
            public string Suggestion;

            public ValidationMessage(ValidationSeverity severity, string message, string ruleName = "", string suggestion = "")
            {
                Severity = severity;
                Message = message;
                RuleName = ruleName;
                Suggestion = suggestion;
            }
        }

        /// <summary>
        /// Validate a LayoutRuleData asset
        /// </summary>
        public static List<ValidationMessage> Validate(LayoutRuleData ruleData)
        {
            var messages = new List<ValidationMessage>();

            if (ruleData == null)
            {
                messages.Add(new ValidationMessage(
                    ValidationSeverity.Error,
                    "LayoutRuleData is null"));
                return messages;
            }

            // Validate address rules
            ValidateAddressRules(ruleData, messages);

            // Validate label rules
            ValidateLabelRules(ruleData, messages);

            // Validate version rules
            ValidateVersionRules(ruleData, messages);

            // Check for overall issues
            if (ruleData.TotalRuleCount == 0)
            {
                messages.Add(new ValidationMessage(
                    ValidationSeverity.Warning,
                    "No rules defined in this LayoutRuleData",
                    suggestion: "Add at least one address, label, or version rule"));
            }

            return messages;
        }

        private static void ValidateAddressRules(LayoutRuleData ruleData, List<ValidationMessage> messages)
        {
            if (ruleData.AddressRules == null)
                return;

            var enabledRules = ruleData.AddressRules.Where(r => r != null && r.Enabled).ToList();

            // Check for duplicate rule names
            var duplicateNames = enabledRules
                .GroupBy(r => r.RuleName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                messages.Add(new ValidationMessage(
                    ValidationSeverity.Warning,
                    $"Multiple address rules have the same name: '{name}'",
                    name,
                    "Use unique names for easier debugging"));
            }

            // Validate each rule
            foreach (var rule in ruleData.AddressRules)
            {
                if (rule == null)
                {
                    messages.Add(new ValidationMessage(
                        ValidationSeverity.Error,
                        "Address rule is null"));
                    continue;
                }

                var (isValid, errors) = rule.Validate();
                if (!isValid)
                {
                    foreach (var error in errors)
                    {
                        messages.Add(new ValidationMessage(
                            ValidationSeverity.Error,
                            error,
                            rule.RuleName));
                    }
                }

                // Check for rules with same priority
                var samePriority = enabledRules
                    .Where(r => r != rule && r.Priority == rule.Priority)
                    .ToList();

                if (samePriority.Count > 0)
                {
                    messages.Add(new ValidationMessage(
                        ValidationSeverity.Info,
                        $"Rule '{rule.RuleName}' has the same priority ({rule.Priority}) as {samePriority.Count} other rule(s)",
                        rule.RuleName,
                        "Consider using different priorities for deterministic rule application"));
                }
            }
        }

        private static void ValidateLabelRules(LayoutRuleData ruleData, List<ValidationMessage> messages)
        {
            if (ruleData.LabelRules == null)
                return;

            // Check for duplicate names
            var duplicateNames = ruleData.LabelRules
                .Where(r => r != null && r.Enabled)
                .GroupBy(r => r.RuleName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                messages.Add(new ValidationMessage(
                    ValidationSeverity.Warning,
                    $"Multiple label rules have the same name: '{name}'",
                    name));
            }

            // Validate each rule
            foreach (var rule in ruleData.LabelRules)
            {
                if (rule == null)
                {
                    messages.Add(new ValidationMessage(
                        ValidationSeverity.Error,
                        "Label rule is null"));
                    continue;
                }

                var (isValid, errors) = rule.Validate();
                if (!isValid)
                {
                    foreach (var error in errors)
                    {
                        messages.Add(new ValidationMessage(
                            ValidationSeverity.Error,
                            error,
                            rule.RuleName));
                    }
                }
            }
        }

        private static void ValidateVersionRules(LayoutRuleData ruleData, List<ValidationMessage> messages)
        {
            if (ruleData.VersionRules == null)
                return;

            // Check for duplicate names
            var duplicateNames = ruleData.VersionRules
                .Where(r => r != null && r.Enabled)
                .GroupBy(r => r.RuleName)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);

            foreach (var name in duplicateNames)
            {
                messages.Add(new ValidationMessage(
                    ValidationSeverity.Warning,
                    $"Multiple version rules have the same name: '{name}'",
                    name));
            }

            // Validate each rule
            foreach (var rule in ruleData.VersionRules)
            {
                if (rule == null)
                {
                    messages.Add(new ValidationMessage(
                        ValidationSeverity.Error,
                        "Version rule is null"));
                    continue;
                }

                var (isValid, errors) = rule.Validate();
                if (!isValid)
                {
                    foreach (var error in errors)
                    {
                        messages.Add(new ValidationMessage(
                            ValidationSeverity.Error,
                            error,
                            rule.RuleName));
                    }
                }
            }
        }

        /// <summary>
        /// Get a summary string of validation results
        /// </summary>
        public static string GetValidationSummary(List<ValidationMessage> messages)
        {
            if (messages.Count == 0)
                return "✓ No issues found";

            int errors = messages.Count(m => m.Severity == ValidationSeverity.Error);
            int warnings = messages.Count(m => m.Severity == ValidationSeverity.Warning);
            int infos = messages.Count(m => m.Severity == ValidationSeverity.Info);

            return $"{errors} error(s), {warnings} warning(s), {infos} info message(s)";
        }
    }
}
