# M2 section region compute library

Implemented on `m2/section-regions`, independently of M1a. This is the section compute
boundary, not a completed M2 component. No Grasshopper, scene, snapshot, scheduler,
RhinoDoc, live attribute or document-table access was added. The design follows
[S2 route C](../research/s2-clipping.md), PLAN contracts 4/5 and UX §§1.2–1.3/3.7.

## API and ownership

Core policies and plain values live in `M2DPlus.Core.Sections`. Rhino geometry and
native operations live in `M2DPlus.Rhino.Sections`.

```csharp
var request = new SectionRequest(cutPlane, absoluteTolerance, angleTolerance,
    cropRectangle, CutMode.Separate, HatchMode.Styles);
using var sections = SectionRegionEngine.Compute(sources, request, cancellationToken);
using var visibility = PhysicalSectionClipper.Clip(source, request, farPlane);
```

`SectionSource(Id, Geometry, Style)` borrows caller-owned geometry for the call.
Geometry must remain alive and unchanged until the call returns. Accepts Brep,
Extrusion (converted to Brep), and Mesh. Convert SubD to an owned Brep upstream.
Source ids are nonempty, unique ordinal strings; the host supplies its existing
stable object/instance-path identity. Style values and their lists must remain
unchanged during the call. All native output geometry is independently owned;
dispose the result after its last consumer. Duplicate geometry again for GH outputs.

`SectionRequest` contains only plane, tolerances, optional rectangle, cuts mode and
hatch mode. Plane +Z faces the viewer; look along −Z; keep signed distance ≤0.
Neither the near nor far plane is displaced. Crop must lie on the section plane.
Invalid request/options throw `ArgumentException`. Invalid/unsupported source and
native geometry failures produce diagnostics instead of substituted geometry.
Cancellation throws `OperationCanceledException` and disposes constructed results.
Cancellation checkpoints occur between sources/components/output regions; native
intersection, trim, mesh checking and Boolean calls are synchronous and cannot be
interrupted by this library. Call it through the owning integration's native-work
budget/lifetime mechanism.

`SectionResult` contains:

- `Regions`: material regions and outline branches, with deterministic ids/order.
- `Components`: signed range, classification and certificate description per
  material component. Crossing here is provisional until region/cap checks pass.
- `Diagnostics`: source, status and reason, including overlapping-fill Info and
  the Viewport-background fallback.
- `RequiresQuarantine`: true for any Unsupported/Uncertain diagnostic. Integration
  must honor this; successful completion is not a visibility certificate.

Each `SectionRegion` holds `Id`, `Loops`, exactly one `Hatch` or null, sorted
`SourceIds`, effective `Style`, `Status`, `Area` and `Uncertainty`. Each loop owns a
closed curve, hole flag, and an ordered list of edge intervals. Outline records
can hold open or closed curves, always have OutlineOnly status, area zero and null
Hatch. An explicit no-fill style still retains a material region with null Hatch.

The host maps **one region record to one Cut branch** containing all its loops,
and **one item at the matching Hatch path**. It must not flatten loops or invent a
retraced outer+hole curve. Multiple material islands and crop splits create separate
region records with the same source membership. Outlines have distinct `/outline/`
ids; filled/no-fill material records use `/region/`. No GH paths are constructed here.

## Classification and supported domain

Validity/manifold checks precede material inference. Mesh self-intersection checking
must succeed with no detected perforations/overlaps. Mesh conversion uses planar
face Breps rather than duplicate mesh-plane contours. Brep face-pair intersections
away from shared topology are rejected. Native isolated intersection points must
also lie on both trimmed faces; triangular-face underlying-surface corner points
can otherwise be false positives. These checks are conservative native predicates,
not a general proof for arbitrary freeform self-intersections.

Disconnected shells are checked for intersection and nested by containment using
outward copies. Nested shell orientation must alternate; equal-orientation nested
solids are unsupported, not guessed to be a cavity. Each outer shell keeps its
immediate void shells. Islands inside a void become their own material component.
Disconnected material components are classified separately: two boxes on opposite
sides of the plane do not certify a cut.

Linear planar polyhedra use true vertex extrema, with linear/planar predicates at
a coordinate-scaled floating-point error bound. An exact contact at this bound is
not a modelling-tolerance nudge. Signed extrema inside the ±tolerance band remain
Uncertain unless exact linear contact is established. Roundoff at or exceeding
the modelling tolerance also returns Uncertain. Side/crossing certificates include
that roundoff margin.

