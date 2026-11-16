# Reliability round 2 on beta.3

2026-11-16. `fix/reliability-round2` rebased from `df039a2` onto `e091e09`
(v0.1.0-beta.3), including #38, #39 and #40. Final evidence is external at
`~/.t3/m2d-plus-models/evidence/round2-beta3-presentation/`. Initial rebase evidence
and the pre-fix failure are in `evidence/round2-beta3-rebase/`; that unfinished
gate was stopped when the owner added the presentation fix and is not a final
verdict. The earlier Round 2 census
in `unresolved-reduction.md` remains evidence for its original beta.2 engine.

## Resolution and numerical argument

- `StableSectionCertificate.SlabSlope` retains the geometric sphere-pole bound,
  face/sphere slab-disjointness checks and tangency refusal inside #40's
  `try/finally`. The existing 20,000-patch budget, depth-16 limit and
  `SectionWork.Slab` counter remain unchanged. Analytic sphere bounds do not
  consume Bezier patches; budget counters still cover the generic path and failures.
- The owned source, cut plane, crop and far plane pass through #40's
  `SectionCoordinates` and `ComputeLocal` unchanged. Radius and signed
  sphere-centre distance to the plane are invariant under their common rigid
  translation, so `d / sqrt((r-d)(r+d))` is the same geometric normal bound.
  Recognition and slab roundoff are measured from the local face coordinates.
  The original-coordinate precision guard runs before translation: local work
  cannot authorize input precision already lost at a distant origin. No
  tolerance-sized sphere fit or enlargement of the transversality margin is used.
- Local face validation and SHA-256 reuse of complete translated Brep archives
  are retained. The continuous original-edge attribution recovery and one-sided
  cap contacts operate inside that frame; every independent output/provenance
  owner is restored to world coordinates by #40 before visibility consumes it.
- `SectionDrawing` retains its Settings snapshot, section work diagnostics and
  indexed cut-loop lookup. Certified regions remain independent of unrelated
  line uncertainty, refused sources retain their Unresolved unhatched markers,
  mesh footprint count stays capped, and actual post-trim inputs remain alive
  through the restored-Hidden audit. Disposal happens after verification.

## Presentation provenance at 1 km

The owner's additional normal-mode failure is reproduced from the saved plan-7
drawing, independently of cut-only work. One circular column has centre
`(1032200, 1007000, 1023900)`, radius 200 mm, and length-domain
`[0, 1256.6370614359173]`. World-coordinate overlap extraction at its periodic
seam leaves an arithmetic interval that `ArcCurve.Trim` cannot extract.
The exception escaped `Restyle` and aborted the whole presented view.

The self-contained analytic-circle regression passes at origin and 100 m but
throws that same provenance exception at 1 km against the archived pre-fix
engine (`before-control/before.trx`: two passed, one failed). All three placements
pass after the fix. The full saved plan 7 presents all 153 certified regions in
0.195 s, with agreeing areas and an unchanged raw drawing (`plan7/`).

Presentation now borrows owned copies of region loops and provenance in the same
cut-plane-centred frame as #40's section computation. The plane and crop receive
the same rigid translation. Domains, overlap/snap margins and model tolerance
are unchanged; no tiny nonzero interval is discarded to hide the exception.
Loops, provenance and hatches return to world coordinates before placement;
the style's plane-relative pattern origin stays unchanged. Input precision is
checked before translating, and the cached drawing remains untouched.

Reconstructed material must retain the certified loop/hole counts and agree with
its prior area under the existing `AreaError` bound. Per-region geometric or
provenance failures produce Unresolved drawing loops, no region or hatch, and
explicit diagnostics/counters. Proven neighbours remain independent. Failed
unions also have no certified fill. Partial native intervals/loops are disposed
on refusal. A failure of the frame conversion itself conservatively refuses its
participating regions, with visible Unresolved ink.

Eight new licensed regression cases cover the three circle placements, open-loop
refusal beside a proven square at origin/1 km, ring area/hole truth and hatch
origin under placement at origin/1 km, and refusal of world precision already
lost before translation. The focused run passes all eight.

Replay saved raw plans without repeating visibility computation with
`worker-bench presentation <resolver> <raw-offset-folder> <new-output-folder> [index]`.
Omit the index to present all ten. The probe checks region/loop/hatch counts,
source membership and material area, and byte-identical borrowed input before
and after presentation; it saves each presented drawing and process receipt.

## Combined regression acceptance

Debug and Release solution builds and all four research/bench harness builds
pass with zero warnings/errors. The forced hook passes.

| Actual runtime | Core | UI models | Licensed Rhino | Failures | Optional skips |
|---|---:|---:|---:|---:|---:|
| .NET 8 | 367 | 72 | 303 | 0 | 1 |
| .NET 10 | 367 | 72 | 303 | 0 | 1 |

The sole skip is the opt-in capture profiler. The unfiltered
`Test-WalkingSkeleton.ps1` run includes #40's offset topology/area/locus,
input-precision and exact-archive reuse tests; the dense-mesh cap; capsule;
box/near-tangent-sphere marker; sphere meridians; contact/provenance and Hidden
verification regressions. Capsule and marker cases now also run at translations
0, 1e5 and 1e6. A new sphere-meridian regression at 0, 1e5, 1e6 and 1e7 checks
analytic area, successful retained clips, unchanged source archives, zero slab
budget hits and continued near-tangency refusal.

