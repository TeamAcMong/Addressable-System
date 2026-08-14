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

---

## CDN Content Delivery

One entry per `CdnErrorCode`. Every CDN operation returns a `CdnResult<T>`; on failure
`result.Error.Code` is one of these, and `result.Error.Hint` carries the same advice in
one line.

Before anything else: **`CdnManager.InitializeAsync` must be the first Addressables call
in your boot sequence.** Addressables initialises implicitly on its first load, and the
CDN hooks only apply to content resolved after they are installed. A stray
`LoadAssetAsync`, or an `AssetReference` on an object in your first scene, is enough to
lose them. Initialisation fails loudly with this cause rather than half-applying.

### Quick triage

| Symptom | Likely code | Start here |
|---|---|---|
| First launch hangs on a loading screen, no network | `NoContentAvailableOffline` | [below](#nocontentavailableoffline) |
| Works on Wi-Fi, nothing happens on mobile data | `MeteredNetworkBlocked` | [below](#meterednetworkblocked) |
| Boots fine for you, 404s for shipped players | `CatalogNotFound` | [below](#catalognotfound) |
| Update applies but the changed asset is still old | not an error — see [Static content](#the-update-applied-but-my-change-is-missing) | |
| Game size grows with every patch | not an error — see [Disk](#the-game-keeps-growing-after-every-update) | |

---

### Offline

**Symptom.** `CheckForUpdateAsync` returns success with `WasOfflineFallback == true`, or an
operation fails with this code mid-flight.

**Cause.** No usable network. Note that this is not necessarily an error state: with a warm
cache the game is fully playable and simply cannot check for new content.

**Fix.** Keep playing on cached content and retry later. Do not block the player. The one
thing worth doing is distinguishing this from "checked, nothing new" — `WasOfflineFallback`
exists for exactly that, and treating them the same is how a client ends up stuck on old
content after one bad boot.

---

### NoContentAvailableOffline

**Symptom.** `InitializeAsync` fails on a fresh install. Nothing loads at all.

**Cause.** First launch with no cached catalog and no network. There is genuinely nothing to
play — this is the one case that justifies a blocking screen.

**Fix.** Show a setup screen that retries when connectivity returns. Do not show a generic
error: the player has not done anything wrong and the game is not broken.

**If it happens with a network available**, the network came up after the check but before
the request, or reachability is lying — a captive portal reports as connected. Retry once
before showing the screen.

---

### MeteredNetworkBlocked

**Symptom.** Downloads work on Wi-Fi and silently do nothing on mobile data.

**Cause.** `DownloadPolicy.RequireUnmeteredNetwork` is on and the device is on carrier data.
The layer refused rather than spending the player's data without asking.

**Fix.** Prompt, then retry with `DownloadRequest(..., allowMeteredOverride: true)`. Consent
belongs on the request rather than in settings because it is per download, not permanent.

**Caveat worth knowing.** Unity only reports "carrier data network", so a metered Wi-Fi
hotspot reads as unmetered and a corporate APN reads as metered. This gate is a good default,
not a guarantee.

---

### CatalogNotFound

**Symptom.** Boots fine in the Editor and for you, 404s for shipped players.

**Cause.** No catalog at the URL the player is polling. Almost always one of:

1. **App version mismatch.** The catalog folder is named for `PlayerSettings.bundleVersion`
   at build time. A player on 1.2.0 polls `catalog/1.2.0/…`; if content was published under
   1.3.0, they get a permanent 404. This is the failure `ContentStateManager.Validate` blocks
   at build time — if you see it in production, a build bypassed that gate.
2. **Nothing was published for this platform.** Check the platform folder name: the build
   publishes under the `BuildTarget` name (`StandaloneWindows64`), which is *not* what
   Addressables' own `PlatformMappingService` returns (`Windows`).
3. **Wrong environment.** Check `CdnManager.CurrentBaseUrl` against where you uploaded.

**Fix.** Not retryable. Republish for the version players actually have, or ship a player
build whose version matches what is on the CDN.

**Diagnose it before shipping** with `CdnBuildCLI.VerifyOutput`, which fails when the catalog
is not named for the current app version.

---

### CatalogParseFailed

**Symptom.** The catalog downloads and then initialisation fails.

**Cause.** Truncated or corrupt catalog — usually a partial upload, occasionally a proxy that
rewrote the body.

**Fix.** Clear the catalog cache and retry once. If it recurs, the object on the CDN is
damaged: re-upload it and purge the edge cache. Verify with a plain `curl` that the byte
count matches `build-manifest.json`.

---

### CatalogVersionIncompatible

**Symptom.** The catalog loads but Addressables rejects it.

**Cause.** Built by a different Addressables version, or for a different player version, than
the running build expects.

**Fix.** Force a store update. Not retryable, and not fixable from the server side: the client
binary cannot read that catalog format.

---

### BundleNotFound

**Symptom.** The game boots, the catalog loads, and loading a specific asset 404s.

**Cause.** The catalog references a bundle that is not on the CDN. This is a deploy ordering
mistake: **bundles must be uploaded before the catalog.** Uploading the catalog first creates
a window where clients read a catalog naming bundles that do not exist yet.

**Fix.** Upload the missing bundles, then purge the catalog at the edge. `ci/upload-bundles.sh`
runs before `ci/upload-catalog.sh` for this reason — if you deploy by hand, keep that order.

**Verify** with `CdnBuildCLI.VerifyOutput`, which checks every bundle in the manifest exists.

---

### BundleCrcMismatch

**Symptom.** A download completes and then fails verification, sometimes repeatedly on one
device.

**Cause.** The cached copy is corrupt — interrupted write, failing storage, or a truncated
response cached by an intermediary.

**Fix.** Handled automatically: `DownloadService` evicts the cached dependency and retries
once. If it fails again, the bytes on the CDN are wrong rather than the local copy — compare
the object's SHA256 against `build-manifest.json`.

---

### ServerError

**Symptom.** Intermittent failures, often several clients at once.

**Cause.** 5xx from the origin or edge, or 429 (rate limited).

**Fix.** Handled automatically by `RetryPolicy` with exponential backoff and jitter. If it
persists past the retry budget, tell the player the servers are busy — not that something is
wrong with their device. 429 specifically means retrying faster makes it worse.

---

### Timeout

**Symptom.** Requests hang, then fail with HTTP status 0.

**Cause.** No response arrived: DNS failure, connection refused, or the request exceeded
`DownloadPolicy.TimeoutSeconds`. Status 0 means "never got a response", not "success".

**Fix.** Retried automatically. If it only happens on large bundles, raise
`TimeoutSeconds` — the default 30s is per request, and a slow connection on a large bundle
can legitimately exceed it.

---

### Unauthorized

**Symptom.** 401 or 403 on every request, or after a period of working.

**Cause.** Missing, expired or rejected auth token; or a signed-URL policy that has lapsed;
or bucket permissions.

**Fix.** Set `CdnManager.AuthTokenProvider` **before** `InitializeAsync`. It is called per
request, so a refreshed token is picked up without reinstalling anything. If a fresh token is
still rejected, the problem is the bucket policy rather than the token.

---

### InsufficientDiskSpace

**Symptom.** A download refuses to start.

**Cause.** Free space is below the download size plus `DownloadRequest.MinFreeDiskBytes`
(64 MB by default). The headroom is deliberate — filling the volume breaks the OS, not just
the game.

**Fix.** The error message states how much to free. Consider calling
`CdnManager.Cache.CleanObsoleteAsync()` first: on a long-lived install that often recovers
more than the player would by deleting photos.

---

### Cancelled

Not a failure. The caller cancelled. `CdnResult.IsCancelled` distinguishes it so cancellation
does not surface as an error dialog or a telemetry event. Partial downloads stay cached, so
restarting resumes rather than starting over.

---

### Unknown

**Symptom.** Anything not covered above.

**Cause.** No HTTP response was available to classify the failure. The exception is attached
to `error.Exception`.

**Fix.** Read the exception. If you find a case that should have its own code, it belongs in
`CdnErrorMapper` — classification there is driven by `UnityWebRequestResult.ResponseCode`
rather than by matching words in the message, so adding a case is a small change.

---

## Problems that are not error codes

### The update applied but my change is missing

**Cause.** The changed asset is in a group marked `StaticContent`, which means "not rebuilt by
a content update". Addressables does not fail on this — it logs a warning and reverts the
entry to its previous bundle, so the patch builds, uploads and simply does not contain your
change.

**Fix.** Open **Window → Addressable Manager → CDN Manager → Update Preview** and use
**Prepare content update**. It moves the changed entries into a fresh non-static group so the
next update rebuilds them. Commit the resulting group change.

`CdnBuildCLI.BuildContentUpdate` refuses to build in this state rather than producing a patch
with a hole in it. **A new player build is not required** — that advice appears in older
revisions of the design document and is wrong.

### The game keeps growing after every update

**Cause.** Superseded bundles are not removed automatically by Addressables.

**Fix.** `CdnManager.ApplyUpdateAsync` now calls `CleanObsoleteAsync` for you. If you apply
catalogs through Addressables directly, call `CdnManager.Cache.CleanObsoleteAsync()`
afterwards. Check with `CdnManager.GetCacheStats()`.

### It works in the Editor and 404s in a build

**Cause.** Almost always Play Mode script: **Use Asset Database** and **Simulate Groups** never
touch the network, so the CDN path is not exercised at all. The Editor was reading your
project folder.

**Fix.** Switch to **Use Existing Build** in the Addressables Groups window. This is also why
the integration tests assert against the local server's request log rather than the client's
return value — a green result under Fast Mode proves nothing.

### Everything 404s and the URL contains the wrong platform folder

**Cause.** The `{platform}` token in a base URL was resolved with Unity's own
`PlatformMappingService.GetPlatformPathSubFolder()`, which returns `Windows` where the build
published under `StandaloneWindows64`.

**Fix.** Use `HostRewriter.ResolvePlatformToken()`, which reproduces the `BuildTarget` name —
or better, drop `{platform}` from the base URL entirely. Addressables bakes the platform
segment into the catalog URL at build time, so environments that differ only by host need no
token at all.
