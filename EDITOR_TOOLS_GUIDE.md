# Editor Tools Guide

Companion to the [README](README.md) and [MONITORING_GUIDE](MONITORING_GUIDE.md). This document covers everything you only ever touch inside the Editor: the Addressable Manager hub, the Dashboard window, custom inspectors, ScriptableObject configs, the runtime progress-bar component, menu shortcuts, and the `ScopeManager` API for multi-instance scope setups.

## Quick start

| Action | Where |
|---|---|
| Start anywhere — see what is blocking content | **Window → Addressable Manager → Open** (`Ctrl+Alt+A`) |
| Open the Dashboard | **Window → Addressable Manager → Dashboard** |
| Drop scope objects into a scene | **Tools → Addressable Manager → Quick Setup → Create All Scope Objects** |
| Create a config asset | **Assets → Create → Addressable Manager → …** |

Asset loading is auto-monitored. Anything that goes through `AssetLoader.LoadAssetAsync` (directly or via `Assets.Load`, scope loaders, `ScopeManager`, etc.) shows up in the Dashboard while you Play. `MonitoredAssetLoader` still exists but is a thin forwarder kept for source compatibility — `AssetLoader` reports to the monitor itself, so wrapping it adds nothing.

> **Monitoring is Editor-only.** `EditorAssetMonitor` lives in the Editor assembly, and the tracker is cleared on `ExitingPlayMode`. There is no dashboard, and no tracking, in a player build.

## The Addressable Manager hub

**Window → Addressable Manager → Open** (`Ctrl+Alt+A`). Added in 4.1.0. One window in place of the
four below, reached through a left rail.

**The rail is not a menu.** It is the delivery pipeline — Configure, Author, Build, Publish, Run —
and each stage is coloured by its own health right now, with the connector drawn dead below the
first blocked stage. So *"how far does my content get, and what stops it"* is answered before you
click anything, and the blocker itself is clickable: it takes you to the screen that is blocking it.

Ten sections live on that rail:

| Stage | Sections |
|---|---|
| Configure | Overview · Validator · Profiles |
| Author | Layout Rules |
| Build | Update Preview · Build |
| Publish | Catalog Inspector · Local Server |
| Run | Runtime Monitor · Asset Lifetime |

Four things are worth knowing before you use it:

- **There are four health states, and the fourth is the point.** `NotMeasured` is drawn *hollow*
  rather than filled, and cannot exist without a reason attached. "We checked and found nothing" and
  "we did not check" used to render identically across the Validator, the build manifest, the
  restriction check and the Runtime Monitor — which is how a CDN nobody had ever reached read as
  healthy.
- **The Validator groups by consequence,** not by severity: *Fails the CI gate* is a checkable claim
  against `CatalogVerifier`, not a figure of speech. Rules skipped in Local-only mode get their own
  group instead of being counted as passing.
- **Layout Rules has a dry run,** which the rule system never had. It runs the identical code path
  behind a flag, so it cannot describe a run that will not happen, and Apply is reachable only past
  it. Address collisions come with a *Show both* button rather than a sentence naming two paths you
  then have to find.
- **`Ctrl+K` searches the sections** while the hub has focus. Navigation only — it does not run
  actions.

`CdnManagerWindow` and the Dashboard still open and still work. Three of its six tabs — Catalog
Inspector, Local Server, Runtime Monitor — the hub hosts through an adapter, so they behave
identically in both windows. Validator, Build and Update Preview were rebuilt as hub sections; the
old tabs stay in `CdnManagerWindow` until that window retires. Nothing you had bookmarked has moved.

## Dashboard window

**Window → Addressable Manager → Dashboard.** Window title "Addressable Manager", minimum size
800×600. This no longer binds `Ctrl+Alt+A` — the hub does.

A one-line **CDN status strip** sits above the tabs: environment id, app version, cache size and network state while playing (`not in play mode` / `not initialised` otherwise), plus a **CDN Manager** button that opens that window. It polls once per second while the Dashboard is open.

