# Namespace Change - Unity Addressables System

## ✅ COMPLETED: Namespace Renamed to Avoid Conflicts

**Date**: January 2025
**Version**: 1.0.2
**Change Type**: Breaking (namespace only, API unchanged)

---

## 🔄 What Changed

### Old Namespace (v1.0.0 - v1.0.1)
```csharp
namespace Game.Addressables
```

**Problem**: Could conflict with Unity's `UnityEngine.AddressableAssets.Addressables` static class

### New Namespace (v1.0.2+)
```csharp
namespace AddressableManager
```

**Benefits**:
- ✅ No conflicts with Unity APIs
- ✅ Clear, descriptive name
- ✅ Single word - easy to type
- ✅ Unique identifier

---

## 📊 Impact Summary

| Component | Old | New | Status |
|-----------|-----|-----|--------|
| **Core** | `Game.Addressables.Core` | `AddressableManager.Core` | ✅ Updated |
| **Loaders** | `Game.Addressables.Loaders` | `AddressableManager.Loaders` | ✅ Updated |
| **Scopes** | `Game.Addressables.Scopes` | `AddressableManager.Scopes` | ✅ Updated |
| **Pooling** | `Game.Addressables.Pooling` | `AddressableManager.Pooling` | ✅ Updated |
| **Progress** | `Game.Addressables.Progress` | `AddressableManager.Progress` | ✅ Updated |
| **Facade** | `Game.Addressables.Facade` | `AddressableManager.Facade` | ✅ Updated |

---

## 📝 Migration Guide

### For New Projects
Just use the new namespace:

```csharp
using AddressableManager.Facade;

var sprite = await Assets.Load<Sprite>("UI/Icon");
```

### For Existing Projects

**Step 1**: Update your using statements

```csharp
// OLD - Remove these
using Game.Addressables.Facade;
using Game.Addressables.Scopes;
using Game.Addressables.Progress;

// NEW - Use these
using AddressableManager.Facade;
using AddressableManager.Scopes;
using AddressableManager.Progress;
```

**Step 2**: That's it! API is identical

```csharp
// All these still work exactly the same:
var sprite = await Assets.Load<Sprite>("UI/Icon");
await Assets.CreatePool("Enemies/Zombie", 10);
var enemy = Assets.Spawn("Enemies/Zombie", position);
Assets.Despawn("Enemies/Zombie", enemy);
```

---

## 🔍 What Was Changed

### Code Files (19 files)

**All Runtime C# Files**:
```bash
# Changed namespace declarations
namespace AddressableManager.Core
namespace AddressableManager.Loaders
namespace AddressableManager.Scopes
namespace AddressableManager.Pooling
namespace AddressableManager.Progress
namespace AddressableManager.Facade
```

**Changed using statements**:
```csharp
using AddressableManager.Core;
using AddressableManager.Loaders;
using AddressableManager.Scopes;
// etc...
```

### Assembly Definition

**File renamed**:
- `Game.Addressables.asmdef` → `AddressableManager.asmdef`

**Content updated**:
```json
{
    "name": "AddressableManager",
    "rootNamespace": "AddressableManager",
    "references": [
        "Unity.ResourceManager",
        "Unity.Addressables"
    ]
}
```

### Documentation

**All markdown files updated**:
- README.md
- QUICK_REFERENCE.md
- ARCHITECTURE.md
- BUGFIXES.md
- IMPLEMENTATION_COMPLETE.md
- SUMMARY.md

### Examples

**AddressablesExamples.cs updated**:
```csharp
using AddressableManager.Facade;
using AddressableManager.Scopes;
using AddressableManager.Progress;
```

---

## ✅ What Stayed the Same

### Package Identifier
```json
{
  "name": "com.game.addressables",  // ← Still the same
  "displayName": "Game Addressables System"
}
```

Package name doesn't need to match namespace.

### Public API
**100% identical** - Zero breaking changes to usage:

```csharp
// All these work exactly as before:
Assets.Load<T>()
Assets.LoadSession<T>()
Assets.LoadScene<T>()
Assets.Spawn()
Assets.Despawn()
Assets.StartSession()
Assets.EndSession()
// etc...
```

### Functionality
- ✅ Scope management - unchanged
- ✅ Object pooling - unchanged
- ✅ Progress tracking - unchanged
- ✅ All features - unchanged

---

## 🚀 Quick Fix Script

If you have existing code using old namespace:

### Option 1: Manual Find & Replace

In your IDE (VS Code, Rider, Visual Studio):

1. **Find**: `using Game.Addressables`
2. **Replace**: `using AddressableManager`
3. **Replace All**

### Option 2: Automated Script

```bash
# For all your game scripts
cd YourGameProject/Assets/Scripts
find . -name "*.cs" -exec sed -i 's/using Game\.Addressables/using AddressableManager/g' {} +
```

---

## 📚 Code Examples with New Namespace

### Simple Load
```csharp
using AddressableManager.Facade;

public class GameManager : MonoBehaviour
{
    async void Start()
    {
        var sprite = await Assets.Load<Sprite>("UI/Icon");
        // Use sprite...
    }
}
```

