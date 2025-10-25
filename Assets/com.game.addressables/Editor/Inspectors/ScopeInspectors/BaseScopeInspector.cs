using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Scopes;
using AddressableManager.Editor.Data;
using System.Linq;

namespace AddressableManager.Editor.Inspectors
{
    /// <summary>
    /// Base custom inspector for all scope components
    /// Provides visual monitoring and controls
    /// </summary>
    public abstract class BaseScopeInspector : UnityEditor.Editor
    {
        protected IAssetScope _targetScope;
        protected AssetTrackerService _tracker;
        protected string _scopeName;
        protected Color _scopeColor;

        private VisualElement _root;
        private Label _statusLabel;
        private Label _assetCountLabel;
        private Label _memoryLabel;
        private ProgressBar _memoryBar;
        private Foldout _assetsFoldout;
        private Button _activateBtn;
        private Button _deactivateBtn;
        private Button _cleanupBtn;
        private Button _openDashboardBtn;

        protected abstract string GetScopeName();
        protected abstract Color GetScopeColor();

        public override VisualElement CreateInspectorGUI()
        {
            _scopeName = GetScopeName();
            _scopeColor = GetScopeColor();
            _targetScope = target as IAssetScope;
            _tracker = AssetTrackerService.Instance;

            _root = new VisualElement();
            _root.style.paddingTop = 5;
            _root.style.paddingBottom = 5;

            CreateHeader();
            CreateStatusSection();
            CreateMemorySection();
            CreateAssetsSection();
            CreateActionsSection();

            // Subscribe to tracker updates
            _tracker.OnAssetsChanged += RefreshData;
            EditorApplication.update += OnEditorUpdate;

            RefreshData();

            return _root;
        }

        private void OnDestroy()
        {
            if (_tracker != null)
            {
                _tracker.OnAssetsChanged -= RefreshData;
            }
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (EditorApplication.isPlaying && Time.frameCount % 30 == 0) // Update every 30 frames
            {
                RefreshData();
            }
        }

        #region UI Creation

        private void CreateHeader()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.marginBottom = 10;
            header.style.borderBottomWidth = 3;
            header.style.borderBottomColor = _scopeColor;

            // Icon/Color indicator
            var colorBox = new VisualElement();
            colorBox.style.width = 20;
            colorBox.style.height = 20;
            colorBox.style.backgroundColor = _scopeColor;
            colorBox.style.marginRight = 10;
            colorBox.style.borderTopLeftRadius = 4;
            colorBox.style.borderTopRightRadius = 4;
            colorBox.style.borderBottomLeftRadius = 4;
            colorBox.style.borderBottomRightRadius = 4;

            var titleLabel = new Label($"{_scopeName} Scope");
            titleLabel.style.fontSize = 14;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.flexGrow = 1;

            _statusLabel = new Label();
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.paddingLeft = 8;
            _statusLabel.style.paddingRight = 8;
            _statusLabel.style.paddingTop = 4;
            _statusLabel.style.paddingBottom = 4;
            _statusLabel.style.borderTopLeftRadius = 4;
            _statusLabel.style.borderTopRightRadius = 4;
            _statusLabel.style.borderBottomLeftRadius = 4;
            _statusLabel.style.borderBottomRightRadius = 4;

            header.Add(colorBox);
            header.Add(titleLabel);
            header.Add(_statusLabel);

            _root.Add(header);
        }

        private void CreateStatusSection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginBottom = 10;
            section.style.borderTopLeftRadius = 3;
            section.style.borderTopRightRadius = 3;
            section.style.borderBottomLeftRadius = 3;
            section.style.borderBottomRightRadius = 3;

            var title = new Label("Status");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            _assetCountLabel = new Label();
            _assetCountLabel.style.fontSize = 11;
            _assetCountLabel.style.marginBottom = 3;
            section.Add(_assetCountLabel);

            _memoryLabel = new Label();
            _memoryLabel.style.fontSize = 11;
            section.Add(_memoryLabel);

