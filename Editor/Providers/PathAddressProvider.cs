using System.IO;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides address based on asset path with various transformation options
    /// </summary>
    [CreateAssetMenu(fileName = "PathAddressProvider", menuName = "Addressable Manager/Providers/Address/Path")]
    public class PathAddressProvider : AddressProviderBase
    {
        [Header("Path Settings")]
        [Tooltip("Remove 'Assets/' prefix from path")]
        [SerializeField] private bool _removeAssetsPrefix = true;

        [Tooltip("Remove file extension")]
        [SerializeField] private bool _removeExtension = true;

        [Tooltip("Root folder to remove from path (e.g., 'Assets/Content/')")]
        [SerializeField] private string _removeRootFolder = "";

        [Tooltip("Replace path separator (/) with custom character")]
        [SerializeField] private string _pathSeparatorReplacement = "/";

        [Tooltip("Convert to lowercase")]
        [SerializeField] private bool _toLowerCase = false;

        [Tooltip("Optional prefix to add")]
        [SerializeField] private string _prefix = "";

        [Tooltip("Optional suffix to add")]
        [SerializeField] private string _suffix = "";

        public override string Provide(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            string address = assetPath;

            // Remove extension
            if (_removeExtension)
            {
                address = Path.ChangeExtension(address, null);
                // Remove trailing dot if present
                if (address.EndsWith("."))
                    address = address.Substring(0, address.Length - 1);
            }

            // Remove Assets/ prefix
            if (_removeAssetsPrefix && address.StartsWith("Assets/"))
            {
                address = address.Substring("Assets/".Length);
            }

            // Remove root folder
            if (!string.IsNullOrEmpty(_removeRootFolder))
            {
                if (address.StartsWith(_removeRootFolder))
                {
                    address = address.Substring(_removeRootFolder.Length);
                }
                // Ensure no leading slash
                address = address.TrimStart('/');
            }

            // Replace path separator
            if (!string.IsNullOrEmpty(_pathSeparatorReplacement) && _pathSeparatorReplacement != "/")
            {
                address = address.Replace("/", _pathSeparatorReplacement);
            }

            // Convert to lowercase
            if (_toLowerCase)
            {
                address = address.ToLower();
            }

            return $"{_prefix}{address}{_suffix}";
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return "Address: Path";
        }
    }
}
