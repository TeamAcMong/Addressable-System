# Changelog

All notable changes to this package will be documented in this file.

## [2.3.0] - 2026-05-23 - UniTask switching + dependency auto-detection

### Added
- **Automatic `Task` ↔ `UniTask` switching.** The Runtime asmdef now declares a `versionDefines` entry that defines `UNITASK_PRESENT` whenever `com.cysharp.unitask 2.3.0+` is installed in the consumer project. Every public async method (`AssetLoader.LoadAssetAsync`, `Assets.Load`, `AddressablesFacade.LoadGlobalAsync`, `AddressablePoolManager.CreatePoolAsync`, `ProgressiveAssetLoader.LoadAssetWithProgressAsync` etc. — 30 signatures across 6 files) now returns `UniTask<T>` when the define is active, `Task<T>` otherwise. Body await sites are awaiter-compatible and unchanged.
- `Task.Yield()` / `Task.WhenAll(…)` / `new Task<T>[…]` inside `ProgressiveAssetLoader.LoadMultipleWithProgressAsync` are bracketed with `#if UNITASK_PRESENT` so they switch to the corresponding `UniTask` calls when UniTask is present (avoids the cost of awaiting a `Task.YieldAwaitable` from inside a `UniTask` async method).
- UniTask asmdef listed under the Runtime asmdef `references`. Unity ignores the reference gracefully when UniTask is not installed.

### Fixed
- `Editor/Inspectors/MonitoringHelperInspector.cs` shipped without a `.meta` file in 2.2.0, triggering Unity's "Asset has no meta file, but it's in an immutable folder. The asset will be ignored." warning when the package was consumed via UPM. Generated a stable GUID + `MonoImporter` meta so the inspector loads on first import.

### Notes
- This is a backwards-compatible change for projects **without** UniTask. Projects **with** UniTask installed will see public return types change from `Task<T>` to `UniTask<T>`. The two are awaiter-compatible (you can `await` a `UniTask` from a `Task` async method and vice versa), but code that captured the return value as a concrete `Task<T>` variable will need to either uninstall UniTask, refactor to `var`, or call `.AsTask()` on the result.
- Editor asmdef is unchanged — Editor code uses no `Task`/`UniTask` surface.

## [2.2.1] - 2026-05-23 - Documentation Refresh

Doc-only release; no code changes.

### Changed
- **`README.md`** rewritten end-to-end against the current API surface: install via tagged git URL, scope concept table, scoped/static/direct usage paths, pooling + progress + monitoring snippets, architecture diagram, migration notes for 2.1 → 2.2 and 2.0 → 2.1. ~60 % shorter than the previous version.
- **`MONITORING_GUIDE.md`** rewritten from scratch around the new "always-on, Editor-only" monitoring model. Removed the obsolete "use extension methods" framing, the made-up `LoadAssetAsync(address, scopeName)` overload, and the deprecated `handle.Release(address)` pattern. Added scope-name resolution table, `IAssetMonitor` example, build-time guarantees.
- **`EDITOR_TOOLS_GUIDE.md`** rewritten to match the real API: every `LoadAssetAsync<T>(address, scopeName)` call corrected, every `.Monitored()` reference removed, `ScopeManager` examples updated, recipes consolidated, troubleshooting table tightened.

### Notes
- The docs now treat `Assets` (static) and `AddressablesFacade` (MonoBehaviour) as parity-equivalent entry points (`Assets` gained the missing API in 2.2.0).
- Cross-doc links and footer version bumped to 2.2.1.

## [2.2.0] - 2026-05-23 - Audit-Driven Hardening

A pre-ship audit found three blocker-class Addressables ref-count leaks and a
handful of correctness / API issues. Every finding is addressed below.

### 🔴 Blocker fixes
- **`ProgressiveAssetLoader` leak** — `LoadAssetWithProgressAsync` /
  `DownloadWithProgressAsync` now `Addressables.Release` the underlying handle
  in `finally` when the load fails or throws, instead of orphaning it.
- **`AddressablePoolManager` template-handle leak** — the prefab handle
  acquired during `CreatePoolAsync` is now retained per-pool and disposed in
  `ClearPool` / `ClearAllPools` / `Dispose`. Previously every pool created
  a permanent +1 Addressables ref-count.
- **`MonitoredAssetLoader` build-time leak + AssetReference bypass** —
  rewritten as a thin forwarder around `AssetLoader` (which already owns
  monitoring under `#if UNITY_EDITOR`), so monitoring dispatch no longer
  ships in builds and `LoadAssetAsync<T>(AssetReference)` no longer rewrites
  the cache key through `assetReference.AssetGUID`.

