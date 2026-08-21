using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Debug = UnityEngine.Debug;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Downloads content with progress, cancellation, retry and repair — tasks 3.2 to 3.8.
    /// </summary>
    public interface IDownloadService
    {
#if UNITASK_PRESENT
        /// <summary>Bytes still to download for the given keys. Zero means nothing to do.</summary>
        UniTask<CdnResult<long>> GetDownloadSizeAsync(DownloadRequest request, CancellationToken cancellationToken = default);

        /// <summary>Download the dependencies of the given keys.</summary>
        UniTask<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default);
#else
        /// <summary>Bytes still to download for the given keys. Zero means nothing to do.</summary>
        Task<CdnResult<long>> GetDownloadSizeAsync(DownloadRequest request, CancellationToken cancellationToken = default);

        /// <summary>Download the dependencies of the given keys.</summary>
        Task<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default);
#endif
    }

    /// <summary>
    /// The download orchestrator.
    /// </summary>
    /// <remarks>
    /// WHY SIZE RETURNS A RESULT RATHER THAN A NUMBER
    /// Zero is a real answer — everything is cached — and it is also what a failed lookup would
    /// naturally return. The existing AssetLoader.GetDownloadSizeAsync conflates them, so a caller
    /// cannot tell "nothing to download, carry on" from "could not reach the CDN, do not carry on".
    /// Repo invariant 4 exists because of that bug; this returns CdnResult&lt;long&gt;.
    ///
    /// WHAT RETRY CAN AND CANNOT DO
    /// Retrying re-runs the whole operation, but Unity's bundle cache makes already-completed
    /// bundles free the second time, so a retry after a mid-download failure resumes in effect
    /// rather than starting over. That is also what makes cancel-and-restart cheap.
    ///
    /// PROGRESS ALLOCATES NOTHING IN STEADY STATE
    /// DownloadProgress is a readonly struct and the polling loop reuses no collections, so a
    /// multi-minute download at 4 Hz produces no garbage. The one allocation per operation is the
    /// handle Addressables itself creates.
    /// </remarks>
    public class DownloadService : IDownloadService
    {
        private readonly NetworkPolicy _network;
        private readonly RetryPolicy _retry;
        private readonly IHostRewriter _rewriter;
        private readonly int _progressIntervalMilliseconds;

        /// <summary>Create a service.</summary>
        /// <param name="progressHz">Progress reports per second. Design doc §3.4 defaults to 4.</param>
        public DownloadService(
            NetworkPolicy network,
            RetryPolicy retry = null,
            IHostRewriter rewriter = null,
            int progressHz = 4)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _retry = retry ?? RetryPolicy.Default;
            _rewriter = rewriter;
            _progressIntervalMilliseconds = Mathf.Clamp(1000 / Mathf.Max(1, progressHz), 16, 2000);
        }

        private string ActiveUrl => _rewriter?.ActiveBaseUrl;

        // ================= 3.2 size =================

#if UNITASK_PRESENT
        /// <inheritdoc />
        public async UniTask<CdnResult<long>> GetDownloadSizeAsync(
            DownloadRequest request, CancellationToken cancellationToken = default)
#else
        /// <inheritdoc />
        public async Task<CdnResult<long>> GetDownloadSizeAsync(
            DownloadRequest request, CancellationToken cancellationToken = default)
