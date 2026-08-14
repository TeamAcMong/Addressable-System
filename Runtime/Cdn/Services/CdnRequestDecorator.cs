using System;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Installs the two Addressables hooks the CDN layer needs — task 2.3.
    /// </summary>
    /// <remarks>
    /// TIMING IS THE WHOLE POINT
    /// Both hooks are consulted while content loads. Addressables reads
    /// InternalIdTransformFunc when it resolves a location into a URL, so anything already
    /// resolved before the hook is installed keeps the URL baked at build time. Installing after
    /// initialisation therefore produces a build that mostly works — the catalog was fetched from
    /// the wrong origin, later bundles from the right one — which is far harder to diagnose than a
    /// clean failure.
    ///
    /// Addressables initialises implicitly on the first load call, so a single stray
    /// Addressables.LoadAssetAsync early in boot is enough to lose the hooks. <see cref="Install"/>
    /// refuses in that case rather than installing something that will not be honoured.
    ///
    /// EXISTING HOOKS ARE CHAINED, NOT REPLACED
    /// Both properties hold a single delegate, so assigning clobbers whatever the project already
    /// had — an auth header, a custom timeout. Any existing delegate is captured and called first.
    /// </remarks>
    public static class CdnRequestDecorator
    {
        private static bool _installed;
        private static Action<UnityWebRequest> _previousWebRequestOverride;
        private static Func<IResourceLocation, string> _previousIdTransform;

        /// <summary>Whether the hooks are currently installed.</summary>
        public static bool IsInstalled => _installed;

        /// <summary>
        /// True when Addressables has already loaded a catalog.
        /// </summary>
        /// <remarks>
        /// Reads Addressables.ResourceLocators, which is a plain pass-through
        /// (Addressables.cs:811) and does not itself start initialisation. The property that would
        /// have — ChainOperation — is deliberately not touched here, since a check that causes the
        /// condition it is testing for is worse than no check.
        /// </remarks>
        public static bool HasAddressablesInitialized
        {
            get
            {
                var locators = Addressables.ResourceLocators;
                return locators != null && locators.Any();
            }
        }

        /// <summary>
        /// Install the hooks. Must be called before anything touches Addressables.
        /// </summary>
        /// <param name="rewriter">Supplies the active origin for URL rewriting.</param>
        /// <param name="policy">Supplies the per-request timeout.</param>
        /// <param name="authHeaderProvider">
        /// Optional. Returns a bearer token to attach, or null for none. Called per request, so a
        /// refreshed token is picked up without reinstalling.
        /// </param>
        public static CdnResult<bool> Install(
            IHostRewriter rewriter,
            DownloadPolicy policy,
            Func<string> authHeaderProvider = null)
        {
            if (rewriter == null)
                return CdnResult<bool>.Failure(CdnErrorCode.Unknown, "Host rewriter is null");

            policy = policy ?? DownloadPolicy.Default;

            if (_installed)
            {
                // Idempotent rather than an error: a second Initialize on a warm domain is normal
                // in the Editor, and failing there would be noise.
                return CdnResult<bool>.Success(true);
            }

            if (HasAddressablesInitialized)
            {
                return CdnResult<bool>.Failure(
                    CdnErrorCode.Unknown,
                    "Addressables has already initialised, so the CDN hooks would not be honoured for " +
                    "content resolved before now",
                    hint: "Cdn.InitializeAsync must be the first Addressables call in the boot sequence. " +
                          "Something — often a stray LoadAssetAsync or an AssetReference on a scene object " +
                          "in the first scene — initialised it earlier. Move that after CDN init.");
            }

            _previousWebRequestOverride = Addressables.WebRequestOverride;
            _previousIdTransform = Addressables.InternalIdTransformFunc;

            int timeoutSeconds = policy.TimeoutSeconds;

            Addressables.WebRequestOverride = request =>
            {
                // Whatever the project already installed runs first and keeps its effect unless we
                // deliberately override the same field.
                _previousWebRequestOverride?.Invoke(request);

                if (request == null) return;

                request.timeout = timeoutSeconds;

                string token = authHeaderProvider?.Invoke();
                if (!string.IsNullOrEmpty(token))
                    request.SetRequestHeader("Authorization", $"Bearer {token}");
            };

            Addressables.InternalIdTransformFunc = location =>
            {
                if (location == null) return null;

                // Chain: let an existing transform decide the id first, then rewrite its origin.
                string id = _previousIdTransform != null
                    ? _previousIdTransform(location)
                    : location.InternalId;

                return rewriter.Rewrite(id);
            };

            _installed = true;
            return CdnResult<bool>.Success(true);
        }

        /// <summary>
        /// Remove the hooks and restore whatever was there before.
        /// </summary>
        /// <remarks>
        /// Exists for tests and for domain reloads. Restores the captured delegates rather than
        /// nulling, so uninstalling does not silently remove a project's own auth header.
        /// </remarks>
        public static void Uninstall()
        {
            if (!_installed) return;

            Addressables.WebRequestOverride = _previousWebRequestOverride;
            Addressables.InternalIdTransformFunc = _previousIdTransform;

            _previousWebRequestOverride = null;
            _previousIdTransform = null;
            _installed = false;
        }

        /// <summary>
        /// Reset static state when entering play mode with domain reload disabled.
        /// </summary>
        /// <remarks>
        /// Without this, _installed stays true from the previous play session while the hooks
        /// themselves were reset by Addressables, and Install() would return success having done
        /// nothing — the "already installed" branch lying about state that no longer exists.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _installed = false;
            _previousWebRequestOverride = null;
            _previousIdTransform = null;
        }
    }
}