### Tab 1 — Active Assets
Live row per currently-alive tracked handle, newest load first. A search field (matches address or type name) and a scope dropdown whose choices are rebuilt from the live scope ids as they appear.

Each row is two lines, not a column grid:

- **Address** (bold)
- `Type • <scope> Scope • Loaded <n> ago`
- right-hand side: `Refs: <count>` and an estimated size in KB

Use it to hunt leaks: anything alive longer than expected, or with a refcount that only grows, is a candidate.

> **The memory numbers are per-type constants, not measurements.** `AssetTrackerService.EstimateMemorySize` returns a fixed value per type name (Texture2D 1 MB, AudioClip 512 KB, GameObject 256 KB, Material 64 KB, Mesh 128 KB, ScriptableObject 32 KB, everything else 100 KB). Treat every MB figure in this window — rows, scope totals, the graph — as a proxy for asset *count* weighted by type, never as real memory. The Unity Profiler is still the tool for actual bytes.

Rows are not clickable; there is no ping-in-project from this tab.

### Tab 2 — Performance
Four stat cards plus a graph and a slowest-loads list:

- Total Assets
- Cache Hit Ratio (`hits / (hits + misses)`)
- Total Memory (estimated — see the caveat above)
- Avg Load Time

**Memory graph.** A code-built chart under the stat cards, with a text summary line above it (latest total / cached / active, plus `Peak` and `Avg` once there is data). It keeps the last 300 samples and plots the **total** series only — cached and active appear in the summary text, not as lines. Grid lines mark a warning threshold at 50 MB and a critical threshold at 100 MB; the vertical scale auto-fits to the tallest sample (minimum 10 MB, plus 20 % headroom). One sample is added per Editor update tick while in Play Mode, so the time window it covers depends on the Editor's frame rate — and neither the thresholds nor the sample count are exposed as controls anywhere in the UI.

**Slowest Loading Assets.** Address, type and average load time in ms.

**Export Report (CSV)** opens a save-file panel and writes one row per recorded snapshot: `Timestamp, Total Memory (MB), Active Assets, Avg Load Time (s), Cache Hit Ratio`. Useful for diffing across optimisation passes.

### Tab 3 — Scopes
One foldout per **live** scope id reported to the tracker, created the first time that id is seen — Global, the `ScopeManager` "Session" entry, each `Scene-…` / `Hierarchy-…` scope, each `Hybrid:…` scope, and any custom name you registered via `ScopeManager` or `new AssetLoader("…")`. The header reads `DisplayName (Category Scope)`, or just `Category Scope` when no distinct display name was reported. Each foldout shows `Assets: N | Memory: X MB` (plus `inactive` when the scope is deactivated), the asset list, and a **Cleanup** button. A **Cleanup All Scopes** button sits in the tab toolbar.

> **What Cleanup does depends on Play Mode.** In Play Mode it calls `ScopeManager.ClearScope`, releasing real handles, then clears the Dashboard rows. Outside Play Mode there is no live loader, so it only clears the Dashboard's own rows — the confirmation dialog says which of the two you are about to get.

### Tab 4 — Settings
Widgets, in order: **Log Level** dropdown (`None` / `Errors Only` / `Warnings and Errors` / `All`), **Auto Refresh Dashboard** toggle, **Refresh Interval (ms)** slider (100–5000, default 500), **Simulate Slow Loading** toggle, **Delay (ms)** slider (100–5000), **Failure Rate (%)** slider (0–50), and the buttons **Reset All Settings** / **Reset Statistics**.

Auto-refresh and the refresh interval drive this window. The other four write straight through to `DebugSettings.Instance` and mark the asset dirty.

> **Only the log level has any effect.** `DebugSettings.IsVerbose` (`logLevel == All`) gates verbose logging in `AssetLoader` and `CdnTelemetry`. Nothing in this package reads `simulateSlowLoading`, `simulatedDelayMs` or `simulateFailureRate` — `DebugSettings.ShouldSimulateFailure()` and `GetNetworkDelay()` have no callers anywhere. The three simulation widgets persist a value and change no behaviour.

