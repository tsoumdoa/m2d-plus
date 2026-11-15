# M2D+ requirements (v1)

Agreed with the owner on 2026-11-14. M2D+ is an MIT, open-source Grasshopper plugin for Rhino 8 (Windows, .NET 8) that makes 2D drawings from 3D models better than Rhino's Make2D: faster, more reliable, and much simpler to use.

The Frame2D MVP (`C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/output/frame2d-mvp`) is reference material only. Its engine pieces are ported selectively, after review and tests. Its UX (3 components, about 30 inputs) is rejected.

## Principles

1. **Simplicity first.** One main component. Few inputs, smart defaults, and rare options kept out of the way (right-click menu or an optional settings input). An architect should get a correct plan, section or elevation by wiring geometry and a view.
2. **Never wrong without saying so.** Unresolved or uncertain output is flagged, never shown as visible. Unsupported geometry is reported, never dropped silently.
3. **Never hang Rhino.** Work runs async in the background, shows progress, and stale runs are cancelled when inputs change.

## Scope

| Area | Decision |
|---|---|
| Platform | Rhino 8, Windows only. Product name M2D+ (repo `m2d-plus`). |
| Projection | Parallel views in v1: plan, elevation, section, axonometric. Perspective comes later. |
| Engine | Pragmatic hybrid: Rhino's `HiddenLineDrawing` where it is correct and fast, our own code where it fails or is slow. Uncertain results are flagged. |
| View input | All three: Rhino named views/viewports, Rhino 8 clipping planes (with their section styles), and Grasshopper planes/section planes. |
| Placement | Optional target plane: the 2D result is placed on a user-given plane in 3D. No MVP-style docking/anchor system. |
| Output | Grasshopper outputs plus a one-click bake to tidy Rhino layers (Visible / Hidden / Cut / Hatch / Beyond, per source layer). |
| Run mode | Automatic, async, cancellable, with progress on the component. |

## Geometry that must work

Brep (planar, curved, trimmed, polysurface, open), Extrusion, SubD, Mesh (closed, open, quad, non-manifold), Line, Polyline, Curve, Circle/Arc, Pipe, hollow sections (RHS, CHS, I-beams: solids with inner voids), Point, and Block instances (nested, mirrored). Text and annotations are nice to have.

- 3D curves are projected and hidden behind solids like any edge. Points project to points and are dropped when hidden.
- A geometry-type reliability matrix with fixtures is part of the test suite.

## Drawing features

- Visible lines, hidden lines (separate output), section cuts.
- Cut regions are closed, separate per object by default, with an option to merge touching cuts that share a section style. Hatches come from Rhino 8 section styles (object/layer), with a simple override.
- Depth fade and "beyond" linework (Revit-like).
- Automatic line weights by type: cut > silhouette/profile > visible > beyond.
- Tangent and seam edge control, as in Make2D.

## Scale and performance

- Heavy job: campus/urban models with 50k+ objects. Minutes are acceptable for those, but Rhino must never freeze.
- Typical use draws many views of one model (plans, elevations, sections). Work such as meshing, edges and section curves should be shared across views.
- Models contain many repeated elements (blocks, copies of windows/columns/panels at different positions and rotations). Repetition must be used to save work.

## Test data

Generated building-like fixtures plus the MVP's 527 MiB campus model. No owner project models for now.

## Project setup

Fresh repo with the conduit-plus layout: a Core library with no Rhino types plus xUnit tests, a Rhino-side engine, a Grasshopper project, Yak packaging tools and a local pre-commit hook. Yak is the only install path, even in development.

## Decisions after research (2026-11-14)

Based on [docs/research](research/) and the [UX spec](design/ux.md):

- **Glass** is opaque by default (as in Make2D). An option, or a transparent material or layer, makes glass non-occluding. Lines behind it are then drawn, optionally in a lighter "behind glass" style.
- **Cut policy:** only volumes crossing the plane are cut. Open geometry gives a cut outline with no hatch, and this is flagged in Info. Faces lying exactly on the cut plane are not cut; they draw as visible/beyond lines. The plane is never nudged silently.
- **Crop** must produce closed regions: build the 2D regions first, then intersect them with the crop. The MVP's open cut strokes are a known blocker.
- **Execution is hybrid.** Small and medium jobs run in-process. Big jobs and drawing sets go automatically to headless Rhino worker process(es), for hard cancel, memory caps, crash isolation and parallel views. Licensing, startup and IPC need a spike first.
- **Empty Geometry** draws the whole visible model. The **Settings** input takes `key = value` text. With no section style, a cut is a **solid fill in the object's display colour** (layer colour unless the object overrides it). Rhino 8 section styles (pattern, scale, colours) are honoured from v1 whenever the user has assigned one. M2D+ has no hatch system of its own; `hatch = styles | colour | black | none`.
- **Reopen:** drawings are kept in a disk side-cache and reused if nothing changed. Results are never stored in the `.gh`.
- **Scale works like Revit's view scale** (owner, 2026-11-14). Drawings always stay 1:1 in the model, and placement is 1-to-1 onto Target. `scale` only picks the line-weight preset (default 1:100). Scaling for sheets is done with GH transforms or Rhino Layout details.
