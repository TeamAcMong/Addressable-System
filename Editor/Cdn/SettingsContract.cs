using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// One rule from design doc §9 ("Addressables Configuration Contract"), reduced to data: a stable
    /// id, a human description, a function that reads the live value, the value it is expected to hold,
    /// a predicate that decides pass/fail, and an optional automatic fix.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadCurrent"/> and <see cref="IsSatisfied"/> are delegates, not baked-in values -
    /// every call re-reads the live project state. That is what lets a caller revert a setting by hand
    /// and immediately see the rule fail again on the next evaluation, with no need to reconstruct the
    /// rule list (Phase 0 exit criterion for task 0.9).
    ///
    /// <see cref="Fix"/> is null for rules with no safe automatic fix. Callers (the validator UI; never
    /// CI) must treat a null Fix as "render as manual" and must not invent a fix themselves.
    /// </remarks>
    public sealed class SettingsRule
    {
        /// <summary>Stable identifier, e.g. "settings.BundleTimeout" or "group:Scene:BundleNaming". Safe to log or diff across runs.</summary>
        public string Id { get; }

        /// <summary>Human-readable explanation of what this checks and why, suitable for a tooltip or a CI log line.</summary>
        public string Description { get; }

        /// <summary>True for a per-group rule (see <see cref="GroupName"/>); false for a project-level (AddressableAssetSettings) rule.</summary>
        public bool IsGroupScoped { get; }

        /// <summary>The owning group's display name when <see cref="IsGroupScoped"/> is true; otherwise null.</summary>
        public string GroupName { get; }

        /// <summary>Reads the current live value and formats it for display. Never cached - call fresh each time.</summary>
        public Func<string> ReadCurrent { get; }

        /// <summary>The value the contract requires, formatted for display next to <see cref="ReadCurrent"/>'s result.</summary>
        public string ExpectedDisplay { get; }

        /// <summary>Re-evaluated live on every call. True when the project currently satisfies this rule.</summary>
        public Func<bool> IsSatisfied { get; }

        /// <summary>
        /// Writes the expected value when invoked. Null means there is no safe automatic fix - the
        /// validator UI must render the rule as manual and must not synthesize a fix for it.
        /// Never invoked by CI (CatalogVerifier only reads <see cref="IsSatisfied"/>).
        /// </summary>
        public Action Fix { get; }

        /// <summary>
        /// True for a rule that should render as a warning (▲) rather than a hard failure (✖) when
        /// unsatisfied, and that is always excluded from a "Fix All" batch even on the rare case it
        /// also carries a non-null <see cref="Fix"/>. Today this is exactly one rule:
        /// settings.ContentStateBuildPath - moving it also touches .gitignore, so it is a human call.
        /// </summary>
        public bool IsWarningOnly { get; }

        public SettingsRule(
            string id,
            string description,
            Func<string> readCurrent,
            string expectedDisplay,
            Func<bool> isSatisfied,
            Action fix = null,
            bool isGroupScoped = false,
            string groupName = null,
            bool isWarningOnly = false)
        {
            Id = !string.IsNullOrEmpty(id) ? id : throw new ArgumentNullException(nameof(id));
            Description = !string.IsNullOrEmpty(description) ? description : throw new ArgumentNullException(nameof(description));
            ReadCurrent = readCurrent ?? throw new ArgumentNullException(nameof(readCurrent));
            ExpectedDisplay = expectedDisplay ?? throw new ArgumentNullException(nameof(expectedDisplay));
            IsSatisfied = isSatisfied ?? throw new ArgumentNullException(nameof(isSatisfied));
            Fix = fix;
            IsGroupScoped = isGroupScoped;
            GroupName = groupName;
            IsWarningOnly = isWarningOnly;
        }

        /// <summary>True when this rule can be included in a "Fix All" batch (has a fix, and is not warning-only).</summary>
        public bool CanAutoFix => Fix != null && !IsWarningOnly;

        /// <summary>Reads current value and pass/fail state together, as one point-in-time snapshot.</summary>
        public SettingsRuleEvaluation Evaluate() => new SettingsRuleEvaluation(this);
    }

    /// <summary>A point-in-time read of one <see cref="SettingsRule"/> - current value and whether it passed.</summary>
    public readonly struct SettingsRuleEvaluation
    {
        public SettingsRule Rule { get; }
        public bool Passed { get; }
        public string CurrentDisplay { get; }

        public SettingsRuleEvaluation(SettingsRule rule)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            CurrentDisplay = rule.ReadCurrent();
            Passed = rule.IsSatisfied();
        }
    }

    /// <summary>
    /// Encodes design doc §9 ("Addressables Configuration Contract") as data, once. Both
    /// <c>CatalogVerifier</c> (CI, batchmode, task 1.6) and <c>SettingsValidatorTab</c> (GUI, task 0.9)
    /// build their checklist by calling <see cref="BuildRules()"/> / <see cref="BuildRules(AddressableAssetSettings)"/>
    /// - neither re-implements a rule. See repo Invariant 2 (CLAUDE.md): "Settings contract chỉ có một
    /// nguồn" - the contract has exactly one source, this file.
    /// </summary>
    /// <remarks>
    /// This class has no UnityEngine.UIElements / EditorWindow dependency, so it runs headlessly under
    /// -batchmode (Invariant 2 requires this - CatalogVerifier is a CI gate, not a GUI feature).
    /// </remarks>
    public static class SettingsContract
    {
        // Target values. Kept as named constants (not inlined into each rule) so CatalogVerifier and the
        // CI build scripts can assert against the exact same numbers this contract checks, rather than a
        // second copy of the same magic numbers.
        public const int RequiredBundleRetryCount = 3;
        public const int RequiredBundleTimeoutSeconds = 30;
        public const int RequiredMaxConcurrentWebRequests = 6;

        // Correction #3 (asset audit, not in the original §9 table): design doc §5.1 wants the catalog
        // hash/json request bounded, same reasoning as BundleTimeout - 0 means infinite.
        public const int RequiredCatalogRequestsTimeoutSeconds = 15;

        public const string RequiredOverridePlayerVersion = "[UnityEditor.PlayerSettings.bundleVersion]";

        // Dedicated profile variables for catalog paths (created by CdnProfileManager, task 0.5).
        // Full separation of the catalog path from the bundle path is the design decision for task 0.6
        // (infra §2); this contract enforces it by requiring dedicated catalog variables rather than
        // allowing reuse of the bundle path variables. The separation is critical because bundles are
        // immutable and cached for a year, while catalogs are mutable and per-app-version.
        public const string RemoteCatalogBuildPathVariable = "Remote.CatalogBuildPath";
        public const string RemoteCatalogLoadPathVariable = "Remote.CatalogLoadPath";

        /// <summary>
        /// Convenience overload that resolves the project's <see cref="AddressableAssetSettings"/> via
        /// <see cref="AddressableAssetSettingsDefaultObject"/>. Throws rather than returning an empty or
        /// partially-built rule list, so a missing settings asset is a loud CI failure, not a silently
        /// empty (therefore all-green) checklist.
        /// </summary>
        public static IReadOnlyList<SettingsRule> BuildRules()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "SettingsContract: no AddressableAssetSettings asset found in the project " +
                    "(AddressableAssetSettingsDefaultObject.Settings is null). Open the Addressables " +
                    "Groups window once to create one, or point CI at the correct project.");
            }

            return BuildRules(settings);
        }

        /// <summary>Builds the full rule list (project-level rules, then one block of rules per group) fresh from the live project.</summary>
        public static IReadOnlyList<SettingsRule> BuildRules(AddressableAssetSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var rules = new List<SettingsRule>();
            AddProjectRules(settings, rules);
            AddGroupRules(settings, rules);
            return rules;
        }

        /// <summary>Formats a text report (counts + one line per failing/warning rule) suitable for "Copy report" or a CI log.</summary>
        public static string FormatReport(IEnumerable<SettingsRule> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            var evaluations = rules.Select(r => r.Evaluate()).ToList();
            int passed = evaluations.Count(e => e.Passed);
            int failed = evaluations.Count(e => !e.Passed && !e.Rule.IsWarningOnly);
            int warnings = evaluations.Count(e => !e.Passed && e.Rule.IsWarningOnly);

            var sb = new StringBuilder();
            sb.AppendLine($"CDN Settings Contract (design doc §9) - {passed} passed, {failed} failed, {warnings} warning(s) of {evaluations.Count} checks");

            foreach (var e in evaluations)
            {
                if (e.Passed) continue;

                string tag = e.Rule.IsWarningOnly ? "WARN" : "FAIL";
                string scope = e.Rule.IsGroupScoped ? $"group '{e.Rule.GroupName}' " : string.Empty;
                string fixNote = e.Rule.CanAutoFix ? string.Empty : " [manual]";
                sb.AppendLine($"[{tag}] {scope}{e.Rule.Id}: expected {e.Rule.ExpectedDisplay}, found {e.CurrentDisplay}{fixNote} -- {e.Rule.Description}");
            }

            return sb.ToString();
        }

        private static void AddProjectRules(AddressableAssetSettings settings, List<SettingsRule> rules)
        {
            rules.Add(new SettingsRule(
                id: "settings.BuildRemoteCatalog",
                description: "Without a remote catalog there is nothing for a content update to publish.",
                readCurrent: () => FormatBool(settings.BuildRemoteCatalog),
                expectedDisplay: "true",
                isSatisfied: () => settings.BuildRemoteCatalog,
                fix: () => { settings.BuildRemoteCatalog = true; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.RemoteCatalogBuildPath",
                description: "Must be bound to the dedicated catalog profile variable (infra §2, task 0.6). Catalogs are mutable and per-app-version; reusing the bundle path would force one cache policy on both.",
                readCurrent: () => string.IsNullOrEmpty(settings.RemoteCatalogBuildPath?.Id) ? "(unbound)" : settings.RemoteCatalogBuildPath.GetName(settings),
                expectedDisplay: RemoteCatalogBuildPathVariable,
                isSatisfied: () =>
                {
                    if (string.IsNullOrEmpty(settings.RemoteCatalogBuildPath?.Id))
                        return false;
                    var name = settings.RemoteCatalogBuildPath.GetName(settings);
                    return name == RemoteCatalogBuildPathVariable;
                },
                fix: () =>
                {
                    settings.RemoteCatalogBuildPath.SetVariableByName(settings, RemoteCatalogBuildPathVariable);
                    EditorUtility.SetDirty(settings);
                }));

            rules.Add(new SettingsRule(
                id: "settings.RemoteCatalogLoadPath",
                description: "Must be bound to the dedicated catalog profile variable (infra §2, task 0.6). Catalogs are mutable and per-app-version; reusing the bundle path would force one cache policy on both.",
                readCurrent: () => string.IsNullOrEmpty(settings.RemoteCatalogLoadPath?.Id) ? "(unbound)" : settings.RemoteCatalogLoadPath.GetName(settings),
                expectedDisplay: RemoteCatalogLoadPathVariable,
                isSatisfied: () =>
                {
                    if (string.IsNullOrEmpty(settings.RemoteCatalogLoadPath?.Id))
                        return false;
                    var name = settings.RemoteCatalogLoadPath.GetName(settings);
                    return name == RemoteCatalogLoadPathVariable;
                },
                fix: () =>
                {
                    settings.RemoteCatalogLoadPath.SetVariableByName(settings, RemoteCatalogLoadPathVariable);
                    EditorUtility.SetDirty(settings);
                }));

            rules.Add(new SettingsRule(
                id: "settings.OverridePlayerVersion",
                description: "A stable, non-timestamp catalog filename per app version. A timestamp here would break every content update - shipped players would look for a catalog name that no longer exists.",
                readCurrent: () => settings.OverridePlayerVersion,
                expectedDisplay: RequiredOverridePlayerVersion,
                isSatisfied: () => settings.OverridePlayerVersion == RequiredOverridePlayerVersion,
                fix: () => { settings.OverridePlayerVersion = RequiredOverridePlayerVersion; EditorUtility.SetDirty(settings); }));

            // Automated via literal path (not a profile variable) because ContentStateBuildPath is
            // project-global, not environment-specific. Platform subfolders are appended automatically
            // by GetContentStateBuildPath() via PlatformMappingService.GetPlatformPathSubFolder()
            // (AddressableAssetSettings.cs:1207). The build system creates the directory as part of
            // the content build process (CcdBuildEvents.cs:568-570), not the settings fix.
            rules.Add(new SettingsRule(
                id: "settings.ContentStateBuildPath",
                description: "addressables_content_state.bin must build outside Assets/ (task 0.4) so it survives a clean and CI can archive it independently. Losing it permanently ends delta updates for that app version.",
                readCurrent: () => settings.ContentStateBuildPath,
                expectedDisplay: "a path outside Assets/",
                isSatisfied: () => IsOutsideAssetsFolder(settings.ContentStateBuildPath),
                fix: () =>
                {
                    settings.ContentStateBuildPath = "ServerData/ContentState";
                    EditorUtility.SetDirty(settings);
                }));

            rules.Add(new SettingsRule(
                id: "settings.BundleRetryCount",
                description: "Per-bundle transient failure recovery.",
                readCurrent: () => settings.BundleRetryCount.ToString(),
                expectedDisplay: RequiredBundleRetryCount.ToString(),
                isSatisfied: () => settings.BundleRetryCount == RequiredBundleRetryCount,
                fix: () => { settings.BundleRetryCount = RequiredBundleRetryCount; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.BundleTimeout",
                description: "0 means infinite - a hung request would stall boot forever.",
                readCurrent: () => settings.BundleTimeout.ToString(),
                expectedDisplay: RequiredBundleTimeoutSeconds.ToString(),
                isSatisfied: () => settings.BundleTimeout == RequiredBundleTimeoutSeconds,
                fix: () => { settings.BundleTimeout = RequiredBundleTimeoutSeconds; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.MaxConcurrentWebRequests",
                description: "3 (the Addressables default) under-uses available bandwidth; very high values hurt on mobile.",
                readCurrent: () => settings.MaxConcurrentWebRequests.ToString(),
                expectedDisplay: RequiredMaxConcurrentWebRequests.ToString(),
                isSatisfied: () => settings.MaxConcurrentWebRequests == RequiredMaxConcurrentWebRequests,
                fix: () => { settings.MaxConcurrentWebRequests = RequiredMaxConcurrentWebRequests; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.MonoScriptBundleNaming",
                description: "Keeps the MonoScript bundle name stable across builds so it does not churn every content update.",
                readCurrent: () => settings.MonoScriptBundleNaming.ToString(),
                expectedDisplay: nameof(MonoScriptBundleNaming.ProjectName),
                isSatisfied: () => settings.MonoScriptBundleNaming == MonoScriptBundleNaming.ProjectName,
                fix: () => { settings.MonoScriptBundleNaming = MonoScriptBundleNaming.ProjectName; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.InternalIdNamingMode",
                description: "Shrinks the catalog and stops leaking project folder structure to anyone who downloads it.",
                readCurrent: () => settings.InternalIdNamingMode.ToString(),
                expectedDisplay: nameof(BundledAssetGroupSchema.AssetNamingMode.Filename),
                isSatisfied: () => settings.InternalIdNamingMode == BundledAssetGroupSchema.AssetNamingMode.Filename,
                fix: () => { settings.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.Filename; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.UniqueBundleIds",
                description: "Correct when content update is used properly; enabling it inflates every build.",
                readCurrent: () => FormatBool(settings.UniqueBundleIds),
                expectedDisplay: "false",
                isSatisfied: () => !settings.UniqueBundleIds,
                fix: () => { settings.UniqueBundleIds = false; EditorUtility.SetDirty(settings); }));

            rules.Add(new SettingsRule(
                id: "settings.EnableJsonCatalog",
                description: "Binary catalog is smaller. The Catalog Inspector tab (task 5.9) needs AddressablesTools on a version that reads binary catalogs.",
                readCurrent: () => FormatBool(settings.EnableJsonCatalog),
                expectedDisplay: "false",
                isSatisfied: () => !settings.EnableJsonCatalog,
                fix: () => { settings.EnableJsonCatalog = false; EditorUtility.SetDirty(settings); }));

            // Correction #3 (asset audit): design doc §5.1 wants this bounded; not in the original §9 table.
            rules.Add(new SettingsRule(
                id: "settings.CatalogRequestsTimeout",
                description: "0 means infinite. Design doc §5.1 wants the catalog hash/json request bounded to 15s. Not in the original §9 table - added from an asset audit.",
                readCurrent: () => settings.CatalogRequestsTimeout.ToString(),
                expectedDisplay: RequiredCatalogRequestsTimeoutSeconds.ToString(),
                isSatisfied: () => settings.CatalogRequestsTimeout == RequiredCatalogRequestsTimeoutSeconds,
                fix: () => { settings.CatalogRequestsTimeout = RequiredCatalogRequestsTimeoutSeconds; EditorUtility.SetDirty(settings); }));

            // Correction #4 (asset audit): PreferencesValue defers to a machine-local Editor preference,
            // which is non-deterministic in CI. Pinned to DoNotBuildWithPlayer because Addressables
            // content builds are an explicit CI step (CdnBuildCLI, Phase 1), not an implicit side effect
            // of a player build. Not in the original §9 table - added from an asset audit.
            rules.Add(new SettingsRule(
                id: "settings.BuildAddressablesWithPlayerBuild",
                description: "PreferencesValue defers to a per-machine Editor preference for whether a player build also builds Addressables content - not reproducible in CI. Pinned to DoNotBuildWithPlayer: content builds run as their own explicit CdnBuildCLI step. Not in the original §9 table - added from an asset audit.",
                readCurrent: () => settings.BuildAddressablesWithPlayerBuild.ToString(),
                expectedDisplay: nameof(AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer),
                isSatisfied: () => settings.BuildAddressablesWithPlayerBuild == AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer,
                fix: () =>
                {
                    settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
                    EditorUtility.SetDirty(settings);
                }));
        }

        private static void AddGroupRules(AddressableAssetSettings settings, List<SettingsRule> rules)
        {
            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                string groupName = group.Name;

                // Correction #5 (asset audit): every group needs a BundledAssetGroupSchema to be
                // buildable at all. Checked unconditionally, for every group. No Fix - which template/
                // schema to attach is a human call (a Local group needs different defaults than a
                // Remote one), not something a validator should silently decide. Scene.asset currently
                // has entries and an empty schema set, so this rule reports it red immediately - that is
                // the honest way to satisfy the task 0.9 exit criterion, not a bug in the rule.
                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:HasBundledAssetGroupSchema",
                    description: $"Group '{groupName}' has no BundledAssetGroupSchema, so it cannot be built. Attach one from a group template.",
                    readCurrent: () => group.GetSchema<BundledAssetGroupSchema>() != null ? "present" : "MISSING",
                    expectedDisplay: "present",
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>() != null,
                    fix: null,
                    isGroupScoped: true,
                    groupName: groupName));

                // The remaining group rules only make sense once a schema exists to read from.
                if (group.GetSchema<BundledAssetGroupSchema>() == null)
                    continue;

                // Correction #1 (asset audit): Default Local Group_BundledAssetGroupSchema.asset has
                // m_UseDefaultSchemaSettings: 0, with its own m_Timeout/m_RetryCount/m_InternalIdNamingMode,
                // so setting BundleRetryCount/BundleTimeout/InternalIdNamingMode only at the settings
                // level is a no-op for this group. DECISION: fix Timeout/RetryCount/InternalIdNamingMode
                // directly, per group, and leave UseDefaultSchemaSettings untouched - see
                // BundledAssetGroupSchema's own getters (verified against the Addressables source):
                // Timeout/RetryCount/InternalIdNamingMode are plain passthroughs to their own fields and
                // are NOT gated by UseDefaultSchemaSettings at all (only BundleNaming and the
                // UseAssetBundleCrc* pair are, and they redirect to the project's default GROUP TEMPLATE,
                // not to these AddressableAssetSettings fields). Flipping that flag would therefore not
                // fix these three fields, and would additionally make BundleNaming silently track the
                // default template instead of this contract - an unreviewed side effect on correction #2.
                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:BundleTimeout",
                    description: $"Group '{groupName}' schema Timeout must match the settings-level contract - a per-group value does not inherit just because AddressableAssetSettings.BundleTimeout changed.",
                    readCurrent: () => group.GetSchema<BundledAssetGroupSchema>()?.Timeout.ToString() ?? "(no schema)",
                    expectedDisplay: RequiredBundleTimeoutSeconds.ToString(),
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>()?.Timeout == RequiredBundleTimeoutSeconds,
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.Timeout = RequiredBundleTimeoutSeconds;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));

                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:RetryCount",
                    description: $"Group '{groupName}' schema RetryCount must match the settings-level contract.",
                    readCurrent: () => group.GetSchema<BundledAssetGroupSchema>()?.RetryCount.ToString() ?? "(no schema)",
                    expectedDisplay: RequiredBundleRetryCount.ToString(),
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>()?.RetryCount == RequiredBundleRetryCount,
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.RetryCount = RequiredBundleRetryCount;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));

                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:InternalIdNamingMode",
                    description: $"Group '{groupName}' schema InternalIdNamingMode must match the settings-level contract.",
                    readCurrent: () => group.GetSchema<BundledAssetGroupSchema>()?.InternalIdNamingMode.ToString() ?? "(no schema)",
                    expectedDisplay: nameof(BundledAssetGroupSchema.AssetNamingMode.Filename),
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>()?.InternalIdNamingMode == BundledAssetGroupSchema.AssetNamingMode.Filename,
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.Filename;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));

                // Correction #2 (asset audit): infra §2 makes a content hash in the bundle filename
                // mandatory - without it a content update overwrites the previous build's bundle objects
                // while players still on the old catalog are resolving them. AppendHash and OnlyHash both
                // hash the *bundle content*, so either is acceptable; NoHash never includes a hash, and
                // FileNameHash - per the Addressables source doc comment - hashes only the file's
                // assigned *name*, not its content, so it does not change when content changes and fails
                // this rule exactly like NoHash does. Fix picks AppendHash (design doc's primary
                // recommendation; keeps a readable name prefix, unlike OnlyHash's opaque filenames).
                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:BundleNaming",
                    description: $"Group '{groupName}' bundle filenames must include a content hash (infra §2) or a content update overwrites live bundle objects still being resolved by players on the previous catalog.",
                    readCurrent: () => group.GetSchema<BundledAssetGroupSchema>()?.BundleNaming.ToString() ?? "(no schema)",
                    expectedDisplay: $"{nameof(BundledAssetGroupSchema.BundleNamingStyle.AppendHash)} or {nameof(BundledAssetGroupSchema.BundleNamingStyle.OnlyHash)}",
                    isSatisfied: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return false;
                        var naming = schema.BundleNaming;
                        return naming == BundledAssetGroupSchema.BundleNamingStyle.AppendHash
                            || naming == BundledAssetGroupSchema.BundleNamingStyle.OnlyHash;
                    },
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.AppendHash;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));

                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:UseAssetBundleCrc",
                    description: $"Group '{groupName}': detects corrupted downloads.",
                    readCurrent: () => FormatBool(group.GetSchema<BundledAssetGroupSchema>()?.UseAssetBundleCrc ?? false),
                    expectedDisplay: "true",
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>()?.UseAssetBundleCrc == true,
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.UseAssetBundleCrc = true;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));

                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:UseAssetBundleCrcForCachedBundles",
                    description: $"Group '{groupName}': detects corruption of already-cached bundles.",
                    readCurrent: () => FormatBool(group.GetSchema<BundledAssetGroupSchema>()?.UseAssetBundleCrcForCachedBundles ?? false),
                    expectedDisplay: "true",
                    isSatisfied: () => group.GetSchema<BundledAssetGroupSchema>()?.UseAssetBundleCrcForCachedBundles == true,
                    fix: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return;
                        schema.UseAssetBundleCrcForCachedBundles = true;
                        EditorUtility.SetDirty(schema);
                    },
                    isGroupScoped: true,
                    groupName: groupName));
            }
        }

        /// <summary>True for a project-relative path that is not inside Assets/ (and not empty/unresolved).</summary>
        private static bool IsOutsideAssetsFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/').TrimStart('/');
            return !normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatBool(bool value) => value ? "true" : "false";
    }
}
