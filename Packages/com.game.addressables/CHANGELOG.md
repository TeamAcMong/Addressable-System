# Changelog

All notable changes to this package will be documented in this file.
## [4.1.0-pre.7] - 2026-08-17 - Tiering becomes a setting, not a second loader

`pre.6` shipped the reference-counting fixes. This one finishes the review: the loader fork is
retired, the pooling layer gets the twenty-odd defects that never made it into the original work
order, and the three API stubs `pre.6` shipped with are gone.

**The headline is a deprecation, and it is source-compatible.** `TieredAssetLoader` still compiles
and still runs; it is now a forwarder. See below for the one migration that is not a rename.

### Fixed — the three stubs `pre.6` shipped

- **`Simple.Release<T>` was a single `Debug.Log`.** A caller believed they had released something.
  The address is not recoverable from an asset instance — `AssetLoader._assetCache` is keyed
  `(address, Type)` with no reverse map, and Addressables offers no reverse lookup — so the method
  is `[Obsolete]` rather than quietly reimplemented, and **`Simple.ReleaseAddress(string)`** is added
  as the one that actually works. It is deliberately not a `Release(string)` overload: a non-generic
  `Release(string)` would out-rank `Release<T>` in overload resolution and silently re-bind existing
  `Simple.Release(someString)` call sites.
- **`Standard.ClearCache(scopeName)` was a log-then-return stub.** Implemented, now that scopes
  register with `ScopeManager`. An unregistered scope id logs an error naming the id *and*
  enumerating the live scopes, instead of a message that read like success.
- **`Standard.LoadScene<T>` never loaded a scene.** It loaded an asset and bound it to the *active*
  scene rather than the caller's — verified against current code, not taken from the review. There
  is no scene-loading capability anywhere in the package. It is `[Obsolete]`, pointing at the new
  **`Standard.LoadIntoSceneScope<T>(address)`** and **`(address, Scene)`**, which say what they do
  and let the caller name the scene. The parameterless form now passes
  `SceneManager.GetActiveScene()` explicitly, so that choice is visible at the call site.

### Fixed — pooling, the part the review never wrote down

The handoff's reviewer verified 66 items but the summary was truncated after item 43; the pooling
group was cut mid-sentence with a note that at least three or four items remained. Re-derived from
the code, that turned out to be **twenty-two**. The ones that bite in a real game:

- **A pooled instance destroyed by a scene load poisoned its pool permanently.** `Despawn` bailed on
  the destroyed object before untracking it, so `ClearPool` refused forever, the template handle was
  never released, and `DynamicPool` — reading an `activeCount` that could only climb — grew to
  `MaxSize` and never shrank again. There is now a sweep, driven by `sceneUnloaded`, a maintenance
  pump, and `ClearPool` itself.
- **`preloadCount` meant three different things** across the two create methods and the two
  adapters, and the dynamic path fired the caller's `onGet` while the other did not. One code path
  now, one meaning.
- **Auto grow/shrink only worked under continuous churn.** Shrink was evaluated on `Release`, so a
  wave that spawned 40, despawned 40 and then went quiet kept all 40 resident for the session. A
  pump now evaluates on a clock.
- **`Get()` activated the instance before positioning it**, so `OnEnable` ran at the *previous*
  user's position — a `playOnAwake` particle system emitted a frame at the last despawn site, and
  anything reading `transform.position` in `OnEnable` read stale data. Pose is applied first now.
- **Preload logs reported the requested count, not the achieved one.** `CreatePoolAsync("X",
  preloadCount: 50, maxSize: 10)` logged `"Preloaded 50/50"` with ten pooled and forty destroyed.
  `IMeasuredResizablePool<T>` returns what actually happened, and every log reports that.
- Use-after-`Dispose` was a thrown `ObjectDisposedException` on one pool type and a `null` on
  another, through the same `IObjectPool<T>`. One contract now: log and return neutral, never throw.
- `ClearPool` had no way to report *why* it did nothing, so shutdown code looping over addresses
  could not tell that every call had refused. `TryClearPool` returns a named outcome.
- Also: per-address `[Pools]` holders leaked on clear; an auto-create landing after a teardown
  resurrected a pool; a dead caller-supplied `poolRoot` was dereferenced unguarded; `MaxSize = 0` and
  `ShrinkFactor = 0` were accepted and then quietly meant something else; `ResizePool` refused pools
  it could in fact resize.

- **`Assets.Spawn`, `Assets.Despawn` and `Assets.CreatePool` threw `NullReferenceException` during
  shutdown.** `AddressablesFacade.Initialize()` legitimately leaves the pool manager null when the
  global scope is already gone, and six Facade members went through that field unguarded.

### Fixed — threading

- **`UnityMainThreadDispatcher.EnqueueAndWait` could hard-hang the main thread** — no timeout, no
  log — by waiting on a queue only its own `Update()` could drain. The thread id now latches at
  `SubsystemRegistration` and the pump is created at `BeforeSceneLoad`, which removes the root cause;
  the wait is bounded and guarded besides.
- **`ThreadSafeCacheManager` advertised "safe from any thread" while every operation reached Unity
  API.** An off-thread `Set()` retained a reference and *then* threw on `Time.realtimeSinceStartup`,
  leaking it from a stack that named neither the class nor the thread. See *Changed* for the
  contract that replaced the claim. `_disposed` is now `volatile` and re-checked inside the lock —
  the window `Dispose`'s own comment claimed to have closed was still open.

### Fixed — cache

- **`Pin()` on a key that is not cached is no longer a silent no-op**, which mattered because the
  documentation taught pin-before-load. A pin now waits for the key and is applied when it arrives.
- **An insert larger than the budget could not trigger the eviction that would make room for it** —
  `PerformEviction` ran from inside `Set()` and could not see the entry being inserted.

### Deprecated — `TieredAssetLoader` is now a configuration of `AssetLoader`

**Tiering is a setting on the one loader, not a second loader class.** `TieredAssetLoader` was a
fork of `AssetLoader` that existed only to add tiered caching, and it silently lacked everything
`AssetLoader` had: single-flight join, the post-await thread guard, label loads, the `*Safe`/
`LoadResult` variants, `InstantiateAsync`/`ReleaseInstance`, and `ReleaseAsset`. It is `[Obsolete]`
(a warning, not an error), still works, and is removed in 5.0.0. It is now a thin forwarder onto an
`AssetLoader` built with the same config.

```csharp
// Before
var loader = new TieredAssetLoader("Battle", TieredCacheConfig.Aggressive);
var tex    = await loader.LoadAssetAsync<Texture2D>("Boss/Diffuse");
loader.PinAsset<Texture2D>("Boss/Diffuse");
var stats  = loader.GetCombinedStats();
loader.Dispose();

// After
var loader = new AssetLoader("Battle", TieredCacheConfig.Aggressive);
var tex    = await loader.LoadAssetAsync<Texture2D>("Boss/Diffuse");
loader.PinAsset<Texture2D>("Boss/Diffuse");
var stats  = loader.GetTieredCacheStats();
loader.Dispose();
```

The only two renames are `GetCombinedStats()` → `GetTieredCacheStats()` and
`GetCacheStats<T>()` → `GetTieredCacheStats<T>()`, both forced by an `AssetLoader.GetCacheStats()`
that already existed with a different return type. Everything else is a type-name substitution.

Factory form: `Advanced.CreateTieredLoader(name, cfg)` → `Advanced.CreateLoader(name, cfg)`. The
seven `Advanced.*` members that take a `TieredAssetLoader` are `[Obsolete]` too, each with a
non-obsolete `AssetLoader` sibling of the same name.

