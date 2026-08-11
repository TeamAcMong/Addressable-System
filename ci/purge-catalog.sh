#!/usr/bin/env bash
# Step 5 of infrastructure §5: purge the catalog at the edge.
#
# Only the catalog and its hash file. Bundles are immutable and named by content hash, so purging
# them would evict a warm cache for no benefit and make the next player's download slower.

source "$(dirname "$0")/_common.sh"

require_cmd curl jq
require_vars PLATFORM APP_VERSION GAME CDN_BASE_URL CF_ZONE_ID CF_API_TOKEN
require_manifest

catalog_name="$(jq -r '.catalog.fileName' "$MANIFEST_PATH")"
hash_name="$(jq -r '.catalog.hashFileName' "$MANIFEST_PATH")"

base="$CDN_BASE_URL/$GAME/$PLATFORM/catalog/$APP_VERSION"
log "purging $base/$catalog_name and $base/$hash_name"

payload="$(jq -n --arg a "$base/$catalog_name" --arg b "$base/$hash_name" '{files: [$a, $b]}')"

response="$(curl -sS -X POST \
    "https://api.cloudflare.com/client/v4/zones/$CF_ZONE_ID/purge_cache" \
    -H "Authorization: Bearer $CF_API_TOKEN" \
    -H "Content-Type: application/json" \
    --data "$payload")"

if [ "$(printf '%s' "$response" | jq -r '.success')" != "true" ]; then
    printf '%s\n' "$response" >&2
    fail "cache purge was rejected"
fi

log "OK - edge cache purged for the catalog"
