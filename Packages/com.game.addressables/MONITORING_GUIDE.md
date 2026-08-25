# Monitoring Guide

The Addressable Manager Dashboard tracks every asset load, every release and every scope state change in **real time** while you Play in the Editor. There is no setup code, no extension methods, no "monitored" variants of the API — wherever your code calls `LoadAssetAsync`, `Spawn`, `Release`, etc., the Dashboard sees it.

> Monitoring is **Editor-only**. `EditorAssetMonitor`, `AssetTrackerService` and the Dashboard window all live in the Editor assembly, and there is no player-build UI for any of it. See [Build behaviour](#build-behaviour) for exactly what does and does not compile out.

## Quick start

1. Enter Play Mode.
2. Open **Window → Addressable Manager → Dashboard**. It has no shortcut of its own; `Ctrl+Alt+M` (`Cmd+Alt+M`) opens the hub, which is the only keyboard shortcut the package registers.
3. Load anything — `Assets.Load<T>(…)`, `scope.Loader.LoadAssetAsync<T>(…)`, `Assets.Spawn(…)` — and watch it appear.

Optional: drop a `MonitoringHelper` component on any scene GameObject (Add Component → Addressable Manager → Monitoring Helper). It's purely a discoverable indicator — monitoring runs whether or not it's there. Its two serialized fields (`enableMonitoring`, default on; `verboseLogging`, default off) only decide whether it logs a line in `Awake`; turning `enableMonitoring` off does **not** stop the Dashboard from tracking.

## What is tracked

| Event | Source | Reported as |
|---|---|---|
| Asset load (cache miss) | `AssetLoader.LoadAssetAsync` / `LoadAssetsByLabelAsync` | `OnAssetLoaded(address, typeName, scopeName, loadDuration, fromCache: false)` |
| Asset load (cache hit) | `AssetLoader.LoadAssetAsync` | `OnAssetLoaded(…, fromCache: true)` |
| Asset release | The underlying Addressables operation actually being released — the last `IAssetHandle.Release()`/`Dispose()`, or a `ForceRelease` from the owning loader or cache | `OnAssetReleased(address, typeName)` |
| Scope construction | `BaseAssetScope` ctor (Global / Scene / Hierarchy) | `OnScopeRegistered(scopeId, isActive: false)`, preceded by `ReportScopeDisplayName(scopeId, displayName)` |
| Scope creation via the manager | `ScopeManager.GetOrCreateScope` | `OnScopeRegistered(scopeId, isActive: true)` |
| Scope activation | `BaseAssetScope.Activate` | `OnScopeStateChanged(scopeId, isActive: true)` |
| Scope deactivation | `BaseAssetScope.Deactivate` | `OnScopeStateChanged(scopeId, isActive: false)` |
| Scope dispose | `BaseAssetScope.Dispose` / `ScopeManager.ClearScope` | `OnScopeCleared(scopeId)` |

Handles built through the **public** `AssetHandle` constructor report nothing on release — only handles `AssetLoader` created carry the address and type name that the release report needs.

### Scope identity vs. the label you see

The value carried through the event stream is the scope **id**, and for Scene and Hierarchy scopes the id is instance-qualified so two scopes with the same name stay distinguishable. The Dashboard renders the friendly **display name** next to a category derived from the id, via `AssetMonitorBridge.GetDisplayName`.

| If you load via… | Scope id reported | Shown in the Dashboard as |
|---|---|---|
| `Assets.Load` / `AddressablesFacade.LoadGlobalAsync` | `Global` | `Global` |
| `Assets.LoadSession` / `AddressablesFacade.LoadSessionAsync` | `Session` | `Session` |
| `Assets.LoadScene` / `SceneAssetScope.GetOrCreate().Loader` | `Scene-<sceneName>#h<sceneHandle>` | `<sceneName> (Scene)` |
| `HierarchyAssetScope` on a GameObject | `Hierarchy-<goName>#<tag>`, where `<tag>` is `GetInstanceID()` below Unity 6000.5 and `GetEntityId()` from 6000.5 on | `<goName> (Hierarchy)` |
| `HybridScope.Global` / `.Session` / `.GetNamed(type, name)` | `Hybrid:Global` / `Hybrid:Session` / `Hybrid:<type>:<name>` | `Global` / `Session` / `Hybrid` |
| `Assets.Spawn` and the rest of the pooling API | `Pool` | `Custom` |
| `ScopeManager.Instance.GetOrCreateScope("PlayerSession")` | `PlayerSession` | `Custom` |
| `new AssetLoader("MyScope")` | `MyScope` | `Custom` |
| `new AssetLoader()` | `Unknown` | `Custom` |

`SceneAssetScope.CreateForScene` and `HierarchyAssetScope.AddTo` both take an optional `customScopeId` and `customDisplayName`; both are also `[SerializeField]` fields on the components, so you can set them in the Inspector.

The label is set when the loader is **constructed** — there is no per-call scope override. If you need a unique name, create the loader with one (`ScopeManager.Instance.GetOrCreateScope` is the easiest path; note that it refuses the id `"Global"` and returns null, because `GlobalAssetScope` owns that one).

## Dashboard tabs

Four tabs — **Active Assets**, **Performance**, **Scopes**, **Settings** — plus a CDN status strip above them that polls once a second and hands off to the CDN Manager window.

### Active Assets
Live list of every tracked handle that is still valid. Search filters on address and type name; the scope dropdown filters on scope id and is repopulated from the live scope set. Each row shows the address, then `<type> • <scope> Scope • Loaded <n> ago`, the live refcount, and the estimated memory in KB. Rows are ordered newest-load-first.

Use it to spot leaks — anything that has been alive an order of magnitude longer than expected, or whose refcount keeps climbing.

### Performance
Four counters — Total Assets, Cache Hit Ratio, Total Memory, Avg Load Time — plus a memory-over-time graph with peak and average, and a Slowest Loading Assets list. **Export Report (CSV)** opens a save dialog and writes `PerformanceMetrics.ExportToCSV()` to the chosen file.

A healthy cache-hit ratio depends on your access pattern, but anything below ~30 % usually means you're re-creating loaders instead of reusing scope loaders.

### Scopes
One foldout per **live** scope id, built dynamically as ids appear and sorted by category then display name — not a fixed Global/Session/Scene/Hierarchy set. Each shows `Assets: N | Memory: N MB` (plus `inactive` when the scope is deactivated), the asset list, and a **Cleanup** button. **Cleanup All Scopes** at the top does the same for every tracked scope.

Cleanup does two different things depending on where you are, and the confirmation dialog says which:

- **In Play Mode, with a live loader** — `ScopeManager.ClearScope` runs, so real Addressables handles are released. Anything still using those assets will break.
- **Otherwise** — only the Dashboard's own rows are cleared. Nothing is released, because there is nothing live to release.

### Settings
- **Log Level** (`None` / `Errors Only` / `Warnings and Errors` / `All`), **Simulate Slow Loading**, **Delay (ms)** and **Failure Rate (%)** write straight through to `DebugSettings.Instance`.
- **Auto Refresh** and **Refresh Interval (ms)** (100–5000, default 500) drive the window's own refresh timer.
- **Reset All Settings** restores the widgets to their defaults; **Reset Statistics** clears the tracker and the metrics after a confirm dialog.

Two limits worth knowing before you rely on this tab:

- **Only the log level is actually consumed.** `logLevel` is read through `DebugSettings.IsVerbose` (true only at `All`), which gates verbose logging in `AssetLoader` and `CdnTelemetry`. `simulateSlowLoading`, `simulatedDelayMs` and `simulateFailureRate` are stored on the asset and **nothing reads them** — no load is delayed and no failure is injected.
- **The write only sticks if a real asset exists.** The settings are persisted with `EditorUtility.SetDirty`, and only when the object is in the AssetDatabase. Without an asset at `Resources/AddressableManager/DebugSettings`, `DebugSettings.Instance` is a transient in-memory instance and your change is lost on the next domain reload. Create one via **Window → Addressable Manager → Settings** (it offers to make one) or **Assets → Addressable Manager → Create Debug Settings**.

## Custom monitors

`AssetMonitorBridge` is public — register your own `IAssetMonitor` to forward events to analytics, an in-game overlay, automated tests, etc.

```csharp
using AddressableManager.Monitoring;

public sealed class AnalyticsMonitor : IAssetMonitor
{
    public void OnAssetLoaded(string address, string typeName, string scopeName, float loadDuration, bool fromCache)
        => Analytics.Track("asset.loaded", address, loadDuration, fromCache);

    public void OnAssetReleased(string address, string typeName) { }
    public void OnScopeRegistered(string scopeName, bool isActive) { }
    public void OnScopeStateChanged(string scopeName, bool isActive) { }
    public void OnScopeCleared(string scopeName) { }
}

// During bootstrap
AssetMonitorBridge.RegisterMonitor(new AnalyticsMonitor());
```

`RegisterMonitor` / `UnregisterMonitor` take a lock and swap a fresh array, and the `Report*` methods iterate a snapshot, so registration is thread-safe and monitor implementations are free to do their own work on background threads — just don't touch Unity objects from there. `RegisterMonitor` is idempotent: registering the same instance twice is a no-op.

**Register after `SubsystemRegistration`.** `AssetMonitorBridge` clears its entire monitor list from a `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` hook, so any monitor registered before that point — including from `[InitializeOnLoad]` — is dropped at the start of every Play session. That is deliberate: it stops stale Editor monitors from a previous session firing into disposed objects. `EditorAssetMonitor` handles it by re-registering at `BeforeSceneLoad` and again on `PlayModeStateChange.EnteredPlayMode`; do the same for your own.

`AssetMonitorBridge` also carries the scope id → display name map (`ReportScopeDisplayName` / `GetDisplayName`). `GetDisplayName` returns the id itself when no label was recorded, never null. The map is kept outside `IAssetMonitor` on purpose, so adding it did not break existing implementers.

## Build behaviour

In a non-Editor build:

- The `AssetMonitorBridge.ReportAssetLoaded` call sites in `AssetLoader` and the `ReportAssetReleased` call site in `AssetHandle` are wrapped in `#if UNITY_EDITOR` and compile away entirely.
- The scope-lifecycle reports (`ReportScopeRegistered`, `ReportScopeDisplayName`, `ReportScopeStateChanged`, `ReportScopeCleared`, from `BaseAssetScope`, `ScopeManager` and `HybridScope`) are **not** compiled out. They still run — but with no monitor registered they iterate an empty array, so the cost is a static field read per scope lifecycle event, and no per-load cost at all.
- `MonitoredAssetLoader` is a thin forwarder around `AssetLoader`, kept for source compatibility. It adds no monitoring of its own; `AssetLoader` has had that built in since 2.1.0.
- `DebugSettings.Instance` returns a transient default and `DebugSettings.IsVerbose` is always `false` — the `Resources.Load<DebugSettings>` lookup is itself Editor-gated.
- `MonitoringHelper` compiles into the build. With `verboseLogging` on it logs `"[MonitoringHelper] Monitoring only works in Editor. Dashboard not available in builds."` once, in `Awake`.

There is no separate "monitored" code path. The Dashboard is exclusively an Editor convenience.

## Troubleshooting

**Dashboard shows the scope but no assets.** You opened the Dashboard but never loaded anything yet — loads only appear after the load completes. Drop a `Debug.Log` next to the load to confirm it actually runs.

**Scope name shows as `Unknown`.** A bare `new AssetLoader()` defaults to `"Unknown"`. Either construct with a scope name or get the loader from a real scope (`GlobalAssetScope.Instance.Loader`, `ScopeManager.Instance.GetOrCreateScope("…")`).

**A custom `IAssetMonitor` never fires.** You registered it before `SubsystemRegistration`, which clears the list. Re-register from `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]` or later. See [Custom monitors](#custom-monitors).

**Refcount never goes back to zero.** You are calling `Retain()` without a matching `Release()`, or holding the `IAssetHandle` past the GameObject that owned it. Inspect the asset row in the Dashboard — it shows the live refcount.

**Cache-hit ratio stays near zero.** You're allocating a fresh `new AssetLoader()` per load instead of reusing a scope-owned loader, so each load is its own cache.

**Cleanup didn't free anything.** Outside Play Mode there is no live loader to release — the button only clears the Dashboard's rows, and the dialog says so before you confirm.

**Slow-loading / failure-rate simulation does nothing.** It genuinely does nothing: those `DebugSettings` fields have no readers anywhere in the package. See [Settings](#settings).

**Memory numbers look off.** They are **estimates** keyed by type name, not authoritative bytes. For real numbers use the Unity Profiler. The Dashboard is for relative comparison and leak hunting.

## See also

- [README.md](README.md) — package overview
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) — Dashboard, inspectors, configs
- [CHANGELOG.md](CHANGELOG.md) — release notes
