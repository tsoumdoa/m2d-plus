# PERF A — exact planar edge / face kernel

Branch: `hc/perf-a-edgeface`, based on `origin/m3/wiring`. No commits or pushes.
Implementation is experimental and disconnected from the production native route.

## Protocol and score

The fixed manifest is recorded in `artifacts/hc-perf/iteration-0/manifest.json`.
It contains all 183 required view rows including XS correctness cases (XS is not
in the primary). Primary: geometric mean of nonempty planar-subset extraction,
index, solve and deduplicated output wall time. Native time must use the **same
planar subset**, measured back to back on this machine; old mixed-model native
timings are reference context only, never a substitute for the ratio.

Each scored run appends to [hillclimb/scores.jsonl](hillclimb/scores.jsonl).
No incomplete or failing run is eligible as a winning result. Guards that stop an
oracle fail the gate. No feature or model is removed to improve a score.

| # | Hypothesis | Change | Gate | Primary | Secondary | Kept? |
|---|---|---|---|---|---|---|
| 0 | Exact planar clipping plus projected spatial filtering can beat native HLD | Own Core interval-filter/rational kernel, independent brute-force oracle, BVH, output union, adapter and full scorer | Failed: six unnamed-block fixture crashes; native unavailable; remaining crop checks stopped after diagnosing fixture failure | Invalid (six missing timings) | All 183 rows attempted; 15 perf pair gates passed | No eligible result |
| 1 | Repair the baseline harness so every required fixture can be measured | Null-safe block source names; reference-only scaffolding also parallelizes independent edges (still every pair) and checks raw crop intervals | Incomplete: stopped during L for deduplication correctness review | Invalid (incomplete) | No result rows reused in the next attempt | No eligible result |
| 2 | Certify canonical output before measuring an eligible baseline | Complete support extent, output roundoff budget, exact source endpoints and visible-junction precedence | Oracle/metamorphic gates pass; **overall failed: native gate unavailable** | **0.00921956 s**, 132 nonempty primary views | Perf tiers alone: 4.35176 s GM; observed full-worker managed/private peaks 2.24 / 3.68 GiB; native ratio unavailable | Ineligible as a winner; candidate left for review |

Final run: all 183 rows completed without crashes, one engine hash throughout.
All 165 requested exact-oracle view checks passed, plus all 16 M crop tiles,
15 perf blocks/exploded locus comparisons and four rounded rigid-transform
drawing probes. Per-view face permutations and exact rigid rotations also passed.
The six XS views are correctness-only. Empty curved-only/unsupported subsets do
not contribute to the planar primary; every exclusion remains in the row data.

Native work was rejected before initialization whenever the shared suite mutex
was tested, including the final check. All 12 required M/L native comparisons
remain unavailable, as do 153 supplemental fixture/feature comparisons. These are
explicit failures in the score. No native speed ratio, passed overall gate or
eligible winning score is claimed. Runs 0 and 1 have invalid/incomplete primaries,
so there is no defensible improvement ratio against them. No performance
optimization iteration or ablation was accepted; work stops at the external
native-validation dependency, not at a demonstrated performance plateau.

Observed work contribution, without a timing ablation: M blocks SW evaluated
3,230,637 edge/face pairs versus a dense bound of 1,712,858,400 (~530× fewer).
Interval arithmetic resolved 78.6% of those pair checks without a rational
narrow-phase fallback. BVH filtering and complete-coverage early-out jointly
provide the pair reduction; their individual wall-time contributions are unmeasured.

The separate curved-suite census covers 14 models / 42 views in
`artifacts/hc-perf/mixed-suite.json`. It records exclusions and whole-view native
routing, with no mixed timing claim. Final evidence and binary/source hashes are
in `artifacts/hc-perf/iteration-2/score.json`, `summary.json`, and `provenance.json`.
All memory figures are observed **whole-worker** peaks including validation,
not isolated production-solver allocation maxima.

Final verification: Debug/Release solution and benchmark builds have zero warnings;
197 Core and 40 GH UI tests pass; 94 licensed tests skip by default. The forced
local hook passes. Nothing was committed, pushed, installed or published.

