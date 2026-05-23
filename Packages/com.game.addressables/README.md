# Addressable Manager

Production-grade Unity Addressables management with scope-based lifecycles, object pooling, progress tracking, and an Editor Dashboard that monitors every load — no special API required.

```
com.game.addressables · Unity 2022.3+ · MIT
```

- **Scopes** — Global / Session / Scene / Hierarchy / arbitrary named scopes — each owns an isolated `AssetLoader` with its own cache, ref-counts, and Dashboard label.
- **Pooling** — Addressable-aware pool manager built on `UnityEngine.Pool.ObjectPool` (or any custom factory you plug in).
- **Progress tracking** — observer-style `IProgressTracker` for individual loads and `CompositeProgressTracker` for batches, plus an optional `AddressableProgressBar` UI component.
- **Editor Dashboard** — real-time view of active assets, scopes, memory and load times. Zero overhead in builds — every reporting call is `#if UNITY_EDITOR`.

## Install

`Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/TeamAcMong/Addressable-System.git#2.2.1"
  }
}
```

Or via Package Manager → **+ → Add package from git URL**:

```
https://github.com/TeamAcMong/Addressable-System.git#2.2.1
```

Tags publish only the package subtree (~KB, not MB) — see the repo's `DEPLOY_UPM_SUBTREE.md` for the release flow.

## Quick start

Three APIs at three levels of abstraction; pick whichever matches the shape of your game.

### 1. Static facade (one-liners)

```csharp
using AddressableManager.Facade;

// Global scope
var iconHandle = await Assets.Load<Sprite>("UI/Icon");
image.sprite   = iconHandle.Asset;

// Object pool
await Assets.CreatePool("Enemies/Orc", preloadCount: 8, maxSize: 32);
var orc = Assets.Spawn("Enemies/Orc", spawnPosition);
Assets.Despawn("Enemies/Orc", orc);

// Session lifetime
Assets.StartSession();
var level = await Assets.LoadSession<LevelConfig>("Levels/Level1");
Assets.EndSession();
```

### 2. Scope-direct (typed lifetimes)

```csharp
using AddressableManager.Scopes;

// Lives for the GameObject's lifetime
var scope  = HierarchyAssetScope.AddTo(characterGO);
var weapon = await scope.Loader.LoadAssetAsync<GameObject>("Weapons/Sword");

// Lives for the active scene; auto-cleaned on scene unload
var sceneScope = SceneAssetScope.GetOrCreate();
var material   = await sceneScope.Loader.LoadAssetAsync<Material>("Materials/Floor");
```

### 3. `AssetLoader` directly (full control)

```csharp
using AddressableManager.Loaders;
using AddressableManager.Core;

var loader = new AssetLoader("MyScope"); // scope name shows in Dashboard

var sprite        = await loader.LoadAssetAsync<Sprite>("UI/Icon");
var byReference   = await loader.LoadAssetAsync<GameObject>(playerPrefabReference);
var allMusic      = await loader.LoadAssetsByLabelAsync<AudioClip>("Music");
var instance      = await loader.InstantiateAsync("Prefabs/Player", spawnPoint);

bool downloaded   = await loader.DownloadDependenciesAsync("Level1Bundle");
long downloadSize = await loader.GetDownloadSizeAsync("Level1Bundle");

loader.ClearCache();
loader.Dispose();
```

> Loading methods all return either an `IAssetHandle<T>` (ref-counted) or `null` on failure. Call `handle.Release()` when you're done — the cache holds a refcount of 1, so a single `Release()` per Retain plus the original Get returns the asset to Addressables.

## Concepts: scopes

A **scope** is just an `AssetLoader` with a name + a lifecycle hook that calls `Dispose` at the right moment. The framework ships four built-in scope types; pick one per asset based on how long it should live.

| Scope | Lifecycle | Use for |
|---|---|---|
| **Global** | Whole app, `DontDestroyOnLoad` | UI atlases, audio, fonts, shared config, anything you never want to reload |
| **Session** | Between `Assets.StartSession()` / `Assets.EndSession()` | Player profile, run state, level configs, anything that survives scene changes within a play session |
| **Scene** | Tied to the active scene; disposed on scene unload | Scene-specific materials, decorations, props |
| **Hierarchy** | Tied to a GameObject; disposed on Destroy | Per-character weapons, per-NPC effects, anything owned by a single GameObject |

