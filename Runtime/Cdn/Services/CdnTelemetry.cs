using System;
using AddressableManager.Configs;
using UnityEngine;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Where CDN events go — task 4.4.
    /// </summary>
    /// <remarks>
    /// An interface with no transport behind it, on purpose. Bundling an analytics client would
    /// force a dependency on whichever one this package guessed, and every team already has one.
    /// Implement this and hand it to <see cref="CdnDiagnostics.Sink"/>.
    ///
    /// Everything here is fire-and-forget: an implementation that throws or blocks would turn a
    /// telemetry problem into a content-delivery problem, so <see cref="CdnDiagnostics"/> catches
    /// on the caller's behalf.
    /// </remarks>
    public interface ICdnTelemetry
    {
        /// <summary>A CDN operation started.</summary>
        void OnOperationStarted(string operation, string environmentId);

        /// <summary>A CDN operation finished successfully.</summary>
        void OnOperationSucceeded(string operation, string environmentId, TimeSpan duration);

        /// <summary>A CDN operation failed.</summary>
        void OnOperationFailed(string operation, string environmentId, CdnError error, TimeSpan duration);

        /// <summary>A download finished. Bytes and duration are the interesting part.</summary>
        void OnDownloadCompleted(DownloadReport report, string environmentId);
    }

    /// <summary>
    /// Routes CDN events to a telemetry sink and the console — task 4.4.
    /// </summary>
    /// <remarks>
    /// Console output goes through DebugSettings.IsVerbose — the same gate AssetLoader already uses
    /// on its hot paths — rather than introducing a second verbosity switch.
    /// </remarks>
    public static class CdnDiagnostics
    {
        /// <summary>Where events go. Null means console only.</summary>
        public static ICdnTelemetry Sink { get; set; }

        /// <summary>Record the start of an operation.</summary>
        public static void OperationStarted(string operation, string environmentId)
        {
            Log($"{operation} started ({environmentId})");
            Safely(() => Sink?.OnOperationStarted(operation, environmentId));
        }

        /// <summary>Record a successful operation.</summary>
        public static void OperationSucceeded(string operation, string environmentId, TimeSpan duration)
        {
            Log($"{operation} succeeded in {duration.TotalSeconds:F2}s ({environmentId})");
            Safely(() => Sink?.OnOperationSucceeded(operation, environmentId, duration));
        }

        /// <summary>Record a failed operation.</summary>
        public static void OperationFailed(string operation, string environmentId, CdnError error, TimeSpan duration)
        {
            // Failures are logged regardless of verbosity. A CDN failure is the thing someone will
            // be reading the log to find, and a quiet build hiding it would be the wrong default.
            Debug.LogError($"[Cdn] {operation} failed after {duration.TotalSeconds:F2}s ({environmentId})\n{error}");
            Safely(() => Sink?.OnOperationFailed(operation, environmentId, error, duration));
        }

        /// <summary>Record a completed download.</summary>
        public static void DownloadCompleted(DownloadReport report, string environmentId)
        {
            if (report == null) return;

            Log($"download completed: {report}");
            Safely(() => Sink?.OnDownloadCompleted(report, environmentId));
        }

        /// <summary>
        /// Call a sink method without letting it break the caller.
        /// </summary>
        /// <remarks>
        /// A telemetry sink is third-party code on a hot path. If it throws, the download that
        /// triggered it must still finish — an analytics outage is not a reason to stop delivering
        /// content.
        /// </remarks>
        private static void Safely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Cdn] The telemetry sink threw and was ignored: {ex.Message}");
            }
        }

        private static void Log(string message)
        {
            if (DebugSettings.IsVerbose)
                Debug.Log($"[Cdn] {message}");
        }
    }
}
