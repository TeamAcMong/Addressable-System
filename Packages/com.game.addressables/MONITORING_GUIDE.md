# Monitoring Guide - Real-Time Asset Tracking

## 🎯 Overview

Addressable Manager v2.0 includes **real-time monitoring** that automatically tracks all asset operations and displays them in the Dashboard.

This guide explains how monitoring works and how to use it effectively.

---

## 🚀 Quick Setup

### Option 1: Automatic (Recommended)

Monitoring is **automatically enabled** in Editor. Just open the Dashboard:

1. Enter Play Mode
2. Press `Ctrl+Alt+A` (or **Window → Addressable Manager → Dashboard**)
3. You'll see all scope activations immediately!

### Option 2: Add Monitoring Helper (Visual Indicator)

For a visual reminder that monitoring is active:

1. Create an empty GameObject in your scene
2. **Add Component → Addressable Manager → Monitoring Helper**
3. Enable "Enable Monitoring" checkbox
4. Enter Play Mode

That's it! The Dashboard will now show real-time data.

---

## 🏗️ How It Works

### Architecture

```
Runtime Code                  Bridge                 Editor Code
─────────────────────────────────────────────────────────────────
BaseAssetScope     ──────>   AssetMonitorBridge  ──────>  EditorAssetMonitor
  └─ Activate()                                              └─ AssetTrackerService
  └─ Deactivate()                                                └─ Dashboard Window
  └─ Dispose()

AssetLoader        ──────>   Extension Methods   ──────>  PerformanceMetrics
  └─ LoadAsync()              └─ .Monitored()
  └─ Release()
```

### Data Flow

1. **Runtime**: Scope activates → calls `AssetMonitorBridge.ReportScopeRegistered()`
2. **Bridge**: Notifies all registered monitors
3. **Editor**: `EditorAssetMonitor` receives event → updates `AssetTrackerService`
4. **Dashboard**: Listens to `AssetTrackerService.OnAssetsChanged` → refreshes UI

---

## 📊 What Gets Tracked

### Scope Lifecycle

✅ **Automatically tracked** (no code changes needed):
- Scope registration (when BaseAssetScope is constructed)
- Scope activation (`Activate()` called)
- Scope deactivation (`Deactivate()` called)
- Scope cleanup (`Dispose()` called)

**Example**:
```csharp
// This is automatically tracked:
var globalScope = GlobalAssetScope.Instance;
// Dashboard immediately shows "Global Scope - ACTIVE"
```

### Asset Operations

⚠️ **Requires using extension methods** (see below):
- Asset loads (address, type, duration)
- Cache hits vs cache misses
- Reference counts
- Memory usage (estimated)
- Load failures

---

## 🔧 Using Monitored Asset Loading

To track individual asset loads, use the **extension methods**:

### Before (No Tracking)

```csharp
// ❌ Not tracked by Dashboard
var handle = await loader.LoadAssetAsync<Sprite>("UI/Logo");
```

### After (With Tracking)

```csharp
using AddressableManager.Loaders; // For extension methods

// ✅ Tracked by Dashboard
var handle = await loader.LoadAssetAsyncMonitored<Sprite>(
    "UI/Logo",
    scopeName: "Global" // Specify which scope this belongs to
);
```

### Extension Methods Available

```csharp
// Load by address
await loader.LoadAssetAsyncMonitored<T>(address, scopeName);

// Load by AssetReference
await loader.LoadAssetAsyncMonitored<T>(assetReference, scopeName);

// Load by label
await loader.LoadAssetsByLabelAsyncMonitored<T>(label, scopeName);

// Release (reports to Dashboard)
handle.ReleaseMonitored(address);
```

### Full Example

```csharp
using UnityEngine;
using AddressableManager.Scopes;
using AddressableManager.Loaders;

public class ExampleLoader : MonoBehaviour
{
    private async void Start()
    {
        var globalScope = GlobalAssetScope.Instance;
        var loader = globalScope.Loader;

        // Load with monitoring
        var logoHandle = await loader.LoadAssetAsyncMonitored<Sprite>(
            "UI/MainLogo",
            "Global"
        );

        // Do something with asset
        var logo = logoHandle.Asset;

        // Later, release with monitoring
        logoHandle.ReleaseMonitored("UI/MainLogo");
    }
}
```

Now when you open the Dashboard (`Ctrl+Alt+A`):
- **Active Assets** tab shows "UI/MainLogo"
- **Performance** tab shows load time
- **Scopes** tab shows it under "Global Scope"

---

## 📈 Dashboard Usage

