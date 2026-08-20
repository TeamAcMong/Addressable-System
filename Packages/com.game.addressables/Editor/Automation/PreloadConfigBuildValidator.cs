using System.Collections.Generic;
using System.Text;
using AddressableManager.Configs;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AddressableManager.Editor.Automation
{
    /// <summary>
    /// Runs <see cref="AddressablePreloadConfig.Validate"/> as a player-build step, honouring each
    /// config's own <c>validateOnBuild</c> and <c>failBuildOnError</c> flags.
    /// </summary>
    /// <remarks>
    /// Those two fields shipped as inspector toggles with tooltips, and the editor tools guide told
    /// teams to enable them as a shipping gate - but no build callback of any kind existed anywhere in
    /// the package (<c>IPreprocessBuildWithReport</c> had zero implementations), and nothing else read
    /// either field. A team that ticked both and relied on the build to catch a broken AssetReference
    /// shipped the broken reference.
    ///
    /// Scope is deliberately narrow, matching what the fields promise and nothing more:
    /// <list type="bullet">
    /// <item>Only configs with <c>validateOnBuild</c> set are checked. Default is true, but a config
    /// the project does not want gated can opt out.</item>
    /// <item>A config that validates clean is silent. Build logs are noisy enough.</item>
    /// <item>Errors are always reported. Whether they FAIL the build is the config's own
    /// <c>failBuildOnError</c> decision - a warning otherwise, so a project can adopt the check before
    /// it is ready to be blocked by it.</item>
    /// </list>
    /// This validates the preload config's own entries. It does not build Addressables content; that
    /// stays an explicit step (CdnBuildCLI), which is what
    /// <c>settings.BuildAddressablesWithPlayerBuild = DoNotBuildWithPlayer</c> is pinned for.
    /// </remarks>
    public sealed class PreloadConfigBuildValidator : IPreprocessBuildWithReport
    {
        /// <summary>Early, so a broken config fails before the expensive part of the build starts.</summary>
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var configs = LoadAllConfigs();
            if (configs.Count == 0) return;

            var failures = new StringBuilder();
            int checkedCount = 0;
            bool blocking = false;

            foreach (var config in configs)
            {
                if (config == null || !config.validateOnBuild) continue;

                checkedCount++;

                var (success, errors) = config.Validate();
                if (success) continue;

                if (config.failBuildOnError) blocking = true;

                failures.AppendLine($"  {config.name} ({AssetDatabase.GetAssetPath(config)}):");
                foreach (string error in errors)
                {
                    failures.AppendLine($"    - {error}");
                }
            }

            if (checkedCount == 0 || failures.Length == 0) return;

            string message =
                "[PreloadConfigBuildValidator] Preload configuration is not valid:\n" + failures;

            if (blocking)
            {
                // BuildFailedException is the only thing that actually stops a build from a
                // preprocessor; logging an error does not.
                throw new BuildFailedException(
                    message +
                    "\nThis build was stopped because 'Fail Build On Error' is enabled on at least one of " +
                    "the configs above. Fix the entries, or clear that toggle to downgrade this to a warning.");
            }

            Debug.LogWarning(
                message +
                "\nThe build was allowed to continue because 'Fail Build On Error' is off on every config " +
                "reporting problems. Enable it to make this a hard gate.");
        }

        private static List<AddressablePreloadConfig> LoadAllConfigs()
        {
            var results = new List<AddressablePreloadConfig>();

            foreach (string guid in AssetDatabase.FindAssets("t:AddressablePreloadConfig"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var config = AssetDatabase.LoadAssetAtPath<AddressablePreloadConfig>(path);
                if (config != null) results.Add(config);
            }

            return results;
        }
    }
}
