# M2 section wiring

Implementation branch `m2/wiring`, based on `m1b/baseline` (`f3975ec`). Work is
uncommitted for Claude review. No UI files, installation, commit, push, bake,
Target placement, Beyond/fade or Settings wiring is included.

## Viewing the result

Run `./tools/render/Render.ps1 -Engine m2`. The default destination is
`artifacts/render/m2/index.html`, with individual SVG/PNG files and `results.json`.
The four 305-building views are `repeated_building_305-Plan-storey`,
`repeated_building_305-Long-section`, `repeated_building_305-Box` and
`repeated_building_305-Cropped-plan`. RHS, CHS and section-style fixtures are also
included. All geometry is from the actual capture/compute boundary; these are
CPU drawing renders, not Rhino viewport captures or full-output correctness claims.

Regions render before linework, with even/odd holes, solid source-colour poché,
style backgrounds and simple line patterns. Pattern rotation/spacing/scale are
represented; the inspection renderer simplifies dash shapes/origin and clamps
spacing to two pixels. It is not a pattern export/bake implementation. Source
colours in the unstyled research building are black. The existing M1a/M1b sheets
are preserved. The final refresh contains **16 views**, with no execution errors,
quarantine or invalid extraction. The 305-building's Plan/Long section/Box/Cropped
plan contain respectively 60/55/60/36 region-and-hatch branches. Their measured
compute/extraction times were 3.771/5.168/5.767/5.701 seconds (single serial trials,
not timing gates). `results.json` matches the final compiled engine SHA-256 and
records the Rhino/runtime/input hashes.

## Host boundary and view convention

`ViewRequest.Parse` accepts GH Plane/Rectangle/Box values (via the existing GH
adapter), clipping-plane objects, surfaces, references/GUIDs and names. Text still
resolves named view, then selected clipping plane, then direction. The component
change for references is confined to View input parsing.

Plane +Z faces the viewer; retain signed distance <= 0 and look along -Z. Rectangle
retains its own plane/axes and supplies the crop. Box moves its plane origin to
`Z.Max`, retains XY intervals and uses `Z.Length` for depth. Clipping planes use the
stored `ClippingPlaneSurface.Plane`, including flipped normals, without moving or
nudging it. Source depth is used only when enabled. Auto depth otherwise lies ten
tolerances beyond the scene bound, preventing accidental exact far contact. Native
flipped-plane parity follows the S2 convention; the licensed flipped test checks
which world-side marker survives.

`CutFrame` contains detached plane/crop/depth values, object GUIDs, stable layer
GUIDs and the selected plane's style. Participation is object OR layer membership,
inverted for exclusion. Each expanded source carries root/instance/leaf object
and layer identities. Out-of-scope sources remain whole in the same occlusion
solve. No viewport-active clipping planes are imported into compute.

`SectionCapture` resolves `SectionAttributesSource`: Layer, Object, Parent through
the captured block chain, or Sectioner. A Sectioner request uses the selected
plane's own Object/Layer resolution; unavailable plane style uses the source's
layer fallback. The plane is not a blanket style override. Display/print colours
use `DrawColor`/`ComputedPlotColor`, with Parent colours resolved through the chain.
Pattern lines, bases, offsets, dashes, scale, rotation, boundary linetype and all
raw ARGB values are copied. Both Color.Empty and the non-empty `0x00ffffff` sentinel
are resolved deliberately by SectionPolicy. Missing style and explicit no-fill
remain distinct. No live Rhino attributes or table indices cross into compute.
Hatch-table edits join document witnesses and scene revisions.

Referenced Rhino/Model Objects retain host attributes. Inline unbaked Model Object
attributes are still lost by the pre-existing `Value` geometry adapter; extending
that adapter lies outside the allowed component editing boundary. Such geometry
uses the explicit plain-GH ink fallback. Material/parent renderer parity and
interactive clipping-style parity remain manual gates.