### Tab 1: Active Assets

**What you see**:
- All currently loaded assets
- Address, type, scope assignment
- Reference counts
- Memory usage per asset
- Time since loaded

**Use case**:
- Find memory leaks (assets with high ref counts)
- See what's currently in memory
- Filter by scope to see scope contents

**Actions**:
- Search by name/type
- Filter by scope (Global/Session/Scene/Hierarchy)
- Click asset to see details

### Tab 2: Performance

**What you see**:
- Total assets loaded
- Cache hit ratio (% of loads from cache)
- Total memory usage
- Average load time
- Slowest 10 assets

**Use case**:
- Identify slow-loading assets
- Check if caching is working (high cache hit ratio = good!)
- Export CSV for offline analysis

**Actions**:
- Click "Export Report (CSV)" to save metrics

### Tab 3: Scopes

**What you see**:
- All 4 scope types (Global, Session, Scene, Hierarchy)
- Per-scope asset lists
- Per-scope memory usage
- Active/Inactive status

**Use case**:
- Verify scope cleanup is working
- Check memory per scope
- Manually cleanup if needed

**Actions**:
- Expand foldout to see scope contents
- Click "Cleanup" to clear individual scope
- Click "Cleanup All Scopes" to clear everything

### Tab 4: Settings

**What you see**:
- Log level control
- Auto-refresh toggle
- Refresh interval slider (100-5000ms)
- Load simulation settings (for testing)

**Use case**:
- Adjust dashboard update frequency
- Enable slow loading simulation
- Reset statistics

---

## 🎯 Best Practices

### 1. Use Extension Methods for Critical Assets

For assets you want to monitor closely:

```csharp
// ✅ Good - tracked
await loader.LoadAssetAsyncMonitored<Texture2D>("Textures/BigTexture", "Scene");
```

For temporary/debug assets that don't matter:

```csharp
// ⚠️ OK - not tracked (less overhead)
await loader.LoadAssetAsync<Sprite>("Debug/TestIcon");
```

### 2. Specify Correct Scope Names

Always pass the actual scope name:

```csharp
// ✅ Good - correct scope
var sessionLoader = SessionAssetScope.Instance.Loader;
await sessionLoader.LoadAssetAsyncMonitored<T>(address, "Session");

// ❌ Bad - wrong scope name
await sessionLoader.LoadAssetAsyncMonitored<T>(address, "Global"); // Misleading!
```

### 3. Monitor During Development

Keep Dashboard open during Play Mode testing:

1. Dock Dashboard as a tab next to Inspector
2. Enter Play Mode
3. Watch assets load in real-time
4. Check for unexpected loads or leaks

### 4. Use Performance Tab to Optimize

After a Play Mode session:

1. Go to Performance tab
2. Check "Slowest Assets"
3. Optimize those assets (compress, reduce size, etc.)
4. Export CSV to track improvements over time

### 5. Cleanup Verification

Before switching scenes:

1. Open Dashboard → Scopes tab
2. Verify Scene Scope shows 0 assets after scene unload
3. If assets remain, check your cleanup code

---

## 🔍 Debugging with Monitoring

### Finding Memory Leaks

**Symptom**: Asset count keeps growing, never decreases

**Solution**:
1. Open Dashboard → Active Assets
2. Sort by "Time Since Loaded"
3. Assets loaded >5 minutes ago are suspicious
4. Check their reference counts
5. If ref count never decreases, you have a leak!

**Example**:
```
Asset: "Audio/BGM_Battle"
Refs: 5
Loaded: 15m ago
```
↑ This asset was loaded 15 minutes ago and has 5 references.
Check your code - are you calling Retain() without Release()?

### Checking Cache Effectiveness

**Question**: Is my caching working?

**Solution**:
1. Open Dashboard → Performance
2. Check "Cache Hit Ratio"
3. **Good**: >70% (most loads from cache)
4. **Bad**: <30% (caching not working)

**Fix for low cache hit ratio**:
- Ensure you're not creating new loaders for each load
- Use scope loaders (they cache automatically)
- Don't call `ClearCache()` too often

### Verifying Scope Cleanup

**Question**: Are my scopes cleaning up properly?

**Solution**:
1. Enter Play Mode
2. Load a scene (Scene Scope gets assets)
3. Open Dashboard → Scopes → Scene Scope (should show assets)
4. Load a different scene
5. Check Scene Scope again (should be 0 assets)

If assets remain, your cleanup code has a bug!

---

## ⚙️ Advanced Configuration

### Disable Monitoring (Performance)

