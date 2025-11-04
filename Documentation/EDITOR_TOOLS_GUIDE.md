# Editor Tools Guide

**Complete reference for all editor windows and tools**

Version 3.5.0 | Unity 2021.3+

---

## Table of Contents

1. [Overview](#overview)
2. [Addressable Manager Dashboard](#addressable-manager-dashboard)
3. [Layout Rule Editor](#layout-rule-editor)
4. [Layout Viewer](#layout-viewer)
5. [Debug Settings Window](#debug-settings-window)
6. [Menu Items & Shortcuts](#menu-items--shortcuts)

---

## Overview

The Addressable Manager provides five main editor tools:

| Tool | Purpose | Shortcut |
|------|---------|----------|
| Dashboard | Real-time monitoring and performance | `Ctrl+Alt+A` |
| Layout Rule Editor | Create and manage automation rules | - |
| Layout Viewer | Visualize addressable layout | - |
| Debug Settings | Configure runtime behavior | - |

---

## Addressable Manager Dashboard

**Path**: `Window > Addressable Manager > Dashboard`
**Shortcut**: `Ctrl+Alt+A` (Windows) / `Cmd+Alt+A` (Mac)

The dashboard provides real-time monitoring of addressable assets during play mode.

### Tabs

#### 1. Assets Tab

**Purpose**: View all currently loaded assets

**Features**:
- **Search Bar**: Filter assets by name or type
- **Scope Filter**: Show assets from specific scopes (Global, Session, Scene, Hierarchy)
- **Asset Count**: Total number of loaded assets
- **Asset Details**:
  - Address
  - Type
  - Scope
  - Load time
  - Reference count
  - Memory size

**Usage**:
```
1. Enter play mode
2. Open Dashboard
3. Switch to Assets tab
4. Search or filter as needed
5. Click asset to ping in project
```

**Performance Tips**:
- Use search to narrow down large lists
- Filter by scope to debug specific systems
- Monitor reference counts to detect leaks

---

#### 2. Performance Tab

**Purpose**: Monitor system performance metrics

**Features**:
- **Quick Stats**:
  - Total Assets: Number of tracked assets
  - Cache Hit Ratio: Percentage of cache hits
  - Total Memory: Memory used by addressables
  - Avg Load Time: Average asset load duration

- **Memory Graph**: Real-time memory usage visualization
  - Shows total, cached, and active memory over time
  - Peak and average memory statistics
  - Threshold indicators (warning/critical)
  - Scrolling 5-minute window
  - Auto-scaling or fixed scale

- **Slowest Assets List**: Top assets by load time
  - Asset address and type
  - Average load duration in milliseconds
  - Useful for identifying bottlenecks

- **Export Report**: Save performance data to CSV
  - Full asset list with metrics
  - Load time statistics
  - Memory usage breakdown
  - Suitable for analysis in Excel/Google Sheets

**Usage**:
```
1. Enter play mode
2. Open Dashboard > Performance tab
3. Memory graph updates automatically
4. Click "Export Report" to save data
5. Analyze slowest assets to optimize
```

**Memory Graph Controls**:
- Graph shows last 300 samples (5 minutes at 1 sample/second)
- Red line: Critical memory threshold (100MB default)
- Orange line: Warning threshold (50MB default)
- Blue area: Total addressable memory
- Auto-scales by default, can configure thresholds

---

#### 3. Scopes Tab

**Purpose**: Manage asset scopes and lifecycles

**Features**:
- **Per-Scope View**: Separate foldouts for each scope
  - Global: Assets that live forever
  - Session: Assets that persist across scenes
  - Scene: Assets tied to current scene
  - Hierarchy: Assets tied to GameObjects

- **Scope Statistics**:
  - Asset count
  - Total memory usage

- **Asset Lists**: Per-scope asset details
  - Address, type, reference count

- **Cleanup Buttons**: Release all assets in a scope
  - Per-scope cleanup
  - Cleanup all scopes at once

**Usage**:
```
# Monitor scene-specific assets
1. Open Dashboard > Scopes tab
2. Expand "Scene" foldout
3. Load scene
4. Watch assets appear
5. Unload scene
6. Assets auto-released

# Manual cleanup
1. Select scope (e.g., Session)
2. Click "Cleanup Session"
3. Confirm dialog
4. Assets in that scope released
```

**Best Practices**:
- Use Scene scope for level-specific assets
- Use Session scope for UI/systems that persist
- Use Global scope sparingly (never auto-cleaned)
- Monitor Hierarchy scope for GameObject-tied assets

---

#### 4. Settings Tab

**Purpose**: Configure dashboard and runtime behavior

**Features**:
- **Log Level**: Control debug verbosity
  - None: No logging
  - Errors Only: Only log errors
  - Warnings and Errors: Default level
  - All: Verbose logging (includes info)

- **Auto-Refresh**: Enable/disable automatic updates
  - Toggle on/off
  - Refresh interval slider (100ms - 5000ms)
  - Recommended: 500ms for good balance

- **Simulation Settings**: Test error scenarios
  - Simulate Slow Loading: Add artificial delay
  - Delay slider: 0-5000ms
  - Failure Rate: Simulate random load failures (0-100%)

- **Actions**:
  - Reset Settings: Restore defaults
  - Reset Statistics: Clear performance data

**Usage**:
```
# Reduce overhead
1. Go to Settings tab
2. Set Auto-Refresh interval to 1000ms or higher
3. Or disable Auto-Refresh entirely

# Test error handling
1. Enable "Simulate Slow Loading"
2. Set delay to 1000ms
3. Set failure rate to 10%
4. Test loading code with delays/failures
```

---

## Layout Rule Editor

**Path**: `Window > Addressable Manager > Layout Rule Editor`

The Rule Editor is the primary tool for creating and managing addressable automation rules.

### Layout

The window is divided into three panels:

```
┌─────────────────────────────────────────────────┐
│ Toolbar (Rule Data, Actions)                    │
├──────────┬──────────────────┬───────────────────┤
│ Rule     │ Configuration    │ Preview           │
│ List     │ Panel            │ Panel             │
│ (Left)   │ (Center)         │ (Right)           │
│          │                  │                   │
│ • Rules  │ Name             │ Matched Assets:   │
│ • Priority│ Description      │ • asset1.png      │
│ • Type   │ Filters          │ • asset2.png      │
│          │ Provider         │ • ...             │
│          │ Settings         │                   │
│          │                  │ Stats: 45/1000    │
└──────────┴──────────────────┴───────────────────┘
```

---

### Toolbar

**Rule Data Selector**:
- Select which LayoutRuleData asset to edit
- Dropdown shows all LayoutRuleData assets in project
- Switching rules clears selection

**Action Buttons**:
- **Validate**: Check rules for errors
  - Shows validation summary dialog
  - Lists all warnings and errors
  - Recommended before applying

- **Apply All**: Execute all rules
  - Applies address, label, and version rules
  - Shows progress bar
  - Displays results summary
  - Cannot undo - recommend backup first

- **Import**: Load rules from JSON
  - Select template or exported rules
  - Choose merge or replace mode
  - Merge: Add rules to existing set
  - Replace: Clear existing rules first

- **Export**: Save rules to JSON
  - Choose output location
  - Creates shareable template
  - Version controlled format
  - Can import back later

---

### Left Panel: Rule List

**Rule Type Tabs**:
- Address: Rules that assign addresses and groups
- Label: Rules that add metadata labels
- Version: Rules that apply version tags

**Rule Items**:
Each rule shows:
- Rule name (click to select)
- Number of filters
- Priority value
- Enabled/disabled state (visual indicator)

**Controls**:
- **+ Add Rule**: Create new rule of current type
- **- Remove**: Delete selected rule (with confirmation)

**Sorting**:
- Rules displayed in priority order (highest first)
- Higher priority rules process first
- Use priority to control override behavior

---

### Center Panel: Configuration

**When rule selected, shows**:

#### Basic Settings
- **Rule Name**: Descriptive identifier
- **Enabled**: Toggle rule on/off without deleting
- **Description**: Documentation for team members
- **Priority**: Processing order (0-1000, higher first)

#### Address Rule Settings
- **Target Group Name**: Which addressable group
  - Group created if doesn't exist
  - Use consistent names
- **Skip Existing**: Preserve manual addresses
  - true: Don't override existing addresses
  - false: Apply rule even if address set

#### Label Rule Settings
- **Append To Existing**: Keep or replace labels
  - true: Add labels to existing set
  - false: Replace all labels

#### Version Rule Settings
- **Skip Existing**: Preserve manual versions
  - true: Don't override existing versions
  - false: Apply rule even if version set

#### Filters Section
- List of filters (AND logic - all must pass)
- **Add/Remove** buttons for each filter slot
- Drag-and-drop filter assets from project
- Empty slots ignored

**Common Filter Assets**:
- PathFilter: Match by file path
- TypeFilter: Match by Unity type
- ExtensionFilter: Match by file extension
- AddressFilter: Match existing addresses
- AddressableGroupFilter: Match by group
- ObjectFilter: Match specific assets
- FindAssetsFilter: Search using AssetDatabase
- DependentObjectFilter: Match dependencies

#### Provider Section
- Single provider per rule
- Generates the output (address/labels/version)
- Drag-and-drop from project

**Address Providers**:
- FileNameAddressProvider: Use asset filename
- PathAddressProvider: Use relative path

**Label Providers**:
- ConstantLabelProvider: Fixed label(s)
- FolderLabelProvider: Label from folder name

**Version Providers**:
- ConstantVersionProvider: Fixed version
- BuildNumberVersionProvider: From Unity build settings
- GitCommitVersionProvider: From git commit/tag
- DateVersionProvider: From timestamp

---

### Right Panel: Preview

**Purpose**: See which assets match the selected rule before applying

**Features**:
- **Preview Limit Slider**: Control sample size (10-200)
  - Lower limit for fast preview
  - Higher limit for comprehensive check

- **Refresh Preview Button**: Generate preview
  - Scans project for matching assets
  - Shows first N matches
  - Displays generated output

- **Statistics**: Shows X of Y matched
  - X: Number shown in preview
  - Y: Total matching assets

- **Asset List**: Each item shows:
  - Asset filename (clickable - pings in project)
  - Full asset path
  - Generated address/label/version
  - Error message if generation failed

**Usage**:
```
1. Select a rule from left panel
2. Adjust preview limit if needed (default: 50)
3. Click "Refresh Preview"
4. Wait for preview generation
5. Review matched assets
6. Check generated outputs are correct
7. If wrong, adjust filters/provider
8. Refresh preview again
9. When satisfied, click "Apply All"
```

**Performance Tips**:
- Use lower limit (20-30) for quick checks
- Increase limit when finalizing rules
- Preview can be slow with 1000+ matches
- Consider splitting rules if preview too slow

---

## Layout Viewer

**Path**: `Window > Addressable Manager > Layout Viewer`

Visualize the complete addressable layout and detect conflicts.

### Features

#### Asset Search
- **Search Box**: Filter by address, type, path
- **Type Filter**: Show only specific asset types
- **Group Filter**: Show only specific groups
- **Clear Filters**: Reset all filters

#### Asset Tree View
Displays all addressable assets in hierarchical view:
- **Group Level**: Addressable groups
- **Asset Level**: Individual assets
  - Address
  - Type
  - Path
  - Labels
  - Version
  - File size
  - Reference count (runtime)

#### Inspector Panel
When asset selected:
- Full asset details
- Applied rules (which rules configured this asset)
- Dependencies (what this asset references)
- Referrers (what references this asset)
- Validation status

#### Conflict Detection
- **Detect Conflicts Button**: Scan for issues
  - Duplicate addresses
  - Missing dependencies
  - Orphaned assets
  - Invalid paths

- **Conflict Report**:
  - Conflict type
  - Affected assets
  - Severity (warning/error)
  - Suggested fixes

### Usage

**Verify Rule Application**:
```
1. Apply rules in Rule Editor
2. Open Layout Viewer
3. Search for specific assets
4. Verify addresses match expectations
5. Check labels are correct
6. Confirm version tags applied
```

**Debug Conflicts**:
```
1. Open Layout Viewer
2. Click "Detect Conflicts"
3. Review conflict list
4. For each conflict:
   - Note affected assets
   - Review rule priorities
   - Adjust filters or priorities
   - Reapply rules
5. Detect conflicts again
6. Repeat until clean
```

**Audit Addressable Layout**:
```
1. Open Layout Viewer
2. Expand all groups
3. Review asset organization
4. Check for:
   - Consistent naming
   - Proper grouping
   - Complete labeling
   - Version coverage
5. Export report if needed
```

---

## Debug Settings Window

**Path**: `Window > Addressable Manager > Debug Settings`

Configure runtime debugging and simulation behavior.

### Settings

#### Logging
- **Enable Console Logging**: Toggle all logging
- **Log Level**: Verbosity control
- **Log Timestamps**: Add timestamps to logs
- **Log Stack Traces**: Include call stacks

#### Simulation
- **Simulate Slow Network**: Add latency
- **Network Delay (ms)**: 0-5000ms
- **Simulate Failures**: Random load failures
- **Failure Rate (%)**: 0-100%
- **Fail After N Successes**: Periodic failures

#### Validation
- **Strict Mode**: Extra runtime checks
- **Validate On Load**: Check assets on load
- **Check Dependencies**: Verify dependencies exist
- **Warn On Missing**: Log missing assets

#### Performance
- **Track All Assets**: Enable full tracking
- **Record Load Times**: Measure durations
- **Profile Memory**: Track memory usage
- **Sample Interval (ms)**: Metric collection rate

### Presets

**Development**:
- Full logging
- All validation
- Performance tracking
- No simulation

**Testing**:
- Moderate logging
- Validation enabled
- Simulate slow network (500ms)
- 5% failure rate

**Production**:
- Errors only
- No validation (performance)
- No simulation
- Minimal tracking

---

## Menu Items & Shortcuts

### Window Menu

```
Window > Addressable Manager >
├─ Dashboard (Ctrl+Alt+A)
├─ Layout Rule Editor
├─ Layout Viewer
├─ Debug Settings
└─ [Submenu: Examples]
   ├─ Basic UI Rules
   ├─ Multi-Platform Setup
   └─ DLC Configuration
```

### Assets Menu

```
Assets > Addressable Manager >
├─ Create Rule Data
├─ Create Composite Rule Data
├─ Create Path Filter
├─ Create Type Filter
├─ Create Extension Filter
├─ Create File Name Provider
├─ Create Path Provider
├─ Create Constant Label Provider
├─ Create Folder Label Provider
├─ Create Constant Version Provider
├─ Create Build Number Version Provider
├─ Create Git Commit Version Provider
└─ Create Date Version Provider
```

### Context Menu (Right-Click)

On LayoutRuleData:
- Apply Rules
- Validate Rules
- Export to JSON
- Duplicate

On Filters/Providers:
- Test on Selection
- Edit Settings
- Duplicate

### Keyboard Shortcuts

| Shortcut | Action | Context |
|----------|--------|---------|
| `Ctrl+Alt+A` | Open Dashboard | Global |
| `Ctrl+R` | Refresh Preview | Rule Editor |
| `Ctrl+Shift+V` | Validate Rules | Rule Editor |
| `Ctrl+Shift+A` | Apply Rules | Rule Editor |
| `F5` | Refresh | Layout Viewer |
| `Ctrl+F` | Focus Search | Layout Viewer |

---

## Tips & Tricks

### Rule Editor

**Tip 1: Use Descriptive Names**
```
❌ "Rule 1", "Test", "Temp"
✅ "UI Sprites by Filename", "Character Prefabs"
```

**Tip 2: Document Rules**
```
Always fill in Description field:
- What it matches
- Why it exists
- Any special considerations
```

**Tip 3: Test with Preview**
```
1. Create rule
2. Preview (low limit)
3. Verify matches
4. Adjust if needed
5. Preview (high limit)
6. Apply rules
```

**Tip 4: Use Validation**
```
Run validation before applying:
- Catches configuration errors
- Prevents broken rules
- Shows warnings
```

### Layout Viewer

**Tip 1: Regular Audits**
```
Weekly:
- Open Layout Viewer
- Run conflict detection
- Review any issues
- Clean up unused assets
```

**Tip 2: Track Changes**
```
Before major changes:
- Export current layout
- Apply changes
- Compare layouts
- Verify expected changes only
```

**Tip 3: Use Filters**
```
Narrow down large projects:
- Filter by group for area-specific review
- Filter by type for asset-class audit
- Search for specific patterns
```

### Dashboard

**Tip 1: Monitor During Testing**
```
Keep Dashboard open during play sessions:
- Watch memory usage
- Track slow assets
- Identify leaks early
```

**Tip 2: Export Reports**
```
Before optimization:
- Export baseline report
After optimization:
- Export new report
- Compare metrics
- Quantify improvements
```

**Tip 3: Adjust Refresh Rate**
```
High refresh (100-200ms): Detailed monitoring
Medium refresh (500ms): General use
Low refresh (1000ms+): Reduce overhead
```

---

## Troubleshooting

### Rule Editor Not Showing Assets

**Check**:
1. Is LayoutRuleData selected?
2. Are filters configured correctly?
3. Do assets exist at filter paths?
4. Is preview limit too low?

**Solution**: See [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

### Dashboard Shows No Data

**Check**:
1. Are you in play mode?
2. Have any assets been loaded?
3. Is auto-refresh enabled?
4. Check console for errors

### Preview Generation Slow

**Causes**:
- Too many assets matched
- Complex filter chains
- Large project size

**Solutions**:
- Reduce preview limit
- Make filters more specific
- Split rules into smaller sets

### Memory Graph Not Updating

**Causes**:
- Not in play mode
- No addressable assets loaded
- Auto-refresh disabled

**Solutions**:
- Enter play mode
- Load some assets
- Check Settings > Auto-Refresh

---

## Related Documentation

- [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md) - Automation system details
- [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md) - Practical examples
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common issues and solutions
- [API_REFERENCE.md](API_REFERENCE.md) - Runtime API documentation

---

**Version**: 3.5.0 | **Last Updated**: January 2025 | **Unity**: 2021.3+
