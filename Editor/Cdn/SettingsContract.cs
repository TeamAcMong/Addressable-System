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
        /// <summary>
        /// Whether this assembly was compiled with ENABLE_JSON_CATALOG - i.e. whether the build will
        /// actually emit a JSON catalog, regardless of what the settings bool says.
        /// </summary>
        private static bool JsonCatalogDefineActive =>
#if ENABLE_JSON_CATALOG
            true;
#else
            false;
#endif

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
            AddExternalRules(settings, rules);
            return rules;
        }

        /// <summary>
        /// Appends rules contributed by the consuming project, discovered via
        /// <see cref="SettingsRuleProviderAttribute"/>.
        /// </summary>
        /// <remarks>
        /// WHY TypeCache AND NOT A STATIC EVENT. An event in an Editor assembly loses every subscriber
        /// on domain reload - on every script compile, and on entering Play Mode with reload enabled.
        /// A project that subscribed from anywhere other than [InitializeOnLoadMethod] would simply
        /// stop contributing rules, and an absent rule reads as GREEN in all four consumers of this
        /// list (the validator tab, "Fix All", CatalogVerifier, and the unattended CdnSetupCLI). That
        /// is the same false-green this contract exists to prevent. Unity rebuilds TypeCache before any
        /// user code runs after a reload, so there is nothing to re-register and nothing to lose.
        ///
        /// Everything here is defensive on purpose: this list feeds a CI gate and a batchmode CLI that
        /// calls Fix() unattended, so one malformed provider in a consuming project must degrade to a
        /// loud, skipped provider - never to a thrown exception that makes the whole contract
        /// unevaluatable, and never to a silently shorter list.
        /// </remarks>
        private static void AddExternalRules(AddressableAssetSettings settings, List<SettingsRule> rules)
        {
            var providers = TypeCache.GetMethodsWithAttribute<SettingsRuleProviderAttribute>()
                .Where(m => m.IsStatic)
                // Deterministic order: without this, report row order follows assembly load order and
                // every "Copy report" diff churns for reasons unrelated to the project.
                .OrderBy(m => m.DeclaringType?.Assembly.GetName().Name ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(m => m.DeclaringType?.FullName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal);

            var seen = new HashSet<string>(rules.Select(r => r.Id), StringComparer.Ordinal);

            foreach (var method in providers)
            {
                string origin = $"{method.DeclaringType?.FullName}.{method.Name}";

                var parameters = method.GetParameters();
                if (!typeof(IEnumerable<SettingsRule>).IsAssignableFrom(method.ReturnType)
                    || parameters.Length != 1
                    || parameters[0].ParameterType != typeof(AddressableAssetSettings))
                {
                    UnityEngine.Debug.LogError(
                        $"[SettingsContract] {origin} carries [SettingsRuleProvider] but does not have the " +
                        "required signature 'static IEnumerable<SettingsRule> M(AddressableAssetSettings)'. " +
                        "Ignored.");
                    continue;
                }

                IEnumerable<SettingsRule> produced;
                try
                {
                    produced = (IEnumerable<SettingsRule>)method.Invoke(null, new object[] { settings });
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[SettingsContract] rule provider {origin} threw: {ex}. Its rules are omitted.");
                    continue;
                }

                if (produced == null) continue;

                foreach (var rule in produced)
                {
                    if (rule == null) continue;

                    // Ids key the report, "Fix All", and any CI allowlist. A duplicate would make one of
                    // the two rules invisible depending on iteration order, so it is refused loudly.
                    if (!seen.Add(rule.Id))
                    {
                        UnityEngine.Debug.LogError(
                            $"[SettingsContract] provider {origin} returned rule id '{rule.Id}', which is " +
                            "already in use. Dropped - ids must be unique across the whole contract.");
                        continue;
                    }

                    rules.Add(rule);
                }
            }
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
            // The invariant the whole two-config design rests on, and which nothing stated until now.
            //
            // Two systems decide where content comes from, at two different times:
            //   Addressables profile Remote.LoadPath  -> the origin BAKED INTO THE CATALOG at build time
            //   CdnSettings environments[].baseUrl    -> the origin HostRewriter swaps TO at runtime
            //
            // HostRewriter.Rewrite only rewrites a URL whose origin StartsWith one it was told about;
            // anything else it deliberately leaves alone. So if the baked origin is not one of the
            // configured base URLs, switching environment at runtime does nothing at all and every
            // request goes wherever the build was pointed.
            //
            // Nothing checked this. A build against a profile whose host is not in CdnSettings - the
            // package's own Dev/Staging/Prod templates ship a literal "<domain>" placeholder, so this
            // is the DEFAULT state - produced a successful build, a passing verifier, and a player
            // that fetched everything from a host that does not exist, with no log line anywhere.
            //
            // No Fix: which of the two sides is wrong is a human call. Adding the origin to
            // CdnSettings and rebuilding against a different profile are both valid answers and they
            // mean different things.
            rules.Add(new SettingsRule(
                id: "settings.RemoteOriginIsKnown",
                description:
                    "The origin baked into the catalog by the active profile's Remote paths must be one of " +
                    "the base URLs in CdnSettings, or HostRewriter cannot redirect it and switching " +
                    "environment at runtime silently does nothing.",
                readCurrent: () => DescribeRemoteOrigins(settings),
                expectedDisplay: "every remote origin matches a CdnEnvironment.BaseUrl",
                isSatisfied: () => CdnBuildModes.IsLocalOnly || UnknownRemoteOrigins(settings).Count == 0,
                fix: null));

            rules.Add(new SettingsRule(
                id: "settings.BuildRemoteCatalog",
                description: "Without a remote catalog there is nothing for a content update to publish. " +
                             "Not required in local-only mode (CdnSettings > Build Mode).",
                readCurrent: () => CdnBuildModes.IsLocalOnly
                    ? $"{CdnBuildModes.NotApplicable} {FormatBool(settings.BuildRemoteCatalog)}"
                    : FormatBool(settings.BuildRemoteCatalog),
                expectedDisplay: "true (Remote mode) / any (LocalOnly)",
                isSatisfied: () => CdnBuildModes.IsLocalOnly || settings.BuildRemoteCatalog,
                // No fix in local-only mode. This rule carrying an unconditional auto-fix is what let
                // "Fix All" and unattended CdnSetupCLI runs switch a deliberately-local project back to
                // remote, minutes after someone turned it off.
                fix: CdnBuildModes.IsLocalOnly
                    ? (Action)null
                    : () => { settings.BuildRemoteCatalog = true; EditorUtility.SetDirty(settings); }));

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

            // ContentStateBuildPath must include [BuildTarget] token so that each platform (Android, iOS,
            // Windows, etc.) stores its content state file separately. Without platform separation, a
            // second platform build silently overwrites the first's state file, permanently ending
            // delta updates for that platform version (infrastructure §6, risk R1). The token is expanded
            // at build time by AddressableAssetSettings.ContentStateBuildPath / EvaluateString.
            rules.Add(new SettingsRule(
                id: "settings.ContentStateBuildPath",
                description: "addressables_content_state.bin must build outside Assets/ (task 0.4) so it survives a clean and CI can archive it independently. Losing it permanently ends delta updates for that app version. Path must include [BuildTarget] token to prevent Android/iOS/Windows from overwriting each other's state files.",
                readCurrent: () => settings.ContentStateBuildPath,
                expectedDisplay: "a path outside Assets/ with [BuildTarget] token (e.g., 'ServerData/ContentState/[BuildTarget]')",
                isSatisfied: () => IsOutsideAssetsFolder(settings.ContentStateBuildPath) && settings.ContentStateBuildPath.Contains("[BuildTarget]"),
                fix: () =>
                {
                    settings.ContentStateBuildPath = "ServerData/ContentState/[BuildTarget]";
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

            // In Addressables 2.9.x the catalog FORMAT is decided at compile time by the
            // ENABLE_JSON_CATALOG scripting define. settings.EnableJsonCatalog is a UI mirror that
            // Unity's own inspector keeps in sync by also calling UpdateSymbolsForBuildTarget for every
            // build target. Reading and writing only the bool therefore produced two false verdicts:
            // a project whose define is set but whose bool is false reported "satisfied" while every
            // build emitted catalog.json, and the auto-fix "corrected" the bool without touching the
            // define, so the rule went green and the build did not change. The define is what the
            // editor assembly itself was compiled with, so it can simply be asked.
            rules.Add(new SettingsRule(
                id: "settings.EnableJsonCatalog",
                description: "Binary catalog is smaller. The Catalog Inspector tab (task 5.9) needs AddressablesTools on a version that reads binary catalogs. In Addressables 2.9+ the format is set by the ENABLE_JSON_CATALOG scripting define, not by this bool alone.",
                readCurrent: () => $"bool={FormatBool(settings.EnableJsonCatalog)}, define={(JsonCatalogDefineActive ? "ENABLE_JSON_CATALOG present" : "absent")}",
                expectedDisplay: "bool=false, define=absent",
                isSatisfied: () => !settings.EnableJsonCatalog && !JsonCatalogDefineActive,
                fix: JsonCatalogDefineActive
                    ? (Action)null   // removing a scripting define triggers a full recompile - a human call
                    : () => { settings.EnableJsonCatalog = false; EditorUtility.SetDirty(settings); }));

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

        /// <summary>
        /// Clears <c>UseDefaultSchemaSettings</c> before a per-field fix writes to the schema.
        /// </summary>
        /// <remarks>
        /// BundledAssetGroupSchema's BundleNaming / UseAssetBundleCrc / UseAssetBundleCrcForCachedBundles
        /// getters short-circuit to the group-template defaults while UseDefaultSchemaSettings is on, and
        /// ignore the private backing fields entirely. A fix that assigns the property in that state
        /// produces a real, dirty, committed diff in the schema asset while the matching IsSatisfied()
        /// keeps reading the template value and keeps returning false: the rule becomes permanently
        /// unfixable, "Fix All" counts a write that did nothing, and CdnSetupCLI classifies it as
        /// "fixable rule still failing after auto-fix attempt (bug in fix logic)" and exits 1.
        ///
        /// Turning the toggle off is the honest resolution - the fix is being asked to set a per-group
        /// value, and a per-group value is precisely what the toggle disables - but it is a visible
        /// change to the group, so it is logged rather than done quietly.
        /// </remarks>
        /// <summary>
        /// True when <paramref name="settings"/> is the all-zero sentinel Addressables returns for a
        /// schema that has no default template behind it.
        /// </summary>
        /// <remarks>
        /// DefaultSchemaSettings is a plain struct with no equality members, so `== default` will not
        /// compile and `Equals(default)` would box and compare field-by-field via reflection. Spelling
        /// the comparison out is cheaper and, more importantly, it fails to compile rather than silently
        /// ignoring a field if Unity adds one to the struct - which is the failure mode that matters
        /// here, since a missed field would put this check back to guessing.
        ///
        /// No real template is all-zero: every entry Unity builds in CreateDefaultSchemaSettings sets
        /// compression to LZ4 or LZMA and useAssetBundleCache to true.
        /// </remarks>
        private static bool IsDefaultSchemaSettings(BundledAssetGroupSchema.DefaultSchemaSettings settings)
        {
            return settings.compression == default
                && settings.useAssetBundleCache == default
                && settings.assetBundledCacheClearBehavior == default
                && settings.useAssetBundleCrc == default
                && settings.useAssetBundleCrcForCachedBundles == default;
        }

        private static void EnsurePerGroupSchemaSettings(BundledAssetGroupSchema schema, string groupName)
        {
            if (schema == null || !schema.UseDefaultSchemaSettings) return;

            // SEVEN properties are gated behind this toggle, not one. While it is on, every one of them
            // ignores its own serialized field and returns GetDefaultSchemaSettings(). Flipping the
            // toggle to make ONE fix land would therefore silently switch the other six from the
            // template defaults the group is effectively running on to whatever stale values their
            // backing fields happen to hold - a config change nobody asked for, invisible in the
            // validator, and exactly the class of silent drift this contract exists to catch.
            //
            // So: read the effective values first (still the template's, the toggle is on), flip, then
            // write them all back. Afterwards the group is running per-group settings that are
            // byte-for-byte what it was already running, and the caller's single assignment is the only
            // thing that actually changes.
            //
            // BUT the snapshot is only meaningful if there IS a template behind the toggle. On
            // Addressables 2.9.x, GetDefaultSchemaSettings() returns default(DefaultSchemaSettings) -
            // all zeros - whenever the schema uses custom paths, or its LoadPath is bound to anything
            // other than the built-in Local.LoadPath / Remote.LoadPath (a third pair such as
            // CDN.LoadPath counts). 2.3.1 could not reach that state through this function because its
            // UseDefaultSchemaSettings getter short-circuited to false for those groups; 2.9.1 dropped
            // that short-circuit, so the getter says true and the snapshot silently reads zeros.
            // Writing THOSE back would persist Uncompressed + no CRC + no bundle cache onto a live
            // group, unattended, via CdnSetupCLI - strictly worse than the bug this guard exists to fix.
            //
            // GetDefaultSchemaSettings() is public, so ask it directly rather than inferring the state
            // from path bindings.
            bool hasRealTemplate = !IsDefaultSchemaSettings(schema.GetDefaultSchemaSettings());
            if (!hasRealTemplate)
            {
                // No template behind the toggle: the seven getters are already handing out zeros, so
                // there is nothing worth preserving and nothing safe to copy. Turn the toggle off so
                // the caller's fix can land, and leave the group on its own stored values - which is
                // what it will now start using. Say so, because it changes more than the one field.
                schema.UseDefaultSchemaSettings = false;

                UnityEngine.Debug.LogWarning(
                    $"[SettingsContract] Group '{groupName}' had \"Use Default Schema Settings\" enabled but has " +
                    "no default template behind it (custom paths, or a LoadPath that is not Local.LoadPath / " +
                    "Remote.LoadPath). On Addressables 2.9+ that means it was silently building Uncompressed " +
                    "with no CRC and no bundle cache. Turned the toggle off so the group now uses its own " +
                    "stored schema values - review Compression / UseAssetBundleCache / CRC on this group.");
                return;
            }

            var compression = schema.Compression;
            var cacheClear = schema.AssetBundledCacheClearBehavior;
            var stripDownload = schema.StripDownloadOptions;
            var useCache = schema.UseAssetBundleCache;
            var useCrc = schema.UseAssetBundleCrc;
            var useCrcCached = schema.UseAssetBundleCrcForCachedBundles;
            var bundleNaming = schema.BundleNaming;

            schema.UseDefaultSchemaSettings = false;

            schema.Compression = compression;
            schema.AssetBundledCacheClearBehavior = cacheClear;
            schema.StripDownloadOptions = stripDownload;
            schema.UseAssetBundleCache = useCache;
            schema.UseAssetBundleCrc = useCrc;
            schema.UseAssetBundleCrcForCachedBundles = useCrcCached;
            schema.BundleNaming = bundleNaming;

            UnityEngine.Debug.LogWarning(
                $"[SettingsContract] Group '{groupName}' had \"Use Default Schema Settings\" enabled, which " +
                "makes its per-group bundle settings read-only. Turned it off so the fix can take effect, " +
                "and copied the seven previously-inherited values onto the group first so nothing else " +
                "changed. If this group should keep following the group template, revert the fix and edit " +
                "the template instead.");
        }

        /// <summary>
        /// Origins the active profile bakes into content, that no <c>CdnEnvironment</c> claims.
        /// </summary>
        /// <remarks>
        /// Returns empty when there is no CdnSettings asset at all: a project that does not use the
        /// runtime CDN layer has no rewriter, so the invariant does not apply to it and reporting a
        /// failure would be noise. It also returns empty when a path is unset - other rules already
        /// cover that, and reporting the same hole twice helps nobody.
        /// </remarks>
        private static List<string> UnknownRemoteOrigins(AddressableAssetSettings settings)
        {
            var unknown = new List<string>();

            var loaded = AddressableManager.Cdn.CdnSettings.Load();
            if (loaded.IsFailure || loaded.Value == null)
                return unknown;

            var knownOrigins = new List<string>();
            foreach (var environment in loaded.Value.Environments)
            {
                if (environment == null || string.IsNullOrEmpty(environment.BaseUrl)) continue;
                knownOrigins.Add(environment.BaseUrl);
            }

            if (knownOrigins.Count == 0)
                return unknown;

            foreach (string variable in new[]
                     {
                         AddressableAssetSettings.kRemoteLoadPath,
                         RemoteCatalogLoadPathVariable
                     })
            {
                string raw = settings.profileSettings.GetValueByName(settings.activeProfileId, variable);
                if (string.IsNullOrEmpty(raw)) continue;

                string evaluated = settings.profileSettings.EvaluateString(settings.activeProfileId, raw);
                if (string.IsNullOrEmpty(evaluated)) continue;

                // Only absolute http(s) content is rewritten; a local path is not this rule's business.
                if (!evaluated.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !evaluated.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool matched = false;
                foreach (string origin in knownOrigins)
                {
                    if (evaluated.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                    unknown.Add($"{variable}={evaluated}");
            }

            return unknown;
        }

        /// <summary>Human-readable current state for the remote-origin rule.</summary>
        private static string DescribeRemoteOrigins(AddressableAssetSettings settings)
        {
            if (CdnBuildModes.IsLocalOnly)
                return CdnBuildModes.NotApplicable;

            var loaded = AddressableManager.Cdn.CdnSettings.Load();
            if (loaded.IsFailure || loaded.Value == null)
                return "(no CdnSettings asset - runtime CDN layer not in use, rule does not apply)";

            var unknown = UnknownRemoteOrigins(settings);
            if (unknown.Count == 0)
                return "all remote origins match a configured environment";

            return "NOT in CdnSettings: " + string.Join("; ", unknown);
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

                // Correction #6 (Phase 1, task 1.0): every group that builds also needs a
                // ContentUpdateGroupSchema to participate in content updates. If absent, the group
                // is silently excluded from delta logic even if it is buildable. No Fix - same
                // reasoning as HasBundledAssetGroupSchema: a human must decide whether this group
                // should be static (shipped with player, immutable) or dynamic (patchable).
                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:HasContentUpdateGroupSchema",
                    description: $"Group '{groupName}' has no ContentUpdateGroupSchema, so it cannot participate in content updates. Attach one from a group template.",
                    readCurrent: () => group.GetSchema<ContentUpdateGroupSchema>() != null ? "present" : "MISSING",
                    expectedDisplay: "present",
                    isSatisfied: () => group.GetSchema<ContentUpdateGroupSchema>() != null,
                    fix: null,
                    isGroupScoped: true,
                    groupName: groupName));

                // The remaining group rules only make sense once schemas exist to read from.
                if (group.GetSchema<BundledAssetGroupSchema>() == null || group.GetSchema<ContentUpdateGroupSchema>() == null)
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

                // The gap CatalogVerifier structurally cannot close. That verifier only proves the
                // files on disk match the manifest the same build just wrote - a closed loop. If ONE
                // group of several is still bound to Local.* while the rest are Remote.*, the remote
                // folder still contains the other groups' bundles, the count is non-zero, every hash
                // matches, and the build passes while that group's content is silently baked into the
                // player instead of published. Only a per-group path assertion can catch it, and only a
                // human can say which groups are meant to be remote - so this rule reports rather than
                // guesses, and a genuinely-local group is marked as such by name.
                rules.Add(new SettingsRule(
                    id: $"group:{groupName}:RemotePathsConsistent",
                    description: $"Group '{groupName}': BuildPath and LoadPath must both be remote or both be local. A group with one of each builds to a place its catalog does not point at, and no other check in this package can see it.",
                    readCurrent: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return "(no schema)";
                        return $"build={schema.BuildPath.GetName(settings) ?? "(unset)"}, load={schema.LoadPath.GetName(settings) ?? "(unset)"}";
                    },
                    expectedDisplay: "both Remote.* or both Local.*",
                    isSatisfied: () =>
                    {
                        var schema = group.GetSchema<BundledAssetGroupSchema>();
                        if (schema == null) return false;

                        string build = schema.BuildPath.GetName(settings);
                        string load = schema.LoadPath.GetName(settings);
                        if (string.IsNullOrEmpty(build) || string.IsNullOrEmpty(load)) return false;

                        bool buildRemote = build.StartsWith("Remote.", StringComparison.Ordinal);
                        bool loadRemote = load.StartsWith("Remote.", StringComparison.Ordinal);
                        return buildRemote == loadRemote;
                    },
                    fix: null,   // which side is correct is the human's call, not the validator's
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
                        EnsurePerGroupSchemaSettings(schema, groupName);
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
                        EnsurePerGroupSchemaSettings(schema, groupName);
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
                        EnsurePerGroupSchemaSettings(schema, groupName);
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
