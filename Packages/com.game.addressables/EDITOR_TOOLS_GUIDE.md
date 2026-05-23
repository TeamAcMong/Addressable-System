# Editor Tools Guide

Companion to the [README](README.md) and [MONITORING_GUIDE](MONITORING_GUIDE.md). This document covers everything you only ever touch inside the Editor: the Dashboard window, custom inspectors, ScriptableObject configs, the runtime progress-bar component, menu shortcuts, and the `ScopeManager` API for multi-instance scope setups.

## Quick start

| Action | Where |
|---|---|
| Open the Dashboard | **Window → Addressable Manager → Dashboard** (`Ctrl+Alt+A`) |
| Drop scope objects into a scene | **Tools → Addressable Manager → Quick Setup → Create All Scope Objects** |
| Create a config asset | **Assets → Create → Addressable Manager → …** |

Asset loading is auto-monitored — there are no `Monitored` variants. Anything that goes through `AssetLoader.LoadAssetAsync` (directly or via `Assets.Load`, scope loaders, `ScopeManager`, etc.) shows up in the Dashboard while you Play.

## Dashboard window

Shortcut: `Ctrl+Alt+A`.

### Tab 1 — Active Assets
Live row per currently-alive asset handle. Search by name, filter by scope. Visible columns:

- Address
- Type
- Scope (label from the loader that created the handle)
- Reference count
- Estimated memory
- Time since loaded

Use it to hunt leaks: anything alive longer than expected, or with a refcount that only grows, is a candidate.

### Tab 2 — Performance
Aggregated counters and a slowest-10 list:

- Total handles
- Cache-hit ratio (`hits / (hits + misses)`)
- Estimated total memory
- Average load time
- Slowest-10 loads

`Export Report (CSV)` writes the current metrics next to the project, useful for diffing across optimisation passes.

### Tab 3 — Scopes
One foldout per known scope (Global / Session / Scene / Hierarchy / any custom name registered via `ScopeManager` or `new AssetLoader("…")`). Each foldout exposes per-scope asset count, estimated memory, and a manual cleanup button.

### Tab 4 — Settings
Controls the live `DebugSettings` instance: log level, dashboard refresh interval, simulated slow loading / failure rate / network type. Useful for stress-testing without touching real assets.

## Custom inspectors

### Scope components
Selecting any `GlobalAssetScope` / `SessionAssetScope` / `SceneAssetScope` / `HierarchyAssetScope` in the Hierarchy shows the same rich inspector layout:

- Coloured banner (Global = green, Session = blue, Scene = yellow, Hierarchy = red) with ACTIVE / INACTIVE badge
- Live counters (assets loaded, estimated memory)
- Coloured memory bar (< 50 % green, 50–80 % yellow, > 80 % red)
- Expandable list of assets the scope currently owns
- Buttons: Activate, Deactivate, Cleanup, Open Dashboard

The same inspector also drives the `MonitoringHelper` component (with an "Open Dashboard" jump button).

### Config inspectors

**AddressablePreloadConfig.** Adds a `Validate All Addresses` button, a `Sort by Priority` button, and a stats panel summarising total / startup / invalid counts.

**PoolConfiguration.** Validates `preloadCount <= maxSize`, flags duplicate addresses, and logs warnings on `OnValidate`.

**DebugSettings.** Just the default inspector, but pairs with the Dashboard's Settings tab — both edit the same asset.

### AddressableProgressBar inspector (Play Mode)
While playing, the inspector exposes interactive testing controls:

- Progress slider (0–100 %)
- Show / Hide / Reset buttons
- Animate (auto 0 → 100 % over the configured fill speed)
- Status text input

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
├── Priority          lower = earlier
└── Label             optional debug label

Global Settings
├── Validate On Build
├── Fail Build On Error
├── Load In Parallel
└── Max Concurrent Loads (1–20)
```

Code usage:

```csharp
var config = Resources.Load<AddressablePreloadConfig>("MyPreloadConfig");

