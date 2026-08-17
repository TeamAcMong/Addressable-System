using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.AddressableAssets.Build;
using UnityEngine;
// UnityEngine and System.Diagnostics both define Debug; the alias picks Unity's so log lines
// reach the Editor console and the batchmode log file.
using Debug = UnityEngine.Debug;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Populates and writes <see cref="BuildManifest"/> to disk after a content build — task 1.7.
    /// </summary>
    /// <remarks>
    /// The manifest is the hand-off between the build and everything downstream: CI knows what to
    /// upload and can verify integrity afterwards, task 1.6 (CatalogVerifier) gets an authoritative
    /// bundle inventory, and task 1.11 (ContentDiff) gets content hashes to compare across builds.
    ///
    /// SERIALIZER: JsonUtility, not Newtonsoft.
    /// BuildManifest.cs was written with a comment claiming Newtonsoft was required because
    /// "JsonUtility cannot serialize complex nested types". Both halves are wrong. Newtonsoft is not
    /// in this project at all — com.unity.nuget.newtonsoft-json is absent from packages-lock.json,
    /// absent from the package cache, and not a transitive dependency of Addressables 2.9.1, whose
    /// only dependency of that kind is com.unity.modules.jsonserialize. And JsonUtility handles
    /// exactly the shape BuildManifest has: public fields, a nested [Serializable] class, and a
    /// List of a [Serializable] class. Its actual limits are dictionaries, polymorphism, and
    /// top-level arrays, none of which appear here.
    ///
    /// CONTENT HASHES ARE COMPUTED FROM FILE BYTES.
    /// BundleBuildResult.Hash is recorded as unityHash for traceability but is not used for
    /// comparison: it is an internal value whose meaning is unspecified, and it is only assigned
    /// when the bundle resolves to a catalog location (BuildScriptPackedMode.cs:1461-1463), so it
    /// can legitimately be empty. The authoritative contentHash is SHA256 over the bytes on disk,
    /// which is the only thing that answers "must the player download this again".
    /// </remarks>
    public static class BuildManifestWriter
    {
        /// <summary>
        /// File name of the manifest.
        /// </summary>
        public const string ManifestFileName = "build-manifest.json";

        /// <summary>
        /// Directory the manifest is written to, relative to the project root.
        /// </summary>
        /// <remarks>
        /// The root of the output tree, deliberately ABOVE the per-platform folders that CI uploads
        /// (CdnProfileManager.RemoteBundlePath.BuildPathValue is ServerData/[BuildTarget]/bundles,
        /// catalogs are ServerData/[BuildTarget]/catalog/[version]). A CI step that syncs
        /// ServerData/&lt;platform&gt;/ therefore cannot pick the manifest up by accident — it
        /// records internal build metadata and git SHAs and is archived privately, never published.
        ///
        /// The design doc gives four contradictory locations for this file; this is the one that was
        /// settled on. The doc has not been corrected yet.
        /// </remarks>
        public const string OutputRootDir = "ServerData";

        /// <summary>
        /// Schema version written into the manifest. Bump on breaking structural changes.
        /// </summary>
        private const string CurrentManifestVersion = "1.0";

        /// <summary>
        /// Build and write the manifest for a completed content build.
        /// </summary>
        /// <param name="result">The build result. Used for Unity's own hash/CRC values only.</param>
        /// <param name="bundleDir">Resolved directory containing the built bundles.</param>
        /// <param name="catalogDir">Resolved directory containing the catalog and its .hash file.</param>
        /// <param name="isUpdateBuild">True for a content update, false for a full build.</param>
        /// <param name="outputDir">Directory to write into. Defaults to <see cref="OutputRootDir"/>.</param>
        /// <returns>Success with the written path; Failure with the reason.</returns>
        public static CdnEditorResult<string> Write(
            AddressablesPlayerBuildResult result,
            string bundleDir,
            string catalogDir,
            bool isUpdateBuild,
            string outputDir = null)
        {
            if (result == null)
            {
                return CdnEditorResult<string>.Failure("Build result is null; nothing to describe");
            }

            if (string.IsNullOrEmpty(bundleDir) || !Directory.Exists(bundleDir))
            {
                return CdnEditorResult<string>.Failure($"Bundle directory does not exist: {bundleDir}");
            }

            if (string.IsNullOrEmpty(catalogDir) || !Directory.Exists(catalogDir))
            {
                return CdnEditorResult<string>.Failure($"Catalog directory does not exist: {catalogDir}");
            }

            outputDir = string.IsNullOrEmpty(outputDir) ? OutputRootDir : outputDir;

            try
            {
                var manifest = new BuildManifest
                {
                    manifestVersion = CurrentManifestVersion,
                    appVersion = PlayerSettings.bundleVersion,
                    platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    buildType = isUpdateBuild ? "Update" : "Full",
                    buildDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    gitSha = ResolveGitSha()
                };

                var catalogResult = DescribeCatalog(catalogDir, manifest.catalog);
                if (catalogResult.IsFailure)
                {
                    return CdnEditorResult<string>.Failure(catalogResult.ErrorMessage);
                }

                manifest.bundles = DescribeBundles(bundleDir, result);
                manifest.totalSizeBytes = manifest.bundles.Sum(b => b.sizeBytes);

                Directory.CreateDirectory(outputDir);
                string manifestPath = Path.Combine(outputDir, ManifestFileName);

                // Task 1.11: diff against the manifest this one is about to replace. The previous
                // build's manifest is sitting at exactly this path until the write below, so no
                // separate bookkeeping is needed — but it must be read BEFORE the overwrite.
                DescribePatch(manifestPath, manifest);

                File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));

                // Verify by reading back, not by trusting the write. A manifest that cannot be
                // parsed is worse than none: CI would upload against it and 1.6 would verify
                // against it.
                var verification = VerifyWritten(manifestPath, manifest);
                if (verification.IsFailure)
                {
                    return CdnEditorResult<string>.Failure(verification.ErrorMessage);
                }

                return CdnEditorResult<string>.Success(manifestPath);
            }
            catch (Exception ex)
            {
                return CdnEditorResult<string>.Failure($"Failed to write build manifest: {ex.Message}");
            }
        }

        // ========== private implementation ==========

        private static CdnEditorResult<bool> DescribeCatalog(string catalogDir, CatalogInfo catalog)
        {
            // AddressablesPlayerBuildResult.RemoteCatalogJsonFilePath and RemoteCatalogHashFilePath
            // look like exactly the right source and are public and correctly typed, but they are
            // never assigned anywhere in Addressables 2.9.1 — they are always null. The catalog is
            // located on disk instead.
            string[] catalogFiles = Directory.GetFiles(catalogDir, "catalog_*.bin")
                .Concat(Directory.GetFiles(catalogDir, "catalog_*.json"))
                .ToArray();

            if (catalogFiles.Length == 0)
            {
                return CdnEditorResult<bool>.Failure($"No catalog file found in {catalogDir}");
            }

            if (catalogFiles.Length > 1)
            {
                return CdnEditorResult<bool>.Failure(
                    $"Expected one catalog in {catalogDir}, found {catalogFiles.Length}: " +
                    $"{string.Join(", ", catalogFiles.Select(Path.GetFileName))}. " +
                    "Two catalogs in one version folder means an earlier build wrote a version this " +
                    "build did not overwrite; the manifest cannot say which one clients should fetch.");
            }

            string catalogPath = catalogFiles[0];
            catalog.fileName = Path.GetFileName(catalogPath);
            catalog.sizeBytes = new FileInfo(catalogPath).Length;
            catalog.contentHash = ComputeSha256(catalogPath);

            string hashPath = Path.Combine(
                catalogDir,
                Path.GetFileNameWithoutExtension(catalogPath) + ".hash");

            if (!File.Exists(hashPath))
            {
                return CdnEditorResult<bool>.Failure(
                    $"Catalog hash file missing: {hashPath}. Clients poll this file to detect new " +
                    "content, so a catalog without one is unreachable.");
            }

            catalog.hashFileName = Path.GetFileName(hashPath);
            catalog.hashFileSizeBytes = new FileInfo(hashPath).Length;
            catalog.hashFileContentHash = ComputeSha256(hashPath);

            return CdnEditorResult<bool>.Success(true);
        }

        private static List<BundleInfo> DescribeBundles(string bundleDir, AddressablesPlayerBuildResult result)
        {
            // Index Unity's own values by file name so they can be attached where available.
            // AssetBundleBuildResults is populated per built bundle (BuildScriptPackedMode.cs:1532),
            // but on a content update only the rebuilt bundles appear, while the output directory
            // still holds every bundle from earlier builds — those must stay listed, because
            // players on older catalogs still fetch them.
            var unityResults = new Dictionary<string, AddressablesPlayerBuildResult.BundleBuildResult>();
            foreach (var bundleResult in result.AssetBundleBuildResults)
            {
                if (bundleResult == null || string.IsNullOrEmpty(bundleResult.FilePath))
                {
                    continue;
                }

                unityResults[Path.GetFileName(bundleResult.FilePath)] = bundleResult;
            }

            var bundles = new List<BundleInfo>();
            string[] files = Directory.GetFiles(bundleDir, "*.bundle", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal); // stable order so manifests diff cleanly

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                var info = new BundleInfo
                {
                    fileName = fileName,
                    sizeBytes = new FileInfo(file).Length,
                    contentHash = ComputeSha256(file)
                };

                if (unityResults.TryGetValue(fileName, out var unityResult))
                {
                    info.name = string.IsNullOrEmpty(unityResult.InternalBundleName)
                        ? Path.GetFileNameWithoutExtension(fileName)
                        : unityResult.InternalBundleName;
                    info.unityHash = unityResult.Hash ?? string.Empty;
                    info.unityCrc = unityResult.Crc;
                }
                else
                {
                    // Carried over from an earlier build: still part of what the CDN must serve.
                    info.name = Path.GetFileNameWithoutExtension(fileName);
                }

                bundles.Add(info);
            }

            return bundles;
        }

        /// <summary>
        /// Fill in <see cref="BuildManifest.patch"/> by comparing against the manifest currently at
        /// <paramref name="previousManifestPath"/>, if there is one.
        /// </summary>
        /// <remarks>
        /// A missing or unreadable previous manifest leaves patch.available false rather than
        /// producing zeroes. Zeroes would read as "nothing to download", which is the opposite of
        /// what a first build means.
        /// </remarks>
        private static void DescribePatch(string previousManifestPath, BuildManifest current)
        {
            if (!File.Exists(previousManifestPath))
            {
                return;
            }

            var previous = ContentDiff.Load(previousManifestPath);
            if (previous.IsFailure)
            {
                Debug.LogWarning($"[BuildManifestWriter] Previous manifest could not be read, so this build " +
                                 $"records no patch size: {previous.ErrorMessage}");
                return;
            }

            ContentDiffResult diff;
            try
            {
                diff = ContentDiff.Compare(previous.Value, current);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BuildManifestWriter] Could not diff against the previous manifest: {ex.Message}");
                return;
            }

            current.patch = new PatchInfo
            {
                available = true,
                comparedToBuildDate = previous.Value.buildDate ?? string.Empty,
                comparedToGitSha = previous.Value.gitSha ?? string.Empty,
                newBundleCount = diff.NewBundles.Count,
                changedBundleCount = diff.ChangedBundles.Count,
                removedBundleCount = diff.RemovedBundles.Count,
                unchangedBundleCount = diff.UnchangedCount,
                catalogChanged = diff.CatalogChanged,
                patchSizeBytes = diff.PatchSizeBytes
            };
        }

        private static CdnEditorResult<bool> VerifyWritten(string manifestPath, BuildManifest expected)
        {
            if (!File.Exists(manifestPath))
            {
                return CdnEditorResult<bool>.Failure(
                    $"Manifest reported written but does not exist at {manifestPath}");
            }

            BuildManifest reloaded;
            try
            {
                reloaded = JsonUtility.FromJson<BuildManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                return CdnEditorResult<bool>.Failure($"Manifest at {manifestPath} does not parse: {ex.Message}");
            }

            if (reloaded == null)
            {
                return CdnEditorResult<bool>.Failure($"Manifest at {manifestPath} deserialized to null");
            }

            if (reloaded.bundles == null || reloaded.bundles.Count != expected.bundles.Count)
            {
                return CdnEditorResult<bool>.Failure(
                    $"Manifest round-trip lost bundles: wrote {expected.bundles.Count}, " +
                    $"read back {reloaded.bundles?.Count ?? 0}");
            }

            if (reloaded.catalog == null || reloaded.catalog.fileName != expected.catalog.fileName)
            {
                return CdnEditorResult<bool>.Failure("Manifest round-trip lost the catalog entry");
            }

            return CdnEditorResult<bool>.Success(true);
        }

        /// <summary>
        /// SHA256 of a file's bytes, formatted "sha256:&lt;hex&gt;".
        /// </summary>
        /// <remarks>
        /// Public so CatalogVerifier (task 1.6) recomputes hashes exactly the way the manifest
        /// recorded them. Two implementations of this would compare equal until one of them
        /// changed the prefix or the casing, and then every verification would fail for a reason
        /// unrelated to the content.
        /// </remarks>
        public static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha256.ComputeHash(stream);
                var builder = new System.Text.StringBuilder(hash.Length * 2 + 7);
                builder.Append("sha256:");
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        /// <summary>
        /// Resolve the git commit this build came from. Empty string when unavailable — a local
        /// build without git is normal and must not fail the build.
        /// </summary>
        /// <remarks>
        /// CI environment variables are checked first: on a CI checkout the runner already knows the
        /// SHA, and asking git can return the merge commit rather than the head of the branch.
        /// </remarks>
        private static string ResolveGitSha()
        {
            string[] ciVariables = { "GITHUB_SHA", "GIT_COMMIT", "CI_COMMIT_SHA", "GIT_SHA" };
            foreach (string variable in ciVariables)
            {
                string value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(value))
                {
                    return value.Length > 12 ? value.Substring(0, 12) : value;
                }
            }

            try
            {
                var startInfo = new ProcessStartInfo("git", "rev-parse --short=12 HEAD")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return string.Empty;
                    }

                    string output = process.StandardOutput.ReadToEnd().Trim();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        return string.Empty;
                    }

                    return process.ExitCode == 0 ? output : string.Empty;
                }
            }
            catch (Exception ex)
            {
                // git absent from PATH, or not a repository. Not a build failure.
                Debug.Log($"[BuildManifestWriter] Could not resolve git SHA ({ex.Message}); leaving it empty");
                return string.Empty;
            }
        }
    }
}
