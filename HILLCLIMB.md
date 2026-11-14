# Theory D: perturbation stability and outward winding

Branch: `hc/sec-d-stability`, based on `origin/m3/wiring`. No commit, push,
installation, interactive process attachment, or runtime-setting change.

## Frozen benchmark and scoring

`tools/hillclimb/Score.ps1 -Iteration 0 -Summary 'baseline'` runs its own serial
Rhino.Inside process. The six curved feature model/sidecar pairs are frozen in
`tests/fixtures/features/`. Every sidecar plane is tested; families lacking a
plan/side plane receive explicit controls. Instance references are recursively
expanded with their transforms. No family or difficult object is omitted.

Generated controls add arc CHS at 0/30/45/60 degrees, transverse spline RHS,
twisted RHS and filleted curved I, exact/±0.5/±2-tolerance pipe contact,
disconnected non-crossing lumps, overlapping node sources, a closed SubD control
cage converted to Brep, and an open shell. The three exact swept RHS/I controls
reuse the frozen generator geometry, with additional mid-member section planes.

The independent oracle tessellates exact surfaces and integrates triangle/plane
contour polygons. It never calls BrepPlane, the product's region builder, trim,
cap, or solid membership. The declared surface discretization is 0.1 tolerance;
the area budget is perimeter × modelling tolerance (minimum 16 tolerance²).
Loop count and nesting parity are checked separately. Analytic torus integrals,
cylinder chords and spherical-node areas use a 2e-5 area budget. Uncertified
crossings reduce coverage; open/non-crossing/tangent fills fail the gate.
Per-object cost excludes the independent oracle and records the median of three
back-to-back uncached calls after one warm-up. No timing implies isolation from
other users' machine load. Full per-case records are under
`artifacts/hillclimb/iteration-N/records.json`.

The audited references are now frozen in **`hillclimb/expectations.json`**, with
exact fixture hashes, Rhino build, expected areas, budgets, loop/hole counts and
classes. Subsequent candidates load the same data. Timing includes owned
conversion/validation and region/hatch realization, not just the mathematical
predicate. The benchmark uses the sidecar planes without crop; crop/merge and
component/visibility behavior have separate licensed and render gates.

## Certificate

Only a valid, manifold, outward closed material component is eligible. The
existing planar/cylindrical path is preserved. Route D intersects unchanged
parallel planes at d−4tol, d, d+4tol and requires simple closed non-touching loops,
identical loop count and containment forest, and area/centroid motion bounded by
the perimeter and a certified normal-slope bound.

Rational Bézier patches bound the complete ±5tol slab (4tol perturbation plus
intersection tolerance). Convex hulls bound homogeneous derivative control nets;
outward-rounded intervals bound their cross product. The lower transverse-normal
bound must exceed 0.02 of the upper normal magnitude. Adaptive subdivision
responds to curvature; depth/budget exhaustion rejects the certificate. This
does not infer transversality from finite angle samples. Positive weights,
regular patches and sufficient numerical separation are required.

Each loop's tangent is checked against outward face normal × plane normal on
each curve span, consistently with nesting parity. The central certified loops
are owned evidence reused for regions and visibility caps. Fill eligibility does
not call point-in-solid, clipping, capping or volume conservation. The existing
disconnected-shell grouping still uses its independent shell-containment policy.

## Iterations

- 0, initial oracle: 61/247, 24.696%, 63.1904 ms; geometric gate initially
  reported pass. **Invalidated by oracle audit:** exact-seam polygon contours
  were dropped, and the native planar-curve relationship was unreliable for the
  independently tessellated loops. These preliminary numbers are not used for
  comparison. The raw rows remain in `scores.jsonl` and a provisional archive.
- 1: slab transversality plus perturbation topology and outward winding.
  Full set scored; gate failed (native span-conversion nulls, plus false oracle
  expectations for closed SubD). No improvement; reverted. Snapshot retained
  under ignored `artifacts/hillclimb/rejected-1/`.
- 0, corrected oracle: re-runs the identical 1,517 cases after joining mesh-plane
  contour fragments at 0.1 tolerance, projecting to the plane, and using an
  independent polygon ray-crossing nesting test. The generated SubD control is
  asserted closed. Audit found three incomplete contact/longitudinal contours;
  the gate failed, as recorded. A second audit used side witnesses for contact,
  exact analytic controls, and the analytic longitudinal CHS profile for the two
  remaining straight members. No benchmark shrink or gate relaxation.
- **0, complete frozen baseline: 61/254 (24.0157%), 64.8517 ms, gate passes.**
  All 1,517 cases have independent expectations. Longitudinal straight CHS use
  exact rectangular wall strips; all other curved expectations use the frozen
  polygon integration or explicit analytic controls. Repeated baseline timing
  ran immediately before candidate 2 using these same expectations.
- 2: hypothesis: enumerate **nonempty control-net knot spans**, including the
  repeated-knot slots skipped by Rhino's span count, to cover all rational
  Bézier patches without null conversions. **124/254 (48.8189%), 92.3411 ms.**
  Full geometric gate passes; licensed curved-tube test fails on downstream
  unattributed boundary intervals. Rejected and reverted; snapshot in
  `artifacts/hillclimb/rejected-2/`.
