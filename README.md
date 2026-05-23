# Addressable Manager

Production-grade Unity Addressables management with scope-based lifecycles, object pooling, progress tracking, and an Editor Dashboard that monitors every load — no special API required.

> **Package source:** [`Packages/com.game.addressables/`](Packages/com.game.addressables/) — this Unity project is also the package's authoring workspace.

## Install

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/TeamAcMong/Addressable-System.git#2.2.1"
  }
}
```

Or via Unity: **Window → Package Manager → ＋ → Add package from git URL**:

```
https://github.com/TeamAcMong/Addressable-System.git#2.2.1
```

Tags publish only the package subtree (~KB, not MB) — see [DEPLOY_UPM_SUBTREE.md](DEPLOY_UPM_SUBTREE.md) for the release flow.

## What's inside

- **Scopes** — Global / Session / Scene / Hierarchy / arbitrary named scopes — each owns an isolated `AssetLoader` with its own cache, ref-counts, and Dashboard label.
- **Pooling** — Addressable-aware pool manager on `UnityEngine.Pool.ObjectPool`, with `IPoolFactory` extension point for DI containers.
- **Progress tracking** — `IProgressTracker` for individual loads, `CompositeProgressTracker` for batches, plus an optional `AddressableProgressBar` UI component.
- **Editor Dashboard** — `Ctrl+Alt+A` opens a live view of active assets, scopes, memory, and load times. Reporting is `#if UNITY_EDITOR` so shipping builds carry zero overhead.

## Quick start

```csharp
using AddressableManager.Facade;

// Global-scope load
var icon = await Assets.Load<Sprite>("UI/Icon");
image.sprite = icon.Asset;

// Pooled spawning
await Assets.CreatePool("Enemies/Orc", preloadCount: 8, maxSize: 32);
var orc = Assets.Spawn("Enemies/Orc", spawnPosition);
Assets.Despawn("Enemies/Orc", orc);

// Session lifetime
Assets.StartSession();
var level = await Assets.LoadSession<LevelConfig>("Levels/Level1");
Assets.EndSession();
```

Full API tour + architecture diagram + best practices: [`Packages/com.game.addressables/README.md`](Packages/com.game.addressables/README.md).

## Documentation

- [Package README](Packages/com.game.addressables/README.md) — overview, install, API tour, architecture, migration
- [MONITORING_GUIDE](Packages/com.game.addressables/MONITORING_GUIDE.md) — Dashboard, custom monitors, build behavior
- [EDITOR_TOOLS_GUIDE](Packages/com.game.addressables/EDITOR_TOOLS_GUIDE.md) — inspectors, configs, `ScopeManager`, recipes
- [CHANGELOG](Packages/com.game.addressables/CHANGELOG.md) — release notes
- [DEPLOY_UPM_SUBTREE](DEPLOY_UPM_SUBTREE.md) — UPM tag release flow

## Requirements

- **Unity 2022.3** or later
- `com.unity.addressables` 2.3.1+
- TextMeshPro 3.0+ — optional (only `AddressableProgressBar` uses it, gated by `TMP_PRESENT`)

No UniTask, no Newtonsoft, no other runtime dependencies. Async surface is plain `System.Threading.Tasks.Task` + `AsyncOperationHandle.Task`.

## License

MIT — see [LICENSE](Packages/com.game.addressables/LICENSE.md).