**One migration is not a mechanical rename.** `Advanced.CreateTieredLoader("X")` with no config
meant *tiering on, with `TieredCacheConfig.Default`*. `Advanced.CreateLoader("X")` is the
pre-existing overload and means *tiering off*. Write `Advanced.CreateLoader("X",
TieredCacheConfig.Default)` to keep the old behaviour — otherwise you get a loader that never
evicts, silently. Tiering is **off** by default on `AssetLoader`, so nothing that exists today
changes behaviour; only the two-argument constructor turns it on.

**What the move fixes, beyond removing a fork:**

- **A tiered loader's cache could never be invalidated after a CDN catalog update.** It was not an
  `AssetLoader`, registered with a different registry, and had no `InvalidateAddresses` — so
  `AssetLoaderRegistry.InvalidateAll`, which `CatalogService` calls after `UpdateCatalogs`, could
  not reach it by any path. Its entries went on serving handles resolved against the previous
  catalog for the rest of the session. Because the shim's inner loader is a plain `AssetLoader`, it
  registers in the registry the invalidation walk uses, and **every existing
  `new TieredAssetLoader(...)` call site is now reached after a catalog update without its author
  changing a line.**
- **Eviction now ranks candidates of every `Type` together in one pass.** It used to keep one cache
  per `Type`, where a cache could only evict its own entries and relied on a round-robin over
  siblings to converge on the shared ceiling.
- `TieredAssetLoaderRegistry` (added earlier today, never in a release) is deleted; the periodic
  tier/eviction pump and the `Application.lowMemory` sweep now walk `AssetLoaderRegistry`. An
  untiered loader's `EvaluateTiers()`/`ForceEviction()` are no-ops, so pumping every loader is
  correct and costs one virtual call each.

`TieredCache<T>`, `ThreadSafeCacheManager<T>`, `TieredCacheConfig`, `CacheEntry<T>` and `CacheTier`
are **not** deprecated. `TieredCache<T>` remains a supported standalone cache via
`Advanced.CreateTieredCache<T>`; it is simply no longer what a loader uses internally.

### Changed

- **`TieredAssetLoader.LoadAssetAsync<T>` keeps returning `Task<IAssetHandle<T>>`** in every
  project, UniTask installed or not. No signature on this class changed. An earlier draft of this
  release made it return `UniTask<IAssetHandle<T>>` under `UNITASK_PRESENT` to match the rest of the
  package; that was reverted before shipping, because a warning-level `[Obsolete]` promises source
  compatibility until 5.0.0 and a return type that changes with an unrelated package's presence
  breaks `Task<T> t = loader.LoadAssetAsync<Sprite>(a);` and every `Task.WhenAll` call site with a
  hard compile error — on the same line as the deprecation warning that was supposed to be the
  migration signal. `Standard.LoadScene<T>` was already held at `Task` for exactly this reason.
  Under UniTask the shim pays one `AsTask()` conversion per load; that cost is the reason to
  migrate, and the replacement (`Advanced.CreateLoader(name, cfg)` → `AssetLoader`) has a genuinely
  dual `LoadAssetAsync<T>` with no conversion at all.

- `TieredAssetLoader.GetCacheStats<T>()` no longer returns `null` before the first load of `T`.
  There is no per-type cache object to test for existence any more; through this class it now always
  returns a struct, all-zero when nothing of `T` is cached. Test `TotalEntries == 0` instead of a
  null check. Its `TotalAccesses`/`CacheHits`/`HitRate`/`TotalEvictions`/`TotalPromotions`/
  `TotalDemotions` are now loader-wide rather than per-`Type`; the entry counts and byte figures
  remain exact per-`Type`. Keeping the counters per-`Type` would have meant a second book, which is
  the bug class this whole change removes.

- **`ThreadSafeCacheManager<T>.Set` and `.TryGet` now throw `InvalidOperationException` off the main
  thread.** Both reach `Time.realtimeSinceStartup` and `IAssetHandle.IsValid`, neither callable off
  it at any price, so an off-thread call already failed — deeper in, after `TryRetain()` had taken a
  reference that then leaked, from a stack naming neither this class nor the calling thread. The
  class doc now states a two-group thread contract: `Set`/`TryGet` are main-thread-only; `Remove`,
  `Clear`, `Dispose`, `Pin`/`Unpin`, `ContainsKey`, `Count`, `CurrentSize`, `GetStatistics` and
  `ResetStatistics` are callable from any thread and marshal their handle releases to the main
  thread. If you were calling `Set`/`TryGet` from a worker, wrap it in
  `UnityMainThreadDispatcher.Enqueue`.

- **The any-thread group no longer throws `ObjectDisposedException` after `Dispose()`** — each
  returns its neutral value (`Remove`/`TryPin`/`TryUnpin` → `false`, `GetStatistics` → an all-zero
  snapshot, `Clear` → no-op). Publishing an "any thread" contract invites a worker to hold the
  object across a `Dispose` it cannot observe, and the lock it would have taken is no longer
  disposed for the same reason.

- **`CachePinOutcome` and `PinWithOutcome(key)`** are added to `TieredCache<T>` and
  `ThreadSafeCacheManager<T>`. `TryPin` returns `false` both for a pin that was *deferred* (it will
  be applied when the key loads) and for one that was *refused* (the pending-pin list is full; it
  never will be) — opposite meanings behind one `bool`, distinguishable before only in the log.
  `TryPin` keeps its signature; prefer `PinWithOutcome` when the difference matters.

### Known limitations

- **`unity` stays at `2023.1`.** The runtime assembly is now verified clean against 2022.3.62f3 in
  both the `Task` and `UniTask` configurations — but the *editor* assembly cannot be verified by
  `Tools/check-min-unity-api.sh`, which cannot reproduce Unity's editor reference set. The floor
  will drop to 2022.3 once the editor half has been compiled inside a real 2022.3 project rather
  than asserted. `pre.4` shipped broken because a version claim ran ahead of its evidence.
- `TieredAssetLoader` is no longer a fork — see the deprecation section above. It forwards to an
  `AssetLoader`, so it now has single-flight joins and catalog invalidation. `ReleaseAsset`, label
  loads, the `LoadResult` variants and the dual `UniTask` signature are still not exposed *through
  this wrapper* — its own signatures are frozen until 5.0.0; call them on an `AssetLoader` directly.
- **`TieredCache<T>`'s constructor reads `Time.realtimeSinceStartup`**, which throws off the main
  thread, so a standalone cache cannot be constructed from a background thread. The merged tiering
  inside `AssetLoader` avoids this by construction (it latches the clock on first use, not at
  construction); the standalone type was left as-is.

### Verification

Both assemblies compile. Runtime verified against Unity 2022.3.62f3 in both the `Task` and `UniTask`
configurations — the second is the one that shipped broken as `pre.4` — with 0 real errors in each.
142/142 EditMode tests pass (64 before this release; the five new fixtures add 78).

Not verified: `Profiler.GetRuntimeMemorySizeLong` in a non-development build (it can return 0 on some
platforms, which would leave eviction permanently idle), and the two pooling paths that need a real
`LoadSceneMode.Single` transition to reproduce.

## [4.1.0-pre.6] - 2026-08-17 - Reference counting, one ownership rule, and pools that keep their own books

The largest correctness release in the 4.1 line. An external review verified 66 defects across the
caches, loaders, API surface, scopes and pooling; this ships the fixes for most of them. Nearly
every one is a lifetime bug — a reference taken and never given back, or given back twice, or a
cache serving an asset it did not own.

**Read this before upgrading if you use `TieredCache<T>`, `ThreadSafeCacheManager<T>` or
`AddressablePoolManager` directly.** Some methods now do what their names always claimed, which is
a behaviour change even though no signature changed.

### Fixed — reference counting

