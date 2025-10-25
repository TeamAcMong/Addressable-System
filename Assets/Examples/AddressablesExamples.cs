using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using AddressableManager.Facade;
using AddressableManager.Scopes;
using AddressableManager.Progress;

/// <summary>
/// Comprehensive examples demonstrating all features of the Game Addressables System
/// </summary>
public class AddressablesExamples : MonoBehaviour
{
    [Header("UI References (optional)")]
    public Slider progressBar;
    public Text statusText;
    public Image iconImage;

    private void Start()
    {
        // Run examples
        StartCoroutine(RunExamples());
    }

    private IEnumerator RunExamples()
    {
        Debug.Log("=== Game Addressables System Examples ===");

        // Wait a frame for setup
        yield return null;

        // Example 1: Simple Load
        yield return Example1_SimpleLoad();

        yield return new WaitForSeconds(1f);

        // Example 2: Session Management
        yield return Example2_SessionManagement();

        yield return new WaitForSeconds(1f);

        // Example 3: Progress Tracking
        yield return Example3_ProgressTracking();

        yield return new WaitForSeconds(1f);

        // Example 4: Object Pooling
        yield return Example4_ObjectPooling();

        yield return new WaitForSeconds(1f);

        // Example 5: Scene Scope
        yield return Example5_SceneScope();

        yield return new WaitForSeconds(1f);

        // Example 6: Hierarchy Scope
        yield return Example6_HierarchyScope();

        Debug.Log("=== All Examples Complete ===");
    }

    #region Example 1: Simple Load

    private IEnumerator Example1_SimpleLoad()
    {
        Debug.Log("\n--- Example 1: Simple Asset Loading ---");

        // Simple one-liner to load asset
        var task = Assets.Load<Sprite>("UI/Icon");
        yield return new WaitUntil(() => task.IsCompleted);

        var handle = task.Result;
        if (handle != null && handle.IsValid)
        {
            Debug.Log($"✓ Loaded sprite: {handle.Asset.name}");

            // Use the sprite
            if (iconImage != null)
            {
                iconImage.sprite = handle.Asset;
            }

            // Asset is cached and will be auto-released when app closes
            // Or manually release when done:
            // handle.Release();
        }
        else
        {
            Debug.LogWarning("Failed to load sprite (address not found in Addressables)");
        }
    }

    #endregion

    #region Example 2: Session Management

    private IEnumerator Example2_SessionManagement()
    {
        Debug.Log("\n--- Example 2: Session Management ---");

        // Start a gameplay session
        Assets.StartSession();
        Debug.Log("✓ Session started");

        // Load session-specific assets (auto-cleanup when session ends)
        var task = Assets.LoadSession<TextAsset>("Config/LevelConfig");
        yield return new WaitUntil(() => task.IsCompleted);

        var handle = task.Result;
        if (handle != null && handle.IsValid)
        {
            Debug.Log($"✓ Loaded session config: {handle.Asset.name}");
        }

        yield return new WaitForSeconds(2f);

        // End session - all session assets automatically released
        Assets.EndSession();
        Debug.Log("✓ Session ended - all session assets released");
    }

    #endregion

    #region Example 3: Progress Tracking

    private IEnumerator Example3_ProgressTracking()
    {
        Debug.Log("\n--- Example 3: Progress Tracking ---");

        // Load with progress callback
        var task = Assets.Load<Texture2D>("Textures/LargeTexture", OnProgress);
        yield return new WaitUntil(() => task.IsCompleted);

        var handle = task.Result;
        if (handle != null && handle.IsValid)
        {
            Debug.Log($"✓ Loaded texture with progress tracking: {handle.Asset.name}");
        }
        else
        {
            Debug.LogWarning("Texture not found - using mock progress");
            // Simulate progress for demo
            for (float p = 0; p <= 1f; p += 0.1f)
            {
                OnProgress(new ProgressInfo(p, "Mock Loading"));
                yield return new WaitForSeconds(0.1f);
            }
        }
    }

    private void OnProgress(ProgressInfo info)
    {
        Debug.Log($"Progress: {info.Progress * 100:F1}% - {info.CurrentOperation}");

        if (progressBar != null)
        {
            progressBar.value = info.Progress;
        }

        if (statusText != null)
        {
            statusText.text = $"{info.Progress * 100:F0}% - {info.CurrentOperation}";
        }
    }

    #endregion

    #region Example 4: Object Pooling

