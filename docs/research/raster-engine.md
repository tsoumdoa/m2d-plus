# Raster visibility — stages 0 and 1

The stage 0 record below is preserved as historical evidence. The owner approved
a second opt-in experimental engine on 2026-11-28; see [stage 1](#stage-1--owner-approved-opt-in).

## Stage 0 record

2026-11-28. Standalone prototype and measurement only. No product engine, setting,
cache identity, worker protocol or HLD behavior changes. No commit, installation
or release. The stage 1 decision remains with the owner.

## Question and method

Can a complete approximate CPU raster visibility engine beat the HLD step by at
least 3× on owner-2 Top and Plan A, with at most 1% wrong-visible length on quick
controls? This is deliberately not an HLD culling hybrid. Raster classifies every
candidate without HLD. A separate single-threaded HLD solve supplies the reference.

`worker-bench raster-poc` uses the existing `ObserveNativeInputs` research hook in
`VisibilityInputOrder.Native`. It copies the ordered registration batch and throws
a private sentinel before `AddGeometry` or `Compute`. The ordinary view pipeline
has already done capture exclusion, block transforms/normalization, source culling,
mesh face crop, Route C physical trim/cap, wire/point clipping and clear-glass
substitution. The harness checks the sentinel, nonempty inputs and zero HLD calls,
and retains the same scene lease throughout. Both reference and prototype consume
the same copies, frame, resolved view and tolerance. Receipts contain source and
registration counts and a digest of geometry fingerprints/source identities/far flags.
Authored `SourceDepth` is explicitly passed to settings resolution and asserted
against both effective depth and the final resolved cut.

The comparison boundary is **raw projection of those registrations**, before the
shared Route C output suppression/crop/Cut precedence and presentation. Both sides
use that boundary. The stage 0 tool does not claim final product drawing parity or
new section certification. Preparation is recorded separately and excluded from
both projection times.

All timing runs acquire `Local\M2DPlusLocalModelSuiteRhino` before starting their
owned, invisible Rhino.Inside host. One process is used at a time. No interactive
Rhino is inspected or changed. Accuracy HLD uses `MultipleThreads=false`;
the two primary cases also have separately measured production-threaded HLD.
CPU raster,
meshing, extraction and classification use up to 16 managed threads. Startup,
capture/preparation, serialization, comparison and error images are outside the
projection timer. Each model/resolution is measured once unless explicitly labelled
as a diagnostic retry. HLD agreement is a reference comparison, not independent
geometric truth or an accuracy certificate. The mutex serializes cooperating
research work; machine-wide CPU quiescence is not established.

## Prototype

All implementation is in `tools/worker-bench/RasterPoc/`; the only existing-code
edits are two command dispatches in the standalone tool's `Program.cs`.

* `RasterBuffer.cs`: double-precision depth plus triangle ID, deterministic binning
  into 32×32 tiles, disjoint tile ownership under `Parallel.For`, two-sided triangle
  coverage. Triangle IDs resolve to unique prepared face IDs. The buffer uses
  12 bytes/pixel and is capped at 512 MiB; aspect ratio and the actual clamped grid
  dimensions are recorded. The cap does not bound native meshes, bins or outputs.
* `RasterProjection.cs`: view-scale native Brep meshing, direct input meshes, exact
  Brep edges, wire curves, points and tangent silhouettes. Product preparation
  supplies converted extrusions/surfaces/SubD and transformed block leaves. Unknown
  prepared types refuse rather than disappear. A whole-Brep mesher face-count
  mismatch triggers explicit per-face meshing, with document-tolerance retries
  for small faces rather than silently dropping material.
  Smooth edges and seams follow their requested flags; defaults are off.
* Native `Silhouette.Compute` tangencies and mesh face-normal sign-change contours
  are timed separately. Both alternatives are measured; the unselected extraction
  phase's wall time is subtracted from the reported total. Shared allocation/GC
  effects remain. Native tangencies preserve exact candidate
  loci. Mesh mode is a command-line experiment, not a setting.
* Samples are at most 0.4 pixel apart along the projected exact curve. Class
  transitions receive eight bisection steps; emitted curves are exact trimmed
  candidates (converted exactly to NURBS before flattening) with independent
  projected and original-depth geometry, source
  metadata, Visible/Hidden, Profile and FarCandidate. Profile marks a tangent
  candidate, not a claimed equivalent of HLD's scene-profile flag. Points are
  retained only when visible. No HLD call is made inside the raster prototype.

Mesh tolerance is `max(document tolerance, nominal pixel / 8)`. Classification
extrapolates the front triangle plane from pixel centre to candidate ray. The bias
is `2 * tolerance + 2 * mesh tolerance`, plus a slope allowance of
`min(2 * actual pixel, actual pixel * (abs(dz/dx) + abs(dz/dy)) / 4)`.
This is an approximation, not a conservative bound.

An important deviation from the suggested design: an adjacent-face ID alone is
**not** enough to show a topological edge. A cylindrical face wraps around its
body; its front pixels would show a rear cap rim. Stage 0 restricts the ID shortcut
to actual tangent silhouette candidates. Unset silhouette component IDs are matched
to nearby face supports at five curve stations. General concave/multi-patch faces
still need stronger admission/association rules before integration.

## Evidence and reproduction

Private model paths are read only through `owner/manifest.json` labels. Models,
lossless drawing archives, receipts, red-span PNGs and logs remain outside git:

`~/.t3/m2d-plus-models/evidence/raster-2026-11-28/`

`trial-1` is the initial adjacent-face diagnostic; `trial-2` corrects rear-rim ID
handling and silhouette face matching. `matrix-1` preserves the owner-2 Top HLD
reference and the first mesher refusal. `matrix-2` and `matrix-3` preserve the
small-face meshing and curve-projection refusal trials. These are preserved, not
scored as final matrix results. `matrix-4` contains the corrected three-resolution
measurement matrix; owner-2 Top reuses the exact checked reference from `matrix-1`.
`threaded` holds separate production-threaded HLD timings with matching input
digests. Plan A's additional threaded timing is in its case folder. Accuracy
always uses the single-threaded reference. The Plan A name denotes the existing
boxed request in the earlier owner-profile evidence, not a named viewport in the
model. Its external descriptor is copied under `view-descriptors`, hashed in the
case receipt and supplied with `--view-request`. No private geometry coordinates
are embedded in this repository. The authored depth is preserved and asserted.

Build `tools/worker-bench` in Release, set `M2D_RHINO_TESTS=1` and
`DOTNET_ROLL_FORWARD=LatestMajor` for the measured .NET 10 host, then run:

```text
dotnet tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll raster-poc <resolver> <label> <view-name> <fresh-evidence-folder> 2048,4096,8192
```

Accepted labels are `owner-1`, `owner-2`, `perf-M`, `curved-S` and an explicit small
fixture allowlist. Each invocation refuses to overwrite evidence. Add `mesh` to
select mesh silhouette candidates. Every invocation first runs managed raster
depth, adjacency and serial/parallel controls. `--reference=<case.json>` reuses a
completed single-threaded drawing only after exact model, settings, engine,
source-census, input-digest, frame and tolerance checks. `--timing-only=<case.json>`
measures threaded HLD after the same input checks. `--also-threaded` measures
threaded HLD first on the current captured registrations, followed by the
single-threaded reference and raster matrix. The case records that order.

The existing pipe-gate `DrawingComparison` is linked unchanged. Drawings are
converted to millimetres before comparison: arc-length midpoint stations at
≤0.25 mm (at least 33/stroke), exact curve distance ≤0.05 mm. Wrong-visible samples
raster Visible supported only by HLD Hidden, divided by raster Visible length.
Wrong-hidden samples HLD Visible supported only by raster Hidden, divided by HLD
Visible length. Same-class support wins for coincident loci. Missing length in
both directions, Unresolved and point disagreements/absence are separate. Duplicate
strokes count as length rather than a geometric union; short events can be missed.

Each `case.json` records exact runtime/Rhino/engine/benchmark fingerprints, model
hash, threading, settings, input assertions, HLD phases and source census. Each
resolution's `result.json` records all raster phases, actual resolution/pixel size,
mesh tolerance, triangle/candidate counts, process private/working-set peaks sampled
at 25 ms, speed ratio and the full comparator receipt. Memory includes the host,
input, retained HLD reference and both silhouette alternatives; it is not an
incremental raster-only allocation. Total stops when the drawing/result is
assembled; subsequent mesh/candidate cleanup is outside that total.
Error spans, feature-length attribution and whole-drawing PNGs are separate files.

The archives use the existing `ViewDrawing`/codec container and HLD effective
settings; no raster enum is shipped. The standalone receipt and filename identify
the raster output. Raster's `NativeCalls` is zero, and the global HLD counter is
asserted unchanged across each prototype solve.

`raster-rays <resolver> <case-folder> <resolution> <fresh-output-folder>` repeats
the prepared-input capture with digest checks and makes no HLD solve. It examines
the twelve longest error spans per direction using trimmed-Brep forward
intersections and strict solid-interior midpoint tests, on both the candidate ray
and its pixel-centre ray. These are representative controls, not a length-integrated
oracle. `report.py <evidence-root>` summarizes the preserved JSON without Rhino.

## Results

Single observations on Rhino 8.36.26296.17001, .NET 10.0.15 x64, 32 logical
processors; up to 16 managed raster/meshing/classification tasks. All results use
native tangent silhouettes. HLD serial is the correctness reference; the threaded
column is the measured production policy on the primary cases. Speedups in the
table use **threaded HLD Compute / complete raster projection**. An em dash means
threaded timing was not measured for that control.

Lengths are millimetres and include duplicate strokes. Raster-only means raster
loci absent from HLD; HLD-only means HLD loci absent from raster. These are sampled
coverage results at the comparator's declared spacing/distance. Peak private
memory includes the headless host and retained reference/diagnostic allocations.

<!-- stage0-results:start -->

| Model / view | Requested px | Actual grid | Pixel mm | HLD serial s | HLD threaded s | Raster s | Threaded speedup | Wrong-visible mm / % | Wrong-hidden mm / % | Raster-only / HLD-only mm | Peak private GiB |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| owner-2 / Top | 2048 | 2048×1050 | 34.7810 | 344.605 | 89.233 | 6.276 | 14.22× | 2069423.71 / 38.8807 | 104178.90 / 3.7264 | 3975.63 / 0.00 | 6.66 |
| owner-2 / Top | 4096 | 4096×2100 | 17.3905 | 344.605 | 89.233 | 13.383 | 6.67× | 848362.94 / 20.5887 | 84311.80 / 3.0158 | 3975.92 / 0.00 | 6.32 |
| owner-2 / Top | 8192 | 8192×4201 | 8.6932 | 344.605 | 89.233 | 19.448 | 4.59× | 417081.22 / 11.2790 | 76329.60 / 2.7303 | 3975.73 / 0.00 | 6.32 |
| owner-2 / Plan A | 2048 | 2047×1392 | 24.1826 | 153.415 | 43.526 | 3.058 | 14.23× | 739286.66 / 29.5639 | 38589.95 / 2.6114 | 121.40 / 0.00 | 2.78 |
| owner-2 / Plan A | 4096 | 4095×2785 | 12.0884 | 153.415 | 43.526 | 3.577 | 12.17× | 245184.82 / 12.2189 | 35929.58 / 2.4313 | 122.03 / 0.00 | 2.75 |
| owner-2 / Plan A | 8192 | 8111×5515 | 6.1035 | 153.415 | 43.526 | 4.454 | 9.77× | 187571.08 / 9.5781 | 27840.28 / 1.8839 | 121.23 / 0.00 | 3.06 |
| owner-1 / Top | 2048 | 2048×1557 | 2.4164 | 1.268 | — | 0.289 | — | 523.95 / 1.6366 | 392.46 / 1.4095 | 0.00 / 0.00 | 0.36 |
| owner-1 / Top | 4096 | 4096×3114 | 1.2082 | 1.268 | — | 0.276 | — | 406.62 / 1.2727 | 355.08 / 1.2753 | 0.00 / 0.00 | 0.52 |
| owner-1 / Top | 8192 | 7671×5832 | 0.6451 | 1.268 | — | 0.394 | — | 355.52 / 1.1161 | 370.17 / 1.3294 | 0.00 / 0.00 | 0.91 |
| perf-M / Plan level 1 | 2048 | 1768×2047 | 14.1399 | 12.874 | — | 2.234 | — | 6380.12 / 0.1120 | 1924.33 / 0.1728 | 0.00 / 0.00 | 1.34 |
| perf-M / Plan level 1 | 4096 | 3537×4095 | 7.0680 | 12.874 | — | 2.709 | — | 2492.69 / 0.0614 | 898.50 / 0.0807 | 0.00 / 0.00 | 2.17 |
| perf-M / Plan level 1 | 8192 | 6216×7196 | 4.0220 | 12.874 | — | 3.660 | — | 1388.17 / 0.0238 | 595.84 / 0.0535 | 0.00 / 0.00 | 2.56 |
| curved-S / Top | 2048 | 2048×849 | 24.6995 | 18.586 | — | 2.014 | — | 43275.87 / 2.2649 | 18503.32 / 1.4497 | 3.99 / 0.00 | 2.71 |
| curved-S / Top | 4096 | 4096×1698 | 12.3497 | 18.586 | — | 2.024 | — | 41315.41 / 2.1581 | 14493.79 / 1.1356 | 3.49 / 0.00 | 2.93 |
| curved-S / Top | 8192 | 8192×3396 | 6.1749 | 18.586 | — | 2.452 | — | 19413.84 / 1.0215 | 12813.85 / 1.0040 | 3.35 / 0.00 | 3.14 |
| brep_box / SW | 2048 | 1782×2048 | 1.6491 | 0.395 | — | 0.261 | — | 2.48 / 0.0101 | 0.00 / 0.0000 | 0.00 / 0.00 | 0.33 |
| brep_box / SW | 4096 | 3565×4096 | 0.8243 | 0.395 | — | 0.109 | — | 0.91 / 0.0037 | 0.00 / 0.0000 | 0.00 / 0.00 | 0.47 |
| brep_box / SW | 8192 | 6240×7169 | 0.4710 | 0.395 | — | 0.324 | — | 0.69 / 0.0028 | 0.00 / 0.0000 | 0.00 / 0.00 | 0.97 |
| brep_curved_sphere / SW | 2048 | 2048×2048 | 1.0104 | 0.766 | — | 0.341 | — | 0.00 / 0.0000 | 0.00 / 0.0000 | 0.00 / 0.00 | 0.35 |
| brep_curved_sphere / SW | 4096 | 4096×4096 | 0.5052 | 0.766 | — | 0.225 | — | 0.00 / 0.0000 | 0.00 / 0.0000 | 0.00 / 0.00 | 0.53 |
| brep_curved_sphere / SW | 8192 | 6688×6688 | 0.3094 | 0.766 | — | 0.455 | — | 0.00 / 0.0000 | 0.00 / 0.0000 | 0.00 / 0.00 | 1.01 |
| chs_hollow / SW | 2048 | 1487×2048 | 1.4044 | 0.390 | — | 0.463 | — | 52.44 / 0.2469 | 8.25 / 0.0574 | 0.00 / 0.00 | 0.32 |
| chs_hollow / SW | 4096 | 2974×4096 | 0.7022 | 0.390 | — | 0.182 | — | 8.70 / 0.0411 | 9.25 / 0.0644 | 0.00 / 0.00 | 0.45 |
| chs_hollow / SW | 8192 | 5699×7849 | 0.3665 | 0.390 | — | 0.353 | — | 7.50 / 0.0354 | 10.50 / 0.0731 | 0.00 / 0.00 | 0.96 |

### Highest-resolution phase times

| Model / view | Tessellate s | Raster s | Native silhouettes s | Mesh silhouettes alternative s | Edges ms | Classify/output s | Total s |
|---|---:|---:|---:|---:|---:|---:|---:|
| owner-2 / Top | 16.744 | 0.524 | 0.792 | 0.549 | 51.15 | 1.089 | 19.448 |
| owner-2 / Plan A | 2.897 | 0.422 | 0.341 | 0.243 | 15.07 | 0.660 | 4.454 |
| owner-1 / Top | 0.107 | 0.225 | 0.018 | 0.031 | 0.61 | 0.041 | 0.394 |
| perf-M / Plan level 1 | 1.243 | 0.517 | 0.581 | 0.177 | 110.11 | 1.003 | 3.660 |
| curved-S / Top | 1.230 | 0.361 | 0.233 | 0.484 | 3.74 | 0.493 | 2.452 |
| brep_box / SW | 0.001 | 0.315 | 0.001 | 0.000 | 0.26 | 0.008 | 0.324 |
| brep_curved_sphere / SW | 0.047 | 0.321 | 0.004 | 0.027 | 0.10 | 0.083 | 0.455 |
| chs_hollow / SW | 0.027 | 0.233 | 0.002 | 0.009 | 0.23 | 0.091 | 0.353 |

### Source census and preparation

| Model / view | Captured sources | Registration records | Registered sources | Preparation s | HLD serial peak private GiB |
|---|---:|---:|---:|---:|---:|
| owner-2 / Top | 3280 | 3280 | 3280 | 0.962 | 1.94 |
| owner-2 / Plan A | 1597 | 1537 | 1534 | 27.094 | 1.31 |
| owner-1 / Top | 11 | 11 | 11 | 0.071 | 0.54 |
| perf-M / Plan level 1 | 5000 | 5000 | 5000 | 0.716 | 0.94 |
| curved-S / Top | 500 | 500 | 500 | 0.207 | 0.73 |
| brep_box / SW | 1 | 1 | 1 | 0.197 | 0.53 |
| brep_curved_sphere / SW | 1 | 1 | 1 | 0.136 | 0.54 |
| chs_hollow / SW | 1 | 1 | 1 | 0.041 | 0.53 |

### Points and native Unresolved at the highest resolution

Hidden points are omitted by both adapters; absent visible points do not identify a hidden/Unset reference class. Other controls have no visible points or native Unresolved curve length.

| Model / view | Raster visible points | HLD visible points | Raster points absent from HLD | HLD points absent from raster | HLD Unresolved curve mm |
|---|---:|---:|---:|---:|---:|
| owner-2 / Top | 100 | 68 | 51 | 19 | 5289.872 |
| owner-2 / Plan A | 54 | 36 | 19 | 1 | 368.356 |
| owner-1 / Top | 0 | 0 | 0 | 0 | 1.073 |

### Representative native ray controls

Counts concern the twelve longest spans per direction, one midpoint per span. They are not length-weighted accuracy estimates. Material means positive strict solid-interior length between native forward intersections.

| Case | Direction | Controls | Candidate hits / material | Centre hits / material | Candidate-only material | Centre-only material | Own-body material | In requested frame | Failed queries |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| curved-S-Top | WrongVisible | 12 | 0 / 0 | 4 / 1 | 0 | 1 | 0 | 12 | 0 |
| curved-S-Top | WrongHidden | 12 | 12 / 12 | 12 / 12 | 0 | 0 | 0 | 12 | 0 |
| owner-1-Top | WrongVisible | 12 | 12 / 12 | 8 / 6 | 6 | 0 | 9 | 12 | 0 |
| owner-1-Top | WrongHidden | 12 | 12 / 12 | 12 / 12 | 0 | 0 | 7 | 12 | 0 |
| owner-2-Plan-A-retry | WrongVisible | 12 | 12 / 12 | 7 / 7 | 5 | 0 | 0 | 12 | 0 |
| owner-2-Plan-A-retry | WrongHidden | 12 | 10 / 10 | 12 / 12 | 0 | 2 | 1 | 12 | 0 |
| owner-2-Top | WrongVisible | 12 | 12 / 12 | 9 / 9 | 3 | 0 | 0 | 12 | 0 |
| owner-2-Top | WrongHidden | 12 | 12 / 12 | 12 / 12 | 0 | 0 | 2 | 12 | 0 |

<!-- stage0-results:end -->

Both primary HLD-step speed targets pass at every tested resolution. The accuracy
target fails on owner-1 and curved-S at every tested resolution. The image cap
reduces the requested 8192-pixel grids on several models; the actual grids and
physical pixel sizes above are the measured values, not assumed resolutions.

Plan A reuses the archived boxed request's authored depth. Capture's fixed-volume
exclusion precedes the 1,597-source scene. Route C reports 26 section refusals,
130 independently certified regions and 29 failed-clip diagnostics; the resulting
1,537 registration records represent 1,534 sources. HLD accepts the entire
registration batch. These pre-existing preparation outcomes are shared by both
engines; stage 0 does not reclassify or repair them. The reported projection
errors precede the ordinary failed-footprint demotion/crop/suppression step.

Plan A preparation alone costs 27.094 s. Combining that measured preparation
subtotal with the highest-resolution projection gives approximately 2.24× for
preparation + projection (43.526 s HLD versus 4.454 s raster after preparation),
excluding initial capture and later presentation. The 9.77× HLD-step ratio is not
an end-to-end drawing speedup.

## Where time and memory go

Tessellation is the principal cost on the owner views. On owner-2 Top, its time
increases from 3.716 to 10.650 to the highest-resolution value above, despite the
same 8,442,977 triangles. Meshing/allocations/native scheduling show substantial
single-run variation; no median or guaranteed latency is claimed. Rasterization
itself is a small fraction of total time on the primary cases. GPU work is not justified by this
profile.

Native tangent silhouettes are retained. On owner-2 Top the alternative mesh
contour extraction saves only a fraction of a second, while emitting 179,993
segments versus 27,912 native tangent records. It would also approximate candidate
loci and add fragmented output/classification work. Native silhouette cost does
not consume the speed win. Mesh contours were profiled, not selected for the final
accuracy matrix.

The 512 MiB image cap is not a process cap: native meshes, two triangle collections,
candidate/output curves and the retained HLD reference raise owner-2 Top to
6.66 GiB sampled private memory. A production implementation needs a separate
whole-job memory estimate/admission policy; this harness's peaks cannot be read
as incremental raster-only memory. Separately measured production-threaded HLD
peaks are 2.47 GiB for owner-2 Top and 1.38 GiB for Plan A.

## Error attribution

At the highest requested resolution:

* Owner-1 wrong-visible length is dominated by curved trim/contact edges
  (270.53 of 355.52 mm), followed by cap/sharp edges (73.58 mm). Wrong-hidden
  includes 250.54 mm of curved trims and 87.63 mm of tangent silhouettes. Contact
  and grazing/self-occlusion behavior remains after the gross rear-rim ID bug is
  fixed; error does not decrease monotonically in both directions.
* Owner-2 Top wrong-visible length shifts from tangent contours at coarse grids
  to wires/contact features at the highest grid: 284,646.11 of 417,081.22 mm.
  Cap/sharp and curved-trim edges contribute 57,602.43 and 56,741.15 mm. Tangent
  contours contribute 18,091.53 mm. The coarse-to-fine improvement is strong, but
  shallow-depth/contact and thin/grazing-coverage errors remain substantial.
* Plan A likewise has 129,121.06 of 187,571.08 mm wrong-visible on wire/contact
  features. Curved-S has 10,702.22 of 19,413.84 mm on curved trim/contact edges
  and 6,368.20 mm on tangent contours.

The native ray-control table records material obstruction at representative
candidate and pixel-centre rays. Centre/candidate differences support a pixel
coverage mechanism; same-ray material within the depth allowance is consistent
with bias/contact errors. Own-body obstruction on a tangent candidate is also
consistent with an unsafe self-occluding-face ID shortcut. These mechanisms are
inferences from the controls, not proof for every reported millimetre. Native
surface/interior controls may also disagree with HLD at a contact; HLD remains the
specified comparator reference and no error bands are widened.

All 12 longest wrong-visible controls on each owner-2 view have positive native
material at the candidate ray. Three Top controls and five Plan A controls have
no material at the pixel-centre ray, supporting a finite-pixel coverage mechanism.
Owner-1 has the same pattern in six of twelve controls, and nine controls show
own-body obstruction. All 96 controls are inside their requested frames; none of
their native queries failed.

The remaining controls do not establish one universal cause. In particular,
curved-S's twelve longest wrong-visible midpoints have no forward native hits
beyond the `4 * tolerance` ray-start offset: near-contact/coincidence or HLD's
contact policy remains an unresolved explanation for that subset. Several
HLD-visible/wrong-hidden supports also have native material in front. Projected
coincidence/duplicate ownership and reference/kernel differences must be audited
before interpreting those supports as independent physical truth.

Code inspection identifies another risk to audit: the selected pixel's triangle
plane is extrapolated to the candidate ray without checking whether that ray is
inside the triangle. Grazing-plane extrapolation, the depth allowance and missing
subpixel coverage can therefore interact. The representative native controls do
not attribute an exact error length to each mechanism.

## Validation

* Debug and Release solution builds: zero warnings/errors.
* Forced local Git hook passed: 586 Core tests and 140 GH UI-model tests.
* Full default Debug solution test run passed; its Rhino test assembly passed
  nine managed checks and skipped 344 licensed cases as designed.
* Licensed `DrawingComparisonTests` on the .NET 10 host: nine passed, none skipped.
* Standalone benchmark Debug and Release builds passed with warnings as errors.
* Every matrix invocation passed raster depth/adjacency/bounds controls under
  one and four threads. Every prototype solve asserted zero additional HLD calls.
* Additional closed-quad mesh smokes at 256 requested pixels passed the same input
  and zero-HLD-call checks. SW goes through Route C's Brep conversion: 0.031 s
  raster versus 0.402 s serial HLD; 13.19 mm (0.0538%) wrong-visible, zero
  wrong-hidden. Top exercises the direct Mesh adapter: 0.031 s versus 0.633 s,
  with zero detected curve-class or coverage errors. Both are separate from the
  three-resolution matrix and retained under `smoke`.
* Model hashes, exact production/runtime fingerprints, source census, input
  digests and authored/resolved depth checks are retained in external receipts.

No product source files, GUIDs, settings, workers, cache identities or installation
paths changed. No owner model, raw geometry receipt, private filename, GUID or
coordinate is added to git. No commit or push was made.

## Stage 1 decision and required work

**No-go for integration as built.** The highest tested grids still give 1.1161%
wrong-visible on owner-1 and 1.0215% on curved-S, above the requested 1% quick-model
gate. Owner-2 Top is fast enough (4.59× the measured threaded HLD step at the
highest resolution), but its 11.2790% wrong-visible length is substantial.
The measurements support further accuracy research before product integration.
No stage 1 product work is included here.

A subsequent stage would need:

1. **Admission and accuracy:** fix or bound the face-ID shortcut on self-occluding
   faces, contact/coincident-depth policy, thin-occluder coverage and grazing bias.
   Define a physical pixel-size/detail policy and memory admission. Reject an
   entire unsupported view, with an explicit HLD fallback remark; never send a
   reduced scene or ambiguous intervals to HLD. Preserve cut-only/zero-depth
   no-projection behavior and independent Route C section certification.
2. **Settings and disclosure:** an explicit `visibility=raster` opt-in, owner-decided
   resolution/precision controls, requested hidden/tangent/seam handling, and Info
   that reports actual pixel size, approximate visibility and fallback reasons.
   Resolve tangent feature weighting versus HLD scene-profile semantics. Keep
   job state separate from accuracy, and preserve hidden-off/min-visible policy.
3. **Ownership, memory and routing:** budget meshes, triangle arrays/bins, buffers
   and output fragments, rather than just the 512 MiB image cap. Reuse definition
   meshes under proper density/geometry/transform keys. Add cancellation checks,
   complete-current publication, leases and worker hard-cancel behavior. Respect
   `HostPlatform`/in-process admission; do not introduce a heavy in-process fallback
   when workers are unavailable.
4. **Cache and transport:** key the opt-in engine, precision/meshing/bias policy
   and implementation fingerprint. Leave HLD identities unchanged. Carry the
   request and experimental Info/quality counters through settings conversion and
   worker transport; update schema/negotiation if semantics change and reject old
   peers explicitly. Preserve every geometry/depth/source/path/Profile/FarCandidate
   field, with codec round trips and `WorkerCodecStateTests` coverage.
5. **Gates:** independent primitive visibility controls, tiny/contact/coincident/
   grazing/folded faces, thin tubes and sheets, mirrored/nested blocks, mesh/SubD,
   clear glass, loose curves and points, near/far caps and holes. Run whole-view
   HLD-fallback and zero-HLD-call assertions. Test the full shared suppression,
   crop, Cut precedence, Source/Target, min-visible, tree/Key, Bake and worker
   paths; stage 0 measures raw projection and does not certify those integrations.


# Stage 1 — owner-approved opt-in

2026-11-28. The owner accepted the overall shape of the stage 0 renders and asked
for the extra linework to be reduced, then for **Raster (experimental)** and a
trial Yak. This supersedes stage 0's integration no-go. HLD remains the default;
this is approximate, pixel-limited visibility, never an accuracy certificate.

## Correction and integration

The image now supplies a broad phase, not the visibility answer. A sample checks
its own parallel view ray against actual triangles in the containing 32x32 tile,
including triangles too small to cover a pixel centre. Barycentric coverage is
required. The pixel-centre plane extrapolation, slope allowance and all face-ID
visibility shortcuts are removed. A wrapping/folded face may occlude its own rear
edge or tangency. The classifier considers the entire batch and never calls HLD.

Brep meshing uses `max(document tolerance, nominal pixel / 32)`, four times finer
than stage 0's nominal pixel / 8. Depth allowance is `2 * document tolerance +
mesh tolerance`, with no pixel/slope term. This is a tessellation allowance, not a
proved error bound. Exact curve candidates and transition bisection are retained.
Brep tessellation also explicitly requests `DoublePrecision = true`. The first
strict ray/triangle run exposed a serious grazing-silhouette regression on owner-1:
9,936.67 mm / 35.6875% wrong-hidden, mostly tangent contours. Eleven of its twelve
longest wrong-hidden native controls had no forward material obstruction. Keeping
float mesh coordinates at survey locations was consistent with this self-occlusion
mechanism. Enabling double vertices reduced the probe to 426.89 mm / 1.5332%
wrong-hidden, while wrong-visible stayed about 1.96 mm. The full final matrix below
uses this corrected precision policy. The independent translated analytic-cylinder
continuity check is a positive control; it also passed on the old policy and does
not independently reproduce this model-specific NURBS regression.

No definition-mesh reuse is added; global tighter, double-precision meshing is the
measured trade-off.

The product consumes the same prepared ordered registration batch as HLD in both
uncut and Route C views. Normalization, block transforms, culling, physical cuts,
clear glass, regions/hatches, crop, Cut precedence and presentation remain shared.
The experimental projection transfers only on complete success. Any unsupported
batch, missing finite bounds, failed tessellation or projection failure discards
it and falls back to HLD for the **whole view**, with a grey remark. Cancellation
propagates without fallback. Unknown geometry cannot silently disappear.

A blanket Rhino-validity refusal initially blocked owner-2 Top before projection.
The captured batch has two invalid sources before **and** after HLD. As in the
prototype, sources that can tessellate remain in the full approximate batch;
invalid-source counts are disclosed in Info. Finite bounds and complete face
meshing are still required. This does not certify the source geometry or sections.

Resolution uses scene diagonal / (64 * document tolerance), clamped to 64..8192,
then clamps the actual grid by aspect ratio, the 512 MiB image ceiling and remaining
projection memory. Memory admission estimates meshes, triangles, bins, candidates,
output and image together; runtime count/allocation checks can refuse the batch.
Windows worker routing reserves additional raster memory, with no heavy in-process
fallback if workers are unavailable. A scene that fits the ordinary HLD in-process budget
may fall back to HLD with a remark after Raster refuses its extra memory. macOS uses the existing in-process-only live
budget. Native meshing/silhouette calls remain cooperatively cancellable between
calls; only workers have hard cancel and memory caps.

`visibility = hld | pipes | raster` and the menu add Raster after Pipes. HLD's
adapter/policy and default settings/run/visibility-key text are unchanged. Raster
keys its engine, precision policy and exact assembly/process fingerprint. Schema 4
is retained: mandatory exact Core/Rhino fingerprints reject older peers before
scene data; the existing enum converter carries Raster. Result pixel size (mm),
triangle/candidate counts, memory estimate, phases and diagnostics round-trip.
Host-captured unit scale also travels with the scene. Raster cache and retained
worker scene keys include it, even if a units edit preserves geometry/tolerance;
a regression test changes metres to centimetres without scaling and reuses the
same worker process. HLD scene and default visibility identities remain unchanged. The full stored-field census
in `WorkerCodecStateTests` covers the new result state.

## Measurement boundary and evidence

The final matrix calls the shipped projection and Core buffer directly from
`raster-poc`, at 8192 requested pixels, once per case. Every case freshly measures
threaded HLD, then the serial HLD accuracy reference, then Raster. The shared
`Local\M2DPlusLocalModelSuiteRhino` mutex serializes these owned headless hosts.
No interactive Rhino was inspected or changed, and no curved-M/campus run occurred.
Raw projection timing excludes capture, Route C preparation, comparison, renders
and cleanup, as in stage 0. A separate product smoke uses the calculated worker cap,
the ordinary full pipeline and an HLD-call guard. Raw speed is not end-to-end speed.

All evidence is outside git under:

`~/.t3/m2d-plus-models/evidence/raster-2026-11-28/stage1/`

`matrix-final-double` is scored. `runtime-final-double` preserves its binaries;
the Core/Rhino DLLs match the trial Yak. Earlier stage 1 probe/matrix folders are
preserved integration and float-mesh diagnostics, not mixed into the final table.
The float matrix in `matrix-reviewed` and the double-vertex probe make the
silhouette regression/correction auditable. Historical reference reuse refused a
changed exact assembly fingerprint; no reference identity checks were weakened.

The setting-out layer is identified only in an external UTF-8 descriptor. A second
comparison removes it from **both archived drawings**, without changing either
solve's inputs. Stage 0 archives are re-scored with the unchanged comparator,
requiring the same exact native Rhino build and schema; the measurement-only
geometry importer does not relax product cache/worker restoration. Receipts record
archive hashes and the scoring fingerprint. No private filenames, layer names,
object IDs or coordinates are committed.

<!-- stage1-results:start -->

All lengths below count duplicate strokes and use the unchanged 0.25 mm / 0.05 mm comparator. Percent denominators are each result’s Raster Visible length (wrong-visible) and HLD Visible length (wrong-hidden). Native HLD is a reference, not an independent certificate. Times are single observations; machine-wide CPU quiescence was not established.

| Model / view | Raster s before -> after | Final threaded HLD s | Final speedup | Wrong-visible mm / % before -> after | Wrong-hidden mm / % before -> after |
|---|---:|---:|---:|---:|---:|
| owner-2 / Top | 19.448 -> 13.964 | 98.584 | 7.06x | 417,081.22 / 11.2790 -> 40,652.89 / 1.2639 | 76,329.60 / 2.7303 -> 106,458.95 / 3.8080 |
| owner-2 / Plan A | 4.454 -> 5.688 | 44.538 | 7.83x | 187,571.08 / 9.5781 -> 8,901.10 / 0.5088 | 27,840.28 / 1.8839 -> 46,774.16 / 3.1652 |
| owner-1 / Top | 0.394 -> 1.228 | 0.468 | 0.38x | 355.52 / 1.1161 -> 1.96 / 0.0062 | 370.17 / 1.3294 -> 426.89 / 1.5332 |
| curved-S / Top | 2.452 -> 4.242 | 11.380 | 2.68x | 19,413.84 / 1.0215 -> 9,300.32 / 0.5719 | 12,813.85 / 1.0040 -> 11,750.83 / 0.9207 |
| perf-M / Plan level 1 | 3.660 -> 3.917 | 23.715 | 6.05x | 1,388.17 / 0.0238 -> 0.00 / 0.0000 | 595.84 / 0.0535 -> 2.25 / 0.0002 |
| brep_box / SW | 0.324 -> 0.399 | 0.351 | 0.88x | 0.69 / 0.0028 -> 0.26 / 0.0011 | 0.00 / 0.0000 -> 0.00 / 0.0000 |
| brep_curved_sphere / SW | 0.455 -> 0.555 | 0.768 | 1.38x | 0.00 / 0.0000 -> 0.00 / 0.0000 | 0.00 / 0.0000 -> 0.00 / 0.0000 |
| chs_hollow / SW | 0.353 -> 0.511 | 0.501 | 0.98x | 7.50 / 0.0354 -> 0.00 / 0.0000 | 10.50 / 0.0731 -> 0.00 / 0.0000 |

### Comparison with the setting-out layer removed

This removes the selected layer from both drawings for scoring, not from the prepared scene before solving. This exclusion applies to the identified owner-2 setting-out layer; no layer is excluded on the other controls.

| View | Wrong-visible mm / % before -> after | Wrong-hidden mm / % before -> after |
|---|---:|---:|
| owner-2 / Top | 132,615.70 / 4.1452 -> 3,496.52 / 0.1165 | 73,214.52 / 2.8386 -> 68,168.18 / 2.6429 |
| owner-2 / Plan A | 58,513.03 / 3.4244 -> 1,605.83 / 0.0975 | 25,852.35 / 1.9071 -> 20,435.73 / 1.5075 |

Owner-2 wrong-visible length falls 90.3% (Top) and 95.3% (Plan A) overall,
and 97.4% / 97.3% outside the setting-out layer. Outside that layer, wrong-hidden
length also falls 6.9% / 21.0%; the overall increase is concentrated in that
layer. Both owner-2 views exceed the required 3x projection speed, but small
models can be slower than HLD. These are single-run observations.

### Final phases and memory

| View | Actual grid / pixel mm | Mesh tolerance mm | Triangles | Tessellate / classify s | Sampled peak private GiB |
|---|---:|---:|---:|---:|---:|
| owner-2 / Top | 8192x4201 / 8.6932 | 0.2716 | 11,857,661 | 4.721 / 7.629 | 5.16 |
| owner-2 / Plan A | 8111x5515 / 6.1035 | 0.1888 | 5,655,880 | 2.136 / 2.659 | 3.00 |
| owner-1 / Top | 7671x5832 / 0.6451 | 0.0189 | 805,859 | 0.344 / 0.552 | 0.97 |
| curved-S / Top | 8192x3396 / 6.1749 | 0.1930 | 7,750,330 | 1.680 / 1.808 | 2.49 |
| perf-M / Plan level 1 | 6216x7196 / 4.0220 | 0.1104 | 4,305,065 | 0.997 / 1.377 | 2.45 |
| brep_box / SW | 6240x7169 / 0.4710 | 0.1000 | 16 | 0.006 / 0.020 | 0.77 |
| brep_curved_sphere / SW | 6688x6688 / 0.3094 | 0.1000 | 65,024 | 0.049 / 0.160 | 0.79 |
| chs_hollow / SW | 5699x7849 / 0.3665 | 0.1000 | 23,228 | 0.029 / 0.108 | 0.79 |

Peak private memory includes the headless host, retained reference and experimental allocations, not just incremental Raster memory. Comparison/render allocations and cleanup are outside the projection timer, matching stage 0. Inputs match stage 0 by model hash and prepared-input digest: owner-2-Top=True, owner-2-Plan-A=True, owner-1-Top=True, curved-S-Top=True, perf-M-Plan-level-1=True, brep_box-SW=True, brep_curved_sphere-SW=True, chs_hollow-SW=True.

### Full product smoke

These per-view times use an already prepared scene after the input-capture witness; they are not cold end-to-end timings. HLD setup/solve is forbidden by the smoke guard.

| View | Pipeline s | Pixel mm | Pieces / regions | Worker cap / projection estimate GiB | HLD calls |
|---|---:|---:|---:|---:|---:|
| owner-2 / Top | 13.020 | 8.6932 | 73,870 / 0 | 10.00 / 4.24 | 0 |
| owner-2 / Plan A | 28.425 | 6.1035 | 31,792 / 130 | 6.00 / 2.37 | 0 |

### Representative native controls

Twelve longest spans per direction per control set; layer-excluded sets target residual junction linework. Queries use the original trimmed Breps at the candidate ray and pixel-centre ray, with a 4-tolerance start offset. Hits/material are midpoint controls, not length-integrated truth; open sheets and near-contact policy need separate interpretation.

| Case | Direction | Candidate hits / material | Centre hits / material | Own-body material | Failed native queries |
|---|---|---:|---:|---:|---:|
| curved-S-Top | WrongVisible | 0 / 0 | 6 / 1 | 0 | 0 |
| curved-S-Top | WrongHidden | 12 / 12 | 12 / 12 | 0 | 0 |
| owner-1-Top | WrongVisible | 0 / 0 | 0 / 0 | 0 | 0 |
| owner-1-Top | WrongHidden | 12 / 12 | 12 / 12 | 7 | 0 |
| owner-2-Plan-A | WrongVisible | 12 / 12 | 5 / 5 | 0 | 0 |
| owner-2-Plan-A | WrongHidden | 12 / 12 | 6 / 6 | 2 | 0 |
| owner-2-Plan-A-without-layer | WrongVisible | 1 / 0 | 6 / 6 | 0 | 0 |
| owner-2-Plan-A-without-layer | WrongHidden | 12 / 12 | 9 / 9 | 5 | 0 |
| owner-2-Top | WrongVisible | 12 / 12 | 8 / 8 | 0 | 0 |
| owner-2-Top | WrongHidden | 12 / 12 | 9 / 9 | 0 | 0 |
| owner-2-Top-without-layer | WrongVisible | 2 / 0 | 8 / 8 | 0 | 0 |
| owner-2-Top-without-layer | WrongHidden | 12 / 12 | 12 / 12 | 0 | 0 |

The remaining longest all-layer owner-2 wrong-visible controls still meet native
solid material at their candidate ray (12/12 each). The layer-excluded controls
meet no forward solid material (0/12 each), while several pixel-centre rays do.
This supports removing centre-ray extrapolation at junctions, but does not prove
every remaining HLD disagreement is a Raster error. Conversely every sampled
final wrong-hidden control meets native material; some are its own body. These
midpoint controls do not certify whole intervals or settle near-contact policy.

### Renders

The original external `visual/render.py` creates new PNGs under `stage1/visual/`. The driver and common-window selection are preserved in `stage1/render_stage1.py` and `stage1/visual/render-manifest.json`. Each owner view has a full rendering, a rendering without the setting-out layer, and three common before/after dense-error windows selected outside that layer. The combined dense-window PNG shows before Raster, after Raster, HLD and after-error overlay (red extra / blue missing).

* `stage1/visual/owner-2-Top-before-after-dense-1.png` (also dense-2, dense-3).
* `stage1/visual/owner-2-Plan-A-before-after-dense-1.png` (also dense-2, dense-3).
* Full PNGs: `stage1/visual/<case>-8192-stage1-full.png` for every matrix case.

Per-feature attribution and complete curve/point/missing-locus receipts remain in each case folder. These include wrong-hidden increases, not just improvements.

<!-- stage1-results:end -->

## Implementation files

| Area | Files |
|---|---|
| Plain raster and precision/memory policy | `src/M2DPlus.Core/Experimental/RasterVisibility/RasterBuffer.cs`, `RasterPolicy.cs` |
| Native adapter, atomic fallback and run identity | `src/M2DPlus.Rhino/Experimental/RasterVisibility/RasterProjection.cs`, `RasterProjectionRoute.cs`, `RasterEngineIdentity.cs`, `README.md` |
| Settings and menu | `src/M2DPlus.Core/Settings/SettingsModels.cs`, `SettingsValues.cs`; `src/M2DPlus.Grasshopper/UI/Model/SettingsCatalog.cs` |
| Component run/progress identity | `src/M2DPlus.Grasshopper/DrawingComponent.Async.cs` |
| Shared prepared-batch dispatch and result state | `src/M2DPlus.Rhino/Services/NativeDrawing.cs`, `SectionDrawing.cs`, `DrawingPresentation.cs`, `ViewNotices.cs` |
| Routing, memory reservation, captured units, transport | `src/M2DPlus.Rhino/Services/DrawingWorkerPool.cs`, `HostCapture.cs`, `Scene.cs`, `WorkerClient.cs`, `WorkerCodec.cs` |
| Tests | `tests/M2DPlus.Core.Tests/RasterVisibilityTests.cs`; `tests/M2DPlus.Grasshopper.Tests/PipeVisibilityUiTests.cs`; `tests/M2DPlus.Rhino.Tests/RasterVisibilityTests.cs`, `WorkerCodecStateTests.cs` |
| Harness | `tools/worker-bench/RasterPoc/RasterBench.cs`, `RasterControls.cs`, `RasterRays.cs`, `report.py`, `README.md`; `tools/worker-bench/Program.cs`; prototype buffer/projection moved into product libraries |
| Contracts and evidence | `AGENTS.md`, `docs/PLAN.md`, `docs/REQUIREMENTS.md`, `docs/design/native-engine.md`, `docs/design/ux.md`, this report |

## Validation and trial

<!-- stage1-validation:start -->

* Debug and Release solution builds: zero warnings/errors; worker-bench Release
  also builds with warnings as errors.
* Focused licensed fixture/routing/geometry/codec checks: **17 passed on .NET 8**
  and **17 passed on .NET 10**, none skipped. This includes uncut/Route C,
  primitive/mesh/SubD/mirrored block fixtures, independent shallow-wire/point
  visibility, crop/Cut/Hatch/Bake, whole-view fallback, default HLD adapter parity,
  borrowed-mesh immutability, cancellation without fallback, unit changes through
  the same retained worker and drawing cache, and the forced Mac route.
* Forced local hook: **592 Core** and **141 GH UI-model** tests passed. Full
  default Debug suite: 592 Core, 141 GH UI-model and 9 managed Rhino checks passed;
  352 licensed cases skipped as designed. Logs are under `stage1/validation`.
* Trial Yak: `artifacts/yak/trials/m2d-plus-0.1.0-beta.17-rh8_20-win.yak`.
  SHA-256: `22275c6cd7b4d7139dae118c1d64d6fd14fe1328f6349743a07cf24c75602585`.
  The archive inventory/versions/payload checks pass (19 files, matching .NET 8/10
  workers, no host DLLs). Its Core/Rhino DLLs match `runtime-final-double` exactly.
* No version bump, commit, push, installation or publication. The intentional
  package-name/assembly-name Yak warning remains. Installed-Yak interactive and
  native macOS validation remain manual.

<!-- stage1-validation:end -->

## Limits to disclose

This is an owner-approved trial, not a new default or a certificate. Tightening the
classifier reduces extra linework but can increase wrong-hidden disagreement.
HLD agreement is a reference comparison; duplicate ownership, shallow contacts,
near coincidence, grazing faces, native tessellation and sampled transitions remain
limits. Very short visibility events can be missed between samples, and duplicate
strokes remain. Profile means a tangent candidate, not HLD's scene silhouette.
Unsupported/failed views can take HLD time. Physical section refusals and failed
clip-footprint demotion remain independent and unchanged. Memory estimates are not
hard native allocation bounds. macOS is compiled and exercised through the forced
route on Windows, with no native Mac or installed-Yak interactive validation.
No version bump, commit, push, installation or publication is part of this work.
