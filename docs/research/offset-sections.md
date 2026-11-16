# Offset plan sections and ten-view throughput

2026-11-16; implementation worktree from `df039a2` (v0.1.0-beta.2).
Nothing committed, pushed, installed or published. Rhino 8.36.26296.17001,
Windows x64; performance measurements use Release and .NET 8.0.34.
The prescribed `native-curved-hld.md` is absent from this checkout; the other
requested studies, worker design and section-fix report were read.

## Findings

**There is a real offset-sensitive section-validation defect.** Native face
intersections sometimes omit their shared-edge overlaps on translated boxes.
The continuous `CoveredBySharedTopology` fallback then recursively trims a long
edge toward tolerance-sized boxes. A stack of our owned profiling process caught
16 nested `Cover` calls below `ValidateFaceIntersections` / `CanonicalVerdict`.
The fallback has a 262,144-node budget, distinct from the slab certificate's
20,000-patch / depth-16 budget. Exhaustion rejects the source; it does not prove it.

The first fixture's first-view section phase was 8.371 s at origin, 12.403 s at
100 m and 38.128 s at 1 km. Its trim phase was only 0.828 / 0.924 / 1.073 s;
native HLD was 0.387 / 0.408 / 0.409 s. Thus the initial offset spike was in
validation, not trim or HLD. An exact replay of the saved 1 km model and source
identities isolates validation:

| Same saved 1 km model | Summed validation s | False refusals | Slowest source s |
|---|---:|---:|---:|
| Before | 36.723 | 23 | 0.941 |
| Local validation + exact local-archive reuse | 0.383 | 0 | 0.028 |

The model has valid independent rectangular columns; native overlap loss caused
the refusals. A separate regression also reproduced an offset round-column
trim-edge attribution refusal. Both paths now compute on local owned geometry.
No shared-topology or slab proof, tolerance, depth limit or work budget was relaxed.

**The final production worker path meets the requested throughput on the generated
fixture:** ten plans take 14.570 s cold at origin, 18.368 s at 100 m, and 17.578 s
at 1 km. Offset ratios are 1.261 and 1.206, below 1.5. The owner's actual model
was not supplied: these reproduce a relevant failure mechanism, not its entire
ten-minute solve or a universal runtime guarantee.

## Changes

- `SectionCoordinates` translates the section source copies, cut/crop planes and
  far plane by the negative cut-plane origin. The existing region/trim/cap/slab
  code runs unchanged in that frame. It restores every retained solid, contact,
  loop, hatch and independently owned provenance curve once before visibility.
  Source geometry and live documents are never transformed.
- Face-intersection validation uses a separate owned copy centred on its own
  bounds. This also covers definition-local/world standalone canonical geometry
  and direct section-engine calls. Excessive input coordinate roundoff is refused
  before translation; local coordinates cannot recover lost input precision.
- `SectionVerdictCache` retains its existing definition/scale/tolerance keys and
  adds a scene-owned cache keyed by SHA-256 of the **complete translated Brep
  archive**, plus tolerance. Exploded translations can therefore reuse an identical
  validation shape. There is no quantization, CRC-only key, sampled congruence,
  per-view cache invalidation or sharing of live native geometry. A 0.001 mm shape
  change remains a different entry even with 0.01 mm modelling tolerance.
- Route C's composed-output path now uses the same conservative spatial lookup
  for nearby cut loops as its native-output path. References remain in their
  original order; the existing overlap/provenance proof still decides subtraction.
- Workers handshake before claiming views. A slow startup no longer holds a view
  away from ready siblings. Once all views finish, unused startups are cancelled
  and their owned processes are reaped. Caps, admission, retries, hard cancellation,
  matching runtime/engine checks and complete-generation publication are unchanged.
- Info gains observational slab-patch/shared-topology-node and budget-hit counts;
  `section validation` is separately timed inside `sections`.

The prepared scene was already shared between views, and canonical verdicts already
survived view changes. There was no ten-view serial scheduling bug in the production
pool. Each process still prepares/receives its scene once and owns its own verdict
cache. The new exact-archive reuse reduces this fixture to nine actual local shape
validations per process, rather than 1,362 canonical source keys. A local first-view
validation takes 0.272 s; subsequent views use the settled verdicts.
Planar extraction is 0.54–0.59 s and kernel solving 1.01–1.11 s summed over all
ten local views. Their clipped faces depend on the plane; these small phases do
not justify another cross-view geometry cache. Coordinate copies are temporary
per-view owners and are disposed before visibility; prepared world geometry stays
shared by the scene. Hard worker caps and the original scene leases remain in force.

### Translation, classification and bytes

