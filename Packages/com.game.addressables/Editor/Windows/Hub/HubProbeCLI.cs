using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Batchmode smoke check for <see cref="AddressableManagerHub"/>: builds every section's view,
    /// runs its first refresh, and asserts the shell's own contracts.
    /// </summary>
    /// <remarks>
    /// The window cannot be seen from batchmode, so this checks the parts that can be. Beyond the
    /// per-section checks the CDN tab probe already does, this asserts three things the shell
    /// depends on and that nothing else would catch:
    ///
    /// <list type="bullet">
    /// <item><b>Every name the C# queries exists in the UXML.</b> A rename leaves a null and a
    /// handler that silently no-ops. This package has shipped dead UI that way twice.</item>
    /// <item><b>Section ids are unique and stable.</b> They are persisted in SessionState and used
    /// by deep links; a duplicate makes routing pick whichever came first.</item>
    /// <item><b>Every stage on the rail has at least one section</b>, and every section's stage is a
    /// real one - otherwise a step of the pipeline is unreachable and nothing says so.</item>
    /// </list>
    ///
    /// It does not check that anything looks right. That still needs a human with the window open.
    /// </remarks>
    public static class HubProbeCLI
    {
        /// <summary>Entry point: <c>-executeMethod AddressableManager.Editor.Windows.Hub.HubProbeCLI.ProbeHub</c></summary>
        public static void ProbeHub()
        {
            // Gate on compilation before anything else. Unity runs -executeMethod against a stale
            // assembly and still exits 0 when compilation failed, so without this the probe would
            // report on code that is not the code in the working tree.
            if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogError("[HubProbe] FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            int failures = 0;

            failures += CheckShellMarkup();
            failures += CheckSectionContracts(out var sections);
            failures += BuildEverySection(sections);

            if (failures > 0)
            {
                Debug.LogError($"[HubProbe] FAILURE: {failures} problem(s)");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[HubProbe] SUCCESS: {sections.Count} section(s) built and refreshed");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Every element name <see cref="AddressableManagerHub"/> resolves must exist in the UXML.
        /// </summary>
        /// <remarks>
        /// The list is written out by hand rather than reflected off the window, deliberately: a
        /// reflective check would pass on a field the window stopped using, and the thing worth
        /// catching is the mismatch between what the C# asks for and what the markup provides.
        /// </remarks>
        private static int CheckShellMarkup()
        {
            const string uxmlPath =
                "Packages/com.game.addressables/Editor/Windows/Hub/UI/AddressableManagerHub.uxml";
            const string ussPath =
                "Packages/com.game.addressables/Editor/Windows/Hub/UI/AddressableManagerHub.uss";

            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (tree == null)
            {
                Debug.LogError($"[HubProbe] UXML did not resolve at {uxmlPath}");
                return 1;
            }

            if (AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath) == null)
            {
                Debug.LogError($"[HubProbe] USS did not resolve at {ussPath}");
                return 1;
            }

            var root = tree.CloneTree();

            string[] required =
            {
                "hub-rail-stages", "hub-section-body", "hub-section-actions", "hub-rail-blocker",
                "hub-status-dot", "hub-header-context", "hub-section-title", "hub-section-subtitle",
                "hub-rail-blocker-title", "hub-rail-blocker-body", "hub-status-text",
                "hub-status-context", "hub-header-chips", "hub-header-search",
            };

            int missing = 0;
            foreach (var name in required)
            {
                if (root.Q<VisualElement>(name) == null)
                {
                    Debug.LogError($"[HubProbe] UXML is missing an element named '{name}' that the window queries");
                    missing++;
                }
            }

            if (missing == 0)
                Debug.Log($"[HubProbe] shell markup: all {required.Length} queried names present");

            return missing;
        }

        private static int CheckSectionContracts(out List<IHubSection> sections)
        {
            sections = HubSections.Create();
            int failures = 0;

            if (sections.Count == 0)
            {
                Debug.LogError("[HubProbe] HubSections.Create() returned no sections");
                return 1;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stagesCovered = new HashSet<PipelineStage>();

            foreach (var section in sections)
            {
                if (string.IsNullOrEmpty(section.Id))
                {
                    Debug.LogError($"[HubProbe] section '{section.Title}' has no Id");
                    failures++;
                }
                else if (!seen.Add(section.Id))
                {
                    Debug.LogError($"[HubProbe] duplicate section id '{section.Id}' — routing would " +
                                   "silently pick whichever was registered first");
                    failures++;
                }

                if (string.IsNullOrEmpty(section.Subtitle))
                {
                    Debug.LogError($"[HubProbe] section '{section.Id}' has no Subtitle");
                    failures++;
                }

                if (Array.IndexOf(PipelineStages.All, section.Stage) < 0)
                {
                    Debug.LogError($"[HubProbe] section '{section.Id}' has stage {section.Stage}, " +
                                   "which the rail does not draw — the section would be unreachable");
                    failures++;
                }

                stagesCovered.Add(section.Stage);
            }

            foreach (var stage in PipelineStages.All)
            {
                if (!stagesCovered.Contains(stage))
                {
                    Debug.LogWarning($"[HubProbe] pipeline stage '{PipelineStages.Label(stage)}' has " +
                                     "no sections yet, so the rail will not draw it");
                }
            }

            if (failures == 0)
                Debug.Log($"[HubProbe] section contracts: {sections.Count} section(s), ids unique, stages valid");

            return failures;
        }

        private static int BuildEverySection(List<IHubSection> sections)
        {
            int failures = 0;

            foreach (var section in sections)
            {
                try
                {
                    var view = section.CreateView();
                    if (view == null)
                    {
                        Debug.LogError($"[HubProbe] {section.Id}: CreateView returned null");
                        failures++;
                        continue;
                    }

                    // Attach so layout-dependent code paths run, the same way the CDN tab probe does.
                    var host = new VisualElement();
                    host.Add(view);

                    section.OnShown();

                    // A hosted CDN tab must arrive with the stylesheet its markup was written
                    // against. Nothing else notices when it does not: the view builds, every query
                    // resolves, no exception is thrown - and the screen renders with its shared
                    // layout classes unstyled, which looks like a rendering glitch rather than a
                    // missing file. That shipped in 4.1.1 and was found by looking at it.
                    if (section is CdnTabSection)
                    {
                        var shared = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                            CdnTabSection.SharedStyleSheetPath);

                        if (shared == null)
                        {
                            Debug.LogError(
                                $"[HubProbe] {section.Id}: {CdnTabSection.SharedStyleSheetPath} " +
                                "did not resolve");
                            failures++;
                        }
                        else if (!view.styleSheets.Contains(shared))
                        {
                            Debug.LogError(
                                $"[HubProbe] {section.Id}: view is missing the shared CDN stylesheet, " +
                                "so its cdn-* layout classes resolve to nothing");
                            failures++;
                        }
                    }

                    // Health must be cheap AND must not throw - the rail calls it once a second for
                    // every section, so one that throws would repeat the failure forever and bury
                    // the Console. Call it twice: a probe that only works the first time is a probe
                    // that has not met the contract.
                    var first = section.GetHealth();
                    var second = section.GetHealth();

                    Debug.Log($"[HubProbe] {section.Id}: built, health={first.State}" +
                              (string.IsNullOrEmpty(first.Badge) ? "" : $" \"{first.Badge}\"") +
                              (first.State == second.State ? "" : $" (UNSTABLE: second call said {second.State})"));

                    if (first.State != second.State)
                    {
                        Debug.LogError($"[HubProbe] {section.Id}: GetHealth is not stable across calls");
                        failures++;
                    }

                    // A NotMeasured with no reason is the failure mode this whole state exists to
                    // prevent, one level up: an honest "unknown" that tells the user nothing.
                    if (first.State == HealthState.NotMeasured && string.IsNullOrEmpty(first.Reason))
                    {
                        Debug.LogError($"[HubProbe] {section.Id}: reports NotMeasured with no reason");
                        failures++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[HubProbe] {section.Id}: threw {ex.GetType().Name}: {ex.Message}");
                    Debug.LogError(ex.StackTrace);
                    failures++;
                }
            }

            return failures;
        }
    }
}
