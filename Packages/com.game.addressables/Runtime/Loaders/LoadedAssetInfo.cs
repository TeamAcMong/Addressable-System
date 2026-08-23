namespace AddressableManager
{
    /// <summary>
    /// One asset a loader is currently holding alive, as read by
    /// <see cref="Loaders.AssetLoader.SnapshotLoadedAssets"/>.
    /// </summary>
    /// <remarks>
    /// A value type with no references back into the loader, so a caller can hold a snapshot for as
    /// long as it likes without keeping anything resident that would otherwise have been released -
    /// a diagnostics view that changes what it measures is worse than none.
    /// </remarks>
    public readonly struct LoadedAssetInfo
    {
        /// <summary>The address, or the runtime key for a sub-object reference.</summary>
        public readonly string Address;

        /// <summary>The requested type's short name, e.g. <c>Sprite</c>.</summary>
        /// <remarks>
        /// Part of the identity, not decoration: one address loaded as two types is two cache
        /// entries holding the same bundle, and a list keyed only by address would show one row and
        /// understate what is resident.
        /// </remarks>
        public readonly string TypeName;

        /// <summary>False once the underlying Addressables operation has been released.</summary>
        /// <remarks>
        /// A dead handle still sitting in the cache is worth seeing rather than filtering out: it is
        /// the shape a release-ordering bug takes, and hiding it would hide the bug.
        /// </remarks>
        public readonly bool IsAlive;

        /// <summary>
        /// Estimated resident bytes, or 0 when this loader is not tiered.
        /// </summary>
        /// <remarks>
        /// Zero means <b>not measured</b>, not "free". The byte accounting exists only to drive tier
        /// eviction, so an untiered loader never computes it. Any UI showing this has to distinguish
        /// the two, or it will report a hundred megabytes of resident content as costing nothing.
        /// </remarks>
        public readonly long EstimatedBytes;

        /// <summary>Create a snapshot row.</summary>
        public LoadedAssetInfo(string address, string typeName, bool isAlive, long estimatedBytes)
        {
            Address = address;
            TypeName = typeName;
            IsAlive = isAlive;
            EstimatedBytes = estimatedBytes;
        }
    }
}
