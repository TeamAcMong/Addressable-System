using System.Collections.Generic;
using UnityEngine.UIElements;

namespace AddressableManager.Editor.Windows.Hub
{
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
