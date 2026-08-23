# Shipping content from a CDN

A guide for the person building the game. It covers the whole road: deciding whether
you need remote content at all, setting it up, the boot sequence, downloading,
patching, and what to do when each thing fails.

The design document (`Documentation/CDN_SYSTEM.html` at the repository root) is for
whoever works on this package. This is for whoever uses it.

> **Maturity.** The download, cache, error-mapping and build paths have automated
> coverage (`Tests/Runtime/CdnFaultInjectionTests.cs`,
> `Tests/Runtime/Phase2CdnBootIntegrationTests.cs`, `Tests/Editor/Cdn*Tests.cs`), which
> run against the local content server. The **catalog-update** flow —
> `CheckForUpdateAsync` / `ApplyUpdateAsync` — is the exception: it is built on
> `Addressables.CheckForCatalogUpdates`, which returned an empty list on every call in
> the Addressables versions this package was originally written against, so the flow was
> dead code. The package now pins `com.unity.addressables` 2.9.1, where the call works,
> but the update path has never been exercised end to end against a real CDN. Treat §4
> and §6 as untested code and verify them yourself before shipping a patch to players.

---

## Contents

1. [Do you need this?](#1-do-you-need-this)
2. [How it fits together](#2-how-it-fits-together)
3. [Setting up](#3-setting-up)
4. [The boot sequence](#4-the-boot-sequence)
5. [Downloading content](#5-downloading-content)
6. [Shipping a patch](#6-shipping-a-patch)
7. [The cache](#7-the-cache)
8. [Environments](#8-environments)
9. [Errors, and what to do about each](#9-errors-and-what-to-do-about-each)
10. [CI](#10-ci)
11. [The Editor window](#11-the-editor-window)
12. [Things that will catch you out](#12-things-that-will-catch-you-out)

---

## 1. Do you need this?

Remote content buys you two things: a smaller install, and the ability to change
content without shipping a build. It costs you a network dependency on first run, a
CDN bill, and a class of bug that only happens on other people's connections.

You probably want it if any of these are true:

- The store install limit is a real constraint (Google Play's 200 MB base, cellular
  download warnings on iOS).
- You ship content on a cadence faster than you can get builds through review.
- You have content that most players never see — a language pack, high-resolution
  textures, later chapters.

You probably do not want it if your whole game is 80 MB and ships twice a year. Local
Addressables groups give you the loading API and the memory management without any of
the delivery problems.

You can also do both, and most games should: keep the first-run experience local so
the game is playable the moment it opens, and put everything after that behind a
download.

---

## 2. How it fits together

```
Editor                          CDN                    Device
──────                          ───                    ──────
Build content        ──upload──▶ catalog            ◀──check──  CdnManager.InitializeAsync
  bundles/                       bundles/*.bundle   ◀──fetch──  CheckForUpdateAsync
  catalog/                                                      ApplyUpdateAsync
  build-manifest.json (kept private)                            DownloadAsync
```

Three pieces do the work:

- **The catalog** is an index: address → which bundle, how big, what hash. The game
  fetches it at boot. A content patch is, first and foremost, a new catalog.
- **The bundles** are the content. They are immutable — a changed asset produces a
  new bundle with a new name, it never overwrites the old one.
- **The manifest** (`build-manifest.json`, `BuildManifestWriter.ManifestFileName`) is
  written by this package alongside every build, next to the bundles. Addressables does
  not produce it. It carries a SHA256 of every bundle as written to disk, which is what
  makes verification and patch-size estimates trustworthy. It is a private build
  artifact — archive it, do not upload it.

The important consequence of immutable bundles: **your CDN accumulates**. Every patch
adds bundles and never removes any. Section 7 covers the device side; the server side
is your deployment's job, and the Catalog tab will tell you which bundles the current
catalog no longer references.

---

## 3. Setting up

### 3.1 Install and check

Add the package, then open **Window ▸ Addressable Manager ▸ CDN Manager** and look at
the **Validator** tab first. It checks the requirements a working CDN setup has —
Addressables settings (remote catalog, catalog build and load paths, player-version
override, content-state build path, bundle timeout and retry count, catalog request
timeout, naming and unique-bundle-id settings) plus per-group schema and timeout rules
— and offers **Fix All**.

**Fix All does not fix everything.** A rule is auto-fixable only when it carries a fix
delegate and is not warning-only; the rest are printed with a `[manual]` marker and are
yours to do. Re-check after fixing.

Do this before anything else. The alternative is discovering the same requirements one
404 at a time, from a device, with no error message that names the cause.

### 3.2 Create the settings asset

**Assets ▸ Create ▸ Addressable Manager ▸ CDN Settings**.

It must live in a folder named `Resources` and keep the file name `CdnSettings` — it
is loaded by `Resources.Load<CdnSettings>("CdnSettings")` (`CdnSettings.ResourceName`),
a fixed name, not a search. It cannot be an Addressable itself: it is the thing that
tells Addressables where to look.

The asset has five serialized settings:

| Field | Default | What it is |
|---|---|---|
| `environments` | one entry, id `Local` → `http://localhost:8080` | Every `CdnEnvironment` this build can be pointed at. |
| `defaultEnvironmentId` | `"Local"` | Which one `InitializeAsync()` picks when you pass no id. |
| `hostEnvironmentVariable` | `"CDN_BASE_URL"` | If this process environment variable is set, it overrides the active base URL. **Editor and standalone only** — mobile and console have no process environment, and the override is silently skipped there. |
| `downloadPolicy` | see §5 | How the CDN layer may use the network. |
| `logUrlRewrites` | `false` | Log every rewritten URL. Noisy; for diagnosing a wrong-host problem. |

Each `CdnEnvironment` carries:

| Field | What it is |
|---|---|
| `id` | How your code names it — `"Local"`, `"Dev"`, `"staging"`. Lookup is case-insensitive, and duplicate ids are a validation error. |
| `displayName` | Label for tools. |
| `baseUrl` | Where the content lives. **A trailing `/` is a validation error, not something the package trims.** `{platform}` and `{appVersion}` tokens are expanded (see §8). |
| `failoverUrls` | Optional alternates, promoted one at a time by `CdnManager.PromoteFailover()`. |

`CdnSettings.Validate()` returns every problem at once, and `InitializeAsync` refuses
to continue while the list is non-empty rather than failing later at a point that no
longer names the cause.

### 3.3 Mark content remote

In the Addressables Groups window, set the group's build and load paths to the remote
profile variables. The Validator tab will tell you if you missed one.

Decide **now**, per group, whether it is static:

- **Static content** (`StaticContent = true`) means "this will not change without a
  new player build". Addressables uses that promise when computing a patch.
- If you change an asset in a static group and build an update, Addressables **warns
  and reverts the entry to its previous bundle**. The patch ships without your change
  and the build succeeds. This is the single most expensive surprise in the whole
  system.

The Update Preview tab exists to catch exactly this before you publish, and
`CdnBuildPipeline.BuildUpdate` fails the build rather than letting it through. See §6.

---

## 3.4 Where the URL comes from, and the one rule that keeps it switchable

Two systems decide where content is fetched from, at two different times, and they have to agree.

| System | Decides | When |
| :-- | :-- | :-- |
| Addressables profile `Remote.LoadPath` / `Remote.CatalogLoadPath` | the URL **baked into the catalog** | build time |
| `CdnSettings.environments[].baseUrl` | the origin `HostRewriter` swaps **to** | runtime |

`HostRewriter.Rewrite` only rewrites a URL whose origin matches one it was told about, and it swaps
**only that prefix** — `activeBaseUrl + url.Substring(origin.Length)`. Everything after the origin
survives verbatim. Two consequences follow, and both used to be silent when broken.

### The convention

A remote path is **origin + suffix**, where the suffix is identical for every profile:

```
bundles   <origin>/[BuildTarget]/bundles
catalog   <origin>/[BuildTarget]/catalog/[UnityEditor.PlayerSettings.bundleVersion]
```

A path prefix belongs to the **origin**, not the suffix. A CDN serving several games from
`https://cdn.example.com/game-a` declares that whole string as the environment's `baseUrl`, and the
profile path is that string plus the shared suffix. That is why the generated profiles carry
`<cdnBase>` rather than `<domain>`: what replaces it is an origin, optionally including a path
prefix, never just a hostname.

> Before `4.1.0-pre.14` the generated templates broke this themselves: `Local` used
> `/[BuildTarget]/bundles` while `Dev`/`Staging`/`Prod` used `/game/[BuildTarget]/bundles`. Content
> built with `Local` and rewritten to `Prod` landed at a different path than content built with
> `Prod` directly — two routes, two CDN layouts, and whichever one you had not tested was broken.
> The suffix now comes from one constant per path kind, so the two cannot drift.

### Every origin a profile can bake must be in `CdnSettings`

If the baked origin is not one of the configured base URLs, `Rewrite` leaves the URL alone,
switching environment at runtime does nothing at all, and every request goes wherever the build was
pointed. The `settings.RemoteOriginIsKnown` rule in the Validator tab checks this before you build;
at runtime, the rewriter now warns once per unknown origin regardless of `logUrlRewrites`.

### Which profile should CI build with?

Build with the profile for the environment you are shipping to — `Prod` for a production release.
The URL that profile carries is the one your players will poll forever, and no later upload can
change it.

Runtime environment switching then still works, and is what QA uses: one build, pointed at staging
or dev without a rebuild, provided every one of those origins is declared in `CdnSettings`.

A build refuses to start if the active profile still contains a `<...>` placeholder. Filling it is
your job, not the package's — the real host is a release decision and usually a CI secret. Set it in
the Addressables profile, or call `CdnProfileManager.InjectRemoteHostFromEnvironment` with `CDN_HOST`
set before building.

## 3.5 Turning the CDN off

`CdnSettings > Build Mode` has two values:

| Mode | Meaning |
| :-- | :-- |
| `Remote` (default) | Content is published to a CDN and fetched at runtime. |
| `LocalOnly` | Everything ships inside the player. |

In `LocalOnly` the remote half of the configuration contract is **skipped rather than reported as
failing**: `settings.BuildRemoteCatalog` and `settings.RemoteOriginIsKnown` show as not applicable
and carry no auto-fix, and `CdnBuildPipeline` writes no remote manifest and runs no remote
verification — there is no remote output to verify, and verifying an empty directory against a
manifest of that same empty directory proves nothing.

This exists because a project that deliberately turned the CDN off used to be dragged back:
`settings.BuildRemoteCatalog` carried an unconditional auto-fix, so every "Fix All" and every
unattended `CdnSetupCLI` run switched it on again. Two rules writing the same field in opposite
directions means the winner is whichever ran last.

A project with **no `CdnSettings` asset at all** is treated as `LocalOnly`. That is the honest
reading rather than a fallback: the runtime CDN layer cannot function without that asset, so a
project without one is not publishing to a CDN whatever its Addressables settings say.

## 4. The boot sequence

### The one rule

```csharp
var init = await CdnManager.InitializeAsync();
```

**This must run before anything else touches Addressables.** Not "early in the first
scene" — before. Addressables initialises implicitly on its first load call, and the
CDN hooks (`Addressables.InternalIdTransformFunc` and `Addressables.WebRequestOverride`)
are only honoured for content resolved after they are installed.

Enough to break it:

- An `AssetReference` field on any object in the first scene.
- A `LoadAssetAsync` in another `Awake`.
- Anything that reads `Addressables.ResourceLocators` — including some third-party
  packages' initialisation.

When that happens the game boots normally and then 404s every remote bundle against
the wrong host. `CdnRequestDecorator.Install` detects it and `InitializeAsync` returns
a failure saying so, rather than continuing with a half-applied configuration — but a
failure at boot is still a failure, so put this in a bootstrap scene that contains
nothing else.

Both hooks chain: whatever the project already installed runs first and keeps its
effect, and `CdnRequestDecorator.Uninstall()` restores the captured delegates rather
than nulling them.

### The whole sequence

```csharp
using AddressableManager.Cdn;

async Task Boot()
{
    var init = await CdnManager.InitializeAsync();
    if (init.IsFailure)
    {
        // NoContentAvailableOffline is the one to handle specially: first run, no
        // network, nothing cached. There is no game to show yet — this needs a
        // blocking screen, not a toast.
        if (init.ErrorCode == CdnErrorCode.NoContentAvailableOffline)
        {
            ShowFirstRunNeedsNetworkScreen(init.ErrorMessage);
            return;
        }

        // Everything else: log it, and carry on with whatever is cached. A player
        // who already has content should not be stopped by a failed update check.
        Debug.LogError(init.Error);
    }

    var check = await CdnManager.CheckForUpdateAsync();
    if (check.IsSuccess)
    {
        // Read WasOfflineFallback FIRST. An offline result also has HasUpdate == false,
        // so testing HasUpdate alone reports "you are up to date" to a player whose
        // check never reached the server.
        if (check.Value.WasOfflineFallback)
            ShowCouldNotCheckNotice();
        else if (check.Value.HasUpdate)
            await CdnManager.ApplyUpdateAsync(check.Value);
    }

    LoadFirstScene();
}
```

`InitializeAsync`, `CheckForUpdateAsync`, `ApplyUpdateAsync`, `GetDownloadSizeAsync`
and `DownloadAsync` return `UniTask<CdnResult<T>>` when UniTask is installed and
`Task<CdnResult<T>>` otherwise. Awaiting them reads the same either way; only an
explicit `Task<...>` variable declaration does not.

**`ApplyUpdateAsync` refuses to run twice at once.** A second call while one is in
flight returns a failure (`CdnErrorCode.Unknown`, message "A catalog update is already
being applied") instead of racing the first. That matters because a boot flow plus a
"check for updates" button is enough to overlap, and Addressables mutates shared
locator state with no interlock. When you get that failure, wait, then call
`CheckForUpdateAsync` again — the `CatalogUpdateInfo` you are holding describes the
pre-update state and must not be applied on top of the result.

`ApplyUpdateAsync` also runs `Cache.CleanObsoleteAsync()` for you after a successful
apply; if the clean fails it logs a warning and still reports the apply as a success.

### Every call returns a result

Nothing in this API throws for an expected failure, and nothing returns a sentinel.
`CdnResult<T>` exposes:

```csharp
bool          IsSuccess;      // true when the operation produced a value
bool          IsFailure;
bool          IsRetryable;    // false on success; forwards Error.IsRetryable
bool          IsCancelled;    // Error.Code == CdnErrorCode.Cancelled
T             Value;          // default on failure — check IsSuccess first
CdnError      Error;          // null on success
CdnErrorCode  ErrorCode;      // CdnErrorCode.None on success
string        ErrorMessage;   // empty on success

T    Unwrap();                        // throws InvalidOperationException on failure
T    UnwrapOr(T fallback);
T    UnwrapOrElse(Func<CdnError, T> factory);
void Match(Action<T> onSuccess, Action<CdnError> onFailure);
CdnResult<U> Map<U>(Func<T, U> mapper);
CdnResult<U> FlatMap<U>(Func<T, CdnResult<U>> mapper);
// implicit operator bool, so `if (result)` reads naturally
```

`CdnError` carries `Code`, `Message`, `Hint`, `Url`, `HttpStatusCode` (0 when the
failure happened before a response arrived — DNS, timeout, no route), `Exception`, and
`IsRetryable`.

`IsRetryable` comes from the error itself (`CdnError.IsRetryableByDefault`), not from
the caller's judgement, so the CLI, the Editor and your runtime code cannot disagree
about what is worth retrying. Do not write your own retry decision on top of it. Note
what it means: "retryable" is not "retry immediately and silently" — `Unauthorized`,
`InsufficientDiskSpace` and `MeteredNetworkBlocked` are all retryable and all need
something to change first. The precondition is in `Hint`.

---

## 5. Downloading content

```csharp
var request = DownloadRequest.For("chapter-2");     // an address or a label
var progress = new Progress<DownloadProgress>(p => bar.SetDownloadProgress(p));

var result = await CdnManager.DownloadAsync(request, progress, cancellation.Token);
```

`DownloadRequest.For(object key, bool allowMeteredOverride = false)` covers the one-key
case. The full constructor takes several keys plus the knobs:

```csharp
new DownloadRequest(
    keys,                                       // IEnumerable<object>
    Addressables.MergeMode.Union,               // default
    minFreeDiskBytes: 64L * 1024 * 1024,        // default headroom left free afterwards
    allowMeteredOverride: false);
```

`minFreeDiskBytes` is enforced by a pre-flight check: `size + minFreeDiskBytes` must be
free, or the call fails with `InsufficientDiskSpace` before anything is transferred.

`DownloadProgress` is a readonly struct and carries real numbers, not a fraction
dressed up as one:

| Field | Meaning |
|---|---|
| `DownloadedBytes`, `TotalBytes` | Actual byte counts. `IsSizeKnown` is false while `TotalBytes` is 0, which means "not known yet", not "nothing to download". |
| `BytesPerSecond` | `double`, smoothed, so it does not flicker. Zero before the first measurement. |
| `EtaSeconds` | `double`. `-1` when it genuinely cannot be estimated yet. Show "calculating", not "-1 seconds". |
| `Percent` | `float` in **0..1**, not 0..100. Zero while the size is unknown. |

The call returns `CdnResult<DownloadReport>`; the report carries `BytesDownloaded`,
`TotalBytes`, `Duration`, `Attempts`, `RepairedCorruptBundle` and a derived
`AverageBytesPerSecond`.

### Ask the size first

```csharp
var size = await CdnManager.GetDownloadSizeAsync(request);
if (size.IsFailure) { /* could not find out — this is not "nothing to download" */ }
else if (size.Value == 0) { /* already on the device */ }
else if (size.Value > 50 * 1024 * 1024) { /* ask before spending the player's data */ }
```

`GetDownloadSizeAsync` returns `CdnResult<long>` precisely so that "zero bytes to
download" and "could not find out" are different answers. The old
`AssetLoader.GetDownloadSizeAsync` returned a bare `long` and could not tell you
which one you had; it is deprecated and goes away in 5.0.0.

### Retry and repair, and what they cover

`DownloadAsync` retries with exponential backoff and jitter (`RetryPolicy.Default`),
and on a `BundleCrcMismatch` it clears the dependency cache once and re-fetches before
retrying. By the time a retryable download error reaches you, it has already been
retried.

**This applies to downloads only.** `InitializeAsync`, `CheckForUpdateAsync` and
`ApplyUpdateAsync` go through `CatalogService`, which is constructed without a retry
policy — a transient 503 on a catalog request surfaces to you on the first attempt. If
you want a catalog operation retried, retry it yourself, gated on `IsRetryable`.

### Cancellation, and what "resume" actually means

Cancelling returns a result with `IsCancelled == true`. Bundles that had already
finished downloading stay in Unity's bundle cache, so restarting the download does not
re-fetch them. **The bundle that was mid-transfer is not resumed** — Unity's cache only
commits completed downloads, so that one restarts from zero. Do not clear the cache on
cancel; that throws away the bundles the player did finish.

### Metered connections and the download policy

`DownloadPolicy`, on the settings asset, has five fields. Only three are wired up:

| Field | Default | Effect |
|---|---|---|
| `requireUnmeteredNetwork` | **`false`** | When true, `NetworkPolicy.CanDownload()` refuses on a carrier connection with `MeteredNetworkBlocked`. **The default is false, so out of the box downloads are allowed on cellular.** Turn it on if you want the guard. |
| `timeoutSeconds` | `30` (range 1–300) | Applied to catalog, hash and text requests. **Deliberately not applied to bundle downloads** — Addressables' bundle timeout is an idle timer, and putting a wall-clock cap on a large bundle over a slow connection aborted it mid-transfer and, because the cache commits only completed downloads, made it permanently un-downloadable. |
| `reachabilityWaitSeconds` | `0` (range 0–120) | How long `NetworkPolicy.WaitForReachableAsync` waits by default. Zero returns immediately. |
| `maxRetries` | `3` | **Not read by anything.** Retry counts come from `RetryPolicy.Default`. |
| `maxConcurrentDownloads` | `6` | **Not read by anything.** |

Once the player has agreed to a mobile-data download, retry with
`DownloadRequest.For(key, allowMeteredOverride: true)` — consent is per download, which
is why it lives on the request rather than in settings. Read
`CdnManager.NetworkState` (a `NetworkReachabilityState`) if you want to make that
decision yourself.

---

## 6. Shipping a patch

### The first build

A **full build** produces the bundles, the catalog, and
`addressables_content_state.bin`. That last file is the baseline every later patch is
computed against. **Keep it.** Commit it, or store it as a CI artifact keyed by
version. Losing it means you cannot patch that build — ever, for the players running
it.

An **update** build never emits a new state file: Addressables gates that write on
there being no previous state. The baseline for the next update is still this build's
input file, so keep using the same one.

### A patch

1. Change the content.
2. Open **Update Preview**. It lists every entry in a static group that changed. If
   anything is listed, the patch will silently omit it. Select the entries and use
   **Prepare content update** — it moves them into a fresh non-static group, which is
   the fix. A new player build is *not* required.
3. Open **Build** and use **Build Update**. It gates, in order, on: script compilation,
   the state file existing, the state file matching the live
   `PlayerSettings.bundleVersion`, and the content-update restriction check — then
   builds, writes the manifest, and verifies the output against it.
4. Upload the new catalog and the new bundles. Do not delete old bundles yet;
   installed players are still running the old catalog.

The restriction check also fails the build when it *cannot* be evaluated. Unprovable is
not the same as safe.

### What the patch costs

The Build tab's "Patch cost of the last build" section reports the diff after building:
new bundles, changed bundles, removed bundles, unchanged count, and the bytes a player
will actually download (`ContentDiffResult.PatchSizeBytes` — removed bundles cost
nothing). This comes from comparing SHA256 hashes in the manifests, not sizes or
timestamps: on a content update Addressables rebuilds bundles whose assets did not
change and the result is usually byte-identical, so a size comparison would call those
changed and inflate every estimate.

### Before you upload

The **Catalog** tab reads the built catalog and checks it against the bundle folder.
Three things it will tell you:

- **Missing** — the catalog references a bundle that is not in the folder. Do not
  upload; every entry behind it will 404.
- **Size mismatch** — the catalog and the bundles came from different builds.
- **Orphaned** — bundles in the folder that this catalog no longer references. Not an
  error; this is your CDN storage bill (`OrphanBytes`). Check against the catalogs your
  installed players are running before deleting anything.

`IsPublishable` requires zero missing, zero size mismatches, **and** at least one
bundle actually checked — an inspection that verified nothing does not pass. The same
check runs headless (§10).

---

## 7. The cache

Addressables caches downloaded bundles on the device and **does not remove superseded
ones**. Left alone, an install grows by roughly one generation of content per patch.

`ApplyUpdateAsync` cleans them automatically. If you apply catalogs some other way,
call it yourself:

```csharp
await CdnManager.Cache.CleanObsoleteAsync();   // CdnResult<CacheStats>
```

Passing no catalog ids preserves every currently loaded catalog and removes what none
of them reference — which is exactly "the previous generation of bundles".

Other things worth knowing:

```csharp
var stats = CdnManager.GetCacheStats();  // CacheStats, a readonly struct
if (stats.IsValid)                       // false = the platform did not report,
{                                        // which is NOT the same as an empty cache
    Debug.Log($"{stats.OccupiedBytes} used, {stats.FreeBytes} free, at {stats.Path}");
}

await CdnManager.Cache.ClearAsync(new object[] { "chapter-2" });  // specific keys
CdnManager.Cache.ClearAll();                                      // everything, synchronous
```

`CdnManager.Cache` is null before `InitializeAsync` succeeds. `GetCacheStats()` is safe
to call at any time — it returns `CacheStats.Unavailable` (`IsValid == false`) when
there is no cache service yet.

`ClearAll` returning a failure while content is loaded is Unity's behaviour, not a
bug here — `Caching.ClearCache()` refuses while any bundle is live. Unload first, or
clear at a point where nothing is live.

---

## 8. Environments

Define as many as you need in the settings asset, then pick one at boot:

```csharp
await CdnManager.InitializeAsync(environmentId: "staging");
```

You can also switch afterwards with `CdnManager.SetEnvironment("staging")`, which
returns `CdnResult<string>`. It affects URLs resolved from then on; bundles already
downloaded stay cached and are not re-fetched from the new origin. That makes it useful
for QA and wrong for switching between environments that serve different content under
the same names.

`CdnManager.PromoteFailover()` advances to the next `failoverUrls` entry for the active
environment. `CdnManager.CurrentEnvironmentId` and `CurrentBaseUrl` report where you
are (empty strings before initialisation).

The `hostEnvironmentVariable` setting lets CI override the active base URL without
touching the asset, which is how you point a build at a per-branch bucket — but it is
read from the process environment, so it works in the Editor and in standalone players
and does nothing on mobile or console.

**Do not let a shipped build switch environments from a plain config value.** A
player-editable setting that repoints the CDN is a content-injection vector. Gate it
on a development build or a signed debug command.

### Platform paths

`{platform}` and `{appVersion}` in a `baseUrl` are expanded by
`HostRewriter.ResolveTokens`. `{appVersion}` is `Application.version`.

`{platform}` is the trap. The token is resolved by
`HostRewriter.ResolvePlatformToken()`, which reproduces the folder names Addressables
publishes into — `StandaloneWindows64`, `Android`, `iOS`. If you build a URL yourself
somewhere, do **not** use Unity's `PlatformMappingService`: it returns `Windows` for the
same target and will 404 everything. 64-bit is assumed; if you ship 32-bit Windows, do
not use `{platform}`. On a platform the token cannot be resolved for, resolution fails
with a message telling you to use a per-environment URL instead.

---

## 9. Errors, and what to do about each

Every failure carries a `CdnErrorCode`. The full table, one entry per code with cause
and fix, is in [TROUBLESHOOTING.md](TROUBLESHOOTING.md). The ones worth designing UI
around:

| Code | What happened | What the game should do |
|---|---|---|
| `NoContentAvailableOffline` | First run, no network, nothing cached. | Blocking screen. There is no game to fall back to. |
| `Offline` | No connection now, but content is cached. | Continue on cache; retry in the background. |
| `Timeout`, `ServerError` | Transient. `IsRetryable` is true. | Already retried with backoff **on downloads**; on catalog calls, retry yourself. |
| `CatalogNotFound` | 404 on the catalog or its hash file. | A deploy problem, not a player problem. Log loudly; continue on cache. Not retryable. |
| `CatalogParseFailed`, `CatalogVersionIncompatible` | The catalog downloaded but this build cannot use it. | Force an app update; a retry will not help. |
| `BundleNotFound` | 404 on a bundle the catalog references. | Catalog and bundles are out of sync — the Catalog tab would have caught this before upload. Not retryable. |
| `BundleCrcMismatch` | The response could not be processed into a bundle. `CdnErrorMapper` maps `UnityWebRequest.Result.DataProcessingError` here **regardless of HTTP status**, so a corrupt object served with a clean 200 lands here instead of falling through to `Unknown`. | On a download, the dependency cache is cleared once and the bundle re-fetched before you see it. If it recurs, the object on the CDN is corrupt and re-uploading is the fix. |
| `Unauthorized` | 401/403. | Signed URL expired or the token is wrong. Refresh the token your `CdnManager.AuthTokenProvider` returns — refresh it on your own schedule and cache it, never from inside the provider: it runs inside Addressables' update loop. Return the raw token; the `Bearer ` prefix is added for you. |
| `InsufficientDiskSpace` | Pre-flight failed: size + `MinFreeDiskBytes` exceeds free space. | Tell the player how much is needed. |
| `MeteredNetworkBlocked` | Policy refused a download on a metered connection. Only happens when you set `requireUnmeteredNetwork`. | Ask, then retry with `DownloadRequest.For(key, allowMeteredOverride: true)`. |
| `Cancelled` | Your own cancellation token fired. | Not an error. Completed bundles stay cached. |
| `Unknown` | Unmapped, or a package-level refusal: unusable settings, "not initialised", a second concurrent `ApplyUpdateAsync`, a cache operation Unity declined. | Read `Message` and `Hint`. Treated as non-retryable. |

One more that is not a `CdnErrorCode`: **`LoadErrorCode.ContentNotDownloaded`**
(`AddressableManager.Core`) comes back from a *load* call and means "the address is
valid but the bundle is not on this device". Download it — do not report it as a
missing asset.

`CdnErrorMapper` classifies from the HTTP response code, not from message text, and
falls back to parsing `ResponseCode : NNN` out of the flattened text that
`CheckCatalogsOperation` produces instead of nesting its child exceptions. Only when
neither is available does it reach `Unknown`.

---

## 10. CI

Every Editor entry point is available in batchmode, exits non-zero on failure, and
gates on `EditorUtility.scriptCompilationFailed` first.

**The profile names are `Local`, `Dev`, `Staging`, `Prod`.** There is no `Production`.

```bash
UNITY=".../Unity.exe"
P="-batchmode -quit -nographics -projectPath . -logFile -"

# One-time setup of profiles, paths and schemas   (-profile, default Local)
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnSetupCLI.ApplyPhaseZeroSetup -profile Prod

# Full build                                       (-cdnProfile, default Local)
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent -cdnProfile Prod

# Patch build            (-cdnProfile; optional -contentStatePath <file>)
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContentUpdate -cdnProfile Prod

# Verify the output against its manifest  (-cdnProfile; optional -manifest or -manifestPath)
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.VerifyOutput -cdnProfile Prod

# Check the catalog against the bundles that are about to be uploaded
# (optional -catalogPath <file> and -bundleDir <dir>; with neither, the active profile decides)
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CatalogInspectCLI.Inspect
```

Exit codes: `0` success, `1` the operation ran and failed (build refused, verification
failed, catalog not publishable, a setup rule still failing), `2` an exception or a
missing required argument. `CatalogInspectCLI.Inspect` exits `0` when the catalog is
publishable — orphaned bundles are reported but do not fail the run.

**Never trust Unity's exit code alone.** Unity exits 0 when `-executeMethod` runs
against an assembly that did not compile — the method simply never runs. Grep the log
for `error CS`, and check that the side effect you expected actually happened.

---

## 11. The Editor window

**Window ▸ Addressable Manager ▸ CDN Manager**

Six tabs, in this order. The labels on the tab strip are exactly these:

| Tab | Answers |
|---|---|
| **Validator** | Is this project configured for remote content? **Fix All** for what can be fixed automatically, **Re-check**, **Copy report**. |
| **Server** | Serves a built content folder over HTTP on localhost, port editable (default 8080), with real `Cache-Control` and `ETag` headers and range-request support, and a live request log. Test the whole flow with no CDN. |
| **Update Preview** | What would a content update silently omit, and **Prepare content update** to fix it. |
| **Build** | **Build Full** / **Build Update** with the gates in the right order, **Verify output**, and the patch cost of the last build. |
| **Catalog** | What is actually in the built catalog, and does it match the bundles on disk. **Re-scan**, **Show bundle folder**, **Copy report**. |
| **Runtime Monitor** | Live state during play mode. **Check for update**, **Clean obsolete**, **Clear cache**, **Refresh**. |

The local server is also driveable from the main menu without the window:
**Tools ▸ Addressable Manager ▸ Start Local Content Server** and
**Tools ▸ Addressable Manager ▸ Stop Local Content Server** (greyed out when it is not
running).

The Server tab is the one to start with. It lets you exercise boot, update and
download end to end before you have a CDN at all, and it is what this package's own
integration tests run against.

---

## 12. Things that will catch you out

**A static group that changed ships an empty patch.** Covered in §3.3 and §6, repeated
here because it is the one that costs a release.

**`InitializeAsync` must be first.** §4. One `AssetReference` in the boot scene is
enough.

**The catalog-update flow is the least-exercised part of this package.** See the note
at the top. Test it against the local server, and then against a real origin, before a
patch depends on it.

**A trailing slash in `baseUrl` fails validation.** It is not trimmed for you, and
`InitializeAsync` will not start with an invalid settings asset.

**Keep `addressables_content_state.bin`.** §6. Without it you cannot patch a shipped
build.

**Line endings can change bundle contents.** With `core.autocrlf` on and no
`.gitattributes`, git rewrites LF to CRLF in text assets on checkout, and the same
commit then produces different bundles on different machines. Mark text assets that go
into bundles as `-text`.

**Your CDN storage only grows.** Bundles are immutable and old catalogs keep
referencing old bundles. Plan a retention policy based on which client versions you
still support, and use the Catalog tab's orphan list as input, not as an instruction.

**"Could not check" is not "no update".** Both leave `HasUpdate` false. Look at
`WasOfflineFallback` before telling the player they are up to date.

**Catalog calls are not retried.** Only `DownloadAsync` has a retry policy. §5.

**Downloads are allowed on cellular by default.** `requireUnmeteredNetwork` is `false`
out of the box. §5.

**Fast Mode proves nothing about your CDN.** With the Addressables Play Mode script
set to "Use Asset Database", loads resolve from the AssetDatabase and never touch the
network. A green test in that mode says nothing about whether your content is
reachable. Use the Server tab and check its request log.

---

## Where to go next

- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — one entry per error code.
- The **CDN Boot** sample (`Samples~/CdnBoot/CdnBootExample.cs`) — the boot sequence as
  runnable, commented code.
- `Documentation/CDN_SYSTEM.html` at the repository root — the design document, if you
  need to know why something works the way it does.
