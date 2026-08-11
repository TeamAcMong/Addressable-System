using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Outcome of a catalog verification pass.
    /// </summary>
    public sealed class CatalogVerificationResult
    {
        /// <summary>
        /// True when nothing in <see cref="Problems"/> was found. Warnings do not affect this.
        /// </summary>
        public bool Passed => Problems.Count == 0;

        /// <summary>
        /// Findings that make the output unfit to publish.
        /// </summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>
        /// Findings worth reporting that do not block publishing.
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// Number of bundles checked against the manifest.
        /// </summary>
        public int BundlesChecked { get; internal set; }
    }

    /// <summary>
    /// Verifies that a completed build's output is internally consistent and safe to publish —
    /// task 1.6.
    /// </summary>
    /// <remarks>
    /// Answers one question: if this output were uploaded exactly as it is, would a player be able
    /// to fetch everything the catalog points at?
    ///
    /// THE BUNDLE INVENTORY COMES FROM THE MANIFEST, NOT THE CATALOG.
    /// Reading it from the catalog would be the obvious design and is not possible:
    /// ContentCatalogData.LoadFromFile is internal and the package ships no InternalsVisibleTo for
    /// it, so the binary catalog cannot be parsed from outside Addressables. build-manifest.json
    /// (task 1.7) is written from the same build and is the authority instead. That makes 1.6
    /// depend on 1.7 having run — verification without a manifest is refused rather than skipped,
    /// because "nothing to check" and "everything is fine" must not look alike.
    ///
    /// SETTINGS RULES ARE NOT RESTATED HERE.
    /// The §9 contract is encoded once in SettingsContract and shared with the GUI validator tab.
    /// This calls BuildRules() and evaluates them; it does not keep its own copy.
    /// </remarks>
    public static class CatalogVerifier
    {
        /// <summary>
        /// Verify a build's output directory against its manifest and the settings contract.
        /// </summary>
        /// <param name="bundleDir">Directory holding the built bundles.</param>
        /// <param name="catalogDir">Directory holding the catalog and its .hash file.</param>
        /// <param name="manifestPath">
        /// Path to build-manifest.json. Defaults to the location BuildManifestWriter uses.
        /// </param>
        public static CatalogVerificationResult Verify(string bundleDir, string catalogDir, string manifestPath = null)
        {
            var result = new CatalogVerificationResult();
            manifestPath = string.IsNullOrEmpty(manifestPath)
                ? Path.Combine(BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName)
                : manifestPath;

            VerifySettingsContract(result);

            var manifest = LoadManifest(manifestPath, result);
            if (manifest == null)
            {
                return result;
            }

            VerifyAppVersion(manifest, result);
            VerifyCatalog(manifest, catalogDir, result);
            VerifyBundles(manifest, bundleDir, result);

            return result;
        }

        // ========== private implementation ==========

        private static void VerifySettingsContract(CatalogVerificationResult result)
        {
            foreach (var rule in SettingsContract.BuildRules())
            {
                var evaluation = rule.Evaluate();
                if (evaluation.Passed)
                {
                    continue;
                }

                string finding = $"Settings contract rule '{rule.Id}' fails: {rule.Description}. " +
                                 $"Expected {rule.ExpectedDisplay}, found {evaluation.CurrentDisplay}.";

                if (rule.IsWarningOnly)
                {
                    result.Warnings.Add(finding);
                }
                else
                {
                    result.Problems.Add(finding);
                }
            }
        }

        private static BuildManifest LoadManifest(string manifestPath, CatalogVerificationResult result)
        {
            if (!File.Exists(manifestPath))
            {
                result.Problems.Add(
                    $"No build manifest at {manifestPath}. Verification needs the inventory the build " +
                    "wrote (task 1.7); the binary catalog cannot be parsed to reconstruct it. " +
                    "Run a build first, or pass the archived manifest for the build being verified.");
                return null;
            }

            BuildManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<BuildManifest>(File.ReadAllText(manifestPath));
            }
            catch (System.Exception ex)
            {
                result.Problems.Add($"Build manifest at {manifestPath} does not parse: {ex.Message}");
                return null;
            }

            if (manifest == null)
            {
                result.Problems.Add($"Build manifest at {manifestPath} deserialized to null");
                return null;
            }

            if (manifest.bundles == null)
            {
                result.Problems.Add($"Build manifest at {manifestPath} has no bundle list");
                return null;
            }

            return manifest;
        }

        private static void VerifyAppVersion(BuildManifest manifest, CatalogVerificationResult result)
        {
            string liveVersion = PlayerSettings.bundleVersion;
            if (manifest.appVersion != liveVersion)
            {
                result.Problems.Add(
                    $"Manifest was written for app version '{manifest.appVersion}' but " +
                    $"PlayerSettings.bundleVersion is now '{liveVersion}'. Either the manifest belongs " +
                    "to a different build than the output being verified, or the version was bumped " +
                    "after the build — in which case the catalog is addressed to a version no shipped " +
                    "player is polling for.");
            }
        }

        private static void VerifyCatalog(BuildManifest manifest, string catalogDir, CatalogVerificationResult result)
        {
            if (string.IsNullOrEmpty(catalogDir) || !Directory.Exists(catalogDir))
            {
                result.Problems.Add($"Catalog directory does not exist: {catalogDir}");
                return;
            }

            if (manifest.catalog == null || string.IsNullOrEmpty(manifest.catalog.fileName))
            {
                result.Problems.Add("Manifest records no catalog file");
                return;
            }

            // The name players poll for is derived from the app version, so an otherwise valid
            // catalog under the wrong name is unreachable rather than broken.
            string expectedStem = $"catalog_{manifest.appVersion}";
            string actualStem = Path.GetFileNameWithoutExtension(manifest.catalog.fileName);
            if (actualStem != expectedStem)
            {
                result.Problems.Add(
                    $"Catalog is named '{manifest.catalog.fileName}', expected '{expectedStem}.bin' " +
                    $"(or .json) for app version '{manifest.appVersion}'. Players resolve the catalog " +
                    "URL from their own version, so they would never request this name.");
            }

            VerifyFileMatchesRecord(
                Path.Combine(catalogDir, manifest.catalog.fileName),
                manifest.catalog.sizeBytes,
                manifest.catalog.contentHash,
                "Catalog",
                result);

            if (string.IsNullOrEmpty(manifest.catalog.hashFileName))
            {
                result.Problems.Add("Manifest records no catalog .hash file; clients poll it to detect new content");
                return;
            }

            VerifyFileMatchesRecord(
                Path.Combine(catalogDir, manifest.catalog.hashFileName),
                manifest.catalog.hashFileSizeBytes,
                manifest.catalog.hashFileContentHash,
                "Catalog hash file",
                result);
        }

        private static void VerifyBundles(BuildManifest manifest, string bundleDir, CatalogVerificationResult result)
        {
            if (string.IsNullOrEmpty(bundleDir) || !Directory.Exists(bundleDir))
            {
                result.Problems.Add($"Bundle directory does not exist: {bundleDir}");
                return;
            }

            foreach (var bundle in manifest.bundles)
            {
                if (bundle == null || string.IsNullOrEmpty(bundle.fileName))
                {
                    result.Problems.Add("Manifest contains a bundle entry with no file name");
                    continue;
                }

                result.BundlesChecked++;
                VerifyFileMatchesRecord(
                    Path.Combine(bundleDir, bundle.fileName),
                    bundle.sizeBytes,
                    bundle.contentHash,
                    $"Bundle '{bundle.fileName}'",
                    result);
            }

            // Files on disk the manifest does not know about. Not fatal — the manifest is the
            // authority on what gets published, so an extra file is simply not published — but it
            // usually means output from an earlier build was left behind, which inflates the upload
            // and confuses later diffs.
            var manifestNames = new HashSet<string>(manifest.bundles
                .Where(b => b != null && !string.IsNullOrEmpty(b.fileName))
                .Select(b => b.fileName));

            foreach (string file in Directory.GetFiles(bundleDir, "*.bundle", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (!manifestNames.Contains(fileName))
                {
                    result.Warnings.Add(
                        $"Bundle on disk but not in the manifest: {fileName}. Left over from an earlier " +
                        "build; it will not be published and will not be verified.");
                }
            }
        }

        private static void VerifyFileMatchesRecord(
            string path,
            long expectedSize,
            string expectedHash,
            string label,
            CatalogVerificationResult result)
        {
            if (!File.Exists(path))
            {
                // This is the exit criterion: deleting a bundle from the output must fail the build.
                result.Problems.Add($"{label} is missing from the output: {path}");
                return;
            }

            long actualSize = new FileInfo(path).Length;
            if (actualSize != expectedSize)
            {
                result.Problems.Add(
                    $"{label} size mismatch: manifest says {expectedSize:N0} B, on disk {actualSize:N0} B");
                return;
            }

            if (string.IsNullOrEmpty(expectedHash))
            {
                result.Warnings.Add($"{label} has no recorded content hash; only its size was checked");
                return;
            }

            string actualHash = BuildManifestWriter.ComputeSha256(path);
            if (actualHash != expectedHash)
            {
                result.Problems.Add(
                    $"{label} content hash mismatch: manifest {expectedHash}, on disk {actualHash}. " +
                    "Same size, different bytes — the file was modified or replaced after the build.");
            }
        }
    }
}
