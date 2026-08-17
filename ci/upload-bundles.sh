#!/usr/bin/env bash
# Step 3 of infrastructure §5: publish bundles.
#
# MUST run before upload-catalog.sh. Uploading the catalog first opens a window in which clients
# read a catalog naming bundles that are not on the origin yet, which is a 404 in production.
# Bundles first makes the deploy atomic from the client's side: new content stays invisible until
# the catalog names it.

source "$(dirname "$0")/_common.sh"

require_cmd aws jq
require_vars PLATFORM APP_VERSION BUCKET GAME R2_ENDPOINT
require_manifest

BUNDLE_DIR="$SERVER_DATA/$PLATFORM/bundles"
[ -d "$BUNDLE_DIR" ] || fail "bundle directory not found: $BUNDLE_DIR"

expected_count="$(jq '.bundles | length' "$MANIFEST_PATH")"
log "publishing $expected_count bundle(s) from $BUNDLE_DIR"

# --size-only rather than the default timestamp comparison: a bundle's file name embeds a content
# hash, so a name that already exists holds identical bytes. Re-uploading it would churn the edge
# cache for nothing. This is what keeps the bundle space append-only.
#
# immutable, one year: the name changes whenever the content does, so a bundle can never go stale.
aws s3 sync "$BUNDLE_DIR/" "$(bucket_prefix)/bundles/" \
    --endpoint-url "$R2_ENDPOINT" \
    --cache-control "public, max-age=31536000, immutable" \
    --content-type "application/octet-stream" \
    --exclude "*" \
    --include "*.bundle" \
    --size-only

log "confirming every manifest bundle is on the origin"
missing=0
while IFS= read -r name; do
    if ! aws s3api head-object \
            --bucket "$BUCKET" \
            --key "$GAME/$PLATFORM/bundles/$name" \
            --endpoint-url "$R2_ENDPOINT" >/dev/null 2>&1; then
        printf '  missing on origin: %s\n' "$name" >&2
        missing=$((missing + 1))
    fi
done < <(jq -r '.bundles[].fileName' "$MANIFEST_PATH")

[ "$missing" -eq 0 ] || fail "$missing bundle(s) did not reach the origin; do NOT upload the catalog"

log "OK - $expected_count bundle(s) present on the origin"