For the exact rigid map `T(p) = p - a`, translating a plane's origin by the same
`a` leaves `n · (p - o)` unchanged. Distances, incidence, winding, nesting, area,
normal directions, retained halfspaces and visibility ordering are invariant.
Tolerance remains in the same mm units. The implementation applies no scaling,
plane nudge, repair, membership guess or alternative certificate.

This is an exact geometric argument, **not** a claim that Rhino's floating-point
native operations commute byte-for-byte with translation. Validation copies leave
the original source archive byte-identical. Locally computed section output is
restored by the inverse transform, including provenance and hatches. Native trim
and intersection approximations can differ from the previous world-coordinate
operation; repairing the false refusals intentionally changes its output.
Native HLD, composition and the independent verifier still consume restored world
geometry: their old bytes and classes are not claimed invariant at every offset.
The large fixture's differing piece partitions/Unresolved counts are recorded below.
The independent box/round-column section controls retain their component classes,
loop topology and analytic areas at all three offsets. The drawing-line control
compares same-source/same-class collinear unions at 1e-7 mm resolution: a 1.5e-19 mm
origin-only arithmetic needle collapses to a point under the inverse transform.
No such comparison tolerance is used to promote a production classification.

**Worker/local equality remains strict:** all 60 final view payloads match after
excluding only diagnostics and wall/phase timings. No rounding, sorting or
geometric equivalence substitutes for the hash check. All geometry, depth geometry,
styles, attribution, topology, hatches, ordering and quality state remain included.

## Models and measurement

Generated models and all evidence are outside Git:

```
~/.t3/m2d-plus-models/offset/building-1700-v2-{0,100000,1000000}.3dm
~/.t3/m2d-plus-models/evidence/offset-sections/
```

`tools/worker-bench/OffsetBench.cs` generates the origin fixture in mm at 0.01 mm
tolerance. Ten storeys are 3,200 mm apart; plans are at 1,500 + 3,200*i mm. The
1,700 elements are 510 walls, 170 slabs, 497 rectangular columns, 170 round columns,
10 closed stepped stair flights, 170 door blocks, 170 window blocks and three
closed rational triaxial ellipsoids. Most elements are standalone copies, so the
test exercises exploded repetition as well as blocks. Offset files are translated
copies of the saved origin document, translating top-level placements only;
definition geometry is not translated twice. Each file has a `.generation.json`
with recipe, counts, source and SHA-256. Existing models are never overwritten.
The earlier v1 root-cause fixture is retained separately; it is not substituted
for v2 in the final matrix.

Local references run sequentially on one retained scene, using benchmark-only
admission. Production sets automatically use the ordinary worker pool. Workers
use default count/admission, automatic 2 GiB caps, native threading off and one
serialized host reply reservation. Cold batches admit seven/eight workers plus
4 GiB host transport, depending on current available RAM/commit. All views are
parallel whole-view work, returned in input order. Second passes are labelled
**resized repeats**, not warm: changing availability changed worker count and
processor allowance, so workers restarted. No count/cap override was used.

The final worker series ran serially with this task's other Rhino hosts stopped.
Other users/agents and OS caches were uncontrolled; these are n=1 observations.
Local references overlapped some acceptance work, so small phase differences do
not establish statistically controlled regressions. At most two owned non-worker
Rhino hosts were used; worker acceptance was explicitly allowed to exceed that.
Only known processes started by this task were profiled or stopped.

### Rejected benchmark evidence

Early worker timings were invalidated by a **benchmark sampler bug**, not a new
native-HLD finding. Its 25 ms timer allowed overlapping callbacks to block on a
lock around process-memory queries. A coordinator stack captured 52 such callbacks;
a child stack captured a compute thread blocked writing progress to that starved
coordinator. Apparent 10–14 s native phases and 67–132 s batch times were therefore
instrumentation-induced pipe backpressure. `WorkerMemorySample` now skips a sample
when the previous callback holds the lock. OS Job Object peaks remain available.
All pre-fix worker timings, including the four-worker controls, are excluded from
the final table; their successful hashes remain correctness evidence only.
The same v2 origin scene/requests/engine then took 14.570 s cold with the corrected
sampler. This sampler defect is not attributed to the owner's GH solve.

A separate real scheduling defect was demonstrated by a pre-startup view claim
waiting until its 120 s deadline after siblings ran out of work. The licensed
regression now stalls one startup for 60 s and requires its healthy sibling to
finish the complete matching batch within 30 s, with the unused process reaped.

## Final timing matrix

Times exclude capture, local baseline archive writes and the separately timed raw
section yardstick. Worker batch times include startup, handshake, scene transfer,
preparation, computation and compressed reply transfer/file writes. Strict hashing
is outside the worker timer. `section validation` is nested in `sections`; exact
fallback work is summed thread elapsed time nested in kernel wall, and ray-audit
work is nested in verifier wall. Worker phase sums are concurrent view observations,
not additive batch critical-path time.

