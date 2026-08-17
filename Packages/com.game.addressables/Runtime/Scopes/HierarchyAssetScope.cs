using UnityEngine;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Hierarchy scope - tied to specific GameObject lifetime.
    /// Auto-cleanup when GameObject is destroyed.
    /// Use for: per-character assets, per-enemy resources, UI panel assets, etc.
    ///
    /// Identity: each <c>HierarchyAssetScope</c> generates a unique scope id
    /// of the form <c>Hierarchy-{name}#{tag}</c> by default, where <c>tag</c> is a
    /// per-object identifier guaranteed unique for the object's lifetime (its shape
    /// varies by Unity version -- see <see cref="Awake"/>). Override via
    /// <see cref="customScopeId"/> (Inspector) or pass to
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

            // Unity 6000.5 marks Object.GetInstanceID() Obsolete(error: true) (CS0619) --
            // it no longer compiles. The obvious fix, GetEntityId() (available since
            // 6000.4), returns the 64-bit EntityId struct -- but casting it back to int is
            // *also* Obsolete(error: true) on 6000.5 ("EntityId will not be representable
            // by an int in the future"), so the pre-6000.5 int-shaped id cannot be
            // preserved past this version; Unity deliberately closed that door. This id is
            // only used as an in-process ScopeManager dictionary key / monitoring channel
            // name (see BaseAssetScope) -- never persisted or parsed back -- so a
            // differently-shaped-but-still-unique string is safe, it just must not change
            // *silently*. Below 6000.5: "#<signed 32-bit instance id>" (e.g. "#-1234").
            // From 6000.5 on: "#<EntityId.ToString()>", which prints the struct's raw
            // 64-bit value as an unsigned decimal (e.g. "#4294967295") -- same uniqueness
            // guarantee, different shape. Do not "simplify" this back into a single call.
#if UNITY_6000_5_OR_NEWER
            string GetScopeInstanceTag() => GetEntityId().ToString();
#else
            string GetScopeInstanceTag() => GetInstanceID().ToString();
#endif

            var id = string.IsNullOrEmpty(customScopeId)
                ? $"Hierarchy-{gameObject.name}#{GetScopeInstanceTag()}"
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
