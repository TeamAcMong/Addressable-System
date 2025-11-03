using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Editor.Automation
{
    /// <summary>
    /// Batch operations for updating addressable addresses
    /// </summary>
    public static class BatchAddressUpdater
    {
        /// <summary>
        /// Find and replace in addresses
        /// </summary>
        public static int FindAndReplace(string find, string replace, bool caseSensitive = false)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[BatchAddressUpdater] Addressable settings not found");
                return 0;
            }

            int count = 0;
            var comparison = caseSensitive ? System.StringComparison.Ordinal : System.StringComparison.OrdinalIgnoreCase;

            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    if (entry.address.IndexOf(find, comparison) >= 0)
                    {
                        string newAddress = caseSensitive
                            ? entry.address.Replace(find, replace)
                            : System.Text.RegularExpressions.Regex.Replace(entry.address, find, replace,
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        entry.SetAddress(newAddress, false);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BatchAddressUpdater] Updated {count} address(es)");
            }

            return count;
        }

        /// <summary>
        /// Add prefix to all addresses in a group
        /// </summary>
        public static int AddPrefix(string groupName, string prefix)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return 0;

            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                Debug.LogError($"[BatchAddressUpdater] Group not found: {groupName}");
                return 0;
            }

            int count = 0;
            foreach (var entry in group.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                if (!entry.address.StartsWith(prefix))
                {
                    entry.SetAddress(prefix + entry.address, false);
                    count++;
                }
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BatchAddressUpdater] Added prefix to {count} address(es)");
            }

            return count;
        }

        /// <summary>
        /// Remove prefix from addresses
        /// </summary>
        public static int RemovePrefix(string prefix)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return 0;

            int count = 0;
            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    if (entry.address.StartsWith(prefix))
                    {
                        entry.SetAddress(entry.address.Substring(prefix.Length), false);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BatchAddressUpdater] Removed prefix from {count} address(es)");
            }

            return count;
        }

        /// <summary>
        /// Convert all addresses to lowercase
        /// </summary>
        public static int ConvertToLowercase(string groupName = null)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return 0;

            int count = 0;
            var groupsToProcess = string.IsNullOrEmpty(groupName)
                ? settings.groups
                : new List<AddressableAssetGroup> { settings.FindGroup(groupName) };

            foreach (var group in groupsToProcess)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    string lower = entry.address.ToLower();
                    if (entry.address != lower)
                    {
                        entry.SetAddress(lower, false);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BatchAddressUpdater] Converted {count} address(es) to lowercase");
            }

            return count;
        }

        /// <summary>
        /// Menu item for batch operations window
        /// </summary>
        [MenuItem("Tools/Addressable Manager/Batch Address Updater")]
        private static void ShowBatchUpdaterWindow()
        {
            EditorUtility.DisplayDialog("Batch Address Updater",
                "Use BatchAddressUpdater class methods from code:\n\n" +
                "• FindAndReplace(find, replace)\n" +
                "• AddPrefix(groupName, prefix)\n" +
                "• RemovePrefix(prefix)\n" +
                "• ConvertToLowercase(groupName)\n\n" +
                "Full GUI window coming in future update!",
                "OK");
        }
    }
}