<!-- Generated tables follow; full per-view phases are retained in offset-phases.csv. -->
| Offset | Route | Wall s | Workers | Strict hashes |
|---|---|---:|---:|---|
| origin | local | 47.398 | 1 | reference |
| origin | pool-10-0 cold | 14.570 | 7 | 10/10 |
| origin | pool-10-0 repeat (resized) | 12.442 | 8 | 10/10 |
| 100m | local | 52.123 | 1 | reference |
| 100m | pool-10-0 cold | 18.368 | 8 | 10/10 |
| 100m | pool-10-0 repeat (resized) | 15.444 | 7 | 10/10 |
| 1km | local | 59.224 | 1 | reference |
| 1km | pool-10-0 cold | 17.578 | 8 | 10/10 |
| 1km | pool-10-0 repeat (resized) | 17.227 | 7 | 10/10 |

Phase seconds summed over ten views (nested observations are not additive):

| Phase | Origin local | 100 m local | 1 km local |
|---|---:|---:|---:|
| composition setup | 0.238 | 0.204 | 0.206 |
| curve-plane events | 0.252 | 0.246 | 0.428 |
| interval union | 0.251 | 0.268 | 0.294 |
| kernel exact fallback CPU work | 3.171 | 2.527 | 2.583 |
| kernel solve | 1.112 | 1.068 | 1.011 |
| native extraction and duplicates | 14.858 | 17.873 | 21.797 |
| native ray audit work | 1.779 | 1.736 | 1.836 |
| native ray verification | 1.784 | 1.741 | 1.842 |
| native solve | 7.166 | 7.407 | 7.282 |
| normalisation | 0.065 | 0.081 | 0.070 |
| output construction | 0.513 | 0.467 | 0.455 |
| planar extraction | 0.582 | 0.592 | 0.544 |
| route C trim | 10.169 | 10.404 | 10.379 |
| section output construction | 3.560 | 4.401 | 7.946 |
| section validation | 0.281 | 0.397 | 0.354 |
| sections | 5.908 | 6.400 | 6.054 |
| tolerance census | 0.654 | 0.664 | 0.644 |

Cold worker phase seconds summed over ten views (concurrent, not batch critical-path seconds):

| Phase | Origin workers | 100 m workers | 1 km workers |
|---|---:|---:|---:|
| composition setup | 0.662 | 0.671 | 0.615 |
| curve-plane events | 0.302 | 0.281 | 0.504 |
| interval union | 0.524 | 0.521 | 0.565 |
| kernel exact fallback CPU work | 4.798 | 3.211 | 3.114 |
| kernel solve | 2.840 | 3.104 | 2.665 |
| native extraction and duplicates | 16.009 | 18.580 | 22.433 |
| native ray audit work | 1.980 | 1.935 | 2.038 |
| native ray verification | 2.008 | 1.965 | 2.070 |
| native solve | 8.064 | 7.963 | 8.261 |
| normalisation | 0.625 | 0.688 | 1.828 |
| output construction | 0.484 | 0.541 | 0.529 |
| planar extraction | 0.846 | 0.882 | 0.851 |
| route C trim | 11.461 | 11.915 | 11.295 |
| section output construction | 3.571 | 4.577 | 8.226 |
| section validation | 2.668 | 3.529 | 3.198 |
| sections | 9.370 | 10.761 | 9.852 |
| tolerance census | 0.722 | 0.731 | 0.727 |

## Certificate work and uncertainty

Totals over the ten local views; worker results have the same quality payloads.
Patch visits include initial Bezier patches and subdivisions. Exact fallbacks are
the planar kernel's rational predicates, not retries of the section engine.

| Offset | Exact fallbacks | Slab patch visits | Shared-topology nodes | Slab / topology budget hits | Unresolved / pieces | Regions |
|---|---:|---:|---:|---|---|---:|
| Origin | 85,268 | 240 | 0 | 0 / 0 | 84 / 1,875 (4.48%) | 1,525 |
| 100 m | 81,099 | 240 | 0 | 0 / 0 | 73 / 1,851 (3.94%) | 1,525 |
| 1 km | 81,099 | 240 | 0 | 0 / 0 | 109 / 1,968 (5.54%) | 1,525 |

All three have 1,525 loops and non-null hatches. Their summed region area is
549,062,830.3080398 mm², identical in the stored results. This large-scene aggregate
is a consistency observation; independent analytic area oracles are the rectangular
and round-column regression controls. Remaining native partition/verification
differences are explicit Unresolved output, not false-confidence promotions.

There is no magnitude-dependent slab-subdivision explosion in these runs. The
native HLD sum is effectively flat; trim remains about 10.2–10.4 s over ten local
views. World-coordinate output/duplicate work still varies (see phase table).
It does not violate the complete-view offset target: local totals are 1.100× and
1.249× origin; default cold workers are 1.261× and 1.206×. Second worker passes
are 1.241× and 1.385×, also below 1.5. A serial local ten-view run takes 47–59 s;
the product's drawing-set route is the 14–18 s worker path.

