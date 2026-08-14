using System;
using AddressableManager.Cdn;
using UnityEngine;

namespace AddressableManager.Examples
{
    /// <summary>
    /// The canonical CDN boot sequence — task 2.10, design doc §6.
    /// </summary>
    /// <remarks>
    /// Drop this on a GameObject in your first scene and nothing else. It is written to be read
    /// rather than reused: every branch corresponds to one of the boot outcomes in design doc §7,
    /// and the comments say what a real game should do in each.
    ///
    /// THE ONE RULE
    /// This must run before anything else touches Addressables. Addressables initialises implicitly
    /// on its first load call, and the CDN hooks are only honoured for content resolved after they
    /// are installed. An AssetReference on another object in this scene, or a LoadAssetAsync in
    /// another Awake, is enough to lose them. If that happens, InitializeAsync fails with a message
    /// saying so rather than continuing with a half-applied configuration.
    /// </remarks>
    public class CdnBootExample : MonoBehaviour
    {
        [Header("Environment")]
        [Tooltip("Leave empty to use the default from the CdnSettings asset.")]
        [SerializeField] private string environmentId = "";

        [Header("Behaviour")]
        [Tooltip("Check for a newer catalog after initialising.")]
        [SerializeField] private bool checkForUpdateOnBoot = true;

        [Tooltip("Apply an update automatically. A real game usually asks the player first when the " +
                 "download is large or the connection is metered.")]
        [SerializeField] private bool applyUpdateAutomatically = true;

        /// <summary>True once the boot sequence has finished, whatever the outcome.</summary>
        public bool BootComplete { get; private set; }

        /// <summary>The outcome of the last boot, for a loading screen to read.</summary>
        public string Status { get; private set; } = "Not started";

        /// <remarks>
        /// async rather than a coroutine on purpose: the facade returns Task or UniTask depending on
        /// whether UniTask is installed (repo invariant 3), and `await` is the only form that
        /// compiles against both. Polling .IsCompleted would tie this sample to the Task build.
        /// </remarks>
        private async void Start()
        {
            try
            {
                await Boot();
            }
            catch (Exception ex)
            {
                // async void swallows exceptions otherwise, and a boot sequence that fails silently
                // is worse than one that fails loudly.
                Status = "Content delivery crashed";
                BootComplete = true;
                Debug.LogException(ex);
            }
        }

        private async System.Threading.Tasks.Task Boot()
        {
            Status = "Initialising content delivery...";

            var init = await CdnManager.InitializeAsync(
                string.IsNullOrEmpty(environmentId) ? null : environmentId);

            if (init.IsFailure)
            {
                HandleInitFailure(init.Error);
                BootComplete = true;
                return;
            }

            Debug.Log($"[CdnBootExample] Initialised against {CdnManager.CurrentEnvironmentId} " +
                      $"({CdnManager.CurrentBaseUrl})");

            if (!checkForUpdateOnBoot)
            {
                Status = "Ready";
                BootComplete = true;
                return;
            }

            Status = "Checking for content updates...";

            var check = await CdnManager.CheckForUpdateAsync();

            if (check.IsFailure)
            {
                // Not fatal. The game has a usable catalog — it just could not find out whether a
                // newer one exists, so it plays on what it has and asks again later.
                Debug.LogWarning($"[CdnBootExample] Update check failed, continuing on current content: {check.Error}");
                Status = "Ready (update check failed)";
                BootComplete = true;
                return;
            }

            if (check.Value.WasOfflineFallback)
            {
                // Distinct from "no update": this means the question could not be asked. Worth
                // retrying on reconnect, where "no update" is not.
                Debug.Log("[CdnBootExample] Offline — playing on cached content, will re-check later");
                Status = "Ready (offline)";
                BootComplete = true;
                return;
            }

            if (!check.Value.HasUpdate)
            {
                Debug.Log("[CdnBootExample] Content is up to date");
                Status = "Ready";
                BootComplete = true;
                return;
            }

            Debug.Log($"[CdnBootExample] {check.Value.CatalogsWithUpdates.Count} catalog(s) have updates");

            if (!applyUpdateAutomatically)
            {
                Status = "Update available";
                BootComplete = true;
                return;
            }

            Status = "Applying content update...";

            var apply = await CdnManager.ApplyUpdateAsync(check.Value);

            if (apply.IsFailure)
            {
                if (apply.Error.Code == CdnErrorCode.MeteredNetworkBlocked)
                {
                    // The policy refused rather than spending the player's data silently. A real
                    // game prompts here and retries with the player's consent.
                    Debug.Log("[CdnBootExample] Update needs mobile data — ask the player first");
                    Status = "Update available (mobile data)";
                }
                else
                {
                    Debug.LogWarning($"[CdnBootExample] Update failed, continuing on current content: {apply.Error}");
                    Status = "Ready (update failed)";
                }

                BootComplete = true;
                return;
            }

            Debug.Log($"[CdnBootExample] Applied {apply.Value.Count} catalog(s)");
            Status = "Ready (updated)";
            BootComplete = true;
        }

        /// <summary>
        /// The three initialisation outcomes that need different handling — design doc §7.
        /// </summary>
        private void HandleInitFailure(CdnError error)
        {
            switch (error.Code)
            {
                case CdnErrorCode.NoContentAvailableOffline:
                    // First launch with no cache and no network. There is genuinely nothing to
                    // play, so this is the one case that justifies a blocking screen. It should
                    // retry on reconnect rather than dead-ending.
                    Status = "No content — connect to the internet to finish setup";
                    Debug.LogError($"[CdnBootExample] {error}");
                    break;

                case CdnErrorCode.CatalogNotFound:
                    // Reachable, but the origin has no catalog for this app version. A deploy is
                    // broken or the app version does not match what was published. Report it.
                    Status = "Content unavailable — please try again later";
                    Debug.LogError($"[CdnBootExample] Deploy problem, not a player problem: {error}");
                    break;

                default:
                    Status = "Content delivery unavailable";
                    Debug.LogError($"[CdnBootExample] {error}");
                    break;
            }
        }
    }
}
