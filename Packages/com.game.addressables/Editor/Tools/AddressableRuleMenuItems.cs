using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;
using AddressableManager.Editor.Windows;

namespace AddressableManager.Editor.Tools
{
    /// <summary>
    /// Menu items for addressable rule system
    /// </summary>
    public static class AddressableRuleMenuItems
    {
        [MenuItem("Window/Addressable Manager/Layout Rule Editor", priority = 201)]
        public static void OpenLayoutRuleEditor()
        {
            LayoutRuleEditorWindow.ShowWindow();
        }

        [MenuItem("Window/Addressable Manager/Layout Viewer", priority = 202)]
        public static void OpenLayoutViewer()
        {
            LayoutViewerWindow.ShowWindow();
        }

        [MenuItem("Assets/Addressables/Apply Layout Rules", priority = 50)]
        private static void ApplyLayoutRulesToSelection()
        {
            var selectedPaths = GetSelectedAssetPaths();
            if (selectedPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("No Selection", "Please select one or more assets to apply rules to.", "OK");
                return;
            }

            // Find all LayoutRuleData assets
            var ruleDataGuids = AssetDatabase.FindAssets("t:LayoutRuleData");
            if (ruleDataGuids.Length == 0)
            {
                EditorUtility.DisplayDialog("No Rules Found", "No LayoutRuleData assets found in project. Create one first.", "OK");
                return;
            }

            // If only one, use it directly
            LayoutRuleData ruleData;
            if (ruleDataGuids.Length == 1)
            {
                var path = AssetDatabase.GUIDToAssetPath(ruleDataGuids[0]);
                ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(path);
            }
            else
            {
                // Let user choose which rule data to use
                var menu = new GenericMenu();
                foreach (var guid in ruleDataGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(path);
                    menu.AddItem(new GUIContent(asset.name), false, () =>
                    {
                        ApplyRulesToAssets(asset, selectedPaths);
                    });
                }
                menu.ShowAsContext();
                return;
            }

            ApplyRulesToAssets(ruleData, selectedPaths);
        }

        [MenuItem("Assets/Addressables/Apply Layout Rules", validate = true)]
        private static bool ValidateApplyLayoutRules()
        {
            return Selection.objects.Length > 0;
        }

        [MenuItem("Assets/Create/Addressable Manager/Layout Rule Data", priority = 50)]
        private static void CreateLayoutRuleData()
        {
            var asset = ScriptableObject.CreateInstance<LayoutRuleData>();
            ProjectWindowUtil.CreateAsset(asset, "NewLayoutRuleData.asset");
        }

        private static System.Collections.Generic.List<string> GetSelectedAssetPaths()
        {
            var paths = new System.Collections.Generic.List<string>();
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        private static void ApplyRulesToAssets(LayoutRuleData ruleData, System.Collections.Generic.List<string> assetPaths)
        {
            if (ruleData == null || assetPaths == null || assetPaths.Count == 0)
                return;

            try
            {
                var processor = new LayoutRuleProcessor(ruleData);

                EditorUtility.DisplayProgressBar("Applying Rules", $"Processing {assetPaths.Count} asset(s)...", 0);

                var result = processor.ApplyRulesToAssets(assetPaths, (progress, message) =>
                {
                    EditorUtility.DisplayProgressBar("Applying Rules", message, progress);
                });

                EditorUtility.ClearProgressBar();

                string resultMessage = $"Applied rules to {assetPaths.Count} asset(s):\n\n" +
                    $"Addresses applied: {result.AddressesApplied}\n" +
                    $"Labels applied: {result.LabelsApplied}\n" +
                    $"Versions applied: {result.VersionsApplied}";

                if (result.Errors.Count > 0)
                {
                    resultMessage += $"\n\nErrors: {result.Errors.Count} (see console)";
                    foreach (var error in result.Errors)
                    {
                        Debug.LogError($"[ApplyRules] {error}");
                    }
                }

                EditorUtility.DisplayDialog("Apply Rules Complete", resultMessage, "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Error", $"Failed to apply rules:\n{ex.Message}", "OK");
                Debug.LogException(ex);
            }
        }
    }
}
