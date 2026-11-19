# Scope-first capture v2

Owner decision, 2026-11-19. Implementation worktree `t3/scope-first-capture`, rebased by
fast-forward onto `e172205` (beta.10, including PR #66 mesh face cropping) for the original
measurements below. The deterministic follow-up integrates `4159e1b` (#67 trimmed-face section
certificates). No installation, commit, push or publication. Original evidence:
`~/.t3/m2d-plus-models/evidence/scope-first/v2/`; follow-up: `v2-attribution/` beside it.

**The original threaded zero-difference locus/class gate fails.** V2 is faster in the measured cropped
cases, but this evidence does not meet the owner's correctness acceptance bar. Cut regions,
hatches and frames match throughout. Main's own fresh threaded solves also fail the same
locus/class gate in diagnostic controls; that is evidence of native repeat variance, not a
waiver for v2 or proof that all v2 differences have the same cause. The deterministic
follow-up below isolates the remaining camera effect.

## Contract and implementation

Before capture, reject a source only when its cheap conservative bounding box is strictly
separated from every requested view volume. A tolerance plus 128-epsilon arithmetic margin
keeps touching, straddling and invalid boxes. Profile XY separation applies to all sources.
Wholly foreground sources are rejected only if they participate in the cut; back-face
separation also requires far clipping. Nonparticipants remain for foreground Cut/Hatch admission.
The engine's physical section clipping and exact per-view culling remain in place.

Scoping requires every view to be a source-defined finite volume: an M2D+ view box, a GH Box,
or a rectangle with authored axes and explicit depth. Named views, unbounded rectangles,
clipping planes without a crop, input-derived/scene-faced frames and mixed bounded/unbounded
requests keep the whole scene. Box geometry remains authoritative for depth. Authored auto
and explicit zero remain distinct capture keys for rectangles.

The bounds census, census caches, exact whole-scene bound overrides and background census
conversions are removed. Scene bounds are the captured survivors' bounds. Worker transport
remains main's whole captured scene with its shared revision, at schema 4; it has no special
bounds or drawing-frame payload. Mesh face crop remains intact in SolveView and policy.

Document geometry uses Rhino's non-tight bounding boxes before section/style capture,
fingerprinting, copying or normalisation. GH primitive values use value-type boxes before
conversion. Instances are tested before expansion; contained instances skip leaf tests,
and straddling instances use leaf boxes. Definition-local boxes are rebuilt once per capture
from current member boxes, recursively through nested instances, with conservative transformed
AABBs. No persistent Rhino instance bbox is trusted after an inner-definition edit. Definition
membership and excluded references remain watch dependencies; moving into a box reruns capture.
A rejected whole instance counts once in Info: "excluded N objects outside the view boxes
before capture". Additional per-view culls have a separate count.

## Consumers of survivor bounds

| Consumer | Why scoped views are independent |
|---|---|
| NativeViewport | Camera radius/near/far distances can change; this is the permitted difference. |
| ViewRequest.Resolve | A source crop returns its own fixed plane and rectangle, including an empty survivor set. |
| SolveView far-clip synthesis | Scoped views already have a Cut; scene-derived named/direction far clips are ineligible. |
| SectionDrawing and MeshViewCrop autoDepth | Scoped views have non-null source/effective depth, so autoDepth is unused. |
| GH SceneDepth/settings resolution | Box depth comes from SourceDepth; Rectangle requires an authored explicit distance. SceneDepth cannot replace either. |
| Fade/beyond | ViewRange uses fixed effective Depth and Beyond, independent of survivor bounds. |
| DrawingFrames | A source crop returns the fixed frame before the input-bounds inference path. Target/Source remain placement only. |

## Validation and measurements

Reporting series: `timings-final/`; machine Rhino 8.36.26296.17001, CLR 8.0.34 x64,
32 advertised logical processors, threaded HLD, one owned host and no drawing workers.
Production source baseline is the exact `e1722056f17483e13651fcf4994fd1a69f140528` fetched and
fast-forwarded in Step 0. `main-source-proof.json` checks all 107 production files: only
archive line endings differ. The shared origin/main ref advanced during this task; those
later changes are not in this implementation or its baseline.
The initial timing series in `timings/` precedes the final watch/primitive clean-up and is
diagnostic only. `timings-final/` is the reporting series. Main production sources in
`main-source/` are an archive of `origin/main`; only the shared research harness is added.
The owner's project checkout is not used for builds or edits.

ABBA order is main, v2, v2, main, one measured run after a warmup per process: two measured
runs per state. Cold uses a fresh SceneCache after JIT/native warmup. Warm reruns first
capture and draw with the same cache, then edit an extrusion on the top storey outside the
plan/elevation volume and observe the watch before timing recapture and drawing. No result
cache is used. The generated ten-storey building contains 1,170 sources: slabs, columns,
facade walls around openings, side walls and interior objects, mixing Breps, Extrusions
and ten SubDs. The files and generation manifest remain outside git. Adjacent members touching a box face
are retained, as required. `multi-storey.3dm.generation.json` records the model SHA256.

UI capture is constructor plus summed Step active time and the synchronous portion of
AcceptAsync. Maximum slice means maximum Step; constructor and acceptance callbacks are
also retained separately. Capture plus drawing includes background acceptance and the
1 ms yield between incomplete slices; startup, model I/O, initial watch creation, archive
writes and teardown are excluded. Timing runs use one in-process headless host, no workers.
CPU time covers the measured capture and drawing interval. Peak commit/working set are process
lifetime peaks, including startup and warmup, not interval-only peaks.

All native work is serial: at most one owned Rhino.Inside process at a time. No interactive
Rhino is attached to, terminated or changed. No curved-M or campus model is opened.


### Final ABBA results

Two measured runs per state, after warmup. UI capture and total are arithmetic means;
worst slice is the maximum observed Step across both runs, not a hard callback bound.

| Case | UI capture ms, main / v2 | Worst slice ms, main / v2 | Capture + drawing s, main / v2 | Sources, main / v2 |
|---|---:|---:|---:|---:|
| 10-storey plan, cold | 136.3 / 34.7 | 16.5 / 16.0 | 1.503 / 1.045 | 1170 / 184 |
| 10-storey plan, warm outside edit | 171.2 / 29.1 | 48.3 / 16.0 | 1.312 / 0.863 | 1170 / 184 |
| 10-storey elevation, cold | 117.6 / 24.0 | 16.6 / 15.9 | 0.650 / 0.351 | 1170 / 68 |
| 10-storey elevation, warm outside edit | 183.0 / 31.4 | 68.1 / 16.0 | 0.763 / 0.348 | 1170 / 68 |
| perf-M tenth crop | 262.8 / 152.8 | 65.4 / 31.5 | 4.227 / 3.599 | 5000 / 804 |
| Unbounded named view | 159.4 / 162.2 | 22.0 / 22.7 | 5.364 / 5.173 | 1170 / 1170 |

Observed capture-plus-drawing savings are 30.5% / 34.2% for cold / warm one-storey plans,
46.1% / 54.4% for cold / warm elevations, and 14.9% for the perf-M tenth crop. Capture wall
time also decreases in all five cropped cases. This v2 series **is faster than main on the
measured cropped and storey cases**, while failing the separate correctness gate below.

The unbounded named view retains all 1,170 sources. Its UI capture rises 159.4 -> 162.2 ms
(+1.7%), capture wall time rises 289.3 -> 297.1 ms, and total falls 5.364 -> 5.173 s.
The total ranges overlap (main 5.019â€“5.708 s; v2 5.096â€“5.249 s). These n=2 observations do
not establish a strict no-regression claim for unbounded capture. The worst v2 host callback
is 32.4 ms on perf-M; a strict 16 ms UI budget is not demonstrated.

### Locus, class and region gate

All 338 allowed view descriptors and 20 explicit-depth section crops are replayed against
beta.10 main. Curved-M and campus are excluded. There are 345 nonempty drawing pairs and
13 empty controls. All model hashes match. Reference and candidate solves are fresh; saved
single realizations are compared, not sorted archive bytes.

The independent research sampler uses an RTree, arc-length spacing max(0.01, 10 x tolerance),
at least 33 points per curve, and **1.25 x tolerance** for matching. Every accepted target
must match Class, Profile and SolvedClass simultaneously. Points are included. Tiny degenerate
curves whose normalized-length evaluation fails use parameter sampling only when their length
is at most the drawing tolerance; 2,418 samples use that fallback and zero remain invalid.
Three saved-archive self controls (facade, the degenerate curved-S case and perf-M) have zero
missing/extra samples. No matching tolerance or classification rule is widened.

| Corpus | Cases | Failing pairs | Strict missing samples | Strict extra samples | Exact frames / cut regions and hatches |
|---|---:|---:|---:|---:|---:|
| View descriptors + section crops | 358 | 266 | 676,113 | 675,812 | 358 / 358 |

Total bidirectional samples: **161,592,350**. Of the strict failures, 1,244,356 samples have
wrong-class evidence, 58,341 wrong-Profile evidence and 27,188 wrong-SolvedClass evidence.
These diagnostic categories overlap the strict missing/extra counters; they are not disjoint
counts of curves. All 20 section crops pass. **The requested zero missing/extra/class bar
fails for the view suite.** See `locus-complete/summary.json`, the per-case `locus.json` files
(including mismatch examples), and `locus-complete-summary.json`.

The six benchmark drawings are also compared: four fail the strict linework gate, while all
frames and complete cut/hatch archives match. They contribute 332,680 samples and 5,804 strict
failures (`benchmark-locus/`). Faster measured wall time is not an output-equivalence claim.

### Diagnostic controls

Facade case 0: three main and three v2 solves have identical ordered native input hashes
(`ABBAEEA2...D04297`), while captured source counts are 33 / 20 and bounds differ. Fresh main
repeat 0 versus repeats 1/2 has respectively 32/31 and 60/65 strict missing/extra samples.
The three main/v2 controls fail too. This demonstrates repeat variance with unchanged inputs;
it does not isolate the contribution of a changed camera radius.

Perf-M grid 00 case 0 is a control with **no exclusion**: main and v2 both retain 5,000 sources,
keep bit-identical bounds and have the same ordered native input hash (`6CFDF158...8CBF50`)
in all four solves. Main repeat 0 versus repeat 1 has 17,974/15,169 strict missing/extra
samples. Main/v2 controls have 14,278/14,822 and 15,871/16,286. Thus this gate can fail with
no scoping or camera-bounds change at all. The same-archive perf-M control has zero failures.
See `controls/`, `main-repeat-locus/`, `perf-main-repeat-locus/`, `control-locus/`,
`perf-control-locus/` and `perf-control-inputs.json`. These controls do not turn the failed
acceptance gate into a pass; the later single-threaded follow-up below resolves camera
versus exclusion attribution.

### Build and licensed gates

Debug and Release solution builds: zero warnings/errors. Forced local hook: 506 Core and
76 GH UI-model tests pass. Final licensed selections pass **57 tests on each of .NET 8 and .NET 10**, with the one
opt-in mesh timing theory skipped in each. They include 33 ScopeFirstTests (including the
deep-survivor camera-radius control), actual GH reruns, WorkerCodecStateTests and
MeshViewCropTests. `M2D_EXPECT_RUNTIME` asserts the actual runtime; the saved mesh test
fingerprints confirm CLR 8.0.34 and 10.0.15 with the same Rhino build/revision and RhinoCommon.
See `gates/final-net8.trx`, `gates/final-net10.trx`, their logs, the build logs and
`gates/forced-hook.log`. Bench and main bench builds also have zero warnings/errors.

All 358 main and 358 v2 solves finish. The first main harness invocation stopped after
338 descriptors and two section crops because an empty document had no bbox corners.
The research harness now supplies a fixed finite fallback crop for an empty document and
resumes only missing cases; all earlier results are preserved. The initial sampler stopped
on a degenerate normalized-length evaluation; `locus-complete/` is the completed corrected
sampler series and `locus/` retains that initial diagnostic series. No production behavior
is changed to accommodate those harness errors.

## Deterministic attribution (2026-11-19)

Current main `4159e1b` was fetched and fast-forwarded before restoring v2. The HLD policy
retains both `trimmed-slab-v2` and `scope-first-v2`. Internal `NativeSettings.MultipleThreads`
(default `true`) and `CameraBounds` (default `null`) control research computation and the
HLD viewport only; neither is a public setting. Production keeps threaded HLD and survivor
bounds. One owned headless process at a time; no curved-M or campus.

All 358 pairs replay on CLR 8.0.34 / Rhino 8.36.26296.17001. Exact geometry/class/region/frame
identity passes directly; nonidentical pairs use the unchanged 1.25 x tolerance sampler.
There are 39,402,233 sampled points in the raw nonidentical pairs. No tolerance or class,
Profile or SolvedClass rule is widened. Main's 107 production files are checked against the
commit; instrumented source patches, hashes and executed binaries accompany the evidence.

| Single-threaded comparison | Pairs | Failing | Strict missing / extra samples |
|---|---:|---:|---:|
| Main repeat 1 / repeat 2 | 358 | 0 | 0 / 0 |
| Main / v2, objects excluded | 261 | 112 | 44,756 / 46,847 |
| Main / v2, none excluded | 97 | 0 | 0 / 0 |
| Main / v2, main camera bounds pinned | 358 | 0 | 0 / 0 |

Main repeats and all pinned results have exact geometry/class/region/frame identities.
All 358 main/v2 ordered native input hashes match. All frames and complete cut/hatch archives
match. Raw wrong-Class / wrong-Profile / wrong-SolvedClass evidence is 71,219 / 3,477 / 12,112
samples (overlapping diagnostic categories). **All 112 strict failures vanish with only the
camera bounds pinned; no exclusion-caused difference remains and no culling bug was found.**
Per-case counts, exclusion split, bounds and input equality are in `main-repeat-cases.csv`,
`v2-cases.csv`, `pinned-cases.csv` and `summary.json` under `v2-attribution/`.

The cheap radius prototype unions live drawable objects' non-tight cached geometry bboxes,
without conversions or geometry copies. It fails 2 of 4 targeted controls: facade case 0
has 66 / 15 strict missing / extra samples, and curved-S case 0 has 206 / 283. Steel case 18
passes; the equal-bounds space-frame case 0 is an exact positive control. All four native
input hashes still match main. Facade's radius is 27.217909739225913 for main versus
27.229533850111313 for the cheap bbox: conservative bounds do not preserve native numerical
classification. This falsifies the option; no full cheap-radius corpus claim is made.
`cheap-radius-witnesses.json` carries retained source identities, sample locations and
class/Profile neighbours. There are no remaining exclusion-bug witnesses to fix.

The standalone extra host-thread bbox pass, with two initial passes then 20 warmed samples:

| Model | Objects | Warm mean ms | Warm min–max ms | First pass ms |
|---|---:|---:|---:|---:|
| Multi-storey | 1,170 | 5.83 | 3.39–15.21 | 19.21 |
| perf-M | 5,000 | 20.22 | 15.66–31.70 | 52.82 |

Startup/model I/O are excluded. The first pass does not imply empty native bbox caches.
This is a measured additional pass, not a production census or an integrated capture timing.
No production compute/capture speed change was made, so the v2 timing table was not rerun.

The first licensed run caught a disclosure-only bug: `NativeHldOptions` hardcoded a threaded
options/diagnostic/result flag although Compute already consumed the internal `false` setting.
It now honours the setting, including section-only results; tests cover live state, observer,
diagnostic and lossless codec state. Original evidence is preserved with
`threading-disclosure-erratum.json` and executed source/binaries. Only reporting changed after
the matrix; the native Compute arguments, viewport and geometry pipeline did not change.

Final Debug/Release and branch/main bench builds have zero warnings/errors. Forced hook:
506 Core and 76 GH UI-model tests pass. Corrected licensed selection: **65 pass and one opt-in
timing skip on each of .NET 8 and .NET 10**, including the original selection, the new internal
controls and current-main trimmed-slab tests. Actual runtimes are asserted. See
`gates/corrected-net8.trx`, `gates/corrected-net10.trx`, build/hook logs and mesh fingerprints.

## Remaining limits

- The production survivor-radius path still fails the strict locus/class bar in 112 cases.
  Pinned-camera equivalence isolates the cause; it does not make the current default
  correctness-accepted or merge-ready under that bar. The cheap non-tight bbox option is
  insufficient to preserve main's classification.
- Current main `4159e1b` is integrated and checked. The original v2 timing series remains
  historical beta.10 (`e172205`) evidence, not a new current-main timing claim.
- Corpus comparisons and timings are on .NET 8; the licensed selection covers both runtimes.

Cheap bbox/dependency enumeration still visits the original top-level objects and unique
block definitions. Conservative transformed AABBs can retain more leaves near rotated faces.
Native bbox/copy calls and final coherence verification are atomic; a hard 16 ms callback
bound is not promised. Two ABBA samples per state are observations on this machine, not a
statistical performance guarantee. Locus sampling is bidirectional fixture evidence, not a
whole-curve equivalence proof or a universal Rhino HLD certificate.
