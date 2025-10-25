using System;
using UnityEngine;
using AddressableManager.Loaders;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Base implementation for asset scopes
    /// </summary>
    public abstract class BaseAssetScope : IAssetScope
    {
        private readonly string _scopeName;
        private AssetLoader _loader;
        private bool _isActive;
        private bool _disposed;

        public string ScopeName => _scopeName;
        public AssetLoader Loader => _loader;
        public bool IsActive => _isActive;

        protected BaseAssetScope(string scopeName)
        {
            _scopeName = scopeName;
            _loader = new AssetLoader();
            _isActive = false;
        }

        public virtual void Activate()
        {
            if (_disposed)
            {
                Debug.LogError($"[{_scopeName}] Cannot activate disposed scope");
                return;
            }

            if (_isActive)
            {
                Debug.LogWarning($"[{_scopeName}] Scope already active");
                return;
            }

            _isActive = true;
            Debug.Log($"[{_scopeName}] Scope activated");
        }

        public virtual void Deactivate()
        {
            if (!_isActive) return;

            Debug.Log($"[{_scopeName}] Deactivating scope");
            _loader?.ClearCache();
            _isActive = false;
        }

        public virtual void Dispose()
        {
            if (_disposed) return;

            Debug.Log($"[{_scopeName}] Disposing scope");
            Deactivate();
            _loader?.Dispose();
            _loader = null;
            _disposed = true;
        }
    }
}
