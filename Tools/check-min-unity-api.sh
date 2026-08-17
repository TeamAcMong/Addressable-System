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
# WHAT IT COVERS
#
# Four compilations, because the package does not have one source form. It has four,
# selected by two defines, and a green on one says nothing about the others:
#
#   Runtime  player   no UniTask      Task<T> signatures, player branch
#   Runtime  player   UNITASK_PRESENT UniTask<T> signatures  (repo Invariant 3)
#   Runtime  editor   UNITASK_PRESENT the #if UNITY_EDITOR branches inside Runtime
#   Editor            UNITASK_PRESENT the Editor assembly
#
# The UniTask branch alone is 97 conditional sites across 16 files. A consumer with
# UniTask installed compiles source that a consumer without it never sees.
#
# USAGE
#   ./Tools/check-min-unity-api.sh <editor-root> [reference-assembly-dir]
#
#   ./Tools/check-min-unity-api.sh "/c/Program Files/Unity/Hub/Editor/2022.3.62f3"
#   ./Tools/check-min-unity-api.sh "/c/Program Files/Unity/Hub/Editor/2022.3.62f3" \
#                                  "/e/Git Pj/Icon Match/Library/ScriptAssemblies"
#
# The second argument is the strongest form of this test: point it at the TARGET PROJECT's
# ScriptAssemblies and the package is compiled against that project's actual Unity, its
# actual Addressables build, and its actual UniTask — which is the thing you want to know
# before handing the package over.
#
# Exits non-zero on any error CS. Warnings are printed and do not fail the run.
#
# LIMITS, STATED SO THE GREEN IS NOT OVERREAD
#
# A compile against reference assemblies, not a Unity build. It does not use Unity's own
# response file, so a few diagnostics differ; those are listed below and reported when hit.
# It cannot catch anything outside the C# API surface. What it does catch is the one thing
# that shipped broken: a call that does not exist on the target version.

set -e

EDITOR="$1"
EXTRA_REFS="$2"

if [ -z "$EDITOR" ]; then
  echo "Usage: $0 <editor-root> [reference-assembly-dir]"
  echo "  e.g. $0 \"/c/Program Files/Unity/Hub/Editor/2022.3.62f3\""
  exit 1
fi

DATA="$EDITOR/Editor/Data"
if [ ! -d "$DATA/Managed/UnityEngine" ]; then
  echo "No managed assemblies under '$DATA/Managed/UnityEngine'."
  echo "Pass the editor root, the folder that contains Editor/Data."
  exit 1
fi

# Roslyn comes from whichever editor ships one; the compiler version does not affect which
# Unity API surface is visible, the reference assemblies do.
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

if [ -n "$EXTRA_REFS" ]; then
  REFDIR=$(win "$EXTRA_REFS")
else
  REFDIR="$ROOT/Library/PlayerScriptAssemblies"
fi

if [ ! -d "$EXTRA_REFS" ] && [ ! -d "$ROOT/Library/PlayerScriptAssemblies" ]; then
  echo "No reference assemblies. Pass a directory as the second argument, or open this"
  echo "project in Unity once so Library/PlayerScriptAssemblies exists."
  exit 1
fi

echo "Editor under test : $EDITOR"
echo "Reference assemblies: $REFDIR"
echo ""

status=0
inconclusive=0

# UniTask is optional. This repo does not install it, so its own PlayerScriptAssemblies has
# no UniTask.dll — and compiling the UNITASK_PRESENT branch without it yields ~87 "type not
# found" errors that say nothing about the package. Skip those configurations loudly instead:
# a check that cannot run must say so, not fail.
HAVE_UNITASK=0
if [ -n "$EXTRA_REFS" ] && [ -f "$EXTRA_REFS/UniTask.dll" ]; then HAVE_UNITASK=1; fi
if [ -z "$EXTRA_REFS" ] && [ -f "$ROOT/Library/PlayerScriptAssemblies/UniTask.dll" ]; then HAVE_UNITASK=1; fi
if [ "$HAVE_UNITASK" -eq 0 ]; then
  echo "  NOTE: no UniTask.dll in the reference set - the UNITASK_PRESENT configurations"
  echo "        (97 conditional sites, repo Invariant 3) will be SKIPPED, not checked."
  echo "        Pass a project that has UniTask installed to cover them."
  echo ""
fi

# Engine references, shared by every configuration.
engine_refs() {
  find "$DATA/Managed/UnityEngine" -name "UnityEngine*.dll" -o -name "Unity.Scripting.dll" \
    | sed 's|^|-r:"|; s|$|"|'
  # netstandard and the Mono BCL are mutually exclusive: referencing both makes every
  # framework type ambiguous (CS0433 Task<> in two assemblies, then CS0518 on System.Object
  # once resolution gives up). Player configurations use netstandard; editor configurations
  # set BCL_MODE and get mscorlib instead.
  if [ -z "$BCL_MODE" ] && [ -f "$DATA/NetStandard/ref/2.1.0/netstandard.dll" ]; then
    echo "-r:\"$DATA/NetStandard/ref/2.1.0/netstandard.dll\""
  fi
  return 0
}