- **A cache stored handles it did not own a reference to.** `Set()` kept the caller's handle without
  retaining it, and eviction later released it — so eviction destroyed an asset the caller was still
  holding, and the caller's own `Release()` then decremented a count that no longer belonged to it.
  Caches now take their own reference via `TryRetain()`, independent of the caller's, and `TryGet()`
  hands back a retained copy. **You must still release what `TryGet()` gives you.**

- `Clear()`, `Dispose()` and `Remove()` dropped entries without releasing anything — the bundles
  stayed loaded for the rest of the session with nothing left that could free them.

- `Set()` on a key that already held a live entry silently discarded the incoming handle, orphaning
  one reference per call. It now releases the losing duplicate and documents that the handle you
  passed may be invalid when the call returns.

- Two `if (IsValid) Retain()` pairs on the cache-hit path. `Retain()` throws on a dead handle;
  checking `IsValid` first is not the same as it not racing. Both use `TryRetain()` now.

### Fixed — one ownership rule for loaders

A loader belongs to exactly one owner: the object whose lifetime it copies. The owner creates it,
the owner disposes it, and only from its own teardown. Everyone else borrows. The full reasoning is
in `Documentation/LIFETIME_DESIGN.md`.

- **`Simple.Load`, `TryLoad`, `Preload`, `PreloadBatch` and `Standard.PreloadAsync` orphaned one
  reference per call.** The most common entry points in the package leaked on every use.
- **`Simple.Destroy` called `Object.Destroy`**, leaking the Addressables instance refcount that
  only `ReleaseInstance` can return.
- **`GlobalAssetScope.Dispose()` left its field non-null**, so `Loader` returned null for the rest
  of the process. The scope is now owned by the Facade.
- `AddressablesFacade.OnDestroy` ran `EndSession()` and disposed the pool manager outside the
  `_instance` guard, so a duplicate Facade tore down the live one's state.
- `HybridScope` held statics across domain reload and disposed without clearing them. Retired.

### Fixed — catalog updates now reach every cache

After a catalog update, a cached handle still wrapped an operation resolved against the *previous*
catalog. It looked healthy — valid, succeeded — so it was served for the rest of the session.

Loaders are constructed in six places and only one was enumerable, so invalidation reached one
population out of six, and the one it reached was not the default path. `AssetLoaderRegistry` (weak
references, pruned as it walks) now reaches all of them, and `CatalogService` invalidates through it
after `UpdateCatalogs`.

Also: a failed bundle-cache clean no longer reports the whole update as failed. On WebGL, where that
clean always fails, every successful update was being reported as a failure.

### Fixed — pooling

- **`Spawn()` blocked the main thread on an Addressables load** when auto-create hit a missing pool.
  It now starts (or joins) a background create and returns `null` immediately; `SpawnAsync()` is the
  path that waits. The two `null`s never mean the same thing and each is logged where it happens.
- **`preloadCount` created exactly one instance regardless of the number asked for**, then logged the
  number requested as though it had succeeded.
- **`DynamicPool` corrupted the inner pool's accounting in both directions**, driving `activeCount`
  negative and exposing it through the public API.
- Pools now live under a `DontDestroyOnLoad` root, and both adapters walk past instances a scene load
  destroyed underneath them. (`obj == null` on a generic `T` binds to `object.Equals`, not Unity's
  overridden equality, so a destroyed `GameObject` in a free list reads as alive.)
- `Despawn` verifies which pool actually owns an instance. Wrong-pool and double-despawn used to
  produce three different behaviours depending on the adapter.
- `ClearPool` refuses while instances are still borrowed; real teardown destroys them first, then
  clears the pools, then releases templates.

### Fixed — tiered cache and progress

- **Teardown released nothing.** `Dispose()` now hard-releases, matching `AssetLoader.ClearCache`'s
  documented memory-pressure semantics; eviction still uses a plain decrement.
- **The memory budget was applied per `Type`**, so the real ceiling was `MaxCacheSizeBytes` times the
  number of distinct types cached. One shared budget now.
- **Nothing ever ran eviction or tier evaluation.** The Facade pumps every live tiered loader on a
  5-second interval, plus an `Application.lowMemory` sweep.
- **`EstimateAssetSize` returned invented numbers.** A `GameObject` is now measured by walking its
  meshes, materials and the textures those materials reference — the memory a prefab actually pulls
  in, not the size of the native shell.
- Cache dispatch no longer goes through reflection (which IL2CPP can strip, turning the pump into a
  silent no-op), and the per-type key is a struct rather than `$"{address}_{typeof(T).Name}"`.
- **`ProgressiveAssetLoader` opened its own Addressables operations and threw the handles away.**
  It now delegates to `AssetLoader`, so those handles are cached, single-flighted, and reachable by
  `ClearCache` / `Dispose` / catalog invalidation. `LoadMultipleWithProgressAsync` keeps its `bool`
  signature; the honest result lives on an additive `*Safe` overload returning `LoadResult<T>`.

### Fixed — Editor

- A layout rule with every filter disabled matched **every asset in the project**, and the scan it
  ran had no upper bound.
- `PathFilter` had no glob mode while the documentation taught `**` patterns in 32 places.
- A shipped rule template failed its own validation, which was masking an unconditional `SaveAssets`.
- **`AddressableCLI` reported success on a broken build.** All four entry points now gate on
  `EditorUtility.scriptCompilationFailed`. Unity exits 0 from `-executeMethod` even when the
  assembly never compiled.
- The `Samples~/RuleAutomation` sample shipped a live hazard rather than an example.

### Known limitations

- **`unity` stays at `2023.1`.** The runtime assembly is now verified clean against 2022.3.62f3 in
  both the `Task` and `UniTask` configurations — but the *editor* assembly cannot be verified by
  `Tools/check-min-unity-api.sh`, which cannot reproduce Unity's editor reference set. The floor
  will drop to 2022.3 once the editor half has been compiled inside a real 2022.3 project rather
  than asserted. `pre.4` shipped broken because a version claim ran ahead of its evidence.
- `TieredAssetLoader` is still a fork of `AssetLoader` missing single-flight joins, `ReleaseAsset`,
  `LoadResult` variants and dual UniTask signatures — and it is invisible to catalog invalidation,
  so caches it holds keep serving pre-update content. It is being retired in the next pre-release;
  prefer `AssetLoader`. Evidence is recorded in `Documentation/LIFETIME_DESIGN.md`.
- `Standard.ClearCache(scopeName)`, `Simple.Release<T>` and `Standard.LoadScene<T>` are still the
  stubs the review found. Do not rely on them.
- `TieredCache.Pin()` on a key that is not cached is still a silent no-op, despite documentation
  teaching pin-before-load.
- `ThreadSafeCacheManager` still advertises "safe from any thread" while its operations reach Unity
  API. Treat it as main-thread.

### Verification

Both assemblies compile. Runtime verified against Unity 2022.3.62f3 in both the `Task` and `UniTask`
configurations — the second is the one that shipped broken as `pre.4`. 64/64 EditMode tests pass.

Not verified: `Profiler.GetRuntimeMemorySizeLong` in a non-development build (it can return 0 on some
platforms, which would leave eviction permanently idle), and the two pooling paths that need a real
`LoadSceneMode.Single` transition to reproduce.

## [4.1.0-pre.5] - 2026-08-14 - Fixes a compile break in pre.4

**Anyone on 4.1.0-pre.4 should move to this. pre.4 does not compile on Unity 2023.x or
2022.3.**

### Fixed

