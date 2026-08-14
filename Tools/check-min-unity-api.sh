#!/bin/sh
# Compile the package against a chosen Unity's reference assemblies.
#
# WHY THIS EXISTS
#
# 4.1.0-pre.4 shipped code that could not compile on the Unity version the package
# declares support for. It passed every check that ran: 0 error CS, 0 warning CS, the tab
# probe green, 57/57 tests green — all of it against Unity 6000.5.7f1, the newest editor
# on the machine. The call was FindObjectsByType<T>(FindObjectsInactive), which exists on
# Unity 6 and on nothing older, and package.json says unity 2023.1.
#
# Opening the project in an older editor produced:
#   SceneAssetScope.cs(136,58): error CS1503: cannot convert from
#   'UnityEngine.FindObjectsInactive' to 'UnityEngine.FindObjectsSortMode'
#
# Running the newest editor and running the oldest supported one are different tests, and
# only the second one answers "will this compile for the people installing it".
#
# USAGE
#   ./Tools/check-min-unity-api.sh "/c/Program Files/Unity/Hub/Editor/2022.3.62f3"
#
# Exits non-zero on any error CS. Warnings are printed and do not fail the run — this
# checks the API surface, not style.
#
# LIMITS, STATED SO THE GREEN IS NOT OVERREAD
#
# This is a compile against reference assemblies, not a Unity build. It does not run
# Unity's own response file, so a handful of diagnostics differ from what the editor
# reports (CS0649 on serialized fields, CS0619 on GetInstanceID). It will not catch
# anything outside the C# API surface. What it does catch is the one thing that shipped
# broken: a call that does not exist on the target version.

set -e

EDITOR="$1"
if [ -z "$EDITOR" ]; then
  echo "Usage: $0 <path-to-unity-editor-root>"
  echo "  e.g. $0 \"/c/Program Files/Unity/Hub/Editor/2022.3.62f3\""
  exit 1
fi

DATA="$EDITOR/Editor/Data"
if [ ! -d "$DATA/Managed/UnityEngine" ]; then
  echo "No managed assemblies under '$DATA/Managed/UnityEngine'."
  echo "Pass the editor root, the folder that contains Editor/Data."
  exit 1
fi

# Roslyn comes from whichever editor ships one; the compiler version does not affect
# which Unity API surface is visible, the reference assemblies do.
CSC=""
for candidate in "$DATA" /c/Program\ Files/Unity/Hub/Editor/*/Editor/Data; do
  found=$(find "$candidate/DotNetSdk" -name "csc.dll" 2>/dev/null | head -1)
  if [ -n "$found" ]; then CSC="$found"; DOTNET="$candidate/NetCoreRuntime/dotnet.exe"; break; fi
done

if [ -z "$CSC" ] || [ ! -f "$DOTNET" ]; then
  echo "Could not find Roslyn (csc.dll) plus NetCoreRuntime/dotnet.exe in any installed editor."
  exit 1
fi

# csc is a Windows binary and cannot read /c/... paths, so everything handed to it goes
# through cygpath. Without this the references resolve to nothing and the compile "passes"
# by failing to find the very API it is meant to check.
win() { cygpath -m "$1" 2>/dev/null || echo "$1"; }

ROOT=$(cd "$(dirname "$0")/.." && pwd)
ROOT=$(win "$ROOT")
DATA=$(win "$DATA")
CSC=$(win "$CSC")
PKG="$ROOT/Packages/com.game.addressables"
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
OUTWIN=$(win "$OUT")

# Package assemblies the project already built. These are version-neutral for this check:
# what is under test is the Unity API surface, which comes from $DATA.
PLAYER="$ROOT/Library/PlayerScriptAssemblies"
if [ ! -d "$PLAYER" ]; then
  echo "No $PLAYER. Open the project in Unity once so package assemblies exist, then re-run."
  exit 1
fi

echo "Editor under test : $EDITOR"
echo "Compiler          : $CSC"
echo ""

status=0

check_assembly() {
  label="$1"; shift
  srcdir="$1"; shift
  rsp="$OUT/$label.rsp"

  {
    find "$DATA/Managed/UnityEngine" -name "UnityEngine*.dll" -o -name "Unity.Scripting.dll" \
      | sed 's|^|-r:"|; s|$|"|'
    [ -f "$DATA/NetStandard/ref/2.1.0/netstandard.dll" ] \
      && echo "-r:\"$DATA/NetStandard/ref/2.1.0/netstandard.dll\""
    for dep in "$@"; do
      [ -f "$PLAYER/$dep.dll" ] && echo "-r:\"$PLAYER/$dep.dll\""
    done
    echo "-target:library"
    echo "-nostdlib+"
    echo "-langversion:9.0"
    echo "-define:TMP_PRESENT"
    echo "-out:\"$OUTWIN/$label.dll\""
    find "$srcdir" -name "*.cs" | sed 's|^|"|; s|$|"|'
  } > "$rsp"

  "$DOTNET" "$CSC" -noconfig "@$rsp" > "$OUT/$label.txt" 2>&1 || true

  # Diagnostics this harness raises but a real Unity build does not, because it compiles
  # against Editor/Data/Managed/UnityEngine — the editor's own assemblies — rather than
  # through Unity's build pipeline and its default response file.
  #
  # Listed one per line with the reason, and REPORTED when hit. A check that quietly
  # swallowed diagnostics would give a green that covers less than it appears to, which is
  # the failure this whole script exists to prevent.
  #
  #   CS0619 GetInstanceID  Marked obsolete-as-error in the editor's own assemblies;
  #                         Unity's real build of this project reports nothing. Verified by
  #                         grepping the batchmode logs for CS0619: zero hits.
  IGNORE='CS0619.*GetInstanceID'

  grep "error CS" "$OUT/$label.txt" | sort -u > "$OUT/$label.errors" || true
  grep -E "$IGNORE" "$OUT/$label.errors" > "$OUT/$label.ignored" || true
  grep -vE "$IGNORE" "$OUT/$label.errors" > "$OUT/$label.real" || true

  errors=$(grep -c . "$OUT/$label.real" || true)
  ignored=$(grep -c . "$OUT/$label.ignored" || true)
  warnings=$(grep -c "warning CS" "$OUT/$label.txt" || true)

  echo "$label: $errors error(s), $warnings warning(s), $ignored known-divergent ignored"

  if [ "$ignored" -gt 0 ]; then
    echo "  ignored (harness artefact, not a real failure):"
    sed 's|^|    |' "$OUT/$label.ignored"
  fi

  if [ "$errors" -gt 0 ]; then
    echo "  errors:"
    sed 's|^|    |' "$OUT/$label.real"
    status=1
  fi
}

check_assembly "Runtime" "$PKG/Runtime" Unity.Addressables Unity.ResourceManager Unity.TextMeshPro UnityEngine.UI

echo ""
if [ "$status" -eq 0 ]; then
  echo "OK — the package's API usage compiles against this editor."
else
  echo "FAILED — the package uses API that does not exist on this editor."
  echo "Do not publish. See the errors above."
fi

exit "$status"
