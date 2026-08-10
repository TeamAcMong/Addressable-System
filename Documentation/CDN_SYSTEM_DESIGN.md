# CDN System — Technical Design

**Architecture and API specification for remote content delivery in `com.game.addressables`**

Target version: 4.1.0 → 5.0.0 | Status: **Proposed** | Last Updated: August 2026

---

## Table of Contents

1. [Goals and Non-Goals](#1-goals-and-non-goals)
2. [Current State Audit](#2-current-state-audit)
3. [Design Principles](#3-design-principles)
4. [Architecture Overview](#4-architecture-overview)
5. [Module Specifications](#5-module-specifications)
6. [Public API Surface](#6-public-api-surface)
7. [Boot and Download Flows](#7-boot-and-download-flows)
8. [Error Model](#8-error-model)
9. [Addressables Configuration Contract](#9-addressables-configuration-contract)
10. [Integration With Existing Subsystems](#10-integration-with-existing-subsystems)
11. [Backward Compatibility](#11-backward-compatibility)
12. [Testing Strategy](#12-testing-strategy)
13. [Open Questions](#13-open-questions)

---

## 1. Goals and Non-Goals

### Goals

| # | Goal | Success measure |
|---|------|-----------------|
| G1 | Ship content updates without a store resubmission | A content-only change reaches players via catalog update, no new binary |
| G2 | Patches transfer only what changed | Delta patch size ≤ 10% of full content for a typical content-only change |
| G3 | Boot never hard-fails because the CDN is unreachable | Offline boot succeeds using cached catalog + cached bundles |
| G4 | Download UX is truthful | Progress reported in real bytes; speed and ETA derived from bytes, not percent |
| G5 | Every failure is classifiable and actionable | Every CDN failure maps to a `CdnErrorCode` with a defined recovery path |
| G6 | Environment switching requires no rebuild | Dev / Staging / Prod selectable at runtime via host rewrite |
| G7 | The whole pipeline is CI-drivable | Build, content-update, verify, and upload all run in `-batchmode` |

### Non-Goals (this phase)

- Unity Cloud Content Delivery (CCD) integration — we target a generic HTTP origin (R2 + Cloudflare). CCD stays possible because Addressables only needs a URL, but no CCD-specific code is written.
- Play Asset Delivery / Android asset packs — deferred; revisit if the APK crosses the store size limit.
- Player-generated content / MOD loading — out of scope. If this enters the roadmap, re-evaluate YooAsset before extending this design.
- WebGL. The download/cache model here assumes `Caching` is available; WebGL has no bundle cache and would need a separate path.
- Per-asset streaming or CDN-side transcoding.

---

## 2. Current State Audit

Measured against the code on `main` at `4.0.1`.

### What exists

| Symbol | Location | Assessment |
|--------|----------|------------|
| `AssetLoader.DownloadDependenciesAsync(string)` | `Runtime/Loaders/AssetLoader.cs:925` | Thin wrapper. No retry, cancel, progress, or typed error. |
| `AssetLoader.GetDownloadSizeAsync(string)` | `Runtime/Loaders/AssetLoader.cs:966` | Returns `0` for both "nothing to download" and "request failed". |
| `ProgressiveAssetLoader.DownloadWithProgressAsync` | `Runtime/Progress/ProgressiveAssetLoader.cs:100` | Progress from `PercentComplete`; speed/ETA math is dimensionally wrong. |
| `LoadErrorCode.NetworkError` | `Runtime/Core/LoadError.cs:56` | Assigned in exactly one place, by substring matching on the exception message (`AssetLoader.cs:816`). Never produced by the download path. |
| `ProgressInfo.BytesDownloaded` / `.TotalBytes` | `Runtime/Progress/IProgressTracker.cs:12` | Fields declared, never populated. |

### What does not exist

Package-wide grep returns zero hits for all of the following:

`Addressables.InitializeAsync` · `CheckForCatalogUpdates` · `UpdateCatalogs` · `LoadContentCatalogAsync` ·
`WebRequestOverride` · `InternalIdTransformFunc` · `ClearDependencyCacheAsync` · `CleanBundleCache` ·
`Caching.*` · `GetDownloadStatus` · `Application.internetReachability` · `CancellationToken` (download path)

`Editor/CLI/AddressableCLI.cs` exposes only `ApplyRules`, `ValidateLayoutRules`, `SetVersionExpression`,
`DetectConflicts`. There is **no** `BuildPlayerContent` and **no** `ContentUpdateScript.BuildContentUpdate`
entry point.

### Configuration state

`Assets/AddressableAssetsData/AddressableAssetSettings.asset`:

```
m_BuildRemoteCatalog: 0            ← remote catalog disabled
m_RemoteCatalogBuildPath: (empty)
m_RemoteCatalogLoadPath:  (empty)
m_BundleTimeout: 0
m_BundleRetryCount: 0
m_maxConcurrentWebRequests: 3
m_ContentStateBuildPath: (empty → defaults inside Assets/)
m_overridePlayerVersion: '[UnityEditor.PlayerSettings.bundleVersion]'   ← already correct
```

Only one profile (`Default`) exists, with `Remote.LoadPath = <undefined>`.

### Consequence

Without `BuildContentUpdate`, every build is a full build: all bundle hashes change, so every player
re-downloads 100% of remote content on every patch. **This is the binding constraint.** No amount of
runtime work improves it, which is why the pipeline is sequenced first in the implementation plan.

---

## 3. Design Principles

1. **Mirror the existing package idioms.** Tiered API (`Cdn` facade → service interfaces → raw Addressables),
   `#if UNITASK_PRESENT` dual signatures, `ScriptableObject` configs, and a `Result`/`Error` pair that
   parallels the existing `LoadResult<T>` / `LoadError`.
2. **Explicit results, no sentinel values.** Every operation that can fail returns `CdnResult<T>`.
   No method returns `0`, `null`, or `false` to mean two different things.
3. **Cancellable by default.** Every async CDN entry point takes a `CancellationToken`.
4. **Bytes, not percent.** All user-facing progress derives from `AsyncOperationHandle.GetDownloadStatus()`.
5. **Degrade, do not crash.** Network failure at boot degrades to cached content. Only a first install with
   no network and no cache is a hard stop, and that state is reported explicitly so the game can show a
   dedicated screen.
6. **Services are interfaces.** `ICatalogService`, `IDownloadService`, `ICacheService`, `ICdnTelemetry` —
   so tests can substitute fakes and games can substitute custom implementations.
7. **No hidden singletons in the service layer.** The `Cdn` static facade is a convenience wrapper over an
   injectable `CdnRuntime`; everything reachable through the facade is also constructible directly.

---

## 4. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│  Game code                                                       │
│  Cdn.InitializeAsync() · Cdn.DownloadAsync() · Cdn.GetSizeAsync()│
└───────────────────────────────┬──────────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────────┐
│  Cdn (static facade)  →  CdnRuntime (composition root)           │
└───────┬─────────────┬──────────────┬─────────────┬───────────────┘
        │             │              │             │
┌───────▼──────┐ ┌────▼───────┐ ┌────▼────────┐ ┌──▼─────────────┐
│ Catalog      │ │ Download   │ │ Cache       │ │ Diagnostics    │
│ Service      │ │ Service    │ │ Service     │ │ (telemetry)    │
└───────┬──────┘ └────┬───────┘ └────┬────────┘ └────────────────┘
        │             │              │
        └─────────────┼──────────────┘
                      │
        ┌─────────────▼──────────────┐   ┌────────────────────────┐
        │ RetryPolicy · NetworkPolicy│   │ CdnRequestDecorator     │
        │ (backoff, wifi-only gate)  │   │ WebRequestOverride +    │
        └─────────────┬──────────────┘   │ InternalIdTransformFunc │
                      │                  └───────────┬─────────────┘
┌─────────────────────▼──────────────────────────────▼─────────────┐
│  Unity Addressables 2.x  /  ResourceManager  /  UnityWebRequest   │
└───────────────────────────────────────────────────────────────────┘
```

Existing subsystems (`AssetLoader`, scopes, pooling, tiered cache) sit *above* this layer and are
unchanged: by the time they run, content is already local.

### Proposed file layout

```
Runtime/Cdn/
├── Cdn.cs                              static facade (mirrors Facade/Assets.cs)
├── CdnRuntime.cs                       composition root; owns service instances
├── Config/
│   ├── CdnSettings.cs                  ScriptableObject, list of environments
│   ├── CdnEnvironment.cs               [Serializable] name, baseUrl, catalogUrl, flags
│   └── DownloadPolicy.cs               [Serializable] retry, backoff, timeout, concurrency, wifi-only
├── Core/
│   ├── CdnErrorCode.cs
│   ├── CdnError.cs                     mirrors Core/LoadError.cs
│   ├── CdnResult.cs                    mirrors Core/LoadResult.cs
│   ├── DownloadRequest.cs              keys + MergeMode + policy override
│   ├── DownloadProgress.cs             byte-accurate progress payload
│   └── CatalogUpdateInfo.cs            catalog ids, sizes, whether an update exists
├── Services/
│   ├── ICatalogService.cs / CatalogService.cs
│   ├── IDownloadService.cs / DownloadService.cs
│   ├── ICacheService.cs  / CacheService.cs
│   └── CdnRequestDecorator.cs
├── Policy/
│   ├── RetryPolicy.cs                  exponential backoff + jitter
│   └── NetworkPolicy.cs                reachability + metered-connection gate
└── Diagnostics/
    ├── ICdnTelemetry.cs
    └── CdnDiagnostics.cs

Editor/Cdn/
├── CdnBuildCLI.cs                      -batchmode entry points
├── CdnBuildPipeline.cs                 BuildPlayerContent / BuildContentUpdate wrappers
├── ContentStateManager.cs              locate, validate, archive addressables_content_state.bin
├── CdnProfileManager.cs                profile switching + Remote path injection
├── CatalogVerifier.cs                  post-build catalog validation
└── CdnSettingsInspector.cs
```

---

## 5. Module Specifications

### 5.1 `CdnSettings` (ScriptableObject)

Single asset, resolved via `Resources` or an addressable at a fixed address so it can itself be patched.

```csharp
[CreateAssetMenu(menuName = "Addressable Manager/CDN Settings")]
public sealed class CdnSettings : ScriptableObject
{
    public List<CdnEnvironment> Environments;
    public string DefaultEnvironmentId;          // overridable at runtime
    public DownloadPolicy DefaultPolicy;
    public bool AutoCheckCatalogOnInitialize;    // default true
    public bool AutoApplyCatalogUpdate;          // default true
    public bool CleanBundleCacheAfterUpdate;     // default true
    public float CatalogRequestTimeoutSeconds;   // default 15
}

[Serializable]
public sealed class CdnEnvironment
{
    public string Id;              // "dev" | "staging" | "prod"
    public string BundleBaseUrl;   // https://cdn.example.com/game/{platform}/bundles
    public string CatalogBaseUrl;  // https://cdn.example.com/game/{platform}/{appVersion}
    public bool RequiresAuthHeader;
}
```

`{platform}` and `{appVersion}` are tokens resolved at runtime by `CdnRuntime` so one settings asset
serves every build target.

### 5.2 `DownloadPolicy`

```csharp
[Serializable]
public sealed class DownloadPolicy
{
    public int    MaxRetries            = 3;
    public float  InitialBackoffSeconds = 1f;
    public float  BackoffMultiplier     = 2f;
    public float  MaxBackoffSeconds     = 30f;
    public float  JitterRatio           = 0.2f;   // ±20% to avoid thundering herd
    public int    BundleTimeoutSeconds  = 30;
    public int    MaxConcurrentRequests = 6;
    public bool   RequireUnmeteredNetwork = false; // "wifi only"
    public long   MinFreeDiskBytes      = 200L * 1024 * 1024;
}
```

Retry applies to the *whole* download operation, not to individual bundles — Addressables already retries
per-bundle via `BundleRetryCount`. The two layers are complementary:
`BundleRetryCount` handles a transient 5xx on one bundle; `RetryPolicy` handles "the operation failed,
the user is back on WiFi, try again".

### 5.3 `ICatalogService`

Owns everything about content catalogs.

```csharp
public interface ICatalogService
{
    UniTask<CdnResult<CdnInitReport>>     InitializeAsync(CancellationToken ct);
    UniTask<CdnResult<CatalogUpdateInfo>> CheckForUpdateAsync(CancellationToken ct);
    UniTask<CdnResult<CatalogApplyReport>> ApplyUpdateAsync(CatalogUpdateInfo info, CancellationToken ct);
}
```

Implementation notes:

- `InitializeAsync` wraps `Addressables.InitializeAsync()` and **awaits it explicitly** rather than
  relying on implicit initialization inside the first load call. This makes init failures observable.
- `CheckForUpdateAsync` wraps `Addressables.CheckForCatalogUpdates(autoReleaseHandle: false)` so the
  handle can be inspected and released deterministically.
- `ApplyUpdateAsync` wraps `Addressables.UpdateCatalogs(catalogs, autoReleaseHandle: false, autoCleanBundleCache: settings.CleanBundleCacheAfterUpdate)`.
- **Offline behaviour:** if `NetworkPolicy` reports unreachable, `CheckForUpdateAsync` returns
  `CdnResult.Success(CatalogUpdateInfo.None)` with `WasOfflineFallback = true` — it is not an error.
  Addressables will resolve the previously cached catalog.
- **First-install-offline:** `InitializeAsync` detects "no cached catalog and no network" and returns
  `CdnErrorCode.NoContentAvailableOffline`. This is the one legitimate hard-stop.

### 5.4 `IDownloadService`

```csharp
public interface IDownloadService
{
    UniTask<CdnResult<long>>           GetDownloadSizeAsync(DownloadRequest req, CancellationToken ct);
    UniTask<CdnResult<DownloadReport>> DownloadAsync(DownloadRequest req,
                                                     IProgress<DownloadProgress> progress,
                                                     CancellationToken ct);
}

public sealed class DownloadRequest
{
    public IReadOnlyList<object> Keys;          // addresses, labels, or AssetReferences
    public Addressables.MergeMode MergeMode = Addressables.MergeMode.Union;
    public DownloadPolicy PolicyOverride;       // null → settings default
}
```

Progress loop replaces the current percent-based implementation:

```csharp
var status = handle.GetDownloadStatus();       // DownloadStatus { DownloadedBytes, TotalBytes, IsDone }
long deltaBytes = status.DownloadedBytes - lastBytes;
float deltaTime = Time.unscaledDeltaTime;      // real frame delta, not elapsed-since-start
// EMA smoothing so the number does not flicker frame to frame
speedBytesPerSecond = Mathf.Lerp(speedBytesPerSecond, deltaBytes / deltaTime, 0.1f);
eta = speedBytesPerSecond > 0
    ? (status.TotalBytes - status.DownloadedBytes) / speedBytesPerSecond
    : -1f;                                     // -1 = unknown, never a fabricated number
```

The progress callback is throttled (default 4 Hz, configurable) rather than fired every frame. Because
`DownloadProgress` is a struct and the callback is `IProgress<T>`, the loop allocates nothing steady-state.

Pre-flight checks before starting, each mapping to a distinct error code:
1. Network reachable, and unmetered if `RequireUnmeteredNetwork`.
2. `Caching.defaultCache.spaceFree` ≥ download size + `MinFreeDiskBytes`.
3. Size query succeeded (a failed size query must not be treated as "0 bytes to download").

### 5.5 `ICacheService`

```csharp
public interface ICacheService
{
    CdnCacheStats                GetStats();                                  // occupied, free, path
    UniTask<CdnResult<bool>>     ClearAsync(object key, CancellationToken ct); // ClearDependencyCacheAsync
    UniTask<CdnResult<bool>>     ClearAllAsync(CancellationToken ct);          // Caching.ClearCache
    UniTask<CdnResult<bool>>     CleanObsoleteAsync(CancellationToken ct);     // Addressables.CleanBundleCache
}
```

`CleanObsoleteAsync` is the important one: after a catalog update, bundles referenced only by the old
catalog stay on disk forever unless explicitly cleaned. On a long-lived install this is the main source of
"the game is using 8 GB" complaints.

### 5.6 `CdnRequestDecorator`

Two Addressables hooks, both installed once during `CdnRuntime` construction.

```csharp
Addressables.WebRequestOverride = request =>
{
    request.timeout = policy.BundleTimeoutSeconds;
    if (environment.RequiresAuthHeader)
        request.SetRequestHeader("Authorization", tokenProvider.Current);
    request.SetRequestHeader("X-Client-Version", Application.version);
};

Addressables.InternalIdTransformFunc = location =>
{
    // Rewrites the host recorded in the catalog at build time to the host chosen at runtime.
    // Enables: dev/staging/prod switching without rebuild, and CDN failover.
    return hostRewriter.Rewrite(location.InternalId);
};
```

`InternalIdTransformFunc` is what decouples the built catalog from a specific hostname. The catalog is
built against a placeholder host; the runtime substitutes the real one. This is also the failover
mechanism: on repeated failures against host A, `hostRewriter` promotes host B and the retry resolves
to a different origin.

> **Constraint:** `InternalIdTransformFunc` must be installed *before* `Addressables.InitializeAsync()`,
> otherwise the catalog request itself is not rewritten. `CdnRuntime` enforces this ordering.

### 5.7 `NetworkPolicy` and `RetryPolicy`

`NetworkPolicy` wraps `Application.internetReachability` and exposes:
`IsReachable`, `IsMetered` (`ReachableViaCarrierDataNetwork`), and a `WaitForReachableAsync(ct)` helper
used to resume a paused download when connectivity returns.

> `internetReachability` reports the *interface*, not actual internet access (captive portals report
> reachable). Treat it as a fast negative check only: if it says unreachable, skip the request; if it says
> reachable, still handle failure normally.

`RetryPolicy` computes `min(initial * multiplier^attempt, max) * (1 ± jitter)` and decides retryability
from the error code table in §8 — 5xx and timeouts retry, 403/404 and CRC mismatch do not.

### 5.8 `ICdnTelemetry`

Fire-and-forget hooks so the game can forward to its own analytics. No transport is bundled.

```csharp
public interface ICdnTelemetry
{
    void OnCatalogChecked(CatalogUpdateInfo info, TimeSpan duration, CdnError error);
    void OnDownloadCompleted(DownloadReport report);
    void OnDownloadFailed(DownloadRequest req, CdnError error, int attempt);
    void OnCacheCleaned(long bytesFreed);
}
```

Recommended metrics to derive: download success rate, p50/p95 duration by size bucket, error code
distribution, bytes served per app version. Without these you cannot tell a CDN problem from a client bug.

---

## 6. Public API Surface

Three tiers, matching the existing `Assets` / `StandardAPI` / `Advanced` split.

### Tier 1 — `Cdn` static facade (covers ~90% of use)

```csharp
public static class Cdn
{
    // Boot
    UniTask<CdnResult<CdnInitReport>> InitializeAsync(string environmentId = null, CancellationToken ct = default);

    // Patch check
    UniTask<CdnResult<CatalogUpdateInfo>>  CheckForUpdateAsync(CancellationToken ct = default);
    UniTask<CdnResult<CatalogApplyReport>> ApplyUpdateAsync(CancellationToken ct = default);

    // Download
    UniTask<CdnResult<long>>           GetDownloadSizeAsync(params object[] keys);
    UniTask<CdnResult<DownloadReport>> DownloadAsync(object key,
                                                     IProgress<DownloadProgress> progress = null,
                                                     CancellationToken ct = default);
    UniTask<CdnResult<DownloadReport>> DownloadAsync(DownloadRequest request,
                                                     IProgress<DownloadProgress> progress = null,
                                                     CancellationToken ct = default);

    // Cache
    CdnCacheStats            GetCacheStats();
    UniTask<CdnResult<bool>> ClearCacheAsync(object key = null, CancellationToken ct = default);
    UniTask<CdnResult<bool>> CleanObsoleteBundlesAsync(CancellationToken ct = default);

    // State
    string  CurrentEnvironmentId { get; }
    bool    IsInitialized        { get; }
}
```

### Tier 2 — services

`CdnRuntime.Catalog`, `.Download`, `.Cache` expose the interfaces from §5 for callers that need to
inject policies per call or swap implementations.

### Tier 3 — advanced

`CdnRuntime` constructor takes `(CdnSettings, ICatalogService, IDownloadService, ICacheService, ICdnTelemetry)`,
so a game can replace any part. `CdnRequestDecorator` exposes `IHostRewriter` and `IAuthTokenProvider`
for signed-URL or token-refresh schemes.

### Canonical usage

```csharp
async UniTask BootAsync(CancellationToken ct)
{
    var init = await Cdn.InitializeAsync(ct: ct);
    if (!init.IsSuccess)
    {
        if (init.Error.Code == CdnErrorCode.NoContentAvailableOffline)
        { ShowFirstInstallNeedsInternetScreen(); return; }
        // any other init failure → continue on cached content
        Log.Warn(init.Error);
    }

    var update = await Cdn.CheckForUpdateAsync(ct);
    if (update.IsSuccess && update.Value.HasUpdate)
    {
        await Cdn.ApplyUpdateAsync(ct);

        var size = await Cdn.GetDownloadSizeAsync("preload_core");
        if (size.IsSuccess && size.Value > 0)
        {
            if (!await ConfirmDownloadAsync(size.Value)) return;

            var progress = new Progress<DownloadProgress>(p =>
                patchUI.Set(p.NormalizedProgress, p.DownloadedBytes, p.TotalBytes,
                            p.BytesPerSecond, p.EstimatedSecondsRemaining));

            var result = await Cdn.DownloadAsync("preload_core", progress, ct);
            if (!result.IsSuccess) ShowPatchFailed(result.Error);
        }
    }

    await Cdn.CleanObsoleteBundlesAsync(ct);
}
```

Note what the caller can now do that it cannot today: distinguish "nothing to download" from "size query
failed", cancel, show real byte counts, and branch on a typed error.

---

## 7. Boot and Download Flows

### 7.1 Cold boot, online, first install

```
InstallDecorator (WebRequestOverride + InternalIdTransformFunc)
  → Addressables.InitializeAsync()          catalog fetched from CatalogBaseUrl
  → CheckForUpdateAsync()                   no cached catalog → HasUpdate = false
  → GetDownloadSizeAsync(preload keys)      full size
  → DownloadAsync(...)                      bytes-accurate progress
  → ready
```

### 7.2 Warm boot, online, no content change

```
Initialize → CheckForUpdate → catalog hash matches → HasUpdate = false → ready
```
Cost: one `.hash` request (a few bytes). This is why the `.hash` file must have a short edge TTL — see
the infrastructure guide.

### 7.3 Warm boot, online, content changed

```
Initialize → CheckForUpdate → HasUpdate = true, catalogs = [id]
  → ApplyUpdateAsync (UpdateCatalogs, autoCleanBundleCache = true)
  → GetDownloadSizeAsync → delta only, because unchanged bundles are still cached and their
                            hashes are unchanged (this is what BuildContentUpdate guarantees)
  → DownloadAsync → ready
```

### 7.4 Warm boot, offline

```
Initialize → NetworkPolicy.IsReachable == false
  → Addressables resolves the cached catalog
  → CheckForUpdate short-circuits: Success(None, WasOfflineFallback = true)
  → GetDownloadSize skipped
  → ready, running on cached content
```
No exception, no blocking dialog. The game may show a passive "offline" indicator.

### 7.5 Cold boot, offline, first install

```
Initialize → no cached catalog + unreachable
  → CdnResult.Failure(NoContentAvailableOffline)
  → game shows a dedicated "connect to the internet to finish setup" screen
```

### 7.6 Download with retry

```
attempt 0 → 503 on bundle X
  → RetryPolicy.ShouldRetry(ServerError) = true, delay = 1s ± 20%
attempt 1 → connection dropped mid-transfer
  → partial bytes remain in Unity's cache; Addressables resumes from the cached partial
  → delay = 2s ± 20%
attempt 2 → success
  → DownloadReport { BytesDownloaded, Duration, Attempts = 3, ResumedFromPartial = true }
```

### 7.7 Cancellation

`ct` cancellation stops the await loop and releases the handle. Bytes already written to Unity's cache
are kept — a subsequent download resumes rather than restarting. The result is
`CdnResult.Cancelled`, distinct from failure, so UI does not show an error toast for a user-initiated back press.

### 7.8 Disk full mid-download

Pre-flight catches the common case. If the device fills up during transfer, Addressables surfaces a
caching failure; `DownloadService` maps it to `CdnErrorCode.InsufficientDiskSpace` and reports
`RequiredBytes` / `AvailableBytes` so the UI can say how much to free.

---

## 8. Error Model

`CdnError` mirrors `Core/LoadError.cs` (code, message, hint, exception) and adds `HttpStatusCode`,
`Url`, and `IsRetryable`.

| Code | Trigger | Retryable | Recommended UX |
|------|---------|-----------|----------------|
| `None` | success | — | — |
| `Offline` | reachability check failed | yes, on reconnect | Passive indicator; keep playing on cached content |
| `NoContentAvailableOffline` | first install, no cache, no network | yes, on reconnect | Blocking setup screen |
| `MeteredNetworkBlocked` | `RequireUnmeteredNetwork` and on cellular | user decision | "Download over mobile data?" prompt |
| `CatalogNotFound` | 404 on catalog or `.hash` | no | Hard error — a deploy is broken. Report to telemetry. |
| `CatalogParseFailed` | malformed / truncated catalog | no | Clear catalog cache, retry once, then hard error |
| `CatalogVersionIncompatible` | catalog built for a different player version | no | Force store update |
| `BundleNotFound` | 404 on a bundle | no | Deploy is incomplete — bundles uploaded after catalog |
| `BundleCrcMismatch` | CRC/hash verification failed | yes, after evicting that bundle | Auto-repair: clear that dependency, retry once |
| `ServerError` | 5xx | yes, backoff | Silent retry, then "servers are busy" |
| `Timeout` | request exceeded `BundleTimeoutSeconds` | yes, backoff | Silent retry |
| `Unauthorized` | 401/403 | only after token refresh | Refresh token via `IAuthTokenProvider`, then retry once |
| `InsufficientDiskSpace` | pre-flight or caching failure | after user frees space | "Free N MB to continue" |
| `Cancelled` | caller cancelled | n/a | No error UI |
| `Unknown` | unmapped | no | Log with full exception |

Classification comes from `RemoteProviderException` and its HTTP response code where available, **not**
from substring matching on the message — that is the flaw in the current `DetermineErrorCode`
(`AssetLoader.cs:804`). Substring matching stays only as a last-resort fallback for `Unknown`.

> Implementation note: verify the exact exception type and response-code property name against the pinned
> Addressables version during Phase 3; the `ResourceManager` exception hierarchy has changed between
> 1.x and 2.x.

---

## 9. Addressables Configuration Contract

The runtime code is only correct if the project settings match. These become assertions in
`CatalogVerifier` and are checked in CI.

| Setting | Required value | Why |
|---------|----------------|-----|
| `BuildRemoteCatalog` | `true` | Without it there is no remote catalog to update |
| `RemoteCatalogBuildPath` / `LoadPath` | Remote profile vars | Catalog must be separable from bundles (§ infra) |
| `OverridePlayerVersion` | `[UnityEditor.PlayerSettings.bundleVersion]` | Stable catalog filename per app version. **Already set correctly.** A timestamp here would break content update — old players would look for a catalog name that no longer exists. |
| `ContentStateBuildPath` | A path **outside** `Assets/`, archived by CI | Losing `addressables_content_state.bin` permanently ends delta updates for that app version |
| `BundleRetryCount` | `2–3` (currently `0`) | Per-bundle transient failure recovery |
| `BundleTimeout` | `30` (currently `0` = infinite) | Prevents a hung request from stalling boot forever |
| `MaxConcurrentWebRequests` | `4–8` (currently `3`) | 3 under-uses available bandwidth; very high values hurt on mobile |
| `MonoScriptBundleNaming` | `Project Name` (currently `Disabled`) | Keeps the MonoScript bundle name stable across builds so it does not churn every content update |
| `InternalIdNamingMode` | `Filename` or `Dynamic` (currently `Full Path`) | Shrinks the catalog and stops leaking project folder structure to anyone who downloads it |
| `UniqueBundleIds` | `false` (already) | Correct when content update is used properly; enabling it inflates every build |
| `EnableJsonCatalog` | `false` (already) | Binary catalog is smaller. Note: `AddressablesTools` must be on a version that reads binary catalogs for CI verification. |
| Group `UseAssetBundleCrc` | `true` (already) | Detects corrupted downloads |
| Group `UseAssetBundleCrcForCachedBundles` | `true` (already) | Detects corruption of already-cached bundles |

### Profiles

Four profiles, created and validated by `CdnProfileManager`:

| Profile | `Remote.BuildPath` | `Remote.LoadPath` |
|---------|--------------------|-------------------|
| `Local` | `ServerData/[BuildTarget]` | `http://localhost:8080/[BuildTarget]` |
| `Dev` | `ServerData/[BuildTarget]` | `https://cdn-dev.<domain>/...` |
| `Staging` | `ServerData/[BuildTarget]` | `https://cdn-stg.<domain>/...` |
| `Prod` | `ServerData/[BuildTarget]` | `https://cdn.<domain>/...` |

Because `InternalIdTransformFunc` rewrites hosts at runtime, the *built* profile matters less than it
normally would — but the profile still determines what is baked into the catalog as the fallback, so it
must be correct for the target ring.

---

## 10. Integration With Existing Subsystems

| Subsystem | Interaction | Change required |
|-----------|-------------|-----------------|
| `AssetLoader` | Loads assets once content is local | Deprecate its `DownloadDependenciesAsync` / `GetDownloadSizeAsync`; forward to `IDownloadService` |
| `ProgressiveAssetLoader` | `DownloadWithProgressAsync` | Replace implementation with `DownloadService`; keep the signature during the deprecation window |
| `ProgressInfo` | Already has `BytesDownloaded` / `TotalBytes` | Populate them; add an adapter from `DownloadProgress` |
| `AddressablePreloadConfig` | Declares what to preload | Extend with a `RemoteOnly` flag so preload lists can drive `DownloadRequest` directly |
| `LoadError` / `LoadResult` | Load-time errors | Unchanged. `CdnError` is a sibling, not a replacement. Add `LoadErrorCode.ContentNotDownloaded` for "asset exists in catalog but bundle is not local". |
| Scopes / pooling / tiered cache | Above the CDN layer | None |
| `AddressableManagerWindow` | Editor dashboard | Add a "CDN" tab: current environment, cache stats, catalog id, manual clear/clean |
| `AddressableCLI` | Editor CLI | New `CdnBuildCLI` alongside it; keep the existing rule commands untouched |

`AddressableManager.asmdef` needs no new references — everything used is in `Unity.Addressables`,
`Unity.ResourceManager`, and `UnityEngine.Networking` (part of the engine).

---

## 11. Backward Compatibility

The three existing CDN-adjacent methods stay, with behaviour preserved, marked `[Obsolete]` with a
pointer to the replacement:

| Existing | Status in 4.1.0 | Removed in |
|----------|-----------------|-----------|
| `AssetLoader.DownloadDependenciesAsync(string)` | Obsolete (warning). Forwards to `IDownloadService`. Still returns `bool`. | 5.0.0 |
| `AssetLoader.GetDownloadSizeAsync(string)` | Obsolete (warning). Still returns `long`, still `0` on failure — the ambiguity is why it is deprecated. | 5.0.0 |
| `ProgressiveAssetLoader.DownloadWithProgressAsync` | Reimplemented over `DownloadService`. **Behaviour change:** `DownloadSpeed` becomes real KB/s and `BytesDownloaded`/`TotalBytes` become populated. | kept |
| `Assets.Download` / `Assets.GetDownloadSize` | Obsolete (warning). | 5.0.0 |
| `StandardAPI.DownloadDependencies` / `GetDownloadSize` | Obsolete (warning). | 5.0.0 |

The `DownloadSpeed` change is technically breaking for anyone who calibrated UI against the old
(meaningless) number. It is called out in the changelog as a fix rather than a feature.

---

## 12. Testing Strategy

### Unit (EditMode, no network)

- `RetryPolicy`: backoff sequence, jitter bounds, retryability per error code.
- `HostRewriter`: URL rewriting across environments, failover promotion, malformed input.
- Error mapping: given a synthetic `RemoteProviderException` with status N → expected `CdnErrorCode`.
- `DownloadProgress` math: fixed byte/time sequences → expected speed and ETA, including the
  divide-by-zero and stalled-transfer cases.

### Integration (PlayMode, local HTTP server)

A small editor-hosted static server serving a real built catalog + bundles, with fault injection:

| Scenario | Injection |
|----------|-----------|
| Happy path | none |
| Catalog 404 | remove catalog |
| Bundle 404 | remove one bundle after catalog upload |
| 503 then success | fail first N requests |
| Slow transfer | throttle to 50 KB/s, assert ETA converges |
| Mid-transfer disconnect | close connection at 50% |
| Corrupt bundle | flip a byte, assert `BundleCrcMismatch` and auto-repair |
| Cancellation | cancel at 30%, assert `Cancelled` and partial cache retained |

### Manual / device matrix

Real patch flow on a physical device over WiFi, over cellular, with airplane mode toggled mid-download,
and with the device near-full on storage.

### CI gates

`CatalogVerifier` runs on every content build and fails the job on: settings contract violations (§9),
a catalog referencing a bundle absent from the upload set, or a catalog filename that changed
unexpectedly for an existing app version.

---

## 13. Open Questions

1. **Addressables version.** The package pins `com.unity.addressables: 2.3.1`; current is 2.9.x.
   Upgrading before writing this code avoids rework, but needs a regression pass on the existing loaders.
   *Recommendation: upgrade in Phase 0.*
2. **Where does `CdnSettings` live?** `Resources` (always in build, simple) vs. a local addressable
   (patchable, but chicken-and-egg with initialization). *Recommendation: `Resources`, with environment
   overridable by a runtime argument so QA can point a prod build at staging.*
3. **Signed URLs vs. token header.** Signed URLs mean the catalog cannot contain final URLs, forcing all
   rewriting through `InternalIdTransformFunc` with a signing step per request. A bearer header via
   `WebRequestOverride` is much simpler. *Recommendation: header, unless there is a hard requirement to
   prevent link sharing.*
4. **Do we need per-bundle download prioritisation?** Addressables does not expose queue priority for
   dependency downloads. Achievable only by splitting into multiple sequential `DownloadAsync` calls by
   label. *Recommendation: model priority as ordered label groups, not as a scheduler.*
5. **`ProgressInfo` vs. `DownloadProgress`.** Two progress types is a wart. Merging means changing
   `ProgressInfo` semantics for existing callers. *Recommendation: keep both for 4.x, adapter between
   them, unify in 5.0.0.*