### Session Management
```csharp
using AddressableManager.Facade;

public class LevelLoader : MonoBehaviour
{
    public async void LoadLevel()
    {
        Assets.StartSession();

        var levelData = await Assets.LoadSession<LevelConfig>("Levels/Level1");
        // Use levelData...

        // When leaving level
        Assets.EndSession();
    }
}
```

### Object Pooling
```csharp
using AddressableManager.Facade;
using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
    async void Start()
    {
        await Assets.CreatePool("Enemies/Zombie", preloadCount: 10);
    }

    public void SpawnEnemy(Vector3 position)
    {
        var zombie = Assets.Spawn("Enemies/Zombie", position);
    }

    public void DespawnEnemy(GameObject enemy)
    {
        Assets.Despawn("Enemies/Zombie", enemy);
    }
}
```

### Progress Tracking
```csharp
using AddressableManager.Facade;
using AddressableManager.Progress;
using UnityEngine;
using UnityEngine.UI;

public class LoadingScreen : MonoBehaviour
{
    public Slider progressBar;

    public async void LoadAssets()
    {
        await Assets.Load<Texture2D>("LargeTexture", progress =>
        {
            progressBar.value = progress.Progress;
        });
    }
}
```

### Scope-Based Loading
```csharp
using AddressableManager.Scopes;
using UnityEngine;

public class Character : MonoBehaviour
{
    private HierarchyAssetScope _scope;

    void Awake()
    {
        // Assets tied to this GameObject's lifetime
        _scope = HierarchyAssetScope.AddTo(gameObject);
    }

    public async void EquipWeapon(string weaponAddress)
    {
        var weaponHandle = await _scope.Loader.LoadAssetAsync<GameObject>(weaponAddress);

        if (weaponHandle.IsValid)
        {
            var weapon = Instantiate(weaponHandle.Asset, weaponSlot);
            // Weapon auto-released when character destroyed
        }
    }
}
```

---

## 🔧 Technical Details

### Namespace Structure

```
AddressableManager
├── Core                    (IAssetHandle, AssetHandle)
├── Loaders                 (AssetLoader)
├── Scopes                  (Global, Session, Scene, Hierarchy)
├── Pooling                 (IObjectPool, IPoolFactory, Adapters)
├── Progress                (IProgressTracker, ProgressTracker)
└── Facade                  (AddressablesFacade, Assets)
```

### Assembly Definition

```json
{
    "name": "AddressableManager",
    "rootNamespace": "AddressableManager",
    "references": [
        "Unity.ResourceManager",    // Unity's resource management
        "Unity.Addressables"        // Unity's Addressables (no conflict!)
    ]
}
```

---

## ❓ FAQ

### Q: Do I need to rebuild Addressables?
**A**: No. This is only a code namespace change.

### Q: Will this break my existing project?
**A**: Only if you're already using the old namespace. Just update your `using` statements.

### Q: What about saved prefabs/ScriptableObjects?
**A**: Unity handles namespace changes automatically for MonoBehaviours and ScriptableObjects.

### Q: Is the package.json name changing?
**A**: No. Package ID stays `com.game.addressables`.

### Q: Do I need to update Unity packages?
**A**: No. Unity Addressables package version stays the same.

### Q: Can I still call it "Game Addressables System"?
**A**: Yes! Display name is unchanged. Only internal namespace is different.

---

## 🎯 Verification Checklist

After updating, verify:

- [ ] No compilation errors in Unity Console
- [ ] IntelliSense shows `AddressableManager` namespace
- [ ] Example scripts compile
- [ ] Assets load successfully at runtime
- [ ] No conflicts with Unity's Addressables API

### Quick Test

```csharp
using AddressableManager.Facade;
using UnityEngine;

public class NamespaceTest : MonoBehaviour
{
    async void Start()
    {
        // This should compile without errors
        var handle = await Assets.Load<Sprite>("TestIcon");

        if (handle != null && handle.IsValid)
        {
            Debug.Log("✅ Namespace change successful!");
        }
    }
}
```

---

## 📈 Version History

| Version | Namespace | Status |
|---------|-----------|--------|
| v1.0.0 | `Game.AddressableSystem` | ❌ Deprecated (had bugs) |
| v1.0.1 | `Game.Addressables` | ⚠️ Deprecated (conflicts) |
| v1.0.2+ | `AddressableManager` | ✅ Current |

---

## 🎉 Summary

**What You Need to Do**:
1. Update `using` statements in your code
2. That's it!

**What Changed**:
- Namespace only

**What Didn't Change**:
- API
- Functionality
- Package name
- Features
- Performance

**Result**:
- ✅ No conflicts with Unity
- ✅ Clearer namespace
- ✅ Same great features

---

**Status**: ✅ **ALL FILES UPDATED AND READY**

The system is now using `AddressableManager` namespace and is ready for production use with zero conflicts!

---

*Last Updated: January 2025*
*Version: 1.0.2*
