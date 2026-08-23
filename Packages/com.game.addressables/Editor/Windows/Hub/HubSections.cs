using System.Collections.Generic;
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

                new CdnTabSection(
                    Ids.Validator,
                    "Validator",
                    "Every rule the settings contract checks, grouped by what it costs you",
                    PipelineStage.Configure,
                    new SettingsValidatorTab()),

                new CdnTabSection(
                    Ids.UpdatePreview,
                    "Update Preview",
                    "What a returning player would download",
                    PipelineStage.Build,
                    new UpdatePreviewTab()),

                new CdnTabSection(
                    Ids.Build,
                    "Build",
                    "Produce bundles and a catalog for the active profile",
                    PipelineStage.Build,
                    new BuildTab()),

                new CdnTabSection(
                    Ids.Catalog,
                    "Catalog Inspector",
                    "Read a built or published catalog",
                    PipelineStage.Publish,
                    new CatalogInspectorTab()),

                new CdnTabSection(
                    Ids.LocalServer,
                    "Local Server",
                    "Serve ServerData/ on localhost, and watch what asks for it",
                    PipelineStage.Publish,
                    new LocalServerTab()),

                new CdnTabSection(
                    Ids.RuntimeMonitor,
                    "Runtime Monitor",
                    "What the running player is doing",
                    PipelineStage.Run,
                    new RuntimeMonitorTab()),
            };
        }
    }
}