    private IEnumerator Example4_ObjectPooling()
    {
        Debug.Log("\n--- Example 4: Object Pooling ---");

        // Create a pool (in real game, do this during loading screen)
        var createTask = Assets.CreatePool("Prefabs/Projectile", preloadCount: 5, maxSize: 20);
        yield return new WaitUntil(() => createTask.IsCompleted);

        if (!createTask.Result)
        {
            Debug.LogWarning("Pool creation failed (prefab not found). Skipping pool example.");
            yield break;
        }

        Debug.Log("✓ Pool created with 5 preloaded instances");

        // Spawn multiple objects
        var spawnedObjects = new System.Collections.Generic.List<GameObject>();

        for (int i = 0; i < 10; i++)
        {
            var position = new Vector3(i * 2f, 0, 0);
            var instance = Assets.Spawn("Prefabs/Projectile", position);

            if (instance != null)
            {
                spawnedObjects.Add(instance);
                Debug.Log($"✓ Spawned instance {i + 1} at {position}");
            }
        }

        yield return new WaitForSeconds(2f);

        // Return to pool
        foreach (var obj in spawnedObjects)
        {
            Assets.Despawn("Prefabs/Projectile", obj);
        }

        Debug.Log($"✓ Returned {spawnedObjects.Count} instances to pool");
    }

    #endregion

    #region Example 5: Scene Scope

    private IEnumerator Example5_SceneScope()
    {
        Debug.Log("\n--- Example 5: Scene-Scoped Assets ---");

        // Get or create scene scope (auto-cleanup on scene unload)
        var sceneScope = SceneAssetScope.GetOrCreate();
        Debug.Log($"✓ Scene scope created: {sceneScope.ScopeName}");

        // Load scene-specific asset
        var task = Assets.LoadScene<Material>("Scene/SpecialMaterial");
        yield return new WaitUntil(() => task.IsCompleted);

        var handle = task.Result;
        if (handle != null && handle.IsValid)
        {
            Debug.Log($"✓ Loaded scene material: {handle.Asset.name}");
            Debug.Log("  (Will auto-release when scene unloads)");
        }
        else
        {
            Debug.LogWarning("Scene material not found - but scope is still functional");
        }
    }

    #endregion

    #region Example 6: Hierarchy Scope

    private IEnumerator Example6_HierarchyScope()
    {
        Debug.Log("\n--- Example 6: Hierarchy-Scoped Assets ---");

        // Create a test GameObject
        var testObject = new GameObject("TestCharacter");
        Debug.Log($"✓ Created test GameObject: {testObject.name}");

        // Add hierarchy scope (assets auto-release when GameObject destroyed)
        var hierarchyScope = HierarchyAssetScope.AddTo(testObject);
        Debug.Log($"✓ Added hierarchy scope: {hierarchyScope.ScopeName}");

        // Load character-specific asset
        var task = hierarchyScope.Loader.LoadAssetAsync<AudioClip>("Sounds/CharacterTheme");
        yield return new WaitUntil(() => task.IsCompleted);

        var handle = task.Result;
        if (handle != null && handle.IsValid)
        {
            Debug.Log($"✓ Loaded character audio: {handle.Asset.name}");
        }
        else
        {
            Debug.LogWarning("Audio not found - but hierarchy scope is functional");
        }

        yield return new WaitForSeconds(2f);

        // Destroy GameObject - all hierarchy-scoped assets auto-released
        Destroy(testObject);
        Debug.Log("✓ Destroyed GameObject - all its assets auto-released");
    }

    #endregion

    #region Bonus: Advanced Examples

    private void BonusExample_CustomPoolFactory()
    {
        Debug.Log("\n--- Bonus: Custom Pool Factory ---");

        // You can create your own pool factory
        // Example: Zenject-based pool factory
        /*
        public class ZenjectPoolFactory : IPoolFactory
        {
            private DiContainer _container;

            public ZenjectPoolFactory(DiContainer container)
            {
                _container = container;
            }

            public IObjectPool<T> CreatePool<T>(
                Func<T> createFunc,
                Action<T> onGet,
                Action<T> onRelease,
                Action<T> onDestroy,
                int maxSize) where T : class
            {
                // Use Zenject's MemoryPool or custom implementation
                return new ZenjectPoolAdapter<T>(_container, createFunc, onGet, onRelease, onDestroy, maxSize);
            }
        }

        // Then set it:
        var facade = AddressablesFacade.Instance;
        facade.SetPoolFactory(new ZenjectPoolFactory(container));
        */

        Debug.Log("See code comments for custom pool factory example");
    }

    private void BonusExample_DownloadWithProgress()
    {
        Debug.Log("\n--- Bonus: Download with Progress ---");

        // Download large asset bundles with progress
        /*
        await Assets.Download("LargeBundle", progress =>
        {
            Debug.Log($"Download: {progress.Progress * 100}% at {progress.DownloadSpeed} KB/s");
            Debug.Log($"ETA: {progress.EstimatedTimeRemaining} seconds");
        });
        */

        Debug.Log("See code comments for download example");
    }

    #endregion
}
