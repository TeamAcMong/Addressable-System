# Lifetime and Ownership Design — com.game.addressables 4.1.0

Status: **partially implemented.** A-2, A-6, A-7, and A-12 (§5 steps 4, 5, 6 minus the naming
half of A-12 being everything but the storage collapse) are landed, plus the §3.6 shutdown guard
for `GlobalAssetScope.Instance`/`Simple.Destroy` **and**, added in the follow-up review-fix pass,
`AddressablesFacade.Instance`/`Simple.Load`/`Simple.TryLoad`/`Simple.Spawn`(×3)/`Simple.Pool`/
`Simple.Recycle`/`Simple.ClearAll`/`Simple.IsLoaded`(×2)/`Simple.GetStats`. See "Implementation
status (this pass)" below §4 for exactly what changed and what is still open. Everything else this
document describes — §3.0's `AddressableRuntime.MainThreadId`/`IsMainThread` wiring (step 2), the
remaining three §3.6 shutdown-guard sites (`UnityMainThreadDispatcher.Instance`,
`SceneAssetScope.GetOrCreate(Scene)`, `HybridScope`'s singleton getters), step 7 (collapsing
`HybridScope` onto `ScopeManager`), and step 8 (borrowers stop caching) — is still design, not yet
implemented.

Supersedes the "needs a human" markers in `HANDOFF_TO_SESSION_B.md` A-2, A-6, A-7, A-12 — each
of those is decided below, with the evidence that decided it.

Scope of this document: who owns an `AssetLoader`, who may dispose one, and what happens to every
storage in the package at play-mode exit, domain reload (both settings), application quit, and when
a scope dies under a caller still holding handles.

---

## 1. The one rule

> **A loader belongs to exactly one owner — the object whose lifetime it copies. The owner creates
> it, the owner disposes it, and the owner disposes it only from its own teardown callback.
> Everyone else *borrows*: a borrower re-resolves the loader from its owner on every use, never
> caches it, never disposes it, and releases only the handles it personally took.**

Three corollaries, each of which resolves a specific open item:

**1a. Teardown is identity-guarded.** A teardown path clears a shared field only if that field still
points at *me*. This is `InputManager.UninstallGlobals`
(`Library/PackageCache/com.unity.inputsystem@7a4e1a2a8194/InputSystem/Runtime/InputManager.cs:2309-2341`),
which does `if (ReferenceEquals(InputRuntime.s_Instance, m_Runtime)) InputRuntime.s_Instance = null;`
for exactly this reason. The package already does the right thing in two places
(`AddressablesFacade.cs:362`, `GlobalAssetScope.cs:63`) and must do it everywhere.

**1b. No state is terminal.** Every object reachable from a public API must, after any legal
sequence of calls, be in a state that some call can leave. A-2 is not "Dispose does the wrong
thing" — it is that `GlobalAssetScope` after `Dispose()` sits in a state (`_scope != null`,
`_scope._loader == null`) that no method exits, so `Loader` returns `null` for the rest of the
process (`GlobalAssetScope.cs:15` reading `BaseAssetScope.cs:107`).

**1c. A borrower that caches is a second owner in disguise.** `AddressablesFacade._sessionLoader`
(`AddressablesFacade.cs:33`) is the proof: any caller of `ScopeManager.Instance.ClearScope("Session")`
(`ScopeManager.cs:83-100`) disposes the loader the Facade still points at, and the Facade never
learns. `IsSessionActive()` then returns `true` off a corpse (`:340`), `GetSessionLoader()` returns
it (`:331`), `LoadSessionAsync` reuses it because its only guard is `_sessionLoader == null` (`:147`),
and every session load silently returns `null` — `AssetLoader` logs `Cannot load from disposed
loader` and returns `null` rather than throwing (`AssetLoader.cs:461-465`).

### Why this rule and not "one big manager owns everything"

Because Unity destroys `MonoBehaviour`s in an order the package does not control, and the two
process-wide singletons here (`AddressablesFacade`, `GlobalAssetScope`) are both `DontDestroyOnLoad`
(`AddressablesFacade.cs:49,64`; `GlobalAssetScope.cs:26,43`). A design that needs a teardown
*sequence* is a design that breaks whenever Unity picks the other order. The rule above makes order
irrelevant instead of prescribing one — the same trade Unity's own Input System took, and stated:
the identity guards "exist because the ordering is genuinely not guaranteed", choosing "make
out-of-order teardown harmless" over "make ordering deterministic".

