# Changelog

All notable changes to this package will be documented in this file.
## [4.4.1] - 2026-08-26 - "Create test asset" closed the Editor, and eleven more entry points could have

Reported as a crash. It was not a crash — it was `EditorApplication.Exit(0)`, reached from a button.

`CdnTestContentCLI.CreateRemoteTestContent` is a batchmode entry point: it ends the Editor to report
its status, which is exactly right for a CLI. **4.4.0 wired a button straight to it.** Nothing in
Unity announces itself exiting on purpose, so it reads as a crash — and `Exit` does not offer to save,
so anything unsaved went with it.

**This is the second time in three releases**, and the first one was two commits earlier: `ProbeHub`
closed a live session, was guarded, and the guard was not carried to the seven other files that call
`Exit`. Fixing one instance of a pattern and not looking for the rest is the defect the 4.1.0 release
exists to describe, committed inside the release that describes it.

### Fixed - the work is separated from the exit

`CreateRemoteTestContentCore` and `GenerateTestCorpusCore` do the work and **return** an exit code.
The public entry points call them and then exit, and only in batchmode. The buttons call the work.

The code is reported rather than swallowed: these write to the Addressables settings, and a run that
stopped half way leaves the project in a state worth being told about. Silence after pressing a
button that changes the project is its own defect.

### Added - `BatchmodeGate`, and all nine entry points now use it

Every `public static void` reachable by `-executeMethod` refuses to exit outside batchmode and says
why. One helper rather than nine copies, because nine copies is how one of them ends up different.

Guarded: `CatalogInspectCLI`, `CdnBuildCLI` (3), `CdnSetupCLI`, `CdnTabProbeCLI`, `AddressableCLI`
(5), plus the two already done. CI is unaffected — in batchmode the gate returns true.

**Verified by running a guarded entry point from a live Editor through the automation bridge**: the
Editor survived and logged the refusal. Before this it would have closed.

Checked that no guarded method is called from anywhere else in the package first: the apparent
callers were same-named methods on other types — `LayoutRuleProcessor.ApplyRules` is an instance
method, `RuleConflictDetector.DetectConflicts` is a different type, `LayoutRuleEditorWindow.ImportRules`
is private. A guard on a method the UI depends on would have traded a crash for a dead button.

## [4.4.0] - 2026-08-26 - The local server can be made to fail on purpose, from a button

### Added - `Test content` and `Make it fail` on the Local Server screen

**The machinery already existed and nothing could reach it.** `ServerFaults` can return any status for
any path, drop connections mid-response, throttle to a byte rate and add latency — and it was called
from exactly one place in the package: `CdnFaultInjectionTests`. `CdnTestContentCLI` can create remote
test content and a whole corpus, and carried no menu item, so both were reachable only through
`-executeMethod` in batchmode. **A capability nobody can press is a capability the team does not have.**

Both are now on the screen that owns the server, in code rather than in the UXML so
`CdnManagerWindow` gets them without a second markup file to keep in step.

### Notes - faults are one global state, and the panel says so

`InjectStatus` and `InjectConnectionDrop` **share** the path filter and the request budget — setting
one overwrites the other — and the `IDisposable` every injector returns calls `Clear()`, which removes
**all** faults rather than the one it came from. A panel offering several independently-removable
faults would describe an engine that does not exist, so this offers one fault at a time and one way
to remove it. The returned disposables are deliberately discarded.

**The banner is the important part.** The state is static and survives domain reloads, so a 503
switched on and forgotten makes everything afterwards fail with nothing on screen explaining why —
and the next person debugs a CDN over a switch someone flipped ten minutes ago. While a fault is
active the screen says so, and it clears itself on leaving play mode.

Hidden when nothing is wrong: a permanent "no faults active" strip is a line people stop reading,
which is the one thing this must not become.

### Added - the `Reachability` design, as a preview only

`Documentation/design/Reachability.dc.html`, in `DESIGN_PREVIEW.html`. Asking "can we reach the CDN,
and is it newer" today requires **play mode** — `CdnManager.CheckForUpdateAsync` needs the game to
have initialised the CDN layer, so testing infrastructure means running the game. The Editor assembly
has never made an outbound request; its two networked files are an `HttpListener` that *serves*.

The design decision is the number of outcomes: **four, not two.** "Up to date" and "could not ask"
are opposites that render as the same pixel on a screen with one tick and one cross. A 401 is a fifth
thing again — the host answered, and refused.

Not implemented. **Deliberately after this release**: four of those five states can only be produced
by making a server fail, and building the check before the means of failing it would have proven one
branch out of five.

## [4.3.0] - 2026-08-25 - Which bundles the device actually has, and two screens that were showing less than they knew

### Added - `Downloaded Content`, and `CacheInventory` underneath it

The tool could report how much the cache held and nothing about what was in it: a total in bytes on
the Runtime Monitor, and no way to ask which bundles those bytes were.

`CacheInventory.Snapshot()` reads the loaded catalog, asks the cache about every bundle in it, and
returns a `LoadResult<CacheInventoryReport>`. Three facts shaped it, all read out of Addressables
2.9.1 rather than assumed:

- **The cache cannot be enumerated.** `Caching` answers whether a bundle *you can name* is present;
  `Caching.GetAllCachePaths` lists directories, not contents. So an inventory can only cover the
  loaded catalog — and what an older catalog left behind is reported as `UnaccountedBytes` rather
  than quietly omitted. **That figure is what `Clean obsolete` deletes**, and no other view shows it.
- **The cache key is `AssetBundleRequestOptions.BundleName` plus the hash, not the file name.**
  Addressables builds `new CachedAssetBundle(m_Options.BundleName, hash)` at every call site.
  `CatalogReader` deliberately keys on the *file* name because its question is whether a file exists
  on a CDN. Two questions, two keys — and using the CDN key here would have reported every bundle as
  missing, which reads as a broken screen rather than a wrong lookup.
- **Local bundles are never cached.** They ship inside the player, so absent from the cache is the
  right answer for them. "Not downloaded yet" and "never will be" are different facts and get
  different groups.

The screen groups by what the answer means, sizes its split bar by **bytes rather than count** — "340
bundles" is a number, "2.0 GB over a phone connection" is a decision — and offers `Evict` per cached
bundle, confirmed, naming the bundle and how many addresses resolve through it.

### Fixed - Asset Lifetime refreshed once and never again

Reported: *"phải tắt tab rồi bật lại mới update UI đúng"*. Exactly right, and the cause was one line:
`OnShown() => Rebuild();` with no poll. Closing the tab and reopening it "fixed" it because the shell
rebuilds a section on every navigation, which made a missing refresh look like a quirk of the window.

Its header button could not have kept up either. `Release leaked (N)` is drawn during navigation and
only then, so the count would have gone stale while the body underneath refreshed correctly — **half a
screen updating is worse than none of it**, because the half that stopped still looks authoritative.
`IHubHost.RefreshHeaderActions()` exists for that, and is called only when the number changes: a
button rebuilt every second is a button that vanishes under a cursor on its way to clicking it.

### Fixed - Layout Rules showed one rule set out of however many the project has

Reported: *"rõ ràng tôi có nhiều layout nhưng show thì nó show có 1"*. Not a defect in the rule
engine — a defect in this window. `FindRuleData()` returned the first asset `AssetDatabase.FindAssets`
happened to yield and ignored the rest, and **`FindAssets` promises no order**, so the screen was not
merely incomplete: which rule set you saw could change between sessions.

Rule sets are not alternatives to each other — every one of them runs. There is a picker now when
there is more than one, the list is sorted by path so the answer repeats, and the choice survives the
session. **Conflicts scans all of them**, because two rule sets can address the same asset and a scan
of one would call that clean while the build fails.

### Fixed - a one-second poll threw away the reader's place

Both polling screens refilled their `ScrollView` every tick, resetting the scroll offset — so a list
long enough to need scrolling could not be read while the poll ran. They now rebuild only when the
answer moved, and restore the offset when it does.

### Fixed - the batchmode probes closed a live Editor

`HubProbeCLI.ProbeHub` and `HubLayoutProbeCLI.ProbeLayout` end in `EditorApplication.Exit`. Invoked
from a live session through an automation bridge, that closes the window someone is working in —
without the prompt `Quit` gives, so unsaved scene changes go with it. **It did exactly that during
this work.** Both now refuse to run outside batchmode and name the menu item instead. A CLI entry
point that is destructive when called the wrong way has to say no, not rely on nobody calling it
that way.

## [4.2.4] - 2026-08-25 - The rail label shipped as a property nobody read, and only looking at it found that out

`ValidatorSection` carried `public string RailLabel => "Validator";` and **did not declare
`IHubRailLabel`**. The rail tests `section is IHubRailLabel`, the test failed, and the rail fell back
to `Title` - so it read **Configuration** where the design says Validator.

It shipped in 4.2.0 and survived 4.2.1, 4.2.2 and 4.2.3.

**Nothing could have caught it.** The property is valid C#, so the compiler is content. `HubProbeCLI`
checked ids, stages, subtitles and health, none of which this touches. No test asserted a rail label.
Every string check passed, because the string *was there* - in a member the rail never reads. A
property nobody reads is dead code wearing the shape of a feature, which is the defect this window
exists to remove, and it was in the window.

It was found by taking a screenshot of the running Editor and reading the rail.

### Fixed

`ValidatorSection` implements `IHubRailLabel`.

### Added - the probe now catches this class of defect

A section that declares a `RailLabel` property without implementing the interface is reported by
name. Reflection rather than a compile-time check, because the whole failure is that the compiler
has no opinion here.

### Notes

The screenshot was taken through the Unity-MCP bridge with `InternalEditorUtility.ReadScreenPixel`.
That reads the **desktop**, not a window: two earlier attempts captured other applications entirely,
because Windows refuses to let a background process pull itself to the front. It only worked once the
hub was a floating window and the Editor was already in front.

## [4.2.3] - 2026-08-25 - Three things the design has, found by testing every sentence instead of a chosen few

The previous conformance passes checked a **hand-picked list** of the design's strings. This one
extracts every literal the design puts on screen and tests all of them, so the answer stops depending
on which ones occurred to me. Twenty-two came back unmatched; nineteen were the mockup's fake data -
`14 groups · 2.4 GB total`, `cdn.acme.com`, `catalog 1.4.1 → 1.4.2` - which this build computes rather
than prints, and two more were the checker's own false alarms, where the shipped sentence carries a
substituted value the literal comparison could not see.

Three were real.

### Added - the pipeline nodes carry a mark, not only a colour

The design draws a tick inside a stage that is done and a cross inside one that is blocked, cut out
of the fill. This build had the coloured dot and no glyph - which carries the state to nobody who
cannot separate amber from green, and to nobody reading a screenshot in grey. Shape now says what
colour says.

### Added - Apply says that it is undoable, and where it writes

`Applying writes to the Addressables settings asset. Ctrl+Z undoes the whole run.`

The design puts that beside the button, and it is the sentence that makes the button safe to press.
Apply reads as irreversible, so a reader who does not already know about the undo either does not
press it, or presses it and cannot find the way back. Naming the file is the other half: it is what
they would have to revert by hand if the undo were missed.

### Added - the environment variable as a line you can copy

`or:  CDN_HOST=<your-host>  before the build`

The screen already explained in prose that CI can inject the host. Someone wiring a build agent needs
the literal text; a sentence *naming* `CDN_HOST` makes them go and look up the spelling of the thing
the sentence just mentioned.

### Notes

Not fixed, and not a defect: the stat cards on Overview and Asset Lifetime are additions this build
keeps deliberately, and Catalog Inspector and Local Server have no body in the design at all - the
artboard says so in its own words.

## [4.2.2] - 2026-08-25 - The responsive checks were opinion; now they are measurements

Reported as "the UI still has responsive bugs". It did, and nothing in the package could have told
you: every check up to here read the **stylesheet**, and the defect that shipped was in an element the
stylesheet says nothing about.

### Added - `HubLayoutProbeCLI`, which opens the window and measures it

Five window sizes from the legal minimum to 1440x820, every one of the eleven sections at each,
walking the whole visual tree and reporting any element drawn outside its parent. **55 combinations.**

Three things make it worth trusting:

- **It refuses to pass on an unlaid-out tree.** UI Toolkit does not run layout in batchmode, so the
  first version measured `NaN` everywhere - which satisfies every comparison. It now drives the
  panel's layout pass directly and fails loudly if the root still has no size. A green built on
  measuring nothing is the most convincing false green this repository could produce.
- **It proves its own text check fires** before reporting that the check found nothing. Batchmode
  draws these screens nearly empty, so no string is longer than its box; a checker that cannot fire
  reporting zero findings is not a clean bill of health. The probe plants a label 400 characters wide
  in a 60px row, confirms it is flagged, and removes it.
- **The first run's eight findings were seven parts ruler.** `contentRect` is expressed in the
  parent's space with its origin pushed in by padding and border; `layout` is measured from the outer
  edge. Comparing one against the other reported every child of a padded row as overflowing by
  exactly the padding. Corrected to compare against `contentRect.xMax`, and the eight became one.

### Fixed - the search chip shrank and its contents did not

That one was real: at 620x420 the header's search affordance shrinks to 109px, and `Ctrl K` was drawn
**15px outside it**. Giving the chip `flex-shrink` in 4.1.2 was half a fix - a shrinkable box whose
contents cannot shrink has not been made responsive, it has been made to overflow more quietly. The
prompt now yields with an ellipsis; the shortcut does not yield at all.

### Changed - the hub moved to `Ctrl+Alt+M`

The Unity-MCP package registers `Window/AI Game Developer — MCP %&a`, which is `Ctrl+Alt+A` — the
hub's chord. Unity accepts two menu items claiming one chord **without defining which of them runs**,
and that is the exact defect 4.1.0 removed from this package, where three items claimed this chord
between them. Being right first is not a reason to keep a collision: the other window has no second
way in, while the hub has a menu entry and `Ctrl+K` once open.

The claim also had to be corrected in five documents, three of which still told the reader the
**Dashboard** answers this chord — which stopped being true in 4.1.0. `README.md`'s menu table was
still the pre-4.1.0 one, missing Open, Validate Setup, Profiles and Build Content entirely.

### Added - the probe runs from a live Editor

`Window > Addressable Manager > Check layout at every size` runs the same measurement without
`EditorApplication.Exit`, restores the window's position afterwards, and closes it again if it was not
already open — it runs in an Editor somebody is using.

That is the run that matters: this measurement was first taken against the real project through the
Unity-MCP bridge, and it is the first time the numbers came from screens with content in them rather
than from batchmode's empty ones.

### Notes

What the batchmode probe still cannot see: it has no catalog loaded, no play session and no scope
holding anything, so every section is measured close to empty. Long real data - a 90-character asset path, a
base URL, a build target called `StandaloneWindows64` - is exactly what breaks these rows, and the
content-independent check exists for that reason, but it has not yet met real content.

## [4.2.1] - 2026-08-24 - The screens matched the design in structure and not in a single sentence

Every earlier conformance check compared **section lists, titles and header actions**. Those matched,
so every check reported a match — while the sentences actually printed on the screens had never been
compared to anything. Extracting the design's literal on-screen text and diffing it against the
strings in the sections found ten differences on screens that had already passed three reviews.

### Fixed - wording, taken from the design rather than paraphrased

| Screen | Was | Design |
| :-- | :-- | :-- |
| Layout Rules | `Show both` | `Ping both` |
| Update Preview | `Changed since the live release` | `Changed since the last publish` |
| Build | `What this build would produce` | `What would be produced` |
| Validator | "Why some **rules** have no Fix button" | "Why some **issues** have no Fix button" |
| Overview | `Recent, on this machine` | `Recent`, with the machine caveat moved beside it |

### Added - blocks the design has and the build did not

- **Overview: `Full history`.** The card shows six entries from a file that holds far more. Without a
  way to reach the rest, the screen was quietly deciding for the reader that older entries do not
  matter. Opens the log in the OS file browser, and says so rather than doing nothing when nothing has
  been recorded yet.
- **Asset Lifetime leads with the leak.** *"N handles outlived the object that took them"*, with the
  design's own explanation of why that costs a session's memory. A count of live scopes is a fact; a
  handle that outlived its owner is a bug, and it was sitting behind four cards of arithmetic. Absent
  entirely when nothing has leaked — a headline reading "0 leaked" every session teaches people to
  skip past the place the real one appears.
- **`Live handles by owning scope`,** with the sample time stamped beside it. Every row under it is a
  sample rather than a live reading, and a list that does not say when it was taken gets read as
  current however old it is.
- **`Not a memory profiler.`** Unity's is better at bytes; this screen answers the one question it
  cannot — which scope is still holding this, and who took the reference.

