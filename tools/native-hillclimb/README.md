# Native-only performance research and hidden-flag reproducer

Current result: **no accepted speedup**. The owner supplied completed S3 evidence
and requested a modest wrap-up; the large sweep was stopped, and threading/tiling
were dropped. `batch.py` is retired and cannot start the old candidate sweep.
The product engine, defaults and native settings remain unchanged.

Start with [the results](../../HILLCLIMB.md) and
[the standalone negative regression](../../hillclimb/repros/README.md).

```powershell
dotnet build tools/native-hillclimb/NativeHillclimb.csproj -c Release
& 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe' -B tools/native-hillclimb/replay.py
```

The small replay requires only the three included Breps and their JSON camera
settings. It bypasses SceneCapture, blocks, section logic and uncertainty handling,
and calls raw HLD twice with only IncludeHiddenCurves changed. It verifies the
input SHA-256 and count, writes both Visible outputs, and records the mismatch.
A failed equivalence gate is the expected negative-regression result.

The project compiles current Rhino-side source into its isolated executable so
research can call the existing per-view method without a new public product API.
Core remains Rhino-free. RhinoCommon runtime binaries are not distributed; the
existing compatible resolver and licensed Rhino installation are required.

`run.py` preserves the original full benchmark manifest and historical laboratory
modes for explicit research reproduction. **Do not restart those modes as part of
this completed task.** Existing artifacts record eight completed architectural
S/M/L/XL block/exploded model workers; the curved suite and fixtures were stopped
per the later scope change. Every unmeasured row remains in the full denominator;
no full-set cold/warm score or accepted speedup is available. Full scores cannot
be derived from the completed subset.

`HiddenAudit.cs` reads saved exact native curves and classifies class/source/locus
and multiplicity differences. `HiddenProbe.cs` reduces the missing Visible span
and performs diagnostic tolerance/ray checks. `HiddenReplay.cs` independently
replays the resulting three-object fixture. Scorer self-checks cover split and
reversed lines, circles, missing/extra spans, visibility/profile changes and
spatially different duplicate coverage with equal total length.

`supervise.py` creates only owned workers, suspended, assigns a Windows Job Object
before resume, and never attaches to another Rhino process. Kill-on-close releases
only that worker tree. Its aggregate allowance reserves 128 MiB for the monitored
supervisor; sampled private memory includes both. Research benchmark peaks include
validation overhead and are not a production six-view GH/IPC memory measurement.
All original model bytes remain untouched. No install, commit or push was made.
