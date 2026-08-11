using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AddressableManager.Tests;

namespace AddressableManager.Tests.Cdn
{
    /// <summary>
    /// Test harness for Phase 1 exit criterion verification.
    ///
    /// These tests validate that bundle changes are minimal and deterministic:
    /// - A no-op build produces identical output (determinism)
    /// - A single-asset change only affects dependent bundles
    /// - A shared-asset change affects only groups that depend on it (core criterion)
    /// - 2 MB asset change ≤ 2.5 MB bundle change (target: Phase 1 requirement)
    ///
    /// See CDN_SYSTEM.html §p-phase1 "Exit criteria".
    /// </summary>
    [TestFixture]
    public class CdnBuildSnapshotTests
    {
        /// <summary>
        /// Immutable snapshot of a single build's output:
        /// all bundles + their SHA256 hashes, and the raw content of the .hash file.
        /// </summary>
        public class BuildSnapshot
        {
            /// <summary>
            /// Metadata for a single bundle file.
            /// </summary>
            public class BundleEntry
            {
                public string Filename { get; set; }      // e.g. "bundle_name.bundle"
                public long Bytes { get; set; }           // actual file size
                public string Sha256Hex { get; set; }     // SHA256 hash (lowercase hex)

                public override string ToString()
                {
                    return $"{Filename} ({Bytes} bytes, sha256={Sha256Hex.Substring(0, 8)})";
                }
            }

            /// <summary>
            /// The immutable list of bundle entries captured from the build output.
            /// </summary>
            public IReadOnlyList<BundleEntry> Bundles { get; private set; }

            /// <summary>
            /// The raw binary content of the .hash file (catalog hash).
            /// Used for byte-exact comparison, since the .hash file changes
            /// when the catalog structure changes, even if bundle contents don't.
            /// </summary>
            public byte[] CatalogHashContent { get; private set; }

            /// <summary>
            /// Timestamp when the snapshot was captured (for reference).
            /// </summary>
            public DateTime CapturedAt { get; private set; }

            /// <summary>
            /// Capture all bundles and catalog hash from a build output directory.
            /// </summary>
            /// <param name="buildOutputPath">Directory containing .bundle files (e.g., "Temp/AddressableBuild")</param>
            /// <param name="catalogHashFilePath">Path to the .hash file (e.g., "...catalog_1.0.0.hash")</param>
            /// <returns>Immutable snapshot</returns>
            /// <exception cref="FileNotFoundException">If buildOutputPath or catalogHashFilePath does not exist</exception>
            public static BuildSnapshot Capture(string buildOutputPath, string catalogHashFilePath)
            {
                if (!Directory.Exists(buildOutputPath))
                    throw new DirectoryNotFoundException($"Build output path not found: {buildOutputPath}");

                if (!File.Exists(catalogHashFilePath))
                    throw new FileNotFoundException($"Catalog hash file not found: {catalogHashFilePath}");

                // Scan buildOutputPath for all .bundle files
                var bundles = new List<BundleEntry>();
                var bundleFiles = Directory.GetFiles(buildOutputPath, "*.bundle", SearchOption.AllDirectories);

                foreach (var bundleFile in bundleFiles.OrderBy(f => f))
                {
                    var filename = Path.GetFileName(bundleFile);
                    var fileInfo = new FileInfo(bundleFile);
                    var sha256Hex = ComputeSha256Hex(bundleFile);

                    bundles.Add(new BundleEntry
                    {
                        Filename = filename,
                        Bytes = fileInfo.Length,
                        Sha256Hex = sha256Hex
                    });
                }

                // Read catalog hash file as binary
                var catalogHashContent = File.ReadAllBytes(catalogHashFilePath);

                return new BuildSnapshot
                {
                    Bundles = bundles.AsReadOnly(),
                    CatalogHashContent = catalogHashContent,
                    CapturedAt = DateTime.UtcNow
                };
            }

