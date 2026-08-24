using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AddressableManager.Editor.Cdn.Windows;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Hosts an existing <see cref="ICdnManagerTab"/> as a hub section.
    /// </summary>
    /// <remarks>
    /// An adapter rather than a rewrite, on purpose. The six CDN tabs are the most exercised Editor
    /// code in the package - they are what the Icon Match integration ran against - and porting them
    /// by hand into a new interface would put six working screens through a needless rewrite to gain
    /// nothing a delegating class does not already give.
    ///
    /// It also keeps <see cref="CdnManagerWindow"/> working unchanged during the transition, so the
    /// hub can ship section by section instead of as one flag day.
    /// </remarks>
    public sealed class CdnTabSection : IHubSection
    {
        private readonly ICdnManagerTab _tab;
        private readonly Func<SectionHealth> _health;

        /// <inheritdoc />
        public string Id { get; }

        /// <inheritdoc />
        public string Title { get; }

        /// <inheritdoc />
        public string Subtitle { get; }

        /// <inheritdoc />
        public PipelineStage Stage { get; }

        /// <summary>Wrap a CDN tab.</summary>
        /// <param name="id">Stable routing id, lowercase kebab-case.</param>
        /// <param name="title">Rail label. May differ from the tab's own short strip name.</param>
        /// <param name="subtitle">One line saying what this screen answers.</param>
        /// <param name="stage">Pipeline stage.</param>
        /// <param name="tab">The tab to host.</param>
        /// <param name="health">
        /// Optional health probe. When null the section reports
        /// <see cref="HealthState.NotMeasured"/> rather than <see cref="HealthState.Ok"/> - a section
        /// nobody has taught to measure itself has not passed, it simply has not been asked.
        /// </param>
        public CdnTabSection(
            string id,
            string title,
            string subtitle,
            PipelineStage stage,
            ICdnManagerTab tab,
            Func<SectionHealth> health = null)
        {
            Id = id;
            Title = title;
            Subtitle = subtitle;
            Stage = stage;
            _tab = tab;
            _health = health;
        }

        /// <inheritdoc />
        public SectionHealth GetHealth()
        {
            if (_health == null)
                return SectionHealth.NotMeasured("This screen does not report a health signal yet.");

            // A probe that throws must not take the rail - or the window - down with it. The rail
            // refreshes on a timer, so an exception here would otherwise repeat every second and
            // bury the Console under one section's failure.
            try
            {
                return _health();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AddressableManagerHub] Health probe for '{Id}' threw: {ex.Message}");
                return SectionHealth.NotMeasured("The check for this screen failed to run.");
            }
        }

        /// <summary>
        /// The stylesheet every CDN tab's markup is written against, and which only
        /// <see cref="CdnManagerWindow"/> used to supply.
        /// </summary>
        internal const string SharedStyleSheetPath =
            "Packages/com.game.addressables/Editor/Cdn/UI/CdnManagerWindow.uss";

        /// <inheritdoc />
        /// <remarks>
        /// The shared sheet is attached here because a tab does not attach it itself. Each tab loads
        /// only its OWN stylesheet; the classes its UXML shares with the other tabs - cdn-tab-page,
        /// cdn-preview-summary, cdn-preview-state, cdn-actions-row, cdn-btn - live in
        /// CdnManagerWindow.uss, which that window adds to its root once for all six.
        ///
        /// Hosting a tab outside that window therefore has to bring the sheet with it. Without it the
        /// tab still builds, still binds, still populates: nothing is null and nothing throws. What is
        /// lost is layout - cdn-preview-summary and cdn-preview-state lose flex-shrink: 0, the list's
        /// flex-grow: 1 squeezes them to nothing, and their text renders on top of the rows below,
        /// because overflow is visible by default. It reads as a rendering glitch rather than as a
        /// missing file, which is why it survived the section being opened and looked at.
        /// </remarks>
        public VisualElement CreateView()
        {
            var view = _tab.CreateView();
            if (view == null) return null;

            var shared = AssetDatabase.LoadAssetAtPath<StyleSheet>(SharedStyleSheetPath);

            if (shared == null)
            {
                // Loud. A silently missing stylesheet is exactly the failure this comment describes:
                // the screen appears, so nothing looks broken enough to investigate.
                Debug.LogError(
                    $"[AddressableManagerHub] '{Id}' could not load {SharedStyleSheetPath}. The tab " +
                    "will render with its shared layout classes unstyled - overlapping text and a " +
                    "collapsed header.");
                return view;
            }

            // Guard against a host that already attached it. Adding the same sheet twice is not fatal,
            // but it makes the resolved style order depend on how many times it was added.
            if (!view.styleSheets.Contains(shared))
                view.styleSheets.Add(shared);

            return view;
        }

        /// <inheritdoc />
        public void OnShown() => _tab.OnShown();
    }
}
