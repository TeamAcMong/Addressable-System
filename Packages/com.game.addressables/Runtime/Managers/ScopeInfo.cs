namespace AddressableManager.Managers
{
    /// <summary>
    /// Whether the object that registered a scope is still there.
    /// </summary>
    /// <remarks>
    /// Five values rather than a bool, because "no" has three distinct meanings and only one of them
    /// is a problem. Collapsing them would report a manager-owned scope, which has no external owner
    /// by design, as a leak on every snapshot.
    /// </remarks>
    public enum ScopeOwnerState
    {
        /// <summary>The owner exists and has not been destroyed.</summary>
        Alive = 0,

        /// <summary>
        /// The owning UnityEngine.Object was destroyed while its loader is still registered and
        /// still holding assets. <b>This is the leak.</b>
        /// </summary>
        Destroyed = 1,

        /// <summary>
        /// The owner was garbage collected. Same practical consequence as
        /// <see cref="Destroyed"/> - nothing is going to release this scope - but reached from a
        /// plain C# object rather than a Unity one.
        /// </summary>
        Collected = 2,

        /// <summary>
        /// The scope manager built this loader itself, so it has no external owner and its lifetime
        /// is the manager's business. Not a leak.
        /// </summary>
        ManagerOwned = 3,

        /// <summary>
        /// Registered without an owner reference. Nothing can be said about it - which is not the
        /// same as saying it is fine.
        /// </summary>
        Unknown = 4,
    }

    /// <summary>
    /// One registered scope, as read by <see cref="ScopeManager.SnapshotScopes"/>.
    /// </summary>
    /// <remarks>
    /// A detached value: holding one keeps nothing alive, so a diagnostics view can retain a
    /// snapshot without changing what it is measuring.
    /// </remarks>
    public readonly struct ScopeInfo
    {
        /// <summary>The scope's id.</summary>
        public readonly string ScopeId;

        /// <summary>True when the scope manager created this loader itself.</summary>
        public readonly bool ManagerOwned;

        /// <summary>The owning object's type name, or null.</summary>
        public readonly string OwnerTypeName;

        /// <summary>Whether the owner is still there.</summary>
        public readonly ScopeOwnerState OwnerState;

        /// <summary>How many assets this scope's loader is holding.</summary>
        public readonly int HeldAssetCount;

        /// <summary>
        /// True when the owner is gone and the loader is still holding something.
        /// </summary>
        /// <remarks>
        /// Both halves are required. A dead owner holding nothing is a registration that has not been
        /// tidied up - untidy, not expensive. A live owner holding a great deal is a game doing its
        /// job. It is only the pair that means memory nobody is going to give back.
        /// </remarks>
        public bool IsLeaked =>
            HeldAssetCount > 0 &&
            (OwnerState == ScopeOwnerState.Destroyed || OwnerState == ScopeOwnerState.Collected);

        /// <summary>Create a snapshot row.</summary>
        public ScopeInfo(
            string scopeId, bool managerOwned, string ownerTypeName,
            ScopeOwnerState ownerState, int heldAssetCount)
        {
            ScopeId = scopeId;
            ManagerOwned = managerOwned;
            OwnerTypeName = ownerTypeName;
            OwnerState = ownerState;
            HeldAssetCount = heldAssetCount;
        }
    }
}