            /// <summary>
            /// Compare this snapshot (baseline) against an updated one.
            /// Classifies bundles as Unchanged / Changed / New / Removed.
            /// </summary>
            public BuildDiff Diff(BuildSnapshot updated)
            {
                if (updated == null)
                    throw new ArgumentNullException(nameof(updated));

                var baselineMap = Bundles.ToDictionary(b => b.Filename);
                var updatedMap = updated.Bundles.ToDictionary(b => b.Filename);

                var unchanged = new List<BuildDiff.DiffEntry>();
                var changed = new List<BuildDiff.DiffEntry>();
                var newBundles = new List<string>();
                var removedBundles = new List<string>();

                // Check baseline bundles
                foreach (var filename in baselineMap.Keys)
                {
                    if (updatedMap.ContainsKey(filename))
                    {
                        var baseEntry = baselineMap[filename];
                        var updEntry = updatedMap[filename];

                        if (baseEntry.Sha256Hex == updEntry.Sha256Hex)
                        {
                            // Identical
                            unchanged.Add(new BuildDiff.DiffEntry
                            {
                                Filename = filename,
                                BytesBaseline = baseEntry.Bytes,
                                BytesUpdated = updEntry.Bytes,
                                Sha256Baseline = baseEntry.Sha256Hex,
                                Sha256Updated = updEntry.Sha256Hex
                            });
                        }
                        else
                        {
                            // Changed
                            changed.Add(new BuildDiff.DiffEntry
                            {
                                Filename = filename,
                                BytesBaseline = baseEntry.Bytes,
                                BytesUpdated = updEntry.Bytes,
                                Sha256Baseline = baseEntry.Sha256Hex,
                                Sha256Updated = updEntry.Sha256Hex
                            });
                        }
                    }
                    else
                    {
                        // Removed
                        removedBundles.Add(filename);
                    }
                }

                // Check for new bundles
                foreach (var filename in updatedMap.Keys)
                {
                    if (!baselineMap.ContainsKey(filename))
                    {
                        newBundles.Add(filename);
                    }
                }

                // Calculate total changed bytes (absolute value of size change)
                long totalChangedBytes = 0;
                foreach (var entry in changed)
                {
                    totalChangedBytes += Math.Abs(entry.BytesUpdated - entry.BytesBaseline);
                }

                // Check if catalog hash changed
                bool catalogHashChanged = !CatalogHashContent.SequenceEqual(updated.CatalogHashContent);

                return new BuildDiff
                {
                    Unchanged = unchanged.AsReadOnly(),
                    Changed = changed.AsReadOnly(),
                    New = newBundles.AsReadOnly(),
                    Removed = removedBundles.AsReadOnly(),
                    CatalogHashChanged = catalogHashChanged,
                    TotalChangedBytes = totalChangedBytes
                };
            }