It is also what the studio's kit already does. `SingletonMonoBehaviour<T>.OnDestroy` is the only
place that nulls the static, and ~20 V2 managers override it to unsubscribe what they subscribed —
`MLAppFlowManager` even records the reason in a comment ("missing this leaves a dead manager
dispatching into a destroyed instance after a domain reload"). No manager in that kit disposes
another manager. Per requirement 6 (fit), the package follows that convention.

---

## 2. The state machine

### 2.1 Loader states

An `AssetLoader` has three states, all already implemented; this design only names them and makes
the transitions total.

| State | `_disposed` | Cache | Loads | How you get here |
|---|---|---|---|---|
| **Live** | false | may hold entries | succeed | constructed (`AssetLoader.cs:101-113`) |
| **Emptied** | false | cleared | succeed (re-fetch) | `ClearCache()` (`:1637-1657`) or `ReleaseAsset()` (`:1825-1833`) |
| **Dead** | true | cleared | refused, logged, `null` returned (`:461-465`) | `Dispose()` (`:1889-1928`) |

`Emptied` is not a pause: `ClearCache` force-releases unconditionally (`:1642-1646`), so a caller
still holding an `IAssetHandle` sees `IsValid == false` afterwards. That is deliberate and
documented (`AssetLoader.cs:30-45`, `:1619-1634`) and this design does not change it.

`Dead` is terminal **for the loader**. It is not terminal for the *scope*, which is the whole point
of §2.2.

### 2.2 Scope states

| State | Meaning | Exits to |
|---|---|---|
| **Absent** | no scope object exists | `Live` (owner's getter / `Awake` / `AddTo`) |
| **Live** | owner alive, loader Live or Emptied | `Emptied`, `Dead` |
| **Emptied** | owner alive, loader Emptied — `Deactivate()`/`ClearCache()` | `Live` (next load) |
| **Dead** | loader disposed **and** the owner's reference to it nulled | `Absent` (owner destroyed) or `Live` (singleton owner rebuilds — §3.6) |

**The illegal fifth state, which is A-2:** owner alive, `_scope` non-null, `_scope._loader` null.
`GlobalAssetScope.Dispose()` (`:56-59`) reaches it today because `BaseAssetScope.Dispose` ends with
`_loader = null` (`BaseAssetScope.cs:107`) while `GlobalAssetScope._scope` keeps pointing at the
husk. `ScopeName` still answers `"Global"` (`:14`), `IsActive` answers `false` (`:16`) — it reads as
"switched off", not "destroyed". The transition table must not contain it.

`HybridScope` has the same shape with a louder symptom: `Deactivate()` is literally `Dispose()`
(`HybridScope.cs:293-296`), which sets `_disposed` but leaves `_globalInstance` pointing at the
disposed object (`:340-348`), and the getter only rebuilds on `== null` (`:55`) — so the next
`Advanced.GetGlobalScope().Loader` throws `ObjectDisposedException` (`:253-254`) forever. Two
methods away, `ClearSessionSingleton` does it correctly: dispose *and* null (`:194-195`).

### 2.3 The transition rules, as code the implementer writes

```
Dispose()                    → tear down the loader, THEN null the owner's reference to the scope.
                               Never one without the other. (fixes A-2, A-6b)

Deactivate()                 → ClearCache() only. The scope stays usable.
                               (HybridScope.Deactivate stops aliasing Dispose.)

<singleton>.Instance getter  → if the backing static is null AND we are not shutting down,
                               rebuild. Otherwise return what exists (possibly null). (§3.6)

<singleton>.HasInstance      → true iff an instance exists and we are not shutting down.
```

---

## 3. Each lifecycle event

A single new type carries the cross-cutting state. Everything in this section refers to it.

### 3.0 `Runtime/Core/AddressableRuntime.cs` — new, ~40 lines

```csharp
public static class AddressableRuntime
{
    public static bool IsShuttingDown { get; private set; }
    public static int  MainThreadId  { get; private set; }
    public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init()
    {
        IsShuttingDown = false;                 // ← see the warning below
        MainThreadId   = Thread.CurrentThread.ManagedThreadId;

        Application.quitting -= OnQuitting;     // ← load-bearing, not cosmetic
        Application.quitting += OnQuitting;
    }

    private static void OnQuitting() => IsShuttingDown = true;
}
```

Three details, each of which is a real bug if dropped:

- **`IsShuttingDown = false` on every `Init()`.** With *Enter Play Mode Options → domain reload
  disabled*, the static survives the previous session's `Application.quitting`. Without this line
  the second Play session begins with `IsShuttingDown == true`, every singleton getter refuses to
  create, and the package is inert with no error. This is the highest-risk line in the design and
  it needs its own test (§7).

- **`-=` before `+=`.** `Application.quitting` is an engine-side static event; with domain reload
  disabled its invocation list is *not* cleared between sessions, so a bare `+=` accumulates one
  handler per Play. The repo already uses this dance at `Editor/Cdn/LocalContentServer.cs:56-57`;
  Unity uses it at `.../com.unity.inputsystem@.../InputSystem/Editor/InputSystemEditorInitializer.cs:147-149`
  and `.../com.unity.addressables@.../Runtime/AssetReference.cs:437-438`.

- **`Application.quitting`, not `EditorApplication.playModeStateChanged`.** Unity raises
  `Application.quitting` both when a player exits and when the editor leaves Play mode, so one
  runtime subscription covers both and no `#if UNITY_EDITOR` island enters `Runtime/`. This is the
  Input System's `gameIsPlaying` lesson (`InputManager.cs:533-538`): collapse the conditional to one
  property that everything else reads.

**`Init()` must stay synchronous and must not `await`.** `SubsystemRegistration` fires *before*
UniTask's `AfterAssembliesLoaded` `Init()`
(`Library/PackageCache/com.cysharp.unitask@dc216dc4183d/Runtime/PlayerLoopHelper.cs:288-293`), so a
`#if UNITASK_PRESENT` await issued from any of the package's five `SubsystemRegistration` hooks hits
UniTask before `runners` exists and gets a bare `NullReferenceException` — its own guard checks
`runners[(int)timing]`, not `runners` (`PlayerLoopHelper.cs:492-515`). Write that as a comment in
the file so the next person does not add one.

### 3.1 Play mode exit (editor)

Order of events, and what each does under this design:

1. `Application.quitting` → `AddressableRuntime.IsShuttingDown = true`. **No getter creates anything
   from here on.**
2. Unity destroys scene objects and `DontDestroyOnLoad` objects in an order the package does not
   control.
3. `AddressablesFacade.OnDestroy` (`:353-389`) — already correctly gated on `if (_instance == this)`
   (`:362`). It disposes `_poolManager` (`:367`), ends the session (`:371`), drops its reference to
   the global scope without disposing it (`:386`), and nulls `_instance` (`:387`).
4. `GlobalAssetScope.OnDestroy` (`:61-68`) — gated on `_instance == this` (`:63`); disposes its own
   scope and nulls the static.
5. `SceneAssetScope.OnDestroy` → `Dispose` (`:79-89`) unsubscribes `sceneUnloaded` (`:83`) then
   disposes. `HierarchyAssetScope.OnDestroy` → `Dispose` (`:85-89`).
6. `UnityMainThreadDispatcher.OnDestroy` (`:149-155`) — gated, nulls the static.

**Steps 3–6 may occur in any order.** That is safe because:

- `AssetLoader.Dispose` sets `_disposed = true` before doing anything (`:1908`) and returns
  immediately on re-entry (`:1891`), so a late second dispose is a no-op.
- `ForceRelease` is idempotent (`AssetLoader.cs:1800-1801`), so `AddressablePoolManager.ClearAllPools`
  (`:467-484`) disposing template handles after the loader already tore down is harmless.
- The Facade no longer disposes `GlobalAssetScope` (`:386`), so "who runs first" no longer decides
  whether `Simple.*` works for the rest of the process.

**Pending awaiters are drained, not abandoned.** `AssetLoader.Dispose` completes every joined
in-flight load with `null` before tearing down (`:1912-1917`). That is the drain-then-clear order
UniTask uses at play-mode boundaries (`PlayerLoopHelper.cs:202-222`: `runner.Run(); runner.Clear();`)
and it is the reason a `Standard.LoadGlobal` awaited across Stop resolves instead of hanging. Keep
it; do not reorder it.

### 3.2 Domain reload **enabled** (Unity default; both studio projects ship this way —
`ucm/ProjectSettings/EditorSettings.asset:27-28`, `Icon Match/.../EditorSettings.asset:27-28`)

Nothing survives. Every static is re-zeroed by the reload itself; every `SubsystemRegistration` hook
re-runs against already-blank state and is a no-op. **No hook needs an
`EditorSettings.enterPlayModeOptionsEnabled` gate.**

That is a deliberate departure from the Input System, which *does* gate two of its resets
(`InputActionState.cs:4506-4519`, `PlayerInput.cs:1372-1386`). The reason it gates: those resets are
*destructive* when a real reload already happened — the system was just re-initialised by static
constructors and re-zeroing would undo it. None of this package's resets are destructive: they null
a field, clear a dictionary, or re-latch a thread id. Running them twice costs nothing. Getting the
gate backwards, on the other hand, produces a bug that reproduces under exactly one Enter Play Mode
setting. Do not add the gate; record the reason where a reviewer will look for it.

### 3.3 Domain reload **disabled** (Enter Play Mode Options)

Every mutable static needs a named owner for its reset. The audit, and what changes:

| Static | File:line | Reset today | After |
|---|---|---|---|
| `ScopeManager._instance` + `_loaders` | `ScopeManager.cs:27,30` | `ResetOnLoad` `:170-179` | keep; stop swallowing (§5) |
| `AssetLoader._mainThreadId` | `AssetLoader.cs:79` | `CaptureMainThread` `:91-95` | delegate to `AddressableRuntime` |
| `AssetMonitorBridge._monitors` | `AssetMonitorBridge.cs:17` | `ResetOnLoad` `:118-121` | keep as-is |
| `CdnRequestDecorator` install flags | `CdnRequestDecorator.cs:157-163` | `ResetStatics` | keep as-is |
| `HybridScope._globalInstance/_sessionInstance/_namedInstances` | `HybridScope.cs:36-38` | **none** | add `ResetOnLoad` → `ClearAll()` (§5) |
| `AssetLoaderRegistry.Loaders` | `AssetLoaderRegistry.cs:55` | **none** | add `ResetOnLoad` → clear list |
| `UnityMainThreadDispatcher._mainThreadId` | `UnityMainThreadDispatcher.cs:17` | **none** (written only in `Awake`, `:55`) | delegate to `AddressableRuntime` |
| `TieredAssetLoader._mainThreadId` | `TieredAssetLoader.cs:32,45-48` | **none** (latched by whichever thread built the first instance) | delegate to `AddressableRuntime` |
| `CdnManager._settings/_catalog/_downloads/_cache/...` | `CdnManager.cs:52-57` | `Reset()` exists `:282-292`, **nothing calls it on load** | add `ResetOnLoad` → `Reset()` |
| `GlobalAssetScope._instance`, `AddressablesFacade._instance`, `UnityMainThreadDispatcher._instance` | `:11`, `:24`, `:14` | Unity fake-null heals the getter | document, no code |
| `Simple._facade`, `Standard._facade` | `SimpleAPI.cs:23`, `StandardAPI.cs:26` | fake-null heals | document, no code |

`AddressableRuntime.IsShuttingDown` joins the top group (§3.0).

**The two main-thread latches are currently contradictory and must become one.**
`AssetLoader.IsMainThread` treats an unlatched `0` as *allow* (`:122-124`);
`UnityMainThreadDispatcher.IsMainThread` treats it as *deny* (`:22`) and is only ever written from
`Awake` (`:55`), so `Advanced.IsMainThread()` (`AdvancedAPI.cs:289-291`) returns `false` on the real
main thread until some code forces the dispatcher GameObject into existence. UniTask latches its
equivalent in `Init()` above the early-return precisely so this window does not exist
(`PlayerLoopHelper.cs:294-302` vs the guard at `:309`). One latch, in `AddressableRuntime`, read by
all three. `AssetLoader` keeps its constructor `CompareExchange` fallback (`:105-106`) for edit-mode
tooling where no `RuntimeInitializeOnLoadMethod` runs — move that fallback into `AddressableRuntime`
so it is also one line in one place.

### 3.4 Application quit (player)

Identical to §3.1: `Application.quitting` fires, then `OnDestroy` runs on the `DontDestroyOnLoad`
objects. The flag is what makes it different from a normal scene teardown — see §3.6.

**Do asset releases at quit even matter?** On a platform where the process exits, no. They matter in
two cases the package must not assume away: the editor with domain reload disabled (the process does
*not* exit, and an unreleased handle persists into the next Play session), and mobile restart paths
where the app is re-entered without a fresh process. So: **run normal teardown at quit; do not
special-case it.** One caveat to verify by running rather than by reading (CLAUDE.md tier 3): whether
`Addressables.Release` / `ReleaseInstance` are safe after Addressables' own
`ResourceManagerCallbacks` GameObject has been destroyed. Unity's `ComponentSingleton` destroys it
from an editor-only `PlayModeChanged` handler
(`.../com.unity.addressables@.../Runtime/ResourceManager/Util/ComponentSingleton.cs:112-122`), which
implies destruction can precede our `OnDestroy`. If a release throws there, wrap `TearDownAll`'s
loop body in a try/catch that logs — but measure first, do not add the catch speculatively.

### 3.5 A scope disposed while a caller still holds handles

**Semantics, unchanged and now stated as contract:**

- The handle reports `IsValid == false`. The counter is what `IsValid` reads
  (`AssetLoader.cs:1631-1634`), so the failure mode is a visibly dead handle, not a live-looking one
  pointing at freed memory.
- `handle.Dispose()` afterwards is a no-op (idempotent `ForceRelease`, `:1800-1801`).
- A `LoadAssetAsync` awaited across the disposal completes with `null` rather than hanging
  (`:1912-1917`).

**What a caller should do instead of checking `IsValid` everywhere:** pass the scope's cancellation
token. `BaseAssetScope.DisposedToken` already exists (`:52`) and already fires at the top of
`Dispose` (`:101`). Today nothing consumes it, because `AssetLoader.LoadAssetAsync` has no
`CancellationToken` overload — `AssetLoader.cs:416` records that this "has to land when the CDN
layer's tokens are threaded through". **This design does not add that overload** (it is a
cross-cutting async change, and adding it half-way is worse than not having it), but it does two
cheap things now:

1. Document `DisposedToken` as the supported way to unwind a long await when the owning GameObject
   dies mid-load, with an example in the scope XML docs.
2. Record the overload as the *only* remaining gap in this model, so the CDN token work picks it up
   rather than re-deriving it.

**What is explicitly not adopted:** waiting for outstanding handles before tearing down. Unity's own
Addressables has no such mechanism — `AddressablesImpl` has no `Dispose` at all, and
`ResourceManager.Dispose` only unsubscribes an update delegate and has zero production callers
(`.../ResourceManager/ResourceManager.cs:1127-1134`). A teardown that blocks on refcounts is a
teardown that can deadlock at quit; a teardown that force-releases cannot.

### 3.6 Resurrection during shutdown — the mechanism, precisely

**The problem, concretely.** `Simple.Destroy` reaches `GlobalAssetScope.Instance.Loader.ReleaseInstance(instance)`
(`SimpleAPI.cs:142`). `GlobalAssetScope.Instance` (`:18-30`) creates a `GameObject` and calls
`DontDestroyOnLoad` whenever `_instance == null` — with no shutdown guard. A pooled object destroyed
from another object's `OnDestroy` during quit therefore constructs a fresh `DontDestroyOnLoad`
GameObject in the middle of teardown, which Unity then reports as leaked. The same shape exists at
`AddressablesFacade.cs:45-50` and `UnityMainThreadDispatcher.cs:31-40`, and
`UnityMainThreadDispatcher.Enqueue` touches `Instance` unconditionally at `:101`.

**The mechanism.** `AddressableRuntime.IsShuttingDown` (§3.0), consulted in exactly five places:

| Site | File:line | Change |
|---|---|---|
| `GlobalAssetScope.Instance` | `GlobalAssetScope.cs:18-30` | do not create when shutting down |
| `AddressablesFacade.Instance` | `AddressablesFacade.cs:41-53` | same |
| `UnityMainThreadDispatcher.Instance` | `UnityMainThreadDispatcher.cs:27-44` | same |
| `SceneAssetScope.GetOrCreate(Scene)` | `SceneAssetScope.cs:134-167` | return the found scope; do not `CreateForScene` when shutting down |
| `HybridScope.Global` / `.Session` / `.GetNamed` | `HybridScope.cs:49-63, 68-82, 89-112` | do not construct a new scope when shutting down |

Plus one companion per singleton, matching the studio's own idiom
(`Icon Match/.../Singleton/SingletonMonoBehaviour.cs` exposes `HasInstance`, and the bridge layer
uses it as the "was the app booted?" guard):

```csharp
public static bool HasInstance => _instance != null && !AddressableRuntime.IsShuttingDown;
```

