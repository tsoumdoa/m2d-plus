# Per-view mesh face crop

2026-11-18; implementation based on main `c82263cab1c6449797018791762aa1f7bd70e586`.
Changes are uncommitted; nothing installed or published.

## Rule and ownership

`MeshViewCrop` runs after whole-source culling, before route selection/HLD. It
borrows prepared world meshes and owns reduced duplicates until extraction ends.
Original scene bounds and input-derived drawing frames remain unchanged. Each
instance/view gets its own reduction; shared prepared meshes and canonical
definition geometry are never edited. Reduced projection leaves clear their
canonical/placement/definition metadata; section leaves retain all of it.

Only valid meshes with at least 10,000 faces are eligible. Transparent meshes and
ngons pass through: their silhouette/feature preprocessing and polygon edge
semantics have not been validated. No crop, cut-only, full containment or a kept
fraction of 80% or more also passes through unchanged. The existing internal
`CullSources` switch disables both source culling and mesh reduction, including
worker requests; no public setting or transport field was added. Assembly
fingerprints invalidate previous drawing/timing records.

Vertices are transformed by `PlaneToPlane(frame.Plane, WorldXY)`. Keep every face
whose XY bounding box touches the crop expanded by **32 times** the existing
tolerance-plus-128-epsilon arithmetic margin. Add one vertex-neighbour face ring
from that seed set, including unwelded coincident vertices. Test the 80% limit
after adding the ring. Duplicate the mesh and delete other faces without
remeshing, moving vertices, changing normals or clipping Z. A removed face's
whole footprint is strictly outside the expanded rectangle, so its shared new
boundary is outside the output crop; the neighbour ring gives further separation.
Existing output cropping removes the resulting boundary/silhouette strokes.

Participating bodies must classify as `SectionProjectionRelation.Projection`
against the **original** near/far bounds. Crossings, contacts, uncertain bounds
and excluded bodies are not reduced. Scoped nonparticipants also remain whole:
their original bounds gate excluded-foreground Cut/Hatch admission, even though
their linework uses plain projection. Auto far depth lies beyond the original scene;
explicit far planes use the same construction as `SectionDrawing`. Cut/Hatch
admission, physical clips, provenance and canonical section reuse are unchanged.
Info reports dropped faces/mesh count per view; `mesh face crop` records its time.
The Info line appears only for actual reductions. A non-cancellation reduction
failure keeps that mesh whole. Null source depth now uses SectionDrawing's exact
auto-depth plus `ViewRange` construction as well.

## Correctness evidence

Evidence lives outside git at
`~/.t3/m2d-plus-models/evidence/mesh-face-crop/`. One owned Rhino.Inside process
ran at a time; no other Rhino process was attached to or changed. No campus or
curved-M model was used.

The baseline is a `git archive` source snapshot of main, built independently with
the new test harness and an unused helper (main's production call site is
unchanged). Baseline drawings and owned scene archives freeze geometry, source
identities and instance transforms across builds. `main-net8/` and `main-net10/`
hold these controls; `branch-net8/` and `branch-net10/` hold comparisons. The test
loader checks Rhino build/revision, RhinoCommon, architecture and runtime, then
allows only the expected cross-build assembly fingerprint difference. Production
cache/transport fingerprint rejection is unchanged.

Bidirectional, same-class locus sampling uses **0.01 document-unit spacing** and
**0.00125 distance (1.25 x document tolerance)**, with an RTree only to find
nearby target curves. Every fixture must have nonempty linework. Closed box
strips (both face orientations), a closed sphere cropped across its limb, terrain
with a building/background probe, translated instances, a rotated frame, a
section-view projection bypass, and a crossing section body have zero missing,
extra or wrong-Visible samples. Foreground/background probes explicitly exercise
visible and hidden classes. Closed reductions become open without changing these
in-crop classes/silhouettes. Regions are also compared independently by existing
topology/area/loop/hatch gates.

Disabled runs match main's stable drawing bytes on these fixtures; below-threshold
on/off/main also match. Stable archives omit timings/diagnostics; comparisons
exclude the expected assembly fingerprint difference, retaining geometry, depth,
classes, source metadata, regions, frame and counters. No byte-equivalence claim
is made for enabled reductions or arbitrary fresh threaded HLD solves.

## Terrain timing

`branch-net8/synthetic-terrain.3dm`: 115,200 triangles and three buildings, generated outside
git. Rhino 8.36.26296.17001, .NET 8.0.34, 32 advertised logical processors,
threaded HLD, tolerance 0.001. Warm prepared scene, no drawing-result cache;
one warmup per state, then **ABBA**, two measured runs per state. Baseline A has
`CullSources=false`; every source intersects each crop, so whole-source removal
does not confound the face reduction. Crops cover 5%, 25%, 100% of XY frame area.

