# Changelog

All notable changes to this package will be documented in this file.

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