            _root.Add(section);
        }

        private void CreateMemorySection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginBottom = 10;
            section.style.borderTopLeftRadius = 3;
            section.style.borderTopRightRadius = 3;
            section.style.borderBottomLeftRadius = 3;
            section.style.borderBottomRightRadius = 3;

            var title = new Label("Memory Usage");
            title.style.fontSize = 12;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 5;
            section.Add(title);

            _memoryBar = new ProgressBar();
            _memoryBar.title = "0 MB / 100 MB";
            _memoryBar.style.height = 20;
            section.Add(_memoryBar);

            _root.Add(section);
        }

        private void CreateAssetsSection()
        {
            var section = new VisualElement();
            section.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;
            section.style.marginBottom = 10;
            section.style.borderTopLeftRadius = 3;
            section.style.borderTopRightRadius = 3;
            section.style.borderBottomLeftRadius = 3;
            section.style.borderBottomRightRadius = 3;

            _assetsFoldout = new Foldout();
            _assetsFoldout.text = "Loaded Assets (0)";
            _assetsFoldout.value = false;
            _assetsFoldout.style.fontSize = 12;

            section.Add(_assetsFoldout);
            _root.Add(section);
        }

        private void CreateActionsSection()
        {
            var section = new VisualElement();
            section.style.paddingTop = 10;
            section.style.paddingBottom = 10;
            section.style.paddingLeft = 10;
            section.style.paddingRight = 10;

            _activateBtn = new Button(() => ActivateScope());
            _activateBtn.text = "Activate Scope";
            _activateBtn.style.marginBottom = 5;
            _activateBtn.style.backgroundColor = new Color(0.2f, 0.6f, 0.3f);
            section.Add(_activateBtn);

            _deactivateBtn = new Button(() => DeactivateScope());
            _deactivateBtn.text = "Deactivate Scope";
            _deactivateBtn.style.marginBottom = 5;
            _deactivateBtn.style.backgroundColor = new Color(0.6f, 0.5f, 0.2f);
            section.Add(_deactivateBtn);

            _cleanupBtn = new Button(() => CleanupScope());
            _cleanupBtn.text = "Cleanup Scope";
            _cleanupBtn.style.marginBottom = 5;
            _cleanupBtn.style.backgroundColor = new Color(0.7f, 0.3f, 0.3f);
            section.Add(_cleanupBtn);

            _openDashboardBtn = new Button(() => OpenDashboard());
            _openDashboardBtn.text = "Open Dashboard";
            _openDashboardBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.8f);
            section.Add(_openDashboardBtn);

            _root.Add(section);
        }

        #endregion

        #region Data Refresh

        private void RefreshData()
        {
            if (_targetScope == null || _tracker == null) return;

            // Update status
            bool isActive = _targetScope.IsActive;
            _statusLabel.text = isActive ? "ACTIVE" : "INACTIVE";
            _statusLabel.style.backgroundColor = isActive ?
                new Color(0.2f, 0.7f, 0.3f) :
                new Color(0.5f, 0.5f, 0.5f);

            // Get assets for this scope
            var assets = _tracker.GetAssetsByScope(_scopeName);
            var totalMemory = assets.Sum(a => a.MemorySize);

            // Update counts
            _assetCountLabel.text = $"Assets Loaded: {assets.Count}";
            _memoryLabel.text = $"Total Memory: {totalMemory / (1024f * 1024f):F2} MB";

            // Update memory bar
            float maxMemoryMB = 100f; // Configurable limit
            float currentMemoryMB = totalMemory / (1024f * 1024f);
            _memoryBar.value = currentMemoryMB / maxMemoryMB * 100f;
            _memoryBar.title = $"{currentMemoryMB:F2} MB / {maxMemoryMB:F0} MB";

            // Change color based on usage
            if (currentMemoryMB > maxMemoryMB * 0.8f)
                _memoryBar.style.color = new Color(0.9f, 0.3f, 0.3f); // Red - High usage
            else if (currentMemoryMB > maxMemoryMB * 0.5f)
                _memoryBar.style.color = new Color(0.9f, 0.7f, 0.2f); // Yellow - Medium usage
            else
                _memoryBar.style.color = new Color(0.3f, 0.8f, 0.4f); // Green - Low usage

            // Update assets list
            RefreshAssetsList(assets);

            // Update button states
            _activateBtn.SetEnabled(!isActive && EditorApplication.isPlaying);
            _deactivateBtn.SetEnabled(isActive && EditorApplication.isPlaying);
            _cleanupBtn.SetEnabled(assets.Count > 0 && EditorApplication.isPlaying);
        }

        private void RefreshAssetsList(System.Collections.Generic.List<AssetTrackerService.TrackedAsset> assets)
        {
            _assetsFoldout.text = $"Loaded Assets ({assets.Count})";
            _assetsFoldout.Clear();

            if (assets.Count == 0)
            {
                var emptyLabel = new Label("No assets loaded");
                emptyLabel.style.fontSize = 10;
                emptyLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                emptyLabel.style.paddingLeft = 10;
                _assetsFoldout.Add(emptyLabel);
                return;
            }

            foreach (var asset in assets.OrderBy(a => a.Address))
            {
                var assetItem = new VisualElement();
                assetItem.style.flexDirection = FlexDirection.Row;
                assetItem.style.justifyContent = Justify.SpaceBetween;
                assetItem.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
                assetItem.style.paddingTop = 5;
                assetItem.style.paddingBottom = 5;
                assetItem.style.paddingLeft = 5;
                assetItem.style.paddingRight = 5;
                assetItem.style.marginBottom = 2;
                assetItem.style.borderTopLeftRadius = 3;
                assetItem.style.borderTopRightRadius = 3;
                assetItem.style.borderBottomLeftRadius = 3;
                assetItem.style.borderBottomRightRadius = 3;

                var leftSection = new VisualElement();
                leftSection.style.flexGrow = 1;

                var nameLabel = new Label(asset.Address);
                nameLabel.style.fontSize = 10;
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                leftSection.Add(nameLabel);

                var infoLabel = new Label($"{asset.TypeName} • Loaded {GetTimeSince(asset.LoadTime)}");
                infoLabel.style.fontSize = 9;
                infoLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
                leftSection.Add(infoLabel);

                var rightSection = new VisualElement();
                rightSection.style.alignItems = Align.FlexEnd;

                var refsLabel = new Label($"Refs: {asset.ReferenceCount}");
                refsLabel.style.fontSize = 9;
                refsLabel.style.color = new Color(0.4f, 0.8f, 1f);
                rightSection.Add(refsLabel);

                var sizeLabel = new Label($"{asset.MemorySize / 1024f:F0} KB");
                sizeLabel.style.fontSize = 9;
                sizeLabel.style.color = new Color(1f, 0.7f, 0.3f);
                rightSection.Add(sizeLabel);

                assetItem.Add(leftSection);
                assetItem.Add(rightSection);

                _assetsFoldout.Add(assetItem);
            }
        }

        #endregion

        #region Actions

        private void ActivateScope()
        {
            if (EditorApplication.isPlaying)
            {
                _targetScope?.Activate();
                _tracker.UpdateScopeState(_scopeName, true);
                RefreshData();
            }
        }

        private void DeactivateScope()
        {
            if (EditorApplication.isPlaying)
            {
                _targetScope?.Deactivate();
                _tracker.UpdateScopeState(_scopeName, false);
                RefreshData();
            }
        }

        private void CleanupScope()
        {
            if (EditorUtility.DisplayDialog($"Cleanup {_scopeName} Scope",
                $"Are you sure you want to cleanup all assets in the {_scopeName} scope?\n" +
                "This will release all loaded assets and clear the cache.",
                "Yes", "Cancel"))
            {
                if (EditorApplication.isPlaying)
                {
                    _tracker.ClearScope(_scopeName);
                    _targetScope?.Deactivate();
                    _targetScope?.Activate(); // Reactivate empty scope
                    RefreshData();
                }
            }
        }

        private void OpenDashboard()
        {
            EditorApplication.ExecuteMenuItem("Window/Addressable Manager/Dashboard");
        }

        #endregion

        #region Helpers

        private string GetTimeSince(System.DateTime time)
        {
            var span = System.DateTime.Now - time;

            if (span.TotalSeconds < 60)
                return $"{span.TotalSeconds:F0}s ago";
            if (span.TotalMinutes < 60)
                return $"{span.TotalMinutes:F0}m ago";
            return $"{span.TotalHours:F0}h ago";
        }

        #endregion
    }
}
