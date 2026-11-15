# PERF A: edge / planar face experiment

This is an experimental, opt-in benchmark, not a registered product route. No
Grasshopper contracts or native scheduler behavior change. Core uses only owned
plain values. `PlanarExtraction.OutputClass` explicitly maps unknown decisions to
`LineClass.Unresolved`. The safe mixed-view route is the existing **whole view
native solve**; curved, annotation, point and other unsupported sources are
excluded explicitly from the planar-subset benchmarks and listed in each row.
Do not use the resulting planar subset as a mixed-view drawing.

## Geometry and tie convention

Inputs are parallel view coordinates; larger Z is nearer. Each face has an outer
loop and any number of holes; even/odd loop containment is independent of winding.
Loops are closed implicitly. The first non-collinear triple defines the support
plane; every vertex must lie within model tolerance of it. A malformed/nonfinite
or detectably nonplanar occluder quarantines the entire view as Unresolved. The
adapter requires all Brep faces planar and all edges linear at tolerance. Mesh
quads are split into triangles; internal coplanar edges are suppressed. Naked,
crease and non-manifold mesh edges remain candidates. No curved silhouettes are
invented. Edges projecting exactly to a point produce no line.

A face hides a candidate only when its depth is **strictly greater than the edge
depth plus model tolerance**. Coplanar/self contacts do not hide. Polygon boundaries
are closed: an edge exactly aligned with a nearer polygon boundary is hidden by
that face. Edge-on faces have no projected area and do not occlude. Interval
endpoints carry no independent point primitive. No coordinate nudging is used.

The raw kernel result preserves per-candidate intervals. The authoritative drawing
loci are `VisibilityLoci.Create`: supporting lines are verified within 0.1 model
tolerance before union, coincident intervals are emitted once, Unresolved takes
precedence over Visible, then Hidden; visible loci subtract coincident hidden
loci. Source ownership is the ordinal-smallest source identity on that supporting
line. This convention deliberately chooses drawing-locus identity, not which
coincident physical edge a dimension should reference. It must be revisited before
production provenance or dimension association.
Hidden residues no longer than model tolerance at a visible junction or corner
inherit Visible, including corners between different support directions. Both
residue endpoints must lie within tolerance of the visible corner. Isolated short
features are preserved. This prevents a rounded transform from inventing hidden
point primitives at otherwise visible coincident junctions.

## Arithmetic and reference

The production narrow phase evaluates outward-rounded binary64 intervals. A sign
is accepted only when disjoint intervals certify it (or both are equal singletons).
Overflow, division by an interval containing zero, ambiguous comparisons and
insufficient endpoint accuracy transfer the **whole edge/face pair** to exact
`BigInteger` rational arithmetic of the original binary64 inputs. No epsilon
decides a predicate sign. The endpoint interval width must certify a geometric
error below a quarter of model tolerance. A BVH indexes projected face bounds;
candidate faces are ordered near-to-far. Only complete hidden coverage of an edge
permits the production edge early-out. Edges run with bounded `Parallel.For`.

`ExactVisibilityOracle` is an independent event-arrangement implementation. It
uses rational arithmetic exclusively, visits and counts **every edge × face**,
has no index or front-to-back order, and never stops at complete edge coverage.
Exact scalar disjoint-bound and depth-halfspace tests reject empty pairs. It
subdivides with a sorted exact event set and uses a winding-number implementation
separate from production parity. Both oracles and metamorphic gates compare
classed interval coverage in both directions within drawing tolerance, not piece
counts or total lengths. Reference edges can run independently on eight threads;
serial and parallel results and complete pair counts have a dedicated test. The
general rounded-translation stress diagnostic initially found contact sensitivity
and hidden point residues. Every view checks an exact signed-axis rigid rotation;
M North/SW additionally compare canonical drawing loci after a rounded rotation
plus translation. Those probes are required gates. The corrected corner rule
passes the preliminary M blocks North/SW probes; full results remain authoritative.

Tolerance must be positive. Coordinates whose ULP exceeds 1/64 of drawing
tolerance, XY magnitudes at or above 1e150, and tolerances below 1e-150 cause
whole-view Unresolved. Exact interval endpoints must also meet their geometric
rounding budget. Honor Unresolved before constructing canonical drawing loci.

## Run

```powershell
dotnet build tools/hc-perf -c Release
dotnet build tools/hc-perf/reader -c Release
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
& $py -B -u tools/hc-perf/run.py --iteration 0
# Kernel-only diagnostic; native gates remain explicitly failed:
& $py -B -u tools/hc-perf/run.py --iteration 0 --licence-free
# A2 correctness-only native cache, serial owned processes, no timing mutex:
dotnet build tools/hc-perf -c Release -o artifacts/hc-perf/slot-bin
& $py -B -u tools/hc-perf/native_gate.py --all
# Scored kernel candidate against immutable native correctness evidence:
& $py -B -u tools/hc-perf/run.py --iteration 5 --native-cache artifacts/hc-perf/native-a2
# Later candidates can reuse exact crop truth from a passed scored iteration:
& $py -B -u tools/hc-perf/run.py --iteration 6 --native-cache artifacts/hc-perf/native-a2 --oracle-cache artifacts/hc-perf/iteration-5
```

