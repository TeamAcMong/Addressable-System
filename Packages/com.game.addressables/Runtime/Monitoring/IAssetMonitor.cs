namespace AddressableManager.Monitoring
{
    /// <summary>
    /// Interface for monitoring asset operations
    /// Implemented by Editor code to track runtime operations
    /// </summary>
    public interface IAssetMonitor
    {
        /// <summary>
        /// Called when an asset is loaded
        /// </summary>
        void OnAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache);

        /// <summary>
        /// Called when an asset is released
        /// </summary>
        void OnAssetReleased(string address, string typeName);

        /// <summary>
        /// Called when a scope is registered
        /// </summary>
        void OnScopeRegistered(string scopeName, bool isActive);

        /// <summary>
        /// Called when a scope state changes
        /// </summary>
        void OnScopeStateChanged(string scopeName, bool isActive);

        /// <summary>
        /// Called when a scope is cleared
        /// </summary>
        void OnScopeCleared(string scopeName);
    }
}
