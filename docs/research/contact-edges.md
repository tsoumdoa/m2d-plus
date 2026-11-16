# Contact edges: beta.4 review fixes

Current base: **`894065b`**, the committed contact implementation rebased onto
**`9d4b9a7` / 0.1.0-beta.4**. The review fixes described here are uncommitted.

## Review fixes

- Complete Hidden coverage stops budgeted occlusion clipping with either joints
  value. Joints off then performs only contact checks behind a filtered world-plane
  distance test, without charging `MaxFacesPerEdge`. A failed suppression check
  retains independently proved Hidden coverage.
- Flush requires explicit certified outward normals from closed, outward-oriented
  solids/meshes. Open/unoriented sources carry null normals; neither kernel nor
  independent oracle substitutes triangle winding. The mirror regression now uses
  document capture, block transformation, orientation normalisation and Identity
  extraction, matching production.
- Full world polygons are lazy and created only inside the contact band. They are
  not globally prepared/validated as a second set of occluders. Approximate contact
  in a cell takes precedence over exact Flush suppression.
- Info and the contracts disclose that the tie is retired **between source objects
  only**. Same-source/self-face ties remain, including one mesh building or a
  multi-lump Brep.
- Cut-only views omit joints from the effective visibility key. Fast mode retains
  it: fast still uses the exact planar kernel, and a cache regression demonstrates
  different joint output with on/off. Removing its key would reuse the wrong drawing.
- Regressions include the stacked-slab budget probe, missing/wrong open orientation,
  real mirrored preparation, axonometric bearing ownership and third-object hiding,
  far coordinates, byte-identical section regions/hatches, mixed exact/Approximate
  cells, and active/inactive cache behavior.

## Current review verification and measurements

Both unfiltered `Test-WalkingSkeleton.ps1` runtime passes succeed on the rebased
code plus fixes: **461 Core, 74 UI-model and 313 licensed Rhino tests per runtime**,
with one opt-in capture-profile skip. This includes persistent-worker byte identity
and the new contact/section/cache regressions. Debug and Release solution builds,
the licensed and license-free gate tools, and the forced local hook pass with zero
warnings/errors. The current run records are under `artifacts/contact-review-894065b`.
The fresh **light gate passes 223/223 required views**, with zero known bugs,
zero proven wrong Visible results and zero legacy `tieConvention` exceptions.
It retains **29 Unresolved/Info cases**. The stricter verified whole-output
comparison remains 186/223 passing rows, with 49,248 unclassified comparison
samples; this is not a general native/freeform or full global-beta certificate.
All rows share the fixed Core/Rhino/harness fingerprints. The 160 independently
certified tolerance-boundary samples comprise ten small planar samples and
58 / 92 mixed perf-M samples; mixed certificates include complete planar
interval/interior-point checks and complete captured curved-body ray checks.

The durable review archive is
`~/.t3/m2d-plus-models/evidence/contact-review-894065b-20261116`. It contains the
fresh raw rows/certificates, light-gate receipts bound to archived paths, strict
off-gate, original/fixed timing binaries and observations, native transformed
captures/census, full test logs/TRX, source snapshot and SHA-256 manifest.
No review fixes were committed, pushed, installed or published.

### Before/after timing observations

Before is the engine at `894065b`, preserved before editing production code. Both
builds use identical timing instrumentation, Top view, hidden on, beyond off, one
observation per cell, a retained prepared scene, and on then off in that order.
View-solve wall time excludes file opening/capture/preparation, result dumps, GH
publication and worker IPC. Both kernel time and total solve time are reported;
the machine was shared and load/CPU/memory receipts are retained. These are
descriptive observations, not a calibrated speedup or throughput claim.

| Fixture | Objects | Joints on: before → after (s) | Joints off: before → after (s) |
|---|---:|---:|---:|
| perf-M blocks | 5,000 | 8.087 → 9.466 | 13.956 → 8.916 |
| perf-M exploded | 5,000 | 8.089 → 8.340 | 13.125 → 8.713 |
| Generated small Breps | 50,000 | 25.282 → 25.066 | 19.776 → 19.881 |
| Real terrain campus | 2,983 | 193.268 → 191.528 | 185.383 → 204.914 |

The 527 MiB terrain campus has about 14 million mesh triangles; it is **not** the
50k-object fixture. Both were measured to avoid conflating them.

| Fixture | Kernel on: before → after (s) | Kernel off: before → after (s) |
|---|---:|---:|
| perf-M blocks | 2.125 → 2.056 | 7.311 → 2.964 |
| perf-M exploded | 1.882 → 1.778 | 7.448 → 2.756 |
| Generated small Breps | 8.579 → 7.813 | 6.065 → 5.841 |
| Real terrain campus | 0.324 → 0.249 | 2.351 → 0.752 |

