# Curved native HLD versus the real Make2D command

Investigation from `fix/units-latency`, branch `investigate/make2d-curved-hld`.
Rhino 8.36.26296.17001, Rhino.Inside .NET 8, parallel North (`Back`).
No installation, commits or pushes. No analytic curved-engine changes.

## Finding

The 99% stall is reproducible inside native HLD. On the 5,000-source all-curved
M fixture, serial HLD with hidden curves takes 263.337 s, reaching 99% at
102.274 s and spending another 161.063 s before returning. The real Make2D
command with hidden lines off takes 43.525 s. Changing only hidden generation
and threading in our raw HLD harness gives 43.117 s. With hidden lines on,
the real command takes 148.471 s, consistent with our threaded HLD measurements
of 141.335 / 144.469 / 152.793 s.

This isolates the dominant gap to **hidden-line generation plus serial native
execution**, rather than millimetre constants, a coarser Make2D tolerance,
capture, the independent audit, a scheduler lock, document ownership, or a
special faster Make2D engine. The native progress indicator does not represent
remaining wall time. We cannot identify the exact internal C++ subroutine behind
the long tail without native profiling symbols; this is not a claim to have
fixed McNeel's kernel.

Neither flag is changed in production. Hidden-off changes visible loci on M,
confirming the earlier finding on a curved scene. Threaded M runs have different
segment counts. Small curved-steel controls alone do not expose that instability.
The faster experimental settings cannot be called byte-equivalent.

## Measuring the actual command

