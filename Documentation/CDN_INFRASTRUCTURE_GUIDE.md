# CDN System — Infrastructure and Operations Guide

**Hosting layout, cache policy, deployment pipeline, and runbook**

Status: **Proposed** | Last Updated: August 2026

Companion documents: [CDN_SYSTEM_DESIGN.md](CDN_SYSTEM_DESIGN.md) · [CDN_IMPLEMENTATION_PLAN.md](CDN_IMPLEMENTATION_PLAN.md)

---

## Table of Contents

1. [Topology](#1-topology)
2. [Storage Layout](#2-storage-layout)
3. [Cache Policy](#3-cache-policy)
4. [Environments](#4-environments)
5. [Deployment Workflow](#5-deployment-workflow)
6. [Content State Lifecycle](#6-content-state-lifecycle)
7. [CI/CD Reference](#7-cicd-reference)
8. [Rollback](#8-rollback)
9. [Security](#9-security)
10. [Monitoring](#10-monitoring)
11. [Cost Model](#11-cost-model)
12. [Runbook](#12-runbook)

---

## 1. Topology

```
   Unity CI (GitHub Actions)
        │  build → verify → upload
        ▼
   Cloudflare R2  ──────────────►  Cloudflare CDN  ──────────────►  Players
   (origin, zero egress)            (edge cache)                    (Unity UWR)
```

Addressables does not care what serves the content — it needs a URL that returns the catalog, its hash
file, and the bundles. That makes the origin a free choice, and R2 behind Cloudflare's CDN is the
cost-efficient one: R2 charges no egress to the internet, and cached bundles are served from the edge.

Unity Cloud Content Delivery remains a drop-in alternative (change one profile variable) if bandwidth
billing ever becomes preferable to running this. Nothing in the design is R2-specific.

### Requirements the origin must satisfy

| Requirement | Why |
|-------------|-----|
| HTTP range requests | Unity's caching downloader resumes partial transfers with `Range` |
| Stable `ETag` | Edge revalidation |
| Per-object `Cache-Control` | Bundles and catalogs need opposite policies (§3) |
| Prefix-scoped cache purge | Fast catalog rollback and deploy invalidation |

R2 + Cloudflare satisfies all four.

---

## 2. Storage Layout

This is the most consequential decision in this document. Get it wrong and either patches break or
caching does.

```
r2://game-content/
└── <game>/
    └── <platform>/                        ios | android | standalonewindows64
        ├── bundles/                       ── append-only, content-addressed, immutable
        │   ├── ui_common_a3f1c9e4….bundle
        │   ├── stage01_assets_7b2d0f11….bundle
        │   └── …
        └── catalog/
            └── <appVersion>/              ── mutable, one folder per shipped player version
                ├── catalog_<appVersion>.bin
                ├── catalog_<appVersion>.hash
                └── build-manifest.json
```

### Why bundles and catalogs are separated

Addressables lets you configure the remote catalog path independently of group load paths
(`RemoteCatalogBuildPath` / `RemoteCatalogLoadPath` in settings, versus `LoadPath` on the group schema).
Using that separation gives two objects with opposite lifecycles:

- **Bundles are immutable and shared.** Bundle filenames contain a content hash, so a changed asset
  produces a *new* file rather than overwriting an old one. Old players keep resolving old bundles;
  new players get new ones. The folder is append-only and every object can be cached for a year.
- **Catalogs are mutable and per-app-version.** A content update overwrites the catalog for exactly one
  app version. It must never be cached for long, and it must never be shared between app versions —
  a catalog produced by `BuildContentUpdate` is only valid against the `content_state.bin` of the player
  build it was derived from.

Collapsing these into one folder forces a single cache policy on both, which means either stale catalogs
or bundles that are re-validated on every request.

### Mandatory group setting

Bundle naming **must** include the content hash — `Append Hash to Filename` or `Use Hash of AssetBundle`
on the group's `BundledAssetGroupSchema`. Without a hash in the filename, a content update overwrites
existing bundle objects, which simultaneously breaks immutable caching and breaks every player still on
the previous content version. `CatalogVerifier` asserts this.

### Path mapping

| Addressables setting | Value |
|----------------------|-------|
| Group `LoadPath` | `https://cdn.<domain>/<game>/[BuildTarget]/bundles` |
| Group `BuildPath` | `ServerData/[BuildTarget]/bundles` |
| `RemoteCatalogLoadPath` | `https://cdn.<domain>/<game>/[BuildTarget]/catalog/[PlayerVersion]` |
| `RemoteCatalogBuildPath` | `ServerData/[BuildTarget]/catalog/[PlayerVersion]` |

`[PlayerVersion]` comes from `OverridePlayerVersion`, already set to
`[UnityEditor.PlayerSettings.bundleVersion]` in the project — keep it that way. If it were left as the
default timestamp, each build would produce a differently-named catalog and shipped players would request
a filename that no longer exists.

At runtime the host portion is rewritten by `InternalIdTransformFunc` (design §5.6), so these baked URLs
act as the fallback rather than the only option.

---

## 3. Cache Policy

| Path pattern | `Cache-Control` | Edge TTL | Rationale |
|--------------|-----------------|----------|-----------|
| `/*/bundles/*.bundle` | `public, max-age=31536000, immutable` | 1 year | Content-addressed filename; the object can never change |
| `/*/catalog/*/catalog_*.hash` | `public, max-age=0, must-revalidate` | 60 s | Polled on every boot. This is the propagation-latency knob for the whole system. |
| `/*/catalog/*/catalog_*.bin` | `public, max-age=0, must-revalidate` | 60 s | Fetched only when the hash differs |
| `/*/catalog/*/build-manifest.json` | `no-store` | bypass | Operational metadata, never client-facing |

### Content types

| Extension | `Content-Type` |
|-----------|----------------|
| `.bundle` | `application/octet-stream` |
| `.bin` | `application/octet-stream` |
| `.json` | `application/json` |
| `.hash` | `text/plain` |

Do not enable Brotli/gzip on `.bundle` — bundles are already LZ4 or LZMA compressed, so re-compressing
burns edge CPU for no size reduction.

### Purge on deploy

TTL is a safety net, not the propagation mechanism. Every deploy explicitly purges the catalog prefix:

```bash
curl -X POST "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/purge_cache" \
  -H "Authorization: Bearer $CF_API_TOKEN" \
  -H "Content-Type: application/json" \
  --data "{\"files\":[
      \"https://cdn.$DOMAIN/$GAME/$PLATFORM/catalog/$APP_VERSION/catalog_$APP_VERSION.hash\",
      \"https://cdn.$DOMAIN/$GAME/$PLATFORM/catalog/$APP_VERSION/catalog_$APP_VERSION.bin\"
  ]}"
```

Purging both together avoids the window where a client sees a fresh `.hash` but a stale catalog.
**Never purge the bundle prefix** — those objects are immutable, and purging them only forces the edge
to refetch identical bytes from origin.

---

## 4. Environments

| Ring | Host | Bucket prefix | Who |
|------|------|---------------|-----|
| Local | `http://localhost:8080` | `ServerData/` on disk | Individual developer |
| Dev | `https://cdn-dev.<domain>` | `game-content-dev/` | Every merge to `develop` |
| Staging | `https://cdn-stg.<domain>` | `game-content-stg/` | Release candidates, QA |
| Prod | `https://cdn.<domain>` | `game-content/` | Tagged releases only |

Separate buckets, not separate prefixes in one bucket — it makes an accidental cross-ring write
impossible rather than merely unlikely.

Because the runtime host rewrite exists, a single prod build can be pointed at staging by QA without a
rebuild. That capability is deliberately gated: it is enabled by a development-build flag or a signed
debug command, never by a plain config value that could ship enabled.

---

## 5. Deployment Workflow

Order matters. Each step is a gate for the next.

```
1. Build          BuildContent (full) or BuildContentUpdate (delta)
                  └─ requires: correct profile, content_state.bin for update builds
2. Verify         CatalogVerifier — settings contract, catalog↔bundle consistency
                  └─ fails the job before anything reaches the network
3. Upload bundles bundles/ → R2, Cache-Control: immutable
                  └─ append-only; existing objects are never overwritten
4. Upload catalog catalog/<appVersion>/ → R2, Cache-Control: must-revalidate
                  └─ ALWAYS AFTER bundles
5. Purge          catalog .hash + catalog body at the edge
6. Post-check     HEAD every URL in the manifest; assert 200 and expected headers
7. Archive        content_state.bin + build-manifest.json → CI artifact store
```

**Step 3 before step 4 is not a stylistic preference.** Uploading the catalog first creates a window in
which clients read a catalog referencing bundles that do not exist yet, producing 404s in production.
Bundles-then-catalog makes the deploy atomic from the client's perspective: the new content is invisible
until the catalog names it.

### Upload commands

R2 is S3-compatible.

```bash
# 3. bundles — immutable, append-only
aws s3 sync "ServerData/$PLATFORM/bundles/" "s3://$BUCKET/$GAME/$PLATFORM/bundles/" \
  --endpoint-url "$R2_ENDPOINT" \
  --cache-control "public, max-age=31536000, immutable" \
  --content-type "application/octet-stream" \
  --size-only

# 4. catalog — short TTL, overwrite
aws s3 sync "ServerData/$PLATFORM/catalog/$APP_VERSION/" \
  "s3://$BUCKET/$GAME/$PLATFORM/catalog/$APP_VERSION/" \
  --endpoint-url "$R2_ENDPOINT" \
  --cache-control "public, max-age=0, must-revalidate" \
  --exclude "build-manifest.json"
```

`--size-only` on the bundle sync is safe precisely because filenames are content-hashed: same name means
same bytes.

### Post-deploy verification

```bash
# every URL in the manifest must return 200 with the expected cache header
jq -r '.bundles[].url, .catalog.url, .catalog.hashUrl' build-manifest.json \
  | while read -r url; do
      code=$(curl -sS -o /dev/null -w '%{http_code}' -I "$url")
      [ "$code" = "200" ] || { echo "FAIL $code $url"; exit 1; }
    done
```

A deploy that skips this step is how R2 (risk register) reaches production.

---

## 6. Content State Lifecycle

`addressables_content_state.bin` records the state of content at the moment a player build was made.
`BuildContentUpdate` diffs against it to decide which bundles must be rebuilt.

**Losing it for a shipped app version permanently ends delta updates for that version.** Every subsequent
patch for those players becomes a full re-download. There is no way to reconstruct it.

### Rules

1. Store it **outside `Assets/`** — `ServerData/ContentState/[BuildTarget]/` — and gitignore it.
   Keeping it in `Assets/` means it churns in every commit and gets resolved incorrectly on merge.
2. Archive it as a CI artifact on **every** build, keyed `<platform>/<appVersion>/`.
   Also mirror it to a bucket prefix so it survives the CI retention window.
3. Retain for as long as the app version has meaningful player population, plus a margin.
   Minimum: two years or two major versions, whichever is longer.
4. A content-update build **fails** if the state file is missing. It must never silently fall back to a
   full build — that produces a catalog that looks valid but forces every player to redownload everything.
5. Validate that the archived state file matches the app version being patched before using it.

### Retrieval

```bash
aws s3 cp "s3://$STATE_BUCKET/$GAME/$PLATFORM/$APP_VERSION/addressables_content_state.bin" \
  "ServerData/ContentState/$PLATFORM/" --endpoint-url "$R2_ENDPOINT"
```

---

## 7. CI/CD Reference

Two workflows. The distinction between them is exactly the distinction between a player release and a
content patch.

### `content-full.yml` — runs alongside a player build

```yaml
name: Content Full Build
on:
  workflow_dispatch:
    inputs:
      platform:    { required: true, type: choice, options: [Android, iOS] }
      environment: { required: true, type: choice, options: [dev, staging, prod] }

jobs:
  build:
    runs-on: [self-hosted, unity]
    steps:
      - uses: actions/checkout@v4

      - name: Build addressable content
        run: |
          "$UNITY" -batchmode -quit -nographics \
            -projectPath . \
            -logFile - \
            -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContent \
            -cdnProfile "${{ inputs.environment }}" \
            -buildTarget "${{ inputs.platform }}" \
            -outputDir "ServerData"

      - name: Verify catalog
        run: |
          "$UNITY" -batchmode -quit -nographics -projectPath . -logFile - \
            -executeMethod AddressableManager.Editor.Cdn.CatalogVerifier.VerifyCli \
            -manifest "ServerData/build-manifest.json"

      - name: Upload bundles
        run: ./ci/upload-bundles.sh
        env: { R2_ENDPOINT: "${{ secrets.R2_ENDPOINT }}" }

      - name: Upload catalog
        run: ./ci/upload-catalog.sh

      - name: Purge edge cache
        run: ./ci/purge-catalog.sh

      - name: Post-deploy check
        run: ./ci/verify-urls.sh

      - name: Archive content state          # never skip; see §6
        uses: actions/upload-artifact@v4
        with:
          name: content-state-${{ inputs.platform }}-${{ env.APP_VERSION }}
          path: ServerData/ContentState/**/addressables_content_state.bin
          retention-days: 90
          if-no-files-found: error

      - name: Mirror content state to durable storage
        run: ./ci/archive-content-state.sh
```

### `content-update.yml` — content-only patch, no player build

Identical except for two steps at the front:

```yaml
      - name: Fetch content state for target app version
        run: ./ci/fetch-content-state.sh
        env:
          APP_VERSION: ${{ inputs.app_version }}

      - name: Build content update
        run: |
          "$UNITY" -batchmode -quit -nographics -projectPath . -logFile - \
            -executeMethod AddressableManager.Editor.Cdn.CdnBuildCLI.BuildContentUpdate \
            -cdnProfile "${{ inputs.environment }}" \
            -buildTarget "${{ inputs.platform }}" \
            -contentState "ServerData/ContentState/${{ inputs.platform }}/addressables_content_state.bin"
```

`BuildContentUpdate` also runs the static-content restriction check (plan §1.4) and fails if a group
marked static changed — that situation cannot be patched and requires a new player build.

---

## 8. Rollback

### Bad content

Because bundles are append-only and content-addressed, the previous content is still on the origin.
Rolling back is a catalog swap:

```bash
# restore the previous catalog for this app version
aws s3 cp "s3://$ARCHIVE/$GAME/$PLATFORM/$APP_VERSION/<previous-build-id>/" \
  "s3://$BUCKET/$GAME/$PLATFORM/catalog/$APP_VERSION/" \
  --recursive --endpoint-url "$R2_ENDPOINT" \
  --cache-control "public, max-age=0, must-revalidate"

./ci/purge-catalog.sh
```

Recovery time: one upload plus the 60 s edge TTL. This is why every catalog upload is also archived
under a build id — without that archive there is nothing to roll back *to*.

### Bad player build

Not recoverable through the CDN. Requires a store release. This is the reason the static-content check
is a build-time gate rather than a warning.

### Client-side corruption in the field

`Cdn.ClearCacheAsync(key)` for a targeted eviction, `ClearAllAsync` as the last resort. Both are worth
exposing behind a remote-config flag so they can be triggered without shipping a client update.

---

## 9. Security

Start from an honest threat model. Asset bundles are not secret — anyone who installs the game can
extract them. Authentication on the CDN is about **bandwidth theft and abuse**, not confidentiality.

| Control | Recommendation |
|---------|---------------|
| Hotlink protection | Enable. Cheapest defence against third parties serving their content from your bucket. |
| Rate limiting (WAF) | Per-IP request ceiling well above legitimate first-install traffic. |
| Bearer token via `WebRequestOverride` | Optional. Simple to implement; validated by a Cloudflare Worker. Note the token ships inside the client, so it deters casual scraping only. |
| Signed URLs | Only if there is a hard requirement to prevent link sharing. Costs: the catalog can no longer contain final URLs, every request must be signed through `InternalIdTransformFunc`, and expiry interacts badly with long downloads and resume. **Not recommended for this project.** |
| Bucket write access | CI service account only, scoped per environment bucket. No human write credentials to the prod bucket. |
| Public read | Bundles and catalogs are public-read by design. Never place anything else in these buckets. |
| TLS | Enforced; HTTP requests redirected. Unity clients must use `https://` load paths. |

`content_state.bin` and `build-manifest.json` are **not** public. They live in a separate, private
archive bucket.

---

## 10. Monitoring

### Edge (Cloudflare analytics)

| Metric | Alert threshold |
|--------|----------------|
| Cache hit ratio, bundle paths | < 90% sustained over 1 h |
| 4xx rate | > 1% of requests — usually a missing bundle or a broken deploy |
| 5xx rate | > 0.1% |
| Egress bytes | > 2× the 7-day rolling median (anomaly, possible scraping) |
| p95 origin response time | > 500 ms |

### Client (via `ICdnTelemetry`)

| Metric | Why |
|--------|-----|
| Download success rate by app version | Distinguishes a CDN problem from a client-build problem |
| Error code distribution | `BundleNotFound` spiking means a deploy is incomplete; `Timeout` spiking means a network/region problem |
| p50 / p95 patch duration by size bucket | Detects regional degradation the edge dashboard hides |
| Bytes downloaded per app version | Validates that delta updates are actually delta |
| Offline-fallback rate | If high, boot is too dependent on the network |

The edge and client views answer different questions. Edge tells you the CDN is healthy; client tells
you players are succeeding. They diverge more often than you would expect.

---

## 11. Cost Model

The variables that matter, in rough order of impact:

1. **Egress.** The dominant cost for game content at scale, and the reason for R2: object storage with no
   per-GB egress charge to the internet, fronted by a CDN that serves cache hits without touching origin.
2. **Storage.** Append-only bundle folders grow monotonically. Budget for growth and define a pruning
   policy for app versions with no remaining players (see §6 retention).
3. **Class A/B operations.** Writes and reads against the bucket. Negligible relative to egress unless
   cache hit ratio is poor — which loops back to §3 being configured correctly.
4. **Full-download waste.** The single largest avoidable cost is shipping full content on every patch.
   That is what Phase 1 of the implementation plan eliminates, and it dwarfs infrastructure tuning.

Check current provider pricing before modelling; the qualitative ranking above is stable, the numbers are
not. Unity CCD is billed on bandwidth and is worth re-pricing at the point where operational simplicity
outweighs egress cost.

---

## 12. Runbook

### Players report "stuck on old content"

1. `curl -I` the `.hash` URL. Check `cf-cache-status` and `age`.
2. If `age` is large, the purge step failed or the cache rule is misconfigured → purge manually, then fix
   the rule.
3. If the header is correct, compare the served hash against the archived `build-manifest.json`. A
   mismatch means the wrong catalog was uploaded.

### Spike in `BundleNotFound`

Almost always upload ordering (§5, step 3 before 4) or a partial bundle sync.

1. Diff the manifest's bundle list against the bucket listing.
2. Re-upload the missing bundles. No purge needed — they were never cached.
3. Verify the deploy script's failure handling; a partial `aws s3 sync` should have failed the job.

### Spike in `BundleCrcMismatch`

1. Confirm it is not a single client's corrupted cache (check spread across devices).
2. If widespread, the object at origin is corrupt — re-upload from the CI artifact and purge.
3. The client auto-repair path (design §8) evicts and retries once, so a transient case self-heals.

### Egress anomaly

1. Check cache hit ratio first — a config regression on the bundle cache rule looks identical to abuse.
2. Check request distribution by IP/ASN for scraping.
3. Tighten WAF rate limits; consider hotlink protection if not already on.

### Content update produced a full-size patch

1. Was `content_state.bin` present and correct for that app version? (§6)
2. Did a static group change? Check the restriction report from the build.
3. Did `MonoScriptBundleNaming` or `BuiltInBundleNaming` change between builds? Either causes global
   bundle churn. Both are pinned by the settings contract (design §9) for this reason.

### Need to point a prod build at staging

Use the runtime environment switch (design §5.6). Confirm it is gated to development builds or a signed
debug command — if a plain config value can do it, that is a bug to fix, not a workflow to use.
