#!/usr/bin/env bash
# Run one Unity -executeMethod entry point and refuse to call it a success on the strength of the
# exit code alone.
#
# WHY THIS EXISTS
#
# Unity exits 0 from -executeMethod when the assembly failed to compile. The method never runs, no
# side effect happens, and the step goes green. This repo's CLAUDE.md records it as a hard rule:
#
#   "Không tin exit code của Unity. -executeMethod mà assembly hỏng thì method không chạy và Unity
#    vẫn exit 0. Mọi lệnh batchmode phải: grep `error CS` trong log, và kiểm tác dụng phụ mong đợi
#    có thật không."
#
# The C# side of that rule is a scriptCompilationFailed gate inside each entry point, and every CDN
# entry point now carries one. But a gate inside the assembly cannot fire when the assembly is the
# thing that is broken — that is precisely the case it exists for and precisely the case it cannot
# reach. So the log has to be read from outside, which is this script's whole job.
#
# The third part of the rule — verify the real side effect — stays with the caller, because only the
# caller knows what artifact was supposed to appear. Pass --expect <path> and this checks it.
#
# USAGE
#   ci/unity-run.sh --name "Build addressable content" \
#                   [--expect ServerData/build-manifest.json] \
#                   -- -executeMethod Some.Entry.Point -arg value
#
# Requires UNITY to point at the editor binary.

set -uo pipefail

name="unity"
expect=""

while [ $# -gt 0 ]; do
    case "$1" in
        --name)   name="$2"; shift 2 ;;
        --expect) expect="$2"; shift 2 ;;
        --)       shift; break ;;
        *)        echo "unity-run: unexpected argument '$1' before --" >&2; exit 2 ;;
    esac
done

if [ $# -eq 0 ]; then
    echo "unity-run: nothing to run; expected '-- -executeMethod ...'" >&2
    exit 2
fi

: "${UNITY:?unity-run: UNITY is not set}"

log="$(mktemp -t unity-run-XXXXXX.log)"
trap 'rm -f "$log"' EXIT

echo "::group::$name"

# -logFile - streams to stdout. tee keeps it visible in the Actions log AND gives us a copy to scan;
# without the copy we would be grepping the terminal.
"$UNITY" -batchmode -quit -nographics -projectPath . -logFile - "$@" 2>&1 | tee "$log"
unity_status=${PIPESTATUS[0]}

echo "::endgroup::"

failed=0

# 1. Compiler diagnostics. Checked FIRST: when this fires, every other signal is meaningless.
#    -F is deliberate — "error CS" is a literal, and a regex here would risk matching a project
#    path that happens to contain the words.
if grep -qF "error CS" "$log"; then
    echo "::error title=$name::Script compilation failed. Unity's exit code was $unity_status, which is why this is checked separately."
    echo "--- compiler errors ---"
    grep -F "error CS" "$log" | sort -u | head -40
    failed=1
fi

# 2. Anything the entry point itself reported as fatal. The CDN CLIs all log this prefix before
#    EditorApplication.Exit(1), so a non-zero exit is corroborated rather than guessed at.
if grep -qF "FAILURE:" "$log"; then
    echo "::error title=$name::The entry point reported a failure."
    grep -F "FAILURE:" "$log" | sort -u | head -20
    failed=1
fi

# 3. Unity's own exit code, last, because it is the least trustworthy of the three.
if [ "$unity_status" -ne 0 ]; then
    echo "::error title=$name::Unity exited $unity_status."
    failed=1
fi

# 4. The side effect. A green run that produced nothing is the failure mode the exit code hides.
if [ -n "$expect" ] && [ ! -e "$expect" ]; then
    echo "::error title=$name::Reported success but '$expect' does not exist."
    failed=1
fi

if [ "$failed" -ne 0 ]; then
    exit 1
fi

if [ -n "$expect" ]; then
    echo "$name: ok (exit 0, no compiler errors, '$expect' present)"
else
    echo "$name: ok (exit 0, no compiler errors)"
fi