### Native yardstick

The independently timed raw `Intersection.BrepPlane` sweep over all 1,700 bodies
and ten planes takes 1.579 / 1.629 / 1.667 s at origin / 100 m / 1 km. It constructs
intersection curves and disposes them; it does not validate sources, prove material
topology, cap/attribute retained solids or solve visibility. The native HLD phase
in the full drawing takes 7.166 / 7.407 / 7.282 s summed locally. That is the
composed route's native work, not a monolithic Make2D command on the full scene.
No interactive Section/Make2D command or owner Rhino process was used.

## Verification and evidence

- Debug and Release solution builds: zero warnings/errors.
- `tools/Test-WalkingSkeleton.ps1 -Resolver <supplied resolver>`: the final unfiltered
  run passes **364 Core, 71 UI-model and 258 licensed Rhino tests on each of .NET 8
  and .NET 10**. Only the opt-in `CaptureProfileTests.ProfileHostCapture` is skipped
  on each runtime; all correctness/worker tests run. Full log: `test-full-final.log`.
  The nine offset cases and all fifteen worker cases also passed as a focused
  24-test suite on each runtime before the final unfiltered run.
- Offset coverage includes the frozen-coordinate column regression at 0, 1e5,
  1e6 and 1e7 mm; unchanged source archive bytes; box/round section topology,
  component classes, analytical areas and retained solids; published line classes
  and loci at 0 / 1e5 / 1e6; exact local-archive reuse with a sub-tolerance shape
  change; and refusal of insufficient input precision.
- The timing guard warms both placements, alternates their order and compares
  five-run medians over twelve fresh validations, allowing a 20 ms timer reserve.
  Final unfiltered observations: .NET 8 ratio 0.995, .NET 10 ratio 1.037, both below 1.5.
  It does not use a timed certificate cutoff.
- The startup regression checks complete current results, strict local hashes,
  an unused stalled process reaped before its 60 s timeout, and a subsequent batch.
  Existing crash, hard-cancel, deadline, no-worker, isolated memory retry,
  presentation, codec-state and exact runtime/engine mismatch tests remain enabled.
- The final matrix has 60/60 strict worker/local payload matches on the exact
  scene/request bytes. The benchmark sampler is fixed before these measurements.
- The forced local pre-commit hook passes (`hook-verified-final.log`) after licensed
  hosts exit. An earlier attempt overlapped a licensed host and hit its loaded GHA
  file lock; that failed attempt is retained separately, not counted as verification.

The external evidence root contains `verified-{origin,100m,1km}/` with detached
scenes, requests, local drawings/hashes, raw replies/hashes, all Info/phase records,
OS memory observations and exact runtime/Rhino/engine fingerprints. The final
engine SHA-256 is `2DE749C65296DFC443B22AC19ECB54D141570D955285587BA01A840FDBE3F401`.
`offset-phases.csv` contains **every phase for every view** in all three local,
three cold-worker and three repeat-worker sets (90 rows); `offset-summary.json`
and `offset-tables.md` summarize it. Reproduce those tables with:

```powershell
python tools/worker-bench/offset-report.py "$HOME/.t3/m2d-plus-models/evidence/offset-sections"
```

`SHA256SUMS.txt`, `source/`, `source.diff`, `engine/`, copied model-generation
sidecars and `final-test-results/` bind the final source, binaries, model hashes,
requests/results and both-runtime TRX files to this evidence set.

`profile-1km*/validation.json` and the original managed stack retain the controlled
before/after validator evidence. `worker-starvation-stack.txt` and
`coordinator-progress-stack.txt` retain the rejected sampler observations. Earlier
`before-*`, `final-*` and `candidate-*` folders are historical intermediate runs;
only `verified-*` worker timings use the corrected sampler. Models were never
committed, and generated evidence is not placed in temporary worktrees.

## Integration overlap

Reliability round 2 overlaps `SectionGeometry`, `SectionRegionEngine`,
`StableSectionCertificate`, `SectionReuse` and `SectionDrawing`. The substantive
section algorithms remain in the renamed `ComputeLocal` body / local face-validation
body: preserve the other agent's certificate, region and quarantine fixes there.
The added certificate edits only count work in a `finally`; budgets and proofs
are unchanged. The new coordinate wrapper restores all owned output/provenance.
`DrawingWorkerPool` changes only startup queue ownership/completion.
`ComposedVisibility`, native-engine selection and the analytic curved engine are
untouched. Integrate the separate native `engine = fast` change independently and
rerun its equality checks against these coordinate and exact-cache changes.