- **`SceneAssetScope.GetOrCreate(Scene)` used an API that only exists on Unity 6.**

  ```
  SceneAssetScope.cs(136,58): error CS1503: cannot convert from
  'UnityEngine.FindObjectsInactive' to 'UnityEngine.FindObjectsSortMode'
  ```

  pre.4 replaced a deprecated call with `FindObjectsByType<T>(FindObjectsInactive)`,
  which is what Unity's own deprecation message tells you to use. That overload was
  added in Unity 6. On 2022.3 and 2023.x the only generic overloads take a
  `FindObjectsSortMode`, so the single-argument form binds to the wrong one and fails to
  compile — and `package.json` declares `unity: 2023.1`.

  Now uses the two-argument form, which exists on every supported version, with the
  Unity 6 deprecation warning suppressed at the call site and the reason recorded there.
  Dropping `FindObjectsInactive.Include` instead would have compiled everywhere and
  silently stopped finding scopes on inactive objects, which is worse.

### Added

- **`Tools/check-min-unity-api.sh`** — compiles the package against a chosen editor's
  reference assemblies, so "does this compile on the oldest Unity we support" is a
  question that gets asked before publishing rather than by the first person to install
  it. Known harness/editor divergences are listed with reasons and reported when hit, so
  a pass cannot quietly cover less than it claims.

- Two CI steps in `content-update.yml`: the catalog-versus-bundles check, and the
  minimum-Unity compile. The second warns loudly and continues when
  `MIN_UNITY_EDITOR` is unset, rather than passing silently.

### Why pre.4 passed every check

Every verification ran against Unity 6000.5.7f1, the newest editor on the build
machine: 0 error CS, 0 warning CS, six tabs probed, 57/57 tests green, twice. All of it
true, and none of it about the version the package says it supports. Running the newest
installed Unity and running the oldest supported one are different tests; only the
second one answers whether the package compiles for the people installing it.

## [4.1.0-pre.4] - 2026-08-14 - Catalog Inspector, samples, and a clear-out

Closes the last piece of Editor tooling, moves the examples to where a package is
supposed to keep them, and removes code that was doing nothing.

### Added

- **Catalog Inspector tab** (task 5.9) — reads a built binary catalog and shows what is
  in it: every entry with the bundle it comes from, every bundle with its size, CRC and
  dependents, and a cross-check against the bundles on disk. Missing, orphaned and
  size-mismatched bundles are reported separately because they mean different things:
  missing breaks players, orphaned costs storage, mismatched means the catalog and the
  bundles came from different builds.

  No third-party dependency was added. `AddressablesTools` would have meant owning a
  binary-format parser that has to track every Addressables release; instead
  `CatalogReader` reflects two internals — `ContentCatalogData.LoadFromFile` and
  `CreateCustomLocator` — and everything after that is public interface.
  `CatalogReaderTests` resolves both against the installed Addressables, so an upgrade
  that renames either fails a test rather than producing an empty inspector.

- **`CatalogInspectCLI`** — the same check in batchmode, exit 1 when the output is not
  publishable. Worth running before an upload: a catalog that references a bundle you
  did not upload produces a 404 on a device and nothing in the build log.

- **Samples, as UPM samples.** `CDN Boot`, `API Examples` and `Rule Automation Presets`
  now install from Package Manager instead of sitting in `Assets/`. They are opt-in and
  no longer compile in every project that installs the package. The rule preset set is
  wired together — a layout rule referencing real filters and a provider, and a
  composite referencing the layout rule — so it demonstrates something rather than
  being empty scriptable objects.

- **`Documentation/CDN_USAGE_GUIDE.md`** (task 5.5) — a guide for the person building
  the game, shipped inside the package: whether you need remote content at all, setup,
  the boot sequence, downloads, patching, the cache, environments, one row per error
  code, CI, and the things that will catch you out. Every API name in it was checked
  against the source.

- `TROUBLESHOOTING.md` now ships **inside the package**, so it is available offline to
  whoever hits the error message that names it.

### Fixed

- **Bundle location shown for what it is.** Found on the Catalog Inspector's first run
  against a real catalog, which reported 8 missing bundles and 6 orphans on a build that
  was fine. Two causes, both worth knowing about:

  `AssetBundleRequestOptions.BundleName` in a built catalog is an internal hash, not a
  file name — the requested file name is the last segment of `InternalId`. And a
  catalog holds both remote bundles and bundles that ship inside the player; comparing
  the second kind against a CDN folder produces a false alarm per bundle.

  A load path the package cannot classify is now reported as unchecked rather than
  quietly skipped, so a clean verdict cannot silently cover less than it claims.

- **Shared tab styles are actually loaded.** `.cdn-tab-page` and the row, button and
  section classes were defined in individual tab stylesheets, which are attached to that
  tab's root and torn down when the shell rebuilds the tab body. Three tabs used classes
  that did not exist while they were showing. The shared vocabulary moved to
  `CdnManagerWindow.uss`, which is always loaded; per-tab overrides still win.

- **A characterization test that had never passed.** `Message_ExtraWhitespace_StillMatches`
  asserted that `"not  found"` matches the keyword `"not found"`. It does not — the
  classifier lowercases and calls `Contains`, nothing more. The test was written from the
  doc comment rather than from a run. It now records the real behaviour, with a
  counterpart test for padding around an intact keyword.

### Removed

- **`AddressableProgressBar.autoFindTracker`** — a serialized inspector toggle,
  defaulted to true, read by nothing. No auto-find code was ever written; binding has
  always been explicit through `BindToTracker`. A checkbox promising behaviour the
  component does not have is worse than no checkbox.

- **`README.backup.md`** — 1273 lines of superseded README that `git subtree split` was
  shipping to every consumer.

### Changed

- `MemoryGraphView.Clear()` is now `ClearSamples()`. It hid `VisualElement.Clear()`,
  which means the same call did two unrelated things depending on the static type of the
  reference. It had no callers.
- Two private `DrawHeader()` methods in custom inspectors renamed to `DrawTitleSection()`;
  they hid `Editor.DrawHeader()`.
- Deprecated Unity APIs replaced: `FindObjectOfType` → `FindAnyObjectByType`,
  `FindObjectsByType(…, FindObjectsSortMode)` → the overload without it.
- README rewritten around what the package is now: correct Unity and Addressables
  versions, the current install URL, all six CDN tabs, the samples, and a deprecation
  table instead of a version history that stopped at 3.0.0.

### Still not here

Phase 5's field validation: no device matrix, no staging soak, no measurement against a
real CDN, and the CI workflows have never run. Three Phase 3 measurements are also unrun
— cancel-and-resume, speed accuracy under throttling, and the allocation check — each
needing test infrastructure rather than code. The six tabs build and reach correct
conclusions in batchmode; nobody has looked at them.

## [4.1.0-pre.3] - 2026-08-14 - Downloads, cache and diagnostics (Phases 3 and 4)

`pre.2` could boot against a CDN and apply a catalog update. This one downloads content
properly and stops the install growing forever.

### Added

- **`CdnManager.DownloadAsync` / `GetDownloadSizeAsync`** — progress in real bytes,
  cancellation, retry with exponential backoff and jitter, and pre-flight checks for
  reachability, metered network and free disk. Size returns `CdnResult<long>`, so
  "nothing to download" is distinguishable from "could not find out".
- **`RetryPolicy`** — retryability comes from the error, not the policy, so the CLI, the
  GUI and the runtime cannot disagree about what is worth retrying.
- **`CdnErrorMapper`** — classifies from the HTTP response code rather than by matching
  words in a message. A 404 on a bundle and a 404 on a catalog mean different things
  about your deploy and now produce different codes.
- **`CacheService`** — usage stats, eviction by key, and obsolete-bundle cleanup.
- **`ICdnTelemetry`** — an interface with no transport bundled; wire it to whatever you
  already use.
- **`LoadErrorCode.ContentNotDownloaded`** — "the address is valid but the bundle is not
  on this device", previously indistinguishable from `AssetNotFound`.
