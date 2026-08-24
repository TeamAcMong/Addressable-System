using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// What a build would produce, why it cannot start, and what ships where.
    /// </summary>
    /// <remarks>
    /// The old Build tab offered the two build buttons and let the pipeline explain itself
    /// afterwards. The expensive failure is the one that does NOT stop the build: a profile whose
    /// host was never resolved bakes a hostname that does not exist into the catalog, and every
    /// player of that build polls it forever - no later upload can change it. So this screen leads
    /// with the blocking check and disables the buttons behind it, rather than reporting the problem
    /// once the damage would already be on disk.
    ///
    /// "What ships where" is the second thing worth seeing before pressing anything: which groups
    /// ride inside the player and which are fetched. It is the question that decides download size
    /// on first launch, and it was previously answerable only by opening every group's schema.
    /// </remarks>
    public sealed class BuildSection : IHubSection, IHubHostAware
    {
        private readonly HealthThrottle _health = new HealthThrottle();
        private IHubHost _host;
        private VisualElement _body;

        /// <inheritdoc />
        public string Id => HubSections.Ids.Build;

        /// <inheritdoc />
        public string Title => "Build";

        /// <inheritdoc />
        public string Subtitle => "Produce bundles and a catalog for the active profile";

        /// <inheritdoc />
        public PipelineStage Stage => PipelineStage.Build;

        /// <inheritdoc />
        public void Bind(IHubHost host) => _host = host;

        /// <inheritdoc />
        public SectionHealth GetHealth() => _health.Get(ComputeHealth);

        private SectionHealth ComputeHealth()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return SectionHealth.NotMeasured("No AddressableAssetSettings asset, so nothing can be built.");

            if (CdnBuildModes.IsLocalOnly)
                return SectionHealth.NotMeasured("Build Mode is Local-only; there is no remote build to check.");

            if (HasUnresolvedHost(settings, out string where))
            {
                return SectionHealth.Blocked(
                    "host not set",
                    $"The active profile's {where} still contains an origin placeholder, so a build " +
                    "would bake a hostname that does not exist.");
            }

            string manifest = System.IO.Path.Combine(
                BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName);

            return System.IO.File.Exists(manifest)
                ? SectionHealth.Ok()
                : SectionHealth.NotMeasured("No build has run in this workspace yet.");
        }

        /// <inheritdoc />
        public VisualElement CreateView()
        {
            _body = new ScrollView { name = "build-root" };
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
                _body.Add(Note("This project has no AddressableAssetSettings asset, so there is nothing to build."));
                return;
            }

            if (CdnBuildModes.IsLocalOnly)
            {
                _body.Add(Note(
                    "Build Mode is Local-only, so all content ships inside the player and there is no " +
                    "remote build to make here. Use Unity's own player build. This is a deliberate " +
                    "configuration, not a problem to fix."));
            }

            bool blocked = HasUnresolvedHost(settings, out string where);
            if (blocked) _body.Add(BuildBlockedCard(where));

            _body.Add(BuildFactsCard(settings));
            _body.Add(BuildSplitCard(settings));
            _body.Add(BuildActions(settings, blocked));
        }

        private VisualElement BuildBlockedCard(string where)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");

            var title = new Label("This build cannot start");
            title.AddToClassList("hub-card-title");
            ApplyText(title, HealthState.Blocked);
            head.Add(title);
            card.Add(head);

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 6;
            body.style.paddingBottom = 8;

            var text = new Label(
                $"The active profile's {where} still contains an origin placeholder " +
                $"({CdnProfileManager.PathConvention.AcceptedPlaceholders}).\n\n" +
                "Building now would write a hostname that does not exist into the catalog. Every " +
                "player on that build would ask for it forever, and no later upload could change it.");
            text.AddToClassList("hub-note-text");
            body.Add(text);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 8;

            var go = new Button(() => _host?.Navigate(HubSections.Ids.Profiles)) { text = "Set the host" };
            go.AddToClassList("hub-btn");
            go.style.marginLeft = 0;
            actions.Add(go);

            var inject = new Button(InjectFromEnvironment) { text = "Read CDN_HOST from the environment" };
            inject.AddToClassList("hub-btn");
            actions.Add(inject);

            body.Add(actions);
            card.Add(body);
            return card;
        }

        private void InjectFromEnvironment()
        {
            try
            {
                var before = CdnProfileManager.InjectRemoteHostFromEnvironment();

                // The injector warns on its own when nothing matched, so this only reports the
                // outcome rather than restating it. Rebuilding is what actually proves it took.
                Debug.Log($"[Build] Injection ran against {before.Count} profile variable(s). " +
                          "The values above are re-read from the settings asset.");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Injection failed", ex.Message, "OK");
                Debug.LogException(ex);
            }
            finally
            {
                _health.Invalidate();
                Rebuild();
            }
        }

        private static VisualElement BuildFactsCard(AddressableAssetSettings settings)
        {
            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");
            var title = new Label("What this build would produce");
            title.AddToClassList("hub-card-title");
            head.Add(title);
            card.Add(head);

            string profileId = settings.activeProfileId;
            string profileName = settings.profileSettings.GetProfileName(profileId);

            string bundles = Evaluate(settings, profileId, AddressableAssetSettings.kRemoteLoadPath);
            string catalog = Evaluate(settings, profileId, CdnProfileManager.RemoteCatalogLoadPathVariable);

            card.Add(Fact("Profile", string.IsNullOrEmpty(profileName) ? "(none active)" : profileName,
                string.IsNullOrEmpty(profileName) ? HealthState.Blocked : HealthState.Ok));

            card.Add(Fact("Target", EditorUserBuildSettings.activeBuildTarget.ToString(), HealthState.Ok));

            card.Add(Fact("Bundles load from", bundles,
                CdnProfileManager.PathConvention.ContainsOriginPlaceholder(bundles)
                    ? HealthState.Blocked : HealthState.Ok));

            card.Add(Fact("Catalog loads from", catalog,
                CdnProfileManager.PathConvention.ContainsOriginPlaceholder(catalog)
                    ? HealthState.Blocked : HealthState.Ok));

            card.Add(BuildContentStateFact());

            return card;
        }

        /// <summary>Is there a baseline to patch from, and how old is it?</summary>
        /// <remarks>
        /// Age matters and a bare "present" would hide it: content_state.bin from a build three
        /// releases ago still produces a patch, just one that re-downloads most of the game.
        /// </remarks>
        private static VisualElement BuildContentStateFact()
        {
            try
            {
                var resolved = ContentStateManager.ResolvePath();
                if (resolved.IsFailure || string.IsNullOrEmpty(resolved.Value))
                    return Fact("Content state", "path could not be resolved", HealthState.NotMeasured);

                if (!System.IO.File.Exists(resolved.Value))
                {
                    return Fact("Content state", "none — the next release cannot be a patch",
                        HealthState.Warning);
                }

                var age = DateTime.Now - System.IO.File.GetLastWriteTime(resolved.Value);
                string ageText = age.TotalDays >= 1
                    ? $"{(int)age.TotalDays}d old"
                    : age.TotalHours >= 1
                        ? $"{(int)age.TotalHours}h old"
                        : $"{(int)age.TotalMinutes}m old";

                return Fact("Content state", ageText, HealthState.Ok);
            }
            catch (Exception ex)
            {
                return Fact("Content state", $"unreadable: {ex.Message}", HealthState.NotMeasured);
            }
        }

        /// <summary>The split between what ships in the player and what is fetched.</summary>
        private static VisualElement BuildSplitCard(AddressableAssetSettings settings)
        {
            int remote = 0, local = 0, unschemad = 0;

            foreach (var group in settings.groups)
            {
                if (group == null) continue;

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null)
                {
                    // A group with no bundled schema builds nothing. Counted separately rather than
                    // filed under "local", because "ships in the player" would be a lie about it.
                    unschemad++;
                    continue;
                }

                if (schema.BuildPath != null &&
                    schema.BuildPath.GetName(settings) == AddressableAssetSettings.kRemoteBuildPath)
                    remote++;
                else
                    local++;
            }

            var card = new VisualElement();
            card.AddToClassList("hub-card");

            var head = new VisualElement();
            head.AddToClassList("hub-card-header");
            var title = new Label("What ships where");
            title.AddToClassList("hub-card-title");
            head.Add(title);

            var count = new Label($"{remote + local + unschemad} group(s)");
            count.AddToClassList("hub-card-count");
            head.Add(count);
            card.Add(head);

            int total = remote + local + unschemad;

            if (total > 0)
            {
                // A proportional bar rather than three numbers: the question here is a ratio - how
                // much of the game is a download on first launch - and a ratio is read faster than
                // it is computed.
                var bar = new VisualElement();
                bar.AddToClassList("hub-splitbar");

                if (remote > 0) bar.Add(Segment("hub-splitbar-remote", remote, total));
                if (local > 0) bar.Add(Segment("hub-splitbar-local", local, total));
                if (unschemad > 0) bar.Add(Segment("hub-splitbar-none", unschemad, total));

                card.Add(bar);

                var legend = new VisualElement();
                legend.AddToClassList("hub-splitlegend");

                if (remote > 0)
                    legend.Add(LegendItem("hub-splitbar-remote", $"{remote} fetched from the CDN"));
                if (local > 0)
                    legend.Add(LegendItem("hub-splitbar-local", $"{local} inside the player"));
                if (unschemad > 0)
                    legend.Add(LegendItem("hub-splitbar-none", $"{unschemad} build nothing"));

                card.Add(legend);
            }

            var note = new Label(
                "Groups, not megabytes. Size needs a build to measure, and reporting an estimate here " +
                "as though it were measured is the habit this window exists to break.");
            note.AddToClassList("hub-rule-meta");
            note.style.paddingLeft = 8;
            note.style.paddingRight = 8;
            note.style.paddingBottom = 7;
            card.Add(note);

            return card;
        }

        private VisualElement BuildActions(AddressableAssetSettings settings, bool blocked)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 2;

            var blurb = new Label(
                "A full build replaces the catalog. An update keeps it and patches only what changed " +
                "since the content state above.");
            blurb.AddToClassList("hub-rule-meta");
            blurb.style.flexGrow = 1;
            row.Add(blurb);

            string profileName = settings.profileSettings.GetProfileName(settings.activeProfileId);

            var full = new Button(() => RunBuild(profileName, update: false)) { text = "Build content" };
            full.AddToClassList("hub-btn");
            full.SetEnabled(!blocked);
            full.tooltip = blocked ? "The active profile has an unresolved host." : string.Empty;
            row.Add(full);

            var update = new Button(() => RunBuild(profileName, update: true)) { text = "Build update" };
            update.AddToClassList("hub-btn");
            update.SetEnabled(!blocked);
            update.tooltip = blocked ? "The active profile has an unresolved host." : string.Empty;
            row.Add(update);

            return row;
        }

        private void RunBuild(string profileName, bool update)
        {
            // Named consequence, not "Are you sure?". A content build rewrites ServerData/ and, for a
            // full build, the catalog every existing player is about to be told to trust.
            bool go = EditorUtility.DisplayDialog(
                update ? "Build a content update?" : "Build all content?",
                update
                    ? $"Patches ServerData/ against the existing content state, using profile " +
                      $"'{profileName}'.\n\nThe catalog keeps its identity, so players already on this " +
                      "release receive only what changed."
                    : $"Rebuilds every bundle and the catalog with profile '{profileName}'.\n\n" +
                      "This replaces the content state, so it becomes the new baseline and the " +
                      "previous one can no longer be patched from. Archive it if you need it.",
                update ? "Build update" : "Build content", "Cancel");

            if (!go) return;

            try
            {
                var result = update
                    ? CdnBuildPipeline.BuildUpdate(profileName)
                    : CdnBuildPipeline.BuildFull(profileName);

                if (result.IsFailure)
                {
                    EditorUtility.DisplayDialog("The build did not run",
                        result.ErrorMessage + "\n\nNothing was published.", "OK");
                    Debug.LogError($"[Build] {result.ErrorMessage}");

                    HubHistory.Record(HistoryKind.Failed,
                        update ? "Content update refused" : "Content build refused",
                        profileName);
                }
                else
                {
                    Debug.Log($"[Build] {(update ? "Update" : "Full build")} finished with profile '{profileName}'.");

                    HubHistory.Record(HistoryKind.Ok,
                        update ? "Content update built" : "Content built",
                        $"{profileName} · {EditorUserBuildSettings.activeBuildTarget}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("The build threw", $"{ex.GetType().Name}: {ex.Message}", "OK");

                HubHistory.Record(HistoryKind.Failed,
                    update ? "Content update threw" : "Content build threw", ex.GetType().Name);
            }
            finally
            {
                _health.Invalidate();
                Rebuild();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static bool HasUnresolvedHost(AddressableAssetSettings settings, out string where)
        {
            string id = settings.activeProfileId;

            string bundles = settings.profileSettings.GetValueByName(id, AddressableAssetSettings.kRemoteLoadPath);
            if (CdnProfileManager.PathConvention.ContainsOriginPlaceholder(bundles))
            {
                where = "bundle load path";
                return true;
            }

            string catalog = settings.profileSettings.GetValueByName(id, CdnProfileManager.RemoteCatalogLoadPathVariable);
            if (CdnProfileManager.PathConvention.ContainsOriginPlaceholder(catalog))
            {
                where = "catalog load path";
                return true;
            }

            where = null;
            return false;
        }

        private static string Evaluate(AddressableAssetSettings settings, string profileId, string variable)
        {
            string raw = settings.profileSettings.GetValueByName(profileId, variable);
            if (string.IsNullOrEmpty(raw)) return "(not set)";

            // The RAW value, not the evaluated one, when it still holds a placeholder: evaluating it
            // would print the literal token anyway and the raw form is what the user has to edit.
            return CdnProfileManager.PathConvention.ContainsOriginPlaceholder(raw)
                ? raw
                : settings.profileSettings.EvaluateString(profileId, raw);
        }

        /// <summary>One slice of the split bar, sized by its share of the whole.</summary>
        /// <remarks>
        /// A minimum width so a single group out of hundreds is still visible. A slice rounded to
        /// nothing would be a category silently missing from the picture, which is worse than one
        /// drawn slightly too wide.
        /// </remarks>
        private static VisualElement Segment(string styleClass, int count, int total)
        {
            var segment = new VisualElement();
            segment.AddToClassList(styleClass);
            segment.style.flexGrow = count;
            segment.style.flexBasis = 0;
            segment.style.minWidth = 4;
            segment.tooltip = $"{count} of {total} group(s)";
            return segment;
        }

        private static VisualElement LegendItem(string styleClass, string text)
        {
            var item = new VisualElement();
            item.AddToClassList("hub-splitlegend-item");

            var swatch = new VisualElement();
            swatch.AddToClassList("hub-splitlegend-swatch");
            swatch.AddToClassList(styleClass);
            item.Add(swatch);

            var label = new Label(text);
            label.AddToClassList("hub-splitlegend-label");
            item.Add(label);

            return item;
        }

        private static VisualElement Fact(string key, string value, HealthState state)
        {
            var row = new VisualElement();
            row.AddToClassList("hub-prow");

            var k = new Label(key);
            k.AddToClassList("hub-pcol");
            k.style.width = 150;
            k.style.flexShrink = 0;
            row.Add(k);

            var v = new Label(value);
            v.AddToClassList("hub-pcol");
            v.style.flexGrow = 1;
            v.tooltip = value;
            if (state != HealthState.Ok) ApplyText(v, state);
            row.Add(v);

            return row;
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

        /// <summary>Colour a label by health state.</summary>
        /// <remarks>
        /// Delegates. This used to apply the dot classes and then clear style.backgroundColor inline
        /// to undo the half of them that does not belong on text - five sections carried a copy of
        /// that, and the copies had already drifted. HubStyle has the distinction instead.
        /// </remarks>
        private static void ApplyText(VisualElement element, HealthState state) =>
            HubStyle.Text(element, state);
    }
}
