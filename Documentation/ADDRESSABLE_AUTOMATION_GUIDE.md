# Addressable Automation Guide

**Complete guide to automating addressable asset management using the rule-based system**

Version 3.5.0 | Last Updated: January 2025

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

The Addressable Automation System transforms manual addressable asset management into a powerful, rule-based workflow. Instead of manually configuring each asset, you define rules that automatically assign addresses, labels, and versions based on asset properties, paths, types, and custom criteria.

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

- **Address Assignment**: Generate addresses from filename, path, or custom logic
- **Label Management**: Apply platform, quality, content-type, or custom labels
- **Version Control**: Automatic versioning using build numbers, git commits, or timestamps
- **Group Organization**: Route assets to appropriate addressable groups
- **Content Updates**: Manage DLC and remote content delivery

---

## Core Concepts

### The Rule-Based System

The automation system uses three types of rules that work together:

```
Asset → Filters (Match?) → Providers (Generate) → Output
```

**1. Address Rules**
- Determine which group an asset belongs to
- Generate unique addresses for asset loading
- Control group membership

**2. Label Rules**
- Add metadata tags to assets
- Enable conditional loading (platform, quality, language)
- Support multiple labels per asset

**3. Version Rules**
- Tag assets with version identifiers
- Track content across builds
- Enable content update strategies

### Anatomy of a Rule

Every rule has the same core structure:

```csharp
Rule {
    Name: "Descriptive rule name"
    Enabled: true/false
    Priority: 100 (higher = processed first)
    Description: "What this rule does"

    Filters: [List of conditions that must match]
    Provider: {Logic that generates the output}

    Skip Existing: true/false (respect manual config)
}
```

### How Rules Are Applied

1. **Setup Phase**: All filters and providers initialize
2. **Matching Phase**: Assets are tested against filters (AND logic)
3. **Priority Sorting**: Higher priority rules process first
4. **Generation Phase**: Provider generates address/label/version
5. **Application Phase**: Output is applied to asset entry

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
Select your MainRules asset
```

### 3. Create a Simple Address Rule

**Goal**: Assign all sprites in `Assets/UI/` using their filename as address

**Steps**:
```
1. Click "Address" tab
2. Click "+ Add Rule"
3. Configure:
   - Rule Name: "UI Sprites"
   - Target Group: "UI"
   - Priority: 100
   - Enabled: ✓

4. Add PathFilter:
   - Create > Addressable Manager > Filters > Path Filter
   - Path Pattern: "Assets/UI/**/*.png"

5. Add FileNameAddressProvider:
   - Create > Addressable Manager > Providers > Address > File Name