**And one call-site rule**, which is what actually fixes `Simple.Destroy`:

```csharp
public static void Destroy(GameObject instance)
{
    if (instance == null) return;

    // Ask before touching Instance: during shutdown the getter must not build a GameObject,
    // and Addressables is going away with the process anyway.
    if (GlobalAssetScope.HasInstance &&
        GlobalAssetScope.Instance.Loader.ReleaseInstance(instance)) return;

    UnityEngine.Object.Destroy(instance);
}
```

Correct in all three states: no scope ever existed → plain `Destroy` (nothing to give back); scope
alive → released through the same loader `Spawn` used (`SimpleAPI.cs:107,116,125`); shutting down →
plain `Destroy`.

**On the getter returning `null` and repo invariant 4 (no sentinel values).** A reviewer will raise
this. The answer: `Instance` returning `null` here encodes exactly one fact — "no instance exists and
none may be created" — not two facts collapsed into one value, which is what invariant 4 forbids
(the `GetDownloadSizeAsync` case, where `0` meant both *nothing to download* and *could not find
out*). `HasInstance` is the non-null way to ask the same question, and every internal call site uses
it. Throwing from a getter during teardown was considered and rejected: it converts a shutdown-order
problem into an exception storm inside `OnDestroy`, where nothing can handle it.

**Why we add this when three shipping packages do not.** Addressables has no quit guard anywhere —
`grep -rni "quitting"` over the whole package returns zero hits — and its
`ComponentSingleton<T>.Instance` creates unconditionally on null
(`.../ResourceManager/Util/ComponentSingleton.cs:23-34`). It gets away with it because its quit-time
surface is one method, and that method checks `Exists` rather than `Instance`
(`DelayedActionManager.cs:195-199`). UniTask avoids the class of bug entirely by owning no
GameObject at all. The studio kit has no quit hook either. This package has **three**
lazy-create-GameObject singletons plus a pooling layer whose `Despawn`/`Destroy` paths are called
from gameplay `OnDestroy` — the exact resurrection case. Omitting the guard is cheap; the resulting
bug is intermittent and unreproducible, which is the most expensive kind.

---

## 4. What a developer writes

**The success criterion for this design is that none of these lines change.** A lifetime rework that
shows up at the call site will be worked around.

### Boot — one line in the studio's `Systems.cs` init list

```csharp
// Systems.cs initSteps, after RemoteConfigManager, before AudioManager / ShopManager / UI:
() => CdnManager.InitializeAsync(environmentId),
```

Placement reasoning: after remote config so a flag can pick the catalog environment, before the
first consumers of remotely-hosted content. The list is the studio's existing ordering mechanism
(`Icon Match/.../Initializer/SceneScripts/Systems.cs:37-73`) and the loading bar derives from it, so
one inserted lambda also gets progress for free.

Nothing else is needed at boot. Scopes are lazy by design; `CdnBootExample`
(`Samples~/CdnBoot/CdnBootExample.cs`) remains the canonical sequence.

### Load — unchanged

```csharp
var sprite  = await Simple.Load<Sprite>("UI/Icon");                 // returns the asset
using var h = await Standard.LoadGlobal<Sprite>("UI/Icon");         // returns a handle
var cfg     = await Assets.LoadSession<TextAsset>("Config/Level");  // session-scoped
```

### Scene change — unchanged

```csharp
Assets.StartSession();          // gameplay begins
// ... load, spawn, play ...
Assets.EndSession();            // gameplay ends; every session asset released
```

Per-scene assets need no call at all: drop a `SceneAssetScope` component in the scene (or
`Assets.GetOrCreateSceneScope()`), and its `OnSceneUnloaded` → `Destroy` → `OnDestroy` → `Dispose`
chain (`SceneAssetScope.cs:68-74, 79-89`) releases them. Per-object assets: `HierarchyAssetScope.AddTo(go)`
(`:102-133`), released when the GameObject dies.

### Teardown — unchanged, and mostly nothing

```csharp
// In a MonoBehaviour's OnDestroy: nothing. Scopes clean up after themselves.
// The one exception, for code that may run during application quit:
if (GlobalAssetScope.HasInstance) { /* release something */ }
```

### Recommended (not required) setup, for studio fit

Place `AddressablesFacade` and `GlobalAssetScope` on a `[Manager] Addressables` GameObject in the
`Systems` scene, next to `[Manager] Save` / `[Manager] Ads`
(`Icon Match/.../MLCore/Scenes/Systems.unity`). Then the lazy-create branch in each `Instance`
getter becomes the fallback for "someone opened a gameplay scene directly" — which is the studio's
documented normal workflow, the one `PlayFromBootToggle` exists to opt out of — rather than the
primary path. Zero API change; the getters still work when the scene is absent.

---

## Implementation status (this pass)

Scoped to A-2, A-6, A-7, A-12, plus the §3.6 shutdown-resurrection guard for
`Simple.Destroy`/`GlobalAssetScope.Instance` specifically (not the other four §3.6 sites — see
below). Compile gate (`bash Tools/CompileGate/run.sh`) passes both assemblies after every item
below.

A follow-up review-fix pass (same worktree) then closed five gaps a review found in the above —
see "Landed in the review-fix follow-up pass" below. It also lands the `AddressablesFacade.Instance`
§3.6 site, so only three of the original five §3.6 sites remain open.

**Landed:**

- **§3.0 / `Runtime/Core/AddressableRuntime.cs` (new).** `IsShuttingDown`, `MainThreadId`,
  `IsMainThread`, one `SubsystemRegistration` hook, exactly as specified — including the
  `IsShuttingDown = false` reset and the `-=`-before-`+=` dance. `MainThreadId`/`IsMainThread`
  are not yet consulted anywhere (step 2, main-thread-latch consolidation across `AssetLoader` /
  `UnityMainThreadDispatcher` / `TieredAssetLoader`, was out of scope for this pass and is still
  open).
- **A-2 (§5 step 4) — `GlobalAssetScope.cs`.** Private `Scope` getter rebuilds after `Dispose()`
  instead of leaving the husk; `Dispose()` nulls `_scope`. `Instance` getter and new
  `HasInstance` gate on `AddressableRuntime.IsShuttingDown` (this is also the §3.6 shutdown
  guard for this specific singleton).
- **§3.6 call-site rule — `SimpleAPI.Destroy`.** Checks `GlobalAssetScope.HasInstance` before
  touching `Instance`, per the design's exact snippet.
- **A-6 (§5 step 5) — `HybridScope.cs`.** `ResetOnLoad` (`SubsystemRegistration`) added;
  `Deactivate()` now calls `ClearCache()` instead of `Dispose()`; `Global`/`Session`/`GetNamed`
  treat a disposed instance as absent instead of handing back a husk.
- **A-7 (§5 step 6, registration half) — `Managers/ScopeManager.cs`, `Scopes/BaseAssetScope.cs`.**
  `ScopeManager` is now the directory: a `Registration` struct tags each entry manager-owned or
  foreign; `RegisterExternal`/`UnregisterExternal` (internal) let a scope register itself without
  handing this manager disposal rights; `ClearScope`/`ClearAllExcept`/`ClearAll` only
  dispose-and-remove manager-owned entries, and only `ClearCache()` foreign ones.
  `BaseAssetScope`'s ctor/`Dispose` call `RegisterExternal`/`UnregisterExternal` beside the
  existing `AssetMonitorBridge` calls, so Global/Scene/Hierarchy scopes are visible in the
  directory for the first time and `ClearAllExceptGlobal()` finally protects something real.
  Went with option (i) from A-7's "decision needed" callout, as the design instructs.
- **Open question 4, resolved as recommended** — `ClearScope` on a foreign entry stays `void`,
  logs an error naming the owner's type, and `IsManagerOwned(string)` is now public so a caller
  can check first. See §8 item 4 for detail.
- **A-12 (§5 step 6, naming half) — `HybridScope.cs`, `API/AdvancedAPI.cs`, `Facade/AddressablesFacade.cs`
  docs.** `HybridScope` self-reports to monitoring *and* now registers with `ScopeManager`'s
  directory under `"Hybrid:"`-prefixed ids (`"Hybrid:Global"`, `"Hybrid:Session"`,
  `"Hybrid:{type}:{name}"`), collapsed into the constructor/`Dispose()` instead of being scattered
  across every call site. `Advanced.GetHybridGlobalScope()`/`GetHybridSessionScope()` added;
  `Advanced.GetGlobalScope()`/`GetSessionScope()` marked `[Obsolete(..., false)]` naming the
  replacements. Storage map documented on `GlobalAssetScope`, `HybridScope`, `ScopeManager`,
  `AdvancedAPI`'s Hybrid Scopes region, and `AddressablesFacade.GetGlobalScope`/`GetSessionLoader`.
  **Not done: the storage collapse itself (step 7)** — deliberately; step 7 is documented above as
  separable, and A-12's own minimal fix explicitly says not to merge storage in 4.x.
- **One defensive addition beyond the literal design snippets (superseded — see below):**
  `RegisterExternal` originally refused (and logged) a foreign registration that would silently
  overwrite an existing *manager-owned* entry under the same id. The review-fix pass generalized
  this to a full identity guard; see the next section.

**Landed in the review-fix follow-up pass** (HANDOFF_TO_SESSION_B.md §4.3 review findings against
A-2/A-6/A-7/A-12 above):

