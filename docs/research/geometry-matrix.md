# Geometry reliability matrix

Research date: 2026-11-14. This is a fixture study, not a certification of arbitrary buildings or every geometry subtype.

## Build and runtime

- Core SHA-256: `a4433126988cd51b9e779e0894b9c2b9aea7c22ad2de533e148de83c9e9ebc26`.
- GHa SHA-256: `d819af0565e12bed6fc1d5404667b1e036bb7493981fb0643735df6a51d058cd`.
- Rhino **8.36.26296.17001**, runtime **.NET 8.0.34**, Windows, licensed Rhino.Inside. The MVP DLL itself targets net48; no new build was substituted.
- Fixtures use metres, tolerance **0.0001**, parallel top view, view Z toward camera, kept side **world z ≤ 0** for section tests; crop off unless explicitly stated. Hidden on, tangent edges/seams off, native internal multithreading on.
- Each fixture ran raw RhinoCommon HLD and the complete MVP Native/Custom/Auto pipeline both without and with a cut. Raw HLD uses AddClippingPlane; MVP physically trims/caps first. Raw records retain Duplicate/Projecting classifications for diagnosis; these are not output strokes in the MVP.
- Final primary run: [matrix_final.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/matrix_final.json), [log](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/matrix_final.log), [watchdog](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/matrix_final-watchdog.json). It completed in one serial headless worker.

## How to read the table

Engine cells are **no cut / cut** execution statuses. `ok` only means the call completed. `UNSUP` means native AddGeometry declined the type; `REJECT` means MVP validation rejected it; `UNRES` means some intervals remain unresolved; `EXTRACT?` means compute returned but diagnostic extraction found invalid native proxies.

The final column is the tested-property verdict: **P** = stated property passed, **F** = reproducible failure/unsupported requested feature, **U** = runs but correctness not independently established. A P for section area is not a full drawing-quality pass. Open inputs are allowed to produce open section curves; that is not a hatchable region and should be reported as such.

## Results

