# Monitoring Guide

The Addressable Manager Dashboard tracks every asset load, every release and every scope state change in **real time** while you Play in the Editor. There is no setup code, no extension methods, no "monitored" variants of the API — wherever your code calls `LoadAssetAsync`, `Spawn`, `Release`, etc., the Dashboard sees it.

> Monitoring is **Editor-only**. Reporting calls are wrapped in `#if UNITY_EDITOR`, so shipping builds carry zero monitoring overhead.

## Quick start

1. Enter Play Mode.
2. Open **Window → Addressable Manager → Dashboard** (shortcut: `Ctrl+Alt+A`).
3. Load anything — `Assets.Load<T>(…)`, `scope.Loader.LoadAssetAsync<T>(…)`, `Assets.Spawn(…)` — and watch it appear.

Optional: drop a `MonitoringHelper` component on any scene GameObject (Add Component → Addressable Manager → Monitoring Helper). It's purely a discoverable indicator — monitoring runs whether or not it's there.

## What is tracked

| Event | Source | Reported as |
|---|---|---|
| Asset load (cache miss) | `AssetLoader.LoadAssetAsync` / `LoadAssetsByLabelAsync` | `OnAssetLoaded(address, type, scope, duration, fromCache: false)` |
| Asset load (cache hit) | `AssetLoader.LoadAssetAsync` | `OnAssetLoaded(..., fromCache: true)` |
| Asset release | `IAssetHandle.Release` going to refcount 0 | `OnAssetReleased(address, type)` |
| Scope construction | `BaseAssetScope` ctor | `OnScopeRegistered(name, isActive: false)` |
| Scope activation | `BaseAssetScope.Activate` | `OnScopeStateChanged(name, isActive: true)` |
| Scope deactivation | `BaseAssetScope.Deactivate` | `OnScopeStateChanged(name, isActive: false)` |
| Scope dispose | `BaseAssetScope.Dispose` / `ScopeManager.ClearScope` | `OnScopeCleared(name)` |

The **scope name** that appears in the Dashboard comes from whichever channel originated the load:

| If you load via… | Scope name shown |
|---|---|
| `Assets.Load` / `AddressablesFacade.LoadGlobalAsync` | `Global` |
| `Assets.LoadSession` / session scope | `Session` |
| `Assets.LoadScene` / `SceneAssetScope.GetOrCreate().Loader` | `Scene-<sceneName>` |
| `HierarchyAssetScope` on a GameObject | `Hierarchy-<goName>` |
| `ScopeManager.Instance.GetOrCreateScope("PlayerSession")` | `PlayerSession` |
| `new AssetLoader("MyScope")` | `MyScope` |
| `new AssetLoader()` | `Unknown` |

The label is set when the loader is **constructed** — there is no per-call scope override. If you need a unique name, create the loader with one (`ScopeManager.GetOrCreateScope` is the easiest path).

## Dashboard tabs

### Active Assets
Live list of every handle currently alive. Search by name, filter by scope. Columns: address, type, scope, refcount, est. memory, time-since-loaded.

Use it to spot leaks — anything that has been alive an order of magnitude longer than expected, or whose refcount keeps climbing.

### Performance
Aggregated counters: total assets, cache-hit ratio, total estimated memory, average load time, top-10 slowest loads. "Export Report (CSV)" dumps the metrics to disk for offline analysis.

A healthy cache-hit ratio depends on your access pattern, but anything below ~30 % usually means you're re-creating loaders instead of reusing scope loaders.

### Scopes
One foldout per scope (Global / Session / Scene / Hierarchy / custom). Shows asset count, estimated memory, and a per-scope cleanup button. Useful for verifying that scopes actually drain when you expect them to (e.g. after a scene unload).

### Settings
Log-level for `DebugSettings`, dashboard refresh rate (100–5000 ms), and load-simulation toggles (slow loading, artificial failure rate) for stress testing.

## Custom monitors

`AssetMonitorBridge` is public — register your own `IAssetMonitor` to forward events to analytics, an in-game overlay, automated tests, etc.

```csharp
using AddressableManager.Monitoring;

public sealed class AnalyticsMonitor : IAssetMonitor
{
    public void OnAssetLoaded(string address, string type, string scope, float duration, bool fromCache)
        => Analytics.Track("asset.loaded", address, duration, fromCache);

    public void OnAssetReleased(string address, string type) { }
    public void OnScopeRegistered(string scopeName, bool isActive) { }
    public void OnScopeStateChanged(string scopeName, bool isActive) { }
    public void OnScopeCleared(string scopeName) { }
}

// During bootstrap
AssetMonitorBridge.RegisterMonitor(new AnalyticsMonitor());
```

`RegisterMonitor` / `UnregisterMonitor` are thread-safe (the listener list is copy-on-write), so monitor implementations are free to do their own work on background threads — just don't touch Unity objects from there.

The bridge is auto-cleared on domain reload and `SubsystemRegistration`, so leftover Editor-side monitors from a previous Play session never fire into disposed objects.

## Build behavior

In a non-Editor build:

- `AssetMonitorBridge.Report*` calls compile to no-ops at the call sites in `AssetLoader` (each is wrapped in `#if UNITY_EDITOR`).
- `MonitoredAssetLoader` is a thin pass-through wrapper around `AssetLoader` — same zero overhead.
- `DebugSettings.Instance` returns a transient default; the `Resources.Load<DebugSettings>` lookup is itself Editor-gated.

There is no separate "monitored" code path. The Dashboard is exclusively an Editor convenience.

## Troubleshooting

**Dashboard shows the scope but no assets.** You opened the Dashboard but never loaded anything yet — loads only appear after `LoadAssetAsync` completes. Drop a `Debug.Log` next to the load to confirm it actually runs.

**Scope name shows as `Unknown`.** A bare `new AssetLoader()` defaults to `"Unknown"`. Either construct with a scope name or get the loader from a real scope (`GlobalAssetScope.Instance.Loader`, `ScopeManager.Instance.GetOrCreateScope("…")`).

**Refcount never goes back to zero.** You are calling `Retain()` without a matching `Release()`, or holding the `IAssetHandle` past the GameObject that owned it. Inspect the asset row in the Dashboard — the column shows the live refcount.

**Cache-hit ratio stays near zero.** You're allocating a fresh `new AssetLoader()` per load instead of reusing a scope-owned loader, so each load is its own cache.

**Memory numbers look off.** They are **estimates** keyed by type, not authoritative bytes. For real numbers use the Unity Profiler. The Dashboard is for relative comparison and leak hunting.

## See also

- [README.md](README.md) — package overview
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) — Dashboard, inspectors, configs
- [CHANGELOG.md](CHANGELOG.md) — release notes