Need more (e.g. one scope per multiplayer match)? Use [`ScopeManager`](EDITOR_TOOLS_GUIDE.md#scopemanager-for-multi-instance-scopes):

```csharp
using AddressableManager.Managers;

var matchLoader = ScopeManager.Instance.GetOrCreateScope($"Match_{matchId}");
var map = await matchLoader.LoadAssetAsync<MapData>($"Maps/{matchId}");

// Later
ScopeManager.Instance.ClearScope($"Match_{matchId}");
```

`ScopeManager` is reset on domain reload (`SubsystemRegistration`) so leftover state from a previous Play session never bleeds into the next.

## Pooling

```csharp
using AddressableManager.Facade;
using AddressableManager.Pooling;

// Create a pool with 8 instances preloaded
await Assets.CreatePool("Enemies/Zombie", preloadCount: 8, maxSize: 32);

// Spawn / despawn
for (int i = 0; i < 20; i++)
    Assets.Spawn("Enemies/Zombie", randomPosition);

Assets.Despawn("Enemies/Zombie", instance);

// Stats and cleanup
var stats = Assets.GetPoolStats("Enemies/Zombie"); // (activeCount, pooledCount)?
Assets.ClearPool("Enemies/Zombie");

// Swap factory at runtime (e.g. plug in Zenject/VContainer)
Assets.SetPoolFactory(new MyDiBackedPoolFactory());
```

The default factory is `UnityPoolFactory` (wraps `UnityEngine.Pool.ObjectPool`). Implement `IPoolFactory` for custom DI-driven pools. As of 2.2.0 the template prefab handle is retained per-pool and released by `ClearPool` / `Dispose`, so creating a pool no longer leaves a permanent +1 Addressables refcount.

## Progress tracking

```csharp
using AddressableManager.Progress;
using AddressableManager.Loaders;

var tracker = new ProgressTracker();
tracker.OnProgressChanged += info =>
{
    bar.SetProgress(info.Progress);
    statusLabel.text = info.CurrentOperation;
};

await loader.LoadAssetWithProgressAsync<Texture2D>(
    "Textures/Big",
    info => tracker.UpdateProgress(info));
```

Batch loads:

```csharp
var composite = new CompositeProgressTracker();
composite.OnProgressChanged += info => Debug.Log($"Batch: {info.Progress:P0}");

var t1 = new ProgressTracker(); composite.AddTracker(t1, weight: 1f);
var t2 = new ProgressTracker(); composite.AddTracker(t2, weight: 2f); // counts double

await loader.LoadMultipleWithProgressAsync<Sprite>(
    new[] { "UI/Icon1", "UI/Icon2" },
    info => composite.UpdateProgress(info));
```

Pair any `IProgressTracker` with the [`AddressableProgressBar`](EDITOR_TOOLS_GUIDE.md#runtime-ui-addressableprogressbar) component for an instant loading screen.

## Monitoring

Every load that goes through `AssetLoader` (directly or via the static `Assets` facade / scope loaders / `ScopeManager`) is reported to the Editor Dashboard automatically — no extension methods, no "monitored" overloads.

- **Window → Addressable Manager → Dashboard** (`Ctrl+Alt+A`).
- Tabs: Active Assets · Performance · Scopes · Settings.
- Implement `IAssetMonitor` and call `AssetMonitorBridge.RegisterMonitor` to forward events to analytics or in-game overlays.

Reporting is wrapped in `#if UNITY_EDITOR` — shipping builds carry zero monitoring overhead. Full details: [MONITORING_GUIDE.md](MONITORING_GUIDE.md).

## Editor tooling

- Dashboard window with live asset table, performance counters, per-scope foldouts, CSV export.
- Custom inspectors for scope components and config ScriptableObjects.
- ScriptableObject configs: `AddressablePreloadConfig`, `PoolConfiguration`, `DebugSettings`.
- `AddressableProgressBar` Play-mode inspector for visual testing of loading screens.

Full reference: [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md).

## Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                         Facade layer                          │
│   Assets (static one-liners)  →  AddressablesFacade (MB)      │
└───────────────────────────────────────────────────────────────┘
                              ↓
┌───────────────────────────────────────────────────────────────┐
│                          Scope layer                          │
│   GlobalAssetScope · SessionAssetScope · SceneAssetScope ·    │
│   HierarchyAssetScope · ScopeManager (named multi-instance)   │
│   Each owns one AssetLoader with isolated cache.              │
└───────────────────────────────────────────────────────────────┘
                              ↓
┌───────────────────────────────────────────────────────────────┐
│                          Core layer                           │
│   AssetLoader  →  IAssetHandle<T>  (ref-counted)              │
│   Address / AssetReference / Label load paths · Instantiate · │
│   DownloadDependencies · GetDownloadSize · ReleaseAsset.      │
└───────────────────────────────────────────────────────────────┘
                              ↓
┌───────────────────────────────────────────────────────────────┐
│                    Cross-cutting concerns                     │
│   ┌──────────────────────┐    ┌─────────────────────────────┐ │
│   │  Pooling             │    │  Progress tracking          │ │
│   │  IPoolFactory →      │    │  IProgressTracker /         │ │
│   │  IObjectPool<T>      │    │  CompositeProgressTracker   │ │
│   │  Unity / Custom      │    │  + AddressableProgressBar   │ │
│   └──────────────────────┘    └─────────────────────────────┘ │
│                                                                │
│   Monitoring: AssetMonitorBridge → IAssetMonitor (Editor)     │
└───────────────────────────────────────────────────────────────┘
```

Design patterns: Facade (top), Strategy/Factory (pooling), Observer (progress + monitoring), Adapter (`UnityPoolAdapter`, `CustomPoolAdapter`).

## Best practices

1. **Pick the right scope.** Loading a shader into `Hierarchy` will free-reload it every spawn; loading a per-enemy prefab into `Global` will pin the bundle forever. Use the table above.
2. **Reuse scope loaders.** Each `new AssetLoader()` is a fresh cache and a fresh Dashboard row. Resolve from the scope (`GlobalAssetScope.Instance.Loader`, `ScopeManager.GetOrCreateScope("Player")`) and pass that around.
3. **Pool spawn-heavy prefabs.** Anything you'd otherwise Instantiate / Destroy in a tight loop — projectiles, particles, enemies — belongs in `Assets.CreatePool`.
4. **Pre-download for large drops.** `Assets.GetDownloadSize` and `Assets.Download` give you size + progress for a remote group before the player needs it. Show a download bar.
5. **Trust the cache; clean at natural seams.** Scope `Dispose` and `ScopeManager.ClearScope` are the seams. Don't `ClearCache` per frame — it defeats the cache.
6. **Cap pool preloads at `maxSize`.** `preloadCount > maxSize` triggers a config validation warning; Unity's pool will destroy the overflow immediately anyway.

## Package layout

```
Packages/com.game.addressables/
├── Runtime/
│   ├── Core/          IAssetHandle, AssetHandle
│   ├── Loaders/       AssetLoader, MonitoredAssetLoader (forwarder)
│   ├── Scopes/        Global / Session / Scene / Hierarchy + Base
│   ├── Managers/      ScopeManager
│   ├── Pooling/       AddressablePoolManager + Unity / Custom adapters
│   ├── Progress/      ProgressTracker, CompositeProgressTracker,
│   │                  ProgressiveAssetLoader (extension methods)
│   ├── Monitoring/    AssetMonitorBridge, IAssetMonitor, MonitoringHelper
│   ├── UI/            AddressableProgressBar
│   ├── Facade/        Assets (static), AddressablesFacade (MonoBehaviour)
│   └── Configs/       AddressablePreloadConfig, PoolConfiguration, DebugSettings
└── Editor/
    ├── Windows/       AddressableManagerWindow (the Dashboard)
    ├── Inspectors/    Custom inspectors for scopes + configs
    ├── Data/          AssetTrackerService, EditorAssetMonitor, PerformanceMetrics
    └── Tools/         Menu items, quick-setup helpers
```

## Migration

### From 2.1.x → 2.2.x

- `AssetLoader.DownloadDependenciesAsync` now returns `Task<bool>` (it previously returned `Task<long>` with a sentinel `1`/`0`). Update any `result == 1` checks to `result == true`.
- `AssetLoaderExtensions.*Monitored` is removed. Call `AssetLoader.LoadAssetAsync` directly — monitoring is automatic.
- `MonitoredAssetLoader` is now a thin forwarder around `AssetLoader`; keep using it if you like the type, but the explicit double-monitoring layer is gone.
- TextMeshPro is optional. `AddressableProgressBar` uses TMP only when `com.unity.textmeshpro 3.0.0+` is present (asmdef `TMP_PRESENT` define); otherwise it falls back to `UnityEngine.UI.Text`.
- `PoolConfiguration.destroyOnFull` is `[Obsolete]` and hidden — the default pool always destroys excess instances above `maxSize`.

### From 2.0.x → 2.1.x

- Monitoring became automatic across all `AssetLoader` paths. Drop any explicit `LoadAssetAsyncMonitored` calls.

## Requirements

- **Unity 2022.3** or later
- `com.unity.addressables` 2.3.1+
- TextMeshPro 3.0+ — **optional**, only the `AddressableProgressBar` component needs it (gated by `TMP_PRESENT`)

No UniTask, no Newtonsoft, no other runtime dependencies. Async surface is plain `System.Threading.Tasks.Task` + `AsyncOperationHandle.Task`.

## See also

- [MONITORING_GUIDE.md](MONITORING_GUIDE.md) — Dashboard tabs, custom monitors, build behavior
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) — inspectors, configs, `ScopeManager`, recipes
- [CHANGELOG.md](CHANGELOG.md) — release notes

## License

MIT — see [LICENSE.md](LICENSE.md).

**Version**: 2.2.1
