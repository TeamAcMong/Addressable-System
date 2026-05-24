using System;
using System.Threading;
using UnityEngine;
using AddressableManager.Loaders;
using AddressableManager.Monitoring;

namespace AddressableManager.Scopes
{
    /// <summary>
    /// Base implementation for asset scopes.
    ///
    /// Identity vs display: every scope has a unique <see cref="ScopeId"/> used
    /// as a dictionary key by <see cref="Managers.ScopeManager"/> and as the
    /// monitoring channel by <see cref="AssetMonitorBridge"/>. The
    /// <see cref="DisplayName"/> is the friendly label shown by the Dashboard
    /// inspector and falls back to the id if not supplied.
    ///
    /// Two scopes with the same display label are still distinguishable as long
    /// as their ids differ — multi-instance scopes (Scene / Hierarchy) take
    /// advantage of this by encoding owner identity (scene handle, GameObject
    /// instance id) into the id.
    /// </summary>
    public abstract class BaseAssetScope : IAssetScope
    {
        private readonly string _scopeId;
        private readonly string _displayName;
        private AssetLoader _loader;
        private bool _isActive;
        private bool _disposed;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Unique identifier used for ScopeManager lookup + monitoring.</summary>
        public string ScopeId => _scopeId;

        /// <summary>Friendly label shown in the Dashboard / inspectors. Defaults to ScopeId.</summary>
        public string DisplayName => _displayName;

        /// <summary>
        /// Back-compat alias for ScopeId. Pre-4.0 code reads this property; new
        /// code should prefer <see cref="ScopeId"/> for clarity.
        /// </summary>
        public string ScopeName => _scopeId;

        public AssetLoader Loader => _loader;
        public bool IsActive => _isActive;

        /// <summary>
        /// Cancellation token that fires when the scope is disposed.
        /// Pass this to long-running awaits so they unwind cleanly when the
        /// owning GameObject is destroyed mid-load.
        /// </summary>
        public CancellationToken DisposedToken => _cts.Token;

        protected BaseAssetScope(string scopeId, string displayName = null)
        {
            _scopeId = scopeId;
            _displayName = string.IsNullOrEmpty(displayName) ? scopeId : displayName;
            _loader = new AssetLoader(_scopeId);
            _isActive = false;

            AssetMonitorBridge.ReportScopeRegistered(_scopeId, false);
        }

        public virtual void Activate()
        {
            if (_disposed)
            {
                Debug.LogError($"[{_scopeId}] Cannot activate disposed scope");
                return;
            }

            if (_isActive)
            {
                Debug.LogWarning($"[{_scopeId}] Scope already active");
                return;
            }

            _isActive = true;
            Debug.Log($"[{_scopeId}] Scope activated");

            AssetMonitorBridge.ReportScopeStateChanged(_scopeId, true);
        }

        public virtual void Deactivate()
        {
            if (!_isActive) return;

            Debug.Log($"[{_scopeId}] Deactivating scope");
            _loader?.ClearCache();
            _isActive = false;

            AssetMonitorBridge.ReportScopeStateChanged(_scopeId, false);
        }

        public virtual void Dispose()
        {
            if (_disposed) return;

            Debug.Log($"[{_scopeId}] Disposing scope");

            try { _cts.Cancel(); } catch { /* ignore */ }

            Deactivate();
            AssetMonitorBridge.ReportScopeCleared(_scopeId);

            _loader?.Dispose();
            _loader = null;

            _cts.Dispose();
            _disposed = true;
        }
    }
}
