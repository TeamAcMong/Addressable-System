using UnityEngine;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Hierarchy scope - tied to specific GameObject lifetime.
    /// Auto-cleanup when GameObject is destroyed.
    /// Use for: per-character assets, per-enemy resources, UI panel assets, etc.
    ///
    /// Identity: each <c>HierarchyAssetScope</c> generates a unique scope id
    /// of the form <c>Hierarchy-{name}#{instanceId}</c> by default. Override
    /// via <see cref="customScopeId"/> (Inspector) or pass to
    /// <see cref="AddTo(GameObject, string, string)"/>.
    /// </summary>
    public class HierarchyAssetScope : MonoBehaviour, IAssetScope
    {
        [SerializeField, Tooltip("Optional unique scope id. If empty, auto-derived from GameObject name + InstanceID — guaranteed unique per Unity object instance.")]
        private string customScopeId;

        [SerializeField, Tooltip("Optional friendly label for Dashboard. Defaults to the GameObject name.")]
        private string customDisplayName;

        // Static handoff used by AddTo(GameObject, customScopeId) so the
        // newly-AddComponent'd instance picks up the id before its Awake runs.
        // Only safe under Unity's single-threaded main-thread model.
        private static string _pendingScopeId;
        private static string _pendingDisplayName;

        private BaseAssetScope _scope;

        public string ScopeId => _scope?.ScopeId ?? "Hierarchy";
        public string DisplayName => _scope?.DisplayName ?? gameObject.name;
        public string ScopeName => ScopeId; // back-compat
        public Loaders.AssetLoader Loader => _scope?.Loader;
        public bool IsActive => _scope?.IsActive ?? false;

        private void Awake()
        {
            // Consume any pending id queued by the AddTo factory.
            if (string.IsNullOrEmpty(customScopeId) && !string.IsNullOrEmpty(_pendingScopeId))
            {
                customScopeId = _pendingScopeId;
            }
            if (string.IsNullOrEmpty(customDisplayName) && !string.IsNullOrEmpty(_pendingDisplayName))
            {
                customDisplayName = _pendingDisplayName;
            }

            var id = string.IsNullOrEmpty(customScopeId)
                ? $"Hierarchy-{gameObject.name}#{GetInstanceID()}"
                : customScopeId;
            var display = string.IsNullOrEmpty(customDisplayName)
                ? gameObject.name
                : customDisplayName;

            _scope = new InternalScope(id, display);
            _scope.Activate();
        }

        public void Activate() => _scope?.Activate();
        public void Deactivate() => _scope?.Deactivate();
        public void Dispose() => _scope?.Dispose();

        private void OnDestroy()
        {
            Debug.Log($"[HierarchyAssetScope] GameObject {gameObject.name} destroyed, cleaning up scope {ScopeId}");
            Dispose();
        }

        // Internal wrapper
        private class InternalScope : BaseAssetScope
        {
            public InternalScope(string id, string display) : base(id, display) { }
        }

        /// <summary>
        /// Add a HierarchyAssetScope to a GameObject.
        /// Pass <paramref name="customScopeId"/> to make the scope id semantic
        /// (e.g. "PlayerInventory") instead of the default name+InstanceID form.
        /// </summary>
        public static HierarchyAssetScope AddTo(GameObject target, string customScopeId = null, string customDisplayName = null)
        {
            if (target == null)
            {
                Debug.LogError("[HierarchyAssetScope] Cannot add to null GameObject");
                return null;
            }

            var existing = target.GetComponent<HierarchyAssetScope>();
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(customScopeId) && existing.ScopeId != customScopeId)
                {
                    Debug.LogWarning($"[HierarchyAssetScope] {target.name} already has HierarchyAssetScope " +
                                     $"with id '{existing.ScopeId}'; ignoring requested id '{customScopeId}'.");
                }
                return existing;
            }

            // Stash the requested id/display so the newly-added component's Awake picks it up.
            _pendingScopeId = customScopeId;
            _pendingDisplayName = customDisplayName;
            try
            {
                return target.AddComponent<HierarchyAssetScope>();
            }
            finally
            {
                _pendingScopeId = null;
                _pendingDisplayName = null;
            }
        }
    }
}