- **`RegisterExternal`/`UnregisterExternal` are now identity-guarded (§1a) —
  `Managers/ScopeManager.cs`, `Scopes/BaseAssetScope.cs`, `Scopes/HybridScope.cs`.**
  `UnregisterExternal` now takes the caller's own `AssetLoader` and only removes the directory
  entry if that exact loader is still the one registered under the id
  (`ReferenceEquals(reg.Loader, loader)`, mirroring `InputManager.UninstallGlobals`, cited at line
  32 above). `RegisterExternal` refuses (and logs) overwriting an existing *foreign* entry whose
  loader differs from the one registering, not just manager-owned collisions. Without this, two
  foreign owners sharing an id — reachable through ordinary misuse, e.g. a duplicate
  `[GlobalAssetScope]` rejected in `Awake()` but still touched before its deferred `Destroy()`
  runs, or two `HierarchyAssetScope`/`SceneAssetScope` instances sharing a caller-chosen
  `customScopeId` briefly alive together across a respawn — could silently corrupt the directory:
  the *second* registrant's entry would win, and the *first* owner's disposal would then remove
  whatever was currently in the slot (possibly the second owner's own live entry), regardless of
  whether it still owned it.
- **`ScopeManager.GetOrCreateScope` refuses `"Global"` — `Managers/ScopeManager.cs`.** `"Global"`
  is reserved (`ReservedGlobalScopeId`) for `GlobalAssetScope`'s own foreign registration. Before
  this, `GetOrCreateScope("Global")` — exactly what `EDITOR_TOOLS_GUIDE.md`'s "Multi-session
  sketch" and "RPG-style segmentation" samples taught — raced GlobalAssetScope's registration:
  whichever ran first either silently blocked the other (loud but unhelpful error naming the
  private `InternalScope` wrapper) or handed the developer's "independent" scope back
  GlobalAssetScope's own loader (aliasing, no error at all). `GetOrCreateScope("Global")` now
  always refuses with an actionable error; the two doc samples were updated to use `"AppGlobal"`
  instead (`Packages/com.game.addressables/EDITOR_TOOLS_GUIDE.md`).
- **`AddressablePoolManager` gets its own dedicated loader — `Facade/AddressablesFacade.cs`.**
  `Initialize()` no longer constructs the Facade's pool manager over `GlobalAssetScope`'s loader;
  it builds a private `_poolLoader` (a plain, unregistered `AssetLoader`) instead, disposed
  alongside `_poolManager` in `OnDestroy()`. Reason: A-7 makes `"Global"` a real foreign directory
  entry, so `ScopeManager.ClearAll()`/`ClearAllExcept(...)` (any call that doesn't name `"Global"`
  in its keep-list) — and the pre-existing `Simple.ClearAll()`/`Standard.ClearGlobalCache()` —
  call `ClearCache()` on it, which force-releases every cached handle unconditionally. Before this
  fix that included the pool manager's own `_templateHandles`, captured into pool factory closures
  the pool manager believes it exclusively owns; force-releasing them out from under it could hand
  a `dynamicPool.Get()` a dead prefab reference with no error anywhere. A private loader nothing
  else ever registers or clears removes the collision structurally rather than special-casing
  `ClearCache()`'s foreign-entry behavior (which stays exactly as step 6 specifies).
- **`AddressablesFacade.Instance` gains the §3.6 shutdown guard —
  `Facade/AddressablesFacade.cs`.** Same `IsShuttingDown` guard as `GlobalAssetScope.Instance`,
  plus a `HasInstance` companion. `Initialize()` also now tolerates `GlobalAssetScope.Instance`
  returning null (logs and skips pool-manager setup) instead of assuming it can't. This closes the
  gap the "Deliberately not touched" list below used to name explicitly for this site.
- **`SimpleAPI` call sites gated against both now-nullable singletons — `API/SimpleAPI.cs`.**
  `Load<T>`, `TryLoad<T>`, and all three `Spawn` overloads null-check `GlobalAssetScope.Instance`'s
  own return (matching invariant 4 — `default`/`null` was already each method's documented failure
  value) rather than a `HasInstance` pre-check: a `HasInstance`-first guard, applied literally,
  would also refuse the *very first* call ever made in the process, before any instance has been
  built — `HasInstance` requires an instance to already exist, so it cannot substitute for calling
  the lazy-create getter itself in a path that is supposed to create on demand. `Pool`, `Recycle`,
  `ClearAll`, `IsLoaded`×2, and `GetStats` were similarly null-checked against
  `AddressablesFacade.Instance` now that it can return null too.

**Deliberately not touched, still open (in scope of a future pass, not this one):**

- Step 2 (main-thread latch consolidation) — `AssetLoader.cs`, `UnityMainThreadDispatcher.cs`,
  `TieredAssetLoader.cs` untouched.
- The remaining three §3.6 shutdown-guard sites — `UnityMainThreadDispatcher.Instance`,
  `SceneAssetScope.GetOrCreate(Scene)`, `HybridScope`'s singleton getters.
  `AddressableRuntime.IsShuttingDown` is ready for all three; none read it yet.
  (`AddressablesFacade.Instance`, the fourth original site, landed in the review-fix pass above.)
  Also still open: comprehensively gating `StandardAPI.cs`'s ~30 `AddressablesFacade`-touching call
  sites and `Facade/Assets.cs`'s the same way `SimpleAPI.cs` now is — the review-fix pass scoped
  its `AddressablesFacade`-side guards to `SimpleAPI.cs`, the file the reviewing finding's own
  reachability evidence cited, rather than sweeping every call site in the package without
  dedicated review of each.
- Step 7 (collapse `HybridScope` onto `ScopeManager`) and open question 1 (whether to `[Obsolete]`
  `HybridScope` itself) — tied together, neither attempted.
- Step 8 (borrowers stop caching, e.g. `AddressablesFacade._sessionLoader`) — untouched.
- A-10 (`Standard.ClearCache(scopeName)` stub) — A-7 makes this implementable
  (`ScopeManager.Instance.GetScope(scopeName)?.ClearCache()`) but it was not wired; the stub is
  unchanged.
- Open questions 1, 2, 3, 5 — untouched, exactly as written in §8.

---

## 5. What changes, per file

Ordered so that a partial landing is still coherent. Steps 1–6 are one change; step 7 is the second
half and is required for this release but separable if it must slip.

### Step 1 — `Runtime/Core/AddressableRuntime.cs` (new)

As §3.0. `IsShuttingDown`, `MainThreadId`, `IsMainThread`, one `SubsystemRegistration` hook.
No dependencies on anything else in the package.

### Step 2 — the three main-thread latches become one

| File | Change |
|---|---|
| `Runtime/Loaders/AssetLoader.cs:79,91-95,105-106,118-125` | delete the private static + hook; `IsMainThread` reads `AddressableRuntime`. Keep the "unlatched ⇒ allow" semantics and its comment — it is correct for edit-mode tooling and is the *documented* behaviour. |
| `Runtime/Threading/UnityMainThreadDispatcher.cs:17,22,55` | delete `_mainThreadId`; `IsMainThread => AddressableRuntime.IsMainThread`. Fixes the false-negative window that `Advanced.IsMainThread()` currently inherits. |
| `Runtime/Loaders/TieredAssetLoader.cs:32,45-48,70-76` | same; removes the "latched by whichever thread built the first instance" hazard. |

### Step 3 — resurrection guard at the five creation sites

`GlobalAssetScope.cs:18-30`, `AddressablesFacade.cs:41-53`, `UnityMainThreadDispatcher.cs:27-44`,
`SceneAssetScope.cs:134-167`, `HybridScope.cs:49-63,68-82,89-112`. Each gains the
`IsShuttingDown` check plus a `HasInstance` companion (where a static instance exists).
`SimpleAPI.cs:132-146` gains the `HasInstance` call-site guard (§3.6).

### Step 4 — A-2: make `GlobalAssetScope` total

```csharp
private BaseAssetScope Scope
{
    get
    {
        if (_scope == null && !AddressableRuntime.IsShuttingDown && this != null)
        {
            _scope = new InternalScope("Global");
            _scope.Activate();
        }
        return _scope;
    }
}

public void Dispose()
{
    _scope?.Dispose();
    _scope = null;          // ← the missing half. Never leave the husk.
}
```

`Loader`, `ScopeName`, `IsActive`, `Activate`, `Deactivate` all route through `Scope`
(`GlobalAssetScope.cs:14-16, 46-54`). `Awake` (`:32-44`) keeps claiming the static and building the
scope eagerly; the getter is the recovery path, not the primary one.

**The A-2 decision, and why.** The handoff offered (a) rebuild-on-demand, or (b) stop letting the
Facade dispose a process-wide singleton. **Take (b) as the ownership answer *and* (a)'s first half as
the correctness fix.** They are not alternatives — (b) decides *who may dispose*, (a) decides *what
state Dispose leaves behind*, and the bug needs both answered.

- **(b) alone** — which is what `AddressablesFacade.cs:374-386` already implements — is incomplete:
  `Dispose()` is public (it is an `IAssetScope : IDisposable` member, `IAssetScope.cs:9`), so an
  inspector, a `using`, or game code can still reach the husk state. A rule enforced only by one
  call site is not a rule.
- **(a) alone** — Facade keeps disposing, GlobalAssetScope rebuilds — means every Facade restart
  silently drops every global asset every other system loaded. The symptom is "my UI atlas vanished
  when an additive scene loaded", which is exactly the class of bug nobody traces back to a
  lifetime decision.

Evidence that decides it toward (b):

1. `InputManager.UninstallGlobals` clears a global **only if it still points at itself**
   (`InputManager.cs:2309-2341`), and tracks asset ownership by a checkable marker (`hideFlags ==
   HideAndDontSave`, `:105-106`) so it destroys only what it created. The Facade did not create
   `GlobalAssetScope` — `GlobalAssetScope.Awake` did (`:40-41`) — so under that rule the Facade may
   not tear it down.
2. Unity's `Addressables` singleton is replaced from exactly one place, the property choke point
   (`.../com.unity.addressables@.../Runtime/Addressables.cs:566-584`), and `AddressablesImpl` exposes
   no `Dispose` at all. A process-wide cache root with many consumers has no safe external disposer;
   Unity's answer was to not provide one.
3. Studio fit: `SingletonMonoBehaviour<T>.OnDestroy` is the only place the static is cleared, and no
   manager in either kit disposes another manager.

Consequence of (b) that the reader should accept knowingly: **the global scope outlives Facade
restarts.** Assets loaded into it before a Facade teardown are still cached after the next one comes
up. That is the intended reading of "Global scope — persists throughout entire application lifetime"
(`GlobalAssetScope.cs:6`), and `Simple.ClearAll()` / `Standard.ClearGlobalCache()` remain the way to
empty it on purpose.

### Step 5 — A-6: `HybridScope` reset hook + `Deactivate` stops meaning `Dispose`

```csharp
// Matches ScopeManager.cs:170-179, comment included.
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
private static void ResetOnLoad()
{
    try { ClearAll(); }
    catch (Exception ex) { Debug.LogError($"[HybridScope] Reset failed: {ex}"); }
    lock (_lock) { _globalInstance = null; _sessionInstance = null; _namedInstances.Clear(); }
}
```

`ClearAll` (`:207-229`) already disposes and nulls both singletons and clears the dictionary, so the
hook is mostly delegation — but the explicit re-null after the catch is required, because a throw
partway through `ClearAll` must not leave a static pointing at a half-disposed scope.

`Deactivate()` (`:293-296`) changes from `Dispose()` to `ClearCache()`, matching
`BaseAssetScope.Deactivate` (`:84-93`) and making `IAssetScope.Deactivate` mean one thing across
implementations for the first time. This is a **behaviour change with no signature change**, so
invariant 6 holds; and the behaviour it replaces is the A-6(b) defect, so changing it is the fix
rather than a break.

Belt-and-braces for the case where someone still calls `Dispose()` directly on a singleton instance:
the `Global`/`Session` getters treat a disposed instance as absent — `if (_globalInstance == null ||
_globalInstance._disposed)`. That is the smaller of the two fixes the handoff proposed and it also
covers the named-instance case, where `ClearNamed` removes the entry (`:137-138`) but a direct
`Dispose` does not.

### Step 6 — A-7 and A-12: `ScopeManager` becomes the directory; monitoring ids stop colliding

**Decision on A-7: option (i), scopes register — with the ownership tag that makes it safe.**
Option (ii) (rename/`[Obsolete]` `ClearAllExceptGlobal` because it cannot do what it says) treats a
symptom. Today `ClearAllExceptGlobal` (`:122-125`) protects a key nothing ever creates — a
package-wide grep for `GetOrCreateScope("Global")` finds only the sample code in
`EDITOR_TOOLS_GUIDE.md:280,348` — so it is identical to `ClearAll()` in every reachable state, and
`Standard.ClearCache(scopeName)` (`StandardAPI.cs:268-272`) is a log-and-return stub because there is
nothing to look up. Registering fixes both.

The hazard the handoff correctly flags — a strong registry next to the weak `AssetLoaderRegistry`,
with two things able to dispose one loader — is answered by tagging ownership on the entry:

```csharp
private readonly struct Registration
{
    public readonly AssetLoader Loader;
    public readonly bool ManagerOwned;   // true only for loaders GetOrCreateScope built
}
```

| Method | Manager-owned entry | Foreign entry (scope-owned) |
|---|---|---|
| `GetScope` / `HasScope` / `ActiveScopes` | as today | now visible for the first time |
| `ClearScope(id)` (`:83-100`) | dispose + remove (unchanged; this is the `"Session"` path) | `loader.ClearCache()` only, plus an error log naming the owner — disposing it would violate §1 |
| `ClearAll` (`:130-148`) / `ClearAllExcept` (`:105-117`) | dispose + remove | `ClearCache` only, entry stays until its owner unregisters |
| `ResetOnLoad` (`:170-179`) | dispose + remove | dictionary cleared; the previous session's owners were already destroyed and already unregistered |

Registration and unregistration go **next to the existing monitoring calls**, so the two can never
diverge on the id:

- `BaseAssetScope` ctor, beside `AssetMonitorBridge.ReportScopeRegistered(_scopeId, false)`
  (`BaseAssetScope.cs:61`) → `ScopeManager.RegisterExternal(_scopeId, _loader, this)`.
- `BaseAssetScope.Dispose`, beside `AssetMonitorBridge.ReportScopeCleared(_scopeId)` (`:104`) →
  `ScopeManager.UnregisterExternal(_scopeId)`.

This makes `"Global"` a real key for the first time (`GlobalAssetScope.cs:41` →
`BaseAssetScope.cs:56-58`), so `ClearAllExceptGlobal` finally does what its name says and
`Standard.ClearCache(scopeName)` becomes implementable as
`ScopeManager.Instance.GetScope(scopeName)?.ClearCache()` — note `ClearCache`, **not** `ClearScope`:
the latter also disposes and removes, which is `EndSession` semantics.

Also in this step, `ScopeManager.ResetOnLoad` stops swallowing: `try { ClearAll(); } catch { }`
(`:175-176`) becomes a catch that logs the exception and the number of entries that survived.
UniTask's rule for the same situation — drain first, then discard, make the discard unconditional but
the failure *visible* — is the one to follow; a silent partial teardown leaves live Addressables
handles with no diagnostic.

**A-12, the naming half.** `AddressablesFacade.GetGlobalScope()` (`:318-321`, returns
`GlobalAssetScope`) and `Advanced.GetGlobalScope()` (`AdvancedAPI.cs:64-67`, returns `HybridScope`)
are two methods with one name returning two unrelated storages, and both self-report to monitoring as
`"Global"` (`BaseAssetScope.cs:61` vs `HybridScope.cs:58`); the same holds for `"Session"`
(`ScopeManager.cs:57` vs `HybridScope.cs:77`). Fix in three parts:

1. `HybridScope` reports `"Hybrid:Global"` / `"Hybrid:Session"` / `"Hybrid:{type}:{name}"` to
   monitoring and to the directory. One dashboard channel per real cache.
2. `Advanced.GetGlobalScope` / `GetSessionScope` gain siblings `GetHybridGlobalScope` /
   `GetHybridSessionScope`; the old names get `[Obsolete("... use GetHybridGlobalScope ...", false)]`
   naming the replacement, kept to 5.0.0 per invariant 6. The collision becomes visible at the call
   site, which is where it misleads.
3. The storage map goes into the XML docs of `GlobalAssetScope`, `ScopeManager`, `HybridScope` and
   `AdvancedAPI`, because the map is the thing a reader currently cannot derive.

### Step 7 — collapse storage D into B (required this release; separable)

After step 6 the package still has *three* scope mechanisms — `BaseAssetScope` (Global / Scene /
Hierarchy), `ScopeManager` (named entries), `HybridScope` — and `HybridScope` has no capability the
other two lack. Its only construction sites are `AdvancedAPI.cs:64-83`; a repo-wide grep finds no
other consumer in `Runtime/` or `Editor/`.

Rather than deleting it (a breaking change, forbidden until 5.0.0), **re-implement it over
`ScopeManager`**:

```csharp
// HybridScope keeps every public member; it stops owning a loader.
public AssetLoader Loader => ScopeManager.Instance.GetOrCreateScope(DirectoryId);
//                                                  DirectoryId = "Hybrid:Global" | "Hybrid:Session" | "Hybrid:{type}:{name}"

public void Deactivate() => Loader.ClearCache();
public void Dispose()    { ScopeManager.Instance.ClearScope(DirectoryId); /* manager-owned ⇒ allowed */ }
```

What this buys, in one move: storage D stops existing, so A-6(a) needs no separate reset hook (the
directory already has one at `ScopeManager.cs:170-179`), A-6(b) cannot recur (the static holds a
handle, not a loader), the monitoring collision is structurally impossible, and
`Advanced.GetGlobalScope().Loader.ClearCache()` finally clears something a reader can find.

What it costs, stated plainly: `HybridScope.Loader` changes from a `readonly` field
(`HybridScope.cs:41,243`) to a lookup, so it can return a *different* loader after a `ClearScope`.
That is self-healing rather than throwing `ObjectDisposedException` forever, which is strictly better
— but it *is* an observable semantics change for anyone who cached the loader, and it belongs in the
CHANGELOG under "Changed", not "Fixed".

If step 7 slips, step 5 keeps `HybridScope` correct in isolation and the release is still coherent —
just with one more storage than it needs.

### Step 8 — borrowers stop caching (§1c)

| File:line | Today | After |
|---|---|---|
| `AddressablesFacade.cs:33,118,133,147-150,331,340,348` | `_sessionLoader` field | delete the field; every path re-resolves `ScopeManager.Instance.GetScope("Session")` / `GetOrCreateScope`. Kills the stale-loader state (Q4-b) with no signature change. |
| `AddressablesFacade.cs:36,163-164` | `_sceneScope` field | delete; it is already write-then-return-only (`GetOrCreateSceneScope` re-resolves at `:163` on every call). |
| `AddressablesFacade.cs:27,72,255,296,318-321` | `_globalScope` field | replace with a private accessor over `GlobalAssetScope` that returns the instance without creating one during shutdown. `GetGlobalScope()` keeps its signature. |
| `AddressablesFacade.cs:39,75` | `_poolManager` holds a strong `AssetLoader` (`AddressablePoolManager.cs:21,34-40`) | keep — the pool manager is *owned* by the Facade and disposed by it (`:367`), so it is an owner, not a borrower. Its loader reference is fine because `ForceRelease` is idempotent. |

The load surfaces get one shared private accessor rather than ten null-checks:

```csharp
// StandardAPI / SimpleAPI
private static bool TryGetGlobalLoader(out AssetLoader loader) { ... }   // logs once, names the reason
```

Public methods that already return `default` on failure (`Simple.Load`, `SimpleAPI.cs:47`) keep
doing so — no new sentinel is introduced, because "load failed" is a signal those methods already
have. The `*Safe` surfaces (`Standard.LoadSafe`, `StandardAPI.cs:78-82`) return a proper failed
`LoadResult`, satisfying invariant 4 where a result type exists.

---

## 6. What is deliberately NOT changed, and why

| Not changed | Why |
|---|---|
| **Refcount-per-handle; no global teardown ceremony.** | Unity's own model. `AddressablesImpl` has no `Dispose`; `ResourceManager.Dispose` unsubscribes one delegate and has zero production callers (`.../ResourceManager/ResourceManager.cs:1127-1134`). Replacing it is a rewrite, not a lifetime fix. The studio also has no `IDisposable`/`Shutdown()` convention — its managers use `OnDestroy` — so inventing one here would read as foreign. |
| **`ClearCache` / `ReleaseAsset` force-release unconditionally** (`AssetLoader.cs:1637-1657`, `:1825-1833`) | Documented memory-pressure semantics (`:1619-1634`). A decrement would leave exactly the entries a caller under memory pressure wants gone. The historical justification (the `Simple.Load` leak) is now obsolete and the docstring already says so; the semantics stand on their own. |
| **`AssetLoaderRegistry` stays weak** (`AssetLoaderRegistry.cs:55`) | "A registry of live objects that keeps them alive is a leak with a nice name" (its own docstring, `:31-35`). Its job is catalog invalidation (`CatalogService.cs:436`), not lifetime. The directory in step 6 is the lifetime story; keeping them separate keeps neither one doing two jobs. |
| **`AssetLoader.Dispose` logs and returns off the main thread** (`:1899-1903`) | Deliberate, with the reasoning in the code: a throwing `Dispose` abandons teardown inside a `using`/`finally`, and carrying on would tear a `Dictionary` and mutate the `ResourceManager` silently. `ThreadSafeAssetLoader.Dispose` dispatches for exactly this reason (`ThreadSafeAssetLoader.cs:225-239`). |
| **`SceneAssetScope.GetOrCreate(Scene)`'s `#pragma` block** (`SceneAssetScope.cs:136-161`) | Byte-for-byte. It is the fix for the 4.1.0-pre.4 compile break on 2022.3/2023.x and the comment is the only thing preventing its reintroduction. Step 3 adds a guard *around* the create call, not inside that block. |
| **No `EditorSettings.enterPlayModeOptionsEnabled` gate on any reset hook** | §3.2. None of this package's resets are destructive when a real reload already happened, so the gate would add a second decision path whose failure mode reproduces under exactly one editor setting. |
| **No PlayerLoop injection** | UniTask already owns that surface and injects in edit mode too (`PlayerLoopHelper.cs:325-365`). A second injector competing for `PlayerLoop.SetPlayerLoop` is a real hazard for zero gain — the package's only per-frame need is `UnityMainThreadDispatcher.Update` (`:59-77`), which a hidden GameObject serves adequately. |
| **`Simple.Release<T>` stays a no-op** (`SimpleAPI.cs:198-205`) | It cannot work without an asset→handle reverse map that `AssetLoader` does not have. Out of scope here; the ownership rule says what the answer must be when it is addressed — either the reverse map, or `[Obsolete]` pointing at `Simple.ClearAll`. Do not "fix" it as part of this change. |
| **No `CancellationToken` overload on `LoadAssetAsync`** | §3.5. It lands with the CDN token threading (`AssetLoader.cs:416`), not here. Half of it is worse than none. |
| **`Assets.LoadScene<T>` / `Standard.LoadScene<T>` naming and active-scene binding** (A-8) | Real defect, but a naming/binding problem, not a lifetime one. Fixing it here would put two unrelated behaviour changes in one review. |

---

## 7. Verification this design owes

Per CLAUDE.md's three tiers — reading this document proves nothing.

1. **Compile** against the *oldest* supported editor via `Tools/check-min-unity-api.sh`, not the
   newest installed. This is what 4.1.0-pre.4 skipped.
2. **Run twice under both Enter Play Mode settings.** The matrix is 2×2: domain reload on/off ×
   run 1 / run 2. Specific assertions:
   - After Play → Stop → Play with domain reload **disabled**, `AddressableRuntime.IsShuttingDown`
     is `false` and `GlobalAssetScope.Instance` builds. (This is the §3.0 failure mode; without the
     `IsShuttingDown = false` line the package is inert with no error.)
   - `Application.quitting`'s handler count does not grow across Play sessions. (The `-=` before
     `+=`.)
   - `AssetLoaderRegistry.LiveCount` (`:88-98`) returns to its baseline after Stop. Borrow the
     Input System's fixture discipline here: assert the post-reset condition rather than trusting it
     (`.../Tests/TestFixture/InputTestFixture.cs:179-180`, `Assert.Fail("Input system should not
     have devices after reset")`).