- 3: hypothesis: physical ancestry survives native planar-loop reparameterization.
  Section candidates derive exclusively from physical loops, crop and planar
  arrangements; unmatched boundary intervals inherit physical ancestry while
  crop/dual matches retain explicit provenance. Visibility edge attribution is
  unchanged. **126/254 (49.6063%), 50.7988 ms; gate passes; kept.**
  186 Core, 40 UI and 140 licensed tests pass on each runtime. The old blanket
  converted-SubD rejection test was replaced with independent area/topology
  checks (2.2702951327607623 ±0.00053448, one outer, no hole), while its
  self-intersecting mesh rejection remains intact. Best snapshot: `best-3/`.
- 4: hypothesis: standardize face/surface orientation on an owned copy before
  winding checks. A native probe of the exact twisted RHS shows its reversed
  outer face's `NormalAt` already points outward; manually reversing it twice
  rejects its correct clockwise loop. Standardization makes both orientations
  identical explicitly, rather than assuming a runtime convention.
  Exact-profile graph integration at every NURBS profile knot replaces corner-
  bevelled mesh references for eligible swept RHS/I cases. The generated twisted
  RHS mid-station has the analytic area 0.4×0.6−0.3×0.5 = **0.09**.
  These reference corrections affect cases the baseline never filled; its
  crossing count, certified count and gate are unchanged.
  **215/254 (84.6457%), 61.6509 ms; gate passes; kept.**
  Full serial acceptance: **186 Core + 40 UI + 141 licensed** tests pass on
  each of .NET 8 and .NET 10, zero build warnings/errors. Snapshot: `best-4/`.
  Missing-crossing reasons: 32 transversality, 4 face intersections,
  2 degenerate/self-intersecting loop sets, 1 tolerance-band case.
- 5: hypothesis: lowering the proved sine margin from 0.02 to 0.01 admits shallow
  but still uniformly transverse loops. Same slab, winding, topology and area
  requirements. **215/254, 82.5409 ms**, zero geometric failures. Coverage unchanged,
  observed cost higher; **reverted**. Plateau count 1. Non-improving candidates do
  not receive another full licensed rerun; the retained implementation's full
  .NET 8/10 gate remains the acceptance evidence.
- 6: hypothesis: refine rational Bézier bounds to depth 20 instead of 16 to resolve
  additional high-curvature transverse slabs. **215/254, 84.6821 ms**, zero geometric
  failures. No coverage gain; restored depth 16. Plateau count 2.
- 7: hypothesis: bias three of four subdivisions toward the higher-degree
  surface direction, retaining refinement in both directions. Compare against
  the retained code immediately beforehand: **215/254, 87.1292 ms**, versus
  retained **215/254, 87.0062 ms**. Zero geometric failures; **reverted**.
  Plateau count **3: stopped**, retaining iteration 4.

Shared-machine cost drift is substantial: retained iteration 4 repeated at
87.0062 ms versus its earlier 61.6509 ms. Paired per-member resampling
(`compare_timing.py`, 3,000 fixed-seed samples, same upper-middle median convention
as the scorer) gives 95% difference ranges of −6.821..+1.201 ms for trial 5,
−3.433..+1.692 ms for trial 6, and −1.445..+5.501 ms for trial 7 versus the repeat.
None demonstrates a consistent secondary improvement. Resampling cannot remove
shared global load; no speedup is claimed from the unpaired observations.

Final original/best scoring is run **back to back** on the frozen audited
expectations, followed serially by the same `tools/render/` model pair:

- **Baseline: 61/254 = 24.0157%, 61.3872 ms.**
- **Best, iteration 4: 215/254 = 84.6457%, 83.3439 ms.**
- **Zero wrong fills, area errors or loop/hole mismatches** across 1,517 cases.
  The gain is certification coverage, not a speedup. Full per-case records for
  the final pair overwrite their earlier local record files; raw score rows
  preserve every measurement. `hillclimb/final.json` identifies the final pair.
- Debug and Release: **0 warnings / 0 errors**. Ordinary unlicensed tests and
  forced Git-sh local hook pass. All **186 Core + 40 UI + 141 licensed** tests
  pass on each runtime, .NET 8 and .NET 10, through the serial acceptance script.
  The restored retained certificate source matches the full-tested snapshot.
- Before/after render sheets: `artifacts/render/sec-d-before/index.html` and
  `artifacts/render/sec-d-after/index.html`; individual PNG/SVG and full Info
  are in the same directories. All five cards render without execution errors.
- Curved-steel long section: **0 → 4 regions; 18 → 0 Unresolved pieces**.
  Curved-steel plan: **0 → 4 regions**.
- Diagrid long section: **0 → 2 regions; 16 → 164 Unresolved pieces**, with
  **23 Visible + 12 Hidden** pieces now surviving. More geometry is emitted,
  but much background remains quarantined. Its plan still publishes no region:
  two sources fail existing face-intersection validation and two certified
  sources hit existing visibility-edge attribution failures. These are not
  silently promoted to confident output.
- No commit, push, installation or owner's Rhino process access.

## Limits

The certificate relies on Rhino's valid material topology, exact-to-tolerance
intersection results, and NURBS-to-Bézier conversion. It does not establish
arbitrary intra-face injectivity. Native trim/join can still fail; visibility
remains quarantined if that happens. Underlying untrimmed surface bounds can
reject a safe trimmed face. Near contacts inside the perturbation slab,
degenerate parameterizations, nonpositive rational weights and insufficient
subdivision budget are deliberately uncertain. No worker or runtime change.