- **Runtime Monitor tab** and a **CDN status row** in the Addressables dashboard.
- **`AddressableProgressBar.SetDownloadProgress`** — real bytes, speed and ETA.
- **Fault injection** for the local test server: HTTP errors, dropped connections,
  latency and bandwidth throttling.
- **Troubleshooting guide** with an entry for every `CdnErrorCode`.

### Fixed

- **Obsolete bundles are now removed after a catalog update.** Addressables leaves the
  superseded bundles on disk and nothing else removes them, so an install accumulates a
  generation of content per patch. `ApplyUpdateAsync` cleans them automatically.
- **Download speed and ETA were meaningless.** `ProgressiveAssetLoader` computed speed as
  a fraction-per-second multiplied by 100 and labelled it KB/s — a 10 MB and a 10 GB
  download reported the same number — over an elapsed time that was never reset, so the
  figure decayed toward zero regardless of the network. `ProgressInfo.BytesDownloaded`
  and `TotalBytes` existed and were never populated. Both are correct now.
- **Catalog operations never retried.** `CatalogService` classified its own failures with
  a placeholder that returned `Unknown`, which is not retryable, so a transient 503 during
  an update check was treated as permanent.
- **Line endings could change bundle contents.** With `core.autocrlf` on and no
  `.gitattributes`, git rewrote LF to CRLF in TextAssets on checkout — the same commit
  then produced different bundles on different machines.

### Deprecated

`AssetLoader.DownloadDependenciesAsync` / `GetDownloadSizeAsync`, `Assets.Download` /
`GetDownloadSize`, `StandardAPI.DownloadDependencies` / `GetDownloadSize`. Warnings only,
kept until 5.0.0, each naming its replacement.

### Still not here

Phase 5's field validation: no device matrix, no staging soak, no measurement against a real CDN,
and the CI workflows have never run. Three Phase 3 measurements are also unrun — the
cancel-and-resume proof, speed accuracy under throttling, and the allocation check — each
needing test infrastructure rather than code.

## [4.1.0-pre.2] - 2026-08-14 - CDN runtime layer (Phase 2)

The half that was missing. `4.1.0-pre.1` could produce and verify content; this one
lets a game boot against a CDN and consume it.

### Added

- **`CdnManager`** — the entry point. `InitializeAsync`, `CheckForUpdateAsync`,
  `ApplyUpdateAsync`, `SetEnvironment`, `PromoteFailover`.
- **`CdnResult<T>` / `CdnError` / `CdnErrorCode`** — 15 codes with retry
  classification, mirroring the existing `LoadResult` / `LoadError` shape.
- **`CdnSettings`** — ScriptableObject in `Resources`, with environments, failover
  origins, an env-var host override, and a download policy.
- **`CatalogService`** — initialise, check, apply, each returning a result rather
  than throwing.
- **`CdnRequestDecorator`** — installs the Addressables hooks, and refuses if
  Addressables already initialised.
- **`HostRewriter`** — swaps the origin so one build can be pointed at another
  environment without a rebuild.
- **`NetworkPolicy`** — reachability and metered detection, with its limits documented.
- **`CdnBootExample`** — the canonical boot sequence, branching on every outcome.

### Call it first

`CdnManager.InitializeAsync` must run before anything else touches Addressables.
Addressables initialises implicitly on its first load call, and the CDN hooks only
apply to content resolved after they are installed. A stray `LoadAssetAsync`, or an
`AssetReference` on an object in your boot scene, is enough to lose them — so
initialisation fails loudly in that case instead of half-applying.

```csharp
var init = await CdnManager.InitializeAsync();
if (init.IsFailure)
{
    if (init.ErrorCode == CdnErrorCode.NoContentAvailableOffline)
        ShowBlockingSetupScreen();   // first launch, nothing cached
    return;
}

var check = await CdnManager.CheckForUpdateAsync();
if (check.IsSuccess && check.Value.HasUpdate)
    await CdnManager.ApplyUpdateAsync(check.Value);
```

Create the settings asset via **Assets > Create > Addressable Manager > CDN Settings**
and put it in a `Resources` folder. It has to load from Resources, because it is what
tells Addressables where the catalog is.

### Named CdnManager, not Cdn

The design documentation calls the facade `Cdn`. That name does not compile: the
runtime types live in namespace `AddressableManager.Cdn`, so `Cdn` at a call site
binds to the namespace.

### Verified

Four PlayMode integration tests against a real HTTP server, green twice in a row.
Assertions are made against the server's request log rather than the client's return
value — Addressables in Fast Mode reports success without a byte crossing HTTP, so a
passing call proves nothing on its own.

### Still not here

Phase 3 and beyond: download orchestration, progress in real bytes, cancellation,
resume, retry with backoff, cache management, and the diagnostics window. Content
downloads currently go through Addressables' own path with no CDN-specific retry or
progress reporting. Nothing has been tested against a real CDN — only a local server.

## [4.1.0-pre.1] - 2026-08-11 - CDN build pipeline (Editor only)

Pre-release for integration testing. Adds the Editor-side CDN pipeline: build
remote content, refuse to build a broken patch, verify the output, and see what
a patch costs a player.

### Read this before integrating

**There is no runtime CDN layer in this release.** Nothing under `Runtime/`
knows about a CDN. Your game cannot yet initialise against one, check for a
catalog update, apply one, or define what happens offline — that is Phase 2 and
is not written. What you can do today is produce and verify content from the
Editor or from batchmode. If you are evaluating "can my game download content
from a CDN", this release does not answer that question yet.

Everything below is also **unverified against a real CDN**. It has been run
against a local HTTP server only.

### Requirements

- Unity **2023.1+** (raised from 2022.3 — Addressables 2.9.1 sets this floor)
- `com.unity.addressables` **2.9.1** (raised from 2.3.1; 2.3.1 will not compile,
  several APIs used here do not exist in it)

### Added

- **`CdnBuildPipeline`** — the gated build sequence, shared by the CLI and the
  GUI so the order of checks exists in one place. Every gate runs before the
  build, because the failure the version gate prevents cannot be detected after.
- **`CdnBuildCLI`** — batchmode entry points `BuildContent`,
  `BuildContentUpdate`, `VerifyOutput`. Non-zero exit on any failure, and each
  gates on `EditorUtility.scriptCompilationFailed` first: Unity exits 0 when
  `-executeMethod` runs against a broken assembly and the method never runs.
- **`ContentStateManager`** — resolve, validate and archive
  `addressables_content_state.bin`. `Validate` compares the state file's
  `playerVersion` against `PlayerSettings.bundleVersion` and refuses a mismatch.
- **`ContentUpdateRestrictions`** — reports which entries in StaticContent
  groups changed, and whether each was modified directly or pulled in as a
  dependency.
- **`CatalogVerifier`** — checks the settings contract, that the catalog is
  named for the app version players actually poll for, and that every bundle in
  the manifest exists on disk with the recorded size and SHA256.
- **`BuildManifest` / `BuildManifestWriter`** — `build-manifest.json` next to
  the output: app version, platform, build type, git SHA, catalog metadata, and
  every bundle with its SHA256. Serialised with `JsonUtility`; no Newtonsoft
  dependency is added.
- **`ContentDiff`** — compares two manifests by content hash and reports what a
  player on the older build must download. The result is embedded in each
  manifest, so CI can assert on patch size without re-running the comparison.
- **`CdnProfileManager`** — Local / Dev / Staging / Prod profiles, a catalog
  path separate from the bundle path, and CDN host injection from an
  environment variable so URLs are not committed.
- **`LocalContentServer`** — static file server with real cache headers and
  range-request support, for testing without a CDN.
- **CDN Manager window** (`Window → Addressable Manager → CDN Manager`) with
  four tabs: Settings Validator, Local Server, Update Preview, Build.