3. **Run for real**: a scene that spawns pooled objects and is unloaded during application quit must
   produce zero "GameObject created during quit" / leaked-DontDestroyOnLoad reports. Assert on the
   *absence of a new GameObject*, not on the absence of an exception — an independent observation of
   the mechanism, not of the code that drives it.

---

## 8. Open questions for a human

1. **Does `HybridScope` get `[Obsolete]` in 4.1.0, announcing removal at 5.0.0?**
   Step 7 collapses its storage either way and needs no answer. The attribute does: marking it
   obsolete tells every current user to migrate to `ScopeManager.GetOrCreateScope`; not marking it
   keeps a third named mechanism alive that the docs then have to justify. *Recommendation:*
   `[Obsolete(..., false)]` in 4.1.0 — it has zero in-package consumers outside `AdvancedAPI` and its
   entire value proposition ("start singleton, upgrade to named", `HybridScope.cs:18-31`) is what
   `ScopeManager` already does with fewer moving parts.

2. **Does the package ship a `[Manager] Addressables` prefab / documented Systems-scene placement
   (§4), or stay purely lazy?** Placement matches the studio kit exactly and turns the lazy-create
   path into a fallback; purely lazy means zero setup and works in projects that have no Systems
   scene. These are not exclusive — the question is which one the README leads with.

