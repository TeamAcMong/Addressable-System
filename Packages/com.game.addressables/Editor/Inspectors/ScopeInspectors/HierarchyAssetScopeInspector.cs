using UnityEditor;
using UnityEngine;
using AddressableManager.Scopes;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(HierarchyAssetScope))]
    public class HierarchyAssetScopeInspector : BaseScopeInspector
    {
        protected override string GetScopeName() => "Hierarchy";
        protected override Color GetScopeColor() => new Color(1f, 0.3f, 0.3f); // Red
    }
}
