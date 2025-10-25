# Editor Tools Guide - Addressable Manager v2.0

## 🎉 Welcome to the Professional Edition!

Version 2.0 transforms Addressable Manager from a code-only library into a **professional-grade Unity package** with comprehensive Editor tools, visual debugging, and zero hardcoding required!

---

## 🚀 Quick Start

### 1. Open the Dashboard

**Window → Addressable Manager → Dashboard** (or press `Ctrl+Alt+A`)

The Dashboard is your central hub for monitoring all addressable operations in real-time.

### 2. Add Scopes to Your Scene

**GameObject → Addressable Manager → Add [Scope Type]**

Or use the quick setup:
**Tools → Addressable Manager → Quick Setup → Create All Scope Objects**

### 3. Create Configuration Files

**Assets → Addressable Manager → Create [Config Type]**

Or use the quick setup:
**Tools → Addressable Manager → Quick Setup → Create Sample Configs**

---

## 📊 Dashboard Window

Access: **Window → Addressable Manager → Dashboard**
Shortcut: `Ctrl+Alt+A`

### Tab 1: Active Assets

Real-time list of all loaded assets:
- **Search**: Filter by asset name or type
- **Scope Filter**: Show only assets from specific scopes
- **Asset Details**: Address, type, scope, reference count, memory usage, load time
- **Live Updates**: Refreshes automatically every 500ms (configurable)

**Use Case**: Monitor which assets are currently loaded and catch memory leaks

### Tab 2: Performance

Performance metrics and statistics:
- **Stat Cards**: Total assets, cache hit ratio, total memory, average load time
- **Memory Chart**: Visual graph of memory usage over time *(placeholder for future)*
- **Slowest Assets**: Top 10 slowest-loading assets
- **Export Report**: Save metrics to CSV for analysis

**Use Case**: Optimize load times and identify performance bottlenecks

### Tab 3: Scopes

Overview of all scope management:
- **4 Foldouts**: Global, Session, Scene, Hierarchy scopes
- **Per-Scope Stats**: Asset count and memory usage
- **Asset Lists**: Expandable lists showing all assets in each scope
- **Cleanup Buttons**: Clear individual scopes or all at once

**Use Case**: Manage scope lifecycles and prevent cross-scene memory leaks

### Tab 4: Settings

Runtime configuration and debugging:
- **Log Level**: Control verbosity (None, Errors Only, Warnings, All)
- **Auto Refresh**: Toggle automatic dashboard updates
- **Refresh Interval**: Adjust update frequency (100-5000ms)
- **Load Simulation**: Simulate slow loading and failures for testing
- **Reset Buttons**: Clear settings or statistics

**Use Case**: Configure debugging behavior without code changes

---

## 🎨 Custom Inspectors

### Scope Component Inspectors

When you select a GameObject with a scope component (GlobalAssetScope, SessionAssetScope, etc.), you'll see a beautiful custom inspector:

#### Header
- **Color-coded indicator**: Green (Global), Blue (Session), Yellow (Scene), Red (Hierarchy)
- **Status badge**: ACTIVE or INACTIVE

#### Status Section
- Assets Loaded count
- Total Memory usage

#### Memory Usage
- **Progress bar** showing memory consumption
- **Color warnings**: Green (<50%), Yellow (50-80%), Red (>80%)

#### Loaded Assets
- **Expandable foldout** showing all assets in this scope
- Per-asset info: Name, type, references, size, load time

#### Actions
- **Activate Scope**: Enable the scope (Play mode only)
- **Deactivate Scope**: Disable the scope
- **Cleanup Scope**: Release all assets
- **Open Dashboard**: Quick link to main dashboard

**Use Case**: Monitor individual scope behavior without leaving the Inspector

### ScriptableObject Config Inspectors

#### AddressablePreloadConfig Inspector

Special features:
- **Validate All Addresses**: Check if all entries are valid
- **Sort by Priority**: Reorder list by priority values
- **Test Load in Editor**: Simulate loading in Play mode
- **Statistics Panel**: Show total/startup/valid/invalid counts

**Use Case**: Ensure your preload configuration is error-free before building

#### Progress Bar Inspector

