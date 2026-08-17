#!/usr/bin/env bash
# First step of content-update.yml: restore the baseline this patch will be built against.
#
# A content update is a diff, and this is the other side of it. Without the state file from the
# build that shipped to players, Addressables has no baseline: BuildContentUpdate would throw
# FileNotFoundException, and CdnBuildCLI.BuildContentUpdate refuses before it gets that far rather
# than degrading into a full build. A full build here would produce content that shipped players
# cannot patch to, which is the failure this whole flow exists to avoid.

source "$(dirname "$0")/_common.sh"

require_cmd aws
require_vars PLATFORM APP_VERSION ARCHIVE_BUCKET GAME R2_ENDPOINT

DEST_DIR="$SERVER_DATA/ContentState/$PLATFORM"
DEST="$DEST_DIR/addressables_content_state.bin"
SOURCE_KEY="$GAME/$PLATFORM/$APP_VERSION/latest/addressables_content_state.bin"

if [ -f "$DEST" ]; then
    # Refuse rather than overwrite. On a self-hosted runner the workspace persists between jobs, and
    # silently replacing a state file left by an earlier run makes the source of the baseline
    # ambiguous exactly when it matters most.
    fail "$DEST already exists. Clean the workspace before fetching, so the baseline this patch is \
built against is unambiguous."
fi

log "fetching baseline for $PLATFORM $APP_VERSION"

if ! aws s3api head-object \
        --bucket "$ARCHIVE_BUCKET" \
        --key "$SOURCE_KEY" \
        --endpoint-url "$R2_ENDPOINT" >/dev/null 2>&1; then
    fail "no archived content state at s3://$ARCHIVE_BUCKET/$SOURCE_KEY. There is no baseline for \
app version $APP_VERSION on $PLATFORM, so no delta update can be built for it. Either the full \
build for this version never archived its state, or the version is wrong."
fi

mkdir -p "$DEST_DIR"
aws s3 cp "s3://$ARCHIVE_BUCKET/$SOURCE_KEY" "$DEST" --endpoint-url "$R2_ENDPOINT"

[ -s "$DEST" ] || fail "fetched content state is empty: $DEST"

log "OK - baseline restored to $DEST ($(wc -c < "$DEST") bytes)"
log "    ContentStateManager.Validate will reject it if its playerVersion is not $APP_VERSION"