- **`SettingsContract`** — 70 rules covering the Addressables configuration a
  CDN setup requires, shared by the GUI validator and the CI verifier so the
  two cannot disagree.
- **`CdnSetupCLI`** — applies the whole contract from batchmode, idempotent.

### Fixed

Two pre-existing package bugs found while building the above:

- **`AddressRule`** created groups with no schemas attached, so those groups
  silently produced no content. `Scene.asset` in the sample project had been
  broken this way.
- **`HierarchyAssetScope`** used `Object.GetInstanceID()`, which Unity 6000.5
  marks `Obsolete(error: true)`. Now guarded by `#if UNITY_6000_5_OR_NEWER`, so
  the package still compiles below that version without raising its floor.

### Known gaps

- No runtime CDN layer (above).
- The GitHub Actions workflows and `ci/*.sh` shipped in the repository — not in
  this package — have never been executed.
- The four Editor tabs have been verified to build, refresh and report correctly
  from batchmode, but nobody has looked at them yet.
- The design documentation still states that a changed StaticContent group
  requires a new player build. It does not; the remedy is the Prepare step in
  the Update Preview tab. The code is correct, the prose is not.

### Note for projects with TextAssets in remote groups

Git rewrites line endings on checkout when `core.autocrlf` is on, and for a
TextAsset those bytes are what ends up in the bundle — the same commit then
produces different bundles on different machines. Mark such assets `-text` in
`.gitattributes`.

## [4.0.1] - 2026-05-24 - Fix StandardAPI.InstantiateSession compile error

4.0.0 removed `AddressablesFacade.GetSessionScope()` and replaced it
with `GetSessionLoader()` returning the underlying `AssetLoader`
directly, but `StandardAPI.InstantiateSession` still called the old
name — broke compile for any consumer touching that method.

### Fixed
- **`StandardAPI.InstantiateSession(address)`** now calls
  `Facade.GetSessionLoader()`. Behaviour also tightened: if the
  session hasn't been started, the method auto-starts it (via
  `Facade.StartSession()`) instead of logging an error and returning
  null — matching the ergonomic of `LoadSessionAsync` from 4.0.0.

### Notes
- `AdvancedAPI.GetSessionScope()` is unrelated — it returns
  `HybridScope.Session` (a different class, still present). No change
  needed there.

## [4.0.0] - 2026-05-24 - Scope identity overhaul + SessionAssetScope removal

Two architectural changes graduating from the design review:

1. **Scope identity vs display split** — every scope now has a unique
   `ScopeId` (used for ScopeManager lookup + monitoring channel) and a
   separate friendly `DisplayName` (used for Dashboard / inspector
   labels). Multi-instance scopes (Scene / Hierarchy) encode owner
   identity into the id so they stop colliding.
2. **`SessionAssetScope` deleted** — the class was redundant
   (mechanically identical to `GlobalAssetScope` apart from teardown
   timing) and *limiting* (singleton, so no parallel sessions
   possible). Sessions now route through
   `ScopeManager.GetOrCreateScope("Session")`; the facade ergonomics
   (`Assets.StartSession()` / `LoadSession<T>` / `EndSession()`) are
   preserved as thin forwarders.

### Added
- **`BaseAssetScope.ScopeId`** — unique identifier, used as dictionary
  key by `ScopeManager` and as the monitoring channel by
  `AssetMonitorBridge`.
- **`BaseAssetScope.DisplayName`** — friendly label shown in the
  Dashboard inspector, defaulting to `ScopeId` if not provided.
  `BaseAssetScope.ScopeName` is kept as a back-compat alias for
  `ScopeId`.
- **`customScopeId` + `customDisplayName`** `[SerializeField]` on
  both `SceneAssetScope` and `HierarchyAssetScope` — Inspector-set
  semantic ids ("PlayerInventory", "Match_42", "Lobby"…). Empty =
  use the auto-derived default.
- **`HierarchyAssetScope.AddTo(GameObject, customScopeId,
  customDisplayName)`** factory overload. Internally stashes the
  pending id on a static field consumed by the newly-added
  component's Awake — letting you inject the id *before*
  initialisation without having to deactivate the GameObject.
- **`SceneAssetScope.CreateForScene(Scene, customScopeId,
  customDisplayName)`** + **`GetOrCreate(Scene)`** overloads.
  Cross-scene safe — `GetOrCreate(Scene)` filters by
  `_ownerScene == scene` instead of returning the first match across
  every loaded scene.
- **`SceneAssetScope.OwnerScene`** public property — exposes the
  scope's bound scene.

### Changed
- **Default `Hierarchy` scope id** is now
  `Hierarchy-{name}#{GetInstanceID()}` (was `Hierarchy-{name}`).
  Unique per Unity object, so 50 enemies with the same name no longer
  share a ScopeManager dictionary key.
- **Default `Scene` scope id** is now
  `Scene-{sceneName}#h{scene.handle}` (was `Scene-{sceneName}`).
  Unique per loaded scene instance — additive loads of the same scene
  no longer collide.
- **`SceneAssetScope.CreateForCurrentScene()`** now calls
  `SceneManager.MoveGameObjectToScene(go, scene)` so the new scope
  GameObject lives in the target scene, not in whichever scene was
  active when the call ran (latent bug).
- **`AddressablesFacade.StartSession()`** / **`EndSession()`** now
  back themselves with `ScopeManager.Instance.GetOrCreateScope("Session")`
  and `ClearScope("Session")` respectively. `LoadSessionAsync<T>`
  auto-starts the session on first call instead of erroring "no
  active session".
- **`AddressablesFacade.GetSessionScope()` removed** — replaced by
  **`GetSessionLoader()`** which returns the underlying
  `AssetLoader` directly. Same shape (one method, one return value),
  but no more pretending the session is a special class.
- **GameObject menu "Add Session Scope" removed** from Editor
  context menus. Quick Setup's "Create All Scope Objects" no longer
  spawns a `[SessionAssetScope]` GameObject.

### Removed
- **`SessionAssetScope` class** — deleted (`Runtime/Scopes/SessionAssetScope.cs` +
  meta + Editor inspector). Use `ScopeManager.GetOrCreateScope("Session")`
  for direct access or the unchanged `Assets.StartSession()` /
  `Assets.LoadSession<T>(...)` facade helpers.

### Migration
- **Code calling `SessionAssetScope.Instance` / `StartSession()` /
  `EndSession()` directly** — replace with the facade
  (`Assets.StartSession()` etc.) or `ScopeManager.Instance.GetOrCreateScope("Session")`.
- **Code matching scope names exactly** (e.g.
  `if (monitor.scopeName == "Hierarchy-Enemy(Clone)") { … }`) breaks
  because the default id now embeds `#{InstanceID}`. Either:
  - Switch to `StartsWith("Hierarchy-")` / `Contains(":")`, OR
  - Set `customScopeId` to a known string and match that exactly.
- **GameObject prefabs containing `SessionAssetScope`** — open and
  remove the now-missing-script entry. Re-add the behaviour via
  `Assets.StartSession()` at runtime if needed.
- **`AssetScopeType.Session` enum value retained** — semantic still
  valid; resolvers should map it to
  `ScopeManager.GetOrCreateScope("Session")`.

## [3.5.1] - 2026-05-23 - UniTask compile-fix for merged branch code

Fixes compile errors that surfaced when 3.5.0 was consumed by a
project that has `com.cysharp.unitask` installed (UNITASK_PRESENT
define active). The branch's code was written before the
Task ↔ UniTask switch existed in 2.3.0, so several call sites
expected concrete `Task<T>` types where the switched API now
returns `UniTask<T>`.