Run 1 repairs baseline scaffolding; it changes no production clipping/index logic.
The reference's eight threads share no visibility or index code with production.
It counts every edge/face pair; a dedicated test compares serial and parallel
reference results and pair counts. The earlier completed crop probe visited
407 million pairs in one tile; reference parallelism keeps the full gate practical.

No optimization iteration has been retained. Until the baseline passes the
correctness gate there is no valid primary improvement to hill climb. Preparatory
diagnostics are not scored iterations. Their S/M sub-results are not a best score.

## 81% axonometric-length mystery: resolved metric error

The old native benchmark sets `HiddenLineDrawingParameters.Flatten = false`.
It transforms each extracted curve by a **rigid** world-to-view transform and
then calls `GetLength()`: view-space Z (depth) is retained. The SVG writer keeps
only X and Y. Fable measured XY projected length. These are different metrics.

For the axis-aligned building edges in the SW basis, the projected length is
`sqrt(2/3) = 0.81649658` times the 3D length. Re-measuring every visible path in
the archived native SVG gives:

| Model | Reported native (3D m) | Native SVG (XY m) | XY / 3D |
|---|---:|---:|---:|
| M blocks SW | 5,970.5634 | 4,874.2979 | 81.6388% |
| L blocks SW | 19,252.8631 | 15,717.0615 | 81.6349% |

Fable's report gives 4,860.2082 m / 15,623 m projected, so the corrected length
ratios are about 99.71% / 99.40%, not 81%. A diagnostic artifact-only copy with
its closed-boundary convention returned 4,856.9903 m for M. Against archived M
SVGs, sampled at 10 mm and compared within 0.1 mm, it misses ~125.17 m and adds
~106.31 m of projected visible loci; ~116.15 m of native visible loci have no
prototype candidate at all, and ~9.02 m have candidates with different visibility.
These differences are **not** established to be all curved silhouettes. They
include the prototype's approximate occluders, omitted mesh/SubD candidates,
trim sampling and tie behavior. The length-metric explanation resolves the 19%
headline; it does not validate either engine or explain away locus disagreements.

Evidence: `artifacts/hc-perf/foreshortening.json`,
`artifacts/hc-perf/fable-probe/M-analysis.json`, and the original
`tools/models/native.py` length/extraction code. Product code contains no Fable
code; `tools/hc-perf/fable_probe.py` instruments only an artifact copy for this
diagnosis.

## Domain and gate details

See [tools/hc-perf/README.md](tools/hc-perf/README.md) for arithmetic, eligibility,
hole/boundary/contact rules, coincident ownership, mixed-view fallback, guards,
memory sampling and reproducible commands. Raw per-edge intervals are retained;
the deduplicated locus output is a separate authoritative drawing representation.

The rounded-translation preparatory stress check exposed contact sensitivity in
raw candidate intervals and microscopic hidden junction residues in canonical
output. Run 2 absorbs only tolerance-small hidden junction/corner residues under
visible precedence, preserving isolated short features. Every view still checks
an exact signed-axis rotation, and M North/SW in both forms also require rounded
rotation/translation equivalence of canonical drawing loci. Preliminary M blocks
North/SW probes pass. The full gates, including any failures, remain authoritative.

## Next hypotheses, conditional on passing the baseline gate

1. Cache definition-local extraction and exact support-plane coefficients; seed
   interval filters with certified coefficients, including exact zero components.
2. Replace per-edge candidate sorting/allocation with a front-to-back BVH walk.
3. Maintain hidden coverage incrementally instead of sorting it after every face.

These have not been scored or retained. Native locus disagreements must be
classified and exact oracle/pair/tile gates must pass before accepting any speedup.


## A2 native validation and climb (`hc/perf-a2`)

Owner override: native validation is correctness-only. Owned Normal-priority
Rhino.Inside processes run serially without the suite timing mutex; native is
never benchmarked. Rhino 8.36.26296.17001 / .NET 8.0.34, multipleThreads=false.
All 165 native cases use exactly the measured kernel's planar-only census and
exclusions. Curved-only empty subsets have empty output on both sides; Rhino is
not called with an empty HLD parameter list. Unsupported exclusions remain explicit.

