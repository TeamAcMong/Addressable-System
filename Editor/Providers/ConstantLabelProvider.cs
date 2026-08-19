using System.Collections.Generic;
using UnityEngine;

namespace AddressableManager.Editor.Providers
{
    /// <summary>
    /// Emits a fixed set of labels for every asset a rule matches.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="ConstantVersionProvider"/>, and the provider almost every worked
    /// example in the automation guide is built on: any rule that stamps a static label
    /// ("platform_pc", "content_dlc", "lang_en", "quality_high") needs this, because
    /// <c>LabelRule</c> has no inline label list - a provider is its only label source.
    ///
    /// It was documented in ADDRESSABLE_AUTOMATION_GUIDE.md and used throughout
    /// RULE_SYSTEM_EXAMPLES.md, listed as a drag-and-drop option in EDITOR_TOOLS_GUIDE.md, and
    /// referenced by the shipped rule templates - but never existed. Following any of those examples
    /// produced a LabelRule with a null provider, which fails validation, which aborts the entire
    /// rule run before a single asset is processed.
    ///
    /// Empty and whitespace entries are dropped rather than written: Addressables accepts an empty
    /// label string and it then shows up as a blank row in the label list that nothing can select.
    /// Duplicates are collapsed for the same reason - <c>SetLabel</c> would no-op on the second one
    /// anyway, but the count the processor reports should reflect what was actually applied.
    /// </remarks>
    [CreateAssetMenu(
        fileName = "ConstantLabelProvider",
        menuName = "Addressable Manager/Providers/Label/Constant")]
    public class ConstantLabelProvider : LabelProviderBase
    {
        [Header("Label Settings")]
        [Tooltip("Labels applied to every asset this rule matches, e.g. platform_pc, quality_high")]
        [SerializeField] private List<string> _labels = new List<string>();

        /// <summary>
        /// The labels this provider emits. Empty/whitespace entries and duplicates are ignored at
        /// <see cref="Provide"/> time rather than silently written through.
        /// </summary>
        public List<string> Labels => _labels;

        public override List<string> Provide(string assetPath)
        {
            var result = new List<string>(_labels.Count);
            if (_labels.Count == 0) return result;

            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string label in _labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;

                string trimmed = label.Trim();
                if (seen.Add(trimmed)) result.Add(trimmed);
            }

            return result;
        }

        public override string GetDisplayName()
        {
            if (!string.IsNullOrEmpty(_description))
                return _description;

            return _labels.Count == 0
                ? "Constant (no labels set)"
                : $"Constant ({string.Join(", ", _labels)})";
        }
    }
}