**Reset All Settings** resets the widgets' displayed values (and re-reads the current log level from the asset); it does not write defaults back to `DebugSettings`. **Reset Statistics** clears the tracker and the performance metrics after a confirmation dialog.

## Custom inspectors

### Scope components
Selecting a `GlobalAssetScope`, `SceneAssetScope` or `HierarchyAssetScope` in the Hierarchy shows the same inspector layout (`SessionAssetScope` was removed in 4.0.0 — sessions are a `ScopeManager` entry now):

- Coloured banner (Global = green, Scene = yellow, Hierarchy = red) with an ACTIVE / INACTIVE badge, titled `DisplayName (Category Scope)`
- Status block: assets loaded, total estimated memory
- Memory bar against a **hard-coded 100 MB** ceiling (< 50 % green, 50–80 % yellow, > 80 % red) — the ceiling is not configurable
- `Loaded Assets (N)` foldout, collapsed by default, listing address / type / time since load / refs / estimated KB
- Buttons: **Activate Scope**, **Deactivate Scope**, **Cleanup Scope**, **Open Dashboard**

The first three buttons are **disabled outside Play Mode** — there is no live scope to act on in Edit mode. Cleanup is also disabled while the scope holds no assets. Only **Open Dashboard** works at any time.

`MonitoringHelper` has its own, much simpler inspector: an explanatory HelpBox, the default fields (`Enable Monitoring`, `Verbose Logging`), and an **Open Dashboard** button that appears only while playing with monitoring enabled.

### Config inspectors

**AddressablePreloadConfig.** Adds an Actions block with **Validate All Addresses**, **Sort by Priority** (ascending, undoable) and **Test Load in Editor**, plus a Statistics block summarising total / startup / valid / invalid entry counts. **Test Load in Editor** does not load anything — outside Play Mode it tells you to enter Play Mode, and inside it shows a dialog listing the first ten assets it *would* load.

**PoolConfiguration.** No custom inspector. Its `OnValidate` logs a warning every time you edit the asset, because nothing consumes it — see the section below.

**DebugSettings.** Default inspector. The Dashboard's Settings tab writes to the same asset (log level and the three simulation fields only).

### AddressableProgressBar inspector
Outside Play Mode the inspector shows the default fields plus a short **Setup Guide** — there are no testing controls in Edit mode.

Enter Play Mode and a **Testing Controls (Play Mode)** block appears:

- **Test Progress** slider (0–1)
- **Show** / **Hide** / **Reset** buttons
- **Animate 0% → 100%** button (about 5 seconds; click again to stop)
- **Test Status** text field, pushed straight to `SetStatus`

Lets you sanity-check your loading screen UX without writing test code.

## ScriptableObject configurations

### AddressablePreloadConfig
**Create:** Assets → Create → Addressable Manager → Preload Configuration.

```
Preload Entry
├── Asset Reference   (drag from Addressables Groups)
├── Address           (manual address as alternative)
├── Scope             Global | Session | Scene | Hierarchy
├── Load On Startup   bool
├── Priority          0–100, lower = earlier
└── Label             optional debug label

Preload Settings
├── Validate On Build      (read by PreloadConfigBuildValidator)
├── Fail Build On Error    (read by PreloadConfigBuildValidator)
├── Load In Parallel       (no readers)
└── Max Concurrent Loads   1–20 (no readers)
```

> **Nothing in this package loads these assets.** There is no startup path: `loadOnStartup`, `priority` and `scope` only describe intent, and `GetEntriesForScope` has no callers. `validateOnBuild` / `failBuildOnError` are real — `PreloadConfigBuildValidator` reads them as a build step — and `GetStartupAssets()` is real, but the loading loop is yours to write. `loadInParallel` and `maxConcurrentLoads` are read by nothing at all, so the loop below is sequential regardless of what they say.

Code usage — one way to write that loop:

```csharp
using AddressableManager.Configs;
using AddressableManager.Managers;
using AddressableManager.Scopes;

var config = Resources.Load<AddressablePreloadConfig>("MyPreloadConfig");

foreach (var entry in config.GetStartupAssets())   // already sorted, lowest priority first
{
    // "Global" is reserved in ScopeManager — reach that cache through GlobalAssetScope.
    var loader = entry.scope == AssetScopeType.Global
        ? GlobalAssetScope.Instance.Loader
        : ScopeManager.Instance.GetOrCreateScope(entry.scope.ToString());

    await loader.LoadAssetAsync<UnityEngine.Object>(entry.GetAddress());
}
```

`GetAddress()` returns `assetReference.AssetGUID` when an `AssetReference` is set, and the manual `address` string otherwise — so an entry authored by dragging a reference is keyed by GUID, not by the address you would type in your own `Load` calls.

### PoolConfiguration
**Create:** Assets → Create → Addressable Manager → Pool Configuration.

> **This asset is inert. No runtime code reads any field or calls any method on it.** The only things in the package that touch `PoolConfiguration` are the two menu items that *create* the asset. `GetAutoCreatePools()` and `GetPoolByAddress()` have no callers, `createAllOnStartup` has no startup path to hook, and `cleanupOnSceneUnload` is never consulted. Editing values here changes nothing, and `OnValidate` says so in the console every time you edit it. The status is deliberate and recorded under "Pooling: decisions still open" in `Documentation/LIFETIME_DESIGN.md`.
>
> Create pools with `Assets.CreatePool`, `Standard.CreatePool` / `Standard.CreateDynamicPool`, or `AddressablePoolManager.CreatePoolAsync` instead. Note the differing defaults: `Standard.CreatePool` defaults `maxSize` to 50, while `Assets.CreatePool` and `AddressablePoolManager` default to 100 (`AddressablePoolManager.DefaultMaxPoolSize`).

The fields it serialises, for reference if you ever wire it up yourself:

```
Pool Settings
├── Prefab Reference  (AssetReference)
├── Address           (manual fallback)
├── Preload Count     0–100
├── Max Size          0–1000  (documented here as 0 = unlimited)
├── Auto Create
├── Pool Root         parent transform
└── Label             optional debug label

Global Pool Settings
├── Default Max Size        (50 here; the real default is 100)
├── Default Preload Count   (5 here; pools are actually created empty)
├── Create All On Startup
└── Cleanup On Scene Unload
```

> Two traps for whoever wires it up. `PoolSettings.GetAddress()` returns `prefabReference.AssetGUID` when a reference is set, so a pool built from it would be keyed by GUID while your code calls `Spawn("Enemies/Orc")`. And `maxSize == 0` means *unlimited* to `IPoolFactory.CreatePool` but is rejected outright as a hard cap of zero by `DynamicPoolConfig` — whichever creation path you pick decides which convention applies.
>
> `destroyOnFull` is obsolete and hidden from the inspector. `UnityEngine.Pool.ObjectPool` always destroys instances released above `maxSize`; toggling the flag had no effect.

### DebugSettings
**Create:** Assets → Create → Addressable Manager → Debug Settings.

The `Instance` accessor looks up `Resources/AddressableManager/DebugSettings` inside `#if UNITY_EDITOR`. In builds the lookup is skipped and a transient default is returned, so the `Resources/` dependency is purely an Editor convenience — your shipping build does not need the asset. **Window → Addressable Manager → Settings** pings that asset, and offers to create one if it is missing.

Fields, and what actually reads them:

| Group | Fields | Read by |
|---|---|---|
| Logging | `logLevel` | `DebugSettings.IsVerbose` → `AssetLoader`, `CdnTelemetry` |
| Logging | `logToFile`, `logFilePath` | nothing |
| Profiling | `enableProfiling`, `showProfilerOverlay`, `recordMetrics` | nothing |
| Simulation | `simulateSlowLoading`, `simulatedDelayMs`, `simulateFailureRate`, `simulateNetworkConditions`, `networkSimulation` | nothing |
| Validation | `validateReferences`, `detectMemoryLeaks`, `leakDetectionMinutes` | nothing |
| Warnings | `warnOnHighRefCount`, `highRefCountThreshold`, `warnOnHighMemory`, `highMemoryThresholdMB` | nothing |