foreach (var entry in config.GetStartupAssets())
{
    var address = entry.GetAddress();
    var loader = ResolveLoaderForScope(entry.scope);
    await loader.LoadAssetAsync<UnityEngine.Object>(address);
}
```

### PoolConfiguration
**Create:** Assets → Create → Addressable Manager → Pool Configuration.

```
Pool Settings
├── Prefab Reference  (AssetReference)
├── Address           (manual fallback)
├── Preload Count     0–100
├── Max Size          0 = unlimited
├── Auto Create       create on startup
├── Pool Root         parent transform
└── Label             optional debug label
```

> `destroyOnFull` is obsolete as of 2.2.0 and hidden from the inspector. `UnityEngine.Pool.ObjectPool` always destroys instances released above `maxSize`; toggling the flag had no effect.

Code usage:

```csharp
var poolConfig = Resources.Load<PoolConfiguration>("MyPoolConfig");

foreach (var pool in poolConfig.GetAutoCreatePools())
{
    await Assets.CreatePool(
        pool.GetAddress(),
        pool.preloadCount,
        pool.maxSize
    );
}
```

### DebugSettings
**Create:** Assets → Create → Addressable Manager → Debug Settings.

The `Instance` accessor looks up `Resources/AddressableManager/DebugSettings` inside `#if UNITY_EDITOR`. In builds the lookup is skipped and a transient default is returned, so the `Resources/` dependency is purely an Editor convenience — your shipping build does not need the asset.

Notable fields:

- **Logging:** level, log-to-file, log-file path
- **Profiling:** enable / overlay / record metrics
- **Simulation (Editor only):** slow-loading toggle + delay ms, failure rate %, network type (`WiFi` / `4G` / `3G` / `Slow`)
- **Validation:** validate references, leak detection threshold (minutes)
- **Warnings:** high-refcount + high-memory thresholds

```csharp
var settings = DebugSettings.Instance;
if (settings.ShouldLog(LogType.Warning))
    Debug.LogWarning("…");

if (settings.simulateSlowLoading)
    await Task.Delay((int)settings.simulatedDelayMs);

if (settings.ShouldSimulateFailure())
    /* simulate failure */;
```

`DebugSettings.IsVerbose` is a fast Editor-only shortcut used inside `AssetLoader` to gate informational logs in the hot path.

## Runtime UI: AddressableProgressBar

**Add Component → Addressable Manager → Progress Bar.**

TextMeshPro is **optional**: the asmdef defines `TMP_PRESENT` only when `com.unity.textmeshpro 3.0.0+` is installed, so the text fields fall back to plain `UnityEngine.UI.Text` otherwise.

Inspector wiring:

1. Fill Image — an Image set to `Filled` type
2. Percent Text — optional, percentage label
3. Status Text — optional, current operation label
4. Download Text — optional, formatted bytes / speed / ETA

Behaviour settings:

- Auto Find Tracker, Smooth Fill, Fill Speed (1–20)
- Hide When Complete, Hide Delay (0–5 s)
- Gradient Colors (red → yellow → green by default)

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

## Menus and shortcuts

**GameObject → Addressable Manager →** Add Global / Session / Scene / Hierarchy Scope · View in Dashboard
**Assets → Create → Addressable Manager →** Preload Configuration · Pool Configuration · Debug Settings
**Window → Addressable Manager →** Dashboard (`Ctrl+Alt+A`) · Documentation · Settings · Clear All Caches
**Tools → Addressable Manager → Quick Setup →** Create All Scope Objects · Create Sample Configs

## Workflow recipes

### Set up a new scene with scopes
1. **Tools → Addressable Manager → Quick Setup → Create All Scope Objects**.
2. Select each scope GameObject — its inspector tells you whether it's active.
3. Press Play, open the Dashboard, verify all four scopes light up.

