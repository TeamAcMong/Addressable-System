using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// One screen inside <see cref="AddressableManagerHub"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately close to the existing <c>ICdnManagerTab</c> so the six CDN tabs move across with
    /// no changes to their bodies (see <see cref="CdnTabSection"/>), with three additions the shell
    /// needs and a tab strip did not: a stable <see cref="Id"/> for routing, the
    /// <see cref="Stage"/> the section belongs to, and <see cref="GetHealth"/> so the rail can show
    /// where the pipeline stops.
    ///
    /// Like the tab contract it replaces, <see cref="CreateView"/> is called fresh every time the
    /// section becomes visible, so a section never tracks its own staleness - it reads live state
    /// each time. Anything long-lived (a running server, a poll loop) belongs in an object that
    /// outlives the <see cref="VisualElement"/>, torn down from a TrickleDown
    /// <c>DetachFromPanelEvent</c> on the section's own root rather than from a shell hook.
    /// </remarks>
    public interface IHubSection
    {
        /// <summary>
        /// Stable identifier used for routing, deep links and the remembered section.
        /// </summary>
        /// <remarks>
        /// Must not change once shipped: it is persisted in <c>SessionState</c> and is what a menu
        /// item passes to open the window at a particular screen. Use lowercase kebab-case.
        /// </remarks>
        string Id { get; }

        /// <summary>Name on the rail and in the section header, e.g. "Validator".</summary>
        string Title { get; }

        /// <summary>
        /// One line under the title saying what this screen answers, in the user's terms.
        /// </summary>
        /// <remarks>
        /// Not decoration. Fourteen sections is past the point where a bare noun tells anyone what
        /// they are looking at, and this is the cheapest place to say it.
        /// </remarks>
        string Subtitle { get; }

        /// <summary>Which pipeline stage this section belongs to.</summary>
        PipelineStage Stage { get; }

        /// <summary>
        /// Health for the rail, re-derived on every refresh.
        /// </summary>
        /// <remarks>
        /// <b>Must be cheap and must not throw.</b> It runs for every section on a timer while the
        /// window is open, so anything expensive belongs behind an explicit action on the section
        /// itself, reported here as <see cref="SectionHealth.NotMeasured"/> until the user runs it.
        /// Returning a stale-but-green value rather than <c>NotMeasured</c> is the exact failure this
        /// window exists to stop.
        /// </remarks>
        SectionHealth GetHealth();

        /// <summary>Build this section's content. Called fresh each time it becomes visible.</summary>
        VisualElement CreateView();

        /// <summary>Called once the view is attached under the shell. Run the first refresh here.</summary>
        void OnShown();
    }
}
