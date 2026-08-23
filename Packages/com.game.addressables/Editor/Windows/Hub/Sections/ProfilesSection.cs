using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Whether each profile follows the convention that keeps a build switchable between
    /// environments — and one editable field, the origin.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not a second profile editor.</b> Addressables owns these values, and this
    /// whole review cycle was about one failure shape: two places holding the same truth and
    /// drifting apart. The <c>&lt;domain&gt;</c> → <c>&lt;cdnBase&gt;</c> rename that killed CI host
    /// injection was exactly that. A second editor manufactures another instance of the bug.
    ///
    /// What the Addressables profile window cannot do is <i>judge</i>. It shows a text field; it does
    /// not know that the origin is missing from CdnSettings, or that one profile's suffix has drifted
    /// from the other three. So this screen adds the judgement and leaves the storage alone. The one
    /// field it writes is the origin, because that is the field CI must set and the one the injector
    /// exists for.
    /// </remarks>
    public sealed class ProfilesSection : IHubSection, IHubHostAware
    {
        /// <summary>One profile's conformance, all three checks read together.</summary>
        private readonly struct Row
        {
            public readonly string Name;
            public readonly bool IsActive;
            public readonly string BundleRaw;
            public readonly string BundleEvaluated;
            public readonly bool HasPlaceholder;
            public readonly bool SuffixOk;
            public readonly HealthState OriginKnown;
            public readonly string OriginKnownNote;
            public readonly string Origin;

            public Row(string name, bool isActive, string bundleRaw, string bundleEvaluated,
                bool hasPlaceholder, bool suffixOk, HealthState originKnown, string originKnownNote,
                string origin)
            {
                Name = name;
                IsActive = isActive;
                BundleRaw = bundleRaw;
                BundleEvaluated = bundleEvaluated;
                HasPlaceholder = hasPlaceholder;
                SuffixOk = suffixOk;
                OriginKnown = originKnown;
                OriginKnownNote = originKnownNote;
                Origin = origin;
            }

            public HealthState Worst
            {
                get
                {
                    if (HasPlaceholder) return HealthState.Blocked;
                    var s = SuffixOk ? HealthState.Ok : HealthState.Warning;
                    return SectionHealth.Worse(s, OriginKnown);
                }
            }
        }

        /// <summary>
        /// Three seconds between full conformance reads.
        /// </summary>
        /// <remarks>
        /// The read walks every profile, evaluates its path variables and calls
        /// <c>CdnSettings.Load()</c>, which is a <c>Resources.Load</c> with no cache of its own. On
        /// the rail's one-second timer that is three avoidable operations a second for as long as
        /// the window is open.
        /// </remarks>
        private readonly HealthThrottle _health = new HealthThrottle();

        private IHubHost _host;
        private VisualElement _body;
        private TextField _originField;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Profiles;

        /// <inheritdoc />
        public string Title => "Profiles";

        /// <inheritdoc />
        public string Subtitle => "Does each environment follow the convention that keeps a build switchable";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Configure;

        /// <inheritdoc />
        public void Bind(IHubHost host) => _host = host;

        /// <inheritdoc />
        public SectionHealth GetHealth() => _health.Get(ComputeHealth);

        private SectionHealth ComputeHealth()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return SectionHealth.NotMeasured("No AddressableAssetSettings asset, so no profile could be read.");

            if (CdnBuildModes.IsLocalOnly)
                return SectionHealth.NotMeasured("Build Mode is Local-only, so no remote profile has to resolve.");

            List<Row> rows;
            try
            {
                rows = ReadRows(settings);
            }
            catch (Exception)
            {
                return SectionHealth.NotMeasured("The profiles could not be read.");
            }

            foreach (var row in rows)
            {
                // The ACTIVE profile is the only one that can block: an unresolved placeholder on a
                // profile nobody is building with is a job for later, not a stopped pipeline.
                if (row.IsActive && row.HasPlaceholder)
                {
                    return SectionHealth.Blocked(
                        "host not set",
                        $"The active profile '{row.Name}' still contains an origin placeholder, so a build " +
                        "would bake a hostname that does not exist into the catalog.");
                }
            }

            int unknown = 0, drift = 0, placeholders = 0;
            foreach (var row in rows)
            {
                if (row.HasPlaceholder) placeholders++;
                if (!row.SuffixOk) drift++;
                if (row.OriginKnown == HealthState.Warning) unknown++;
            }

            if (drift > 0)
                return SectionHealth.Warning($"{drift} suffix drift", "A profile's path suffix differs from the others, so switching environment at runtime would land somewhere the CDN has nothing.");

            if (unknown > 0)
                return SectionHealth.Warning($"{unknown} unknown origin", "An origin is not declared in CdnSettings, so the runtime rewriter would leave it alone.");

            if (placeholders > 0)
                return SectionHealth.Warning($"{placeholders} unset", "An inactive profile still has a placeholder host.");

            return SectionHealth.Ok();
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "profiles-root" };
            _body.AddToClassList("hub-page");
            return _body;
        }

        /// <inheritdoc />
        public void OnShown()
        {
            _health.Invalidate();
            Rebuild();
        }

        // ------------------------------------------------------------------

        private void Rebuild()
        {
            if (_body == null) return;
            _body.Clear();

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                _body.Add(Note(
                    "This project has no AddressableAssetSettings asset, so there are no profiles to " +
                    "check. That is not a pass — nothing here has been read."));
                return;
            }

            _body.Add(Note(
                "Addressables owns these values; this screen only checks them. A second editor for the " +
                "same data is a second source of truth, and the only field editable here is the " +
                "origin — the one CI has to set."));

            List<Row> rows;
            try
            {
                rows = ReadRows(settings);
            }
            catch (Exception ex)
            {
                _body.Add(Note($"The profiles could not be read: {ex.Message}"));
                Debug.LogException(ex);
                return;
            }

            _body.Add(BuildTable(rows));
            _body.Add(BuildOriginEditor(settings, rows));
            _body.Add(BuildCiNote());
        }

        private VisualElement BuildTable(List<Row> rows)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");
            head.Add(Cell("PROFILE", "hub-col-profile", true));
            head.Add(Cell("ORIGIN", "hub-col-origin", true));
            head.Add(Cell("SUFFIX", "hub-col-suffix", true));
            head.Add(Cell("IN CDNSETTINGS", "hub-col-known", true));
            card.Add(head);

            foreach (var row in rows)
            {
                var line = new VisualElement();
                line.AddToClassList("hub-prow");
                if (row.IsActive) line.AddToClassList("hub-prow--active");

                var nameCell = new VisualElement();
                nameCell.AddToClassList("hub-col-profile");
                nameCell.style.flexDirection = FlexDirection.Row;
                nameCell.style.alignItems = Align.Center;

                var dot = new VisualElement();
                dot.AddToClassList("hub-rule-dot");
                dot.style.marginTop = 0;
                ApplyState(dot, row.Worst);
                nameCell.Add(dot);

                var nameLabel = new Label(row.IsActive ? row.Name + "  ●" : row.Name);
                nameLabel.tooltip = row.IsActive ? "The active profile — this is what a build uses." : string.Empty;
                nameCell.Add(nameLabel);
                line.Add(nameCell);

                var origin = Cell(
                    row.HasPlaceholder ? row.Origin + "   (placeholder)" : row.Origin,
                    "hub-col-origin");
                origin.tooltip = row.BundleEvaluated;
                if (row.HasPlaceholder) ApplyText(origin, HealthState.Blocked);
                line.Add(origin);

                var suffix = Cell(row.SuffixOk ? "matches" : "DRIFTED", "hub-col-suffix");
                if (!row.SuffixOk) ApplyText(suffix, HealthState.Warning);
                line.Add(suffix);

                var known = Cell(row.OriginKnownNote, "hub-col-known");
                ApplyText(known, row.OriginKnown);
                line.Add(known);

                card.Add(line);
            }

            return card;
        }

        /// <summary>The one editable field: the origin of the active profile.</summary>
        private VisualElement BuildOriginEditor(AddressableAssetSettings settings, List<Row> rows)
        {
            Row active = default;
            bool found = false;
            foreach (var row in rows)
            {
                if (!row.IsActive) continue;
                active = row;
                found = true;
                break;
            }

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");
            var title = new Label(found && active.HasPlaceholder
                ? $"'{active.Name}' has no host yet"
                : "Set the origin for the active profile");
            title.AddToClassList("hub-card-title");
            if (found && active.HasPlaceholder) ApplyText(title, HealthState.Blocked);
            head.Add(title);
            card.Add(head);

            if (!found)
            {
                var none = new Label("No profile is active, so there is nothing to set.");
                none.AddToClassList("hub-rule-meta");
                none.style.paddingLeft = 8;
                none.style.paddingBottom = 7;
                card.Add(none);
                return card;
            }

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 7;
            body.style.paddingBottom = 8;

            var blurb = new Label(
                "Set it here, or leave it templated and let CI inject it with CDN_HOST. Both are " +
                "normal; a release build must not go out with the placeholder either way. Writing it " +
                "here replaces the origin on BOTH the bundle and the catalog path, keeping the shared " +
                "suffix — that pairing is what makes runtime environment switching work.");
            blurb.AddToClassList("hub-rule-meta");
            body.Add(blurb);

            var editRow = new VisualElement();
            editRow.style.flexDirection = FlexDirection.Row;
            editRow.style.alignItems = Align.Center;
            editRow.style.marginTop = 6;

            _originField = new TextField { value = active.HasPlaceholder ? string.Empty : active.Origin };
            _originField.style.flexGrow = 1;
            editRow.Add(_originField);

            string profileName = active.Name;
            var apply = new Button(() => ApplyOrigin(settings, profileName, _originField.value)) { text = "Set" };
            apply.AddToClassList("hub-btn");
            editRow.Add(apply);

            body.Add(editRow);

            var example = new Label("e.g.  https://cdn.example.com   or   https://cdn.example.com/game-a");
            example.AddToClassList("hub-rule-id");
            example.style.marginTop = 4;
            body.Add(example);

            card.Add(body);
            return card;
        }

        private void ApplyOrigin(AddressableAssetSettings settings, string profileName, string origin)
        {
            origin = (origin ?? string.Empty).Trim().TrimEnd('/');

            if (origin.Length == 0)
            {
                EditorUtility.DisplayDialog("Nothing to set", "Enter an origin first.", "OK");
                return;
            }

            // Refuse a bare hostname rather than silently building an unusable URL. HostRewriter
            // matches on the whole origin including the scheme, so "cdn.example.com" would never
            // match anything and switching environment would quietly do nothing.
            if (!origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog(
                    "The origin needs a scheme",
                    $"'{origin}' has no http:// or https://.\n\n" +
                    "The runtime rewriter matches the whole origin including the scheme, so without " +
                    "one it would never match and switching environment would silently do nothing.",
                    "OK");
                return;
            }

            string profileId = settings.profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(profileId))
            {
                EditorUtility.DisplayDialog("Profile not found", $"No profile named '{profileName}'.", "OK");
                return;
            }

            settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath,
                origin + CdnProfileManager.PathConvention.BundleSuffix);
            settings.profileSettings.SetValue(profileId, CdnProfileManager.RemoteCatalogLoadPathVariable,
                origin + CdnProfileManager.PathConvention.CatalogSuffix);

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Profiles] '{profileName}' now loads from {origin}.");

            // The badge is about to be wrong for three seconds otherwise, right after the user
            // watched themselves fix the thing it is complaining about.
            _health.Invalidate();

            // Re-read rather than assume it took. The row above is rebuilt from the settings asset,
            // so a write that did not land shows immediately instead of being reported as done.
            Rebuild();
        }

        private static VisualElement BuildCiNote()
        {
            return Note(
                "Which profile should CI build with? The one for the environment you are shipping to. " +
                "The URL that profile carries is the one your players poll forever, and no later upload " +
                "can change it.\n\n" +
                "Building with Local and switching environment at runtime is path-safe since " +
                "4.1.0-pre.14, and it is what QA should use — but a shipped build whose rewrite fails " +
                "falls back to localhost with no way to recover it.");
        }

        // ------------------------------------------------------------------ model

        private static List<Row> ReadRows(AddressableAssetSettings settings)
        {
            var rows = new List<Row>();
            var known = KnownOrigins();
            string activeId = settings.activeProfileId;

            // The suffix every profile is supposed to share. Compared against the constant rather
            // than against the other profiles, so a project where ALL FOUR drifted the same way is
            // still reported - agreement is not the same as correctness.
            string expectedSuffix = CdnProfileManager.PathConvention.BundleSuffix;

            foreach (string name in settings.profileSettings.GetAllProfileNames())
            {
                string id = settings.profileSettings.GetProfileId(name);
                if (string.IsNullOrEmpty(id)) continue;

                string raw = settings.profileSettings.GetValueByName(id, AddressableAssetSettings.kRemoteLoadPath)
                             ?? string.Empty;
                string evaluated = string.IsNullOrEmpty(raw)
                    ? string.Empty
                    : settings.profileSettings.EvaluateString(id, raw) ?? string.Empty;

                bool placeholder = CdnProfileManager.PathConvention.ContainsOriginPlaceholder(raw);
                bool suffixOk = raw.EndsWith(expectedSuffix, StringComparison.Ordinal);

                string origin = suffixOk && raw.Length >= expectedSuffix.Length
                    ? raw.Substring(0, raw.Length - expectedSuffix.Length)
                    : raw;

                var knownState = ClassifyOrigin(evaluated, placeholder, known, out string knownNote);

                rows.Add(new Row(name, id == activeId, raw, evaluated, placeholder, suffixOk,
                    knownState, knownNote, string.IsNullOrEmpty(origin) ? "(not set)" : origin));
            }

            return rows;
        }

        /// <summary>
        /// Whether a profile's origin is one the runtime knows about.
        /// </summary>
        /// <remarks>
        /// Three answers, not two, and the third is the point. A profile that still holds a
        /// placeholder has an origin nobody can evaluate yet, so "not in CdnSettings" would be a
        /// verdict on a value that does not exist. It reports "cannot tell yet" instead.
        /// </remarks>
        private static HealthState ClassifyOrigin(
            string evaluated, bool placeholder, List<string> known, out string note)
        {
            if (placeholder)
            {
                note = "cannot tell yet";
                return HealthState.NotMeasured;
            }

            if (known == null)
            {
                note = "no CdnSettings";
                return HealthState.NotMeasured;
            }

            if (string.IsNullOrEmpty(evaluated) ||
                (!evaluated.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !evaluated.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                note = "not remote";
                return HealthState.Ok;
            }

            foreach (string origin in known)
            {
                if (evaluated.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                {
                    note = "yes";
                    return HealthState.Ok;
                }
            }

            note = "no — add it";
            return HealthState.Warning;
        }

        /// <summary>Base URLs declared in CdnSettings, or null when the asset cannot be read.</summary>
        /// <remarks>
        /// Null rather than an empty list on failure, so "no CdnSettings asset" and "an asset that
        /// declares no environments" stay distinguishable. Collapsing them would report every profile
        /// as an unknown origin on a project that simply has not configured the CDN yet.
        /// </remarks>
        private static List<string> KnownOrigins()
        {
            var loaded = AddressableManager.Cdn.CdnSettings.Load();
            if (loaded.IsFailure || loaded.Value == null) return null;

            var origins = new List<string>();
            foreach (var environment in loaded.Value.Environments)
            {
                if (environment == null || string.IsNullOrEmpty(environment.BaseUrl)) continue;
                origins.Add(environment.BaseUrl.TrimEnd('/'));
            }

            return origins;
        }

        // ------------------------------------------------------------------ helpers

        private static Label Cell(string text, string cls, bool header = false)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            label.AddToClassList(header ? "hub-pcol-head" : "hub-pcol");
            return label;
        }

        private static VisualElement Note(string text)
        {
            var box = new VisualElement();
            box.AddToClassList("hub-note");

            var label = new Label(text);
            label.AddToClassList("hub-note-text");
            box.Add(label);

            return box;
        }

        private static void ApplyState(VisualElement element, HealthState state)
        {
            if (element == null) return;
            foreach (var cls in SectionHealth.AllStyleClasses)
                element.RemoveFromClassList(cls);
            element.AddToClassList(SectionHealth.StyleClassFor(state));
        }

        /// <summary>State colour on text, without the filled background a dot wants.</summary>
        private static void ApplyText(VisualElement element, HealthState state)
        {
            ApplyState(element, state);
            element.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0));
            element.style.borderTopColor = new StyleColor(new Color(0, 0, 0, 0));
        }
    }
}