3. **Should asset teardown actually run at application quit on device (§3.4)?** Costs a few
   milliseconds of releases inside `OnDestroy` during quit; buys correctness only for the editor with
   domain reload disabled and for restart-without-process-exit platforms. *Recommendation:* yes, run
   it — but this is a product call about quit latency on low-end Android, and the mobile lens should
   weigh in.

4. **`ScopeManager.ClearScope` on an entry it does not own (§5 step 6): clear-cache-and-log-error
   (recommended), throw, or return a typed result?** Invariant 4 argues for a result type; the
   existing `void` signature and invariant 6 argue against changing it. *Recommendation:*
   keep `void` + error log naming the owner, and add `ScopeManager.IsManagerOwned(string)` so a
   caller can ask before acting — `void` carries no information at all, so there is no sentinel to
   disambiguate.

   **Implemented as recommended** (A-2/A-6/A-7/A-12 pass): `ClearScope` stays `void`; a foreign
   entry gets `ClearCache()` + `Debug.LogError` naming the owner's type
   (`Registration.OwnerTypeName`, set from the `object owner` passed to `RegisterExternal`);
   `ScopeManager.IsManagerOwned(string)` is public. `ClearAllExcept`/`ClearAll` clear a foreign
   entry's cache without the per-entry error log (bulk operations are expected to touch entries
   they don't own; only the targeted `ClearScope` call surfaces it). See
   `Runtime/Managers/ScopeManager.cs`.

5. **Is `GlobalAssetScope` surviving Facade restarts (§5 step 4) the intended product behaviour?**
   This design says yes and cites the class's own doc comment (`GlobalAssetScope.cs:6`). If the
   intent were instead "the Facade is the app's addressables lifetime", the whole ownership rule
   inverts and `GlobalAssetScope` should become a child of the Facade rather than a peer singleton.
   That is the one decision in this document that a different product answer would genuinely change.

---

## L-7: evidence for the TieredAssetLoader decision

> **DECIDED 2026-08-17: DEPRECATE.** The human weighed the evidence below and chose to retire
> `TieredAssetLoader` as a fork. Tiering is now a **configuration of `AssetLoader`**, not a second
> loader class.
>
> **The new configuration surface:**
>
> | What | Where |
> |---|---|
> | `new AssetLoader(string scopeName, TieredCacheConfig tiering)` | `Runtime/Loaders/AssetLoader.cs` — second parameter **required**, no default, so `new AssetLoader("X")` is unambiguously untiered |
> | `AssetLoader.TieringEnabled` | `Runtime/Loaders/AssetLoader.cs` |
> | `PinAsset<T>` · `UnpinAsset<T>` · `EvaluateTiers()` · `ForceEviction()` · `GetTieredCacheStats()` · `GetTieredCacheStats<T>()` | `Runtime/Loaders/AssetLoader.cs`, `#region Tiering` |
> | `Advanced.CreateLoader(string, TieredCacheConfig)` + six `AssetLoader`-typed siblings | `Runtime/API/AdvancedAPI.cs` |
>
> **Tiering is OFF by default** and every existing construction site uses the one-argument
> constructor, so nothing that exists today changes behaviour. Turning it on by default would start
> evicting against `Profiler.GetRuntimeMemorySizeLong`, a number L-5 requirement "(a)" records as
> still unverified in non-development builds — see the last row of "what is still not proven" below.
>
> **What changed structurally:** `TieredAssetLoader` is `[Obsolete]` (warning, not error; removed in
> 5.0.0) and is now a ~200-line forwarder onto an inner `AssetLoader`. `TieredAssetLoaderRegistry`
> is deleted — it never shipped in a release — and the facade's pump and low-memory sweep walk
> `AssetLoaderRegistry` instead. Row L-3 of the table below (a near-line-for-line duplicate registry)
> and row L-4 (per-`Type` byte siloing) are therefore closed by construction rather than patched:
> there is one registry and one byte total over one dictionary.
>
> **Item D below — the catalog-invalidation hole — is closed.** A `TieredAssetLoader`'s inner loader
> is a plain `AssetLoader`, registered by `AssetLoader`'s own constructor via
> `AssetLoaderRegistry.Register(this)`, so `AssetLoaderRegistry.InvalidateAll` now reaches it like
> any other loader. Every existing `new TieredAssetLoader(...)` call site is fixed without its
> author changing a line.
>
> `TieredCache<T>`, `ThreadSafeCacheManager<T>`, `TieredCacheConfig`, `CacheEntry<T>` and `CacheTier`
> are **not** deprecated; `TieredCache<T>` remains supported for standalone use via
> `Advanced.CreateTieredCache<T>`. It is simply no longer what a loader uses internally.
>
> The evidence that produced this decision is left below, unchanged.

Not a recommendation — HANDOFF_TO_SESSION_B.md §2.1 reserves L-7 (whether to deprecate
`TieredAssetLoader` as a fork of `AssetLoader`) for a human, and this pass did not touch that
decision: no `[Obsolete]`, no restructuring, no deletion. This section only records what fixing
L-1/L-3/L-4/L-8/L-9/L-10 *in* `TieredAssetLoader` surfaced, because the surfacing happened as a
side effect of work already in scope and the instruction that reserved L-7 also asked for exactly
this: write the evidence down, act on nothing.

**Every one of the six items fixed re-solved a problem `AssetLoader` had already solved, inside a
second, independent implementation:**

| Item | What `TieredAssetLoader` needed | What `AssetLoader` already had |
|---|---|---|
| L-1 (teardown) | A `Release()` vs `ForceRelease()` distinction between eviction and teardown, and a `_disposed = true`-before-teardown ordering | Both already present and already correct (`AssetLoader.cs` `ClearCache`/`TearDownAll`/`Dispose`) — this pass copied the shape rather than inventing one |
| L-9 (cache key) | A collision-safe, allocation-free `(address, Type)` key | `AssetCacheKey` (`AssetLoader.cs:1982`), reused as-is — no new type needed once the loader in question could see it |
| L-10 (main thread) | A latch that cannot be wrong about which thread is main | `AddressableRuntime.IsMainThread` (built for `AssetLoader`, `UnityMainThreadDispatcher`; `TieredAssetLoader` was the third, separate, buggy latch the design already planned to retire — LIFETIME_DESIGN.md §5 step 2) |
| L-2 (progress) | A way to observe an in-flight load's percent-complete without opening a second Addressables operation | Had to be *added* to `AssetLoader` (`GetLoadProgress<T>`, this pass) precisely because `TieredAssetLoader` has no single-flight map of its own for `ProgressiveAssetLoader` to have joined instead — the seam belongs on the loader that owns single-flight, and only one of the two loader classes does |
| L-3 (eviction pump) | A registry so a facade-owned pump can reach every live instance | `AssetLoaderRegistry` already existed for `AssetLoader`; this pass had to write `TieredAssetLoaderRegistry` as a near-line-for-line duplicate, because the two loader types share no common reachability mechanism |
| L-4 (shared budget) | Byte accounting shared across per-type caches instead of siloed per type | Not applicable to `AssetLoader` at all — it has one cache dictionary keyed by `(address, Type)`, not one dictionary *per* `Type`. The whole class of bug L-4 fixes exists only because `TieredAssetLoader` is shaped differently from `AssetLoader` for no reason connected to tiering itself |

