# Rule System Examples

**Practical examples for common addressable automation scenarios**

This guide provides copy-paste-ready examples for typical use cases. Each example includes complete setup instructions and expected results.

> **Read this first — the one setting that breaks every example.**
> `PathFilter` defaults to **Match Mode = Contains**, which compares the pattern as a plain string.
> Every pattern below that contains `*` or `**` therefore requires **Match Mode = Glob** on the filter
> asset. With the default mode those patterns match zero assets and the run still reports success.

> **Runtime API.** `AddressableManager` is a namespace, not a class. The runtime entry points are the
> static classes `Simple`, `Standard` and `Advanced` in `AddressableManager.API`, and `Assets` in
> `AddressableManager.Facade`. Assets are loaded **either** by address **or** by label — no call takes
> both — and there is no runtime version filtering. Nothing in this package loads a Unity *scene*.

---

## Table of Contents

1. [Example 1: Basic UI Assets](#example-1-basic-ui-assets)
2. [Example 2: Audio Library Organization](#example-2-audio-library-organization)
3. [Example 3: Multi-Platform Textures](#example-3-multi-platform-textures)
4. [Example 4: Character System](#example-4-character-system)
5. [Example 5: DLC Content Management](#example-5-dlc-content-management)
6. [Example 6: Localization System](#example-6-localization-system)
7. [Example 7: Level Scene Assets](#example-7-level-scene-assets)
8. [Example 8: Version-Tracked Assets](#example-8-version-tracked-assets)

---

## Example 1: Basic UI Assets

**Scenario**: You have hundreds of UI sprites that need addressable configuration

**Folder Structure**:
```
Assets/
└─ UI/
   ├─ Buttons/
   │  ├─ btn_start.png
   │  ├─ btn_settings.png
   │  └─ btn_quit.png
   ├─ Icons/
   │  ├─ icon_health.png
   │  └─ icon_mana.png
   └─ Backgrounds/
      └─ bg_main_menu.png
```

**Setup Steps**:

1. **Create Assets**:
```
PathFilter asset: "UIPathFilter"
- Match Mode: Glob            <-- required for the "**" pattern
- Pattern:    "Assets/UI/**/*.png"
- Case Sensitive: false

FileNameAddressProvider: "UIAddressProvider"
- Include Extension: false    (default)
- To Lower Case:     true
```

2. **Create Rule**:
```
LayoutRuleData: "UI_Rules"

Address Rule: "UI Sprites"
- Enabled: ✓
- Priority: 100
- Target Group: "UI"
- Skip Existing: false
- Allow Group Move: true
- Filters: [UIPathFilter]
- Address Provider: UIAddressProvider
```

3. **Apply Rules**:
```
1. Open Window > Addressable Manager > Layout Rule Editor
2. Assign UI_Rules to the "Rule Data" field
3. Click "Apply All"
```

**Expected Results**:
```
btn_start.png     → Address: "btn_start"    Group: UI
btn_settings.png  → Address: "btn_settings" Group: UI
icon_health.png   → Address: "icon_health"  Group: UI
bg_main_menu.png  → Address: "bg_main_menu" Group: UI
```

**Runtime Usage**:
```csharp
using AddressableManager.API;
using UnityEngine;

// Simple tier: returns the asset itself, handle managed for you
Sprite startButton = await Simple.Load<Sprite>("btn_start");
Sprite healthIcon  = await Simple.Load<Sprite>("icon_health");
```

---

## Example 2: Audio Library Organization

**Scenario**: Organize music, SFX, and voice-over audio with proper labeling

**Folder Structure**:
```
Assets/
└─ Audio/
   ├─ Music/
   │  ├─ theme_main.mp3
   │  └─ combat_intense.mp3
   ├─ SFX/
   │  ├─ explosion.wav
   │  └─ footstep.wav
   └─ VO/
      ├─ dialogue_001.wav
      └─ dialogue_002.wav
```

**Setup Steps**:

1. **Create Filter Assets** (all with **Match Mode: Glob**):
```
PathFilter: "MusicFilter"
- Pattern: "Assets/Audio/Music/**"

PathFilter: "SFXFilter"
- Pattern: "Assets/Audio/SFX/**"

PathFilter: "VOFilter"
- Pattern: "Assets/Audio/VO/**"
```

2. **Create Provider Assets**:
```
FileNameAddressProvider: "AudioAddressProvider"

ConstantLabelProvider: "MusicLabel"
- Labels: ["audio_music"]

ConstantLabelProvider: "SFXLabel"
- Labels: ["audio_sfx"]

ConstantLabelProvider: "VOLabel"
- Labels: ["audio_vo"]
```

3. **Create Rules**:
```
LayoutRuleData: "Audio_Rules"

Address Rule 1: "Music Files"
- Priority: 110
- Target Group: "Audio"
- Filters: [MusicFilter]
- Address Provider: AudioAddressProvider

Address Rule 2: "SFX Files"
- Priority: 100
- Target Group: "Audio"
- Filters: [SFXFilter]
- Address Provider: AudioAddressProvider

Address Rule 3: "Voice Over Files"
- Priority: 90
- Target Group: "Audio"
- Filters: [VOFilter]
- Address Provider: AudioAddressProvider

Label Rule 1: "Music Label"
- Priority: 100
- Append To Existing: true       (default)
- Filters: [MusicFilter]
- Label Provider: MusicLabel

Label Rule 2: "SFX Label"
- Priority: 100
- Filters: [SFXFilter]
- Label Provider: SFXLabel

Label Rule 3: "VO Label"
- Priority: 100
- Filters: [VOFilter]
- Label Provider: VOLabel
```

**Expected Results**:
```
theme_main.mp3   → Address: "theme_main"   Labels: ["audio_music"]
explosion.wav    → Address: "explosion"    Labels: ["audio_sfx"]
dialogue_001.wav → Address: "dialogue_001" Labels: ["audio_vo"]
```

**Runtime Usage**:
```csharp
using System.Collections.Generic;
using AddressableManager.API;
using AddressableManager.Core;
using UnityEngine;

// Load every asset carrying a label. Returns handles - you own their lifetime.
List<IAssetHandle<AudioClip>> music = await Standard.LoadByLabel<AudioClip>("audio_music");
foreach (var handle in music)
{
    AudioClip clip = handle.Asset;
    // ... use clip ...
}

// Load one specific SFX by address
AudioClip explosion = await Simple.Load<AudioClip>("explosion");

// Warm the cache for a set of addresses before a scene starts
await Standard.PreloadAsync("dialogue_001", "dialogue_002");

// Release the label batch when the scene ends
foreach (var handle in music) handle.Dispose();
```

> `Standard.LoadByLabel<T>` hands you a `List<IAssetHandle<T>>`. Nothing releases those for you —
> dispose each handle when the batch is no longer needed, or the assets stay resident.

---

## Example 3: Multi-Platform Textures

**Scenario**: Separate high-res and low-res textures for different platforms

**Folder Structure**:
```
Assets/
└─ Textures/
   ├─ HighRes/ (PC/Console)
   │  ├─ char_hero_4k.png
   │  └─ env_forest_2k.png
   └─ LowRes/ (Mobile)
      ├─ char_hero_1k.png
      └─ env_forest_512.png
```

**Setup Steps**:

1. **Create Filters** (**Match Mode: Glob**):
```
PathFilter: "HighResFilter"
- Pattern: "Assets/Textures/HighRes/**"

PathFilter: "LowResFilter"
- Pattern: "Assets/Textures/LowRes/**"
```

2. **Create Providers**:
```
PathAddressProvider: "HighResAddressProvider"
- Remove Assets Prefix: true          (default)
- Remove Root Folder:   "Textures/HighRes/"

PathAddressProvider: "LowResAddressProvider"
- Remove Assets Prefix: true          (default)
- Remove Root Folder:   "Textures/LowRes/"

ConstantLabelProvider: "PCLabel"
- Labels: ["platform_pc", "quality_high"]

ConstantLabelProvider: "MobileLabel"
- Labels: ["platform_mobile", "quality_low"]
```

> `Remove Assets Prefix` is applied **before** `Remove Root Folder`, so the root folder is written
> without the leading `Assets/`. Writing `"Assets/Textures/HighRes"` there silently does nothing.

3. **Create Rules**:
```
LayoutRuleData: "Textures_Rules"

Address Rule 1: "High Res Textures"
- Priority: 100
- Target Group: "Textures_High"
- Filters: [HighResFilter]
- Address Provider: HighResAddressProvider

Address Rule 2: "Low Res Textures"
- Priority: 100
- Target Group: "Textures_Low"
- Filters: [LowResFilter]
- Address Provider: LowResAddressProvider

Label Rule 1: "PC Platform"
- Priority: 90
- Filters: [HighResFilter]
- Label Provider: PCLabel

Label Rule 2: "Mobile Platform"
- Priority: 90
- Filters: [LowResFilter]
- Label Provider: MobileLabel
```

**Expected Results**:
```
HighRes/char_hero_4k.png:
  Address: "char_hero_4k"
  Group:   "Textures_High"
  Labels:  ["platform_pc", "quality_high"]

LowRes/char_hero_1k.png:
  Address: "char_hero_1k"
  Group:   "Textures_Low"
  Labels:  ["platform_mobile", "quality_low"]
```

**Runtime Usage**:
```csharp
using AddressableManager.API;
using UnityEngine;

// Choose the address for the current platform
string heroAddress = Application.isMobilePlatform ? "char_hero_1k" : "char_hero_4k";
Texture2D heroTexture = await Simple.Load<Texture2D>(heroAddress);

// Or pull every texture for the current platform in one call
string platformLabel = Application.isMobilePlatform ? "platform_mobile" : "platform_pc";
var allTextures = await Standard.LoadByLabel<Texture2D>(platformLabel);
// ... use them, then ...
foreach (var handle in allTextures) handle.Dispose();
```

---

## Example 4: Character System

**Scenario**: Manage character prefabs and their weapon prefabs

**Folder Structure**:
```
Assets/
└─ Characters/
   ├─ Prefabs/
   │  ├─ Warrior.prefab (uses Sword)
   │  ├─ Mage.prefab (uses Staff)
   │  └─ Archer.prefab (uses Bow)
   └─ Weapons/
      ├─ Sword.prefab
      ├─ Staff.prefab
      └─ Bow.prefab
```

**Setup Steps**:

1. **Create Filters**:
```
PathFilter: "CharacterFilter"
- Match Mode: Glob
- Pattern: "Assets/Characters/Prefabs/*.prefab"

PathFilter: "WeaponFilter"
- Match Mode: Glob
- Pattern: "Assets/Characters/Weapons/*.prefab"

TypeFilter: "PrefabFilter"
- Type Name: "GameObject"     (short name - a fully-qualified name will not resolve)
- Include Subclasses: true    (default)
```

2. **Create Providers**:
```
FileNameAddressProvider: "CharacterAddressProvider"
- To Lower Case: true

ConstantLabelProvider: "CharacterLabel"
- Labels: ["character"]

ConstantLabelProvider: "WeaponLabel"
- Labels: ["weapon"]
```

3. **Create Rules**:
```
LayoutRuleData: "Characters_Rules"

Address Rule 1: "Character Prefabs"
- Priority: 100
- Target Group: "Characters"
- Filters: [CharacterFilter, PrefabFilter]     (AND logic)
- Address Provider: CharacterAddressProvider

Address Rule 2: "Weapon Prefabs"
- Priority: 100
- Target Group: "Characters"
- Filters: [WeaponFilter, PrefabFilter]
- Address Provider: CharacterAddressProvider

Label Rule 1: "Character Tag"
- Priority: 90
- Filters: [CharacterFilter]
- Label Provider: CharacterLabel

Label Rule 2: "Weapon Tag"
- Priority: 90
- Filters: [WeaponFilter]
- Label Provider: WeaponLabel
```

**Expected Results**:
```
Warrior.prefab → Address: "warrior" Labels: ["character"]
Mage.prefab    → Address: "mage"    Labels: ["character"]
Sword.prefab   → Address: "sword"   Labels: ["weapon"]
Staff.prefab   → Address: "staff"   Labels: ["weapon"]
```

**Runtime Usage**:
```csharp
using System.Collections.Generic;
using AddressableManager.API;
using AddressableManager.Core;
using UnityEngine;

// Instantiate a character into the global scope
GameObject warrior = await Standard.InstantiateGlobal("warrior");

// Load every weapon for a selection screen
var weapons = await Standard.LoadByLabel<GameObject>("weapon");

// Load a fixed set of addresses in one call
Dictionary<string, IAssetHandle<GameObject>> batch =
    await Standard.LoadBatch<GameObject>("warrior", "sword");
GameObject swordPrefab = batch["sword"].Asset;

// A session groups loads so they can be dropped together
Standard.StartSession();
IAssetHandle<GameObject> mage = await Standard.LoadSession<GameObject>("mage");
// ... later ...
Standard.EndSession();
```

> `Standard.LoadBatch` loads sequentially and simply skips any address that fails to load, so check
> the dictionary contains a key before indexing it in production code.

---

## Example 5: DLC Content Management

**Scenario**: Manage base game and DLC content with version tracking

**Folder Structure**:
```
Assets/
├─ BaseGame/
│  ├─ Levels/
│  └─ Characters/
└─ DLC/
   ├─ Expansion1/
   │  ├─ Levels/
   │  └─ Characters/
   └─ Expansion2/
      └─ Levels/
```

**Setup Steps**:

1. **Create Filters** (**Match Mode: Glob**):
```
PathFilter: "BaseGameFilter"
- Pattern: "Assets/BaseGame/**"

PathFilter: "DLC1Filter"
- Pattern: "Assets/DLC/Expansion1/**"

PathFilter: "DLC2Filter"
- Pattern: "Assets/DLC/Expansion2/**"
```

2. **Create Providers**:
```
PathAddressProvider: "BaseGameAddressProvider"
- Remove Root Folder: "BaseGame/"
- To Lower Case: true

PathAddressProvider: "DLC1AddressProvider"
- Remove Root Folder: "DLC/Expansion1/"
- To Lower Case: true

PathAddressProvider: "DLC2AddressProvider"
- Remove Root Folder: "DLC/Expansion2/"
- To Lower Case: true

ConstantVersionProvider: "BaseGameVersion"
- Version: "1.0.0"

BuildNumberVersionProvider: "DLCVersion"
- Version Source: Combined
- Platform: Android            (default; selects which build number is read)

ConstantLabelProvider: "BaseLabel"
- Labels: ["content_base"]

ConstantLabelProvider: "DLC1Label"
- Labels: ["content_dlc", "expansion1"]

ConstantLabelProvider: "DLC2Label"
- Labels: ["content_dlc", "expansion2"]
```

3. **Create Rules**:
```
LayoutRuleData: "Content_Rules"

# Base Game
Address Rule 1: "Base Game Assets"
- Priority: 110
- Target Group: "BaseGame"
- Filters: [BaseGameFilter]
- Address Provider: BaseGameAddressProvider

Label Rule 1: "Base Content Label"
- Priority: 100
- Filters: [BaseGameFilter]
- Label Provider: BaseLabel

Version Rule 1: "Base Game Version"
- Priority: 100
- Filters: [BaseGameFilter]
- Version Provider: BaseGameVersion

# DLC 1
Address Rule 2: "Expansion 1 Assets"
- Priority: 100
- Target Group: "DLC_Expansion1"
- Filters: [DLC1Filter]
- Address Provider: DLC1AddressProvider

Label Rule 2: "Expansion 1 Label"
- Priority: 90
- Filters: [DLC1Filter]
- Label Provider: DLC1Label

Version Rule 2: "Expansion 1 Version"
- Priority: 90
- Filters: [DLC1Filter]
- Version Provider: DLCVersion

# DLC 2
Address Rule 3: "Expansion 2 Assets"
- Priority: 90
- Target Group: "DLC_Expansion2"
- Filters: [DLC2Filter]
- Address Provider: DLC2AddressProvider

Label Rule 3: "Expansion 2 Label"
- Priority: 80
- Filters: [DLC2Filter]
- Label Provider: DLC2Label

Version Rule 3: "Expansion 2 Version"
- Priority: 80
- Filters: [DLC2Filter]
- Version Provider: DLCVersion
```

**Expected Results**:
```
BaseGame/Levels/level1.unity:
  Address: "levels/level1"
  Group:   "BaseGame"
  Labels:  ["content_base", "version:1.0.0"]

DLC/Expansion1/Levels/level_bonus.unity:
  Address: "levels/level_bonus"
  Group:   "DLC_Expansion1"
  Labels:  ["content_dlc", "expansion1", "version:1.2.3.456"]
```

A version is written as an ordinary label of the form `version:<value>`. Applying a version rule
first removes any older `version:` label on the same entry; label rules never touch `version:` labels.

**Runtime Usage**:
```csharp
using AddressableManager.API;
using UnityEngine;

// Check if DLC is owned
bool hasDLC1 = PlayerPrefs.GetInt("OwnsDLC1") == 1;

if (hasDLC1)
{
    var dlcAssets = await Standard.LoadByLabel<GameObject>("expansion1");
    // ... use them ...
    foreach (var handle in dlcAssets) handle.Dispose();
}

// Base game content
var baseAssets = await Standard.LoadByLabel<GameObject>("content_base");
```

> The `.unity` files above become addressable *entries*, but this package does not load scenes. To
> enter a level scene, hand its address to Unity's own Addressables scene-loading API; the rule system
> only assigns the address, group, labels and version.

---

## Example 6: Localization System

**Scenario**: Multi-language text assets

**Folder Structure**:
```
Assets/
└─ Localization/
   ├─ EN/
   │  ├─ ui_strings.txt
   │  └─ dialogue.txt
   ├─ ES/
   │  ├─ ui_strings.txt
   │  └─ dialogue.txt
   └─ JP/
      ├─ ui_strings.txt
      └─ dialogue.txt
```

**Setup Steps**:

1. **Create Filters** (**Match Mode: Glob**):
```
PathFilter: "LocalizationEN"
- Pattern: "Assets/Localization/EN/**"

PathFilter: "LocalizationES"
- Pattern: "Assets/Localization/ES/**"

PathFilter: "LocalizationJP"
- Pattern: "Assets/Localization/JP/**"
```

2. **Create Providers**. Each language gets its own address provider with a distinct `Prefix`:
```
PathAddressProvider: "LocalizationEN_Address"
- Remove Root Folder: "Localization/EN/"
- Prefix: "en/"

PathAddressProvider: "LocalizationES_Address"
- Remove Root Folder: "Localization/ES/"
- Prefix: "es/"

PathAddressProvider: "LocalizationJP_Address"
- Remove Root Folder: "Localization/JP/"
- Prefix: "jp/"

ConstantLabelProvider: "EnglishLabel"
- Labels: ["lang_en"]

ConstantLabelProvider: "SpanishLabel"
- Labels: ["lang_es"]

ConstantLabelProvider: "JapaneseLabel"
- Labels: ["lang_jp"]
```

> **Why the prefix.** Without it, every language reduces to the same address (`ui_strings`).
> Addressables permits that duplicate, but the address is then ambiguous to load and the Layout Viewer
> reports a `DuplicateAddress` conflict. Because there is no runtime "load this address *with* this
> label" call, the language has to live in the address itself.

3. **Create Rules**:
```
LayoutRuleData: "Localization_Rules"

# English
Address Rule 1: "English Localization"
- Priority: 100
- Target Group: "Localization"
- Filters: [LocalizationEN]
- Address Provider: LocalizationEN_Address

Label Rule 1: "English Language"
- Priority: 100
- Filters: [LocalizationEN]
- Label Provider: EnglishLabel

# Spanish
Address Rule 2: "Spanish Localization"
- Priority: 90
- Target Group: "Localization"
- Filters: [LocalizationES]
- Address Provider: LocalizationES_Address

Label Rule 2: "Spanish Language"
- Priority: 90
- Filters: [LocalizationES]
- Label Provider: SpanishLabel

# Japanese
Address Rule 3: "Japanese Localization"
- Priority: 80
- Target Group: "Localization"
- Filters: [LocalizationJP]
- Address Provider: LocalizationJP_Address

Label Rule 3: "Japanese Language"
- Priority: 80
- Filters: [LocalizationJP]
- Label Provider: JapaneseLabel
```

**Expected Results**:
```
EN/ui_strings.txt → Address: "en/ui_strings" Labels: ["lang_en"]
ES/ui_strings.txt → Address: "es/ui_strings" Labels: ["lang_es"]
JP/ui_strings.txt → Address: "jp/ui_strings" Labels: ["lang_jp"]
```

**Runtime Usage**:
```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using AddressableManager.API;
using AddressableManager.Core;
using UnityEngine;

public class LocalizationLoader : MonoBehaviour
{
    private string _currentLanguage = "en";
    private List<IAssetHandle<TextAsset>> _loaded = new List<IAssetHandle<TextAsset>>();

    // Load one known asset for the current language
    public async Task<TextAsset> LoadUiStrings()
    {
        return await Simple.Load<TextAsset>($"{_currentLanguage}/ui_strings");
    }

    // Load everything tagged for a language, keeping the handles so they can be released
    public async Task SwitchLanguage(string newLanguage)
    {
        // Release the previous language: disposing the handles drops this code's references
        foreach (var handle in _loaded) handle.Dispose();
        _loaded.Clear();

        _currentLanguage = newLanguage;
        _loaded = await Standard.LoadByLabel<TextAsset>($"lang_{newLanguage}");
    }
}
```

> There is no "release everything with label X" call. Either keep the handles and dispose them, as
> above, or evict a single address with `Simple.ReleaseAddress(address)` — note that eviction is
> unconditional, so any handle still held for that address becomes invalid.

---

## Example 7: Level Scene Assets

**Scenario**: Automatic address and label configuration for level scenes

**Folder Structure**:
```
Assets/
└─ Scenes/
   ├─ Levels/
   │  ├─ Level_01.unity
   │  ├─ Level_02.unity
   │  └─ Level_Boss.unity
   └─ Shared/
      ├─ LightingData/
      └─ NavMeshData/
```

**Setup Steps**:

1. **Create Filters**:
```
PathFilter: "LevelScenesFilter"
- Match Mode: Glob
- Pattern: "Assets/Scenes/Levels/*.unity"

TypeFilter: "SceneAssetFilter"
- Type Name: "SceneAsset"     (resolves out of the UnityEditor assembly)
```

2. **Create Providers**:
```
FileNameAddressProvider: "LevelAddressProvider"
- To Lower Case: true

FolderLabelProvider: "LevelLabelProvider"
- Use Parent Folder: true     (default)
- To Lower Case:     true
- Prefix:            "level_"
```

> `FolderLabelProvider` has no depth setting: it emits either the immediate parent folder
> (`Use Parent Folder`) or every folder in the path (`Use All Folders`).

3. **Create Rules**:
```
LayoutRuleData: "Levels_Rules"

Address Rule: "Level Scenes"
- Priority: 100
- Target Group: "Levels"
- Filters: [LevelScenesFilter, SceneAssetFilter]
- Address Provider: LevelAddressProvider

Label Rule: "Level Labels"
- Priority: 90
- Filters: [LevelScenesFilter]
- Label Provider: LevelLabelProvider
```

**Expected Results**:
```
Level_01.unity   → Address: "level_01"   Labels: ["level_levels"]
Level_02.unity   → Address: "level_02"   Labels: ["level_levels"]
Level_Boss.unity → Address: "level_boss" Labels: ["level_levels"]
```

(The label is `level_levels` because the parent folder is `Levels`, lowercased to `levels` and then
given the `level_` prefix.)

**Runtime Usage**:

This package does **not** load Unity scenes. Grep confirms it: there is no `LoadSceneAsync` against
Unity's Addressables, no `SceneInstance`, no `LoadSceneMode` anywhere in the runtime. What the rule
system gives you here is a predictable **address** for each level scene; hand that address to Unity's
own Addressables scene-loading API to actually enter the level.

Two names in this package look like scene loaders and are not:

- `Standard.LoadScene<T>(string)` — marked `[Obsolete]`. It loads an **asset**, not a scene.
- `Assets.LoadScene<T>(string)` / `AddressablesFacade.LoadSceneAsync<T>(string)` — these load an asset into the **active scene's cache scope**. "Scene scope" throughout this package means a cache whose lifetime is tied to a scene, never scene loading.

What you *can* do here is bind an asset's lifetime to a scene, so it is released when that scene
unloads:

```csharp
using AddressableManager.API;
using AddressableManager.Core;
using UnityEngine;

// Loads the asset into the ACTIVE scene's scope - released when that scene goes away.
IAssetHandle<GameObject> levelProps = await Standard.LoadIntoSceneScope<GameObject>("level_01_props");
```

> `Standard.LoadIntoSceneScope` returns `UniTask<T>` when UniTask is installed in the project and
> `Task<T>` otherwise. Both are awaitable, so the line above compiles either way, but don't assign the
> result to an explicit `Task<...>` variable.

---

## Example 8: Version-Tracked Assets

**Scenario**: Track assets across builds using Git commits

**Folder Structure**:
```
Assets/
└─ Content/
   ├─ Core/
   │  ├─ essential_data.asset
   │  └─ core_config.asset
   └─ Updates/
      ├─ feature_a.asset
      └─ feature_b.asset
```

**Setup Steps**:

1. **Create Filters** (**Match Mode: Glob**):
```
PathFilter: "CoreContentFilter"
- Pattern: "Assets/Content/Core/**"

PathFilter: "UpdateContentFilter"
- Pattern: "Assets/Content/Updates/**"
```

2. **Create Providers**:
```
PathAddressProvider: "ContentAddressProvider"
- Remove Root Folder: "Content/"
- To Lower Case: true

ConstantVersionProvider: "CoreVersion"
- Version: "1.0.0"

GitCommitVersionProvider: "GitVersion"
- Mode: CommitHash                       (short hash; use CommitHashFull for the 40-char form)
- Fallback Version: "0.0.0-unknown"      (default, used when Git is unavailable)
```

> There is no hash-length setting. `CommitHash` is the short hash and `CommitHashFull` the long one.
> The version is resolved once and reused for every asset in the run.

3. **Create Rules**:
```
LayoutRuleData: "Versioned_Rules"

Address Rule 1: "Core Content"
- Priority: 100
- Target Group: "Content_Core"
- Filters: [CoreContentFilter]
- Address Provider: ContentAddressProvider

Address Rule 2: "Update Content"
- Priority: 90
- Target Group: "Content_Updates"
- Filters: [UpdateContentFilter]
- Address Provider: ContentAddressProvider

Version Rule 1: "Core Fixed Version"
- Priority: 100
- Filters: [CoreContentFilter]
- Version Provider: CoreVersion

Version Rule 2: "Update Git Version"
- Priority: 90
- Filters: [UpdateContentFilter]
- Version Provider: GitVersion
```

**Expected Results**:
```
Core/essential_data.asset:
  Address: "core/essential_data"
  Labels:  ["version:1.0.0"]

Updates/feature_a.asset:
  Address: "updates/feature_a"
  Labels:  ["version:a1b2c3d"]
```

**Restricting a run by version (Editor / CI, not runtime)**

Version filtering happens when rules are **applied**, not when assets are loaded. Set it on the
`LayoutRuleData` asset, or from CI:

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.SetVersionExpression \
  -layoutRuleAssetPath "Assets/Rules/Versioned_Rules.asset" \
  -versionExpression "[1.0.0,2.0.0)" \
  -excludeUnversioned true
```

Only assets whose **existing** `version:` label satisfies the expression are touched by the next run.
Valid formats: `[1.0.0,2.0.0)`, `(1.0.0,2.0.0]`, `[1.0.0,2.0.0]`, `1.0.0`, `>=1.0.0`, `>1.0.0`,
`<=2.0.0`, `<2.0.0`.

**Runtime Usage**:

There is no version-filtered load and no resource-location query in this package. Because a version is
just a label, the one thing you *can* do at runtime is load everything stamped with a given version:

```csharp
using AddressableManager.API;
using UnityEngine;

// "version:" labels are ordinary labels, so they are queryable like any other
var pinned = await Standard.LoadByLabel<ScriptableObject>("version:1.0.0");
foreach (var handle in pinned)
{
    ScriptableObject data = handle.Asset;
    // ... use data ...
}
foreach (var handle in pinned) handle.Dispose();

// Loading a single known asset stays address-based
ScriptableObject featureA = await Simple.Load<ScriptableObject>("updates/feature_a");
```

---

## Summary

These examples demonstrate:

✅ **Address Rules**: Filename-based and path-based addressing, combined with type filtering
✅ **Label Rules**: Platform, content-type, quality and language labeling via `ConstantLabelProvider` and `FolderLabelProvider`
✅ **Version Rules**: Fixed, build number, git commit and timestamp versioning, all written as `version:` labels
✅ **Filter Combinations**: Path + Type, multiple filters ANDed together
✅ **Real-World Scenarios**: UI, audio, textures, characters, DLC, localization, level scenes, versioning

**Things the examples deliberately do not do**, because the package does not support them:
- Load an asset by address *and* label in one call
- Filter loads by version at runtime
- Load a Unity scene (see Example 7)
- Release a whole label in one call (dispose the handles instead)

**Next Steps**:
- Mix and match these patterns for your project
- Use the [Templates](../Packages/com.game.addressables/Editor/Templates/) as starting points
- Import the **Rule Automation Presets** sample from the Package Manager for pre-wired rule assets
- Refer to [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md) for detailed concepts

---

**Need Help?**

Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for common issues.

**Version**: 4.1.0-pre.9 | **Unity**: 2023.1+
