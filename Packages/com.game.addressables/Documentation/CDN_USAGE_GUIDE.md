# Shipping content from a CDN

A guide for the person building the game. It covers the whole road: deciding whether
you need remote content at all, setting it up, the boot sequence, downloading,
patching, and what to do when each thing fails.

The design document (`CDN_SYSTEM.html` in the repository) is for whoever works on
this package. This is for whoever uses it.

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
Editor                          CDN                        Device
──────                          ───                        ──────
Build content        ──upload──▶ catalog_1.0.0.bin  ◀──check──  CdnManager.InitializeAsync
  bundles/                       bundles/*.bundle   ◀──fetch──  CheckForUpdateAsync
  catalog/1.0.0/                                               ApplyUpdateAsync
  build-manifest.json                                          DownloadAsync
```

Three pieces do the work:

- **The catalog** is an index: address → which bundle, how big, what hash. The game
  fetches it at boot. A content patch is, first and foremost, a new catalog.
- **The bundles** are the content. They are immutable — a changed asset produces a
  new bundle with a new name, it never overwrites the old one.
- **The manifest** (`build-manifest.json`) is written by this package alongside every
  build. Addressables does not produce it. It carries a SHA256 of every bundle as
  written to disk, which is what makes verification and patch-size estimates
  trustworthy.

The important consequence of immutable bundles: **your CDN accumulates**. Every patch
adds bundles and never removes any. Section 7 covers the device side; the server side
is your deployment's job, and the Catalog Inspector tab will tell you which bundles
the current catalog no longer references.

---

## 3. Setting up

### 3.1 Install and check

Add the package, then open **Window ▸ Addressable Manager ▸ CDN Manager** and look at
the **Settings Validator** tab first. It checks every requirement a working CDN setup
has — profile variables, group schemas, catalog settings, build paths — and offers
**Fix All** for the ones that can be fixed automatically.

Do this before anything else. The alternative is discovering the same requirements one
404 at a time, from a device, with no error message that names the cause.

### 3.2 Create the settings asset

**Assets ▸ Create ▸ Addressable Manager ▸ CDN Settings**.

It must live in a folder named `Resources` and keep the file name `CdnSettings` — it
is loaded by `Resources.Load<CdnSettings>("CdnSettings")`, a fixed name, not a search.
It cannot be an Addressable itself: it is the thing that tells Addressables where to
look.

Fill in at least one environment:

| Field | What it is |
|---|---|
| `id` | How your code names it — `"production"`, `"staging"`. |
| `baseUrl` | Where the content lives, without a trailing slash. |
| `hostEnvironmentVariable` | Optional. An env var that overrides `baseUrl` — this is how CI points a build at a throwaway bucket without editing an asset. |

### 3.3 Mark content remote

In the Addressables Groups window, set the group's build and load paths to the remote
profile variables. The Settings Validator will tell you if you missed one.

Decide **now**, per group, whether it is static:

- **Static content** (`StaticContent = true`) means "this will not change without a
  new player build". Addressables uses that promise when computing a patch.
- If you change an asset in a static group and build an update, Addressables **warns
  and reverts the entry to its previous bundle**. The patch ships without your change
  and the build succeeds. This is the single most expensive surprise in the whole
  system.

The Update Preview tab exists to catch exactly this before you publish, and the build
pipeline in this package fails the build rather than letting it through. See §6.

---

## 4. The boot sequence

### The one rule

```csharp
var init = await CdnManager.InitializeAsync();
```

**This must run before anything else touches Addressables.** Not "early in the first
scene" — before. Addressables initialises implicitly on its first load call, and the
CDN hooks (the host rewriter and the request decorator) are only honoured for content
resolved after they are installed.

Enough to break it:

- An `AssetReference` field on any object in the first scene.
- A `LoadAssetAsync` in another `Awake`.
- Anything that reads `Addressables.ResourceLocators` — including some third-party
  packages' initialisation.

When that happens the game boots normally and then 404s every remote bundle against
the wrong host. `InitializeAsync` detects it and fails with a message saying so,
rather than continuing with a half-applied configuration — but a failure at boot is
still a failure, so put this in a bootstrap scene that contains nothing else.

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
        if (init.Error.Code == CdnErrorCode.NoContentAvailableOffline)
        {
            ShowFirstRunNeedsNetworkScreen(init.Error.Message);
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

### Every call returns a result

Nothing in this API throws for an expected failure, and nothing returns a sentinel.

```csharp
public class CdnResult<T>
{
    bool          IsSuccess;
    bool          IsFailure;
    T             Value;         // only meaningful when IsSuccess
    CdnError      Error;         // Code, Message, Hint, HttpStatusCode, IsRetryable
    CdnErrorCode  ErrorCode;     // shorthand; CdnErrorCode.None on success
    string        ErrorMessage;  // shorthand; empty on success
    T UnwrapOr(T fallback);
}
```

`Error.IsRetryable` comes from the error itself, not from the caller's judgement, so
the CLI, the Editor and your runtime code cannot disagree about what is worth
retrying. Do not write your own retry decision on top of it.

---

## 5. Downloading content

```csharp
var request = DownloadRequest.For("chapter-2");     // an address or a label
var progress = new Progress<DownloadProgress>(p => bar.SetDownloadProgress(p));

var result = await CdnManager.DownloadAsync(request, progress, cancellation.Token);
```

`DownloadProgress` carries real numbers, not a fraction dressed up as one:

| Field | Meaning |
|---|---|
| `DownloadedBytes`, `TotalBytes` | Actual byte counts. `IsSizeKnown` is false when the total is not known yet. |
| `BytesPerSecond` | Smoothed over a short window, so it does not flicker. |
| `EtaSeconds` | `-1` when it genuinely cannot be estimated yet. Show "calculating", not "-1 seconds". |
| `Percent` | Convenience, derived from the two byte counts. |

The call returns `CdnResult<DownloadReport>`; the report carries what actually
happened — bytes, duration, how many attempts it took, and whether a corrupt bundle
had to be re-fetched.

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

### Cancellation keeps what it got

Cancelling leaves partially downloaded bundles in the cache. Resuming re-downloads
only what is missing. Do not clear the cache on cancel — that throws away the
player's progress.

### Metered connections

`DownloadPolicy` on the settings asset decides whether downloads may start on a
metered connection, and whether they may start at all without an explicit call. The
default refuses large downloads on cellular. Check `CdnManager.NetworkState` if you
want to make that decision yourself.

---

## 6. Shipping a patch

### The first build

A **full build** produces the bundles, the catalog, and
`addressables_content_state.bin`. That last file is the baseline every later patch is
computed against. **Keep it.** Commit it, or store it as a CI artifact keyed by
version. Losing it means you cannot patch that build — ever, for the players running
it.

### A patch

1. Change the content.
2. Open **Update Preview**. It lists every entry in a static group that changed. If
   anything is listed, the patch will silently omit it. Use **Prepare for content
   update** — it moves the changed entries into a fresh non-static group, which is the
   fix. A new player build is *not* required.
3. Open **Build** and use **Build Update**. It gates on: compilation, the state file
   existing, the state file matching, and the static-content check — then builds,
   writes the manifest, and verifies the output against it.
4. Upload the new catalog and the new bundles. Do not delete old bundles yet;
   installed players are still running the old catalog.

### What the patch costs

The Build tab reports the patch size after building — how many bundles are new,
changed, unchanged, and what a player will actually download. This comes from
comparing SHA256 hashes in the manifests, not sizes or timestamps: on a content
update Addressables rebuilds bundles whose assets did not change and the result is
usually byte-identical, so a size comparison would call those changed and inflate
every estimate.

### Before you upload

The **Catalog Inspector** tab reads the built catalog and checks it against the
bundle folder. Three things it will tell you:

- **Missing** — the catalog references a bundle that is not in the folder. Do not
  upload; every entry behind it will 404.
- **Size mismatch** — the catalog and the bundles came from different builds.
- **Orphaned** — bundles in the folder that this catalog no longer references. Not an
  error; this is your CDN storage bill. Check against the catalogs your installed
  players are running before deleting anything.

The same check runs headless (§10).

---

## 7. The cache

Addressables caches downloaded bundles on the device and **does not remove superseded
ones**. Left alone, an install grows by roughly one generation of content per patch.

`ApplyUpdateAsync` cleans them automatically. If you apply catalogs some other way,
call it yourself:

```csharp
await CdnManager.Cache.CleanObsoleteAsync();
```

Other things worth knowing:

```csharp
var stats = CdnManager.GetCacheStats();
if (stats.IsValid)                       // false = the platform did not report,
{                                        // which is NOT the same as an empty cache
    Debug.Log($"{stats.OccupiedBytes} used, {stats.FreeBytes} free, at {stats.Path}");
}

await CdnManager.Cache.ClearAsync(new object[] { "chapter-2" });  // specific keys
CdnManager.Cache.ClearAll();                                      // everything
```

`ClearAll` returning a failure while content is loaded is Unity's behaviour, not a
bug here. Unload first, or clear at a point where nothing is live.

---

## 8. Environments

Define as many as you need in the settings asset, then pick one at boot:

```csharp
await CdnManager.InitializeAsync(environmentId: "staging");
```

The `hostEnvironmentVariable` field lets CI override a `baseUrl` without touching the
asset, which is how you point a build at a per-branch bucket.

**Do not let a shipped build switch environments from a plain config value.** A
player-editable setting that repoints the CDN is a content-injection vector. Gate it
on a development build or a signed debug command.

### Platform paths

Content is published under a platform folder, and the token used is Unity's
`BuildTarget` name — `StandaloneWindows64`, `Android`, `iOS`. If you build the URL
yourself somewhere, do **not** use Unity's `PlatformMappingService`: it returns
`Windows` for the same target and will 404 everything. `HostRewriter.ResolvePlatformToken()`
is the one this package uses.

---

## 9. Errors, and what to do about each

Every failure carries a `CdnErrorCode`. The full table, one entry per code with cause
and fix, is in [TROUBLESHOOTING.md](TROUBLESHOOTING.md). The ones worth designing UI
around:

| Code | What happened | What the game should do |
|---|---|---|
| `NoContentAvailableOffline` | First run, no network, nothing cached. | Blocking screen. There is no game to fall back to. |
| `Offline` | No connection now, but content is cached. | Continue on cache; retry in the background. |
| `Timeout`, `ServerError` | Transient. `IsRetryable` is true. | Already retried with backoff. Surface only after that fails. |
| `CatalogNotFound` | 404 on the catalog. | A deploy problem, not a player problem. Log loudly; continue on cache. |
| `CatalogParseFailed`, `CatalogVersionIncompatible` | The catalog downloaded but this build cannot use it. | Force an app update; a retry will not help. |
| `BundleNotFound` | 404 on a bundle the catalog references. | Catalog and bundles are out of sync — the Catalog Inspector would have caught this before upload. |
| `BundleCrcMismatch` | The bytes arrived corrupt. | Retried once with a cache repair before you see it. |
| `Unauthorized` | 401/403. | Signed URL expired or the token is wrong. Refresh `CdnManager.AuthTokenProvider`. |
| `InsufficientDiskSpace` | Pre-flight failed. | Tell the player how much is needed. |
| `MeteredNetworkBlocked` | Policy refused a download on a metered connection. | Ask, then retry with `DownloadRequest.For(key, allowMeteredOverride: true)`. |
| `Cancelled` | Your own cancellation token fired. | Not an error. Partial downloads stay cached. |

One more that is not a `CdnErrorCode`: **`LoadErrorCode.ContentNotDownloaded`** comes
back from a *load* call and means "the address is valid but the bundle is not on this
device". Download it — do not report it as a missing asset.

Retry is already handled: `RetryPolicy` applies exponential backoff with jitter to
anything whose error says it is retryable. By the time a retryable error reaches you,
it has already been retried.

---

## 10. CI

Every Editor entry point is available in batchmode, exits non-zero on failure, and
gates on `EditorUtility.scriptCompilationFailed` first.

```bash
UNITY=".../Unity.exe"
P="-batchmode -quit -nographics -projectPath . -logFile -"

# One-time setup of profiles, paths and schemas
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnSetupCLI.ApplyPhaseZeroSetup -profile Production

# Build
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent -cdnProfile Production

# Verify the output against its manifest
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.VerifyOutput -cdnProfile Production

# Check the catalog against the bundles that are about to be uploaded
"$UNITY" $P -executeMethod AddressableManager.Editor.Cdn.CatalogInspectCLI.Inspect
```

**Never trust Unity's exit code alone.** Unity exits 0 when `-executeMethod` runs
against an assembly that did not compile — the method simply never runs. Grep the log
for `error CS`, and check that the side effect you expected actually happened.

---

## 11. The Editor window

**Window ▸ Addressable Manager ▸ CDN Manager**

| Tab | Answers |
|---|---|
| **Settings Validator** | Is this project configured for remote content? Fix All for what can be fixed. |
| **Local Server** | Serves a built content folder over HTTP on localhost, with real cache headers. Test the whole flow with no CDN. |
| **Update Preview** | What would a content update silently omit, and the Prepare step that fixes it. |
| **Build** | Build full or update, with the gates in the right order, and what the patch costs a player. |
| **Catalog Inspector** | What is actually in the built catalog, and does it match the bundles on disk. |
| **Runtime Monitor** | Live state during play mode: environment, catalog, network, cache, obsolete bundles. |

The Local Server tab is the one to start with. It lets you exercise boot, update and
download end to end before you have a CDN at all, and it is what this package's own
integration tests run against.

---

## 12. Things that will catch you out

**A static group that changed ships an empty patch.** Covered in §3.3 and §6, repeated
here because it is the one that costs a release.

**`InitializeAsync` must be first.** §4. One `AssetReference` in the boot scene is
enough.

**Keep `addressables_content_state.bin`.** §6. Without it you cannot patch a shipped
build.

**Line endings can change bundle contents.** With `core.autocrlf` on and no
`.gitattributes`, git rewrites LF to CRLF in text assets on checkout, and the same
commit then produces different bundles on different machines. Mark text assets that go
into bundles as `-text`.

**Your CDN storage only grows.** Bundles are immutable and old catalogs keep
referencing old bundles. Plan a retention policy based on which client versions you
still support, and use the Catalog Inspector's orphan list as input, not as an
instruction.

**"Could not check" is not "no update".** Both leave `HasUpdate` false. Look at
`WasOfflineFallback` before telling the player they are up to date.

**Fast Mode proves nothing about your CDN.** With the Addressables Play Mode script
set to "Use Asset Database", loads resolve from the AssetDatabase and never touch the
network. A green test in that mode says nothing about whether your content is
reachable. Use the Local Server tab and check the server's request log.

---

## Where to go next

- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — one entry per error code.
- The **CDN Boot** sample — the boot sequence as runnable, commented code.
- `CDN_SYSTEM.html` in the repository — the design document, if you need to know why
  something works the way it does.
