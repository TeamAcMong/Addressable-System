namespace AddressableManager.Cdn
{
    /// <summary>
    /// The most recent download progress, readable without holding the call that produced it.
    /// </summary>
    /// <remarks>
    /// <c>DownloadAsync</c> reports progress only to the <c>IProgress&lt;DownloadProgress&gt;</c> its
    /// caller passes, so nothing outside that call can see it — and a background prefetch usually
    /// passes <c>null</c>, which is a reasonable thing to do. The CDN Manager's Runtime Monitor tab
    /// therefore had a progress bar it could never fill: the only assignment to it in the whole file
    /// set it to zero. A bar frozen at "No download in progress" while content is visibly downloading
    /// reads as "the CDN is not working", and it sent at least one team off diagnosing the wrong thing.
    ///
    /// This is deliberately a snapshot rather than an event. An event would need subscribers to manage
    /// their lifetime across domain reloads and play-mode transitions — the editor tab already polls
    /// once a second, and a runtime caller that wants every tick still has <c>IProgress</c>.
    ///
    /// Editor-tooling and coarse UI only. It reports whichever download last ticked, so with several
    /// concurrent downloads it is a sample, not a total, and <see cref="Current"/> is a plain struct
    /// read with no lock. Anything needing exact per-download numbers should pass its own
    /// <c>IProgress</c>.
    /// </remarks>
    public static class CdnDownloadMonitor
    {
        private static DownloadProgress _current;
        private static bool _isDownloading;

        /// <summary>True between the first tick of a download and its completion or failure.</summary>
        public static bool IsDownloading => _isDownloading;

        /// <summary>
        /// The last progress reported by any download. Meaningful only while
        /// <see cref="IsDownloading"/> is true; afterwards it holds the final values.
        /// </summary>
        public static DownloadProgress Current => _current;

        /// <summary>Record a tick. Called by the download path regardless of the caller's IProgress.</summary>
        internal static void Report(DownloadProgress progress)
        {
            _current = progress;
            _isDownloading = true;
        }

        /// <summary>Mark the end of a download, successful or not, keeping the final numbers readable.</summary>
        internal static void Complete(DownloadProgress final)
        {
            _current = final;
            _isDownloading = false;
        }

        /// <summary>Mark the end of a download that produced no final figures.</summary>
        internal static void Complete() => _isDownloading = false;

        /// <summary>
        /// Clear the snapshot when entering play mode with domain reload disabled, so a stale
        /// "downloading" state from the previous session does not persist.
        /// </summary>
#if UNITY_EDITOR
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _current = default;
            _isDownloading = false;
        }
#endif
    }
}