The runner creates a fixed 183-row manifest: S/M/L/XL × blocks/exploded ×
North/SW/Top; XS (correctness only); all root fixtures × Top/Front/SW; and every
orthogonal feature model × Top/Front/SW. All sources are read; exclusions are
recorded. No crop/section settings are applied to these uncut direction views.
Native M/L comparisons use exactly the eligible planar subset. Mixed native views
are reported separately. Four 20 m square tiles at quarter-grid centers per M
North/SW view and form get their own brute-force oracle; all overlapping faces
are retained, including those extending beyond the tile. Native and deduplicated
blocks/exploded comparisons use bidirectional sampled-locus coverage at model
tolerance, at least 8 samples per interval and at most 50 mm sample spacing.
Any unexplained disagreement or missing/capped gate fails the run.

A2 changes native execution only: correctness calls run at normal priority without
`Local\M2DPlusLocalModelSuiteRhino`, once per case, serially in owned Rhino.Inside
processes. Views of one model can share a process; HLD calls remain serial.
`native_gate.py` retains all 12 M/L and 153 supplemental native cases. The cache
includes the model hash, actual Rhino/runtime/priority, complete exclusion census,
raw native visible/hidden loci, all other native slot classifications and extracted
planar faces/candidates. An Unset slot is not an accepted exception merely because
of its name. Every lost sample needs evidence; unexplained samples still fail.

`--native-compare` applies the documented drawing-locus union to native output;
raw output remains intact. `--native-oracle` visits every relevant face/candidate
pair on all disputed supports, retaining the exact reference intervals. The
`audit_native.py` diagnostic records a witness for every failed sample. It refuses
native exceptions if this independent reference check fails. Duplicate exceptions
require a native Duplicate slot on the same source, XY locus and depth as a
frontmost reference-visible candidate. They are not inferred from aggregate length.

Native correctness wall duration in the owned-job watch is a guard observation,
never a native benchmark. A2 timing references come from the owner-specified
archived baseline; those rows contain curved geometry and are labelled mixed-native
context. Uncut Top and XL North/SW are absent, so no matching ratio can be reported
for them. Cut/section rows are never substituted for an uncut direction view.

The exact crop-reference cache reuses expected intervals only. Every candidate
still recomputes all 16 cropped kernel results and checks raw intervals and both
directions of canonical locus coverage. Cache use requires a passed full source
iteration, SHA-256 checks of all 16 reference payloads, identical owned face/edge/
tolerance input hashes, and an oracle fingerprint covering the independent oracle,
arithmetic and model source. Every payload records the full drawable-edge x face
pair count; exactly point-projecting candidates produce no curve and are skipped
by the reference. Changed inputs or oracle code recompute rational truth. Fresh,
reuse, changed-algorithm and changed-input probes all pass under
`artifacts/hc-perf/oracle-cache-probe/`. Timings are always freshly measured; no
benchmark rows are reused. Worker guard failures fail both tile and rigid gates,
even if a stale output file exists.

After a complete candidate, run `python -B tools/hc-perf/decide.py N` before
changing its source. It snapshots all five Core visibility sources, records the
gate and primary decision, and updates the single JSONL record and hill-climb row.
It does not restore rejected source: copy the previous winner's five `.cs` files
from `artifacts/hc-perf/snapshots/iteration-N/`, then rebuild the reader before the
next trial. A lower perf-only score cannot override a worse full primary.
Use `dotnet build tools/hc-perf/reader/M2DPlus.HcPerf.Reader.csproj -c Release
--no-restore -t:Rebuild` to reproduce its measured DLL after a solution build;
the final A2 rebuild matches the winning engine byte-for-byte.
After three consecutive non-improvements or eight candidates, restore the winner
and run `python -B tools/hc-perf/report.py`. This checks the snapshot, model and
engine hashes and complete gate inventory, then writes `final-report.json` and
the compact tier comparison under `artifacts/hc-perf/`. All raw local evidence
must remain available for review; no commit, push or native timing is performed.

The primary is geometric-mean extraction + BVH + solve + locus-union wall time
over nonempty planar subsets of all primary rows. Read time is reported separately.
Runs below 10 s have three back-to-back measurements in the same owned process;
longer runs have one. Processes are serial, contained in their own Windows job,
with 16 GiB and 30 minute default guards. Kernel thread count is eight. Managed
and private memory are sampled at 100 ms; job-private peak is sampled by the
supervisor at 200 ms. These are observed peaks, not exact allocation maxima.
Manifests, model/engine hashes, native build/runtime, all per-view intervals and
failed native segments live in `artifacts/hc-perf/iteration-N/`. Only completed
full runs append to `hillclimb/scores.jsonl`; diagnostic `--select` runs never do.

`fable_probe.py` instruments a throwaway artifact copy of the external prototype,
never product code. It exports prototype loci and candidates and aligns archived
native SVG loci for diagnosis. Archived native lengths are **not** a valid native
ratio: `tools/models/native.py` uses `Flatten=false` and measures retained 3D
depth with `GetLength()` while the SVG and prototype use projected XY lengths.
For axis-aligned edges in SW, XY/3D = sqrt(2/3) = 0.81649658. The remaining
prototype differences still require locus analysis; the length-metric error does
not establish prototype correctness.
