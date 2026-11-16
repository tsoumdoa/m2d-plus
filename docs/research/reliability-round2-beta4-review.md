# Round 2 review fixes on beta.4

2026-11-16–17. `fix/reliability-round2` rebased onto `9d4b9a7` (v0.1.0-beta.4,
#41 depth popup, #42 bake layouts, #43 cut only). Evidence is external at
`~/.t3/m2d-plus-models/evidence/round2-beta4-review/`. Earlier beta.2/beta.3
measurements remain historical evidence for their respective engines.

## Review fixes and proof arguments

1. Refused-component markers use an owned private copy centred near the geometry
   on the cut plane. Brep/plane and mesh/plane intersections, footprint creation
   and crop run locally; every marker passes through `NativeDrawing.Crop` before
   restoring world coordinates. An outline surrounding the entire crop gets a
   conservative footprint/crop intersection marker. All ink remains Unresolved,
   with no material region or hatch. Cancellation is checked before native work,
   after intersections and while publishing pieces; cancelled finalization only
   disposes owned geometry. Cropped intersection/fallback regressions run at
   origin and 1 km, including a distant XY plane origin and cancellation.
2. Boolean merge failure is handled per compatible style group. Only that
   group's partial union is discarded; its original certified faces survive
   separately with an explicit diagnostic. Other groups still merge normally.
   No union is claimed without its arrangement/membership/area checks. The test
   injects a null native arrangement for one group and verifies its two separate
   faces and another group's proven union, areas and hatches.
3. Compute and presentation translate by the cut plane's closest point to the
   input bounds centre. The geometric plane is reparameterized around that
   point; crop and far planes move consistently. Original-world precision guards
   remain unchanged. A separate translated pattern plane retains the user's
   original hatch origin without changing style values. The distant-plane test
   checks analytic circular area and the original hatch base point through both
   compute and restyle.
4. Transient component refusals carry only unpublished cut geometry. Visibility
   preparation diagnostics still quarantine line confidence but no longer
   invent an orange outline over a certified region. Other components retain
   independent near-cap checks and topology. Tests cover a compound source with
   one proven box and one near-tangent sphere in normal and cut-only modes, and
   restore zero-Unresolved assertions for the background-only thin-depth case.
5. Recovery still applies the conservative all-source confidence policy: native
   ordering changes are not assumed spatially local. Its continuous box proofs
   have a deterministic 65,536-comparison budget per recovery pass. Restored
   Hidden uses the finite native verifier defaults per pass (4,096 ray tests,
   16,384 samples); exhausted
   or indeterminate recovery neighbourhoods stay Unresolved and retain that
   solved class through hidden-off presentation. No sample promotes Visible.
   Budget regressions force both ray/sample exhaustion and box-proof exhaustion.

The beta.4 thin-depth cut-only assertion encoded the superseded source-wide
marker policy and was updated to require both certified cuts with no orange
overlay. Actual refused-component cut-only ink has its own regression. A worker
persistence assertion now permits #40's late cold-handshake sibling while
requiring ready matching PIDs to persist; its memory conditions are fixed for
the test. Production worker admission is unchanged. `integration-first.*`
preserves the initial integration failures; final acceptance is recorded below.

## Verification and measurements

Release and Debug builds and the forced hook pass with zero warnings/errors.
The accepted unfiltered WalkingSkeleton matrix passes on both actual runtimes:

| Runtime | Core | UI | Licensed Rhino | Failures | Opt-in profiler skip |
|---|---:|---:|---:|---:|---:|
| .NET 8 | 426 | 74 | 336 | 0 | 1 |
| .NET 10 | 426 | 74 | 336 | 0 | 1 |

The validation median guard passes: origin/100 m are 0.061278/0.058812 s on
.NET 8 (ratio 0.960), and 0.058333/0.055119 s on .NET 10 (ratio 0.945).
Logs and all six hash-bound TRX files are in `walking-skeleton.log`, `hook.log`
and `test-results/`. The actual hosts are .NET 8.0.34 and .NET 10.0.15 with
Rhino 8.36.26296.17001. `verification-summary.json` binds all six TRX receipts,
the tested Debug engine and the frozen source hashes.

The final light gate passes **219 / 219 required cases, zero known bugs and zero
evidenced wrong-Visible**, including the complete 50-view Hidden audit below.
Every case has a normal process exit and zero native/kernel error. The idle
coordinator was resumed under one serial admission after 183 completed cases;
no Rhino process was stopped and accepted cases were not repeated. The admission
record and remaining-case log are retained in `routing-admission-resume.*`.

Fresh hash-bound witnesses resolve 58 M-blocks and 92 M-exploded SW tolerance
flags, including the two points on actual saved native-output curves. Their
all-pairs planar queries and 157,500 complete curved-body/ray pairs pass. Ten
further planar/section point witnesses bring the tolerance-artefact count to
160. The strict whole-output comparison passes 186 cases; fresh witnesses
classify one further case's disputed section points, and eight cases have fresh
independent full planar agreement. **24 cases retain
Unresolved/Info status and 32,951 comparisons remain unclassified**; those
comparisons receive no visibility promotion. This is the requested light gate,
not a full 233-view certificate. All section-oracle and analytic area/loop tests
pass with the review regressions.

All 93 uncut planar saved drawing payloads are hash-identical to the historical
beta.3 outputs, including geometry archives and classifications, excluding
timing/Info/phases (`planar-payload-comparison.json`). Section planes are
deliberately reparameterized near geometry; the original world hatch origin,
area and loop truth are checked independently. Cross-engine section archive
identity is not claimed.

All 50 accepted census receipts have normal exit, no row/summary error, matching
model/engine/harness hashes and complete expected body/ray counts. One initial
RhinoCore startup failed with COM `E_FAIL` before opening its model. Only that
missing case was retried on the unchanged build; the failed receipt/log remain
in `startup-failed-census/`. No failed execution is counted as accepted evidence.

| Unresolved cause (metres) | Historical beta.3 | Reviewed beta.4 |
|---|---:|---:|
| Native Unset propagation / continuous-proof refusal | 262.883351 | 259.792826 |
| Unsupported-source footprint | 182.383247 | 182.074555 |
| Section recovery continuous-proof refusal | 152.174550 | 143.742449 |
| Refused-source marker | 91.671957 | 0.000000 |
| Other footprint | 63.806734 | 62.363206 |
| Native duplicate attribution | 47.881935 | 47.882010 |
| Verifier withholding (legacy ledger label: `verifier contradiction`) | 22.303547 | 1,490.944552 |
| Native Unset interval | 19.492366 | 19.775228 |
| Region withholding / native coverage gap | 0.000000 | 0.000000 |
| **Total** | **842.597686** | **2,206.574825** |

The comparison uses the earlier accepted beta.3 evidence, not a fresh beta.4
ablation baseline. It includes the main-branch changes between those engines.
The marker decrease removes source-wide overlays; no refused-component marker
is present inside these census crops. The dedicated box/sphere tests still
require visible refusal ink in both normal and cut-only output.

All 50 views retain their region counts. Facade plan changes 94.741776 to
64.852490 m Unresolved (56.95% to 47.54%), retaining 15 regions; diagrid plan
changes 21.517475 to 22.107339 m (35.91% to 36.90%), retaining 14. The complete
per-view comparison is in `census-views.md` and `region-count-comparison.json`.

The finite recovery audit deliberately costs confidence. Across 14 recovery
passes it reserves 47,835 ray tests and makes 43,236 native casts (historical
unbounded pass: 581,350 / 536,450). Its counters report 44,366 skipped decisions
and 574 indeterminate samples; neither restores Hidden. Continuous bound proofs
make 311,867 comparisons, with 1,869 budget refusals. Restored-Hidden interval
visits are 1,746.622866 m before / 277.539921 m retained / 1,469.082944 m withheld.
The historical visits were 1,738.326408 / 1,724.530210 / 13.796197 m. Visits may
repeat through composition and section paths and are not unique drawing length.
The ledger's verifier bucket includes budget/indeterminate refusals; it does not
mean that all that ink was independently proved wrongly classified. This is a
material Unresolved increase, preserved under the requested budget-to-Unresolved
policy. `census-summary.json`, `census-causes.md` and
`recovery-census-details.json` retain per-case values and producer binding.

The final independent all-body audit has **4,395 samples, 25,141,625 / 25,141,625
body/ray pairs, 4,394 blocked, one margin/inconclusive and zero wrong-Hidden**.
It includes 80,966 original-solid cap containment tests (1,824 hits), with zero
uncastable rays. The smaller audited sample set reflects the withheld Hidden;
it is not a claim of equal coverage or a continuous visibility certificate.
All 50 rows and audit/receipt hashes are retained in `hidden-summary.json`.

The offset benchmark's `100000:10:recovery` mode adds one owned, rotated sphere
whose parameter poles cross plan 0. It modifies only the transient reopened
document, never the saved owner fixture. Normal offset modes keep the original
models. Local compute, cold production pool, strict hashes and presentation are
measured separately at 0, 100 m and 1 km, plus the 100 m recovery case.

| Ten-plan case | Sources | Local s | Cold pool s | Workers | Presentation s | Strict worker matches | Regions / hatches |
|---|---:|---:|---:|---:|---:|---:|---:|
| Origin | 1,700 | 54.870 | 21.111 | 7 | 2.547 | 10/10 | 1,530 / 1,530 |
| 100 m | 1,700 | 57.616 | 21.801 | 7 | 2.433 | 10/10 | 1,530 / 1,530 |
| 1 km | 1,700 | 64.058 | 24.199 | 7 | 2.560 | 10/10 | 1,530 / 1,530 |
| 100 m + sphere | 1,701 | 58.770 | 23.823 | 7 | 2.119 | 10/10 | 1,531 / 1,531 |

All 40 presentations preserve loop/hatch counts, source membership and area and
leave the borrowed raw drawing byte-identical. Default worker sizing/caps are
used, with no overrides. Local and worker offset/origin ratios stay below 1.5.
These are single observations on a shared machine, not controlled speed claims.

The added sphere triggers one recovered source in plan 0. This plan uses
19,015/65,536 box comparisons, with zero budget refusals. Its restored-Hidden
audit checks 17 intervals (6,799.967600 mm), retaining them through 85 exact
planar-Hidden sample decisions with zero native casts; ray budget is 0/4,096.
The separate ordinary native audit uses 1,010/4,096 reserved ray tests. Its
combined audit CPU-work phase is 0.196 s. Whole ten-plan local time increases
by 1.154 s (2.0%) and cold pool by 2.022 s (9.3%) in these observations.
`offset-summary.json` and `recovery-details.json` retain full values, phase data,
budget diagnostics and original process receipts. Source validation reuse stays
at nine unique local shapes per normal view and ten with the added sphere.

| Accepted producer / evidence | SHA-256 |
|---|---|
| Release Rhino engine | `88950968366d3e5d7fe59700487e50c32e9b6e81d4c8f874f952e2f93e4af8a8` |
| Tested Debug Rhino engine | `9a13c1a7c62e409a4039fc66c1775d1d17151d4c9373d5f2b4934c636f73f9c1` |
| Final light gate | `8ff4ceec09a86ccdd21e25c013cf91dc120cdfc045d63dd4e845fec3cdb5a946` |
| Completed census summary | `d3c1eb8d6a55733e87fc06600cc7939b1d814ddfcdd2ecb5a865059db1d13b66` |

`producer.json` also records Core and both harness fingerprints. `acceptance.json`
binds the final source tree, archived source files, tests, gate, audits and offset
measurements. The archived Release assembly directory remains the producer used
by the census, independent witnesses and gate.

## Follow-up only

World-coordinate provenance splits at `SectionDrawing.cs` (review sites
191/273/448) and `ComposedVisibility.cs` (406) are deferred. They were not changed
in this review round. Their local-frame normalization needs separate evidence.