### 🟠 High fixes
- **`AssetLoader.ReleaseAsset`** — the always-null `IAssetHandle<object>`
  cast is replaced with a non-generic `IDisposable` cache. Calling
  `ReleaseAsset(address)` now actually releases every cached handle
  matching that address.
- **`SceneAssetScope`** — collapsed the dual `sceneUnloaded` + `OnDestroy`
  dispose paths into a single `Destroy(gameObject)` funnel so cleanup runs
  exactly once.
- **`HierarchyAssetScope` / `BaseAssetScope`** — added a `DisposedToken`
  (`CancellationToken`) that fires when the scope is disposed. Long-running
  awaits can pass it to `Task.WaitAsync` and unwind cleanly when the owning
  GameObject is destroyed mid-load.
- **`ScopeManager`** — added a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`
  reset that disposes lingering scopes and nulls the static singleton on
  domain reload / new Play sessions.
- **`AddressableProgressBar`** — TextMeshPro is no longer a hard compile-time
  dependency. The runtime asmdef now defines `TMP_PRESENT` only when
  `com.unity.textmeshpro 3.0.0+` is installed; without TMP the component
  falls back to plain `UnityEngine.UI.Text`.

### 🟡 Medium fixes
- **`SharedListOperationTracker`** — initial refcount changed from 0 to 1,
  so empty-result label loads release the list handle immediately instead
  of leaking it until the loader disposes.
- **`AddressablesFacade.OnDestroy`** — now disposes the global scope and
  ends any active session before clearing the singleton, fixing repeat
  Editor Play-mode iterations leaking handles into the next session.
- **Double monitoring removed** — `MonitoredAssetLoader` no longer
  re-reports loads that `AssetLoader` already reported (Dashboard counts
  were doubled in Editor).
- **`DebugSettings`** — the `Resources.Load<DebugSettings>("AddressableManager/DebugSettings")`
  lookup is now inside `#if UNITY_EDITOR`. Shipping builds get a transient
  default instance instead of warning at runtime; added a static
  `IsVerbose` accessor used to gate informational logs.
- **README requirements** — Unity floor corrected to 2022.3 and the footer
  bumped to 2.2.0; flagged TextMeshPro as optional.

### 🟢 Low fixes
- **Verbose logging gated** — `AssetLoader` cache-hit / load-success
  `Debug.Log` calls go through `DebugSettings.IsVerbose`. Mobile shipping
  builds no longer pay a GC-allocating string interpolation per cache hit.
- **`DownloadDependenciesAsync` return type** — changed from `Task<long>`
  with a magic `1`/`0` sentinel to `Task<bool>` matching its semantics.
- **`PoolConfiguration.destroyOnFull`** — annotated `[HideInInspector]` +
  `[Obsolete]`; the underlying `UnityEngine.Pool.ObjectPool` always
  destroys excess instances above `maxSize`, so the flag had no effect.
- **`AddressableManager.Editor.asmdef`** — opaque GUID references
  replaced with portable name references (`AddressableManager`,
  `Unity.Addressables`, `Unity.Addressables.Editor`, `Unity.ResourceManager`).
- **`AssetLoaderExtensions`** — the deprecated forwarder class is removed.
  Use `AssetLoader.LoadAssetAsync` directly (monitoring is automatic).

### ⚪ Nice-to-have
- **`AssetMonitorBridge` thread-safety** — switched the listener list to a
  copy-on-write `IAssetMonitor[]` with a lock around register/unregister,
  plus a `SubsystemRegistration` reset that drops stale Editor listeners
  on domain reload.
- **`Assets` facade parity** — added `GetPoolStats`, `ClearPool(address)`,
  `SetPoolFactory`, `ReleaseInstance`, and `GetOrCreateSceneScope` so the
  static facade now matches `AddressablesFacade`.
- **`MonitoringHelperEditor` moved** — the nested `CustomEditor` was
  promoted to `Editor/Inspectors/MonitoringHelperInspector.cs` so the
  runtime assembly no longer carries an `UnityEditor` type.
- **`ScopeManager.GetScopeMemoryUsage`** — marked `[Obsolete]` with a note
  that runtime memory tracking is not implemented; live numbers stay in
  the Editor Dashboard.

### Migration
- `AssetLoader.DownloadDependenciesAsync` now returns `Task<bool>`. Callers
  that compared the result to `1`/`0` must switch to `true`/`false`.
- `AssetLoaderExtensions.*Monitored` methods are removed. Replace with the
  plain `AssetLoader.LoadAssetAsync` overloads — monitoring is automatic.

## [2.1.0] - 2026-05-23 - Automatic Monitoring

### Maintenance
- Aligned package metadata with the rest of the DreamTech library family: minimum Unity bumped to **2022.3**, author standardised to **DreamTech**.


