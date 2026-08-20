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

        /// <summary>
        /// The rewriter the installed hooks resolve against. A field, not a captured parameter, so a
        /// repeat <see cref="Install"/> actually takes effect.
        /// </summary>
        private static IHostRewriter _activeRewriter;
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
                // in the Editor, and failing there would be noise. But it must still REBIND: CdnManager
                // builds a brand-new HostRewriter on every InitializeAsync, so a first attempt that
                // installed the hooks and then failed later (offline, bad catalog) left every request
                // permanently pointed at the first environment while the facade reported the second.
                _activeRewriter = rewriter;
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

            // Rebind the rewriter even on a repeat Install (see the _installed branch): the hooks below
            // must resolve the CURRENT rewriter per request, not the one captured by whichever call
            // happened to install first.
            _activeRewriter = rewriter;

            _previousWebRequestOverride = Addressables.WebRequestOverride;
            _previousIdTransform = Addressables.InternalIdTransformFunc;

            int timeoutSeconds = policy.TimeoutSeconds;

            Addressables.WebRequestOverride = request =>
            {
                // Whatever the project already installed runs first and keeps its effect unless we
                // deliberately override the same field.
                //
                // Isolated, because this whole delegate runs INSIDE ResourceManager.Update - see
                // InvokeHook. A hook that throws here would otherwise propagate into the middle of
                // Addressables' own update loop.
                InvokeHook(
                    () => _previousWebRequestOverride?.Invoke(request),
                    "the WebRequestOverride this project installed before Cdn.InitializeAsync");

                if (request == null) return;

                // NOT on bundle downloads. UnityWebRequest.timeout is a cap on the whole transfer,
                // while the 30s this package configures everywhere is Addressables' BUNDLE timeout,
                // which is an IDLE timer - AssetBundleProvider resets it on every byte received and
                // never aborts a transfer that is still progressing. Addressables therefore leaves
                // request.timeout unset for bundles on purpose and sets it only for small catalog,
                // hash and text files. Setting it here applied wall-clock semantics to a number chosen
                // for idle semantics, so any bundle taking longer than 30 seconds - a large bundle, a
                // slow phone, a bad network - was aborted mid-download at full speed. Unity's bundle
                // cache only commits completed downloads, so every retry restarted from zero and hit
                // the same wall: a permanently un-downloadable bundle on exactly the connections that
                // need a CDN most.
                if (!(request.downloadHandler is DownloadHandlerAssetBundle))
                {
                    request.timeout = timeoutSeconds;
                }

                // Fetched per request so a refreshed token is picked up without reinstalling - which
                // also means this delegate runs inside ResourceManager.Update on EVERY bundle, catalog
                // and hash request. It must return an already-held token synchronously.
                string token = null;
                InvokeHook(
                    () => token = authHeaderProvider?.Invoke(),
                    "the authHeaderProvider passed to Cdn.InitializeAsync");

                if (!string.IsNullOrEmpty(token))
                    request.SetRequestHeader("Authorization", $"Bearer {token}");
            };

            Addressables.InternalIdTransformFunc = location =>
            {
                if (location == null) return null;

                // Chain: let an existing transform decide the id first, then rewrite its origin.
                // Isolated for the same reason as the request hook - this also runs inside
                // ResourceManager.Update, and falling back to the untransformed id is better than an
                // exception thrown through Addressables' update loop.
                string id = location.InternalId;
                if (_previousIdTransform != null)
                {
                    InvokeHook(
                        () => id = _previousIdTransform(location),
                        "the InternalIdTransformFunc this project installed before Cdn.InitializeAsync");
                }

                var active = _activeRewriter;
                return active != null ? active.Rewrite(id) : id;
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
            _activeRewriter = null;
            _installed = false;
        }

        /// <summary>
        /// Runs a consumer-supplied delegate that executes inside Addressables' update loop, turning
        /// the two ways it can go wrong into something a reader can act on.
        /// </summary>
        /// <remarks>
        /// Both hooks this class installs are invoked by the ResourceManager while it is inside
        /// <c>ResourceManager.Update</c>. Two consequences that are invisible from the call site:
        ///
        /// <para><b>Re-entrancy.</b> Anything that pumps Addressables from in here - most often
        /// <c>WaitForCompletion()</c>, but equally blocking on a Task that only completes once
        /// Addressables advances, or starting and awaiting another Addressables operation - re-enters
        /// the update loop, and Unity throws <c>"Reentering the Update method is not allowed"</c> from
        /// a stack that names only Unity's own frames. The delegate that caused it never appears in
        /// that stack. This is the only place that still knows which delegate was running, so the log
        /// written here names it.</para>
        ///
        /// <para><b>Blocking.</b> Even without re-entering, work done here stalls the loader: the hook
        /// runs on every bundle, catalog and hash request. A token must already be in hand and be
        /// returned synchronously - fetch and cache it before <c>Cdn.InitializeAsync</c>, and refresh
        /// it on your own schedule, never from inside the hook.</para>
        ///
        /// A failing hook does not fail the request: the header is simply not attached, or the id is
        /// left as Addressables resolved it. That surfaces as an ordinary 401 or a miss, which
        /// <see cref="CdnErrorMapper"/> already classifies, instead of an exception thrown through the
        /// middle of Addressables' update.
        /// </remarks>
        private static void InvokeHook(Action hook, string description)
        {
            try
            {
                hook();
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[CdnRequestDecorator] {description} threw inside Addressables' update loop: {ex.Message}\n" +
                    "This hook runs on every request, from inside ResourceManager.Update. It must not call " +
                    "WaitForCompletion, must not block on a Task, and must not start or await an Addressables " +
                    "operation - each of those re-enters the update loop. Return an already-held value " +
                    "synchronously instead. The request continues without this hook's contribution.\n" + ex);
            }
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
            _activeRewriter = null;
        }
    }
}
