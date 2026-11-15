#!/bin/sh
# PERF-F: run one view on the exported main build, then on this tree's build, one owned Rhino.Inside process at a time.
# Usage: sh tools/perf-f/pair.sh <label> <model.3dm> <view> [timeout seconds]
# Needs artifacts/perf-f/main-src (git archive of main + this harness, built with -p:PerfFBaseline=true).
set -u
label="$1"; model="$2"; view="$3"; limit="${4:-1500}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
resolver="C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll"
out="$root/artifacts/perf-f/final/$label"; mkdir -p "$out/main" "$out/new"
# SKIP_MAIN=1 reuses an earlier main run (main does not change between candidate builds).
if [ "${SKIP_MAIN:-}" != "1" ] || [ ! -f "$out/main.log" ]; then
PERFF_TIMEOUT="$limit" dotnet "$root/artifacts/perf-f/main-src/tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll" "$resolver" "$model" "$out/main" "$view" main > "$out/main.log" 2>&1
fi
grep -E "^---|open " "$out/main.log"
PERFF_TIMEOUT="$limit" dotnet "$root/tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll" "$resolver" "$model" "$out/new" "$view" cold warm serial > "$out/new.log" 2>&1
echo "new exit $?"; grep -E "^---|open " "$out/new.log"
