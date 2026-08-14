using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
// IResourceLocator lives under AddressableAssets.ResourceLocators, not ResourceManagement —
// neighbouring namespaces with near-identical names, and IResourceLocation (singular, used by the
// decorator) really is under ResourceManagement.ResourceLocations.
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Boot and catalog lifecycle against a CDN — tasks 2.5, 2.6, 2.7.
    /// </summary>
    public interface ICatalogService
    {
        /// <summary>Whether initialisation has completed successfully.</summary>
        bool IsInitialized { get; }

#if UNITASK_PRESENT
        /// <summary>Initialise Addressables against the configured environment.</summary>
        UniTask<CdnResult<bool>> InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>Ask whether a newer catalog exists.</summary>
        UniTask<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken = default);

        /// <summary>Apply catalogs reported by a previous check.</summary>
        UniTask<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default);
#else
        /// <summary>Initialise Addressables against the configured environment.</summary>
        Task<CdnResult<bool>> InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>Ask whether a newer catalog exists.</summary>
        Task<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken = default);

        /// <summary>Apply catalogs reported by a previous check.</summary>
        Task<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default);
#endif
    }

    /// <summary>
    /// Drives Addressables' own initialisation and catalog-update operations, turning their
    /// results into <see cref="CdnResult{T}"/>.
    /// </summary>
    /// <remarks>
    /// This class deliberately holds no retry or backoff logic — that is Phase 3. What it does own
    /// is the distinction between the boot outcomes in design doc §7, because they need different
    /// UX and only this layer has the information to tell them apart:
    ///
    ///   initialised, online            normal boot
    ///   initialised, offline, warm     play on cached content, retry the check later
    ///   not initialised, offline, cold NoContentAvailableOffline — nothing to play, blocking screen
    ///
    /// The third case is the one Addressables reports poorly: a first launch with no cache and no
    /// network fails inside InitializeAsync with an exception about a missing catalog, which reads
    /// like a broken deploy. Reachability is checked first so the caller gets a code that names the
    /// real cause.
    /// </remarks>
    public class CatalogService : ICatalogService
    {
        private readonly CdnSettings _settings;
        private readonly NetworkPolicy _network;
        private readonly IHostRewriter _rewriter;

        private bool _initialized;

        /// <inheritdoc />
        public bool IsInitialized => _initialized;

        /// <summary>Create a service over the given configuration.</summary>
        public CatalogService(CdnSettings settings, NetworkPolicy network, IHostRewriter rewriter)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _rewriter = rewriter ?? throw new ArgumentNullException(nameof(rewriter));
        }

        // ================= 2.5 initialise =================

#if UNITASK_PRESENT
        /// <inheritdoc />
        public async UniTask<CdnResult<bool>> InitializeAsync(CancellationToken cancellationToken = default)
#else
        /// <inheritdoc />
        public async Task<CdnResult<bool>> InitializeAsync(CancellationToken cancellationToken = default)
#endif
        {
            if (_initialized)
                return CdnResult<bool>.Success(true);

            bool reachable = _network.IsReachable;

            // Addressables.InitializeAsync() is explicit here rather than relying on the implicit
            // initialisation that the first load call triggers. Implicit init gives no handle to
            // await and no place to attribute a failure.
            AsyncOperationHandle<IResourceLocator> handle;
            try
            {
                handle = Addressables.InitializeAsync(false);
            }
            catch (Exception ex)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown, "Addressables.InitializeAsync threw before it started",
                    exception: ex);
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
                return CdnResult<bool>.Cancelled("Cancelled during Addressables initialisation");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Failure(ClassifyInitFailure(ex, reachable));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                SafeRelease(handle);
                return CdnResult<bool>.Cancelled("Cancelled during Addressables initialisation");
            }

            bool succeeded = handle.Status == AsyncOperationStatus.Succeeded;
            Exception failure = handle.OperationException;
            SafeRelease(handle);

            if (!succeeded)
                return CdnResult<bool>.Failure(ClassifyInitFailure(failure, reachable));

            _initialized = true;
            return CdnResult<bool>.Success(true);
        }

        /// <summary>
        /// Turn an initialisation failure into a code the caller can act on.
        /// </summary>
        /// <remarks>
        /// Reachability at the time of the attempt is the deciding input, not the exception text.
        /// The same underlying failure — no catalog — means "cold start with no network" when
        /// offline and "the deploy is broken" when online, and those need opposite responses.
        /// </remarks>
        private CdnError ClassifyInitFailure(Exception exception, bool wasReachable)
        {
            if (!wasReachable)
            {
                return new CdnError(
                    CdnErrorCode.NoContentAvailableOffline,
                    "No network and no cached catalog, so there is no content to start from",
                    hint: "First launch needs a connection once. Show a blocking setup screen that " +
                          "retries when connectivity returns, rather than an error.",
                    url: _rewriter.ActiveBaseUrl,
                    exception: exception);
            }

            return new CdnError(
                CdnErrorCode.CatalogNotFound,
                "Addressables could not load a catalog from the configured origin",
                hint: $"Reachable but no usable catalog at {_rewriter.ActiveBaseUrl}. Check the origin is " +
                      "serving the catalog for this app version, and that the app version matches the " +
                      "folder the content was published into.",
                url: _rewriter.ActiveBaseUrl,
                exception: exception);
        }

        // ================= 2.6 check for update =================

#if UNITASK_PRESENT
        /// <inheritdoc />
        public async UniTask<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken = default)
#else
        /// <inheritdoc />
        public async Task<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(CancellationToken cancellationToken = default)
