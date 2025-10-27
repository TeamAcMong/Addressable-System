using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Configs;
using System.Linq;

namespace AddressableManager.Editor.Inspectors
{
    [CustomEditor(typeof(AddressablePreloadConfig))]
    public class AddressablePreloadConfigInspector : UnityEditor.Editor
    {
        private AddressablePreloadConfig _config;

        public override VisualElement CreateInspectorGUI()
        {
            _config = target as AddressablePreloadConfig;

            var root = new VisualElement();
            root.style.paddingTop = 5;

            // Header
            CreateHeader(root);

            // Default inspector
            var defaultInspector = new IMGUIContainer(() =>
            {
                DrawDefaultInspector();
            });
            root.Add(defaultInspector);

            // Actions section
            CreateActionsSection(root);

            // Status section
            CreateStatusSection(root);

            return root;
        }

        private void CreateHeader(VisualElement root)
        {
            var header = new VisualElement();
            header.style.backgroundColor = new Color(0.2f, 0.5f, 0.8f);
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.marginBottom = 10;

            var title = new Label("Addressable Preload Configuration");
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;

            var subtitle = new Label("Configure assets to preload automatically on startup");
            subtitle.style.fontSize = 11;
            subtitle.style.color = new Color(0.9f, 0.9f, 0.9f);
            subtitle.style.marginTop = 3;

            header.Add(title);
            header.Add(subtitle);
            root.Add(header);
        }

        private void CreateActionsSection(VisualElement root)
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginTop = 10;
            section.style.marginBottom = 10;
            section.style.borderTopLeftRadius = 4;
            section.style.borderTopRightRadius = 4;
            section.style.borderBottomLeftRadius = 4;
            section.style.borderBottomRightRadius = 4;

            var title = new Label("Actions");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            // Validate button
            var validateBtn = new Button(() => ValidateConfig());
            validateBtn.text = "Validate All Addresses";
            validateBtn.style.marginBottom = 5;
            section.Add(validateBtn);

            // Sort by priority button
            var sortBtn = new Button(() => SortByPriority());
            sortBtn.text = "Sort by Priority";
            sortBtn.style.marginBottom = 5;
            section.Add(sortBtn);

            // Load in editor button
            var loadBtn = new Button(() => LoadInEditor());
            loadBtn.text = "Test Load in Editor";
            loadBtn.tooltip = "Load all startup assets in edit mode (for testing)";
            section.Add(loadBtn);

            root.Add(section);
        }

        private void CreateStatusSection(VisualElement root)
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.borderTopLeftRadius = 4;
            section.style.borderTopRightRadius = 4;
            section.style.borderBottomLeftRadius = 4;
            section.style.borderBottomRightRadius = 4;

            var title = new Label("Statistics");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            // Stats
            var totalCount = _config.preloadAssets.Count;
            var startupCount = _config.preloadAssets.Count(e => e.loadOnStartup);
            var validCount = _config.preloadAssets.Count(e => e.IsValid());

            var statsContainer = new VisualElement();
            statsContainer.style.paddingLeft = 10;

            AddStatLabel(statsContainer, "Total Entries", totalCount.ToString());
            AddStatLabel(statsContainer, "Startup Assets", startupCount.ToString());
            AddStatLabel(statsContainer, "Valid Entries", validCount.ToString());
            AddStatLabel(statsContainer, "Invalid Entries", (totalCount - validCount).ToString(),
                totalCount - validCount > 0 ? new Color(1f, 0.3f, 0.3f) : new Color(0.7f, 0.7f, 0.7f));

            section.Add(statsContainer);
            root.Add(section);
        }

        private void AddStatLabel(VisualElement container, string label, string value, Color? valueColor = null)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom = 3;

            var labelElement = new Label(label);
            labelElement.style.fontSize = 10;
            labelElement.style.color = new Color(0.8f, 0.8f, 0.8f);

            var valueElement = new Label(value);
            valueElement.style.fontSize = 10;
            valueElement.style.unityFontStyleAndWeight = FontStyle.Bold;
            valueElement.style.color = valueColor ?? new Color(0.3f, 0.8f, 1f);

            row.Add(labelElement);
            row.Add(valueElement);
            container.Add(row);
        }

        private void ValidateConfig()
        {
            var (success, errors) = _config.Validate();

            if (success)
            {
                EditorUtility.DisplayDialog("Validation Success",
                    "All entries are valid!",
                    "OK");
            }
            else
            {
                var message = "Validation failed with the following errors:\n\n" +
                             string.Join("\n", errors);

                EditorUtility.DisplayDialog("Validation Failed", message, "OK");
                Debug.LogError($"[PreloadConfig] Validation failed:\n{message}", _config);
            }
        }

        private void SortByPriority()
        {
            if (EditorUtility.DisplayDialog("Sort by Priority",
                "This will reorder the list by priority values. Continue?",
                "Yes", "Cancel"))
            {
                Undo.RecordObject(_config, "Sort Preload Config by Priority");

                _config.preloadAssets.Sort((a, b) => a.priority.CompareTo(b.priority));

                EditorUtility.SetDirty(_config);
            }
        }

        private void LoadInEditor()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("Editor Mode Only",
                    "Test loading only works in Play Mode.\n" +
                    "Enter Play Mode and try again.",
                    "OK");
                return;
            }

            var startupAssets = _config.GetStartupAssets();

            EditorUtility.DisplayDialog("Test Load",
                $"Would load {startupAssets.Count} startup assets:\n\n" +
                string.Join("\n", startupAssets.Take(10).Select(e =>
                    $"• {e.label ?? e.GetAddress()} ({e.scope})")),
                "OK");
        }
    }
}
