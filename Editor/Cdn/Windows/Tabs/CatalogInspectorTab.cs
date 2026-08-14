using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Cdn.Windows.Tabs
{
    /// <summary>
    /// Reads a built catalog and checks it against the bundles on disk — task 5.9.
    /// </summary>
    /// <remarks>
    /// The other tabs answer questions about the project. This one answers a question about a
    /// build output that already exists: what did we actually publish, and does it hang together?
    ///
    /// The three views answer the three questions people open a catalog for.
    ///
    /// - <b>Entries</b> — "is this address in the build, and which bundle does it come from?" This
    ///   is the one that settles an argument about a missing asset, because the catalog is the
    ///   authority: if the address is not here, no amount of re-checking the group settings will
    ///   help.
    /// - <b>Bundles</b> — "what is big, and what is it carrying?" Sizes come from the catalog, not
    ///   from the disk, so a discrepancy is itself the finding rather than a rounding difference.
    /// - <b>Problems</b> — the cross-check against the bundle folder. Empty is the expected state.
    ///
    /// The list is a virtualised ListView because a real catalog has thousands of entries and
    /// building that many VisualElements freezes the editor for seconds. This is also why the row
    /// shape is identical across all three views: one makeItem, one bindItem, no rebuild cost when
    /// switching.
    /// </remarks>
    public sealed class CatalogInspectorTab : ICdnManagerTab
    {
        public string TabName => "Catalog";

        private enum ViewMode { Entries, Bundles, Problems }

        private static readonly List<string> ViewChoices = new List<string>
        {
            "Entries", "Bundles", "Problems"
        };

        private HelpBox _summary;
        private DropdownField _catalogPicker;
        private DropdownField _viewPicker;
        private TextField _search;
        private VisualElement _state;
        private ListView _list;
        private Button _browseButton;
        private Button _rescanButton;
        private Button _revealButton;
        private Button _copyButton;

        private readonly List<string> _catalogPaths = new List<string>();
        private readonly List<Row> _rows = new List<Row>();

        private CatalogContents _catalog;
        private CatalogInspectionReport _report;
        private string _bundleDirectory;
        private string _loadError;

        public VisualElement CreateView()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                "Packages/com.game.addressables/Editor/Cdn/UI/CatalogInspectorTab.uxml");

            if (tree == null)
            {
                Debug.LogError("[CatalogInspectorTab] Failed to load CatalogInspectorTab.uxml.");
                var fallback = new VisualElement();
                fallback.Add(new Label("Failed to load UI."));
                return fallback;
            }

            var root = tree.CloneTree();

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.game.addressables/Editor/Cdn/UI/CatalogInspectorTab.uss");
            if (uss != null) root.styleSheets.Add(uss);

            _summary = root.Q<HelpBox>("catalog-summary");
            _catalogPicker = root.Q<DropdownField>("catalog-picker");
            _viewPicker = root.Q<DropdownField>("catalog-view");
            _search = root.Q<TextField>("catalog-search");
            _state = root.Q<VisualElement>("catalog-state");
            _list = root.Q<ListView>("catalog-rows");
            _browseButton = root.Q<Button>("catalog-browse-btn");
            _rescanButton = root.Q<Button>("catalog-rescan-btn");
            _revealButton = root.Q<Button>("catalog-reveal-btn");
            _copyButton = root.Q<Button>("catalog-copy-btn");

            _viewPicker.choices = ViewChoices;
            _viewPicker.index = 0;
            _viewPicker.RegisterValueChangedCallback(_ => RefreshRows());

            _search.label = "Filter";
            _search.RegisterValueChangedCallback(_ => RefreshRows());

            _catalogPicker.RegisterValueChangedCallback(evt => LoadCatalog(evt.newValue));

            _browseButton.clicked += Browse;
            _rescanButton.clicked += Rescan;
            _revealButton.clicked += RevealBundleFolder;
            _copyButton.clicked += CopyReport;

            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.selectionChanged += OnRowSelected;

            return root;
        }

        public void OnShown() => Rescan();

        // ========== scanning and loading ==========

        private void Rescan()
        {
            string previous = _catalogPicker.value;

            _catalogPaths.Clear();
            _catalogPaths.AddRange(FindCatalogs(out _bundleDirectory, out string scanNote));

            if (_catalogPaths.Count == 0)
            {
                _catalog = null;
                _report = null;
                _catalogPicker.choices = new List<string>();
                _catalogPicker.SetValueWithoutNotify(string.Empty);
                Set(HelpBoxMessageType.Info, scanNote);
                RefreshState();
                RefreshRows();
                return;
            }

            var labels = new List<string>(_catalogPaths.Count);
            foreach (string path in _catalogPaths)
                labels.Add(Describe(path));

            _catalogPicker.choices = labels;

            int index = previous != null ? labels.IndexOf(previous) : -1;
            _catalogPicker.SetValueWithoutNotify(labels[index >= 0 ? index : 0]);
            LoadCatalog(_catalogPicker.value);
        }

        private void LoadCatalog(string label)
        {
            int index = _catalogPicker.choices.IndexOf(label);
            if (index < 0 || index >= _catalogPaths.Count)
                return;

            LoadCatalogAt(_catalogPaths[index]);
        }

        private void LoadCatalogAt(string path)
        {
            _loadError = null;
            _catalog = null;
            _report = null;

            var read = CatalogReader.Read(path);
            if (read.IsFailure)
            {
                _loadError = read.ErrorMessage;
                Set(HelpBoxMessageType.Error, read.ErrorMessage);
                RefreshState();
                RefreshRows();
                return;
            }

            _catalog = read.Value;

            // The cross-check is a separate step so a catalog still reads when the bundle folder is
            // gone — inspecting a catalog someone sent you is a legitimate use, and refusing to show
            // its contents because there are no local bundles would be the wrong trade.
            var compare = CatalogInspection.Compare(_catalog, _bundleDirectory);
            if (compare.IsSuccess)
            {
                _report = compare.Value;
                Set(_report.IsPublishable
                        ? (_report.Orphans.Count > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info)
                        : HelpBoxMessageType.Error,
                    _report.Summary());
            }
            else
            {
                Set(HelpBoxMessageType.Info,
                    $"{_catalog.Entries.Count} entries, {_catalog.Bundles.Count} bundles. " +
                    $"Not cross-checked: {compare.ErrorMessage}");
            }

            RefreshState();
            RefreshRows();
        }

        private void Browse()
        {
            string start = Directory.Exists(_bundleDirectory)
                ? _bundleDirectory
                : Path.GetDirectoryName(Application.dataPath);

            string picked = EditorUtility.OpenFilePanelWithFilters(
                "Select a content catalog", start, new[] { "Catalog", "bin,json" });

            if (string.IsNullOrEmpty(picked))
                return;

            // A hand-picked catalog is very often from somewhere else entirely, so it goes into the
            // list rather than replacing it — being able to switch back to the built one without
            // re-scanning is the point.
            if (!_catalogPaths.Contains(picked))
            {
                _catalogPaths.Add(picked);
                var labels = new List<string>(_catalogPicker.choices) { Describe(picked) };
                _catalogPicker.choices = labels;
            }

            _catalogPicker.SetValueWithoutNotify(Describe(picked));
            LoadCatalogAt(picked);
        }

        /// <summary>
        /// Find the catalogs this project has built.
        /// </summary>
        /// <remarks>
        /// Both the catalog folder and the bundle folder are searched. They are separate profile
        /// variables and the layout puts them in separate trees, but a project that has not adopted
        /// the split — or one built before it — has the catalog sitting next to the bundles, and an
        /// inspector that could not open those would be useless exactly when it is needed.
        /// </remarks>
        private static List<string> FindCatalogs(out string bundleDirectory, out string note)
        {
            bundleDirectory = null;
            var found = new List<string>();

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                note = "No Addressables settings in this project, so there is nothing to look in. " +
                       "Use Browse to open a catalog file directly.";
                return found;
            }

            string catalogDirectory;
            try
            {
                bundleDirectory = CdnBuildPipeline.ResolveBundleDir(settings);
                catalogDirectory = CdnBuildPipeline.ResolveCatalogDir(settings);
            }
            catch (Exception e)
            {
                note = $"Could not resolve the build paths from the active profile: {e.Message}";
                return found;
            }

            Collect(catalogDirectory, found);
            Collect(bundleDirectory, found);

            found.Sort(StringComparer.OrdinalIgnoreCase);

            note = found.Count > 0
                ? string.Empty
                : "No built catalog found. Build content from the Build tab first, or use Browse " +
                  $"to open one from elsewhere. Looked in '{catalogDirectory}' and '{bundleDirectory}'.";

            return found;
        }

        private static void Collect(string directory, List<string> into)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            foreach (string pattern in new[] { "catalog*.bin", "catalog*.json" })
            {
                foreach (string path in Directory.GetFiles(directory, pattern, SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(path);
                    if (!into.Contains(full))
                        into.Add(full);
                }
            }
        }

        /// <summary>
        /// A label short enough for the dropdown but long enough to tell two catalogs apart.
        /// </summary>
        /// <remarks>
        /// Every catalog in a content-update layout is called <c>catalog_&lt;version&gt;.bin</c> and
        /// they sit in per-version folders, so the file name alone is ambiguous the moment there is
        /// more than one build. The parent folder is what distinguishes them.
        /// </remarks>
        private static string Describe(string path)
        {
            string parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
            return string.IsNullOrEmpty(parent)
                ? Path.GetFileName(path)
                : $"{parent}/{Path.GetFileName(path)}";
        }

        // ========== views ==========

        private void RefreshState()
        {
            _state.Clear();

            if (_catalog == null)
            {
                AddRow("Catalog", _loadError == null ? "none loaded" : "failed to read", _loadError != null);
                return;
            }

            AddRow("File", _catalog.CatalogPath, false);
            AddRow("Locator id", string.IsNullOrEmpty(_catalog.LocatorId) ? "(unnamed)" : _catalog.LocatorId, false);
            AddRow("Entries", _catalog.Entries.Count.ToString(), false);

            int remote = _catalog.CountBundles(BundleLocation.Remote);
            int local = _catalog.CountBundles(BundleLocation.Local);
            int unknown = _catalog.CountBundles(BundleLocation.Unknown);

            AddRow("Bundles", $"{_catalog.Bundles.Count} ({remote} remote, {local} in the player)", false);

            if (unknown > 0)
            {
                AddRow("Unrecognised load path", $"{unknown} bundle(s) — not checked against the folder", true);
            }

            AddRow("Remote download size", RuntimeMonitorTab.FormatBytes(_catalog.RemoteBundleBytes), false);

            if (_report == null)
            {
                AddRow("Bundle folder", "not compared", false);
                return;
            }

            AddRow("Bundle folder", _report.BundleDirectory, false);
            AddRow("Files on disk", _report.BundlesOnDisk.ToString(), false);

            if (_report.Missing.Count > 0)
                AddRow("Missing", $"{_report.Missing.Count} bundle(s) the catalog needs", true);

            if (_report.SizeMismatches.Count > 0)
                AddRow("Size mismatches", $"{_report.SizeMismatches.Count} bundle(s)", true);

            if (_report.Orphans.Count > 0)
            {
                AddRow("Orphaned",
                    $"{_report.Orphans.Count} file(s), {RuntimeMonitorTab.FormatBytes(_report.OrphanBytes)}",
                    false);
            }
        }

        private void RefreshRows()
        {
            _rows.Clear();

            string filter = _search?.value ?? string.Empty;
            var mode = (ViewMode)Mathf.Max(0, _viewPicker.index);

            if (_catalog != null)
            {
                switch (mode)
                {
                    case ViewMode.Entries: BuildEntryRows(filter); break;
                    case ViewMode.Bundles: BuildBundleRows(filter); break;
                    case ViewMode.Problems: BuildProblemRows(filter); break;
                }
            }

            _list.itemsSource = _rows;
            _list.Rebuild();
        }

        private void BuildEntryRows(string filter)
        {
            foreach (var entry in _catalog.Entries)
            {
                if (!Matches(filter, entry.Address, entry.InternalId, entry.ResourceType))
                    continue;

                string bundles = entry.BundleNames.Count == 0
                    ? "no bundle"
                    : entry.BundleNames.Count == 1
                        ? Path.GetFileName(entry.BundleNames[0])
                        : $"{entry.BundleNames.Count} bundles";

                _rows.Add(new Row(entry.Address, bundles, entry.ResourceType, RowSeverity.Normal, Detail(entry)));
            }

            if (_rows.Count == 0)
                _rows.Add(EmptyRow(string.IsNullOrEmpty(filter) ? "This catalog has no entries." : "No entry matches the filter."));
        }

        private void BuildBundleRows(string filter)
        {
            foreach (var bundle in _catalog.Bundles)
            {
                if (!Matches(filter, bundle.BundleName, bundle.CatalogName, bundle.InternalId, bundle.Hash))
                    continue;

                // A catalog that recorded no size shows "not recorded", never "0 B". They are
                // different facts and only one of them is a reason to go looking.
                string size = bundle.SizeBytes > 0
                    ? RuntimeMonitorTab.FormatBytes(bundle.SizeBytes)
                    : "not recorded";

                string where = bundle.Location == BundleLocation.Remote
                    ? $"{bundle.DependentEntryCount} entries"
                    : bundle.Location == BundleLocation.Local
                        ? "in the player"
                        : "unknown path";

                _rows.Add(new Row(
                    bundle.BundleName, size, where,
                    bundle.Location == BundleLocation.Unknown ? RowSeverity.Warning : RowSeverity.Normal,
                    Detail(bundle)));
            }

            if (_rows.Count == 0)
                _rows.Add(EmptyRow(string.IsNullOrEmpty(filter) ? "This catalog references no bundles." : "No bundle matches the filter."));
        }

        private void BuildProblemRows(string filter)
        {
            if (_report == null)
            {
                _rows.Add(EmptyRow("Not cross-checked — no bundle folder to compare against."));
                return;
            }

            foreach (var problem in _report.Missing)
            {
                if (!Matches(filter, problem.BundleFileName)) continue;
                _rows.Add(new Row(
                    problem.BundleFileName,
                    $"{problem.DependentEntryCount} entries need it",
                    "MISSING", RowSeverity.Error,
                    $"{problem.BundleFileName}\nReferenced by the catalog, absent from {_report.BundleDirectory}."));
            }

            foreach (var problem in _report.SizeMismatches)
            {
                if (!Matches(filter, problem.BundleFileName)) continue;
                _rows.Add(new Row(
                    problem.BundleFileName,
                    $"{RuntimeMonitorTab.FormatBytes(problem.CatalogBytes)} vs {RuntimeMonitorTab.FormatBytes(problem.ActualBytes)}",
                    "SIZE", RowSeverity.Error,
                    $"{problem.BundleFileName}\nCatalog says {problem.CatalogBytes} B, the file is " +
                    $"{problem.ActualBytes} B. The catalog and the bundles came from different builds."));
            }

            foreach (var problem in _report.Orphans)
            {
                if (!Matches(filter, problem.BundleFileName)) continue;
                _rows.Add(new Row(
                    problem.BundleFileName,
                    RuntimeMonitorTab.FormatBytes(problem.ActualBytes),
                    "ORPHAN", RowSeverity.Warning,
                    $"{problem.BundleFileName}\nOn disk, referenced by nothing in this catalog. It may " +
                    "still be needed by an older catalog that installed players are running, so check " +
                    "before deleting it from the CDN."));
            }

            if (_rows.Count == 0)
            {
                _rows.Add(EmptyRow(string.IsNullOrEmpty(filter)
                    ? "Catalog and bundle folder agree."
                    : "No problem matches the filter."));
            }
        }

        private static string Detail(CatalogEntry entry)
        {
            var text = new StringBuilder();
            text.AppendLine(entry.Address);
            text.AppendLine($"key       {entry.Key}");
            text.AppendLine($"type      {entry.ResourceType}");
            text.AppendLine($"provider  {entry.ProviderId}");
            text.AppendLine($"id        {entry.InternalId}");
            if (entry.BundleNames.Count > 0)
                text.AppendLine($"bundles   {string.Join(", ", entry.BundleNames)}");
            return text.ToString();
        }

        private static string Detail(CatalogBundle bundle)
        {
            var text = new StringBuilder();
            text.AppendLine(bundle.BundleName);
            text.AppendLine($"loaded    {bundle.Location}");
            text.AppendLine($"catalog   {bundle.CatalogName}");
            text.AppendLine($"size      {(bundle.SizeBytes > 0 ? bundle.SizeBytes + " B" : "not recorded")}");
            text.AppendLine($"crc       {bundle.Crc}");
            text.AppendLine($"hash      {bundle.Hash}");
            text.AppendLine($"provider  {bundle.ProviderId}");
            text.AppendLine($"id        {bundle.InternalId}");
            text.AppendLine($"needed by {bundle.DependentEntryCount} entries");
            return text.ToString();
        }

        private static bool Matches(string filter, params string[] fields)
        {
            if (string.IsNullOrEmpty(filter)) return true;

            foreach (string field in fields)
            {
                if (!string.IsNullOrEmpty(field) &&
                    field.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static Row EmptyRow(string message) => new Row(message, string.Empty, string.Empty, RowSeverity.Note, message);

        // ========== list plumbing ==========

        private enum RowSeverity { Normal, Warning, Error, Note }

        private readonly struct Row
        {
            public readonly string Primary;
            public readonly string Secondary;
            public readonly string Tag;
            public readonly RowSeverity Severity;
            public readonly string Detail;

            public Row(string primary, string secondary, string tag, RowSeverity severity, string detail)
            {
                Primary = primary;
                Secondary = secondary;
                Tag = tag;
                Severity = severity;
                Detail = detail;
            }
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-catalog-row");

            var primary = new Label { name = "primary" };
            primary.AddToClassList("cdn-catalog-row-primary");
            row.Add(primary);

            var secondary = new Label { name = "secondary" };
            secondary.AddToClassList("cdn-catalog-row-secondary");
            row.Add(secondary);

            var tag = new Label { name = "tag" };
            tag.AddToClassList("cdn-catalog-row-tag");
            row.Add(tag);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            if (index < 0 || index >= _rows.Count) return;

            var row = _rows[index];

            var primary = element.Q<Label>("primary");
            primary.text = row.Primary;
            // The severity word is in the tag column as text as well as in the colour. Colour alone
            // is not a signal a colour-blind reader can act on, and this list is mostly one shade.
            primary.EnableInClassList("cdn-catalog-row-primary-bad", row.Severity == RowSeverity.Error);
            primary.EnableInClassList("cdn-catalog-row-primary-warn", row.Severity == RowSeverity.Warning);
            primary.tooltip = row.Detail;

            element.Q<Label>("secondary").text = row.Secondary;
            element.Q<Label>("tag").text = row.Tag;
        }

        private void OnRowSelected(IEnumerable<object> _)
        {
            int index = _list.selectedIndex;
            if (index < 0 || index >= _rows.Count) return;

            // Selecting a row prints its full detail. A tooltip is fine for a glance; an internal id
            // is something you need to paste into a browser or a bug report.
            Debug.Log("[Catalog Inspector]\n" + _rows[index].Detail);
        }

        // ========== actions ==========

        private void RevealBundleFolder()
        {
            if (string.IsNullOrEmpty(_bundleDirectory) || !Directory.Exists(_bundleDirectory))
            {
                Set(HelpBoxMessageType.Warning,
                    $"No bundle folder at '{_bundleDirectory}'. Build content first.");
                return;
            }

            EditorUtility.RevealInFinder(_bundleDirectory);
        }

        private void CopyReport()
        {
            if (_catalog == null)
            {
                Set(HelpBoxMessageType.Warning, "Nothing to copy — no catalog is loaded.");
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"Catalog: {_catalog.CatalogPath}");
            text.AppendLine($"Locator: {_catalog.LocatorId}");
            text.AppendLine($"Entries: {_catalog.Entries.Count}");
            text.AppendLine($"Bundles: {_catalog.Bundles.Count} " +
                            $"({_catalog.CountBundles(BundleLocation.Remote)} remote, " +
                            $"{RuntimeMonitorTab.FormatBytes(_catalog.RemoteBundleBytes)} to download)");

            if (_report != null)
            {
                text.AppendLine();
                text.AppendLine($"Bundle folder: {_report.BundleDirectory}");
                text.AppendLine(_report.Summary());

                AppendProblems(text, "MISSING (publishing this build breaks these)", _report.Missing);
                AppendProblems(text, "SIZE MISMATCH (catalog and bundles are from different builds)", _report.SizeMismatches);
                AppendProblems(text, "ORPHANED (on disk, unreferenced by this catalog)", _report.Orphans);
            }

            EditorGUIUtility.systemCopyBuffer = text.ToString();
            Set(HelpBoxMessageType.Info, "Report copied to the clipboard.");
        }

        private static void AppendProblems(StringBuilder text, string heading, IReadOnlyList<BundleProblem> problems)
        {
            if (problems.Count == 0) return;

            text.AppendLine();
            text.AppendLine(heading);
            foreach (var problem in problems)
                text.AppendLine($"  {problem.BundleFileName}  catalog={problem.CatalogBytes} disk={problem.ActualBytes}");
        }

        // ========== helpers ==========

        private void Set(HelpBoxMessageType type, string message)
        {
            _summary.messageType = type;
            _summary.text = message;
        }

        private void AddRow(string key, string value, bool isBad)
        {
            var row = new VisualElement();
            row.AddToClassList("cdn-state-row");

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("cdn-state-key");
            row.Add(keyLabel);

            var valueLabel = new Label(string.IsNullOrEmpty(value) ? "—" : value);
            valueLabel.AddToClassList("cdn-state-value");
            valueLabel.AddToClassList(isBad ? "cdn-state-value-bad" : "cdn-state-value-good");
            valueLabel.tooltip = value;
            row.Add(valueLabel);

            _state.Add(row);
        }
    }
}
