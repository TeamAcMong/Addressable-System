using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// Runtime configuration for the CDN layer — task 2.2.
    /// </summary>
    /// <remarks>
    /// HOW THE GAME FINDS THIS
    /// From <c>Resources</c>, by the fixed name <see cref="ResourceName"/>. Resources is the only
    /// load path that works before Addressables has initialised, which is exactly when this is
    /// needed — the settings say where the catalog lives, so they cannot themselves be addressable.
    ///
    /// NO URLS IN THE ASSET FOR SHIPPED ENVIRONMENTS
    /// Committing a production CDN hostname makes it a build-time constant that only a new player
    /// build can change. Leave <c>baseUrl</c> pointing at a placeholder and set
    /// <see cref="HostEnvironmentVariable"/>, or override at runtime through the facade, so the
    /// same build can be pointed at staging. This mirrors what CdnProfileManager does on the
    /// Editor side for build paths.
    /// </remarks>
    [CreateAssetMenu(fileName = "CdnSettings", menuName = "Addressable Manager/CDN Settings", order = 200)]
    public class CdnSettings : ScriptableObject
    {
        /// <summary>The name this asset must have inside a Resources folder.</summary>
        public const string ResourceName = "CdnSettings";

        [SerializeField]
        [Tooltip("Every environment this build can be pointed at.")]
        private List<CdnEnvironment> environments = new List<CdnEnvironment>
        {
            new CdnEnvironment("Local", "Local", "http://localhost:8080")
        };

        [SerializeField]
        [Tooltip("Id of the environment used when nothing overrides it.")]
        private string defaultEnvironmentId = "Local";

        [SerializeField]
        [Tooltip("If set and present, this environment variable overrides the active base URL. " +
                 "Editor and standalone only — mobile and console have no process environment.")]
        private string hostEnvironmentVariable = "CDN_BASE_URL";

        [SerializeField]
        [Tooltip("How the CDN layer may use the network.")]
        private DownloadPolicy downloadPolicy = new DownloadPolicy();

        [SerializeField]
        [Tooltip("Log every rewritten URL. Noisy; for diagnosing a wrong-host problem.")]
        private bool logUrlRewrites = false;

        /// <summary>Every configured environment.</summary>
        public IReadOnlyList<CdnEnvironment> Environments => environments ?? new List<CdnEnvironment>();

        /// <summary>Id of the environment used when nothing overrides it.</summary>
        public string DefaultEnvironmentId => defaultEnvironmentId;

        /// <summary>Environment variable that overrides the base URL, or empty.</summary>
        public string HostEnvironmentVariable => hostEnvironmentVariable;

        /// <summary>How the CDN layer may use the network.</summary>
        public DownloadPolicy DownloadPolicy => downloadPolicy ?? DownloadPolicy.Default;

        /// <summary>Log every rewritten URL.</summary>
        public bool LogUrlRewrites => logUrlRewrites;

        /// <summary>
        /// Load the settings asset from Resources, or return a failure explaining how to create it.
        /// </summary>
        /// <remarks>
        /// A failure here is a setup mistake, not a runtime condition, so the message is written
        /// for whoever is integrating the package rather than for a player-facing dialog.
        /// </remarks>
        public static CdnResult<CdnSettings> Load()
        {
            var settings = Resources.Load<CdnSettings>(ResourceName);
            if (settings == null)
            {
                return CdnResult<CdnSettings>.Failure(
                    CdnErrorCode.Unknown,
                    $"No CdnSettings asset found at Resources/{ResourceName}",
                    hint: "Create one via Assets > Create > Addressable Manager > CDN Settings and put it " +
                          "in any folder named Resources. It has to come from Resources rather than " +
                          "Addressables, because it is what tells Addressables where to look.");
            }

            return CdnResult<CdnSettings>.Success(settings);
        }

        /// <summary>
        /// Find an environment by id.
        /// </summary>
        public CdnResult<CdnEnvironment> GetEnvironment(string environmentId)
        {
            if (string.IsNullOrEmpty(environmentId))
                return CdnResult<CdnEnvironment>.Failure(CdnErrorCode.Unknown, "Environment id is empty");

            var match = Environments.FirstOrDefault(
                e => e != null && string.Equals(e.Id, environmentId, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                string known = Environments.Count == 0
                    ? "(none configured)"
                    : string.Join(", ", Environments.Where(e => e != null).Select(e => e.Id));

                return CdnResult<CdnEnvironment>.Failure(
                    CdnErrorCode.Unknown,
                    $"No environment with id '{environmentId}'",
                    hint: $"Configured environments: {known}");
            }

            return CdnResult<CdnEnvironment>.Success(match);
        }

        /// <summary>
        /// The environment selected by <see cref="DefaultEnvironmentId"/>.
        /// </summary>
        public CdnResult<CdnEnvironment> GetDefaultEnvironment() => GetEnvironment(defaultEnvironmentId);

        /// <summary>
        /// Check the asset is coherent. Returns every problem, not just the first, so a misconfigured
        /// asset can be fixed in one pass.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            if (environments == null || environments.Count == 0)
            {
                problems.Add("No environments configured");
            }
            else
            {
                foreach (var environment in environments)
                {
                    if (environment == null)
                    {
                        problems.Add("An environment entry is null");
                        continue;
                    }

                    if (!environment.IsValid(out string reason))
                        problems.Add(reason);
                }

                var duplicates = environments
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id))
                    .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key);

                foreach (string duplicate in duplicates)
                    problems.Add($"Duplicate environment id '{duplicate}' — lookups would be ambiguous");
            }

            if (string.IsNullOrWhiteSpace(defaultEnvironmentId))
            {
                problems.Add("Default environment id is empty");
            }
            else if (environments != null &&
                     !environments.Any(e => e != null &&
                                            string.Equals(e.Id, defaultEnvironmentId, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"Default environment '{defaultEnvironmentId}' is not in the environment list");
            }

            return problems;
        }
    }
}
