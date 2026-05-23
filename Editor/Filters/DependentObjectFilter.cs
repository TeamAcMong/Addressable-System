using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets that depend on (reference) specific objects
    /// Useful for finding all assets that use a particular material, texture, etc.
    /// </summary>
    [CreateAssetMenu(fileName = "DependentObjectFilter", menuName = "Addressable Manager/Filters/Dependent Object Filter")]
    public class DependentObjectFilter : AssetFilterBase
    {
        public enum DependencyMode
        {
            Direct,         // Only direct dependencies
            Recursive       // Include recursive dependencies
        }

        [Header("Dependent Object Filter Settings")]
        [Tooltip("Objects to check dependencies for")]
        [SerializeField] private List<UnityEngine.Object> _targetObjects = new List<UnityEngine.Object>();

        [Tooltip("Dependency mode")]
        [SerializeField] private DependencyMode _dependencyMode = DependencyMode.Direct;

        [Tooltip("Match if asset depends on ANY of the targets (OR logic)")]
        [SerializeField] private bool _matchAny = true;

        private HashSet<string> _targetPaths;
        private Dictionary<string, HashSet<string>> _dependencyCache = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Target objects
        /// </summary>
        public List<UnityEngine.Object> TargetObjects => _targetObjects;

        /// <summary>
        /// Dependency mode
        /// </summary>
        public DependencyMode Mode
        {
            get => _dependencyMode;
            set => _dependencyMode = value;
        }

        public override void Setup()
        {
            base.Setup();

            // Cache target object paths
            _targetPaths = new HashSet<string>();
            foreach (var obj in _targetObjects)
            {
                if (obj != null)
                {
                    string path = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path))
                    {
                        _targetPaths.Add(path);
                    }
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (_targetPaths == null || _targetPaths.Count == 0)
            {
                Setup();
                if (_targetPaths == null || _targetPaths.Count == 0)
                    return false;
            }

            // Get dependencies from cache
            if (!_dependencyCache.TryGetValue(assetPath, out HashSet<string> dependencies))
            {
                dependencies = new HashSet<string>();

                string[] deps;
                if (_dependencyMode == DependencyMode.Recursive)
                {
                    deps = AssetDatabase.GetDependencies(assetPath, true);
                }
                else
                {
                    deps = AssetDatabase.GetDependencies(assetPath, false);
                }

                foreach (var dep in deps)
                {
                    // Skip self-reference
                    if (dep != assetPath)
                    {
                        dependencies.Add(dep);
                    }
                }

                _dependencyCache[assetPath] = dependencies;
            }

            if (_matchAny)
            {
                // OR logic - match if depends on any target
                return _targetPaths.Any(target => dependencies.Contains(target));
            }
            else
            {
                // AND logic - match if depends on all targets
                return _targetPaths.All(target => dependencies.Contains(target));
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            int count = _targetObjects?.Count(o => o != null) ?? 0;
            string mode = _dependencyMode == DependencyMode.Recursive ? "Recursive" : "Direct";
            return $"Depends On ({mode}): {count} target(s)";
        }

        private void OnValidate()
        {
            _targetPaths = null;
            _dependencyCache.Clear();
        }
    }
}
