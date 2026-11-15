# Small features — measured limits, no eligible numerical winner

Branch `test/small-features`, from fetched `origin/main`; nothing committed,
pushed, installed or published. The kernel-routing branch was not touched.
All Rhino work used the serial `Test-WalkingSkeleton.ps1` harness and its owned
Rhino.Inside hosts. No interactive Rhino process was attached to or altered.

## Protocol and evidence

`tools/models/small_features.py` generates 19 independent details in each of 12
archives: 0.01 / 0.001 mm document tolerances, mm / m document units, origin /
1 km XYZ translation, plus the same four 0.1 mm timing controls. The frozen
archives total 466,377 bytes; each is under 39 KB. The generator uses rhino3dm
without a licence or Rhino process. Geometry, names, UUIDs and analytic truth are
deterministic; archive metadata timestamps are not promised byte-identical.

The fixture manifest contains analytic profile segments/lengths, region/hole
counts, areas and probe heights/classes. The scorer independently evaluates:

- every expected line/arc feature (including ones below 2 tol), and a rear
  Visible/Hidden probe through each solid/void;
- region count, holes and analytic area, plus boundary-location checks in the
  final oracle; a failed hatch is a failed filled-region result;
- the isolated stable-section certificate and its loop areas;
- exact planar output where the *input truth* is entirely polygonal; a curved
  profile is excluded even if `IsLinear(tol)` would approximate it;
- two-source compatible-style gap merging, and a native probe through the open
  0.01 mm gap in the final oracle;
- mm/m and origin/1 km equivalence of feature decisions, failures and uncertainty.

The primary is the sum of correct features/probes/regions/certificates divided
by all expected applicable checks at the two fine tolerances. Coarse controls do
not enter the primary. A successful API call or green measurement test does **not**
pass the drawing gate. `M2D_SMALL_STRICT=1` makes failed fine-tolerance gates fail
test execution. The ordinary measurement test still asserts input integrity and
no known wrong fill at either requested tolerance. The coarse control intentionally
contains sub-tolerance wrong/uncertain results, which remain in evidence.

Compressed full rows, including failures and diagnostic reasons, are in
`evidence/`; their hashes and aggregate scores/timing ratios are in `scores.jsonl`.
No exclusions were introduced to remove failing eligible details. The public
native measurements are uncut `NativeDrawing`; regions and the isolated
certificate are measured separately. This does not certify every Route C
clipping/visibility combination or arbitrary freeform geometry.

## Iterations

| Iteration | One candidate / experiment | Disposition |
|---|---|---|
| Preparatory | Correct outer-to-inner extrusion profile coordinates; split measurement by tolerance to bound each STA test action | Fixture/harness repair, not an engine improvement. Initial shifted-RHS observations are invalid and are not in scores. |
| 0 | Corrected fixtures, unchanged numerical engines (oracle revision 1) | 2,040 / 2,104 = **96.9582%**, .NET 8. Gate fails; no winner. .NET 10 baseline was not run: the harness stopped on the coarse-control wrong-fill assertion, subsequently restricted to the requested fine tolerances. |
| 1 | Suppress projected drawing curves shorter than 2 tol, with roundoff allowance at exactly 2 tol | **Rejected and reverted.** 1,976 / 2,104 = **93.9163%** on both runtimes, and gate still fails. At 0.01 mm the fraction falls from 95.0570% to 88.9734%. |
| Final | Required warning/count reporting only; preserve output geometry. Add boundary-location and native two-object-gap checks (oracle revision 2) | Numerical engines unchanged. Final evidence and limits below; no claim of improvement across the oracle revision. |

Final revision 2: **2,044 / 2,112 = 96.7803% on both .NET 8.0.34 and
.NET 10.0.15**, Rhino 8.36.26296.17001. Both the overall and metamorphic gates
fail. Engine contributions are native 1,253/1,264, exact 532/536, filled regions
138/160, certificate 121/152. Native invalid extraction and Unresolved are zero;
there are no Unset diagnostics. Region components record 11 uncertain decisions.
The isolated certificate refuses 47 proofs, including 16 expected contact refusals;
31 are failed requested crossing certificates. These are different counters,
not one interchangeable Unresolved total.

No tolerance reduction, slab-delta change, epsilon relaxation, native tolerance
override, region join adjustment, mesh approximation, or kernel-routing change is
retained. The required warnings are separate product work, not a numerically
winning hill-climb iteration. Since the gate is already false, a local repair to
one adapter alone cannot be kept as an eligible whole-suite winner.

## Why the gate fails

1. **Native HLD at the boundary:** at 0.01 mm tolerance a 0.02 mm plate outline can
   survive while the rear probe is classified incorrectly. Native Unresolved and
   invalid extraction can both be zero in that case. A zero uncertainty count is
   not a geometric certificate. At 0.001 mm the tested native details pass.
2. **Metres and hatches:** the public region pipeline reports `Hatch construction
   failed` for small RHS and bolt-hole regions in metre units, even when the same
   physical section works in mm. A valid intersection is not a valid filled output.
   Some 0.01 mm bolt-hole cases are also rejected by the face-intersection validator.
   These failures remain counted, not silently accepted as outlines or correct fill.
3. **Exact mesh adapter:** `PlanarExtraction` reads mesh face vertices through
   single-precision `mesh.Vertices[index]`. A 1 km coordinate can erase a 0.02 mm
   face width before filtered/exact predicates receive it. The 1 km mesh probe fails
   at both tolerances. The analytic Core gap/short-edge test, Brep and polyline cases
   pass the same units/offset transformations; no predicate epsilon change is justified.