`ShouldLog(LogType)`, `ShouldSimulateFailure()` and `GetNetworkDelay()` are public and callable, but this package never calls them. If you want simulated latency or failures, run them from your own loading code:

```csharp
var settings = DebugSettings.Instance;

if (settings.simulateSlowLoading)
    await Task.Delay((int)settings.simulatedDelayMs);

if (settings.ShouldSimulateFailure())
    /* your own failure path */;
```

`DebugSettings.IsVerbose` is the one hot-path shortcut the package itself uses; it compiles to a constant `false` outside the Editor.

## Runtime UI: AddressableProgressBar

**Add Component → Addressable Manager → Progress Bar.** The component `[RequireComponent]`s a `CanvasGroup`.

TextMeshPro is **optional**: the asmdef defines `TMP_PRESENT` only when `com.unity.textmeshpro 3.0.0+` is installed, so the text fields fall back to plain `UnityEngine.UI.Text` otherwise.

Inspector wiring:

1. Fill Image — an Image set to `Filled` type (logs an error on `Awake` if unassigned)
2. Percent Text — optional, percentage label
3. Status Text — optional, current operation label
4. Download Text — optional, formatted bytes / speed / ETA

Behaviour settings:

- Smooth Fill, Fill Speed (1–20)
- Hide When Complete, Hide Delay (0–5 s)
- Gradient Colors, with Start / Mid / End colours (red → yellow → green by default)

Binding is always explicit, through `BindToTracker`; the component does not look for a tracker on its own.

Code:

```csharp
progressBar.BindToTracker(myProgressTracker);

// or manual
progressBar.SetProgress(0.5f);
progressBar.SetStatus("Loading textures…");
progressBar.Show();
progressBar.Hide();
progressBar.Reset();
```

`SetDownloadProgress(AddressableManager.Cdn.DownloadProgress)` and the static `AddressableProgressBar.FormatBytes(long)` are also public.

## Menus and shortcuts

Every menu path below is registered by this package; nothing else is.

**GameObject → Addressable Manager →** Add Global Scope · Add Scene Scope · Add Hierarchy Scope · View in Dashboard
**Assets → Addressable Manager →** Create Preload Config · Create Pool Config · Create Debug Settings
**Assets → Addressables →** Apply Layout Rules *(enabled when something is selected)*
**Assets → Create → Addressable Manager →** Layout Rule Data · Composite Layout Rule Data · Preload Configuration · Pool Configuration · Debug Settings · CDN Settings · Filters → … · Providers → …
**Window → Addressable Manager →** Open (`Ctrl+Alt+A`) · Validate Setup · Profiles · Build Content · Dashboard · Layout Rule Editor · Layout Viewer · CDN Manager · Documentation · Settings · Clear All Caches
**Tools → Addressable Manager →** Force Process All Assets · Batch Address Updater · Repair Groups Missing Schemas · Start Local Content Server · Stop Local Content Server
**Tools → Addressable Manager → Quick Setup →** Create All Scope Objects · Create Sample Configs

`Ctrl+Alt+A` opens the **hub**, and it is the only `[MenuItem]` shortcut the package registers.
Before 4.1.0 it opened the Dashboard — and three separate menu items claimed the same chord, which
Unity accepts without defining which one wins. `Ctrl+K` inside the hub is a key handler on that
window rather than a menu shortcut, so it works only while the hub has focus.

Two of these do less than their names suggest:

- **Window → … → Clear All Caches** clears **Editor tracking data only** (`AssetTrackerService` and `PerformanceMetrics`). No runtime asset is released.
- **Tools → … → Batch Address Updater** is not a window. It shows a dialog listing the `BatchAddressUpdater` static methods you can call from code: `FindAndReplace`, `AddPrefix`, `RemovePrefix`, `ConvertToLowercase`.

