using System;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Why a CDN operation failed. Design doc §8.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddressableManager.Core.LoadErrorCode"/> on purpose: that one describes failures of an
    /// individual asset load inside the process, this one describes failures of the content
    /// delivery layer around it. A caller handling "the CDN is unreachable" and a caller handling
    /// "this address is not in the catalog" are doing different things.
    ///
    /// Values are explicit so telemetry stays comparable across versions. Append, never renumber.
    /// </remarks>
    public enum CdnErrorCode
    {
        /// <summary>Success.</summary>
        None = 0,

        /// <summary>Reachability check failed. Cached content is still playable.</summary>
        Offline = 1,

        /// <summary>First install with no cache and no network: there is nothing to play.</summary>
        NoContentAvailableOffline = 2,

        /// <summary>On a metered connection while the policy requires an unmetered one.</summary>
        MeteredNetworkBlocked = 3,

        /// <summary>404 on the catalog or its hash file. A deploy is broken.</summary>
        CatalogNotFound = 10,

        /// <summary>The catalog downloaded but is malformed or truncated.</summary>
        CatalogParseFailed = 11,

        /// <summary>The catalog was built for a different player version.</summary>
        CatalogVersionIncompatible = 12,

        /// <summary>404 on a bundle. The catalog references content that was never uploaded.</summary>
        BundleNotFound = 20,

        /// <summary>A bundle failed CRC or hash verification.</summary>
        BundleCrcMismatch = 21,

        /// <summary>5xx from the origin or edge.</summary>
        ServerError = 30,

        /// <summary>The request exceeded its timeout.</summary>
        Timeout = 31,

        /// <summary>401 or 403.</summary>
        Unauthorized = 32,

        /// <summary>Not enough disk space to cache the content.</summary>
        InsufficientDiskSpace = 40,

        /// <summary>The caller cancelled. Not a failure to report.</summary>
        Cancelled = 50,

        /// <summary>Unmapped. Log with the full exception and treat as non-retryable.</summary>
        Unknown = 999
    }

    /// <summary>
    /// A CDN failure: what went wrong, whether retrying can help, and what the caller should do.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="AddressableManager.Core.LoadError"/> (code, message, hint, exception) and adds the three things
    /// that only matter over a network: the HTTP status, the URL, and whether a retry has any
    /// chance of succeeding.
    /// </remarks>
    public class CdnError
    {
        /// <summary>What class of failure this is.</summary>
        public CdnErrorCode Code { get; }

        /// <summary>What happened, in one line.</summary>
        public string Message { get; }

        /// <summary>What to do about it. Empty when the message already says.</summary>
        public string Hint { get; }

        /// <summary>The URL involved, when there was one.</summary>
        public string Url { get; }

        /// <summary>
        /// HTTP status, or 0 when the failure happened before a response arrived (DNS, timeout,
        /// no route). Zero is "no response", not "success" — check <see cref="Code"/> for that.
        /// </summary>
        public int HttpStatusCode { get; }

        /// <summary>The underlying exception, when the failure came from one.</summary>
        public Exception Exception { get; }

        /// <summary>
        /// Whether retrying the same operation can succeed without shipping a new build.
        /// </summary>
        /// <remarks>
        /// "Retryable" does not mean "retry immediately and silently". Several retryable codes
        /// need something to change first — reconnecting, the user freeing disk space, a token
        /// refresh, evicting a corrupt bundle. The precondition is in <see cref="Hint"/>. Retrying
        /// a non-retryable code cannot help: those mean the content on the CDN is wrong, and no
        /// amount of asking again will fix a 404 on a bundle that was never uploaded.
        /// </remarks>
        public bool IsRetryable { get; }

        /// <summary>
        /// Create an error. <paramref name="isRetryable"/> defaults to the classification for
        /// <paramref name="code"/>; pass it explicitly only when the call site knows better.
        /// </summary>
        public CdnError(
            CdnErrorCode code,
            string message,
            string hint = null,
            string url = null,
            int httpStatusCode = 0,
            Exception exception = null,
            bool? isRetryable = null)
        {
            Code = code;
            Message = message ?? string.Empty;
            Hint = hint ?? string.Empty;
            Url = url ?? string.Empty;
            HttpStatusCode = httpStatusCode;
            Exception = exception;
            IsRetryable = isRetryable ?? IsRetryableByDefault(code);
        }

        /// <summary>
        /// Default retry classification, from the table in design doc §8.
        /// </summary>
        public static bool IsRetryableByDefault(CdnErrorCode code)
        {
            switch (code)
            {
                // Retrying works once the network comes back.
                case CdnErrorCode.Offline:
                case CdnErrorCode.NoContentAvailableOffline:

                // Retrying works once the user allows it, frees space, or a token is refreshed.
                case CdnErrorCode.MeteredNetworkBlocked:
                case CdnErrorCode.Unauthorized:
                case CdnErrorCode.InsufficientDiskSpace:

                // Retrying works after evicting the bad bundle.
                case CdnErrorCode.BundleCrcMismatch:

                // Transient at the server end.
                case CdnErrorCode.ServerError:
                case CdnErrorCode.Timeout:
                    return true;

                // The content itself is wrong. Asking again returns the same wrong content, or the
                // same 404. These need a fixed deploy or a new player build.
                case CdnErrorCode.CatalogNotFound:
                case CdnErrorCode.CatalogParseFailed:
                case CdnErrorCode.CatalogVersionIncompatible:
                case CdnErrorCode.BundleNotFound:

                // Not failures to retry.
                case CdnErrorCode.None:
                case CdnErrorCode.Cancelled:

                // Unmapped: treat as permanent rather than retry forever against an unknown cause.
                case CdnErrorCode.Unknown:
                default:
                    return false;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var text = $"[{Code}] {Message}";

            if (HttpStatusCode > 0)
                text += $" (HTTP {HttpStatusCode})";

            if (!string.IsNullOrEmpty(Url))
                text += $"\n  URL: {Url}";

            if (!string.IsNullOrEmpty(Hint))
                text += $"\n  Hint: {Hint}";

            if (!IsRetryable && Code != CdnErrorCode.None && Code != CdnErrorCode.Cancelled)
                text += "\n  Not retryable: retrying returns the same result.";

            if (Exception != null)
                text += $"\n  Exception: {Exception.GetType().Name}: {Exception.Message}";

            return text;
        }
    }
}
