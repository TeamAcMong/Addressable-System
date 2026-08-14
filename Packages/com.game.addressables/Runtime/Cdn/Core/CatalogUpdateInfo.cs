using System;
using System.Collections.Generic;

namespace AddressableManager.Cdn
{
    /// <summary>
    /// What a check for catalog updates found.
    /// </summary>
    public class CatalogUpdateInfo
    {
        /// <summary>Catalog ids with an update available. Empty when there is nothing to do.</summary>
        public IReadOnlyList<string> CatalogsWithUpdates { get; }

        /// <summary>True when at least one catalog has an update.</summary>
        public bool HasUpdate => CatalogsWithUpdates.Count > 0;

        /// <summary>
        /// True when the check could not reach the network and answered from cache instead.
        /// </summary>
        /// <remarks>
        /// The distinction that matters: <see cref="HasUpdate"/> false means "checked, nothing new",
        /// while this flag means "could not check". Both let the game continue on cached content,
        /// but only the second is worth retrying later, and conflating them is how a client ends up
        /// permanently stuck on old content after one bad boot.
        /// </remarks>
        public bool WasOfflineFallback { get; }

        /// <summary>Create a result.</summary>
        public CatalogUpdateInfo(IReadOnlyList<string> catalogsWithUpdates, bool wasOfflineFallback = false)
        {
            CatalogsWithUpdates = catalogsWithUpdates ?? Array.Empty<string>();
            WasOfflineFallback = wasOfflineFallback;
        }

        /// <summary>Nothing to update, checked successfully.</summary>
        public static CatalogUpdateInfo None => new CatalogUpdateInfo(Array.Empty<string>());

        /// <summary>Could not check; the game continues on cached content.</summary>
        public static CatalogUpdateInfo Offline => new CatalogUpdateInfo(Array.Empty<string>(), true);

        /// <inheritdoc />
        public override string ToString()
        {
            if (WasOfflineFallback) return "CatalogUpdateInfo(offline, not checked)";

            return HasUpdate
                ? $"CatalogUpdateInfo({CatalogsWithUpdates.Count} catalog(s) with updates)"
                : "CatalogUpdateInfo(up to date)";
        }
    }
}
