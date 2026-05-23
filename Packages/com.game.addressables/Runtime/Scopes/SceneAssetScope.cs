using UnityEngine;
using UnityEngine.SceneManagement;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Scene scope - tied to specific scene lifetime, auto-cleanup on scene unload
    /// Use for: Scene-specific prefabs, materials, scene configs, etc.
    /// </summary>
    public class SceneAssetScope : MonoBehaviour, IAssetScope
    {
        private BaseAssetScope _scope;
        private Scene _ownerScene;
        private bool _subscribed;

        public string ScopeName => _scope?.ScopeName ?? "Scene";
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        private void Awake()
        {
            _ownerScene = gameObject.scene;
            _scope = new InternalScope($"Scene-{_ownerScene.name}");
            _scope.Activate();

            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _subscribed = true;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (scene != _ownerScene) return;

            Debug.Log($"[SceneAssetScope] Scene {scene.name} unloaded, cleaning up scope");
            // Destroying the GameObject funnels every cleanup path through OnDestroy below,
            // collapsing scene-unload and explicit-destroy into a single Dispose() call.
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
            public InternalScope(string name) : base(name) { }
        }

        /// <summary>
        /// Create a SceneAssetScope for current scene
        /// </summary>
        public static SceneAssetScope CreateForCurrentScene()
        {
            var go = new GameObject("[SceneAssetScope]");
            return go.AddComponent<SceneAssetScope>();
        }

        /// <summary>
        /// Find or create SceneAssetScope for current scene
        /// </summary>
        public static SceneAssetScope GetOrCreate()
        {
            var existing = FindFirstObjectByType<SceneAssetScope>();
            return existing != null ? existing : CreateForCurrentScene();
        }
    }
}