4. **Certificate margin:** delta remains **4 tol**, sin lower bound **> 0.02**.
   `Bound` tests a slab of delta + tol = 5 tol. For a 0.1 mm plate extruded
   symmetrically through the plane at tol 0.01 mm, its caps are exactly at that
   slab boundary, so transversality is correctly not certified by this proof.
   Merely shrinking delta would change the proof domain, not establish a new bound.
   A 0.02 mm in-plane plate also exposes winding association with an opposite face
   within the current 2 tol witness radius; it is refused, not incorrectly filled.
   At tol 0.001 mm all isolated certificate controls pass, including the thin plate.

No wrong filled region was observed at either requested fine tolerance. The
0.01 mm gap remains two regions under compatible-style merging in every fine
unit/offset variant. This is a measured fixture result, not a general join proof.
The final native two-source probe loses that opening at 0.01 mm tolerance in all
four variants, and preserves it at 0.001 mm tolerance in all four variants. The
gap warning is asserted at the coarse tolerances in the licensed scorer.

## Smallest passing controls

All dimensions below are **mm**, and a passing control must work in all four
unit/offset variants. These are observed controls, not universal feature-size
guarantees. The 0.01414 mm edge is the diagonal of the 0.01 mm chamfer. Plate is
the transverse rectangular plate's in-plane thickness; the rounded steel plate
extruded only 0.1 mm normal to the cut is a separate certificate limitation.

| Engine / output | Tol 0.01: edge / gap / plate | Tol 0.001: edge / gap / plate |
|---|---|---|
| Native uncut visibility | 0.01414 / 0.01 fails / 0.1 | 0.01414 / 0.01 / 0.02 |
| Exact planar Brep/profile | 0.01414 / 0.01 / 0.02 | 0.01414 / 0.01 / 0.02 |
| Filled section controls | 0.01414 / 0.01 / 0.02 | 0.01414 / 0.01 / 0.02 |
| Isolated slab certificate | n/a / n/a / 0.1 | n/a / n/a / 0.02 |

The exact **mesh** adapter has no reliable large-offset plate classification in
this set at either tolerance. The filled-section row covers the successful
polygonal controls; metre-unit RHS/bolt-hole fills still fail. The certificate
does not draw/classify edge loci or merge multiple sources, so no independent
edge/gap limit is assigned to it. No smaller plate or alternative larger gap was
tested. Fillets/chamfers below 2 tol were retained in the primary rather than
discarded to inflate the score.

## Warning rule shipped for review

The selected drawing tolerance stays the document absolute tolerance. Explicit
`tolerance =` affects the drawing only; it never changes the document. Scale
advice compares physical millimetres:

| Scale denominator | Warn above |
|---|---:|
| 1–5 | 0.01 mm |
| >5 and <50, including 10–20 | 0.1 mm |
| 50 and above | 1 mm |

GH shows a runtime Warning, and Info reports source edges at or below 2 tol,
positive gaps at or below 2 tol between adjacent source bounds in view X/Y, and
uncertain section components. Counts survive cached presentation/placement.
The adjacent-box census costs sorting rather than an all-pairs geometry-distance
search; its conservative warning count is not exhaustive. Source edges can be
hidden or outside the view. Uncertain section components can have causes beyond
tolerance. The UI identifies these as risk/uncertainty, not a measured missing-line
count. Adapter tolerance drops are zero; native internal drops are explicitly
**unknown**. The previous UX claim that this adapter suppressed all curves shorter
than 2 tol was unimplemented; experimentally adding it lost correct detail, so it
was withdrawn. No fabricated native loss count is reported.

## Reproduce

```powershell
python tools/models/small_features.py
$env:M2D_SMALL_RESULTS = "$PWD/artifacts/small-features/verification"
./tools/Test-WalkingSkeleton.ps1 -Resolver 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
python tools/models/score_small_features.py artifacts/small-features/verification --iteration verification --change 'Describe one change' --disposition 'Record gate and keep/reject decision'
```

Timing ratios pair each engine/detail/units/offset with its 0.1 mm control.
They are medians of single observations, not speedup claims; order, JIT and host
activity are not controlled. Native times use `ViewDrawing.Seconds` and exclude
the new post-solve risk census and capture. Certificate/region times include their
own validation; exact times include extraction, kernel, locus construction and
analytic output checks. No RAM/CPU isolation or repeated large-model benchmark
was attempted. Fine-tolerance failures are not omitted from timing.

Final machine-readable validation, source hashes and limits are recorded alongside
the score after the serial .NET 8/10 runs. The overall small-features gate remains
failed; the new tests are a reproducible measurement and warning regression suite,
not a release certification.

## Final verification and timing

All **211 Core + 40 GH UI-model + 146 licensed Rhino tests pass on each runtime**
in the final serial run. Debug and Release builds have zero warnings; the forced
local hook passes. Fixture and source hashes were checked after the final run.
`validation.json`, `provenance.json`, and `final-score.json` retain those records.
Green execution does not change the failed drawing gate exported in the score.

| Engine | 0.01 / 0.1 mm paired median time ratio (.NET 8 / 10) | 0.001 / 0.1 mm (.NET 8 / 10) |
|---|---:|---:|
| Native | 0.984 / 1.009 | 0.958 / 0.983 |
| Exact, including analytic checks | 1.307 / 1.163 | 1.344 / 1.187 |
| Regions | 1.434 / 1.270 | 1.314 / 1.285 |
| Certificate | 1.415 / 1.098 | 1.966 / 2.064 |

Native medians are about 0.36 s per tiny detail on this host; no native fine-
tolerance blow-up was observed. The certificate reaches about twice its paired
coarse-control median at 0.001 mm. These are single observations with the timing
scope limitations above, not a performance guarantee or a measured overhead for
the new warning census. Nothing was committed or pushed.
