using AddressableManager.Cdn;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Reads the project's <see cref="CdnBuildMode"/> for editor-side code: the settings contract,
    /// the build pipeline and the verifier all have to agree on it.
    /// </summary>
    /// <remarks>
    /// No CdnSettings asset means LocalOnly, and that is not a fallback - it is the honest reading.
    /// The runtime CDN layer cannot function without that asset (CdnSettings.Load is what supplies
    /// every environment and the download policy), so a project without one is not publishing to a
    /// CDN whatever its Addressables settings happen to say. Defaulting to Remote instead would make
    /// a project that has never touched the CDN fail a contract it never opted into.
    /// </remarks>
    internal static class CdnBuildModes
    {
        /// <summary>The mode this project is configured for.</summary>
        internal static CdnBuildMode Current
        {
            get
            {
                var loaded = CdnSettings.Load();
                if (loaded.IsFailure || loaded.Value == null)
                    return CdnBuildMode.LocalOnly;

                return loaded.Value.BuildMode;
            }
        }

        /// <summary>True when the remote half of the configuration contract does not apply.</summary>
        internal static bool IsLocalOnly => Current == CdnBuildMode.LocalOnly;

        /// <summary>
        /// Prefix for a rule's ReadCurrent when the rule is inapplicable, so the validator shows why a
        /// rule is green rather than implying it was checked.
        /// </summary>
        internal const string NotApplicable = "(local-only: rule does not apply)";
    }
}
