using System.Threading;
using UnityEngine;
using AddressableManager.Loaders;
using AddressableManager.Monitoring;

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
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public string ScopeName => _scopeName;
        public AssetLoader Loader => _loader;
        public bool IsActive => _isActive;

        /// <summary>
        /// Cancellation token that fires when the scope is disposed.
        /// Pass this to long-running awaits so they unwind cleanly when the
        /// owning GameObject is destroyed mid-load.
        /// </summary>
        public CancellationToken DisposedToken => _cts.Token;

        protected BaseAssetScope(string scopeName)
        {
            _scopeName = scopeName;
            // Pass scope name to AssetLoader for automatic monitoring
            _loader = new AssetLoader(scopeName);
            _isActive = false;

            // Report scope registration
            AssetMonitorBridge.ReportScopeRegistered(_scopeName, false);
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

            // Report scope state change
            AssetMonitorBridge.ReportScopeStateChanged(_scopeName, true);
        }

        public virtual void Deactivate()
        {
            if (!_isActive) return;

            Debug.Log($"[{_scopeName}] Deactivating scope");
            _loader?.ClearCache();
            _isActive = false;

            // Report scope state change
            AssetMonitorBridge.ReportScopeStateChanged(_scopeName, false);
        }

        public virtual void Dispose()
        {
            if (_disposed) return;

            Debug.Log($"[{_scopeName}] Disposing scope");

            // Signal in-flight async work that the scope is gone before we tear the loader down.
            try { _cts.Cancel(); } catch { /* ignore */ }

            Deactivate();

            // Report scope cleared
            AssetMonitorBridge.ReportScopeCleared(_scopeName);

            _loader?.Dispose();
            _loader = null;

            _cts.Dispose();
            _disposed = true;
        }
    }
}
