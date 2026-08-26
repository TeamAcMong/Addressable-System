using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Batchmode form of the Catalog Inspector — task 5.9.
    /// </summary>
    /// <remarks>
    /// The tab answers the question for a person; this answers it for CI, before an upload. A build
    /// whose catalog references a bundle that is not in the folder is not publishable, and that is
    /// worth finding in a pipeline step rather than in a player's crash report.
    ///
    /// Exit codes: 0 publishable, 1 not. Orphans do not fail the run — they cost storage, not
    /// players, and a bundle orphaned by the newest catalog is often still needed by an older one
    /// that installed players are running.
    ///
    /// <code>
    /// Unity -batchmode -quit -projectPath . \
    ///   -executeMethod AddressableManager.Editor.Cdn.CatalogInspectCLI.Inspect
    /// </code>
    ///
    /// Override the paths with <c>-catalogPath &lt;file&gt;</c> and <c>-bundleDir &lt;dir&gt;</c>;
    /// with neither, the active Addressables profile decides.
    /// </remarks>
    public static class CatalogInspectCLI
    {
        public static void Inspect()
        {
            if (!BatchmodeGate.MayExit("CatalogInspect")) return;

            // Invariant: a CLI must not report on an assembly that did not build. Unity exits 0 from
            // -executeMethod when compilation failed and the method never ran, so this gate is the
            // only thing standing between a broken build and a green pipeline step.
            if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogError("[CatalogInspect] FAILURE: script compilation failed");
                EditorApplication.Exit(1);
                return;
            }

            string catalogPath = ReadArgument("-catalogPath");
            string bundleDirectory = ReadArgument("-bundleDir");

            var settings = AddressableAssetSettingsDefaultObject.Settings;

            if (string.IsNullOrEmpty(bundleDirectory))
            {
                if (settings == null)
                {
                    Debug.LogError("[CatalogInspect] FAILURE: no Addressables settings and no -bundleDir given");
                    EditorApplication.Exit(1);
                    return;
                }

                bundleDirectory = CdnBuildPipeline.ResolveBundleDir(settings);
            }

            if (string.IsNullOrEmpty(catalogPath))
            {
                var found = FindCatalogs(settings, bundleDirectory);
                if (found.Count == 0)
                {
                    Debug.LogError("[CatalogInspect] FAILURE: no catalog found. Build content first, " +
                                   "or pass -catalogPath.");
                    EditorApplication.Exit(1);
                    return;
                }

                if (found.Count > 1)
                {
                    // Picking one silently would make the result depend on directory order, and the
                    // whole point of this step is a result you can act on.
                    Debug.LogError($"[CatalogInspect] FAILURE: {found.Count} catalogs found; pass " +
                                   "-catalogPath to say which one:\n  " + string.Join("\n  ", found));
                    EditorApplication.Exit(1);
                    return;
                }

                catalogPath = found[0];
            }

            Debug.Log($"[CatalogInspect] catalog: {catalogPath}");
            Debug.Log($"[CatalogInspect] bundles: {bundleDirectory}");

            var read = CatalogReader.Read(catalogPath);
            if (read.IsFailure)
            {
                Debug.LogError($"[CatalogInspect] FAILURE: {read.ErrorMessage}");
                EditorApplication.Exit(1);
                return;
            }

            var catalog = read.Value;
            Debug.Log($"[CatalogInspect] {catalog.Entries.Count} entries, {catalog.Bundles.Count} bundles");

            foreach (var bundle in catalog.Bundles)
            {
                Debug.Log($"[CatalogInspect]   [{bundle.Location}] {bundle.BundleName} " +
                          $"({bundle.SizeBytes} B, {bundle.DependentEntryCount} entries) -> {bundle.InternalId}");
            }

            var crossing = catalog.FindRemoteEntriesNeedingLocalBundles();
            if (crossing.Count == 0)
            {
                Debug.Log("[CatalogInspect] no remote entry depends on an in-player bundle — remote " +
                          "content is self-contained and can be built separately from the player");
            }
            else
            {
                Debug.LogWarning(
                    $"[CatalogInspect] {crossing.Count} remote entr(ies) depend on bundles shipped " +
                    "inside the player. Remote content and the player must then come from the SAME " +
                    "content build; building them on different machines resolves these to bundles " +
                    "the app does not have.");

                foreach (var entry in crossing)
                    Debug.LogWarning($"[CatalogInspect] CROSSES  {entry.Address} -> {string.Join(", ", entry.LocalBundles)}");
            }

            var compared = CatalogInspection.Compare(catalog, bundleDirectory);
            if (compared.IsFailure)
            {
                Debug.LogError($"[CatalogInspect] FAILURE: {compared.ErrorMessage}");
                EditorApplication.Exit(1);
                return;
            }

            var report = compared.Value;
            Debug.Log($"[CatalogInspect] {report.Summary()}");

            foreach (var problem in report.Missing)
                Debug.LogError($"[CatalogInspect] MISSING  {problem.BundleFileName} ({problem.DependentEntryCount} entries need it)");

            foreach (var problem in report.SizeMismatches)
                Debug.LogError($"[CatalogInspect] SIZE     {problem.BundleFileName} catalog={problem.CatalogBytes} disk={problem.ActualBytes}");

            foreach (var problem in report.Orphans)
                Debug.LogWarning($"[CatalogInspect] ORPHAN   {problem.BundleFileName} ({problem.ActualBytes} B)");

            if (report.UncheckedBundles > 0)
            {
                Debug.LogWarning(
                    $"[CatalogInspect] {report.UncheckedBundles} bundle(s) had a load path this package " +
                    "does not recognise and were NOT checked. A pass here covers less than it looks like.");
            }

            if (!report.IsPublishable)
            {
                Debug.LogError("[CatalogInspect] FAILURE: this output is not publishable");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[CatalogInspect] SUCCESS: catalog and bundle folder agree");
            EditorApplication.Exit(0);
        }

        private static List<string> FindCatalogs(
            UnityEditor.AddressableAssets.Settings.AddressableAssetSettings settings, string bundleDirectory)
        {
            var found = new List<string>();

            if (settings != null)
                Collect(CdnBuildPipeline.ResolveCatalogDir(settings), found);

            Collect(bundleDirectory, found);
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        private static void Collect(string directory, List<string> into)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            foreach (string pattern in new[] { "catalog*.bin", "catalog*.json" })
            {
                foreach (string path in Directory.GetFiles(directory, pattern, SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(path);
                    if (!into.Contains(full))
                        into.Add(full);
                }
            }
        }

        private static string ReadArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }
    }
}
