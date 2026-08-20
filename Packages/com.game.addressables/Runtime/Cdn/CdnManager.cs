using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Cdn
{
    /// <summary>
    /// The CDN entry point — task 2.9. Boot subset only; downloads arrive in Phase 3.
    /// </summary>
    /// <remarks>
    /// CALL <see cref="InitializeAsync"/> BEFORE ANY OTHER ADDRESSABLES CALL
    /// It installs the hooks that point Addressables at the CDN, and Addressables initialises
    /// implicitly on its first load. One stray LoadAssetAsync, or an AssetReference on an object in
    /// the boot scene, is enough to initialise it first — after which the hooks are ignored for
    /// everything already resolved. Initialize refuses in that case rather than pretending, so the
    /// failure is loud instead of a partially-wrong origin.
    ///
    /// THE CANONICAL BOOT, design doc §6:
    /// <code>
    /// var init = await CdnManager.InitializeAsync();
    /// if (init.IsFailure)
    /// {
    ///     if (init.ErrorCode == CdnErrorCode.NoContentAvailableOffline)
    ///         ShowBlockingSetupScreen();   // first launch, nothing cached
    ///     else
    ///         ReportAndContinue(init.Error);
    ///     return;
    /// }
    ///
    /// var check = await CdnManager.CheckForUpdateAsync();
    /// if (check.IsSuccess &amp;&amp; check.Value.HasUpdate)
    ///     await CdnManager.ApplyUpdateAsync(check.Value);
    /// </code>
    ///
    /// Static because there is exactly one Addressables instance in a process and the hooks it
    /// installs are global. A second configured instance could not be honoured.
    ///
    /// NAMED CdnManager, NOT Cdn
    /// The design doc calls this facade "Cdn". That name cannot be used: the runtime CDN types live
    /// in namespace AddressableManager.Cdn, mirroring AddressableManager.Editor.Cdn on the Editor
    /// side, so at any call site with `using AddressableManager.Cdn;` the identifier Cdn resolves to
    /// the namespace and every member access fails to compile (CS0234). Renaming the namespace
    /// instead would have broken that symmetry across eight files to save four characters.
    /// </remarks>
    public static class CdnManager
    {
        private static CdnSettings _settings;
        private static NetworkPolicy _network;
        private static IHostRewriter _rewriter;
        private static CatalogService _catalog;
        private static DownloadService _downloads;
        private static CacheService _cache;

        /// <summary>
        /// Set while a catalog update is being applied, so a second overlapping apply is refused
        /// instead of running concurrently.
        /// </summary>
        /// <remarks>
        /// There was no guard of any kind. ApplyUpdateAsync checked only that _catalog was non-null,
        /// and CatalogService checked only that it was initialised, so two overlapping calls - a boot
        /// flow plus a "check for updates" button is enough - both reached Addressables.UpdateCatalogs.
        /// Addressables builds a fresh UpdateCatalogsOperation per call and mutates shared
        /// ResourceLocatorInfo state with no interlock, and both applies then race this class's own
        /// CleanObsoleteAsync. Refusing the second is right rather than queueing it: by the time the
        /// first finishes, the second caller's CatalogUpdateInfo describes a catalog state that no
        /// longer exists, so it should be re-checked rather than applied.
        ///
        /// int + Interlocked rather than a bool: this is the only cross-thread-safe way to make
        /// test-and-set atomic, and callers may well be on different threads.
        /// </remarks>
        private static int _updateInFlight;

        /// <summary>Whether the CDN layer has initialised successfully.</summary>
        public static bool IsInitialized => _catalog != null && _catalog.IsInitialized;

        /// <summary>The environment currently in use, or empty before initialisation.</summary>
        public static string CurrentEnvironmentId => _rewriter?.ActiveEnvironmentId ?? string.Empty;

        /// <summary>The origin currently in use, or empty before initialisation.</summary>
        public static string CurrentBaseUrl => _rewriter?.ActiveBaseUrl ?? string.Empty;

        /// <summary>The loaded settings, or null before initialisation.</summary>
        public static CdnSettings Settings => _settings;

        /// <summary>
        /// Supplies a bearer token per request. Set before <see cref="InitializeAsync"/>.
        /// Called on every request, so a refreshed token is picked up without reinstalling.
        /// </summary>
        /// <remarks>
        /// <para><b>This runs inside Addressables' update loop, and must return immediately.</b>
        /// Addressables invokes <c>WebRequestOverride</c> from within <c>ResourceManager.Update</c>,
        /// so this delegate does too - on every bundle, catalog and hash request.</para>
        ///
        /// <para>It must therefore <b>return a token it already holds</b>. It must not call
        /// <c>WaitForCompletion()</c>, must not block on a <c>Task</c> or coroutine, and must not
        /// start or await an Addressables operation: each of those re-enters the update loop and
        /// Unity throws <c>"Reentering the Update method is not allowed"</c> - from a stack that names
        /// only Unity's own frames, never the delegate that caused it. Even a non-re-entrant blocking
        /// call is a problem, because it stalls every download for as long as it runs.</para>
        ///
        /// <para>Fetch and refresh the token on your own schedule, cache it in a field, and let this
        /// return that field. If it throws, the package logs which delegate threw and the request
        /// continues without an Authorization header - which surfaces as an ordinary 401 rather than
        /// an exception thrown through the middle of Addressables' update.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // Refreshed elsewhere; the hook only reads it.
        /// private static string _token;
        /// CdnManager.AuthTokenProvider = () =&gt; _token;
        /// </code>
        /// </example>
        public static Func<string> AuthTokenProvider { get; set; }

#if UNITASK_PRESENT
        /// <summary>
        /// Load settings, install the Addressables hooks, and initialise against the CDN.
        /// </summary>
        /// <param name="environmentId">Environment to use. Null uses the configured default.</param>
        /// <param name="cancellationToken">Cancels initialisation.</param>
        public static async UniTask<CdnResult<bool>> InitializeAsync(
            string environmentId = null, CancellationToken cancellationToken = default)
#else
        /// <summary>
        /// Load settings, install the Addressables hooks, and initialise against the CDN.
        /// </summary>
        /// <param name="environmentId">Environment to use. Null uses the configured default.</param>
        /// <param name="cancellationToken">Cancels initialisation.</param>
        public static async Task<CdnResult<bool>> InitializeAsync(
            string environmentId = null, CancellationToken cancellationToken = default)
#endif
        {
            if (IsInitialized)
                return CdnResult<bool>.Success(true);

            var loaded = CdnSettings.Load();
            if (loaded.IsFailure)
                return CdnResult<bool>.Failure(loaded.Error);

            _settings = loaded.Value;

            var problems = _settings.Validate();
            if (problems.Count > 0)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown,
                    $"CdnSettings is not usable: {string.Join("; ", problems)}",
                    hint: "Fix the settings asset. Initialising against a half-valid configuration " +
                          "would fail later at a point that no longer names the cause.");
            }

            _network = new NetworkPolicy(_settings.DownloadPolicy);
            var rewriter = new HostRewriter(_settings);
            _rewriter = rewriter;

            if (!string.IsNullOrEmpty(environmentId))
            {
                var switched = rewriter.SetEnvironment(environmentId);
                if (switched.IsFailure)
                    return CdnResult<bool>.Failure(switched.Error);
            }

            ApplyHostEnvironmentVariableOverride();

            // Hooks first. Installing after Addressables has initialised silently does nothing for
            // anything already resolved, so this returns a failure rather than continuing.
            var installed = CdnRequestDecorator.Install(_rewriter, _settings.DownloadPolicy, AuthTokenProvider);
            if (installed.IsFailure)
                return CdnResult<bool>.Failure(installed.Error);

            _catalog = new CatalogService(_settings, _network, _rewriter);
            _downloads = new DownloadService(_network, RetryPolicy.Default, _rewriter);
            _cache = new CacheService();

            return await _catalog.InitializeAsync(cancellationToken);
        }

