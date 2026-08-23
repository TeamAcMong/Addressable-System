using System.Collections.Generic;

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