## Route C and ownership

`SectionDrawing` runs inside the existing global budgeted scheduler and scene
lease. `SectionRegionEngine.Compute(..., prepareVisibility: true, farPlane: ...)`
retains its certified `PhysicalClipResult`s in `SectionResult.Visibility`. The
integration consumes those very owned pieces; it does not run a second near trim.
The standalone API's default remains unchanged. Optional far clipping occurs in
that same physical preparation, with Original/NearTrim/FarTrim provenance.

One HLD contains retained owned bodies and curves, with **no registered native
clipping planes**. Points bypass native point extraction and ray-test those same
retained occluders, including cap material and hollow voids. Curves are split at
near/far roots. Native Duplicate/Projecting records are not drawing features.
Cancellation, leases and all-view generation publication retain the M1 lifecycle.

Trim/cap or topology failures never substitute uncut geometry. If an original
source's projected bound is available, its footprint quarantines intersecting
strokes/points and section regions; unbounded failures quarantine the view.
Whole excluded foreground bodies that overlap a section region's projected bound
cause that region/Hatch to be withheld and its outline emitted Unresolved. This
is intentionally conservative; partial hatch masking behind excluded bodies is
not certified in this slice.

## Outputs and range

Cut has one branch containing every loop of one material region, including holes.
Open contours use separate outline branches. Hatch has one item or null at each
matching path. The path is `{view; source path; region ordinal}`; document mode is
`{view; 0; region ordinal}`. Ordinals are allocated per input branch in stable source
order. Region and outline numbering inside the engine now uses dictionaries,
removing the previous repeated scan over all earlier regions.

GH outputs duplicate geometry. GH_Hatch receives a detached ModelHatchPattern
constructed from values, and the copied hatch carries `M2D.SectionStyle` JSON for
inspection/consumers. Pattern/colour preview and document bake UI remain M3 work.
A compute hatch's placeholder index is never treated as a live document pattern.

Cut loci remove only native Visible/Hidden **overlap intervals**, using curve/curve
overlap endpoints; a continuation outside the overlap survives. Every hole loop
participates. New trim/cap edges use per-source provenance references, not plane
membership. Pre-existing far-plane edges retain Original ownership.

The built-in range is depth Auto, farclip NoLine, hatch Styles, cuts Separate.
`CutFrame.FarClip` is an internal friend-test seam. With Line, unchanged independent
far-plane intersections are registered for classification against retained bodies;
visible generated far-edge intervals are recovered even when native duplicate
attribution belongs to a body edge. Cut wins coincident intervals. Far lines have
no hatch, hidden far candidates are suppressed, and NoLine suppresses generated
far boundaries only. Exactly zero depth retains sections without participating
background volume. Positive depth <= tolerance remains explicitly uncertain,
with no confident background/far output; it is never coerced to zero.

## Unset investigation and rule

The large-model baseline's Unset count was not itself a count of missing edges.
The census reproduced the suite adapter's exact camera, transforms, native near
plane, tolerance, serial HLD and SubD conversion on Rhino 8.36.26296.17001 / .NET
8.0.34. Input hashes and per-record samples are in
`artifacts/research/unset/census.json`; the command is:

```powershell
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
$suite = 'C:/Users/tomosandego/.t3/worktrees/m2d-plus/t3-3a78edd1-1cd89a1b1b9b38a1'
& $py -B -u tools/research/Inspect-Unset.py $suite artifacts/research/unset
& $py -B -u tools/research/Inspect-Unset.py $suite artifacts/research/unset --extract
```

| Scene/view | Unset | Empty native slots | Real zero-length/duplicate curves | Nonzero ambiguous curves |
|---|---:|---:|---:|---:|
| S blocks, long section | 900 | 900 | 0 | 0 |
| S blocks, South elevation | 0 | 0 | 0 | 0 |
| M blocks, long section | 6,279 | 6,279 | 0 | 0 |
| M blocks, South elevation | 0 | 0 | 0 | 0 |
| Three-source extracted section | 4 | 4 | 0 | 0 |
| Old block_instance, SW (actual M2D+ capture) | 1 | 0 | 0 | 1 |

