using System.Collections.Generic;
using System.IO;
using AddressableManager.Editor.Cdn;
using NUnit.Framework;
using UnityEngine;

namespace AddressableManager.Tests.Editor
{
    /// <summary>
    /// Pins the reflection the Catalog Inspector depends on, and the cross-check it draws — task 5.9.
    /// </summary>
    /// <remarks>
    /// <see cref="CatalogReader"/> reaches two internal members of Addressables because there is no
    /// public way to open a binary catalog without booting the runtime. That is a deliberate trade,
    /// and this fixture is the other half of it: an Addressables upgrade that renames or reshapes
    /// either member fails here, at the point where the cause is obvious, instead of shipping a tab
    /// that silently shows an empty catalog.
    ///
    /// The comparison tests use fabricated models rather than a built catalog, so they run without a
    /// content build. That is the right split — reading a real catalog is verified by the tab probe
    /// against this project's own build output, while the arithmetic of missing versus orphaned
    /// versus mismatched is logic that deserves cases a real build cannot conveniently produce.
    /// </remarks>
    [TestFixture]
    public class CatalogReaderTests
    {
        [Test]
        public void LoadFromFile_IsStillReachable_OnTheInstalledAddressables()
        {
            var loader = CatalogReader.ResolveLoader();

            Assert.IsTrue(loader.IsSuccess,
                "CatalogReader can no longer find ContentCatalogData.LoadFromFile. The Catalog " +
                "Inspector reads catalogs through it, so this is a hard break, not a warning. " +
                "Reason given: " + loader.ErrorMessage);
        }

        [Test]
        public void CreateCustomLocator_IsStillReachable_OnTheInstalledAddressables()
        {
            var method = typeof(UnityEngine.AddressableAssets.ResourceLocators.ContentCatalogData)
                .GetMethod(
                    "CreateCustomLocator",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);

            Assert.IsNotNull(method,
                "ContentCatalogData.CreateCustomLocator(string, string) is gone from the installed " +
                "Addressables. CatalogReader needs it to turn catalog data into a locator.");
        }

        [Test]
        public void Read_ReportsTheMissingFile_RatherThanThrowing()
        {
            string absent = Path.Combine(Path.GetTempPath(), "no-such-catalog-9d2f.bin");

            var result = CatalogReader.Read(absent);

            Assert.IsTrue(result.IsFailure, "Reading a file that is not there must fail, not return an empty catalog.");
            StringAssert.Contains("no-such-catalog-9d2f.bin", result.ErrorMessage);
        }

