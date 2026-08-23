# Addressable Rule Templates

This directory contains template JSON files that demonstrate common use cases for the Addressable Manager rule system.

## Available Templates

### 1. BasicAddressRules.json
**Purpose:** Simple address assignment patterns
**Use Case:** Getting started with addressable automation
**Contents:** three address rules, no label or version rules
- `UI Sprites by Filename` — assets under `Assets/UI/` → group `UI`, addressed by `FileNameAddressProvider`
- `Prefabs by Path` — assets under `Assets/Prefabs/` → group `Prefabs`, addressed by `PathAddressProvider`
- `Audio by Filename` — assets under `Assets/Audio/` → group `Audio`, addressed by `FileNameAddressProvider`

This is the only template that references filter and provider **assets by path**. Those assets ship
next to it in `Editor/Templates/BasicAddressRules/` (`UISpritesPathFilter`, `PrefabsPathFilter`,
`AudioPathFilter`, `FileNameAddressProvider`, `PathAddressProvider`). Importing it wires your rules
to those shipped assets directly — editing one changes it for every rule set that imported the
template. The three path filters use Match Mode `StartsWith`, so they match on a literal folder
prefix.

**When to use:** Starting a new project or learning the rule system

---

### 2. PlatformSpecificRules.json
**Purpose:** Platform-conditional asset loading
**Use Case:** Multi-platform games with platform-specific assets
**Contents:** two address rules, four label rules
- Address rules put `Assets/**/HighQuality/**` into group `HighQuality` and `Assets/**/Mobile/**`
  into group `Mobile`, both addressed by `PathAddressProvider`
- Label rules add the constant labels `platform_pc`, `platform_mobile`, `platform_android` and
  `platform_ios` via `ConstantLabelProvider`

The `platform_android` and `platform_ios` rules are filtered on `Assets/**` — every asset in the
project. Narrow them before applying.

**When to use:** Building for multiple platforms with different quality tiers

---

### 3. VersionedAssetsRules.json
**Purpose:** Asset versioning for content updates
**Use Case:** Live service games with remote content updates
**Contents:** two address rules, two label rules, three version rules
- Core vs DLC content separation (`Assets/**` → `CoreAssets`, `Assets/DLC/**` → `DLC`)
- `content_static` / `content_dynamic` labels via `ConstantLabelProvider`
- `BuildNumberVersionProvider` (enabled), `GitCommitVersionProvider` and `DateVersionProvider`
  (both shipped disabled)
- Sets `versionExpression` to `[1.0.0,2.0.0)` and turns on `verboseLogging`

**When to use:** Games with downloadable content or remote asset updates

---

### 4. MaterialTextureRules.json
**Purpose:** Organizing materials and textures by quality
**Use Case:** Projects with extensive art assets and quality levels
**Contents:** five address rules, six label rules
- Materials (`.mat`) into `Materials`; textures (`.png,.jpg,.jpeg,.tga,.psd`) into `Textures_High`,
  `Textures_Normal` and `Textures_Low`
- `material_pbr`, `texture_diffuse`, `texture_normal`, `quality_high/medium/low` labels

The three texture rules use the **same** extension filter, and the two material rules use the same
one as each other, so the High/Normal/Low split is a placeholder — the rules do not distinguish
resolution. An asset is addressed by the **first** matching rule in descending priority order and by
no other, so as shipped `High Res Textures` (priority 90) takes every texture and
`Character Materials` (110) takes every material. Give each rule its own filter before applying.

**When to use:** Art-heavy projects with multiple quality presets

---

### 5. ComprehensiveRules.json
**Purpose:** Complete demonstration of all features
**Use Case:** Reference for building complex rule sets
**Contents:** five address rules, eight label rules, three version rules
- All three rule types in one file
- Both address providers (`FileNameAddressProvider`, `PathAddressProvider`)
- Constant labels including localization (`lang_*`)
- `versionExpression` `[1.0.0,)` with `excludeUnversioned` on

**When to use:** Reference for advanced setups or learning all features

---

## How to Use Templates

### Method 1: Import via Layout Rule Editor

1. Open `Window > Addressable Manager > Layout Rule Editor`
2. Create or select a LayoutRuleData asset (`Assets > Create > Addressable Manager > Layout Rule Data`)
3. Click the `Import` button in the toolbar
4. Select a template JSON file — the file panel opens in `Assets/`, so navigate up to
   `Packages/com.game.addressables/Editor/Templates/`
5. Choose import mode in the dialog:
   - **Replace:** Removes existing rules, then imports the template *and* its file-level settings
     (description, auto-apply flags, verbose logging, version expression)
   - **Merge:** Keeps existing rules and appends the template's rules; file-level settings are left
     alone
6. Click `Validate` in the toolbar before `Apply All` — see **Before you apply** below

### Method 2: Import via Code