6. Assign filter and provider to rule
7. Click "Apply All"
```

**Result**: All PNG files in UI folder now addressable as their filename

### 4. Verify with Preview

```
1. Select the rule in the list
2. Click "Refresh Preview" in right panel
3. See matched assets and generated addresses
```

---

## Rule Types

### Address Rules

**Purpose**: Determine addressable identity and group membership

**Key Settings**:
- `TargetGroupName`: Which addressable group to add assets to
- `SkipExisting`: Preserve manually-set addresses

**Common Patterns**:

#### Pattern 1: By Filename
```
Use Case: Simple UI elements, audio clips
Filter: PathFilter("Assets/Audio/**/*.wav")
Provider: FileNameAddressProvider
Result: "explosion.wav", "music_theme.wav"
```

#### Pattern 2: By Path
```
Use Case: Prefabs, scenes with unique paths
Filter: TypeFilter(GameObject)
Provider: PathAddressProvider
Result: "prefabs/characters/player", "prefabs/enemies/zombie"
```

#### Pattern 3: By Custom Logic
```
Use Case: Complex naming schemes
Filter: Multiple filters combined
Provider: Custom provider with transformation logic
Result: "char_warrior_lvl5", "weapon_sword_epic"
```

### Label Rules

**Purpose**: Add metadata for conditional loading

**Key Settings**:
- `AppendToExisting`: Add to existing labels vs replace
- `Priority`: Order of label application

**Common Patterns**:

#### Pattern 1: Platform Labels
```yaml
Rule: "Mobile Optimized Assets"
Filter: PathFilter("Assets/Mobile/**")
Provider: ConstantLabelProvider(["mobile", "lowres"])
Result: Assets tagged for mobile-only loading
```

#### Pattern 2: Content Type Labels
```yaml
Rule: "DLC Content"
Filter: PathFilter("Assets/DLC/**")
Provider: FolderLabelProvider
Result: Assets tagged with "dlc_expansion1", "dlc_expansion2"
```

#### Pattern 3: Quality Tiers
```yaml
Rule: "High Quality Textures"
Filter: PathFilter("Assets/Textures/4K/**")
Provider: ConstantLabelProvider(["quality_high", "texture"])
Result: Enable quality-based loading
```

### Version Rules

**Purpose**: Track asset versions across builds

**Key Settings**:
- `SkipExisting`: Preserve manual version tags
- `Provider`: Source of version information

**Common Patterns**:

#### Pattern 1: Build Number Versioning
```yaml
Rule: "All Assets - Build Version"
Filter: (none - matches all)
Provider: BuildNumberVersionProvider
Result: Assets tagged as "version:1.0.0.123"
```

#### Pattern 2: Git Commit Tracking
```yaml
Rule: "Track by Git Commit"
Filter: PathFilter("Assets/Content/**")
Provider: GitCommitVersionProvider(mode: CommitHash)
Result: Assets tagged as "version:a1b2c3d"
```

#### Pattern 3: Date-Based Versions
```yaml
Rule: "Daily Build Stamp"
Filter: (none)
Provider: DateVersionProvider(format: YYYYMMDD)
Result: Assets tagged as "version:20250104"
```

---

## Filters

Filters define which assets a rule should match. All filters in a rule must pass (AND logic).

### Available Filter Types

#### 1. PathFilter
**Matches**: Assets based on file path patterns

```csharp
PathFilter
├─ Pattern: "Assets/UI/**/*.png"
├─ IncludeSubdirectories: true
└─ Recursive: true
```

**Glob Patterns**:
- `*` = Any characters in one segment
- `**` = Any directories (recursive)
- `?` = Single character
- `[abc]` = Character class

**Examples**:
```
"Assets/Characters/**/*.prefab" → All prefabs under Characters
"Assets/Scenes/Level*.unity" → Level1, Level2, etc.
"Assets/Audio/SFX/*.{wav,mp3}" → WAV or MP3 in SFX folder
```

#### 2. TypeFilter
**Matches**: Assets based on Unity type

```csharp
TypeFilter
└─ AssetType: GameObject, Texture2D, AudioClip, etc.
```

**Common Types**:
- `GameObject` → Prefabs
- `Texture2D` → Textures, sprites
- `AudioClip` → Audio files
- `Material` → Materials
- `SceneAsset` → Scenes

#### 3. ExtensionFilter
**Matches**: Assets by file extension

```csharp
ExtensionFilter
└─ Extensions: [".png", ".jpg", ".jpeg"]
```

**Use Case**: Match multiple asset types that don't share a Unity type

#### 4. AddressFilter
**Matches**: Assets that already have specific addresses

```csharp
AddressFilter
└─ AddressPattern: "ui_*" (supports wildcards)
```

**Use Case**: Apply labels to previously addressed assets

#### 5. AddressableGroupFilter
**Matches**: Assets in specific addressable groups

```csharp
AddressableGroupFilter
└─ GroupName: "Characters"
```

**Use Case**: Apply labels/versions to assets already in a group

#### 6. ObjectFilter
**Matches**: Specific asset objects

```csharp
ObjectFilter
└─ TargetObjects: [List of asset references]
```

**Use Case**: Manually curate small sets of special assets

#### 7. FindAssetsFilter
**Matches**: Using Unity's AssetDatabase search

```csharp
FindAssetsFilter
└─ SearchQuery: "t:Prefab l:Important"
```

**Search Syntax**:
- `t:Type` → Type filter
- `l:Label` → Label filter
- `"name"` → Name search

#### 8. DependentObjectFilter
**Matches**: Assets that depend on specified objects

```csharp
DependentObjectFilter
└─ RootObjects: [Master assets]
```

**Use Case**: Group assets with their dependencies

---

## Providers

Providers generate the actual output (address, labels, versions) for matched assets.

### Address Providers

#### FileNameAddressProvider
**Generates**: Filename without extension

```
Assets/UI/button_start.png → "button_start"
```

**Settings**:
- `StripExtension`: true/false
- `ToLowerCase`: Normalize to lowercase

**Best For**: Simple assets with unique filenames

#### PathAddressProvider
**Generates**: Relative path from base directory

```
Assets/Prefabs/Characters/Player.prefab → "characters/player"
```

**Settings**:
- `BaseDirectory`: Path to strip from beginning
- `Separator`: '/' or '\\'
- `ToLowerCase`: Normalize casing

**Best For**: Assets needing hierarchical addresses

### Label Providers

#### ConstantLabelProvider
**Generates**: Fixed label(s)

```yaml
Labels: ["platform_pc", "quality_high"]
```

**Best For**: Static classification

#### FolderLabelProvider
**Generates**: Label from folder structure

```
Assets/Content/Levels/Forest/ → "levels_forest"
```

**Settings**:
- `FolderDepth`: Which folder level to use
- `Prefix`: Add prefix to label
- `ToLowerCase`: Normalize casing

**Best For**: Content organization by folders

### Version Providers

#### ConstantVersionProvider
**Generates**: Fixed version string

```yaml
Version: "1.0.0"
```

**Best For**: Manual version control

#### BuildNumberVersionProvider
**Generates**: Version from Unity build settings

```yaml
Source: BundleVersion → "1.2.3"
Source: BuildNumber → "456"
Source: Combined → "1.2.3.456"
```

**Settings**:
- `VersionSource`: BundleVersion | BuildNumber | Combined
- `Platform`: Android | iOS | Standalone
- `Prefix`/`Suffix`: Add decorators

**Best For**: Official releases

#### GitCommitVersionProvider
**Generates**: Version from Git repository

```yaml
Mode: CommitHash → "a1b2c3d"
Mode: LatestTag → "v1.0.0"
Mode: Describe → "v1.0.0-5-ga1b2c3d"
```

**Settings**:
- `GitVersionMode`: CommitHash | LatestTag | TagOrHash | Describe
- `HashLength`: Short (7) or full (40)

**Best For**: Development builds, continuous deployment

#### DateVersionProvider
**Generates**: Version from timestamp

```yaml
Format: YYYYMMDD → "20250104"
Format: ISO8601 → "2025-01-04T15:05:30Z"
Format: UnixTimestamp → "1704380730"
```

**Settings**:
- `DateFormat`: Predefined formats or custom
- `UseUTC`: UTC vs local time
- `CustomFormat`: C# date format string

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

```yaml
# Address Rule 1: High Quality
Name: "PC/Console Assets"
Filter: PathFilter("Assets/Content/HighRes/**")
Provider: PathAddressProvider(base: "Assets/Content/HighRes")
Target Group: "Content_HighQuality"
Priority: 100

