using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace AddressableManager.Editor.Cdn
{
    /// <summary>
    /// Creates and validates the four CDN profiles (Local, Dev, Staging, Prod) and manages the
    /// dedicated catalog-path profile variables, implementing the infrastructure layout from design doc §2.
    /// </summary>
    /// <remarks>
    /// Two profile variables are created for catalogs, distinct from the bundle path variables:
    /// - <see cref="RemoteCatalogBuildPathVariable"/> — where catalogs are built (per-app-version folder)
    /// - <see cref="RemoteCatalogLoadPathVariable"/> — where catalogs are loaded (per-app-version folder)
    ///
    /// These are joined by the existing Remote.BuildPath/Remote.LoadPath variables used for bundles.
    /// The separation is critical: bundles are immutable, content-hashed, and cached for a year;
    /// catalogs are mutable, per-app-version, and must not be cached long. One cache policy cannot
    /// serve both (infra §2, task 0.6).
    ///
    /// Environment switching requires no rebuild because <see cref="InternalIdTransformFunc"/> rewrites
    /// the host at runtime. The profile determines the fallback baked into the catalog, not the final URL.
    /// </remarks>
    public static class CdnProfileManager
    {
        /// <summary>
        /// Dedicated profile variable for catalog build paths. All catalogs build under
        /// <c>ServerData/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]</c>,
        /// independent of bundle build paths.
        /// </summary>
        public const string RemoteCatalogBuildPathVariable = "Remote.CatalogBuildPath";

        /// <summary>
        /// Dedicated profile variable for catalog load paths. Must point at a per-app-version folder so
        /// one app version's catalog does not affect another's. <see cref="InternalIdTransformFunc"/>
        /// rewrites the host at runtime; this variable holds the fallback URL baked into the catalog.
        /// </summary>
        public const string RemoteCatalogLoadPathVariable = "Remote.CatalogLoadPath";

        /// <summary>
        /// Default values for catalog paths per profile. The <c>[UnityEditor.PlayerSettings.bundleVersion]</c> token
        /// is evaluated by <see cref="AddressablesRuntimeProperties.EvaluateString"/> using reflection to access the property.
        /// </summary>
        private static class CatalogPathDefaults
        {
            /// <summary>All catalogs build to a per-app-version folder. This is stable across all profiles.</summary>
            public const string BuildPathValue = "ServerData/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]";

            /// <summary>Local profile: localhost on a dev machine.</summary>
            public const string LocalLoadPathValue = "http://localhost:8080/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]";

            /// <summary>Dev profile: development CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string DevLoadPathValue = "https://cdn-dev.<domain>/game/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]";

            /// <summary>Staging profile: staging CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string StagingLoadPathValue = "https://cdn-stg.<domain>/game/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]";

            /// <summary>Production profile: production CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string ProdLoadPathValue = "https://cdn.<domain>/game/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]";
        }

        /// <summary>
        /// Bundle path defaults (the existing Remote.BuildPath / Remote.LoadPath variables).
        /// Bundles are immutable and content-addressed, so they live in a shared folder, not a per-app-version one.
        /// </summary>
        private static class BundlePathDefaults
        {
            /// <summary>All bundles build to a shared folder regardless of app version.</summary>
            public const string BuildPathValue = "ServerData/[BuildTarget]/bundles";

            /// <summary>Local profile: localhost on a dev machine.</summary>
            public const string LocalLoadPathValue = "http://localhost:8080/[BuildTarget]/bundles";

            /// <summary>Dev profile: development CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string DevLoadPathValue = "https://cdn-dev.<domain>/game/[BuildTarget]/bundles";

            /// <summary>Staging profile: staging CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string StagingLoadPathValue = "https://cdn-stg.<domain>/game/[BuildTarget]/bundles";

            /// <summary>Production profile: production CDN. Requires env var injection at runtime; this is the fallback.</summary>
            public const string ProdLoadPathValue = "https://cdn.<domain>/game/[BuildTarget]/bundles";
        }

        /// <summary>
        /// The four profiles required by design doc §9, task 0.5.
        /// </summary>
        private static class ProfileNames
        {
            public const string Local = "Local";
            public const string Dev = "Dev";
            public const string Staging = "Staging";
            public const string Prod = "Prod";
        }

        /// <summary>
        /// Create or validate the four profiles and their variables. If any profile is missing,
        /// this method creates it from scratch. If a profile exists, its variables are validated
        /// but not overwritten.
        /// </summary>
        /// <remarks>
        /// This is idempotent: calling it multiple times has the same effect as calling it once.
        /// It does NOT overwrite existing variable values — manual edits are preserved.
        /// </remarks>
        public static void EnsureProfilesExist()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "CdnProfileManager.EnsureProfilesExist: no AddressableAssetSettings found. " +
                    "Open the Addressables Groups window to create one.");
            }

            // Ensure the dedicated catalog variables exist (created once, never deleted).
            EnsureCatalogVariablesExist(settings);

            // Ensure all four profiles exist and are populated.
            EnsureProfile(settings, ProfileNames.Local);
            EnsureProfile(settings, ProfileNames.Dev);
            EnsureProfile(settings, ProfileNames.Staging);
            EnsureProfile(settings, ProfileNames.Prod);

            EditorUtility.SetDirty(settings);
        }

        /// <summary>
        /// Switch the active profile to one of the four ring profiles by name.
        /// </summary>
        /// <param name="profileName">One of "Local", "Dev", "Staging", or "Prod".</param>
        /// <exception cref="ArgumentException">If profileName does not name an existing profile.</exception>
        public static void SetActiveProfile(string profileName)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "CdnProfileManager.SetActiveProfile: no AddressableAssetSettings found.");
            }

            string profileId = settings.profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(profileId))
            {
                throw new ArgumentException(
                    $"CdnProfileManager.SetActiveProfile: profile '{profileName}' does not exist. " +
                    $"Call EnsureProfilesExist() first.",
                    nameof(profileName));
            }

            settings.activeProfileId = profileId;
            EditorUtility.SetDirty(settings);
        }

        /// <summary>
        /// Validate that the project's profiles and catalog variables are correctly configured.
        /// </summary>
        /// <returns>A list of validation errors (empty if all checks pass).</returns>
        /// <remarks>
        /// Used by CI (CatalogVerifier, task 1.6) to gate content builds. This method returns errors
        /// as strings rather than throwing, so a CI caller can collect all errors and report them together.
        /// </remarks>
        public static List<string> ValidateProfiles()
        {
            var errors = new List<string>();
            var settings = AddressableAssetSettingsDefaultObject.Settings;

            if (settings == null)
            {
                errors.Add("No AddressableAssetSettings found.");
                return errors;
            }

            // Check for the two dedicated catalog variables.
            if (settings.profileSettings.GetProfileDataByName(RemoteCatalogBuildPathVariable) == null)
            {
                errors.Add($"Catalog profile variable '{RemoteCatalogBuildPathVariable}' does not exist.");
            }

            if (settings.profileSettings.GetProfileDataByName(RemoteCatalogLoadPathVariable) == null)
            {
                errors.Add($"Catalog profile variable '{RemoteCatalogLoadPathVariable}' does not exist.");
            }

            // Check for the four profiles.
            foreach (string profileName in new[] { ProfileNames.Local, ProfileNames.Dev, ProfileNames.Staging, ProfileNames.Prod })
            {
                if (string.IsNullOrEmpty(settings.profileSettings.GetProfileId(profileName)))
                {
                    errors.Add($"Profile '{profileName}' does not exist.");
                }
            }

            return errors;
        }

        /// <summary>
        /// Inject the remote CDN host from an environment variable, overriding the built-in fallback
        /// for both bundle and catalog load paths. This is called from InternalIdTransformFunc at runtime
        /// to support environment switching without a rebuild.
        /// </summary>
        /// <param name="envVarName">The environment variable name (e.g., "CDN_HOST") to read.</param>
        /// <param name="profileId">The profile ID of the active profile.</param>
        /// <remarks>
        /// This is an Editor-only method. At runtime, <see cref="InternalIdTransformFunc"/> consumes
        /// an injected host string; here we provide the mechanism to set it from the environment.
        ///
        /// The host must not include the path — e.g., <c>https://cdn.example.com</c>, not
        /// <c>https://cdn.example.com/game</c>. The path is supplied by the profile variables.
        /// </remarks>
        public static string GetRemoteHostFromEnvironment(string envVarName)
        {
            string host = Environment.GetEnvironmentVariable(envVarName);
            if (string.IsNullOrEmpty(host))
            {
                return null; // Fall back to profile-baked URL
            }

            // Normalize: strip trailing slash so callers can write either form.
            return host.TrimEnd('/');
        }

        // ========== private implementation ==========

        private static void EnsureCatalogVariablesExist(AddressableAssetSettings settings)
        {
            // Create the two catalog variables if they don't exist. If they do exist, leave them untouched.
            if (settings.profileSettings.GetProfileDataByName(RemoteCatalogBuildPathVariable) == null)
            {
                settings.profileSettings.CreateValue(
                    RemoteCatalogBuildPathVariable,
                    CatalogPathDefaults.BuildPathValue);
            }

            if (settings.profileSettings.GetProfileDataByName(RemoteCatalogLoadPathVariable) == null)
            {
                settings.profileSettings.CreateValue(
                    RemoteCatalogLoadPathVariable,
                    CatalogPathDefaults.LocalLoadPathValue); // Default to Local; will be overwritten per profile
            }
        }

        private static void EnsureProfile(AddressableAssetSettings settings, string profileName)
        {
            string profileId = settings.profileSettings.GetProfileId(profileName);

            // If profile already exists, just validate its variables exist; don't overwrite values.
            if (!string.IsNullOrEmpty(profileId))
            {
                EnsureProfileVariablesExist(settings, profileId);
                return;
            }

            // Profile doesn't exist; create it.
            // AddProfile(name, copyFromId) will use the default profile if copyFromId is null
            profileId = settings.profileSettings.AddProfile(profileName, null);

            // Set the two catalog variables for this profile.
            settings.profileSettings.SetValue(profileId, RemoteCatalogBuildPathVariable, CatalogPathDefaults.BuildPathValue);

            switch (profileName)
            {
                case ProfileNames.Local:
                    settings.profileSettings.SetValue(profileId, RemoteCatalogLoadPathVariable, CatalogPathDefaults.LocalLoadPathValue);
                    settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, BundlePathDefaults.LocalLoadPathValue);
                    break;

                case ProfileNames.Dev:
                    settings.profileSettings.SetValue(profileId, RemoteCatalogLoadPathVariable, CatalogPathDefaults.DevLoadPathValue);
                    settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, BundlePathDefaults.DevLoadPathValue);
                    break;

                case ProfileNames.Staging:
                    settings.profileSettings.SetValue(profileId, RemoteCatalogLoadPathVariable, CatalogPathDefaults.StagingLoadPathValue);
                    settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, BundlePathDefaults.StagingLoadPathValue);
                    break;

                case ProfileNames.Prod:
                    settings.profileSettings.SetValue(profileId, RemoteCatalogLoadPathVariable, CatalogPathDefaults.ProdLoadPathValue);
                    settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteLoadPath, BundlePathDefaults.ProdLoadPathValue);
                    break;

                default:
                    throw new ArgumentException($"Unknown profile name: {profileName}", nameof(profileName));
            }

            // Ensure bundle build paths are set (they should inherit from Default, but make them explicit).
            settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kRemoteBuildPath, BundlePathDefaults.BuildPathValue);
        }

        private static void EnsureProfileVariablesExist(AddressableAssetSettings settings, string profileId)
        {
            // This method checks that a profile has both catalog variables. If they're missing (e.g., because
            // an old profile was created before catalog variables existed), we add them. We do NOT overwrite
            // existing values — manual edits are preserved.

            if (string.IsNullOrEmpty(settings.profileSettings.GetValueByName(profileId, RemoteCatalogBuildPathVariable)))
            {
                settings.profileSettings.SetValue(profileId, RemoteCatalogBuildPathVariable, CatalogPathDefaults.BuildPathValue);
            }

            if (string.IsNullOrEmpty(settings.profileSettings.GetValueByName(profileId, RemoteCatalogLoadPathVariable)))
            {
                // This should not happen in normal usage (EnsureCatalogVariablesExist ran first), but handle it gracefully.
                settings.profileSettings.SetValue(profileId, RemoteCatalogLoadPathVariable, CatalogPathDefaults.LocalLoadPathValue);
            }
        }
    }
}
