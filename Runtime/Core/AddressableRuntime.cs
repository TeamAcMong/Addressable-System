using System.Threading;
using UnityEngine;

namespace AddressableManager.Core
{
    /// <summary>
    /// Process-wide runtime state shared by every lazy-create singleton in the package.
    ///
    /// Today this backs exactly one thing: <see cref="IsShuttingDown"/>, consulted by
    /// <see cref="AddressableManager.Scopes.GlobalAssetScope"/>'s <c>Instance</c> getter and
    /// <c>Scope</c> getter so neither one builds a fresh object mid-teardown — see
    /// Documentation/LIFETIME_DESIGN.md §3.6 ("Resurrection during shutdown") and
    /// Documentation/HANDOFF_TO_SESSION_B.md A-2. The design names four more call sites that
    /// should eventually read the same flag (<c>AddressablesFacade.Instance</c>,
    /// <c>UnityMainThreadDispatcher.Instance</c>, <c>SceneAssetScope.GetOrCreate(Scene)</c>,
    /// <c>HybridScope</c>'s singleton getters) — deliberately not wired here yet; see this file's
    /// "not yet wired" note below and LIFETIME_DESIGN.md §3.6's site table.
    ///
    /// <see cref="MainThreadId"/> / <see cref="IsMainThread"/> are included because the design
    /// (§3.0, §3.3) specifies them as part of this same primitive — the package currently has
    /// three separate main-thread latches (<c>AssetLoader</c>, <c>UnityMainThreadDispatcher</c>,
    /// <c>TieredAssetLoader</c>) that disagree on default behaviour when unlatched. Consolidating
    /// them onto this one is LIFETIME_DESIGN.md §5 step 2 — a separate, not-yet-landed change.
    ///
    /// <para><b>These two members now have live readers</b>, so <see cref="IsMainThread"/>'s
    /// fail-open default is load-bearing rather than theoretical:
    /// <c>ThreadSafeCacheManager.AssertMainThread</c> and <c>.ReleaseOnMainThread</c>,
    /// <c>AddressablePoolManager.EnsureMainThreadAsync</c>, and the message text of the assertion
    /// itself. Fail-open is right for the assertion (it must not block edit-mode tooling that never
    /// triggers <see cref="Init"/>) and is a deliberate trade for the two marshalling helpers, which
    /// read it as "safe to do this inline" — see LIFETIME_DESIGN.md, "Threading: decisions still
    /// open". Do not change the default without reading that entry.</para>
    /// </summary>
    public static class AddressableRuntime
    {
        /// <summary>
        /// True once <see cref="Application.quitting"/> has fired for this process/session. Every
        /// lazy-create singleton getter that checks this must refuse to construct a new instance
        /// (GameObject or otherwise) once it is true — building one after quitting has begun is
        /// what an editor "leaked DontDestroyOnLoad" / "GameObject created during quit" report is
        /// complaining about (LIFETIME_DESIGN.md §3.6).
        /// </summary>
        public static bool IsShuttingDown { get; private set; }

        /// <summary>Managed thread id latched at <see cref="Init"/> — see <see cref="IsMainThread"/>.</summary>
        public static int MainThreadId { get; private set; }

        /// <summary>
        /// True on the thread that ran <see cref="Init"/> (Unity's main thread, under normal
        /// startup). Unlatched (<see cref="MainThreadId"/> still its default 0, i.e. before
        /// <see cref="Init"/> has ever run — edit-mode tooling that never triggers a
        /// RuntimeInitializeOnLoadMethod) reads as true: callers fail open rather than throwing
        /// before the package has had a chance to latch anything.
        /// </summary>
        public static bool IsMainThread =>
            MainThreadId == 0 || Thread.CurrentThread.ManagedThreadId == MainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            // Must run, and must reset IsShuttingDown, on every fresh Play session — even with
            // domain reload disabled. Without this line the static survives the previous
            // session's Application.quitting, the second Play session begins with
            // IsShuttingDown == true, and every guarded singleton getter in the package refuses
            // to create anything with no error at all (LIFETIME_DESIGN.md §3.0). This is the
            // highest-risk line in this file — do not remove it to "simplify".
            IsShuttingDown = false;
            MainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Application.quitting's invocation list is NOT cleared between Play sessions when
            // domain reload is disabled, so a bare "+=" here would accumulate one handler per
            // Play session. Unsubscribe first — the same dance already used at
            // Editor/Cdn/LocalContentServer.cs and by Unity's own InputSystem /
            // AssetReference.cs (LIFETIME_DESIGN.md §3.0).
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        private static void OnQuitting() => IsShuttingDown = true;
    }
}