**Start Local Content Server** serves on port 8080; **Stop Local Content Server** is greyed out while nothing is running.

## Workflow recipes

### Set up a new scene with scopes
1. **Tools → Addressable Manager → Quick Setup → Create All Scope Objects** — creates `[GlobalAssetScope]`, `[SceneAssetScope]` and `[HierarchyAssetScope]`. There is no Session GameObject; use `ScopeManager.Instance.GetOrCreateScope("Session")` or `Assets.StartSession()` in code.
2. Select each scope GameObject — the banner reads INACTIVE in Edit mode, because activation happens in the component's `Awake`.
3. Press Play, open the Dashboard → Scopes, and verify a foldout appears for each scope.

### Configure asset preloading
1. **Assets → Create → Addressable Manager → Preload Configuration** → name it `GlobalPreloadConfig`.
2. Add entries, drag assets onto `Asset Reference`, pick a Scope, set Priority.
3. Click **Validate All Addresses** — fix anything reported invalid.
4. From your bootstrap, `Resources.Load<AddressablePreloadConfig>("GlobalPreloadConfig")` and iterate `GetStartupAssets()` yourself (see the sample above). Nothing preloads on its own.

### Build a loading screen
1. Drop a Canvas with a CanvasGroup, add **Progress Bar** to it.
2. Hierarchy:
   ```
   Canvas
   └── LoadingScreen (CanvasGroup + AddressableProgressBar)
       ├── Background (Image)
       ├── FillBar    (Image — Fill type → Fill Image slot)
       ├── PercentText (Text or TMP_Text → Percent Text slot)
       └── StatusText (Text or TMP_Text → Status Text slot)
   ```
3. Bind a tracker and load:
   ```csharp
   using AddressableManager.Progress;   // ProgressTracker, LoadAssetWithProgressAsync

   var tracker = new ProgressTracker();
   progressBar.BindToTracker(tracker);
   await loader.LoadAssetWithProgressAsync<Texture2D>(
       "Textures/Big",
       info => tracker.UpdateProgress(info));
   ```
4. Enter Play Mode and use the inspector's Testing Controls (slider / Animate) to check the visuals. Those controls do not exist in Edit mode.

### Debug a memory issue
1. Play, open Dashboard → Active Assets, filter to the suspect scope.
2. The list is ordered newest-load-first and there are no sortable columns — read the `Loaded … ago` line on each row; anything older than expected is suspicious.
3. Check `Refs:` on the row. A handle whose count never returns to 0 was retained without a matching release.
4. Cross-reference Performance → Slowest Loading Assets if the slowdown shows up at load time.
5. Remember the KB/MB figures are per-type constants, not measurements — use them to spot *which* assets are alive, then confirm real bytes in the Unity Profiler.

### Validate the preload config before shipping
1. Select the config asset → click **Validate All Addresses**.
2. Enable `Validate On Build` and `Fail Build On Error` on the asset so `PreloadConfigBuildValidator` re-runs the validation as a build step and blocks the build on failure.

## `ScopeManager` for multi-instance scopes

Use `ScopeManager` instead of the built-in singletons when:

- You need more than one `Session`-style scope at a time (e.g. `PlayerSession`, `GameSession`, `MatchSession`).
- You want a scope per match / level / quest with a structured name.
- You're plugging Addressable Manager into a DI container that already owns lifetime.

Use the built-in singletons when one global + one session + per-scene is enough.

> **`"Global"` is a reserved id.** `GlobalAssetScope` registers its own loader under that exact
> string as a foreign directory entry the first time it's touched (directly, or via any
> `Simple.*`/`Standard.*` call). `GetOrCreateScope("Global")` refuses it and logs an error rather
> than aliasing or silently losing to whichever side registered first. Reach the real Global cache
> via `GlobalAssetScope.Instance.Loader`; pick your own id (e.g. `"AppGlobal"`, used below) for a
> `ScopeManager`-owned scope that happens to also be app-wide.