### 🎉 Breaking Changes (Minor)
- **AssetLoaderExtensions deprecated**: `.LoadAssetAsyncMonitored()` methods marked as obsolete
  - Migration: Simply remove `.Monitored` from method names (e.g., `LoadAssetAsync` instead of `LoadAssetAsyncMonitored`)
  - Old code will still work (backward compatible) but shows warnings

### Added
- **Automatic Monitoring**: All asset loads now automatically tracked in Dashboard (Editor-only, zero build overhead)
- **AssetLoader constructor**: Now accepts optional `scopeName` parameter for automatic scope tracking
- **Monitoring integration**: All core load methods (`LoadAssetAsync`, `LoadAssetsByLabelAsync`, etc.) include built-in monitoring

### Changed
- **AssetLoader**: Added `#if UNITY_EDITOR` wrapped monitoring calls to all load operations
- **BaseAssetScope**: Now passes scope name to AssetLoader constructor
- **ScopeManager**: Passes scope ID to AssetLoader for proper tracking
- **Documentation**: Updated README and EDITOR_TOOLS_GUIDE to reflect automatic monitoring

### Improved
- **Simplified API**: No need to remember special "Monitored" methods
- **Complete tracking**: Dashboard always has full data (no missed loads)
- **Consistent behavior**: Same methods work everywhere (Facade, Scopes, custom loaders)
- **Zero overhead**: Monitoring code completely stripped in builds

### Fixed
- Issue where users had to manually use `.LoadAssetAsyncMonitored()` extensions
- Dashboard missing data when users forgot to use monitored versions
- Inconsistent API between monitored and non-monitored loads

---

## [2.0.0] - 2025-01-XX - MAJOR UPDATE

### Added - Editor Tools & Monitoring
- **Dashboard Window**: Real-time asset monitoring with 4 tabs (Assets, Performance, Scopes, Settings)
- **Asset Tracker Service**: Centralized tracking of all loaded assets with memory usage and reference counts
- **Performance Metrics System**: Real-time performance monitoring, load time tracking, cache hit ratio analysis
- **Custom Inspectors**: Beautiful UI Toolkit inspectors for all scope components with live data
- **Progress Bar Inspector**: Interactive testing controls for AddressableProgressBar component
- **ScriptableObject Config Inspectors**: Validation and management tools for configurations

### Added - Configuration System
- **AddressablePreloadConfig**: ScriptableObject for configuring asset preloading (no more hardcoded addresses!)
- **PoolConfiguration**: Centralized pool management configuration
- **DebugSettings**: Runtime debug settings with load simulation and profiling options
- **AssetScopeType enum**: Type-safe scope selection

### Added - Runtime UI Components
- **AddressableProgressBar**: Visual progress bar component with gradient colors, smooth animation, and auto-binding
- Support for TextMeshPro for better text rendering
- Auto-hide functionality when loading completes

### Added - Context Menus & Shortcuts
- GameObject menu: Quick add scope components
- Assets menu: Create configs directly
- Window menu: Dashboard (Ctrl+Alt+A), Documentation, Settings
- Tools menu: Quick setup wizards for scopes and configs

### Added - UI/UX
- Modern UI Toolkit-based dashboard with dark theme
- Color-coded scope visualization (Green=Global, Blue=Session, Yellow=Scene, Red=Hierarchy)
- Real-time data refresh with configurable intervals
- Memory usage progress bars with color warnings
- Expandable asset lists per scope
- Export performance reports to CSV

### Improved
- Better memory size estimation for different asset types
- Leak detection algorithm for assets loaded without ref count changes
- Validation system for configs with duplicate detection
- Automatic sorting and prioritization of preload entries

### Technical
- New Editor assembly definition (AddressableManager.Editor.asmdef)
- UI Toolkit UXML/USS for modern Editor UI
- Observer pattern for real-time updates
- Lazy loading for better performance

## [1.0.3] - 2025-01-XX

### Fixed
- Fixed LoadAssetsByLabelAsync type mismatch
- Added SharedListOperationTracker and ListItemHandle classes
- Proper reference counting for label-loaded assets

## [1.0.2] - 2025-01-XX

### Changed
- Renamed namespace from Game.Addressables to AddressableManager
- Updated all runtime files with new namespace

## [1.0.1] - 2025-01-XX

### Fixed
- Fixed Convert<object>() bug
- Changed caching strategy to use type-specific cache keys

## [1.0.0] - 2025-01-XX

### Added
- Initial production release
- Core Foundation with AssetLoader
- Scope Management (Global/Session/Scene/Hierarchy)
- Object Pooling System with Factory Pattern
- Progress Tracking with Observer Pattern
- Facade APIs
