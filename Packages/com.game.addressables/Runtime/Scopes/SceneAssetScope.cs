using UnityEngine;
using UnityEngine.SceneManagement;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Scene scope - tied to specific scene lifetime, auto-cleanup on scene unload.
    /// Use for: scene-specific prefabs, materials, scene configs, etc.
    ///
    /// Identity: each <c>SceneAssetScope</c> generates a unique scope id of the
    /// form <c>Scene-{sceneName}#h{handle}</c> by default. The handle is unique
    /// per loaded scene instance, so additive scenes with the same name no
    /// longer collide. Override via <see cref="customScopeId"/> or pass to
    /// <see cref="CreateForScene(Scene, string, string)"/>.
    /// </summary>
    public class SceneAssetScope : MonoBehaviour, IAssetScope
    {
        [SerializeField, Tooltip("Optional unique scope id. If empty, auto-derived from scene name + handle — guaranteed unique even when two scenes share a name.")]
        private string customScopeId;

        [SerializeField, Tooltip("Optional friendly label for Dashboard. Defaults to the scene name.")]
        private string customDisplayName;

        // Handoff used by CreateForScene to inject id before Awake fires.
        private static string _pendingScopeId;
        private static string _pendingDisplayName;

        private BaseAssetScope _scope;
        private Scene _ownerScene;
        private bool _subscribed;

        public string ScopeId => _scope?.ScopeId ?? "Scene";
        public string DisplayName => _scope?.DisplayName ?? (_ownerScene.IsValid() ? _ownerScene.name : "Scene");
        public string ScopeName => ScopeId; // back-compat
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        /// <summary>The scene this scope is bound to.</summary>
        public Scene OwnerScene => _ownerScene;

        private void Awake()
        {
            _ownerScene = gameObject.scene;

            if (string.IsNullOrEmpty(customScopeId) && !string.IsNullOrEmpty(_pendingScopeId))
            {
                customScopeId = _pendingScopeId;
            }
            if (string.IsNullOrEmpty(customDisplayName) && !string.IsNullOrEmpty(_pendingDisplayName))
            {
                customDisplayName = _pendingDisplayName;
            }

            var id = string.IsNullOrEmpty(customScopeId)
                ? $"Scene-{_ownerScene.name}#h{_ownerScene.handle}"
                : customScopeId;
            var display = string.IsNullOrEmpty(customDisplayName)
                ? _ownerScene.name
                : customDisplayName;

            _scope = new InternalScope(id, display);
            _scope.Activate();

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _subscribed = true;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene != _ownerScene) return;

            Debug.Log($"[SceneAssetScope] Scene {scene.name} (h={scene.handle}) unloaded, cleaning up scope {ScopeId}");
            if (this != null) Destroy(gameObject);
        }

        public void Activate() => _scope?.Activate();
        public void Deactivate() => _scope?.Deactivate();

        public void Dispose()
        {
            if (_subscribed)
            {
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                _subscribed = false;
            }
            _scope?.Dispose();
        }

        private void OnDestroy() => Dispose();

        // Internal wrapper
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string id, string display) : base(id, display) { }
        }

        /// <summary>
        /// Create a SceneAssetScope bound to <paramref name="scene"/>.
        /// The new GameObject is moved into the target scene so the scope's
        /// owner matches the intent, not the currently-active scene.
        /// </summary>
        public static SceneAssetScope CreateForScene(Scene scene, string customScopeId = null, string customDisplayName = null)
        {
            var go = new GameObject("[SceneAssetScope]");
            if (scene.IsValid() && scene != SceneManager.GetActiveScene())
            {
                SceneManager.MoveGameObjectToScene(go, scene);
            }

            _pendingScopeId = customScopeId;
            _pendingDisplayName = customDisplayName;
            try
            {
                return go.AddComponent<SceneAssetScope>();
            }
            finally
            {
                _pendingScopeId = null;
                _pendingDisplayName = null;
            }
        }

        /// <summary>
        /// Create a SceneAssetScope for the currently-active scene.
        /// </summary>
        public static SceneAssetScope CreateForCurrentScene(string customScopeId = null, string customDisplayName = null)
            => CreateForScene(SceneManager.GetActiveScene(), customScopeId, customDisplayName);

        /// <summary>
        /// Find an existing SceneAssetScope bound to <paramref name="scene"/>,
        /// or create one. Cross-scene safe — unlike the parameterless overload,
        /// this never returns a scope from a different loaded scene.
        /// </summary>
        public static SceneAssetScope GetOrCreate(Scene scene)
        {
            // DO NOT "fix" this warning by following the message Unity prints with it.
            //
            // On Unity 6 this overload is obsolete and the message says to use
            // "FindObjectsByType<T>() or FindObjectsByType<T>(FindObjectsInactive)". Those exist
            // on Unity 6 only. This package declares unity 2023.1, and on 2022.3/2023.x the sole
            // generic overloads are:
            //
            //     FindObjectsByType<T>(FindObjectsSortMode)
            //     FindObjectsByType<T>(FindObjectsInactive, FindObjectsSortMode)
            //
            // so the single-argument form binds to the sort-mode overload and fails with
            // CS1503: cannot convert from 'FindObjectsInactive' to 'FindObjectsSortMode'.
            //
            // That is not hypothetical. 4.1.0-pre.4 shipped the single-argument form. It compiled
            // here, because it was only ever compiled against the newest editor on the machine,
            // and broke on the first integration into a project on an older one. Verifying against
            // the newest Unity installed says nothing about the oldest Unity supported —
            // Tools/check-min-unity-api.sh now compiles against a chosen editor for exactly this.
            //
            // The two-argument form works on every supported version, and SortMode.None is what
            // this call wants anyway. So the warning is suppressed at the call site rather than
            // designed around; dropping Include would silently stop finding scopes on inactive
            // objects, which is worse than a suppressed warning.
#pragma warning disable CS0618 // Unity 6 names a replacement that does not exist on 2023.1; see above
            var all = FindObjectsByType<SceneAssetScope>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
            foreach (var s in all)
            {
                if (s._ownerScene == scene) return s;
            }
            return CreateForScene(scene);
        }

        /// <summary>
        /// Find or create SceneAssetScope for the currently-active scene.
        /// </summary>
        public static SceneAssetScope GetOrCreate() => GetOrCreate(SceneManager.GetActiveScene());
    }
}
