# Editor Tools Guide

**Complete reference for all editor windows and tools**

Version 4.1.0-pre.9 | Unity 2023.1+

---

## Table of Contents

1. [Overview](#overview)
2. [Addressable Manager Dashboard](#addressable-manager-dashboard)
3. [Layout Rule Editor](#layout-rule-editor)
4. [Layout Viewer](#layout-viewer)
5. [CDN Manager](#cdn-manager)
6. [Custom Inspectors](#custom-inspectors)
7. [Menu Items & Shortcuts](#menu-items--shortcuts)

---

## Overview

The package ships four editor windows:

| Tool | Purpose | Shortcut |
|------|---------|----------|
| Dashboard | Real-time monitoring and performance | `Ctrl+Alt+A` |
| Layout Rule Editor | Create and manage automation rules | - |
| Layout Viewer | Inspect the addressable layout and its conflicts | - |
| CDN Manager | Validate, build and inspect remote content | - |

Debug settings are a ScriptableObject (`Assets > Create > Addressable Manager > Debug Settings`), edited either in its own inspector or from the Dashboard's Settings tab. There is no separate Debug Settings window.

---

## Addressable Manager Dashboard

**Path**: `Window > Addressable Manager > Dashboard`
**Shortcut**: `Ctrl+Alt+A` (Windows) / `Cmd+Alt+A` (Mac)
**Minimum size**: 800×600

The dashboard provides real-time monitoring of addressable assets during play mode. Monitoring is Editor-only — `EditorAssetMonitor` lives in the Editor assembly, and the tracker is cleared when you exit Play Mode.

A one-line **CDN status strip** sits above the tabs: environment id, app version, cache size and network state while playing (`not in play mode` / `not initialised` otherwise), plus a button that opens the CDN Manager window. It refreshes once per second while the Dashboard is open.

### Tabs

#### 1. Active Assets

**Purpose**: View all currently tracked asset handles

**Features**:
- **Search field**: matches the address or the type name
- **Scope filter**: a dropdown rebuilt from the live scope ids as they appear (there is no fixed Global/Session/Scene/Hierarchy list — scene and hierarchy scope ids are instance-qualified, like `Scene-MainScene#h1234`, and are shown as `DisplayName (Category)`)
- **Asset count label**
- **Rows**, ordered newest-load-first, each rendered as two lines rather than a column grid:
  - address (bold)
  - `Type • <scope> Scope • Loaded <n> ago`
  - right-hand side: `Refs: <count>` and an estimated size in KB

**Usage**:
```
1. Enter play mode
2. Open Dashboard
3. Switch to Active Assets tab
4. Search or filter as needed
```

Rows are not clickable — there is no ping-in-project from this tab, and no sortable columns.

> **The memory figures are per-type constants, not measurements.** `AssetTrackerService.EstimateMemorySize` returns a fixed value per type name: Texture2D 1 MB, AudioClip 512 KB, GameObject 256 KB, Material 64 KB, Mesh 128 KB, ScriptableObject 32 KB, everything else 100 KB. Every MB figure in this window — rows, scope totals, the graph, the CSV export — is a proxy for asset count weighted by type. Use the Unity Profiler for real bytes.

**Performance Tips**:
- Use search to narrow down large lists
- Filter by scope to debug specific systems
- Monitor reference counts to detect leaks

---

#### 2. Performance

**Purpose**: Monitor system performance metrics

**Features**:
- **Stat cards**:
  - Total Assets
  - Cache Hit Ratio
  - Total Memory (estimated — see the caveat above)
  - Avg Load Time

- **Memory Graph**: built in code and inserted under the stat cards, with a summary label above it showing the latest total / cached / active figures plus `Peak` and `Avg` once samples exist.
  - Keeps the last 300 samples
  - Plots the **total** series only; cached and active appear in the summary text, not as lines
  - Threshold grid lines at 50 MB (warning) and 100 MB (critical)
  - Vertical scale auto-fits the tallest sample (minimum 10 MB, plus 20 % headroom)
  - One sample per Editor update tick while in Play Mode, so the time window it covers depends on the Editor's frame rate
  - Nothing in the UI exposes the thresholds, the sample count or the auto-scale flag — they are code defaults

- **Slowest Loading Assets**: address, type and average load time in ms

- **Export Report (CSV)**: opens a save-file panel and writes one row per recorded snapshot
  - Columns: `Timestamp, Total Memory (MB), Active Assets, Avg Load Time (s), Cache Hit Ratio`
  - Suitable for analysis in Excel / Google Sheets

**Usage**:
```
1. Enter play mode
2. Open Dashboard > Performance tab
3. Memory graph updates automatically
4. Click "Export Report (CSV)" to save data
5. Analyze slowest assets to optimize
```

---

#### 3. Scopes

**Purpose**: Inspect and clear asset scopes

**Features**:
- **Per-scope foldouts**, created lazily the first time a live scope id is reported to the tracker — Global, the `ScopeManager` "Session" entry, each `Scene-…` / `Hierarchy-…` scope, each `Hybrid:…` scope, and any custom scope you created. The header reads `DisplayName (Category Scope)`, or just `Category Scope` when no distinct display name was reported.
- **Scope statistics**: `Assets: N | Memory: X MB`, with `inactive` appended when the scope is deactivated
- **Asset list**: `address (Type) - Refs: N`
- **Cleanup buttons**: one per scope, plus **Cleanup All Scopes** in the tab toolbar

> **What Cleanup does depends on Play Mode.** In Play Mode it calls `ScopeManager.ClearScope`, releasing real handles, then clears the Dashboard's rows. Outside Play Mode there is no live loader, so it only clears the Dashboard's own rows — the confirmation dialog tells you which of the two you are about to get.

**Best Practices**:
- Use Scene scope for level-specific assets
- Use a `ScopeManager` session scope for UI/systems that persist across scenes
- Use Global scope sparingly (never auto-cleaned)
- Watch Hierarchy scopes for GameObject-tied assets

---

#### 4. Settings

**Purpose**: Configure the dashboard, and the `DebugSettings` asset it writes to

**Widgets**, in order:
- **Log Level** dropdown: `None`, `Errors Only`, `Warnings and Errors` (default), `All`
- **Auto Refresh Dashboard** toggle
- **Refresh Interval (ms)** slider: 100–5000, default 500
- **Simulate Slow Loading** toggle
- **Delay (ms)** slider: 100–5000
- **Failure Rate (%)** slider: 0–50
- **Reset All Settings** / **Reset Statistics** buttons

Auto-refresh and the interval drive this window. The other four write through to `DebugSettings.Instance` and mark the asset dirty.

> **Only the log level changes behaviour.** `DebugSettings.IsVerbose` (`logLevel == All`) gates verbose logging in `AssetLoader` and `CdnTelemetry`. Nothing in the package reads `simulateSlowLoading`, `simulatedDelayMs` or `simulateFailureRate`; `DebugSettings.ShouldSimulateFailure()` and `GetNetworkDelay()` have no callers. The three simulation widgets persist a value and do nothing else — call those methods from your own loading code if you want the behaviour.

**Reset All Settings** resets the widgets' displayed values (and re-reads the log level from the asset); it does not write defaults back to `DebugSettings`. **Reset Statistics** clears the tracker and the performance metrics after a confirmation.

**Usage**:
```
# Reduce overhead
1. Go to Settings tab
2. Set Refresh Interval to 1000ms or higher
3. Or turn Auto Refresh Dashboard off entirely
```

---

## Layout Rule Editor

**Path**: `Window > Addressable Manager > Layout Rule Editor`
**Minimum size**: 1000×600

The Rule Editor is the primary tool for creating and managing addressable automation rules. On open it selects the first `LayoutRuleData` it finds in the project; with none selected it shows a **Create New LayoutRuleData** button instead of the three panels.

### Layout

```
┌─────────────────────────────────────────────────┐
│ Toolbar (Rule Data, Validate/Apply/Import/Export)│
├──────────┬──────────────────┬───────────────────┤
│ Rule     │ Configuration    │ Preview           │
│ List     │ Panel            │ Panel             │
│ (250px)  │ (Center)         │ (350px)           │
│          │                  │                   │
│ Address  │ Name             │ Preview Limit     │
│ Label    │ Enabled          │ Refresh Preview   │
│ Version  │ Description      │ • asset1.png      │
│ tabs     │ Filters          │ • asset2.png      │
│          │ Provider         │ Showing 45 of 300 │
└──────────┴──────────────────┴───────────────────┘
```

---

### Toolbar

**Rule Data Selector**: an ObjectField accepting a `LayoutRuleData` asset. Changing it clears the current rule selection.

**Action Buttons**:
- **Validate**: runs `RuleValidator` and shows a summary dialog. The dialog lists the first ten messages with their severity; every message is also written to the console.

- **Apply All**: confirms, then runs `LayoutRuleProcessor.ApplyRules` with a progress bar and reports processed / addresses / labels / error counts. There is no undo — back up first.

- **Import**: file panel for a `.json` file, then a **Merge / Replace** choice.
  - Merge: keep existing rules and add the imported ones
  - Replace: remove all existing rules first

- **Export**: save panel for a `.json` file, then offers to reveal it in the file browser. The default file name comes from the rule data's Description.

Import/export goes through `RuleSerializer`, which stores each filter's and provider's type name, asset path and serialized JSON — resolving by path on import, and otherwise reconstructing the object from the type name and storing it as a sub-asset of the rule data.

---

### Left Panel: Rule List

**Rule Type Toggles** (toolbar buttons, exactly one active):
- Address: rules that assign addresses and groups
- Label: rules that add labels
- Version: rules that apply version tags

**Rule Items**: each shows the rule name (click to select) and one mini line, `Filters: N | Priority: P`. There is no enabled/disabled badge in the list — the Enabled toggle lives in the configuration panel.

**Controls**:
- **+ Add Rule**: appends a new rule of the current type, named `Address Rule N` / `Label Rule N` / `Version Rule N`
- **- Remove**: deletes the selected rule (enabled only while a rule is selected; no confirmation)

**Ordering**: the list shows rules in stored order. Priority affects *processing*, not display — `LayoutRuleProcessor` sorts by priority descending (highest first) unless the rule data's `Preserve Rule Order` is on, in which case stored order wins.

---

### Center Panel: Configuration

**When a rule is selected, the panel draws every field by hand:**

#### Address rule
- **Rule Name**
- **Enabled**
- **Description** (multi-line text area)
- **Target Group** — the addressable group name. `/` and `\` are not valid in group names; the editor warns and shows the normalized name it will use.
- **Group Template** — an `AddressableAssetGroupTemplate`. Leave it empty and a group this rule has to *create* inherits the Default Group's bundle mode and build/load paths; the editor shows a HelpBox saying so.
- **Priority** — a plain int field, default 0, no clamping. Higher is applied first.
- **Skip Existing** — don't touch assets that already have an address. This also stops the rule relocating those entries into its target group.
- **Allow Group Move** — default on. Turn it off to protect hand-configured groups from being emptied by this rule.
- **Filters (AND logic)** — object fields for `AssetFilterBase`, one `-` button per row, `+ Add Filter` at the bottom
- **Address Provider** — a single `AddressProviderBase` object field

#### Label rule
Rule Name, Enabled, Description, Priority, **Append to Existing** (on = add to the existing label set, off = replace it), Filters, **Label Provider**.

#### Version rule
Rule Name, Enabled, Description, Priority, **Skip Existing**, Filters, **Version Provider**.

#### Available filters (8, all created from `Assets > Create > Addressable Manager > Filters`)
- **PathFilter** — match by asset path
- **TypeFilter** — match by Unity type
- **ExtensionFilter** — match by file extension
- **AddressFilter** — match on the existing address
- **AddressableGroupFilter** — match by group membership
- **ObjectFilter** — match specific assets
- **FindAssetsFilter** — match an `AssetDatabase.FindAssets` search filter
- **DependentObjectFilter** — match dependencies of given objects

> **PathFilter defaults to `Contains` matching.** Its `Match Mode` options are `Contains`, `StartsWith`, `EndsWith`, `Exact`, `Regex` and `Glob`, and the first four compare with plain string operations — a pattern containing `*` or `**` matches **nothing** until you switch Match Mode to `Glob`. Typing `**` under `Regex` throws an invalid-pattern exception that is caught and logged as `[PathFilter] Invalid Regex pattern …`, after which the filter rejects everything. The default pattern is `Assets/` and matching is case-insensitive by default.
>
> Also note a **disabled filter matches everything** — `AssetFilterBase.IsMatch` returns true when `Enabled` is off, so unchecking a filter widens the rule rather than disabling the rule.

#### Available providers (8, from `Assets > Create > Addressable Manager > Providers`)
- Address: **PathAddressProvider**, **FileNameAddressProvider**
- Label: **ConstantLabelProvider**, **FolderLabelProvider**
- Version: **ConstantVersionProvider**, **DateVersionProvider**, **BuildNumberVersionProvider**, **GitCommitVersionProvider**

---

### Right Panel: Preview

**Purpose**: See which assets match the selected rule before applying.

**Features**:
- **Preview Limit** slider: 10–200, default 50
- **Refresh Preview** button (its label reads "Generating…" while a preview runs)
- **Statistics**: `Showing X of Y matched assets`
- **Conflict box**: after an *address* preview, the whole rule set is simulated and any duplicate addresses it would create are listed as an error HelpBox. Label and version previews do not run this check.
- **Asset list**: each item shows the file name (click to ping and select it in the project), the full asset path, and the generated `Address:` / `Labels:` / `Version:` value — or a red error box when generation threw or produced an empty value.

The preview scan covers `Assets` only, and skips `Assets/AddressableAssetsData/`.

**Usage**:
```
1. Select a rule from the left panel
2. Adjust Preview Limit if needed (default: 50)
3. Click "Refresh Preview"
4. Review matched assets and generated outputs
5. If wrong, adjust filters/provider and refresh again
6. When satisfied, click "Apply All"
```

**Performance Tips**:
- Use a lower limit (20–30) for quick checks — the limit caps the rows built, not the scan
- Every refresh walks every asset under `Assets`, so it gets slower with project size
- Split rules into smaller sets if the scan becomes painful

---

## Layout Viewer

**Path**: `Window > Addressable Manager > Layout Viewer`
**Minimum size**: 900×500

Inspect the live `AddressableAssetSettings` and the conflicts `RuleConflictDetector` finds in it. Without initialized Addressables settings the window shows an error box and nothing else.

### Toolbar
- **Refresh** — re-run conflict detection now
- **Auto Refresh** toggle — on by default, re-runs every 2 seconds
- **Search** field — filters *entries* by address substring, case-insensitive
- **Conflicts Only** toggle — hide groups with no conflicting entries
- **Export Report** — save panel writing a CSV with columns `Type, Message, Affected Assets, Suggestion`

### Summary bar
`Groups: N`, `Entries: N`, `Labels: N`, and either `✖ N Error(s)` / `⚠ N Warning(s)` or `✓ No Issues`. Duplicate and empty addresses count as errors; every other conflict type counts as a warning.

### Left panel: Addressable Groups
One foldout per group, labelled `GroupName (N entries)` and marked `⚠` when it contains a conflicting entry. Expanding shows each entry as one row: the address, its labels in brackets when it has any, and a `→` button that pings and selects the asset. There is no type filter, group filter, per-asset inspector, dependency view or file-size column.

### Right panel: Validation & Conflicts
Conflicts grouped by type, each with its message, up to five affected asset paths (each with its own `→` ping button, plus an "… and N more" line), and the detector's suggestion. Conflict types:

`DuplicateAddress`, `InvalidAddressCharacters`, `EmptyAddress`, `CircularDependency`, `MissingReference`, `GroupConflict`, `SettingsNotInitialized`.

Detection runs on open, on **Refresh**, and on the auto-refresh tick — there is no separate "Detect Conflicts" button in this window (the `LayoutRuleData` inspector has one).

### Usage

**Verify rule application**:
```
1. Apply rules in the Rule Editor
2. Open Layout Viewer
3. Search for specific addresses
4. Verify addresses and labels match expectations
```

**Debug conflicts**:
```
1. Open Layout Viewer
2. Read the Validation & Conflicts panel (it refreshes itself)
3. For each conflict: note affected assets, review rule priorities,
   adjust filters or priorities, reapply rules
4. Repeat until the summary reads "✓ No Issues"
```

---

## CDN Manager

**Path**: `Window > Addressable Manager > CDN Manager`
**Minimum size**: 560×400

Six tabs, in this order, exactly one active at a time: **Validator**, **Server**, **Update Preview**, **Build**, **Catalog**, **Runtime Monitor**. The header shows the current `EditorUserBuildSettings.activeBuildTarget`.

See `Documentation/CDN_SYSTEM.html` for the CDN workflow itself.

---

## Custom Inspectors

The package registers eight custom inspectors.

**`LayoutRuleData`** — header, stats, and Quick Actions: **Apply All Rules**, **Open Rule Editor**, **Open Viewer**, **Validate Rules**, **Detect Conflicts**, **Clear All Rules**.

**`CompositeLayoutRuleData`** — **Apply Combined Rules**, **Validate All**, and a Combined Rules Preview section.

**`AddressablePreloadConfig`** — **Validate All Addresses**, **Sort by Priority** (ascending, undoable), **Test Load in Editor**, plus total / startup / valid / invalid counts. **Test Load in Editor** does not load anything: outside Play Mode it tells you to enter Play Mode, and inside it lists the first ten assets it *would* load.

**`GlobalAssetScope` / `SceneAssetScope` / `HierarchyAssetScope`** — a shared layout: a coloured banner (Global green, Scene yellow, Hierarchy red) with an ACTIVE / INACTIVE badge, a status block, a memory bar against a hard-coded 100 MB ceiling, a collapsed `Loaded Assets (N)` foldout, and four buttons — **Activate Scope**, **Deactivate Scope**, **Cleanup Scope**, **Open Dashboard**. The first three are **disabled outside Play Mode**; Cleanup is also disabled while the scope holds no assets.

**`MonitoringHelper`** — a HelpBox, the default fields, and an **Open Dashboard** button that appears only while playing with monitoring enabled.

**`AddressableProgressBar`** — in Edit Mode, the default fields plus a short Setup Guide. In Play Mode, a Testing Controls block: a **Test Progress** slider (0–1), **Show** / **Hide** / **Reset** buttons, an **Animate 0% → 100%** button (about 5 seconds, click again to stop) and a **Test Status** text field.

There is no custom inspector for `PoolConfiguration` or `DebugSettings`.

---

## Menu Items & Shortcuts

Every path below is backed by a `[MenuItem]` or `[CreateAssetMenu]` in the package. Nothing else exists.

### Window Menu

```
Window > Addressable Manager >
├─ Dashboard (Ctrl+Alt+A)
├─ Layout Rule Editor
├─ Layout Viewer
├─ CDN Manager
├─ Documentation
├─ Settings
└─ Clear All Caches
```

- **Documentation** looks for `Assets/com.game.addressables/README.md`. Installed under `Packages/`, that path does not exist and you get a "not found" dialog — open `Packages/com.game.addressables/README.md` directly.
- **Settings** pings `Resources/AddressableManager/DebugSettings`, offering to create it when missing.
- **Clear All Caches** clears **Editor tracking data only** (`AssetTrackerService`, `PerformanceMetrics`). No runtime asset is released.

### Tools Menu

```
Tools > Addressable Manager >
├─ Force Process All Assets
├─ Batch Address Updater
├─ Repair Groups Missing Schemas
├─ Start Local Content Server
├─ Stop Local Content Server
└─ Quick Setup >
   ├─ Create All Scope Objects
   └─ Create Sample Configs
```

- **Batch Address Updater** is not a window. It shows a dialog listing the `BatchAddressUpdater` static methods you call from code: `FindAndReplace`, `AddPrefix`, `RemovePrefix`, `ConvertToLowercase`.
- **Start Local Content Server** serves on port 8080. **Stop** is greyed out while nothing is running.
- **Create All Scope Objects** creates `[GlobalAssetScope]`, `[SceneAssetScope]` and `[HierarchyAssetScope]`. There is no Session GameObject — `SessionAssetScope` was removed in 4.0.0; use `ScopeManager.Instance.GetOrCreateScope("Session")` or `Assets.StartSession()`.
- **Create Sample Configs** creates `PreloadConfig.asset`, `PoolConfig.asset` and `DebugSettings.asset` in `Assets/`.

### Assets Menu

```
Assets > Addressables >
└─ Apply Layout Rules            (enabled when something is selected;
                                  shows a picker if several LayoutRuleData exist)

Assets > Addressable Manager >
├─ Create Preload Config
├─ Create Pool Config
└─ Create Debug Settings

Assets > Create > Addressable Manager >
├─ Layout Rule Data
├─ Composite Layout Rule Data
├─ Preload Configuration
├─ Pool Configuration
├─ Debug Settings
├─ CDN Settings
├─ Filters >
│  ├─ Path Filter
│  ├─ Type Filter
│  ├─ Extension Filter
│  ├─ Address Filter
│  ├─ Object Filter
│  ├─ Find Assets Filter
│  ├─ Dependent Object Filter
│  └─ Addressable Group Filter
└─ Providers >
   ├─ Address > Path
   ├─ Address > File Name
   ├─ Label > Constant
   ├─ Label > Folder Name
   ├─ Version > Constant
   ├─ Version > Date
   ├─ Version > Build Number
   └─ Version > Git Commit
```

### GameObject Menu

```
GameObject > Addressable Manager >
├─ Add Global Scope
├─ Add Scene Scope
├─ Add Hierarchy Scope
└─ View in Dashboard
```

### Add Component

Two entries: `Addressable Manager/Monitoring Helper` and `Addressable Manager/Progress Bar`.

### Keyboard Shortcuts

| Shortcut | Action | Context |
|----------|--------|---------|
| `Ctrl+Alt+A` | Open Dashboard | Global |

That is the **only** keyboard shortcut in the package. The Rule Editor and Layout Viewer register none — use their toolbar buttons.

---

## Tips & Tricks

### Rule Editor

**Tip 1: Use descriptive names**
```
❌ "Rule 1", "Test", "Temp"
✅ "UI Sprites by Filename", "Character Prefabs"
```

**Tip 2: Document rules**
```
Always fill in the Description field:
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
6. Apply All
```

**Tip 4: Set Match Mode before writing a pattern**
```
PathFilter starts in Contains mode. "Assets/UI/**" matches nothing there —
switch Match Mode to Glob first, or write a plain substring.
```

**Tip 5: Validate before applying**
```
Validate catches configuration errors before Apply All writes entries,
and Apply All cannot be undone.
```

### Layout Viewer

**Tip 1: Regular audits**
```
Weekly:
- Open Layout Viewer (conflicts refresh themselves)
- Review any issues
- Clean up unused assets
```

**Tip 2: Track changes**
```
Before major changes:
- Export Report
- Apply changes
- Export again and diff the two CSVs
```

**Tip 3: Turn Auto Refresh off on large projects**
```
Detection re-runs every 2 seconds while the window is open.
```

### Dashboard

**Tip 1: Monitor during testing**
```
Keep the Dashboard open during play sessions:
- Watch estimated memory
- Track slow assets
- Identify leaks early
```

**Tip 2: Export reports**
```
Before optimization: export a baseline CSV
After optimization: export again and compare
```

**Tip 3: Adjust refresh rate**
```
High refresh (100-200ms): Detailed monitoring
Medium refresh (500ms): General use
Low refresh (1000ms+): Reduce overhead
```

---

## Troubleshooting

### Rule Editor not showing assets

**Check**:
1. Is a `LayoutRuleData` assigned in the toolbar?
2. Is `PathFilter` still in `Contains` mode with a glob pattern?
3. Do assets exist at the filter paths, under `Assets/`?
4. Is the Preview Limit too low to show what you're looking for?

**Solution**: See [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

### Dashboard shows no data

**Check**:
1. Are you in play mode? Tracking is Editor + Play Mode only, and is cleared on exit.
2. Have any assets been loaded through `AssetLoader` / the facade / a scope?
3. Is `Auto Refresh Dashboard` enabled?
4. Check the console for errors

### Preview generation slow

**Causes**:
- Every refresh scans every asset under `Assets`
- Complex filter chains
- Large project size

**Solutions**:
- Make filters more specific
- Split rules into smaller sets
- Lower the Preview Limit (caps the rows built, not the scan)

### Memory graph not updating

**Causes**:
- Not in play mode — samples are only collected while playing
- No addressable assets loaded

**Solutions**:
- Enter play mode
- Load some assets

### Memory numbers look wrong

They are estimates derived from the type name alone (see the Active Assets caveat). Use the Unity Profiler for real bytes.

---

## Related Documentation

- [ADDRESSABLE_AUTOMATION_GUIDE.md](ADDRESSABLE_AUTOMATION_GUIDE.md) - Automation system details
- [RULE_SYSTEM_EXAMPLES.md](RULE_SYSTEM_EXAMPLES.md) - Practical examples
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common issues and solutions
- [LIFETIME_DESIGN.md](LIFETIME_DESIGN.md) - Scope and loader ownership rules
- `Packages/com.game.addressables/EDITOR_TOOLS_GUIDE.md` - inspectors, config assets and the `ScopeManager` API in depth

---

**Version**: 4.1.0-pre.9 | **Unity**: 2023.1+
