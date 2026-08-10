using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Tools
{
    /// <summary>
    /// Repairs Addressable groups that have an empty schema set. A group in that state builds
    /// silently as if it were empty no matter how many entries it holds - see
    /// AddressRule.GetOrCreateTargetGroup / AddressableGroupSchemaUtility for the create-time fix;
    /// this menu command is the corresponding repair path for groups that already shipped broken
    /// (e.g. Assets/AddressableAssetsData/AssetGroups/Scene.asset, which currently holds two scene
    /// entries under an empty m_SchemaSet).
    /// </summary>
    /// <remarks>
    /// Must be run from the Editor: it walks the live AddressableAssetSettings and creates schema
    /// sub-assets through the Addressables API (AddressableAssetGroup.AddSchema), which only
    /// exists in the Editor assembly. There is no headless/CLI equivalent - open the project in
    /// the Unity Editor and run Tools &gt; Addressable Manager &gt; Repair Groups Missing Schemas.
    /// </remarks>
    public static class AddressableGroupSchemaRepair
    {
        [MenuItem("Tools/Addressable Manager/Repair Groups Missing Schemas")]
        public static void RepairGroupsMissingSchemas()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorUtility.DisplayDialog("Repair Groups Missing Schemas",
                    "No AddressableAssetSettings found in this project.", "OK");
                return;
            }

            var repaired = new List<string>();

            try
            {
                foreach (var group in settings.groups)
                {
                    if (group == null)
                        continue;

                    if (group.Schemas != null && group.Schemas.Count > 0)
                        continue;

                    AddressableGroupSchemaUtility.EnsureSchemas(group, settings);

                    if (group.Schemas != null && group.Schemas.Count > 0)
                    {
                        repaired.Add(group.Name);
                        EditorUtility.SetDirty(group);
                    }
                }

                if (repaired.Count > 0)
                {
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();

                    var message = new StringBuilder();
                    message.AppendLine($"Repaired {repaired.Count} group(s) with an empty schema set:");
                    foreach (var name in repaired)
                        message.AppendLine($"- {name}");

                    Debug.Log($"[AddressableGroupSchemaRepair] {message}");
                    EditorUtility.DisplayDialog("Repair Groups Missing Schemas", message.ToString(), "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Repair Groups Missing Schemas",
                        "No groups with an empty schema set were found. Nothing to repair.", "OK");
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Repair Groups Missing Schemas",
                    $"Repair failed:\n{ex.Message}", "OK");
                Debug.LogException(ex);
            }
        }

        [MenuItem("Tools/Addressable Manager/Repair Groups Missing Schemas", validate = true)]
        private static bool ValidateRepairGroupsMissingSchemas()
        {
            return AddressableAssetSettingsDefaultObject.Settings != null;
        }
    }
}
