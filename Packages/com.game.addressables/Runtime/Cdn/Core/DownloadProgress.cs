using System;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// A download's state at one instant — task 3.4.
    /// </summary>
    /// <remarks>
    /// A struct, and delivered through <see cref="IProgress{T}"/>, so a download reporting at 4 Hz
    /// for several minutes allocates nothing in steady state. Every field is a value type for the
    /// same reason: one string in here would put an allocation in the progress loop.
    /// </remarks>
    public readonly struct DownloadProgress
    {
        /// <summary>Bytes transferred so far.</summary>
        public readonly long DownloadedBytes;

        /// <summary>
        /// Total bytes to transfer, or 0 while Addressables is still working it out.
        /// </summary>
        /// <remarks>
        /// Zero means "not known yet", not "nothing to download". GetDownloadStatus reports zero
        /// totals early in an operation (design doc §12 risk table), so never divide by this —
        /// use <see cref="IsSizeKnown"/> and show a "calculating" state instead of 0% or NaN.
        /// </remarks>
        public readonly long TotalBytes;

        /// <summary>Bytes per second, smoothed. Zero before the first measurement.</summary>
        public readonly double BytesPerSecond;

        /// <summary>
        /// Seconds remaining, or -1 when it cannot be estimated.
        /// </summary>
        /// <remarks>
        /// -1 rather than 0 or a guess: an ETA of "0 seconds" on a download that has not started is
        /// a lie the UI will render, and a fabricated estimate is worse than an honest "unknown".
        /// </remarks>
        public readonly double EtaSeconds;

        /// <summary>Whether <see cref="TotalBytes"/> is known yet.</summary>
        public bool IsSizeKnown => TotalBytes > 0;

        /// <summary>
        /// Fraction complete in 0..1, or 0 while the size is unknown.
        /// </summary>
        public float Percent => TotalBytes > 0
            ? (float)((double)DownloadedBytes / TotalBytes)
            : 0f;

        /// <summary>Create a progress snapshot.</summary>
        public DownloadProgress(long downloadedBytes, long totalBytes, double bytesPerSecond, double etaSeconds)
        {
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
            BytesPerSecond = bytesPerSecond;
            EtaSeconds = etaSeconds;
        }

        /// <summary>Nothing transferred, nothing known.</summary>
        public static DownloadProgress None => new DownloadProgress(0, 0, 0, -1);

        /// <inheritdoc />
        public override string ToString()
        {
            if (!IsSizeKnown)
                return $"{DownloadedBytes:N0} B of unknown total (calculating)";

            string eta = EtaSeconds >= 0 ? $"{EtaSeconds:F0}s" : "unknown";
            return $"{DownloadedBytes:N0}/{TotalBytes:N0} B ({Percent:P0}), {BytesPerSecond / 1024:F0} KB/s, ETA {eta}";
        }
    }
}
