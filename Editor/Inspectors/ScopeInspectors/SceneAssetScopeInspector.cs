using UnityEditor;
using UnityEngine;
using AddressableManager.Scopes;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(SceneAssetScope))]
    public class SceneAssetScopeInspector : BaseScopeInspector
    {
        protected override string GetScopeName() => "Scene";
        protected override Color GetScopeColor() => new Color(1f, 0.8f, 0.2f); // Yellow
    }
}
