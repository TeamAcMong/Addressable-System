using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Makes <see cref="LocalContentServer"/> misbehave on purpose — task 5.1, design doc §12.
    /// </summary>
    /// <remarks>
    /// WHY THIS EXISTS
    /// Every error path in the CDN layer is written for a failure that is, by nature, rare and not
    /// reproducible on demand. Without injection the only way to exercise a 503 or a mid-download
    /// disconnect is to wait for one, so those branches ship untested and the first time they run
    /// is in front of a player. The fault-injection matrix in design doc §12 exists to turn each of
    /// them into a test.
    ///
    /// Static and global because the server is a singleton and the tests drive it from outside.
    /// Every configuration method returns an IDisposable scope, so a test cannot leak a fault into
    /// the next one — a leaked 500 would fail an unrelated test with a baffling message.
    /// </remarks>
    public static class ServerFaults
    {
        private static int _statusToInject;
        private static string _pathSubstring;
        private static int _requestsToFail;
        private static int _requestsFailed;
        private static bool _dropConnection;
        private static long _bytesPerSecond;
        private static int _delayMilliseconds;

        /// <summary>Bytes per second the server will send, or 0 for unthrottled.</summary>
        public static long BytesPerSecond => _bytesPerSecond;

        /// <summary>Whether any fault is currently armed.</summary>
        public static bool IsActive =>
            _statusToInject != 0 || _dropConnection || _bytesPerSecond > 0 || _delayMilliseconds > 0;

        /// <summary>
        /// Answer matching requests with an HTTP error.
        /// </summary>
        /// <param name="status">Status to return, e.g. 503.</param>
        /// <param name="pathSubstring">Only paths containing this. Null matches everything.</param>
        /// <param name="requestCount">How many requests to fail before behaving normally again.</param>
        /// <remarks>
        /// A finite count is what makes retry testable: fail twice, succeed on the third, and the
        /// test can assert the client actually recovered rather than just that it gave up.
        /// </remarks>
        public static IDisposable InjectStatus(int status, string pathSubstring = null, int requestCount = int.MaxValue)
        {
            _statusToInject = status;
            _pathSubstring = pathSubstring;
            _requestsToFail = requestCount;
            _requestsFailed = 0;

            Debug.Log($"[ServerFaults] Injecting HTTP {status} for " +
                      $"{(pathSubstring ?? "all paths")}, {DescribeCount(requestCount)}");

            return new Scope();
        }

        /// <summary>
        /// Close the connection without a response, simulating a network drop.
        /// </summary>
        public static IDisposable InjectConnectionDrop(string pathSubstring = null, int requestCount = int.MaxValue)
        {
            _dropConnection = true;
            _pathSubstring = pathSubstring;
            _requestsToFail = requestCount;
            _requestsFailed = 0;

            Debug.Log($"[ServerFaults] Dropping connections for " +
                      $"{(pathSubstring ?? "all paths")}, {DescribeCount(requestCount)}");

            return new Scope();
        }

        /// <summary>
        /// Cap the send rate, so a local file behaves like a slow network.
        /// </summary>
        /// <remarks>
        /// Needed for the Phase 3 speed criterion: reading a local file completes instantly, so an
        /// unthrottled server measures the disk rather than anything the download layer computed.
        /// </remarks>
        public static IDisposable Throttle(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));

            _bytesPerSecond = bytesPerSecond;
            Debug.Log($"[ServerFaults] Throttling to {bytesPerSecond / 1024.0:F0} KB/s");

            return new Scope();
        }

        /// <summary>Delay before responding, simulating latency.</summary>
        public static IDisposable InjectLatency(int milliseconds)
        {
            _delayMilliseconds = Math.Max(0, milliseconds);
            return new Scope();
        }

        /// <summary>Disarm everything.</summary>
        public static void Clear()
        {
            _statusToInject = 0;
            _pathSubstring = null;
            _requestsToFail = 0;
            _requestsFailed = 0;
            _dropConnection = false;
            _bytesPerSecond = 0;
            _delayMilliseconds = 0;
        }

        /// <summary>
        /// Apply an armed fault to a request, if one matches.
        /// </summary>
        /// <returns>True when the request was answered by the fault and must not be served.</returns>
        internal static bool TryApply(string path, HttpListenerContext context, out int status)
        {
            status = 0;

            if (_delayMilliseconds > 0)
                Thread.Sleep(_delayMilliseconds);

            if (_statusToInject == 0 && !_dropConnection)
                return false;

            if (!string.IsNullOrEmpty(_pathSubstring) &&
                path.IndexOf(_pathSubstring, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (_requestsFailed >= _requestsToFail)
                return false;

            _requestsFailed++;

            if (_dropConnection)
            {
                try
                {
                    // Abort rather than close: a clean close with no body can be read as an empty
                    // 200, and the point is to look like the connection died.
                    context.Response.Abort();
                }
                catch (Exception)
                {
                    // Already torn down.
                }

                status = 0;
                return true;
            }

            status = _statusToInject;

            try
            {
                context.Response.StatusCode = status;
                context.Response.ContentLength64 = 0;
                context.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerFaults] Could not write the injected {status}: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Copy a stream, honouring the throttle when one is set.
        /// </summary>
        /// <remarks>
        /// Paces by sleeping between fixed-size chunks rather than by computing a per-byte delay:
        /// simpler, and accurate enough over the multi-second transfers this is used for. The chunk
        /// is deliberately small relative to the cap so the rate is smooth rather than bursty —
        /// a client measuring speed over a 250 ms window would otherwise see spikes and gaps.
        /// </remarks>
        internal static void CopyThrottled(Stream source, Stream destination)
        {
            long rate = _bytesPerSecond;

            if (rate <= 0)
            {
                source.CopyTo(destination);
                return;
            }

            // Ten chunks per second, floored so a very low cap still moves.
            int chunkSize = (int)Math.Max(1024, rate / 10);
            var buffer = new byte[chunkSize];
            var clock = Stopwatch.StartNew();
            long written = 0;

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, read);
                written += read;

                // Where this many bytes should have taken us, versus where we are.
                double expectedSeconds = (double)written / rate;
                double actualSeconds = clock.Elapsed.TotalSeconds;
                double aheadBy = expectedSeconds - actualSeconds;

                if (aheadBy > 0)
                    Thread.Sleep((int)(aheadBy * 1000));
            }
        }

        private static string DescribeCount(int count) =>
            count == int.MaxValue ? "until cleared" : $"{count} request(s)";

        /// <summary>Clears every fault when disposed.</summary>
        private sealed class Scope : IDisposable
        {
            public void Dispose() => Clear();
        }
    }
}