For curved material, bounds are broad phase only. Actual vertices must witness both
sides beyond tolerance, faces must be planar/cylindrical, and the section loops and
nondegenerate physical trim/cap must validate. This covers the tested extruded RHS,
CHS and I sections. Unverified freeform/converted SubD crossings return Uncertain,
without fill. Exact curved tangent/coplanar contact certification remains open;
it must not be promoted from sampled signs. Open analytic surfaces give outlines,
even when their intersection is a closed circle. Unsupported/non-manifold material
does not get a region or cap.

## Regions, crop, merge and edge provenance

Per material component: intersection → join → valid closed planar/self-intersection
checks → containment forest (even depth material, odd depth void) → planar material
regions → nesting/area conservation checks. Closed loops alone never authorize fill
for an open source. Components that cannot be certified produce explicit diagnostics.

Crop splits an already built material face with the rectangle. A tessellation
triangle supplies an interior point checked against the trimmed face; area centroids
are not used as material witnesses. Crop can disconnect an object or open a hole into
the outside; the returned boundaries remain closed. Area must be positive above
tolerance² and cannot increase. Numerically unresolved slivers are unsupported.

Separate is the default, preserving overlapping column/slab cuts. Merge runs **after
crop**, partitioned by effective style compatibility. Compatibility compares pattern
line definitions/dashes by value, scale, rotation, origin, display/print colours,
background mode/colours and boundary visibility/width/colours/linetype. Pattern names
alone are not identity. Overrides are applied before comparison.

Merge builds the planar arrangement, selects every atomic cell lying in any source
material, and combines adjacent selected cells. It keeps holes and per-result source
membership, checks area bounds, and retains disjoint islands separately. A failed
merge is diagnosed as Unsupported and returns the separate material records with
RequiresQuarantine set. Incompatible positive-area overlaps remain separate in stable
source order with an Info diagnostic; touching alone is not an overlapping fill.

`EdgeOrigin` flags include PhysicalCut, Crop, Original, NearTrim and FarTrim. Output
edges are split at native curve-overlap endpoints against physical intersection
curves and the crop boundary. Ownership is assigned to those overlap intervals,
including PhysicalCut|Crop where coincident. This does not assign a whole curve's
provenance from a single geometric midpoint. Unattributed region intervals fail
the region; unattributed generated visibility edges fail source preparation.

## Hatch values and detached export

`SectionStyleSnapshot` has no Rhino types or indices. It contains:

- absence versus explicit style/no-fill;
- `PatternDefinition`: name, solid/lines and line angles/base points/offsets/dashes;
- scale, rotation and explicit view-plane origin policy/value;
- pattern/background display and print ARGB values;
- background None/SolidColour/Viewport;
- boundary visibility, width multiplier, display/print colours and linetype values;
- resolved object display/print colours for fallback.

The host must resolve Rhino source modes (including nested Parent and selected
Sectioner scope) before calling. Raw ARGB 0x00ffffff and Color.Empty's zero are
resolved deliberately to source colour; the raw caller snapshot is unchanged.
Boundary defaults use ink. Viewport background resolves to object colour with Info.
Absent style means solid source-colour fill; explicit style with no pattern and
no background means null. `colour` gives solid source colour, `black` solid black,
and `none` null; these overrides reset boundary policy to ink/default width.

Hatches are constructed from outer+inner loops in the request plane, preserving
scale/rotation and using the explicit origin. No view-scale multiplication occurs.
A compute Hatch has a **placeholder pattern index 0**; it must never be inserted
into a live document unchanged. The authoritative pattern travels in Style.
Geometry alone carries no preview/bake colours; the consumer must use Style.

`SectionHatches.CreatePattern` reconstructs an owned Rhino pattern from values.
`AddToFile(File3dm, region)` adds the full definition to the receiving file, remaps
the copied hatch index and applies display/print/background attributes. Background-only
styles use a solid hatch in their background colour. This helper is detached file
export, not RhinoDoc bake. It deliberately avoids name-only pattern reuse. A baked
line-pattern hatch is tested after save/reopen for a real referenced definition,
line angle/offset/dashes, unchanged scale/rotation and object colour.

## Physical route C helper

`PhysicalClipResult` owns `Pieces` (Brep plus interval edge provenance), ordinary
`ContactFeatures` (curves/points), diagnostics, Succeeded and SectionOnly. It is
all-or-failure per source: failure clears every prepared piece/feature for that
source. Nothing silently falls back to uncut geometry. Closed inward copies are
normalised; caller geometry remains unchanged.