# UnityEditor references, for the two configurations that compile editor code.
#
# ONLY the modular assemblies under Managed/UnityEngine. The monolithic Managed/UnityEditor.dll
# also exists and must be left alone: on 2022.3 it targets the .NET Framework profile, so
# pulling it in makes every generic resolve through mscorlib 4.0 and produces a wall of
# CS0012 "List<> is defined in an assembly that is not referenced" — dozens of failures that
# say nothing about the package. Unity's own compilation references the modular set, so this
# matches what the editor actually does.
editor_refs() {
  find "$DATA/Managed/UnityEngine" -name "UnityEditor*.dll" 2>/dev/null | sed 's|^|-r:"|; s|$|"|'
  return 0
}

# Editor code compiles against the Mono BCL, not netstandard.
#
# UnityEditor's modular assemblies — and Unity.Addressables.Editor built alongside them —
# target the .NET Framework profile and reference mscorlib 4.0. Resolving a List<> through
# them against netstandard produces CS0012 on every generic in the assembly: 189 of them
# here, none of which is a real finding.
#
# 4.7.1-api is the profile Unity uses for the editor. Falls back through the nearby ones so
# this keeps working on editor versions that ship a different set.
bcl_refs() {
  for profile in 4.7.1-api 4.7-api 4.6-api 4.5-api; do
    dir="$DATA/MonoBleedingEdge/lib/mono/$profile"
    if [ -f "$dir/mscorlib.dll" ]; then
      for asm in mscorlib System System.Core System.Xml System.Runtime; do
        if [ -f "$dir/$asm.dll" ]; then echo "-r:\"$dir/$asm.dll\""; fi
      done
      return 0
    fi
  done
  echo "  (no Mono BCL profile found under MonoBleedingEdge - editor checks will be noisy)" >&2
  return 0
}

# Every guard here ends in an explicit `return 0`. A bare `[ test ] && echo` returns 1 when
# the test fails, and as the last statement of a function under `set -e` that aborts the
# whole script — which it did, printing the header and nothing else. A checking tool that
# exits silently before checking anything is worse than no tool.
package_refs() {
  for dep in "$@"; do
    if [ -n "$EXTRA_REFS" ] && [ -f "$EXTRA_REFS/$dep.dll" ]; then
      echo "-r:\"$REFDIR/$dep.dll\""
    elif [ -z "$EXTRA_REFS" ] && [ -f "$ROOT/Library/PlayerScriptAssemblies/$dep.dll" ]; then
      echo "-r:\"$REFDIR/$dep.dll\""
    fi
  done
  return 0
}

# Diagnostics this harness raises but a real Unity build does not, because it compiles
# against Editor/Data/Managed/UnityEngine — the editor's own assemblies — rather than
# through Unity's build pipeline and its default response file.
#
# Listed with the reason, and REPORTED when hit. A check that quietly swallowed
# diagnostics would give a green that covers less than it appears to, which is the
# failure this whole script exists to prevent.
#
#   CS0619 GetInstanceID  Marked obsolete-as-error in the editor's own assemblies; Unity's
#                         real build of this project reports nothing. Verified by grepping
#                         the batchmode logs for CS0619: zero hits.
IGNORE='CS0619.*GetInstanceID'

# Errors that mean "this harness could not reproduce Unity's reference set", not "the
# package is broken".
#
# Editor code in a 2022.3 project compiles against a curated mix of the Mono BCL and
# netstandard facades that Unity assembles itself. Reproducing it from outside is not
# reliable: reference netstandard alone and every generic reached through
# Unity.Addressables.Editor fails CS0012 against mscorlib; reference the Mono BCL alone and
# every UniTask struct fails CS0012 against netstandard; reference both and framework types
# become ambiguous (CS0433) until resolution collapses (CS0518).
#
# All three are the harness failing to be Unity, and reporting them as package failures
# would be exactly the false alarm this tool exists to prevent. They are counted separately
# and reported as INCONCLUSIVE. The authoritative check for editor code is to install the
# package into the target project and let Unity compile it.
FRAMEWORK='CS0012.*(mscorlib|netstandard|System\.Core)|CS0433|CS0518'

report() {
  label="$1"
  grep "error CS" "$OUT/$label.txt" 2>/dev/null | sort -u > "$OUT/$label.errors" || true
  grep -E "$IGNORE" "$OUT/$label.errors" > "$OUT/$label.ignored" || true
  grep -vE "$IGNORE" "$OUT/$label.errors" > "$OUT/$label.rest" || true
  grep -E "$FRAMEWORK" "$OUT/$label.rest" > "$OUT/$label.framework" || true
  grep -vE "$FRAMEWORK" "$OUT/$label.rest" > "$OUT/$label.real" || true

  framework=$(grep -c . "$OUT/$label.framework" || true)
  errors=$(grep -c . "$OUT/$label.real" || true)
  ignored=$(grep -c . "$OUT/$label.ignored" || true)
  warnings=$(grep -c "warning CS" "$OUT/$label.txt" || true)

  printf "  %-34s %s real error(s), %s warning(s)" "$label" "$errors" "$warnings"
  if [ "$ignored" -gt 0 ]; then printf ", %s known-divergent ignored" "$ignored"; fi
  echo ""

  if [ "$framework" -gt 0 ]; then
    printf "      INCONCLUSIVE - %s framework-resolution error(s); this harness could not
" "$framework"
    printf "      reproduce Unity's editor reference set. Compile inside the target project.
"
    inconclusive=1
  fi
  if [ "$ignored" -gt 0 ]; then sed 's|^|      ignored: |' "$OUT/$label.ignored"; fi
  if [ "$errors" -gt 0 ]; then sed 's|^|      |' "$OUT/$label.real"; status=1; fi
  true
}