In **Play Mode**, you get interactive testing controls:
- **Progress Slider**: Manually set progress (0-100%)
- **Show/Hide/Reset Buttons**: Test visibility states
- **Animate Button**: Auto-animate from 0% to 100%
- **Test Status Field**: Set custom status text

**Use Case**: Perfect your loading screen UX without writing test code

---

## ⚙️ ScriptableObject Configurations

### 1. AddressablePreloadConfig

**Purpose**: Define which assets to load on startup without hardcoding addresses

**Create**: Assets → Addressable Manager → Create Preload Config

#### Configuration Options

```
Preload Entry:
├── Asset Reference: Drag asset from Addressables Groups
├── Address: Or manually enter address string
├── Scope: Which scope to load into (Global/Session/Scene/Hierarchy)
├── Load On Startup: Auto-load when game starts
├── Priority: Load order (lower = first)
└── Label: Optional debug label

Global Settings:
├── Validate On Build: Check all addresses before building
├── Fail Build On Error: Stop build if validation fails
├── Load In Parallel: Load multiple assets simultaneously
└── Max Concurrent Loads: Limit parallel operations (1-20)
```

#### Code Usage

```csharp
// Load a specific preload config
var config = Resources.Load<AddressablePreloadConfig>("MyPreloadConfig");

// Get startup assets sorted by priority
var startupAssets = config.GetStartupAssets();

foreach (var entry in startupAssets)
{
    var address = entry.GetAddress();
    var scope = entry.scope;
    // Load asset into appropriate scope...
}
```

### 2. PoolConfiguration

**Purpose**: Configure object pools centrally

**Create**: Assets → Addressable Manager → Create Pool Config

#### Configuration Options

```
Pool Settings:
├── Prefab Reference: Drag GameObject asset
├── Address: Or manual address
├── Preload Count: Instances to create upfront (0-100)
├── Max Size: Pool capacity limit (0 = unlimited)
├── Auto Create: Create pool on startup
├── Pool Root: Parent transform for pooled objects
├── Destroy On Full: Auto-destroy when at max capacity
└── Label: Optional debug label

Global Settings:
├── Default Max Size: Default for new pools
├── Default Preload Count: Default preload amount
├── Create All On Startup: Auto-create all pools
└── Cleanup On Scene Unload: Auto-cleanup on scene change
```

#### Code Usage

```csharp
var poolConfig = Resources.Load<PoolConfiguration>("MyPoolConfig");

// Get pools marked for auto-creation
var autoPools = poolConfig.GetAutoCreatePools();

foreach (var pool in autoPools)
{
    await poolManager.CreatePoolAsync(
        pool.GetAddress(),
        pool.preloadCount,
        pool.maxSize,
        pool.poolRoot
    );
}
```

### 3. DebugSettings

**Purpose**: Runtime debugging configuration

**Create**: Assets → Addressable Manager → Create Debug Settings

#### Configuration Options

```
Logging:
├── Log Level: None | Errors Only | Warnings | All
├── Log To File: Save logs to disk
└── Log File Path: Relative path for log file

Profiling:
├── Enable Profiling: Track performance metrics
├── Show Profiler Overlay: In-game stats (Editor only)
└── Record Metrics: Save metrics history

Simulation (Editor Only):
├── Simulate Slow Loading: Add artificial delay
├── Simulated Delay Ms: Delay duration (0-5000ms)
├── Simulate Failure Rate: Random failures (0-100%)
├── Simulate Network Conditions: Network type simulation
└── Network Simulation: WiFi | 4G | 3G | Slow

Validation:
├── Validate References: Check asset refs on load
├── Detect Memory Leaks: Enable leak detection
└── Leak Detection Minutes: Time threshold (1-60min)

Warnings:
├── Warn On High Ref Count: Alert for unusual refs
├── High Ref Count Threshold: Ref count limit (5-100)
├── Warn On High Memory: Alert for high usage
└── High Memory Threshold MB: Memory limit (10-1000MB)
```

#### Code Usage