The stacked-slab regression proves the budget correction independently of timing:
with ten slabs and `MaxFacesPerEdge=4`, both modes remain Hidden after **one**
budgeted face test; off still finds and suppresses a later certified Flush span.
The campus remains native-dominated and its observed off solve is slower overall.
No full-campus improvement is claimed.

Current frozen Release SHA-256:

- Core: `7F80EF2428831C1C303323013294A8B61DE7770E4438A892BCE9934A000D44DF`
- Rhino: `ACD084FA04052EDB06F2B283C0486513445E15246B9FB6EE64B66E136A4D4ACA`

### Native transformed-geometry census

Both perf-M forms are captured and prepared by Rhino. Owned geometry and the Top
view are then transformed together, followed by production-style **Identity**
extraction. Translation is `(5e6, 5e6, 5e6)`; rotation is `0.37` radians about
axis `(1, 2, 3)`. There is no plane fitting, geometry repair or HLD solve in this
census. Source IDs and local topology/adjacency, rather than global array indices,
match retained candidate edges when eligibility changes.

Counts below are candidate-edge occurrences in the extracted planar subset.
Seated/Flush/Approximate kinds can overlap on an edge. They are not final drawing
records or a certificate for the remaining native sources.

| Form / transform | Planar objects / 5,000 | Seated | Flush | Approximate | Contact edges with Unresolved spans, joints on | Previously Visible edge with a new Unresolved span* |
|---|---:|---:|---:|---:|---:|---:|
| Blocks / identity | 4,790 | 24,729 | 19,582 | 1,298 | 7 | — |
| Blocks / offset | 3,542 | 21,811 | 18,727 | 0 | 0 | 0 |
| Blocks / tilted axis | 4,790 | 8,840 | 54 | 29,059 | 3,416 | 314 |
| Blocks / offset + tilted | 0 | N/A | N/A | N/A | N/A | N/A |
| Exploded / identity | 4,790 | 24,729 | 19,582 | 1,298 | 7 | — |
| Exploded / offset | 3,542 | 21,811 | 18,727 | 0 | 0 | 0 |
| Exploded / tilted axis | 4,790 | 8,814 | 56 | 29,084 | 3,366 | 312 |
| Exploded / offset + tilted | 0 | N/A | N/A | N/A | N/A | N/A |

\* Positive overlap in edge-parameter space on sources with matching candidate
and face counts and local adjacency. Counts do not mean that each whole physical
edge became Unresolved. Rounded view projection can also create microscopic
nonzero projections of formerly edge-on candidates.

Offset alone loses **1,248** previously planar sources to native extraction;
43,764 edges on 3,542 sources remain comparable. Tilt alone retains all 4,790
planar sources / 58,740 candidates but loses many exact binary64 contact proofs,
which become reported uncertainty. Joints off retains the same 3,416 / 3,366
uncertain contact-edge occurrences rather than suppressing them. Offset plus tilt
leaves no planar census domain at all: its zero raw counters must not be read as
zero uncertainty or successful classification. These eligibility/precision limits
are disclosed, not repaired by nudging or by assuming a native certificate.

The separate strict `joints=off` flush-panel gate passes Top/North/SW with
**154** suppressed-joint witnesses (39 / 19 / 96), zero unexplained samples and
zero wrong Visible findings. It uses the fixed producer, not historical receipts.

Historical results below describe the original implementation, not this review's
acceptance or timing comparison.

## Historical original implementation

The original measurements below used beta.3 / `e091e09ceed13d751138ab536cafd60d46e66384`
on 2026-11-16. That implementation was subsequently committed and rebased as
`894065b`. Its evidence archive remains separate from the review-fix archive.

## Rules

```text
Seated: wall on slab       Flush: continuing faces      Coincident outlines
       | wall |           panel A | panel B             A edge = B edge
       +======+                   |                     one stable owner
  ====== slab ======       joints on: draw once
  draw; occlude normally   joints off: suppress
```

The kernel certifies edge/face incidence, exact coplanarity and oriented material
continuation over polygon event intervals. Symbolic infinitesimal side predicates
include holes and distinguish an opposite-facing bearing face from a continuing
outward face. There are no sampled offsets. Filtered signs fall back to rationals
of the supplied binary64 coordinates.

Contact predicates use owned world vertices and normals before rounded camera
projection. An exactly incident world edge cannot be hidden by numerical depth
error in its own support face; all other faces still occlude normally. Mirrored
normals use the inverse transpose. Interval endpoints retain the existing
quarter-tolerance representability certificate in projected drawing coordinates.