#endif
        {
            if (request == null)
                return CdnResult<long>.Failure(CdnErrorCode.Unknown, "Download request is null");

            var keys = request.Keys.ToList();
            if (keys.Count == 0)
            {
                // An empty request is a caller mistake, not "nothing to download": answering 0 would
                // let a bad key list look like fully-cached content.
                return CdnResult<long>.Failure(
                    CdnErrorCode.Unknown, "Download request contains no keys",
                    hint: "An empty key list cannot be distinguished from fully cached content, so it is " +
                          "rejected rather than answered with zero.");
            }

            AsyncOperationHandle<long> handle;
            try
            {
                handle = Addressables.GetDownloadSizeAsync((System.Collections.IEnumerable)keys);
            }
            catch (Exception ex)
            {
                return CdnResult<long>.Failure(CdnErrorMapper.Map(ex, ActiveUrl, _network.IsReachable));
            }

            try
            {
#if UNITASK_PRESENT
                await handle.ToUniTask(cancellationToken: cancellationToken);
#else
                await handle.Task;
#endif
            }
            catch (OperationCanceledException)
            {
                SafeRelease(handle);
                return CdnResult<long>.Cancelled("Cancelled while measuring the download size");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<long>.Failure(CdnErrorMapper.Map(ex, ActiveUrl, _network.IsReachable));
            }

            bool succeeded = handle.Status == AsyncOperationStatus.Succeeded;
            long size = succeeded ? handle.Result : 0;
            Exception failure = handle.OperationException;
            SafeRelease(handle);

            return succeeded
                ? CdnResult<long>.Success(size)
                : CdnResult<long>.Failure(CdnErrorMapper.Map(failure, ActiveUrl, _network.IsReachable));
        }

        // ================= 3.3 to 3.6, 3.8 download =================

#if UNITASK_PRESENT
        /// <inheritdoc />
        public async UniTask<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default)
#else
        /// <inheritdoc />
        public async Task<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress> progress = null, CancellationToken cancellationToken = default)
