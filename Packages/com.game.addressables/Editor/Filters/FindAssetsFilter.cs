using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets using AssetDatabase.FindAssets query
    /// Powerful filter that can search by name, label, type, etc.
    /// </summary>
    [CreateAssetMenu(fileName = "FindAssetsFilter", menuName = "Addressable Manager/Filters/Find Assets Filter")]
    public class FindAssetsFilter : AssetFilterBase
    {
        [Header("Find Assets Filter Settings")]
        [Tooltip("Search filter string (e.g., 't:Prefab', 't:AudioClip l:Music', 'Enemy')")]
        [SerializeField] [TextArea(1, 3)] private string _searchFilter = "t:Prefab";

        [Tooltip("Search in specific folders (leave empty for all folders)")]
        [SerializeField] private List<string> _searchFolders = new List<string>();

        private HashSet<string> _matchedAssetPaths;

        /// <summary>
        /// Search filter
        /// </summary>
        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                _searchFilter = value;
                _matchedAssetPaths = null; // Invalidate cache
            }
        }

        /// <summary>
        /// Search folders
        /// </summary>
        public List<string> SearchFolders => _searchFolders;

        public override void Setup()
        {
            base.Setup();

            // Execute the search query and cache results
            _matchedAssetPaths = new HashSet<string>();

            string[] guids;
            if (_searchFolders != null && _searchFolders.Count > 0)
            {
                // Search in specific folders
                guids = AssetDatabase.FindAssets(_searchFilter, _searchFolders.ToArray());
            }
            else
            {
                // Search in all folders
                guids = AssetDatabase.FindAssets(_searchFilter);
            }

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                {
                    _matchedAssetPaths.Add(path);
                }
            }

            Debug.Log($"[FindAssetsFilter] Found {_matchedAssetPaths.Count} assets matching '{_searchFilter}'");
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (_matchedAssetPaths == null)
            {
                Setup();
            }

            return _matchedAssetPaths != null && _matchedAssetPaths.Contains(assetPath);
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            string folderInfo = (_searchFolders != null && _searchFolders.Count > 0)
                ? $" in {_searchFolders.Count} folder(s)"
                : "";

            return $"Find: '{_searchFilter}'{folderInfo}";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _matchedAssetPaths = null;
        }
    }
}
