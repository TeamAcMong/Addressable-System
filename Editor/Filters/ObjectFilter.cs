using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter by specific object references
    /// Matches assets if they are in the list of target objects
    /// </summary>
    [CreateAssetMenu(fileName = "ObjectFilter", menuName = "Addressable Manager/Filters/Object Filter")]
    public class ObjectFilter : AssetFilterBase
    {
        [Header("Object Filter Settings")]
        [Tooltip("Specific objects to match")]
        [SerializeField] private List<UnityEngine.Object> _targetObjects = new List<UnityEngine.Object>();

        private HashSet<string> _targetPaths;

        /// <summary>
        /// Target objects to match
        /// </summary>
        public List<UnityEngine.Object> TargetObjects => _targetObjects;

        public override void Setup()
        {
            base.Setup();

            // Cache asset paths
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

            if (_targetPaths == null)
            {
                Setup();
            }

            return _targetPaths != null && _targetPaths.Contains(assetPath);
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            int count = _targetObjects?.Count(o => o != null) ?? 0;
            return $"Object: {count} target(s)";
        }

        private void OnValidate()
        {
            // Invalidate cache when objects change
            _targetPaths = null;
        }
    }
}
