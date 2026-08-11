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
        /// <remarks>
        /// internal rather than private so the Build tab's profile dropdown lists exactly the
        /// profiles this manager creates. A hand-typed list in the UI would drift the first time a
        /// profile is renamed, and the symptom would be a dropdown entry that fails to activate.
        /// </remarks>
        internal static class ProfileNames
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
        /// Read the CDN domain from an environment variable, with normalization.
        /// </summary>
        /// <param name="envVarName">The environment variable name to read.</param>
        /// <returns>The value with trailing slashes stripped, or null if the variable is unset.</returns>
        /// <remarks>
        /// This is a utility for reading the env var and normalizing it. The actual host injection
        /// into profiles is performed by <see cref="InjectRemoteHostFromEnvironment"/>.
        ///
        /// The domain is used to replace <domain> placeholders in profile paths.
        /// Example: if the variable contains "example.com", it will replace <domain> in
        /// "https://cdn-dev.<domain>/game/..." to produce "https://cdn-dev.example.com/game/...".
        /// </remarks>
        public static string GetRemoteHostFromEnvironment(string envVarName)
        {
            string host = Environment.GetEnvironmentVariable(envVarName);
            if (string.IsNullOrEmpty(host))
            {
                return null; // Variable not set
            }

            // Normalize: strip trailing slash so callers can write either form.
            return host.TrimEnd('/');
        }

        /// <summary>
        /// Temporarily inject the CDN domain from an environment variable, replacing <domain>
        /// placeholders in the active profile's Remote.LoadPath and Remote.CatalogLoadPath.
        /// This is scoped to the current build: the old values are returned so the caller
        /// can revert them after BuildPlayerContent completes.
        /// </summary>
        /// <param name="envVarName">Environment variable name to read (default "CDN_HOST")</param>
        /// <param name="validatePlaceholders">If true, throw when env var is unset and <domain> placeholders remain</param>
        /// <returns>Dictionary of old values (keys: RemoteCatalogLoadPathVariable, kRemoteLoadPath)
        /// for reverting via <see cref="RevertRemoteHostInjection"/></returns>
        /// <remarks>
        /// The injection modifies the in-memory AddressableAssetSettings but does NOT persist to disk
        /// (does not call EditorUtility.SetDirty). When BuildPlayerContent runs, it reads these
        /// modified values. The caller MUST revert via <see cref="RevertRemoteHostInjection"/> after
        /// the build completes, typically in a finally block, to ensure the profile is restored
        /// even if the build fails.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown if no settings found, no active profile set, or env var unset with placeholders remaining</exception>
        public static Dictionary<string, string> InjectRemoteHostFromEnvironment(
            string envVarName = "CDN_HOST",
            bool validatePlaceholders = true)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "CdnProfileManager.InjectRemoteHostFromEnvironment: no AddressableAssetSettings found. " +
                    "Open the Addressables Groups window to create one.");
            }

            string activeProfileId = settings.activeProfileId;
            if (string.IsNullOrEmpty(activeProfileId))
            {
                throw new InvalidOperationException(
                    "CdnProfileManager.InjectRemoteHostFromEnvironment: no active profile set. " +
                    "Call SetActiveProfile(name) first.");
            }

            // Read the environment variable
            string domain = GetRemoteHostFromEnvironment(envVarName);

            // If unset, check for placeholders and throw if present
            if (string.IsNullOrEmpty(domain))
            {
                if (validatePlaceholders)
                {
                    string existingCatalogPath = settings.profileSettings.GetValueByName(
                        activeProfileId, RemoteCatalogLoadPathVariable) ?? string.Empty;
                    string existingBundlePath = settings.profileSettings.GetValueByName(
                        activeProfileId, AddressableAssetSettings.kRemoteLoadPath) ?? string.Empty;

                    bool hasCatalogPlaceholder = existingCatalogPath.Contains("<domain>");
                    bool hasBundlePlaceholder = existingBundlePath.Contains("<domain>");

                    if (hasCatalogPlaceholder || hasBundlePlaceholder)
                    {
                        throw new InvalidOperationException(
                            $"CdnProfileManager.InjectRemoteHostFromEnvironment: environment variable '{envVarName}' is not set, " +
                            $"but the active profile's load paths contain <domain> placeholders. " +
                            $"Set {envVarName} to the domain you want to inject (e.g., {envVarName}=example.com) and retry.");
                    }
                }

                // No injection needed; return empty dict (revert will be a no-op)
                return new Dictionary<string, string>();
            }

            // Store old values for revert
            var oldValues = new Dictionary<string, string>
            {
                { RemoteCatalogLoadPathVariable, settings.profileSettings.GetValueByName(activeProfileId, RemoteCatalogLoadPathVariable) ?? string.Empty },
                { AddressableAssetSettings.kRemoteLoadPath, settings.profileSettings.GetValueByName(activeProfileId, AddressableAssetSettings.kRemoteLoadPath) ?? string.Empty }
            };

            // Replace <domain> in catalog load path
            string catalogPath = oldValues[RemoteCatalogLoadPathVariable];
            if (!string.IsNullOrEmpty(catalogPath) && catalogPath.Contains("<domain>"))
            {
                string newCatalogPath = catalogPath.Replace("<domain>", domain);
                settings.profileSettings.SetValue(activeProfileId, RemoteCatalogLoadPathVariable, newCatalogPath);
            }

            // Replace <domain> in bundle load path
            string bundlePath = oldValues[AddressableAssetSettings.kRemoteLoadPath];
            if (!string.IsNullOrEmpty(bundlePath) && bundlePath.Contains("<domain>"))
            {
                string newBundlePath = bundlePath.Replace("<domain>", domain);
                settings.profileSettings.SetValue(activeProfileId, AddressableAssetSettings.kRemoteLoadPath, newBundlePath);
            }

            return oldValues;
        }

        /// <summary>
        /// Revert the effects of <see cref="InjectRemoteHostFromEnvironment"/>, restoring
        /// the original profile variable values.
        /// </summary>
        /// <param name="oldValues">Dictionary returned by InjectRemoteHostFromEnvironment</param>
        /// <remarks>
        /// This should be called in a finally block after BuildPlayerContent to ensure
        /// the profile is restored even if the build fails. The revert does not persist to disk.
        ///
        /// Safe to call with an empty dictionary or null (no-op).
        /// </remarks>
        public static void RevertRemoteHostInjection(Dictionary<string, string> oldValues)
        {
            if (oldValues == null || oldValues.Count == 0)
                return;

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return;

            string activeProfileId = settings.activeProfileId;
            if (string.IsNullOrEmpty(activeProfileId))
                return;

            if (oldValues.TryGetValue(RemoteCatalogLoadPathVariable, out string oldCatalogPath))
            {
                settings.profileSettings.SetValue(activeProfileId, RemoteCatalogLoadPathVariable, oldCatalogPath);
            }

            if (oldValues.TryGetValue(AddressableAssetSettings.kRemoteLoadPath, out string oldBundlePath))
            {
                settings.profileSettings.SetValue(activeProfileId, AddressableAssetSettings.kRemoteLoadPath, oldBundlePath);
            }
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
