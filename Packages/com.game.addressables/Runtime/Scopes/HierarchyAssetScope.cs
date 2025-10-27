using UnityEngine;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Hierarchy scope - tied to specific GameObject lifetime
    /// Auto-cleanup when GameObject is destroyed
    /// Use for: Per-character assets, per-enemy resources, UI panel assets, etc.
    /// </summary>
    public class HierarchyAssetScope : MonoBehaviour, IAssetScope
    {
        private BaseAssetScope _scope;

        public string ScopeName => _scope?.ScopeName ?? "Hierarchy";
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        private void Awake()
        {
            _scope = new InternalScope($"Hierarchy-{gameObject.name}");
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
            Debug.Log($"[HierarchyAssetScope] GameObject {gameObject.name} destroyed, cleaning up scope");
            Dispose();
        }

        // Internal wrapper
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string name) : base(name) { }
        }

        /// <summary>
        /// Add HierarchyAssetScope to a GameObject
        /// </summary>
        public static HierarchyAssetScope AddTo(GameObject target)
        {
            if (target == null)
            {
                Debug.LogError("[HierarchyAssetScope] Cannot add to null GameObject");
                return null;
            }

            var existing = target.GetComponent<HierarchyAssetScope>();
            if (existing != null)
            {
                Debug.LogWarning($"[HierarchyAssetScope] {target.name} already has HierarchyAssetScope");
                return existing;
            }

            return target.AddComponent<HierarchyAssetScope>();
        }
    }
}
