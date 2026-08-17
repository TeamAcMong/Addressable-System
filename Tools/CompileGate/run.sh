#!/usr/bin/env bash
# Compile gate for this worktree (feat/cdn-system).
#
# ProjectVersion.txt here is 6000.5.7f1 and that Unity IS installed, so compiler and reference
# assemblies are the same version — no skew. Dependencies are built from PackageCache source, not
# taken from Library/ScriptAssemblies: Unity compiles Editor assemblies against mscorlib while
# this gate references netstandard, and mixing the two produces ~189 phantom CS0012 errors.
#
#   bash Tools/CompileGate/run.sh            # Runtime + Editor
#   bash Tools/CompileGate/run.sh --clean    # rebuild every dependency
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export ADDR_GATE_ROOT="$(cd "$HERE/../.." && pwd)"
export ADDR_GATE_REF_UNITY="C:\\Program Files\\Unity\\Hub\\Editor\\6000.5.7f1\\Editor\\Data"
export ADDR_GATE_CSC_UNITY="C:\\Program Files\\Unity\\Hub\\Editor\\6000.5.7f1\\Editor\\Data"
export ADDR_GATE_PREBUILT_DEPS=0
export ADDR_GATE_OUT="${ADDR_GATE_OUT:-$HERE/.deps}"
node "$HERE/gate.js" "$@"