**Baseline gate passes.** Evidence: `artifacts/hc-perf/native-a2/gate.json`.
All failed canonical samples are accounted for: **28,159 native-quirk, 167
tie-convention and 723 tolerance samples; zero kernel bugs and zero unexplained**.
These are sampled disagreements, not counts of independent geometric defects.
The 12 required M/L cases account for 20,983 / 99 / 604 of those samples,
respectively. Raw native output and visible-over-hidden drawing loci are retained
separately. Sampling stays bidirectional at model tolerance, at least eight
samples per segment and at most 50 mm physical spacing.

Every exception has source/XY/depth/face or native-slot evidence. Clear-front and
interior-blocker exceptions additionally require the independent exact reference
on all disputed candidate supports, retaining every potentially occluding face.
Duplicate witnesses require coincident XY/depth and an exact-visible source;
ownership may be any coincident front source under the drawing-locus convention.
The focused M SW probe proves 769 real Unset records with valid parent/proxy,
GetLength=0 but non-point bounds/endpoints. They are native ambiguity, not benign
empty slots; none is promoted to Visible. Its 1,735 native-quirk samples include
1,579 direct Duplicate witnesses, 147 direct Unset witnesses and nine clear-front
witnesses. Probe: `artifacts/hc-perf/unset-probe/`. Initial and fresh serial M/L
native visible/hidden arrays are identical; no threading exception was needed.

Validation-harness repairs: physically scaled indexing for mm/ft, explicit empty
subsets, and drawing-distance adjacency to exact intervals. The last offset
sample was 5.7423e-11 model units from its reference interval, at depth gap
0.000100000063 versus tolerance 0.0001. No kernel clipping rule changed for it.
Split/line discrepancies within the combined 2.1-tolerance representation budget
are recorded with their actual distance; nearer boundary/depth contacts retain
face index and gap. Coverage itself still uses one model tolerance. The final
complete audit was contained in its own 16 GiB / 30 minute job.

Iteration 3 retrospectively certifies the unchanged climb-A full measurements.
All 12 freshly extracted M/L locus files are byte-identical to the measured files.
All 183 rows, 165 reference checks, 16 crop tiles, 15 pairs and four rounded-rigid
checks remain in the gate. Source hashes and every native/kernel model hash,
edge/face census and exclusion list are checked. Validated primary:
**0.009219560787 s** over 132 nonempty views; perf-only GM: **4.351761153 s**
over 24 views. Score: `artifacts/hc-perf/validated-baseline/score.json`.

Archived native timings are from the owner-specified `test/model-suite` baseline.
They include curved geometry and are labelled contextual mixed-native compute /
complete planar-kernel time, not a same-subset timing claim. The archive has no
uncut Top or XL North/SW rows; cut views are never substituted. Baseline tier
kernel GM: S 0.404990, M 2.604339, L 11.179092, XL 30.416676 s. Matching archived
North/SW ratios (four rows/tier): S 4.538x, M 4.443x, L 4.580x. All per-view
ratios are in the score; Top/XL remain unavailable.

Scored candidates reuse hash-verified native inputs/output, and rerun every
kernel/reference/native, pair, crop and rigid gate. Native cache data is copied
into each iteration with provenance; native HLD is never re-timed. A candidate
wins only if the full primary improves and the full gate passes. Stop at three
consecutive non-improvements or eight speed candidates.

| # | Hypothesis / change | Full gate | Primary (s) | Perf GM (s) | Kept? |
|---|---|---|---:|---:|---|
| 3 | Complete native validation of unchanged climb-A output | Pass, documented exceptions | 0.00921956 | 4.35176 | Validated baseline |
| 4 | Certified cached exact plane coefficients for interval filtering | Pass | 0.008683216 | 3.913391 | Yes |
| 5 | Front-ordered BVH walk, individual face bounds and stack-owned edge visitor | Pass | 0.008822072 | 4.110512 | No |
| 6 | Incremental hidden union and lazy clip results; repair scale-aware locus grouping | Pass | 0.008458538 | 3.745575 | Yes |
| 7 | Individual leaf-face bounds with original global depth ordering | Pass | 0.008194989 | 3.434379 | Yes |
| 8 | Prefer ordinary certified filtered planes; seed exact coefficients on preparation ambiguity | Pass | 0.007713360 | 3.253379 | Yes |
| 9 | Exclude exactly edge-on validated faces from the BVH | Pass | 0.007734532 | 3.115846 | No |
| 10 | Avoid parallel scheduling below 128 candidate edges | Pass | 0.008568694 | 3.237433 | No |

