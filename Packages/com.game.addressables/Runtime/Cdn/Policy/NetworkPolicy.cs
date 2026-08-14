using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITASK_PRESENT
using Cysharp.Threading.Tasks;
#endif

namespace AddressableManager.Cdn
{
    /// <summary>How the device is currently connected.</summary>
    public enum NetworkReachabilityState
    {
        /// <summary>No connection.</summary>
        Offline = 0,

        /// <summary>Wi-Fi, ethernet, or anything else Unity does not call carrier data.</summary>
        Unmetered = 1,

        /// <summary>Carrier data. Downloading here may cost the player money.</summary>
        Metered = 2
    }

    /// <summary>
    /// Whether the network may be used right now — task 2.8.
    /// </summary>
    /// <remarks>
    /// WHAT THIS CAN AND CANNOT TELL YOU
    /// <see cref="Application.internetReachability"/> reports the type of the active interface, not
    /// whether anything is actually reachable through it. A captive portal, an expired hotel
    /// session, a VPN that dropped, DNS that resolves to nothing — all report Reachable. This class
    /// is a cheap pre-flight that avoids obviously doomed work; it is not proof the CDN can be
    /// reached, and callers must still handle a request failing after it said yes.
    ///
    /// Metered detection has the same shape of limitation: Unity reports "carrier data network",
    /// which misses a metered Wi-Fi hotspot and misreports an unmetered corporate APN. It is the
    /// only signal Unity exposes without a native plugin, so it is what the policy uses, and the
    /// design doc's UX for MeteredNetworkBlocked is a prompt rather than a hard block for exactly
    /// this reason.
    /// </remarks>
    public class NetworkPolicy
    {
        private readonly DownloadPolicy _policy;

        /// <summary>Create a policy from download settings.</summary>
        public NetworkPolicy(DownloadPolicy policy)
        {
            _policy = policy ?? DownloadPolicy.Default;
        }

        /// <summary>How the device is connected right now.</summary>
        public virtual NetworkReachabilityState CurrentState
        {
            get
            {
                switch (Application.internetReachability)
                {
                    case NetworkReachability.NotReachable:
                        return NetworkReachabilityState.Offline;
                    case NetworkReachability.ReachableViaCarrierDataNetwork:
                        return NetworkReachabilityState.Metered;
                    case NetworkReachability.ReachableViaLocalAreaNetwork:
                        return NetworkReachabilityState.Unmetered;
                    default:
                        // Unity has not added a fourth value in years; if it does, treating it as
                        // offline fails safe rather than downloading over something unknown.
                        return NetworkReachabilityState.Offline;
                }
            }
        }

        /// <summary>True when some interface reports as up.</summary>
        public bool IsReachable => CurrentState != NetworkReachabilityState.Offline;

        /// <summary>True when on carrier data.</summary>
        public bool IsMetered => CurrentState == NetworkReachabilityState.Metered;

        /// <summary>
        /// Whether a download may start now.
        /// </summary>
        /// <returns>
        /// Success when downloading is allowed. <see cref="CdnErrorCode.Offline"/> when nothing is
        /// reachable, <see cref="CdnErrorCode.MeteredNetworkBlocked"/> when the policy forbids
        /// carrier data and that is all there is.
        /// </returns>
        public CdnResult<NetworkReachabilityState> CanDownload()
        {
            var state = CurrentState;

            if (state == NetworkReachabilityState.Offline)
            {
                return CdnResult<NetworkReachabilityState>.Failure(
                    CdnErrorCode.Offline,
                    "No network connection",
                    hint: "Cached content remains playable. Retry when the connection returns.");
            }

            if (state == NetworkReachabilityState.Metered && _policy.RequireUnmeteredNetwork)
            {
                return CdnResult<NetworkReachabilityState>.Failure(
                    CdnErrorCode.MeteredNetworkBlocked,
                    "On a carrier data connection and the policy requires an unmetered one",
                    hint: "Ask the player whether to download over mobile data, then retry with the " +
                          "policy override rather than silently spending their data.");
            }

            return CdnResult<NetworkReachabilityState>.Success(state);
        }

#if UNITASK_PRESENT
        /// <summary>
        /// Wait until the network is reachable, or until the timeout expires.
        /// </summary>
        /// <param name="timeoutSeconds">
        /// Seconds to wait. Pass a negative value to use <see cref="DownloadPolicy.ReachabilityWaitSeconds"/>.
        /// Zero returns immediately with the current state.
        /// </param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        public async UniTask<CdnResult<NetworkReachabilityState>> WaitForReachableAsync(
            int timeoutSeconds = -1,
            CancellationToken cancellationToken = default)
#else
        /// <summary>
        /// Wait until the network is reachable, or until the timeout expires.
        /// </summary>
        /// <param name="timeoutSeconds">
        /// Seconds to wait. Pass a negative value to use <see cref="DownloadPolicy.ReachabilityWaitSeconds"/>.
        /// Zero returns immediately with the current state.
        /// </param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        public async Task<CdnResult<NetworkReachabilityState>> WaitForReachableAsync(
            int timeoutSeconds = -1,
            CancellationToken cancellationToken = default)
#endif
        {
            int budgetSeconds = timeoutSeconds < 0 ? _policy.ReachabilityWaitSeconds : timeoutSeconds;

            if (IsReachable || budgetSeconds <= 0)
                return CanDownload();

            // Poll rather than subscribe: Unity exposes no reachability-changed event, and a one
            // second tick is far below the cost of the download this is gating.
            const int pollMilliseconds = 1000;
            int elapsedMilliseconds = 0;

            while (elapsedMilliseconds < budgetSeconds * 1000)
            {
                if (cancellationToken.IsCancellationRequested)
                    return CdnResult<NetworkReachabilityState>.Cancelled("Cancelled while waiting for the network");

#if UNITASK_PRESENT
                await UniTask.Delay(pollMilliseconds, cancellationToken: cancellationToken);
#else
                await Task.Delay(pollMilliseconds, cancellationToken);
#endif
                elapsedMilliseconds += pollMilliseconds;

                if (IsReachable)
                    return CanDownload();
            }

            return CdnResult<NetworkReachabilityState>.Failure(
                CdnErrorCode.Offline,
                $"Still offline after waiting {budgetSeconds}s",
                hint: "Continue on cached content and retry later rather than blocking the player here.");
        }
    }
}
