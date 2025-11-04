# Addressable Rule Templates

This directory contains template JSON files that demonstrate common use cases for the Addressable Manager rule system.

## Available Templates

### 1. BasicAddressRules.json
**Purpose:** Simple address assignment patterns
**Use Case:** Getting started with addressable automation
**Features:**
- Filename-based addresses for UI sprites
- Path-based addresses for prefabs
- Basic audio clip addressing

**When to use:** Starting a new project or learning the rule system

---

### 2. PlatformSpecificRules.json
**Purpose:** Platform-conditional asset loading
**Use Case:** Multi-platform games with platform-specific assets
**Features:**
- Separate groups for high-quality and mobile assets
- Platform labels (PC, Mobile, Android, iOS)
- Conditional loading based on platform

**When to use:** Building for multiple platforms with different quality tiers

---

### 3. VersionedAssetsRules.json
**Purpose:** Asset versioning for content updates
**Use Case:** Live service games with remote content updates
**Features:**
- Core vs DLC content separation
- Static vs dynamic content labeling
- Build number, git commit, and date versioning
- Version expression filtering

**When to use:** Games with downloadable content or remote asset updates

---

### 4. MaterialTextureRules.json
**Purpose:** Organizing materials and textures by quality
**Use Case:** Projects with extensive art assets and quality levels
**Features:**
- Separate groups for character/environment materials
- Quality-based texture grouping (High/Normal/Low)
- PBR material labeling
- Texture type labels (Diffuse, Normal, etc.)

**When to use:** Art-heavy projects with multiple quality presets

---

### 5. ComprehensiveRules.json
**Purpose:** Complete demonstration of all features
**Use Case:** Reference for building complex rule sets
**Features:**
- All rule types (Address, Label, Version)
- Multiple address providers
- Extensive labeling system
- Version filtering
- Platform and localization rules

**When to use:** Reference for advanced setups or learning all features

---

## How to Use Templates

### Method 1: Import via Layout Rule Editor

1. Open `Window > Addressable Manager > Layout Rule Editor`
2. Create or select a LayoutRuleData asset
3. Click the `Import` button in the toolbar
4. Select a template JSON file
5. Choose import mode:
   - **Replace:** Removes existing rules and imports template
   - **Merge:** Keeps existing rules and adds template rules

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

### Method 3: Import via CLI (CI/CD)

```bash
# During build process
Unity -batchmode -quit -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ImportRules \
  -layoutRuleAssetPath "Assets/Rules/Main.asset" \
  -importFilePath "Packages/com.game.addressables/Editor/Templates/VersionedAssetsRules.json" \
  -mergeMode true
```

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
  "autoApplyOnImport": true,
  "addressRules": [...],
  "labelRules": [...],
  "versionRules": [...]
}
```

---

## Creating Custom Templates

You can create your own templates:

1. Set up rules in the Layout Rule Editor
2. Click `Export` to save as JSON
3. Share the JSON file with your team
4. Store in version control for CI/CD

### Best Practices

- **Use descriptive names:** Make rule names clear and purpose-driven
- **Set appropriate priorities:** Higher priority rules match first
- **Document your templates:** Add clear descriptions to all rules
- **Test before committing:** Use the Preview Panel to verify matches
- **Version your templates:** Keep templates in version control

---

## Template Compatibility

- **Version:** 1.0
- **Minimum Package Version:** 3.5.0
- **Unity Version:** 2021.3+
- **Addressables Version:** 2.3.1+

### Important Notes

⚠️ **Filter and Provider Paths:** Templates reference filter and provider assets by path. Ensure these assets exist in your project, or recreate them manually.

⚠️ **Group Names:** Templates reference addressable group names. If groups don't exist, they will be created automatically when rules are applied.

⚠️ **Merge Mode:** When merging templates, duplicate rule names will skip the import. Rename rules if you need both versions.

---

## Troubleshooting

### Issue: "Filter not found" warnings during import

**Solution:** Create the required filter assets manually or modify the template to remove filter references.

### Issue: Provider not working after import

**Solution:** Templates can't serialize provider configuration. Create provider assets in your project and update rule references.

### Issue: Rules not matching expected assets

**Solution:** Use the Preview Panel in Layout Rule Editor to see what each rule matches. Adjust filter criteria as needed.

---

## Additional Resources

- **Documentation:** `Packages/com.game.addressables/Documentation/`
- **Rule System Guide:** `RULE_SYSTEM_EXAMPLES.md`
- **Automation Guide:** `ADDRESSABLE_AUTOMATION_GUIDE.md`
- **Troubleshooting:** `TROUBLESHOOTING.md`

For more information, see the main package README.