If you don't need monitoring (e.g., in builds), monitoring is automatically disabled outside Editor.

In Editor, if you want to disable it:

```csharp
// Monitoring is Editor-only, no runtime overhead in builds!
// To disable in Editor, just don't use .Monitored() extensions
```

### Custom Monitoring

You can implement your own `IAssetMonitor`:

```csharp
using AddressableManager.Monitoring;

public class MyCustomMonitor : IAssetMonitor
{
    public void OnAssetLoaded(string address, string type, string scope, float duration, bool fromCache)
    {
        // Your custom logic (e.g., analytics, logging)
        MyAnalytics.TrackAssetLoad(address, duration);
    }

    // Implement other methods...
}

// Register it
AssetMonitorBridge.RegisterMonitor(new MyCustomMonitor());
```

---

## 🐛 Troubleshooting

### Dashboard Shows No Data

**Cause**: Monitoring not active or no assets loaded yet

**Solution**:
1. Ensure you're in Play Mode
2. Load some assets using scopes
3. Check that scopes are activated
4. Use `.Monitored()` extension methods for asset loads

### Assets Not Appearing in Dashboard

**Cause**: Not using monitored extension methods

**Solution**:
Use `LoadAssetAsyncMonitored()` instead of `LoadAssetAsync()`:

```csharp
// ❌ Not tracked
await loader.LoadAssetAsync<T>(address);

// ✅ Tracked
await loader.LoadAssetAsyncMonitored<T>(address, scopeName);
```

### Scopes Show But No Assets

**Cause**: Scopes are tracked automatically, but assets require extension methods

**Solution**:
- Scopes are always tracked (Activate/Deactivate)
- Assets require using `.Monitored()` extensions
- Check your loading code uses the right methods

### Memory Numbers Seem Wrong

**Cause**: Memory is estimated, not actual

**Solution**:
- Memory values are rough estimates based on asset type
- For accurate memory, use Unity Profiler
- Dashboard is for relative comparison, not absolute values

---

## 📚 API Reference

### AssetMonitorBridge (Runtime)

```csharp
// Register a custom monitor
AssetMonitorBridge.RegisterMonitor(IAssetMonitor monitor);

// Unregister
AssetMonitorBridge.UnregisterMonitor(IAssetMonitor monitor);

// Report events (called by framework, you rarely need these)
AssetMonitorBridge.ReportAssetLoaded(address, type, scope, duration, fromCache);
AssetMonitorBridge.ReportAssetReleased(address, type);
AssetMonitorBridge.ReportScopeRegistered(scopeName, isActive);
AssetMonitorBridge.ReportScopeStateChanged(scopeName, isActive);
AssetMonitorBridge.ReportScopeCleared(scopeName);
```

### Extension Methods (Runtime)

```csharp
// Load with monitoring
await loader.LoadAssetAsyncMonitored<T>(address, scopeName);
await loader.LoadAssetAsyncMonitored<T>(assetReference, scopeName);
await loader.LoadAssetsByLabelAsyncMonitored<T>(label, scopeName);

// Release with monitoring
handle.ReleaseMonitored(address);
```

### AssetTrackerService (Editor Only)

```csharp
var tracker = AssetTrackerService.Instance;

// Get all tracked assets
var assets = tracker.TrackedAssets;

// Get assets by scope
var globalAssets = tracker.GetAssetsByScope("Global");

// Detect leaks
var leaks = tracker.DetectPotentialLeaks(minutesThreshold: 5);

// Stats
var totalMemory = tracker.TotalMemoryUsage;
var cacheHits = tracker.CacheHits;
var cacheMisses = tracker.CacheMisses;
var cacheRatio = tracker.CacheHitRatio;
```

---

## ✅ Summary

**Monitoring is:**
- ✅ Automatic for scopes (no code changes)
- ⚠️ Manual for assets (use `.Monitored()` extensions)
- ✅ Editor-only (zero runtime overhead in builds)
- ✅ Real-time (updates every 500ms by default)
- ✅ Extensible (implement `IAssetMonitor` for custom tracking)

**To enable full monitoring:**
1. Enter Play Mode
2. Open Dashboard (`Ctrl+Alt+A`)
3. Use `.LoadAssetAsyncMonitored()` for assets you want to track
4. Watch real-time data appear!

**Most common issue:**
- "I see scopes but no assets" → Use `.Monitored()` extension methods!

---

**For more info:**
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) - Complete Editor features guide
- [README.md](README.md) - Package overview
- [QUICK_REFERENCE.md](QUICK_REFERENCE.md) - API reference

Happy monitoring! 🎉
