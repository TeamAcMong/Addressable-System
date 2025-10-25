# Game Addressables System

**Production-ready Unity Addressables management system** with scope-based lifecycle, object pooling, and progress tracking.

## Features

- **Scope-Based Lifecycle Management**
  - Global Scope (persistent, DontDestroyOnLoad)
  - Session Scope (gameplay session lifetime)
  - Scene Scope (auto-cleanup on scene unload)
  - Hierarchy Scope (GameObject lifetime)

- **Smart Object Pooling**
  - Factory Pattern for pluggable pool implementations
  - Built-in adapters: Unity ObjectPool, Custom Pool
  - Easy integration with Zenject, VContainer, or custom DI
  - Runtime pool factory switching

- **Progress Tracking**
  - Observer Pattern for real-time progress updates
  - Download speed & ETA calculation
  - Composite tracking for batch operations

- **Clean Architecture**
  - Reference counting to prevent memory leaks
  - Automatic cache management
  - Type-safe API with async/await
  - Zero breaking changes - backward compatible

- **Multiple API Levels**
  - **Facade Pattern**: Simple high-level API
  - **Static Assets Class**: One-liner operations
  - **Core API**: Full control for advanced scenarios

## Quick Start

### 1. Simple Usage (Static API)

```csharp
using AddressableManager.Facade;

// Load asset
var sprite = await Assets.Load<Sprite>("UI/Icon");
if (sprite.IsValid)
{
    image.sprite = sprite.Asset;
}

// Spawn from pool
var enemy = await Assets.Spawn("Enemies/Orc", spawnPosition);

// Return to pool
Assets.Despawn("Enemies/Orc", enemy);
```

### 2. Session Management

```csharp
// Start gameplay session
Assets.StartSession();

// Load session-specific assets (auto-cleanup when session ends)
var levelData = await Assets.LoadSession<LevelConfig>("Levels/Level1");

// End session (all session assets auto-released)
Assets.EndSession();
```

### 3. Progress Tracking

```csharp
using AddressableManager.Progress;

// Load with progress
var handle = await Assets.Load<Texture2D>("LargeTexture", progress =>
{
    progressBar.value = progress.Progress;
    statusText.text = progress.CurrentOperation;
});
```

### 4. Object Pooling

```csharp
// Create pool with preloading
await Assets.CreatePool("Enemies/Zombie", preloadCount: 10, maxSize: 50);

// Spawn multiple
for (int i = 0; i < 20; i++)
{
    var zombie = Assets.Spawn("Enemies/Zombie", randomPosition);
}

// Despawn when done
Assets.Despawn("Enemies/Zombie", zombie);
```

### 5. Scene-Specific Assets

```csharp
using AddressableManager.Scopes;

// Automatically cleanup when scene unloads
var sceneScope = SceneAssetScope.GetOrCreate();
var sceneAssets = await sceneScope.Loader.LoadAssetAsync<Material>("Scene/Materials");

// Or use static API
var handle = await Assets.LoadScene<Prefab>("Scene/Decoration");
```

### 6. Hierarchy-Scoped Assets (Per-GameObject)

```csharp
using AddressableManager.Scopes;

// Add scope to GameObject - auto-cleanup when destroyed
var scope = HierarchyAssetScope.AddTo(characterGameObject);
var weapon = await scope.Loader.LoadAssetAsync<GameObject>("Weapons/Sword");

// When characterGameObject is destroyed, all its assets are released automatically
```

## Advanced Usage

### Custom Pool Factory

```csharp
using AddressableManager.Pooling;

// Create custom pool factory (e.g., Zenject-based)
public class ZenjectPoolFactory : IPoolFactory
{
    public IObjectPool<T> CreatePool<T>(/*...*/) where T : class
    {
        // Your custom implementation
    }
}

// Switch to custom pool
var facade = AddressablesFacade.Instance;
facade.SetPoolFactory(new ZenjectPoolFactory());
```

### Direct AssetLoader Usage

```csharp
using AddressableManager.Loaders;
using AddressableManager.Core;

var loader = new AssetLoader();

// Load by address
var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon");

// Load by AssetReference
var handle2 = await loader.LoadAssetAsync<GameObject>(myAssetReference);

// Load multiple by label
var handles = await loader.LoadAssetsByLabelAsync<AudioClip>("Music");

// Instantiate
var instance = await loader.InstantiateAsync("Prefabs/Player", spawnPoint);

// Cleanup
loader.ClearCache();
loader.Dispose();
```

### Composite Progress Tracking

```csharp
using AddressableManager.Progress;

var composite = new CompositeProgressTracker();

composite.OnProgressChanged += info =>
{
    Debug.Log($"Overall Progress: {info.Progress * 100}%");
};

// Add child trackers for different operations
var tracker1 = new ProgressTracker();
var tracker2 = new ProgressTracker();

composite.AddTracker(tracker1, weight: 1f);
composite.AddTracker(tracker2, weight: 2f); // This operation is weighted 2x

// Load multiple assets
await Assets.Load<Sprite>("Icon1", info => tracker1.UpdateProgress(info));
await Assets.Load<Sprite>("Icon2", info => tracker2.UpdateProgress(info));
```

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                     Facade Layer (Simple API)                │
│  Assets (static) → AddressablesFacade → Unified Interface   │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                      Scope Management                        │
│  GlobalScope │ SessionScope │ SceneScope │ HierarchyScope   │
│  (Each has its own AssetLoader with auto-cleanup)           │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                      Core Loading Layer                      │
│  AssetLoader → IAssetHandle<T> → Reference Counting         │
│  Caching │ Progress Tracking │ Error Handling               │
└─────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────┐
│                    Pooling Layer (Optional)                  │
│  AddressablePoolManager → IPoolFactory → IObjectPool<T>     │
│  UnityPoolAdapter │ CustomPoolAdapter │ YourAdapter         │
└─────────────────────────────────────────────────────────────┘
```

## Design Patterns Used

- **Facade Pattern**: Simplified high-level API
- **Factory Pattern**: Pluggable pool implementations
- **Adapter Pattern**: Unified pool interface
- **Observer Pattern**: Progress tracking events
- **Repository Pattern**: Asset caching and retrieval
- **Strategy Pattern**: Runtime pool factory switching

## Best Practices

1. **Use appropriate scopes**:
   - Global: UI atlases, sound effects, shaders
   - Session: Character data, level configs
   - Scene: Scene-specific prefabs, materials
   - Hierarchy: Per-character weapons, effects

2. **Pool frequently spawned objects**:
   - Projectiles, enemies, particles
   - Preload during loading screen

3. **Monitor progress for large assets**:
   - Show loading bars for better UX
   - Use composite tracking for batch operations

4. **Clean up properly**:
   - Scopes auto-cleanup, but you can manually clear if needed
   - Use `ClearCache()` when switching levels

## Package Structure

```
/Packages/com.game.addressables/
├── Runtime/
│   ├── Core/                    # IAssetHandle, AssetHandle
│   ├── Loaders/                 # AssetLoader
│   ├── Scopes/                  # Scope managers
│   ├── Pooling/                 # Pool interfaces & adapters
│   ├── Progress/                # Progress tracking
│   └── Facade/                  # High-level API
├── package.json
└── README.md
```

## Requirements

- Unity 2021.3 or later
- Unity Addressables package 2.3.1+

## License

MIT License - Feel free to use in your projects!