### Fixed
- **`Runtime/Threading/ThreadSafeAssetLoader.cs`** — every public
  `LoadAssetAsync` / `LoadAssetsByLabelAsync` / `InstantiateAsync`
  overload now switches its return type between `Task<T>` and
  `UniTask<T>` via `#if UNITASK_PRESENT`, matching the rest of the
  package. The internal `TaskCompletionSource` for the by-label
  branch also switches to `UniTaskCompletionSource` when UniTask
  is present.
- **`Runtime/Threading/LoadOperation.cs`** — the internal queued
  load + instantiate operations now use
  `UniTaskCompletionSource<T>` + `Func<UniTask<T>>` under
  `UNITASK_PRESENT`, falling back to the original
  `TaskCompletionSource<T>` + `Func<Task<T>>` otherwise.
- **`Runtime/Pooling/AddressablePoolManager.cs`** —
  `task.Wait()` + `task.Result` replaced with
  `task.GetAwaiter().GetResult()` in the auto-create code path.
  `Wait` / `Result` are Task-only members; the awaiter form works
  for both Task and UniTask.
- **`Runtime/API/StandardAPI.cs`** — `DownloadDependencies` return
  type corrected from `Task<long>` to `Task<bool>` to match
  `AssetLoader.DownloadDependenciesAsync` (changed from
  `Task<long>` to `Task<bool>` in 2.2.0).
- **`Runtime/API/AdvancedAPI.cs`** — `LoadFromBackgroundThread`
  uses `UniTask.RunOnThreadPool` under `UNITASK_PRESENT`, falling
  back to `Task.Run` otherwise. `Task.Run` cannot accept
  `Func<UniTask<T>>`.

### Notes
- No public API change for consumers without UniTask installed.
- Consumers with UniTask installed previously got a build failure
  on first compile; now compiles cleanly. Public return types in
  `ThreadSafeAssetLoader` correctly become `UniTask<T>` to match
  the rest of the surface.

## [3.5.0] - 2026-05-23 - Branch merge: Tiered API + Rules engine + Hardening lineage

Merges the `feat/asset-importer` branch (Tiered API v3, thread-safety,
SmartAssetHandle, Result pattern, DynamicPool, Rule-based automation
with 8 filter types, Layout Rule Editor, CLI tools, auto-apply on
import) into the 2.3.x lineage (audit hardening, UniTask switching,
documentation refresh). All work from both branches is preserved.

### Added (from `feat/asset-importer`)
- **Tiered API** — Simple / Standard / Advanced loader layers with
  progressive complexity.
- **Thread-safety** — `ThreadSafeAssetLoader`, `UnityMainThreadDispatcher`,
  `LoadOperation<T>` for background-thread loads with main-thread
  dispatch. `AssetLoader` now ships `AssertMainThread()` guards with
  detailed remediation hints.
- **SmartAssetHandle<T>** — `IDisposable` wrapper for `using`-statement
  auto-release; GC finalizer as safety net; `.ToSmart()` /
  `.LoadAssetSmartAsync()` extensions.
- **`LoadResult<T>` / `LoadError`** — Rust-style result pattern with 11
  specific error codes, hints, and `.Match` / `.Map` / `.FlatMap` /
  `.Unwrap*` helpers. New `LoadAsyncSafe<T>` overloads on `AssetLoader`.
- **Dynamic pools** — `DynamicPool<T>` + `DynamicPoolConfig` that grow
  / shrink based on usage; `Default` / `Conservative` / `Aggressive`
  presets plus `Fixed()` for static behaviour. `AddressablePoolManager`
  gains `CreateDynamicPoolAsync`, `GetDynamicPoolStats`, `ResizePool`,
  `IsDynamicPool`.
- **Tiered cache + ValidationMode + HybridScope + ThreadSafeCacheManager**
  for advanced cache management.
- **Rule-based automation** — `AddressableRuleConfig` + 8 filter
  types (label / address-equals / glob / regex / group / etc.), Layout
  Rule Editor, Layout Viewer with conflict detection, composite rule
  merging, batch operations, auto-apply-on-import. This is the
  "rules and filters" surface that was previously missing.
- **CLI tools** for CI/CD pipelines.

### Preserved (from 2.2 / 2.3 hardening lineage)
- All Blocker / High / Medium / Low / Nice-to-have fixes from 2.2.0:
  `ProgressiveAssetLoader` release-on-fail, `AddressablePoolManager`
  template-handle release (now applies to both `CreatePoolAsync` and
  `CreateDynamicPoolAsync`), `MonitoredAssetLoader` rewritten as
  forwarder, `AssetLoader.ReleaseAsset` cast fix, `SceneAssetScope`
  dispose collapse, `BaseAssetScope.DisposedToken`, `ScopeManager`
  `SubsystemRegistration` reset, optional TMP via `TMP_PRESENT`,
  shared-list refcount, `Facade.OnDestroy` releases scopes,
  `DebugSettings` Editor-only `Resources.Load`, `Assets` facade
  parity, `MonitoringHelperInspector` moved to Editor.
- **UniTask switching** (`UNITASK_PRESENT` versionDefine) preserved on
  every public async signature — both the original `CreatePoolAsync`
  and the newly-merged `CreateDynamicPoolAsync` switch return types
  between `Task<T>` and `UniTask<T>`.
- Doc refresh from 2.2.1 superseded by branch's v3 README, but
  architecture diagrams and migration tables retained where they
  describe behaviour still present in 3.x.
- `MonitoringHelperInspector.cs.meta` from 2.3.0 retained.

### Migration
- `package.json` jumps from 2.3.0 → 3.5.0 (branch's authoritative
  version).
