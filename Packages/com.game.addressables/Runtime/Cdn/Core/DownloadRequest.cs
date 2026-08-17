using System;
using System.Collections.Generic;
using UnityEngine.AddressableAssets;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// What to download and under what constraints.
    /// </summary>
    public class DownloadRequest
    {
        /// <summary>Addresses or labels to download the dependencies of.</summary>
        public IEnumerable<object> Keys { get; }

        /// <summary>How multiple keys combine. Union by default: download everything named.</summary>
        public Addressables.MergeMode MergeMode { get; }

        /// <summary>
        /// Free bytes that must remain after the download.
        /// </summary>
        /// <remarks>
        /// Filling a device's storage to the last byte breaks the OS, not just the game. The
        /// pre-flight check requires size + this much headroom to be free.
        /// </remarks>
        public long MinFreeDiskBytes { get; }

        /// <summary>Override the policy's metered-network rule for this request.</summary>
        /// <remarks>
        /// Set true after the player has agreed to a mobile-data download. Consent is per download,
        /// not a permanent setting, which is why it lives on the request rather than in settings.
        /// </remarks>
        public bool AllowMeteredOverride { get; }

        /// <summary>Create a request.</summary>
        public DownloadRequest(
            IEnumerable<object> keys,
            Addressables.MergeMode mergeMode = Addressables.MergeMode.Union,
            long minFreeDiskBytes = 64L * 1024 * 1024,
            bool allowMeteredOverride = false)
        {
            Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            MergeMode = mergeMode;
            MinFreeDiskBytes = minFreeDiskBytes;
            AllowMeteredOverride = allowMeteredOverride;
        }

        /// <summary>A request for one key.</summary>
        public static DownloadRequest For(object key, bool allowMeteredOverride = false) =>
            new DownloadRequest(new[] { key }, allowMeteredOverride: allowMeteredOverride);
    }

    /// <summary>
    /// What a completed download did.
    /// </summary>
    public class DownloadReport
    {
        /// <summary>Bytes transferred, as Addressables last reported them.</summary>
        public long BytesDownloaded { get; }

        /// <summary>Total the operation expected to transfer.</summary>
        public long TotalBytes { get; }

        /// <summary>Wall-clock duration.</summary>
        public TimeSpan Duration { get; }

        /// <summary>Attempts made, including the first. 1 means it worked first time.</summary>
        public int Attempts { get; }

        /// <summary>Whether a corrupt bundle was evicted and re-fetched — task 3.8.</summary>
        public bool RepairedCorruptBundle { get; }

        /// <summary>Mean bytes per second over the whole operation, or 0 if it took no measurable time.</summary>
        public double AverageBytesPerSecond => Duration.TotalSeconds > 0
            ? BytesDownloaded / Duration.TotalSeconds
            : 0;

        /// <summary>Create a report.</summary>
        public DownloadReport(long bytesDownloaded, long totalBytes, TimeSpan duration, int attempts, bool repairedCorruptBundle)
        {
            BytesDownloaded = bytesDownloaded;
            TotalBytes = totalBytes;
            Duration = duration;
            Attempts = attempts;
            RepairedCorruptBundle = repairedCorruptBundle;
        }

        /// <inheritdoc />
        public override string ToString() =>
            $"{BytesDownloaded:N0} B in {Duration.TotalSeconds:F1}s " +
            $"({AverageBytesPerSecond / 1024:F0} KB/s avg, {Attempts} attempt(s)" +
            $"{(RepairedCorruptBundle ? ", repaired a corrupt bundle" : "")})";
    }
}
