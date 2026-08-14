# Rule Automation Presets

A working set of rule assets for the automation system, wired to each other so you
can see how the pieces connect rather than starting from empty scriptable objects.

| Asset | What it is |
|---|---|
| `ExampleLayoutRules` | A `LayoutRuleData` with one address rule, one label rule and one version rule. |
| `CompositeLayoutRuleData` | A composite that merges `ExampleLayoutRules` — the entry point when several teams own separate rule files. |
| `PathFilter` | Matches assets under `Assets/`. |
| `FindAssetsFilter` | Matches by an `AssetDatabase.FindAssets` query. |
| `ExtensionFilter` | Matches `.unity`. |
| `ObjectFilter`, `TypeFilter` | The remaining two filter kinds, unconfigured, as templates. |
| `FileNameAddressProvider` | Derives the address from the file name. |

The address rule in `ExampleLayoutRules` combines `PathFilter` and
`FindAssetsFilter`, then names matches with `FileNameAddressProvider` and assigns
them to the group `Scene`. Change the target group name before running it — the
sample cannot know your group names, and a rule pointing at a group that does not
exist is reported rather than silently skipped.

## Running them

Open **Window ▸ Addressable Manager ▸ Layout Rule Editor**, load
`CompositeLayoutRuleData`, and use Preview. Preview never writes: it lists what
each rule would do and flags conflicts where two rules claim the same asset. Apply
only after the preview is what you want.

For CI, the same rules run headless through the CLI — see
`Documentation/ADDRESSABLE_AUTOMATION_GUIDE.md`.

## Filters are extensible

There are eight built-in filter types — the five above plus `AddressFilter`,
`AddressableGroupFilter` and `DependentObjectFilter` — all `ScriptableObject`s
deriving from `AssetFilterBase`. If none of them expresses your rule, subclass
rather than contorting a `FindAssetsFilter` query; the automation guide has the
base class and a worked example.
