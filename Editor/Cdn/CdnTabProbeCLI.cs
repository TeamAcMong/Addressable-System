using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn.Windows;


namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Batchmode smoke check for CdnManagerWindow tabs: builds every tab's view and runs its first
    /// refresh, outside the Editor GUI.
    /// </summary>
    /// <remarks>
    /// A tab cannot be seen from batchmode, so this checks the part that can be: that the UXML and
    /// USS assets resolve, that every element the C# queries by name actually exists in the UXML
    /// (a rename leaves a null and a NullReferenceException on first refresh), and that OnShown
    /// runs against live project state without throwing. It does not check that anything looks
    /// right - that still needs a human with the window open.
    /// </remarks>
    public static class CdnTabProbeCLI
    {
        public static void ProbeTabs()
        {
            if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogError("[CdnTabProbe] FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            // Same list the window registers, not a copy of it — see CdnManagerWindow.RegisterTabs.
            var tabs = CdnManagerWindow.CreateTabs();

            int failures = 0;
            foreach (var tab in tabs)
            {
                try
                {
                    var view = tab.CreateView();
                    if (view == null)
                    {
                        Debug.LogError($"[CdnTabProbe] {tab.TabName}: CreateView returned null");
                        failures++;
                        continue;
                    }

                    // Attach to a real panel-less hierarchy so layout code paths run.
                    var host = new VisualElement();
                    host.Add(view);

                    tab.OnShown();

                    // childCount alone only proves a root exists. Report what the tab concluded and
                    // how many data rows it drew, so a run against a project that should produce
                    // findings can be told apart from one that silently rendered nothing.
                    var summary = view.Q<HelpBox>();
                    int dataRows = view.Query<VisualElement>(className: "cdn-entry-row").ToList().Count
                                   + view.Query<VisualElement>(className: "cdn-rule-row").ToList().Count;

                    Debug.Log($"[CdnTabProbe] {tab.TabName}: OnShown completed, {dataRows} data row(s)");
                    if (summary != null)
                        Debug.Log($"[CdnTabProbe] {tab.TabName}: summary = \"{summary.text}\"");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[CdnTabProbe] {tab.TabName}: threw {ex.GetType().Name}: {ex.Message}");
                    Debug.LogError(ex.StackTrace);
                    failures++;
                }
            }

            if (failures > 0)
            {
                Debug.LogError($"[CdnTabProbe] FAILURE: {failures} of {tabs.Count} tab(s) failed");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[CdnTabProbe] SUCCESS: all {tabs.Count} tab(s) built and refreshed");
            EditorApplication.Exit(0);
        }
    }
}
