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
