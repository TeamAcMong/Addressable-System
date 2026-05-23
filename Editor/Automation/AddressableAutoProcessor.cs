using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using AddressableManager.Editor.Rules;

namespace AddressableManager.Editor.Automation
{
    /// <summary>
    /// Automatically applies layout rules when assets are imported, moved, or modified
    /// </summary>
    public class AddressableAutoProcessor : AssetPostprocessor
    {
        private static bool _isProcessing = false;
        private static HashSet<string> _pendingAssets = new HashSet<string>();
        private static double _lastProcessTime;
        private const double BATCH_DELAY = 0.5; // Batch changes within 500ms

        /// <summary>
        /// Called when any assets are imported
        /// </summary>
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (_isProcessing) return;

            // Collect all affected assets
            var affectedAssets = new HashSet<string>();
            affectedAssets.UnionWith(importedAssets);
            affectedAssets.UnionWith(movedAssets);

            if (affectedAssets.Count == 0) return;

            // Add to pending queue
            _pendingAssets.UnionWith(affectedAssets);

            // Schedule batch processing
            _lastProcessTime = EditorApplication.timeSinceStartup;
            EditorApplication.delayCall -= ProcessPendingAssets;
            EditorApplication.delayCall += ProcessPendingAssets;
        }

        private static void ProcessPendingAssets()
        {
            // Wait for batch delay
            if (EditorApplication.timeSinceStartup - _lastProcessTime < BATCH_DELAY)
            {
                EditorApplication.delayCall -= ProcessPendingAssets;
                EditorApplication.delayCall += ProcessPendingAssets;
                return;
            }

            if (_pendingAssets.Count == 0) return;

            // Find all enabled LayoutRuleData assets
            var ruleDataAssets = FindAllLayoutRuleData();
            var enabledRuleData = ruleDataAssets.Where(rd => rd.AutoApplyOnImport).ToList();

            if (enabledRuleData.Count == 0)
            {
                _pendingAssets.Clear();
                return;
            }

            _isProcessing = true;

            try
            {
                var assetsToProcess = _pendingAssets.ToList();
                _pendingAssets.Clear();

                Debug.Log($"[AddressableAutoProcessor] Processing {assetsToProcess.Count} asset(s) with {enabledRuleData.Count} rule set(s)");

                foreach (var ruleData in enabledRuleData)
                {
                    if (!ruleData.AutoApplyOnImport) continue;

                    var processor = new LayoutRuleProcessor(ruleData);
                    var result = processor.ApplyRulesToAssets(assetsToProcess);

                    if (ruleData.VerboseLogging)
                    {
                        Debug.Log($"[AddressableAutoProcessor] {ruleData.name}: " +
                            $"{result.AddressesApplied} addresses, {result.LabelsApplied} labels applied");
                    }

                    if (result.Errors.Count > 0)
                    {
                        foreach (var error in result.Errors)
                        {
                            Debug.LogError($"[AddressableAutoProcessor] {ruleData.name}: {error}");
                        }
                    }
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private static List<LayoutRuleData> FindAllLayoutRuleData()
        {
            var results = new List<LayoutRuleData>();
            var guids = AssetDatabase.FindAssets("t:LayoutRuleData");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<LayoutRuleData>(path);
                if (asset != null)
                {
                    results.Add(asset);
                }
            }

            return results;
        }

        /// <summary>
        /// Force process all assets immediately (for testing)
        /// </summary>
        [MenuItem("Tools/Addressable Manager/Force Process All Assets")]
        public static void ForceProcessAllAssets()
        {
            var allAssets = AssetDatabase.FindAssets("")
                .Select(guid => AssetDatabase.GUIDToAssetPath(guid))
                .Where(path => !string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                .ToList();

            Debug.Log($"[AddressableAutoProcessor] Force processing {allAssets.Count} assets");

            var ruleDataAssets = FindAllLayoutRuleData();

            foreach (var ruleData in ruleDataAssets)
            {
                var processor = new LayoutRuleProcessor(ruleData);
                var result = processor.ApplyRulesToAssets(allAssets, (progress, message) =>
                {
                    EditorUtility.DisplayProgressBar("Force Processing", message, progress);
                });

                EditorUtility.ClearProgressBar();

                Debug.Log($"[AddressableAutoProcessor] {ruleData.name}: Complete - " +
                    $"{result.AddressesApplied} addresses, {result.LabelsApplied} labels applied");
            }
        }
    }
}
