using System;
using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Rewrites the origin of a baked content URL so one build can be pointed at another
    /// environment — task 2.4.
    /// </summary>
    public interface IHostRewriter
    {
        /// <summary>The environment URLs are currently rewritten to.</summary>
        string ActiveEnvironmentId { get; }

        /// <summary>The origin URLs are currently rewritten to.</summary>
        string ActiveBaseUrl { get; }

        /// <summary>Point subsequent rewrites at a different environment.</summary>
        CdnResult<string> SetEnvironment(string environmentId);

        /// <summary>Promote the next failover origin. Returns failure when none are left.</summary>
        CdnResult<string> PromoteFailover();

        /// <summary>Rewrite one URL. Returns it unchanged when it is not a remote content URL.</summary>
        string Rewrite(string url);
    }

    /// <summary>
    /// Swaps the scheme-and-host prefix of remote content URLs, keeping the path intact.
    /// </summary>
    /// <remarks>
    /// WHY REWRITE ONLY THE ORIGIN
    /// Addressables bakes fully resolved URLs into the catalog at build time. The path after the
    /// origin already carries the platform folder and the app-version folder that the build used,
    /// so swapping just the origin moves a build between environments without having to reproduce
    /// any of that. Rebuilding the path at runtime is the fragile version of this, for the reason
    /// below.
    ///
    /// THE {platform} TOKEN IS A TRAP, AND IT IS NOT A THEORETICAL ONE
    /// The CDN layout is built from the Addressables profile variable [BuildTarget], which expands
    /// to EditorUserBuildSettings.activeBuildTarget — "StandaloneWindows64"
    /// (AddressableAssetProfileSettings.cs:484). The obvious runtime counterpart,
    /// PlatformMappingService.GetPlatformPathSubFolder(), maps BuildTarget.StandaloneWindows64 to
    /// AddressablesPlatform.Windows and returns "Windows"
    /// (PlatformMappingService.cs:93, :157). Those are different strings, so using Unity's own API
    /// to fill {platform} produces .../Windows/bundles against content published at
    /// .../StandaloneWindows64/bundles, and every request 404s.
    ///
    /// So {platform} is resolved through <see cref="ResolvePlatformToken"/> below, which reproduces
    /// the BuildTarget name. One case cannot be recovered: a 32-bit and a 64-bit Windows player
    /// both report RuntimePlatform.WindowsPlayer, and the build used different folder names for
    /// them. 64-bit is assumed. Ship 32-bit Windows and you must not use {platform}.
    ///
    /// The token exists for base URLs that include a path segment. If your environments differ only
    /// by host — the normal case — do not use it at all, and this whole problem disappears.
    /// </remarks>
    public class HostRewriter : IHostRewriter
    {
        private readonly CdnSettings _settings;
        private readonly bool _logRewrites;

        private CdnEnvironment _environment;
        private string _activeBaseUrl;
        private int _failoverIndex = -1;

        /// <summary>Origins recognised as rewritable, longest first so the most specific wins.</summary>
        private readonly List<string> _knownOrigins = new List<string>();

        /// <summary>
        /// Origins already reported as unknown, so the warning is once per origin rather than once
        /// per request.
        /// </summary>
        private readonly HashSet<string> _warnedUnknownOrigins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public string ActiveEnvironmentId => _environment?.Id ?? string.Empty;

        /// <inheritdoc />
        public string ActiveBaseUrl => _activeBaseUrl ?? string.Empty;

        /// <summary>Create a rewriter over the given settings, starting at the default environment.</summary>
        public HostRewriter(CdnSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logRewrites = settings.LogUrlRewrites;

            // Every configured origin is rewritable: that is what lets a build baked against Prod
            // be redirected to Staging, and a build baked against Local be redirected anywhere.
            foreach (var environment in settings.Environments)
            {
                if (environment == null) continue;

                // Both forms. The configured string still holds {platform}/{appVersion}, and only the
                // ACTIVE environment ever gets resolved (Apply -> ResolveTokens). Seeding the raw form
                // alone meant an origin belonging to a non-active environment could never StartsWith-
                // match a baked catalog URL, so Rewrite() fell through to "origin not configured" and
                // returned the URL untouched: a build baked against environment A kept fetching from A
                // while the facade cheerfully reported B as current.
                AddKnownOrigin(environment.BaseUrl);
                AddKnownOrigin(ResolveTokens(environment.BaseUrl));

                foreach (string failover in environment.FailoverUrls)
                {
                    AddKnownOrigin(failover);
                    AddKnownOrigin(ResolveTokens(failover));
                }
            }

            _knownOrigins.Sort((a, b) => b.Length.CompareTo(a.Length));

            var initial = settings.GetDefaultEnvironment();
            if (initial.IsSuccess)
                Apply(initial.Value, initial.Value.BaseUrl);
        }

        /// <inheritdoc />
        public CdnResult<string> SetEnvironment(string environmentId)
        {
            var found = _settings.GetEnvironment(environmentId);
            if (found.IsFailure)
                return CdnResult<string>.Failure(found.Error);

            string resolved = ResolveTokens(found.Value.BaseUrl);
            Apply(found.Value, found.Value.BaseUrl);
            _failoverIndex = -1;

            return CdnResult<string>.Success(resolved);
        }

        /// <inheritdoc />
        public CdnResult<string> PromoteFailover()
        {
            if (_environment == null)
            {
                return CdnResult<string>.Failure(
                    CdnErrorCode.Unknown,
                    "No active environment, so there is nothing to fail over from");
            }

            var failovers = _environment.FailoverUrls;
            int next = _failoverIndex + 1;

            if (next >= failovers.Length)
            {
                return CdnResult<string>.Failure(
                    CdnErrorCode.ServerError,
                    $"Environment '{_environment.Id}' has no failover origin left " +
                    $"({failovers.Length} configured, all tried)",
                    hint: "Every configured origin has failed. This is an outage, not a routing " +
                          "problem — surface it rather than retrying the same list again.");
            }

            _failoverIndex = next;
            Apply(_environment, failovers[next]);

            return CdnResult<string>.Success(_activeBaseUrl);
        }

        /// <inheritdoc />
        public string Rewrite(string url)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(_activeBaseUrl))
                return url;

            // Leave anything that is not an http(s) URL alone: local bundles resolve through
            // file paths and {UnityEngine.Application.streamingAssetsPath}, and rewriting those
            // would break loading content that never goes near the CDN.
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            foreach (string origin in _knownOrigins)
            {
                if (!url.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(origin, _activeBaseUrl, StringComparison.OrdinalIgnoreCase))
                    return url; // already pointing where we want it

                string rewritten = _activeBaseUrl + url.Substring(origin.Length);

                if (_logRewrites)
                    Debug.Log($"[CdnHostRewriter] {url}\n  -> {rewritten}");

                return rewritten;
            }

            // An http URL from an origin we do not know about. Left alone deliberately: silently
            // redirecting a URL the game fetches for some other reason would be worse than not
            // rewriting content we were never told about.
            //
            // But "left alone" is also exactly what a misconfigured build looks like, and it used to
            // be visible only behind LogUrlRewrites, which defaults to false. A catalog baked against
            // an origin that is not in CdnSettings then produced a player where every content request
            // went to a host nobody intended, with no log line anywhere - the failure reads as "the
            // CDN is down", not as "this build was pointed somewhere else".
            //
            // Warn once per unknown origin, regardless of the flag. Once per origin rather than per
            // request, because this fires on every bundle and a per-request warning would bury the
            // console it is trying to inform. LogUrlRewrites still controls the per-request detail.
            WarnOnceAboutUnknownOrigin(url);

            return url;
        }

        /// <summary>
        /// Reports, once per origin, that content is being fetched from somewhere this rewriter was
        /// never told about — so it cannot redirect it, and the environment switch does not apply.
        /// </summary>
        private void WarnOnceAboutUnknownOrigin(string url)
        {
            string origin = ExtractOrigin(url);
            if (string.IsNullOrEmpty(origin)) return;
            if (!_warnedUnknownOrigins.Add(origin)) return;

            Debug.LogWarning(
                $"[CdnHostRewriter] Content is being requested from '{origin}', which is not any " +
                "environment's base URL in CdnSettings, so it was left alone - the active environment " +
                $"('{ActiveEnvironmentId}' -> {_activeBaseUrl}) does not apply to it.\n\n" +
                "That origin is baked into the catalog at build time by the Addressables profile's " +
                "Remote.LoadPath / Remote.CatalogLoadPath. If it does not match a CdnEnvironment.BaseUrl, " +
                "switching environment at runtime silently does nothing and every request goes wherever " +
                "the build was pointed - which reads as \"the CDN is down\" rather than as a " +
                "misconfigured build.\n\n" +
                "Either add this origin as a CdnEnvironment, or rebuild content with a profile whose " +
                "Remote paths start with one you already have. The Validator tab's " +
                "settings.RemoteOriginIsKnown rule checks this before you build.");
        }

        /// <summary>scheme://host[:port] of an absolute http(s) URL, or null.</summary>
        private static string ExtractOrigin(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0) return null;

            int hostEnd = url.IndexOf('/', schemeEnd + 3);
            return hostEnd < 0 ? url : url.Substring(0, hostEnd);
        }

        private void Apply(CdnEnvironment environment, string baseUrl)
        {
            _environment = environment;
            _activeBaseUrl = ResolveTokens(baseUrl);
            AddKnownOrigin(_activeBaseUrl);
            _knownOrigins.Sort((a, b) => b.Length.CompareTo(a.Length));
        }

        private void AddKnownOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return;
            if (_knownOrigins.Contains(origin)) return;

            _knownOrigins.Add(origin);
        }

        /// <summary>Expand {platform} and {appVersion} in a configured base URL.</summary>
        /// <remarks>
        /// Token matching is case-insensitive in both directions. It used to detect with
        /// <c>IndexOf(..., OrdinalIgnoreCase)</c> and then substitute with
        /// <c>string.Replace(string, string)</c>, which is case-SENSITIVE — so <c>{Platform}</c> was
        /// recognised and then left in place, and the literal brace text travelled on into a request
        /// URL. Going out of the way to detect any casing and then honouring only one is the kind of
        /// half-implemented intent that reads as correct at both call sites.
        ///
        /// <see cref="ReplaceIgnoreCase"/> rather than the three-argument
        /// <c>string.Replace(string, string, StringComparison)</c> overload: that overload does not
        /// exist on every runtime profile this package is expected to compile against, and a helper
        /// costs nothing here — this runs once per environment switch, not per asset load.
        /// </remarks>
        public static string ResolveTokens(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            url = ReplaceIgnoreCase(url, "{platform}", ResolvePlatformToken());
            url = ReplaceIgnoreCase(url, "{appVersion}", Application.version);

            return url;
        }

        /// <summary>
        /// Ordinal, case-insensitive substring replacement. Returns <paramref name="source"/>
        /// unchanged, and allocates nothing, when the token does not occur.
        /// </summary>
        internal static string ReplaceIgnoreCase(string source, string token, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token)) return source;

            int at = source.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return source;

            value = value ?? string.Empty;

            var built = new System.Text.StringBuilder(source.Length);
            int from = 0;
            while (at >= 0)
            {
                built.Append(source, from, at - from).Append(value);
                from = at + token.Length;
                at = source.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
            }

            built.Append(source, from, source.Length - from);
            return built.ToString();
        }

        /// <summary>
        /// The folder name the build used for this platform — the BuildTarget enum name, not
        /// Addressables' own platform name. See the class remarks for why these differ.
        /// </summary>
        public static string ResolvePlatformToken()
        {
#if UNITY_EDITOR
            // In the Editor the build target is authoritative and exact, which also makes Play mode
            // agree with whatever the last content build produced.
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
            switch (Application.platform)
            {
                // WindowsPlayer covers both StandaloneWindows and StandaloneWindows64 and cannot
                // distinguish them. 64-bit is assumed; see the class remarks.
                case RuntimePlatform.WindowsPlayer: return "StandaloneWindows64";
                case RuntimePlatform.OSXPlayer:     return "StandaloneOSX";
                case RuntimePlatform.LinuxPlayer:   return "StandaloneLinux64";
                case RuntimePlatform.Android:       return "Android";
                case RuntimePlatform.IPhonePlayer:  return "iOS";
                case RuntimePlatform.WebGLPlayer:   return "WebGL";
                case RuntimePlatform.PS4:           return "PS4";
                case RuntimePlatform.PS5:           return "PS5";
                case RuntimePlatform.XboxOne:       return "XboxOne";
                case RuntimePlatform.Switch:        return "Switch";
                default:
                    // Returning the raw enum name would produce a plausible-looking but wrong
                    // folder. Failing loudly beats 404s nobody can explain.
                    Debug.LogError(
                        $"[CdnHostRewriter] No BuildTarget folder name known for {Application.platform}. " +
                        "The {platform} token cannot be resolved on this platform — use an environment " +
                        "base URL without it, or add the mapping.");
                    return Application.platform.ToString();
            }
#endif
        }
    }
}
