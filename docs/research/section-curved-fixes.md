# Curved and steel section corrections

Date: 2026-11-15. Implementation based on `4bbe6b5ea4c029ecef247d2cf9a13813e273650b`.
No commits, pushes, installations or generator changes. Production changes are confined to
`Sections/SectionGeometry.cs`, `StableSectionCertificate.cs` and `PhysicalSectionClipper.cs`.
`SectionDrawing` and its uncertainty propagation policy are unchanged.

## 1. Shared topology falsely reported as self-intersection

`EdgeProvenance.Split` only labels native overlap events. A missing/partial overlap on a
closed rim or one of its adjacent split edges therefore leaves an unlabelled interval,
even though the entire face intersection follows the shared Brep boundary. The existing
point-event path already accepts shared vertices and edge endpoints; it remains in place.

The prior attempt's distance fallback had the right conservative intent, but depended on
a numerical arc-length estimate and a fixed 32,768-node budget. It is replaced with an
enclosing-box proof: recursively enclose each missing interval in a tolerance ball around
a point evaluated ON a shared edge. Every box corner must be inside the ball, with a
coordinate-scale roundoff margin. Convexity then covers the entire box and its curve.
Different subintervals may use adjacent split edges. This does not infer coincidence from
samples, bridge distant endpoints, relax the modelling tolerance, or accept on budget
exhaustion. Native curve intersections still use the existing process-wide gate; serial
cutting and the scene-owned verdict cache are unchanged.

The two original curved-perf Breps are frozen in `tests/fixtures/sections/shared-rims.3dm` with source IDs/hashes in `shared-rims.md`; both now validate on their original local/world placements. Running the archived baseline validator against these exact frozen Breps rejects both; the candidate accepts both on .NET 8 and .NET 10 (`original-rims-before.txt`, `original-rims.txt`, and the licensed tests).

Regression fixtures: a cylinder with both rims split into two edges, its entire/omitted
6% rim intervals, a curve departing from the rim, and a genuinely self-intersecting
bow-tie profile swept along a straight rail. The latter has a known wall crossing at
(1,1,z), away from every shared rim; it still fails validation and emits no regions or
visibility pieces. Existing reuse, mirrored/scaled-invalid-source and parallel-determinism
tests remain enabled.

## 2. RHS crossing loses its physical clip

At stations 2 and 3 the four-face closed-profile RHS sweep passes certificate D.
`Brep.Trim(Plane, tolerance)` returns **zero** pieces; this fails before capping. A finite
planar Brep spanning the source bounds makes `Brep.Split` return **two** valid open pieces.
This isolates a native plane-trim failure, rather than an absent intersection or bad
material-loop nesting. The native implementation's internal cause is not exposed.

Only when plane Trim returns empty, split using that finite plane and select retained
pieces by their complete plane-space bounds. Reject ambiguous/straddling pieces. Then
use the existing certified-loop caps and require valid closed solids, positive volume,
retained-side bounds and fully attributed edges. There is no whole-source substitute.
Both stations now retain a hollow capped solid and their 0.09 m², two-loop regions.

## 3. I-beam and mullion loop rejection

These are neither fillet self-crossings nor tangent contacts. `CurveSelf` returns **zero**
events on their central and ±4-tolerance intersections. The central joined loops satisfy
`IsInPlane(cutPlane, documentTolerance)`, but `AreaMassProperties.Compute(curve)` returns
null under its stricter native planarity requirement. The old combined error message
mislabelled this as “Self-intersecting or degenerate section loop”. The offset intersections
are measurable, explaining the station-specific failure.

For closed intersection loops whose area routine fails, first bound their deviation using
`IsInPlane` at the document tolerance, then project onto the exact requested plane.
Already measurable loops are untouched. All self-intersection, nesting, winding,
perturbation/slab and cap checks still run. The I-beam gets area 0.438626549083 m² / one
loop; mullion 2 gets 0.002513273876 m² / two loops. Safety tests retain bow-tie and off-plane
loop rejection. This is targeted verification of the named cases, not a claim that all 40
historical loop-certificate rejections in the stopped matrix have been rerun or repaired.

## 4. Diagrid source guard

The named `diagrid-iso-0-0` reproducer has four reported face-pair intersection curves:
0/2, 0/3, 1/2 and 1/3. A 1,001-point independent distance audit puts their maximum distances
to shared edges at 5.23e-11, 4.27e-11, 6.40e-11 and 6.04e-11 m respectively. The new
continuous enclosing-box check proves coverage of the missing native-overlap intervals.
No off-topology face-pair crossing was found. This reproducer shares the split/partial-rim
false positive; no generator defect was established and the generator was not changed.
Both named stations now pass their analytic CHS area/hole oracle.