`RhinoDoc.OpenHeadless` plus a real parallel viewport succeeds, but
`RhinoApp.RunScript(doc.RuntimeSerialNumber, script, true)` immediately returns
false, with empty command history and no output. This host cannot execute the
document command on that headless document. This is a failed attempt, not a
zero-second timing. McNeel documents the distinction between
[NoWindow and Hidden hosts](https://developer.rhino3d.com/api/rhinocommon/type/rhino.runtime.inprocess.windowstyle/)
and discusses the [normal-document requirement for RunScript](https://discourse.mcneel.com/t/rhinoapp-runscript-return-false-with-rhino-inside/168589).

The successful measurements use a separate, owned Rhino.Inside `WindowStyle.Hidden`
host, a normal document created with `RhinoDoc.Create(null)`, imported model
settings/geometry, and a parallel viewport configured with the same North frame
as M2D+. This runs the **real installed command**, not an HLD proxy:

```text
_-Make2D _Layout=_View _Properties=_ByOutputLayers
_CreateHiddenLines=_No _ShowTangents=_No _CreateSceneSilhouette=_No
_CreateClippingPlaneIntersections=_No _ShowViewRectangle=_No
_GroupOutput=_No _Enter
```

The hidden-on control changes only `_CreateHiddenLines=_Yes`. Command wall times
include creation of Rhino output objects; history also records Make2D's own
elapsed time. Inputs are selected drawable top-level objects, including blocks.
The models are millimetres with 0.01 mm absolute tolerance. The document has no
participating clipping planes. Scripts, full command history, model identity,
layer counts and exact output curves are archived. The hidden host exits after
flushing evidence because its ordinary shutdown invokes Rhino's toolbar-updater
modal dialog. It never attaches to or alters the owner's Rhino process.

Two failed pilots used old option names or left the `LayerName` string prompt
open. They are excluded. Rhino 8's current options are described in the
[Make2D help](https://docs.mcneel.com/rhino/8/help/en-us/commands/make2d.htm).

## What installed Make2D actually passes

Inspection of the installed managed wrapper and command establishes:

- Absolute tolerance is `doc.ModelAbsoluteTolerance`; no coarser drawing tolerance.
- Hidden curves follow `CreateHiddenLines`; tangent edges follow `ShowTangents`.
  Tangent seams retain the parameter default but are irrelevant with tangents off.
- `Flatten` retains its default false. `Compute` receives `multipleThreads: true`.
- It supplies original geometry with instance transforms, per-object clipping
  participation and occluding-section flags, then computes all objects together.
- For parallel View projection, it moves the camera to scene center plus one
  projected depth, and sets x/y half-extents to 0.55 times the projected size,
  near/far to 0.45/1.65 times its depth. This differs from our conservative camera.
- It calls `RejoinCompatibleVisible` after HLD and removes very short segments
  when placing output. Therefore equal loci need not have equal piece counts.
- In this installed RhinoCommon wrapper, setting public
  `OccludingSectionOption` alone is not consumed by `Compute`; actual section
  participation is carried by individual `AddGeometryAndPlanes` registrations.

These observations describe this installed build, not a permanent API promise.
No decompiled implementation is included in this MIT repository.

Installed assembly SHA-256:

```text
Commands.rhp     F02A2B905F9B7F8074BC312DA40FD15833004534113265CC3511BBE774FF7527
RhinoCommon.dll  5FD146708CD1C357760557FB073933E4D873BABB56FCEE57580C1BF1F8D8FA8B
```

## A/B measurements and output checks

The full generated tables are `measurements.md` under the evidence root below.
Raw HLD rows time `HiddenLineDrawing.Compute` only; command rows time the actual
command. Capture, preparation, geometry conversion, registration and extraction
are separate fields. Most rows are n=1; three threading and serial steel controls
check repeatability. The shared workstation, caches and other agents' work are
uncontrolled. At most two of this investigation's Rhino hosts run concurrently.

| Experiment | Curved steel, s | Curved M, s | Output versus our baseline |
|---|---:|---:|---|
| Real Make2D, hidden off | 0.679 | 43.525 | Visible loci change; command rejoins pieces |
| Real Make2D, hidden on | 1.109 | 148.471 | Steel combined samples agree; M combined loci differ at about 0.22% of samples |
| Our raw HLD, hidden on, serial | 0.450; repeats 0.664 / 0.599 | 263.337 | Reference |
| Only hidden off | 0.352 | 118.094 | Steel adds visible locus; M both loses and adds visible loci |
| Only multithreading | 0.460 / 0.452 / 0.434 | 141.335 / 144.469 / 152.793 | Steel samples agree; M loci differ between runs |
| Multithreading + hidden off | — | 43.117 | M visible/hidden loci change |
| Full command-like combination | 0.369 | 40.758 | Visible/hidden loci change |
| Tangent edges on | 0.420 | — | Adds visible/hidden loci, as requested by the flag |
| Tangent seams on, edges off | 0.531 | — | Steel samples agree; seams flag has no effect with edges off |
| Tangent edges and seams on | 0.554 | — | Same added sampled loci as tangent edges on |
| Absolute tolerance 0.1 mm instead of 0.01 mm | 0.551 | 285.212 | Both scenes lose/add visible and hidden loci |
| Flatten on | 0.543 | 293.905 | Steel samples agree; M exported 2D curve payload is identical; loses native depth |
| Public OccludingSectionOption on | 0.601 | — | Steel samples agree, no participating clip planes |
| Per-object occluding sections on | 0.593 | — | Steel samples agree, no participating clip planes |
| Non-intersecting clipping plane | 0.580 | — | Steel samples agree |
| Clipping plane through scene | 0.580 | — | Intentionally different drawing |
| Viewport extents ×10 | 0.507 | — | Steel samples agree |
| Command camera only | 0.583 | — | Steel samples agree |
| Command camera and frustum | 0.523 | 303.314 | Steel samples agree; M visible/hidden loci change |
| Document Breps + original transforms | 0.559 | 304.687 | Steel samples agree; M visible/hidden loci change |
| Another owned Brep duplicate | 0.579 | — | Steel samples agree |
| Tolerance-matched meshes | 67.789 | — | Large visible/hidden locus changes; meshing time is extra |
| Compound Breps, four sources each | 0.469 | — | Some visible/hidden loci change; loses source attribution |
| First four steel objects | 0.424 | — | Different input subset |
| First 500 / 1,000 / 2,500 M objects | — | 31.646 / 66.941 / 168.776 | Different input subsets, no equality claim |

The count probes do not show a sudden threshold near 5,000 objects. Fewer objects
mean less geometry and different occlusion; their timings do not justify splitting
a scene into independent HLD batches. Tangent/clip controls cannot explain this
stall because the slow reference already has tangents off and no clips. Flatten
does not improve M and would discard the depth geometry used elsewhere.

The comparator checks both directions, independently for Visible and Hidden,
using detached projected curves. It allows 0.01125 mm: 0.01 mm model tolerance
plus two 0.000625 mm polyline fitting allowances. Steel includes every tessellation
vertex and uniform arc samples; M uses arc samples at at most 20 mm spacing,
at least nine points and at most 2,001 points per curve. These are **sampled locus
observations, not whole-span certificates**. Native tessellation occasionally
fails on M; those curves use 33 parameter samples and are explicitly counted,
so the 0.000625 mm fitting allowance is not established for those exceptions.
Exact curve archives remain available. Passing samples do not prove equivalence.

On M, hidden-off loses 19,781 of 323,597 baseline-visible sample matches and adds
2,814 unmatched visible samples. Threading alone loses/adds 12,656/10,862 visible
and 3,826/3,982 hidden sample matches. Two adjacent threaded-run comparisons also
fail: 12,079/11,554 and 11,273/12,416 unmatched visible samples. Real Make2D
hidden-off loses/adds 25,553/6,871 visible sample matches versus our baseline;
hidden-on differs at 4,722/4,615 combined-locus samples (about 0.22%).

To rule out approximation artifacts in the failing comparisons, 64 mismatch
witnesses were rechecked against the exact detached NURBS curves with Rhino's
closest-point routine. All 64 confirm a class-locus difference beyond 0.01125 mm.
For example, baseline-visible `(2571.6407973453242, 9585.300067584223)` lies
exactly on a baseline curve but is 0.27477 mm from the nearest hidden-off visible
curve. Another baseline-visible witness has no threaded visible curve within
1 mm. These are numerical counterexamples, not independent exact certificates.
The witness index searches a 1 mm box; reported distance lower bounds are capped
at 1 mm, since curves outside that box are not individually queried.

Command View output is translated back using the known original viewport
lower-left corner, not a fitted registration or independent bounding-box alignment.
The first command archive omitted layer-table entries, so hidden-on command
comparisons use combined visible/hidden loci; their class split is available only
as aggregate counts from command history. Hidden-off command curves are all
visible. This limitation is explicit rather than guessing classes from order.

Steel covers every requested parameter individually. Larger M experiments
cover the hidden/threading 2×2, command camera/frustum, original geometry with
transforms, flatten, tenfold tolerance and object count. There is no managed
batch-add overload: calls register individual objects before one Compute. The
`batch-4` experiment appends disconnected Breps into compounds, changing source
attribution and some loci; it is not a safe equivalent batching optimization.
The mesh experiment deliberately changes geometry and is dramatically slower.

## Safe production change: stop an already-defeated ray cross

The independent audit tests a central ray and four tolerance-offset rays. A
central Unresolved result or one disagreeing offset irrevocably defeats the
required five-ray agreement. Remaining rays cannot change the decision, witness,
or whether this body makes the sample uncertain. They are now skipped. The four
offset vectors are also allocated once per verifier.

Crucially, skipped offsets **still consume their original logical budget units**.
This preserves exactly which later samples and bodies are audited, where the
4,096-ray budget stops, every veto, and every retained Unresolved/native-confidence
span. No savings are spent on extra coverage. Cancellation is checked before
the shortcut as well as before actual offset calls. Info distinguishes logical
ray units from actual native casts and retains the unsampled/uncertain warning.

Six alternating legacy/optimized passes restore the identical native drawing
before each audit. Stable worker serialization excludes timings and diagnostic
prose, while including geometry, classes, provenance and quality:

| Audit fixture | Legacy median | Optimized median | Actual native casts | Same complete drawing bytes |
|---|---:|---:|---:|---|
| mm curved steel | 1.488 s | 1.133 s (23.8% less) | 4,095 → 3,241 | 12/12 |
| freeform façade (original metre fixture) | 0.305 s | 0.280 s (8.4% less) | 4,095 → 2,524 | 12/12 |

Medians exclude the first pass of each implementation. Both retain 4,095/4,096
logical ray units, identical sample/skipped counts, and identical contradictions.
The façade still finds 20 wrong-visible and nine false-hidden samples, downgrading
the same six neighborhoods to Unresolved. Steel still has 1,566 samples and 893
skips; façade has 4,933 samples and 4,364 skips. Neither audit proves unsampled
interiors. Shrinking budgets or accepting unaudited spans as certified was rejected.

The primary records are `audit-steel-fresh` and `audit-facade-fresh`. Their initial
native solve has zero audit coverage, ensuring each measured pass sees fresh
native classes. Earlier `audit-steel`/`audit-facade` records inadvertently replayed
already-audited input (NativeOnly does not disable verification); they also match
byte for byte, but their 25.5%/13.4% speed reductions are supplementary observations,
not the primary fresh-result measurements above.

The audit is still more expensive than steel HLD. This change does not meet the
single-view whole-pipeline Make2D target. The sixfold M native improvement is an
experimental, output-altering option combination, not the shipped fix.

## Reproduction and evidence

Stable root: `~/.t3/m2d-plus-models/evidence/native-hld/`.
Inputs: `~/.t3/m2d-plus-models/mm/curved-perf-M-blocks.3dm` and `curved-steel.3dm`,
generated in the preceding unit investigation with adjacent generation manifests.
The source generator is `tools/models/curved.py`; M includes freeform glass,
curved sweeps and spline rails as well as spheres/cylinders. Fable's analytic
recognition does not remove all this native workload.

```powershell
dotnet build tools/worker-bench/M2DPlus.WorkerBench.csproj -c Release -o artifacts/hld-harness
$resolver = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll'
# Run one command host at a time. Choose a new output directory.
./artifacts/hld-harness/M2DPlus.WorkerBench.exe hld $resolver $model $output command-owned
# Use HLD_COMMAND_SCRIPT to repeat with hidden lines on.
./artifacts/hld-harness/M2DPlus.WorkerBench.exe hld $resolver $model $output 'baseline,hidden-off,threads,threads-hidden-off'
# Export the already archived real command without rerunning Make2D.
./artifacts/hld-harness/M2DPlus.WorkerBench.exe hld $resolver $model $commandOutput export-command
# Compare identical drawings through old/new audits.
./artifacts/hld-harness/M2DPlus.WorkerBench.exe hld $resolver $model $auditOutput audit
```

Python comparison requires NumPy and Shapely 2 in an isolated environment:

```text
python tools/worker-bench/hld-report.py <evidence-root> --scene mm-steel
python tools/worker-bench/hld-report.py <evidence-root> --scene mm-curved-M --skip-vertices
```

Completed variants refuse overwrite. Progress snapshots are about once per
second. Exact detached `.3dm`, parameter metadata and projected locus JSON stay
outside Git. `fingerprint` fields and the evidence manifest identify the measured
engine. Failed pilots remain archived and are excluded from timing tables.

Production overlap is limited to `NativeVisibilityVerifier.cs`. No changes to
Fable's `AnalyticVisibility`/`ComposedVisibility` hook, sections, capture, component
status or settings reuse. All experimental HLD switches live in the benchmark.

## Verification

Debug and Release solution builds and the standalone benchmark Release build
pass with zero warnings/errors. The forced local pre-commit hook passes.
`tools/Test-WalkingSkeleton.ps1` runs in process-cap-compatible groups on both
.NET 8 and .NET 10: 343 Core, 62 UI-model and 237 distinct licensed Rhino tests
pass per runtime (230 non-worker plus seven worker tests). The new tests cover
central uncertainty, disagreeing offsets, witnesses, original budget exhaustion,
complete drawing byte equality, freeform and spherical bodies, and unchanged
unverified-interior warnings.

Six existing tests were excluded because a Rhino test host plus two workers
would exceed the owner's two-process limit: persistent parallel equality,
crash sibling recovery, startup-timeout sibling recovery, isolated memory retry,
no-headroom sibling preservation, and worker presentation equality. This is not
an unfiltered full-suite claim. Logs are `artifacts-hld-*.log` in the worktree;
raw models/results remain outside Git. No owner/other-agent process was modified.
