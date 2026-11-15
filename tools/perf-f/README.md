# PERF F harness

A research harness, outside the solution. It starts one owned headless Rhino.Inside process per
run, never touches another Rhino process, and writes only under `artifacts/perf-f/` (git-ignored).
Findings: [hillclimb/perf-f/FINDINGS.md](../../hillclimb/perf-f/FINDINGS.md).

```powershell
dotnet build tools/perf-f -c Release
$r = 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
$exe = 'tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll'
dotnet $exe $r <model.3dm> <output folder> <view> <variant> [<variant> ...]
```

| Argument | Values |
|---|---|
| view | `long`, `trans`, `plan` (cuts through the scene bounds); `sidecar=<name>` (a cut plane from the model's JSON sidecar); `Top`, `Front`, `SW`, … (uncut); `curved:<view>` (raw native experiments, below) |
| variant | Any label. Each one is a full view solve on the same prepared scene, so the second is warm. `serial` forces one thread for per-source section work. |

Each run prints wall time and the SHA-256 of a dump holding every piece, region, loop and
diagnostic with round-trip coordinates. Two runs with the same hash produced the same drawing.
`compare.py <a.json> <b.json>` is an independent bidirectional locus and region comparison for
dumps that differ.

Special variants (this tree only, not the baseline build):

- `audit-verdicts` — for every body, compares the definition-local validation verdict with
  validating that instance's own world-space copy. `PERFF_AUDIT_THREADS=1` gives the serial
  reference.
- `stress-parallel` — runs each per-source geometry step serially for a reference, then alone and
  mixed on worker threads, and counts results that differ. `PERFF_STRESS_ROUNDS` sets the rounds.

Curved experiments (`curved:SW census full defonly defout kinds norm:hemi selfcheck@<definition>`)
time raw native HLD on subsets and exact re-parameterisations. `selfcheck@` explains why the
section validation rejects a definition.

## Comparing against main

The same sources build against a tree without the section-reuse internals:

```sh
mkdir -p artifacts/perf-f/main-src && git archive main | tar -x -C artifacts/perf-f/main-src
cp tools/perf-f/*.cs tools/perf-f/*.csproj artifacts/perf-f/main-src/tools/perf-f/   # create the folder first
# add [assembly: InternalsVisibleTo("M2DPlus.PerfF")] to that copy's src/M2DPlus.Rhino/AssemblyInfo.cs
(cd artifacts/perf-f/main-src && dotnet build tools/perf-f -c Release -p:PerfFBaseline=true)
sh tools/perf-f/pair.sh <label> <model.3dm> <view>     # main, then this tree: cold, warm, serial
```

`PERFF_TIMEOUT` (seconds, default 1500) bounds one view solve. The harness calls the view solver
directly, so it bypasses the in-process admission cap and the tolerance census.
