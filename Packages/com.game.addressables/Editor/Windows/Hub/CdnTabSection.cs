using System;
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

        /// <inheritdoc />
        public VisualElement CreateView() => _tab.CreateView();

        /// <inheritdoc />
        public void OnShown() => _tab.OnShown();
    }
}