```csharp
var settings = DebugSettings.Instance;

if (settings.ShouldLog(LogType.Warning))
{
    Debug.LogWarning("Something happened");
}

if (settings.simulateSlowLoading)
{
    await Task.Delay((int)settings.simulatedDelayMs);
}

if (settings.ShouldSimulateFailure())
{
    // Simulate load failure for testing
}
```

---

## 🎮 Runtime UI Components

### AddressableProgressBar

**Purpose**: Visual feedback for loading operations

**Add to Scene**: Add Component → Addressable Manager → Progress Bar

#### Inspector Setup

1. **Fill Image**: Assign an Image component with Fill type
2. **Percent Text** *(optional)*: TextMeshProUGUI for percentage
3. **Status Text** *(optional)*: TextMeshProUGUI for status messages
4. **Download Text** *(optional)*: TextMeshProUGUI for download info

#### Settings

- **Auto Find Tracker**: Automatically bind to active tracker
- **Smooth Fill**: Animate fill changes
- **Fill Speed**: Animation speed (1-20)
- **Hide When Complete**: Auto-hide at 100%
- **Hide Delay**: Delay before hiding (0-5s)
- **Gradient Colors**: Change color based on progress
  - Start Color: 0% progress (default: Red)
  - Mid Color: 50% progress (default: Yellow)
  - End Color: 100% progress (default: Green)

#### Code Usage

```csharp
// Bind to a progress tracker
progressBar.BindToTracker(myProgressTracker);

// Or manually update
progressBar.SetProgress(0.5f); // 50%
progressBar.SetStatus("Loading textures...");

// Show/hide manually
progressBar.Show();
progressBar.Hide();
progressBar.Reset();
```

#### Testing in Play Mode

In the Inspector during Play Mode:
- Drag the **Test Progress slider** to see live updates
- Click **Animate** to auto-animate 0% → 100%
- Test **Show/Hide/Reset** buttons
- Enter custom status text

---

## 🛠️ Context Menus & Shortcuts

### GameObject Menu

Right-click in Hierarchy or use top menu:

**GameObject → Addressable Manager →**
- Add Global Scope
- Add Session Scope
- Add Scene Scope
- Add Hierarchy Scope
- View in Dashboard

### Assets Menu

Right-click in Project or use top menu:

**Assets → Addressable Manager →**
- Create Preload Config
- Create Pool Config
- Create Debug Settings

### Window Menu

**Window → Addressable Manager →**
- Dashboard (`Ctrl+Alt+A`)
- Documentation
- Settings
- Clear All Caches

### Tools Menu

**Tools → Addressable Manager → Quick Setup →**
- Create All Scope Objects
- Create Sample Configs

---

## 💡 Workflow Examples

### Example 1: Setup a New Scene with Scopes

1. **Tools → Addressable Manager → Quick Setup → Create All Scope Objects**
2. Unity creates 4 GameObjects with scope components
3. Select each scope in Hierarchy to see custom inspector
4. Press Play and open **Dashboard** (`Ctrl+Alt+A`) to see scopes activate

### Example 2: Configure Asset Preloading

1. **Assets → Create → Addressable Manager → Preload Configuration**
2. Name it `GlobalPreloadConfig`
3. In Inspector:
   - Add entries by clicking `+` on the list
   - Drag assets from Addressables Groups to **Asset Reference** field
   - Set **Scope** to Global, **Load On Startup** = true
   - Set **Priority** (lower loads first)
4. Click **Validate All Addresses** to check for errors
5. Click **Sort by Priority** to reorder

### Example 3: Create a Loading Screen

1. Create a Canvas with CanvasGroup
2. **Add Component → Addressable Manager → Progress Bar**
3. Create UI hierarchy:
   ```
   Canvas
   └── LoadingScreen (CanvasGroup + AddressableProgressBar)
       ├── Background (Image)
       ├── FillBar (Image - Fill type) ← Assign to Progress Bar
       ├── PercentText (TextMeshProUGUI) ← Assign to Progress Bar
       └── StatusText (TextMeshProUGUI) ← Assign to Progress Bar
   ```
4. In code:
   ```csharp
   var tracker = new ProgressTracker();
   loadingScreen.BindToTracker(tracker);

   // Your loading code will auto-update the UI
   await LoadAssetsWithProgress(tracker);
   ```
