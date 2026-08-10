# CDN System — Implementation Plan

**Phased rollout strategy for the CDN subsystem**

Branch: `feat/cdn-system` | Target: 4.1.0 → 5.0.0 | Status: **Awaiting approval** | Last Updated: August 2026

Companion documents: [CDN_SYSTEM_DESIGN.md](CDN_SYSTEM_DESIGN.md) · [CDN_INFRASTRUCTURE_GUIDE.md](CDN_INFRASTRUCTURE_GUIDE.md)

---

## Table of Contents

1. [Strategy](#1-strategy)
2. [Phase Overview](#2-phase-overview)
3. [Phase 0 — Foundations](#phase-0--foundations)
4. [Phase 1 — Build and Content-Update Pipeline](#phase-1--build-and-content-update-pipeline)
5. [Phase 2 — Runtime Bootstrap](#phase-2--runtime-bootstrap)
6. [Phase 3 — Download Orchestration](#phase-3--download-orchestration)
7. [Phase 4 — Cache, Errors, Diagnostics](#phase-4--cache-errors-diagnostics)
8. [Phase 5 — Hardening and Rollout](#phase-5--hardening-and-rollout)
9. [Release Plan](#9-release-plan)
10. [Risk Register](#10-risk-register)
11. [Rollback Procedures](#11-rollback-procedures)
12. [Definition of Done](#12-definition-of-done)

---

## 1. Strategy

### Sequencing rationale

The instinct is to start with runtime code, because that is where the visible API is. That would be the
wrong order here.

Today every build is a full build. Bundle hashes change wholesale, so a player downloads 100% of remote
content on every patch. **Until `BuildContentUpdate` is wired up, a beautifully engineered download
service just downloads everything, quickly and with a nice progress bar.** The pipeline is the binding
constraint, so it goes first.

Secondary reason: the pipeline produces the artifacts (real catalogs, real bundles, a real
`addressables_content_state.bin`) that Phases 2–4 need in order to be tested against anything other than
mocks. Building it first means every later phase can be verified end to end from day one.

### Guiding rules

1. **Every phase ships something usable.** No phase leaves the branch in a state where the package is
   worse than `main`.
2. **Nothing in `main` breaks.** Existing APIs stay working through the whole plan; deprecation happens
   in 4.1.0 and removal not before 5.0.0.
3. **Infrastructure and code advance together.** A runtime feature with no deployed content to test
   against is not done.
4. **Each phase has a falsifiable exit criterion.** "It works" is not an exit criterion; "a 2 MB content
   change produces a ≤ 2.5 MB patch, verified on device" is.

### Estimation basis

Effort is given in ideal engineer-days for one developer familiar with Addressables, excluding review
latency and infra provisioning waits. Multiply by your own factor.

---

## 2. Phase Overview

| Phase | Focus | Effort | Blocks | Ships as |
|-------|-------|--------|--------|----------|
| 0 | Upgrade, settings contract, profiles, local test server | 3 d | everything | 4.1.0-pre |
| 1 | Build + content-update CLI, content-state lifecycle | 5 d | 2, 3 | 4.1.0 |
| 2 | `CdnRuntime`, catalog service, decorator, offline fallback | 5 d | 3, 4 | 4.2.0 |
| 3 | Download service, retry, cancel, byte progress | 6 d | 4 | 4.3.0 |
| 4 | Cache service, error taxonomy, telemetry, editor tab | 4 d | 5 | 4.4.0 |
| 5 | Test matrix, device validation, docs, deprecation cleanup | 5 d | — | 5.0.0 |

**Total: ~28 ideal days.** Phases 1 and 2 can partially overlap if two people are available — Phase 2
only needs Phase 1's *output format*, not its finished tooling.

---

## Phase 0 — Foundations

**Goal:** the project is configured such that remote content is even possible, and there is a local
environment to test against without touching real infrastructure.

### Tasks

| # | Task | Notes |
|---|------|-------|
| 0.1 | Upgrade `com.unity.addressables` 2.3.1 → 2.9.x | Regression pass on existing loaders, scopes, pooling. Pin the exact version in `package.json`. |
| 0.2 | Decide TMP dependency | Move `com.unity.textmeshpro` out of hard `dependencies` — `TMP_PRESENT` versionDefine already handles it. Unrelated to CDN but cheap to fix while touching `package.json`. |
| 0.3 | Apply the settings contract | `BuildRemoteCatalog = true`, `BundleRetryCount = 3`, `BundleTimeout = 30`, `MaxConcurrentWebRequests = 6`, `MonoScriptBundleNaming = Project Name`, `InternalIdNamingMode = Filename`. See design §9. |
| 0.4 | Move `ContentStateBuildPath` outside `Assets/` | Target `ServerData/ContentState/[BuildTarget]/`. Add to `.gitignore`; CI archives it instead. |
| 0.5 | Create profiles `Local` / `Dev` / `Staging` / `Prod` | Populate `Remote.LoadPath` per environment. |
| 0.6 | Split catalog path from bundle path | `RemoteCatalogBuildPath` / `RemoteCatalogLoadPath` point at the per-app-version folder; group `LoadPath` points at the shared bundle folder. This is the key layout decision — see infra guide §2. |
| 0.7 | Local static HTTP server for testing | Editor menu item that serves `ServerData/` on `localhost:8080` with correct `Cache-Control` headers. ~100 lines over `HttpListener`. |
| 0.8 | Create a remote test group | A handful of real assets marked Remote so there is something to actually download. |

### Exit criteria

- A player build with `Local` profile boots, fetches its catalog from `localhost:8080`, and loads a
  remote asset — with **zero new runtime code written**. This proves the configuration is right before
  any abstraction is layered on it.
- All existing PlayMode/EditMode tests still pass after the Addressables upgrade.

### Risks

Addressables 2.3 → 2.9 may change `ResourceManager` exception types used by
`AssetLoader.DetermineErrorCode`. Budget half a day for fallout.

---

## Phase 1 — Build and Content-Update Pipeline

**Goal:** CI can produce a full build *and* a delta content update, deterministically, from the command
line, with the content-state file managed correctly.

### Deliverables

```
Editor/Cdn/
├── CdnBuildCLI.cs           batchmode entry points
├── CdnBuildPipeline.cs      BuildPlayerContent / BuildContentUpdate wrappers
├── ContentStateManager.cs   locate, validate, archive content_state.bin
├── CdnProfileManager.cs     profile switching + Remote path injection
└── CatalogVerifier.cs       post-build validation
```

### Tasks

| # | Task | Detail |
|---|------|--------|
| 1.1 | `CdnBuildCLI.BuildContent` | `-executeMethod`, args: `-cdnProfile`, `-cdnEnv`, `-buildTarget`, `-outputDir`. Wraps `AddressableAssetSettings.BuildPlayerContent(out result)`. Non-zero exit on failure. |
| 1.2 | `CdnBuildCLI.BuildContentUpdate` | Wraps `ContentUpdateScript.BuildContentUpdate(settings, contentStatePath)`. **Fails loudly if the state file is missing** rather than silently falling back to a full build. |
| 1.3 | `ContentStateManager` | Resolve path per platform + app version; validate the file is readable and matches the current app version; copy to a CI artifact directory after every build. |
| 1.4 | Content-update restriction check | Run `ContentUpdateScript.GatherModifiedEntries` and report which entries moved between static and dynamic content. Fail the build if a *static* group changed — that means the update is not actually delta-able and needs a new player build. |
| 1.5 | `CdnProfileManager` | `SetActiveProfile(name)`; inject `Remote.LoadPath` from an env var so URLs are not committed to the repo. |
| 1.6 | `CatalogVerifier` | Assert: settings contract (design §9) holds; catalog filename matches expected `catalog_<appVersion>`; every bundle referenced by the catalog exists in the output directory. |
| 1.7 | Build manifest emission | Write `build-manifest.json` next to the output: app version, catalog name, bundle count, total bytes, git SHA, timestamp, build type (full \| update). Feeds the upload step and gives the team an audit trail. |
| 1.8 | GitHub Actions workflow | Two jobs: `content-full` and `content-update`. See infra guide §7. |

### Exit criteria

**The measurable one:** make a trivial change to one remote asset, run `BuildContentUpdate`, and confirm
the resulting bundle set differs from the previous build by only the affected bundle(s). Target: a 2 MB
asset change produces ≤ 2.5 MB of changed bundles.

Also: `CatalogVerifier` fails the build when a bundle is deliberately deleted from the output.

### Risks

| Risk | Mitigation |
|------|-----------|
| Content-state file lost or not archived → no more delta updates for that app version | 1.3 makes archiving automatic; CI job fails if the artifact upload fails |
| Static content modified without noticing → update silently ineffective | 1.4 turns it into a build failure |
| `MonoScript` bundle churns on every build, inflating every patch | Fixed by 0.3 (`MonoScriptBundleNaming`); verified by comparing two consecutive update builds |

---

## Phase 2 — Runtime Bootstrap

**Goal:** the game can initialize against a CDN, detect and apply a catalog update, and boot successfully
with no network.

### Deliverables

```
Runtime/Cdn/
├── Cdn.cs                    (partial — boot APIs only)
├── CdnRuntime.cs
├── Config/CdnSettings.cs · CdnEnvironment.cs · DownloadPolicy.cs
├── Core/CdnErrorCode.cs · CdnError.cs · CdnResult.cs · CatalogUpdateInfo.cs
├── Services/ICatalogService.cs · CatalogService.cs · CdnRequestDecorator.cs
└── Policy/NetworkPolicy.cs
```

### Tasks

| # | Task | Detail |
|---|------|--------|
| 2.1 | `CdnResult<T>` / `CdnError` / `CdnErrorCode` | Mirror `LoadResult` / `LoadError` shape so the package stays idiomatic. Include `HttpStatusCode`, `Url`, `IsRetryable`. |
| 2.2 | `CdnSettings` + inspector | Environment list, token resolution, policy defaults. |
| 2.3 | `CdnRequestDecorator` | Install `WebRequestOverride` and `InternalIdTransformFunc` **before** `InitializeAsync`. Add an assertion that fires if init already happened. |
| 2.4 | `IHostRewriter` | Token substitution (`{platform}`, `{appVersion}`), environment switching, failover host promotion. |
| 2.5 | `CatalogService.InitializeAsync` | Explicit `Addressables.InitializeAsync()` await; detect the no-cache-no-network case → `NoContentAvailableOffline`. |
| 2.6 | `CatalogService.CheckForUpdateAsync` | `CheckForCatalogUpdates(autoReleaseHandle: false)`; offline → `Success(None, WasOfflineFallback = true)`. |
| 2.7 | `CatalogService.ApplyUpdateAsync` | `UpdateCatalogs(..., autoCleanBundleCache: true)`; report which catalogs were replaced. |
| 2.8 | `NetworkPolicy` | Reachability, metered detection, `WaitForReachableAsync`. |
| 2.9 | `Cdn` facade — boot subset | `InitializeAsync`, `CheckForUpdateAsync`, `ApplyUpdateAsync`, `CurrentEnvironmentId`, `IsInitialized`. |
| 2.10 | Sample boot scene | `Assets/Examples/CdnBoot/` demonstrating the canonical flow from design §6. |

### Exit criteria

- All five boot flows from design §7.1–7.5 reproduce against the local server, verified by an automated
  PlayMode test where possible and manually where not.
- Airplane mode on a device with warm cache: game boots, no exception, no blocking dialog.
- Environment switch from `Prod` to `Staging` at runtime, without a rebuild, resolves bundles from the
  staging host. This validates that `InternalIdTransformFunc` is wired correctly and is the single most
  useful capability of this phase for QA.

### Risks

| Risk | Mitigation |
|------|-----------|
| Hook installed after implicit initialization (a stray `Addressables.LoadAssetAsync` early in boot triggers init) | 2.3 assertion + document that `Cdn.InitializeAsync` must be the first Addressables call |
| `internetReachability` reports reachable behind a captive portal | Treated as a fast negative check only; real failures still flow through normal error handling |

---

## Phase 3 — Download Orchestration

**Goal:** downloads are cancellable, resumable, retried with backoff, and reported in real bytes.

### Deliverables

```
Runtime/Cdn/
├── Core/DownloadRequest.cs · DownloadProgress.cs · DownloadReport.cs
├── Services/IDownloadService.cs · DownloadService.cs
└── Policy/RetryPolicy.cs
```

### Tasks

| # | Task | Detail |
|---|------|--------|
| 3.1 | `RetryPolicy` | Exponential backoff + jitter; retryability driven by the design §8 table. Fully unit-testable, no Unity dependency. |
| 3.2 | `DownloadService.GetDownloadSizeAsync` | Accepts keys **and labels**, `MergeMode`. Returns `CdnResult<long>` — failure is distinguishable from zero. |
| 3.3 | `DownloadService.DownloadAsync` | `DownloadDependenciesAsync(keys, mergeMode, autoReleaseHandle: false)`; poll `GetDownloadStatus()`; EMA-smoothed speed; ETA `-1` when unknown rather than fabricated. |
| 3.4 | Progress throttling | Default 4 Hz. `DownloadProgress` is a struct delivered through `IProgress<T>` — no steady-state allocation. |
| 3.5 | Cancellation | `CancellationToken` on every entry point; release the handle; keep partial cache; return `CdnResult.Cancelled` distinct from failure. |
| 3.6 | Pre-flight checks | Reachability, metered gate, free disk vs. `size + MinFreeDiskBytes`. Each maps to its own error code. |
| 3.7 | Error mapping | `RemoteProviderException` → `CdnErrorCode` via HTTP status. **Verify the exception type and property names against the pinned Addressables version** — this changed between 1.x and 2.x. |
| 3.8 | CRC auto-repair | On `BundleCrcMismatch`: `ClearDependencyCacheAsync` for that key, retry once, then fail. |
| 3.9 | Rewrite `ProgressiveAssetLoader.DownloadWithProgressAsync` over `DownloadService` | Keeps the old signature; fixes the speed/ETA math and populates `BytesDownloaded` / `TotalBytes`. |
| 3.10 | Deprecate the old surface | `[Obsolete]` on `AssetLoader.DownloadDependenciesAsync`, `AssetLoader.GetDownloadSizeAsync`, `Assets.Download`, `Assets.GetDownloadSize`, `StandardAPI.DownloadDependencies`, `StandardAPI.GetDownloadSize`. |
| 3.11 | Patch UI sample | Extend `AddressableProgressBar` with a byte-accurate variant showing size, speed, ETA, cancel. |

### Exit criteria

- Fault-injection matrix from design §12 passes against the local server.
- Cancel at 30% of a 100 MB download, then restart: the second run transfers roughly the remaining 70%,
  not 100%. This is the concrete proof that resume works.
- Throttled to 50 KB/s, the reported speed stays within ±15% of actual and ETA converges monotonically.
- No GC allocation in the progress loop (verified with the Profiler over a 60-second download).

### Risks

| Risk | Mitigation |
|------|-----------|
| `GetDownloadStatus()` returns zero totals early in the operation | Report `TotalBytes = 0` as "calculating", never divide by it |
| Retrying a whole operation re-downloads already-complete bundles | Unity's cache makes completed bundles free on retry; verified by the cancel/resume test |
| Exception hierarchy differs from expectation | 3.7 explicitly budgets verification time; substring matching stays as `Unknown` fallback |

---

## Phase 4 — Cache, Errors, Diagnostics

**Goal:** disk usage is controllable, failures are attributable, and the team can inspect CDN state from
the editor.

### Tasks

| # | Task | Detail |
|---|------|--------|
| 4.1 | `CacheService` | `GetStats` (occupied / free / path), `ClearAsync(key)`, `ClearAllAsync`, `CleanObsoleteAsync`. |
| 4.2 | Obsolete-bundle cleanup on update | Call `CleanObsoleteAsync` after a catalog update. Without this, superseded bundles accumulate indefinitely — the usual cause of "why is this game 8 GB". |
| 4.3 | Cache budget warning | Raise an event when occupied exceeds a configurable threshold so the game can prompt. |
| 4.4 | `ICdnTelemetry` + `CdnDiagnostics` | Interface only; no transport bundled. Default implementation logs under the existing `DebugSettings` verbosity flags. |
| 4.5 | `LoadErrorCode.ContentNotDownloaded` | New code for "the asset is in the catalog but its bundle is not local" — currently indistinguishable from a generic failure. |
| 4.6 | CDN tab in `AddressableManagerWindow` | Environment, catalog id, cache occupied/free, last check time, last error; buttons for check-update, clear cache, clean obsolete. |
| 4.7 | `TROUBLESHOOTING.md` — CDN section | One entry per `CdnErrorCode`: symptom, cause, fix. |

### Exit criteria

- Two consecutive content updates leave no orphaned bundles on disk (measured via `spaceOccupied`
  before/after).
- Every `CdnErrorCode` is reachable in the fault-injection matrix and documented in `TROUBLESHOOTING.md`.
- The editor CDN tab reports accurate live state during a device-attached play session.

---

## Phase 5 — Hardening and Rollout

**Goal:** validated on real devices against real infrastructure, documented, and the deprecated surface
removed.

### Tasks

| # | Task |
|---|------|
| 5.1 | Full fault-injection suite green in CI (PlayMode + local server) |
| 5.2 | Device matrix: low-end Android, mid iOS; WiFi, cellular, airplane-mode toggle mid-download, near-full storage |
| 5.3 | Staging soak: three consecutive content updates on the same install, verifying delta sizes and cache hygiene |
| 5.4 | Load/cost sanity check against real CDN — egress, cache hit ratio at the edge, p95 latency by region |
| 5.5 | `CDN_USAGE_GUIDE.md` — game-developer-facing guide (the design doc is for the package author) |
| 5.6 | `README.md` + `CHANGELOG.md` updates |
| 5.7 | Remove the `[Obsolete]` surface → 5.0.0 |
| 5.8 | Migration note for existing integrators |

### Exit criteria

- A content-only change goes from commit to live on staging via CI with no manual Unity Editor step.
- A device with the previous build installed receives only the delta.
- Edge cache hit ratio ≥ 95% for bundles after warm-up.

---

## 9. Release Plan

| Version | Contents | Breaking |
|---------|----------|----------|
| `4.1.0` | Phase 0 + 1. Editor/CI only — no runtime API change. Addressables upgraded to 2.9.x. | Addressables upgrade; TMP moved to optional |
| `4.2.0` | Phase 2. `Cdn` boot APIs. Additive. | none |
| `4.3.0` | Phase 3. Download APIs; old surface deprecated. | `DownloadWithProgressAsync` now reports real speed/bytes (behaviour fix) |
| `4.4.0` | Phase 4. Cache, telemetry, editor tab. Additive. | none |
| `5.0.0` | Phase 5. Deprecated surface removed; `ProgressInfo`/`DownloadProgress` unified. | yes — documented migration |

Semver note: 4.3.0's speed/bytes change is a bug fix to a value that was previously meaningless, so it
does not warrant a major bump — but it is called out prominently in the changelog because UI calibrated
against the old number will look wrong.

---

## 10. Risk Register

| # | Risk | Likelihood | Impact | Mitigation | Owner phase |
|---|------|-----------|--------|-----------|-------------|
| R1 | `addressables_content_state.bin` lost for a shipped app version | Medium | **Critical** — that version can never receive a delta update again; all its players force-download full content | Automated archiving (1.3), CI failure if archiving fails, documented retention policy | 1 |
| R2 | Catalog uploaded before bundles → live 404s | Medium | High | Upload order enforced in the deploy script: bundles first, catalog last (infra §5) | 1 |
| R3 | `.hash` cached too long at the edge → players stuck on an old catalog | High if unconfigured | High | Explicit cache rule for `catalog_*.hash`; verified by a post-deploy header check | infra |
| R4 | Addressables 2.9 upgrade regresses existing loaders | Medium | Medium | Phase 0 does it first, in isolation, with the existing test suite as the gate | 0 |
| R5 | Static content accidentally modified in a content update | Medium | High — the update silently fails to apply for existing players | `GatherModifiedEntries` check fails the build (1.4) | 1 |
| R6 | Hooks installed after implicit Addressables init | Medium | High — catalog request not rewritten, wrong environment | Assertion (2.3) + boot-order documentation | 2 |
| R7 | Exception-type drift across Addressables versions breaks error mapping | Medium | Medium | Pin the version; substring fallback for `Unknown`; unit tests over synthetic exceptions | 3 |
| R8 | Bundle cache grows unbounded | High if unaddressed | Medium | `CleanObsoleteAsync` after every update (4.2) | 4 |
| R9 | CDN egress cost higher than modelled | Low with R2 | Medium | R2 zero-egress origin; telemetry on bytes served per version | 5 |
| R10 | Scope creep into MOD support / WebGL | Medium | Medium | Explicit non-goals in design §1; revisit YooAsset if either becomes a requirement | all |

---

## 11. Rollback Procedures

**Bad content deployed.** Re-upload the previous catalog for the affected app version. Because bundles
are content-addressed and append-only, the old bundles are still present — the catalog swap alone is a
complete rollback. Recovery time is one upload plus the `.hash` TTL (≤ 60 s).

**Bad player build.** Not rollback-able via CDN; requires a store update. This is why static content
changes must be caught at build time (R5).

**Bad package version in a game project.** Each phase is an independent minor version; revert the UPM
dependency. Because the old API surface stays until 5.0.0, downgrading from 4.x to 4.x is safe.

**Corrupted client cache in the field.** `Cdn.ClearCacheAsync(key)` for a targeted fix, or
`ClearAllAsync` as the nuclear option, gated behind a remote-config flag so it can be triggered without
a client update.

---

## 12. Definition of Done

The CDN system is done when all of the following are true:

1. A content-only change ships to players through CI with no manual Editor step.
2. Players receive only the delta; measured patch size is within 25% of the changed-asset size.
3. The game boots offline on a warm cache with no error dialog.
4. Every failure mode in the design §8 table is reachable in tests and documented in `TROUBLESHOOTING.md`.
5. Downloads are cancellable and resume from partial state.
6. Progress reports real bytes; speed and ETA are within ±15% under throttled conditions.
7. Disk usage does not grow across consecutive content updates.
8. Rolling back a bad content deploy takes one upload and under a minute.
9. Telemetry can answer "what is our download success rate this week, and where are failures concentrated".
