using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Filters
{
    /// <summary>
    /// Filter assets by their type (Sprite, Prefab, ScriptableObject, etc.)
    /// </summary>
    [CreateAssetMenu(fileName = "TypeFilter", menuName = "Addressable Manager/Filters/Type Filter")]
    public class TypeFilter : AssetFilterBase
    {
        [Header("Type Filter Settings")]
        [Tooltip("Asset type to match (e.g., Sprite, GameObject, AudioClip)")]
        [SerializeField] private string _typeName = "UnityEngine.GameObject";

        [Tooltip("Match derived types as well")]
        [SerializeField] private bool _includeSubclasses = true;

        private Type _cachedType;
        private readonly Dictionary<string, Type> _assetTypeCache = new Dictionary<string, Type>();

        /// <summary>
        /// Type name to filter
        /// </summary>
        public string TypeName
        {
            get => _typeName;
            set
            {
                _typeName = value;
                _cachedType = null; // Invalidate cache
            }
        }

        /// <summary>
        /// Include subclasses of the type
        /// </summary>
        public bool IncludeSubclasses
        {
            get => _includeSubclasses;
            set => _includeSubclasses = value;
        }

        public override void Setup()
        {
            base.Setup();

            // Cache the type on main thread
            if (!string.IsNullOrEmpty(_typeName))
            {
                _cachedType = Type.GetType(_typeName);
                if (_cachedType == null)
                {
                    // Try Unity types without full namespace
                    _cachedType = Type.GetType($"UnityEngine.{_typeName}, UnityEngine");
                }
                if (_cachedType == null)
                {
                    // Try Unity Editor types
                    _cachedType = Type.GetType($"UnityEditor.{_typeName}, UnityEditor");
                }
            }
        }

        protected override bool IsMatchInternal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(_typeName))
                return false;

            // Get asset type from cache
            if (!_assetTypeCache.TryGetValue(assetPath, out Type assetType))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (asset == null)
                    return false;

                assetType = asset.GetType();
                _assetTypeCache[assetPath] = assetType;
            }

            if (_cachedType == null)
                return false;

            if (_includeSubclasses)
            {
                return _cachedType.IsAssignableFrom(assetType);
            }
            else
            {
                return assetType == _cachedType;
            }
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            string subclassInfo = _includeSubclasses ? " (and subclasses)" : "";
            return $"Type: {_typeName}{subclassInfo}";
        }

        private void OnValidate()
        {
            // Invalidate cache when settings change
            _cachedType = null;
            _assetTypeCache.Clear();
        }
    }
}