```csharp
using AddressableManager.Editor.Rules;

// Load your LayoutRuleData
var ruleData = AssetDatabase.LoadAssetAtPath<LayoutRuleData>("Assets/MyRules.asset");

// Import template (replace mode)
RuleSerializer.ImportFromJson(ruleData, "path/to/template.json", mergeMode: false);

// Or merge with existing rules
RuleSerializer.ImportFromJson(ruleData, "path/to/template.json", mergeMode: true);
```

`ImportFromJson` returns `false` if any rule failed to import cleanly. It still writes the rules it
could build — check the Console for `[RuleSerializer]` errors rather than treating `false` as
"nothing happened".

### Method 3: Import via CLI (CI/CD)

```bash
# During build process - import rules from a JSON template
Unity -batchmode -quit -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ImportRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset" \
  -importFilePath "Packages/com.game.addressables/Editor/Templates/VersionedAssetsRules.json" \
  -mergeMode true
```

The ImportRules method accepts the following parameters:
- `-layoutRuleAssetPath`: Path to the LayoutRuleData asset to import into (required)
- `-importFilePath`: Path to the JSON template file to import from (required)
- `-mergeMode`: If true, merges imported rules with existing rules; if false, replaces all rules (optional, defaults to false)

Exit codes: `0` success, `1` the import reported a failure, `2` a missing/invalid argument, a
missing file, a LayoutRuleData that could not be loaded, or an unhandled exception. All CLI entry
points also exit `1` when script compilation has failed.

---

## Before you apply

`LayoutRuleProcessor` validates the **whole** rule set before it does anything, and a single invalid
rule aborts the entire run — no addresses, no labels, no versions. Disabled rules are validated too.
A rule is invalid if it has no filters, has a null filter, or has no provider.

Two consequences that hit imported templates directly:

⚠️ **Label-rule and version-rule filters do not survive a path-less import.** `RuleSerializer`
resolves *address*-rule filters by asset path **and** by type name, but label-rule and version-rule
filters are resolved by `filterAssetPath` only. Every label and version rule in
PlatformSpecificRules, VersionedAssetsRules, MaterialTextureRules and ComprehensiveRules ships with
an empty `filterAssetPath`, so those rules import with **zero filters** — while the import dialog
reports success. Their providers do resolve. After importing one of these four templates, open the
Layout Rule Editor and either assign a filter asset to each label/version rule or delete the rules
you do not want, then run `Validate`. BasicAddressRules is unaffected: it has no label or version
rules, and its address filters resolve by path.

⚠️ **A degraded address rule is imported disabled.** If an address rule loses a filter or its
provider, the importer keeps the rule, sets `Enabled = false`, logs an error naming it, and counts
it as a failure. Dropping a filter would *widen* what the rule matches (filters are ANDed), so it is
disarmed rather than run.

⚠️ **No template assigns a Group Template.** When a rule has to create its target group, and no
`AddressableAssetGroupTemplate` is assigned, the group's schemas are copied from the DefaultGroup —
in a stock project that means PackTogether with local build/load paths — and `AddressRule` logs a
warning saying so. If a group is meant to be remote or label-split, assign a Group Template to the
rule before applying.