**What this pass did *not* have to duplicate, and why that is the sharper data point:**
`TieredAssetLoader` still has none of `AssetLoader`'s single-flight join (`TryJoinInFlight`/
`NewInFlight`/`CompleteInFlight`), none of its `StillAliveAfterAwait`/thread re-check-after-await
guard, no `ReleaseAsset`/`ReleaseInstance`/`InstantiateAsync`, no `*Safe`/`LoadResult` variants, no
`InvalidateAddresses` reachability for catalog updates (`AssetLoaderRegistry.InvalidateAll` walks
`AssetLoader` instances only — a `TieredAssetLoader` is invisible to it, so a cache it holds keeps
serving pre-catalog-update content indefinitely, permanently, with no code path that could ever
reach it to fix that). None of *those* gaps were in scope for this pass (L-7 says don't restructure
the class), so they are still open, undocumented-as-fixed, and — per the fork framing — each one is
a feature `AssetLoader` has that a `TieredAssetLoader` user silently does not, with no signal at the
call site that anything is missing.

**Net observation, stated once and left for the human to weigh:** six independent defects in
`TieredAssetLoader`, fixed this pass, cost real engineering effort (a new `CacheBudget` type, a new
`TieredAssetLoaderRegistry` type, a reasoned-through `ForceReleaseAll` vs `Clear` split) to bring it
up to roughly where `AssetLoader` already stood before any of this pass began — and the fixes still
leave it missing capabilities `AssetLoader` has had all along. This is consistent with, but does not
by itself decide, W4-05's framing ("tiering thành config của loader duy nhất"): the cost observed
here is the cost of *maintaining* the fork at parity, not the cost of *migrating* it, and this pass
did not attempt the latter or estimate it.

---

## Pooling: decisions still open

Written by the pass that implemented the pooling discovery report's defects (P-8, P-9, P-11..P-21,
P-23, P-24, P-26..P-30, P-32, P-33, P-35). **Nothing in this section was decided.** Each item below
was classified in that report as a product decision, or turned out on contact to be one; the pass
recorded the evidence and changed nothing that would pre-empt the choice. Where a defect was
entangled with a decision — P-9 and P-18 in particular — the defect half was fixed and the decision
half is stated here, separately, so choosing differently later is still cheap.

The same instruction that reserved these also required that no inert feature be left silently inert
(REFACTOR_TASKS.md rule 5). Where that applied, the holding action taken is named per item and is
explicitly not the fix.

### 1. `PoolConfiguration`: wire it up, or `[Obsolete]` the whole class? (P-10)

**Observed.** `Runtime/Configs/PoolConfiguration.cs` is 100% inert. Every field, both query methods
and the whole `Validate()`/`OnValidate()` pair are unreachable from runtime behaviour. The only
consumers anywhere in the repo are `Editor/Tools/ContextMenus.cs:83` and `:228`, which *create* the
asset. `Assets/PoolConfig.asset` exists in this project and is read by nothing.

Specifically dead: `prefabReference`, `address`, `preloadCount`, `maxSize`, `autoCreate`,
`poolRoot`, `destroyOnFull`, `label`, `pools`, `defaultMaxSize`, `defaultPreloadCount`,
`createAllOnStartup`, `cleanupOnSceneUnload`, `GetAutoCreatePools()`, `GetPoolByAddress()`.
`createAllOnStartup` has no startup path to hook — nothing in this package runs at startup.
`cleanupOnSceneUnload` now has a *near*-miss: `AddressablePoolManager` does subscribe to
`SceneManager.sceneUnloaded` as of P-8, but only to reclaim destroyed instances; it never clears
pools and never reads the flag.

**Side A — wire it in.** Cost: a startup path has to be invented (there is none), which means
deciding who owns it — `AddressablesFacade.Initialize`, a `RuntimeInitializeOnLoadMethod`, or an
explicit `Assets.ApplyPoolConfig(asset)` call. Two traps must be resolved on the way:
`PoolSettings.GetAddress()` returns `prefabReference.AssetGUID`, so a config-created pool is keyed
by GUID while user code calls `Spawn("Enemies/Orc")` and misses every time; and `maxSize = 0` means
"unlimited" here (matching `IPoolFactory.CreatePool`) but is now a rejected hard cap of zero on
`DynamicPoolConfig` (P-19), so the wiring decides which convention the asset actually gets.
Benefit: the designer-facing, one-place-for-all-pools story the class name and the
`EDITOR_TOOLS_GUIDE.md:113-132` section already promise.

**Side B — deprecate it.** Cost: `[Obsolete]` on the class plus the two `ContextMenus` entries and a
rewrite of the guide section; existing `PoolConfig.asset` files in user projects become dead weight
with a compiler warning attached. Benefit: no invented startup path, and the promise stops being
made. This is the smaller change by a wide margin, which is exactly why it should not be chosen by
default.

**Holding action taken (not the fix).** The inert-ness is now visible rather than implied: a class
doc block stating it plainly and naming both traps; `INERT:` prefixes on every `[Tooltip]` and
`[Header]` so it reads in the Inspector; an explicit `OnValidate` warning that fires the moment a
human edits the asset — the moment they form the belief that editing it does something; and the
`[Obsolete]` message on `destroyOnFull` corrected, because annotating exactly one field as ignored
asserted by omission that the other twelve were honoured. `Runtime/Configs/PoolConfiguration.cs` is
outside the `Runtime/Pooling/**` scope this pass owned; it was touched only for this, additively,
and no behaviour changed.

### 2. `Despawn` with the wrong address: destroy, route, or refuse? (P-9)

**Observed.** `Despawn("Enemies/Orc", goblin)` where `goblin` is a live instance of pool
`"Enemies/Goblin"` destroys it. The manager *knows* the real owner — `ownerAddress` is right there
in the guard — and could route the release to it, or refuse and leave the object alone. P-7 unified
three previous per-adapter behaviours (`CustomPoolAdapter` warned and left it alive;
`UnityPoolAdapter` silently adopted it) onto the most destructive of the three.

- **Destroy** (current): one wrong string argument kills a healthy on-screen object. Predictable,
  and never recycles an instance into the wrong pool.
- **Route to the real owner**: the friendly option, and the object survives. It also means a typo
  silently *works*, so the bug never surfaces; and it is only correct while `_instanceOwners` is
  authoritative, which it is exactly as far as P-8's sweep keeps it honest.
- **Refuse and leave alone**: safest for the object, but the instance stays borrowed forever, which
  is precisely the state P-8 was written to eliminate — it would reintroduce a `ClearPool` block by
  a different route.

**Defect half, already fixed, independent of the choice.** Whatever the policy, the owning pool's
accounting has to be corrected. Destroying a foreign live instance without telling its pool left
that pool counting a phantom active forever, which feeds `DynamicPool.CheckForGrowth` a ratio that
only climbs — the pool grows to `MaxSize` and `CheckForShrinkage` can never fire again.
`ReclaimLostInstance` now tells the real owner via the new `IReclaimablePool<T>.ForgetActive`.

### 3. `DynamicPoolConfig.InitialCapacity`: make it prewarm, or rename it? (P-18)

**Observed.** `InitialCapacity` creates nothing. It is only the denominator of
`usageRatio = activeCount / capacity`. `CreateDynamicPoolAsync(addr, config)` with the default
`preloadCount: 0` produces a pool with **zero** instances that reports
`DynamicPoolStats.CurrentCapacity == 10` and `TotalCount == 0`, and the first growth does not
trigger until 8 instances are concurrently checked out. `DynamicPoolConfig.Fixed(20)` — doc-commented
"Traditional pool behavior" — creates nothing, disables auto-resize, and yields a plain on-demand
pool with a 20-cap free list.

- **Make it prewarm**: the name becomes true. But every existing `CreateDynamicPoolAsync` call in
  every project silently starts instantiating `InitialCapacity` GameObjects at creation time — ten
  by default, on the main thread, per pool. That is a behaviour change large enough to be a release
  note, not a bug fix.
- **Rename** (`InitialCapacityBudget`, or fold into a `GrowthBudget` concept): honest and cheap at
  runtime, but renaming a public serialized field is a source break and needs invariant 6's
  `[Obsolete]` shim on the old name until 5.0.0.

**Holding action taken (not the fix).** Only the lies were removed: the field, `MinSize`,
`DynamicPoolStats.CurrentCapacity` and `Fixed()` now document what they actually do; the pool
creation log says "resize budget" and states that nothing exists yet instead of printing
`(capacity: 10)` next to an empty pool; and `DynamicPool_InitialCapacity_CreatesNothing` pins the
current behaviour so that changing it has to be deliberate.

### 4. Pool creation's `bool`: add a `LoadResult`-shaped sibling? (P-22)

**Observed.** `CreatePoolAsync`/`CreateDynamicPoolAsync` return `true` for three distinct successes
("created", "already existed", "lost a race and someone else's pool exists") and `false` for
"disposed manager", "invalid config", "load failed", and "threw". It reaches `Assets.CreatePool`,
`Standard.CreatePool`, `Standard.CreateDynamicPool`, `Advanced.CreateDynamicPool`, and the sample at
`Samples~/ApiExamples/AddressablesExamples.cs:218`, which prints "Pool creation failed (prefab not
found)" for every one of the failure meanings. This is invariant 4's shape on the pooling public
API.

Invariant 6 forbids deleting the `bool` overload before 5.0.0, so the decision is only whether to
add a `LoadResult`-shaped sibling now (and `[Obsolete]` the `bool` pointing at it) or to leave the
API alone until 5.0.0 and carry the ambiguity. Not decided here; the surface is on the
`Runtime/API` boundary this pass did not own.

**Related defect half, already fixed.** One `false` was outright wrong rather than merely ambiguous:
a preload that threw made the method return `false` while `_pools[address]` held a working pool, so
`RunAutoCreate` logged "Auto-create failed", `SpawnAsync` returned `null`, and the very next
synchronous `Spawn()` succeeded. Preload failures are now logged and swallowed by `PreloadInto`, and
a committed pool reports `true` (P-13).

### 5. `SetPoolFactory` mid-session: document, or rebuild? (P-25)

**Observed.** Existing pools keep the adapter they were built with. P-11/P-12/P-14/P-15 removed the
observable divergences between the two *shipped* adapters, but a third-party `IPoolFactory` is
unconstrained, so a mid-session swap can still leave one manager whose pools disagree through the
same `IObjectPool<T>` interface. Rebuilding on swap would mean destroying and re-creating every
pooled instance mid-game — the opposite of what pooling is for — and would have to decide what
happens to instances currently borrowed.

**Holding action taken (not the fix).** The log no longer announces a switch that mostly did not
happen: when any pool already exists, `SetPoolFactory` says so and states that only new pools use
the new factory, and the XML doc says it in the API surface. Refusing the swap outright once a pool
exists is the other candidate and was not chosen.

### 6. Where the pool maintenance clock should live (P-17)

**Observed.** `DynamicPool`'s shrink is a two-step state machine that could only advance inside
`Release()`, so a pool that went idle — the one case shrinking exists for — never shrank. Fixing it
needs a clock. This pass added `PoolMaintenancePump`, a manager-owned `DontDestroyOnLoad`
MonoBehaviour ticking `RunMaintenance()` (P-8 sweep + P-17 shrink evaluation) once a second.

