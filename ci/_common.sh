#!/usr/bin/env bash
# Shared setup for every ci/*.sh script.
#
# Sourced, not executed. Every script starts with:
#     source "$(dirname "$0")/_common.sh"
#
# Why a common file: the deploy order in infrastructure §5 is a sequence of gates, and a step that
# proceeds on a half-configured environment defeats the gate after it. Requiring variables up front,
# by name, turns a misconfigured runner into an immediate stop with a readable message instead of an
# aws command that fails three steps later for an unrelated-looking reason.

set -euo pipefail

# --- required of every script -------------------------------------------------------------------
# PLATFORM     Unity build target, matching the ServerData subfolder (e.g. Android, iOS,
#              StandaloneWindows64)
# APP_VERSION  PlayerSettings.bundleVersion this build was made for. The catalog folder is named
#              after it, and players poll the path derived from their own version.
# BUCKET       R2 bucket name
# GAME         Path prefix inside the bucket, so one bucket can host several titles
# R2_ENDPOINT  S3-compatible endpoint for the R2 account
#
# AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY are read by the aws CLI itself and are not checked here;
# the CLI reports its own credential errors clearly.

SERVER_DATA="${SERVER_DATA:-ServerData}"
MANIFEST_PATH="${MANIFEST_PATH:-$SERVER_DATA/build-manifest.json}"

log()  { printf '[%s] %s\n' "$(basename "${0%.sh}")" "$*"; }
fail() { printf '[%s] FAILURE: %s\n' "$(basename "${0%.sh}")" "$*" >&2; exit 1; }

require_vars() {
    local missing=()
    for name in "$@"; do
        if [ -z "${!name:-}" ]; then
            missing+=("$name")
        fi
    done

    if [ ${#missing[@]} -gt 0 ]; then
        fail "missing required environment variable(s): ${missing[*]}"
    fi
}

require_cmd() {
    for name in "$@"; do
        command -v "$name" >/dev/null 2>&1 || fail "required command not found on PATH: $name"
    done
}

# The manifest is the authority on what this build produced. Reading paths off the filesystem
# instead would happily publish leftovers from an earlier build that happen to still be there.
require_manifest() {
    [ -f "$MANIFEST_PATH" ] || fail "no build manifest at $MANIFEST_PATH (did the build step run?)"
    require_cmd jq

    local manifest_version
    manifest_version="$(jq -r '.manifestVersion // empty' "$MANIFEST_PATH")"
    [ -n "$manifest_version" ] || fail "$MANIFEST_PATH has no manifestVersion; it is not a build manifest"

    case "$manifest_version" in
        1.0) ;;
        *) fail "manifest schema $manifest_version is newer than these scripts understand (expected 1.0)" ;;
    esac

    local manifest_app_version manifest_platform
    manifest_app_version="$(jq -r '.appVersion' "$MANIFEST_PATH")"
    manifest_platform="$(jq -r '.platform' "$MANIFEST_PATH")"

    [ "$manifest_app_version" = "$APP_VERSION" ] || fail \
        "manifest is for app version '$manifest_app_version' but APP_VERSION is '$APP_VERSION'. \
Uploading would put this content under a version path no player built from it will poll."

    [ "$manifest_platform" = "$PLATFORM" ] || fail \
        "manifest is for platform '$manifest_platform' but PLATFORM is '$PLATFORM'"
}

# Object key prefix shared by every script, matching the layout in infrastructure §2.
bucket_prefix() {
    printf 's3://%s/%s/%s' "$BUCKET" "$GAME" "$PLATFORM"
}
