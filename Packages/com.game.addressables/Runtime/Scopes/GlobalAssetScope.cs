using UnityEngine;
using AddressableManager.Core;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Global scope - persists throughout entire application lifetime (DontDestroyOnLoad)
    /// Use for: UI atlases, sound effects, global configs, etc.
    ///
    /// Storage identity (see Documentation/LIFETIME_DESIGN.md §5 step 4/6 and
    /// Documentation/HANDOFF_TO_SESSION_B.md A-2/A-12 for the full map across the package): this
    /// is storage "A" — the loader behind every <c>Simple.*</c>/<c>Standard.*</c> global call and
    /// <see cref="AddressableManager.Facade.AddressablesFacade.GetGlobalScope"/>. It shares no
    /// state with <see cref="HybridScope.Global"/> (storage "D", reached through
    /// <c>Advanced.GetHybridGlobalScope()</c>) despite the similar name — they are independent
    /// caches with independent monitoring channels.
    ///
    /// Lifetime: this singleton outlives <c>AddressablesFacade</c> restarts on purpose — only
    /// this class's own <see cref="OnDestroy"/> disposes it (LIFETIME_DESIGN.md §5 step 4,
    /// decision (b)). <see cref="Dispose"/> is total: it never leaves the object in a state no
    /// method can exit (the A-2 defect) — the scope rebuilds itself on next access via the
    /// private <c>Scope</c> getter rather than NullReferencing forever.
    /// </summary>
    public class GlobalAssetScope : MonoBehaviour, IAssetScope
    {
        private static GlobalAssetScope _instance;
        private BaseAssetScope _scope;

        public string ScopeName => Scope?.ScopeName ?? "Global";
        public Loaders.AssetLoader Loader => Scope?.Loader;
        public bool IsActive => Scope?.IsActive ?? false;

        /// <summary>
        /// The live scope backing this singleton, rebuilding itself on demand. Total across every
        /// legal call sequence (LIFETIME_DESIGN.md §2.2): before this getter existed, the state
        /// after <see cref="Dispose"/> was "_scope non-null, _scope.Loader null" — a husk that
        /// read as merely deactivated (ScopeName still "Global", IsActive false) while
        /// <see cref="Loader"/> NullReferenced for the rest of the process, because nothing ever
        /// rebuilt it. This getter is that missing exit: any access after Dispose() constructs a
        /// fresh scope instead. Guarded the same way every lazy-create singleton in the package
        /// is guarded — do not build anything once the process is shutting down
        /// (<see cref="AddressableRuntime.IsShuttingDown"/>), and do not resurrect state on a
        /// MonoBehaviour Unity has actually destroyed (<c>this != null</c>).
        /// </summary>
        private BaseAssetScope Scope
        {
            get
            {
                if (_scope == null && !AddressableRuntime.IsShuttingDown && this != null)
                {
                    _scope = new InternalScope("Global");
                    _scope.Activate();
                }
                return _scope;
            }
        }

        public static GlobalAssetScope Instance
        {
            get
            {
                // Do not build a new DontDestroyOnLoad GameObject once the process is shutting
                // down — a pooled object released from another object's OnDestroy during quit
                // would otherwise construct one in the middle of teardown, which Unity reports as
                // a leak (LIFETIME_DESIGN.md §3.6). Callers that may run during quit should check
                // HasInstance first; SimpleAPI.Destroy is the shipped example.
                if (_instance == null && !AddressableRuntime.IsShuttingDown)
                {
                    var go = new GameObject("[GlobalAssetScope]");
                    _instance = go.AddComponent<GlobalAssetScope>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        /// <summary>
        /// True when an instance already exists and may be used right now. The non-sentinel way
        /// to ask "would Instance hand me something real" — <c>Instance == null</c> would itself
        /// build the instance it is trying to detect the absence of once the process is *not*
        /// shutting down, and would silently answer "no instance" for two different reasons (none
        /// ever created; one exists but we're mid-teardown) if read during shutdown. Any code that
        /// may run from another object's <c>OnDestroy</c> — pooled-instance cleanup chief among
        /// them — should check this before touching <see cref="Instance"/>
        /// (LIFETIME_DESIGN.md §3.6).
        /// </summary>
        public static bool HasInstance => _instance != null && !AddressableRuntime.IsShuttingDown;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _scope = new InternalScope("Global");
            _scope.Activate();
            DontDestroyOnLoad(gameObject);
        }

        public void Activate()
        {
            Scope?.Activate();
        }

        public void Deactivate()
        {
            Scope?.Deactivate();
        }

        /// <summary>
        /// Tears down the current scope and drops the reference to it — never one without the
        /// other. Leaving <c>_scope</c> non-null-but-disposed was the actual A-2 defect: reads
        /// afterwards looked like "switched off" (ScopeName still "Global", IsActive false)
        /// instead of "dead", and <see cref="Loader"/> NullReferenced for the rest of the process
        /// because nothing ever rebuilt it. The GameObject survives this call by design — this
        /// singleton is not owned by whoever calls Dispose() (see the class docs); the next access
        /// to <see cref="Loader"/>/<see cref="ScopeName"/>/<see cref="IsActive"/> rebuilds a fresh
        /// scope through the private <c>Scope</c> getter.
        /// </summary>
        public void Dispose()
        {
            _scope?.Dispose();
            _scope = null;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                Dispose();
                _instance = null;
            }
        }

        // Internal wrapper class
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string name) : base(name, "Global") { }
        }
    }
}