- Unity floor stays at **2022.3** (the higher of the two branches' floors).
- `com.cysharp.unitask 2.3.0+` continues to auto-switch the async
  surface to `UniTask<T>`.
- Consumers that were on 2.3.0 and relied on `Task<T>` return types
  without UniTask installed see no change. With UniTask installed,
  return types become `UniTask<T>` as documented.
- Consumers on 1.x / 2.0 / 2.1 should re-read the 2.2.0 audit
  CHANGELOG for behavioural changes around handle release, scope
  dispose, and `DownloadDependenciesAsync` signature (`Task<long>` →
  `Task<bool>`).

## [2.3.0] - 2026-05-23 - UniTask switching + dependency auto-detection

### Added
- **Automatic `Task` ↔ `UniTask` switching.** The Runtime asmdef now declares a `versionDefines` entry that defines `UNITASK_PRESENT` whenever `com.cysharp.unitask 2.3.0+` is installed in the consumer project. Every public async method (`AssetLoader.LoadAssetAsync`, `Assets.Load`, `AddressablesFacade.LoadGlobalAsync`, `AddressablePoolManager.CreatePoolAsync`, `ProgressiveAssetLoader.LoadAssetWithProgressAsync` etc. — 30 signatures across 6 files) now returns `UniTask<T>` when the define is active, `Task<T>` otherwise. Body await sites are awaiter-compatible and unchanged.
- `Task.Yield()` / `Task.WhenAll(…)` / `new Task<T>[…]` inside `ProgressiveAssetLoader.LoadMultipleWithProgressAsync` are bracketed with `#if UNITASK_PRESENT` so they switch to the corresponding `UniTask` calls when UniTask is present (avoids the cost of awaiting a `Task.YieldAwaitable` from inside a `UniTask` async method).
- UniTask asmdef listed under the Runtime asmdef `references`. Unity ignores the reference gracefully when UniTask is not installed.

### Fixed
- `Editor/Inspectors/MonitoringHelperInspector.cs` shipped without a `.meta` file in 2.2.0, triggering Unity's "Asset has no meta file, but it's in an immutable folder. The asset will be ignored." warning when the package was consumed via UPM. Generated a stable GUID + `MonoImporter` meta so the inspector loads on first import.

### Notes
- This is a backwards-compatible change for projects **without** UniTask. Projects **with** UniTask installed will see public return types change from `Task<T>` to `UniTask<T>`. The two are awaiter-compatible (you can `await` a `UniTask` from a `Task` async method and vice versa), but code that captured the return value as a concrete `Task<T>` variable will need to either uninstall UniTask, refactor to `var`, or call `.AsTask()` on the result.
- Editor asmdef is unchanged — Editor code uses no `Task`/`UniTask` surface.

## [2.2.1] - 2026-05-23 - Documentation Refresh

Doc-only release; no code changes.

### Changed
- **`README.md`** rewritten end-to-end against the current API surface: install via tagged git URL, scope concept table, scoped/static/direct usage paths, pooling + progress + monitoring snippets, architecture diagram, migration notes for 2.1 → 2.2 and 2.0 → 2.1. ~60 % shorter than the previous version.
- **`MONITORING_GUIDE.md`** rewritten from scratch around the new "always-on, Editor-only" monitoring model. Removed the obsolete "use extension methods" framing, the made-up `LoadAssetAsync(address, scopeName)` overload, and the deprecated `handle.Release(address)` pattern. Added scope-name resolution table, `IAssetMonitor` example, build-time guarantees.
- **`EDITOR_TOOLS_GUIDE.md`** rewritten to match the real API: every `LoadAssetAsync<T>(address, scopeName)` call corrected, every `.Monitored()` reference removed, `ScopeManager` examples updated, recipes consolidated, troubleshooting table tightened.

### Notes
- The docs now treat `Assets` (static) and `AddressablesFacade` (MonoBehaviour) as parity-equivalent entry points (`Assets` gained the missing API in 2.2.0).
- Cross-doc links and footer version bumped to 2.2.1.

## [2.2.0] - 2026-05-23 - Audit-Driven Hardening

A pre-ship audit found three blocker-class Addressables ref-count leaks and a
handful of correctness / API issues. Every finding is addressed below.

### 🔴 Blocker fixes
- **`ProgressiveAssetLoader` leak** — `LoadAssetWithProgressAsync` /
  `DownloadWithProgressAsync` now `Addressables.Release` the underlying handle
  in `finally` when the load fails or throws, instead of orphaning it.
- **`AddressablePoolManager` template-handle leak** — the prefab handle
  acquired during `CreatePoolAsync` is now retained per-pool and disposed in
  `ClearPool` / `ClearAllPools` / `Dispose`. Previously every pool created
  a permanent +1 Addressables ref-count.
- **`MonitoredAssetLoader` build-time leak + AssetReference bypass** —
  rewritten as a thin forwarder around `AssetLoader` (which already owns
  monitoring under `#if UNITY_EDITOR`), so monitoring dispatch no longer
  ships in builds and `LoadAssetAsync<T>(AssetReference)` no longer rewrites
  the cache key through `assetReference.AssetGUID`.

### 🟠 High fixes
- **`AssetLoader.ReleaseAsset`** — the always-null `IAssetHandle<object>`
  cast is replaced with a non-generic `IDisposable` cache. Calling
  `ReleaseAsset(address)` now actually releases every cached handle
  matching that address.
- **`SceneAssetScope`** — collapsed the dual `sceneUnloaded` + `OnDestroy`
  dispose paths into a single `Destroy(gameObject)` funnel so cleanup runs
  exactly once.
- **`HierarchyAssetScope` / `BaseAssetScope`** — added a `DisposedToken`
  (`CancellationToken`) that fires when the scope is disposed. Long-running
  awaits can pass it to `Task.WaitAsync` and unwind cleanly when the owning
  GameObject is destroyed mid-load.
- **`ScopeManager`** — added a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`
  reset that disposes lingering scopes and nulls the static singleton on
  domain reload / new Play sessions.
- **`AddressableProgressBar`** — TextMeshPro is no longer a hard compile-time
  dependency. The runtime asmdef now defines `TMP_PRESENT` only when
  `com.unity.textmeshpro 3.0.0+` is installed; without TMP the component
  falls back to plain `UnityEngine.UI.Text`.

### 🟡 Medium fixes
- **`SharedListOperationTracker`** — initial refcount changed from 0 to 1,
  so empty-result label loads release the list handle immediately instead
  of leaking it until the loader disposes.
- **`AddressablesFacade.OnDestroy`** — now disposes the global scope and
  ends any active session before clearing the singleton, fixing repeat
  Editor Play-mode iterations leaking handles into the next session.
- **Double monitoring removed** — `MonitoredAssetLoader` no longer
  re-reports loads that `AssetLoader` already reported (Dashboard counts
  were doubled in Editor).
- **`DebugSettings`** — the `Resources.Load<DebugSettings>("AddressableManager/DebugSettings")`
  lookup is now inside `#if UNITY_EDITOR`. Shipping builds get a transient
  default instance instead of warning at runtime; added a static
  `IsVerbose` accessor used to gate informational logs.
- **README requirements** — Unity floor corrected to 2022.3 and the footer
  bumped to 2.2.0; flagged TextMeshPro as optional.

### 🟢 Low fixes
- **Verbose logging gated** — `AssetLoader` cache-hit / load-success
  `Debug.Log` calls go through `DebugSettings.IsVerbose`. Mobile shipping
  builds no longer pay a GC-allocating string interpolation per cache hit.
- **`DownloadDependenciesAsync` return type** — changed from `Task<long>`
  with a magic `1`/`0` sentinel to `Task<bool>` matching its semantics.
- **`PoolConfiguration.destroyOnFull`** — annotated `[HideInInspector]` +
  `[Obsolete]`; the underlying `UnityEngine.Pool.ObjectPool` always
  destroys excess instances above `maxSize`, so the flag had no effect.
- **`AddressableManager.Editor.asmdef`** — opaque GUID references
  replaced with portable name references (`AddressableManager`,
  `Unity.Addressables`, `Unity.Addressables.Editor`, `Unity.ResourceManager`).
- **`AssetLoaderExtensions`** — the deprecated forwarder class is removed.
  Use `AssetLoader.LoadAssetAsync` directly (monitoring is automatic).

### ⚪ Nice-to-have
- **`AssetMonitorBridge` thread-safety** — switched the listener list to a
  copy-on-write `IAssetMonitor[]` with a lock around register/unregister,
  plus a `SubsystemRegistration` reset that drops stale Editor listeners
  on domain reload.
- **`Assets` facade parity** — added `GetPoolStats`, `ClearPool(address)`,
  `SetPoolFactory`, `ReleaseInstance`, and `GetOrCreateSceneScope` so the
  static facade now matches `AddressablesFacade`.
- **`MonitoringHelperEditor` moved** — the nested `CustomEditor` was
  promoted to `Editor/Inspectors/MonitoringHelperInspector.cs` so the
  runtime assembly no longer carries an `UnityEditor` type.
- **`ScopeManager.GetScopeMemoryUsage`** — marked `[Obsolete]` with a note
  that runtime memory tracking is not implemented; live numbers stay in
  the Editor Dashboard.

### Migration
- `AssetLoader.DownloadDependenciesAsync` now returns `Task<bool>`. Callers
  that compared the result to `1`/`0` must switch to `true`/`false`.
- `AssetLoaderExtensions.*Monitored` methods are removed. Replace with the
  plain `AssetLoader.LoadAssetAsync` overloads — monitoring is automatic.

## [2.1.0] - 2025-01-XX - Automatic Monitoring

### Maintenance (added 2026-05-23)
- Aligned package metadata with the rest of the DreamTech library family: minimum Unity bumped to **2022.3**, author standardised to **DreamTech**.



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
