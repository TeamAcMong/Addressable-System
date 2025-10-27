# Architecture Documentation

## System Overview

The Game Addressables System is built with **clean architecture principles**, emphasizing separation of concerns, scalability, and maintainability. It consists of multiple layers, each with specific responsibilities.

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

## Design Patterns Employed

### 1. Facade Pattern
**Where**: `AddressablesFacade`, `Assets` static class

**Why**: Provides a simplified, unified interface to the complex subsystems. Users don't need to understand the internals.

**Example**:
```csharp
// Complex internal operation simplified to:
var sprite = await Assets.Load<Sprite>("UI/Icon");
```

### 2. Factory Pattern
**Where**: `IPoolFactory`, `UnityPoolFactory`, `CustomPoolFactory`

**Why**: Allows runtime switching between different pooling implementations without changing client code. Supports DI frameworks like Zenject/VContainer.

**Example**:
```csharp
// Switch from Unity pool to Zenject pool at runtime
facade.SetPoolFactory(new ZenjectPoolFactory(container));
```

### 3. Adapter Pattern
**Where**: `UnityPoolAdapter`, `CustomPoolAdapter`

**Why**: Wraps different pooling APIs into a unified `IObjectPool<T>` interface. Makes the system independent of specific pool implementations.

**Example**:
```csharp
IObjectPool<GameObject> pool = new UnityPoolAdapter<GameObject>(...);
// Or
IObjectPool<GameObject> pool = new CustomPoolAdapter<GameObject>(...);
```

### 4. Observer Pattern
**Where**: `IProgressTracker`, `ProgressTracker`, `CompositeProgressTracker`

**Why**: Decouples progress tracking from loading operations. Multiple observers can listen to progress without tight coupling.

**Example**:
```csharp
tracker.OnProgressChanged += info => {
    progressBar.value = info.Progress;
};
```

### 5. Repository Pattern
**Where**: `AssetLoader` with caching

**Why**: Provides a clean abstraction over data access (Addressables API). Handles caching, deduplication, and lifecycle management.

**Example**:
```csharp
// AssetLoader acts as repository
var handle = await loader.LoadAssetAsync<Sprite>("UI/Icon");
// Subsequent loads use cache
```

### 6. Strategy Pattern
**Where**: Pool factory selection

**Why**: Allows selecting different pooling strategies at runtime based on context (e.g., mobile vs PC, early vs late game).

### 7. Singleton Pattern (with DontDestroyOnLoad)
**Where**: `GlobalAssetScope`, `SessionAssetScope`, `AddressablesFacade`

**Why**: Ensures single instance for global systems that persist across scenes.

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

### Pooling System

#### `IObjectPool<T>`
- **Responsibility**: Generic pool interface
- **Methods**: Get, Release, Clear, GetStats
- **Why Generic**: Works with any pooling implementation

#### `IPoolFactory`
- **Responsibility**: Create pools
- **Why**: Allows dependency injection of different pool implementations

#### `AddressablePoolManager`
- **Responsibility**: Manage pools for addressable assets
- **Features**:
  - Pool creation with preloading
  - Spawn/Despawn
  - Runtime factory switching
  - Pool statistics

### Progress Tracking

#### `IProgressTracker`
- **Responsibility**: Track and report loading progress
- **Features**:
  - Observer pattern events
  - Progress info with operation details
  - Download speed & ETA calculation

#### `CompositeProgressTracker`
- **Responsibility**: Aggregate progress from multiple operations
- **Use Case**: Batch loading, multi-stage loading screens

### Facade Layer

#### `AddressablesFacade`
- **Responsibility**: Unified high-level interface
- **Features**:
  - Manages all scopes
  - Manages pool system
  - Provides convenient methods
  - Singleton with DontDestroyOnLoad

#### `Assets` (Static Class)
- **Responsibility**: Ultra-simple one-liner API
- **Features**:
  - Static methods for common operations
  - Minimal boilerplate
  - Perfect for prototyping

## Data Flow Examples

### Example 1: Simple Load

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

### Example 2: Pooled Spawn

```
User Code
   │
   └─► Assets.Spawn("Enemies/Zombie", position)
         │
         └─► AddressablesFacade.Spawn()
               │
               └─► AddressablePoolManager.Spawn()
                     │
                     ├─► Find pool for "Enemies/Zombie"
                     │
                     └─► IObjectPool<GameObject>.Get()
                           │
                           ├─► Pool has instance ──► Return from pool
                           │                          │
                           │                          └─► Call onGet (SetActive)
                           │
                           └─► Pool empty ──► Create new instance
                                              │
                                              └─► Return new instance
```

### Example 3: Session Lifecycle

```
User: Assets.StartSession()
   │
   └─► SessionAssetScope.StartSession()
         │
         └─► Create new GameObject with SessionAssetScope
               │
               └─► DontDestroyOnLoad
                     │
                     └─► Create AssetLoader

User: Assets.LoadSession<T>("address")
   │
   └─► SessionAssetScope.Loader.LoadAssetAsync()
         │
         └─► Load asset (cached in session loader)

User: Assets.EndSession()
   │
   └─► SessionAssetScope.EndSession()
         │
         └─► Dispose scope
               │
               ├─► Loader.ClearCache()
               │     │
               │     └─► Release all handles
               │           │
               │           └─► Addressables.Release()
               │
               └─► Destroy GameObject
```

## Extension Points

The system is designed for easy extension:

### 1. Custom Pool Implementation

Implement `IPoolFactory` and `IObjectPool<T>`:

```csharp
public class MyPoolFactory : IPoolFactory { }
public class MyPoolAdapter<T> : IObjectPool<T> { }
```

### 2. Custom Scope

Extend `BaseAssetScope` or implement `IAssetScope`:

```csharp
public class MyCustomScope : BaseAssetScope
{
    public MyCustomScope() : base("Custom") { }
}
```

### 3. Custom Progress Tracker

Implement `IProgressTracker`:

```csharp
public class MyProgressTracker : IProgressTracker
{
    // Custom implementation
}
```

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

## Testing Strategies

### Unit Testing
```csharp
[Test]
public async Task TestAssetLoading()
{
    var loader = new AssetLoader();
    var handle = await loader.LoadAssetAsync<Sprite>("Test/Sprite");
    Assert.IsTrue(handle.IsValid);
}
```

### Integration Testing
```csharp
[UnityTest]
public IEnumerator TestSessionLifecycle()
{
    Assets.StartSession();
    var task = Assets.LoadSession<TextAsset>("Test/Config");
    yield return new WaitUntil(() => task.IsCompleted);
    Assets.EndSession();
    // Verify cleanup
}
```

## Future Enhancements

Potential areas for extension:

1. **Asset preloading strategies** (by tags, dependencies)
2. **Memory budget management** (auto-unload when over limit)
3. **Analytics integration** (track load times, cache hit rates)
4. **Editor tools** (visualize cache, pool stats)
5. **Addressable asset validation** (check for missing references)
6. **Retry logic** for failed downloads
7. **Priority-based loading** (high/low priority queues)

## Conclusion

This architecture prioritizes:

- **Separation of Concerns**: Each component has single responsibility
- **Open/Closed Principle**: Open for extension, closed for modification
- **Dependency Inversion**: Depends on abstractions, not concretions
- **Clean Code**: Readable, maintainable, well-documented
- **Scalability**: Can grow from small indie to AAA game

The system is production-ready while remaining flexible for future needs.
