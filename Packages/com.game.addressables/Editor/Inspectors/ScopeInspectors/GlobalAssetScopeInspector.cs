using UnityEditor;
using UnityEngine;
using AddressableManager.Scopes;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(GlobalAssetScope))]
    public class GlobalAssetScopeInspector : BaseScopeInspector
    {
        protected override string GetScopeName() => "Global";
        protected override Color GetScopeColor() => new Color(0.2f, 0.8f, 0.3f); // Green
    }
}