# Address Rule 2: Mobile
Name: "Mobile Assets"
Filter: PathFilter("Assets/Content/LowRes/**")
Provider: PathAddressProvider(base: "Assets/Content/LowRes")
Target Group: "Content_Mobile"
Priority: 100

# Label Rule 1: Platform Tags
Name: "PC Platform Tag"
Filter: AddressableGroupFilter("Content_HighQuality")
Provider: ConstantLabelProvider(["platform_pc"])
Priority: 90

# Label Rule 2: Mobile Platform Tag
Name: "Mobile Platform Tag"
Filter: AddressableGroupFilter("Content_Mobile")
Provider: ConstantLabelProvider(["platform_mobile"])
Priority: 90
```

3. **Runtime Loading**:
```csharp
// Load platform-specific assets
string platform = "platform_mobile"; // or "platform_pc"
await AddressableManager.LoadAsync<GameObject>(address, labels: new[] { platform });
```

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

2. **Create Version-Aware Rules**:

```yaml
# Base Game - Static Version
Name: "Base Game Content"
Filter: PathFilter("Assets/BaseGame/**")
Provider: PathAddressProvider
Target Group: "BaseGame"
Labels: ["content_base", "static"]
Version: ConstantVersionProvider("1.0.0")

# DLC - Build Version
Name: "DLC Expansion 1"
Filter: PathFilter("Assets/DLC/Expansion1/**")
Provider: PathAddressProvider
Target Group: "DLC_Expansion1"
Labels: ["content_dlc", "expansion1"]
Version: BuildNumberVersionProvider(source: Combined)

