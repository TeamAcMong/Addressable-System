using System;
using AddressableManager.Loaders;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Interface for scoped asset management with automatic lifecycle
    /// </summary>
    public interface IAssetScope : IDisposable
    {
        /// <summary>
        /// Name identifier for this scope
        /// </summary>
        string ScopeName { get; }

        /// <summary>
        /// Asset loader for this scope
        /// </summary>
        AssetLoader Loader { get; }

        /// <summary>
        /// Whether this scope is active
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Activate this scope
        /// </summary>
        void Activate();

        /// <summary>
        /// Deactivate and cleanup this scope
        /// </summary>
        void Deactivate();
    }
}
