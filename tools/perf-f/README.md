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

- `cull-off`, `cull-on` — toggle the production per-view source filter. Their reported hashes use
  `.drawing.json`: exact Rhino archives of every piece/depth curve and region/hatch, with source
  paths, style and provenance, plus Frame/Error/quarantine. Info, timings and work counters are
  excluded because they intentionally differ. No coordinate rounding or segmentation normalization.
  Prefix with `native+` to exercise native mode.

```powershell
python -B tools/perf-f/cull-audit.py --resolver $r --out "$env:USERPROFILE/.t3/m2d-plus-models/evidence/source-cull/correctness"
python -B tools/perf-f/cull-audit.py --resolver $r --timings --out "$env:USERPROFILE/.t3/m2d-plus-models/evidence/source-cull/timings"
```

The controller runs one headless process at a time, batching a model's correctness views with
the harness's `view1;view2;...` syntax. Correctness covers the thirteen planar
controls, curved S blocks/exploded SW, space-frame SW, owner pipes Top/SW/nodebox, and four
`region_*` fixtures with both an unbounded section and a cropped depth box. Timing records one
`cull-off-warmup` row followed by ABBA (off/on/on/off); exclude the warmup from timing summaries.
It holds `SerialRhino(wait_seconds=None)` throughout both timing views.
The default timing model is `buildings/perf-M-blocks.3dm`; `--timing-model` overrides it.
`tenthbox` is centred on the model, with one tenth of its Top bounding rectangle's area and
depth encompassing the whole scene; `Top` has no crop. Timings are direct solves after shared
capture/preparation, bypassing admission and the tolerance census. All archives stay outside git.

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

## Mesh-tier spike

`PERFF_MT=1` makes the `ce:` prototype take its own-face exemptions and per-triangle deviation bounds from
control-net certificates (`MeshTier.cs`) instead of Rhino ray tests; `mt-census` (in place of the view) reports face
kinds, bound against measured deviation and silhouette accuracy. `mt_run.py` runs one view under the shared Rhino
mutex. `da:<view>` solves a view with the product's dense native audit off and on, reports what moved to Unresolved and
its cost, and ray-checks the moved stretches (`quick`: one solve each; `PERFF_DA_WITNESSES=<json>` counts witness
points that lie on a moved stretch). `dp:<view>` does the same through one production worker (off, on, on again) and
reports the worker's peak memory against its cap; `check` compares its drawing with the in-process one byte for byte;
`PERFF_DP_CAP_MIB` overrides the cap. Switches and results: [hillclimb/mesh-tier/FINDINGS.md](../../hillclimb/mesh-tier/FINDINGS.md).

## Stage 1 check of the exact analytic tier

`av:<view>` checks the production analytic tier (`AnalyticVisibility`, Core `AnalyticSolids`). It solves the view
through the product with the tier off and on, back to back on one prepared scene, reports time, native calls and class
lengths, compares the two drawings as classed loci, and checks sampled points of the new drawing with direct ray/Brep
intersections on the original sources (in a section the ray ends at the cut plane and a solid crossing the plane is
capped there). Variants: `glass` (glass = clear), `samples:<n>` per group (default 300), `why` (list Unresolved pieces,
lines that disappeared, and what blocks a flagged sample). `bodies` lists every source with its recognised shape or the
refusal reason and, for Breps, each face's surface type.

```sh
dotnet $EXE $R <model.3dm> <out> av:SW
dotnet $EXE $R <model.3dm> <out> "av:sidecar=Section transverse" why
python tools/perf-f/av_report.py <out>                        # markdown tables from every *-av.json in the folder
```

`av:SW exact-repeat` is a correctness-only determinism check: two analytic solves (cold and warm), with separate
`cold-exact-<count>.txt` and `warm-exact-<count>.txt` dumps, exact hashes in `exact-repeat.json`, and the recognised
dimensions and source fingerprints in `recognition.json`. It skips native comparison, ray sampling and timing.
For fresh-process coverage, invoke it sequentially about 30 times with a different output folder each time and compare
the complete dump SHA-256 hashes, including cold versus warm. No timing mutex is needed; keep one owned Rhino host
at a time. Recognition includes sphere centres/radii: their last bits can move another source's split parameters.

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

`dotnet $exe $r round-cover-bench <output folder>` generates 400 stacked round columns in memory, solves Top three
times and checks 200 different-seam coincident circle pairs. It writes `round-cover-bench.json`; hold the shared
`Local\M2DPlusLocalModelSuiteRhino` timing mutex for this benchmark. No model or document is saved.

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