Iteration 4 improves the primary by 6.18% (perf GM 4.35176 -> 3.91339 s).
It adds a subnormal-area regression in both windings; all 198 Core tests pass.
Iteration 5 removes per-edge candidate materialisation/sorting, traverses nearer
child bounds first with sorted leaves, and rejects disjoint individual face bounds.
These traversal/allocation changes are scored together, with no separate ablation
claim. Only complete hidden coverage or Unresolved can terminate an edge walk.

For modest validation cost, iteration 5 also starts retaining exact crop truth.
Later runs can reuse only expected raw intervals with identical input and oracle
source fingerprints and a passed source score checking all 16 payload hashes.
Each new kernel still recomputes and checks every crop. The independent oracle's
full drawable-edge x face count is retained. Point-projecting edges have no curve.
Fresh/reuse/changed-code/changed-input cache probes pass; benchmark timings are
never reused. Tile/rigid supervisor failures now explicitly fail the gate even
if stale output files exist. Iteration 5 computes all 16 references afresh.


A separate supported-scale union bug was reproduced during iteration 5 review:
parallel Visible/Hidden segments of length/separation 1e-137 at tolerance 1e-150
collapse to one Visible locus. The fixed 1e-10 grouping floor and unit extent
floor ignore the drawing tolerance. This is outside the benchmark-size native
mismatches (their kernel-bug count stays zero), but must be repaired and given a
regression before finalizing. Reproduction: `artifacts/hc-perf/scale-probe.json`.
The current scored engine remains immutable until iteration 5 completes.
The native interior-blocker exceptions were additionally checked in 3D: their
minimum perpendicular plane clearance is 40.8163 model tolerances, above the
combined representation budget, rather than near-depth tolerance contacts.

Iteration 5 passes the complete gate but is 1.60% slower in the primary (perf GM
also regresses), so its traversal changes are restored to iteration 4. Its exact
crop references remain valid: all 16 were computed by the independent oracle and
the complete source score passed, even though the performance candidate lost.
Iteration 6 reuses those expected intervals under the checked cache protocol.

The scale defect is repaired in iteration 6 by removing absolute/world-unit floors
from support grouping, scaling the bucket/angle budget to tolerance and actual
extent. A three-scale analytic regression proves separate Visible/Hidden supports
at 1e-137, 1 and 1e137; the tiny case failed before the fix. All 201 Core tests now
pass. This is one fixed pre-existing kernel union bug outside the native benchmark
sizes, not an unexplained native disagreement or a performance exclusion.
The speed hypothesis keeps hidden intervals sorted incrementally and allocates a
clip-result list only when a pair emits a hidden interval. Predicates and the
independent oracle are unchanged. Correctness repair and allocation changes are
recorded together; no isolated ablation is claimed.


Iteration 6 passes all gates and improves the primary by 2.66% over iteration 4;
perf GM is 3.745575 s. All 16 crop results are freshly solved and compared with
hash-verified exact intervals; all 16 references are reused. The scale repair and
201 passing Core tests stay in the retained source. Native sample classes/counts
are unchanged. Iteration 7 isolates individual leaf-face bound filtering while
retaining the original global front-to-back sort, an ablation of iteration 5's
rejected traversal bundle.


Iteration 7 passes every gate and improves the primary by 3.22%, perf GM to
3.434379 s. Filtering disjoint leaf faces works with the global depth order:
M blocks SW 3.551 s versus 4.519 s in iteration 6; L blocks SW 14.004 versus
17.607 s. This isolates the useful part of iteration 5's rejected bundle.
Iteration 8 tries ordinary interval preparation first and uses the cached exact
coefficient seed only when that preparation is indeterminate. Plane validation,
certified interval signs and whole-pair rational fallback are retained.


Iteration 8 passes the complete gate and improves the primary by 6.24% over
iteration 7; perf GM is 3.253379 s. Interval-only preparation is used only when
certified, with exact seeding/rational fallback unchanged on ambiguity.
Iteration 9 leaves validation and the source census intact but indexes only
faces whose exact rational normal has nonzero Z. No epsilon decides projected
area; the subnormal-area regression remains green. All 201 Core tests pass.