| Fixture (.3dm) | Raw HLD | MVP Native | Custom | Auto | Verdict and measured notes |
|---|---|---|---|---|---|
| [brep_box](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_box.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P** — Square visible perimeter 8; cut area 4. Custom retains coincident hidden perimeter. |
| [surface_planar_open](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/surface_planar_open.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **F Custom** — Edge-on surface: Native visible length 2, Custom length 4 with one exact duplicate; cut is open, no hatch. |
| [brep_curved_sphere](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_curved_sphere.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic** — Radius-1 silhouette length 2π. After cut, Visible and Cut both contain the same circular locus; style precedence required. |
| [brep_trimmed_curved](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_trimmed_curved.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **U** — Trimmed open sphere runs; closed equatorial region area π. No independent full trimmed-silhouette oracle. |
| [brep_open_box](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_open_box.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P open-cut** — Missing side produces an open 3-sided cut; no fill should be invented. Full visibility uncertified. |
| [brep_concave_polysurface](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_concave_polysurface.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P section** — L profile: cut area 3, one loop. Full occlusion beyond this top view uncertified. |
| [brep_trimmed_planar_annulus](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/brep_trimmed_planar_annulus.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P topology** — Coplanar surface yields two loops, one annular hatch; cut-contact semantics need policy. |
| [extrusion_solid](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/extrusion_solid.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic** — Converted extrusion yields box outline/cut, area 4. |
| [subd_rounded_cube](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/subd_rounded_cube.3dm) | UNSUP/UNSUP | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F direct** — Direct SubD rejected. ToBrep normalization tested separately; no universal SubD accuracy claim. |
| [mesh_closed_quad](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/mesh_closed_quad.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **F cut** — Native/Auto cut Visible length 9 overlaps an 8-unit contour; Custom retraces boundary, length 18. Cut loop itself area 4. |
| [mesh_closed_tri](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/mesh_closed_tri.3dm) | ok/ok | ok/ok | UNRES/ok | ok/ok | **F Custom/cut** — Custom no-cut is Unresolved length 20; clipped mesh has same duplicate-boundary issue as quad cube. |
| [mesh_open_quad](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/mesh_open_quad.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P open-cut** — Sloped quad section is an open line; no hatch. Basic projected outline runs. |
| [mesh_nonmanifold](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/mesh_nonmanifold.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **F Custom; U native** — Custom cut adds an exact duplicate; open branched section, zero closed regions. Native runs, not certified for arbitrary non-manifold input. |
| [line_3d](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/line_3d.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P projection** — XY length 6; cut retains length 3. No area/region expected from a line. |
| [polyline_3d](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/polyline_3d.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic projection** — XY projected length 5.656854; cut length 2.828427. Not a tessellation of a smooth curve. |
| [nurbs_curve_3d](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/nurbs_curve_3d.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic projection** — NURBS retained; projected length 6.059995, cut 3.029997. Shape sampled, not a complete curve-error proof. |
| [circle_3d](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/circle_3d.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic projection** — Tilted circle projects to rational ellipse; length 5.402576, cut half 2.701288. |
| [arc_3d](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/arc_3d.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P basic projection** — Tilted 270° arc remains rational; projected length 4.051932; retained cut arc 2.701288. |
| [pipe_curved](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/pipe_curved.3dm) | ok/ok | ok/ok | UNRES/UNRES | ok/ok | **F Custom; U native** — Custom leaves curved silhouette intervals Unresolved with/without cut. Native/Auto run; closed pipe section hatchable. |
| [rhs_hollow](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/rhs_hollow.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P section void** — Boolean-constructed valid hollow solid; 2 loops, one hatch with inner hole, area 2.04; volume asserted 4.08. |
| [chs_hollow](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/chs_hollow.3dm) | ok/ok | ok/ok | UNRES/UNRES | ok/ok | **F Custom; P section** — 2 circles, one annular hatch, area 0.51π; Custom unresolved. Native/Auto preserve hole. |
| [i_beam](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/i_beam.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P section** — Concave I profile area 1.76, one hatch. An I-beam has a concave open-sided profile, not an enclosed inner loop. |
| [point](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/point.3dm) | ok/ok | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F MVP; P raw** — Raw HLD returns one point, then zero when point is before cut. MVP validator rejects it. |
| [text_entity](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/text_entity.3dm) | UNSUP/UNSUP | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F direct** — Raw AddGeometry returns false; all MVP modes reject. Outline conversion/text preservation not implemented or certified. |
| [text_dot](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/text_dot.3dm) | UNSUP/UNSUP | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F direct** — Raw AddGeometry returns false; all MVP modes reject. No annotation-layout support. |
| [column_through_slab](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/column_through_slab.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P per-object cuts; U visibility** — Two independent closed cuts, areas 0.16 and 16, retained separately despite overlap. Top view does not reproduce oblique penetration failures. |
| [pipe_behind_glass](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/pipe_behind_glass.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P opaque contract; F transparency** — All engines treat panel as opaque; material-aware counterpart saved/tested in controls.json. Transparency is not an engine input. |
| [mesh_terrain_brep_building](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/mesh_terrain_brep_building.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P section split; U full** — Building cut area 4 hatchable; terrain gives 2 open contours. Mixed visibility runs, full truth uncertified. |
| [curve_on_surface](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/curve_on_surface.3dm) | ok/ok | ok/ok | ok/ok | ok/ok | **P contact probe** — Explicit source curve on top face remains visible length 1.6; after cutting z>0 it is removed. |
| [box_probe](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/box_probe.3dm) | ok/EXTRACT? | ok/ok | ok/ok | ok/ok | **P interval; raw extraction note** — Expected probe hidden [-1,1], visible [-3,-1]∪[1,3]. Native boundary error 2.27e-5 < 1e-4; Custom/Auto exact to rounding. Raw cut has invalid Unset proxies. |
| [block_instance](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/block_instance.3dm) | UNSUP/UNSUP | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F direct** — Native AddGeometry does not expand references; MVP rejects. Private headless document supplied real definitions. |
| [block_nested_mirrored](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/block_nested_mirrored.3dm) | UNSUP/UNSUP | REJECT/REJECT | REJECT/REJECT | REJECT/REJECT | **F direct** — Same direct limitation. Two-level definition transforms and mirrored references tested after explicit expansion. |

## Timings for every fixture

Milliseconds, **no cut / cut**, one trial per route in the final primary worker. These small-case figures include wrapper/JIT/thread overhead and are not stable benchmarks. Raw timing includes diagnostic recording of all classes; MVP includes validation, fingerprint, physical clipping, solve and output clipping. `—` denotes unsupported/rejected without a successful geometry solve. Do not compare a rejected call's latency with a successful draw.

| Fixture | Raw HLD ms | Native ms | Custom ms | Auto ms |
|---|---:|---:|---:|---:|
| brep_box | 770.0 / 618.9 | 777.5 / 717.0 | 21.7 / 8.2 | 5.4 / 5.2 |
| surface_planar_open | 278.8 / 315.8 | 230.7 / 287.4 | 12.7 / 2.7 | 320.8 / 295.7 |
| brep_curved_sphere | 810.8 / 630.7 | 763.1 / 572.0 | 485.1 / 136.6 | 786.2 / 507.5 |
| brep_trimmed_curved | 689.1 / 760.3 | 810.7 / 450.1 | 176.8 / 124.9 | 656.5 / 525.8 |
| brep_open_box | 704.6 / 714.4 | 795.4 / 674.4 | 4.3 / 5.7 | 746.8 / 655.7 |
| brep_concave_polysurface | 671.2 / 504.6 | 670.4 / 759.5 | 6.3 / 8.8 | 720.5 / 585.5 |
| brep_trimmed_planar_annulus | 674.6 / 609.9 | 645.3 / 578.1 | 8.9 / 6.0 | 698.0 / 692.2 |
| extrusion_solid | 647.9 / 546.5 | 820.8 / 824.3 | 2.6 / 4.6 | 2.2 / 3.9 |
| subd_rounded_cube | — / — | — / — | — / — | — / — |
| mesh_closed_quad | 672.6 / 692.5 | 801.3 / 635.7 | 1.6 / 3.4 | 663.0 / 683.7 |
| mesh_closed_tri | 752.0 / 687.1 | 709.9 / 650.2 | 2.1 / 2.9 | 699.0 / 533.0 |
| mesh_open_quad | 657.3 / 429.4 | 546.4 / 271.4 | 1.8 / 4.6 | 747.3 / 334.5 |
| mesh_nonmanifold | 652.4 / 391.4 | 528.5 / 349.3 | 2.0 / 3.8 | 594.4 / 427.1 |
| line_3d | 7.4 / 6.5 | 7.8 / 7.8 | 2.0 / 2.0 | 6.9 / 6.9 |
| polyline_3d | 6.2 / 7.8 | 6.7 / 9.3 | 1.1 / 1.4 | 7.7 / 10.4 |
| nurbs_curve_3d | 8.0 / 8.6 | 8.3 / 9.9 | 2.2 / 1.9 | 7.6 / 9.3 |
| circle_3d | 8.2 / 6.7 | 7.1 / 7.2 | 2.0 / 1.6 | 8.5 / 7.6 |
| arc_3d | 6.2 / 9.6 | 9.0 / 8.9 | 2.3 / 2.8 | 12.4 / 9.0 |
| pipe_curved | 530.5 / 352.2 | 467.8 / 455.7 | 180.3 / 117.9 | 479.0 / 463.0 |
| rhs_hollow | 561.1 / 740.7 | 747.7 / 574.3 | 6.8 / 16.5 | 664.2 / 715.6 |
| chs_hollow | 719.0 / 665.4 | 776.2 / 786.2 | 108.0 / 92.0 | 811.6 / 695.8 |
| i_beam | 686.2 / 628.5 | 699.3 / 592.2 | 9.4 / 18.2 | 575.0 / 690.8 |
| point | 6.3 / 4.8 | — / — | — / — | — / — |
| text_entity | — / — | — / — | — / — | — / — |
| text_dot | — / — | — / — | — / — | — / — |
| column_through_slab | 680.8 / 611.5 | 875.1 / 804.7 | 3.4 / 11.2 | 2.2 / 10.6 |
| pipe_behind_glass | 495.7 / 417.9 | 331.9 / 379.8 | 62.0 / 60.6 | 287.3 / 351.4 |
| mesh_terrain_brep_building | 671.5 / 751.9 | 633.2 / 636.1 | 6.0 / 7.7 | 735.7 / 744.2 |
| curve_on_surface | 693.6 / 773.5 | 784.2 / 707.8 | 2.1 / 7.3 | 2.0 / 5.9 |
| box_probe | 754.9 / 667.6 | 916.2 / 815.4 | 2.5 / 8.8 | 2.8 / 7.2 |
| block_instance | — / — | — / — | — / — | — / — |
| block_nested_mirrored | — / — | — / — | — / — | — / — |

## Per-object section and hatch checks

Independent intersections were grouped **per original object**, joined at tolerance, passed to `Brep.CreatePlanarBreps`, then to `Hatch.Create(curves, patternIndex=0, rotation=0, scale=1, tolerance)`. Inner hatch loops and area were measured separately; an outer-loop-only fill was not counted as a hollow-section pass. These hatch objects are from a research prototype, not MVP outputs. Layer/style creation is tested in the 500-object experiment in performance.md.

| Fixture | Closed / open joined contours | Regions | Region area | Hatches / inner loops |
|---|---:|---:|---:|---:|
| brep_box | 1 / 0 | 1 | 4.000000000 | 1 / 0 |
| surface_planar_open | 0 / 1 | 0 | 0.000000000 | 0 / 0 |
| brep_curved_sphere | 1 / 0 | 1 | 3.141592692 | 1 / 0 |
| brep_trimmed_curved | 1 / 0 | 1 | 3.141592692 | 1 / 0 |
| brep_open_box | 0 / 1 | 0 | 0.000000000 | 0 / 0 |
| brep_concave_polysurface | 1 / 0 | 1 | 3.000000000 | 1 / 0 |
| brep_trimmed_planar_annulus | 2 / 0 | 1 | 9.424778077 | 1 / 1 |
| extrusion_solid | 1 / 0 | 1 | 4.000000000 | 1 / 0 |
| subd_rounded_cube | 1 / 0 | 1 | 2.270309404 | 1 / 0 |
| mesh_closed_quad | 1 / 0 | 1 | 4.000000000 | 1 / 0 |
| mesh_closed_tri | 1 / 0 | 1 | 4.000000000 | 1 / 0 |
| mesh_open_quad | 0 / 1 | 0 | 0.000000000 | 0 / 0 |
| mesh_nonmanifold | 0 / 2 | 0 | 0.000000000 | 0 / 0 |
| pipe_curved | 1 / 0 | 1 | 0.177894552 | 1 / 0 |
| rhs_hollow | 2 / 0 | 1 | 2.040000000 | 1 / 1 |
| chs_hollow | 2 / 0 | 1 | 1.602212273 | 1 / 1 |
| i_beam | 1 / 0 | 1 | 1.760000000 | 1 / 0 |
| column_through_slab | 2 / 0 | 2 | 16.160000000 | 2 / 0 |
| mesh_terrain_brep_building | 1 / 2 | 1 | 4.000000000 | 1 / 0 |
| curve_on_surface | 1 / 0 | 1 | 4.000000000 | 1 / 0 |
| box_probe | 1 / 0 | 1 | 4.000000000 | 1 / 0 |

SubD's independent section row converts to Brep first; it does not imply direct SubD support. Curves/points/annotations do not create filled section areas in this test. A planar annulus exactly on the cut returns its boundary loops; whether that counts as cut fill is a product policy. A closed loop from an open surface can be hatchable mathematically without representing a closed solid volume.

## Counterexamples and conversion follow-ups

Evidence: [edgecases.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/edgecases.json), [reuse_validation.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/reuse_validation.json), and [controls.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/controls.json).

1. **Cropped section closure fails in all MVP modes.** Unit half-width box cut at z=0, crop x=[−0.5,0.5], y=[−2,2]: two open horizontal Cut strokes, total length 2, zero closed regions; status `ok`. The filled crop intersection should be a rectangle of area 2, requiring new vertical crop-boundary edges. Curve clipping alone cannot construct that region. This is a concrete blocker for the requested section/hatch contract.
2. **Mesh cut linework is duplicated.** A closed cube's section loop is correctly area 4. Native/Auto also output an 8-unit contour plus a 1-unit stroke on the same boundary. Custom outputs an 18-unit retraced visible polycurve where the simple projected boundary is 8. The exact duplicate detector only finds curves with identical samples; it does not catch partial overlap or retracing, so its zero value does not exonerate this case.
3. **Unsupported normalization is feasible, but must be explicit.** Rounded-cube SubD→Brep completed in all routes with and without cut. Recursive File3dm definition expansion produced two valid leaves for both a simple reference scene and a two-level nested/mirrored scene, and all routes completed after expansion. Fixtures: [converted SubD](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/subd_converted_brep.3dm), [expanded nested/mirrored blocks](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/block_nested_mirrored_expanded.3dm). These tests do not establish exact SubD limit-surface conversion error, correct arbitrary nonuniform transforms, or every mirror/normal orientation case. The mirrored base fixture is a symmetric box; asymmetric mirrored geometry remains an important missing oracle.
4. **Points require their own native extraction path.** Raw HLD classifies a point behind a box Hidden and an external point Visible. They live in HLD.Points, not Segments. The MVP rejects both. Enabling the global native occluding-section option returned zero points, even when per-object occluding flags were restricted to solids. Preserve a separately tested point path; do not adopt this mode globally without resolving that behavior.
5. **Native clipping has observable invalid Unset proxies.** Default native clipping on the box/probe scene returns four Unset segments with no parent and an HldCurveProxy that throws during ToNurbsCurve. The final recorder retains those extraction errors while recording the usable Visible/Hidden output. Enabling occluding sections returned usable probe intervals in the tested scene. Do not call an Unset proxy an omitted visible feature; do not dereference every raw segment without checking parent/classification.
6. **Glass is a visibility-policy gap.** [pipe_behind_glass_material.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/pipe_behind_glass_material.3dm) carries a real object material with Transparency=0.8. SourceItem/AddGeometry receive GeometryBase only, not that material. All routes therefore behave as opaque geometry. The original matrix's glass label was a userstring; the material counterpart is the stronger follow-up. Decide whether glazing occludes, transmits, or produces a separate drawing style.
7. **Tangent/seam control needs broader fixtures.** Sphere/top-CHS and an oblique side CHS view were measured with tangent edges/seams both off and on; this selection did not expose a count/length difference. That does not establish that the controls are ineffective. A multi-face tangent blend with a separately visible seam is still needed. Native hard-codes both flags off; Custom has no corresponding public control and traces Rhino's silhouette flags.
8. **Contact cuts need an explicit volume/face policy.** A box with its bottom at −0.01, 0 and +0.01 relative to the cut was tested without nudging. Together with the inherited large-scene contact reproduction, these distinguish crossing, coplanar contact and excluded geometry. No automatic plane displacement is recommended.

## Rerun

The original harness uses Python 3.12, rhinoinside 0.8.2/pythonnet 3.0.5 and `rhinoinside.load(8, "net8.0")`; no Rhino.Compute service, UI macro, interactive Rhino document or Grasshopper Libraries install is needed. A matching licensed Rhino installation is required. The existing venv executable is read only; `-B`/PYTHONDONTWRITEBYTECODE avoid writing import caches there.

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
$research = 'C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114'
& $py -B -u "$research/scripts/supervise.py" matrix_final 240 12
& $py -B -u "$research/scripts/supervise.py" edgecases 240 12
& $py -B -u "$research/scripts/supervise.py" reuse_validation 180 12
& $py -B -u "$research/scripts/supervise.py" controls 120 12
```

Run these serially. The supervisor samples aggregate native private memory for only its launched worker and descendants, applies a wall/private-memory cap, and can terminate only that owned process tree. It never targets a pre-existing Rhino process. Every result records DLL and harness hashes; the final scratch manifest covers fixture/script hashes. Scripts overwrite their named result/fixture paths on rerun; copy the scratch folder first to retain this evidence set.

Primary scripts: [research.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/research.py), [supervise.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/supervise.py), [edgecases.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/edgecases.py), [controls.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/controls.py). All .3dm fixtures are in the linked scratch `fixtures` folder. No fixture is saved in the repo.

## Exclusions and limits

Early harness pilots had Python/.NET overload mistakes, a point-enumerable Count mistake, text export errors, and a null silhouette-curve recorder error. One text-export pilot exited with a native finalizer access violation after the unsupported export attempt. Those pilots are retained in scratch, excluded from performance/reliability verdicts, and must not be counted as engine failures. Initial RHS/CHS extrusion attempts failed to add an inner profile; those rows were excluded and replaced with Boolean-constructed hollow solids validated by volume, loop count and area in **matrix_final.json**. The final full matrix completed successfully.

This study does not include exhaustive all-view truth for freeform silhouettes, trimmed surface holes viewed obliquely, arbitrary non-manifold topology, general text/dimensions, 50k-object scenes, nonuniformly scaled blocks, or multiple nested material islands. Numeric contour/interval/area checks are stronger than counts, but sampled locus checks can miss narrow features. No interactive GH solve/redraw, native Make2D UI command, or separate stock GH Make2D component was benchmarked; the comparison is the RhinoCommon HLD kernel and the MVP pipelines.

API references: McNeel [HLD](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hiddenlinedrawing/), [parameters and accepted types](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hiddenlinedrawingparameters/), [Hatch](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hatch/). Installed RhinoCommon XML/API reflection was also checked against the actual Rhino 8 runtime.
