using UnityEditor;
using UnityEngine;
using AddressableManager.Scopes;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(SessionAssetScope))]
    public class SessionAssetScopeInspector : BaseScopeInspector
    {
        protected override string GetScopeName() => "Session";
        protected override Color GetScopeColor() => new Color(0.3f, 0.6f, 1f); // Blue
    }
}