#if UNITASK_PRESENT
        /// <summary>Ask whether newer content is available.</summary>
        public static UniTask<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(
            CancellationToken cancellationToken = default)
#else
        /// <summary>Ask whether newer content is available.</summary>
        public static Task<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(
            CancellationToken cancellationToken = default)
#endif
        {
            if (_catalog == null)
                return FromResult(CdnResult<CatalogUpdateInfo>.Failure(NotInitialized()));

            return _catalog.CheckForUpdateAsync(cancellationToken);
        }

#if UNITASK_PRESENT
        /// <summary>Apply catalogs reported by <see cref="CheckForUpdateAsync"/>.</summary>
        public static UniTask<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default)
#else
        /// <summary>Apply catalogs reported by <see cref="CheckForUpdateAsync"/>.</summary>
        public static Task<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default)
#endif
        {
            if (_catalog == null)
                return FromResult(CdnResult<IReadOnlyList<string>>.Failure(NotInitialized()));

            if (Interlocked.CompareExchange(ref _updateInFlight, 1, 0) != 0)
            {
                return FromResult(CdnResult<IReadOnlyList<string>>.Failure(new CdnError(
                    CdnErrorCode.Unknown,
                    "A catalog update is already being applied",
                    hint: "Wait for the in-flight ApplyUpdateAsync to finish, then call CheckForUpdateAsync " +
                          "again - the CatalogUpdateInfo you are holding describes the pre-update state and " +
                          "must not be applied on top of the result.")));
            }

            return ApplyUpdateGuardedAsync(update, cancellationToken);
        }

        /// <summary>
        /// Point subsequent requests at a different environment, without a rebuild.
        /// </summary>
        /// <remarks>
        /// Affects URLs resolved from now on. Bundles already downloaded stay cached and are not
        /// re-fetched from the new origin, which is what makes this useful for QA and wrong for
        /// switching between environments that serve different content under the same names.
        /// </remarks>
        public static CdnResult<string> SetEnvironment(string environmentId)
        {
            if (_rewriter == null)
                return CdnResult<string>.Failure(NotInitialized());

            return _rewriter.SetEnvironment(environmentId);
        }

        /// <summary>Promote the next failover origin for the active environment.</summary>
        public static CdnResult<string> PromoteFailover()
        {
            if (_rewriter == null)
                return CdnResult<string>.Failure(NotInitialized());

            return _rewriter.PromoteFailover();
        }

