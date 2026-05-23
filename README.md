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

- Unity **2022.3** or later
- Unity Addressables package 2.3.1+
- TextMeshPro 3.0+ (optional — only required if you use `AddressableProgressBar`; gated by the `TMP_PRESENT` define)

## License

MIT License - Feel free to use in your projects!

---

# 📚 Comprehensive Guide

## Table of Contents

1. [System Architecture](#system-architecture)
2. [API Reference](#api-reference)
3. [Design Decisions & Limitations](#design-decisions--limitations)
4. [Performance & Memory](#performance--memory)
5. [Extension Points](#extension-points)

---

# System Architecture

## Architectural Layers

```
┌──────────────────────────────────────────────────────────────┐
│                    Presentation Layer                         │
│              (Facade Pattern - Simple API)                    │
│                                                                │
│  Assets (Static API)  ─────►  AddressablesFacade             │
│  • One-liner operations      • Unified interface              │
│  • Zero boilerplate          • High-level orchestration       │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│                  Business Logic Layer                         │
│              (Scope-Based Lifecycle Management)               │
│                                                                │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐             │
│  │  Global    │  │  Session   │  │   Scene    │             │
│  │  Scope     │  │  Scope     │  │   Scope    │             │
│  └────────────┘  └────────────┘  └────────────┘             │
│         │              │              │                       │
│         └──────────────┼──────────────┘                       │
│                        │                                      │
│                  ┌────────────┐                               │
│                  │ Hierarchy  │                               │
│                  │   Scope    │                               │
│                  └────────────┘                               │
│                                                                │
│  Each scope has its own AssetLoader with isolated cache       │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│                    Data Access Layer                          │
│             (Core Loading & Cache Management)                 │
│                                                                │
│  AssetLoader                                                  │
│  ├─ Load by Address/Reference/Label                          │
│  ├─ Instantiate GameObjects                                  │
│  ├─ Download Dependencies                                    │
│  └─ Cache Management with Reference Counting                 │
│                                                                │
│  IAssetHandle<T>                                              │
│  ├─ Wraps AsyncOperationHandle                               │
│  ├─ Reference Counting (Retain/Release)                      │
│  └─ Automatic Cleanup                                        │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│                  Cross-Cutting Concerns                       │
│                                                                │
│  ┌─────────────────────┐      ┌──────────────────────────┐  │
│  │  Pooling System     │      │  Progress Tracking       │  │
│  │  (Factory Pattern)  │      │  (Observer Pattern)      │  │
│  │                     │      │                          │  │
│  │  IPoolFactory       │      │  IProgressTracker        │  │
│  │  ├─ Unity Adapter   │      │  ├─ ProgressTracker      │  │
│  │  ├─ Custom Adapter  │      │  └─ CompositeTracker     │  │
│  │  └─ Your Adapter    │      │                          │  │
│  └─────────────────────┘      └──────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────┐
│               Infrastructure Layer                            │
│            (Unity Addressables API)                           │
│                                                                │
│  Addressables.LoadAssetAsync<T>()                            │
│  Addressables.InstantiateAsync()                             │
│  Addressables.DownloadDependenciesAsync()                    │
│  Addressables.Release()                                      │
└──────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

### Core Components

#### `IAssetHandle<T>`
- **Responsibility**: Wrapper for `AsyncOperationHandle<T>` with lifecycle management
- **Key Features**:
  - Reference counting (Retain/Release)
  - Safe disposal
  - Status tracking
- **Dependencies**: Unity Addressables

#### `AssetLoader`
- **Responsibility**: Core asset loading and cache management
- **Key Features**:
  - Load by address/reference/label
  - Instantiate GameObjects
  - Download dependencies
  - Cache with deduplication
  - Reference counting
- **Dependencies**: Unity Addressables, `IAssetHandle<T>`

### Scope Management

#### `IAssetScope`
- **Responsibility**: Define lifecycle contract for scoped asset management
- **Implementations**:
  - `GlobalAssetScope`: App lifetime (DontDestroyOnLoad)
  - `SessionAssetScope`: Gameplay session
  - `SceneAssetScope`: Scene lifetime
  - `HierarchyAssetScope`: GameObject lifetime

#### Scope Lifecycle Comparison

| Scope | Created | Destroyed | Use Case |
|-------|---------|-----------|----------|
| Global | On first access | Never (app lifetime) | UI atlases, shaders, sounds |
| Session | `StartSession()` | `EndSession()` | Character data, level configs |
| Scene | Scene load | Scene unload | Scene-specific prefabs |
| Hierarchy | Added to GameObject | GameObject destroyed | Per-character weapons, effects |

### Data Flow Example: Simple Load

```
User Code
   │
   └─► Assets.Load<Sprite>("UI/Icon")
         │
         └─► AddressablesFacade.LoadGlobalAsync()
               │
               └─► GlobalAssetScope.Loader.LoadAssetAsync()
                     │
                     ├─► Check cache (hit) ──► Return cached handle (Retain)
                     │
                     └─► Check cache (miss)
                           │
                           └─► Addressables.LoadAssetAsync()
                                 │
                                 └─► Wrap in AssetHandle
                                       │
                                       └─► Store in cache
                                             │
                                             └─► Return handle
```

---

# API Reference

## 🚀 Common Operations

### Load Asset

```csharp
// Global scope (persistent)
var sprite = await Assets.Load<Sprite>("UI/Icon");

// Session scope (until session ends)
var config = await Assets.LoadSession<Config>("Data/Config");

// Scene scope (until scene unloads)
var material = await Assets.LoadScene<Material>("Materials/Special");
```

### Load with Progress

```csharp
var texture = await Assets.Load<Texture2D>("Textures/Large", progress =>
{
    progressBar.value = progress.Progress;
    statusText.text = progress.CurrentOperation;
});
```

### Object Pooling

```csharp
// Create pool
await Assets.CreatePool("Enemies/Zombie", preloadCount: 10, maxSize: 50);

// Spawn
var zombie = Assets.Spawn("Enemies/Zombie", position, rotation);

// Despawn
Assets.Despawn("Enemies/Zombie", zombie);
```

### Session Management

```csharp
// Start session
Assets.StartSession();

// Load session assets
var levelData = await Assets.LoadSession<LevelData>("Levels/Level1");

// End session (auto-cleanup)
Assets.EndSession();
```

### Hierarchy-Scoped Assets

```csharp
// Add to GameObject - auto-cleanup when destroyed
var scope = HierarchyAssetScope.AddTo(characterGameObject);
var weapon = await scope.Loader.LoadAssetAsync<GameObject>("Weapons/Sword");
```

## 📦 Scope Comparison

| Scope | Lifetime | Use Cases |
|-------|----------|-----------|
| **Global** | App lifetime (DontDestroyOnLoad) | UI atlases, sound effects, shaders |
| **Session** | Gameplay session | Character data, level configs |
| **Scene** | Current scene | Scene-specific prefabs, materials |
| **Hierarchy** | GameObject lifetime | Per-character weapons, effects |

## 🛠️ Advanced API

### Direct AssetLoader

```csharp
using AddressableManager.Loaders;

var loader = new AssetLoader();

// Load by address
var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon");

// Load by AssetReference
var handle2 = await loader.LoadAssetAsync<GameObject>(assetRef);

// Load multiple by label
var handles = await loader.LoadAssetsByLabelAsync<AudioClip>("Music");

// Instantiate
var instance = await loader.InstantiateAsync("Prefabs/Player");

// Cleanup
loader.Dispose();
```

### Custom Pool Factory

```csharp
using AddressableManager.Pooling;

public class MyPoolFactory : IPoolFactory
{
    public IObjectPool<T> CreatePool<T>(
        Func<T> createFunc,
        Action<T> onGet,
        Action<T> onRelease,
        Action<T> onDestroy,
        int maxSize) where T : class
    {
        // Your implementation
    }
}

// Set factory
AddressablesFacade.Instance.SetPoolFactory(new MyPoolFactory());
```

### Composite Progress Tracking

```csharp
using AddressableManager.Progress;

var composite = new CompositeProgressTracker();
composite.OnProgressChanged += info => Debug.Log($"Progress: {info.Progress}");

var tracker1 = new ProgressTracker();
var tracker2 = new ProgressTracker();

composite.AddTracker(tracker1, weight: 1f);
composite.AddTracker(tracker2, weight: 2f);

// Trackers automatically update composite
```

## 🎯 Best Practices

### 1. Choose the Right Scope

```csharp
// ❌ Bad - Loading UI atlas in session scope
var atlas = await Assets.LoadSession<Sprite>("UI/Atlas");

// ✅ Good - UI atlas in global scope
var atlas = await Assets.Load<Sprite>("UI/Atlas");

// ❌ Bad - Scene material in global scope
var material = await Assets.Load<Material>("Scene1/Floor");

// ✅ Good - Scene material in scene scope
var material = await Assets.LoadScene<Material>("Scene1/Floor");
```

### 2. Use Pooling for Frequently Spawned Objects

```csharp
// ❌ Bad - Instantiating bullets without pooling
for (int i = 0; i < 100; i++)
{
    var bullet = await loader.InstantiateAsync("Projectiles/Bullet");
}

// ✅ Good - Pool bullets
await Assets.CreatePool("Projectiles/Bullet", preloadCount: 20);
for (int i = 0; i < 100; i++)
{
    var bullet = Assets.Spawn("Projectiles/Bullet", position);
}
```

### 3. Show Progress for Large Downloads

```csharp
// ✅ Good - Show progress for better UX
await Assets.Download("LargeAssetBundle", progress =>
{
    loadingBar.value = progress.Progress;
    speedText.text = $"{progress.DownloadSpeed:F1} KB/s";
    etaText.text = $"ETA: {progress.EstimatedTimeRemaining:F0}s";
});
```

### 4. Cleanup When Needed

```csharp
// Manual cleanup (optional - scopes auto-cleanup)
Assets.ClearCache();      // Clear global cache
Assets.ClearPools();      // Clear all pools
Assets.EndSession();      // End session and cleanup
```

## 🔧 Troubleshooting

### Asset Not Loading?

```csharp
var handle = await Assets.Load<Sprite>("UI/Icon");
if (handle == null || !handle.IsValid)
{
    Debug.LogError("Asset not found! Check:");
    Debug.LogError("1. Address is correct");
    Debug.LogError("2. Asset is marked as Addressable");
    Debug.LogError("3. Addressable groups are built");
}
```

### Pool Not Found?

```csharp
// Create pool before spawning
await Assets.CreatePool("Prefabs/Enemy");

// Then spawn
var enemy = Assets.Spawn("Prefabs/Enemy", position);
```

### Session Assets Not Releasing?

```csharp
// Make sure to end session
Assets.EndSession();

// Or check if session is active
if (SessionAssetScope.Instance != null)
{
    Debug.Log("Session is active");
}
```

## 📊 Performance Tips

1. **Preload pools during loading screens**
   ```csharp
   await Assets.CreatePool("Enemies/Zombie", preloadCount: 20);
   ```

2. **Download bundles before gameplay**
   ```csharp
   await Assets.Download("Level1Assets");
   ```

3. **Use labels for batch loading**
   ```csharp
   var handles = await loader.LoadAssetsByLabelAsync<Sprite>("UI");
   ```

4. **Monitor cache stats**
   ```csharp
   var stats = loader.GetCacheStats();
   Debug.Log($"Cached: {stats.cachedAssets}, Active: {stats.activeHandles}");
   ```

---

# Design Decisions & Limitations

## ✅ Automatic Monitoring (v2.1+)

**All asset loading operations are automatically monitored in the Editor!**

```csharp
// Everything is automatically tracked in Dashboard:
var sprite = await Assets.Load<Sprite>("UI/Logo"); // ✅ Monitored
var config = await GlobalAssetScope.Instance.Loader.LoadAssetAsync<Config>("Data/Config"); // ✅ Monitored
var loader = new AssetLoader("MyScope");
var handle = await loader.LoadAssetAsync<Texture>("Textures/Icon"); // ✅ Monitored
```

**How it works:**
- AssetLoader automatically reports to Dashboard when loading assets (Editor-only)
- Each scope passes its name to AssetLoader for proper tracking
- Zero overhead in builds (monitoring code is `#if UNITY_EDITOR`)
- No need for special "Monitored" methods anymore

**Deprecated Extensions:**
- `LoadAssetAsync()` - NO LONGER NEEDED (will show obsolete warning)
- Just use `LoadAssetAsync()` directly - monitoring is built-in!

---

## ⚠️ Known Limitations & Workarounds

### Issue: Scopes Are Singletons - Not Flexible

**Problem**:
```csharp
// Only 1 Session Scope possible:
var session = SessionAssetScope.Instance;

// But what if I want multiple sessions?
// - PlayerSession (player inventory)
// - GameSession (current game state)
// - MatchSession (multiplayer match data)
```

**Why This Design**:
- Original design assumed: 1 global, 1 session, 1 scene, multiple hierarchy
- Simplicity for basic use cases
- Singleton pattern prevents multiple global scopes

**Current Limitations**:
```csharp
// ❌ Can't do this:
var playerSession = new SessionAssetScope("PlayerSession");
var gameSession = new SessionAssetScope("GameSession");

// ❌ All SessionAssetScope share same loader!
```

**Solution: Use ScopeManager**

For complex apps needing multiple scopes, use the custom `ScopeManager` class (see [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md#scopemanager-for-complex-apps) for full examples):

```csharp
using AddressableManager.Managers;
using AddressableManager.Loaders;

public class ScopeManager
{
    private static ScopeManager _instance;
    public static ScopeManager Instance => _instance ??= new ScopeManager();

    private Dictionary<string, AssetLoader> _loaders = new();

    // Create custom scope with unique ID
    public AssetLoader GetOrCreateScope(string scopeId)
    {
        if (!_loaders.ContainsKey(scopeId))
        {
            _loaders[scopeId] = new AssetLoader();
            Debug.Log($"Created scope: {scopeId}");
        }

        return _loaders[scopeId];
    }

    // Clear specific scope
    public void ClearScope(string scopeId)
    {
        if (_loaders.TryGetValue(scopeId, out var loader))
        {
            loader.ClearCache();
            loader.Dispose();
            _loaders.Remove(scopeId);
        }
    }

    // Clear all except specific scopes
    public void ClearAllExcept(params string[] keepScopes)
    {
        var toRemove = _loaders.Keys.Where(k => !keepScopes.Contains(k)).ToList();
        foreach (var key in toRemove)
        {
            ClearScope(key);
        }
    }
}

// Usage:
var scopeManager = ScopeManager.Instance;

// Create multiple sessions
var playerLoader = scopeManager.GetOrCreateScope("PlayerSession");
var gameLoader = scopeManager.GetOrCreateScope("GameSession");
var matchLoader = scopeManager.GetOrCreateScope("MatchSession");

// Load into specific session (automatically monitored)
var playerData = await playerLoader.LoadAssetAsync<PlayerData>("Data/PlayerProfile");

// Clear specific session
scopeManager.ClearScope("MatchSession");
```

**Benefits**:
- ✅ Multiple sessions/scopes with unique IDs
- ✅ Full control over lifecycle
- ✅ Automatic monitoring in Dashboard
- ✅ Dashboard shows each scope separately
- ✅ No singleton limitations

---

## 🎯 Recommended Architecture

### For Simple Apps (Use Built-in Scopes)

```csharp
using AddressableManager.Scopes;
using AddressableManager.Loaders;

public class SimpleGame : MonoBehaviour
{
    async void Start()
    {
        // Use singleton scopes (automatically monitored in Dashboard)
        var global = GlobalAssetScope.Instance;
        var session = SessionAssetScope.Instance;

        // Load assets - automatically tracked in Dashboard!
        var logo = await global.Loader.LoadAssetAsync<Sprite>("UI/Logo");
        var playerData = await session.Loader.LoadAssetAsync<PlayerData>("Data/Player");
    }
}
```

**Good for**:
- Prototypes
- Small games
- Single-player games
- Simple asset management

---

### For Complex Apps (Custom Scope Manager)

```csharp
public class ComplexGame : MonoBehaviour
{
    private ScopeManager _scopes;

    async void Start()
    {
        _scopes = ScopeManager.Instance;

        // Create multiple scopes
        var globalLoader = _scopes.GetOrCreateScope("Global");
        var playerLoader = _scopes.GetOrCreateScope("PlayerSession");
        var gameLoader = _scopes.GetOrCreateScope("GameSession");
        var matchLoader = _scopes.GetOrCreateScope("MatchSession");

        // Load assets - automatically tracked in Dashboard!
        var logo = await globalLoader.LoadAssetAsync<Sprite>("UI/Logo");
        var inventory = await playerLoader.LoadAssetAsync<InventoryData>("Data/Inventory");
    }

    void OnApplicationQuit()
    {
        // Cleanup
        _scopes.ClearAllExcept("Global");
    }

    void OnMatchEnd()
    {
        // Clear only match data
        _scopes.ClearScope("MatchSession");
    }
}
```

**Good for**:
- Large games
- Multiplayer games
- Multiple independent systems
- Fine-grained control

---

## 🔧 Why These Design Choices?

### Monitoring Is Automatic (v2.1+)

**Why automatic monitoring?**:
1. **Simplicity**: No need to remember special methods
2. **Complete Data**: Dashboard always has full picture
3. **Zero Overhead in Builds**: Monitoring code is `#if UNITY_EDITOR` only
4. **Consistent API**: Same methods work with and without monitoring

**How it's implemented**:
- AssetLoader constructor accepts optional `scopeName` parameter
- All load methods have monitoring calls wrapped in `#if UNITY_EDITOR`
- Scopes automatically pass their name to AssetLoader
- Zero performance impact in builds (code is completely stripped)

### Scopes Are Singletons (By Default)

**Reasons**:
1. **Simplicity**: Easy to use for beginners
2. **Prevent Mistakes**: Can't accidentally create multiple global scopes
3. **Common Case**: Most games need 1 global, 1 session

**Trade-off**:
- ❌ Not flexible for complex apps
- ✅ Simple API for 80% of use cases
- ✅ Users can create custom managers for advanced needs

---

## ✅ Summary

### Current State (v2.1)

**Monitoring**:
- ✅ **Automatic** - all loads are tracked in Dashboard (Editor-only)
- ✅ Zero overhead in builds
- ✅ No special methods needed
- ✅ Facade, Scopes, and custom loaders all work

**Scopes**:
- ⚠️ Singletons (not flexible for complex apps)
- ✅ Simple API for basic cases
- ✅ Can create custom ScopeManager for advanced needs

### Recommended Approach

**For Most Projects**:
```csharp
// Everything is automatically monitored!
var sprite = await Assets.Load<Sprite>("UI/Logo"); // ✅ Tracked
var handle = await GlobalAssetScope.Instance.Loader.LoadAssetAsync<T>(address); // ✅ Tracked
```

**For Complex Projects**:
```csharp
// Create custom ScopeManager - also automatically monitored
var loader = ScopeManager.Instance.GetOrCreateScope("PlayerSession");
var handle = await loader.LoadAssetAsync<T>(address); // ✅ Tracked
```

### What To Avoid

❌ **Don't use deprecated .Monitored() extensions**
```csharp
// Old way (deprecated):
var sprite = await loader.LoadAssetAsync<Sprite>(address, scope);

// New way (automatic):
var sprite = await loader.LoadAssetAsync<Sprite>(address); // ✅
```

❌ **Don't expect multiple sessions from built-in scopes**
```csharp
// Won't work - singleton:
var session1 = SessionAssetScope.Instance;
var session2 = SessionAssetScope.Instance; // Same as session1!

// Solution: Use ScopeManager for multiple sessions
```

---

# Performance & Memory

## Performance Considerations

### 1. Caching Strategy
- **Scope-level caching**: Each scope has isolated cache
- **Reference counting**: Prevents premature unloading
- **Automatic cleanup**: Scopes auto-release on lifecycle end

### 2. Pooling Benefits
- **Reduces GC pressure**: Reuses objects instead of creating/destroying
- **Faster spawning**: Pre-instantiated objects
- **Configurable limits**: Max pool size prevents memory bloat

### 3. Async/Await
- **Non-blocking**: UI remains responsive during loads
- **Efficient**: Uses Unity's async system
- **Cancellable**: Can be cancelled if needed

## Memory Management

### Reference Counting Flow

```
Load Asset
   │
   └─► AssetHandle created (refCount = 1)
         │
         ├─► User calls Retain() ──► refCount++
         │
         ├─► User calls Release() ──► refCount--
         │     │
         │     └─► refCount == 0 ──► Dispose handle
         │                             │
         │                             └─► Addressables.Release()
         │
         └─► Scope disposed ──► Force release all handles
```

### Automatic Cleanup Triggers

1. **Scope disposal** (Scene/Session/Hierarchy)
2. **Manual `ClearCache()` call**
3. **Reference count reaches 0**
4. **Application quit**

## Thread Safety

- **AssetLoader**: Not thread-safe, use from main thread
- **Progress events**: Fired on main thread
- **Pooling**: Spawn/Despawn from main thread only
- **Async operations**: Unity's async system is thread-safe

---

# Extension Points

The system is designed for easy extension:

## 1. Custom Pool Implementation

Implement `IPoolFactory` and `IObjectPool<T>`:

```csharp
public class MyPoolFactory : IPoolFactory
{
    public IObjectPool<T> CreatePool<T>(/*...*/) where T : class
    {
        return new MyPoolAdapter<T>(/*...*/);
    }
}

public class MyPoolAdapter<T> : IObjectPool<T>
{
    public T Get() { /* ... */ }
    public void Release(T item) { /* ... */ }
    // ...
}

// Use it:
AddressablesFacade.Instance.SetPoolFactory(new MyPoolFactory());
```

## 2. Custom Scope

Extend `BaseAssetScope` or implement `IAssetScope`:

```csharp
public class MyCustomScope : BaseAssetScope
{
    public MyCustomScope() : base("Custom") { }

    // Override lifecycle methods as needed
}
```

## 3. Custom Progress Tracker

Implement `IProgressTracker`:

```csharp
public class MyProgressTracker : IProgressTracker
{
    public event Action<ProgressInfo> OnProgressChanged;

    public void UpdateProgress(float progress, string operation)
    {
        var info = new ProgressInfo { Progress = progress, CurrentOperation = operation };
        OnProgressChanged?.Invoke(info);
    }
}
```

---

## 📚 Additional Documentation

- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) - Dashboard, Inspectors, Configs, and UI components
- [MONITORING_GUIDE.md](MONITORING_GUIDE.md) - Complete monitoring guide with integration details
- [CHANGELOG.md](CHANGELOG.md) - Version history and release notes

---

**Version**: 2.2.0
**Last Updated**: January 2025

For issues or questions, please refer to the documentation or examine existing components to see how they work!
