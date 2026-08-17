using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Reads a built content catalog off disk and turns it into a plain model — task 5.9.
    /// </summary>
    /// <remarks>
    /// WHY THIS USES REFLECTION
    ///
    /// A built catalog is binary (<c>EnableJsonCatalog</c> is false, per the settings contract), and
    /// Unity's loader for it — <c>ContentCatalogData.LoadFromFile</c> — is <c>internal</c>. So is the
    /// factory that turns the loaded data into a locator, <c>CreateCustomLocator</c>. Everything after
    /// that point is public interface: <see cref="IResourceLocator"/>, <see cref="IResourceLocation"/>,
    /// <see cref="AssetBundleRequestOptions"/>.
    ///
    /// Two alternatives were rejected:
    ///
    /// - <c>Addressables.LoadContentCatalogAsync</c> is public and would work, but it mutates the
    ///   running Addressables instance: it boots initialisation (the chain operation starts init when
    ///   it is read — see AddressablesImpl.cs:530) and registers the catalog as a live locator. An
    ///   inspector must not change what it is inspecting, and this one has to work in batchmode where
    ///   there is no Addressables session to disturb.
    /// - A third-party catalog parser (AddressablesTools) means owning a binary-format dependency that
    ///   has to track every Addressables release. The two method names below are a much smaller
    ///   surface to track.
    ///
    /// The reflection is pinned by CatalogReaderTests, which resolves both members against the
    /// installed Addressables. An Addressables upgrade that renames either one fails that test rather
    /// than producing an empty inspector at runtime.
    ///
    /// Unity's own <c>ContentCatalogData.ExtractBinaryCatalog</c> (public, Editor-only) walks the
    /// catalog exactly this way — load with internal-id resolving disabled, create a locator, iterate
    /// Keys and Locate. This class produces a model instead of a text dump.
    /// </remarks>
    public static class CatalogReader
    {
        private const string LoadMethodName = "LoadFromFile";
        private const string LocatorMethodName = "CreateCustomLocator";

        /// <summary>
        /// Read a catalog file into a model.
        /// </summary>
        /// <param name="catalogPath">Path to a built <c>catalog_*.bin</c> (or <c>.json</c>) file.</param>
        public static CdnEditorResult<CatalogContents> Read(string catalogPath)
        {
            if (string.IsNullOrEmpty(catalogPath))
                return CdnEditorResult<CatalogContents>.Failure("No catalog path given.");

            if (!File.Exists(catalogPath))
                return CdnEditorResult<CatalogContents>.Failure($"No catalog file at '{catalogPath}'.");

            var loaderResult = ResolveLoader();
            if (loaderResult.IsFailure)
                return CdnEditorResult<CatalogContents>.Failure(loaderResult.ErrorMessage);

            object catalogData;
            try
            {
                catalogData = loaderResult.Value.Invoke(catalogPath);
            }
            catch (Exception e)
            {
                // A TargetInvocationException here is almost always a format mismatch: a JSON catalog
                // handed to the binary reader, or a catalog from an Addressables version whose binary
                // layout differs. Say which, because "deserialization failed" sends people looking at
                // their content instead of at their versions.
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                return CdnEditorResult<CatalogContents>.Failure(
                    $"Could not parse '{Path.GetFileName(catalogPath)}': {inner.Message}. " +
                    "Check that the file is a catalog built by the Addressables version currently " +
                    "installed, and that its format matches EnableJsonCatalog.");
            }

            var locatorMethod = catalogData.GetType().GetMethod(
                LocatorMethodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);

            if (locatorMethod == null)
            {
                return CdnEditorResult<CatalogContents>.Failure(
                    $"ContentCatalogData.{LocatorMethodName}(string, string) is missing from the " +
                    "installed Addressables. The Catalog Inspector needs it to walk a catalog; " +
                    "see CatalogReader for what to change.");
            }

            IResourceLocator locator;
            try
            {
                locator = locatorMethod.Invoke(catalogData, new object[] { string.Empty, null }) as IResourceLocator;
            }
            catch (Exception e)
            {
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                return CdnEditorResult<CatalogContents>.Failure(
                    $"Could not build a locator from '{Path.GetFileName(catalogPath)}': {inner.Message}");
            }

            if (locator == null)
            {
                return CdnEditorResult<CatalogContents>.Failure(
                    $"ContentCatalogData.{LocatorMethodName} returned something that is not an " +
                    "IResourceLocator. The installed Addressables has changed shape.");
            }

            return CdnEditorResult<CatalogContents>.Success(Walk(catalogPath, locator));
        }

        /// <summary>
        /// Resolve <c>ContentCatalogData.LoadFromFile</c>, whichever overload this build has.
        /// </summary>
        /// <remarks>
        /// The binary and JSON catalog code paths are two mutually exclusive halves of the same source
        /// file, split on <c>ENABLE_JSON_CATALOG</c>, and each declares its own overload:
        /// <c>(string, bool resolveInternalIds)</c> for binary, <c>(string, int cacheSize)</c> for
        /// JSON. Only one is compiled, so resolve by signature rather than assuming.
        ///
        /// Internal-id resolving is switched OFF for the binary overload. An inspector should show what
        /// the catalog actually contains, placeholders and all — <c>{Addressables.RuntimePath}</c> in
        /// an internal id is exactly the kind of thing you open this tab to check, and resolving it
        /// against the Editor's own paths would hide it behind a local answer that no device will see.
        /// </remarks>
        internal static CdnEditorResult<Func<string, object>> ResolveLoader()
        {
            var type = typeof(ContentCatalogData);

            var binary = type.GetMethod(
                LoadMethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(bool) },
                null);

            if (binary != null)
            {
                return CdnEditorResult<Func<string, object>>.Success(
                    path => binary.Invoke(null, new object[] { path, false }));
            }

            var json = type.GetMethod(
                LoadMethodName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(int) },
                null);

            if (json != null)
            {
                return CdnEditorResult<Func<string, object>>.Success(
                    path => json.Invoke(null, new object[] { path, 1024 }));
            }

            return CdnEditorResult<Func<string, object>>.Failure(
                $"Neither ContentCatalogData.{LoadMethodName}(string, bool) nor " +
                $"{LoadMethodName}(string, int) exists in the installed Addressables. The Catalog " +
                "Inspector reads catalogs through one of them; see CatalogReader for what to change.");
        }

        private static CatalogContents Walk(string catalogPath, IResourceLocator locator)
        {
            var entries = new List<CatalogEntry>();
            var bundles = new Dictionary<string, CatalogBundle>(StringComparer.Ordinal);

            foreach (object key in locator.Keys)
            {
                if (!locator.Locate(key, typeof(object), out var locations) || locations == null)
                    continue;

                foreach (var location in locations)
                {
                    if (location == null) continue;

                    // A bundle location carries AssetBundleRequestOptions in Data; an asset location
                    // does not. This is the load-time distinction Addressables itself makes, rather
                    // than a guess from the provider name — provider ids are configurable and a custom
                    // one would break a name check.
                    if (location.Data is AssetBundleRequestOptions bundleOptions)
                    {
                        Record(bundles, location, bundleOptions);
                        continue;
                    }

                    var dependencyNames = new List<string>();
                    if (location.Dependencies != null)
                    {
                        foreach (var dependency in location.Dependencies)
                        {
                            if (dependency?.Data is AssetBundleRequestOptions dependencyOptions)
                            {
                                var recorded = Record(bundles, dependency, dependencyOptions);
                                recorded.DependentEntryCount++;
                                dependencyNames.Add(recorded.BundleName);
                            }
                        }
                    }

                    entries.Add(new CatalogEntry(
                        address: location.PrimaryKey,
                        key: key as string ?? key?.ToString() ?? string.Empty,
                        internalId: location.InternalId,
                        providerId: location.ProviderId,
                        resourceType: location.ResourceType?.Name ?? "?",
                        bundleNames: dependencyNames));
                }
            }

            // Keys are enumerated in catalog order, which is neither alphabetical nor stable across
            // builds. Sorting makes two inspections of two catalogs comparable by eye, which is the
            // only way this tab gets used.
            entries.Sort((a, b) => string.CompareOrdinal(a.Address, b.Address));

            var bundleList = new List<CatalogBundle>(bundles.Values);
            bundleList.Sort((a, b) => string.CompareOrdinal(a.BundleName, b.BundleName));

            return new CatalogContents(catalogPath, locator.LocatorId, entries, bundleList);
        }

        private static CatalogBundle Record(
            IDictionary<string, CatalogBundle> bundles,
            IResourceLocation location,
            AssetBundleRequestOptions options)
        {
            // The file name comes from the InternalId, NOT from options.BundleName.
            //
            // This looks backwards and is not. In a built catalog, BundleName is an internal
            // identifier — a bare hash like "0198c01022dfc87272228a59dbea2a92", or that hash with a
            // "_monoscripts" suffix. The name of the file that actually gets requested is the last
            // segment of the InternalId:
            //
            //   BundleName  0198c01022dfc87272228a59dbea2a92
            //   InternalId  http://host/StandaloneWindows64/bundles/remotecorpusshared_assets_all_62b4….bundle
            //
            // Keying on BundleName made every bundle in this project's own build look missing while
            // every file on disk looked orphaned, which is how the difference was found. The file
            // name is what a player's HTTP request asks for, so it is the only key that can be
            // compared against a folder or a CDN listing.
            string fileName = FileNameFromInternalId(location.InternalId);
            string key = string.IsNullOrEmpty(fileName)
                ? options.BundleName ?? string.Empty
                : fileName;

            if (bundles.TryGetValue(key, out var existing))
                return existing;

            var bundle = new CatalogBundle(
                bundleName: key,
                catalogName: options.BundleName ?? string.Empty,
                internalId: location.InternalId,
                providerId: location.ProviderId,
                sizeBytes: options.BundleSize,
                crc: options.Crc,
                hash: options.Hash,
                location: ClassifyLocation(location.InternalId));

            bundles[key] = bundle;
            return bundle;
        }

        /// <summary>
        /// The last path segment of an internal id, whether it is a URL or a Windows path.
        /// </summary>
        /// <remarks>
        /// <c>Path.GetFileName</c> alone is not enough: a catalog holds both forms in the same build
        /// — remote entries as <c>http://host/a/b.bundle</c> and local ones as
        /// <c>{Addressables.RuntimePath}\Platform\c.bundle</c> — and on a non-Windows machine the
        /// backslash is not a separator, so half of them would come back whole.
        /// </remarks>
        internal static string FileNameFromInternalId(string internalId)
        {
            if (string.IsNullOrEmpty(internalId)) return string.Empty;

            // Drop a query string before splitting; a signed CDN URL carries one and it is not part
            // of the file name.
            int query = internalId.IndexOf('?');
            string withoutQuery = query >= 0 ? internalId.Substring(0, query) : internalId;

            int cut = withoutQuery.LastIndexOfAny(new[] { '/', '\\' });
            return cut >= 0 ? withoutQuery.Substring(cut + 1) : withoutQuery;
        }

        /// <summary>
        /// Where a bundle is loaded from, as the catalog records it.
        /// </summary>
        /// <remarks>
        /// A build has both kinds and they are checked against different things. A local bundle ships
        /// inside the player and will never be in the remote folder; calling it "missing from the
        /// CDN" is not a finding, it is a false alarm — and it was eight of them on the first real
        /// run of this code.
        ///
        /// Anything that is neither is <see cref="BundleLocation.Unknown"/> rather than assumed. A
        /// project with a custom load-path token would otherwise have every remote bundle quietly
        /// classified as local and skipped, which turns this check into one that always passes.
        /// </remarks>
        internal static BundleLocation ClassifyLocation(string internalId)
        {
            if (string.IsNullOrEmpty(internalId)) return BundleLocation.Unknown;

            if (internalId.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                internalId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return BundleLocation.Remote;

            if (internalId.IndexOf("Addressables.RuntimePath", StringComparison.OrdinalIgnoreCase) >= 0 ||
                internalId.IndexOf("StreamingAssets", StringComparison.OrdinalIgnoreCase) >= 0)
                return BundleLocation.Local;

            return BundleLocation.Unknown;
        }
    }

    /// <summary>Where the catalog says a bundle is loaded from.</summary>
    public enum BundleLocation
    {
        /// <summary>An http(s) URL — this is CDN content.</summary>
        Remote,

        /// <summary>Inside the player, via the runtime path or StreamingAssets.</summary>
        Local,

        /// <summary>Neither — an unresolved token, or a load path this package does not recognise.</summary>
        Unknown
    }

    /// <summary>Everything task 5.9 needs from one catalog file.</summary>
    public sealed class CatalogContents
    {
        public string CatalogPath { get; }
        public string LocatorId { get; }
        public IReadOnlyList<CatalogEntry> Entries { get; }
        public IReadOnlyList<CatalogBundle> Bundles { get; }

        public CatalogContents(
            string catalogPath,
            string locatorId,
            IReadOnlyList<CatalogEntry> entries,
            IReadOnlyList<CatalogBundle> bundles)
        {
            CatalogPath = catalogPath;
            LocatorId = locatorId;
            Entries = entries;
            Bundles = bundles;
        }

        public long TotalBundleBytes
        {
            get
            {
                long total = 0;
                foreach (var bundle in Bundles)
                    if (bundle.SizeBytes > 0)
                        total += bundle.SizeBytes;
                return total;
            }
        }

        /// <summary>Bytes a player has to download — remote bundles only.</summary>
        public long RemoteBundleBytes
        {
            get
            {
                long total = 0;
                foreach (var bundle in Bundles)
                    if (bundle.Location == BundleLocation.Remote && bundle.SizeBytes > 0)
                        total += bundle.SizeBytes;
                return total;
            }
        }

        public int CountBundles(BundleLocation location)
        {
            int count = 0;
            foreach (var bundle in Bundles)
                if (bundle.Location == location)
                    count++;
            return count;
        }

        /// <summary>
        /// Entries delivered from the CDN that also need a bundle shipped inside the player.
        /// </summary>
        /// <remarks>
        /// This is the check that decides whether remote content can be built on a different machine
        /// from the player.
        ///
        /// A remote entry whose dependencies are all remote is self-contained: the catalog and every
        /// bundle it names travel together to the CDN, and the player never has to agree with the
        /// machine that built them. A remote entry that also needs a LOCAL bundle is not. The catalog
        /// names that local bundle by the hash the content build produced, and the player carries
        /// whatever its own build produced. Two machines, two hashes, and the dependency resolves to
        /// a bundle that is not in the app.
        ///
        /// It fails at load time, on a device, with a missing-dependency error that points at the
        /// asset rather than at the build topology — which is why it is worth reporting here instead
        /// of discovering later.
        ///
        /// Note that this is about ADDRESSABLE entries in local groups. An ordinary asset referenced
        /// from both sides is an implicit dependency, and Addressables duplicates those into every
        /// bundle that needs them rather than linking across. Duplication costs install size, not
        /// correctness, and does not show up here.
        ///
        /// Unity's own generated bundles — unitybuiltinassets and monoscripts — are the ones that
        /// catch people out. They are shared by everything and land wherever the build puts them.
        /// </remarks>
        public IReadOnlyList<CrossBoundaryEntry> FindRemoteEntriesNeedingLocalBundles()
        {
            var byName = new Dictionary<string, CatalogBundle>(StringComparer.Ordinal);
            foreach (var bundle in Bundles)
                byName[bundle.BundleName] = bundle;

            var found = new List<CrossBoundaryEntry>();

            // One asset appears once per key it is reachable by — its address, its GUID, each label.
            // Counting those separately turns four assets into eight findings, and an inflated count
            // is how a report stops being trusted.
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in Entries)
            {
                if (!reported.Add(entry.Address)) continue;

                List<string> local = null;
                bool hasRemote = false;

                foreach (string name in entry.BundleNames)
                {
                    if (!byName.TryGetValue(name, out var bundle)) continue;

                    if (bundle.Location == BundleLocation.Remote)
                        hasRemote = true;
                    else if (bundle.Location == BundleLocation.Local)
                        (local ??= new List<string>()).Add(name);
                }

                if (hasRemote && local != null)
                    found.Add(new CrossBoundaryEntry(entry.Address, local));
            }

            return found;
        }
    }

    /// <summary>One addressable entry as the catalog records it.</summary>
    public sealed class CatalogEntry
    {
        /// <summary>The entry's primary key — what a game passes to a load call.</summary>
        public string Address { get; }

        /// <summary>The key this entry was found under. Differs from <see cref="Address"/> for labels and GUIDs.</summary>
        public string Key { get; }

        /// <summary>The location string, before any runtime transform.</summary>
        public string InternalId { get; }

        public string ProviderId { get; }
        public string ResourceType { get; }

        /// <summary>Names of the bundles this entry needs. Empty means it is not in a bundle.</summary>
        public IReadOnlyList<string> BundleNames { get; }

        public CatalogEntry(
            string address, string key, string internalId, string providerId,
            string resourceType, IReadOnlyList<string> bundleNames)
        {
            Address = address ?? string.Empty;
            Key = key ?? string.Empty;
            InternalId = internalId ?? string.Empty;
            ProviderId = providerId ?? string.Empty;
            ResourceType = resourceType ?? string.Empty;
            BundleNames = bundleNames ?? Array.Empty<string>();
        }
    }

    /// <summary>A remote entry that also depends on a bundle shipped inside the player.</summary>
    public sealed class CrossBoundaryEntry
    {
        public string Address { get; }

        /// <summary>The in-player bundles this remote entry needs.</summary>
        public IReadOnlyList<string> LocalBundles { get; }

        public CrossBoundaryEntry(string address, IReadOnlyList<string> localBundles)
        {
            Address = address ?? string.Empty;
            LocalBundles = localBundles ?? Array.Empty<string>();
        }
    }

    /// <summary>One bundle as the catalog records it.</summary>
    public sealed class CatalogBundle
    {
        /// <summary>The file name a request asks for — the last segment of <see cref="InternalId"/>.</summary>
        public string BundleName { get; }

        /// <summary>
        /// The catalog's own identifier for this bundle, usually a bare hash. Not a file name; see
        /// <see cref="CatalogReader.FileNameFromInternalId"/> for why the two are not interchangeable.
        /// </summary>
        public string CatalogName { get; }

        public string InternalId { get; }
        public string ProviderId { get; }

        /// <summary>Size the catalog claims, in bytes. 0 when the catalog did not record one.</summary>
        public long SizeBytes { get; }

        public uint Crc { get; }
        public string Hash { get; }

        /// <summary>Remote, local, or neither. Only remote bundles belong in a CDN folder.</summary>
        public BundleLocation Location { get; }

        /// <summary>How many entries in this catalog depend on this bundle.</summary>
        public int DependentEntryCount { get; internal set; }

        public CatalogBundle(
            string bundleName, string catalogName, string internalId, string providerId,
            long sizeBytes, uint crc, string hash, BundleLocation location)
        {
            BundleName = bundleName ?? string.Empty;
            CatalogName = catalogName ?? string.Empty;
            InternalId = internalId ?? string.Empty;
            ProviderId = providerId ?? string.Empty;
            SizeBytes = sizeBytes;
            Crc = crc;
            Hash = hash ?? string.Empty;
            Location = location;
        }
    }
}
