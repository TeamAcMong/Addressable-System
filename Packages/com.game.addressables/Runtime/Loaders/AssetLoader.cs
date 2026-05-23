using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using AddressableManager.Configs;
using AddressableManager.Core;
#if UNITY_EDITOR
using AddressableManager.Monitoring;
#endif

namespace AddressableManager.Loaders
{
    /// <summary>
    /// Core asset loader with caching, reference counting, and lifecycle management
    /// Automatically monitors all operations in Editor for Dashboard tracking
    /// </summary>
    public class AssetLoader : IDisposable
    {
        // Cache: key = (address + type), value = handle (IDisposable so we can release without knowing T)
        private readonly Dictionary<string, IDisposable> _assetCache = new();

        // Track all active handles for cleanup
        private readonly List<IDisposable> _activeHandles = new();

        private bool _disposed;

        // Scope name for monitoring (Editor-only, zero overhead in builds)
        private readonly string _scopeName;

        /// <summary>
        /// Create AssetLoader with optional scope name for monitoring
        /// </summary>
        /// <param name="scopeName">Scope name for Dashboard tracking (Editor-only)</param>
        public AssetLoader(string scopeName = "Unknown")
        {
            _scopeName = scopeName;
        }

        #region Load by Address

        /// <summary>
        /// Load asset asynchronously by address
        /// Automatically monitored in Editor for Dashboard tracking
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(string address)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(address))
            {
                Debug.LogError("[AssetLoader] Address cannot be null or empty");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            // Create cache key with type to allow different types for same address
            string cacheKey = $"{address}_{typeof(T).Name}";

            // Check cache first
            if (_assetCache.TryGetValue(cacheKey, out var cachedObj))
            {
                var cachedHandle = cachedObj as IAssetHandle<T>;
                if (cachedHandle != null && cachedHandle.IsValid)
                {
                    LogVerbose($"[AssetLoader] Cache hit for: {address}");
                    cachedHandle.Retain();

#if UNITY_EDITOR
                    // Report cache hit to monitoring
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        true // from cache
                    );
#endif

                    return cachedHandle;
                }

                // Stale entry — purge before re-loading
                _assetCache.Remove(cacheKey);
                if (cachedObj != null) _activeHandles.Remove(cachedObj);
            }

            try
            {
                LogVerbose($"[AssetLoader] Loading asset: {address}");
                var operation = Addressables.LoadAssetAsync<T>(address);
                await operation.Task;

                // The loader may have been disposed (e.g. owner GameObject destroyed) during
                // the await. Don't cache into a torn-down loader — release immediately.
                if (_disposed)
                {
                    if (operation.IsValid()) Addressables.Release(operation);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handle = new AssetHandle<T>(operation);

                    // Cache the handle
                    _assetCache[cacheKey] = handle;
                    _activeHandles.Add(handle);

                    LogVerbose($"[AssetLoader] Successfully loaded: {address}");

#if UNITY_EDITOR
                    // Report successful load to monitoring
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        false // not from cache
                    );
#endif

                    return handle;
                }

