using System.Collections.Generic;
using UnityEditor;
using AddressableManager.Editor.Cdn;
using AddressableManager.Editor.Cdn.Windows.Tabs;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// The sections the hub shows, in pipeline order. <b>Single source of truth.</b>
    /// </summary>
    /// <remarks>
    /// One list, built once, used by the window, by any deep-link menu item and by the batchmode
    /// smoke check. The package already learned this lesson once: <c>CdnManagerWindow</c> carries a
    /// comment explaining that its tab list lives in one method precisely so the probe CLI exercises
    /// the tabs the window shows, because a second hand-written list drifts and the entry it forgets
    /// is the one nobody ever probes.
    ///
    /// Sections are added HERE and nowhere else.
    /// </remarks>
    public static class HubSections
    {
        /// <summary>Routing ids. Constants rather than literals so a rename is a compile error.</summary>
        public static class Ids
        {
            /// <summary>The pipeline overview - the section a fresh window opens on.</summary>
            public const string Overview = "overview";

            /// <summary>Settings contract validation.</summary>
            public const string Validator = "validator";

            /// <summary>Profile conformance: origin, suffix and whether the runtime knows it.</summary>
            public const string Profiles = "profiles";

            /// <summary>Layout rules, with a dry run in front of the apply.</summary>
            public const string Rules = "rules";

            /// <summary>Local content server.</summary>
            public const string LocalServer = "local-server";

            /// <summary>What a returning player would download.</summary>
            public const string UpdatePreview = "update-preview";

            /// <summary>Content build.</summary>
            public const string Build = "build";

            /// <summary>Read a built or published catalog.</summary>
            public const string Catalog = "catalog";

            /// <summary>What the running player is doing.</summary>
            public const string RuntimeMonitor = "runtime-monitor";

            /// <summary>What is loaded, and which scope is holding it.</summary>
            public const string AssetLifetime = "asset-lifetime";
        }

        /// <summary>
        /// Cheap health signals for the sections that are still hosted CDN tabs.
        /// </summary>
        /// <remarks>
        /// Every one of these runs once a second for as long as the window is open, so each is a file
        /// existence check or a field read - never a scan, a parse or a network call. Anything that
        /// costs more than that belongs behind a button on the section itself and reports
        /// <see cref="HealthState.NotMeasured"/> until someone presses it.
        ///
        /// They report what they can actually see. None of them claims a build is GOOD - only that an
        /// artifact exists or does not. "A manifest is on disk" and "the content in it is correct" are
        /// different statements, and this window only makes the first one.
        /// </remarks>
        internal static class Probes
        {
            private static string ManifestPath => System.IO.Path.Combine(
                BuildManifestWriter.OutputRootDir, BuildManifestWriter.ManifestFileName);

            /// <summary>Has anything been built into this workspace at all?</summary>
            public static SectionHealth Build()
            {
                if (CdnBuildModes.IsLocalOnly)
                    return SectionHealth.NotMeasured("Build Mode is Local-only; there is no remote output to check.");

                return System.IO.File.Exists(ManifestPath)
                    ? SectionHealth.Ok()
                    : SectionHealth.NotMeasured(
                        "No build manifest in ServerData/, so no content build has run in this workspace.");
            }

            /// <summary>Is there a content state to patch from?</summary>
            /// <remarks>
            /// content_state.bin is the one build artifact that cannot be regenerated. Without it no
            /// future build can produce a patch - only a full re-download for every player - so its
            /// absence is worth a badge rather than a silent zero.
            /// </remarks>
            public static SectionHealth UpdatePreview()
            {
                if (CdnBuildModes.IsLocalOnly)
                    return SectionHealth.NotMeasured("Build Mode is Local-only; there is nothing to patch.");

                var resolved = ContentStateManager.ResolvePath();
                if (resolved.IsFailure || string.IsNullOrEmpty(resolved.Value))
                    return SectionHealth.NotMeasured("The content state path could not be resolved.");

                return System.IO.File.Exists(resolved.Value)
                    ? SectionHealth.Ok()
                    : SectionHealth.Warning(
                        "no content state",
                        "There is no addressables_content_state.bin, so the next release cannot be a " +
                        "patch - every player would re-download everything.");
            }

            /// <summary>Nothing is known about a published catalog without going and asking.</summary>
            /// <remarks>
            /// Deliberately always NotMeasured. Reaching a CDN is a network call, and a network call
            /// on a one-second timer is not a health probe, it is a denial of service against your own
            /// origin. The Catalog Inspector has a button for it.
            /// </remarks>
            public static SectionHealth Catalog() =>
                SectionHealth.NotMeasured(
                    "Nothing here has contacted the published catalog. Open this screen and check it.");

            /// <summary>Is the local server up?</summary>
            public static SectionHealth LocalServer()
            {
                var server = LocalContentServerMenu.Instance;
                if (server == null)
                    return SectionHealth.NotMeasured("The local server has not been created in this session.");

                return server.IsRunning
                    ? SectionHealth.Ok($":{server.ActivePort}")
                    : SectionHealth.NotMeasured("The local server is not running.");
            }

            /// <summary>What the player is doing - which is nothing at all outside play mode.</summary>
            public static SectionHealth RuntimeMonitor()
            {
                if (!EditorApplication.isPlaying)
                    return SectionHealth.NotMeasured("Not in play mode, so there is no runtime to observe.");

                return AddressableManager.Cdn.CdnManager.IsInitialized
                    ? SectionHealth.Ok()
                    : SectionHealth.NotMeasured("The CDN layer has not initialised in this play session.");
            }
        }

        /// <summary>
        /// Build the section list. A fresh list on every call, because sections hold view state and the
        /// window must not share instances with a probe running in the same domain.
        /// </summary>
        public static List<IHubSection> Create()
        {
            return new List<IHubSection>
            {
                new OverviewSection(),

                // A section of its own rather than the old tab: the tab listed all fifty-eight
                // rules flat and sorted by id, which is the maintainer's ordering rather than the
                // user's. SettingsValidatorTab stays for CdnManagerWindow until that window retires.
                new ValidatorSection(),
                new ProfilesSection(),
                new RulesSection(),

                new CdnTabSection(
                    Ids.UpdatePreview,
                    "Update Preview",
                    "What a returning player would download",
                    PipelineStage.Build,
                    new UpdatePreviewTab(),
                    Probes.UpdatePreview),

                new CdnTabSection(
                    Ids.Build,
                    "Build",
                    "Produce bundles and a catalog for the active profile",
                    PipelineStage.Build,
                    new BuildTab(),
                    Probes.Build),

                new CdnTabSection(
                    Ids.Catalog,
                    "Catalog Inspector",
                    "Read a built or published catalog",
                    PipelineStage.Publish,
                    new CatalogInspectorTab(),
                    Probes.Catalog),

                new CdnTabSection(
                    Ids.LocalServer,
                    "Local Server",
                    "Serve ServerData/ on localhost, and watch what asks for it",
                    PipelineStage.Publish,
                    new LocalServerTab(),
                    Probes.LocalServer),

                new CdnTabSection(
                    Ids.RuntimeMonitor,
                    "Runtime Monitor",
                    "What the running player is doing",
                    PipelineStage.Run,
                    new RuntimeMonitorTab(),
                    Probes.RuntimeMonitor),

                new AssetLifetimeSection(),
            };
        }
    }
}
