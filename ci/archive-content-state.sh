#!/usr/bin/env bash
# Step 7 of infrastructure §5: mirror the content state to durable storage.
#
# This is risk R1 in the risk register, and it is the one failure in the whole pipeline that cannot
# be undone. addressables_content_state.bin is the only baseline from which a delta update for this
# app version can ever be built. Lose it and every future change to this version's content requires
# shipping a new player build. The GitHub artifact upload alongside this has a retention window;
# this copy does not.
#
# The build-manifest is archived here too, and only here: it holds git SHAs and internal build
# metadata, so it goes to the private archive bucket and never to the CDN.

source "$(dirname "$0")/_common.sh"

require_cmd aws jq
require_vars PLATFORM APP_VERSION ARCHIVE_BUCKET GAME R2_ENDPOINT
require_manifest

# An update build does not emit a new state file - the write is gated on PreviousContentState being
# null (BuildScriptPackedMode.cs:575) and BuildContentUpdate always sets it. On an update the file
# below is the same one the build consumed, carried forward unchanged. Archiving it again is
# intentional: it keeps every build's archive self-contained.
STATE_FILE="$SERVER_DATA/ContentState/$PLATFORM/addressables_content_state.bin"

if [ ! -f "$STATE_FILE" ]; then
    # Do not fall back to a search. A state file found somewhere else is probably from a different
    # platform or an older build, and archiving the wrong baseline is worse than failing here.
    fail "content state not found at $STATE_FILE. A build that reports success without writing one \
means CopyAndRegisterContentState swallowed an exception (BuildScriptBase.cs:376-389). Do not \
publish this build - its successor could never be patched."
fi

build_date="$(jq -r '.buildDate' "$MANIFEST_PATH")"
git_sha="$(jq -r '.gitSha // "nogit"' "$MANIFEST_PATH")"
build_type="$(jq -r '.buildType' "$MANIFEST_PATH")"

# Keyed by version, then by build, so the newest baseline for a version is findable and older ones
# are never overwritten.
stamp="$(printf '%s' "$build_date" | tr -d ':-')"
archive_prefix="s3://$ARCHIVE_BUCKET/$GAME/$PLATFORM/$APP_VERSION"

log "archiving $build_type build $git_sha ($build_date)"

aws s3 cp "$STATE_FILE" "$archive_prefix/builds/$stamp-$git_sha/addressables_content_state.bin" \
    --endpoint-url "$R2_ENDPOINT"

aws s3 cp "$MANIFEST_PATH" "$archive_prefix/builds/$stamp-$git_sha/build-manifest.json" \
    --endpoint-url "$R2_ENDPOINT"

# The pointer fetch-content-state.sh reads. Written last so a failure above never leaves it naming
# an archive that does not exist.
aws s3 cp "$STATE_FILE" "$archive_prefix/latest/addressables_content_state.bin" \
    --endpoint-url "$R2_ENDPOINT"

aws s3 cp "$MANIFEST_PATH" "$archive_prefix/latest/build-manifest.json" \
    --endpoint-url "$R2_ENDPOINT"

log "verifying the archived copy is readable"
aws s3api head-object \
    --bucket "$ARCHIVE_BUCKET" \
    --key "$GAME/$PLATFORM/$APP_VERSION/latest/addressables_content_state.bin" \
    --endpoint-url "$R2_ENDPOINT" >/dev/null \
    || fail "archived content state is not readable back; treat this build as unpublishable"

log "OK - content state and manifest archived under $archive_prefix"
