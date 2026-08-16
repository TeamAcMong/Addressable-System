using UnityEditor;
using UnityEngine;
using AddressableManager.Monitoring;

namespace AddressableManager.Editor.Data
{
    /// <summary>
    /// Editor implementation of IAssetMonitor
    /// Bridges Runtime events to AssetTrackerService
    /// </summary>
    [InitializeOnLoad]
    public class EditorAssetMonitor : IAssetMonitor
    {
        private static EditorAssetMonitor _instance;

        private readonly AssetTrackerService _tracker;

        static EditorAssetMonitor()
        {
            // Registers immediately so edit-mode-only usage (tests, tooling that loads
            // addressables without pressing Play) is monitored too. This alone used to be
            // considered sufficient, but it is not: see OnPlayModeStateChanged below for why a
            // second registration point is required for Play mode specifically
            // (HANDOFF_TO_SESSION_B.md E-CHAIN item 1).
            _instance = new EditorAssetMonitor();
            AssetMonitorBridge.RegisterMonitor(_instance);

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // Why EnteredPlayMode, specifically, and why the static-constructor registration above
        // cannot be relied on for Play mode:
        //
        // AssetMonitorBridge.ResetOnLoad clears every registered monitor at
        // RuntimeInitializeLoadType.SubsystemRegistration. Unity fires SubsystemRegistration
        // AFTER this class's [InitializeOnLoad] static constructor has already run — that
        // constructor runs during the domain reload that precedes entering Play mode (when domain
        // reload is enabled), or not at all on later Play sessions (when domain reload is
        // disabled, since the static already ran once at Editor startup and never runs again).
        // Either way, by the time SubsystemRegistration's Clear() executes, this monitor's
        // registration from the constructor is either wiped immediately (reload enabled) or was
        // never renewed for this session in the first place (reload disabled) — so the Dashboard
        // received nothing for the rest of the Play session. This was the actual defect: not that
        // registration never happened, but that it never happened *after* the reset that always
        // follows it.
        //
        // EditorApplication.playModeStateChanged's EnteredPlayMode fires only once Unity has
        // fully finished entering Play mode — after every RuntimeInitializeOnLoadMethod callback
        // (SubsystemRegistration included) has already run. Re-registering there closes the
        // window regardless of the domain-reload setting, and regardless of whether the
        // constructor above happened to run this session.
        //
        // It is not, by itself, early enough: EnteredPlayMode fires after scene objects already
        // in the open scene have run Awake()/OnEnable() (order is SubsystemRegistration ->
        // AfterAssembliesLoaded -> BeforeSplashScreen -> BeforeSceneLoad -> scene load (Awake /
        // OnEnable) -> AfterSceneLoad -> EnteredPlayMode -> Start/Update). A BaseAssetScope built
        // or an AssetLoader.LoadAsync call made from a scene object's own Awake()/OnEnable()
        // during that window would still find _monitors empty (ResetOnLoad cleared it at
        // SubsystemRegistration, and this class's re-registration hadn't run yet) and be
        // silently dropped. RuntimeInitializeOnLoadMethod-tagged methods on Editor-only
        // assemblies still fire when entering Play mode in the Editor — the callback list isn't
        // partitioned by which assembly registered it — so tagging this stage directly closes
        // the gap instead of only reacting after it already passed.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ReRegisterBeforeSceneLoad()
        {
            if (_instance == null)
            {
                _instance = new EditorAssetMonitor();
            }

            AssetMonitorBridge.RegisterMonitor(_instance);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;

            if (_instance == null)
            {
                _instance = new EditorAssetMonitor();
            }

            // RegisterMonitor is idempotent — a no-op if _instance is already listed — so this is
            // safe to call unconditionally rather than trying to detect whether
            // SubsystemRegistration actually ran (and cleared it) this session.
            AssetMonitorBridge.RegisterMonitor(_instance);
        }

        public EditorAssetMonitor()
        {
            _tracker = AssetTrackerService.Instance;
        }

        public void OnAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        {
            _tracker.RegisterAssetLoad(address, typeName, scopeName, loadDuration, fromCache);

            // Also record in performance metrics
            PerformanceMetrics.Instance.RecordLoadTime(address, typeName, loadDuration, fromCache);
        }

        public void OnAssetReleased(string address, string typeName)
        {
            _tracker.RegisterAssetRelease(address, typeName);
        }

        public void OnScopeRegistered(string scopeName, bool isActive)
        {
            _tracker.RegisterScope(scopeName, isActive);
        }

        public void OnScopeStateChanged(string scopeName, bool isActive)
        {
            _tracker.UpdateScopeState(scopeName, isActive);
        }

        public void OnScopeCleared(string scopeName)
        {
            _tracker.ClearScope(scopeName);
        }
    }
}
