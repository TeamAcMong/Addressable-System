# Addressable Automation Guide

**Complete guide to automating addressable asset management using the rule-based system**

Version 4.1.0-pre.9 | Package: `com.game.addressables`

---

## Table of Contents

1. [Introduction](#introduction)
2. [Core Concepts](#core-concepts)
3. [Quick Start](#quick-start)
4. [Rule Types](#rule-types)
5. [Filters](#filters)
6. [Providers](#providers)
7. [Advanced Workflows](#advanced-workflows)
8. [CI/CD Integration](#cicd-integration)
9. [Best Practices](#best-practices)
10. [Troubleshooting](#troubleshooting)

---

## Introduction

The Addressable Automation System transforms manual addressable asset management into a rule-based workflow. Instead of manually configuring each asset, you define rules that automatically assign addresses, labels, and versions based on asset paths, types, and other criteria.

The whole rule system is **Editor-only**. Rules are applied in the Editor, or in batch mode via the CLI; nothing in this system runs in a player build.

### Why Use Automation?

**Manual Approach Problems:**
- ❌ Time-consuming for large projects (100+ assets)
- ❌ Error-prone (typos, inconsistencies)
- ❌ Hard to maintain (changes require manual updates)
- ❌ No version control for asset configuration
- ❌ Difficult to onboard new team members

**Automation Benefits:**
- ✅ Configure once, apply everywhere
- ✅ Consistent naming and organization
- ✅ Easy to modify and refactor
- ✅ Version-controllable JSON rules
- ✅ CI/CD pipeline integration
- ✅ Team-shareable templates

### What You Can Automate

- **Address Assignment**: Generate addresses from filename or path
- **Label Management**: Apply platform, quality, content-type, or custom labels
- **Version Tagging**: Stamp assets with a `version:` label built from build numbers, git commits, or timestamps
- **Group Organization**: Route assets to appropriate addressable groups

---

## Core Concepts

### The Rule-Based System

The automation system uses three types of rules that work together:

```
Asset → Filters (Match?) → Provider (Generate) → Output
```

**1. Address Rules**
- Determine which group an asset belongs to
- Generate the address used for asset loading
- Control group membership

**2. Label Rules**
- Add metadata tags to assets
- Enable conditional loading (platform, quality, language)
- Support multiple labels per asset

**3. Version Rules**
- Tag assets with a `version:<value>` label
- Track content across builds

> **Versions are labels.** A version rule does not write to a separate field. It writes an ordinary
> Addressables label of the form `version:1.0.0`, and removes any older `version:` label on the same
> entry first. Label rules deliberately never touch labels beginning with `version:`.

### Anatomy of a Rule

All three rule types share the same core fields:

```
Rule {
    Rule Name:   "Descriptive rule name"   (default: "New <Type> Rule")
    Enabled:     true                      (default: true)
    Description: "What this rule does"
    Priority:    0                         (default: 0; higher = processed first)
    Filters:     [ ... ]                   (AND logic; at least one is required)
    Provider:    <one provider asset>      (required)
}
```

Each type then adds its own fields:

| Rule type | Extra fields (defaults) |
|---|---|
| Address | `Target Group Name` (empty = default group), `Group Template` (optional `AddressableAssetGroupTemplate`), `Skip Existing` (false), `Allow Group Move` (true) |
| Label | `Append to Existing` (**true**) |
| Version | `Skip Existing` (false) |

A rule with no filters, or with a null provider, **fails validation and the whole run is aborted**
before any asset is touched.

### How Rules Are Applied

1. **Setup Phase**: All filters and providers initialize (`Setup()`)
2. **Priority Sorting**: Rules are sorted, higher priority first
3. **Matching Phase**: Each asset is tested against a rule's filters (AND logic); the first matching rule wins for that asset
4. **Generation Phase**: The rule's provider generates the address / labels / version
5. **Application Phase**: Output is applied to the addressable entry

---

## Quick Start

### 1. Create Your First Rule Data

```
1. Right-click in Project window
2. Create > Addressable Manager > Layout Rule Data
3. Name it "MainRules"
```

### 2. Open the Rule Editor

```
Window > Addressable Manager > Layout Rule Editor
Assign your MainRules asset to the "Rule Data" field in the toolbar
```

### 3. Create a Simple Address Rule

**Goal**: Address every PNG under `Assets/UI/` by its filename

**Steps**:
```
1. Click the "Address" toolbar toggle
2. Click "+ Add Rule"
3. Configure in the detail pane:
   - Rule Name: "UI Sprites"
   - Enabled: ✓
   - Target Group: "UI"
   - Priority: 100

4. Create a PathFilter asset:
   - Create > Addressable Manager > Filters > Path Filter
   - Match Mode: Glob          <-- REQUIRED for the pattern below
   - Pattern: "Assets/UI/**/*.png"

5. Create a FileNameAddressProvider asset:
   - Create > Addressable Manager > Providers > Address > File Name

6. Assign the filter (+ Add Filter) and the provider to the rule
7. Click "Apply All" in the toolbar
```

> **The single most common mistake.** `PathFilter` defaults to **Match Mode = Contains**, and in
> `Contains` / `StartsWith` / `EndsWith` / `Exact` the pattern is compared as a plain string. A pattern
> containing `*` or `**` therefore matches **nothing at all**, and the run still reports success with
> zero assets processed. Any pattern with a wildcard needs **Match Mode = Glob**. The filter logs one
> console warning when it sees a wildcard pattern in a literal mode — watch for it.

**Result**: All PNG files under the UI folder are addressable by their filename.

### 4. Verify with Preview

```
1. Select the rule in the list pane
2. In the right-hand Preview pane, set "Preview Limit" (10-200, default 50)
3. Click "Refresh Preview"
4. See matched assets and generated addresses
```

---

## Rule Types

### Address Rules

**Purpose**: Determine addressable identity and group membership

**Key Settings**:
- `Target Group Name`: Which addressable group to add assets to. Leave empty for the default group. `/` and `\` are not valid in a group name.
- `Group Template`: Optional `AddressableAssetGroupTemplate` describing how the group is configured when the rule has to create it.
- `Skip Existing` (default false): Skip assets that already have an address. Note this also stops the rule from moving them.
- `Allow Group Move` (default true): Let this rule move an already-addressable asset out of its current group.

**Common Patterns**:

#### Pattern 1: By Filename
```
Use Case: Simple UI elements, audio clips
Filter:   PathFilter, Match Mode = Glob, Pattern "Assets/Audio/**/*.wav"
Provider: FileNameAddressProvider
Result:   "explosion", "music_theme"      (extension excluded by default)
```

#### Pattern 2: By Path
```
Use Case: Prefabs, scenes with unique paths
Filter:   TypeFilter, Type Name "GameObject"
Provider: PathAddressProvider
Result:   "Prefabs/Characters/Player", "Prefabs/Enemies/Zombie"
```

### Label Rules

**Purpose**: Add metadata for conditional loading

**Key Settings**:
- `Append to Existing` (default **true**): keep labels the rule did not ask for. When set to false, labels this run did not ask for are stripped from the entry first — except `version:` labels, which are always left alone.
- `Priority`: Order of label application.

A label rule's only source of labels is its provider; there is no inline label list on the rule itself.

**Common Patterns**:

#### Pattern 1: Platform Labels
```
Rule:     "Mobile Optimized Assets"
Filter:   PathFilter, Match Mode = Glob, Pattern "Assets/Mobile/**"
Provider: ConstantLabelProvider, Labels = ["mobile", "lowres"]
Result:   Assets tagged for mobile-only loading
```

#### Pattern 2: Content Type Labels
```
Rule:     "DLC Content"
Filter:   PathFilter, Match Mode = Glob, Pattern "Assets/DLC/**"
Provider: FolderLabelProvider (Use Parent Folder)
Result:   Assets tagged with their immediate folder name, e.g. "Expansion1"
```

#### Pattern 3: Quality Tiers
```
Rule:     "High Quality Textures"
Filter:   PathFilter, Match Mode = Glob, Pattern "Assets/Textures/4K/**"
Provider: ConstantLabelProvider, Labels = ["quality_high", "texture"]
Result:   Enables quality-based loading
```

### Version Rules

**Purpose**: Track asset versions across builds

**Key Settings**:
- `Skip Existing` (default false): Preserve an existing `version:` label
- `Provider`: Source of version information

**Common Patterns**:

#### Pattern 1: Build Number Versioning
```
Rule:     "All Assets - Build Version"
Filter:   PathFilter, Match Mode = StartsWith, Pattern "Assets/"
Provider: BuildNumberVersionProvider (Version Source = Combined)
Result:   Assets carry the label "version:1.0.0.123"
```

> A rule must have at least one filter — there is no "matches everything" rule. Use a broad
> `PathFilter` (`StartsWith` on `"Assets/"`, which is also its default pattern) when you mean "all".

#### Pattern 2: Git Commit Tracking
```
Rule:     "Track by Git Commit"
Filter:   PathFilter, Match Mode = Glob, Pattern "Assets/Content/**"
Provider: GitCommitVersionProvider (Mode = CommitHash)
Result:   Assets carry the label "version:a1b2c3d"
```

#### Pattern 3: Date-Based Versions
```
Rule:     "Daily Build Stamp"
Filter:   PathFilter, Match Mode = StartsWith, Pattern "Assets/"
Provider: DateVersionProvider (Date Format = YYYYMMDD)
Result:   Assets carry the label "version:20250104"
```

### Rule-Data-Level Version Filtering

`LayoutRuleData` itself carries two fields that gate an entire run:

- `Version Expression` (default empty): only assets whose **existing** `version:` label satisfies the expression are touched by any rule in this run. Valid formats: `[1.0.0,2.0.0)`, `(1.0.0,2.0.0]`, `[1.0.0,2.0.0]`, `1.0.0`, `>=1.0.0`, `>1.0.0`, `<=2.0.0`, `<2.0.0`.
- `Exclude Unversioned` (default false): when true, assets with no `version:` label (or a malformed one) are excluded rather than included.

Other rule-data-level fields: `Auto Apply On Import` (default **false**), `Auto Apply On Modified`
(false), `Preserve Rule Order` (false), `Verbose Logging` (false).

### Applying Rules Outside the Editor Window

- `Tools > Addressable Manager > Force Process All Assets` — scans `Assets` and applies **every** `LayoutRuleData` in the project.
- `Assets > Addressables > Apply Layout Rules` — right-click in the Project window; if more than one `LayoutRuleData` exists you get a picker.
- Automatic on import: an `AssetPostprocessor` batches imports and moves, then applies only those `LayoutRuleData` assets whose **`Auto Apply On Import` is ticked**. It is off by default, so nothing happens on import until you turn it on.

---

## Filters

Filters define which assets a rule should match. All filters in a rule must pass (AND logic).

Every filter shares three fields from its base class:

- `Enabled` (default true) — **a disabled filter returns true for everything**, i.e. it stops filtering rather than blocking everything.
- `Invert` (default false) — negate the result.
- `Description` — optional label shown in the editor.

There are exactly **8** filter types.

### 1. PathFilter
**Matches**: Assets based on their asset path

```
PathFilter
├─ Pattern:        "Assets/"    (default)
├─ Match Mode:     Contains     (default)
└─ Case Sensitive: false        (default)
```

**Match modes**: `Contains`, `StartsWith`, `EndsWith`, `Exact`, `Regex`, `Glob`.

`Contains` / `StartsWith` / `EndsWith` / `Exact` are plain string comparisons — **no wildcards**.
`Regex` compiles the pattern as a .NET regular expression as-is (typing `**` there throws, which is
caught and logged as `[PathFilter] Invalid Regex pattern ...`, after which the filter matches
nothing). `Glob` is the mode that understands wildcards.

**Glob syntax** (Match Mode = Glob only). The pattern is anchored to the whole asset path:
- `*` — any characters **within one path segment** (never crosses `/`)
- `?` — exactly one such character
- `**/` — zero or more whole path segments, including none, so `Assets/UI/**/*.png` matches both `Assets/UI/icon.png` and `Assets/UI/Nested/icon.png`
- trailing `**` — the rest of the path, slashes included (e.g. `Assets/Mobile/**`)

Character classes (`[abc]`) and brace alternation (`{wav,mp3}`) are **not** supported — every other
character is escaped and matched literally. To match two extensions, use two rules or an
`ExtensionFilter`.

**Examples** (all with Match Mode = Glob):
```
"Assets/Characters/**/*.prefab" → all prefabs under Characters, at any depth
"Assets/Scenes/Level*.unity"    → Level1.unity, Level2.unity, ... in that one folder
"Assets/Audio/SFX/*.wav"        → WAV files directly in SFX
```

### 2. TypeFilter
**Matches**: Assets based on Unity type

```
TypeFilter
├─ Type Name:          "GameObject"  (default)
└─ Include Subclasses: true          (default)
```

Use the **short** type name. The filter resolves it by trying, in order, `Type.GetType(name)`, then
`UnityEngine.<name>, UnityEngine`, then `UnityEditor.<name>, UnityEditor`. A fully-qualified name
like `UnityEngine.GameObject` will not resolve, and the filter then matches nothing.

**Common Types**:
- `GameObject` → Prefabs
- `Texture2D` → Textures
- `Sprite` → Sprites
- `AudioClip` → Audio files
- `Material` → Materials
- `SceneAsset` → Scenes (resolves out of `UnityEditor`)

### 3. ExtensionFilter
**Matches**: Assets by file extension

```
ExtensionFilter
├─ Extensions: ".prefab"   (default; comma-separated string, dot optional)
└─ Match Any:  true        (default; OR logic across the listed extensions)
```

**Use Case**: Match multiple asset types that don't share a Unity type, e.g. `".png,.jpg,.jpeg"`

### 4. AddressFilter
**Matches**: Assets by the address they already have

```
AddressFilter
├─ Pattern:        ""           (default)
├─ Match Mode:     HasAddress   (default)
└─ Case Sensitive: false        (default)
```

**Match modes**: `HasAddress`, `NoAddress`, `Contains`, `StartsWith`, `EndsWith`, `Exact`.
Wildcards are not supported by the literal modes above; use **Regex** mode for patterns — `ui_*` would be matched literally. Use `StartsWith` with `ui_`.

**Use Case**: Apply labels to previously addressed assets

### 5. AddressableGroupFilter
**Matches**: Assets in specific addressable groups

```
AddressableGroupFilter
├─ Group Names:       "Default Local Group"  (default; comma-separated for multiple)
├─ Match Any:         true                   (default; OR logic)
└─ Include Ungrouped: false                  (default)
```

**Use Case**: Apply labels/versions to assets already in a group

### 6. ObjectFilter
**Matches**: Specific asset objects

```
ObjectFilter
└─ Target Objects: []   (list of object references, empty by default)
```

**Use Case**: Manually curate small sets of special assets

### 7. FindAssetsFilter
**Matches**: Using Unity's AssetDatabase search

```
FindAssetsFilter
├─ Search Filter:  "t:Prefab"   (default)
└─ Search Folders: []           (empty = all folders)
```

**Search Syntax** (Unity's own):
- `t:Type` → Type filter
- `l:Label` → Label filter
- `name` → Name search

### 8. DependentObjectFilter
**Matches**: Assets that depend on specified objects

```
DependentObjectFilter
├─ Target Objects:  []       (list of object references)
├─ Dependency Mode: Direct   (default; Direct | Recursive)
└─ Match Any:       true     (default; OR logic across targets)
```

**Use Case**: Group assets with their dependencies

---

## Providers

Providers generate the actual output (address, labels, version) for matched assets. Each provider also
has an optional `Description` field used for its display name in the editor.

There are exactly **8** provider types: 2 address, 2 label, 4 version.

### Address Providers

#### FileNameAddressProvider
**Generates**: The file name

```
Assets/UI/button_start.png → "button_start"
```

**Settings**:
- `Include Extension`: false (default)
- `To Lower Case`: false (default)
- `Prefix` / `Suffix`: "" (default)

**Best For**: Simple assets with unique filenames

#### PathAddressProvider
**Generates**: The asset path, trimmed

```
Assets/Prefabs/Characters/Player.prefab → "Prefabs/Characters/Player"
```

**Settings**:
- `Remove Assets Prefix`: true (default) — strips the leading `Assets/`
- `Remove Extension`: true (default)
- `Remove Root Folder`: "" (default) — a further prefix to strip
- `Path Separator Replacement`: "/" (default) — set to e.g. `_` to flatten
- `To Lower Case`: false (default)
- `Prefix` / `Suffix`: "" (default)

> **Order matters.** `Remove Assets Prefix` runs **before** `Remove Root Folder`. With the default
> (`Remove Assets Prefix` = true), the value you type into `Remove Root Folder` must therefore **not**
> include `Assets/`: to reduce `Assets/Textures/HighRes/char.png` to `char`, set `Remove Root Folder`
> to `Textures/HighRes/`, not `Assets/Textures/HighRes/`.

**Best For**: Assets needing hierarchical addresses

### Label Providers

#### ConstantLabelProvider
**Generates**: A fixed set of labels for every matched asset

```
Labels: ["platform_pc", "quality_high"]
```

**Settings**:
- `Labels`: a `List<string>`, empty by default

Empty and whitespace entries are dropped, and duplicates are collapsed, rather than written through.

**Best For**: Static classification. This is the provider nearly every worked example needs, because a
label rule has no inline label list — a provider is its only label source.

#### FolderLabelProvider
**Generates**: Label(s) from the folder structure

```
Assets/Content/Levels/Forest/tree.prefab → "Forest"
```

**Settings**:
- `Use Parent Folder`: true (default) — emit only the immediate parent folder name
- `Use All Folders`: false (default) — when true, emit **every** folder in the path as its own label (this takes precedence over `Use Parent Folder`)
- `Skip Assets Folder`: true (default) — only meaningful with `Use All Folders`
- `To Lower Case`: false (default)
- `Prefix`: "" (default)

There is no folder-depth setting: it is either the immediate parent, or all of them.

**Best For**: Content organization by folders

### Version Providers

#### ConstantVersionProvider
**Generates**: Fixed version string

```
Version: "1.0.0"   (default)
```

**Best For**: Manual version control

#### BuildNumberVersionProvider
**Generates**: Version from Unity player settings

```
Version Source: BundleVersion → "1.2.3"
Version Source: BuildNumber   → "456"
Version Source: Combined      → "1.2.3.456"
Version Source: Custom        → the Custom Version field
```

**Settings**:
- `Version Source`: `BundleVersion` (default) | `BuildNumber` | `Combined` | `Custom`
- `Custom Version`: "1.0.0" (used when source is `Custom`)
- `Platform`: a `RuntimePlatform` value, default `Android` — selects which build number is read
- `Prefix` / `Suffix`: ""

**Best For**: Official releases

#### GitCommitVersionProvider
**Generates**: Version from the Git repository

```
Mode: CommitHash     → "a1b2c3d"
Mode: CommitHashFull → full 40-character hash
Mode: LatestTag      → "v1.0.0"
Mode: TagOrHash      → tag if one exists, otherwise the hash   (default)
Mode: Describe       → "v1.0.0-5-ga1b2c3d"
```

**Settings**:
- `Mode`: `CommitHash` | `CommitHashFull` | `LatestTag` | `TagOrHash` (default) | `Describe`
- `Prefix` / `Suffix`: ""
- `Fallback Version`: `"0.0.0-unknown"` — used when Git is unavailable

There is no hash-length setting; `CommitHash` is the short hash, `CommitHashFull` the long one. The
version is resolved once per setup and reused for every asset in the run.

**Best For**: Development builds, continuous deployment

#### DateVersionProvider
**Generates**: Version from a timestamp

```
Date Format: YYYYMMDD       → "20250104"        (default)
Date Format: YYYYMMDDHHMMSS → "20250104150530"
Date Format: YYYYdotMMdotDD → "2025.01.04"
Date Format: ISO8601        → "2025-01-04T15:05:30Z"
Date Format: UnixTimestamp  → "1704380730"
Date Format: Custom         → uses the Custom Format string
```

**Settings**:
- `Date Format`: one of the six above
- `Custom Format`: `"yyyyMMdd-HHmmss"` (default), a C# date format string
- `Use UTC`: true (default)
- `Prefix` / `Suffix`: ""
- `Cache Version`: true (default) — resolve once per setup so every asset in the run gets the same stamp

**Best For**: Daily builds, time-based content

---

## Advanced Workflows

### Multi-Platform Asset Management

**Goal**: Separate high-quality assets for PC and optimized assets for mobile

**Setup**:

1. **Folder Structure**:
```
Assets/
├─ Content/
│  ├─ HighRes/  (PC, Console)
│  └─ LowRes/   (Mobile)
```

2. **Create Rules**:

```
# Address Rule 1: High Quality
Name:         "PC/Console Assets"
Filter:       PathFilter (Glob) "Assets/Content/HighRes/**"
Provider:     PathAddressProvider (Remove Root Folder: "Content/HighRes/")
Target Group: "Content_HighQuality"
Priority:     100

# Address Rule 2: Mobile
Name:         "Mobile Assets"
Filter:       PathFilter (Glob) "Assets/Content/LowRes/**"
Provider:     PathAddressProvider (Remove Root Folder: "Content/LowRes/")
Target Group: "Content_Mobile"
Priority:     100

# Label Rule 1: Platform Tags
Name:     "PC Platform Tag"
Filter:   AddressableGroupFilter (Group Names: "Content_HighQuality")
Provider: ConstantLabelProvider (Labels: ["platform_pc"])
Priority: 90

# Label Rule 2: Mobile Platform Tag
Name:     "Mobile Platform Tag"
Filter:   AddressableGroupFilter (Group Names: "Content_Mobile")
Provider: ConstantLabelProvider (Labels: ["platform_mobile"])
Priority: 90
```

3. **Runtime Loading**:

The runtime API loads either **by address** or **by label** — there is no call that takes an address
and a label set together, and there is no runtime version filtering. Pick the address for the current
platform, or load the whole label:

```csharp
using System.Collections.Generic;
using AddressableManager.API;
using AddressableManager.Core;
using UnityEngine;

// By address, chosen per platform
string address = Application.isMobilePlatform ? "LowRes/hero" : "HighRes/hero";
Texture2D hero = await Simple.Load<Texture2D>(address);

// Or everything carrying the platform label
string platformLabel = Application.isMobilePlatform ? "platform_mobile" : "platform_pc";
List<IAssetHandle<Texture2D>> handles = await Standard.LoadByLabel<Texture2D>(platformLabel);
foreach (var handle in handles)
{
    Texture2D tex = handle.Asset;
    // ... use tex ...
}
// Dispose each handle when you are done with it
foreach (var handle in handles) handle.Dispose();
```

> `Simple.Load<T>` returns the raw asset and disposes the handle for you, so the caller has no way to
> say "I am still using this". That is fine for assets that live as long as the process, but anything
> that must survive a CDN catalog update should use `Standard` and hold the handle.

### DLC and Content Update System

**Goal**: Manage base game vs downloadable content with version tracking

**Setup**:

1. **Organize Content**:
```
Assets/
├─ BaseGame/
│  ├─ Characters/
│  └─ Levels/
└─ DLC/
   ├─ Expansion1/
   └─ Expansion2/
```

2. **Create Rules**. Addresses, labels and versions are three **separate** rules over the same filter:

```
# Base Game - address
Address Rule: "Base Game Content"
  Filter:       PathFilter (Glob) "Assets/BaseGame/**"
  Provider:     PathAddressProvider
  Target Group: "BaseGame"

# Base Game - labels
Label Rule: "Base Game Labels"
  Filter:   PathFilter (Glob) "Assets/BaseGame/**"
  Provider: ConstantLabelProvider (Labels: ["content_base", "static"])

# Base Game - version
Version Rule: "Base Game Version"
  Filter:   PathFilter (Glob) "Assets/BaseGame/**"
  Provider: ConstantVersionProvider (Version: "1.0.0")

# DLC 1 - address
Address Rule: "DLC Expansion 1"
  Filter:       PathFilter (Glob) "Assets/DLC/Expansion1/**"
  Provider:     PathAddressProvider
  Target Group: "DLC_Expansion1"

# DLC 1 - labels
Label Rule: "DLC Expansion 1 Labels"
  Filter:   PathFilter (Glob) "Assets/DLC/Expansion1/**"
  Provider: ConstantLabelProvider (Labels: ["content_dlc", "expansion1"])

# DLC 1 - version
Version Rule: "DLC Expansion 1 Version"
  Filter:   PathFilter (Glob) "Assets/DLC/Expansion1/**"
  Provider: BuildNumberVersionProvider (Version Source: Combined)
```

On the `LayoutRuleData` asset itself you can restrict the whole run to assets that already sit inside
a version range:

```
Version Expression:  "[1.1.0,2.0.0)"
Exclude Unversioned: true
```

3. **Loading DLC content at runtime**:
```csharp
using AddressableManager.API;
using UnityEngine;

if (PlayerPrefs.GetInt("OwnsDLC1") == 1)
{
    var dlcAssets = await Standard.LoadByLabel<GameObject>("expansion1");
    // ... use dlcAssets ...
    foreach (var handle in dlcAssets) handle.Dispose();
}
```

### Localization System

**Goal**: Manage multi-language assets with proper labeling

**Setup**:

1. **Folder Structure**:
```
Assets/
└─ Localization/
   ├─ EN/  (English)
   ├─ ES/  (Spanish)
   ├─ JP/  (Japanese)
   └─ FR/  (French)
```

2. **Language Rules** — one address rule and one label rule per language:

```
# English
Address Rule: "English Assets"
  Filter:       PathFilter (Glob) "Assets/Localization/EN/**"
  Provider:     PathAddressProvider (Prefix: "en/", Remove Root Folder: "Localization/EN/")
  Target Group: "Localization"
Label Rule: "English Language"
  Filter:   PathFilter (Glob) "Assets/Localization/EN/**"
  Provider: ConstantLabelProvider (Labels: ["lang_en"])

# Spanish
Address Rule: "Spanish Assets"
  Filter:       PathFilter (Glob) "Assets/Localization/ES/**"
  Provider:     PathAddressProvider (Prefix: "es/", Remove Root Folder: "Localization/ES/")
  Target Group: "Localization"
Label Rule: "Spanish Language"
  Filter:   PathFilter (Glob) "Assets/Localization/ES/**"
  Provider: ConstantLabelProvider (Labels: ["lang_es"])

# Repeat for JP, FR...
```

> The `Prefix` matters. Without it, each language rule strips its own language folder and
> `EN/ui_strings.txt` and `ES/ui_strings.txt` both reduce to the address `ui_strings`. Addressables
> permits the duplicate, but a load by that address is then ambiguous, and the Layout Viewer reports
> it as a `DuplicateAddress` conflict.

3. **Language Selection** — load by the language label:
```csharp
using AddressableManager.API;
using UnityEngine;

string language = "lang_en"; // from settings
var strings = await Standard.LoadByLabel<TextAsset>(language);
```

### Quality Preset System

**Goal**: Organize textures by quality tiers

**Setup**:

1. **Asset Organization**:
```
Assets/
└─ Textures/
   ├─ 4K/     (Ultra)
   ├─ 2K/     (High)
   ├─ 1K/     (Medium)
   └─ 512/    (Low)
```

2. **Quality Rules** — an address rule plus a label rule per tier:

```
# Ultra
Address Rule "4K Textures":  PathFilter (Glob) "Assets/Textures/4K/**"  → Group "Textures_Ultra"
Label Rule   "4K Labels":    same filter, ConstantLabelProvider ["quality_ultra", "texture"]

# High
Address Rule "2K Textures":  PathFilter (Glob) "Assets/Textures/2K/**"  → Group "Textures_High"
Label Rule   "2K Labels":    same filter, ConstantLabelProvider ["quality_high", "texture"]

# Medium
Address Rule "1K Textures":  PathFilter (Glob) "Assets/Textures/1K/**"  → Group "Textures_Medium"
Label Rule   "1K Labels":    same filter, ConstantLabelProvider ["quality_medium", "texture"]

# Low
Address Rule "512 Textures": PathFilter (Glob) "Assets/Textures/512/**" → Group "Textures_Low"
Label Rule   "512 Labels":   same filter, ConstantLabelProvider ["quality_low", "texture"]
```

3. **Quality-Based Loading**:
```csharp
using AddressableManager.API;
using UnityEngine;

string quality = QualitySettings.GetQualityLevel() >= 3 ? "quality_ultra" : "quality_low";
var textures = await Standard.LoadByLabel<Texture2D>(quality);
```

---

## CI/CD Integration

### Command Line Interface

The package provides `-executeMethod` entry points for automation pipelines. Every one of them first
checks `EditorUtility.scriptCompilationFailed` and exits `1` if the project does not compile.

Argument form is `-key value`. A `-key` followed by another `-flag`, or by the end of the arguments,
is read as `true`. Booleans accept `true`/`false` (case-insensitive) or `1`.

#### Apply Rules

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -validateOnly false \
  -warningAsError true \
  -resultFilePath "build_logs/addressable_report.json"
```

**Parameters**:
- `-layoutRuleAssetPath`: Path to the LayoutRuleData asset (**required**)
- `-validateOnly`: Only validate, don't apply (default: false)
- `-warningAsError`: Treat validation and processor warnings as errors (default: false)
- `-resultFilePath`: JSON report output path (default: none written)

**Exit Codes**:
- `0` = Success
- `1` = Validation errors, or apply failed
- `2` = Missing `-layoutRuleAssetPath`, asset not found, or an exception

**Result JSON shape** (written when `-resultFilePath` is given):
```json
{
  "success": true,
  "totalAssetsProcessed": 0,
  "addressesApplied": 0,
  "labelsApplied": 0,
  "versionsApplied": 0,
  "warnings": [],
  "errors": [],
  "timestamp": ""
}
```

#### Validate Rules

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ValidateLayoutRules \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -errorLogFilePath "build_logs/validation_errors.txt"
```

Exit codes: `0` clean, `1` errors found, `2` missing argument / asset not found / exception.

#### Set Version Expression

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.SetVersionExpression \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -versionExpression "[1.0.0,2.0.0)" \
  -excludeUnversioned true
```

Writes the expression onto the `LayoutRuleData` asset. Exit codes: `0` ok, `1` invalid expression,
`2` missing argument / asset not found / exception.

Valid expression formats: `[1.0.0,2.0.0)`, `(1.0.0,2.0.0]`, `[1.0.0,2.0.0]`, `1.0.0`, `>=1.0.0`,
`>1.0.0`, `<=2.0.0`, `<2.0.0`.

#### Detect Conflicts

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.DetectConflicts \
  -reportFilePath "build_logs/conflicts.json"
```

`-reportFilePath` defaults to `conflicts.json`. Exit codes: `0` no conflicts, `1` conflicts found,
`2` exception.

**Conflict JSON shape**:
```json
{
  "timestamp": "",
  "totalConflicts": 0,
  "conflicts": [
    { "type": "", "message": "", "affectedAssets": [], "suggestion": "" }
  ]
}
```

Conflict types: `DuplicateAddress`, `InvalidAddressCharacters`, `EmptyAddress`, `CircularDependency`,
`MissingReference`, `GroupConflict`, `SettingsNotInitialized`.

#### Import Rules

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ImportRules \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -importFilePath "CI/rules/production.json" \
  -mergeMode true
```

`-layoutRuleAssetPath` and `-importFilePath` are both required. `-mergeMode` (default false) adds the
imported rules to the existing ones instead of replacing them. Exit codes: `0` ok, `1` import failed,
`2` missing argument / file or asset not found / exception.

#### Building CDN Content

Content building has its own CLI in the same package:

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent \
  -cdnProfile Local
```

`-cdnProfile` accepts `Local` (default), `Dev`, `Staging`, `Prod`. Exit codes: `0` ok, `1` refused or
failed, `2` exception. Companion entry points: `CdnBuildCLI.BuildContentUpdate` (adds
`-contentStatePath`) and `CdnBuildCLI.VerifyOutput` (adds `-manifestPath`).

### GitHub Actions Example

```yaml
name: Build Addressables

on:
  push:
    branches: [ main, develop ]

jobs:
  build:
    runs-on: ubuntu-latest

    steps:
    - uses: actions/checkout@v4

    - name: Setup Unity
      uses: game-ci/unity-builder@v4
      with:
        unityVersion: 2023.1.0f1

    - name: Validate Addressable Rules
      run: |
        unity-editor \
          -quit -batchmode \
          -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ValidateLayoutRules \
          -layoutRuleAssetPath "Assets/Rules/CI_Rules.asset" \
          -errorLogFilePath "validation.log"

    - name: Apply Addressable Rules
      run: |
        unity-editor \
          -quit -batchmode \
          -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
          -layoutRuleAssetPath "Assets/Rules/CI_Rules.asset" \
          -warningAsError true \
          -resultFilePath "result.json"

    - name: Build Content
      run: |
        unity-editor \
          -quit -batchmode \
          -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent \
          -cdnProfile Staging

    - name: Upload Artifacts
      uses: actions/upload-artifact@v4
      with:
        name: addressables
        path: ServerData/**
```

### Jenkins Pipeline Example

```groovy
pipeline {
    agent any

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Validate Rules') {
            steps {
                script {
                    def exitCode = bat(
                        returnStatus: true,
                        script: """
                        Unity.exe -quit -batchmode ^
                          -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ValidateLayoutRules ^
                          -layoutRuleAssetPath "Assets/Rules/Production.asset" ^
                          -errorLogFilePath "validation.log"
                        """
                    )

                    if (exitCode != 0) {
                        error("Rule validation failed")
                    }
                }
            }
        }

        stage('Apply Rules') {
            steps {
                bat """
                Unity.exe -quit -batchmode ^
                  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules ^
                  -layoutRuleAssetPath "Assets/Rules/Production.asset" ^
                  -resultFilePath "result.json"
                """
            }
        }

        stage('Build Content') {
            steps {
                bat """
                Unity.exe -quit -batchmode ^
                  -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent ^
                  -cdnProfile Prod
                """
            }
        }
    }

    post {
        always {
            archiveArtifacts artifacts: '**/*.log, **/*.json', allowEmptyArchive: true
        }
    }
}
```

---

## Best Practices

### Rule Organization

**1. Use Descriptive Names**
```
❌ Bad: "Rule 1", "Test", "Temp"
✅ Good: "UI Sprites by Filename", "Platform-Specific Materials"
```
Rule names are also checked by the validator: duplicate names within the same rule list raise a
warning.

**2. Document Your Rules**
```
Always fill in the Description field with:
- What assets it matches
- Why this configuration is needed
- Any special considerations
```

**3. Prioritize Correctly**
```
Priority defaults to 0. Higher values are processed first.

Higher priority (200+): Specific overrides
Medium priority (100):  General rules
Lower priority (50-):   Fallback/default rules
```
The validator warns when several rules share the same priority, because the order between them is
then not deterministic.

**4. Group Related Rules**
```
Create separate LayoutRuleData assets for:
- Different content types (UI, Audio, Models)
- Different platforms
- Base game vs DLC
```
A `CompositeLayoutRuleData` asset (Create > Addressable Manager > Composite Layout Rule Data) merges
several `LayoutRuleData` sources; it defaults to respecting source order and de-duplicating rules by
name.

### Performance Optimization

**1. Efficient Filter Chains**
```
✅ Fast: PathFilter → ExtensionFilter → TypeFilter
❌ Slow: DependentObjectFilter, FindAssetsFilter

Order filters from most restrictive to least restrictive
```

**2. Limit Preview Scope**
```
The Preview pane has a "Preview Limit" slider (range 10-200, default 50)
Don't preview rules that match thousands of assets
```

**3. Batch Apply**
```
Apply all rules at once rather than one-by-one
Use the CLI for bulk operations in builds
```

### Maintenance

**1. Version Control Rules**
```
✅ Commit LayoutRuleData assets and Filter/Provider assets
✅ Use descriptive commit messages
✅ Review rule changes in pull requests
```

**2. Test Before Applying**
```
1. Use the Preview pane to verify matches
2. Use "Validate" before "Apply All"
3. Test in a separate branch first
4. Keep backups of addressable settings
```

**3. Regular Audits**
```
Periodically:
- Run conflict detection (Window > Addressable Manager > Layout Viewer, or the DetectConflicts CLI)
- Review unused rules
- Update documentation
```

### Team Collaboration

**1. Share Templates**
```
Export working rule sets as JSON with the Rule Editor's "Export" button
Store them in a shared repository; re-apply with "Import" or the ImportRules CLI
```
Exported JSON carries each filter/provider's type name, its asset path, and its own serialized JSON.
On import the asset is resolved by path first; if that fails, a fresh instance is constructed from the
type name and stored as a sub-asset of the rule data.

**2. Naming Conventions**
```
Establish team standards for:
- Rule names
- Address formats
- Label naming
- Group organization
```

**3. Code Reviews**
```
Include rule changes in PR reviews
Verify preview results
Test applied rules locally
Document breaking changes
```

---

## Troubleshooting

### Common Issues

#### Issue: Rule Not Matching Assets

**Symptoms**: Preview shows 0 matches

**Solutions**:
1. **Check the `PathFilter` Match Mode first.** It defaults to `Contains`, which treats `*` and `**` as literal characters. A wildcard pattern needs **Match Mode = Glob**. The filter logs a one-time console warning about this.
2. Remember glob patterns are anchored to the whole asset path, and `*` never crosses `/`. Use `**/` for "at any depth".
3. For `TypeFilter`, use the **short** type name (`GameObject`, not `UnityEngine.GameObject`) — a name that fails to resolve makes the filter match nothing.
4. Check file extensions match the `ExtensionFilter` (comma-separated, dot optional).
5. Remember all filters on a rule are ANDed — one over-narrow filter kills the rule. Check `Invert` is not set by accident.
6. A **disabled** filter does not exclude anything; it passes everything. If you disabled a filter expecting it to block assets, it is doing the opposite.
7. If the rule data has a `Version Expression` set, assets outside that range are skipped for every rule in the run — and with `Exclude Unversioned` ticked, assets with no `version:` label are skipped too.

#### Issue: Wrong Address Generated

**Symptoms**: Assets have unexpected addresses

**Solutions**:
1. Check the provider configuration.
2. For `PathAddressProvider`, remember `Remove Assets Prefix` runs first — so `Remove Root Folder` must be written **without** the leading `Assets/`.
3. Check for a conflicting rule with higher priority; the first matching rule wins for an asset.
4. Disable other rules temporarily to isolate the issue.
5. Use the Preview pane to see what a single rule produces before applying.

#### Issue: Labels Not Applying

**Symptoms**: Assets missing expected labels

**Solutions**:
1. Verify the asset is addressable first — labels only apply to addressable entries, so an address rule normally has to run before the label rule.
2. Check the label rule's priority order.
3. Check the provider is generating a non-empty list. `ConstantLabelProvider` drops empty and whitespace entries, so a list of blank strings produces nothing.
4. If existing labels are disappearing, `Append to Existing` has been turned off — it defaults to true.
5. Verify the rule is enabled.

#### Issue: Version Tags Missing

**Symptoms**: No `version:X.X.X` labels on assets

**Solutions**:
1. Check the version provider is assigned and configured.
2. For `GitCommitVersionProvider`, ensure the `.git` folder is reachable — otherwise you get the `Fallback Version` (`0.0.0-unknown`) instead.
3. For `BuildNumberVersionProvider`, check the PlayerSettings values and that `Platform` matches the platform whose build number you expect.
4. Verify the version rule is enabled and that `Skip Existing` is not preserving an old tag.
5. Check the rule data's `Version Expression` isn't excluding the assets.

### Validation Errors

Validation runs before anything is applied, and any error aborts the entire run.

#### Error: "Rule '&lt;name&gt;': Filter at index N is null"

**Cause**: A filter slot in the rule is empty

**Fix**: Assign a filter asset, or remove the empty slot with the `-` button

#### Error: "Rule '&lt;name&gt;': Must have at least one filter"

**Cause**: The rule has an empty filter list

**Fix**: Add at least one filter. There is no "match everything" rule — use a broad `PathFilter`.

#### Error: "Rule '&lt;name&gt;': Address provider is not assigned" (likewise "Label provider" / "Version provider")

**Cause**: No provider assigned to the rule

**Fix**: Create and assign the appropriate provider asset

#### Error: "Rule name cannot be empty"

**Cause**: The Rule Name field was cleared

**Fix**: Give the rule a name

#### Error: "Invalid version expression"

**Cause**: Malformed version expression syntax

**Fix**: Use a valid format: `[1.0.0,2.0.0)`, `(1.0.0,2.0.0]`, `[1.0.0,2.0.0]`, `1.0.0`, `>=1.0.0`, `>1.0.0`, `<=2.0.0`, `<2.0.0`

### Conflicts

Conflicts are reported separately from validation, by the Layout Viewer window and by the
`DetectConflicts` CLI. `CircularDependency` (an asset that depends on itself) is one of them, along
with `DuplicateAddress`, `InvalidAddressCharacters`, `EmptyAddress`, `MissingReference`,
`GroupConflict` and `SettingsNotInitialized`.

### Performance Issues

#### Issue: Slow Rule Application

**Symptoms**: ApplyRules takes many minutes

**Solutions**:
1. Reduce the number of active rules
2. Use more specific filters to reduce matching overhead
3. Avoid `DependentObjectFilter` on large asset sets
4. Turn off `Verbose Logging` on the rule data
5. Consider splitting rules across multiple LayoutRuleData assets

#### Issue: Editor Lag in Rule Editor

**Symptoms**: UI becomes unresponsive

**Solutions**:
1. Reduce the Preview Limit slider toward its minimum (10)
2. Don't keep refreshing the preview continuously
3. Close other heavy editor windows
4. Split complex rule sets into smaller ones

---

## Summary

The Addressable Automation System provides:

✅ **Rule-Based Workflow**: Define once, apply everywhere
✅ **8 Filter Types**: Match assets precisely
✅ **8 Providers**: 2 address, 2 label, 4 version
✅ **Visual Tools**: Layout Rule Editor (with preview), Layout Viewer (with conflict detection)
✅ **CI/CD Integration**: Five `AddressableCLI` entry points plus the CDN build CLI
✅ **Team Collaboration**: JSON import/export and version control
✅ **Version Management**: `version:` labels and version-expression filtering

**Next Steps**:
1. Read [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md) for practical examples
2. Browse the [Templates directory](../Packages/com.game.addressables/Editor/Templates/) for the five shipped starter rule sets: `BasicAddressRules.json`, `ComprehensiveRules.json`, `MaterialTextureRules.json`, `PlatformSpecificRules.json`, `VersionedAssetsRules.json`
3. Import the **Rule Automation Presets** sample from the Package Manager for a set of rule, filter and provider assets already wired together

**Related Documentation**:
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) - Editor window details
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common problems and solutions

---

**Version**: 4.1.0-pre.9 | **Unity**: 2023.1+
