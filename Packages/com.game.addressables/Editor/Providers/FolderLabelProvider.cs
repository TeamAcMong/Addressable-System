using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides labels based on folder names in the asset path
    /// </summary>
    [CreateAssetMenu(fileName = "FolderLabelProvider", menuName = "Addressable Manager/Providers/Label/Folder Name")]
    public class FolderLabelProvider : LabelProviderBase
    {
        [Header("Folder Label Settings")]
        [Tooltip("Use parent folder name as label")]
        [SerializeField] private bool _useParentFolder = true;

        [Tooltip("Use all folder names in path as separate labels")]
        [SerializeField] private bool _useAllFolders = false;

        [Tooltip("Skip 'Assets' folder in labels")]
        [SerializeField] private bool _skipAssetsFolder = true;

        [Tooltip("Convert labels to lowercase")]
        [SerializeField] private bool _toLowerCase = false;

        [Tooltip("Optional prefix for labels")]
        [SerializeField] private string _prefix = "";

        public override List<string> Provide(string assetPath)
        {
            var labels = new List<string>();

            if (string.IsNullOrEmpty(assetPath))
                return labels;

            string directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory))
                return labels;

            // Normalize path separators
            directory = directory.Replace('\\', '/');

            string[] folders = directory.Split('/');

            if (_useAllFolders)
            {
                // Add all folders as labels
                foreach (var folder in folders)
                {
                    if (string.IsNullOrEmpty(folder))
                        continue;

                    if (_skipAssetsFolder && folder.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    string label = folder;
                    if (_toLowerCase)
                        label = label.ToLower();

                    if (!string.IsNullOrEmpty(_prefix))
                        label = _prefix + label;

                    labels.Add(label);
                }
            }
            else if (_useParentFolder && folders.Length > 0)
            {
                // Use only the immediate parent folder
                string parentFolder = folders[folders.Length - 1];
                if (!string.IsNullOrEmpty(parentFolder))
                {
                    string label = parentFolder;
                    if (_toLowerCase)
                        label = label.ToLower();

                    if (!string.IsNullOrEmpty(_prefix))
                        label = _prefix + label;

                    labels.Add(label);
                }
            }

            return labels;
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return _useAllFolders ? "Label: All Folders" : "Label: Parent Folder";
        }
    }
}