#endif
        {
            if (request == null)
                return CdnResult<DownloadReport>.Failure(CdnErrorCode.Unknown, "Download request is null");

            var stopwatch = Stopwatch.StartNew();

            // ---- 3.6 pre-flight ----
            var sizeResult = await GetDownloadSizeAsync(request, cancellationToken);
            if (sizeResult.IsFailure)
                return CdnResult<DownloadReport>.Failure(sizeResult.Error);

            long requiredBytes = sizeResult.Value;

            if (requiredBytes == 0)
            {
                // Everything is cached. A success with zero bytes, distinct from a failed lookup.
                progress?.Report(new DownloadProgress(0, 0, 0, 0));
                return CdnResult<DownloadReport>.Success(
                    new DownloadReport(0, 0, stopwatch.Elapsed, 1, false));
            }

            var preflight = RunPreflightChecks(request, requiredBytes);
            if (preflight.IsFailure)
                return CdnResult<DownloadReport>.Failure(preflight.Error);

            // ---- attempt loop ----
            int attempts = 0;
            bool repaired = false;

            while (true)
            {
                attempts++;

                var attempt = await RunOneDownloadAttempt(request, requiredBytes, progress, cancellationToken);

                if (attempt.IsSuccess)
                {
                    return CdnResult<DownloadReport>.Success(new DownloadReport(
                        attempt.Value.DownloadedBytes,
                        attempt.Value.TotalBytes > 0 ? attempt.Value.TotalBytes : requiredBytes,
                        stopwatch.Elapsed, attempts, repaired));
                }

                if (attempt.IsCancelled)
                    return CdnResult<DownloadReport>.Failure(attempt.Error);

                // ---- 3.8 CRC auto-repair, once ----
                if (attempt.Error.Code == CdnErrorCode.BundleCrcMismatch && !repaired)
                {
                    repaired = true;
                    Debug.LogWarning("[CdnDownloadService] Corrupt bundle detected; evicting the cached " +
                                     "dependencies for these keys and retrying once.");

                    var cleared = await ClearDependencyCache(request, cancellationToken);
                    if (cleared.IsFailure)
                        return CdnResult<DownloadReport>.Failure(cleared.Error);

                    continue;
                }

                if (!_retry.ShouldRetry(attempt.Error, attempts))
                    return CdnResult<DownloadReport>.Failure(attempt.Error);

                var delay = _retry.GetDelay(attempts);
                Debug.Log($"[CdnDownloadService] Attempt {attempts} failed ({attempt.Error.Code}); " +
                          $"retrying in {delay.TotalSeconds:F1}s");

                try
                {
#if UNITASK_PRESENT
                    await UniTask.Delay(delay, cancellationToken: cancellationToken);
#else
                    await Task.Delay(delay, cancellationToken);
#endif
                }
                catch (OperationCanceledException)
                {
                    return CdnResult<DownloadReport>.Cancelled("Cancelled while waiting to retry");
                }
            }
        }

        /// <summary>
        /// Pre-flight gates — task 3.6. Each maps to its own error code so a caller can respond
        /// differently to "no network", "not on mobile data" and "no room".
        /// </summary>
        private CdnResult<bool> RunPreflightChecks(DownloadRequest request, long requiredBytes)
        {
            if (!request.AllowMeteredOverride)
            {
                var allowed = _network.CanDownload();
                if (allowed.IsFailure)
                    return CdnResult<bool>.Failure(allowed.Error);
            }
            else if (!_network.IsReachable)
            {
                // The metered override waives the metered rule, not the need for a network.
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Offline, "No network connection",
                    hint: "The metered override allows mobile data; it cannot conjure a connection.");
            }

            long needed = requiredBytes + request.MinFreeDiskBytes;
            long free = GetFreeDiskBytes();

            if (free >= 0 && free < needed)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.InsufficientDiskSpace,
                    $"Need {needed:N0} B free ({requiredBytes:N0} B of content plus " +
                    $"{request.MinFreeDiskBytes:N0} B headroom) but only {free:N0} B is available",
                    hint: $"Ask the player to free about {(needed - free) / (1024 * 1024) + 1} MB. " +
                          "The headroom is deliberate: filling the volume breaks the OS, not just the game.");
            }

            return CdnResult<bool>.Success(true);
        }

        /// <summary>
        /// Free bytes on the volume holding the bundle cache, or -1 when it cannot be determined.
        /// </summary>
        /// <remarks>
        /// -1 rather than 0 or long.MaxValue: unknown must not read as "full" and block a download
        /// that would have worked, nor as "infinite" and skip the check silently. The caller treats
        /// -1 as "check skipped". DriveInfo is unavailable on some platforms and throws on others.
        /// </remarks>
        private static long GetFreeDiskBytes()
        {
            try
            {
                string path = Application.persistentDataPath;
                if (string.IsNullOrEmpty(path)) return -1;

                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;

                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>One download attempt, with polling progress.</summary>
#if UNITASK_PRESENT
        private async UniTask<CdnResult<DownloadStatus>> RunOneDownloadAttempt(
#else
        private async Task<CdnResult<DownloadStatus>> RunOneDownloadAttempt(
#endif
            DownloadRequest request, long expectedBytes,
            IProgress<DownloadProgress> progress, CancellationToken cancellationToken)
        {
            var keys = request.Keys.ToList();

            AsyncOperationHandle handle;
            try
            {
                // autoReleaseHandle: false — the handle is needed for GetDownloadStatus while the
                // operation runs, and releasing it automatically would invalidate it mid-poll.
                handle = Addressables.DownloadDependenciesAsync(
                    (System.Collections.IEnumerable)keys, request.MergeMode, false);
            }
            catch (Exception ex)
            {
                return CdnResult<DownloadStatus>.Failure(CdnErrorMapper.Map(ex, ActiveUrl, _network.IsReachable));
            }

            // Exponential moving average over instantaneous rates. A raw delta is far too noisy at
            // 4 Hz to drive an ETA a human reads — it swings by an order of magnitude between polls.
            const double smoothing = 0.3;
            double smoothedBytesPerSecond = 0;
            long lastBytes = 0;
            var lastPollAt = Stopwatch.StartNew();
            var status = default(DownloadStatus);

            while (!handle.IsDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    // 3.5: release the handle but leave the partial cache alone. Unity keeps what it
                    // already wrote, which is what makes a restart resume rather than start over.
                    SafeRelease(handle);
                    return CdnResult<DownloadStatus>.Cancelled("The download was cancelled");
                }

                try
                {
#if UNITASK_PRESENT
                    await UniTask.Delay(_progressIntervalMilliseconds, cancellationToken: cancellationToken);
#else
                    await Task.Delay(_progressIntervalMilliseconds, cancellationToken);
#endif
                }
                catch (OperationCanceledException)
                {
                    SafeRelease(handle);
                    return CdnResult<DownloadStatus>.Cancelled("The download was cancelled");
                }

                if (!handle.IsValid())
                    break;

                status = handle.GetDownloadStatus();

                double elapsedSeconds = lastPollAt.Elapsed.TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    long delta = status.DownloadedBytes - lastBytes;
                    if (delta >= 0)
                    {
                        double instant = delta / elapsedSeconds;
                        smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                            ? instant
                            : (smoothing * instant) + ((1 - smoothing) * smoothedBytesPerSecond);
                    }

                    lastBytes = status.DownloadedBytes;
                    lastPollAt.Restart();
                }

                long total = status.TotalBytes > 0 ? status.TotalBytes : expectedBytes;

                // ETA is -1 unless both the total and a rate are known. Reporting 0, or dividing by
                // a zero total, is how a progress bar ends up claiming "0 seconds remaining" for a
                // minute — see the risk table in design doc §12.
                double eta = -1;
                if (total > 0 && smoothedBytesPerSecond > 1)
                {
                    long remaining = total - status.DownloadedBytes;
                    if (remaining > 0) eta = remaining / smoothedBytesPerSecond;
                    else eta = 0;
                }

                var tick = new DownloadProgress(
                    status.DownloadedBytes, status.TotalBytes, smoothedBytesPerSecond, eta);

                // The monitor is fed regardless of whether the caller wanted progress: a background
                // prefetch passing null is normal, and it is exactly the case where an observer
                // outside the call has no other way to know anything is happening.
                CdnDownloadMonitor.Report(tick);
                progress?.Report(tick);
            }

            bool succeeded = handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded;
            Exception failure = handle.IsValid() ? handle.OperationException : null;

            if (handle.IsValid())
                status = handle.GetDownloadStatus();

            SafeRelease(handle);

            if (!succeeded)
            {
                // Without this the monitor would report "downloading" forever after any failure -
                // the same frozen-UI lie this monitor exists to remove.
                CdnDownloadMonitor.Complete();
                return CdnResult<DownloadStatus>.Failure(CdnErrorMapper.Map(failure, ActiveUrl, _network.IsReachable));
            }

            var final = new DownloadProgress(
                status.DownloadedBytes,
                status.TotalBytes > 0 ? status.TotalBytes : status.DownloadedBytes,
                smoothedBytesPerSecond, 0);

            CdnDownloadMonitor.Complete(final);
            progress?.Report(final);

            return CdnResult<DownloadStatus>.Success(status);
        }

        /// <summary>Evict cached bundles for the request's keys — task 3.8.</summary>
#if UNITASK_PRESENT
        private async UniTask<CdnResult<bool>> ClearDependencyCache(
#else
        private async Task<CdnResult<bool>> ClearDependencyCache(
#endif
            DownloadRequest request, CancellationToken cancellationToken)
        {
            AsyncOperationHandle<bool> handle;
            try
            {
                handle = Addressables.ClearDependencyCacheAsync(
                    (System.Collections.IEnumerable)request.Keys.ToList(), false);
            }
            catch (Exception ex)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown, $"Could not clear the dependency cache: {ex.Message}", exception: ex);
            }

            try
            {
#if UNITASK_PRESENT
                await handle.ToUniTask(cancellationToken: cancellationToken);
#else
                await handle.Task;
#endif
            }
            catch (OperationCanceledException)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Cancelled("Cancelled while clearing the dependency cache");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown, $"Clearing the dependency cache failed: {ex.Message}", exception: ex);
            }

            bool ok = handle.Status == AsyncOperationStatus.Succeeded && handle.Result;
            SafeRelease(handle);

            if (!ok)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.BundleCrcMismatch,
                    "A bundle failed verification and the cached copy could not be evicted",
                    hint: "Without eviction the retry would read the same corrupt bytes, so it is not " +
                          "attempted. Clearing the app's cache manually is the remaining option.");
            }

            return CdnResult<bool>.Success(true);
        }

        private static void SafeRelease(AsyncOperationHandle handle)
        {
            try
            {
                if (handle.IsValid()) Addressables.Release(handle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CdnDownloadService] Failed to release a handle: {ex.Message}");
            }
        }

        private static void SafeRelease<T>(AsyncOperationHandle<T> handle)
        {
            try
            {
                if (handle.IsValid()) Addressables.Release(handle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CdnDownloadService] Failed to release a handle: {ex.Message}");
            }
        }
    }
}
