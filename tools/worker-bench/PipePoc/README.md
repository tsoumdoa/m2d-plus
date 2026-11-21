# Pipe-node hidden-line drawing experiment

Can an orthographic drawing classify visibility on the original pipe surfaces quickly enough to beat threaded Rhino HLD? This standalone command extracts one promising approach for review. It runs in a headless Rhino process on Windows.

This experiment shipped as **Pipes (experimental)** in beta.12 (PR #69).
The classifier now lives in [PipeVisibility](../../../src/M2DPlus.Rhino/Experimental/PipeVisibility/README.md);
`pipe-poc` calls those same classes. Rhino HLD remains the default.

## Try it

Use .NET 8 x64, a licensed Rhino 8 installation, and a compatible Rhino.Inside resolver. From the repository root, point the command at the resolver and your own millimetre model. A simple input is two intersecting closed cylinders saved as Breps in Rhino. The research fixture contains five closed pipes, five setting-out curves and one point.

```powershell
dotnet build tools/worker-bench -c Release
$env:M2D_RHINO_TESTS = '1'
$resolver = 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
$inputModel = 'C:/models/pipes.3dm'
$drawing = 'C:/models/pipes-sw-drawing.3dm'
dotnet tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll pipe-poc $resolver $inputModel $drawing SW
```

Choose a fresh output filename and either `Top` or `SW`. Open the exported file in Rhino's Top viewport. The drawing lies in World XY. Toggle the Hidden layer to inspect the visible edges. The export includes visible and hidden curves. Loose points appear only when visible. Object names carry the visibility class and source object GUID. The reference image uses dashed strokes for the Hidden layer.

## How it works

1. Collect sharp Brep edges, apparent contours and loose curves. Split their parameter domains at projected boundary crossings and at depth crossings with other bodies.
2. Classify the intervals by shooting orthographic rays. Plane and cylinder equations handle the common faces. Translated quadratic profiles cover several saddle faces. Rhino trimmed-face intersections handle the remaining surface cases.
3. Refine short edges near contact, trim the original curves, and project them. Independent curves are classified on up to eight threads. On AVX2-capable machines four face bounds and cylinder exit depths can be tested per batch. Other machines use scalar arithmetic.

## Evidence and limits

On the five-pipe fixture, the research harness measured first-drawing medians of **198 ms custom versus 425 ms native in Top**, and **148 ms versus 452 ms in SW**. Each figure covers six fresh Rhino 8.36 hosts on a Ryzen AI Max+ 395 with a ReadyToRun build. Timing covered the complete drawing calculation and excluded host startup, model loading and disk writes. One of the six Top pairs missed the 2× target. The source model and benchmark archives live outside the repository.

A separate smoke test ran this extracted command on the same fixture. SW produced 94 visible and 153 hidden curves. Top produced 112 visible and 132 hidden curves. Both matched the audited drawing's source IDs and classes. At 33 stations per curve the maximum positional difference after aligning the drawing planes was below 5 × 10⁻¹¹ mm on both views. The timing medians above belong to the research harness.

The demo covers opaque, closed, valid Breps with optional loose curves and points in millimetres. Projected silhouettes, depth-event sampling and near-contact refinement can miss short intervals. Sections, blocks and arbitrary freeform drawings have additional requirements. Shipping drawings use Rhino HLD by default; Pipes is an explicit experimental choice.

## Regression gate

From the repository root, build Release and run on the host runtime being measured
(the checked-in baseline uses .NET 10):

```powershell
dotnet build tools/worker-bench -c Release
$env:M2D_RHINO_TESTS = '1'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
dotnet tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll pipe-gate $resolver
# Review new measurements before replacing the baseline:
dotnet tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll pipe-gate $resolver --update-baseline
```

`--repeats N` defaults to three HLD solves (minimum two). Pipes is solved once per
fixture/view. `--baseline path` selects another baseline; the default is
`tools/worker-bench/pipe-gate-baseline.json`. A normal run exits non-zero on a
wrong-visible or wrong-hidden regression, missing baseline, environment/model mismatch, failed
solve or Pipes fallback. Updates are explicit and require a complete run.

`--resume <receipt-folder>` retains completed passing rows from an interrupted
run after checking exact production fingerprints, settings, repeat count and model
hashes. A new receipt links the earlier rows/archives; prior evidence is preserved.
The benchmark owns its headless RhinoCore and uses worker admission with the
ordinary memory budget and serial scheduler, including for the 320-pipe cases.

`pipe-compare <resolver> <pipes-drawing.json> <hld-drawing.json> <new-result.json>`
reuses the comparator on the gate's lossless drawing archives without rerunning
visibility. The archives must have matching production fingerprints and frames.
`pipe-diagnose <resolver> <model.3dm> <drawing-archive-folder> <new-output-folder>`
replays owner source/feature attribution and PNG generation from those archives.
Use the same build configuration/runtime as the archives; production fingerprint
checks remain enforced. It refuses to overwrite an existing output folder.

The matrix contains the original owner node (loose curves/point retained) and
5/20/80/320-pipe Brep-only cases in both layouts. Stacked lattice dimensions and
12%-of-extent spacing match the scaling report. Exact spacing is pinned to its
manifest (624.6750184008374, 464.3282399935208, 82.49185454824 mm), with the original
owner SHA-256 checked; RhinoCommon's bounding-box extents differ slightly.
Lateral copies use 120% of the
original X extent with no Z stacking. The one-copy layouts are deliberate controls.
Views are Top, SW and the fixed no-junction-cut box centred at (65986, 40014, 14669),
local XY ±1500 and Z ±3000 mm. The fixed box crops the matrix; it does not expand
to enclose the copies. A solve over 120 seconds skips subsequent 320-pipe work;
partial runs are reported and cannot replace the baseline.

Models are generated/reused under `$M2D_MODELS/generated/pipe-gate-v3`, receipts
under `$M2D_MODELS/evidence/pipe-gate-<date-time>`; the root defaults to
`~/.t3/m2d-plus-models`. Preserve these assets outside git. Receipts include model
SHA-256, settings, runtime/Rhino/assembly fingerprints, commit and dirty-tree state,
per-repeat comparisons and median HLD timing. Timings are per-view engine time,
including culling, admission and output construction; they exclude startup, capture,
scene preparation, tolerance census, serialization, comparison and IO. Initial
JIT/cache costs can affect Pipes' single run.

The shared comparator uses projected arc-length midpoint stations at ≤0.25 mm,
at least 33 per stroke, and exact curve distances ≤0.05 mm. Wrong-visible is Pipes
Visible supported only by HLD Hidden; its denominator is Pipes Visible length.
Wrong-hidden is HLD Visible supported only by Pipes Hidden; its denominator is
HLD Visible length. The separate Pipes-side Hidden-over-HLD-Visible diagnostic
samples Pipes Hidden supported only by HLD Visible. Missing loci in both directions, each engine's Unresolved
length, and directional point class/absence counts are separate. Same-class support
wins where loci coincide. All strokes, including duplicates, contribute length;
shorter-than-station events can be missed. Hidden points omitted by an engine are
reported as point absence, since no emitted class is available to compare.

Schema 2 stores each fixture's maximum wrong-visible and wrong-hidden over HLD
repeats, each in mm and percent, with separate observed repeatability ranges and
0.5 mm margins (converted using each metric's own Visible denominator). All four
limits must pass. Full bidirectional class/missing HLD
variation over every repeat pair is retained separately, so unrelated HLD noise
does not loosen either band. HLD agreement is not independent accuracy
certification. Run the licensed `DrawingComparisonTests` for self/swap/deletion,
coincident-class, Unresolved and point controls.

Pipes self-overlap is diagnostic only: arc-length stations within 0.05 mm of
another emitted piece of the same class, excluding the entire sampled piece.
Both copies count, so two coincident 10 mm strokes report 20 mm, not 10 mm excess.
It is not a geometric union and does not measure retracing within one piece.
Per-class values are retained in JSON; the table reports their sum.

Owner Top and SW receipts include `wrong-hidden.json` (source GUID, feature,
arc-length cell parameters and own-body ray controls), `wrong-hidden.md`, a grey
whole drawing with red error spans (`wrong-hidden.png`), and a densest-cluster
crop (`wrong-hidden-worst-cluster.png`). The HLD repeat with greatest wrong-hidden
is used. Features are matched to the archived HLD 3D support and original source
edges/apparent contours at 0.05 mm. Native trimmed-Brep ray intersections and
solid-interior checks at each span midpoint are retained as representative controls,
not a length-integrated accuracy oracle. Cap-boundary arcs count as pipe end/cap features;
nonplanar junction boundaries count as junction trims. Ray-control findings suggest
mechanisms; they do not establish independent geometric truth or change classifiers.

When three repeats miss known threaded realizations, use
`--update-baseline --resume <complete-schema-2-receipt> --calibrate-from <receipt>`
to pool error observations from complete receipts. Repeat `--calibrate-from` for
additional hosts. Schema 1 receipts are read from their directional class buckets,
so the corrected wrong-hidden direction is used. Runtime, exact production
fingerprints, model hashes, settings, spacing and distance must all match.
Calibration can include a complete receipt whose gate bands failed, since its
underlying comparisons remain valid. It never uses unrelated HLD class/missing
variation to widen a band. Each band records its observation count; the baseline
and new receipt identify the calibration sources and resulting limits. Baseline
receipt paths are relative to `M2D_MODELS`; receipt JSON retains absolute paths. A subsequent
normal gate run is still required. `--resume` reruns failed rows and rechecks every
retained passing row against the selected baseline.

Calibration deduplicates retained rows by their actual drawing archive folder,
following receipt resume ancestry. Reusing a passing row does not add observations.
Bands retain `ObservationArchives` alongside the count; conflicting metrics for the
same archive are rejected. Those archive paths are relative to `M2D_MODELS`.
