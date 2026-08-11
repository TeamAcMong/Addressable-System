#!/usr/bin/env bash
# Step 6 of infrastructure §5: check the deploy from the outside.
#
# Every earlier step verified the build or the origin. This is the only one that asks the question a
# player asks: does the CDN return this URL, with the headers that make caching behave? It runs
# after the purge so it observes the state players will actually get.

source "$(dirname "$0")/_common.sh"

require_cmd curl jq awk
require_vars PLATFORM APP_VERSION GAME CDN_BASE_URL
require_manifest

failures=0

header_value() {
    # $1 = raw headers, $2 = lowercase header name
    printf '%s' "$1" | tr -d '\r' | awk -v want="$2" -F': ' \
        'tolower($1) == want { $1=""; sub(/^: /, ""); print; exit }'
}

check_url() {
    local url="$1" expect_cache="$2" expect_bytes="$3"
    local headers status cache_control length

    if ! headers="$(curl -sS -I --max-time 30 "$url" 2>/dev/null)"; then
        printf '  UNREACHABLE   %s\n' "$url" >&2
        failures=$((failures + 1))
        return
    fi

    status="$(printf '%s' "$headers" | awk 'NR==1 { print $2 }')"
    if [ "$status" != "200" ]; then
        printf '  HTTP %-8s %s\n' "$status" "$url" >&2
        failures=$((failures + 1))
        return
    fi

    # Size is compared against the manifest, not merely checked for presence: a truncated upload
    # still answers 200.
    length="$(header_value "$headers" "content-length")"
    if [ -n "$expect_bytes" ] && [ -n "$length" ] && [ "$length" != "$expect_bytes" ]; then
        printf '  SIZE %s != %s   %s\n' "$length" "$expect_bytes" "$url" >&2
        failures=$((failures + 1))
        return
    fi

    cache_control="$(header_value "$headers" "cache-control")"
    case "$cache_control" in
        *"$expect_cache"*) ;;
        *)
            printf '  CACHE-CONTROL "%s" lacks "%s"   %s\n' "$cache_control" "$expect_cache" "$url" >&2
            failures=$((failures + 1))
            ;;
    esac
}

catalog_name="$(jq -r '.catalog.fileName' "$MANIFEST_PATH")"
hash_name="$(jq -r '.catalog.hashFileName' "$MANIFEST_PATH")"
catalog_base="$CDN_BASE_URL/$GAME/$PLATFORM/catalog/$APP_VERSION"

log "checking catalog"
check_url "$catalog_base/$catalog_name" "must-revalidate" "$(jq -r '.catalog.sizeBytes' "$MANIFEST_PATH")"
check_url "$catalog_base/$hash_name" "must-revalidate" "$(jq -r '.catalog.hashFileSizeBytes' "$MANIFEST_PATH")"

count="$(jq '.bundles | length' "$MANIFEST_PATH")"
log "checking $count bundle(s)"
while IFS=$'\t' read -r name size; do
    check_url "$CDN_BASE_URL/$GAME/$PLATFORM/bundles/$name" "immutable" "$size"
done < <(jq -r '.bundles[] | [.fileName, .sizeBytes] | @tsv' "$MANIFEST_PATH")

[ "$failures" -eq 0 ] || fail "$failures URL check(s) failed; the deploy is not serviceable"

log "OK - catalog and $count bundle(s) reachable with the expected cache headers"
