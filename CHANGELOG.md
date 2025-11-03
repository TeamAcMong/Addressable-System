# Changelog

All notable changes to this package will be documented in this file.

## [2.2.0] - 2025-01-XX - Enhanced Features

### 🎉 Major Features

#### 1. Thread-Safe Loading
- **ThreadSafeAssetLoader**: New thread-safe wrapper for background thread loading
  - Automatically dispatches operations to Unity's main thread
  - Zero overhead on main thread (direct passthrough)
  - Full AssetLoader API support
  - Clear error messages for thread safety violations
- **UnityMainThreadDispatcher**: Queue-based dispatcher for cross-thread operations
- **LoadOperation<T>**: Wrapper for queued async operations

#### 2. SmartAssetHandle - Automatic Memory Management
- **SmartAssetHandle<T>**: Auto-dispose wrapper implementing IDisposable
  - Works with C# `using` statements for automatic cleanup
  - GC finalizer as safety net if dispose is forgotten
  - Optional auto-release toggle at runtime
  - Implicit conversions for convenience
- **AssetHandleExtensions**: Extension methods for easy conversion
  - `.ToSmart()` - Convert existing handles
  - `.LoadAssetSmartAsync()` - Load directly as smart handle

#### 3. Result<T> Pattern - Explicit Error Handling
- **LoadResult<T>**: Rust-inspired Result pattern for explicit error handling
  - Pattern matching support (Match, Map, FlatMap)
  - Multiple unwrap strategies (Unwrap, UnwrapOr, UnwrapOrElse)
  - Implicit bool conversion for easy checking
- **LoadError**: Comprehensive error information
  - 11 specific error codes (InvalidAddress, AssetNotFound, ThreadSafetyViolation, etc.)
  - Detailed troubleshooting hints for each error type
  - Address, message, hint, and exception tracking
- **LoadAsyncSafe() methods**: New methods in AssetLoader returning LoadResult<T>
  - LoadAssetAsyncSafe<T>(string address)
  - LoadAssetAsyncSafe<T>(AssetReference)
  - LoadAssetsByLabelAsyncSafe<T>(string label)

#### 4. Dynamic Pools - Auto-Sizing
- **DynamicPool<T>**: Self-adjusting pool that grows/shrinks based on usage
  - Automatic growth when usage exceeds threshold
  - Automatic shrinking after sustained low usage
  - Configurable min/max capacity limits
  - Smart delayed shrinking to prevent thrashing
- **DynamicPoolConfig**: Flexible configuration system
  - 3 presets: Default (balanced), Conservative (high perf), Aggressive (low memory)
  - Fixed() preset for traditional static pools
  - Full validation with helpful error messages
- **AddressablePoolManager enhancements**:
  - CreateDynamicPoolAsync() for creating auto-sizing pools
  - GetDynamicPoolStats() for extended statistics
  - ResizePool() for manual capacity adjustment
  - IsDynamicPool() to check pool type

### Added

**Core Files**:
- `Runtime/Threading/UnityMainThreadDispatcher.cs` - Main thread dispatcher singleton
- `Runtime/Threading/LoadOperation.cs` - Queued load operation wrappers
- `Runtime/Threading/ThreadSafeAssetLoader.cs` - Thread-safe loader wrapper
- `Runtime/Core/SmartAssetHandle.cs` - Auto-dispose handle wrapper
- `Runtime/Core/AssetHandleExtensions.cs` - Extension methods for smart handles
- `Runtime/Core/LoadError.cs` - Error codes and information
- `Runtime/Core/LoadResult.cs` - Result pattern implementation
- `Runtime/Pooling/DynamicPool.cs` - Auto-sizing pool wrapper
- `Runtime/Pooling/DynamicPoolConfig.cs` - Configuration for dynamic pools

**New Namespaces**:
- `AddressableManager.Threading` - Thread-safety infrastructure
- Enhanced `AddressableManager.Core` - Smart handles and error handling
- Enhanced `AddressableManager.Pooling` - Dynamic pool system

### Changed
- **AssetLoader**: Added thread safety checks with detailed error messages
  - AssertMainThread() validates Unity main thread usage
  - Captures main thread ID on first loader creation
  - Updated XML documentation with thread safety warnings
- **AssetLoader**: Added Safe methods region with 3 new LoadAsyncSafe() methods
- **AddressablePoolManager**: Added dynamic pool support with 4 new methods

### Improved
- **Error Handling**: No more null returns - explicit LoadResult<T> with detailed errors
- **Memory Management**: Optional automatic cleanup via SmartAssetHandle
- **Thread Safety**: Can now load from background threads via ThreadSafeAssetLoader
- **Pool Efficiency**: Automatic sizing reduces memory waste and prevents pool exhaustion
- **Developer Experience**: Better error messages, hints, and documentation

### Migration Guide

**Thread Safety**:
```csharp
// Before (would crash):
await Task.Run(async () => {
    var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon"); // CRASH!
});

// After (works):
var threadSafeLoader = new ThreadSafeAssetLoader("MyScope");
await Task.Run(async () => {
    var handle = await threadSafeLoader.LoadAssetAsync<Sprite>("UI/Icon"); // OK!
});
```

**Memory Management**:
```csharp
// Before (manual):
var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon");
handle.Retain();
// ... use ...
handle.Release(); // Easy to forget!

// After (automatic):
using var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon").ToSmart();
// Auto-released on scope exit
```

**Error Handling**:
```csharp
// Before (null checks):
var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon");
if (handle == null) {
    Debug.LogError("Failed to load"); // No details!
    return;
}

// After (explicit):
var result = await loader.LoadAssetAsyncSafe<Sprite>("UI/Icon");
if (result.IsSuccess) {
    using var handle = result.Value;
} else {
    Debug.LogError(result.Error); // Detailed error with hints!
}
```

**Dynamic Pools**:
```csharp
// Before (fixed size):
await poolManager.CreatePoolAsync("Enemies/Zombie", preloadCount: 10, maxSize: 50);

// After (auto-sizing):
await poolManager.CreateDynamicPoolAsync(
    "Enemies/Zombie",
    DynamicPoolConfig.Default,
    preloadCount: 10
);
```

### Backward Compatibility
- ✅ All existing code continues to work unchanged
- ✅ New features are opt-in via extensions and new methods
- ✅ No breaking changes to existing APIs
- ✅ Thread safety checks only fire on actual violations

---

## [2.1.0] - 2025-01-XX - Automatic Monitoring

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