The warmed, alternating-order offset validation median guard passes on both:

| Runtime | Origin median s | 100 m median s | Offset / origin |
|---|---:|---:|---:|
| .NET 8 | 0.049798 | 0.056013 | 1.125 |
| .NET 10 | 0.054500 | 0.051606 | 0.947 |

Logs: `walking-skeleton.log`, `hook.log`; the six TRX files and SHA-256 receipts
are in `test-results/`. An earlier hook attempt overlapped the active test host
and hit its locked GHA; the serial rerun passes with zero warnings/errors.

## Ten-plan offset rerun

Release, .NET 8.0.34, Rhino 8.36.26296.17001. Same saved v2 mm models and ten
planes as #40; one serial local reference followed by one cold production pool
batch at each placement. All own hosts and pool batches share serial admission.
No worker count/cap overrides, tolerance changes, installation or owner Rhino
interaction. Worker timing includes startup/handshake, transfer, preparation,
compute and returned payloads; strict hashing is outside that timer.

| Offset | Local reference s | Worker batch s | Workers | Strict local/worker hashes | Presentation s |
|---|---:|---:|---:|---:|---:|
| Origin | 39.781 | 14.491 | 8 | 10/10 | 1.634 |
| 100 m | 43.315 | 16.982 | 8 | 10/10 | 1.721 |
| 1 km | 56.320 | 18.385 | 7 | 10/10 | 1.749 |

Worker offset/origin ratios are 1.172 and 1.269; local ratios are 1.089 and
1.416. Both stay below #40's 1.5 offset target. Validation is 0.267 / 0.276 /
0.287 s summed over ten plans, with nine exact local shape validations per
process at every view and zero slab/shared-topology budget hits. Source/geometry,
classifications, depth, loops, provenance, hatches, quality and settings match
strictly in all 30 worker replies. Each ten-plan presentation retains all 1,530
regions and 1,530 hatches, with loop counts, source membership and areas agreeing
and the borrowed raw drawing unchanged. All 30 raw drawing archives are also
byte-identical to the initial rebased engine before the presentation fix.
Presentation timing is separate from local compute and worker batch timing.

#40's recorded cold worker times were 14.570 / 18.368 / 17.578 s (7 / 8 / 8
workers); its local totals were 47.398 / 52.123 / 59.224 s. These are n=1 shared
machine observations with different automatic admissions. The offset target
and warmed validation timing guard both pass; this does not establish a
controlled before/after speed change.
`offset-summary.json`, the three `offset-*` folders, process watches, exact
requests/results and raw timing/Info records preserve the measurements.

## Gate and Hidden audit

The final engine's fresh 50-view census and independent restored-Hidden audit are complete.
Every process receipt reports normal exit, no solve failure, and the same
compiled engine/observer. All 139,310,465 expected body/ray pairs were checked:

| Hidden audit | Count |
|---|---:|
| Samples | 44,051 |
| Blocked | 43,187 |
| Margin/indeterminate | 864 |
| Wrong Hidden | 0 |
| Cut-end containment tests / hits | 1,577,478 / 117,904 |
| Uncastable cut rays (kept indeterminate) | 215 |
| Cap contact margins | 221 |

The observer uses every owned body, including self, with the centre ray and four
4-tolerance offsets. Original closed-solid containment at the cut independently
accounts for cap material. It skips 782 unchanged straight Hidden supports only
after continuous endpoint agreement at coordinate roundoff. These samples detect
contradictions; they do not certify unsampled spans or promote uncertainty.
`hidden-summary.json` binds all 50 audit files and receipts by SHA-256.
All 50 complete census dumps (pieces, regions and diagnostics) are byte-identical
to the initial rebased engine before the presentation fix. Their before/after
SHA-256 values are retained in `compute-equivalence.json`.

The fresh light gate passes **219/219 required views, 0 known bugs, 0 evidenced
wrong-Visible and 0 wrong-Hidden witnesses**. Every required process receipt is
complete. The gate binds the final Hidden audit to the same drawing-engine hash.
No new region contradicts the analytic area/loop or independent section tests,
or the gate's region-area/loop comparisons.

All 160 strict boundary flags have fresh hash-bound independent witnesses:
ten small planar/section points, M-blocks SW's 56 interval/point witnesses plus
two points on the actual saved native-output curves, and M-exploded SW's 92
interval/point witnesses. Mixed witnesses include all-pairs planar point checks
and every curved native body with the centre and four offset rays. Distance
tolerances are unchanged. These certificates cover the disputed samples within
drawing tolerance; they are not full-view or unsampled native-span certificates.
Raw gate rows remain unchanged. Complete planar reference agreement clears eight
further cases. **24 comparisons remain Unresolved/Info**, with 32,197 unclassified
samples; none is promoted to certified Visible. No full-inventory verdict is
claimed beyond the requested light gate.

`light-gate/merge-gate.json`, the raw `routing/` inventory/receipts, independent
reference and interval certificates, and `acceptance.json` retain the verdict.
Final merge-gate SHA-256:
`b7b9227d1911482a4d014d2ba602e3e703029f18d33118fdcbb888d8cffe80b1`.

Current engine SHA-256:
`246a740b6320c4eeb3e3bd8e985f52c9dae60a3ed0d29ad136d214f76297da37`.