#if UNITASK_PRESENT
        /// <summary>Bytes still to download for the given keys — task 3.2.</summary>
        /// <remarks>
        /// Returns a result, not a number: zero is a real answer meaning "already cached", and a
        /// failed lookup must not be indistinguishable from it.
        /// </remarks>
        public static UniTask<CdnResult<long>> GetDownloadSizeAsync(
            DownloadRequest request, CancellationToken cancellationToken = default)
#else
        /// <summary>Bytes still to download for the given keys — task 3.2.</summary>
        /// <remarks>
        /// Returns a result, not a number: zero is a real answer meaning "already cached", and a
        /// failed lookup must not be indistinguishable from it.
        /// </remarks>
        public static Task<CdnResult<long>> GetDownloadSizeAsync(
            DownloadRequest request, CancellationToken cancellationToken = default)
#endif
        {
            if (_downloads == null)
                return FromResult(CdnResult<long>.Failure(NotInitialized()));

            return _downloads.GetDownloadSizeAsync(request, cancellationToken);
        }

#if UNITASK_PRESENT
        /// <summary>Download content, with progress, cancellation, retry and repair — task 3.3.</summary>
        public static UniTask<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request,
            IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
#else
        /// <summary>Download content, with progress, cancellation, retry and repair — task 3.3.</summary>
        public static Task<CdnResult<DownloadReport>> DownloadAsync(
            DownloadRequest request,
            IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
#endif
        {
            if (_downloads == null)
                return FromResult(CdnResult<DownloadReport>.Failure(NotInitialized()));

            return _downloads.DownloadAsync(request, progress, cancellationToken);
        }

#if UNITASK_PRESENT
        private static async UniTask<CdnResult<IReadOnlyList<string>>> ApplyUpdateAndCleanAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken)
#else
        private static async Task<CdnResult<IReadOnlyList<string>>> ApplyUpdateAndCleanAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken)
#endif
        {
            var applied = await _catalog.ApplyUpdateAsync(update, cancellationToken);
            if (applied.IsFailure || applied.Value == null || applied.Value.Count == 0)
                return applied;

            // Task 4.2. Superseded bundles are not removed by the update itself, and nothing else
            // removes them either — after twenty updates a player is carrying twenty generations of
            // content. That is the usual answer to "why is this game 8 GB". Done here rather than
            // left to the integrator, because the one place it must never be forgotten is right
            // after an update succeeds.
            var cleaned = await _cache.CleanObsoleteAsync(cancellationToken: cancellationToken);
            if (cleaned.IsFailure)
            {
                // Not fatal: the update worked, the disk is just untidier than it should be.
                Debug.LogWarning($"[Cdn] Catalog updated but obsolete bundles could not be removed: " +
                                 $"{cleaned.ErrorMessage}");
            }

            return applied;
        }

        /// <summary>Cache statistics — task 4.1.</summary>
        public static CacheStats GetCacheStats() => _cache?.GetStats() ?? CacheStats.Unavailable;

        /// <summary>The cache service, or null before initialisation. For clean and clear operations.</summary>
        public static CacheService Cache => _cache;

        /// <summary>How the device is connected right now.</summary>
        public static NetworkReachabilityState NetworkState =>
            _network?.CurrentState ?? NetworkReachabilityState.Offline;

        /// <summary>
        /// Tear down for tests and for domain reloads.
        /// </summary>
        public static void Reset()
        {
            CdnRequestDecorator.Uninstall();
            _settings = null;
            _network = null;
            _rewriter = null;
            _catalog = null;
            _downloads = null;
            _cache = null;
        }

        // ========== internals ==========