All 7,179 large-model Unset proxies had Index -1, null parent and a throwing
geometry proxy. They are empty native table slots, not curves whose zero length,
duplicate coverage or clipped-remnant loci can be measured. The public managed
enumerator visits every slot in `m_curve`. McNeel's
[native accessor source](https://github.com/mcneel/rhino3dm/blob/8.x/src/librhino3dm_native/on_hiddenlinedrawing.cpp)
returns -1/Unset for null slots; the
[managed enumerator](https://github.com/mcneel/rhino3dm/blob/8.x/src/dotnet/opennurbs/opennurbs_hiddenlinedrawing.cs)
still creates a proxy for them. This source explanation plus native observations
supports a narrowly counted benign-slot rule, not dropping arbitrary Unset.
There were no measured real missing edges in those four census views; their
complete general visibility remains unknown. The much larger original 352,291
count was not reclassified by extrapolation.

The old block case has a **real parent/source**. Its curve reports length zero,
but domain 0..2, different endpoints and a non-point projected bound. A zero return
from GetLength is insufficient evidence of degeneracy. It remains a counted
invalid extraction; only that source's four strokes become Unresolved, while
five Visible and two Hidden strokes from the other source survive. Whole-view
quarantine is false. The licensed test records the exact metadata.

The production rule is:

1. Unset + Index < 0 + null parent: count an empty slot and drop it without touching
   its geometry proxy. It is not an invalid extraction.
2. A valid curve with exactly zero length **and** an exactly point-sized projected
   bound may be counted/dropped separately. Tolerance-sized nonzero curves do not
   qualify. No sampled case above used this class.
3. Other Unset records remain invalid. A known source bounds uncertainty to that
   source; otherwise an available finite projected curve bound affects intersecting
   output regions. If neither is trustworthy, quarantine the whole view. Uncertain
   points and hatches are withheld. No nonzero Unset is promoted to Visible.

The small extracted fixture (`tests/fixtures/sections/unset-native-slot.3dm`, 69 KB)
contains one RHS and two wall piers from S. Delta reduction retained the original
camera/near plane and reduced 500 sources to three. Its frozen SHA and external
parent/source names are recorded in the manifest/adjacent JSON. The licensed
regression reproduces four empty slots and then runs Route C over the same sources,
checking the independent projection extents x=6..6.38 and z=3..6.

## Validation and remaining gates

On Rhino 8.36.26296.17001, the serial acceptance harness passed **182 Core** and
**115 licensed Rhino/GH tests** on each of .NET 8.0.34 and .NET 10.0.15. A final
owned-region-path copy was additionally checked by the 18 focused wiring tests on
both runtimes. Debug and Release builds passed with **0 warnings / 0 errors**.
The forced Git-sh pre-commit hook passed (Debug + 182 Core tests). The ordinary
solution run passed Core and skipped all licensed discovery cases without starting
Rhino. `git diff --check` passed.
The suite covers the frozen crossing curve, near-cap points, RHS void, include/exclude object
and layer scopes, flipped plane, real component region trees, independent tapered
far perimeter, coincident Cut precedence, exact zero/thin depth, partial-overlap
subtraction, style modes/values, nested Parent identities, the extracted empty-slot
case and the genuine ambiguous block record.

Conservative engine limits remain: arbitrary freeform crossing certification,
mesh/nonplanar-quad fidelity, tiny slivers/tangencies and partial section presentation
behind excluded foreground geometry. UI preview, inline Model Object attribute
transport, Settings/Target/Beyond/fade/bake and installed-Yak interaction are not
implemented by this wiring change. No owner's Rhino process or runtime setting was
attached to, changed or terminated.
