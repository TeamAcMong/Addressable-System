# Rule Automation Presets

A set of rule assets for the automation system, wired to each other so you can see
how the pieces connect rather than starting from empty scriptable objects.

| Asset | What it is |
|---|---|
| `ExampleLayoutRules` | A `LayoutRuleData` with one working address rule, plus a placeholder label rule and version rule. |
| `CompositeLayoutRuleData` | A composite that merges `ExampleLayoutRules` — the entry point when several teams own separate rule files. |
| `PathFilter` | Match Mode `StartsWith`, pattern `Assets/`. |
| `FindAssetsFilter` | An `AssetDatabase.FindAssets` query, `t:Scene`. |
| `ExtensionFilter` | Matches `.unity`. |
| `TypeFilter` | Matches type `SceneAsset`, subclasses included. |
| `ObjectFilter` | Points at one specific object. |
| `FileNameAddressProvider` | Derives the address from the file name. |

The address rule in `ExampleLayoutRules` (`Address Rule 1`) combines `PathFilter` and
`FindAssetsFilter`, then names matches with `FileNameAddressProvider` and assigns
them to the group `Scene`. `ExtensionFilter`, `TypeFilter` and `ObjectFilter` ship
configured but are referenced by no rule — they are there to be dragged into a rule's
filter list.

Two things to fix before this does anything useful:

- **The label rule and the version rule are placeholders.** `Label Rule 1` and
  `Version Rule 1` each hold one *empty* filter slot and no provider. That is not
  merely inert: `LayoutRuleProcessor` validates the entire rule set before applying
  anything, and one invalid rule aborts the whole run — including the address rule
  that does work. Give them a filter and a provider, or delete them, before you apply.
- **The group name.** `Scene` is created if it does not exist. No Group Template is
  assigned to the rule, so a group created this way copies its schemas from the
  DefaultGroup — in a stock project, PackTogether with local build and load paths —
  and `AddressRule` logs a warning saying so. If the group is meant to be remote,
  assign a Group Template or point the rule at a group you configured yourself.

Two more details worth knowing before you read the preview:

- `FileNameAddressProvider` here has **Include Extension** on and the suffix `Scene`,
  so `Assets/Levels/Level1.unity` is addressed `Level1.unityScene`. Clear both fields
  for a plain file-name address.
- `ObjectFilter` references `Assets/Scenes/SampleScene.unity` from the project this
  sample was authored in. That object does not ship with the sample, so after import
  the reference is missing and the filter matches nothing.

## Running them

Open **Window ▸ Addressable Manager ▸ Layout Rule Editor**, load
`ExampleLayoutRules` into the toolbar's object field, and use `Refresh Preview` in
the right-hand pane. Preview never writes: it lists what each rule would match (up to
the Preview Limit) and calls out duplicate addresses the rule set would create. Use
the toolbar's `Validate` button too — it names the placeholder-rule errors above.
Apply only after the preview is what you want.

`CompositeLayoutRuleData` is a `ScriptableObject` in its own right, not a
`LayoutRuleData`, so the Layout Rule Editor's object field will not accept it. Drive
it from its own Inspector instead, which has **Validate All** and **Apply Combined
Rules** buttons. It has `Respect Source Order` and `Deduplicate By Name` both on:
source order is carried through to the processor as `Preserve Rule Order`, which
suppresses the usual descending-priority sort.

For CI, the same rules run headless:

```bash
Unity -batchmode -quit -executeMethod AddressableManager.Editor.CLI.AddressableCLI.ApplyRules \
  -layoutRuleAssetPath "Assets/Samples/.../ExampleLayoutRules.asset" \
  -resultFilePath "rules-result.json"
```

Exit code `0` on success, `1` if validation or the apply failed, `2` for a missing
argument, a missing asset, or an exception. Add `-validateOnly true` to check without
writing.

## Filters are extensible

There are eight built-in filter types — the five in the table above plus
`AddressFilter`, `AddressableGroupFilter` and `DependentObjectFilter` — all
`ScriptableObject`s deriving from `AssetFilterBase`. If none of them expresses your
rule, subclass rather than contorting a `FindAssetsFilter` query:

```csharp
using AddressableManager.Editor.Filters;
using UnityEngine;

[CreateAssetMenu(menuName = "My Game/Filters/Recently Changed")]
public class MyFilter : AssetFilterBase
{
    protected override bool IsMatchInternal(string assetPath) => /* your test */ true;
}
```

`IsMatchInternal` is the only required member. The public `IsMatch` is not virtual —
it applies the shared `Enabled` and `Invert` fields around your result, and returns
`true` for a *disabled* filter so that switching one off does not veto the filters
that are still on. A rule ANDs its filters and additionally requires at least one of
them to be enabled, so a rule whose every filter is unchecked matches nothing rather
than everything.

Optional overrides: `Setup()`, called once per run before matching, and
`GetDisplayName()` for the editor list.