#if UNITASK_PRESENT
        private static async UniTask<CdnResult<IReadOnlyList<string>>> ApplyUpdateGuardedAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken)
#else
        private static async Task<CdnResult<IReadOnlyList<string>>> ApplyUpdateGuardedAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken)
#endif
        {
            try
            {
                return await ApplyUpdateAndCleanAsync(update, cancellationToken);
            }
            finally
            {
                Interlocked.Exchange(ref _updateInFlight, 0);
            }
        }

        private static CdnError NotInitialized() => new CdnError(
            CdnErrorCode.Unknown,
            "The CDN layer is not initialised",
            hint: "Await CdnManager.InitializeAsync first, and check its result — a failed initialisation " +
                  "leaves the layer unusable rather than degrading quietly.");

        /// <summary>
        /// Apply the environment-variable host override, when one is configured and present.
        /// </summary>
        /// <remarks>
        /// Editor and standalone only: mobile and console have no process environment, and
        /// GetEnvironmentVariable simply returns null there rather than throwing. Kept out of the
        /// settings asset so a production hostname is not a build-time constant.
        /// </remarks>
        private static void ApplyHostEnvironmentVariableOverride()
        {
            string variable = _settings.HostEnvironmentVariable;
            if (string.IsNullOrWhiteSpace(variable)) return;

            string value;
            try
            {
                value = Environment.GetEnvironmentVariable(variable);
            }
            catch (Exception)
            {
                // Some platforms deny access rather than returning null.
                return;
            }

            if (string.IsNullOrWhiteSpace(value)) return;

            Debug.Log($"[Cdn] Base URL overridden by {variable}: {value}");

            // Routed through an ad-hoc environment so the override behaves exactly like a
            // configured one, including being a known origin for rewriting.
            var overrideEnvironment = new CdnEnvironment(
                CurrentEnvironmentId + "-override",
                CurrentEnvironmentId + " (env override)",
                value.TrimEnd('/'));

            if (!overrideEnvironment.IsValid(out string reason))
            {
                Debug.LogError($"[Cdn] Ignoring {variable}: {reason}");
                return;
            }

            _rewriter = new HostRewriterOverride(_rewriter, overrideEnvironment);
        }

#if UNITASK_PRESENT
        private static UniTask<T> FromResult<T>(T value) => UniTask.FromResult(value);
#else
        private static Task<T> FromResult<T>(T value) => Task.FromResult(value);
#endif

        /// <summary>
        /// Wraps a rewriter so every URL lands on a single overridden origin.
        /// </summary>
        private sealed class HostRewriterOverride : IHostRewriter
        {
            private readonly IHostRewriter _inner;
            private readonly CdnEnvironment _override;

            public HostRewriterOverride(IHostRewriter inner, CdnEnvironment overrideEnvironment)
            {
                _inner = inner;
                _override = overrideEnvironment;
            }

            public string ActiveEnvironmentId => _override.Id;
            public string ActiveBaseUrl => _override.BaseUrl;

            public CdnResult<string> SetEnvironment(string environmentId) =>
                CdnResult<string>.Failure(
                    CdnErrorCode.Unknown,
                    $"The base URL is pinned by an environment variable to {_override.BaseUrl}",
                    hint: "Unset the variable to switch environments at runtime. An override that could " +
                          "be silently overridden again would not be an override.");

            public CdnResult<string> PromoteFailover() =>
                CdnResult<string>.Failure(
                    CdnErrorCode.Unknown,
                    "No failover origins when the base URL is pinned by an environment variable");

            public string Rewrite(string url)
            {
                // Let the inner rewriter normalise a known origin first, then force the override.
                string rewritten = _inner.Rewrite(url);
                if (string.IsNullOrEmpty(rewritten)) return rewritten;

                if (!rewritten.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !rewritten.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return rewritten;
                }

                string innerBase = _inner.ActiveBaseUrl;
                if (!string.IsNullOrEmpty(innerBase) &&
                    rewritten.StartsWith(innerBase, StringComparison.OrdinalIgnoreCase))
                {
                    return _override.BaseUrl + rewritten.Substring(innerBase.Length);
                }

                return rewritten;
            }
        }
    }
}
