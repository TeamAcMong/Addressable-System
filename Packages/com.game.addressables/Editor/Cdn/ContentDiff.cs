using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// One bundle that exists in both builds but with different content.
    /// </summary>
    public sealed class BundleChange
    {
        /// <summary>File name, identical in both builds.</summary>
        public string FileName;

        /// <summary>Size in the baseline build.</summary>
        public long PreviousSizeBytes;

        /// <summary>Size in the current build.</summary>
        public long CurrentSizeBytes;
    }

    /// <summary>
    /// What changed between two builds, and what it costs a player on the older one.
    /// </summary>
    public sealed class ContentDiffResult
    {
        /// <summary>Bundles present only in the current build.</summary>
        public List<BundleInfo> NewBundles { get; } = new List<BundleInfo>();

        /// <summary>Bundles present in both builds with a different content hash.</summary>
        public List<BundleChange> ChangedBundles { get; } = new List<BundleChange>();

        /// <summary>Bundles present only in the baseline build.</summary>
        public List<BundleInfo> RemovedBundles { get; } = new List<BundleInfo>();

        /// <summary>Bundles byte-identical in both builds.</summary>
        public int UnchangedCount { get; internal set; }

        /// <summary>True when the catalog's content hash differs.</summary>
        public bool CatalogChanged { get; internal set; }

        /// <summary>
        /// Bytes a player on the baseline build must download: the full size of every new bundle
        /// plus every changed one, plus the catalog and its hash file when the catalog changed.
        /// </summary>
        /// <remarks>
        /// Full sizes, not size deltas. Bundles are fetched whole — there is no byte-range patching
        /// — so a bundle rebuilt with different content at an identical size still costs its entire
        /// size on the wire. Measuring the delta would report zero for it, which is the common case
        /// for a content update and would make the Phase 1 exit criterion trivially satisfiable.
        ///
        /// Removed bundles cost nothing: a player that already has them simply stops using them.
        /// </remarks>
        public long PatchSizeBytes { get; internal set; }

        /// <summary>Human-readable one-liner for logs and the Build tab header.</summary>
        public override string ToString()
        {
            return $"{NewBundles.Count} new, {ChangedBundles.Count} changed, {RemovedBundles.Count} removed, " +
                   $"{UnchangedCount} unchanged, patch {PatchSizeBytes:N0} B";
        }
    }

    /// <summary>
    /// Compares two build manifests — task 1.11.
    /// </summary>
    /// <remarks>
    /// Works from build-manifest.json (task 1.7) rather than the catalogs, for the same reason
    /// CatalogVerifier does: ContentCatalogData.LoadFromFile is internal and the package ships no
    /// InternalsVisibleTo, so a binary catalog cannot be read from outside Addressables.
    ///
    /// Comparison is by contentHash — SHA256 over the bytes on disk — never by Unity's internal
    /// hash or by size. On a content update Addressables rebuilds bundles whose assets did not
    /// change, and the rebuilt file is usually byte-identical; a size or timestamp comparison would
    /// call those changed and inflate every patch estimate. See BuildManifest.contentHash.
    /// </remarks>
    public static class ContentDiff
    {
        /// <summary>
        /// Compare a baseline manifest against a current one.
        /// </summary>
        /// <param name="previous">The build the player already has.</param>
        /// <param name="current">The build being published.</param>
        public static ContentDiffResult Compare(BuildManifest previous, BuildManifest current)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (current == null) throw new ArgumentNullException(nameof(current));

            var result = new ContentDiffResult();

            var previousByName = (previous.bundles ?? new List<BundleInfo>())
                .Where(b => b != null && !string.IsNullOrEmpty(b.fileName))
                .GroupBy(b => b.fileName)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var currentBundles = (current.bundles ?? new List<BundleInfo>())
                .Where(b => b != null && !string.IsNullOrEmpty(b.fileName))
                .ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var bundle in currentBundles.OrderBy(b => b.fileName, StringComparer.Ordinal))
            {
                seen.Add(bundle.fileName);

                if (!previousByName.TryGetValue(bundle.fileName, out var previousBundle))
                {
                    result.NewBundles.Add(bundle);
                    result.PatchSizeBytes += bundle.sizeBytes;
                    continue;
                }

                if (previousBundle.contentHash == bundle.contentHash)
                {
                    result.UnchangedCount++;
                    continue;
                }

                result.ChangedBundles.Add(new BundleChange
                {
                    FileName = bundle.fileName,
                    PreviousSizeBytes = previousBundle.sizeBytes,
                    CurrentSizeBytes = bundle.sizeBytes
                });
                result.PatchSizeBytes += bundle.sizeBytes;
            }

            foreach (var pair in previousByName.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!seen.Contains(pair.Key))
                    result.RemovedBundles.Add(pair.Value);
            }

            string previousCatalogHash = previous.catalog?.contentHash;
            string currentCatalogHash = current.catalog?.contentHash;
            result.CatalogChanged = previousCatalogHash != currentCatalogHash;

            if (result.CatalogChanged && current.catalog != null)
            {
                // Clients fetch the catalog and its hash file on every boot when content changed,
                // so they are part of what the patch costs.
                result.PatchSizeBytes += current.catalog.sizeBytes + current.catalog.hashFileSizeBytes;
            }

            return result;
        }

        /// <summary>
        /// Compare two manifests on disk.
        /// </summary>
        public static CdnEditorResult<ContentDiffResult> CompareFiles(string previousPath, string currentPath)
        {
            var previous = Load(previousPath);
            if (previous.IsFailure)
                return CdnEditorResult<ContentDiffResult>.Failure(previous.ErrorMessage);

            var current = Load(currentPath);
            if (current.IsFailure)
                return CdnEditorResult<ContentDiffResult>.Failure(current.ErrorMessage);

            try
            {
                return CdnEditorResult<ContentDiffResult>.Success(Compare(previous.Value, current.Value));
            }
            catch (Exception ex)
            {
                return CdnEditorResult<ContentDiffResult>.Failure($"Could not compare manifests: {ex.Message}");
            }
        }

        /// <summary>
        /// Read a manifest from disk.
        /// </summary>
        public static CdnEditorResult<BuildManifest> Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return CdnEditorResult<BuildManifest>.Failure($"No build manifest at {path}");

            try
            {
                var manifest = JsonUtility.FromJson<BuildManifest>(File.ReadAllText(path));
                if (manifest == null)
                    return CdnEditorResult<BuildManifest>.Failure($"Build manifest at {path} deserialized to null");

                return CdnEditorResult<BuildManifest>.Success(manifest);
            }
            catch (Exception ex)
            {
                return CdnEditorResult<BuildManifest>.Failure($"Build manifest at {path} does not parse: {ex.Message}");
            }
        }
    }
}