| Crop | Faces dropped | Wall off / on (s) | HLD off / on (s) | Crop phase (s) |
|---|---:|---:|---:|---:|
| 5% | 108,930 | 0.651 / 0.475 | 0.617 / 0.431 | 0.022 |
| 25% | 84,450 | 0.636 / 0.528 | 0.600 / 0.482 | 0.019 |
| 100% | 0 | 0.676 / 0.655 | 0.638 / 0.618 | <0.001 |

Raw ABBA rows, model SHA256, runtime/engine fingerprints and per-phase Info are
in `branch-net8/timing-*.json`. Wall savings are about 27% and 17% on this small synthetic
fixture; full-crop variation is noise (geometry is unchanged). These short timings do not
predict scanned-mesh or real terrain performance.

## Gates and limits

Debug and Release builds: zero warnings/errors. Core: 487 passed. GH UI models:
76 passed. Forced local hook passed with `.githooks` configured. Licensed .NET 8
selection: 194 passed; .NET 10 selection: 194 passed. The focused main comparison
passes eight tests on each runtime; three additional ABBA timing cases passed on
.NET 8. The benchmark theory is skipped unless explicitly enabled. Final focused
logs are `branch-net8/final-net8.trx` and `branch-net10/final-net10.trx`; broader
logs are `licensed-net8.trx` and `branch-net10/main-comparison-net10.trx`.

`licensed-filter.txt` lists the selected small fixture/section/ownership/culling
suites. Worker-starting cases, GH multi-view worker routes and large smokes are
excluded to maintain the one-owned-process limit. Worker transport is untouched;
the helper is reached through the shared native solve. This is fixture evidence,
not a universal Rhino visibility certificate. Remaining domains include arbitrary
nonmanifold/self-overlapping meshes, unusual native silhouette behavior and
threaded HLD variation. Transparent/ngon meshes remain unchanged.

Reproduce focused checks with `M2D_RHINO_TESTS=1` and the resolver from the Rhino
test README; filter `FullyQualifiedName~MeshViewCropTests`. Set
`M2D_MESH_CROP_EVIDENCE` to the output directory. For actual-main comparison,
first run the harness with main's unchanged production call site, then set
`M2D_MESH_CROP_MAIN` to those saved outputs for the branch run. Set
`M2D_MESH_CROP_BENCH=1` only for the terrain timing theory.

## Review follow-up

The class/locus gates now require matching **Profile and SolvedClass** at the same
locus too, in both `HldFixtureComparison` and the dense sampler. Sample evidence
counts Profile and SolvedClass mismatches separately. Six added fixtures place
90-unit Brep building edges across both crop edges on planar terrain, with 0,
0.002 and 0.1 clearance, in plan and oblique views. The oblique cases independently
assert that the bottom edge is Visible inside the crop. The view helper now aligns
camera direction with its rotated frame; the rotated-frame control was regenerated.

Every comparison expected to reduce asserts both the production Info line and a
lower mesh-face count in `ObserveNativeInputs`; the crossing-section control
explicitly asserts no reduction. Effective settings from the real settings resolver
exercise `crop=auto` overriding a named frame's crop-off value, depth/farclip turning
a named view into a Cut, and depth/farclip replacing a pre-existing Box Cut's values.
Native Brep bounds verify the far plane (12 units behind the resolved named frame,
or Z=-12 for the box). A rectangle case exercises null source depth.

Policy is now `mesh-face-crop-v1` within `HldProjection.Policy`. Follow-up main
controls and comparisons live in `review-main-net8/`, `review-main-net10/`,
`review-net8/` and `review-net10/`; scripts, final source hashes and gate summaries
are in `review-followup/`. The main snapshot's production NativeDrawing blob stays
`60c90414c7eee905c8f5d7d86023ea9c2bcde0df`; only its evidence harness was extended,
with production-reduction assertions inverted because main does not reduce faces.

The Profile hypothesis was **not reproduced** on Rhino 8.36.26296.17001 under
.NET 8.0.34 or .NET 10.0.15. Each runtime compared 18 drawings (17 confirmed
reductions): 254,824 bidirectional samples, including 8,014 Profile=true samples,
with zero missing/extra, wrong-Visible, Profile or SolvedClass mismatches. The six
terrain-edge cases contribute 80,568 samples per runtime; their 12,800 input faces
reduce to 798 in plan and 1,158 obliquely. The existing neighbour ring and feature
eligibility remain unchanged. This extends fixture evidence, not a universal
guarantee about native silhouette flags.

Follow-up gates: Debug and Release builds have zero warnings/errors; the forced
hook passes 487 Core and 76 GH UI-model tests. Each runtime passes 16 focused
MeshViewCrop/main-comparison tests and the 202-test small licensed selection.
Each selection skips only the opt-in terrain timing theory. See
`review-net{8,10}/main-comparison-net{8,10}.trx` and `licensed-net{8,10}.trx`;
build/hook logs and the final manifest are under `review-followup/`. One owned
Rhino.Inside host ran at a time throughout; no other Rhino was attached to or
changed, and no curved-M or campus run was made.
