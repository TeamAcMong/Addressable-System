using System.IO;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Provides address based on the file name (with or without extension)
    /// </summary>
    [CreateAssetMenu(fileName = "FileNameAddressProvider", menuName = "Addressable Manager/Providers/Address/File Name")]
    public class FileNameAddressProvider : AddressProviderBase
    {
        [Header("File Name Settings")]
        [Tooltip("Include file extension in address")]
        [SerializeField] private bool _includeExtension = false;

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

            string fileName = _includeExtension
                ? Path.GetFileName(assetPath)
                : Path.GetFileNameWithoutExtension(assetPath);

            if (_toLowerCase)
                fileName = fileName.ToLower();

            return $"{_prefix}{fileName}{_suffix}";
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return "Address: File Name" + (_includeExtension ? " (with ext)" : "");
        }
    }
}