        [Test]
        public void Read_RejectsAFileThatIsNotACatalog()
        {
            string path = Path.Combine(Path.GetTempPath(), "not-a-catalog-9d2f.bin");
            File.WriteAllText(path, "this is not a catalog");

            try
            {
                var result = CatalogReader.Read(path);

                // The point is not which message comes back but that garbage in produces a failure
                // with an explanation, rather than an exception escaping into the Editor UI.
                Assert.IsTrue(result.IsFailure, "A file that is not a catalog must be reported as a failure.");
                Assert.IsNotEmpty(result.ErrorMessage);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ========== reading the two fields that are easy to confuse ==========

        [Test]
        public void FileNameFromInternalId_TakesTheLastSegment_OfBothUrlsAndWindowsPaths()
        {
            // Both forms appear in the same catalog. On a machine where '\' is not a path separator,
            // Path.GetFileName returns the second one whole, which is how a build with local bundles
            // ends up reporting them all as missing from the CDN.
            Assert.AreEqual("a_assets_all_62b4.bundle", CatalogReader.FileNameFromInternalId(
                "http://host:8080/StandaloneWindows64/bundles/a_assets_all_62b4.bundle"));

            Assert.AreEqual("scene_scenes_all_8ca3.bundle", CatalogReader.FileNameFromInternalId(
                @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\StandaloneWindows64\scene_scenes_all_8ca3.bundle"));
        }

        [Test]
        public void FileNameFromInternalId_DropsASignedUrlQueryString()
        {
            Assert.AreEqual("a.bundle", CatalogReader.FileNameFromInternalId(
                "https://cdn.example.invalid/a.bundle?X-Amz-Signature=deadbeef&X-Amz-Expires=900"));
        }

        [Test]
        public void ClassifyLocation_SeparatesRemoteFromInPlayer_AndAdmitsWhenItCannotTell()
        {
            Assert.AreEqual(BundleLocation.Remote, CatalogReader.ClassifyLocation("https://cdn.example.invalid/a.bundle"));
            Assert.AreEqual(BundleLocation.Remote, CatalogReader.ClassifyLocation("http://localhost:8080/a.bundle"));

            Assert.AreEqual(BundleLocation.Local, CatalogReader.ClassifyLocation(
                @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\StandaloneWindows64\a.bundle"));

            // The important case: an unrecognised load path must not be quietly filed as local, which
            // would drop it from the check and let a broken publish pass.
            Assert.AreEqual(BundleLocation.Unknown, CatalogReader.ClassifyLocation("{SomeCustomToken}/a.bundle"));
        }

        // ========== the cross-check ==========

        [Test]
        public void Compare_DoesNotCallInPlayerBundlesMissingFromTheCdn()
        {
            string directory = MakeBundleFolder(("remote.bundle", 100));

            try
            {
                var catalog = Catalog(
                    Bundle("remote.bundle", 100, dependents: 2),
                    LocalBundle("builtin_shaders.bundle", 150024));

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(0, report.Value.Missing.Count,
                    "A bundle that ships inside the player is not missing from the CDN folder.");
                Assert.AreEqual(1, report.Value.LocalBundles);
                Assert.IsTrue(report.Value.IsPublishable);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_SaysSoWhenItCouldNotClassifyABundle()
        {
            string directory = MakeBundleFolder(("remote.bundle", 100));

            try
            {
                var unclassified = new CatalogBundle(
                    "mystery.bundle", "mystery", "{SomeCustomToken}/mystery.bundle",
                    "AssetBundleProvider", 10, 0u, "hash", BundleLocation.Unknown);

                var catalog = Catalog(Bundle("remote.bundle", 100, dependents: 1), unclassified);

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(1, report.Value.UncheckedBundles);
                StringAssert.Contains("not checked", report.Value.Summary(),
                    "A clean verdict that silently skipped a bundle is worse than no verdict.");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_FindsMissingBundles_AndCountsWhatNeedsThem()
        {
            string directory = MakeBundleFolder(("present.bundle", 100));

            try
            {
                var catalog = Catalog(
                    Bundle("present.bundle", 100, dependents: 2),
                    Bundle("absent.bundle", 400, dependents: 3));

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(1, report.Value.Missing.Count);
                Assert.AreEqual("absent.bundle", report.Value.Missing[0].BundleFileName);
                Assert.AreEqual(3, report.Value.Missing[0].DependentEntryCount,
                    "A missing bundle is worth reporting in proportion to how many entries it breaks.");
                Assert.IsFalse(report.Value.IsPublishable, "A build with a missing bundle must not read as publishable.");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_FindsOrphans_AndDoesNotCallThemMissing()
        {
            string directory = MakeBundleFolder(
                ("used.bundle", 100),
                ("leftover.bundle", 250));

            try
            {
                var catalog = Catalog(Bundle("used.bundle", 100, dependents: 1));

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(0, report.Value.Missing.Count);
                Assert.AreEqual(1, report.Value.Orphans.Count);
                Assert.AreEqual("leftover.bundle", report.Value.Orphans[0].BundleFileName);
                Assert.AreEqual(250, report.Value.OrphanBytes);
                Assert.IsTrue(report.Value.IsPublishable,
                    "An orphan costs storage; it does not break a player. The build is still publishable.");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_FlagsASizeMismatch_BecauseItMeansTwoDifferentBuilds()
        {
            string directory = MakeBundleFolder(("shifted.bundle", 999));

            try
            {
                var catalog = Catalog(Bundle("shifted.bundle", 100, dependents: 1));

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(1, report.Value.SizeMismatches.Count);
                Assert.AreEqual(100, report.Value.SizeMismatches[0].CatalogBytes);
                Assert.AreEqual(999, report.Value.SizeMismatches[0].ActualBytes);
                Assert.IsFalse(report.Value.IsPublishable);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_TreatsAnUnrecordedSizeAsUnknown_NotAsZero()
        {
            string directory = MakeBundleFolder(("sizeless.bundle", 4096));

            try
            {
                // A catalog can legitimately carry no size for a bundle. Reporting that as
                // "expected 0 bytes, found 4096" would be a fabricated finding — exactly the sentinel
                // confusion repo invariant 4 exists to prevent.
                var catalog = Catalog(Bundle("sizeless.bundle", 0, dependents: 1));

                var report = CatalogInspection.Compare(catalog, directory);

                Assert.IsTrue(report.IsSuccess, report.ErrorMessage);
                Assert.AreEqual(0, report.Value.SizeMismatches.Count,
                    "An unrecorded size is not a mismatch.");
                Assert.IsTrue(report.Value.IsPublishable);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Compare_ReportsTheMissingFolder_RatherThanAnEmptyResult()
        {
            var catalog = Catalog(Bundle("anything.bundle", 1, dependents: 1));

            var report = CatalogInspection.Compare(
                catalog, Path.Combine(Path.GetTempPath(), "no-such-bundle-folder-9d2f"));

            Assert.IsTrue(report.IsFailure,
                "No bundle folder is 'could not check', which must not arrive looking like " +
                "'checked, everything is fine'.");
        }

        // ========== fixtures ==========

        private static string MakeBundleFolder(params (string name, int size)[] files)
        {
            string directory = Path.Combine(Path.GetTempPath(), "catalog-inspection-" + Path.GetRandomFileName());
            Directory.CreateDirectory(directory);

            foreach (var (name, size) in files)
                File.WriteAllBytes(Path.Combine(directory, name), new byte[size]);

            return directory;
        }

        private static CatalogBundle Bundle(string name, long size, int dependents)
        {
            return new CatalogBundle(
                bundleName: name,
                catalogName: "0198c01022dfc87272228a59dbea2a92",
                internalId: "http://example.invalid/bundles/" + name,
                providerId: "AssetBundleProvider",
                sizeBytes: size, crc: 0u, hash: "hash",
                location: BundleLocation.Remote)
            {
                DependentEntryCount = dependents
            };
        }

        private static CatalogBundle LocalBundle(string name, long size)
        {
            return new CatalogBundle(
                bundleName: name,
                catalogName: name,
                internalId: @"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\StandaloneWindows64\" + name,
                providerId: "AssetBundleProvider",
                sizeBytes: size, crc: 0u, hash: "hash",
                location: BundleLocation.Local)
            {
                DependentEntryCount = 4
            };
        }

        private static CatalogContents Catalog(params CatalogBundle[] bundles)
        {
            return new CatalogContents(
                "fabricated.bin", "test-locator",
                new List<CatalogEntry>(),
                new List<CatalogBundle>(bundles));
        }
    }
}