# Apply to specific version range
VersionExpression: "[1.1.0,2.0.0)"
ExcludeUnversioned: true
```

3. **Version-Based Loading**:
```csharp
// Load assets matching version criteria
await AddressableManager.LoadAsync<T>(
    address,
    labels: new[] { "expansion1" },
    versionFilter: "[1.1.0,)"
);
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

2. **Language Rules**:

```yaml
# English Content
Name: "English Assets"
Filter: PathFilter("Assets/Localization/EN/**")
Provider: PathAddressProvider(base: "Assets/Localization/EN")
Target Group: "Localization"
Labels: ["lang_en"]

# Spanish Content
Name: "Spanish Assets"
Filter: PathFilter("Assets/Localization/ES/**")
Provider: PathAddressProvider(base: "Assets/Localization/ES")
Target Group: "Localization"
Labels: ["lang_es"]

# Repeat for JP, FR...
```

3. **Language Selection**:
```csharp
string language = "lang_en"; // From settings
await AddressableManager.LoadAsync<TextAsset>(
    "ui_strings",
    labels: new[] { language }
);
```

### Quality Preset System

**Goal**: Organize textures/materials by quality tiers

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

2. **Quality Rules**:

```yaml
# Ultra Quality
Name: "4K Textures"
Filter: PathFilter("Assets/Textures/4K/**")
Labels: ["quality_ultra", "texture"]
Group: "Textures_Ultra"

# High Quality
Name: "2K Textures"
Filter: PathFilter("Assets/Textures/2K/**")
Labels: ["quality_high", "texture"]
Group: "Textures_High"

# Medium Quality
Name: "1K Textures"
Filter: PathFilter("Assets/Textures/1K/**")
Labels: ["quality_medium", "texture"]
Group: "Textures_Medium"

# Low Quality
Name: "512px Textures"
Filter: PathFilter("Assets/Textures/512/**")
Labels: ["quality_low", "texture"]
Group: "Textures_Low"
```

3. **Quality-Based Loading**:
```csharp
string quality = "quality_" + QualitySettings.GetQualityLevel();
await AddressableManager.LoadAsync<Texture2D>(
    textureName,
    labels: new[] { quality, "texture" }
);
```

---

## CI/CD Integration

### Command Line Interface

The Addressable Manager provides CLI commands for automation pipelines.

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
- `-layoutRuleAssetPath`: Path to LayoutRuleData asset (required)
- `-validateOnly`: Only validate, don't apply (default: false)
- `-warningAsError`: Treat warnings as errors (default: false)
- `-resultFilePath`: JSON report output path

**Exit Codes**:
- `0` = Success
- `1` = Validation errors
- `2` = Exception/fatal error

#### Validate Rules

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ValidateLayoutRules \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -errorLogFilePath "build_logs/validation_errors.txt"
```

#### Set Version Expression

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.SetVersionExpression \
  -layoutRuleAssetPath "Assets/Rules/MainRules.asset" \
  -versionExpression "[1.0.0,2.0.0)" \
  -excludeUnversioned true
```

#### Detect Conflicts

```bash
Unity.exe -quit -batchmode \
  -executeMethod AddressableManager.Editor.CLI.AddressableCLI.DetectConflicts \
  -reportFilePath "build_logs/conflicts.json"
```

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
    - uses: actions/checkout@v2

    - name: Setup Unity
      uses: game-ci/unity-builder@v2
      with:
        unityVersion: 2021.3.0f1

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

    - name: Build Addressables
      run: |
        unity-editor \
          -quit -batchmode \
          -executeMethod UnityEditor.AddressableAssets.Settings.AddressableAssetSettings.BuildPlayerContent

    - name: Upload Artifacts
      uses: actions/upload-artifact@v2
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
                  -executeMethod BuildScript.BuildAddressables
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

**2. Document Your Rules**
```
Always fill in the Description field with:
- What assets it matches
- Why this configuration is needed
- Any special considerations
```

**3. Prioritize Correctly**
```
Higher priority (200+): Specific overrides
Medium priority (100): General rules
Lower priority (50-): Fallback/default rules
```

**4. Group Related Rules**
```
Create separate LayoutRuleData assets for:
- Different content types (UI, Audio, Models)
- Different platforms
- Base game vs DLC
```

### Performance Optimization

**1. Efficient Filter Chains**
```
✅ Fast: PathFilter → ExtensionFilter → TypeFilter
❌ Slow: DependentObjectFilter → FindAssetsFilter

Order filters from most restrictive to least restrictive
```

