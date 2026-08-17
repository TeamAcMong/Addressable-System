using System.IO;
using AddressableManager.Cdn;
using UnityEditor;
using UnityEngine;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Creates the CdnSettings asset the Phase 2 PlayMode tests need.
    /// </summary>
    /// <remarks>
    /// Why this exists as an -executeMethod step rather than something the test does itself:
    /// CdnSettings is loaded through Resources, and Resources content is resolved when play mode
    /// starts. Creating the asset from inside [SetUp] would be too late — Resources.Load would miss
    /// it on the run that created it and find it on the next, which is exactly the kind of
    /// order-dependent green the repo rules forbid.
    ///
    /// Usage, before running PlayMode tests:
    ///   Unity -batchmode -quit -executeMethod AddressableManager.Tests.CdnSettingsFixture.EnsureTestSettings
    /// </summary>
    public static class CdnSettingsFixture
    {
        /// <summary>Folder the fixture asset lives in.</summary>
        public const string ResourcesFolder = "Assets/Resources";

        /// <summary>Full asset path of the fixture.</summary>
        public const string AssetPath = ResourcesFolder + "/" + CdnSettings.ResourceName + ".asset";

        /// <summary>
        /// Create the settings asset if it is missing, pointed at the local content server.
        /// </summary>
        /// <remarks>
        /// The base URL is the origin only. Addressables bakes the platform and app-version
        /// segments into the catalog URL at build time, and the Local profile already points at
        /// http://localhost:8080, so the rewriter has nothing to change on a local run — which is
        /// the point: the test exercises the real path without the rewriter masking a mistake in it.
        /// </remarks>
        public static void EnsureTestSettings()
        {
            if (!Directory.Exists(ResourcesFolder))
            {
                Directory.CreateDirectory(ResourcesFolder);
                AssetDatabase.Refresh();
            }

            var existing = AssetDatabase.LoadAssetAtPath<CdnSettings>(AssetPath);
            if (existing != null)
            {
                Debug.Log($"[CdnSettingsFixture] Already present at {AssetPath}");
                ReportValidation(existing);
                return;
            }

            var settings = ScriptableObject.CreateInstance<CdnSettings>();

            // Serialized private fields, set through SerializedObject so the asset matches what the
            // inspector would produce rather than relying on reflection into private state.
            var serialized = new SerializedObject(settings);

            var environments = serialized.FindProperty("environments");
            environments.arraySize = 1;
            var local = environments.GetArrayElementAtIndex(0);
            local.FindPropertyRelative("id").stringValue = "Local";
            local.FindPropertyRelative("displayName").stringValue = "Local";
            local.FindPropertyRelative("baseUrl").stringValue = "http://localhost:8080";
            local.FindPropertyRelative("failoverUrls").arraySize = 0;

            serialized.FindProperty("defaultEnvironmentId").stringValue = "Local";

            // Empty: an env-var override would make the test depend on the shell it was launched
            // from, and CI sets CDN_BASE_URL for the deploy scripts.
            serialized.FindProperty("hostEnvironmentVariable").stringValue = string.Empty;
            serialized.FindProperty("logUrlRewrites").boolValue = true;

            serialized.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CdnSettingsFixture] Created {AssetPath}");
            ReportValidation(AssetDatabase.LoadAssetAtPath<CdnSettings>(AssetPath));
        }

        private static void ReportValidation(CdnSettings settings)
        {
            if (settings == null)
            {
                Debug.LogError("[CdnSettingsFixture] Asset could not be loaded back after creation");
                return;
            }

            var problems = settings.Validate();
            if (problems.Count == 0)
            {
                Debug.Log($"[CdnSettingsFixture] Valid. Default environment: {settings.DefaultEnvironmentId}, " +
                          $"base URL: {settings.GetDefaultEnvironment().Value?.BaseUrl}");
                return;
            }

            foreach (string problem in problems)
                Debug.LogError($"[CdnSettingsFixture] {problem}");
        }
    }
}