That is now the **second** pump in the package: `AddressablesFacade` already runs a 5s
`TieredAssetLoaderRegistry.PumpAll()` plus an `Application.lowMemory` hook (L-3). Folding pooling
maintenance into the facade's pump would mean one clock instead of two — but it would also mean
pooling maintenance only runs for facade users, and `AddressablePoolManager` is public,
constructible and usable without the facade (`Advanced.CreatePoolManager(loader)`), so that would
recreate the inert-feature shape for those users. A registry mirroring
`TieredAssetLoaderRegistry` would resolve it properly and is the third option. Not decided;
`RunMaintenance()` and `EvaluateAutoResize()` are public precisely so a host can take the clock over.

Also unaddressed by design: nothing hooks `Application.lowMemory` to force a pool shrink, the way
L-3 does for the tiered cache. That is the same decision.

### 7. The four disagreeing default `maxSize` values (P-33)

**Observed.** The same concept had four defaults: `Assets.CreatePool` 100,
`Facade.CreatePoolAsync` 100, `IPoolFactory.CreatePool` 100, `Standard.CreatePool` 50, a bare
literal `50` hard-coded in `RunAutoCreate`, `DynamicPoolConfig.MaxSize` 100, and
`PoolConfiguration.defaultMaxSize` 50 (inert). The `Simple.Pool` path was silently capped at 50
with no configuration hook of any kind.

**Fixed within this pass's scope.** The hard-coded literal is gone: `AddressablePoolManager` now has
one `public const int DefaultMaxPoolSize = 100` used by `CreatePoolAsync` and by auto-create, and
`EnableAutoCreatePools(config, maxSize)` gives the previously-unreachable auto-create cap a hook.
**Still disagreeing, outside this pass's files:** `Standard.CreatePool`'s 50
(`Runtime/API/StandardAPI.cs:170`) and `PoolConfiguration.defaultMaxSize`'s 50. Unifying those is a
one-line change each and a product call about whether 50 or 100 is the house default.

### 8. Not a decision — handed off, not done

Two report items are defects with no product question attached that this pass could not touch,
because they are outside `Runtime/Pooling/**` and other agents held those files:

- **P-31** — `AddressablesFacade.cs:260, 268, 276, 284, 292, 300` use a bare `_poolManager.` while
  `:307`, `:312`, `:371` use `?.`. `Initialize()` legitimately returns early with
  `_poolManager == null` when `GlobalAssetScope` is unavailable during shutdown, so
  `Assets.Spawn`/`Assets.Despawn`/`Assets.CreatePool` throw `NullReferenceException` at shutdown.
  `SimpleAPI.cs:194-195` already guards for exactly this. Fix: `?.` (and a neutral return) on all
  six.
- **P-34** — `README.md:52`, `:252` and `:396` still show
  `var enemy = Simple.Pool("Enemies/Orc"); Simple.Recycle("Enemies/Orc", enemy);` returning an
  instance. Post-P-1 the first call for any address *always* returns `null`, so the documented
  two-liner produces a warning, a `null`, and a second warning from `Despawn`'s guard.
  `README.md:125` ("Auto Grow/Shrink") and `:256-263` (`InitialCapacity = 10` as meaningful) are the
  P-17 and P-18 wordings and should be revisited in the same pass.

---

## Threading and blast-radius: decisions still open

Written by the pass that fixed the Wave F review findings (the refcount/lifetime, threading/
re-entrancy and contract/invariant lenses). **Nothing in this section was decided.** Each item is
either a product decision the reviewing pass surfaced but had no authority to settle, or a residual
whose only remaining fix is a restructure that needs a human to sanction it. Everything else those
three reviews raised was fixed in code.

### 1. `Simple.ReleaseAddress` and `Standard.ClearCache(scopeName)` widen who can reach `ForceRelease`

Both are new *reachability*, not new call sites — the hard release they end in already existed and
already had this semantic.

- `Simple.ReleaseAddress(address)` (`SimpleAPI.cs`) reaches `AssetLoader.ReleaseAsset` ->
  `EvictAddress` -> `RemoveCacheEntry(key, hard: true)` -> `handle.ForceRelease()`.
- `Standard.ClearCache(scopeName)` (`StandardAPI.cs`) reaches `AssetLoader.ClearCache()` ->
  `entry.Handle?.ForceRelease()` over every entry. Before this wave that method was a
  log-and-return stub, so this route is entirely new.

The consequence in both cases: `var h = await Standard.LoadGlobal<Sprite>("UI/Icon");` gives `h` a
refcount of 2 (caller + cache). An unrelated system calling either API drives the counter to 0
regardless of holders; `h.IsValid` reads `false` and `h.Asset` reads `null` while the caller still
owns a reference it never gave back. There is no callback and no way to detect it beforehand.

**Why this was not "fixed".** It is deliberate and documented at both call sites, and it follows the
ownership rule this document already sets out: the section 6 table says a foreign (scope-owned)
entry gets `loader.ClearCache()` and nothing stronger, which is exactly what `Standard.ClearCache`
does — the open question is not *which method* but *what `ClearCache` means*.
`AssetLoader.ClearCache`'s own remarks argue the hard release is required for a memory-pressure API
to reclaim anything at all. Changing it to a decrement would make both APIs unable to free memory in
the presence of any holder, which is the failure A-4 was raised about.

**The decision for a human,** stated once: is "clear/release under memory pressure" allowed to
invalidate handles other systems hold? This design says yes and both APIs say so in their XML docs.
If the answer is no, the fix is not at these two call sites — it is `AssetLoader.ClearCache` and
`ReleaseAsset` becoming decrements, plus a separate explicitly-named `ForceClearCache` for teardown,
and the two tiers' docs rewritten to match. Do not change one end without the other.

Worth weighing: `Simple.*` is the tier documented "for beginners", and it is the tier whose caller is
least likely to know that the `Standard`-tier handle it just invalidated exists at all.

### 2. `AddressableRuntime.IsMainThread` fails **open**, and two callers use it to decide whether to act inline

`AddressableRuntime.cs` — `IsMainThread => MainThreadId == 0 || Thread.CurrentThread.ManagedThreadId
== MainThreadId`. Unlatched (before `Init`'s `RuntimeInitializeOnLoadMethod` has run — edit mode, and
EditMode tests) it reads `true` on **every** thread, deliberately.

Fail-open is right for `ThreadSafeCacheManager.AssertMainThread`: an assertion that fires in edit
mode, where nothing ever latches, would block tooling for no benefit. It is a genuine trade for the
two callers that read the same predicate as "safe to do the unsafe thing inline":

- `ThreadSafeCacheManager.ReleaseOnMainThread` — unlatched, a worker-thread `Remove`/`Clear`/
  `Dispose` takes the inline `handle.Release()` branch, i.e. `Addressables.Release` from a worker.
- `AddressablePoolManager.EnsureMainThreadAsync` — unlatched, returns `Task.CompletedTask` and the
  caller proceeds off-thread into `_pools` and Unity APIs.

**Why this was not flipped.** Both branches are bad when unlatched, and the alternative is arguably
worse: in edit mode there is no `Update()` pumping `UnityMainThreadDispatcher`, so "marshal instead"
means the action is queued and *never runs* — a guaranteed leak in place of a possible race, and the
release path even logs "the asset will stay loaded" while the action sits in the queue. This pass
narrowed the window instead of changing the default: `UnityMainThreadDispatcher` now latches its own
main-thread id from `SubsystemRegistration` and creates its pump at `BeforeSceneLoad`, so in play
mode the unlatched window no longer exists in practice.

**The decision for a human:** should `AddressableRuntime` also latch in the editor (an
`#if UNITY_EDITOR` `[InitializeOnLoadMethod]` alongside `Init`), which would make "unlatched" mean
"genuinely nothing has started" and let the two helpers above fail *closed* safely? That is a small
change with a wide blast radius across edit-mode tooling and EditMode tests, so it is recorded
rather than taken.

### 3. Residual: `UnityPoolAdapter` suppression is scoped to a region, not to a pop

`UnityPoolAdapter._suppressOnGetDepth` was changed from a `bool` to a depth counter this pass, which
fixes the half that was reachable by mutation: a nested `Prewarm`/`TrimExcessMeasured` no longer
clears the outer region's suppression halfway through and fires the caller's `onGet` on instances
the outer loop is about to `Release()` (the P-11 defect returning).

The other half is **not** fixed and cannot be with this seam. Suppression covers a span of time, so
a *genuine* `Get()` that nests inside a `Prewarm` region — reachable when a pooled prefab's `Awake`
spawns from the same address on the same adapter — is suppressed too, and its caller receives an
instance that never got `SetActive(true)`/`TrackActive`. `Despawn` then finds no owner in
`_instanceOwners` and the pool's active count is wrong from then on.

Per-pop scoping is the correct fix and `UnityEngine.Pool.ObjectPool<T>` gives no seam for it: it
invokes `createFunc` (hence `Awake`, hence the nested `Get`) **before** `actionOnGet` for the outer
pop, so a "consume on first callback" token is consumed by the nested pop rather than the outer one.
The options are (a) stop routing `Prewarm`/`TrimExcess` through `Get()` and drive the inner pool's
free list directly, (b) have `AddressablePoolManager` stop depending on `onGet` firing and do
`SetActive`/`TrackActive` idempotently after `pool.Get()` returns — it already does exactly this for
`ApplyPendingPlacement`, which is the precedent — or (c) accept it and document. Option (b) looks
cheapest and most consistent, but it changes who owns activation for every pool implementation
including third-party `IObjectPool<T>`s, so it is a decision rather than a patch.

### 4. Release hygiene: this wave's notes went into an already-tagged CHANGELOG section

All of Wave F's CHANGELOG content was written into `## [4.1.0-pre.6]`, a heading that exists at HEAD
(`release: 4.1.0-pre.6`, pushed to `origin/feat/cdn-system`) and whose version `package.json` already
carries. There is no `[Unreleased]` heading. Note also that the `4.1.0-pre.6` **tag** points at a
different commit (`df19080`) that is *not* an ancestor of HEAD, so the release appears to have been
cut twice and the tag is stranded on the orphaned one.

This pass corrected the statements in that section that had become false (the
`Standard.ClearCache`/`Simple.Release`/`Standard.LoadScene` "still stubs" bullet, the
`TieredCache.Pin()` no-op bullet, the `ThreadSafeCacheManager` "still advertises any thread" bullet,
the `TieredAssetLoader` "dual UniTask signatures" claim, and the test count) and left the section
structure alone. **Whether the wave's content should be re-cut under a new `[4.1.0-pre.7]` heading,
and what to do about the stranded tag, is a release-process decision** — with a divergent tag in
play, re-cutting a pushed release section unilaterally is exactly the kind of thing that should not
happen without the human who owns the release deciding it.
