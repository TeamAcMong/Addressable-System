# Troubleshooting Guide

**Solutions to common issues and error messages**

This guide helps you diagnose and fix common problems with the Addressable Manager system.

---

## Quick Diagnosis Table

| Symptom | Likely Cause | Section |
|---------|-------------|---------|
| Loads return null | Address typo, wrong type, or disposed scope | [Runtime Loading](#runtime-loading-issues) |
| Rules not matching | Filter misconfiguration (usually Match Mode) | [Rule Matching](#rule-matching-issues) |
| Dashboard blank | UXML file missing | [Editor Windows](#editor-window-issues) |
| Slow performance | Too many tracked assets | [Performance](#performance-issues) |
| Memory not freed | Handles not released | [Memory Management](#memory-management-issues) |
| Compilation errors | Missing assembly reference or old API | [Compilation](#compilation-errors) |
| Version errors | Git/Build config | [Versioning](#versioning-issues) |
| CDN error code | See the per-code table | [CDN Content Delivery](#cdn-content-delivery) |

---

## Table of Contents

1. [Runtime Loading Issues](#runtime-loading-issues)
2. [Rule Matching Issues](#rule-matching-issues)
3. [Editor Window Issues](#editor-window-issues)
4. [Performance Issues](#performance-issues)
5. [Memory Management Issues](#memory-management-issues)
6. [Compilation Errors](#compilation-errors)
7. [Versioning Issues](#versioning-issues)
8. [CI/CD Issues](#cicd-issues)
9. [Platform-Specific Issues](#platform-specific-issues)
10. [Data Corruption](#data-corruption)
11. [CDN Content Delivery](#cdn-content-delivery)

> **API orientation.** The public entry points are the three tiers —
> `AddressableManager.API.Simple`, `.Standard`, `.Advanced` — plus the
> `AddressableManager.Facade.Assets` facade. There is no `AddressableManager` *type*;
> `AddressableManager` is a namespace root. Nothing in this package loads a Unity
> **scene**: "scene scope" means a cache whose lifetime is tied to a scene.

---

## Runtime Loading Issues

### Issue: "Failed to load asset" in the Console

**Symptoms**:
```
InvalidKeyException: Exception of type 'UnityEngine.AddressableAssets.InvalidKeyException' was thrown
No locations found for key: 'my_asset'
[AssetLoader] Failed to load asset: my_asset. Error: ...
```

**Causes**:
1. Address doesn't exist
2. Asset not marked as addressable
3. Typo in address string
4. Content built for a different platform or not built at all

**Note**: `AssetLoader.LoadAssetAsync<T>` does **not** rethrow. It logs the error and
returns `null`, so the exception you see in the Console comes from Addressables itself.
Your `IAssetHandle<T>` will simply be null — always null-check it.

**Solutions**:

**Step 1: Verify Address Exists**
```csharp
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;

var handle = Addressables.LoadResourceLocationsAsync("my_asset");
IList<IResourceLocation> locations = await handle.Task;
if (locations == null || locations.Count == 0)
{
    Debug.LogError("Address 'my_asset' not found");
}
Addressables.Release(handle);
```

**Step 2: Check Addressable Settings**
```
1. Window > Asset Management > Addressables > Groups
2. Search for your asset
3. Verify it's in an addressable group
4. Check the address is correct (case-sensitive)
```

**Step 3: Rebuild Addressables**
```
1. Window > Asset Management > Addressables > Groups
2. Build > New Build > Default Build Script
3. Try loading again
```

**Step 4: Use Layout Viewer**
```
1. Window > Addressable Manager > Layout Viewer
2. Type the asset name into the toolbar Search field
3. Check the computed address and labels
```

**Step 5: Get a typed error instead of a null**

`LoadAssetAsync` throws nothing and tells you nothing. The `Safe` variants return a
`LoadResult<T>` carrying a `LoadErrorCode`:

```csharp
using AddressableManager.API;
using AddressableManager.Core;

var result = await Standard.LoadSafe<Texture2D>("texture");
if (result.IsFailure)
{
    Debug.LogError($"{result.ErrorCode}: {result.ErrorMessage}\n{result.Error.Hint}");
}
```

`LoadErrorCode` values: `None`, `InvalidAddress`, `AssetNotFound`,
`InvalidAssetReference`, `InvalidLabel`, `OperationFailed`, `LoaderDisposed`,
`ThreadSafetyViolation`, `TypeMismatch`, `NetworkError`, `ContentNotDownloaded`.

**`ContentNotDownloaded` is the one people misdiagnose.** It means the address is valid
and the bundle simply is not on this device yet — download it, do not go hunting for a
missing entry in your Addressables groups.

---

### Issue: Loading Returns Null

**Symptoms**:
```csharp
var handle = await Standard.LoadGlobal<Texture2D>("texture");
// handle is null, but no exception thrown
```

**Causes**:
1. Wrong type specified (`TypeMismatch`)
2. Empty or null address
3. The loader was disposed, or its scope was
4. The load was started off the main thread

**Check Asset Type**:
```csharp
// If the asset is actually a Sprite, not a Texture2D:
var handle = await Standard.LoadGlobal<Sprite>("texture");
```

The cache is keyed by `(address, Type)`, so loading the same address as two different
types produces two independent cache entries — that is by design, not a bug.

**Verify Asset Loaded**:
```csharp
var handle = await Standard.LoadGlobal<Texture2D>("texture");
if (handle != null && handle.IsValid)   // IsValid is a PROPERTY, not a method
{
    Debug.Log($"Loaded {handle.Asset.name}, refs: {handle.ReferenceCount}");
}
```

**Check Scope Lifetime**:
```csharp
using AddressableManager.API;
using AddressableManager.Scopes;

// BAD: the scope's cache is cleared while the caller is still using the asset
var scope = SceneAssetScope.GetOrCreate();
var handle = await scope.Loader.LoadAssetAsync<Texture2D>("texture");
scope.Deactivate();                        // ClearCache() runs here
myRenderer.material.mainTexture = handle.Asset;   // handle.IsValid is now false

// GOOD: keep the handle for as long as you use the asset, and release it yourself
_handle = await Standard.LoadGlobal<Texture2D>("texture");
myRenderer.material.mainTexture = _handle.Asset;
// ... later ...
_handle.Release();
```

**Simple.Load hands you a raw asset you do not control.**
`Simple.Load<T>(address)` returns the asset, not a handle, and disposes the handle
internally. That is fine for a long-lived asset in the Global scope, but it means you
have no way to keep it alive: applying a CDN catalog update invalidates every loader
cache, and `Simple.ReleaseAddress` / `Simple.ClearAll` evict unconditionally — either
can destroy the object while you are still holding the reference. Use
`Standard.LoadGlobal<T>` when the lifetime matters.

---

### Issue: A named scope is missing

**Symptoms**:
```
[Standard.ClearCache] ...  Registered scopes: Global, Session, ...
[ScopeManager] 'Global' is reserved for GlobalAssetScope ...
```

**Causes**:
1. The scope was disposed (its GameObject was destroyed, or the scene unloaded)
2. The scope id is not what you think it is
3. You tried to create a manager-owned scope named `"Global"`

**Check before you use it**:
```csharp
using AddressableManager.Managers;

if (ScopeManager.Instance.HasScope("PlayerSession"))
{
    var loader = ScopeManager.Instance.GetScope("PlayerSession");
    var handle = await loader.LoadAssetAsync<Texture2D>("logo");
}
else
{
    Debug.LogWarning("Scope 'PlayerSession' no longer exists");
}
```

`ScopeManager.Instance.ActiveScopes` enumerates every registered scope id, which is the
fastest way to see what the ids actually are.

**Scope ids are not always the names you typed.** Scene and Hierarchy scopes qualify
their ids with the owner's identity so two scopes with the same name stay distinct:

| Scope | Id |
|---|---|
| `GlobalAssetScope` | `Global` |
| Facade session | `Session` |
| `SceneAssetScope` | `Scene-<sceneName>#h<sceneHandle>` |
| `HierarchyAssetScope` | `Hierarchy-<goName>#<instanceTag>` |
| `HybridScope` | `Hybrid:Global`, `Hybrid:Session`, `Hybrid:<type>:<name>` |
| `ScopeManager.GetOrCreateScope("X")` | `X` |

Both `SceneAssetScope` and `HierarchyAssetScope` accept a `customScopeId` (a
constructor argument on `CreateForScene` / `AddTo`, and a serialized field in the
Inspector) if you need a stable id.

**`GetOrCreateScope("Global")` returns null** and logs an error — that id belongs to
`GlobalAssetScope`. Use `GlobalAssetScope.Instance.Loader`, or
`AddressablesFacade.Instance.GlobalLoader`, for the real Global cache.

**These are four separate caches, not one.** `GlobalAssetScope`, the ScopeManager's
`"Session"` entry, Scene/Hierarchy scopes and `HybridScope` share no state.
`HybridScope.Global` is **not** `AddressablesFacade.Instance.GetGlobalScope()`. Loading
through one and clearing another does nothing.

---

## Rule Matching Issues

### Issue: Rules Not Matching Any Assets

**Symptoms**:
- Preview pane shows nothing
- Apply reports 0 assets processed

**Cause #1 — and by a wide margin the most common: PathFilter's Match Mode.**

`PathFilter` defaults to **`Contains`**, with `_pattern = "Assets/"` and
`_caseSensitive = false`. In `Contains`, `StartsWith`, `EndsWith` and `Exact` the
pattern is compared with plain string operations, so **a pattern containing `*` or `**`
matches nothing at all**.

```
Match Mode: Contains  +  Pattern "Assets/UI/**/*.png"   →  0 assets. Always.
Match Mode: Glob      +  Pattern "Assets/UI/**/*.png"   →  works
Match Mode: Contains  +  Pattern "Assets/UI/"           →  works
```

`PathMatchMode` values, in declaration order: `Contains`, `StartsWith`, `EndsWith`,
`Exact`, `Regex`, `Glob`. (`Glob` is last for serialization compatibility — do not
reorder them.)

Glob syntax, when Match Mode is `Glob`: `*` matches within one path segment, `?`
matches one character, `**/` matches zero or more segments, and a trailing `**` matches
the rest of the path.

Setting Match Mode to `Regex` and typing `**` is not a workaround: it throws, the
exception is caught and logged as `[PathFilter] Invalid Regex pattern …`, and the
filter then returns false for everything.

**Cause #2 — a disabled filter is not a no-op you can ignore.** `AssetFilterBase.IsMatch`
returns **true** for a disabled filter, so disabling one widens the rule rather than
narrowing it. `Invert` flips the result of an enabled filter.

**Other causes**: file extension mismatch, asset type mismatch, or no assets at the path.

**Solutions**:

**Test Filter Individually**
```
1. Remove all but one filter from the rule
2. Click "Refresh Preview"
3. If it matches, add the next filter back
4. Filters combine with AND — find which one excludes everything
```

**Check Asset Database**
```csharp
var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/UI" });
Debug.Log($"Found {guids.Length} textures in Assets/UI");

foreach (var guid in guids)
{
    string path = AssetDatabase.GUIDToAssetPath(guid);
    Debug.Log($"Asset: {path}");
}
```

---

### Issue: Wrong Assets Matching

**Symptoms**:
- Preview shows unexpected assets
- Rules applying to wrong files

**Causes**:
1. Filter pattern too broad
2. Multiple rules conflict
3. Priority ordering issue

**Make Filter More Specific** (with Match Mode set to `Glob`):
```
TOO BROAD: "Assets/**/*.png"
BETTER:    "Assets/UI/**/*.png"
SPECIFIC:  "Assets/UI/Buttons/**/*.png"
```

**Check Rule Priority**
```
Rules are processed in priority order (highest first).
If two rules match the same asset, the higher priority wins.

Example:
- Rule A (Priority 200): "Assets/UI/**/*.png" → Group "UI"
- Rule B (Priority 100): "Assets/**/*.png"    → Group "All"
Result: UI assets go to "UI" group (Rule A wins)
```

**Find conflicts**
```
1. Window > Addressable Manager > Layout Viewer
2. Click "Refresh" — conflicts are computed as part of the scan
3. Tick "Conflicts Only" to hide everything else
4. "Export Report" writes the result to a file
```

There is no "Detect Conflicts" button; the Layout Viewer toolbar is
**Refresh / Auto Refresh / Search / Conflicts Only / Export Report**. For CI, use
`AddressableCLI.DetectConflicts`, which writes JSON to `-reportFilePath` (default
`conflicts.json`) and exits 1 when conflicts exist.

---

### Issue: Provider Generating Empty Output

**Symptoms**:
- Assets end up with blank addresses

**Causes**:
1. Provider misconfigured
2. Every path segment stripped away
3. Asset filename issues

**Real provider settings** (these are the fields that exist — there is no "Base
Directory" or "Strip Extension" field anywhere):

```
FileNameAddressProvider
  Include Extension   (default false — the address is the bare file name)
  To Lower Case       (default false)
  Prefix / Suffix     (default empty)

PathAddressProvider
  Remove Assets Prefix        (default true)
  Remove Extension            (default true)
  Remove Root Folder          (default "" — a folder name, not a path)
  Path Separator Replacement  (default "/")
  To Lower Case               (default false)
  Prefix / Suffix             (default empty)
```

If `PathAddressProvider` produces an empty address, check `Remove Root Folder`: it
strips a named folder from the front of the path and can leave nothing behind for
shallow assets.

**Test a provider manually**:
```csharp
using AddressableManager.Editor.Providers;

var provider = ScriptableObject.CreateInstance<FileNameAddressProvider>();
provider.Setup();

string address = provider.Provide("Assets/UI/button_start.png");
Debug.Log($"Generated address: {address}"); // "button_start"
```

---

## Editor Window Issues

### Issue: Dashboard Shows Only an Error Label

**Symptoms**:
- **Window > Addressable Manager > Dashboard** opens with a single label:
  `Failed to load UI. Check UXML file path.`
- Console shows `[AddressableManager] Failed to load UXML file. Creating fallback UI.`

**Cause**: The Dashboard is the only window built from UXML, and it loads its assets by
absolute package path. If either is missing, the window falls back to that one label —
the fallback has **no functionality at all**, it is purely a diagnostic.

**Check the files exist**:
```
Packages/com.game.addressables/Editor/UI/AddressableManagerWindow.uxml
Packages/com.game.addressables/Editor/UI/Styles.uss
```

**Verify Package Installation**:
```
1. Window > Package Manager
2. Find "Addressable Manager"
3. If the package was copied rather than installed, the hard-coded
   "Packages/com.game.addressables/..." path will not resolve — install it under
   Packages/ with that exact folder name.
```

The **Layout Rule Editor**, **Layout Viewer** and the scope Inspectors are IMGUI and use
no UXML, so a missing UXML file cannot blank them. The **CDN Manager** window has its own
UXML under `Editor/Cdn/UI/`.

---

### Issue: Preview Pane Not Updating

**Symptoms**:
- Click "Refresh Preview" and nothing changes
- Preview shows old data

**Causes**:
1. Filter `Setup()` not called
2. Asset database out of sync
3. The preview limit was already reached

**Force Asset Database Refresh**:
```
1. Right-click in the Project window
2. Reimport All
3. Wait for the import to complete
4. Try the preview again
```

**Preview Limit**:
```
The Layout Rule Editor's preview pane has a "Preview Limit:" slider, range 10-200,
default 50. The preview stops collecting once it hits the limit, so a rule that matches
thousands of assets shows only the first N. Raise it to see more; lower it if generating
the preview is slow.
```

**Check Console for Errors**:
```
Preview generation logs filter and provider errors rather than surfacing them in the UI.
```

---

## Performance Issues

### Issue: Slow Rule Application

**Symptoms**:
- "Apply All" takes several minutes
- Editor becomes unresponsive

**Causes**:
1. Too many assets in project
2. Complex filter combinations
3. `DependentObjectFilter` in `Recursive` mode on large sets
4. Verbose logging enabled

**Optimize Filters**:
```
SLOW:
- FindAssetsFilter (runs an AssetDatabase search per evaluation)
- DependentObjectFilter with Dependency Mode = Recursive
- Broad glob patterns

FAST:
- PathFilter with a specific pattern
- TypeFilter
- ExtensionFilter
```

**Disable Verbose Logging**:
```
1. Select the LayoutRuleData asset
2. Uncheck "Verbose Logging" (it is off by default)
3. Apply rules
```

**Split Rule Sets**:
```
Instead of one giant rule set:
- UI_Rules.asset (UI assets only)
- Audio_Rules.asset (audio assets only)
- Models_Rules.asset (models only)

Apply each separately, or merge them with a CompositeLayoutRuleData when you do want
one pass.
```

**Use the CLI**:
```bash
Unity.exe -quit -batchmode -projectPath . \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset"
```

**Check auto-apply**: `AddressableAutoProcessor` runs after every asset import, but only
for `LayoutRuleData` assets whose **Auto Apply On Import** is ticked (it is off by
default). If imports feel slow, that flag is the first thing to check.

---

### Issue: Editor Lag in the Dashboard

**Symptoms**:
- Dashboard window stutters
- High CPU usage

**Causes**:
1. Too many tracked assets
2. Auto-refresh with a short interval

**Adjust Refresh Settings**:
```
1. Open the Dashboard
2. Settings tab
3. Raise "Refresh Interval (ms)" — the slider runs 100-5000, default 500
4. Or untick "Auto Refresh"
```

These two are the only Settings-tab controls that affect the window itself.

**Limit Tracked Assets**:
```
Release unused assets promptly, and use scope-owned loaders so cleanup is automatic.
Window > Addressable Manager > Clear All Caches empties the Editor's tracking data
(AssetTrackerService and PerformanceMetrics). It does NOT release any runtime asset.
```

**Close Unused Windows**:
```
The Layout Viewer's "Auto Refresh" toggle rescans on a timer. Turn it off or close the
window when you are not using it.
```

---

## Memory Management Issues

### Issue: Memory Not Released

**Symptoms**:
- Memory usage keeps growing
- Assets remain loaded after you thought you released them

**Causes**:
1. Handles not released
2. Scopes not disposed
3. References held in code
4. Pooled objects destroyed instead of recycled

**Always Release Handles**:
```csharp
using AddressableManager.API;

// BAD: handle leaked
var handle = await Standard.LoadGlobal<Texture2D>("texture");
// never released

// GOOD: manual release
var handle = await Standard.LoadGlobal<Texture2D>("texture");
// ... use handle.Asset ...
handle.Release();

// BETTER: using statement — Dispose() is identical to Release()
using var handle = await Standard.LoadGlobal<Texture2D>("texture");
```

`Release()` decrements the reference count and drops the Addressables operation when it
reaches zero. `Retain()` increments it and **throws `ObjectDisposedException`** if the
handle already hit zero — use the `TryRetain()` extension where that is an expected
outcome.

**`Simple.Release<T>(asset)` releases nothing.** It is `[Obsolete]`, it is a genuine
no-op, and it logs one warning per process. An asset instance cannot be mapped back to
the cache entry holding it: the cache is keyed by `(address, Type)` with no reverse map.
Use instead:

```csharp
Simple.ReleaseAddress("texture");   // evict every (address, Type) entry in Global
Simple.ClearAll();                  // clear the whole Global cache
Standard.LoadGlobal<T>("texture");  // ... or take a handle you dispose yourself
```

`ReleaseAddress` evicts unconditionally: any `IAssetHandle` still held for that address
goes `IsValid == false`.

**Clear the right cache**:
```csharp
Standard.ClearGlobalCache();          // Global only
Standard.ClearSessionCache();         // the facade's "Session" scope
Standard.ClearCache("PlayerSession"); // a ScopeManager scope, by id

// Unknown id -> Debug.LogError listing the scopes that ARE registered.
// This clears the cache; it does NOT dispose the loader, and pools are not reachable
// this way (the pool manager has its own unregistered loader named "Pool").
```

**Dispose Scopes**:
```csharp
using AddressableManager.Scopes;

// Scene / Hierarchy scopes are MonoBehaviours: they dispose when their GameObject
// or scene goes away. Nothing extra to do.
var scope = SceneAssetScope.GetOrCreate();

// A ScopeManager scope is disposed by id:
ScopeManager.Instance.ClearScope("PlayerSession");
ScopeManager.Instance.ClearAllExceptGlobal();
```

**Return Pooled Objects**:
```csharp
// Create the pool first — Simple.Pool returns NULL on the first call for an address,
// because it starts pool creation in the background rather than blocking.
await Standard.CreatePool("Enemies/Orc", preloadCount: 10);   // maxSize defaults to 50 here

var enemy = Simple.Pool("Enemies/Orc");        // non-null now
// ... when done ...
Simple.Recycle("Enemies/Orc", enemy);          // NOT Object.Destroy — that bypasses the pool

// The Standard-tier equivalents:
Standard.Spawn("Enemies/Orc");
Standard.Despawn("Enemies/Orc", enemy);
```

`Simple.Destroy(instance)` routes through the loader's `ReleaseInstance` so
Addressables' own instance refcount balances; plain `Object.Destroy` leaves a
permanently retained bundle reference behind.

Note the default pool size differs by entry point: `Standard.CreatePool` defaults
`maxSize` to **50**, while `Assets.CreatePool` and `AddressablesFacade.CreatePoolAsync`
default to **100** (`AddressablePoolManager.DefaultMaxPoolSize`).

**Find suspects in the Dashboard**:
```
Window > Addressable Manager > Dashboard > Active Assets
Sort through the list for anything alive far longer than expected, or whose refcount
keeps climbing. AssetTrackerService.DetectPotentialLeaks(minutesThreshold) exposes the
same query in code — there is no automatic "memory leak detected" warning anywhere in
the package, so nothing will tell you unaided.
```

**Use the Memory Profiler**:
```
1. Window > Analysis > Memory Profiler
2. Take a snapshot after loading
3. Take a snapshot after releasing
4. Compare

The Dashboard's memory column is an ESTIMATE keyed by type name, not a measurement.
```

---

### Issue: Out of Memory Crashes

**Symptoms**:
- App crashes with an OOM error
- Memory graph shows a spike

**Causes**:
1. Loading too many assets at once
2. Not releasing unused assets
3. Texture/mesh size too large
4. No cache size limit

**Load in batches, and release as you go**:
```csharp
using AddressableManager.API;

// Loads every asset carrying the label, all at once:
var handles = await Standard.LoadByLabel<Texture2D>("textures");

// Loading a known set instead, in chunks you control.
// LoadBatch takes `params string[]`, so pass an array.
const int batchSize = 10;
for (int i = 0; i < addresses.Length; i += batchSize)
{
    string[] chunk = addresses.Skip(i).Take(batchSize).ToArray();
    Dictionary<string, IAssetHandle<Texture2D>> batch =
        await Standard.LoadBatch<Texture2D>(chunk);
    // ... process ...
    foreach (var handle in batch.Values) handle.Release();
}
```

`LoadBatch` loads sequentially and skips addresses that fail, so a missing entry does
not abort the batch.

**Clean up between scenes**:
```csharp
// There is no ReleaseByLabel. Release the handles you were given, or clear the scope.
Standard.ClearSessionCache();
// or, for a named scope:
ScopeManager.Instance.ClearScope("Level_Previous");

Resources.UnloadUnusedAssets();
```

**Put a cap on a scope's cache with tiered caching**:
```csharp
using AddressableManager.API;
using AddressableManager.Core;

// Tiering is OFF unless you pass a config. The config argument has no default and
// null throws ArgumentNullException; an invalid config throws ArgumentException.
var config = TieredCacheConfig.Aggressive;   // 50 MB. Also: Default (100 MB),
                                             // Lenient (200 MB), Disabled (unlimited)
var loader = Advanced.CreateLoader("Level", config);

// Or build one field by field:
var custom = Advanced.CreateCacheConfig(
    maxSizeBytes: 64L * 1024 * 1024,
    promoteToHotThreshold: 15.0f,
    evictionTriggerRatio: 0.9f,
    enableAutoTiering: true,
    enableAutoEviction: true);

Advanced.EvaluateTiers(loader);   // re-score entries now
Advanced.ForceEviction(loader);   // evict now
var stats = Advanced.GetCombinedCacheStats(loader);
```

`Advanced.GetTieredCacheStats<T>(loader)` returns an **all-zero struct** when tiering is
off — check `loader.TieringEnabled` before believing a zero.

**Thread-safety, if you go looking**: the standalone `TieredCache<T>` is documented as
**not thread-safe** and is for single (main) thread use. `ThreadSafeCacheManager<T>` is
the multi-thread equivalent, but even there `Set()` and `TryGet()` are main-thread only;
`Remove`, `Clear`, `Pin`, `Unpin` and `GetStatistics` work from any thread.

---

## Compilation Errors

### Error: "Type or namespace 'AddressableManager' could not be found"

**Cause**: Missing assembly reference, or the wrong namespace.

**Solution**:
```csharp
// The namespaces that actually exist:
using AddressableManager.API;         // Simple, Standard, Advanced
using AddressableManager.Facade;      // Assets, AddressablesFacade
using AddressableManager.Core;        // IAssetHandle<T>, LoadResult<T>, LoadErrorCode,
                                      // SmartAssetHandle<T>, TieredCacheConfig
using AddressableManager.Loaders;     // AssetLoader, ThreadSafeAssetLoader
using AddressableManager.Scopes;      // GlobalAssetScope, SceneAssetScope, ...
using AddressableManager.Managers;    // ScopeManager
using AddressableManager.Monitoring;  // AssetMonitorBridge, IAssetMonitor
using AddressableManager.Cdn;         // CdnManager, CdnResult<T>, CdnErrorCode
using AddressableManager.Configs;     // DebugSettings, AddressablePreloadConfig
```

There is no `AddressableManager.Runtime` namespace.

**Assembly Definition References** — the assemblies are named:
```
AddressableManager           (Runtime)
AddressableManager.Editor    (Editor)
```
Reference `AddressableManager` from runtime asmdefs and `AddressableManager.Editor`
from editor asmdefs. Editor-side types — filters, providers, `LayoutRuleData`, the CLI
— live in `AddressableManager.Editor.*` namespaces and are not reachable from runtime
code.

---

### Error: "Cannot access internal member" on `AssetLoader.IsCached`

**Cause**: `AssetLoader.IsCached` and `IsCached<T>` are `internal`. `InternalsVisibleTo`
is granted only to `AddressableManager.Tests.Editor`.

**Solution**: use the public route.
```csharp
bool any   = Simple.IsLoaded("texture");            // any type at this address
bool typed = Simple.IsLoaded<Texture2D>("texture"); // exact (address, Type) key
```

---

### Error: "SmartAssetHandle does not contain a definition for 'IsValid'"

**Cause**: `IsValid` is a **property**, not a method — on `SmartAssetHandle<T>` and on
`IAssetHandle<T>` alike.

**Solution**:
```csharp
if (handle.IsValid) { }     // correct
if (handle.IsValid()) { }   // does not compile

// SmartAssetHandle<T> also has an implicit bool conversion:
if (handle) { }             // same as handle != null && handle.IsValid
```

---

### Warning: obsolete members

These still compile in 4.x and are removed in 5.0.0:

| Obsolete | Use instead |
|---|---|
| `Simple.Release<T>(asset)` | `Simple.ReleaseAddress(address)` / `Simple.ClearAll()` |
| `Standard.LoadScene<T>` | `Standard.LoadIntoSceneScope<T>` — neither loads a Unity scene |
| `Standard.DownloadDependencies` | `CdnManager.DownloadAsync` |
| `Standard.GetDownloadSize` | `CdnManager.GetDownloadSizeAsync` |
| `Assets.GetDownloadSize` / `Assets.Download` | the `CdnManager` equivalents |
| `Advanced.CreateTieredLoader` | `Advanced.CreateLoader(scopeName, config)` |
| `Advanced.GetGlobalScope()` / `GetSessionScope()` | `Advanced.GetHybridGlobalScope()` / `GetHybridSessionScope()` |
| the six `TieredAssetLoader` overloads of `PinAsset` / `UnpinAsset` / `EvaluateTiers` / `ForceEviction` / `GetTieredCacheStats` / `GetCombinedCacheStats` | the `AssetLoader` overloads of the same names |

---

### Error: `Task<...>` cannot be converted to `UniTask<...>` (or vice versa)

**Cause**: part of the API changes its return type depending on whether
`com.cysharp.unitask` is installed (the `UNITASK_PRESENT` define).

**Dual-return (UniTask when installed, Task otherwise)**: everything on `Assets`,
`AddressablesFacade`, `AssetLoader`, `CdnManager`, `AddressablePoolManager`, plus
`Standard.LoadIntoSceneScope` and `Advanced.LoadFromBackgroundThread`.

**Always `Task`**: every member of `Simple`, and every other member of `Standard` and
`Advanced`.

**Solution**: `await` the call instead of assigning it to an explicitly typed variable,
or use `var`.

---

## Versioning Issues

### Issue: Git Version Provider Returns the Fallback

**Symptoms**:
- `GitCommitVersionProvider` produces `0.0.0-unknown` (its `Fallback Version` default)
- Console warnings about the git command

**Causes**:
1. Git not installed or not on PATH
2. `.git` folder not accessible
3. Not a git repository
4. The requested mode has nothing to report (`LatestTag` with no tags)

**Verify Git Installation**:
```bash
git --version
```

**Check Repository**:
```bash
git status
```

**Pick a mode that can succeed**. `GitVersionMode` values: `CommitHash`,
`CommitHashFull`, `LatestTag`, `TagOrHash`, `Describe`. The default is `TagOrHash`,
which falls back to a hash when there is no tag — `LatestTag` on a repository with no
tags cannot produce anything.

**Fallback to another provider**:
```
BuildNumberVersionProvider
DateVersionProvider
ConstantVersionProvider     (default "1.0.0")
```

---

### Issue: Build Number Version Is Not What You Expect

**Cause**: `BuildNumberVersionProvider` reads `PlayerSettings`, and which field it reads
depends on `Version Source`:

| Version Source | Reads |
|---|---|
| `BundleVersion` (default) | `PlayerSettings.bundleVersion` |
| `BuildNumber` | `PlayerSettings.Android.bundleVersionCode` or `PlayerSettings.iOS.buildNumber`, chosen by the provider's `Platform` field (default `RuntimePlatform.Android`) |
| `Combined` | both, joined as `bundle.buildNumber` |
| `Custom` | the provider's `Custom Version` field (default `"1.0.0"`) |

If reading `PlayerSettings` throws, the provider logs a warning and falls back to
`Custom Version` — so an unexpected `1.0.0` usually means the read failed, not that the
version is `1.0.0`.

**Solution**:
```
1. Edit > Project Settings > Player
2. Set "Version"
3. Set the iOS "Build" number / Android "Bundle Version Code"

Or programmatically:
PlayerSettings.bundleVersion = "1.2.3";
PlayerSettings.Android.bundleVersionCode = 456;
```

---

### Issue: Version expression rejected

`AddressableCLI.SetVersionExpression` exits 1 and prints the formats it accepts:
```
[1.0.0,2.0.0)   (1.0.0,2.0.0]   [1.0.0,2.0.0]   1.0.0
>=1.0.0   >1.0.0   <=2.0.0   <2.0.0
```

---

## CI/CD Issues

### Issue: CLI Commands Fail in the Build Pipeline

**Symptoms**:
- Unity exits with code 2
- `LayoutRuleData not found at path: ...`

**Causes**:
1. Incorrect asset path
2. Missing required argument
3. Scripts did not compile

**Every CLI method checks `EditorUtility.scriptCompilationFailed` first and exits 1.**

**Use project-relative asset paths**:
```bash
# BAD
-layoutRuleAssetPath "Rules/Main.asset"

# GOOD — the path AssetDatabase uses
-layoutRuleAssetPath "Assets/Rules/Main.asset"
```

**Arguments, per method** (`AddressableManager.Editor.CLI.AddressableCLI`):

| Method | Arguments |
|---|---|
| `ApplyRules` | `-layoutRuleAssetPath` (required), `-validateOnly`, `-warningAsError`, `-resultFilePath` |
| `ValidateLayoutRules` | `-layoutRuleAssetPath` (required), `-errorLogFilePath` |
| `SetVersionExpression` | `-layoutRuleAssetPath` (required), `-versionExpression`, `-excludeUnversioned` |
| `DetectConflicts` | `-reportFilePath` (default `conflicts.json`) |
| `ImportRules` | `-layoutRuleAssetPath` (required), `-importFilePath` (required), `-mergeMode` |

A `-key` with no value, or followed by another `-flag`, is parsed as `"true"`. Booleans
accept `true` (any case) or `1`.

**Verify Package in the Project**:
```json
// Packages/manifest.json
{
  "dependencies": {
    "com.game.addressables": "file:../Packages/com.game.addressables"
  }
}
```

**Check Exit Codes**:
```bash
#!/bin/bash
unity-editor -batchmode -quit -projectPath . \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset"

EXIT_CODE=$?
case $EXIT_CODE in
  0) echo "Success" ;;
  1) echo "Validation or apply failure"; exit 1 ;;
  2) echo "Missing argument, asset not found, or exception"; exit 2 ;;
esac
```

**Never trust the exit code alone.** Unity exits 0 when `-executeMethod` targets an
assembly that did not compile — the method simply never runs. Grep the log for
`error CS` as well.

**`ApplyRules -resultFilePath` writes JSON**:
```json
{"success":true,"totalAssetsProcessed":0,"addressesApplied":0,"labelsApplied":0,
 "versionsApplied":0,"warnings":[],"errors":[],"timestamp":"..."}
```

---

## Platform-Specific Issues

### Issue: Android Build Fails to Load Assets

**Cause**: Path separators or case sensitivity

**Solution**:
```csharp
// Use consistent casing — addresses are case-sensitive
"my_asset"  // not "My_Asset" or "MY_ASSET"

// Verify addresses in the built player
var handle = Addressables.LoadResourceLocationsAsync("my_asset");
var locations = await handle.Task;
Debug.Log($"Found {locations.Count} locations");
Addressables.Release(handle);
```

### Issue: `hostEnvironmentVariable` has no effect on device

**Cause**: The CDN host override reads a process environment variable. Mobile and
console have no process environment, so the override is skipped there — it works in the
Editor and in standalone players only.

**Solution**: use per-environment `baseUrl` values, or `CdnManager.SetEnvironment` at
runtime.

### Issue: iOS Asset Loading Slow

**Cause**: Too many small files or wrong compression

**Solution**:
```
1. Window > Asset Management > Addressables > Groups
2. Select the group > Content Packing & Loading > Advanced Options
3. Set Asset Bundle Compression (LZ4 for speed, LZMA for size)
4. Group small assets together
```

---

## Data Corruption

### Issue: Addressable Settings Corrupted

**Symptoms**:
- Groups missing after rule application
- Can't open the Addressable Groups window

**Restore from Version Control**:
```bash
git checkout AddressableAssetsData/
```
then rebuild: **Window > Asset Management > Addressables > Groups > Build > New Build >
Default Build Script**.

**Regenerate Settings**:
```
1. Delete the AddressableAssetsData folder
2. Window > Asset Management > Addressables > Groups
3. Click "Create Addressables Settings"
4. Reapply rules
```

**Groups missing a schema** — a group created outside the normal path can end up
without a `BundledAssetGroupSchema`, which breaks builds in confusing ways:
```
Tools > Addressable Manager > Repair Groups Missing Schemas
```

**Backup Before Applying**:
```
Before major rule changes:
1. Commit the current state to version control
2. Or copy the AddressableAssetsData folder
3. Apply rules
4. If there are problems, restore the backup
```

---

## Getting Help

If your issue isn't covered here:

1. **Check the other documentation**:
   - [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md)
   - [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md)
   - [CDN_USAGE_GUIDE.md](CDN_USAGE_GUIDE.md)
   - `EDITOR_TOOLS_GUIDE.md` and `MONITORING_GUIDE.md` at the package root

2. **Enable Verbose Logging**:
   ```
   LayoutRuleData > Verbose Logging  — for rule application
   DebugSettings  > Log Level = All  — the only DebugSettings field the runtime reads
                                       (via DebugSettings.IsVerbose, which is always
                                       false in a player build)
   ```

3. **Use the built-in diagnostics**:
   ```
   Window > Addressable Manager > Dashboard
   Performance tab > Export Report (CSV)
   Scopes tab for per-scope asset counts
   ```

4. **Report Issues**: include Unity version, package version, error logs, and
   reproduction steps.

---

**Package version**: 4.1.0-pre.9

---

## CDN Content Delivery

One entry per `CdnErrorCode`. Every CDN operation returns a `CdnResult<T>`; on failure
`result.Error.Code` is one of these, and `result.Error.Hint` carries the same advice in
one line.

Before anything else: **`CdnManager.InitializeAsync` must be the first Addressables call
in your boot sequence.** Addressables initialises implicitly on its first load, and the
CDN hooks only apply to content resolved after they are installed. A stray
`LoadAssetAsync`, or an `AssetReference` on an object in your first scene, is enough to
lose them. Initialisation fails loudly with this cause rather than half-applying.

> **The catalog-update flow is unproven.** `CheckForUpdateAsync` and `ApplyUpdateAsync`
> are built on `Addressables.CheckForCatalogUpdates`, which returned an empty list on
> every call in the Addressables versions this package was written against — the whole
> update path was dead code. The package now pins 2.9.1, where the call works, but the
> flow has not been exercised end to end against a real CDN. Test it yourself before a
> release depends on it.

### Quick triage

| Symptom | Likely code | Start here |
|---|---|---|
| First launch hangs on a loading screen, no network | `NoContentAvailableOffline` | [below](#nocontentavailableoffline) |
| Works on Wi-Fi, nothing happens on mobile data | `MeteredNetworkBlocked` | [below](#meterednetworkblocked) |
| Boots fine for you, 404s for shipped players | `CatalogNotFound` | [below](#catalognotfound) |
| "A catalog update is already being applied" | `Unknown` | [below](#unknown) |
| Update applies but the changed asset is still old | not an error — see [Static content](#the-update-applied-but-my-change-is-missing) | |
| Game size grows with every patch | not an error — see [Disk](#the-game-keeps-growing-after-every-update) | |

---

### Offline

**Symptom.** `CheckForUpdateAsync` returns success with `WasOfflineFallback == true`, or an
operation fails with this code mid-flight.

**Cause.** No usable network. Note that this is not necessarily an error state: with a warm
cache the game is fully playable and simply cannot check for new content.

**Fix.** Keep playing on cached content and retry later. Do not block the player. The one
thing worth doing is distinguishing this from "checked, nothing new" — `WasOfflineFallback`
exists for exactly that, and treating them the same is how a client ends up stuck on old
content after one bad boot.

---

### NoContentAvailableOffline

**Symptom.** `InitializeAsync` fails on a fresh install. Nothing loads at all.

**Cause.** First launch with no cached catalog and no network. There is genuinely nothing to
play — this is the one case that justifies a blocking screen.

**Fix.** Show a setup screen that retries when connectivity returns. Do not show a generic
error: the player has not done anything wrong and the game is not broken.

**If it happens with a network available**, the network came up after the check but before
the request, or reachability is lying — a captive portal reports as connected. Retry once
before showing the screen.

---

### MeteredNetworkBlocked

**Symptom.** Downloads work on Wi-Fi and refuse to start on mobile data.

**Cause.** `DownloadPolicy.RequireUnmeteredNetwork` is on and the device is on carrier data.
The layer refused rather than spending the player's data without asking.

**This is opt-in.** `requireUnmeteredNetwork` defaults to **`false`**, so out of the box
downloads proceed on cellular and you will never see this code. If you wanted the guard and
did not get it, tick the field on the `CdnSettings` asset.

**Fix.** Prompt, then retry with the override on the request — consent belongs on the
request rather than in settings because it is per download, not permanent:
```csharp
var request = DownloadRequest.For("chapter-2", allowMeteredOverride: true);
var result  = await CdnManager.DownloadAsync(request);
```

**Caveat worth knowing.** Unity only reports "carrier data network", so a metered Wi-Fi
hotspot reads as unmetered and a corporate APN reads as metered. This gate is a good default,
not a guarantee.

---

### CatalogNotFound

**Symptom.** Boots fine in the Editor and for you, 404s for shipped players.

**Cause.** No catalog at the URL the player is polling. Almost always one of:

1. **App version mismatch.** The catalog folder is named for `PlayerSettings.bundleVersion`
   at build time. A player on 1.2.0 polls `catalog/1.2.0/…`; if content was published under
   1.3.0, they get a permanent 404. This is the failure `ContentStateManager.Validate` blocks
   at build time — if you see it in production, a build bypassed that gate.
2. **Nothing was published for this platform.** Check the platform folder name: the build
   publishes under the `BuildTarget` name (`StandaloneWindows64`), which is *not* what
   Addressables' own `PlatformMappingService` returns (`Windows`).
3. **Wrong environment.** Check `CdnManager.CurrentBaseUrl` against where you uploaded.

**Fix.** Not retryable. Republish for the version players actually have, or ship a player
build whose version matches what is on the CDN.

**Diagnose it before shipping** with `CdnBuildCLI.VerifyOutput`, which fails when the
manifest's app version does not match `PlayerSettings.bundleVersion`, and again when the
catalog file is not named for that version.

---

### CatalogParseFailed

**Symptom.** The catalog downloads and then initialisation fails.

**Cause.** Truncated or corrupt catalog — usually a partial upload, occasionally a proxy that
rewrote the body.

**Fix.** Clear the catalog cache and retry once. If it recurs, the object on the CDN is
damaged: re-upload it and purge the edge cache. Verify with a plain `curl` that the byte
count matches `build-manifest.json`.

---

### CatalogVersionIncompatible

**Symptom.** The catalog loads but Addressables rejects it.

**Cause.** Built by a different Addressables version, or for a different player version, than
the running build expects.

**Fix.** Force a store update. Not retryable, and not fixable from the server side: the client
binary cannot read that catalog format.

---

### BundleNotFound

**Symptom.** The game boots, the catalog loads, and loading a specific asset 404s.

**Cause.** The catalog references a bundle that is not on the CDN. This is a deploy ordering
mistake: **bundles must be uploaded before the catalog.** Uploading the catalog first creates
a window where clients read a catalog naming bundles that do not exist yet.

**Fix.** Upload the missing bundles, then purge the catalog at the edge. The repository's
`ci/upload-bundles.sh` runs before `ci/upload-catalog.sh` for this reason — if you deploy by
hand, keep that order.

**Verify** with `CdnBuildCLI.VerifyOutput`, which checks that every bundle in the manifest
exists in the output and matches its recorded hash, and with `CatalogInspectCLI.Inspect`,
which checks the built catalog against the bundle folder.

---

### BundleCrcMismatch

**Symptom.** A download completes and then fails verification, sometimes repeatedly on one
device.

**Cause.** The bytes could not be turned into a bundle: an interrupted write, failing
storage, a truncated response cached by an intermediary, or a corrupt object on the CDN.

`CdnErrorMapper` maps `UnityWebRequest.Result.DataProcessingError` to this code
**regardless of HTTP status**, checked before the status branch. That matters because a
bundle that transfers cleanly and then fails CRC validation inside
`DownloadHandlerAssetBundle` reports `DataProcessingError` with **status 200** — a complete
HTTP response really did arrive. Testing it only under status 0 sent the single most
important corruption case through to `Unknown`, which is classified as non-retryable and
which no auto-repair path matches.

**Fix.** Handled automatically on a download: `DownloadService` evicts the cached dependency
and retries once, and `DownloadReport.RepairedCorruptBundle` records that it happened. If it
fails again, the bytes on the CDN are wrong rather than the local copy — compare the object's
SHA256 against `build-manifest.json`.

---

### ServerError

**Symptom.** Intermittent failures, often several clients at once.

**Cause.** 5xx from the origin or edge, or 408/429.

**Fix.** Handled automatically by `RetryPolicy` with exponential backoff and jitter — **on
downloads only**. `InitializeAsync`, `CheckForUpdateAsync` and `ApplyUpdateAsync` go through
`CatalogService`, which has no retry policy, so a transient 503 on a catalog request reaches
you on the first attempt; retry it yourself, gated on `IsRetryable`. If a download persists
past the retry budget, tell the player the servers are busy — not that something is wrong
with their device. 429 specifically means retrying faster makes it worse.

---

### Timeout

**Symptom.** Requests hang, then fail with HTTP status 0.

**Cause.** No response arrived: DNS failure, connection refused, or no route. Status 0 means
"never got a response", not "success".

**`DownloadPolicy.TimeoutSeconds` (default 30) does not apply to bundle downloads.**
`UnityWebRequest.timeout` is a wall-clock cap on the whole transfer, whereas Addressables'
bundle timeout is an *idle* timer that resets on every byte received. Applying the wall-clock
value to bundles aborted any bundle taking longer than 30 seconds — a large bundle on a slow
phone — mid-download at full speed, and because Unity's cache commits only completed
downloads, every retry restarted from zero. The hook therefore sets `request.timeout` only
for non-bundle requests (catalog, hash and text files). Raising `TimeoutSeconds` will not
help a slow bundle, because it was never the limit.

**Fix.** Retried automatically on downloads. If it happens on catalog requests, that is where
`TimeoutSeconds` does apply.

---

### Unauthorized

**Symptom.** 401 or 403 on every request, or after a period of working.

**Cause.** Missing, expired or rejected auth token; or a signed-URL policy that has lapsed;
or bucket permissions.

**Fix.** Set `CdnManager.AuthTokenProvider` **before** `InitializeAsync`. It is called per
request, so a refreshed token is picked up without reinstalling anything. If a fresh token is
still rejected, the problem is the bucket policy rather than the token.

---

### InsufficientDiskSpace

**Symptom.** A download refuses to start.

**Cause.** Free space is below the download size plus `DownloadRequest.MinFreeDiskBytes`
(64 MB by default). The headroom is deliberate — filling the volume breaks the OS, not just
the game.

**Fix.** The error message states how much to free. Consider calling
`CdnManager.Cache.CleanObsoleteAsync()` first: on a long-lived install that often recovers
more than the player would by deleting photos.

---

### Cancelled

Not a failure. The caller cancelled. `CdnResult.IsCancelled` distinguishes it so cancellation
does not surface as an error dialog or a telemetry event.

Bundles that had already finished downloading stay in the cache, so restarting does not
re-fetch them — but **the bundle that was mid-transfer restarts from zero**, because Unity's
bundle cache commits only completed downloads. Do not clear the cache on cancel.

---

### Unknown

**Symptom.** Anything not covered above, plus a few deliberate refusals from this package:
unusable `CdnSettings`, "not initialised", a cache operation Unity declined, and
**"A catalog update is already being applied"**.

That last one is a guard, not a bug: `ApplyUpdateAsync` refuses a second concurrent call
rather than letting two applies race Addressables' shared locator state. Wait for the
in-flight apply to finish, then call `CheckForUpdateAsync` again — the `CatalogUpdateInfo`
you are holding describes the pre-update state and must not be applied on top of the result.

**Cause (for the rest).** No HTTP response was available to classify the failure, or the
status was a 4xx that is not 401/403/404/408/429. The exception is attached to
`error.Exception`.

**Fix.** Read `Message`, `Hint` and the exception. If you find a case that should have its own
code, it belongs in `CdnErrorMapper` — classification there is driven by
`UnityWebRequestResult.ResponseCode` rather than by matching words in the message, so adding
a case is a small change.

---

## Problems that are not error codes

### The update applied but my change is missing

**Cause.** The changed asset is in a group marked `StaticContent`, which means "not rebuilt by
a content update". Addressables does not fail on this — it logs a warning and reverts the
entry to its previous bundle, so the patch builds, uploads and simply does not contain your
change.

**Fix.** Open **Window → Addressable Manager → CDN Manager → Update Preview**, select the
listed entries, and press **Prepare content update**. It moves them into a fresh non-static
group so the next update rebuilds them. Commit the resulting group change.

`CdnBuildCLI.BuildContentUpdate` refuses to build in this state rather than producing a patch
with a hole in it — and it also refuses when the restriction check cannot be evaluated at all,
because unprovable is not the same as safe. **A new player build is not required.**

### The game keeps growing after every update

**Cause.** Superseded bundles are not removed automatically by Addressables.

**Fix.** `CdnManager.ApplyUpdateAsync` calls `CleanObsoleteAsync` for you after a successful
apply (a failure there is logged as a warning and does not fail the update). If you apply
catalogs through Addressables directly, call `CdnManager.Cache.CleanObsoleteAsync()`
afterwards. Check with `CdnManager.GetCacheStats()` — and note that `CacheStats.IsValid ==
false` means the platform did not report, which is not the same as an empty cache.

### It works in the Editor and 404s in a build

**Cause.** Almost always Play Mode script: **Use Asset Database** and **Simulate Groups** never
touch the network, so the CDN path is not exercised at all. The Editor was reading your
project folder.

**Fix.** Switch to **Use Existing Build** in the Addressables Groups window, and serve the
output from the CDN Manager window's **Server** tab. This is also why the integration tests
assert against the local server's request log rather than the client's return value — a green
result under Fast Mode proves nothing.

### Everything 404s and the URL contains the wrong platform folder

**Cause.** The `{platform}` token in a base URL was resolved with Unity's own
`PlatformMappingService.GetPlatformPathSubFolder()`, which returns `Windows` where the build
published under `StandaloneWindows64`.

**Fix.** Use `HostRewriter.ResolvePlatformToken()`, which reproduces the `BuildTarget` name —
or better, drop `{platform}` from the base URL entirely. Addressables bakes the platform
segment into the catalog URL at build time, so environments that differ only by host need no
token at all. Note that 64-bit is assumed: if you ship 32-bit Windows, do not use
`{platform}`.

### `InitializeAsync` fails before it reaches the network

**Cause.** `CdnSettings.Validate()` found a problem and initialisation refused to continue on
a half-valid configuration. The most common one is a **trailing `/` on `baseUrl`**, which is a
validation error rather than something the package trims. Duplicate environment ids and a
`defaultEnvironmentId` that is not in the list are the other two.

**Fix.** The failure message lists every problem at once. Fix them all and re-run.