#endif
        {
            if (!_initialized)
            {
                return CdnResult<CatalogUpdateInfo>.Failure(
                    CdnErrorCode.Unknown, "Not initialised",
                    hint: "Call InitializeAsync before checking for catalog updates.");
            }

            // Offline is a normal answer here, not a failure: the game keeps playing on cached
            // content. WasOfflineFallback tells the caller it is worth asking again later, which a
            // plain empty list would not.
            if (!_network.IsReachable)
                return CdnResult<CatalogUpdateInfo>.Success(CatalogUpdateInfo.Offline);

            // autoReleaseHandle: false — the handle carries the result list, and releasing it
            // automatically would free the list before it can be read.
            AsyncOperationHandle<List<string>> handle;
            try
            {
                handle = Addressables.CheckForCatalogUpdates(false);
            }
            catch (Exception ex)
            {
                return CdnResult<CatalogUpdateInfo>.Failure(
                    CdnErrorCode.Unknown, "CheckForCatalogUpdates threw before it started", exception: ex);
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
                return CdnResult<CatalogUpdateInfo>.Cancelled("Cancelled while checking for catalog updates");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<CatalogUpdateInfo>.Failure(ClassifyNetworkFailure(ex));
            }

            bool succeeded = handle.Status == AsyncOperationStatus.Succeeded;
            var catalogs = succeeded && handle.Result != null
                ? new List<string>(handle.Result)
                : new List<string>();
            Exception failure = handle.OperationException;
            SafeRelease(handle);

            if (!succeeded)
            {
                // The connection dropped between the reachability check and the request. Treated as
                // the offline answer rather than an error, for the same reason as above.
                if (!_network.IsReachable)
                    return CdnResult<CatalogUpdateInfo>.Success(CatalogUpdateInfo.Offline);

                return CdnResult<CatalogUpdateInfo>.Failure(ClassifyNetworkFailure(failure));
            }

            return CdnResult<CatalogUpdateInfo>.Success(new CatalogUpdateInfo(catalogs));
        }

        // ================= 2.7 apply update =================

#if UNITASK_PRESENT
        /// <inheritdoc />
        public async UniTask<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default)
#else
        /// <inheritdoc />
        public async Task<CdnResult<IReadOnlyList<string>>> ApplyUpdateAsync(
            CatalogUpdateInfo update, CancellationToken cancellationToken = default)
#endif
        {
            if (!_initialized)
            {
                return CdnResult<IReadOnlyList<string>>.Failure(
                    CdnErrorCode.Unknown, "Not initialised",
                    hint: "Call InitializeAsync before applying a catalog update.");
            }

            if (update == null || !update.HasUpdate)
            {
                // Nothing to do is a success with an empty list, never an error: the common case is
                // a boot where the content is already current.
                return CdnResult<IReadOnlyList<string>>.Success(Array.Empty<string>());
            }

            var canDownload = _network.CanDownload();
            if (canDownload.IsFailure)
                return CdnResult<IReadOnlyList<string>>.Failure(canDownload.Error);

            var catalogs = update.CatalogsWithUpdates.ToList();

            // autoCleanBundleCache is the FIRST parameter of this overload — the package comments it
            // as "must be listed first to avoid breaking API" (Addressables.cs:2145). Passing it by
            // name is not a style choice: positionally it would bind to the catalog list.
            AsyncOperationHandle<List<IResourceLocator>> handle;
            try
            {
                handle = Addressables.UpdateCatalogs(
                    autoCleanBundleCache: true,
                    catalogs: catalogs,
                    autoReleaseHandle: false);
            }
            catch (Exception ex)
            {
                return CdnResult<IReadOnlyList<string>>.Failure(
                    CdnErrorCode.Unknown, "UpdateCatalogs threw before it started", exception: ex);
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
                return CdnResult<IReadOnlyList<string>>.Cancelled("Cancelled while applying catalog updates");
            }
            catch (Exception ex)
            {
                SafeRelease(handle);
                return CdnResult<IReadOnlyList<string>>.Failure(ClassifyNetworkFailure(ex));
            }

            bool succeeded = handle.Status == AsyncOperationStatus.Succeeded;
            var applied = succeeded && handle.Result != null
                ? handle.Result.Where(l => l != null).Select(l => l.LocatorId).ToList()
                : new List<string>();
            Exception failure = handle.OperationException;
            SafeRelease(handle);

            if (!succeeded)
                return CdnResult<IReadOnlyList<string>>.Failure(ClassifyNetworkFailure(failure));

            return CdnResult<IReadOnlyList<string>>.Success(applied);
        }

        // ================= shared =================

        /// <summary>
        /// Best-effort classification of a network-stage failure.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="CdnErrorMapper"/>, which classifies from the HTTP response code
        /// as design doc §8 requires.
        ///
        /// This used to be a local implementation returning Unknown for everything reachable, added
        /// in Phase 2 before the mapper existed, with a comment saying Phase 3 would replace it.
        /// Phase 3 added the mapper and did not come back here — so an injected 503 on the catalog
        /// came out as Unknown and non-retryable, meaning a transient server error would have been
        /// treated as permanent and the retry policy would never have run for catalog operations.
        /// The fault-injection suite caught it; nothing else would have, because both codes look
        /// like a failure to a caller that only checks IsFailure.
        /// </remarks>
        private CdnError ClassifyNetworkFailure(Exception exception)
        {
            return CdnErrorMapper.Map(exception, _rewriter.ActiveBaseUrl, _network.IsReachable);
        }

        /// <summary>
        /// Release a handle without letting a release failure mask the original error.
        /// </summary>
        private static void SafeRelease<T>(AsyncOperationHandle<T> handle)
        {
            try
            {
                if (handle.IsValid())
                    Addressables.Release(handle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CdnCatalogService] Failed to release an operation handle: {ex.Message}");
            }
        }
    }
}