5. **Test in Play Mode**: Use Inspector sliders and buttons

### Example 4: Debug Memory Issues

1. Enter Play Mode
2. Open **Dashboard** (`Ctrl+Alt+A`)
3. Go to **Active Assets** tab
4. Filter by scope or search for specific assets
5. Watch memory usage in real-time
6. Go to **Performance** tab
7. Check "Slowest Assets" to find bottlenecks
8. Click **Export Report** to save CSV for analysis

### Example 5: Validate Before Build

1. Select your `PreloadConfig` in Project
2. Click **Validate All Addresses** in Inspector
3. If errors appear, fix invalid entries
4. Enable **Validate On Build** checkbox
5. Optionally enable **Fail Build On Error** to prevent bad builds

---

## 📈 Best Practices

### 1. Use ScriptableObjects for Configuration

**❌ Bad - Hardcoded:**
```csharp
await loader.LoadAssetAsync<Sprite>("UI/MainMenuBackground");
await loader.LoadAssetAsync<AudioClip>("Audio/BGM_Menu");
```

**✅ Good - Configured:**
```csharp
var config = Resources.Load<AddressablePreloadConfig>("GlobalPreload");
var assets = config.GetStartupAssets();

foreach (var entry in assets)
{
    await LoadAsset(entry);
}
```

### 2. Monitor with Dashboard During Development

- Keep Dashboard open during Play Mode testing
- Watch for unexpected loads or leaks
- Use Performance tab to identify slow assets
- Export reports for team analysis

### 3. Use Custom Inspectors for Quick Debugging

- Select scope GameObjects to see live stats
- Use Cleanup buttons to test cleanup logic
- Verify reference counts in Inspector
- No need to add Debug.Log everywhere!

### 4. Test Loading Screens Interactively

- Use Progress Bar Inspector in Play Mode
- Test all visual states without writing test code
- Verify gradient colors and animations
- Ensure text displays correctly

### 5. Simulate Real Conditions

- Use DebugSettings to simulate slow networks
- Test with simulated failures
- Verify your error handling works
- Don't wait for real slow networks to test!

---

## 🎨 Color Coding Reference

Throughout the Editor tools, scopes are color-coded:

- 🟢 **Green**: Global Scope (persists forever)
- 🔵 **Blue**: Session Scope (persists between scenes)
- 🟡 **Yellow**: Scene Scope (cleared on scene unload)
- 🔴 **Red**: Hierarchy Scope (cleared on GameObject destroy)

Memory warnings:
- 🟢 **Green**: < 50% usage (healthy)
- 🟡 **Yellow**: 50-80% usage (moderate)
- 🔴 **Red**: > 80% usage (high)

---

## 🐛 Troubleshooting

### Dashboard Not Updating

**Solution**: Check Settings tab → ensure "Auto Refresh" is enabled

### Inspector Shows No Assets

**Solution**: Scope might not be active. Click "Activate Scope" button in Play Mode

### Progress Bar Not Animating

**Solution**: Ensure "Smooth Fill" is enabled and Fill Speed > 0

### Config Validation Fails

**Solution**: Click "Validate All Addresses" to see specific errors. Check for:
- Invalid AssetReferences
- Empty address strings
- Duplicate entries

### Can't Find Dashboard Window

**Solution**: Use menu **Window → Addressable Manager → Dashboard** or press `Ctrl+Alt+A`

---

## 🚀 What's Next?

This is version 2.0 with Editor Tools Phase 1 implemented. Future updates may include:

- Memory Profiler Window with leak detection
- Dependency Graph visualization
- Scene View overlays and gizmos
- 8 complete sample scenes
- Automated testing tools
- Build report generation
- Advanced analytics

Stay tuned! 🎉

---

**For more information, see:**
- [README.md](README.md) - Package overview
- [ARCHITECTURE.md](ARCHITECTURE.md) - System architecture
- [QUICK_REFERENCE.md](QUICK_REFERENCE.md) - API reference
- [CHANGELOG.md](CHANGELOG.md) - Version history

**Need help?** Check the documentation or inspect existing components to see how they work!