### Notes

The stat cards on Overview and Asset Lifetime are **not** in the design; they are kept deliberately
and the design's blocks were added around them.

What will not converge, and why: USS has no `gap`, no `text-wrap`, no inline SVG, and repo invariant 5
requires `--unity-colors-*` rather than the design's hex values. Spacing, iconography and exact colour
therefore differ by construction. That accounts for how the screens look — it never accounted for the
ten sentences above.

## [4.2.0] - 2026-08-24 - The screens the design drew, and the ones it deliberately did not

Read against `Documentation/design/*.dc.html` screen by screen rather than by section list, which is
what the 4.1.0 check did - and why it reported "matches" for screens that had never been built.

### Added - Conflicts is its own screen

The design puts it on the rail under Author with a red dot; 4.1.0 folded it into Layout Rules. Two
assets resolving to one address is not a detail of authoring rules: it is the one outcome of the rule
system that makes an asset unreachable at runtime, and it survives being scrolled past far too easily
as the last card on a screen about something else.

**Nothing is scanned until asked.** Finding collisions means running the rule set over every eligible
asset, which is far past what a health probe may cost - the rail calls those once a second for every
section. So the state is `NotMeasured` until a scan runs, and the screen says so in those words. A
scan that throws is recorded as unmeasured, never as clean.

### Added - Runtime Monitor rebuilt to the design

Two panels side by side - live state on the left, the transfer on the right - an outcome that stays
until another action replaces it, and the three verbs along the bottom. The hosted tab read the same
values in one stacked column, which put the download, the only thing on the screen that changes
second to second, underneath a six-row table that does not.

The outcome is deliberately static state: all three buttons used to write their result onto a label
the next refresh overwrote within a frame, on a screen that refreshes once a second, so all three
appeared to do nothing. An outcome is the only evidence a button worked.

`RuntimeMonitorTab` stays for `CdnManagerWindow` until that window retires.

### Added - the header actions the design specifies

Four of the seven screens had an empty header. The design puts a verb there on every one:

| Screen | Action |
| :-- | :-- |
| Overview | `Re-scan everything` |
| Layout Rules | `Import…` · `Export…` |
| Profiles | `Open Addressables Profiles` |
| Asset Lifetime | `Release leaked (N)`, beside a renamed `Snapshot` |

`Re-scan everything` needed `HealthThrottle.InvalidateAll()` to exist. Each section owns its own
three-second health cache, so without it the button could only redraw the screen it sits on, from the
same stale answers it was already showing — **a control reporting less than its label, in the header of
the window built to remove exactly that.**

`Release leaked (N)` counts from a snapshot taken as the button is drawn, so the number on it and the
rows beneath it come from one reading, and re-reads before acting because play mode can end in
between. It confirms first and names the scopes: a leaked scope is a bug being reported, not a state
to silently repair.

### Changed - labels

- The rail and the header can now differ, through the optional `IHubRailLabel`. The design uses
  `Validator` on a 196px rail and `Configuration` above the content; one string meant either the rail
  wrapped or the header under-described. Only one section needs it.
- Validator's subtitle carries the rule count once a run has produced one, and omits it before that
  rather than printing a figure nobody measured.
- Asset Lifetime's subtitle says **and what leaked**. Leak detection shipped in 4.1.0; the line
  describing the screen never caught up.
- Local Server names its port, from `LocalContentServer.ConfiguredPort` - a new static that reports
  the port the next start would use, because `ActivePort` is 0 while stopped and would print
  `localhost:0`.

### Notes - what the design does not specify

Catalog Inspector and Local Server have a rail entry, a title and a subtitle in the design and no
body. The artboard says why, in its own words: *"Shares the shell and the vocabulary; drawn in the
build, not in this prototype."* The shipped tabs are that build. They are hosted through the adapter
and are not a gap against the design.

## [4.1.2] - 2026-08-24 - A hosted tab arrived without its stylesheet, and state classes painted labels solid

Reported from a screenshot of the Catalog Inspector, which was rendering its header text on top of
its own rows.

### Fixed - the three hosted CDN tabs lost their layout entirely

Each CDN tab loads only its *own* stylesheet. The classes its UXML shares with the other five —
`cdn-tab-page`, `cdn-preview-summary`, `cdn-preview-state`, `cdn-actions-row`, `cdn-btn` — live in
`CdnManagerWindow.uss`, which that window adds to its root once for all of them. The hub hosts three
of those tabs through an adapter and never brought the sheet.

Nothing was null, nothing threw, every query resolved. What was lost was `flex-shrink: 0` on the
summary and state blocks: the list's `flex-grow` squeezed them to nothing and their text rendered on
top of the rows below, because `overflow` is visible by default. **It reads as a rendering glitch
rather than as a missing file**, which is why it survived the screen being opened and looked at.

`HubProbeCLI` now asserts the sheet is attached to every hosted tab. Verified in the other direction:
with the attach disabled it reports all three as failures.

### Fixed - `hub-state--*` painted labels as solid blocks

Those classes set a background colour, a border colour and a text colour together, because they were
written for the 8×8 dots on the rail, which have to be filled. Applied to a `Label` the background
half makes a solid rectangle — and the class sets the text to the **same** colour, so the text
disappears into it. The rail's stage badge shipped as a solid amber block where a word should be.

Every section that ever coloured a label had already met this and worked around it locally: five
copies of "apply the class, then clear `style.backgroundColor`", one of which also cleared
`borderTopColor` and not the other three borders, plus a sixth workaround written into the USS. Six
workarounds for one missing distinction.

There are two families now — `hub-state--*` fills, `hub-text--*` colours — and all six workarounds
are gone.

### Fixed - three things did not fit at the window's own minimum size

`minSize` is 620×420, which leaves a 424px content column.

- The two-pane split in Layout Rules had a fixed 280px left pane that refused to shrink, leaving the
  dry-run panel **136px**. The two panes exist to be read against each other. Both now wrap.
- Four stat cards across 424px is 99px each — narrower than the figures they exist to show. They
  wrap to two rows.
- The header's Profile / Target / Mode chips were the only content-sized items on that row and the
  only ones that could not shrink, so a build target named `StandaloneWindows64` pushed them past the
  right edge. **A chip that has fallen off the window still reports its value**: the reader sees no
  Profile chip and concludes there is no profile. The title yields first now, then the search chip.

Flow labels and notes clip with an ellipsis rather than overrunning their cell, and the summary strip
wraps.

## [4.1.1] - 2026-08-24 - Every already-addressed asset reported itself as a duplicate

### Fixed - duplicate-address detection accused assets of colliding with themselves

`4.1.0` taught `LayoutRuleProcessor` to seed every address already in the project before a run, so a
collision with an entry *outside* the batch became visible - the on-import path processes one asset
per call and could not otherwise see anything to collide with. The seed does not exclude the entries
taking part in the run, and the ownership check did not either. So an asset that already held the
address the rule generates was reported as colliding with itself:

```
Duplicate address 'tribal2-06': generated for both
'Assets/IconMatch/Art/GameIcons/tribal2-06.png' and
'Assets/IconMatch/Art/GameIcons/tribal2-06.png'
```

Both paths in that sentence are the same path. A reporting project measured **3591 collisions across
3591 assets, every one of them self-against-self, against zero real collisions** - 3723 lines in a
single `Editor.log` after one bulk re-import.

Nothing was corrupted: the address is written regardless, deliberately, so behaviour is unchanged for
anyone already depending on it. What was destroyed is the warning's usefulness. This check exists to
catch two *different* assets claiming one address, which makes one of them unreachable at runtime -
and a check that fires 3591 times for something that never happened buries the one time it matters.

The defect reads as correct on a first run over fresh assets, because the seed knows nothing about
them. It only appears on the second run - and a stable address provider makes every subsequent run a
second run, so it reproduces forever once it starts.

`ApplyAddressRule` now compares the recorded owner against the asset being processed, ordinally. Real
collisions are untouched: a second asset arriving at an address someone else holds still has
`firstOwner != assetPath` and is still reported, as data and as prose.

Comparing at the check rather than filtering the seed is deliberate - it is also correct when the same
path appears twice in one batch, which a seed-side filter would not be.

`SkipExisting = true` suppresses the noise by returning before the check, but it also stops rules from
moving entries between groups, which is a real workflow (ship-in-build ↔ CDN). It is not a workaround.

### Added - `LayoutRuleCollisionTests`

Three EditMode tests, none of which write. `AssetKeepingItsOwnAddress_IsNotACollision` is the
regression guard, and it was verified by reverting the fix and watching it fail with the reported
symptom. `TwoAssetsClaimingOneAddress_IsStillACollision` is the half that must not have been narrowed
away. Both also assert that the structured `Collisions` list and the prose `Errors` agree in each
direction, since they are written at the same site and can drift.

The first asserts `SkipExisting` is off, so it cannot pass by never reaching the check under test.

A third test sweeps 200 assets and asserts no collision anywhere names one asset twice. It is
documented in the file as **not** the regression guard: run against the unfixed processor it passes,
because it reproduces the defect only where an asset's existing address equals its own file name, and
in this project none do.

Thanks to the Icon Match team, who diagnosed this to the line and proposed the fix.

## [4.1.0] - 2026-08-23 - One Editor window, and eleven fixes where the patch had landed on one side of a pair

