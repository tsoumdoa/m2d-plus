#!/bin/sh
# Usage: tools/hc-perf/run.sh <spec.json> <out-dir> [model-filter]
# Copies the built bench to a private folder so rebuilding during a run is safe, then runs serially.
set -e
here=$(cd "$(dirname "$0")" && pwd)
resolver=${M2D_RHINO_INSIDE:-C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll}
dotnet build "$here/M2DPlus.HcPerf.csproj" -c Release --nologo -v q
mkdir -p "$2"; rm -rf "$2/bin"; cp -r "$here/bin/Release/net8.0-windows" "$2/bin"
exec dotnet "$2/bin/M2DPlus.HcPerf.dll" "$resolver" "$1" "$2" $3
