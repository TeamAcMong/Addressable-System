using System;
using UnityEngine;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// One place content can be served from: Local, Dev, Staging, Prod.
    /// </summary>
    /// <remarks>
    /// Mirrors the Editor-side profiles in <c>CdnProfileManager</c>. The ids must match the profile
    /// names used at build time, because switching environment at runtime rewrites URLs that were
    /// baked by one of those profiles.
    /// </remarks>
    [Serializable]
    public class CdnEnvironment
    {
        [SerializeField]
        [Tooltip("Stable identifier. Must match the Editor profile name: Local, Dev, Staging, Prod.")]
        private string id = "Local";

        [SerializeField]
        [Tooltip("Name shown in menus and diagnostics. Free text.")]
        private string displayName = "Local";

        [SerializeField]
        [Tooltip("Scheme and host only, no trailing slash. e.g. https://cdn-dev.example.com/game\n" +
                 "Supports {platform} and {appVersion} tokens.")]
        private string baseUrl = "http://localhost:8080";

        [SerializeField]
        [Tooltip("Tried in order if baseUrl fails. Same token support. Optional.")]
        private string[] failoverUrls = Array.Empty<string>();

        /// <summary>Stable identifier, matching the Editor profile name.</summary>
        public string Id => id;

        /// <summary>Name for menus and diagnostics.</summary>
        public string DisplayName => string.IsNullOrEmpty(displayName) ? id : displayName;

        /// <summary>Primary origin, scheme and host, no trailing slash.</summary>
        public string BaseUrl => baseUrl;

        /// <summary>Alternate origins, tried in order when the primary fails.</summary>
        public string[] FailoverUrls => failoverUrls ?? Array.Empty<string>();

        /// <summary>Construct in code. The inspector uses the serialized fields instead.</summary>
        public CdnEnvironment(string id, string displayName, string baseUrl, string[] failoverUrls = null)
        {
            this.id = id;
            this.displayName = displayName;
            this.baseUrl = baseUrl;
            this.failoverUrls = failoverUrls ?? Array.Empty<string>();
        }

        /// <summary>Whether this entry is usable.</summary>
        public bool IsValid(out string reason)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                reason = "Environment id is empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                reason = $"Environment '{id}' has no base URL";
                return false;
            }

            if (baseUrl.EndsWith("/"))
            {
                // Left as an error rather than trimmed silently: a trailing slash produces a double
                // slash mid-path, which some CDNs treat as a distinct cache key and others 404.
                reason = $"Environment '{id}' base URL must not end with '/': {baseUrl}";
                return false;
            }

            if (!baseUrl.StartsWith("http://") && !baseUrl.StartsWith("https://"))
            {
                reason = $"Environment '{id}' base URL must start with http:// or https://: {baseUrl}";
                return false;
            }

            reason = null;
            return true;
        }

        /// <inheritdoc />
        public override string ToString() => $"{DisplayName} ({id}) -> {baseUrl}";
    }
}
