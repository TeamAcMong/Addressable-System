using System;
using System.Collections.Generic;
using System.IO;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// How a catalog compares to the bundles actually sitting in the output folder — task 5.9.
    /// </summary>
    /// <remarks>
    /// The catalog and the bundle folder are published together and are assumed to agree. When they
    /// do not, the symptom on a device is a 404 mid-download with nothing in the build log to explain
    /// it, because nothing in the build checks this: Addressables writes the catalog from what it
    /// built, and whatever else is in the folder from an earlier build stays there.
    ///
    /// Three things can be wrong, and they fail differently:
    ///
    /// - <b>Missing</b> — the catalog references a bundle that is not in the folder. The build is not
    ///   publishable. Every entry behind that bundle 404s.
    /// - <b>Orphan</b> — the folder holds a bundle no catalog entry references. Harmless to players
    ///   and expensive to you: it is paid-for CDN storage, and after enough patches it is most of the
    ///   folder. Note that a bundle orphaned by *this* catalog may still be referenced by an older
    ///   catalog that installed players are running, which is why this is reported and not deleted.
    /// - <b>Size mismatch</b> — the file is there but not the size the catalog claims. This is the
    ///   one worth stopping for: it means the catalog and the bundles came from different builds.
    /// </remarks>
    public static class CatalogInspection
    {
        /// <summary>
        /// Compare a catalog against a directory of bundles.
        /// </summary>
        /// <param name="catalog">A catalog read by <see cref="CatalogReader"/>.</param>
        /// <param name="bundleDirectory">The folder the bundles were built into.</param>
        public static CdnEditorResult<CatalogInspectionReport> Compare(
            CatalogContents catalog, string bundleDirectory)
        {
            if (catalog == null)
                return CdnEditorResult<CatalogInspectionReport>.Failure("No catalog to compare.");

            if (string.IsNullOrEmpty(bundleDirectory) || !Directory.Exists(bundleDirectory))
            {
                return CdnEditorResult<CatalogInspectionReport>.Failure(
                    $"No bundle folder at '{bundleDirectory}'. Build content first, or point the " +
                    "inspector at the folder the bundles were published from.");
            }

            var onDisk = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(bundleDirectory, "*.bundle", SearchOption.AllDirectories))
                onDisk[Path.GetFileName(path)] = new FileInfo(path).Length;

            var missing = new List<BundleProblem>();
            var mismatched = new List<BundleProblem>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skippedLocal = 0;
            int skippedUnknown = 0;

            foreach (var bundle in catalog.Bundles)
            {
                // Only remote bundles belong in this folder. A local bundle ships inside the player
                // and is not expected here; reporting it as missing from the CDN would be a false
                // alarm, and on this project's own build it was eight of them at once.
                if (bundle.Location == BundleLocation.Local)
                {
                    skippedLocal++;
                    continue;
                }

                if (bundle.Location == BundleLocation.Unknown)
                {
                    // Not skipped silently. A load path this package cannot classify would otherwise
                    // turn the whole check into one that always passes.
                    skippedUnknown++;
                    continue;
                }

                string fileName = bundle.BundleName;

                if (!onDisk.TryGetValue(fileName, out long actualSize))
                {
                    missing.Add(new BundleProblem(fileName, bundle.SizeBytes, -1, bundle.DependentEntryCount));
                    continue;
                }

                matched.Add(fileName);

                // A catalog that records no size (0) is not a mismatch — it is a catalog that did not
                // record a size. Reporting that as "expected 0 bytes, found 4 MB" would be a lie in
                // the shape of a finding.
                if (bundle.SizeBytes > 0 && bundle.SizeBytes != actualSize)
                    mismatched.Add(new BundleProblem(fileName, bundle.SizeBytes, actualSize, bundle.DependentEntryCount));
            }

            var orphans = new List<BundleProblem>();
            foreach (var pair in onDisk)
            {
                if (!matched.Contains(pair.Key))
                    orphans.Add(new BundleProblem(pair.Key, -1, pair.Value, 0));
            }

            missing.Sort(CompareByName);
            mismatched.Sort(CompareByName);
            orphans.Sort(CompareByName);

            return CdnEditorResult<CatalogInspectionReport>.Success(
                new CatalogInspectionReport(
                    bundleDirectory, onDisk.Count, matched.Count,
                    missing, orphans, mismatched, skippedLocal, skippedUnknown));
        }

        private static int CompareByName(BundleProblem a, BundleProblem b)
            => string.CompareOrdinal(a.BundleFileName, b.BundleFileName);
    }

    /// <summary>The outcome of comparing one catalog to one bundle folder.</summary>
    public sealed class CatalogInspectionReport
    {
        public string BundleDirectory { get; }

        /// <summary>How many .bundle files the folder holds.</summary>
        public int BundlesOnDisk { get; }

        /// <summary>How many of them the catalog references.</summary>
        public int BundlesMatched { get; }

        /// <summary>Referenced by the catalog, absent from the folder. Publishing this build breaks those entries.</summary>
        public IReadOnlyList<BundleProblem> Missing { get; }

        /// <summary>In the folder, referenced by nothing in this catalog.</summary>
        public IReadOnlyList<BundleProblem> Orphans { get; }

        /// <summary>Present, but not the size the catalog claims.</summary>
        public IReadOnlyList<BundleProblem> SizeMismatches { get; }

        /// <summary>Bundles the catalog loads from inside the player. Not expected in this folder.</summary>
        public int LocalBundles { get; }

        /// <summary>
        /// Bundles whose load path this package could not classify, so they were not checked at all.
        /// A non-zero value means the verdict below covers less than it appears to.
        /// </summary>
        public int UncheckedBundles { get; }

        /// <summary>True when nothing would 404 and nothing came from a different build.</summary>
        public bool IsPublishable => Missing.Count == 0 && SizeMismatches.Count == 0;

        public long OrphanBytes
        {
            get
            {
                long total = 0;
                foreach (var orphan in Orphans)
                    if (orphan.ActualBytes > 0)
                        total += orphan.ActualBytes;
                return total;
            }
        }

        public CatalogInspectionReport(
            string bundleDirectory, int bundlesOnDisk, int bundlesMatched,
            IReadOnlyList<BundleProblem> missing,
            IReadOnlyList<BundleProblem> orphans,
            IReadOnlyList<BundleProblem> sizeMismatches,
            int localBundles = 0,
            int uncheckedBundles = 0)
        {
            BundleDirectory = bundleDirectory;
            BundlesOnDisk = bundlesOnDisk;
            BundlesMatched = bundlesMatched;
            Missing = missing;
            Orphans = orphans;
            SizeMismatches = sizeMismatches;
            LocalBundles = localBundles;
            UncheckedBundles = uncheckedBundles;
        }

        /// <summary>A one-line verdict for a HelpBox.</summary>
        public string Summary()
        {
            // The unchecked count is stated in every branch, including the clean one. A verdict that
            // says "everything agrees" while quietly skipping bundles it did not understand is worse
            // than no verdict.
            string caveat = UncheckedBundles > 0
                ? $" {UncheckedBundles} bundle(s) had a load path this package does not recognise and were not checked."
                : string.Empty;

            if (IsPublishable && Orphans.Count == 0)
            {
                return $"Catalog and folder agree: {BundlesMatched} remote bundle(s) matched, " +
                       $"nothing missing, nothing orphaned.{caveat}";
            }

            var parts = new List<string>();
            if (Missing.Count > 0) parts.Add($"{Missing.Count} missing");
            if (SizeMismatches.Count > 0) parts.Add($"{SizeMismatches.Count} size mismatch(es)");
            if (Orphans.Count > 0) parts.Add($"{Orphans.Count} orphaned");

            return $"{BundlesMatched} of {BundlesOnDisk} file(s) matched — "
                   + string.Join(", ", parts) + "." + caveat;
        }
    }

    /// <summary>One disagreement between a catalog and a bundle folder.</summary>
    public sealed class BundleProblem
    {
        public string BundleFileName { get; }

        /// <summary>Size the catalog claims, or -1 when the catalog does not mention this bundle at all.</summary>
        public long CatalogBytes { get; }

        /// <summary>Size on disk, or -1 when the file is not there.</summary>
        public long ActualBytes { get; }

        /// <summary>How many catalog entries depend on it. 0 for an orphan, by definition.</summary>
        public int DependentEntryCount { get; }

        public BundleProblem(string bundleFileName, long catalogBytes, long actualBytes, int dependentEntryCount)
        {
            BundleFileName = bundleFileName ?? string.Empty;
            CatalogBytes = catalogBytes;
            ActualBytes = actualBytes;
            DependentEntryCount = dependentEntryCount;
        }
    }
}
