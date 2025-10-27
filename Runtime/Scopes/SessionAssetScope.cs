using UnityEngine;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Session scope - tied to gameplay session (e.g., from MainMenu -> Gameplay -> EndScreen)
    /// Auto-cleanup when session ends. Persists across scene transitions within session.
    /// Use for: Character data, level-specific assets, session configs, etc.
    /// </summary>
    public class SessionAssetScope : MonoBehaviour, IAssetScope
    {
        private static SessionAssetScope _instance;
        private BaseAssetScope _scope;

        public string ScopeName => _scope?.ScopeName ?? "Session";
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        public static SessionAssetScope Instance => _instance;

        /// <summary>
        /// Start a new session
        /// </summary>
        public static SessionAssetScope StartSession()
        {
            if (_instance != null)
            {
                Debug.LogWarning("[SessionAssetScope] Session already active, ending previous session");
                EndSession();
            }

            var go = new GameObject("[SessionAssetScope]");
            _instance = go.AddComponent<SessionAssetScope>();
            DontDestroyOnLoad(go);

            Debug.Log("[SessionAssetScope] New session started");
            return _instance;
        }

        /// <summary>
        /// End current session and cleanup
        /// </summary>
        public static void EndSession()
        {
            if (_instance != null)
            {
                Debug.Log("[SessionAssetScope] Ending session");
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _scope = new InternalScope("Session");
            _scope.Activate();
        }

        public void Activate()
        {
            _scope?.Activate();
        }

        public void Deactivate()
        {
            _scope?.Deactivate();
        }

        public void Dispose()
        {
            _scope?.Dispose();
        }

        private void OnDestroy()
        {
            Dispose();
            if (_instance == this)
            {
                _instance = null;
            }
        }

        // Internal wrapper
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string name) : base(name) { }
        }
    }
}
