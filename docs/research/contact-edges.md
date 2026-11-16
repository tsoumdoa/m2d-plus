# Contact edges for beta.4

Implementation against beta.3 / `e091e09ceed13d751138ab536cafd60d46e66384`, 2026-11-16.
The implementation and evidence are uncommitted.

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

## Verification

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

## Measurements

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

## Evidence

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
