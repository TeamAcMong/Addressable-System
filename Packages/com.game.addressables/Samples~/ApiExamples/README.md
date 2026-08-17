# API Examples

`AddressablesExamples` runs one example after another from `Start()` and logs what
it did. It exists to show the shape of each API tier side by side, so you can pick
the lowest tier that covers your case.

| Tier | Entry point | Use it when |
|---|---|---|
| Simple | `Simple.Load<T>`, `Simple.Pool` | You want an asset and will let the manager decide the lifetime. |
| Standard | `Standard.LoadGlobal/LoadSession/LoadScene` | You want scopes, batch loading and explicit release. |
| Advanced | `Advanced.CreateLoader(name, config)`, named scopes, pool factories | You are managing memory yourself. |

The `Assets` facade is the shortest path — `Assets.Load<T>`, `Assets.LoadSession<T>`,
`Assets.LoadScene<T>`, `Assets.CreatePool`/`Spawn`/`Despawn` — and it returns
`UniTask` when UniTask is installed and `Task` when it is not, from the same call
site.

Attach it to a GameObject in an empty scene. The UI fields are optional — leave
them empty and the examples log to the console instead.

## What it covers

Six examples, run in order, each a separate coroutine named for what it shows:
simple load, session management, progress tracking, object pooling, scene scope and
hierarchy scope. Delete the ones you do not need.

The examples assume the assets they reference are addressable in your project. They
log and continue when an address is missing rather than throwing, so an incomplete
setup produces a readable console rather than a stack trace.
