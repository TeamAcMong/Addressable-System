#!/usr/bin/env bash
# Step 4 of infrastructure §5: publish the catalog.
#
# Runs ONLY after upload-bundles.sh confirmed every bundle reached the origin. The catalog is the
# switch that makes new content visible; flipping it early points players at objects that do not
# exist yet.

source "$(dirname "$0")/_common.sh"

require_cmd aws jq
require_vars PLATFORM APP_VERSION BUCKET GAME R2_ENDPOINT
require_manifest

CATALOG_DIR="$SERVER_DATA/$PLATFORM/catalog/$APP_VERSION"
[ -d "$CATALOG_DIR" ] || fail "catalog directory not found: $CATALOG_DIR"

catalog_name="$(jq -r '.catalog.fileName' "$MANIFEST_PATH")"
hash_name="$(jq -r '.catalog.hashFileName' "$MANIFEST_PATH")"

[ -n "$catalog_name" ] && [ "$catalog_name" != "null" ] || fail "manifest records no catalog file name"
[ -n "$hash_name" ] && [ "$hash_name" != "null" ] || fail "manifest records no catalog hash file name"

# The name players request is derived from their own app version, so a correctly built catalog
# under any other name is unreachable. Checked before publishing rather than discovered after.
expected_stem="catalog_$APP_VERSION"
[ "${catalog_name%.*}" = "$expected_stem" ] || fail \
    "catalog is named '$catalog_name', expected '$expected_stem.bin' or '$expected_stem.json' for app version $APP_VERSION"

[ -f "$CATALOG_DIR/$catalog_name" ] || fail "catalog missing on disk: $CATALOG_DIR/$catalog_name"
[ -f "$CATALOG_DIR/$hash_name" ] || fail "catalog hash missing on disk: $CATALOG_DIR/$hash_name"

log "publishing $catalog_name and $hash_name under catalog/$APP_VERSION/"

# max-age=0, must-revalidate: the hash file is polled on every boot to detect new content, so it
# must be revalidated rather than served from cache. build-manifest.json is excluded on principle
# even though it lives outside this directory - it carries git SHAs and internal build metadata and
# is never published.
aws s3 sync "$CATALOG_DIR/" "$(bucket_prefix)/catalog/$APP_VERSION/" \
    --endpoint-url "$R2_ENDPOINT" \
    --cache-control "public, max-age=0, must-revalidate" \
    --exclude "build-manifest.json"

log "OK - catalog published"
