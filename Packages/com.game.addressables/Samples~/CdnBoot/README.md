# CDN Boot

The canonical boot sequence for a game that ships content from a CDN. Drop
`CdnBootExample` on a GameObject in your **first** scene and read it top to bottom —
it is written to be read rather than reused. Every branch corresponds to one boot
outcome, with a comment saying what a real game should do there.

## The one rule

`CdnManager.InitializeAsync()` must run before anything else touches Addressables.

Addressables initialises implicitly on its first load call, and the CDN hooks — the
host rewriter and the request decorator — are only honoured for content resolved
*after* they are installed. A single `AssetReference` on another object in the same
scene, or a `LoadAssetAsync` in another `Awake`, is enough to lose them.

That failure is silent by nature: the game boots, then 404s every remote bundle
against the wrong host. So `InitializeAsync` detects it and fails with a message
saying so, rather than continuing with a half-applied configuration.

## Before it will do anything

The example needs a `CdnSettings` asset with at least one environment, and it must
live in a `Resources` folder — that is how it is found at runtime
(`Resources.Load<CdnSettings>`). Create it with **Assets ▸ Create ▸ Addressable
Manager ▸ CDN Settings**, put it in any folder named `Resources`, and keep the file
name `CdnSettings` — the name is a constant, not a search.

Then open **Window ▸ Addressable Manager ▸ CDN Manager** and check the Settings
Validator tab. It reports every unmet requirement and offers a Fix All for the ones
that can be fixed automatically, which is faster than discovering them one 404 at a
time.

To try the flow with no CDN at all, use the Local Server tab: it serves a built
content folder over HTTP on localhost, which is what the package's own integration
tests run against.

## What to read next

- `Documentation/CDN_USAGE_GUIDE.md` in the package — the full guide, including
  what to do in each failure case.
- `Documentation/TROUBLESHOOTING.md` — one entry per `CdnErrorCode`.