**2. Limit Preview Scope**
```
Use Preview Panel's limit slider (default: 50)
Don't preview rules that match thousands of assets
```

**3. Batch Apply**
```
Apply all rules at once rather than one-by-one
Use CLI for bulk operations in builds
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
1. Use Preview Panel to verify matches
2. Use "Validate" before "Apply"
3. Test in a separate branch first
4. Keep backups of addressable settings
```

**3. Regular Audits**
```
Monthly:
- Run conflict detection
- Review unused rules
- Update documentation
- Check for deprecated patterns
```

### Team Collaboration

**1. Share Templates**
```
Export working rule sets as JSON templates
Store in shared repository
Document template usage in README
```

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
1. Check filter paths are correct (case-sensitive on some platforms)
2. Verify asset type matches TypeFilter
3. Test filters individually
4. Check file extensions match ExtensionFilter
5. Ensure assets aren't excluded by parent filter

#### Issue: Wrong Address Generated

**Symptoms**: Assets have unexpected addresses

**Solutions**:
1. Check provider configuration
2. Verify base path settings in PathAddressProvider
3. Test provider logic with known assets
4. Check for conflicting rules with higher priority
5. Disable other rules temporarily to isolate issue

#### Issue: Labels Not Applying

**Symptoms**: Assets missing expected labels

**Solutions**:
1. Verify asset is addressable first (labels only apply to addressable assets)
2. Check LabelRule priority order
3. Ensure AppendToExisting is enabled if keeping existing labels
4. Check provider is generating non-empty label list
5. Verify labels are enabled in rule

#### Issue: Version Tags Missing

**Symptoms**: No `version:X.X.X` labels on assets

**Solutions**:
1. Check VersionProvider is configured correctly
2. For GitCommitVersionProvider, ensure .git folder accessible
3. For BuildNumberVersionProvider, check PlayerSettings values
4. Verify VersionRule is enabled
5. Check version expression filter isn't excluding assets

### Validation Errors

#### Error: "Null filter in rule"

**Cause**: Filter slot is empty

**Fix**: Assign a filter asset or remove the empty slot

#### Error: "Provider not set"

**Cause**: No provider assigned to rule

**Fix**: Create and assign appropriate provider

#### Error: "Invalid version expression"

**Cause**: Malformed version expression syntax

**Fix**: Use valid formats: `[1.0.0,2.0.0)`, `>=1.5.0`, `1.0.0`

#### Error: "Circular dependency detected"

**Cause**: DependentObjectFilter creates circular reference

**Fix**: Review dependency chain, break circular reference

### Performance Issues

#### Issue: Slow Rule Application

**Symptoms**: ApplyRules takes many minutes

**Solutions**:
1. Reduce number of active rules
2. Use more specific filters to reduce matching overhead
3. Avoid DependentObjectFilter on large asset sets
4. Disable verbose logging
5. Consider splitting rules across multiple LayoutRuleData assets

#### Issue: Editor Lag in Rule Editor

**Symptoms**: UI becomes unresponsive

**Solutions**:
1. Reduce preview limit to 20-30
2. Don't keep preview refreshing continuously
3. Close other heavy editor windows
4. Split complex rule sets into smaller ones

---

## Summary

The Addressable Automation System provides:

✅ **Rule-Based Workflow**: Define once, apply everywhere
✅ **8 Filter Types**: Match assets precisely
✅ **Multiple Providers**: Flexible output generation
✅ **Visual Tools**: Rule Editor, Preview Panel, Layout Viewer
✅ **CI/CD Integration**: Command-line tools for automation
✅ **Team Collaboration**: JSON templates and version control
✅ **Version Management**: Track assets across builds

**Next Steps**:
1. Read [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md) for practical examples
2. Browse [Templates directory](../Packages/com.game.addressables/Editor/Templates/) for starter rule sets
3. Join the community and share your automation workflows!

**Related Documentation**:
- [EDITOR_TOOLS_GUIDE.md](EDITOR_TOOLS_GUIDE.md) - Editor window details
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common problems and solutions
- [API_REFERENCE.md](API_REFERENCE.md) - Runtime API documentation

---

**Questions or Issues?**
Report at: https://github.com/your-org/addressable-manager/issues

**Version**: 3.5.0 | **License**: MIT | **Unity**: 2021.3+