Each material component is classified before trim. Behind stays whole; front is
removed. Retained contact volumes stay whole. Removed-side exact coplanar faces
are retained as surface Breps; isolated edge/point contacts are ordinary features,
not Cut/Hatch. Crossing solids are trimmed, then capped with the **complete planar
material regions across all shells** and joined/validated. Capping shells separately
would incorrectly fill enclosed cavities. Output solids must be valid, nondegenerate,
outward and on the retained side. Open Breps trim without becoming filled occluders.
Open meshes are explicitly unsupported for visibility trim/cap. Closed manifold
mesh controls convert to exact planar-face Breps and use the same cap path; this
does not claim arbitrary nonplanar quad conversion fidelity.

Optional far plane must be oppositely oriented, parallel and behind near. It applies
the same halfspace preparation, with no far hatch. Zero depth returns SectionOnly
and no background geometry. Positive separation ≤tolerance is explicitly Uncertain.
Original edges, including ones on a clipping plane, retain Original provenance;
new cap/trim intervals get NearTrim/FarTrim for downstream suppression. Plane
membership alone never deletes an original edge.

## M2 integration work and remaining gates

M1a integration still needs host source/style snapshots, selected-plane participation
and depth resolution, scene leases/scheduler admission, native HLD input registration,
candidate-interval suppression using this provenance, point visibility against the
same capped occluders, generation-gated all-view publication, matching Cut/Hatch
trees, preview colours/boundaries, placement and RhinoDoc bake pattern remapping.
Far-clip line visibility/precedence is also integration work; this helper only
prepares/supplies far edge provenance. No visibility engine is invoked here.

Open gates: general freeform crossing/contact certification; curved same-face
self-intersections; arbitrary mesh/nonplanar quad fidelity and scale; adversarial
tangencies/tiny coincident intervals; near/far projected overlap ownership; excluded
occluder visibility of section candidates; nested scope/source-style viewport parity;
renderer interpretation of unset colours; actual host preview/bake; native .NET 10
host loading; cancellation/resource injection; performance on large face counts.
Face-pair validation and planar arrangements are intentionally conservative and can
be expensive; no campus-scale performance claim is made.

## Validation

The licensed suite uses one owned RhinoCore on one STA queue, serial for the entire
collection. Ordinary test runs skip it and never initialize Rhino. Test expectations
use rectangle arithmetic, circle integrals, counts and point containment independently
of the production region pipeline. Eighteen unchanged S2 3dm files (each ≤2 MB) are
bundled under `tests/fixtures/sections` with hashes in the fixture manifest.

Coverage includes RHS/CHS/I-beam; nested holes/islands and enclosed cavity shells;
crop splitting an L (two area-1 regions); cropped box (area 2); cropped RHS/CHS and
nested circles; overlapping column/slab; touching/overlap merge and style separation;
exact face/edge/point contact and ±0.5/±2 tolerance; open sheet/cylinder; disconnected
opposite-side Breps; closed quad/tri meshes without duplicate loops; unsupported
self-intersecting/non-manifold/open occluders; rigid placement/permutation; overrides,
no-fill, source ownership and embedded line-pattern save/reopen.

Verified 2026-11-14 on Rhino 8.36.26296.17001 with .NET 8.0.34:

- Debug and Release solution builds: **0 warnings, 0 errors**.
- Core: **43 passed**, 0 failed/skipped.
- Gated serial Rhino integration: **29 passed** (28 section tests plus the smoke
  test), 0 failed/skipped.
- Default solution test run: Core 43 passed; all 29 licensed tests skipped.
- Forced Git-sh local pre-commit hook: build and Core tests passed.
- All 18 copied S2 fixture sizes/hashes verified against both manifest and originals
  (554,563 bytes total); `git diff --check` clean.

Run commands and licensed resolver requirements remain in the Rhino test README;
no owner Rhino process is attached to, changed or terminated. No commit, push,
installation or package publication was performed.

## M2 wiring extension

`Compute` now optionally accepts `prepareVisibility: true` and `farPlane` after its
existing cancellation argument. `SectionResult.Visibility` owns the resulting
per-source `PhysicalClipResult`s until disposal. Wiring reuses those certification
pieces for HLD/point classification, avoiding the former trim-and-discard followed
by a second near trim. Standalone callers retain their existing default behaviour.
Region and outline ordinals use keyed counters. See [m2-wiring.md](m2-wiring.md).