## 5. Uncertainty propagation

Policy is unchanged. Twisted RHS station 4 still has two invalid native segments,
53 Unresolved pieces and no final region. A successful section certificate proves the
material cross-section independently; source-level native Unset quarantine nonetheless
removes the whole source region, rather than only a local segment footprint. Thus the
withholding is wider than the localized segment evidence, conservatively and intentionally.
The section-failure footprint path also uses a projected axis-aligned source box and
withholds an entire intersecting region, including portions outside the uncertainty box.
Those are existing completeness limitations, not reasons to relabel any line Visible.
Diagrid iso-1-0 station 3 now keeps its certified region because the other sources' false
self-intersection evidence is removed. The policy itself did not narrow.

## End-to-end before / after

Areas are m²; entries are area / total loops. Every before target was 0 / 0. The same
read-only generated archives and frozen view definitions were used for both builds.

| Model / normal station | Before | After | Analytic expectation | Unresolved before → after |
|---|---:|---:|---:|---:|
| Steel RHS-arc 2 | 0 / 0 | 0.0900000006005 / 2 | 0.09 / 2 | 2 → 0 |
| Steel RHS-arc 3 | 0 / 0 | 0.08999999999998 / 2 | 0.09 / 2 | 2 → 0 |
| Steel I-beam filleted 3 | 0 / 0 | 0.438626549083 / 1 | 0.438626548246 / 1 | 0 → 0 |
| Facade curved-mullion-2 2 | 0 / 0 | 0.00251327387567 / 2 | 0.00251327412287 / 2 | 135 → 126 |
| Diagrid iso-0-0 1 | 0 / 0 | 0.00859536001051 / 2 | 0.00859539750022 / 2 | 3 → 0 |
| Diagrid iso-0-0 2 | 0 / 0 | 0.00859542307802 / 2 | 0.00859539750022 / 2 | 50 → 48 |
| Diagrid iso-1-0 3 | 0 / 0 | 0.00859541656982 / 2 | 0.00859539750022 / 2 | 125 → 0 |
| Steel twisted-RHS 4 | 0 / 0 | 0 / 0 | 0.09 / 2 | 53 → 53 |

Seven of eight targets now pass. Twisted RHS remains explicitly withheld. Remaining Unresolved
pieces elsewhere in the facade/diagrid scenes were not promoted or claimed repaired.
Independent analytic profile expectations, rather than native HLD agreement, judge these areas.

Machine-readable results, archive/engine hashes and per-view outputs:
`artifacts/section-fixes/summary.json`, `before/`, `after/`.
Geometry probes: `artifacts/section-probe/{before,detail,mass,after-probe}.txt`.
All eight views ran through `tools/models/run.py --engine view_engine --threads off --wall 120`.
The shared Rhino mutex serializes this task's licensed processes; no other process was
attached to, stopped or modified. Other agents were active, so timings are indicative.

## Verification

- Debug and Release solution builds: zero warnings, zero errors.
- `tools/Test-WalkingSkeleton.ps1 -Resolver <supplied resolver>`: 218 Core, 40 UI-model and 190 licensed Rhino tests pass on EACH of .NET 8 and .NET 10, with zero skips. The native load test asserts the actual runtime major. Final results recorded in `artifacts/section-probe/test-final.log` and the projects' `TestResults/m3-*-net{8,10}.trx` files.
- Eleven new licensed regression cases in `CurvedSectionRegressionTests`: two frozen original rims, split-rim cylinder/partial-overlap coverage and departure rejection, genuine crossed sweep, six analytic station profiles with holes/hatches/capped retained-solid/provenance checks, and crossed/off-plane loop rejection.
- Forced local pre-commit hook passes (build, 218 Core tests, 40 UI-model tests).
- `tools/perf-f/pair.sh section-fix-M-long <perf-M-blocks.3dm> long 120`: baseline, cold, warm and serial drawing dumps all SHA-256 `23205E35DA1541821706B8F32443BA6FC1F4162A47178FB0A50EBBFC08B2BDFD`. Independent `compare.py` also passes baseline vs cold. One cold sample: 7.511 → 7.592 s; warm 6.505 s; serial 6.389 s, under contention (no meaningful speed-regression claim from a 1% difference). Verdict computations remain 15, preserving definition reuse. Logs/dumps: `artifacts/perf-f/final/section-fix-M-long/`.
- The tests and model workers run serially under the shared mutex. No cloud CI, Rhino installation, Grasshopper Libraries copy, commit or push.


