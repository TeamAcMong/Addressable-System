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
                        // Match and substitution must use the SAME semantics. The guard above is a
                        // literal substring test, so the replacement has to be literal too. It used to
                        // hand `find` to Regex.Replace as a PATTERN and `replace` as a substitution
                        // TEMPLATE, which disagreed with the guard in both directions:
                        //   FindAndReplace("[UI]", "ui")        -> "[UI]" is a character class, so it
                        //                                          rewrote every U and I in every
                        //                                          address that merely contained "[UI]"
                        //   FindAndReplace("Icon(Small)", "..") -> "(Small)" is a capture group, so the
                        //                                          pattern never matched and nothing
                        //                                          was replaced
                        // and a `replace` containing $1 or $& injected captured text instead of literal
                        // characters. Regex.Escape on the needle and Match.Result-free replacement fix
                        // both; the case-insensitive path keeps using Regex only because
                        // string.Replace(string, string, StringComparison) is not available on the
                        // C# version this package targets.
                        string newAddress = caseSensitive
                            ? entry.address.Replace(find, replace)
                            : System.Text.RegularExpressions.Regex.Replace(
                                entry.address,
                                System.Text.RegularExpressions.Regex.Escape(find),
                                replace.Replace("$", "$$"),
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        // Count what actually changed, not what was attempted. SetAddress no-ops when
                        // the value is unchanged, so an unconditional count++ reported successful
                        // renames for entries it had not touched.
                        if (string.IsNullOrEmpty(newAddress) || newAddress == entry.address) continue;

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
            int skipped = 0;
            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    if (entry.address.StartsWith(prefix))
                    {
                        // An address equal to the prefix would shorten to "". SetAddress does not
                        // reject that - it assigns the empty string and then substitutes AssetPath,
                        // so the entry silently ends up keyed by "Assets/UI/Panel.prefab" instead of
                        // anything the caller asked for, every runtime load on the old key throws
                        // InvalidKeyException, and the log still counts it as a successful shorten.
                        // Same guard the sibling FindAndReplace carries.
                        string newAddress = entry.address.Substring(prefix.Length);
                        if (string.IsNullOrEmpty(newAddress) || newAddress == entry.address)
                        {
                            skipped++;
                            continue;
                        }

                        entry.SetAddress(newAddress, false);
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

            if (skipped > 0)
            {
                Debug.LogWarning(
                    $"[BatchAddressUpdater] Skipped {skipped} entr(ies) whose address is exactly '{prefix}' - " +
                    "removing the prefix would have left an empty address, which Addressables silently " +
                    "replaces with the asset path.");
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

            // Collision check BEFORE writing anything. SetAddress performs no uniqueness check of its
            // own, and no conflict detector runs on any write path, so "UI/Icon" and "ui/icon" both
            // collapsing to "ui/icon" produced two entries sharing one key: a single-asset load
            // resolves to one of them and the other asset becomes permanently unreachable, while the
            // log reported "Converted 2 address(es)". A batch that would collide is refused whole
            // rather than half-applied.
            //
            // ToLowerInvariant, not ToLower: on a tr-TR editor 'I' lowercases to 'ı' (U+0131), which no
            // ordinal runtime key comparison will ever match, and the result differs per machine.
            var planned = new Dictionary<AddressableAssetEntry, string>();
            var claimed = new Dictionary<string, string>(System.StringComparer.Ordinal);

            foreach (var group in groupsToProcess)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    string lower = entry.address.ToLowerInvariant();
                    if (entry.address != lower) planned[entry] = lower;
                }
            }

            // Every address that will exist afterwards: the rewritten ones plus the untouched ones.
            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                foreach (var entry in group.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.address)) continue;

                    string final = planned.TryGetValue(entry, out var rewritten) ? rewritten : entry.address;
                    if (claimed.TryGetValue(final, out var firstOwner))
                    {
                        Debug.LogError(
                            $"[BatchAddressUpdater] Aborted: lowercasing would give '{entry.address}' and " +
                            $"'{firstOwner}' the same address '{final}'. Two entries sharing one address " +
                            "makes one of the assets unreachable at runtime, and nothing downstream detects " +
                            "it. No address was changed.");
                        return 0;
                    }

                    claimed[final] = entry.address;
                }
            }

            foreach (var kvp in planned)
            {
                kvp.Key.SetAddress(kvp.Value, false);
                count++;
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