                Debug.LogError($"[AssetLoader] Failed to load asset: {address}. Error: {operation.OperationException}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading asset: {address}. Error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Load by AssetReference

        /// <summary>
        /// Load asset by AssetReference
        /// Automatically monitored in Editor for Dashboard tracking
        /// </summary>
        public async Task<IAssetHandle<T>> LoadAssetAsync<T>(AssetReference assetReference)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (assetReference == null || !assetReference.RuntimeKeyIsValid())
            {
                Debug.LogError("[AssetLoader] Invalid AssetReference");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            var address = assetReference.AssetGUID;
            string cacheKey = $"{address}_{typeof(T).Name}";

            // Check cache
            if (_assetCache.TryGetValue(cacheKey, out var cachedObj))
            {
                var cachedHandle = cachedObj as IAssetHandle<T>;
                if (cachedHandle != null && cachedHandle.IsValid)
                {
                    cachedHandle.Retain();

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        true // from cache
                    );
#endif

                    return cachedHandle;
                }

                _assetCache.Remove(cacheKey);
                if (cachedObj != null) _activeHandles.Remove(cachedObj);
            }

            try
            {
                var operation = assetReference.LoadAssetAsync<T>();
                await operation.Task;

                if (_disposed)
                {
                    if (operation.IsValid()) Addressables.Release(operation);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handle = new AssetHandle<T>(operation);

                    _assetCache[cacheKey] = handle;
                    _activeHandles.Add(handle);

#if UNITY_EDITOR
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    AssetMonitorBridge.ReportAssetLoaded(
                        address,
                        typeof(T).Name,
                        _scopeName,
                        loadDuration,
                        false // not from cache
                    );
#endif

                    return handle;
                }

                Debug.LogError($"[AssetLoader] Failed to load AssetReference. Error: {operation.OperationException}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading AssetReference: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Load Multiple by Label

        /// <summary>
        /// Load multiple assets by label
        /// Automatically monitored in Editor for Dashboard tracking
        /// </summary>
        public async Task<List<IAssetHandle<T>>> LoadAssetsByLabelAsync<T>(string label)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot load from disposed loader");
                return null;
            }

            if (string.IsNullOrEmpty(label))
            {
                Debug.LogError("[AssetLoader] Label cannot be null or empty");
                return null;
            }

#if UNITY_EDITOR
            var startTime = Time.realtimeSinceStartup;
#endif

            try
            {
                LogVerbose($"[AssetLoader] Loading assets with label: {label}");
                var operation = Addressables.LoadAssetsAsync<T>(label, null);
                await operation.Task;

                if (_disposed)
                {
                    if (operation.IsValid()) Addressables.Release(operation);
                    return null;
                }

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    var handles = new List<IAssetHandle<T>>();

                    // Create a shared tracker for the list operation. Start with refcount 1 so the
                    // tracker itself keeps the underlying handle alive; we release it once after the
                    // foreach so that the surviving refcount is equal to the number of ListItemHandles.
                    var sharedTracker = new SharedListOperationTracker<T>(operation);
                    _activeHandles.Add(sharedTracker);

                    foreach (var asset in operation.Result)
                    {
                        var wrapper = new ListItemHandle<T>(sharedTracker, asset);
                        handles.Add(wrapper);
                        _activeHandles.Add(wrapper);
                    }

                    // Drop our extra reference; if results were empty the tracker disposes immediately
                    // and releases the underlying Addressables handle.
                    sharedTracker.Release();

                    LogVerbose($"[AssetLoader] Loaded {handles.Count} assets with label: {label}");

#if UNITY_EDITOR
                    // Report each asset loaded
                    var loadDuration = Time.realtimeSinceStartup - startTime;
                    var avgTimePerAsset = handles.Count > 0 ? loadDuration / handles.Count : loadDuration;

                    foreach (var _ in handles)
                    {
                        AssetMonitorBridge.ReportAssetLoaded(
                            $"{label}/*",
                            typeof(T).Name,
                            _scopeName,
                            avgTimePerAsset,
                            false // not from cache
                        );
                    }
#endif

                    return handles;
                }

                Debug.LogError($"[AssetLoader] Failed to load assets by label: {label}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception loading by label: {label}. Error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Instantiate

        /// <summary>
        /// Instantiate a GameObject from addressable
        /// </summary>
        public async Task<GameObject> InstantiateAsync(string address, Transform parent = null)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot instantiate from disposed loader");
                return null;
            }

            try
            {
                var operation = Addressables.InstantiateAsync(address, parent);
                await operation.Task;

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    return operation.Result;
                }

                Debug.LogError($"[AssetLoader] Failed to instantiate: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception instantiating: {address}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Instantiate with position and rotation
        /// </summary>
        public async Task<GameObject> InstantiateAsync(string address, Vector3 position, Quaternion rotation, Transform parent = null)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot instantiate from disposed loader");
                return null;
            }

            try
            {
                var operation = Addressables.InstantiateAsync(address, position, rotation, parent);
                await operation.Task;

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    return operation.Result;
                }

                Debug.LogError($"[AssetLoader] Failed to instantiate: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception instantiating: {address}. Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Release instantiated GameObject
        /// </summary>
        public bool ReleaseInstance(GameObject instance)
        {
            if (instance == null) return false;

            try
            {
                return Addressables.ReleaseInstance(instance);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Error releasing instance: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Preload & Download

        /// <summary>
        /// Preload/Download asset without loading it into memory.
        /// Returns true on success, false otherwise.
        /// </summary>
        public async Task<bool> DownloadDependenciesAsync(string address)
        {
            if (_disposed)
            {
                Debug.LogError("[AssetLoader] Cannot download from disposed loader");
                return false;
            }

            try
            {
                var operation = Addressables.DownloadDependenciesAsync(address);
                await operation.Task;

                bool succeeded = operation.Status == AsyncOperationStatus.Succeeded;

                if (!succeeded)
                {
                    Debug.LogError($"[AssetLoader] Failed to download dependencies: {address}");
                }

                if (operation.IsValid())
                {
                    Addressables.Release(operation);
                }

                return succeeded;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception downloading dependencies: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get download size for address
        /// </summary>
        public async Task<long> GetDownloadSizeAsync(string address)
        {
            try
            {
                var operation = Addressables.GetDownloadSizeAsync(address);
                await operation.Task;

                if (operation.Status == AsyncOperationStatus.Succeeded)
                {
                    long size = operation.Result;
                    Addressables.Release(operation);
                    return size;
                }

                Debug.LogError($"[AssetLoader] Failed to get download size: {address}");
                if (operation.IsValid()) Addressables.Release(operation);
                return 0;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AssetLoader] Exception getting download size: {ex.Message}");
                return 0;
            }
        }

        #endregion

        #region Cache Management

        /// <summary>
        /// Clear all cached handles and release them
        /// </summary>
        public void ClearCache()
        {
            LogVerbose($"[AssetLoader] Clearing cache ({_assetCache.Count} items)");

            foreach (var obj in _assetCache.Values)
            {
                obj?.Dispose();
            }

            _assetCache.Clear();
            _activeHandles.Clear();
        }

        /// <summary>
        /// Release specific asset by address (all types stored for that address).
        /// Each cached handle is disposed once.
        /// </summary>
        public void ReleaseAsset(string address)
        {
            if (string.IsNullOrEmpty(address)) return;

            string prefix = address + "_";
            List<string> keysToRemove = null;

            foreach (var kvp in _assetCache)
            {
                if (!kvp.Key.StartsWith(prefix)) continue;

                keysToRemove ??= new List<string>();
                keysToRemove.Add(kvp.Key);

                kvp.Value?.Dispose();
                if (kvp.Value != null) _activeHandles.Remove(kvp.Value);
            }

            if (keysToRemove != null)
            {
                foreach (var key in keysToRemove)
                {
                    _assetCache.Remove(key);
                }
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public (int cachedAssets, int activeHandles) GetCacheStats()
        {
            return (_assetCache.Count, _activeHandles.Count);
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;

            LogVerbose("[AssetLoader] Disposing loader and releasing all assets");
            ClearCache();
            _disposed = true;
        }

        #endregion

        // Gate verbose informational logs behind DebugSettings.logLevel so shipping
        // builds don't burn GC on string-interpolation for every cache hit.
        private static void LogVerbose(string message)
        {
            if (DebugSettings.IsVerbose)
            {
                Debug.Log(message);
            }
        }
    }

    /// <summary>
    /// Tracks a shared list operation for reference counting
    /// </summary>
    internal class SharedListOperationTracker<T> : IDisposable
    {
        private readonly AsyncOperationHandle<System.Collections.Generic.IList<T>> _listHandle;
        private int _referenceCount;
        private bool _disposed;

        public bool IsValid => _listHandle.IsValid() && _listHandle.Status == AsyncOperationStatus.Succeeded;
        public AsyncOperationStatus Status => _listHandle.Status;

        public SharedListOperationTracker(AsyncOperationHandle<System.Collections.Generic.IList<T>> listHandle)
        {
            _listHandle = listHandle;
            // Start with 1 reference — caller (AssetLoader.LoadAssetsByLabelAsync) drops it
            // once it has wrapped all items in ListItemHandle. Empty-result label loads then
            // release immediately instead of leaking the list handle.
            _referenceCount = 1;
        }

        public void Retain()
        {
            if (!_disposed) _referenceCount++;
        }

        public void Release()
        {
            if (_disposed) return;

            _referenceCount--;
            if (_referenceCount <= 0)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            if (_listHandle.IsValid())
            {
                Addressables.Release(_listHandle);
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Handle for individual items from a list operation
    /// </summary>
    internal class ListItemHandle<T> : IAssetHandle<T>
    {
        private readonly SharedListOperationTracker<T> _tracker;
        private readonly T _asset;
        private int _referenceCount;
        private bool _disposed;

        public T Asset => _asset;
        public bool IsValid => _tracker.IsValid && _asset != null;
        public AsyncOperationStatus Status => _tracker.Status;
        public float Progress => 1f; // Already loaded
        public int ReferenceCount => _referenceCount;

        public ListItemHandle(SharedListOperationTracker<T> tracker, T asset)
        {
            _tracker = tracker;
            _asset = asset;
            _referenceCount = 1;
            _tracker.Retain(); // Increment shared tracker
        }

        public void Retain()
        {
            if (_disposed)
            {
                Debug.LogWarning("[ListItemHandle] Cannot retain disposed handle");
                return;
            }
            _referenceCount++;
        }

        public void Release()
        {
            if (_disposed)
            {
                Debug.LogWarning("[ListItemHandle] Handle already disposed");
                return;
            }

            _referenceCount--;

            if (_referenceCount <= 0)
            {
                Dispose();
            }
        }

        public AsyncOperationHandle<T> GetHandle()
        {
            // Cannot convert IList handle to single item handle
            Debug.LogWarning("[ListItemHandle] GetHandle() not supported for list-loaded items");
            return default;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _tracker.Release(); // Decrement shared tracker
            _disposed = true;
            _referenceCount = 0;
        }
    }
}