`AssetLoader.LoadAssetAsync<T>` returns an `IAssetHandle<T>`, not the asset itself — reach the asset through the handle. The samples below use `var` and keep the handles alive for the lifetime of the scope.

### Multi-session sketch

```csharp
using AddressableManager.Managers;
using AddressableManager.Loaders;
using AddressableManager.Core;

public sealed class GameController : MonoBehaviour
{
    private ScopeManager _scopes;

    async void Start()
    {
        _scopes = ScopeManager.Instance;

        // Three named scopes. The name is also the label shown in the Dashboard.
        // "Global" itself is reserved — GlobalAssetScope registers it automatically as a
        // foreign directory entry, so GetOrCreateScope refuses it. Use GlobalAssetScope.Instance
        // .Loader for the real Global cache, or (as here) pick your own app-level scope id.
        var globalLoader = _scopes.GetOrCreateScope("AppGlobal");
        var playerLoader = _scopes.GetOrCreateScope("PlayerSession");
        var gameLoader   = _scopes.GetOrCreateScope("GameSession");

        var uiAtlas       = await globalLoader.LoadAssetAsync<Texture2D>("UI/Atlas");
        var playerProfile = await playerLoader.LoadAssetAsync<PlayerData>("Data/PlayerProfile");
        var levelData     = await gameLoader.LoadAssetAsync<LevelData>("Levels/Level1");
    }

    void OnApplicationQuit() => _scopes.ClearAllExceptGlobal();
}
```

Dashboard layout that produces:

```
Scopes
├─ AppGlobal      1 asset · 5.2 MB
│  └─ UI/Atlas
├─ PlayerSession  1 asset · 0.5 MB
│  └─ Data/PlayerProfile
└─ GameSession    1 asset · 2.1 MB
   └─ Levels/Level1
```

### Per-match scopes (multiplayer)

```csharp
public sealed class MultiplayerManager : MonoBehaviour
{
    private readonly ScopeManager _scopes = ScopeManager.Instance;
    private string _currentMatchId;

    public async Task StartMatch(string matchId)
    {
        _currentMatchId = $"Match_{matchId}";
        var loader = _scopes.GetOrCreateScope(_currentMatchId);

        var map = await loader.LoadAssetAsync<MapData>($"Maps/{matchId}");
        var players = await loader.LoadAssetsByLabelAsync<GameObject>($"Characters_{matchId}");
    }

    public void EndMatch()
    {
        if (!string.IsNullOrEmpty(_currentMatchId))
        {
            _scopes.ClearScope(_currentMatchId);
            _currentMatchId = null;
        }
    }
}
```

### RPG-style segmentation

```csharp
public sealed class RPGGameManager : MonoBehaviour
{
    private readonly ScopeManager _scopes = ScopeManager.Instance;

    private AssetLoader _global;   // UI, fonts, shared — NOT the built-in GlobalAssetScope; see below
    private AssetLoader _player;   // inventory, save data
    private AssetLoader _world;    // current zone
    private AssetLoader _quests;   // active quests
    private AssetLoader _party;    // companions

    async void Start()
    {
        // "Global" is reserved for the built-in GlobalAssetScope (registered automatically as a
        // foreign directory entry) — GetOrCreateScope refuses it. "AppGlobal" here is this
        // manager's OWN manager-owned scope, independent of GlobalAssetScope.Instance.Loader.
        _global = _scopes.GetOrCreateScope("AppGlobal");
        _player = _scopes.GetOrCreateScope("Player");
        _world  = _scopes.GetOrCreateScope("World");
        _quests = _scopes.GetOrCreateScope("Quests");
        _party  = _scopes.GetOrCreateScope("Party");

        await _global.LoadAssetAsync<Texture2D>("UI/Atlas");
        await _global.LoadAssetAsync<ItemDatabase>("Data/Items");
        await _player.LoadAssetAsync<PlayerProfile>("Save/PlayerProfile");
    }

    public async Task EnterZone(string zone)
    {
        _scopes.ClearScope("World");
        _world = _scopes.GetOrCreateScope("World");

        await _world.LoadAssetAsync<ZoneData>($"Zones/{zone}");
        await _world.LoadAssetsByLabelAsync<GameObject>($"Enemies_{zone}");
    }

    public void ExitToMainMenu() => _scopes.ClearAllExcept("AppGlobal", "Player");
    void OnApplicationQuit()      => _scopes.ClearAll();
}
```