`joints = on | off` defaults to **on**. It follows normal Settings > view-object
`m2d.joints` > menu > built-in precedence and keys visibility. It is in the menu
next to Tangents and Seams; Copy settings and worker settings transport include it.
Only certified flush spans are suppressed. Existing locus reconciliation draws
coincident edges once and retains a real contributing owner in canonical
path/identity order. Kernel contact witnesses reference both contributing source
identities through their edge/face indices. Drawing results retain their existing
single-owner provenance; no `DrawingPiece`, `ViewDrawing` or worker codec fields
were added.

## What remains of the tie rule

The old blanket inter-object `depth gap <= tolerance => Visible` exception no
longer certifies planar contact. A full straight support that could be incident
within the input tolerance, but cannot be certified, is Unresolved unless an
independent occluder proves Hidden. The conservative normal-residual band is
`tolerance * L1(normal)` (up to sqrt(3) Euclidean tolerances). Exact angled faces
remain seated even when their width is below tolerance.

A transverse plane crossing has no positive-length contact interval and keeps
ordinary occlusion, including its depth-tolerance transition. Self-face tolerance
also remains. Neither is a foreign contact certificate.

Native/freeform sources retain native tie and duplicate ownership behaviour in
serial exact mode. Their contact/joint classification is explicitly unverified
in Info, including native-only fallback. Fast retains its existing nondeterminism
disclosure. There is no analytic visibility adapter in this checkout; no analytic
contact certificate is inferred from proximity. Fable's analytic hook must opt
into a whole-support certificate separately.

## Historical verification

The frozen implementation passes both unfiltered `Test-WalkingSkeleton.ps1`
runtimes: **386 Core, 72 UI-model and 285 licensed Rhino tests per runtime**, with
one opt-in capture-profile skip. Both Debug and Release solution builds have zero
warnings/errors. The forced local hook passes.

The added fixtures cover flush panels, vertically stacked wall pieces, wall/slab
bearing (including third-object occlusion), coincident blocks,
partial T-junctions, near-flush gaps on both sides of tolerance, millimetres,
rotations, mirrored normals, oblique projection, thin exact angled faces, ordinary
transverse crossings, joints on/off, repeated solves and persistent-worker byte
identity. The independent comparison oracle also tests foreign incidence and
oriented flush continuity.

The final **light gate passes all 223 required views**: zero known bugs, zero
proven wrong Visible samples, and `tieConvention = 0`. The inventory includes
the additional external view-facade model. All rows use the same frozen Core,
Rhino and compiled gate hashes. The raw whole-output comparisons are preserved:
the stricter verified comparison passes 186/223 rows; the light gate leaves
**29 cases Unresolved/Info** after separate planar references. This is not a
full-view certificate for native/freeform geometry or the full global beta gate.

The independent contact checker accounts for 17,378 contact-rule comparison
samples. Another 195,279 samples are decided by independent exact checks against
an Unresolved native reference; these are samples, not unique edge counts.
There are 160 independently certified tolerance-boundary samples across six
cases. The 150 disputed perf-M samples have complete planar interval/interior
point checks plus every captured curved body's ray checks. A few are endpoints
of owned native `LineCurve` fragments; only their exact straight supports are
certified. General native curves receive no new interval/contact certificate.
Certificates bind inputs, output, complete pair counts and assembly hashes.

A separate flush-panel fixture with `joints = off` passes the strict whole-output
comparison in Top, North and SW. It records **154 independent suppressed-joint
witnesses** (39 / 19 / 96), with zero unexplained samples or wrong Visible results.

## Historical measurements

The whole-output baseline is an untouched build of the commit above. Three
uncached visibility solves of Top, North and SW are compared per model, with the
same owned scene retained between repeats. Class flicker is a class-set change on
matched positive-length curve fingerprints; changing geometry records are
reported separately. Duplicate records use the same positive-length fingerprints,
so zero-length native residues are not counted as drawn duplicate edges. These
are repeatability measurements, not a general freeform coincidence certificate or
a speed benchmark.

The separate planar census replays the prior tie algorithm on identical extracted
geometry by disabling contact metadata. Its denominator is the newly identified
contact candidate set. Counts across views are edge/view occurrences; one edge
may have several contact kinds or visibility classes, so those columns are not
disjoint populations. Native-only facade contact counts are **not certified**, not
zero. Whole-output repeat data still includes those facade models.

### Planar contact census: four views per perf-M form

