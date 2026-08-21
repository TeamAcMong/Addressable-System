using System;
using UnityEngine;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// How the CDN layer is allowed to use the network.
    /// </summary>
    /// <remarks>
    /// Phase 2 uses the reachability and timeout parts. The retry and concurrency fields are
    /// declared here because Phase 3 needs them and moving the type later would break anyone who
    /// serialised a settings asset in between; they are inert until then, which the tooltips say.
    /// </remarks>
    [Serializable]
    public class DownloadPolicy
    {
        [SerializeField]
        [Tooltip("Refuse to download over a carrier data connection. The caller gets " +
                 "MeteredNetworkBlocked and can prompt the user.")]
        private bool requireUnmeteredNetwork = false;

        [SerializeField]
        [Range(1, 300)]
        [Tooltip("Seconds before a single request is abandoned.")]
        private int timeoutSeconds = 30;

        [SerializeField]
        [Range(0, 10)]
        [Tooltip("Retries after the first attempt. Phase 3 — not yet applied.")]
        private int maxRetries = 3;

        [SerializeField]
        [Range(1, 32)]
        [Tooltip("Concurrent downloads. Phase 3 — not yet applied.")]
        private int maxConcurrentDownloads = 6;

        [SerializeField]
        [Tooltip("Seconds to wait for the network to come back before giving up. 0 means do not wait.")]
        [Range(0, 120)]
        private int reachabilityWaitSeconds = 0;

        [SerializeField]
        [Range(0, 120)]
        [Tooltip("Deadline for the whole catalog check/apply, not one request. 0 disables it. " +
                 "Guards the 'online but very slow' case - a captive portal or one bar of signal - " +
                 "where reachability says yes and the operation then never returns.")]
        private int catalogOperationTimeoutSeconds = 5;

        /// <summary>
        /// Deadline applied to a whole catalog check or apply. 0 disables it.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="TimeoutSeconds"/>, which bounds ONE request. A catalog check can
        /// sit inside its reachability guard and then hang anyway: Application.internetReachability
        /// reports the interface, not whether anything answers, so a captive portal or a very weak
        /// connection passes the guard and the operation never returns. Without a deadline the caller's
        /// first screen waits forever, which is why integrations end up bolting their own timeout on
        /// top - work the package should not be pushing outward.
        ///
        /// Default 5s: long enough for a slow-but-real CDN handshake, short enough that a boot screen
        /// gives up and carries on with whatever content shipped in the player.
        /// </remarks>
        public int CatalogOperationTimeoutSeconds => catalogOperationTimeoutSeconds;

        /// <summary>Refuse to download over carrier data.</summary>
        public bool RequireUnmeteredNetwork => requireUnmeteredNetwork;

        /// <summary>Seconds before a single request is abandoned.</summary>
        public int TimeoutSeconds => timeoutSeconds;

        /// <summary>Retries after the first attempt. Phase 3.</summary>
        public int MaxRetries => maxRetries;

        /// <summary>Concurrent downloads. Phase 3.</summary>
        public int MaxConcurrentDownloads => maxConcurrentDownloads;

        /// <summary>Seconds to wait for reachability before giving up. 0 disables waiting.</summary>
        public int ReachabilityWaitSeconds => reachabilityWaitSeconds;

        /// <summary>Defaults suitable for a first integration.</summary>
        public static DownloadPolicy Default => new DownloadPolicy();
    }
}
