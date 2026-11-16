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

## Curved spike

`spike:<view>` runs the curved spike ([findings](../../hillclimb/curved-spike/FINDINGS.md)); nothing in the
product is changed. It first solves the view through the product with observers, then:

- `census` counts curved sources that are provably hidden (behind planar faces; behind a nearer
  instance of the same solid) and times raw native HLD with and without each set;
- `cull` solves the view with each set removed, back to back with the full view, and writes 2D dumps
  for `compare.py`;
- `mesh:<k>` sends curved Breps through the exact kernel as meshes with chord tolerance k x model
  tolerance; `meshparams:<k>` measures which Rhino meshing settings hold that tolerance.

`spike_compare.py` prints locus and class agreement at several budgets. `PERFF_PROOF_SEGMENTS` and
`PERFF_PROOF_QUERIES` raise the proof limits; `PERFF_NO_KINK=1` turns kink splitting off.

## Curved engine prototype

`ce:<view>` runs the curved-engine prototype ([findings](../../hillclimb/curved-engine/FINDINGS.md)); nothing in the
product is changed. `ce-census` (in place of the view) classifies every source the product sends to native HLD, with no
solve.

```sh
dotnet $EXE $R <model.3dm> <out> ce-census x                  # what the curved sources are made of
dotnet $EXE $R <model.3dm> <out> ce:SW eps:30 eps:10          # prototype at 30 x and 10 x model tolerance
dotnet $EXE $R <model.3dm> <out> ce:Front eps:10 t1off        # mesh tier only (no exact analytic solids)
python tools/perf-f/ce_report.py <out folder>                 # markdown tables from every *-ce.json below it
```

It solves the view through the product for the native reference (shipped default, then raw native with the ray audit
off), runs the prototype on exactly the inputs the product sent to native, compares classed loci both ways and
adjudicates differences with direct ray/Brep intersections. `CurvedCore.cs` has no Rhino types.

| Switch | Meaning |
|---|---|
| `eps:<k>` | Chord tolerance of the mesh tier, k x model tolerance. Several may be given. |
| `t1off` | Disable the exact analytic solids (everything goes through meshes). |
| `adjudicate:<n>` | Samples checked per disagreement group (default 250). |
| `PERFF_CE_ONLY=<text>` | Keep only sources whose identity contains the text. |
| `PERFF_CE_CHAIN=strict\|loose` | Own-face exemption rule (default: strict for edges, loose for silhouettes). |
| `PERFF_CE_CAND=kink` | Split kinked faces before taking candidates. |
| `PERFF_CE_RHINO_SILHOUETTES=1` | Use Rhino's silhouettes for exact solids too, not the closed-form outlines. |
| `PERFF_CE_TRACE=0\|1` | Print why samples the ray check calls wrong were classified as they were (1 adds triangles). |

## Stage 1 check of the exact analytic tier

`av:<view>` checks the production analytic tier (`AnalyticVisibility`, Core `AnalyticSolids`). It solves the view
through the product with the tier off and on, back to back on one prepared scene, reports time, native calls and class
lengths, compares the two drawings as classed loci, and checks sampled points of the new drawing with direct ray/Brep
intersections on the original sources (in a section the ray ends at the cut plane and a solid crossing the plane is
capped there). Variants: `glass` (glass = clear), `samples:<n>` per group (default 300), `why` (list Unresolved pieces,
lines that disappeared, and what blocks a flagged sample).

```sh
dotnet $EXE $R <model.3dm> <out> av:SW
dotnet $EXE $R <model.3dm> <out> "av:sidecar=Section transverse" why
python tools/perf-f/av_report.py <out>                        # markdown tables from every *-av.json in the folder
```

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

Native verification measurements (one owned Rhino process at a time):

```powershell
python -B tools/perf-f/native-verifier.py --resolver C:/path/to/Rhino.Inside_v8_net8.0.dll
```

This runs curved-S SW/North and facade/steel/diagrid SW, with `audit-off`, `audit-on`
and `audit-on-repeat` on the same prepared scene. Each solve has a 120 s cancellation
budget; the owned process has a 400 s wall / 8 GiB cap. `measurements.json` binds
fixture/engine hashes, native runtime, counts, phase timings, work-budget coverage
and contradictions. Compare `native ray audit work` with `native solve`; paired
wall differences also include warm native state and machine contention. The work
cap is deterministic, not a promise of a particular wall-time percentage.

`trace-native` records the canonical native inputs, complete planar extraction
and detached native records for mixed gate witnesses. When `PERFF_RAY_POINTS`
names a JSON array of world XYZ points, it additionally checks every owned curved
body against five rays per point, using conservative scalar bounds and direct
Brep/mesh intersections. This is correctness evidence, not a timing variant.
