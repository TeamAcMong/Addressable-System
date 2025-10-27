namespace AddressableManager.Configs
{
    /// <summary>
    /// Defines the lifecycle scope for an asset
    /// </summary>
    public enum AssetScopeType
    {
        /// <summary>
        /// Global scope - persists throughout entire application lifetime
        /// </summary>
        Global,

        /// <summary>
        /// Session scope - persists between scenes but can be manually cleared
        /// </summary>
        Session,

        /// <summary>
        /// Scene scope - automatically cleared on scene unload
        /// </summary>
        Scene,

        /// <summary>
        /// Hierarchy scope - cleared when parent GameObject is destroyed
        /// </summary>
        Hierarchy
    }
}
