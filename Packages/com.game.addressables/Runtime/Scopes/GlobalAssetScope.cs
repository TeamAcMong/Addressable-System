using UnityEngine;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Global scope - persists throughout entire application lifetime (DontDestroyOnLoad)
    /// Use for: UI atlases, sound effects, global configs, etc.
    /// </summary>
    public class GlobalAssetScope : MonoBehaviour, IAssetScope
    {
        private static GlobalAssetScope _instance;
        private BaseAssetScope _scope;

        public string ScopeName => _scope?.ScopeName ?? "Global";
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        public static GlobalAssetScope Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[GlobalAssetScope]");
                    _instance = go.AddComponent<GlobalAssetScope>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
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
            _scope = new InternalScope("Global");
            _scope.Activate();
            DontDestroyOnLoad(gameObject);
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
            if (_instance == this)
            {
                Dispose();
                _instance = null;
            }
        }

        // Internal wrapper class
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string name) : base(name) { }
        }
    }
}