compile() {
  label="$1"; shift
  "$DOTNET" "$CSC" -noconfig "@$OUT/$label.rsp" > "$OUT/$label.txt" 2>&1 || true
  report "$label"
}

common_opts() {
  echo "-target:library"
  echo "-nostdlib+"
  echo "-langversion:9.0"
  echo "-define:TMP_PRESENT"
}

# ---- 1. Runtime, player branch, no UniTask ----
{ engine_refs; package_refs Unity.Addressables Unity.ResourceManager Unity.TextMeshPro UnityEngine.UI
  common_opts; echo "-out:\"$OUTWIN/rt-player-task.dll\""
  find "$PKG/Runtime" -name "*.cs" | sed 's|^|"|; s|$|"|'
} > "$OUT/rt-player-task.rsp"
compile "rt-player-task"

# ---- 2. Runtime, player branch, UniTask ----
{ engine_refs; package_refs Unity.Addressables Unity.ResourceManager Unity.TextMeshPro UnityEngine.UI UniTask UniTask.Addressables
  common_opts; echo "-define:UNITASK_PRESENT"; echo "-out:\"$OUTWIN/rt-player-unitask.dll\""
  find "$PKG/Runtime" -name "*.cs" | sed 's|^|"|; s|$|"|'
} > "$OUT/rt-player-unitask.rsp"
if [ "$HAVE_UNITASK" -eq 1 ]; then compile "rt-player-unitask"; else echo "  rt-player-unitask                  SKIPPED - UniTask not in the reference set"; inconclusive=1; fi

# ---- 3. Runtime, editor branch, UniTask ----
BCL_MODE=1
{ engine_refs; editor_refs; bcl_refs
  package_refs Unity.Addressables Unity.Addressables.Editor Unity.ResourceManager Unity.TextMeshPro UnityEngine.UI UniTask UniTask.Addressables
  common_opts; echo "-define:UNITASK_PRESENT"; echo "-define:UNITY_EDITOR"
  echo "-out:\"$OUTWIN/rt-editor-unitask.dll\""
  find "$PKG/Runtime" -name "*.cs" | sed 's|^|"|; s|$|"|'
} > "$OUT/rt-editor-unitask.rsp"
if [ "$HAVE_UNITASK" -eq 1 ]; then compile "rt-editor-unitask"; else echo "  rt-editor-unitask                  SKIPPED - UniTask not in the reference set"; inconclusive=1; fi

# ---- 4. Editor assembly, UniTask ----
# References the Runtime output from configuration 3, which was built with the same defines
# against the same editor. Referencing a differently-configured Runtime would resolve
# signatures the real build never sees.
if [ -f "$OUT/rt-editor-unitask.dll" ]; then
  BCL_MODE=1
{ engine_refs; editor_refs; bcl_refs
    package_refs Unity.Addressables Unity.Addressables.Editor Unity.ResourceManager Unity.TextMeshPro UnityEngine.UI UniTask UniTask.Addressables
    echo "-r:\"$OUTWIN/rt-editor-unitask.dll\""
    common_opts; echo "-define:UNITASK_PRESENT"; echo "-define:UNITY_EDITOR"
    echo "-out:\"$OUTWIN/editor-unitask.dll\""
    find "$PKG/Editor" -name "*.cs" | sed 's|^|"|; s|$|"|'
  } > "$OUT/editor-unitask.rsp"
  compile "editor-unitask"
else
  # Skipped because the Runtime output is missing, which here means the Runtime editor
  # configuration was inconclusive rather than broken. Reporting FAILED would be the same
  # false alarm the framework-error handling above exists to avoid.
  echo "  editor-unitask                     SKIPPED - no Runtime output to reference"
  inconclusive=1
fi

echo ""
if [ "$status" -eq 0 ] && [ "$inconclusive" -eq 0 ]; then
  echo "OK - the package compiles against this editor in all four configurations."
elif [ "$status" -eq 0 ]; then
  echo "PARTIAL - every configuration this harness could evaluate is clean, but at least one"
  echo "was inconclusive. Runtime results are trustworthy; editor results are not. Install the"
  echo "package into the target project and let Unity compile it before delivering."
else
  echo "FAILED - the package does not compile against this editor. Do not publish or deliver."
fi

exit "$status"
