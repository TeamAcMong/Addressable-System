# Quick Reference Guide

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

## 🎨 Integration Examples

### With VContainer

```csharp
public class GameInstaller : LifetimeScope
{
    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterComponentInHierarchy<AddressablesFacade>();
    }
}
```

### With Zenject

```csharp
public class GameInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        Container.Bind<AddressablesFacade>()
            .FromComponentInHierarchy()
            .AsSingle();
    }
}
```

### With UniTask

```csharp
using Cysharp.Threading.Tasks;

// Convert Task to UniTask
var sprite = await Assets.Load<Sprite>("UI/Icon").AsUniTask();
```

## 📝 Code Templates

### Loading Screen

```csharp
public class LoadingScreen : MonoBehaviour
{
    public Slider progressBar;
    public Text statusText;

    public async void LoadLevel(string levelName)
    {
        // Start session
        Assets.StartSession();

        // Download with progress
        await Assets.Download($"Levels/{levelName}", progress =>
        {
            progressBar.value = progress.Progress;
            statusText.text = $"Loading... {progress.Progress * 100:F0}%";
        });

        // Load level assets
        var levelData = await Assets.LoadSession<LevelData>($"Levels/{levelName}Data");

        // Scene is ready
        Debug.Log("Level loaded!");
    }
}
```

### Enemy Spawner

```csharp
public class EnemySpawner : MonoBehaviour
{
    private async void Start()
    {
        // Create pool
        await Assets.CreatePool("Enemies/Zombie", preloadCount: 10);
    }

    public void SpawnEnemy(Vector3 position)
    {
        var enemy = Assets.Spawn("Enemies/Zombie", position);

        // Setup enemy
        var ai = enemy.GetComponent<EnemyAI>();
        ai.Initialize();
    }

    public void DespawnEnemy(GameObject enemy)
    {
        Assets.Despawn("Enemies/Zombie", enemy);
    }
}
```

### Character Weapon System

```csharp
public class Character : MonoBehaviour
{
    private HierarchyAssetScope _scope;

    private void Awake()
    {
        // Assets tied to this character's lifetime
        _scope = HierarchyAssetScope.AddTo(gameObject);
    }

    public async void EquipWeapon(string weaponAddress)
    {
        var weaponPrefab = await _scope.Loader.LoadAssetAsync<GameObject>(weaponAddress);

        if (weaponPrefab.IsValid)
        {
            var weapon = Instantiate(weaponPrefab.Asset, weaponSlot);
            // Weapon auto-released when character destroyed
        }
    }
}
```