Iteration 9 passes all gates and improves perf GM to 3.115846 s, but the primary
regresses by 0.27% versus iteration 8. It is rejected under the fixed full-primary
protocol and iteration 8 is restored. No edge-on/index census change is retained.
Iteration 10 tests single-thread execution below 128 edges to reduce scheduling
cost on small drawings; all other scenes retain the requested eight threads.
All 201 Core tests pass before the scored run. Consecutive non-improvements: one.


Iteration 10 passes every gate but regresses the primary by 11.09% versus
iteration 8. It is rejected and iteration 8 restored. The tiny-scene scheduling
hypothesis does not improve this fixed corpus. Consecutive non-improvements: two.
Iteration 11 is the eighth and final speed candidate: reduce BVH leaf capacity
from eight to four faces, retaining individual leaf bounds and global depth order.
Stop after this trial regardless of outcome under the eight-attempt budget.

| 11 | Four-face BVH leaves | Pass | 0.007842237 | 3.284689 | No |


## A2 final verdict

**PASS with evidenced native exceptions.** All 183 rows completed; 165 native
planar-only comparisons (including the required 12 M/L views), 165 exact-reference
views, all 16 M crop tiles, 15 blocks/exploded pairs and four rounded-rigid probes
pass. Native failed-sample classes remain 28,159 native quirks, 167 tie conventions
and 723 tolerance artefacts; no native-disagreement kernel bug or unexplained
sample remains. One separate supported-scale locus-union bug was fixed and tested.

Iteration 11 passes the full gate but is 1.67% slower in the full primary than
iteration 8. It is rejected. Stop: eight speed candidates and three consecutive
non-improvements (9, 10, 11). Retained steps: **4, 6, 7, 8**; final source restored
to **iteration 8**, including certified plane seeding, incremental coverage/lazy
clips, individual leaf-face bounds, and ordinary filtered preparation before exact
seeding. The ordered recursive traversal, thread threshold, exact-edge-on index
exclusion and four-face leaves are not retained.

Full primary: **0.007713359902 s** GM over 132 nonempty primary views, **1.19527x**
the validated baseline throughput. Perf tiers: **3.253379319 s** GM over 24 views,
versus validated 4.351761153 s (**1.33761x** throughput). Improvements are measured
on the shared host under the recorded protocol; no native timings were taken.

| Tier | Kernel GM, six views (s) | Archived native / kernel GM ratio, four North/SW views |
|---|---:|---:|
| S | 0.325413 | 5.88226x |
| M | 1.923745 | 6.87665x |
| L | 8.324625 | 6.81844x |
| XL | 21.497705 | Unavailable |

These archived ratios are contextual mixed-native compute / planar-kernel total,
not same-subset timing. Uncut Top and XL archive rows are absent. Per-view ratios
are in `artifacts/hc-perf/iteration-8/score.json` and `final-report.json`.

Final Debug/Release solution, benchmark Release and reader Release builds have
zero warnings/errors. Core 201 and GH UI 40 pass; the 94 licensed xUnit tests skip
by default. The forced local hook passes. `git diff --check` is clean. A forced
reader Release rebuild reproduces measured Core SHA-256
`2a0397b4744099bc96d389b07c3e0b8013e5ee9ceb0491f1e536ba00ca86a464`
byte-for-byte after solution-build assembly bytes differed; every visibility
source matches the winning snapshot. Preserve that rebuild when reproducing the
measured binary. Logs and both observed hashes: `artifacts/hc-perf/final-checks/`.

Evidence: `artifacts/hc-perf/native-a2/gate.json`,
`artifacts/hc-perf/validated-baseline/score.json` and its copied original evidence,
`artifacts/hc-perf/iteration-8/`, `artifacts/hc-perf/snapshots/`,
`artifacts/hc-perf/final-report.json`, plus `hillclimb/scores.jsonl`.
Branch `hc/perf-a2`; HEAD remains `342bd47d4f7fbdaae75ceb876e726715b862307e`.
Nothing was staged, committed, pushed, installed or published. Only owned native
processes were used; other people's Rhino processes were never touched.
