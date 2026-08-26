using System.Collections.Generic;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
    /// <summary>
    /// Implemented by a section whose rail entry reads differently from its header.
    /// </summary>
    /// <remarks>
    /// The rail is a column 196px wide holding eleven entries, so its labels have to be short nouns.
    /// The header has a whole row and can afford the word that actually describes the screen. The
    /// design uses both for the same section - "Validator" on the rail, "Configuration" above the
    /// content - and collapsing them to one string means either the rail wraps or the header
    /// under-describes.
    ///
    /// Optional, like <see cref="IHubSectionActions"/>: a section that does not implement it uses its
    /// <see cref="IHubSection.Title"/> in both places, which is right for the other ten.
    /// </remarks>
    public interface IHubRailLabel
    {
        /// <summary>Short label for the rail entry.</summary>
        string RailLabel { get; }
    }

    /// <summary>
    /// What the shell offers a section that needs to know about the others.
    /// </summary>
    /// <remarks>
    /// Exists for one screen - the overview - and is deliberately tiny. The overview's job is to say
    /// where the pipeline stops, which means it has to read every other section's verdict; the
    /// alternative is a second list of "things that can be wrong", maintained by hand, which would
    /// drift from the sections themselves within a release. This package has already paid for that
    /// mistake more than once.
    ///
    /// Sections do NOT use this to reach into each other. It carries exactly two things: the section
    /// list, read-only, and a way to navigate.
    /// </remarks>
    public interface IHubHost
    {
        /// <summary>Every registered section, in rail order.</summary>
        IReadOnlyList<IHubSection> Sections { get; }

        /// <summary>Show a section by <see cref="IHubSection.Id"/>.</summary>
        void Navigate(string sectionId);

        /// <summary>
        /// Rebuild the header buttons for the section on screen.
        /// </summary>
        /// <remarks>
        /// The shell fills that area once, during navigation. A section whose button carries a
        /// COUNT - "Release leaked (3)" - therefore goes stale the moment the count moves, even
        /// while its own body is refreshing correctly underneath. Half a screen updating is worse
        /// than none of it, because the half that stopped looks authoritative.
        ///
        /// Call it only when the answer actually changed. Rebuilding the row on every tick would
        /// destroy and recreate a button under a cursor that might be on its way to clicking it.
        /// </remarks>
        void RefreshHeaderActions();
    }

    /// <summary>
    /// Implemented by a section that wants buttons in the window's section header.
    /// </summary>
    /// <remarks>
    /// Opt-in, and the shell HIDES the container when nothing fills it. The header's action area was
    /// queried, cleared on every navigation, and never added to by anything — an empty box drawn
    /// forever, which is the same dead-UI defect this window was built to remove and which I managed
    /// to reintroduce while building it. A container that can be empty must be able to disappear.
    ///
    /// A section puts an action here when it is the screen's primary verb and should stay reachable
    /// while the body is scrolled. Everything else belongs next to the thing it acts on.
    /// </remarks>
    public interface IHubSectionActions
    {
        /// <summary>Add buttons to <paramref name="container"/>. Adding none is fine.</summary>
        void PopulateHeaderActions(VisualElement container);
    }

    /// <summary>
    /// Implemented by a section that needs the shell. The hub binds these right after construction.
    /// </summary>
    /// <remarks>
    /// A separate interface rather than a parameter on <see cref="IHubSection"/> so the six adapted
    /// CDN tabs - which want nothing from the shell - are not made to carry a dependency they do not
    /// use.
    /// </remarks>
    public interface IHubHostAware
    {
        /// <summary>Called once, before the section is first shown.</summary>
        void Bind(IHubHost host);
    }
}
