using System.Collections.Generic;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Manifest of a complete build output, serialized to JSON.
    /// Contains bundle list, sizes, content hashes, and metadata for three consumers:
    /// - Task 1.6 (CatalogVerifier): authoritative bundle inventory to verify CDN URLs
    /// - Task 1.11 (ContentDiff): content hashes to detect what changed between builds
    /// - CI deploy: file list and integrity hashes for upload and post-upload verification
    ///
    /// Location: ServerData/build-manifest.json (never uploaded to CDN, archived privately).
    /// Serializer: Newtonsoft.Json (JsonConvert), not JsonUtility (cannot serialize complex nested types).
    /// </summary>
    [System.Serializable]
    public class BuildManifest
    {
        /// <summary>
        /// Schema version for backwards compatibility. Increment on breaking changes to structure.
        /// Consumers check this to know whether they understand the format.
        /// </summary>
        public string manifestVersion = "1.0";

        /// <summary>
        /// Application version string (e.g., "1.2.3").
        /// Aids traceability and cross-referencing with shipped app builds.
        /// </summary>
        public string appVersion = "";

        /// <summary>
        /// Target platform (e.g., "StandaloneWindows64", "iOS", "Android", "WebGL").
        /// Helps identify which manifest is for which platform build.
        /// </summary>
        public string platform = "";

        /// <summary>
        /// Build classification: "Full" for complete player content build, "Update" for content-update build.
        /// Used by CI to determine upload strategy and by 1.11 to guide patch computation.
        /// </summary>
        public string buildType = "Full";

        /// <summary>
        /// UTC timestamp when this build was created (ISO 8601 format, e.g., "2026-08-11T14:30:45Z").
        /// Enables sorting and temporal correlation with other artifacts.
        /// </summary>
        public string buildDate = "";

        /// <summary>
        /// Git commit SHA (typically first 7–12 characters) from which this build was made.
        /// Enables traceability: "which source code version produced this output?"
        /// Empty string if not available (e.g., local build).
        /// </summary>
        public string gitSha = "";

        /// <summary>
        /// Total size in bytes of all bundles (sum of all BundleInfo.sizeBytes).
        /// For diagnostics and capacity planning; not a hard constraint.
        /// </summary>
        public long totalSizeBytes = 0;

        /// <summary>
        /// Metadata for the main catalog file (catalog.json and its .hash file).
        /// Populated by the build step that emits content catalog.
        /// </summary>
        public CatalogInfo catalog = new CatalogInfo();

        /// <summary>
        /// Complete list of asset bundles produced by this build.
        /// Each entry includes name, file path, size, and content hash.
        /// Task 1.11 compares contentHash values between old and new manifests to detect actual changes.
        /// </summary>
        public List<BundleInfo> bundles = new List<BundleInfo>();
    }

    /// <summary>
    /// Metadata for the catalog file pair (catalog.json and catalog.json.hash).
    /// Used by CI deploy to verify upload integrity.
    /// </summary>
    [System.Serializable]
    public class CatalogInfo
    {
        /// <summary>
        /// File name of the main catalog (e.g., "catalog.json").
        /// Relative to the output directory (ServerData/).
        /// </summary>
        public string fileName = "";

        /// <summary>
        /// Size of catalog.json in bytes.
        /// </summary>
        public long sizeBytes = 0;

        /// <summary>
        /// SHA256 hash of the catalog file (format: "sha256:hexstring").
        /// Computed from the actual file bytes on disk after build.
        /// Used by CI to verify integrity after upload.
        /// </summary>
        public string contentHash = "";

        /// <summary>
        /// File name of the catalog hash file (e.g., "catalog.json.hash").
        /// This is a small text file containing the hash value as plain text.
        /// Relative to the output directory (ServerData/).
        /// </summary>
        public string hashFileName = "";

        /// <summary>
        /// Size of the .hash file in bytes.
        /// </summary>
        public long hashFileSizeBytes = 0;

        /// <summary>
        /// SHA256 hash of the catalog.json.hash file itself.
        /// Enables verification of the metadata file.
        /// </summary>
        public string hashFileContentHash = "";
    }

    /// <summary>
    /// Information about a single asset bundle in the build output.
    /// Used by all three consumers for bundle identification, verification, and change detection.
    /// </summary>
    [System.Serializable]
    public class BundleInfo
    {
        /// <summary>
        /// Internal bundle name from the Addressables build (e.g., "assets_scene_main", "assets_ui_common").
        /// Used by task 1.6 (CatalogVerifier) to cross-reference with catalog entries and verify URLs.
        /// </summary>
        public string name = "";

        /// <summary>
        /// File name of this bundle on disk (e.g., "assets_scene_main.bundle").
        /// Used by CI deploy to know what file to upload; by task 1.6 to construct verification URLs.
        /// Relative to the output directory (ServerData/bundles/ or similar).
        /// </summary>
        public string fileName = "";

        /// <summary>
        /// Size of this bundle file in bytes.
        /// Aids in progress estimation, parallelization, and capacity planning.
        /// </summary>
        public long sizeBytes = 0;

        /// <summary>
        /// SHA256 hash of the bundle file content (format: "sha256:hexstring").
        /// This is the authoritative content hash, computed from actual file bytes on disk.
        ///
        /// WHY FILE HASH, NOT ASSET HASH:
        /// On content-update builds, Unity rebuilds bundles even if their asset content is identical.
        /// The file hash (SHA256 of bytes) allows task 1.11 (ContentDiff) to distinguish:
        /// - "Rebuilt but identical" (hash matches prev manifest) → no re-download needed
        /// - "Actually different" (hash differs) → player must re-download
        ///
        /// BundleBuildResult.Hash is an internal Unity value whose content is unspecified
        /// (may reflect asset references, serialized state, or build-time metadata).
        /// It is not reliable for "did content change" across rebuilds, so we do not use it.
        ///
        /// Used by: CI deploy for post-upload integrity check; task 1.11 for patch size computation.
        /// </summary>
        public string contentHash = "";

        /// <summary>
        /// Unity's internal hash (from BundleBuildResult.Hash).
        /// Stored for reference and correlation with Unity's build output, but NOT used for
        /// content comparison or verification. Included for diagnostics and build reproducibility tracing.
        /// </summary>
        public string unityHash = "";

        /// <summary>
        /// CRC checksum computed by Unity for this bundle (from BundleBuildResult.Crc).
        /// Stored as a secondary integrity reference. Not used as primary verification.
        /// </summary>
        public uint unityCrc = 0;
    }
}