### Configure asset preloading
1. **Assets → Create → Addressable Manager → Preload Configuration** → name it `GlobalPreloadConfig`.
2. Add entries, drag assets onto `Asset Reference`, pick a Scope, set Priority.
3. Click **Validate All Addresses** — fix anything reported invalid.
4. From your bootstrap, `Resources.Load<AddressablePreloadConfig>("GlobalPreloadConfig")` and iterate `GetStartupAssets()`.

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
   var tracker = new ProgressTracker();
   progressBar.BindToTracker(tracker);
   await loader.LoadAssetWithProgressAsync<Texture2D>(
       "Textures/Big",
       info => tracker.UpdateProgress(info));
   ```
4. Use the Inspector sliders / Animate button to test visuals without entering Play.

### Debug a memory issue
1. Play, open Dashboard → Active Assets, filter to the suspect scope.
2. Sort by **Time Since Loaded** descending — anything older than expected is suspicious.
3. Check the Refcount column. A handle whose count never returns to 0 was retained without a matching `Release()`.
4. Cross-reference Performance → Slowest Assets if the slowdown shows up at load time.

### Validate the preload config before shipping
1. Select the config asset → click **Validate All Addresses**.
2. Enable `Validate On Build` and `Fail Build On Error` on the asset so the validation re-runs as a build step.

## `ScopeManager` for multi-instance scopes

Use `ScopeManager` instead of the built-in singletons when:

- You need more than one `Session`-style scope at a time (e.g. `PlayerSession`, `GameSession`, `MatchSession`).
- You want a scope per match / level / quest with a structured name.
- You're plugging Addressable Manager into a DI container that already owns lifetime.

Use the built-in singletons when one global + one session + per-scene is enough.

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
        var globalLoader = _scopes.GetOrCreateScope("Global");
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
├─ Global         1 asset · 5.2 MB
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

    private AssetLoader _global;   // UI, fonts, shared
    private AssetLoader _player;   // inventory, save data
    private AssetLoader _world;    // current zone
    private AssetLoader _quests;   // active quests
    private AssetLoader _party;    // companions

    async void Start()
    {
        _global = _scopes.GetOrCreateScope("Global");
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

    public void ExitToMainMenu() => _scopes.ClearAllExcept("Global", "Player");
    void OnApplicationQuit()      => _scopes.ClearAll();
}
```

### Useful patterns

```csharp
// Grouping
public sealed class ScopeGroups
{
    private static readonly string[] Gameplay   = { "World", "Quests", "Combat", "Dialogue" };
    private static readonly string[] Persistent = { "Global", "Player" };

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
| Dashboard tabs don't refresh | Settings tab → ensure `Auto Refresh` is on; bump refresh interval down. |
| Scope inspector shows no assets | Scope hasn't activated yet — `BaseAssetScope.Activate()` runs in the constructor, but if you're inspecting in Edit mode it stays inert until Play. |
| `ScopeManager.GetScopeMemoryUsage` returns 0 | Marked `[Obsolete]` since 2.2.0 — runtime memory tracking isn't implemented. Use the Editor Dashboard's Active Assets tab for live numbers. |
| Progress bar text fields red in inspector | TMP isn't installed and you assigned a `TextMeshProUGUI` reference. Either install TMP (asmdef will define `TMP_PRESENT`) or assign a `UnityEngine.UI.Text` instead. |
| Config validation fails on build | Re-run the inspector's **Validate All Addresses**, fix invalid `AssetReference` rows, then enable `Fail Build On Error`. |
| Can't find the Dashboard | `Window → Addressable Manager → Dashboard` or `Ctrl+Alt+A`. |

## See also

- [README.md](README.md) — package overview, install, quick start
- [MONITORING_GUIDE.md](MONITORING_GUIDE.md) — Dashboard internals, custom monitors
- [CHANGELOG.md](CHANGELOG.md) — release notes
