# Rule System Examples

**Practical examples for common addressable automation scenarios**

This guide provides copy-paste-ready examples for typical use cases. Each example includes complete setup instructions and expected results.

---

## Table of Contents

1. [Example 1: Basic UI Assets](#example-1-basic-ui-assets)
2. [Example 2: Audio Library Organization](#example-2-audio-library-organization)
3. [Example 3: Multi-Platform Textures](#example-3-multi-platform-textures)
4. [Example 4: Character System](#example-4-character-system)
5. [Example 5: DLC Content Management](#example-5-dlc-content-management)
6. [Example 6: Localization System](#example-6-localization-system)
7. [Example 7: Scene-Based Level Loading](#example-7-scene-based-level-loading)
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
- Path Pattern: "Assets/UI/**/*.png"

FileNameAddressProvider: "UIAddressProvider"
- Strip Extension: true
- To Lower Case: true
```

2. **Create Rule**:
```
LayoutRuleData: "UI_Rules"

Address Rule: "UI Sprites"
- Enabled: ✓
- Priority: 100
- Target Group: "UI"
- Skip Existing: false
- Filters: [UIPathFilter]
- Provider: UIAddressProvider
```

3. **Apply Rules**:
```
1. Open Layout Rule Editor
2. Select UI_Rules
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
// Load UI sprites
var startButton = await AddressableManager.LoadAsync<Sprite>("btn_start");
var healthIcon = await AddressableManager.LoadAsync<Sprite>("icon_health");
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

1. **Create Filter Assets**:
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
- Provider: AudioAddressProvider

Address Rule 2: "SFX Files"
- Priority: 100
- Target Group: "Audio"
- Filters: [SFXFilter]
- Provider: AudioAddressProvider

Address Rule 3: "Voice Over Files"
- Priority: 90
- Target Group: "Audio"
- Filters: [VOFilter]
- Provider: AudioAddressProvider

Label Rule 1: "Music Label"
- Priority: 100
- Append To Existing: true
- Filters: [MusicFilter]
- Provider: MusicLabel

Label Rule 2: "SFX Label"
- Priority: 100
- Filters: [SFXFilter]
- Provider: SFXLabel

Label Rule 3: "VO Label"
- Priority: 100
- Filters: [VOFilter]
- Provider: VOLabel
```

**Expected Results**:
```
theme_main.mp3 → Address: "theme_main" Labels: ["audio_music"]
explosion.wav  → Address: "explosion"  Labels: ["audio_sfx"]
dialogue_001.wav → Address: "dialogue_001" Labels: ["audio_vo"]
```

**Runtime Usage**:
```csharp
// Load all music
var musicClips = await AddressableManager.LoadAssetsAsync<AudioClip>(
    labels: new[] { "audio_music" }
);

// Load specific SFX
var explosion = await AddressableManager.LoadAsync<AudioClip>(
    "explosion",
    labels: new[] { "audio_sfx" }
);

// Preload all VO for a scene
await AddressableManager.PreloadAsync<AudioClip>(labels: new[] { "audio_vo" });
```

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

1. **Create Filters**:
```
PathFilter: "HighResFilter"
- Pattern: "Assets/Textures/HighRes/**"

PathFilter: "LowResFilter"
- Pattern: "Assets/Textures/LowRes/**"
```

2. **Create Providers**:
```
PathAddressProvider: "HighResAddressProvider"
- Base Directory: "Assets/Textures/HighRes"

PathAddressProvider: "LowResAddressProvider"
- Base Directory: "Assets/Textures/LowRes"

ConstantLabelProvider: "PCLabel"
- Labels: ["platform_pc", "quality_high"]

ConstantLabelProvider: "MobileLabel"
- Labels: ["platform_mobile", "quality_low"]
```

3. **Create Rules**:
```
LayoutRuleData: "Textures_Rules"

Address Rule 1: "High Res Textures"
- Priority: 100
- Target Group: "Textures_High"
- Filters: [HighResFilter]
- Provider: HighResAddressProvider

Address Rule 2: "Low Res Textures"
- Priority: 100
- Target Group: "Textures_Low"
- Filters: [LowResFilter]
- Provider: LowResAddressProvider

Label Rule 1: "PC Platform"
- Priority: 90
- Filters: [HighResFilter]
- Provider: PCLabel

Label Rule 2: "Mobile Platform"
- Priority: 90
- Filters: [LowResFilter]
- Provider: MobileLabel
```

**Expected Results**:
```
HighRes/char_hero_4k.png:
  Address: "char_hero_4k"
  Group: "Textures_High"
  Labels: ["platform_pc", "quality_high"]

LowRes/char_hero_1k.png:
  Address: "char_hero_1k"
  Group: "Textures_Low"
  Labels: ["platform_mobile", "quality_low"]
```

**Runtime Usage**:
```csharp
// Determine platform at runtime
string platform = "platform_mobile"; // or "platform_pc"

// Load platform-specific texture
var heroTexture = await AddressableManager.LoadAsync<Texture2D>(
    "char_hero_4k", // or "char_hero_1k" based on platform
    labels: new[] { platform }
);

// Or load all textures for current platform
var allTextures = await AddressableManager.LoadAssetsAsync<Texture2D>(
    labels: new[] { platform }
);
```

---

## Example 4: Character System

**Scenario**: Manage character prefabs with automatic weapon dependencies

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
- Pattern: "Assets/Characters/Prefabs/*.prefab"

PathFilter: "WeaponFilter"
- Pattern: "Assets/Characters/Weapons/*.prefab"

TypeFilter: "PrefabFilter"
- Asset Type: GameObject
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
- Filters: [CharacterFilter, PrefabFilter]
- Provider: CharacterAddressProvider

Address Rule 2: "Weapon Prefabs"
- Priority: 100
- Target Group: "Characters"
- Filters: [WeaponFilter, PrefabFilter]
- Provider: CharacterAddressProvider

Label Rule 1: "Character Tag"
- Priority: 90
- Filters: [CharacterFilter]
- Provider: CharacterLabel

Label Rule 2: "Weapon Tag"
- Priority: 90
- Filters: [WeaponFilter]
- Provider: WeaponLabel
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
// Load character with weapon
var warrior = await AddressableManager.LoadAsync<GameObject>(
    "warrior",
    labels: new[] { "character" }
);

// Load all weapons for character selection
var allWeapons = await AddressableManager.LoadAssetsAsync<GameObject>(
    labels: new[] { "weapon" }
);

// Batch load characters and weapons
using var scope = AddressableManager.CreateSessionScope();
await scope.LoadAsync<GameObject>(new[] { "warrior", "sword" });
```

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

1. **Create Filters**:
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
- Base Directory: "Assets/BaseGame"

PathAddressProvider: "DLC1AddressProvider"
- Base Directory: "Assets/DLC/Expansion1"

PathAddressProvider: "DLC2AddressProvider"
- Base Directory: "Assets/DLC/Expansion2"

ConstantVersionProvider: "BaseGameVersion"
- Version: "1.0.0"

BuildNumberVersionProvider: "DLCVersion"
- Source: Combined

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
- Provider: BaseGameAddressProvider

Label Rule 1: "Base Content Label"
- Priority: 100
- Filters: [BaseGameFilter]
- Provider: BaseLabel

Version Rule 1: "Base Game Version"
- Priority: 100
- Filters: [BaseGameFilter]
- Provider: BaseGameVersion

# DLC 1
Address Rule 2: "Expansion 1 Assets"
- Priority: 100
- Target Group: "DLC_Expansion1"
- Filters: [DLC1Filter]
- Provider: DLC1AddressProvider

Label Rule 2: "Expansion 1 Label"
- Priority: 90
- Filters: [DLC1Filter]
- Provider: DLC1Label

Version Rule 2: "Expansion 1 Version"
- Priority: 90
- Filters: [DLC1Filter]
- Provider: DLCVersion

# DLC 2
Address Rule 3: "Expansion 2 Assets"
- Priority: 90
- Target Group: "DLC_Expansion2"
- Filters: [DLC2Filter]
- Provider: DLC2AddressProvider

Label Rule 3: "Expansion 2 Label"
- Priority: 80
- Filters: [DLC2Filter]
- Provider: DLC2Label

Version Rule 3: "Expansion 2 Version"
- Priority: 80
- Filters: [DLC2Filter]
- Provider: DLCVersion
```

**Expected Results**:
```
BaseGame/Levels/level1.unity:
  Address: "levels/level1"
  Group: "BaseGame"
  Labels: ["content_base"]
  Version: "version:1.0.0"

DLC/Expansion1/Levels/level_bonus.unity:
  Address: "levels/level_bonus"
  Group: "DLC_Expansion1"
  Labels: ["content_dlc", "expansion1"]
  Version: "version:1.2.3.456"
```

**Runtime Usage**:
```csharp
// Check if DLC is owned
bool hasDLC1 = PlayerPrefs.GetInt("OwnsDLC1") == 1;

if (hasDLC1)
{
    // Load DLC content
    var dlcLevels = await AddressableManager.LoadAssetsAsync<Scene>(
        labels: new[] { "expansion1" }
    );
}

// Load only base game content
var baseLevels = await AddressableManager.LoadAssetsAsync<Scene>(
    labels: new[] { "content_base" }
);
```

---

## Example 6: Localization System

**Scenario**: Multi-language text assets with fallback support

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

1. **Create Filters**:
```
PathFilter: "LocalizationEN"
- Pattern: "Assets/Localization/EN/**"

PathFilter: "LocalizationES"
- Pattern: "Assets/Localization/ES/**"

PathFilter: "LocalizationJP"
- Pattern: "Assets/Localization/JP/**"
```

2. **Create Providers**:
```
PathAddressProvider: "LocalizationAddressProvider"
- Base Directory: "Assets/Localization/EN" (or ES, JP)

ConstantLabelProvider: "EnglishLabel"
- Labels: ["lang_en"]

ConstantLabelProvider: "SpanishLabel"
- Labels: ["lang_es"]

ConstantLabelProvider: "JapaneseLabel"
- Labels: ["lang_jp"]
```

3. **Create Rules**:
```
LayoutRuleData: "Localization_Rules"

# English
Address Rule 1: "English Localization"
- Priority: 100
- Target Group: "Localization"
- Filters: [LocalizationEN]
- Provider: LocalizationAddressProvider (base: Assets/Localization/EN)

Label Rule 1: "English Language"
- Priority: 100
- Filters: [LocalizationEN]
- Provider: EnglishLabel

# Spanish
Address Rule 2: "Spanish Localization"
- Priority: 90
- Target Group: "Localization"
- Filters: [LocalizationES]
- Provider: LocalizationAddressProvider (base: Assets/Localization/ES)

Label Rule 2: "Spanish Language"
- Priority: 90
- Filters: [LocalizationES]
- Provider: SpanishLabel

# Japanese
Address Rule 3: "Japanese Localization"
- Priority: 80
- Target Group: "Localization"
- Filters: [LocalizationJP]
- Provider: LocalizationAddressProvider (base: Assets/Localization/JP)

Label Rule 3: "Japanese Language"
- Priority: 80
- Filters: [LocalizationJP]
- Provider: JapaneseLabel
```

**Expected Results**:
```
EN/ui_strings.txt → Address: "ui_strings" Labels: ["lang_en"]
ES/ui_strings.txt → Address: "ui_strings" Labels: ["lang_es"]
JP/ui_strings.txt → Address: "ui_strings" Labels: ["lang_jp"]
```

**Runtime Usage**:
```csharp
// Load current language
string currentLanguage = "lang_" + Application.systemLanguage.ToString().ToLower();

var uiStrings = await AddressableManager.LoadAsync<TextAsset>(
    "ui_strings",
    labels: new[] { currentLanguage }
);

// Load all dialogue for current language
var allDialogue = await AddressableManager.LoadAssetsAsync<TextAsset>(
    labels: new[] { currentLanguage }
);

// Switch language at runtime
public async Task SwitchLanguage(string newLanguage)
{
    // Release old language assets
    AddressableManager.ReleaseByLabel($"lang_{_currentLanguage}");

    // Load new language
    _currentLanguage = newLanguage;
    await AddressableManager.LoadAssetsAsync<TextAsset>(
        labels: new[] { $"lang_{newLanguage}" }
    );
}
```

---

## Example 7: Scene-Based Level Loading

**Scenario**: Automatic level scene configuration with dependencies

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
- Pattern: "Assets/Scenes/Levels/*.unity"

TypeFilter: "SceneAssetFilter"
- Asset Type: SceneAsset
```

2. **Create Providers**:
```
FileNameAddressProvider: "LevelAddressProvider"
- To Lower Case: true

FolderLabelProvider: "LevelLabelProvider"
- Folder Depth: 2
- Prefix: "level_"
```

3. **Create Rules**:
```
LayoutRuleData: "Levels_Rules"

Address Rule: "Level Scenes"
- Priority: 100
- Target Group: "Levels"
- Filters: [LevelScenesFilter, SceneAssetFilter]
- Provider: LevelAddressProvider

Label Rule: "Level Labels"
- Priority: 90
- Filters: [LevelScenesFilter]
- Provider: LevelLabelProvider
```

**Expected Results**:
```
Level_01.unity   → Address: "level_01"   Labels: ["level_levels"]
Level_02.unity   → Address: "level_02"   Labels: ["level_levels"]
Level_Boss.unity → Address: "level_boss" Labels: ["level_levels"]
```

**Runtime Usage**:
```csharp
// Load level by index
public async Task LoadLevel(int levelIndex)
{
    string levelAddress = $"level_{levelIndex:00}";

    // Load scene additively
    await AddressableManager.LoadSceneAsync(
        levelAddress,
        UnityEngine.SceneManagement.LoadSceneMode.Additive
    );
}

// Preload next level
public async Task PreloadNextLevel(int currentLevel)
{
    string nextLevel = $"level_{(currentLevel + 1):00}";
    await AddressableManager.PreloadAsync<SceneInstance>(nextLevel);
}

// Get all available levels
var allLevels = await AddressableManager.GetResourceLocationsAsync(
    labels: new[] { "level_levels" }
);
```

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

1. **Create Filters**:
```
PathFilter: "CoreContentFilter"
- Pattern: "Assets/Content/Core/**"

PathFilter: "UpdateContentFilter"
- Pattern: "Assets/Content/Updates/**"
```

2. **Create Providers**:
```
PathAddressProvider: "ContentAddressProvider"
- Base Directory: "Assets/Content"

ConstantVersionProvider: "CoreVersion"
- Version: "1.0.0"

GitCommitVersionProvider: "GitVersion"
- Git Version Mode: CommitHash
- Hash Length: 7
```

3. **Create Rules**:
```
LayoutRuleData: "Versioned_Rules"

Address Rule 1: "Core Content"
- Priority: 100
- Target Group: "Content_Core"
- Filters: [CoreContentFilter]
- Provider: ContentAddressProvider

Address Rule 2: "Update Content"
- Priority: 90
- Target Group: "Content_Updates"
- Filters: [UpdateContentFilter]
- Provider: ContentAddressProvider

Version Rule 1: "Core Fixed Version"
- Priority: 100
- Filters: [CoreContentFilter]
- Provider: CoreVersion

Version Rule 2: "Update Git Version"
- Priority: 90
- Filters: [UpdateContentFilter]
- Provider: GitVersion
```

**Expected Results**:
```
Core/essential_data.asset:
  Address: "core/essential_data"
  Version: "version:1.0.0"

Updates/feature_a.asset:
  Address: "updates/feature_a"
  Version: "version:a1b2c3d"
```

**Runtime Usage**:
```csharp
// Load content matching version criteria
var coreAssets = await AddressableManager.LoadAssetsAsync<ScriptableObject>(
    versionFilter: "1.0.0"
);

// Check content version before loading
var locations = await AddressableManager.GetResourceLocationsAsync(
    "updates/feature_a"
);

foreach (var location in locations)
{
    if (location.HasLabel("version:a1b2c3d"))
    {
        // Load specific version
        await AddressableManager.LoadAsync<ScriptableObject>(location);
    }
}
```

---

## Summary

These examples demonstrate:

✅ **Address Rules**: Filename-based, path-based, type-based addressing
✅ **Label Rules**: Platform, content-type, quality, language labeling
✅ **Version Rules**: Fixed, build number, git commit, timestamp versioning
✅ **Filter Combinations**: Path + Type, multiple filters, dependency tracking
✅ **Real-World Scenarios**: UI, audio, textures, characters, DLC, localization, levels, versioning

**Next Steps**:
- Mix and match these patterns for your project
- Use [Templates](../Packages/com.game.addressables/Editor/Templates/) as starting points
- Refer to [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md) for detailed concepts

---

**Questions or Need Help?**

Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for common issues, or reach out to the community!

**Version**: 3.5.0 | **Unity**: 2021.3+
