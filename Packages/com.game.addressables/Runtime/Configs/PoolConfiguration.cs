using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace AddressableManager.Configs
{
    /// <summary>
    /// Configuration for object pools. Define all your pools in one place!
    /// </summary>
    /// <remarks>
    /// <para><b>WARNING — NOTHING IN THIS ASSET IS READ BY ANY RUNTIME CODE (discovery report
    /// P-10).</b> Not one field, not one method. A repo-wide search for consumers finds only
    /// <c>Editor/Tools/ContextMenus.cs</c> (which <i>creates</i> the asset), the documentation, and
    /// one comment. <c>Assets/PoolConfig.asset</c> exists in this project and is read by nothing.
    /// Editing values here changes no behaviour whatsoever.</para>
    ///
    /// <para>This warning is a <b>holding action</b>, not the fix. Whether this class should be
    /// wired into a startup path or marked <c>[Obsolete]</c> in its entirety (together with the two
    /// <c>ContextMenus</c> entries and the <c>EDITOR_TOOLS_GUIDE.md</c> section) is a product
    /// decision recorded under "Pooling: decisions still open" in
    /// <c>Documentation/LIFETIME_DESIGN.md</c>. What could not be left as-is is the silence:
    /// REFACTOR_TASKS.md rule 5 — an inert feature that looks live is worse than an absent one.</para>
    ///
    /// <para>Two traps for whoever does wire it up:</para>
    /// <list type="bullet">
    /// <item><description><see cref="PoolSettings.GetAddress"/> returns
    /// <c>prefabReference.AssetGUID</c>, so a pool created from a <c>prefabReference</c> would be
    /// keyed by GUID while user code calls <c>Spawn("Enemies/Orc")</c> and misses every time.</description></item>
    /// <item><description><see cref="PoolSettings.maxSize"/> is documented here as "0 = unlimited",
    /// which matches <c>IPoolFactory.CreatePool</c> but is the exact opposite of
    /// <c>DynamicPoolConfig.MaxSize</c>, where 0 is now rejected outright as a hard cap of zero
    /// (P-19). Whichever pool-creation path a wiring pass chooses decides which convention applies.</description></item>
    /// </list>
    ///
    /// <para>The <c>[Obsolete]</c> on <see cref="PoolSettings.destroyOnFull"/> predates this note and
    /// was actively misleading on its own: annotating exactly one field as ignored asserts by
    /// omission that the other twelve are honoured. They are not.</para>
    /// </remarks>
    [CreateAssetMenu(fileName = "PoolConfig", menuName = "Addressable Manager/Pool Configuration", order = 2)]
    public class PoolConfiguration : ScriptableObject
    {
        [System.Serializable]
        public class PoolSettings
        {
            [Tooltip("Prefab to pool (must be a GameObject)")]
            public AssetReference prefabReference;

            [Tooltip("Manual address (alternative to AssetReference)")]
            public string address;

            [Header("Pool Settings  (NOT WIRED UP - see PoolConfiguration class docs)")]
            [Tooltip("INERT: nothing reads this. Number of instances that WOULD be preloaded on pool creation.")]
            [Range(0, 100)]
            public int preloadCount = 10;

            [Tooltip("INERT: nothing reads this. Maximum pool size (0 = unlimited) that WOULD be applied.")]
            [Range(0, 1000)]
            public int maxSize = 100;

            [Tooltip("INERT: nothing reads this, and nothing in this package runs at startup.")]
            public bool autoCreate = true;

            [Header("Advanced  (also inert)")]
            [Tooltip("INERT: nothing reads this. Parent transform that WOULD be used for pooled objects.")]
            public Transform poolRoot;

            // Kept for serialization compatibility with PoolConfig assets authored in 2.1.0
            // and earlier. The active pool implementation (UnityPoolAdapter -> UnityEngine.Pool.ObjectPool)
            // always destroys instances released above maxSize; toggling this flag has no effect.
            //
            // P-10: this used to be the ONLY field carrying an "is ignored" annotation, which read as
            // a promise that every other field was honoured. None of them are — the whole asset is
            // unconsumed. The message below says so rather than singling this one field out.
            [HideInInspector]
            [System.Obsolete("Ignored. Pools always destroy excess instances when full — and note that " +
                "NO field on PoolConfiguration currently reaches runtime behaviour at all; the whole " +
                "asset is unconsumed (see the PoolConfiguration class docs and " +
                "Documentation/LIFETIME_DESIGN.md, \"Pooling: decisions still open\").", false)]
            public bool destroyOnFull = false;

            [Tooltip("Optional label for debugging")]
            public string label;

            /// <summary>
            /// Get the address to use
            /// </summary>
            public string GetAddress()
            {
                if (prefabReference != null && prefabReference.RuntimeKeyIsValid())
                {
                    return prefabReference.AssetGUID;
                }
                return address;
            }

            /// <summary>
            /// Check if settings are valid
            /// </summary>
            public bool IsValid()
            {
                if (prefabReference != null && prefabReference.RuntimeKeyIsValid())
                    return true;

                return !string.IsNullOrEmpty(address);
            }
        }

        [Header("Pool Configurations  (WARNING: this whole asset is inert - see class docs)")]
        [Tooltip("INERT: no runtime code reads this list. Creating pools from it is not implemented.")]
        public List<PoolSettings> pools = new List<PoolSettings>();

        [Header("Global Pool Settings  (also inert)")]
        [Tooltip("INERT. The real default is AddressablePoolManager.DefaultMaxPoolSize (100), and " +
            "this field's 50 disagrees with it.")]
        public int defaultMaxSize = 50;

        [Tooltip("INERT. The real default preload count is 0 - pools are created empty.")]
        public int defaultPreloadCount = 5;

        [Tooltip("INERT. Nothing in this package runs at startup; there is no startup path to hook.")]
        public bool createAllOnStartup = true;

        [Tooltip("INERT. AddressablePoolManager does subscribe to SceneManager.sceneUnloaded, but " +
            "only to reclaim destroyed instances (P-8) - it never clears pools, and it never reads " +
            "this flag.")]
        public bool cleanupOnSceneUnload = true;

        /// <summary>
        /// Get all pools marked for auto-creation.
        /// </summary>
        /// <remarks>
        /// P-10: called by nothing. See the class remarks — this type is entirely unconsumed.
        /// </remarks>
        public List<PoolSettings> GetAutoCreatePools()
        {
            var result = new List<PoolSettings>();

            foreach (var pool in pools)
            {
                if (pool.autoCreate && pool.IsValid())
                {
                    result.Add(pool);
                }
            }

            return result;
        }

        /// <summary>
        /// Get pool settings by address.
        /// </summary>
        /// <remarks>
        /// P-10: called by nothing. Note also that <see cref="PoolSettings.GetAddress"/> returns an
        /// <c>AssetGUID</c> when a <c>prefabReference</c> is set, so this would not match an address
        /// string a caller passed to <c>Spawn</c>.
        /// </remarks>
        public PoolSettings GetPoolByAddress(string address)
        {
            foreach (var pool in pools)
            {
                if (pool.GetAddress() == address)
                {
                    return pool;
                }
            }

            return null;
        }

        /// <summary>
        /// Validate all pool settings
        /// </summary>
        public (bool success, List<string> errors) Validate()
        {
            var errors = new List<string>();

            for (int i = 0; i < pools.Count; i++)
            {
                var pool = pools[i];

                if (!pool.IsValid())
                {
                    errors.Add($"Pool {i}: No valid address or prefab reference set");
                }

                if (pool.maxSize > 0 && pool.preloadCount > pool.maxSize)
                {
                    errors.Add($"Pool {i}: Preload count ({pool.preloadCount}) exceeds max size ({pool.maxSize})");
                }

                // Check for duplicates
                for (int j = i + 1; j < pools.Count; j++)
                {
                    if (pool.GetAddress() == pools[j].GetAddress())
                    {
                        errors.Add($"Pool {i} and {j}: Duplicate address '{pool.GetAddress()}'");
                    }
                }
            }

            return (errors.Count == 0, errors);
        }

        #region Editor Helpers

#if UNITY_EDITOR
        private void OnValidate()
        {
            // P-10 holding action. This runs the moment a human edits the asset in the Inspector,
            // which is exactly the moment they form the belief that editing it does something. The
            // validation below is real and its messages read as though the values matter — that
            // combination (a config that validates but is never consumed) is the false-confidence
            // pattern REFACTOR_TASKS.md rule 5 names, so say the quiet part first.
            Debug.LogWarning("[PoolConfig] This asset is NOT wired into anything: no runtime code " +
                "reads PoolConfiguration, so none of these values affect pooling behaviour. It is " +
                "kept pending a decision on whether to wire it up or deprecate it — see " +
                "Documentation/LIFETIME_DESIGN.md, \"Pooling: decisions still open\". Create pools " +
                "with Assets.CreatePool / Standard.CreateDynamicPool / " +
                "AddressablePoolManager.CreatePoolAsync in the meantime.", this);

            var (success, errors) = Validate();
            if (!success && errors.Count > 0)
            {
                Debug.LogWarning($"[PoolConfig] Validation warnings (for a config nothing consumes):" +
                    $"\n{string.Join("\n", errors)}", this);
            }
        }
#endif

        #endregion
    }
}
