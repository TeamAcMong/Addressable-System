using System.Linq;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace AddressableManager.Tests
{
    /// <summary>
    /// Setup utility for PlayMode tests that require the packed play-mode builder.
    ///
    /// This class must be used before running PlayMode tests that verify HTTP behavior.
    /// The play-mode builder is selected before [SetUp] runs, so it must be set via -executeMethod
    /// during the Editor initialization phase.
    ///
    /// Usage (CI):
    ///   Unity -batchmode -quit -executeMethod AddressableManager.Tests.PlayModeTestSetup.EnsurePackedPlayModeBuilder
    ///   Unity -batchmode -runTests -testPlatform PlayMode
    /// </summary>
    public static class PlayModeTestSetup
    {
        /// <summary>
        /// Selects the packed play-mode builder (BuildScriptPackedPlayMode) and persists the setting.
        /// This must run in -executeMethod BEFORE -runTests is invoked, so the setting takes effect
        /// when play mode is entered.
        ///
        /// The builder is located by type name, not index, to be robust to builder reordering.
        /// </summary>
        public static void EnsurePackedPlayModeBuilder()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[PlayModeTestSetup] AddressableAssetSettings not found. Project may not be initialized.");
                return;
            }

            var dataBuilders = settings.DataBuilders;
            int packedPlayModeIndex = -1;

            // Find the packed play-mode builder by type name
            for (int i = 0; i < dataBuilders.Count; i++)
            {
                var builder = dataBuilders[i];
                if (builder == null) continue;

                var typeName = builder.GetType().Name;
                if (typeName == "BuildScriptPackedPlayMode")
                {
                    packedPlayModeIndex = i;
                    break;
                }
            }

            if (packedPlayModeIndex == -1)
            {
                Debug.LogError("[PlayModeTestSetup] Could not find BuildScriptPackedPlayMode builder. " +
                    $"Available: {string.Join(", ", dataBuilders.Cast<Object>().Select(b => b?.GetType().Name ?? "null"))}");
                return;
            }

            ProjectConfigData.ActivePlayModeIndex = packedPlayModeIndex;
            Debug.Log($"[PlayModeTestSetup] Set active play-mode index to {packedPlayModeIndex} (BuildScriptPackedPlayMode)");
        }

        /// <summary>
        /// Returns the name of the currently active play-mode builder.
        /// Used for diagnostic and error messages.
        /// </summary>
        public static string GetActivePlayModeBuilderName()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return "unknown (no settings)";

            int activeIndex = ProjectConfigData.ActivePlayModeIndex;
            var dataBuilders = settings.DataBuilders;

            if (activeIndex < 0 || activeIndex >= dataBuilders.Count)
                return $"unknown (index {activeIndex} out of range)";

            var builder = dataBuilders[activeIndex];
            return builder?.GetType().Name ?? "unknown (null builder)";
        }
    }
}