The stable release of the 4.1 line. Coming from `4.0.1`, this entry is the last increment, not the
whole story: the CDN content pipeline, the tiered-cache rework, reference counting, the pooling
rewrite and the Unity floor correction all landed across `4.1.0-pre.1` through `4.1.0-pre.15`, each
with its own entry below. Read them in order if you are upgrading from 4.0.x; read
[Migrating from 4.0.x](README.md#-migrating-from-40x) if you only want the renames.

Dropping the `-pre` label says the API surface has stopped moving. It does not say every part has
been exercised in the field — the CDN layer's evidence is still local, and
[Not yet validated](README.md#not-yet-validated) is the list of what nobody has run against a real
CDN yet.

### Fixed - eleven defects, almost all the same shape

A full read of the package turned up seventy-two findings; twenty-six survived an adversarial
second pass. Nearly every one had the same cause: a change applied where it was written and never
propagated to its counterpart. Reading the side that changed always looks correct, which is why six
rounds of review had missed them.

| One side was fixed | The other was not |
| :-- | :-- |
| Profile templates renamed `<domain>` to `<cdnBase>` | `InjectRemoteHostFromEnvironment` still matched `<domain>`, so CI host injection replaced nothing and the build then failed telling the operator to run it |
| `CdnRequestDecorator` reset its statics for play mode | `CdnManager` did not, so a second Play session reported itself initialised with no URL rewriter |
| `Install` rebound the host rewriter on a repeat call | It did not rebind the auth provider or the timeout, so a retry after a failed init went out unauthenticated |
| The address import loop disarmed a degraded rule | The label and version loops did not, so a rule that lost some filters imported still enabled, matching wider, and the CLI exited 0 |
| `DownloadProgress.Percent` is a 0..1 fraction, and the UXML bar is `high-value="1"` | The tab divided it by 100, so 4.1.0-pre.15's fix for a dead progress bar shipped it near-dead |
| Version providers write four label shapes | `SemanticVersion` parsed one, so an unparseable label fell through to "match everything" |
| The compile gate rebuilt dependencies | Its cache ignored their timestamps, so a Runtime rename that breaks Editor call sites reported PASS |

Also: the metered-network and free-disk gates ran once before the retry loop rather than inside it,
so a WiFi-to-cellular handoff mid-download resumed over metered data and returned success; a
cancelled download left `CdnDownloadMonitor` reporting a transfer that had stopped; duplicate-address
detection could only see inside its own batch, so the on-import path — one asset per call — could
never catch a collision with an entry that already existed; and four filters memoised answers about
state the rule run itself rewrites without clearing them, so `Match Mode = NoAddress` kept matching
already-addressed assets for the rest of the editor session.

### Added - `Window > Addressable Manager > Open`

One window replaces four windows, ten tabs and twenty-one menu items spread across four root menus —
four of which were registered twice at the same path, which Unity accepts without defining which
handler runs.

**The rail is not a menu.** It is the delivery pipeline — Configure, Author, Build, Publish, Run —
and each stage's colour is that stage's real health right now, with the connector drawn dead below
the first blocked stage. "How far does my content get, and what stops it" is answered before
anything is clicked.

- **Four health states, and the fourth is the point.** `NotMeasured` cannot be constructed without a
  reason, and the stylesheet paints it hollow rather than filled. "We checked and found nothing" and
  "we did not check" rendered identically across the Validator, the build manifest, the restriction
  check and the Runtime Monitor — which is how a CDN nobody had ever reached read as healthy.
- **Validator, grouped by consequence.** "Fails the CI gate" is a checkable claim, not a figure of
  speech: `CatalogVerifier` files an unsatisfied rule under `Problems` unless it is warning-only.
  Rules skipped in Local-only mode get their own group instead of being counted as passing.
- **Profiles conformance** — four profiles, three checks each, and exactly one editable field: the
  origin. Not a second profile editor; Addressables stays the single owner of those values.
- **Layout Rules with a dry run.** The rule system had no preview at all. `PreviewRules` runs the
  identical code path with a dry-run flag, so it cannot describe a run that will not happen, and
  Apply is reachable only past it.
- **Asset Lifetime** — what each scope is holding. The one question Unity's profiler cannot answer,
  because scopes are this package's idea. Leak detection is not implemented and the screen says so
  rather than showing a fabricated list.
- **Ctrl+K** — subsequence search across sections. Navigation only; it does not run actions.

### Added - public diagnostics on `AssetLoader`

`SnapshotLoadedAssets()` and `CachedAssetCount`, so a QA build can log what a scope is holding
without forking the package.

### Changed

- `Window > Addressable Manager > Dashboard` no longer binds Ctrl+Alt+A; the hub does. Three menu
  items were claiming that chord.
- `AddressableRuleMenuItems.OpenLayoutRuleEditor` / `OpenLayoutViewer` are `[Obsolete]` and no longer
  carry menu items. They forward, and stay until 5.0.0.
- `Create Debug Settings` writes to `Assets/Resources/AddressableManager/`, where
  `DebugSettings.Instance` actually looks, and selects an existing asset instead of creating
  `DebugSettings 1`, `DebugSettings 2`, and so on forever.
- `Window > Addressable Manager > Documentation` looks under `Packages/`, not `Assets/` — a UPM
  package is never under `Assets/`, and the error dialog used to send the reader to that same wrong
  path.

### Notes

`CdnManagerWindow` and the Dashboard still work unchanged. Three of the six CDN tabs — Catalog
Inspector, Local Server, Runtime Monitor — are hosted through an adapter rather than rewritten. The
other three were rebuilt as native sections, because the tab was the problem in each case: the
Validator tab listed all fifty-eight rules flat and sorted by id, which is the maintainer's ordering
rather than the user's, and the Build tab offered its two buttons and let the pipeline explain itself
afterwards, which is too late for the failure that matters. `SettingsValidatorTab` and `BuildTab`
remain for `CdnManagerWindow` until that window retires, so nothing moved as one flag day.

## [4.1.0-pre.15] - 2026-08-21 - The pre.14 local-server fix could never fire, and a progress bar that was never wired

Second round of the Icon Match integration report. `4.1.0-pre.14` closed all seven items from the
first round and their CDN tier now works end to end (58/58 contract rules pass, 347 bundles verified,
70 bundles / 10.95 MB fetched with no failed requests). Two new items came out of running it.

### Fixed - the pre.14 local-server fix was disabled by its own change

`4.1.0-pre.14` taught `LocalContentServer` to survive a domain reload by recording the intent in
`SessionState`. That intent was cleared by `Stop()`, and `Stop()` is what
`AssemblyReloadEvents.beforeAssemblyReload` was wired to - so every domain reload cleared the flag
microseconds before the reload that was supposed to read it. The flag was never true on the other
side and the server never came back. **The feature could not fire once.**

The cause was giving one method two jobs: an automatic teardown is not a decision about whether the
server should be running, but it was calling the method that makes that decision. Split:

| | |
| :-- | :-- |
| `Stop()` (public) | the user's decision - clears the intent, then tears down |
| `Shutdown()` (internal) | releases the listener only; the intent is untouched |

`beforeAssemblyReload` and `quitting` now call `Shutdown`.

### Fixed - the Runtime Monitor tab's download bar was never wired

`monitor-download-bar` was queried at construction and then assigned in exactly one place -
`SetDownloadIdle`, which sets it to zero. No branch could show a running download, and there was
nothing to show one from: progress reached only the `IProgress<DownloadProgress>` the caller of
`DownloadAsync` passed, and a background prefetch normally passes `null`.

A bar frozen at "No download in progress" while content is visibly downloading reads as "the CDN is
not working", and it sent a team off diagnosing the wrong thing.

- New `CdnDownloadMonitor`: a snapshot of the last reported progress plus `IsDownloading`, fed by the
  download path **regardless of whether the caller wanted progress**. Also cleared on the failure
  path - a monitor stuck at "downloading" after an error is the same frozen-state lie pointing the
  other way.
- The tab reads it on its existing one-second refresh. Polling rather than an event: an event would
  need unsubscribing across domain reloads and play-mode transitions for a cosmetic row.
- Documented limit: a fast download can finish between two refreshes - 70 bundles land in about a
  second against a local server. The Server tab's request log remains the reliable view; this row is
  for watching a real CDN transfer.

### On which profile CI should build with

Asked in the report, answered here rather than left implicit. Since `4.1.0-pre.14` unified the path
suffix, building with `Local` and switching environment at runtime is path-safe - but it carries a
failure mode that building with the target profile does not: if the rewrite fails to apply (an origin
missing from `CdnSettings`, or boot not reaching `SetEnvironment`), the URL baked into the catalog is
`http://localhost:8080`, and a shipped build cannot be rescued.

**Build with the profile for the environment you are shipping to.** Runtime switching is for QA
pointing one existing build at another environment. This is what CDN guide 3.4 says.

### Verification

Compile gate PASS on both assemblies. `Tests/Editor/CdnDownloadMonitorTests.cs` added (3 cases,
including the failure path that must clear the flag).

**The EditMode suite did not run for this release.** The project was locked by an open Unity Editor
(`Temp/UnityLockfile`), so batchmode could not acquire it, and this build was cut at the requester's
direction to unblock testing in their production project. The last full green run was 168/168 at
`4.1.0-pre.14`; the changes here are one Editor-only method split and one new static plus its call
sites, all compiling clean, but that is a weaker claim than a test run and is recorded as such.

## [4.1.0-pre.14] - 2026-08-21 - Seven ways the CDN layer let a broken build through without a word

An integration report (Icon Match) traced why its CDN tier had never worked, and the finding was not
a bug in the content path - it was that every guard the package has let the case through silently:
the build reported SUCCESS, the verifier PASSED, and the runtime skipped a wrong URL without logging
it. All seven items were verified against source before acting on them.

### The central defect: two sources of truth about the host, and nothing compared them

`Remote.LoadPath` on the Addressables profile decides the URL **baked into the catalog** at build
time. `CdnSettings.environments[].baseUrl` decides the origin `HostRewriter` swaps **to** at runtime.
They only work together when the baked origin is one of the configured base URLs, because `Rewrite`
only rewrites a URL whose origin it recognises.

Nothing stated that invariant, let alone checked it - `SettingsContract` had 14 rules and zero
references to `CdnSettings`. The package's own generated Dev/Staging/Prod profiles ship a literal
placeholder host, so a build against them was the *default* way to hit this.

### Fixed

- **New contract rule `settings.RemoteOriginIsKnown`.** The active profile's remote origins must
  match a `CdnEnvironment.BaseUrl`. No auto-fix: adding the origin to `CdnSettings` and rebuilding
  against a different profile are both valid answers and they mean different things.

- **The build refuses to start on an unresolved `<...>` host placeholder.** The generated templates'
  own doc comments called those values a fallback that env-var injection was meant to replace -
  nothing called the injector, and nothing checked, so the placeholder was what got baked into the
  catalog. Filling it stays the user's job; the package's job is to refuse to guess.

- **`HostRewriter` warns once per unknown origin, regardless of `logUrlRewrites`.** Skipping an
  unrecognised URL is deliberate, but it is also exactly what a misconfigured build looks like, and
  the only evidence was behind a flag that defaults to false. Once per origin, not per request -
  this fires on every bundle.

- **Profile path templates are now uniform.** `Local` used `/[BuildTarget]/bundles` while
  Dev/Staging/Prod used `/game/[BuildTarget]/bundles`, so content built with one profile and
  rewritten to another landed at a different path - two routes, two CDN layouts, and whichever you
  had not tested was broken. The suffix now comes from one constant per path kind. The placeholder is
  renamed `<cdnBase>`, because what belongs there is an origin (optionally including a path prefix),
  not a hostname.

- **`CdnBuildMode { Remote, LocalOnly }` on `CdnSettings`, as a first-class setting.** In
  `LocalOnly` the remote rules are skipped rather than reported as failing, and the build writes no
  remote manifest and runs no remote verification. This removes the tug-of-war that made a
  deliberately-local project unworkable: `settings.BuildRemoteCatalog` carried an unconditional
  auto-fix, so every "Fix All" and every unattended `CdnSetupCLI` run switched it back on. A project
  with no `CdnSettings` asset is treated as `LocalOnly` - the runtime CDN layer cannot function
  without that asset, so a project without one is not publishing to a CDN whatever else it says.

- **`DownloadPolicy.CatalogOperationTimeoutSeconds` (default 5s).** `Application.internetReachability`
  reports the interface, not whether anything answers, so a captive portal or one bar of signal passed
  the reachability guard and the catalog check then never returned - hanging the caller's first
  screen. A deadline on the whole operation now returns the offline answer instead. Distinct from
  `TimeoutSeconds`, which bounds a single request, and linked to the caller's token so cancellation
  still works.

- **The local content server survives a domain reload.** Entering play mode wiped the static holding
  it and took the `HttpListener` with it; `[InitializeOnLoad]` recreated the instance but not the
  running server. The symptom was `ConnectionError : Cannot connect to destination host` - which
  reads as a broken CDN, not as a server that quietly died, and on Addressables 2.9.1 a failed
  catalog fetch then poisons `ResourceManager.Update` for the rest of the session (see
  `4.1.0-pre.12`). The intent is now kept in `SessionState`, whose lifetime is exactly the server's:
  it survives a domain reload and dies when the editor closes.

### Documentation

New sections in the CDN guide: **3.4 Where the URL comes from** (the origin+suffix convention, why
every origin must be declared, and which profile CI should build with) and **3.5 Turning the CDN
off**.

### Verification

Compile gate PASS on both assemblies, EditMode 168/168 PASS, doc sweep 314/314.

## [4.1.0-pre.13] - 2026-08-20 - "The restriction check could not run" was the wrong answer half the time

Reported from the CDN Manager's Update Preview tab, which refused to evaluate:

> No groups with static content (Cannot Change Post Release) detected. This is a configuration
> failure ... If no content is static, ensure that at least one group has the
> ContentUpdateGroupSchema with StaticContent enabled.

That advice contradicts itself - *if no content is static, mark a group static* - which is the tell
that one branch was being asked to answer two different questions.

### Fixed

`ContentUpdateRestrictions` treated "no group is marked Cannot Change Post Release" as a
configuration failure regardless of why, and returned `CanEvaluate = false`. Two situations were
collapsed into one, and they need opposite answers:

- **Something ships inside the player and was not declared.** A group whose `BuildPath` resolves to a
  Local path is baked into the build and can never be replaced by a content update, so it must be
  marked Cannot Change Post Release. Leaving it unmarked is precisely the misconfiguration this check
  exists to catch, and the check genuinely cannot run. This now reports as before - and **names the
  offending groups**, instead of leaving the user to guess which of them needs the flag.

- **Nothing is immutable.** Every group builds remote, which is an ordinary CDN layout: there is no
  content a content update could break, so the restriction check has nothing to guard and passes for
  that reason. This used to be blocked outright. It now passes, with a message that says *why* -
  "this check passes for that reason, not because content was compared" - so a vacuous pass is never
  mistaken for a real comparison against the content state.

The distinguishing fact is the group's build path, which the package already reads for the
`group:<name>:RemotePathsConsistent` contract rule added in `4.1.0-pre.10`.

### Added

`Tests/Editor/ContentUpdateRestrictionsTests.cs` - three cases pinning both branches and the
no-groups edge, so they cannot collapse back into a single answer. The decision was extracted to
`EvaluateStaticContentConfiguration` (internal) so it can be tested on group configuration alone,
without constructing an `addressables_content_state.bin`. EditMode 165 -> 168.

### Note on the failure this was found alongside

Nothing here changes the underlying Addressables 2.9.1 defect described in `4.1.0-pre.12`: a failed
catalog check still ends the session with a misleading re-entrancy flood. These are separate
problems that surfaced in the same integration.

### Verification

Compile gate PASS on both assemblies, EditMode 168/168 PASS, doc sweep 313/313.

## [4.1.0-pre.12] - 2026-08-20 - A failed catalog check ends the session, and Unity blames the wrong thing

Root-caused from a production log. The reported symptom was thousands of

```
Exception: Reentering the Update method is not allowed.  This can happen when calling
WaitForCompletion on an operation while inside of a callback.
```

That message is wrong on both counts: no `WaitForCompletion` was called, and nothing re-entered. The
two-frame stack (`ResourceManager.Update` <- `MonoBehaviourCallbackHooks.Update`) is the giveaway -
real re-entrancy would carry the caller's frames between them. This is the ordinary once-per-frame
call finding a flag that was already set.

The actual chain, all inside Addressables 2.9.1:

1. `CheckCatalogsOperation` failed - the catalog hash URL was unreachable
   (`http://localhost:8080/Android/catalog/1.0/catalog_1.0.hash`, the Local profile's default, with no
   local server running).
2. `CheckCatalogsOperation.Destroy()` is `m_DepOp.Release()` with no `IsValid()` guard
   (CheckCatalogsOperation.cs:62-65). After a failure that handle is already invalid, so
   `AsyncOperationHandle.get_InternalOp` throws **"Attempting to use an invalid operation handle"**.
3. That throw escapes `ResourceManager.ExecuteDeferredCallbacks` (ResourceManager.cs:1065), called
   from `ResourceManager.Update`, which sets `m_InsideUpdateMethod = true` at line 1100 and clears it
   at 1121 **with no try/finally**. The flag stays set for the rest of the session.
4. Every frame thereafter throws the re-entrancy message, and Addressables is unusable until play
   mode is exited.

Both defects are Unity's, and neither can be caught from this package: the throw happens on Unity's
own stack inside `Update`. What this package can do is stop the developer losing an afternoon to it.

### Added

- `CatalogService` now explains the failure at the moment it becomes inevitable - naming the URL, the
  two Addressables defects, the misleading message about to flood the console, and the fact that only
  exiting play mode recovers. Logged once per service, not per check.
- A `Reentering the Update method` section in the troubleshooting guide, with the quick-diagnosis row
  that points at it, how to find the real first error, and the four causes worth checking.

### Not fixed, because it cannot be

The package cannot prevent or contain either defect. It also cannot pre-flight its way out reliably: a
check that passes can still fail a moment later on the real request. Reporting precisely is the whole
of what is available here.

Relevant if you hit this: the Local profile's catalog path is `http://localhost:8080/...`, which needs
the CDN Manager's Local Server started. `4.1.0-pre.10` fixed a related defect where creating the
profile variable pushed that localhost default into **every** profile, Prod included.

### Verification

Compile gate PASS on both assemblies, EditMode 165/165 PASS, doc sweep 313/313.

## [4.1.0-pre.11] - 2026-08-20 - The auth hook runs inside Addressables' update loop, and never said so

Reported from a production integration: `Reentering the Update method is not allowed. This can happen
when calling WaitForCompletion on an operation while inside of a callback.` thrown from
`ResourceManager.Update`.

The package itself never calls `WaitForCompletion` - grep over `Runtime/` and `Editor/` returns
nothing - and neither of its await paths resumes inside the update loop: Addressables builds its
`Task` with `RunContinuationsAsynchronously` (`AsyncOperationBase.cs:247`), and UniTask's handle
awaiter polls on the PlayerLoop (`AddressablesAsyncExtensions.cs:96,157`). So code after
`await Assets.Load(...)` is not the hazard.

But two pieces of package code *are* invoked from inside `ResourceManager.Update`, and both call
straight back out into consumer delegates: `Addressables.WebRequestOverride` and
`Addressables.InternalIdTransformFunc`, installed by `CdnRequestDecorator`. Between them they invoke
`CdnManager.AuthTokenProvider` on **every** bundle, catalog and hash request, plus whatever hooks the
project already had installed.

`AuthTokenProvider`'s documentation said only "Called on every request, so a refreshed token is
picked up without reinstalling" - which reads as an invitation to fetch a token there. Fetching one
by blocking, or by awaiting an Addressables operation, re-enters the update loop and produces exactly
the reported exception, from a stack that names only Unity's own frames and never the delegate that
caused it.

### Fixed

- **Consumer delegates invoked from the hooks are isolated.** A throwing hook no longer propagates
  into the middle of Addressables' update; it is caught, and the log **names which delegate threw**
  along with the constraint it violated. The request continues without that hook's contribution - an
  ordinary 401 or an untransformed id, both of which `CdnErrorMapper` already classifies.

- **The constraint is documented where the delegate is supplied**: on `CdnManager.AuthTokenProvider`,
  in the package README, and in the CDN usage guide. Return a token you already hold; refresh it on
  your own schedule.

- **The README's auth example was wrong.** It showed `() => $"Bearer {token}"` while the decorator
  writes the header as `Bearer {token}` itself, so following it produced
  `Authorization: Bearer Bearer <token>` and a 401 that looks like an expired credential.

### Still open

This release fixes the package's share: an undocumented re-entrancy-hostile callback whose failure
mode was unreadable. It cannot stop a `WaitForCompletion` in project code, and the reporting
integration's full stack has not been captured yet, so whether `AuthTokenProvider` was the trigger in
that particular case is unconfirmed. With this release the log will name the delegate if it was.

### Verification

Compile gate PASS on both assemblies, EditMode 165/165 PASS, and the mechanical doc sweep still
resolves all 313 assertions.

## [4.1.0-pre.10] - 2026-08-20 - Everything the package said it did, checked against what it does

A review that started from three reported design flaws and ended up auditing the package against
its own documentation. The three findings were real; two of the three fixes proposed for them were
wrong, and are corrected here. Beyond that: a whole-subsystem CDN review that had never been run, a
2.3.1 -> 2.9.1 API drift sweep that had never been run, and a doc-vs-code audit that extracted 388
checkable claims from the documentation and followed each one to the code. 248 held.

One defect class runs through nearly all of it: **the package counted intent and never read back
effect.** Counters counted attempts, not results. `IsPublishable` had no term for how much was
actually checked. The build manifest was generated by scanning the output directory and then
verified against that same directory. Nothing asked "is what I just wrote actually there?", so every
divergence left the building as a green tick.

### Fixed - Addressables 2.9.1 API drift

The package declares `com.unity.addressables` 2.9.1 while much of this code was written against
2.3.x. Unity changed behaviour without changing signatures, so the drift compiled cleanly.

- **`AddressableAssetEntry.labels` returns a copy on 2.9.1** (`m_Labels.ToHashSet()`), where 2.3.1
  returned the live field. Every write through it was a silent no-op. Unity migrated all of its own
  writes off the property in the same release - it added an internal `RemoveLabel` and routed
  `SetLabel`, `CreateKeyList` and `RenameLabel` through it - while leaving reads on it. All three
  sites now use `entry.SetLabel(..., postEvent: false)` with one `BatchModification` event per run,
  and the counters follow `SetLabel`'s return value. The removal site that strips stale `version:`
  labels is included: fixing only the two additions would have made version labels accumulate
  forever, so every version bump left the previous one attached.

  There is a second, version-independent layer underneath. Entry labels are serialized into each
  **group** asset, not into `AddressableAssetSettings.asset`, and the only thing that dirties a group
  asset is `SetLabel -> entry.SetDirty -> parentGroup.SetDirty(groupModified: true)`. The processor
  dirtied only the settings object, so on 2.3.1 the labels did not reach disk either.

- **`AssetReference.LoadAssetAsync()` is single-use per instance.** The second load returned an
  invalid default handle whose `.Task` throws, which surfaced to the caller as a plain `null`. Loads
  now go through `Addressables.LoadAssetAsync<T>(RuntimeKey)`, which is what that method calls
  internally.

### Fixed - content that shipped to the wrong place, silently

- **A target group name containing `/` created one group per matched asset.** `CreateGroup` rewrites
  `/` and `\` to `-` before creating the asset, but the rule looked the group up by the raw string,
  so `FindGroup` never matched - and `GetOrCreateTargetGroup` runs once per **asset**, with
  `FindUniqueGroupName` appending an incrementing suffix each time. A rule targeting `Icons/Small`
  over N assets produced `Icons-Small`, `Icons-Small1`, `Icons-Small2` ...: N groups, one entry each,
  N bundles, with the counters reporting success. Names are normalized before lookup now.

- **`AssetReference` loads keyed the cache by `AssetGUID`, not `RuntimeKey`.** Two sub-object
  references into one atlas share a GUID, so the second caller silently received the first caller's
  sprite. Keyed by `RuntimeKey` now, with `AssetCacheKey.MatchesAddress` keeping GUID-based
  invalidation and release reaching those entries.

- **A load in flight across a catalog update cached its pre-update handle** after the invalidation
  sweep had already run. `AssetLoader` carries an invalidation epoch; a load that crosses one is
  handed to its caller but not cached.

- **`AddressRule` gains an optional `AddressableAssetGroupTemplate`.** Without one, a re-created
  group inherits the DefaultGroup's schema *values* - `Object.Instantiate` copies them, which in a
  stock project means PackTogether and Local paths - so a remote, label-split group came back local
  and packed-together with the entry count unchanged. The rule now says so loudly when it has to
  create a group with no template.

- **Duplicate generated addresses are a `ProcessResult` error.** Nothing checked: `SetAddress`
  accepts any duplicate, `RuleValidator` checks rule *names*, and `RuleConflictDetector` was only
  reachable from read-only surfaces. Two entries sharing one address makes one asset unreachable.

- **`BatchAddressUpdater`**: `FindAndReplace` matched literally but substituted by regex, so
  `FindAndReplace("[UI]", "ui")` rewrote every U and I in every address that merely contained the
  literal; `RemovePrefix` could blank an address, after which Addressables substitutes the asset
  path; `ConvertToLowercase` used culture-sensitive `ToLower` (`I` -> `i` fails on a tr-TR editor)
  and could collide two addresses undetected.

- **`AddressableAutoProcessor` fed raw import paths to the processor.** Unity reports **folders** in
  `importedAssets`/`movedAssets`, and `PathFilter` is pure string matching, so a folder could be made
  addressable - and Addressables expands a folder entry into everything beneath it. One shared
  `IsRuleEligibleAsset` predicate now, instead of three copies with different rules.

### Fixed - CDN

- **The request timeout was applied to bundle downloads.** `UnityWebRequest.timeout` caps the whole
  transfer, while the 30s this package configures everywhere is Addressables' **idle** timeout -
  `AssetBundleProvider` resets it on every byte received and never aborts a progressing download,
  which is why Addressables leaves the field unset for bundles and sets it only for small
  catalog/hash/text files. Any bundle slower than 30 seconds was aborted mid-flight at full speed,
  and because Unity's cache only commits completed downloads, every retry restarted from zero and hit
  the same wall - a permanently un-downloadable bundle on exactly the connections that need a CDN.

- **A second `Install()` did not rebind**, so the hooks kept the rewriter from the first attempt while
  the facade reported the new environment. **`HostRewriter` seeded known origins with unresolved
  `{platform}`/`{appVersion}`**, so an origin from a non-active environment could never match a baked
  URL and `Rewrite()` returned it unchanged. **`CdnErrorMapper` tested `DataProcessingError` only
  under status 0**, but a corrupt bundle reports it with HTTP 200, so the most important corruption
  case fell through to `Unknown` - not retryable, matching no repair path.

- **No concurrency guard existed anywhere.** Two overlapping `ApplyUpdateAsync` calls both reached
  `Addressables.UpdateCatalogs`, which mutates shared locator state with no interlock.

- **Creating a profile variable pushes its default into every profile**, so Dev/Staging/Prod all
  silently acquired the localhost catalog path and a Prod build baked `http://localhost:8080` as its
  remote catalog URL.

- **New per-group contract rule**: `BuildPath` and `LoadPath` must both be remote or both be local.
  `CatalogVerifier` structurally cannot catch this - it only proves the output matches the manifest
  the same build just wrote. It also passed when the manifest listed **zero** bundles, and
  `CatalogInspection.IsPublishable` could not be false when nothing had been checked; both are gated
  now.

### Fixed - ownership

- **`TieredCache.Set` / `ThreadSafeCacheManager.Set` had two opposite ownership outcomes behind one
  `void` return.** The fresh-key path takes its own reference; the duplicate-key path spent the
  *caller's*. A caller doing the documented thing then over-released, which unloaded the asset out
  from under `AssetLoader` whenever the loader held the second reference - and `IsValid` could not
  detect it, because the count was still 1. The contract is uniform now: the caller always keeps its
  reference. The two rejection paths in `ThreadSafeCacheManager` are separated, since the `TryAdd`
  failure path releases the cache's own reference and was always correct.

- **`LoadAssetSmartAsync` / `ToSmart` with `autoRelease: false` orphaned the birth reference.** Those
  overloads create the handle and return only the wrapper, so nothing could ever release it - and the
  finalizer's leak warning is explicitly suppressed for non-owning wrappers.

### Added - features that were documented, or half-built, but did not work

- **`ConstantLabelProvider`.** Documented in two guides, used by roughly thirty worked examples,
  listed in the editor tools guide, referenced by the shipped templates - and absent. `LabelRule` has
  no inline label list, so there was no way to emit a constant label at all.

- **`LayoutRuleData.VersionExpression` / `ExcludeUnversioned` are enforced.** Serialized, exported,
  CLI-writable, syntax-validated by the CLI - and read by nothing. An unparseable expression now
  aborts the run, which is what its error message always claimed; falling through left no filter set
  and applied every rule to every asset. `VersionExpression.TryParse` also gained the four comparison
  forms (`>=`, `>`, `<=`, `<`) that the CLI's own error message has always advertised and the parser
  then rejected.

- **`LabelRule.AppendToExisting` is honoured.** Serialized, exposed, exported - never read. Labels
  were only ever appended. `version:` labels are exempt from replacement, since they belong to the
  version-rule path.

- **`CompositeLayoutRuleData`'s "Respect Source Order" survives.** The composite preserved the order
  and the processor then re-sorted by priority in all three of its loops, discarding it.
  `LayoutRuleData.PreserveRuleOrder` carries the decision through.

- **`RuleConflictDetector.PreviewRuleConflicts` has a caller.** It had zero, anywhere in the
  repository, and iterated rules in a different order than the apply path - a preview that disagreed
  with the run it was previewing. Aligned, and wired into the Rule Editor's preview panel.

- **`AddressablePreloadConfig.validateOnBuild` / `failBuildOnError` are honoured** by a new
  `IPreprocessBuildWithReport`. The package had no build callback of any kind, so a team that enabled
  both and relied on the build to catch a broken `AssetReference` shipped it. Its `preloadEntries`,
  `loadInParallel` and `maxConcurrentLoads` are still read by nothing and are documented as such.

- **The Dashboard's Settings tab writes to `DebugSettings.Instance`.** Four of its six controls were
  queried into fields at initialisation and never read again, and the window never loaded the asset.

- **The Scopes tab cleanup buttons release real handles** via `ScopeManager.ClearScope`. They used to
  touch only the Dashboard's bookkeeping: rows vanished and the memory figure dropped to zero while
  every asset stayed loaded, under a dialog reading "This will release all tracked assets".

- **`SettingsContract` has an extension point**: `[SettingsRuleProvider]` static methods, discovered
  via `TypeCache`. Not a static event - those lose their subscribers on domain reload, and an absent
  rule reads as green.

- **Rule templates work, and rule JSON is portable.** Four of the five shipped templates imported into
  rule sets that could never run: every rule carried `"filters": []` and empty provider paths, the
  importer read only paths, so every rule got no filters and a null provider, failed validation, and
  aborted the whole run - while the import reported success. `RuleSerializer` now resolves a
  filter/provider by **type name** when no asset path resolves, and carries each one's own serialized
  configuration inline, so a rule set survives moving between projects. A rule that ends up with no
  filters is imported disabled and counted as a failure.

- **`PathFilter` warns when a wildcard pattern sits in a literal match mode.** `Contains` is the
  default, so the Quick Start's own `Assets/UI/**/*.png` matched nothing, silently. The mode is not
  switched automatically: a saved mode is the user's decision.

### Documentation

Every document was rewritten against the code and then checked twice - an adversarial pass that
re-tested each deletion and sampled signatures member by member, and a mechanical sweep that extracts
every `Type.Member` and menu path still asserted anywhere in the docs and greps it back against the
source. 313 assertions, all resolvable.

Deleted for want of an implementation: keyboard shortcuts, Development/Testing/Production presets, an
Examples submenu, a separate Debug Settings window, a Layout Viewer inspector panel, and an entire
runtime API built on `AddressableManager.LoadAsync` - `AddressableManager` is a namespace, not a
type, and none of those members exist.

Limits are stated rather than omitted: `PathFilter`'s `Contains` default, Editor-only monitoring, the
non-thread-safe standalone `TieredCache`, and `Simple.Load` handing back a raw asset whose lifetime
the caller does not control.

### Known, not fixed

- **The catalog-update path has never run.** `Addressables.CheckForCatalogUpdates()` always returned
  an empty list before 2.9 - `CanUpdateContent` required `Dependencies.Count == 2` while builds emit
  three - so `CheckForUpdateAsync -> ApplyUpdateAsync -> UpdateCatalogs -> InvalidateLoaderCaches` was
  dead code until the 2.9.1 bump. It compiles, it has unit tests around it, and nothing has yet proved
  it works end to end. That needs an integration test that swaps a real catalog and asserts a
  previously cached handle is re-resolved.

- **`Simple.Load`'s raw asset can be destroyed by a catalog invalidation.** After its `Dispose()`, the
  cache's reference is the only thing keeping the object alive, and the caller holds no handle with
  which to object. Making invalidation retain those would trade a rare crash for unbounded retention
  across every update - a product decision, so the exposure is documented on the API instead.

### Verification

Compile gate PASS on both assemblies; Unity compiles all four assemblies including both test
assemblies with 0 CS errors; EditMode 165/165 PASS.

## [4.1.0-pre.9] - 2026-08-17 - Four review findings, and the CI step that could not see a broken build

An external review of the branch (Qodo, on PR #3) raised four items. Three were real and are fixed
here; one was not, and is answered below rather than acted on. Two further defects found while
fixing them — one that the review had half of, one that a rewritten README exposed — are fixed too.

### Fixed — string comparisons that were wrong for URLs

Both were the same mistake in different clothes: reaching for a `string` overload that takes no
`StringComparison` and inheriting a default that is wrong for a URL.

- **`HostRewriter.ResolveTokens` recognised `{Platform}` and then failed to replace it.** Detection
  used `IndexOf(..., OrdinalIgnoreCase)`; substitution used the two-argument `string.Replace`, which
  is case-**sensitive**. Any casing other than the documented all-lowercase form was found, skipped,
  and travelled on as literal brace characters in a request path. Going out of the way to detect any
  casing and then honouring one is the kind of half-implemented intent that reads as correct at both
  call sites. Replacement is now case-insensitive at every occurrence, via an internal
  `ReplaceIgnoreCase` helper rather than the three-argument `string.Replace` overload, which is not
  available on every runtime profile this package must compile against.

- **`CdnEnvironment.IsValid` rejected `HTTPS://cdn.example.com`.** URI schemes are case-insensitive
  (RFC 3986 §3.1), and this is reachable in practice: `CdnManager` routes the `CDN_BASE_URL`
  environment-variable override through `IsValid`, and a CI variable is typed by hand. A valid
  override was discarded and the build stayed on the environment baked in at build time, with only a
  validation message to say so. The scheme check is now `OrdinalIgnoreCase`.

  The review flagged the scheme check. The `EndsWith("/")` immediately above it had the same defect
  and is fixed in the same pass: the no-comparison overloads of `StartsWith` and `EndsWith` are
  `CurrentCulture`, not merely case-sensitive, so whether a URL was considered valid could depend on
  the machine's locale. Both are now explicit — `Ordinal` for the punctuation, `OrdinalIgnoreCase`
  for the scheme.

Covered by `Tests/Editor/CdnUrlCasingTests.cs`, which is EditMode on purpose: both methods are pure
functions over strings, and a test that drags in a packed build and a local HTTP server to check a
string comparison is a test nobody runs.

### Fixed — batchmode automation could pass on a build that never compiled

Unity exits 0 from `-executeMethod` when the assembly failed to compile. The method never runs, no
side effect happens, and the step goes green. This repository's `CLAUDE.md` carries it as a hard
rule, and `4.1.0-pre.6` applied that rule to `AddressableCLI` — but never to `Editor/Cdn/`.

- **`CdnSetupCLI.ApplyPhaseZeroSetup` had no gate at all**, and it is the worst of the three to run
  blind: it mutates `AddressableAssetSettings` and saves assets, so a run against a half-compiled
  editor writes profile and schema changes derived from whatever still resolved.
- **`CdnBuildCLI.BuildContent` and `BuildContentUpdate` had none either** — only `VerifyOutput` did.
  A delta build also writes the `content_state.bin` that every later update diffs against, so a
  silent no-op there poisons the baseline permanently.

- **New `ci/unity-run.sh`, and both content workflows now route every Unity step through it.** The
  in-assembly gate cannot cover the case it exists for — it lives in the assembly that is broken —
  so the log has to be read from outside. The script scans for `error CS`, then for the `FAILURE:`
  prefix the CDN CLIs log before exiting non-zero, then checks Unity's exit code last as the least
  trustworthy of the three, and finally checks that the artifact the step was supposed to produce
  exists. The two build steps pass `--expect ServerData/build-manifest.json`; a run that reports
  success and leaves no manifest is exactly the failure an exit code hides.

  Note the workflows have still never been executed — see *Not yet validated*. This closes a
  false-green path in them by construction; it is not a report that they were run.

### Not a defect — `CdnManager` and the dual-signature convention

The review also reported that `CdnManager`'s public async methods expose only `UniTask` signatures
under `UNITASK_PRESENT`, and asked for both. **No change made, and the request cannot be satisfied
as stated.** `UniTask<CdnResult<bool>> InitializeAsync(string, CancellationToken)` and
`Task<CdnResult<bool>> InitializeAsync(string, CancellationToken)` differ only in return type; C#
does not overload on return type. Compiling exactly that shape with the editor's own Roslyn gives:

```
error CS0111: Type 'Probe' already defines a member called 'InitializeAsync'
              with the same parameter types
```

The convention in this package is one signature per configuration, selected by the define — the
`#if UNITASK_PRESENT` / `#else` form that `CdnManager` already uses and that `AssetLoader` uses at
`:716` and `:906`. `CdnManager` is a compliant file.

There *is* a real gap of this kind, and it is not in the CDN layer: `SimpleAPI.cs` contains zero
`#if UNITASK_PRESENT` blocks and all eight of its async members return `Task` or are `async void`;
`Standard` has two dual members and `Advanced` one. That is now documented per surface under
[Task or UniTask](README.md#-task-or-unitask) rather than left to the blanket claim, because
assuming the blanket claim costs a compile error. Whether to make the tiers dual is an API decision
and is not made here.

### Fixed — documentation

- The `4.1.0-pre.7` entry said `HybridScope` was "Retired". It was not: the class is public and
  carries no `[Obsolete]`; only its two defects were fixed. Corrected in place. Whether to retire it
  is an open decision recorded in `Documentation/LIFETIME_DESIGN.md`, not something this release
  settles.

### Verification

Compile gate PASS on both assemblies. Runtime verified against Unity 2022.3.62f3 in both the `Task`
and `UniTask` configurations, 0 real errors each. **165/165 EditMode** (142 before; `CdnUrlCasingTests`
adds 23). `ci/unity-run.sh` passes `bash -n`; both workflow files parse as YAML.

`ci/unity-run.sh` itself has not been executed against a real Unity run, because the workflows it is
wired into have never been executed — see *Not yet validated*. Its logic is checked by inspection and
its syntax by `bash -n`; the first real exercise will be the first content build anyone runs.

## [4.1.0-pre.8] - 2026-08-17 - The minimum-Unity claim was wrong

Documentation only. No runtime or editor code changed from `pre.7`; if you are already on `pre.7`
and not confused about which Unity versions this package supports, you can skip it.

### Fixed

- **`pre.6` and `pre.7` both said the Unity floor "will drop to 2022.3" once the editor assembly
  had been compiled there. That was never possible.** `com.unity.addressables` 2.9.1 declares
  `unity: 2023.1` itself, so the dependency sets the floor and this package cannot go below it —
  a fact this changelog already recorded correctly back in `pre.1` and which the later entries
  contradicted. Both wordings are corrected in place.

  What this changes for you: nothing, if you were reading `package.json`. If you were waiting for
  2022.3 support before adopting, stop waiting — it is not coming while Addressables 2.9.1 is the
  dependency.

- **`README.md` at the repository root listed `Unity 2022.3+` and `com.unity.addressables 2.3.1+`.**
  Both wrong, and wrong in the direction that wastes an afternoon: 2.3.1 does not compile this
  package at all, and a 2022.3 project cannot resolve Addressables 2.9.1. The package's own
  `README.md` — the one shipped inside the UPM package — was already correct at `2023.1` / `2.9.1`;
  the stale copy was the one people browsing GitHub read first.

### About the 2022.3 compile runs in earlier entries

They are still worth running and the results still stand, but they mean something narrower than
those entries implied. `Tools/check-min-unity-api.sh` is pointed at 2022.3.62f3 because that is the
oldest editor installed here, not because 2022.3 is supported. Compiling clean against an editor
*older* than the real floor is a strict superset of the check that matters, so it remains a useful
canary for "did we reach for an API newer than we are allowed to use" — which is exactly the defect
that made `4.1.0-pre.4` uncompilable. Pointing it at a 2023.1 editor would be the precise check.

### Verification

Compile gate PASS on both assemblies. The EditMode suite was **not** re-run for this release and
does not need to be: `git diff pre.7..pre.8` touches two `.md` files and the `version` field of
`package.json`. No `.cs` file changed.

## [4.1.0-pre.7] - 2026-08-17 - Tiering becomes a setting, not a second loader

`pre.6` shipped the reference-counting fixes. This one finishes the review: the loader fork is
retired, the pooling layer gets the twenty-odd defects that never made it into the original work
order, and the three API stubs `pre.6` shipped with are gone.

**The headline is a deprecation, and it is source-compatible.** `TieredAssetLoader` still compiles
and still runs; it is now a forwarder. See below for the one migration that is not a rename.

### Fixed — the three stubs `pre.6` shipped

- **`Simple.Release<T>` was a single `Debug.Log`.** A caller believed they had released something.
  The address is not recoverable from an asset instance — `AssetLoader._assetCache` is keyed
  `(address, Type)` with no reverse map, and Addressables offers no reverse lookup — so the method
  is `[Obsolete]` rather than quietly reimplemented, and **`Simple.ReleaseAddress(string)`** is added
  as the one that actually works. It is deliberately not a `Release(string)` overload: a non-generic
  `Release(string)` would out-rank `Release<T>` in overload resolution and silently re-bind existing
  `Simple.Release(someString)` call sites.
- **`Standard.ClearCache(scopeName)` was a log-then-return stub.** Implemented, now that scopes
  register with `ScopeManager`. An unregistered scope id logs an error naming the id *and*
  enumerating the live scopes, instead of a message that read like success.
- **`Standard.LoadScene<T>` never loaded a scene.** It loaded an asset and bound it to the *active*
  scene rather than the caller's — verified against current code, not taken from the review. There
  is no scene-loading capability anywhere in the package. It is `[Obsolete]`, pointing at the new
  **`Standard.LoadIntoSceneScope<T>(address)`** and **`(address, Scene)`**, which say what they do
  and let the caller name the scene. The parameterless form now passes
  `SceneManager.GetActiveScene()` explicitly, so that choice is visible at the call site.

### Fixed — pooling, the part the review never wrote down

The handoff's reviewer verified 66 items but the summary was truncated after item 43; the pooling
group was cut mid-sentence with a note that at least three or four items remained. Re-derived from
the code, that turned out to be **twenty-two**. The ones that bite in a real game:

- **A pooled instance destroyed by a scene load poisoned its pool permanently.** `Despawn` bailed on
  the destroyed object before untracking it, so `ClearPool` refused forever, the template handle was
  never released, and `DynamicPool` — reading an `activeCount` that could only climb — grew to
  `MaxSize` and never shrank again. There is now a sweep, driven by `sceneUnloaded`, a maintenance
  pump, and `ClearPool` itself.
- **`preloadCount` meant three different things** across the two create methods and the two
  adapters, and the dynamic path fired the caller's `onGet` while the other did not. One code path
  now, one meaning.
- **Auto grow/shrink only worked under continuous churn.** Shrink was evaluated on `Release`, so a
  wave that spawned 40, despawned 40 and then went quiet kept all 40 resident for the session. A
  pump now evaluates on a clock.
- **`Get()` activated the instance before positioning it**, so `OnEnable` ran at the *previous*
  user's position — a `playOnAwake` particle system emitted a frame at the last despawn site, and
  anything reading `transform.position` in `OnEnable` read stale data. Pose is applied first now.
- **Preload logs reported the requested count, not the achieved one.** `CreatePoolAsync("X",
  preloadCount: 50, maxSize: 10)` logged `"Preloaded 50/50"` with ten pooled and forty destroyed.
  `IMeasuredResizablePool<T>` returns what actually happened, and every log reports that.
- Use-after-`Dispose` was a thrown `ObjectDisposedException` on one pool type and a `null` on
  another, through the same `IObjectPool<T>`. One contract now: log and return neutral, never throw.
- `ClearPool` had no way to report *why* it did nothing, so shutdown code looping over addresses
  could not tell that every call had refused. `TryClearPool` returns a named outcome.
- Also: per-address `[Pools]` holders leaked on clear; an auto-create landing after a teardown
  resurrected a pool; a dead caller-supplied `poolRoot` was dereferenced unguarded; `MaxSize = 0` and
  `ShrinkFactor = 0` were accepted and then quietly meant something else; `ResizePool` refused pools
  it could in fact resize.

- **`Assets.Spawn`, `Assets.Despawn` and `Assets.CreatePool` threw `NullReferenceException` during
  shutdown.** `AddressablesFacade.Initialize()` legitimately leaves the pool manager null when the
  global scope is already gone, and six Facade members went through that field unguarded.

### Fixed — threading

- **`UnityMainThreadDispatcher.EnqueueAndWait` could hard-hang the main thread** — no timeout, no
  log — by waiting on a queue only its own `Update()` could drain. The thread id now latches at
  `SubsystemRegistration` and the pump is created at `BeforeSceneLoad`, which removes the root cause;
  the wait is bounded and guarded besides.
- **`ThreadSafeCacheManager` advertised "safe from any thread" while every operation reached Unity
  API.** An off-thread `Set()` retained a reference and *then* threw on `Time.realtimeSinceStartup`,
  leaking it from a stack that named neither the class nor the thread. See *Changed* for the
  contract that replaced the claim. `_disposed` is now `volatile` and re-checked inside the lock —
  the window `Dispose`'s own comment claimed to have closed was still open.

### Fixed — cache

- **`Pin()` on a key that is not cached is no longer a silent no-op**, which mattered because the
  documentation taught pin-before-load. A pin now waits for the key and is applied when it arrives.
- **An insert larger than the budget could not trigger the eviction that would make room for it** —
  `PerformEviction` ran from inside `Set()` and could not see the entry being inserted.

### Deprecated — `TieredAssetLoader` is now a configuration of `AssetLoader`

**Tiering is a setting on the one loader, not a second loader class.** `TieredAssetLoader` was a
fork of `AssetLoader` that existed only to add tiered caching, and it silently lacked everything
`AssetLoader` had: single-flight join, the post-await thread guard, label loads, the `*Safe`/
`LoadResult` variants, `InstantiateAsync`/`ReleaseInstance`, and `ReleaseAsset`. It is `[Obsolete]`
(a warning, not an error), still works, and is removed in 5.0.0. It is now a thin forwarder onto an
`AssetLoader` built with the same config.

```csharp
// Before
var loader = new TieredAssetLoader("Battle", TieredCacheConfig.Aggressive);
var tex    = await loader.LoadAssetAsync<Texture2D>("Boss/Diffuse");
loader.PinAsset<Texture2D>("Boss/Diffuse");
var stats  = loader.GetCombinedStats();
loader.Dispose();

// After
var loader = new AssetLoader("Battle", TieredCacheConfig.Aggressive);
var tex    = await loader.LoadAssetAsync<Texture2D>("Boss/Diffuse");
loader.PinAsset<Texture2D>("Boss/Diffuse");
var stats  = loader.GetTieredCacheStats();
loader.Dispose();
```

The only two renames are `GetCombinedStats()` → `GetTieredCacheStats()` and
`GetCacheStats<T>()` → `GetTieredCacheStats<T>()`, both forced by an `AssetLoader.GetCacheStats()`
that already existed with a different return type. Everything else is a type-name substitution.

Factory form: `Advanced.CreateTieredLoader(name, cfg)` → `Advanced.CreateLoader(name, cfg)`. The
seven `Advanced.*` members that take a `TieredAssetLoader` are `[Obsolete]` too, each with a
non-obsolete `AssetLoader` sibling of the same name.

**One migration is not a mechanical rename.** `Advanced.CreateTieredLoader("X")` with no config
meant *tiering on, with `TieredCacheConfig.Default`*. `Advanced.CreateLoader("X")` is the
pre-existing overload and means *tiering off*. Write `Advanced.CreateLoader("X",
TieredCacheConfig.Default)` to keep the old behaviour — otherwise you get a loader that never
evicts, silently. Tiering is **off** by default on `AssetLoader`, so nothing that exists today
changes behaviour; only the two-argument constructor turns it on.

**What the move fixes, beyond removing a fork:**

- **A tiered loader's cache could never be invalidated after a CDN catalog update.** It was not an
  `AssetLoader`, registered with a different registry, and had no `InvalidateAddresses` — so
  `AssetLoaderRegistry.InvalidateAll`, which `CatalogService` calls after `UpdateCatalogs`, could
  not reach it by any path. Its entries went on serving handles resolved against the previous
  catalog for the rest of the session. Because the shim's inner loader is a plain `AssetLoader`, it
  registers in the registry the invalidation walk uses, and **every existing
  `new TieredAssetLoader(...)` call site is now reached after a catalog update without its author
  changing a line.**
- **Eviction now ranks candidates of every `Type` together in one pass.** It used to keep one cache
  per `Type`, where a cache could only evict its own entries and relied on a round-robin over
  siblings to converge on the shared ceiling.
- `TieredAssetLoaderRegistry` (added earlier today, never in a release) is deleted; the periodic
  tier/eviction pump and the `Application.lowMemory` sweep now walk `AssetLoaderRegistry`. An
  untiered loader's `EvaluateTiers()`/`ForceEviction()` are no-ops, so pumping every loader is
  correct and costs one virtual call each.

`TieredCache<T>`, `ThreadSafeCacheManager<T>`, `TieredCacheConfig`, `CacheEntry<T>` and `CacheTier`
are **not** deprecated. `TieredCache<T>` remains a supported standalone cache via
`Advanced.CreateTieredCache<T>`; it is simply no longer what a loader uses internally.

### Changed

- **`TieredAssetLoader.LoadAssetAsync<T>` keeps returning `Task<IAssetHandle<T>>`** in every
  project, UniTask installed or not. No signature on this class changed. An earlier draft of this
  release made it return `UniTask<IAssetHandle<T>>` under `UNITASK_PRESENT` to match the rest of the
  package; that was reverted before shipping, because a warning-level `[Obsolete]` promises source
  compatibility until 5.0.0 and a return type that changes with an unrelated package's presence
  breaks `Task<T> t = loader.LoadAssetAsync<Sprite>(a);` and every `Task.WhenAll` call site with a
  hard compile error — on the same line as the deprecation warning that was supposed to be the
  migration signal. `Standard.LoadScene<T>` was already held at `Task` for exactly this reason.
  Under UniTask the shim pays one `AsTask()` conversion per load; that cost is the reason to
  migrate, and the replacement (`Advanced.CreateLoader(name, cfg)` → `AssetLoader`) has a genuinely
  dual `LoadAssetAsync<T>` with no conversion at all.

- `TieredAssetLoader.GetCacheStats<T>()` no longer returns `null` before the first load of `T`.
  There is no per-type cache object to test for existence any more; through this class it now always
  returns a struct, all-zero when nothing of `T` is cached. Test `TotalEntries == 0` instead of a
  null check. Its `TotalAccesses`/`CacheHits`/`HitRate`/`TotalEvictions`/`TotalPromotions`/
  `TotalDemotions` are now loader-wide rather than per-`Type`; the entry counts and byte figures
  remain exact per-`Type`. Keeping the counters per-`Type` would have meant a second book, which is
  the bug class this whole change removes.

- **`ThreadSafeCacheManager<T>.Set` and `.TryGet` now throw `InvalidOperationException` off the main
  thread.** Both reach `Time.realtimeSinceStartup` and `IAssetHandle.IsValid`, neither callable off
  it at any price, so an off-thread call already failed — deeper in, after `TryRetain()` had taken a
  reference that then leaked, from a stack naming neither this class nor the calling thread. The
  class doc now states a two-group thread contract: `Set`/`TryGet` are main-thread-only; `Remove`,
  `Clear`, `Dispose`, `Pin`/`Unpin`, `ContainsKey`, `Count`, `CurrentSize`, `GetStatistics` and
  `ResetStatistics` are callable from any thread and marshal their handle releases to the main
  thread. If you were calling `Set`/`TryGet` from a worker, wrap it in
  `UnityMainThreadDispatcher.Enqueue`.

- **The any-thread group no longer throws `ObjectDisposedException` after `Dispose()`** — each
  returns its neutral value (`Remove`/`TryPin`/`TryUnpin` → `false`, `GetStatistics` → an all-zero
  snapshot, `Clear` → no-op). Publishing an "any thread" contract invites a worker to hold the
  object across a `Dispose` it cannot observe, and the lock it would have taken is no longer
  disposed for the same reason.

- **`CachePinOutcome` and `PinWithOutcome(key)`** are added to `TieredCache<T>` and
  `ThreadSafeCacheManager<T>`. `TryPin` returns `false` both for a pin that was *deferred* (it will
  be applied when the key loads) and for one that was *refused* (the pending-pin list is full; it
  never will be) — opposite meanings behind one `bool`, distinguishable before only in the log.
  `TryPin` keeps its signature; prefer `PinWithOutcome` when the difference matters.

### Known limitations

- **`unity` stays at `2023.1`.** *(Corrected in `pre.8`: an earlier wording here said the floor
  would drop to 2022.3 once the editor assembly had been proven there. It will not — `2023.1` is
  `com.unity.addressables` 2.9.1's own declared floor, so the dependency fixes it. The 2022.3
  compile runs below are a stricter-than-required canary, not a step toward supporting 2022.3.)*
  The runtime assembly is verified clean against 2022.3.62f3 in both the `Task` and `UniTask`
  configurations; the *editor* assembly cannot be verified by `Tools/check-min-unity-api.sh`, which
  cannot reproduce Unity's editor reference set.
- `TieredAssetLoader` is no longer a fork — see the deprecation section above. It forwards to an
  `AssetLoader`, so it now has single-flight joins and catalog invalidation. `ReleaseAsset`, label
  loads, the `LoadResult` variants and the dual `UniTask` signature are still not exposed *through
  this wrapper* — its own signatures are frozen until 5.0.0; call them on an `AssetLoader` directly.
- **`TieredCache<T>`'s constructor reads `Time.realtimeSinceStartup`**, which throws off the main
  thread, so a standalone cache cannot be constructed from a background thread. The merged tiering
  inside `AssetLoader` avoids this by construction (it latches the clock on first use, not at
  construction); the standalone type was left as-is.

### Verification

Both assemblies compile. Runtime verified against Unity 2022.3.62f3 in both the `Task` and `UniTask`
configurations — the second is the one that shipped broken as `pre.4` — with 0 real errors in each.
142/142 EditMode tests pass (64 before this release; the five new fixtures add 78).

Not verified: `Profiler.GetRuntimeMemorySizeLong` in a non-development build (it can return 0 on some
platforms, which would leave eviction permanently idle), and the two pooling paths that need a real
`LoadSceneMode.Single` transition to reproduce.

## [4.1.0-pre.6] - 2026-08-17 - Reference counting, one ownership rule, and pools that keep their own books

The largest correctness release in the 4.1 line. An external review verified 66 defects across the
caches, loaders, API surface, scopes and pooling; this ships the fixes for most of them. Nearly
every one is a lifetime bug — a reference taken and never given back, or given back twice, or a
cache serving an asset it did not own.

**Read this before upgrading if you use `TieredCache<T>`, `ThreadSafeCacheManager<T>` or
`AddressablePoolManager` directly.** Some methods now do what their names always claimed, which is
a behaviour change even though no signature changed.

### Fixed — reference counting

- **A cache stored handles it did not own a reference to.** `Set()` kept the caller's handle without
  retaining it, and eviction later released it — so eviction destroyed an asset the caller was still
  holding, and the caller's own `Release()` then decremented a count that no longer belonged to it.
  Caches now take their own reference via `TryRetain()`, independent of the caller's, and `TryGet()`
  hands back a retained copy. **You must still release what `TryGet()` gives you.**

- `Clear()`, `Dispose()` and `Remove()` dropped entries without releasing anything — the bundles
  stayed loaded for the rest of the session with nothing left that could free them.

- `Set()` on a key that already held a live entry silently discarded the incoming handle, orphaning
  one reference per call. It now releases the losing duplicate and documents that the handle you
  passed may be invalid when the call returns.

- Two `if (IsValid) Retain()` pairs on the cache-hit path. `Retain()` throws on a dead handle;
  checking `IsValid` first is not the same as it not racing. Both use `TryRetain()` now.

### Fixed — one ownership rule for loaders

A loader belongs to exactly one owner: the object whose lifetime it copies. The owner creates it,
the owner disposes it, and only from its own teardown. Everyone else borrows. The full reasoning is
in `Documentation/LIFETIME_DESIGN.md`.

- **`Simple.Load`, `TryLoad`, `Preload`, `PreloadBatch` and `Standard.PreloadAsync` orphaned one
  reference per call.** The most common entry points in the package leaked on every use.
- **`Simple.Destroy` called `Object.Destroy`**, leaking the Addressables instance refcount that
  only `ReleaseInstance` can return.
- **`GlobalAssetScope.Dispose()` left its field non-null**, so `Loader` returned null for the rest
  of the process. The scope is now owned by the Facade.
- `AddressablesFacade.OnDestroy` ran `EndSession()` and disposed the pool manager outside the
  `_instance` guard, so a duplicate Facade tore down the live one's state.
- `HybridScope` held statics across domain reload and disposed without clearing them. Both fixed.
  *(Corrected in `pre.9`: this line originally ended "Retired." It was not retired — the class is
  still public and carries no `[Obsolete]`. Only the two defects were fixed.)*

### Fixed — catalog updates now reach every cache

After a catalog update, a cached handle still wrapped an operation resolved against the *previous*
catalog. It looked healthy — valid, succeeded — so it was served for the rest of the session.

Loaders are constructed in six places and only one was enumerable, so invalidation reached one
population out of six, and the one it reached was not the default path. `AssetLoaderRegistry` (weak
references, pruned as it walks) now reaches all of them, and `CatalogService` invalidates through it
after `UpdateCatalogs`.

Also: a failed bundle-cache clean no longer reports the whole update as failed. On WebGL, where that
clean always fails, every successful update was being reported as a failure.

### Fixed — pooling

- **`Spawn()` blocked the main thread on an Addressables load** when auto-create hit a missing pool.
  It now starts (or joins) a background create and returns `null` immediately; `SpawnAsync()` is the
  path that waits. The two `null`s never mean the same thing and each is logged where it happens.
- **`preloadCount` created exactly one instance regardless of the number asked for**, then logged the
  number requested as though it had succeeded.
- **`DynamicPool` corrupted the inner pool's accounting in both directions**, driving `activeCount`
  negative and exposing it through the public API.
- Pools now live under a `DontDestroyOnLoad` root, and both adapters walk past instances a scene load
  destroyed underneath them. (`obj == null` on a generic `T` binds to `object.Equals`, not Unity's
  overridden equality, so a destroyed `GameObject` in a free list reads as alive.)
- `Despawn` verifies which pool actually owns an instance. Wrong-pool and double-despawn used to
  produce three different behaviours depending on the adapter.
- `ClearPool` refuses while instances are still borrowed; real teardown destroys them first, then
  clears the pools, then releases templates.

### Fixed — tiered cache and progress

- **Teardown released nothing.** `Dispose()` now hard-releases, matching `AssetLoader.ClearCache`'s
  documented memory-pressure semantics; eviction still uses a plain decrement.
- **The memory budget was applied per `Type`**, so the real ceiling was `MaxCacheSizeBytes` times the
  number of distinct types cached. One shared budget now.
- **Nothing ever ran eviction or tier evaluation.** The Facade pumps every live tiered loader on a
  5-second interval, plus an `Application.lowMemory` sweep.
- **`EstimateAssetSize` returned invented numbers.** A `GameObject` is now measured by walking its
  meshes, materials and the textures those materials reference — the memory a prefab actually pulls
  in, not the size of the native shell.
- Cache dispatch no longer goes through reflection (which IL2CPP can strip, turning the pump into a
  silent no-op), and the per-type key is a struct rather than `$"{address}_{typeof(T).Name}"`.
- **`ProgressiveAssetLoader` opened its own Addressables operations and threw the handles away.**
  It now delegates to `AssetLoader`, so those handles are cached, single-flighted, and reachable by
  `ClearCache` / `Dispose` / catalog invalidation. `LoadMultipleWithProgressAsync` keeps its `bool`
  signature; the honest result lives on an additive `*Safe` overload returning `LoadResult<T>`.

### Fixed — Editor

- A layout rule with every filter disabled matched **every asset in the project**, and the scan it
  ran had no upper bound.
- `PathFilter` had no glob mode while the documentation taught `**` patterns in 32 places.
- A shipped rule template failed its own validation, which was masking an unconditional `SaveAssets`.
- **`AddressableCLI` reported success on a broken build.** All four entry points now gate on
  `EditorUtility.scriptCompilationFailed`. Unity exits 0 from `-executeMethod` even when the
  assembly never compiled.
- The `Samples~/RuleAutomation` sample shipped a live hazard rather than an example.

### Known limitations

- **`unity` stays at `2023.1`.** *(Corrected in `pre.8`: an earlier wording here said the floor
  would drop to 2022.3 once the editor assembly had been proven there. It will not — `2023.1` is
  `com.unity.addressables` 2.9.1's own declared floor, so the dependency fixes it. The 2022.3
  compile runs below are a stricter-than-required canary, not a step toward supporting 2022.3.)*
  The runtime assembly is verified clean against 2022.3.62f3 in both the `Task` and `UniTask`
  configurations; the *editor* assembly cannot be verified by `Tools/check-min-unity-api.sh`, which
  cannot reproduce Unity's editor reference set.
- `TieredAssetLoader` is still a fork of `AssetLoader` missing single-flight joins, `ReleaseAsset`,
  `LoadResult` variants and dual UniTask signatures — and it is invisible to catalog invalidation,
  so caches it holds keep serving pre-update content. It is being retired in the next pre-release;
  prefer `AssetLoader`. Evidence is recorded in `Documentation/LIFETIME_DESIGN.md`.
- `Standard.ClearCache(scopeName)`, `Simple.Release<T>` and `Standard.LoadScene<T>` are still the
  stubs the review found. Do not rely on them.
- `TieredCache.Pin()` on a key that is not cached is still a silent no-op, despite documentation
  teaching pin-before-load.
- `ThreadSafeCacheManager` still advertises "safe from any thread" while its operations reach Unity
  API. Treat it as main-thread.

### Verification

Both assemblies compile. Runtime verified against Unity 2022.3.62f3 in both the `Task` and `UniTask`
configurations — the second is the one that shipped broken as `pre.4`. 64/64 EditMode tests pass.

Not verified: `Profiler.GetRuntimeMemorySizeLong` in a non-development build (it can return 0 on some
platforms, which would leave eviction permanently idle), and the two pooling paths that need a real
`LoadSceneMode.Single` transition to reproduce.

## [4.1.0-pre.5] - 2026-08-14 - Fixes a compile break in pre.4

**Anyone on 4.1.0-pre.4 should move to this. pre.4 does not compile on Unity 2023.x or
2022.3.**

### Fixed

- **`SceneAssetScope.GetOrCreate(Scene)` used an API that only exists on Unity 6.**

  ```
  SceneAssetScope.cs(136,58): error CS1503: cannot convert from
  'UnityEngine.FindObjectsInactive' to 'UnityEngine.FindObjectsSortMode'
  ```

  pre.4 replaced a deprecated call with `FindObjectsByType<T>(FindObjectsInactive)`,
  which is what Unity's own deprecation message tells you to use. That overload was
  added in Unity 6. On 2022.3 and 2023.x the only generic overloads take a
  `FindObjectsSortMode`, so the single-argument form binds to the wrong one and fails to
  compile — and `package.json` declares `unity: 2023.1`.

  Now uses the two-argument form, which exists on every supported version, with the
  Unity 6 deprecation warning suppressed at the call site and the reason recorded there.
  Dropping `FindObjectsInactive.Include` instead would have compiled everywhere and
  silently stopped finding scopes on inactive objects, which is worse.

### Added

- **`Tools/check-min-unity-api.sh`** — compiles the package against a chosen editor's
  reference assemblies, so "does this compile on the oldest Unity we support" is a
  question that gets asked before publishing rather than by the first person to install
  it. Known harness/editor divergences are listed with reasons and reported when hit, so
  a pass cannot quietly cover less than it claims.

- Two CI steps in `content-update.yml`: the catalog-versus-bundles check, and the
  minimum-Unity compile. The second warns loudly and continues when
  `MIN_UNITY_EDITOR` is unset, rather than passing silently.

### Why pre.4 passed every check

Every verification ran against Unity 6000.5.7f1, the newest editor on the build
machine: 0 error CS, 0 warning CS, six tabs probed, 57/57 tests green, twice. All of it
true, and none of it about the version the package says it supports. Running the newest
installed Unity and running the oldest supported one are different tests; only the
second one answers whether the package compiles for the people installing it.

## [4.1.0-pre.4] - 2026-08-14 - Catalog Inspector, samples, and a clear-out

Closes the last piece of Editor tooling, moves the examples to where a package is
supposed to keep them, and removes code that was doing nothing.

### Added

- **Catalog Inspector tab** (task 5.9) — reads a built binary catalog and shows what is
  in it: every entry with the bundle it comes from, every bundle with its size, CRC and
  dependents, and a cross-check against the bundles on disk. Missing, orphaned and
  size-mismatched bundles are reported separately because they mean different things:
  missing breaks players, orphaned costs storage, mismatched means the catalog and the
  bundles came from different builds.

  No third-party dependency was added. `AddressablesTools` would have meant owning a
  binary-format parser that has to track every Addressables release; instead
  `CatalogReader` reflects two internals — `ContentCatalogData.LoadFromFile` and
  `CreateCustomLocator` — and everything after that is public interface.
  `CatalogReaderTests` resolves both against the installed Addressables, so an upgrade
  that renames either fails a test rather than producing an empty inspector.

- **`CatalogInspectCLI`** — the same check in batchmode, exit 1 when the output is not
  publishable. Worth running before an upload: a catalog that references a bundle you
  did not upload produces a 404 on a device and nothing in the build log.

- **Samples, as UPM samples.** `CDN Boot`, `API Examples` and `Rule Automation Presets`
  now install from Package Manager instead of sitting in `Assets/`. They are opt-in and
  no longer compile in every project that installs the package. The rule preset set is
  wired together — a layout rule referencing real filters and a provider, and a
  composite referencing the layout rule — so it demonstrates something rather than
  being empty scriptable objects.

- **`Documentation/CDN_USAGE_GUIDE.md`** (task 5.5) — a guide for the person building
  the game, shipped inside the package: whether you need remote content at all, setup,
  the boot sequence, downloads, patching, the cache, environments, one row per error
  code, CI, and the things that will catch you out. Every API name in it was checked
  against the source.

- `TROUBLESHOOTING.md` now ships **inside the package**, so it is available offline to
  whoever hits the error message that names it.

### Fixed

- **Bundle location shown for what it is.** Found on the Catalog Inspector's first run
  against a real catalog, which reported 8 missing bundles and 6 orphans on a build that
  was fine. Two causes, both worth knowing about:

  `AssetBundleRequestOptions.BundleName` in a built catalog is an internal hash, not a
  file name — the requested file name is the last segment of `InternalId`. And a
  catalog holds both remote bundles and bundles that ship inside the player; comparing
  the second kind against a CDN folder produces a false alarm per bundle.

  A load path the package cannot classify is now reported as unchecked rather than
  quietly skipped, so a clean verdict cannot silently cover less than it claims.

- **Shared tab styles are actually loaded.** `.cdn-tab-page` and the row, button and
  section classes were defined in individual tab stylesheets, which are attached to that
  tab's root and torn down when the shell rebuilds the tab body. Three tabs used classes
  that did not exist while they were showing. The shared vocabulary moved to
  `CdnManagerWindow.uss`, which is always loaded; per-tab overrides still win.

- **A characterization test that had never passed.** `Message_ExtraWhitespace_StillMatches`
  asserted that `"not  found"` matches the keyword `"not found"`. It does not — the
  classifier lowercases and calls `Contains`, nothing more. The test was written from the
  doc comment rather than from a run. It now records the real behaviour, with a
  counterpart test for padding around an intact keyword.

### Removed

- **`AddressableProgressBar.autoFindTracker`** — a serialized inspector toggle,
  defaulted to true, read by nothing. No auto-find code was ever written; binding has
  always been explicit through `BindToTracker`. A checkbox promising behaviour the
  component does not have is worse than no checkbox.

- **`README.backup.md`** — 1273 lines of superseded README that `git subtree split` was
  shipping to every consumer.

### Changed

- `MemoryGraphView.Clear()` is now `ClearSamples()`. It hid `VisualElement.Clear()`,
  which means the same call did two unrelated things depending on the static type of the
  reference. It had no callers.
- Two private `DrawHeader()` methods in custom inspectors renamed to `DrawTitleSection()`;
  they hid `Editor.DrawHeader()`.
- Deprecated Unity APIs replaced: `FindObjectOfType` → `FindAnyObjectByType`,
  `FindObjectsByType(…, FindObjectsSortMode)` → the overload without it.
- README rewritten around what the package is now: correct Unity and Addressables
  versions, the current install URL, all six CDN tabs, the samples, and a deprecation
  table instead of a version history that stopped at 3.0.0.

### Still not here

Phase 5's field validation: no device matrix, no staging soak, no measurement against a
real CDN, and the CI workflows have never run. Three Phase 3 measurements are also unrun
— cancel-and-resume, speed accuracy under throttling, and the allocation check — each
needing test infrastructure rather than code. The six tabs build and reach correct
conclusions in batchmode; nobody has looked at them.

## [4.1.0-pre.3] - 2026-08-14 - Downloads, cache and diagnostics (Phases 3 and 4)

`pre.2` could boot against a CDN and apply a catalog update. This one downloads content
properly and stops the install growing forever.

### Added

- **`CdnManager.DownloadAsync` / `GetDownloadSizeAsync`** — progress in real bytes,
  cancellation, retry with exponential backoff and jitter, and pre-flight checks for
  reachability, metered network and free disk. Size returns `CdnResult<long>`, so
  "nothing to download" is distinguishable from "could not find out".
- **`RetryPolicy`** — retryability comes from the error, not the policy, so the CLI, the
  GUI and the runtime cannot disagree about what is worth retrying.
- **`CdnErrorMapper`** — classifies from the HTTP response code rather than by matching
  words in a message. A 404 on a bundle and a 404 on a catalog mean different things
  about your deploy and now produce different codes.
- **`CacheService`** — usage stats, eviction by key, and obsolete-bundle cleanup.
- **`ICdnTelemetry`** — an interface with no transport bundled; wire it to whatever you
  already use.
- **`LoadErrorCode.ContentNotDownloaded`** — "the address is valid but the bundle is not
  on this device", previously indistinguishable from `AssetNotFound`.
- **Runtime Monitor tab** and a **CDN status row** in the Addressables dashboard.
- **`AddressableProgressBar.SetDownloadProgress`** — real bytes, speed and ETA.
- **Fault injection** for the local test server: HTTP errors, dropped connections,
  latency and bandwidth throttling.
- **Troubleshooting guide** with an entry for every `CdnErrorCode`.

### Fixed

- **Obsolete bundles are now removed after a catalog update.** Addressables leaves the
  superseded bundles on disk and nothing else removes them, so an install accumulates a
  generation of content per patch. `ApplyUpdateAsync` cleans them automatically.
- **Download speed and ETA were meaningless.** `ProgressiveAssetLoader` computed speed as
  a fraction-per-second multiplied by 100 and labelled it KB/s — a 10 MB and a 10 GB
  download reported the same number — over an elapsed time that was never reset, so the
  figure decayed toward zero regardless of the network. `ProgressInfo.BytesDownloaded`
  and `TotalBytes` existed and were never populated. Both are correct now.
- **Catalog operations never retried.** `CatalogService` classified its own failures with
  a placeholder that returned `Unknown`, which is not retryable, so a transient 503 during
  an update check was treated as permanent.
- **Line endings could change bundle contents.** With `core.autocrlf` on and no
  `.gitattributes`, git rewrote LF to CRLF in TextAssets on checkout — the same commit
  then produced different bundles on different machines.

### Deprecated

`AssetLoader.DownloadDependenciesAsync` / `GetDownloadSizeAsync`, `Assets.Download` /
`GetDownloadSize`, `StandardAPI.DownloadDependencies` / `GetDownloadSize`. Warnings only,
kept until 5.0.0, each naming its replacement.

### Still not here

Phase 5's field validation: no device matrix, no staging soak, no measurement against a real CDN,
and the CI workflows have never run. Three Phase 3 measurements are also unrun — the
cancel-and-resume proof, speed accuracy under throttling, and the allocation check — each
needing test infrastructure rather than code.

## [4.1.0-pre.2] - 2026-08-14 - CDN runtime layer (Phase 2)

The half that was missing. `4.1.0-pre.1` could produce and verify content; this one
lets a game boot against a CDN and consume it.

### Added

- **`CdnManager`** — the entry point. `InitializeAsync`, `CheckForUpdateAsync`,
  `ApplyUpdateAsync`, `SetEnvironment`, `PromoteFailover`.
- **`CdnResult<T>` / `CdnError` / `CdnErrorCode`** — 15 codes with retry
  classification, mirroring the existing `LoadResult` / `LoadError` shape.
- **`CdnSettings`** — ScriptableObject in `Resources`, with environments, failover
  origins, an env-var host override, and a download policy.
- **`CatalogService`** — initialise, check, apply, each returning a result rather
  than throwing.
- **`CdnRequestDecorator`** — installs the Addressables hooks, and refuses if
  Addressables already initialised.
- **`HostRewriter`** — swaps the origin so one build can be pointed at another
  environment without a rebuild.
- **`NetworkPolicy`** — reachability and metered detection, with its limits documented.
- **`CdnBootExample`** — the canonical boot sequence, branching on every outcome.

### Call it first

`CdnManager.InitializeAsync` must run before anything else touches Addressables.
Addressables initialises implicitly on its first load call, and the CDN hooks only
apply to content resolved after they are installed. A stray `LoadAssetAsync`, or an
`AssetReference` on an object in your boot scene, is enough to lose them — so
initialisation fails loudly in that case instead of half-applying.

```csharp
var init = await CdnManager.InitializeAsync();
if (init.IsFailure)
{
    if (init.ErrorCode == CdnErrorCode.NoContentAvailableOffline)
        ShowBlockingSetupScreen();   // first launch, nothing cached
    return;
}

var check = await CdnManager.CheckForUpdateAsync();
if (check.IsSuccess && check.Value.HasUpdate)
    await CdnManager.ApplyUpdateAsync(check.Value);
```

Create the settings asset via **Assets > Create > Addressable Manager > CDN Settings**
and put it in a `Resources` folder. It has to load from Resources, because it is what
tells Addressables where the catalog is.

### Named CdnManager, not Cdn

The design documentation calls the facade `Cdn`. That name does not compile: the
runtime types live in namespace `AddressableManager.Cdn`, so `Cdn` at a call site
binds to the namespace.

### Verified

Four PlayMode integration tests against a real HTTP server, green twice in a row.
Assertions are made against the server's request log rather than the client's return
value — Addressables in Fast Mode reports success without a byte crossing HTTP, so a
passing call proves nothing on its own.

### Still not here

Phase 3 and beyond: download orchestration, progress in real bytes, cancellation,
resume, retry with backoff, cache management, and the diagnostics window. Content
downloads currently go through Addressables' own path with no CDN-specific retry or
progress reporting. Nothing has been tested against a real CDN — only a local server.

## [4.1.0-pre.1] - 2026-08-11 - CDN build pipeline (Editor only)

Pre-release for integration testing. Adds the Editor-side CDN pipeline: build
remote content, refuse to build a broken patch, verify the output, and see what
a patch costs a player.

### Read this before integrating

**There is no runtime CDN layer in this release.** Nothing under `Runtime/`
knows about a CDN. Your game cannot yet initialise against one, check for a
catalog update, apply one, or define what happens offline — that is Phase 2 and
is not written. What you can do today is produce and verify content from the
Editor or from batchmode. If you are evaluating "can my game download content
from a CDN", this release does not answer that question yet.

Everything below is also **unverified against a real CDN**. It has been run
against a local HTTP server only.

### Requirements

- Unity **2023.1+** (raised from 2022.3 — Addressables 2.9.1 sets this floor)
- `com.unity.addressables` **2.9.1** (raised from 2.3.1; 2.3.1 will not compile,
  several APIs used here do not exist in it)

### Added

- **`CdnBuildPipeline`** — the gated build sequence, shared by the CLI and the
  GUI so the order of checks exists in one place. Every gate runs before the
  build, because the failure the version gate prevents cannot be detected after.
- **`CdnBuildCLI`** — batchmode entry points `BuildContent`,
  `BuildContentUpdate`, `VerifyOutput`. Non-zero exit on any failure, and each
  gates on `EditorUtility.scriptCompilationFailed` first: Unity exits 0 when
  `-executeMethod` runs against a broken assembly and the method never runs.
- **`ContentStateManager`** — resolve, validate and archive
  `addressables_content_state.bin`. `Validate` compares the state file's
  `playerVersion` against `PlayerSettings.bundleVersion` and refuses a mismatch.
- **`ContentUpdateRestrictions`** — reports which entries in StaticContent
  groups changed, and whether each was modified directly or pulled in as a
  dependency.
- **`CatalogVerifier`** — checks the settings contract, that the catalog is
  named for the app version players actually poll for, and that every bundle in
  the manifest exists on disk with the recorded size and SHA256.
- **`BuildManifest` / `BuildManifestWriter`** — `build-manifest.json` next to
  the output: app version, platform, build type, git SHA, catalog metadata, and
  every bundle with its SHA256. Serialised with `JsonUtility`; no Newtonsoft
  dependency is added.
- **`ContentDiff`** — compares two manifests by content hash and reports what a
  player on the older build must download. The result is embedded in each
  manifest, so CI can assert on patch size without re-running the comparison.
- **`CdnProfileManager`** — Local / Dev / Staging / Prod profiles, a catalog
  path separate from the bundle path, and CDN host injection from an
  environment variable so URLs are not committed.
- **`LocalContentServer`** — static file server with real cache headers and
  range-request support, for testing without a CDN.
- **CDN Manager window** (`Window → Addressable Manager → CDN Manager`) with
  four tabs: Settings Validator, Local Server, Update Preview, Build.
- **`SettingsContract`** — 70 rules covering the Addressables configuration a
  CDN setup requires, shared by the GUI validator and the CI verifier so the
  two cannot disagree.
- **`CdnSetupCLI`** — applies the whole contract from batchmode, idempotent.

### Fixed

Two pre-existing package bugs found while building the above:

- **`AddressRule`** created groups with no schemas attached, so those groups
  silently produced no content. `Scene.asset` in the sample project had been
  broken this way.
- **`HierarchyAssetScope`** used `Object.GetInstanceID()`, which Unity 6000.5
  marks `Obsolete(error: true)`. Now guarded by `#if UNITY_6000_5_OR_NEWER`, so
  the package still compiles below that version without raising its floor.

### Known gaps

- No runtime CDN layer (above).
- The GitHub Actions workflows and `ci/*.sh` shipped in the repository — not in
  this package — have never been executed.
- The four Editor tabs have been verified to build, refresh and report correctly
  from batchmode, but nobody has looked at them yet.
- The design documentation still states that a changed StaticContent group
  requires a new player build. It does not; the remedy is the Prepare step in
  the Update Preview tab. The code is correct, the prose is not.

### Note for projects with TextAssets in remote groups

Git rewrites line endings on checkout when `core.autocrlf` is on, and for a
TextAsset those bytes are what ends up in the bundle — the same commit then
produces different bundles on different machines. Mark such assets `-text` in
`.gitattributes`.

## [4.0.1] - 2026-05-24 - Fix StandardAPI.InstantiateSession compile error

4.0.0 removed `AddressablesFacade.GetSessionScope()` and replaced it
with `GetSessionLoader()` returning the underlying `AssetLoader`
directly, but `StandardAPI.InstantiateSession` still called the old
name — broke compile for any consumer touching that method.

### Fixed
- **`StandardAPI.InstantiateSession(address)`** now calls
  `Facade.GetSessionLoader()`. Behaviour also tightened: if the
  session hasn't been started, the method auto-starts it (via
  `Facade.StartSession()`) instead of logging an error and returning
  null — matching the ergonomic of `LoadSessionAsync` from 4.0.0.

### Notes
- `AdvancedAPI.GetSessionScope()` is unrelated — it returns
  `HybridScope.Session` (a different class, still present). No change
  needed there.

## [4.0.0] - 2026-05-24 - Scope identity overhaul + SessionAssetScope removal

Two architectural changes graduating from the design review:

1. **Scope identity vs display split** — every scope now has a unique
   `ScopeId` (used for ScopeManager lookup + monitoring channel) and a
   separate friendly `DisplayName` (used for Dashboard / inspector
   labels). Multi-instance scopes (Scene / Hierarchy) encode owner
   identity into the id so they stop colliding.
2. **`SessionAssetScope` deleted** — the class was redundant
   (mechanically identical to `GlobalAssetScope` apart from teardown
   timing) and *limiting* (singleton, so no parallel sessions
   possible). Sessions now route through
   `ScopeManager.GetOrCreateScope("Session")`; the facade ergonomics
   (`Assets.StartSession()` / `LoadSession<T>` / `EndSession()`) are
   preserved as thin forwarders.

### Added
- **`BaseAssetScope.ScopeId`** — unique identifier, used as dictionary
  key by `ScopeManager` and as the monitoring channel by
  `AssetMonitorBridge`.
- **`BaseAssetScope.DisplayName`** — friendly label shown in the
  Dashboard inspector, defaulting to `ScopeId` if not provided.
  `BaseAssetScope.ScopeName` is kept as a back-compat alias for
  `ScopeId`.
- **`customScopeId` + `customDisplayName`** `[SerializeField]` on
  both `SceneAssetScope` and `HierarchyAssetScope` — Inspector-set
  semantic ids ("PlayerInventory", "Match_42", "Lobby"…). Empty =
  use the auto-derived default.
- **`HierarchyAssetScope.AddTo(GameObject, customScopeId,
  customDisplayName)`** factory overload. Internally stashes the
  pending id on a static field consumed by the newly-added
  component's Awake — letting you inject the id *before*
  initialisation without having to deactivate the GameObject.
- **`SceneAssetScope.CreateForScene(Scene, customScopeId,
  customDisplayName)`** + **`GetOrCreate(Scene)`** overloads.
  Cross-scene safe — `GetOrCreate(Scene)` filters by
  `_ownerScene == scene` instead of returning the first match across
  every loaded scene.
- **`SceneAssetScope.OwnerScene`** public property — exposes the
  scope's bound scene.

### Changed
- **Default `Hierarchy` scope id** is now
  `Hierarchy-{name}#{GetInstanceID()}` (was `Hierarchy-{name}`).
  Unique per Unity object, so 50 enemies with the same name no longer
  share a ScopeManager dictionary key.
- **Default `Scene` scope id** is now
  `Scene-{sceneName}#h{scene.handle}` (was `Scene-{sceneName}`).
  Unique per loaded scene instance — additive loads of the same scene
  no longer collide.
- **`SceneAssetScope.CreateForCurrentScene()`** now calls
  `SceneManager.MoveGameObjectToScene(go, scene)` so the new scope
  GameObject lives in the target scene, not in whichever scene was
  active when the call ran (latent bug).
- **`AddressablesFacade.StartSession()`** / **`EndSession()`** now
  back themselves with `ScopeManager.Instance.GetOrCreateScope("Session")`
  and `ClearScope("Session")` respectively. `LoadSessionAsync<T>`
  auto-starts the session on first call instead of erroring "no
  active session".
- **`AddressablesFacade.GetSessionScope()` removed** — replaced by
  **`GetSessionLoader()`** which returns the underlying
  `AssetLoader` directly. Same shape (one method, one return value),
  but no more pretending the session is a special class.
- **GameObject menu "Add Session Scope" removed** from Editor
  context menus. Quick Setup's "Create All Scope Objects" no longer
  spawns a `[SessionAssetScope]` GameObject.

### Removed
- **`SessionAssetScope` class** — deleted (`Runtime/Scopes/SessionAssetScope.cs` +
  meta + Editor inspector). Use `ScopeManager.GetOrCreateScope("Session")`
  for direct access or the unchanged `Assets.StartSession()` /
  `Assets.LoadSession<T>(...)` facade helpers.

### Migration
- **Code calling `SessionAssetScope.Instance` / `StartSession()` /
  `EndSession()` directly** — replace with the facade
  (`Assets.StartSession()` etc.) or `ScopeManager.Instance.GetOrCreateScope("Session")`.
- **Code matching scope names exactly** (e.g.
  `if (monitor.scopeName == "Hierarchy-Enemy(Clone)") { … }`) breaks
  because the default id now embeds `#{InstanceID}`. Either:
  - Switch to `StartsWith("Hierarchy-")` / `Contains(":")`, OR
  - Set `customScopeId` to a known string and match that exactly.
- **GameObject prefabs containing `SessionAssetScope`** — open and
  remove the now-missing-script entry. Re-add the behaviour via
  `Assets.StartSession()` at runtime if needed.
- **`AssetScopeType.Session` enum value retained** — semantic still
  valid; resolvers should map it to
  `ScopeManager.GetOrCreateScope("Session")`.

## [3.5.1] - 2026-05-23 - UniTask compile-fix for merged branch code

Fixes compile errors that surfaced when 3.5.0 was consumed by a
project that has `com.cysharp.unitask` installed (UNITASK_PRESENT
define active). The branch's code was written before the
Task ↔ UniTask switch existed in 2.3.0, so several call sites
expected concrete `Task<T>` types where the switched API now
returns `UniTask<T>`.

### Fixed
- **`Runtime/Threading/ThreadSafeAssetLoader.cs`** — every public
  `LoadAssetAsync` / `LoadAssetsByLabelAsync` / `InstantiateAsync`
  overload now switches its return type between `Task<T>` and
  `UniTask<T>` via `#if UNITASK_PRESENT`, matching the rest of the
  package. The internal `TaskCompletionSource` for the by-label
  branch also switches to `UniTaskCompletionSource` when UniTask
  is present.
- **`Runtime/Threading/LoadOperation.cs`** — the internal queued
  load + instantiate operations now use
  `UniTaskCompletionSource<T>` + `Func<UniTask<T>>` under
  `UNITASK_PRESENT`, falling back to the original
  `TaskCompletionSource<T>` + `Func<Task<T>>` otherwise.
- **`Runtime/Pooling/AddressablePoolManager.cs`** —
  `task.Wait()` + `task.Result` replaced with
  `task.GetAwaiter().GetResult()` in the auto-create code path.
  `Wait` / `Result` are Task-only members; the awaiter form works
  for both Task and UniTask.
- **`Runtime/API/StandardAPI.cs`** — `DownloadDependencies` return
  type corrected from `Task<long>` to `Task<bool>` to match
  `AssetLoader.DownloadDependenciesAsync` (changed from
  `Task<long>` to `Task<bool>` in 2.2.0).
- **`Runtime/API/AdvancedAPI.cs`** — `LoadFromBackgroundThread`
  uses `UniTask.RunOnThreadPool` under `UNITASK_PRESENT`, falling
  back to `Task.Run` otherwise. `Task.Run` cannot accept
  `Func<UniTask<T>>`.

### Notes
- No public API change for consumers without UniTask installed.
- Consumers with UniTask installed previously got a build failure
  on first compile; now compiles cleanly. Public return types in
  `ThreadSafeAssetLoader` correctly become `UniTask<T>` to match
  the rest of the surface.

## [3.5.0] - 2026-05-23 - Branch merge: Tiered API + Rules engine + Hardening lineage

Merges the `feat/asset-importer` branch (Tiered API v3, thread-safety,
SmartAssetHandle, Result pattern, DynamicPool, Rule-based automation
with 8 filter types, Layout Rule Editor, CLI tools, auto-apply on
import) into the 2.3.x lineage (audit hardening, UniTask switching,
documentation refresh). All work from both branches is preserved.

### Added (from `feat/asset-importer`)
- **Tiered API** — Simple / Standard / Advanced loader layers with
  progressive complexity.
- **Thread-safety** — `ThreadSafeAssetLoader`, `UnityMainThreadDispatcher`,
  `LoadOperation<T>` for background-thread loads with main-thread
  dispatch. `AssetLoader` now ships `AssertMainThread()` guards with
  detailed remediation hints.
- **SmartAssetHandle<T>** — `IDisposable` wrapper for `using`-statement
  auto-release; GC finalizer as safety net; `.ToSmart()` /
  `.LoadAssetSmartAsync()` extensions.
- **`LoadResult<T>` / `LoadError`** — Rust-style result pattern with 11
  specific error codes, hints, and `.Match` / `.Map` / `.FlatMap` /
  `.Unwrap*` helpers. New `LoadAsyncSafe<T>` overloads on `AssetLoader`.
- **Dynamic pools** — `DynamicPool<T>` + `DynamicPoolConfig` that grow
  / shrink based on usage; `Default` / `Conservative` / `Aggressive`
  presets plus `Fixed()` for static behaviour. `AddressablePoolManager`
  gains `CreateDynamicPoolAsync`, `GetDynamicPoolStats`, `ResizePool`,
  `IsDynamicPool`.
- **Tiered cache + ValidationMode + HybridScope + ThreadSafeCacheManager**
  for advanced cache management.
- **Rule-based automation** — `AddressableRuleConfig` + 8 filter
  types (label / address-equals / glob / regex / group / etc.), Layout
  Rule Editor, Layout Viewer with conflict detection, composite rule
  merging, batch operations, auto-apply-on-import. This is the
  "rules and filters" surface that was previously missing.
- **CLI tools** for CI/CD pipelines.

### Preserved (from 2.2 / 2.3 hardening lineage)
- All Blocker / High / Medium / Low / Nice-to-have fixes from 2.2.0:
  `ProgressiveAssetLoader` release-on-fail, `AddressablePoolManager`
  template-handle release (now applies to both `CreatePoolAsync` and
  `CreateDynamicPoolAsync`), `MonitoredAssetLoader` rewritten as
  forwarder, `AssetLoader.ReleaseAsset` cast fix, `SceneAssetScope`
  dispose collapse, `BaseAssetScope.DisposedToken`, `ScopeManager`
  `SubsystemRegistration` reset, optional TMP via `TMP_PRESENT`,
  shared-list refcount, `Facade.OnDestroy` releases scopes,
  `DebugSettings` Editor-only `Resources.Load`, `Assets` facade
  parity, `MonitoringHelperInspector` moved to Editor.
- **UniTask switching** (`UNITASK_PRESENT` versionDefine) preserved on
  every public async signature — both the original `CreatePoolAsync`
  and the newly-merged `CreateDynamicPoolAsync` switch return types
  between `Task<T>` and `UniTask<T>`.
- Doc refresh from 2.2.1 superseded by branch's v3 README, but
  architecture diagrams and migration tables retained where they
  describe behaviour still present in 3.x.
- `MonitoringHelperInspector.cs.meta` from 2.3.0 retained.

### Migration
- `package.json` jumps from 2.3.0 → 3.5.0 (branch's authoritative
  version).
- Unity floor stays at **2022.3** (the higher of the two branches' floors).
- `com.cysharp.unitask 2.3.0+` continues to auto-switch the async
  surface to `UniTask<T>`.
- Consumers that were on 2.3.0 and relied on `Task<T>` return types
  without UniTask installed see no change. With UniTask installed,
  return types become `UniTask<T>` as documented.
- Consumers on 1.x / 2.0 / 2.1 should re-read the 2.2.0 audit
  CHANGELOG for behavioural changes around handle release, scope
  dispose, and `DownloadDependenciesAsync` signature (`Task<long>` →
  `Task<bool>`).

## [2.3.0] - 2026-05-23 - UniTask switching + dependency auto-detection

### Added
- **Automatic `Task` ↔ `UniTask` switching.** The Runtime asmdef now declares a `versionDefines` entry that defines `UNITASK_PRESENT` whenever `com.cysharp.unitask 2.3.0+` is installed in the consumer project. Every public async method (`AssetLoader.LoadAssetAsync`, `Assets.Load`, `AddressablesFacade.LoadGlobalAsync`, `AddressablePoolManager.CreatePoolAsync`, `ProgressiveAssetLoader.LoadAssetWithProgressAsync` etc. — 30 signatures across 6 files) now returns `UniTask<T>` when the define is active, `Task<T>` otherwise. Body await sites are awaiter-compatible and unchanged.
- `Task.Yield()` / `Task.WhenAll(…)` / `new Task<T>[…]` inside `ProgressiveAssetLoader.LoadMultipleWithProgressAsync` are bracketed with `#if UNITASK_PRESENT` so they switch to the corresponding `UniTask` calls when UniTask is present (avoids the cost of awaiting a `Task.YieldAwaitable` from inside a `UniTask` async method).
- UniTask asmdef listed under the Runtime asmdef `references`. Unity ignores the reference gracefully when UniTask is not installed.

### Fixed
- `Editor/Inspectors/MonitoringHelperInspector.cs` shipped without a `.meta` file in 2.2.0, triggering Unity's "Asset has no meta file, but it's in an immutable folder. The asset will be ignored." warning when the package was consumed via UPM. Generated a stable GUID + `MonoImporter` meta so the inspector loads on first import.

### Notes
- This is a backwards-compatible change for projects **without** UniTask. Projects **with** UniTask installed will see public return types change from `Task<T>` to `UniTask<T>`. The two are awaiter-compatible (you can `await` a `UniTask` from a `Task` async method and vice versa), but code that captured the return value as a concrete `Task<T>` variable will need to either uninstall UniTask, refactor to `var`, or call `.AsTask()` on the result.
- Editor asmdef is unchanged — Editor code uses no `Task`/`UniTask` surface.

## [2.2.1] - 2026-05-23 - Documentation Refresh

Doc-only release; no code changes.

### Changed
- **`README.md`** rewritten end-to-end against the current API surface: install via tagged git URL, scope concept table, scoped/static/direct usage paths, pooling + progress + monitoring snippets, architecture diagram, migration notes for 2.1 → 2.2 and 2.0 → 2.1. ~60 % shorter than the previous version.
- **`MONITORING_GUIDE.md`** rewritten from scratch around the new "always-on, Editor-only" monitoring model. Removed the obsolete "use extension methods" framing, the made-up `LoadAssetAsync(address, scopeName)` overload, and the deprecated `handle.Release(address)` pattern. Added scope-name resolution table, `IAssetMonitor` example, build-time guarantees.
- **`EDITOR_TOOLS_GUIDE.md`** rewritten to match the real API: every `LoadAssetAsync<T>(address, scopeName)` call corrected, every `.Monitored()` reference removed, `ScopeManager` examples updated, recipes consolidated, troubleshooting table tightened.

### Notes
- The docs now treat `Assets` (static) and `AddressablesFacade` (MonoBehaviour) as parity-equivalent entry points (`Assets` gained the missing API in 2.2.0).
- Cross-doc links and footer version bumped to 2.2.1.

## [2.2.0] - 2026-05-23 - Audit-Driven Hardening

A pre-ship audit found three blocker-class Addressables ref-count leaks and a
handful of correctness / API issues. Every finding is addressed below.

### 🔴 Blocker fixes
- **`ProgressiveAssetLoader` leak** — `LoadAssetWithProgressAsync` /
  `DownloadWithProgressAsync` now `Addressables.Release` the underlying handle
  in `finally` when the load fails or throws, instead of orphaning it.
- **`AddressablePoolManager` template-handle leak** — the prefab handle
  acquired during `CreatePoolAsync` is now retained per-pool and disposed in
  `ClearPool` / `ClearAllPools` / `Dispose`. Previously every pool created
  a permanent +1 Addressables ref-count.
- **`MonitoredAssetLoader` build-time leak + AssetReference bypass** —
  rewritten as a thin forwarder around `AssetLoader` (which already owns
  monitoring under `#if UNITY_EDITOR`), so monitoring dispatch no longer
  ships in builds and `LoadAssetAsync<T>(AssetReference)` no longer rewrites
  the cache key through `assetReference.AssetGUID`.

### 🟠 High fixes
- **`AssetLoader.ReleaseAsset`** — the always-null `IAssetHandle<object>`
  cast is replaced with a non-generic `IDisposable` cache. Calling
  `ReleaseAsset(address)` now actually releases every cached handle
  matching that address.
- **`SceneAssetScope`** — collapsed the dual `sceneUnloaded` + `OnDestroy`
  dispose paths into a single `Destroy(gameObject)` funnel so cleanup runs
  exactly once.
- **`HierarchyAssetScope` / `BaseAssetScope`** — added a `DisposedToken`
  (`CancellationToken`) that fires when the scope is disposed. Long-running
  awaits can pass it to `Task.WaitAsync` and unwind cleanly when the owning
  GameObject is destroyed mid-load.
- **`ScopeManager`** — added a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`
  reset that disposes lingering scopes and nulls the static singleton on
  domain reload / new Play sessions.
- **`AddressableProgressBar`** — TextMeshPro is no longer a hard compile-time
  dependency. The runtime asmdef now defines `TMP_PRESENT` only when
  `com.unity.textmeshpro 3.0.0+` is installed; without TMP the component
  falls back to plain `UnityEngine.UI.Text`.

### 🟡 Medium fixes
- **`SharedListOperationTracker`** — initial refcount changed from 0 to 1,
  so empty-result label loads release the list handle immediately instead
  of leaking it until the loader disposes.
- **`AddressablesFacade.OnDestroy`** — now disposes the global scope and
  ends any active session before clearing the singleton, fixing repeat
  Editor Play-mode iterations leaking handles into the next session.
- **Double monitoring removed** — `MonitoredAssetLoader` no longer
  re-reports loads that `AssetLoader` already reported (Dashboard counts
  were doubled in Editor).
- **`DebugSettings`** — the `Resources.Load<DebugSettings>("AddressableManager/DebugSettings")`
  lookup is now inside `#if UNITY_EDITOR`. Shipping builds get a transient
  default instance instead of warning at runtime; added a static
  `IsVerbose` accessor used to gate informational logs.
- **README requirements** — Unity floor corrected to 2022.3 and the footer
  bumped to 2.2.0; flagged TextMeshPro as optional.

### 🟢 Low fixes
- **Verbose logging gated** — `AssetLoader` cache-hit / load-success
  `Debug.Log` calls go through `DebugSettings.IsVerbose`. Mobile shipping
  builds no longer pay a GC-allocating string interpolation per cache hit.
- **`DownloadDependenciesAsync` return type** — changed from `Task<long>`
  with a magic `1`/`0` sentinel to `Task<bool>` matching its semantics.
- **`PoolConfiguration.destroyOnFull`** — annotated `[HideInInspector]` +
  `[Obsolete]`; the underlying `UnityEngine.Pool.ObjectPool` always
  destroys excess instances above `maxSize`, so the flag had no effect.
- **`AddressableManager.Editor.asmdef`** — opaque GUID references
  replaced with portable name references (`AddressableManager`,
  `Unity.Addressables`, `Unity.Addressables.Editor`, `Unity.ResourceManager`).
- **`AssetLoaderExtensions`** — the deprecated forwarder class is removed.
  Use `AssetLoader.LoadAssetAsync` directly (monitoring is automatic).

### ⚪ Nice-to-have
- **`AssetMonitorBridge` thread-safety** — switched the listener list to a
  copy-on-write `IAssetMonitor[]` with a lock around register/unregister,
  plus a `SubsystemRegistration` reset that drops stale Editor listeners
  on domain reload.
- **`Assets` facade parity** — added `GetPoolStats`, `ClearPool(address)`,
  `SetPoolFactory`, `ReleaseInstance`, and `GetOrCreateSceneScope` so the
  static facade now matches `AddressablesFacade`.
- **`MonitoringHelperEditor` moved** — the nested `CustomEditor` was
  promoted to `Editor/Inspectors/MonitoringHelperInspector.cs` so the
  runtime assembly no longer carries an `UnityEditor` type.
- **`ScopeManager.GetScopeMemoryUsage`** — marked `[Obsolete]` with a note
  that runtime memory tracking is not implemented; live numbers stay in
  the Editor Dashboard.

### Migration
- `AssetLoader.DownloadDependenciesAsync` now returns `Task<bool>`. Callers
  that compared the result to `1`/`0` must switch to `true`/`false`.
- `AssetLoaderExtensions.*Monitored` methods are removed. Replace with the
  plain `AssetLoader.LoadAssetAsync` overloads — monitoring is automatic.

## [2.1.0] - 2025-01-XX - Automatic Monitoring

### Maintenance (added 2026-05-23)
- Aligned package metadata with the rest of the DreamTech library family: minimum Unity bumped to **2022.3**, author standardised to **DreamTech**.



### 🎉 Breaking Changes (Minor)
- **AssetLoaderExtensions deprecated**: `.LoadAssetAsyncMonitored()` methods marked as obsolete
  - Migration: Simply remove `.Monitored` from method names (e.g., `LoadAssetAsync` instead of `LoadAssetAsyncMonitored`)
  - Old code will still work (backward compatible) but shows warnings

### Added
- **Automatic Monitoring**: All asset loads now automatically tracked in Dashboard (Editor-only, zero build overhead)
- **AssetLoader constructor**: Now accepts optional `scopeName` parameter for automatic scope tracking
- **Monitoring integration**: All core load methods (`LoadAssetAsync`, `LoadAssetsByLabelAsync`, etc.) include built-in monitoring

### Changed
- **AssetLoader**: Added `#if UNITY_EDITOR` wrapped monitoring calls to all load operations
- **BaseAssetScope**: Now passes scope name to AssetLoader constructor
- **ScopeManager**: Passes scope ID to AssetLoader for proper tracking
- **Documentation**: Updated README and EDITOR_TOOLS_GUIDE to reflect automatic monitoring

### Improved
- **Simplified API**: No need to remember special "Monitored" methods
- **Complete tracking**: Dashboard always has full data (no missed loads)
- **Consistent behavior**: Same methods work everywhere (Facade, Scopes, custom loaders)
- **Zero overhead**: Monitoring code completely stripped in builds

### Fixed
- Issue where users had to manually use `.LoadAssetAsyncMonitored()` extensions
- Dashboard missing data when users forgot to use monitored versions
- Inconsistent API between monitored and non-monitored loads

---

## [2.0.0] - 2025-01-XX - MAJOR UPDATE

### Added - Editor Tools & Monitoring
- **Dashboard Window**: Real-time asset monitoring with 4 tabs (Assets, Performance, Scopes, Settings)
- **Asset Tracker Service**: Centralized tracking of all loaded assets with memory usage and reference counts
- **Performance Metrics System**: Real-time performance monitoring, load time tracking, cache hit ratio analysis
- **Custom Inspectors**: Beautiful UI Toolkit inspectors for all scope components with live data
- **Progress Bar Inspector**: Interactive testing controls for AddressableProgressBar component
- **ScriptableObject Config Inspectors**: Validation and management tools for configurations

### Added - Configuration System
- **AddressablePreloadConfig**: ScriptableObject for configuring asset preloading (no more hardcoded addresses!)
- **PoolConfiguration**: Centralized pool management configuration
- **DebugSettings**: Runtime debug settings with load simulation and profiling options
- **AssetScopeType enum**: Type-safe scope selection

### Added - Runtime UI Components
- **AddressableProgressBar**: Visual progress bar component with gradient colors, smooth animation, and auto-binding
- Support for TextMeshPro for better text rendering
- Auto-hide functionality when loading completes

### Added - Context Menus & Shortcuts
- GameObject menu: Quick add scope components
- Assets menu: Create configs directly
- Window menu: Dashboard (Ctrl+Alt+A), Documentation, Settings
- Tools menu: Quick setup wizards for scopes and configs

### Added - UI/UX
- Modern UI Toolkit-based dashboard with dark theme
- Color-coded scope visualization (Green=Global, Blue=Session, Yellow=Scene, Red=Hierarchy)
- Real-time data refresh with configurable intervals
- Memory usage progress bars with color warnings
- Expandable asset lists per scope
- Export performance reports to CSV

### Improved
- Better memory size estimation for different asset types
- Leak detection algorithm for assets loaded without ref count changes
- Validation system for configs with duplicate detection
- Automatic sorting and prioritization of preload entries

### Technical
- New Editor assembly definition (AddressableManager.Editor.asmdef)
- UI Toolkit UXML/USS for modern Editor UI
- Observer pattern for real-time updates
- Lazy loading for better performance

## [1.0.3] - 2025-01-XX

### Fixed
- Fixed LoadAssetsByLabelAsync type mismatch
- Added SharedListOperationTracker and ListItemHandle classes
- Proper reference counting for label-loaded assets

## [1.0.2] - 2025-01-XX

### Changed
- Renamed namespace from Game.Addressables to AddressableManager
- Updated all runtime files with new namespace

## [1.0.1] - 2025-01-XX

### Fixed
- Fixed Convert<object>() bug
- Changed caching strategy to use type-specific cache keys

## [1.0.0] - 2025-01-XX

### Added
- Initial production release
- Core Foundation with AssetLoader
- Scope Management (Global/Session/Scene/Hierarchy)
- Object Pooling System with Factory Pattern
- Progress Tracking with Observer Pattern
- Facade APIs
