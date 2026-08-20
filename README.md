# Addressable Manager

[![Version](https://img.shields.io/badge/version-4.1.0--pre.9-blue.svg)](Packages/com.game.addressables/CHANGELOG.md)
[![Unity](https://img.shields.io/badge/Unity-2023.1%2B-black.svg)](#requirements)
[![Addressables](https://img.shields.io/badge/com.unity.addressables-2.9.1-black.svg)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

A Unity package that layers three things onto Addressables that Addressables leaves to you:
**scoped lifetimes**, so assets are released when the thing that needed them dies;
**reference-counted handles**, so two systems can hold the same asset without fighting over who
frees it; and a **CDN pipeline** — build, publish, boot against a remote catalog, download with
typed errors. The API comes in three tiers: `Simple` to prototype, `Standard` to ship, `Advanced`
when you need to own the machinery. They share the same caches, so mixing them is normal.

> **The reference documentation is
> [`Packages/com.game.addressables/README.md`](Packages/com.game.addressables/README.md).**
> This page orients you and routes you there. The two used to be near-duplicate copies of one
> guide; they drifted, and the drift shipped wrong dependency versions to whoever read the wrong
> copy. So this one is deliberately short and the package one is deliberately complete. Where they
> disagree, believe the package README — it ships inside the package and every API in it was
> checked against source.

---

## 🚀 Install

### Requirements

| Requirement | Version | Notes |
| :-- | :-- | :-- |
| Unity | **2023.1 or newer** | This floor is set by `com.unity.addressables` 2.9.1, which declares `unity: 2023.1` itself. It **cannot be lowered** while that dependency stands. |
| `com.unity.addressables` | **2.9.1** | Exactly this. 2.3.1 does not compile this package. Resolved automatically by UPM. |
| `com.unity.textmeshpro` | 3.0.6 | Declared as a dependency, so UPM installs it. The code that uses it is gated behind `TMP_PRESENT` and is limited to `AddressableProgressBar`. |
| UniTask (`com.cysharp.unitask`) | 2.3.0+ | **Optional.** Not a declared dependency. When present, `UNITASK_PRESENT` is defined and much of the async surface returns `UniTask<T>` instead of `Task<T>`. See [Task or UniTask](Packages/com.game.addressables/README.md#-task-or-unitask). |

This table is reproduced word for word from the
[package README](Packages/com.game.addressables/README.md#requirements), bar the link target in the
last row, which necessarily differs. Same for the install line below. Those two are the only
deliberate duplication left between the pages; if you ever find them disagreeing, that is the bug
this page was rewritten to prevent — fix it rather than picking one.

### Add the package

Package Manager → **Add package from git URL**, pinned to a tag:

```text
https://github.com/TeamAcMong/Addressable-System.git#4.1.0-pre.12
```

Always pin to a tag. Tracking a branch means a `git pull` can change your API surface. The
`Packages/manifest.json` form of the same line is in
[Add the package](Packages/com.game.addressables/README.md#add-the-package).

The URL needs no `?path=` even though this repository is a whole Unity project: release tags point
at a tree whose root *is* the package. See [How releases are cut](#how-releases-are-cut).

---

## ⚡ Quick start

The `Simple` tier, which is the whole of it for a prototype:

```csharp
using UnityEngine;
using UnityEngine.UI;
using AddressableManager.API;

public class TitleScreen : MonoBehaviour
{
    [SerializeField] private Image _logo;

    private async void Start()
    {
        // Returns the asset itself, not a handle — and null if the load failed.
        var sprite = await Simple.Load<Sprite>("UI/Logo");
        if (sprite != null)
            _logo.sprite = sprite;
    }

    // Safe during application quit: Simple.* checks for the global scope before touching it.
    private void OnDestroy() => Simple.ReleaseAddress("UI/Logo");
}
```

`Simple` never frees anything until you ask it to, which is fine for one sprite and fatal at scale.
Read [Ownership and lifetime](Packages/com.game.addressables/README.md#-ownership-and-lifetime)
before you build on this; it is the one section in the reference that a shipping game cannot skip.
If you are not sure which tier you want,
[Choosing a tier](Packages/com.game.addressables/README.md#choosing-a-tier) is a decision diagram.

---

## 🚦 Status

`4.1.0-pre.12` is a **pre-release**, and the label is doing real work: this line is far more correct
than 4.0.x, and far less proven.

- **A correctness pass produced `pre.6` through `pre.8`.** An external review verified **66
  defects** across the caches, loaders, API surface, scopes and pooling; `4.1.0-pre.6` ships the
  fixes for most of them. That review's summary was truncated partway through the pooling group, so
  the remainder was re-derived from the code — **22 more** — and `4.1.0-pre.7` fixed those, retired
  the `TieredAssetLoader` fork, and replaced three API methods that were stubs. Nearly every one was
  a lifetime bug: a reference taken and never given back, or given back twice, or a cache serving an
  asset it did not own.
- **`pre.8` is documentation only.** No `.cs` file changed: the release commit touches
  `CHANGELOG.md`, this file, and the `version` field of `package.json`. It exists because the two
  previous entries both claimed the Unity floor would drop to 2022.3, which was never possible.
- **`pre.9` answers a second external review (Qodo, on PR #3).** Two URL string comparisons were
  wrong — `HostRewriter.ResolveTokens` detected `{Platform}` case-insensitively and then substituted
  case-sensitively, and `CdnEnvironment.IsValid` rejected `HTTPS://` and compared its punctuation
  under the current culture. Both are ordinal now, covered by `CdnUrlCasingTests`. Separately, the
  batchmode compile gate that `pre.6` gave `AddressableCLI` was extended to `CdnSetupCLI` and both
  `CdnBuildCLI` build entry points, and a new `ci/unity-run.sh` reads the Unity log from outside the
  assembly, because an in-assembly gate cannot fire when the assembly is the thing that is broken.
- **The CDN layer has never been validated in the field.** No device matrix, no staging soak, no
  measurement against a real CDN, and the changelog records that the content workflows in
  `.github/workflows/` have never run — nor has `ci/unity-run.sh`. Its automated evidence is ten
  PlayMode tests against a local `HttpListener` plus 53 EditMode cases, all of it local. If you are
  the first to point it at a real CDN, expect to find something — see
  [Not yet validated](Packages/com.game.addressables/README.md#not-yet-validated).
- **Several surfaces are knowingly inert or deprecated.** Two `ScriptableObject` assets
  (`PoolConfiguration`, `AddressablePreloadConfig`) are read by no runtime code, and
  `TieredAssetLoader` is deprecated for removal in 5.0.0. The full list is
  [Known inert surfaces](Packages/com.game.addressables/README.md#-known-inert-surfaces).

For the per-area breakdown — what is shipping, what is off by default, what nobody has exercised —
read [Maturity by area](Packages/com.game.addressables/README.md#maturity-by-area) in the package
README. It is maintained in exactly one place, on purpose.

---

## 🧩 What is in the box

Each row links to the part of the package README that documents it. A table rather than a diagram
because the routing *is* the content here, and GitHub strips the interactivity out of rendered
mermaid, so a picture could not carry the links. The one diagram worth having — which tier to reach
for — already exists in the reference and is linked above rather than copied.

| Area | What it gives you | Reference |
| :-- | :-- | :-- |
| **Three tiers** | The asset, a handle you own, or the loader itself | [The three tiers](Packages/com.game.addressables/README.md#-the-three-tiers) |
| **Ownership and refcounting** | One rule for who disposes a loader; caches hold their own reference | [Ownership and lifetime](Packages/com.game.addressables/README.md#-ownership-and-lifetime) |
| **Scopes** | Four caches that share no state: global, named, Unity-object, and the older statics | [Four independent storages](Packages/com.game.addressables/README.md#four-independent-storages) |
| **Tiered caching** | Hot / Warm / Cold scoring with cold evicted first. Off unless you pass a config | [Tiered caching](Packages/com.game.addressables/README.md#-tiered-caching) |
| **Object pooling** | Prefab pools, dynamic pools on a clock, custom backends, scene-load sweep | [Object pooling](Packages/com.game.addressables/README.md#-object-pooling) |
| **CDN content delivery** | Build, publish, boot on a remote catalog, patch, download with progress | [CDN content delivery](Packages/com.game.addressables/README.md#-cdn-content-delivery) |
| **Progress reporting** | Per-load and per-download progress, plus a drop-in progress bar | [Progress reporting](Packages/com.game.addressables/README.md#-progress-reporting) |
| **Typed errors** | `LoadResult<T>` and `CdnResult<T>` instead of exceptions, with retryability | [LoadResult](Packages/com.game.addressables/README.md#loadresult) |
| **Threading** | Main thread by default, and honest about which paths are not | [Threading](Packages/com.game.addressables/README.md#-threading) |
| **Monitoring** | The `IAssetMonitor` pipeline that feeds the Dashboard. Editor-only — the monitor lives in the Editor assembly, so a player build carries none of it | [MONITORING_GUIDE.md](Packages/com.game.addressables/MONITORING_GUIDE.md) |
| **Dashboard** | Four tabs — Active Assets, Performance, Scopes, Settings — and no setup code | [Dashboard](Packages/com.game.addressables/README.md#dashboard) |
| **Rule automation** | Addresses, groups, labels and versions from rules at import time | [Rule automation](Packages/com.game.addressables/README.md#rule-automation) |
| **CDN Manager window** | Six tabs: Validator, Server, Update Preview, Build, Catalog, Runtime Monitor | [The CDN Manager window](Packages/com.game.addressables/README.md#the-cdn-manager-window) |
| **Command line** | Thirteen batchmode entry points for CI — five for rules, eight for the CDN | [Command line for CI](Packages/com.game.addressables/README.md#command-line-for-ci) |
| **Task or UniTask** | The same call sites either way, `UniTask<T>` when UniTask is installed | [Task or UniTask](Packages/com.game.addressables/README.md#-task-or-unitask) |

Upgrading from 4.0.x? Everything that changed, with a before/after for each rename, is in
[Migrating from 4.0.x](Packages/com.game.addressables/README.md#-migrating-from-40x).

---

## 📖 Documentation

### Reference and guides

These ship inside the package, so they are available offline to anyone who installs it.

| Document | What you will find |
| :-- | :-- |
| [Package README](Packages/com.game.addressables/README.md) | **The reference.** Every public API with its verified signature, the ownership rules, the CDN chapter, the deprecations, the migration guide. Read this after this page |
| [CHANGELOG.md](Packages/com.game.addressables/CHANGELOG.md) | Every release with its reasoning, including what was wrong before. The most reliable account of what actually changed and why |
| [CDN_USAGE_GUIDE.md](Packages/com.game.addressables/Documentation/CDN_USAGE_GUIDE.md) | The CDN road for the person building a game: whether you need remote content, setup, boot, download, patching, and what to do when each step fails |
| [TROUBLESHOOTING.md](Packages/com.game.addressables/Documentation/TROUBLESHOOTING.md) | Symptom-first fixes. Referenced by the package README, `Editor/Templates/README.md` and `Samples~/CdnBoot/README.md`; no runtime error message names it |
| [EDITOR_TOOLS_GUIDE.md](Packages/com.game.addressables/EDITOR_TOOLS_GUIDE.md) | Dashboard tabs column by column, the custom inspectors, the config assets, and the real menu paths |
| [MONITORING_GUIDE.md](Packages/com.game.addressables/MONITORING_GUIDE.md) | The monitoring pipeline behind the Dashboard, and why it costs a shipping build nothing |

Runnable code lives in the package's three samples — CDN Boot, API Examples, Rule Automation
Presets — imported from **Package Manager ▸ Addressable Manager ▸ Samples**, with two known rough
edges documented at [Samples](Packages/com.game.addressables/README.md#samples).

### Design notes and working documents

These stay in this repository and are not shipped in the package. They are written for whoever
works *on* the package rather than *with* it.

| Document | What you will find |
| :-- | :-- |
| [CDN_SYSTEM.html](Documentation/CDN_SYSTEM.html) · [CDN_SYSTEM_VI.html](Documentation/CDN_SYSTEM_VI.html) | The CDN design document — architecture, build phases, the decisions and their alternatives. HTML, so download and open it in a browser; GitHub will not render it. The `_VI` file is the Vietnamese twin |
| [LIFETIME_DESIGN.md](Documentation/LIFETIME_DESIGN.md) | Why the ownership rule is what it is, storage by storage, at play-mode exit, domain reload and quit — plus which parts are still design and not yet implemented. Required reading before changing lifetime code. The package README quotes it and points here, because it does not ship inside the package |
| [ADDRESSABLE_AUTOMATION_GUIDE.md](Documentation/ADDRESSABLE_AUTOMATION_GUIDE.md) | Long-form tour of the rule system: filters, providers, versioning, CI |
| [RULE_SYSTEM_EXAMPLES.md](Documentation/RULE_SYSTEM_EXAMPLES.md) | Worked rule setups for common cases — UI atlases, audio libraries, per-level content |
| [DEPLOY_UPM_SUBTREE.md](DEPLOY_UPM_SUBTREE.md) | The reasoning behind the subtree release, and recovery steps when a tag goes wrong |

> **Two cautions about these documents.** `Documentation/EDITOR_TOOLS_GUIDE.md` is a second, longer
> copy of the editor tools guide; the copy that ships in the package is the one kept in step with the
> code, so prefer it. And `DEPLOY_UPM_SUBTREE.md` still shows the old `Assets/com.game.addressables`
> path in its examples — `deploy.sh` sets `PREFIX="Packages/com.game.addressables"`, and that is the
> real one. Where any repository-only document disagrees with the package README, the package README
> wins: it is the one whose every API claim was checked against source.

`Documentation/` also holds handoff, review and task-tracking notes (`HANDOFF_TO_SESSION_B.md`,
`REFACTOR_TASKS.md`, `PARALLEL_SESSIONS.md`, `CDN_HANDOFF.html`, `REVIEW_4.1.0-pre.9.html`). They are
a live work log, not documentation, and they are partly in Vietnamese. `TROUBLESHOOTING.md` in that
folder is a six-line pointer at the package copy, not a second guide.

---

## 📁 Repository layout

### Where things live

This repository is a Unity project that *hosts* the package. Only one directory ships.

| Path | What it is |
| :-- | :-- |
| `Packages/com.game.addressables/` | **The package.** `Runtime/`, `Editor/`, `Tests/`, `Samples~/`, its own docs, and the `package.json` that is authoritative for version and dependencies. This directory is what the install URL resolves to |
| `Assets/`, `ProjectSettings/` | The host project — test scenes, example content, Addressables settings. Never shipped. It currently opens in Unity 6 with UniTask installed, which is a development choice and not the supported floor; see [Requirements](#requirements) |
| `Documentation/` | Design and process documents, repository-only. The package carries its own separate `Documentation/` folder |
| `ci/` | The shell steps the content workflows call: `unity-run.sh` (runs one batchmode step and reads the log for compiler errors, the CDN CLIs' `FAILURE:` prefix, and the artifact the step should have produced), upload bundles, upload catalog, verify URLs, purge, archive content state, fetch content state |
| `.github/workflows/` | `content-full.yml` and `content-update.yml` — a full content build and a delta patch for an already-shipped player version. Neither has run yet |
| `Tools/` | `CompileGate/run.sh` compiles the Runtime and Editor assemblies outside Unity; `check-min-unity-api.sh` compiles against an older editor as a canary for APIs newer than the floor allows |
| `deploy.sh` | Cuts a release — see below |

### Working in this repository

There is no `CONTRIBUTING.md`, and no issue or pull-request template. The conventions the codebase is
maintained under are in `CLAUDE.md`; the compile gate under `Tools/` is expected to pass on both
assemblies before anything is committed; and the release path is `deploy.sh`.

### How releases are cut

```bash
./deploy.sh --semver "4.1.0-pre.12"
```

The script `git subtree split`s `Packages/com.game.addressables` onto a temporary `upm` branch,
tags that commit with the version, pushes branch and tag, deletes the remote branch, and then
re-queries origin to confirm the tag actually landed before reporting success. Tags are permanent;
the branch is scaffolding.

That is why the install URL points straight at a tag with no subdirectory: the tag's tree root is
the package. It is also why `package.json` must carry the version you are tagging — bump it in the
same commit. And it is why a `git diff` between two release tags shows only package files: the tags
contain nothing else, so a change to this file appears in the release *commit* but not in the tag
range.

---

## 📄 License

MIT. [`LICENSE`](LICENSE) at the repository root, and
[`LICENSE.md`](Packages/com.game.addressables/LICENSE.md) inside the package.

The two are identical MIT text except for the copyright holder — `TeamAcMong` at the root,
`Game Team` in the package. That is unresolved drift, noted here rather than quietly picked.