### Useful patterns

```csharp
// Grouping
public sealed class ScopeGroups
{
    private static readonly string[] Gameplay   = { "World", "Quests", "Combat", "Dialogue" };
    private static readonly string[] Persistent = { "AppGlobal", "Player" };

    public void ClearGameplay()
    {
        foreach (var s in Gameplay) ScopeManager.Instance.ClearScope(s);
    }

    public void ClearAllExceptPersistent()
        => ScopeManager.Instance.ClearAllExcept(Persistent);
}

// Per-id scopes with a prefix convention
public AssetLoader GetLevelScope(int levelId)
    => ScopeManager.Instance.GetOrCreateScope($"Level_{levelId}");

public void ClearAllLevels()
{
    var levelScopes = ScopeManager.Instance.ActiveScopes
        .Where(s => s.StartsWith("Level_")).ToList();
    foreach (var s in levelScopes) ScopeManager.Instance.ClearScope(s);
}
```

### Best practices for `ScopeManager`

1. **Use stable, descriptive names.** `"PlayerSession"`, `"World_Overworld"`, `"Match_<id>"`. Avoid `"temp"`, `"scope1"` — the name is what you'll see in the Dashboard.
2. **Reuse the loader.** `GetOrCreateScope("X")` caches; calling it twice returns the same `AssetLoader`. Don't allocate `new AssetLoader()` per call — you lose the cache and the Dashboard label.
3. **Clear at natural seams.** Scene unload, match end, logout, zone change. Don't `ClearAll()` from `Update()` — it defeats the cache.
4. **`ScopeManager` is reset on domain reload** (`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`). You don't need a manual reset between Play sessions.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Dashboard tabs don't refresh | Settings tab → ensure `Auto Refresh Dashboard` is on; bump the refresh interval down. |
| Scope inspector shows no assets, buttons greyed out | You're in Edit mode. A scope activates in its component's `Awake`, so it stays INACTIVE — and Activate / Deactivate / Cleanup stay disabled — until you press Play. |
| Dashboard memory numbers look wrong | They are estimates. `AssetTrackerService` derives size from the type name alone; use the Unity Profiler for real bytes. |
| Simulation sliders change nothing | They don't. Nothing in the package reads `simulateSlowLoading` / `simulatedDelayMs` / `simulateFailureRate` — call `DebugSettings.ShouldSimulateFailure()` from your own load path if you want the behaviour. |
| `ScopeManager.GetScopeMemoryUsage` returns 0 | Marked `[Obsolete]` — runtime memory tracking isn't implemented. Use the Editor Dashboard's Active Assets tab, with the caveat above. |
| `GetOrCreateScope("Global")` returns null and logs an error | `"Global"` is reserved for `GlobalAssetScope`. Use `GlobalAssetScope.Instance.Loader`, or pick another id. |
| Progress bar text fields red in inspector | TMP isn't installed and you assigned a `TextMeshProUGUI` reference. Either install TMP (asmdef will define `TMP_PRESENT`) or assign a `UnityEngine.UI.Text` instead. |
| Config validation fails on build | Re-run the inspector's **Validate All Addresses**, fix invalid `AssetReference` rows, then enable `Fail Build On Error`. |
| Editing PoolConfig logs a warning every time | Working as intended — the asset is not wired into anything. Create pools in code instead. |
| Can't find the Dashboard | `Window → Addressable Manager → Dashboard` or `Ctrl+Alt+A`. |

## See also

- [README.md](README.md) — package overview, install, quick start
- [MONITORING_GUIDE.md](MONITORING_GUIDE.md) — Dashboard internals, custom monitors
- [CHANGELOG.md](CHANGELOG.md) — release notes
