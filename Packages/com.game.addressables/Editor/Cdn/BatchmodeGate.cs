using UnityEditor;
using UnityEngine;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// The check every batchmode entry point makes before it is allowed to end the Editor.
    /// </summary>
    /// <remarks>
    /// <c>EditorApplication.Exit</c> closes the Editor <b>without the prompt Quit gives</b>, so
    /// unsaved scene changes go with it. In batchmode that is exactly right - it is how a CLI
    /// reports its status. Reached any other way it destroys someone's session, and reads as a
    /// crash rather than an exit, because nothing in Unity announces itself closing on purpose.
    ///
    /// That is not hypothetical here. An entry point invoked from a live session through an
    /// automation bridge closed the Editor mid-session, and a button wired to another one closed it
    /// again two releases later - the second time in the very release that guarded the first.
    ///
    /// One helper rather than a copy in each of the nine files that call Exit. Nine copies is how
    /// one of them ends up subtly different from the others.
    /// </remarks>
    public static class BatchmodeGate
    {
        /// <summary>
        /// True when this process may end the Editor. Logs the refusal when it may not.
        /// </summary>
        /// <param name="tag">Log prefix of the caller, e.g. "CdnBuildCLI".</param>
        /// <param name="alternative">What the reader should do instead, in a sentence.</param>
        public static bool MayExit(string tag, string alternative = null)
        {
            if (Application.isBatchMode) return true;

            Debug.LogError(
                $"[{tag}] This is a batchmode entry point: it ends the Editor to report its status, " +
                "and would close this session without offering to save. Refused. " +
                (alternative ?? "Run it with -batchmode -executeMethod instead."));

            return false;
        }
    }
}