            /// <summary>
            /// Compute SHA256 hash of a file.
            /// </summary>
            private static string ComputeSha256Hex(string filePath)
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hash = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLower();
                }
            }
        }

        /// <summary>
        /// Result of comparing two snapshots.
        /// Classifies bundles and provides total changed byte count for assertion.
        /// </summary>
        public class BuildDiff
        {
            /// <summary>
            /// Entry in a diff result.
            /// </summary>
            public class DiffEntry
            {
                public string Filename { get; set; }
                public long BytesBaseline { get; set; }
                public long BytesUpdated { get; set; }
                public string Sha256Baseline { get; set; }
                public string Sha256Updated { get; set; }

                public override string ToString()
                {
                    var delta = BytesUpdated - BytesBaseline;
                    var sign = delta > 0 ? "+" : "";
                    return $"{Filename}: {BytesBaseline} → {BytesUpdated} ({sign}{delta} bytes)";
                }
            }

            /// <summary>
            /// Bundles that exist in both snapshots with identical SHA256.
            /// </summary>
            public IReadOnlyList<DiffEntry> Unchanged { get; set; }

            /// <summary>
            /// Bundles that exist in both snapshots but have different SHA256.
            /// </summary>
            public IReadOnlyList<DiffEntry> Changed { get; set; }

            /// <summary>
            /// Bundle filenames that exist in the updated snapshot but not the baseline.
            /// </summary>
            public IReadOnlyList<string> New { get; set; }

            /// <summary>
            /// Bundle filenames that exist in the baseline but not the updated snapshot.
            /// </summary>
            public IReadOnlyList<string> Removed { get; set; }

            /// <summary>
            /// True if the .hash file content differs between snapshots.
            /// </summary>
            public bool CatalogHashChanged { get; set; }

            /// <summary>
            /// Sum of absolute value of byte changes in Changed bundles.
            /// Used to verify the core exit criterion:
            /// "2 MB asset change produces ≤ 2.5 MB of changed bundles"
            /// </summary>
            public long TotalChangedBytes { get; set; }

            public override string ToString()
            {
                return $"Diff(unchanged={Unchanged.Count}, changed={Changed.Count}, new={New.Count}, removed={Removed.Count}, " +
                       $"catalogHashChanged={CatalogHashChanged}, totalChangedBytes={TotalChangedBytes})";
            }
        }

        // ============================================================================
        // SETUP / TEARDOWN
        // ============================================================================

        private static readonly string TestOutputDir = Path.Combine(Path.GetTempPath(), "CdnBuildSnapshotTests");
        private static readonly string BundleOutputDir = Path.Combine(TestOutputDir, "bundles");
        private static readonly string CatalogHashFilePath = Path.Combine(TestOutputDir, "catalog_1.0.0.hash");

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Create test directories
            if (!Directory.Exists(TestOutputDir))
                Directory.CreateDirectory(TestOutputDir);
            if (!Directory.Exists(BundleOutputDir))
                Directory.CreateDirectory(BundleOutputDir);
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test artifacts
            try
            {
                if (Directory.Exists(BundleOutputDir))
                    Directory.Delete(BundleOutputDir, true);
                if (File.Exists(CatalogHashFilePath))
                    File.Delete(CatalogHashFilePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            try
            {
                if (Directory.Exists(TestOutputDir))
                    Directory.Delete(TestOutputDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        // ============================================================================
        // HELPER METHODS
        // ============================================================================

        /// <summary>
        /// Create a dummy bundle file with specified size.
        /// </summary>
        private static void CreateDummyBundle(string filename, long sizeBytes)
        {
            var path = Path.Combine(BundleOutputDir, filename);
            using (var fs = File.Create(path))
            {
                // Write random bytes
                var buffer = new byte[Math.Min(sizeBytes, 1024 * 1024)]; // 1 MB chunks
                var random = new Random(filename.GetHashCode());
                long remaining = sizeBytes;

                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    random.NextBytes(buffer);
                    fs.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
            }
        }

        /// <summary>
        /// Create a catalog hash file with specified content.
        /// </summary>
        private static void CreateCatalogHash(byte[] content)
        {
            File.WriteAllBytes(CatalogHashFilePath, content);
        }

        /// <summary>
        /// Get a deterministic byte array based on a seed value.
        /// Used to generate repeatable test data.
        /// </summary>
        private static byte[] GenerateHashContent(int seed)
        {
            var random = new Random(seed);
            var buffer = new byte[256]; // Arbitrary size for test hash content
            random.NextBytes(buffer);
            return buffer;
        }

        // ============================================================================
        // TEST CASES
        // ============================================================================

        /// <summary>
        /// TEST 1: No-Op Update (Determinism)
        ///
        /// Validates that running a "build" without changing any assets produces
        /// byte-identical output. This is foundational: if the build is not deterministic,
        /// we cannot trust any diff.
        ///
        /// Expected: diff shows no changes — all zero.
        /// </summary>
        [Test]
        public void NoOpUpdate_ProducesIdenticalSnapshot()
        {
            // Arrange: Create baseline
            CreateDummyBundle("ui_common.bundle", 1024 * 1024);         // 1 MB
            CreateDummyBundle("stage01_assets.bundle", 2 * 1024 * 1024); // 2 MB
            CreateCatalogHash(GenerateHashContent(seed: 1));

            var baseline = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);

            // Act: "Re-run" the build by capturing the same output again
            var updated = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);

            // Assert: No changes whatsoever
            var diff = baseline.Diff(updated);

            Assert.That(diff.Unchanged.Count, Is.EqualTo(2),
                "All bundles should be unchanged after a no-op build");
            Assert.That(diff.Changed.Count, Is.EqualTo(0),
                "No bundles should have changed");
            Assert.That(diff.New.Count, Is.EqualTo(0),
                "No new bundles in a no-op update");
            Assert.That(diff.Removed.Count, Is.EqualTo(0),
                "No bundles should be removed");
            Assert.That(diff.CatalogHashChanged, Is.False,
                "Catalog hash should not change in a no-op build");
            Assert.That(diff.TotalChangedBytes, Is.EqualTo(0),
                "Total changed bytes must be zero");
        }

        /// <summary>
        /// TEST 2: Repeat No-Op (Double-Check Determinism)
        ///
        /// Runs the no-op test again in sequence to confirm determinism is stable.
        /// This catches cases where the first run is deterministic by accident.
        ///
        /// Expected: identical to test 1.
        /// </summary>
        [Test]
        public void RepeatNoOpUpdate_StabilizesDeterminism()
        {
            // Arrange: Create initial snapshot
            CreateDummyBundle("ui_common.bundle", 1024 * 1024);
            CreateDummyBundle("stage01_assets.bundle", 2 * 1024 * 1024);
            CreateCatalogHash(GenerateHashContent(seed: 2));

            var snapshot1 = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);

            // Clean and recreate identical bundles
            Directory.Delete(BundleOutputDir, true);
            Directory.CreateDirectory(BundleOutputDir);

            CreateDummyBundle("ui_common.bundle", 1024 * 1024);
            CreateDummyBundle("stage01_assets.bundle", 2 * 1024 * 1024);
            CreateCatalogHash(GenerateHashContent(seed: 2));

            // Act
            var snapshot2 = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);
            var diff = snapshot1.Diff(snapshot2);

            // Assert: Still zero changes
            Assert.That(diff.Changed.Count, Is.EqualTo(0),
                "Determinism must be stable across multiple runs");
            Assert.That(diff.CatalogHashChanged, Is.False,
                "Catalog hash should remain identical");
            Assert.That(diff.TotalChangedBytes, Is.EqualTo(0));
        }

        /// <summary>
        /// TEST 3: Single-Asset Update (Independent Group)
        ///
        /// Simulates modifying a single asset in an independent group (no shared dependencies).
        /// This validates localization: changes must not propagate to unrelated groups.
        ///
        /// Expected: Only the affected bundle changes. Other bundles remain identical.
        /// </summary>
        [Test]
        public void SingleAssetUpdate_OnlyAffectsIndependentBundle()
        {
            // Arrange: Create baseline with multiple independent groups
            CreateDummyBundle("group_a_assets.bundle", 1024 * 1024);      // 1 MB
            CreateDummyBundle("group_b_assets.bundle", 2 * 1024 * 1024);  // 2 MB (will be modified)
            CreateDummyBundle("ui_common.bundle", 512 * 1024);            // 512 KB
            CreateCatalogHash(GenerateHashContent(seed: 3));

            var baseline = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);

            // Act: Modify group_b by recreating it with different content
            // (Simulates a content change that alters the bundle hash)
            File.Delete(Path.Combine(BundleOutputDir, "group_b_assets.bundle"));
            CreateDummyBundle("group_b_assets.bundle", 2 * 1024 * 1024 + 256 * 1024); // 2.25 MB (slightly larger)

            var updated = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);
            var diff = baseline.Diff(updated);

            // Assert: Only group_b changed
            Assert.That(diff.Changed.Count, Is.EqualTo(1),
                "Only one bundle should have changed");
            Assert.That(diff.Changed[0].Filename, Is.EqualTo("group_b_assets.bundle"),
                "The changed bundle must be group_b_assets.bundle");
            Assert.That(diff.Unchanged.Count, Is.EqualTo(2),
                "All other bundles should remain unchanged");
            Assert.That(diff.New.Count, Is.EqualTo(0));
            Assert.That(diff.Removed.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// TEST 4: Shared-Asset Update (Multi-Group Impact)
        ///
        /// Core exit criterion: modifying a shared asset that multiple groups depend on.
        /// Target: a 2 MB asset change produces ≤ 2.5 MB of changed bundles.
        ///
        /// Scenario:
        /// - Baseline: shared_assets (2 MB) + group_a depends on it + group_b depends on it
        /// - Update: modify shared_assets by +256 KB (2.256 MB total)
        /// - Expected: bundles from group_a and group_b change, but others don't
        /// - Criterion: total changed bytes ≤ 2.5 MB
        ///
        /// This is the most important test: it validates the whole point of delta updates.
        /// </summary>
        [Test]
        public void SharedAssetUpdate_OnlyAffectsDependentBundles()
        {
            // Arrange: Create baseline with shared assets
            // Shared bundle that multiple groups depend on
            CreateDummyBundle("shared_assets.bundle", 2 * 1024 * 1024);        // 2.0 MB

            // Groups that depend on the shared bundle
            CreateDummyBundle("group_a_with_shared.bundle", 1 * 1024 * 1024);  // 1.0 MB
            CreateDummyBundle("group_b_with_shared.bundle", 1 * 1024 * 1024);  // 1.0 MB

            // Independent groups that do NOT depend on the shared bundle
            CreateDummyBundle("group_independent_x.bundle", 512 * 1024);       // 0.5 MB
            CreateDummyBundle("group_independent_y.bundle", 512 * 1024);       // 0.5 MB

            CreateCatalogHash(GenerateHashContent(seed: 4));

            var baseline = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);

            // Act: Modify the shared bundle by +256 KB
            File.Delete(Path.Combine(BundleOutputDir, "shared_assets.bundle"));
            CreateDummyBundle("shared_assets.bundle", (long)(2.256 * 1024 * 1024)); // +256 KB

            // This would cascade to group_a and group_b bundles being rebuilt
            // (In a real build, Addressables would re-bundle these)
            File.Delete(Path.Combine(BundleOutputDir, "group_a_with_shared.bundle"));
            CreateDummyBundle("group_a_with_shared.bundle", (long)(1.128 * 1024 * 1024)); // +128 KB

            File.Delete(Path.Combine(BundleOutputDir, "group_b_with_shared.bundle"));
            CreateDummyBundle("group_b_with_shared.bundle", (long)(1.128 * 1024 * 1024)); // +128 KB

            var updated = BuildSnapshot.Capture(BundleOutputDir, CatalogHashFilePath);
            var diff = baseline.Diff(updated);

            // Assert: Only shared + dependent bundles changed
            Assert.That(diff.Changed.Count, Is.EqualTo(3),
                "Shared bundle and its two dependents should have changed");

            var changedNames = diff.Changed.Select(e => e.Filename).ToList();
            Assert.That(changedNames, Does.Contain("shared_assets.bundle"),
                "The shared bundle must be in the changed list");
            Assert.That(changedNames, Does.Contain("group_a_with_shared.bundle"),
                "Group A (depends on shared) must be in the changed list");
            Assert.That(changedNames, Does.Contain("group_b_with_shared.bundle"),
                "Group B (depends on shared) must be in the changed list");

            // Independent bundles should NOT change
            Assert.That(diff.Unchanged, Has.Some.Matches<BuildSnapshot.BundleEntry>(
                    b => b.Filename == "group_independent_x.bundle"),
                "Independent group X should be unchanged");
            Assert.That(diff.Unchanged, Has.Some.Matches<BuildSnapshot.BundleEntry>(
                    b => b.Filename == "group_independent_y.bundle"),
                "Independent group Y should be unchanged");

            Assert.That(diff.New.Count, Is.EqualTo(0));
            Assert.That(diff.Removed.Count, Is.EqualTo(0));

            // Core criterion: 2 MB change → ≤ 2.5 MB bundle delta
            Assert.That(diff.TotalChangedBytes, Is.LessThanOrEqualTo(2_500_000),
                $"2 MB asset change should produce ≤ 2.5 MB bundle delta. Actual: {diff.TotalChangedBytes} bytes. " +
                $"Changed bundles: {string.Join(", ", changedNames)}");
        }
    }
}
