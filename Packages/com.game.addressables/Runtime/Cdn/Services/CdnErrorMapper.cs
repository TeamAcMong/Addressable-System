using System;
using UnityEngine.ResourceManagement.Exceptions;
using UnityEngine.ResourceManagement.Util;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Turns an Addressables exception into a <see cref="CdnErrorCode"/> — task 3.7.
    /// </summary>
    /// <remarks>
    /// CLASSIFY FROM THE RESPONSE CODE, NOT THE MESSAGE
    /// Design doc §8 is explicit about this, and the reason is sitting in this repo:
    /// AssetLoader.DetermineErrorCode matches substrings against exception text, so it reclassifies
    /// whenever Unity rewords a message and cannot tell a 404 from a 500 at all. The HTTP status is
    /// the fact; the message is prose about the fact.
    ///
    /// VERIFIED AGAINST THE PINNED VERSION
    /// The doc flagged that the exception hierarchy moved between Addressables 1.x and 2.x and
    /// budgeted time to confirm it. Confirmed for 2.9.1:
    ///   RemoteProviderException : ProviderException      (Exceptions.cs:166)
    ///   RemoteProviderException.WebRequestResult         (Exceptions.cs:188) — may be null
    ///   UnityWebRequestResult.ResponseCode : long        (UnityWebRequestUtilities.cs:146)
    ///   UnityWebRequestResult.Result : UnityWebRequest.Result
    /// Both types are public. WebRequestResult is null when the failure happened before a response
    /// existed — DNS, no route, a cancelled request — which is itself informative.
    ///
    /// Substring matching survives only as the last resort, and only to reach Unknown with a hint
    /// rather than to claim a specific code.
    /// </remarks>
    public static class CdnErrorMapper
    {
        /// <summary>
        /// Map an exception to a CDN error.
        /// </summary>
        /// <param name="exception">The failure from Addressables.</param>
        /// <param name="fallbackUrl">URL to report when the exception does not carry one.</param>
        /// <param name="isReachable">
        /// Whether the network was up. Decides between Offline and a server-side fault when there
        /// is no response to inspect.
        /// </param>
        public static CdnError Map(Exception exception, string fallbackUrl = null, bool isReachable = true)
        {
            if (exception == null)
            {
                return new CdnError(
                    CdnErrorCode.Unknown,
                    "The operation failed without reporting an exception",
                    hint: "Addressables can fail an operation without attaching one. The operation's " +
                          "own status is the only detail available.",
                    url: fallbackUrl);
            }

            if (exception is OperationCanceledException)
                return new CdnError(CdnErrorCode.Cancelled, "The operation was cancelled");

            var remote = FindRemoteProviderException(exception);
            var webResult = remote?.WebRequestResult;

            if (webResult != null)
                return FromResponse(webResult, remote, fallbackUrl);

            // Some Addressables operations flatten their children into text instead of nesting them.
            // CheckCatalogsOperation is the one that matters here: it reports
            // "CheckCatalogsOperation failed with the following errors:" followed by the child
            // exception rendered as a string, with no InnerException to walk — so the loop above
            // finds nothing and a real 503 would fall through to Unknown and be marked
            // non-retryable, which would stop the retry policy running for catalog operations at
            // all. The fault-injection suite is what surfaced this.
            //
            // Parsing "ResponseCode : NNN" out of that text is reading a structured field that
            // UnityWebRequestResult.ToString emits in a fixed format — not the substring matching
            // on prose that design doc §8 rules out. The distinction is that this pattern changes
            // only if Unity changes the formatter, whereas message wording changes freely.
            int recoveredStatus = TryRecoverResponseCodeFromText(exception.ToString());
            if (recoveredStatus > 0)
            {
                return FromStatusOnly(recoveredStatus, fallbackUrl, exception);
            }

            // No response to classify from. Reachability is the only signal left, and it separates
            // "the network went away" from "something else broke".
            if (!isReachable)
            {
                return new CdnError(
                    CdnErrorCode.Offline,
                    "The request failed and the network is unreachable",
                    hint: "Keep playing on cached content and retry when connectivity returns.",
                    url: UrlOf(remote, fallbackUrl),
                    exception: exception);
            }

            return Unclassified(exception, UrlOf(remote, fallbackUrl));
        }

        /// <summary>
        /// Map from an HTTP response.
        /// </summary>
        private static CdnError FromResponse(
            UnityWebRequestResult webResult, RemoteProviderException remote, string fallbackUrl)
        {
            long status = webResult.ResponseCode;
            string url = !string.IsNullOrEmpty(webResult.Url) ? webResult.Url : fallbackUrl;
            bool looksLikeBundle = LooksLikeBundle(url);

            // 0 means the request never got a response: DNS failure, connection refused, timeout at
            // the transport layer. Distinguished from a served error, which has a real status.
            if (status == 0)
            {
                if (webResult.Result == UnityEngine.Networking.UnityWebRequest.Result.DataProcessingError)
                {
                    return new CdnError(
                        CdnErrorCode.BundleCrcMismatch,
                        "The response arrived but could not be processed, which usually means a corrupt bundle",
                        hint: "Clear the cached dependency for this key and retry once. If it recurs, the " +
                              "object on the CDN is corrupt and re-uploading is the fix.",
                        url: url, httpStatusCode: 0, exception: remote);
                }

                return new CdnError(
                    CdnErrorCode.Timeout,
                    $"No response from the server ({webResult.Error})",
                    hint: "The request never reached a server or never came back. Retry with backoff.",
                    url: url, httpStatusCode: 0, exception: remote);
            }

            switch (status)
            {
                case 401:
                case 403:
                    return new CdnError(
                        CdnErrorCode.Unauthorized,
                        $"The server rejected the request ({status})",
                        hint: "Refresh the auth token and retry once. A second rejection means the token is " +
                              "not the problem — check the signed-URL policy or the bucket permissions.",
                        url: url, httpStatusCode: (int)status, exception: remote);

                case 404:
                case 410:
                    // The deploy is wrong, not the client. Which half is wrong depends on what was
                    // being fetched, and that changes who needs to fix it.
                    return looksLikeBundle
                        ? new CdnError(
                            CdnErrorCode.BundleNotFound,
                            $"A bundle the catalog references is not on the CDN ({status})",
                            hint: "The catalog was published before its bundles, or an upload was partial. " +
                                  "Bundles must be uploaded before the catalog — see infrastructure §5.",
                            url: url, httpStatusCode: (int)status, exception: remote)
                        : new CdnError(
                            CdnErrorCode.CatalogNotFound,
                            $"The catalog is not on the CDN at the expected path ({status})",
                            hint: "Either nothing was published for this app version, or the catalog landed " +
                                  "in a folder named for a different version. Not retryable.",
                            url: url, httpStatusCode: (int)status, exception: remote);

                case 408:
                case 429:
                    return new CdnError(
                        CdnErrorCode.ServerError,
                        $"The server asked the client to slow down or timed out ({status})",
                        hint: "Back off and retry. 429 in particular means retrying immediately makes it worse.",
                        url: url, httpStatusCode: (int)status, exception: remote);
            }

            if (status >= 500 && status <= 599)
            {
                return new CdnError(
                    CdnErrorCode.ServerError,
                    $"The origin or edge returned an error ({status})",
                    hint: "Transient at the server end. Retry with backoff, then tell the player the servers " +
                          "are busy rather than that something is broken on their device.",
                    url: url, httpStatusCode: (int)status, exception: remote);
            }

            if (status >= 400 && status <= 499)
            {
                return new CdnError(
                    CdnErrorCode.Unknown,
                    $"The server rejected the request ({status})",
                    hint: "A 4xx that is not 401/403/404/408/429. The request itself is malformed or blocked — " +
                          "retrying it unchanged will not help.",
                    url: url, httpStatusCode: (int)status, exception: remote);
            }

            return Unclassified(remote, url, (int)status);
        }

        /// <summary>
        /// Last resort. Reaches Unknown with whatever hint the message supports, without claiming a
        /// specific code on the strength of a substring.
        /// </summary>
        private static CdnError Unclassified(Exception exception, string url, int status = 0)
        {
            string message = exception?.Message ?? string.Empty;
            string hint = "No HTTP response was available to classify this failure.";

            // These do not set the code — they only add a hint. Getting the hint wrong costs a
            // confusing sentence in a log; getting the code wrong costs a wrong retry decision.
            if (message.IndexOf("crc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("checksum", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hint += " The message mentions a checksum, which would point at a corrupt cached bundle.";
            }
            else if (message.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     message.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hint += " The message mentions disk space, which would point at a full cache directory.";
            }

            return new CdnError(
                CdnErrorCode.Unknown, message, hint: hint,
                url: url, httpStatusCode: status, exception: exception);
        }

        /// <summary>
        /// Pull an HTTP status out of a flattened exception rendering, or 0.
        /// </summary>
        /// <remarks>
        /// Matches "ResponseCode : NNN", the fixed shape UnityWebRequestResult.ToString produces.
        /// Only reached when no RemoteProviderException object could be found in the chain.
        /// </remarks>
        private static int TryRecoverResponseCodeFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"ResponseCode\s*:\s*(\d{3})");

            if (!match.Success) return 0;

            return int.TryParse(match.Groups[1].Value, out int status) ? status : 0;
        }

        /// <summary>
        /// Classify from a status alone, when the response object itself is unavailable.
        /// </summary>
        private static CdnError FromStatusOnly(int status, string url, Exception exception)
        {
            bool looksLikeBundle = LooksLikeBundle(url) ||
                                   (exception != null && LooksLikeBundle(exception.ToString()));

            if (status == 401 || status == 403)
            {
                return new CdnError(CdnErrorCode.Unauthorized,
                    $"The server rejected the request ({status})",
                    hint: "Refresh the auth token and retry once.",
                    url: url, httpStatusCode: status, exception: exception);
            }

            if (status == 404 || status == 410)
            {
                return looksLikeBundle
                    ? new CdnError(CdnErrorCode.BundleNotFound,
                        $"A bundle the catalog references is not on the CDN ({status})",
                        hint: "Bundles must be uploaded before the catalog — see infrastructure §5.",
                        url: url, httpStatusCode: status, exception: exception)
                    : new CdnError(CdnErrorCode.CatalogNotFound,
                        $"The catalog is not on the CDN at the expected path ({status})",
                        hint: "Nothing was published for this app version, or it landed in a folder " +
                              "named for a different one. Not retryable.",
                        url: url, httpStatusCode: status, exception: exception);
            }

            if (status == 408 || status == 429 || (status >= 500 && status <= 599))
            {
                return new CdnError(CdnErrorCode.ServerError,
                    $"The origin or edge returned an error ({status})",
                    hint: "Transient at the server end. Retry with backoff.",
                    url: url, httpStatusCode: status, exception: exception);
            }

            return new CdnError(CdnErrorCode.Unknown,
                $"The server rejected the request ({status})",
                hint: "A status with no specific handling. Retrying it unchanged is unlikely to help.",
                url: url, httpStatusCode: status, exception: exception);
        }

        /// <summary>Walk the inner-exception chain for a RemoteProviderException.</summary>
        /// <remarks>
        /// Addressables wraps provider failures inside operation exceptions, so the useful one is
        /// rarely the outermost. Depth-limited because a cyclic InnerException would otherwise hang.
        /// </remarks>
        private static RemoteProviderException FindRemoteProviderException(Exception exception)
        {
            const int maxDepth = 8;
            var current = exception;

            for (int depth = 0; depth < maxDepth && current != null; depth++)
            {
                if (current is RemoteProviderException remote)
                    return remote;

                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        var found = FindRemoteProviderException(inner);
                        if (found != null) return found;
                    }

                    return null;
                }

                current = current.InnerException;
            }

            return null;
        }

        private static string UrlOf(RemoteProviderException remote, string fallback)
        {
            string url = remote?.WebRequestResult?.Url;
            return string.IsNullOrEmpty(url) ? fallback : url;
        }

        /// <summary>
        /// Whether a URL points at a bundle rather than a catalog.
        /// </summary>
        /// <remarks>
        /// Only used to pick between two 404 messages, both of which say "the deploy is wrong". A
        /// wrong guess here changes the wording, not the code or the retry decision.
        /// </remarks>
        private static bool LooksLikeBundle(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            return url.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
                   url.IndexOf("/bundles/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
