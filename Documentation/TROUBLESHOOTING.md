# Troubleshooting Guide

**Solutions to common issues and error messages**

This guide helps you diagnose and fix common problems with the Addressable Manager system.

---

## Quick Diagnosis Table

| Symptom | Likely Cause | Section |
|---------|-------------|---------|
| Assets not loading | Address typo or scope issue | [Runtime Loading](#runtime-loading-issues) |
| Rules not matching | Filter misconfiguration | [Rule Matching](#rule-matching-issues) |
| Editor window blank | UXML file missing | [Editor Windows](#editor-window-issues) |
| Slow performance | Too many tracked assets | [Performance](#performance-issues) |
| Memory leak | Handles not released | [Memory Management](#memory-management-issues) |
| Compilation errors | Missing dependencies | [Compilation](#compilation-errors) |
| Version errors | Git/Build config | [Versioning](#versioning-issues) |

---

## Table of Contents

1. [Runtime Loading Issues](#runtime-loading-issues)
2. [Rule Matching Issues](#rule-matching-issues)
3. [Editor Window Issues](#editor-window-issues)
4. [Performance Issues](#performance-issues)
5. [Memory Management Issues](#memory-management-issues)
6. [Compilation Errors](#compilation-errors)
7. [Versioning Issues](#versioning-issues)
8. [CI/CD Issues](#cicd-issues)
9. [Platform-Specific Issues](#platform-specific-issues)
10. [Data Corruption](#data-corruption)

---

## Runtime Loading Issues

### Issue: "Failed to load asset" Error

**Symptoms**:
```
InvalidKeyException: Exception of type 'UnityEngine.AddressableAssets.InvalidKeyException' was thrown
No locations found for key: 'my_asset'
```

**Causes**:
1. Address doesn't exist
2. Asset not marked as addressable
3. Typo in address string
4. Label filter excludes asset

**Solutions**:

**Step 1: Verify Address Exists**
```csharp
// Check if address exists
var locations = await Addressables.LoadResourceLocationsAsync("my_asset");
if (locations.Count == 0)
{
    Debug.LogError("Address 'my_asset' not found");
}
```

**Step 2: Check Addressable Settings**
```
1. Window > Asset Management > Addressables > Groups
2. Search for your asset
3. Verify it's in an addressable group
4. Check the address is correct (case-sensitive)
```

**Step 3: Rebuild Addressables**
```
1. Window > Asset Management > Addressables > Groups
2. Build > New Build > Default Build Script
3. Try loading again
```

**Step 4: Use Layout Viewer**
```
1. Window > Addressable Manager > Layout Viewer
2. Search for your asset
3. Check computed address and labels
```

---

### Issue: Loading Returns Null

**Symptoms**:
```csharp
var asset = await AddressableManager.LoadAsync<Texture2D>("texture");
// asset is null, but no exception thrown
```

**Causes**:
1. Wrong type specified
2. Asset not ready
3. Scope lifetime ended

**Solutions**:

**Check Asset Type**:
```csharp
// If asset is actually a Sprite, not Texture2D:
var sprite = await AddressableManager.LoadAsync<Sprite>("texture");
```

**Verify Asset Loaded**:
```csharp
try
{
    var handle = await AddressableManager.LoadAsync<Texture2D>("texture");
    if (handle.IsValid())
    {
        Debug.Log("Asset loaded successfully");
    }
}
catch (Exception ex)
{
    Debug.LogError($"Failed to load: {ex.Message}");
}
```

**Check Scope Lifetime**:
```csharp
// BAD: Scope disposed before using asset
using var scope = AddressableManager.CreateSessionScope();
var texture = await scope.LoadAsync<Texture2D>("texture");
// scope disposed here

// Later...
myRenderer.material.mainTexture = texture; // May fail!

// GOOD: Keep scope alive while using asset
_scope = AddressableManager.CreateSessionScope();
_texture = await _scope.LoadAsync<Texture2D>("texture");
// Use texture...
// Dispose scope when done
```

---

### Issue: "Scope Not Found" Exception

**Symptoms**:
```
ScopeNotFoundException: Scope 'SessionScope' not found or has been disposed
```

**Causes**:
1. Scope disposed too early
2. Trying to use asset after scope cleanup
3. Scope name typo

**Solutions**:

**Use Correct Scope Lifetime**:
```csharp
// Scene scope - lives until scene unloads
using var scope = AddressableManager.CreateSceneScope();
var prefab = await scope.LoadAsync<GameObject>("enemy");
Instantiate(prefab); // Safe - scene keeps it alive

// Session scope - lives until manually disposed
_sessionScope = AddressableManager.CreateSessionScope();
_texture = await _sessionScope.LoadAsync<Texture2D>("logo");
// Keep _sessionScope as field, dispose when appropriate

// Global scope - lives forever (use sparingly)
var globalAsset = await AddressableManager.LoadAsync<T>(
    "persistent_data",
    scope: AddressableScope.Global
);
```

**Check Scope Before Using**:
```csharp
if (AddressableManager.ScopeExists("MyScope"))
{
    var asset = await AddressableManager.LoadAsync<T>("address", scope: "MyScope");
}
else
{
    Debug.LogWarning("Scope 'MyScope' no longer exists");
}
```

---

## Rule Matching Issues

### Issue: Rules Not Matching Any Assets

**Symptoms**:
- Preview Panel shows "No assets match this rule"
- Apply Rules reports 0 assets processed

**Causes**:
1. Filter path incorrect
2. File extensions don't match
3. Asset type mismatch
4. No assets exist at specified path

**Solutions**:

**Verify Path Pattern**:
```
✅ CORRECT: "Assets/UI/**/*.png"
❌ WRONG: "Assets/UI/*.png" (doesn't search subdirectories)
❌ WRONG: "Assets/ui/**/*.png" (case mismatch)
❌ WRONG: "/Assets/UI/**/*.png" (leading slash)
```

**Test Filter Individually**:
```
1. Remove all but one filter from rule
2. Click "Refresh Preview"
3. If it matches, add next filter
4. Find which filter is excluding assets
```

**Check Asset Database**:
```csharp
// Manually check if assets exist
var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/UI" });
Debug.Log($"Found {guids.Length} textures in Assets/UI");

foreach (var guid in guids)
{
    string path = AssetDatabase.GUIDToAssetPath(guid);
    Debug.Log($"Asset: {path}");
}
```

---

### Issue: Wrong Assets Matching

**Symptoms**:
- Preview shows unexpected assets
- Rules applying to wrong files

**Causes**:
1. Filter pattern too broad
2. Multiple rules conflict
3. Priority ordering issue

**Solutions**:

**Make Filter More Specific**:
```
TOO BROAD: "Assets/**/*.png"
BETTER: "Assets/UI/**/*.png"
SPECIFIC: "Assets/UI/Buttons/**/*.png"
```

**Check Rule Priority**:
```
Rules are processed in priority order (highest first)
If two rules match the same asset, higher priority wins

Example:
- Rule A (Priority 200): "Assets/UI/**/*.png" → Group "UI"
- Rule B (Priority 100): "Assets/**/*.png" → Group "All"
Result: UI assets go to "UI" group (Rule A wins)
```

**Use Rule Conflict Detector**:
```
1. Window > Addressable Manager > Layout Viewer
2. Click "Detect Conflicts"
3. Review reported conflicts
4. Adjust priorities or filters
```

---

### Issue: Provider Generating Empty Output

**Symptoms**:
- Preview shows "Empty address generated" error
- Assets have blank addresses

**Causes**:
1. Provider misconfigured
2. Base path incorrect
3. Asset filename issues

**Solutions**:

**Check Provider Settings**:
```
FileNameAddressProvider:
✅ Strip Extension: true
✅ To Lower Case: optional

PathAddressProvider:
✅ Base Directory: "Assets/MyFolder"
❌ Base Directory: "Assets/MyFolder/" (trailing slash may cause issues)
```

**Test Provider Manually**:
```csharp
// Create test provider
var provider = CreateInstance<FileNameAddressProvider>();
provider.Setup();

// Test on known asset
string address = provider.Provide("Assets/UI/button_start.png");
Debug.Log($"Generated address: {address}"); // Should be "button_start"
```

---

## Editor Window Issues

### Issue: Layout Rule Editor Shows Blank

**Symptoms**:
- Window opens but is empty
- Shows "Failed to load UXML file" error

**Causes**:
1. UXML file missing
2. USS stylesheet missing
3. Package not installed correctly

**Solutions**:

**Verify Package Installation**:
```
1. Window > Package Manager
2. Find "Addressable Manager" package
3. If not found, reinstall:
   - Delete Packages/com.game.addressables
   - Reimport package
```

**Check File Exists**:
```
Packages/com.game.addressables/Editor/UI/AddressableManagerWindow.uxml
Packages/com.game.addressables/Editor/UI/Styles.uss
```

**Use Fallback UI**:
```
If UXML fails, window creates fallback IMGUI
This provides basic functionality
Consider reporting the issue
```

---

### Issue: Preview Panel Not Updating

**Symptoms**:
- Click "Refresh Preview" but nothing happens
- Preview shows old data

**Causes**:
1. Filter Setup() not called
2. Asset database out of sync
3. Too many assets (performance)

**Solutions**:

**Force Asset Database Refresh**:
```
1. Right-click in Project window
2. Reimport All
3. Wait for import to complete
4. Try preview again
```

**Reduce Preview Limit**:
```
1. In Preview Panel, use the slider
2. Set limit to 20-30 instead of 200
3. Click "Refresh Preview"
```

**Check Console for Errors**:
```
Preview generation may log errors
Check Console for filter/provider issues
```

---

## Performance Issues

### Issue: Slow Rule Application

**Symptoms**:
- "Apply All" takes several minutes
- Editor becomes unresponsive

**Causes**:
1. Too many assets in project
2. Complex filter combinations
3. DependentObjectFilter on large sets
4. Verbose logging enabled

**Solutions**:

**Optimize Filters**:
```
❌ SLOW:
- FindAssetsFilter (searches entire database)
- DependentObjectFilter (recursive dependencies)
- Multiple wildcard paths

✅ FAST:
- PathFilter with specific patterns
- TypeFilter
- ExtensionFilter
```

**Disable Verbose Logging**:
```
1. Select LayoutRuleData asset
2. Uncheck "Verbose Logging"
3. Apply rules
```

**Split Rule Sets**:
```
Instead of one giant rule set:
- UI_Rules.asset (UI assets only)
- Audio_Rules.asset (audio assets only)
- Models_Rules.asset (models only)

Apply each separately
```

**Use Batch Operations**:
```csharp
// Batch update in CLI
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset"
```

---

### Issue: Editor Lag in Dashboard

**Symptoms**:
- Dashboard window stutters
- High CPU usage
- Editor becomes slow

**Causes**:
1. Too many tracked assets
2. Auto-refresh enabled with short interval
3. Memory graph updating too frequently

**Solutions**:

**Adjust Refresh Settings**:
```
1. Open Dashboard
2. Go to Settings tab
3. Reduce refresh interval to 1000ms or more
4. Or disable auto-refresh
```

**Limit Tracked Assets**:
```
Only track assets you need to monitor
Release unused assets promptly
Use scoped loading to auto-cleanup
```

**Close Unused Windows**:
```
Close Layout Viewer and Rule Editor when not in use
They consume resources even when hidden
```

---

## Memory Management Issues

### Issue: Memory Not Released

**Symptoms**:
- Memory usage keeps growing
- Assets remain loaded after release
- "Memory leak detected" warnings

**Causes**:
1. Handles not released
2. Scopes not disposed
3. References held in code
4. Pooled objects not returned

**Solutions**:

**Always Release Handles**:
```csharp
// BAD: Handle leaked
var handle = await AddressableManager.LoadAsync<Texture2D>("texture");
// Never released

// GOOD: Manual release
var handle = await AddressableManager.LoadAsync<Texture2D>("texture");
// ... use it ...
handle.Release();

// BETTER: Using statement
using var handle = await AddressableManager.LoadAsync<Texture2D>("texture");
// Auto-released when scope exits
```

**Dispose Scopes**:
```csharp
// BAD: Scope never disposed
var scope = AddressableManager.CreateSessionScope();
await scope.LoadAsync<T>("asset");
// Scope leaked

// GOOD: Dispose when done
var scope = AddressableManager.CreateSessionScope();
try
{
    await scope.LoadAsync<T>("asset");
}
finally
{
    scope.Dispose();
}

// BETTER: Using statement
using var scope = AddressableManager.CreateSessionScope();
await scope.LoadAsync<T>("asset");
```

**Return Pooled Objects**:
```csharp
// Get from pool
var enemy = await AddressableManager.GetOrCreatePooledAsync<GameObject>("enemy_prefab");

// When done, return to pool
AddressableManager.ReturnToPool(enemy);
// NOT Destroy(enemy) - that bypasses the pool!
```

**Use Memory Profiler**:
```
1. Window > Analysis > Memory Profiler
2. Take snapshot after loading
3. Take snapshot after releasing
4. Compare to find leaks
```

---

### Issue: Out of Memory Crashes

**Symptoms**:
- App crashes with OOM error
- Unity freezes during loading
- Memory graph shows spike

**Causes**:
1. Loading too many assets at once
2. Not releasing unused assets
3. Texture/mesh size too large
4. No tiered caching strategy

**Solutions**:

**Use Batch Loading with Limits**:
```csharp
// BAD: Load all at once
var allAssets = await AddressableManager.LoadAssetsAsync<Texture2D>(
    labels: new[] { "textures" }
); // May load 1000+ textures!

// GOOD: Load in batches
const int batchSize = 10;
for (int i = 0; i < addresses.Length; i += batchSize)
{
    var batch = addresses.Skip(i).Take(batchSize);
    await AddressableManager.LoadBatchAsync<Texture2D>(batch);

    // Process batch
    // Release if not needed long-term
}
```

**Implement Aggressive Cleanup**:
```csharp
// Release assets by label when changing scenes
public void OnSceneChange()
{
    // Release all level-specific assets
    AddressableManager.ReleaseByLabel("level_previous");

    // Force garbage collection
    System.GC.Collect();
    Resources.UnloadUnusedAssets();
}
```

**Use Tiered Caching**:
```csharp
// Configure cache tiers
AddressableManager.ConfigureCache(new CacheConfig
{
    TierSizes = new[] { 100, 500, 2000 }, // L1, L2, L3 in MB
    EvictionPolicy = CacheEvictionPolicy.LRU
});
```

---

## Compilation Errors

### Error: "Type or namespace 'AddressableManager' could not be found"

**Cause**: Package not imported or namespace missing

**Solution**:
```csharp
// Add using statement
using AddressableManager;
using AddressableManager.Runtime;

// Verify package installed
Window > Package Manager > Addressable Manager (should be listed)

// Check Assembly Definition References
If using asmdef files, reference:
- AddressableManager.Runtime
- AddressableManager.Editor (for editor scripts)
```

---

### Error: "SmartAssetHandle does not contain a definition for 'IsValid'"

**Cause**: API version mismatch

**Solution**:
```csharp
// Old API (v2.x)
if (handle != null) { }

// New API (v3.x)
if (handle.IsValid()) { }

// Update code to use new API
// See MIGRATION_GUIDE.md for full changes
```

---

## Versioning Issues

### Issue: Git Version Provider Returns Empty

**Symptoms**:
- GitCommitVersionProvider generates empty versions
- Warnings: "Git command failed"

**Causes**:
1. Git not installed
2. .git folder not accessible
3. Not a git repository
4. Git command blocked

**Solutions**:

**Verify Git Installation**:
```bash
# Test git command
git --version

# Should output: git version X.X.X
# If not, install Git and add to PATH
```

**Check Repository**:
```bash
# Ensure you're in a git repo
git status

# If error, initialize repo:
git init
git add .
git commit -m "Initial commit"
```

**Fallback to Alternative Provider**:
```
If git unavailable, use:
- BuildNumberVersionProvider
- DateVersionProvider
- ConstantVersionProvider
```

---

### Issue: Build Number Version Always "0.0.0"

**Cause**: PlayerSettings not configured

**Solution**:
```
1. Edit > Project Settings > Player
2. Set "Version" (e.g., "1.2.3")
3. Set iOS "Build" number
4. Set Android "Bundle Version Code"

Or set programmatically:
PlayerSettings.bundleVersion = "1.2.3";
PlayerSettings.Android.bundleVersionCode = 456;
```

---

## CI/CD Issues

### Issue: CLI Commands Fail in Build Pipeline

**Symptoms**:
- Unity exits with code 2
- "LayoutRuleData not found" errors

**Causes**:
1. Incorrect asset path
2. Unity not finding package
3. Missing dependencies

**Solutions**:

**Use Absolute Paths**:
```bash
# BAD: Relative path may not work
-layoutRuleAssetPath "Rules/Main.asset"

# GOOD: Full project path
-layoutRuleAssetPath "Assets/Rules/Main.asset"
```

**Verify Package in Build**:
```json
// manifest.json
{
  "dependencies": {
    "com.game.addressables": "file:../Packages/com.game.addressables"
  }
}
```

**Check Exit Codes**:
```bash
#!/bin/bash
unity-editor -batchmode -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset"

EXIT_CODE=$?
if [ $EXIT_CODE -eq 0 ]; then
    echo "Success"
elif [ $EXIT_CODE -eq 1 ]; then
    echo "Validation errors"
    exit 1
elif [ $EXIT_CODE -eq 2 ]; then
    echo "Fatal error"
    exit 2
fi
```

---

## Platform-Specific Issues

### Issue: Android Build Fails to Load Assets

**Cause**: Path separators or case sensitivity

**Solution**:
```csharp
// Use consistent casing
"my_asset" not "My_Asset" or "MY_ASSET"

// Verify addresses in built player
var locations = await Addressables.LoadResourceLocationsAsync("my_asset");
Debug.Log($"Found {locations.Count} locations");
```

---

### Issue: iOS Asset Loading Slow

**Cause**: Too many small files or wrong compression

**Solution**:
```
1. Window > Asset Management > Addressables > Settings
2. Content Packing & Loading > Asset Bundle Provider
3. Set appropriate compression (LZ4 for speed, LZMA for size)
4. Group small assets together
```

---

## Data Corruption

### Issue: Addressable Settings Corrupted

**Symptoms**:
- Groups missing after rule application
- Settings file shows errors
- Can't open Addressable Groups window

**Solutions**:

**Restore from Version Control**:
```bash
# Revert addressable settings
git checkout AddressableAssetsData/

# Rebuild
Window > Asset Management > Addressables > Groups
Build > New Build > Default Build Script
```

**Regenerate Settings**:
```
1. Delete AddressableAssetsData folder
2. Window > Asset Management > Addressables > Groups
3. Click "Create Addressables Settings"
4. Reapply rules
```

**Backup Before Applying**:
```
Before major rule changes:
1. Commit current state to version control
2. Or copy AddressableAssetsData folder
3. Apply rules
4. If issues, restore backup
```

---

## Getting Help

If your issue isn't covered here:

1. **Check Documentation**:
   - [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md)
   - [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md)
   - [API_REFERENCE.md](API_REFERENCE.md)

2. **Enable Verbose Logging**:
   ```
   LayoutRuleData > Verbose Logging: ✓
   Check Console for detailed errors
   ```

3. **Use Built-in Diagnostics**:
   ```
   Window > Addressable Manager > Dashboard
   Check Performance and Scopes tabs
   Export performance report
   ```

4. **Report Issues**:
   - GitHub Issues: https://github.com/your-org/addressable-manager/issues
   - Include: Unity version, package version, error logs, reproduction steps

---

**Version**: 3.5.0 | **Last Updated**: January 2025