This table sums Top, North, SW and Long-section. Each form has **83,002 tagged
contact candidate edge/view occurrences**. Both forms identify 75,638 seated,
62,183 flush and 5,211 approximate edge/view occurrences; kinds overlap when an
edge contacts several faces. Visibility columns also overlap for split edges.
These are input-edge counts before full mixed drawing reconciliation and section
presentation, not final output-record counts.

| Model | Visible: prior tie → joints on → off | Unresolved: prior tie → on → off | Contact candidates wholly removed with off |
|---|---:|---:|---:|
| perf-M blocks | 9,820 → 9,004 → 3,120 | 0 → 900 → 417 | 52,394 |
| perf-M exploded | 9,883 → 9,051 → 3,173 | 0 → 916 → 433 | 52,394 |
| curved-facade | Not certified: native-only | Not certified | Not certified |
| view-curved-facade | Not certified: native-only | Not certified | Not certified |

Hidden contact-edge occurrences are unchanged with joints on: 76,988 blocks and
77,014 exploded. With joints off they become 28,019 and 28,016 respectively, as
certified flush spans are removed in every visibility class. The raw candidate
set contains 25,802 / 25,813 exactly coincident view-coordinate 3D instances;
these are input coincidences, not additional output edges.

The diagnostic managed projection union retains a few microscopic duplicate
segments (all at most `3.972054645195637e-15` units): blocks 3 → 2 → 0 and
exploded 3 → 3 → 3 for prior/on/off. Their coordinates and owners are retained
in `duplicateGroups`. This helper precedes full mixed-source reconciliation,
clipping and presentation; final output duplicate counts are measured separately
below, without discarding positive-length records by a tolerance threshold.

### Whole visibility: untouched beta.3 → contact implementation

Counts below sum **view/record occurrences** over Top, North and SW. They are not
unique physical-edge counts. Splitting/merging can change the number of records.

| Model | Visible records | Unresolved records | Projected duplicate fingerprints | Class flicker |
|---|---:|---:|---:|---:|
| perf-M blocks | 16,831 → 16,709 | 325 → 446 | 1 → 1* | 0 → 0 |
| perf-M exploded | 16,874 → 16,753 | 313 → 434 | 1 → 1* | 0 → 0 |
| curved-facade | 1,516 → 1,516 | 122 → 122 | 0 → 0 | 0 → 0 |
| view-curved-facade | 1,516 → 1,516 | 122 → 122 | 0 → 0 | 0 → 0 |

All 12 views completed three runs before and three after (72 visibility solves).
There were **zero class-flickering matched curve records and zero changing
geometry records** in either build. The baseline flicker was not reproduced in
these serial exact-mode runs; this is repeatability evidence, not a measured
flicker reduction. Facade output classes are unchanged and retain native contact
limitations. Additional perf-M uncertainty is explicitly reported approximate
source contact, rather than a blanket Visible tie.

\* Each North form retains one projected fingerprint coincidence of two native
Hidden residues only `1.7763568394002505e-15` units long. The retained world-depth
evidence for the block form places them **12 units apart**, so these are not
coincident 3D shared edges. The case is unchanged from beta.3. Exact shared-edge
fixtures retain one contributing owner and draw the edge once.

## Coordination

- Fable overlap: the `ComposedVisibility` kernel-options initializer and
  `PlanarExtraction` metadata. World geometry is plain Core data; the analytic hook
  remains independent. No result/worker schema extension is needed.
- Opus overlap: shared menu/catalog/settings and UX documentation. Bake-layout and
  view-depth dialog implementation files are untouched.
- Section/region/quarantine implementation files are untouched. The common native
  options helper adds the contact disclosure for all native routes.
- Gate harness compatibility: publication now passes beta.3's captured colour
  dictionary; permutations/subsets preserve adjacency and world metadata. The
  contact gate rejects legacy blanket-tie evidence and records independent
  contact-edge / suppressed-joint witnesses.

## Historical evidence

The durable archive is under
`~/.t3/m2d-plus-models/evidence/contact-edges-20261116-e091e09` (or the corresponding
`M2D_MODELS` root). It contains the final raw routing rows and witnesses,
`light-gate-final/merge-gate.json`, planar census, untouched-baseline repeats,
source snapshot, assembly hashes and passing build/test logs. Generated evidence
and models remain outside git.

Frozen Release assembly SHA-256:

- Core: `0D64C69F67598A6EF678D03B314E7209BF75510E08E3DC971983E93060680603`
- Rhino: `8FDACF5D53541BEF1FBDAD0096E0E7368400764957E4CAFECC2912AB2BF44C8B`

No commits, pushes, package installation or publication were performed.