⚠️ **Group names are normalized.** `/` and `\` in a target group name become `-`, because that is
what `AddressableAssetSettings.CreateGroup` does. A rule targeting `Icons/Small` produces a group
named `Icons-Small`.

---

## Customizing Templates

Templates are regular JSON files that you can customize:

1. Export your current rules: `Layout Rule Editor > Export`
2. Edit the JSON file with your preferred text editor
3. Modify rule properties, add/remove rules
4. Import the modified template back

### Template Structure

```json
{
  "version": "1.0",
  "description": "Template description",
  "autoApplyOnImport": false,
  "autoApplyOnModified": false,
  "verboseLogging": false,
  "versionExpression": "",
  "excludeUnversioned": false,
  "addressRules": [],
  "labelRules": [],
  "versionRules": []
}
```

`version` is written on export and carried on import, but nothing checks it.

An address rule looks like this:

```json
{
  "ruleName": "Prefabs by Path",
  "description": "Assign prefabs using their full path as address",
  "enabled": true,
  "priority": 90,
  "skipExisting": false,
  "targetGroupName": "Prefabs",
  "targetGroupTemplatePath": "",
  "filters": [
    {
      "filterType": "PathFilter",
      "filterAssetPath": "",
      "filterJson": "{\"_enabled\":true,\"_invert\":false,\"_description\":\"\",\"_pattern\":\"Assets/Prefabs/**\",\"_matchMode\":5,\"_caseSensitive\":false}"
    }
  ],
  "addressProviderType": "PathAddressProvider",
  "addressProviderPath": "",
  "addressProviderJson": ""
}
```

Label rules carry `appendToExisting` plus `labelProviderType` / `labelProviderPath` /
`labelProviderJson`; version rules carry `skipExisting` plus `versionProviderType` /
`versionProviderPath` / `versionProviderJson`. Both also carry a `filters` list, but see the
path-only warning above.

**Fields the format does not carry:** a rule's `Allow Group Move` flag and the rule data's
`Preserve Rule Order` flag are not exported, so an export/import round-trip returns them to their
defaults (`Allow Group Move` = true, `Preserve Rule Order` = false).

### How filters and providers are resolved

`RuleSerializer.ResolveOrCreate` tries, in order:

1. **`filterAssetPath` / `*ProviderPath`** — if the path loads an asset of the right type, that asset
   is used as-is. An asset you point at is never rewritten by an import; `filterJson` is ignored in
   this case.
2. **`filterType` / `*ProviderType`** — the type name is matched against every non-abstract type
   derived from the base (`AssetFilterBase`, `AddressProviderBase`, `LabelProviderBase`,
   `VersionProviderBase`). A new instance is created, `filterJson` / `*ProviderJson` is applied over
   it with `JsonUtility.FromJsonOverwrite`, and the instance is added as a **sub-asset of the rule
   data** so it persists and stays editable in the Inspector.

That is why a template no longer needs to ship `.asset` files, and why a rule set is portable
between projects: the JSON carries each filter's and provider's own serialized configuration inline.
If the configuration cannot be applied, the object is still created — at its default values — and a
`[RuleSerializer]` warning says so.

⚠️ **`_matchMode` is a `PathFilter` enum index.** `0` Contains, `1` StartsWith, `2` EndsWith,
`3` Exact, `4` Regex, `5` Glob — in that order, and `Glob` must stay last for serialization
compatibility. Glob is the only mode that understands `*`, `?` and `**`; in Contains/StartsWith/
EndsWith/Exact the pattern is compared with plain string operations, so a pattern containing `*`
matches **nothing**. The inline templates all use `"_matchMode":5`. If you retype a pattern in the
Inspector on a freshly created `PathFilter`, its Match Mode defaults to `Contains` — set it to
`Glob` yourself.

---

## Creating Custom Templates

You can create your own templates:

1. Set up rules in the Layout Rule Editor
2. Click `Export` to save as JSON
3. Share the JSON file with your team
4. Store in version control for CI/CD

### Best Practices

- **Use descriptive names:** Make rule names clear and purpose-driven
- **Set appropriate priorities:** Rules are processed in descending `priority` order, unless the
  rule data has `Preserve Rule Order` on (which `CompositeLayoutRuleData` sets from its
  `Respect Source Order` flag)
- **Document your templates:** Add clear descriptions to all rules
- **Test before committing:** Use the Preview panel to verify matches — it also lists duplicate
  addresses the rule set would create
- **Version your templates:** Keep templates in version control

---

## Template Compatibility

- **Format version:** 1.0
- **Package version:** 4.1.0 — the `filterJson` / `*ProviderJson` fields and type-name
  resolution are read by this version's `RuleSerializer`; older versions ignore them and fall back
  to paths only
- **Unity Version:** 2023.1+ (`package.json`)
- **Addressables Version:** 2.9.1

### Important Notes

⚠️ **Merge Mode:** Merging appends. There is no duplicate-name check in the importer, so importing
the same template twice in Merge mode gives you two copies of every rule. (`CompositeLayoutRuleData`
does deduplicate by name, but that is a different mechanism, applied when combining source assets.)

⚠️ **Group Names:** Templates reference addressable group names. Groups that do not exist are
created when rules are applied — read the Group Template warning above before relying on that.

---

## Troubleshooting

### Issue: "filter could not be resolved" errors during import

**Solution:** The path did not load and the type name did not match any filter type in the project.
Check the spelling of `filterType` against the eight built-in filters (`PathFilter`, `TypeFilter`,
`ExtensionFilter`, `AddressFilter`, `ObjectFilter`, `FindAssetsFilter`, `DependentObjectFilter`,
`AddressableGroupFilter`) or your own `AssetFilterBase` subclass. The rule is imported disabled.

### Issue: Apply does nothing and reports "Must have at least one filter"

**Solution:** A label or version rule imported without filters — see **Before you apply**. Assign a
filter asset to the rule, or remove the rule. Validation covers the whole rule set, so one bad rule
blocks every other rule too.

### Issue: A glob pattern matches nothing

**Solution:** The filter's Match Mode is not `Glob`. Set it in the Inspector, or `"_matchMode":5` in
the JSON.

### Issue: Rules not matching expected assets

**Solution:** Use the Preview panel in Layout Rule Editor to see what each rule matches. Adjust
filter criteria as needed.

---

## Additional Resources

- **CDN guide:** `Packages/com.game.addressables/Documentation/CDN_USAGE_GUIDE.md`
- **Troubleshooting:** `Packages/com.game.addressables/Documentation/TROUBLESHOOTING.md`
- **Editor tools:** `Packages/com.game.addressables/EDITOR_TOOLS_GUIDE.md`
- **Automation guide and rule examples:** `Documentation/ADDRESSABLE_AUTOMATION_GUIDE.md` and
  `Documentation/RULE_SYSTEM_EXAMPLES.md` in the repository — these live outside the package and are
  not installed with it

For more information, see the main package README.
