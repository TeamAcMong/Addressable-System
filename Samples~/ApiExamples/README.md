# API Examples

`AddressablesExamples` runs one example after another from `Start()` and logs what
it did. It shows the `Assets` facade in use — it does **not** demonstrate the
`Simple` / `Standard` / `Advanced` tiers, which the sample never imports; every
example goes through `Assets` or through a scope's own `Loader`.

| Tier | Entry point | Use it when |
|---|---|---|
| Simple | `Simple.Load<T>`, `Simple.Pool` | You want an asset and will let the manager decide the lifetime. |
| Standard | `Standard.LoadGlobal/LoadSession/LoadIntoSceneScope` | You want scopes, batch loading and explicit release. |
| Advanced | `Advanced.CreateLoader(name, config)`, named scopes, pool factories | You are managing memory yourself. |

`Simple`, `Standard` and `Advanced` are static classes in `AddressableManager.API`.
`Advanced.CreateLoader` has a one-argument overload that leaves tiering off; the
two-argument one turns it on and throws on a null config.

The `Assets` facade (`AddressableManager.Facade`) is the shortest path —
`Assets.Load<T>`, `Assets.LoadSession<T>`, `Assets.LoadScene<T>`,
`Assets.CreatePool`/`Spawn`/`Despawn` — and it returns `UniTask` when UniTask is
installed and `Task` when it is not, from the same call site. `Simple.*` and most of
`Standard.*` are `Task`-only.

Attach it to a GameObject in an empty scene. The UI fields are optional — leave
them empty and the examples log to the console instead.

## What it covers

Six examples, run in order, each a separate coroutine named for what it shows:
simple load, session management, progress tracking, object pooling, scene scope and
hierarchy scope. Delete the ones you do not need.

The examples assume the assets they reference are addressable in your project. They
log and continue when an address is missing rather than throwing, so an incomplete
setup produces a readable console rather than a stack trace.

Two further methods, `BonusExample_CustomPoolFactory` and
`BonusExample_DownloadWithProgress`, are never called from `Start()`. They exist for
their commented-out code. The download snippet uses `Assets.Download`, which is
`[Obsolete]` — use `CdnManager.DownloadAsync` for new code.

## Things the samples rely on that are easy to get wrong

- **Nothing here loads a Unity scene.** `Assets.LoadScene<T>` loads an *asset* into
  the active scene's cache, so it is released when that scene unloads. Despite the
  name it does not call `Addressables.LoadSceneAsync`; no part of this package does.
  `Standard.LoadScene<T>` is `[Obsolete]` for the same reason — prefer
  `Standard.LoadIntoSceneScope<T>`, which also binds to the **active** scene when you
  do not pass one.
- **Create the pool before you spawn.** Example 4 awaits `Assets.CreatePool` first on
  purpose. `Assets.Spawn` and `Simple.Pool` return `null` for an address whose pool
  does not exist yet: auto-create is started in the background and the call that
  triggered it gets nothing back.
- **`Simple.Load<T>` hands back the raw asset, not a handle**, and disposes the handle
  internally — you do not control its lifetime. The examples use `Assets.Load<T>`,
  which returns `IAssetHandle<T>` (`AddressableManager.Core`) and lets you check
  `IsValid` and release when you are done.
