# M2D+ requirements (v1)

Agreed with the owner on 2026-11-14. M2D+ is an MIT, open-source Grasshopper plugin for Rhino 8 (Windows, .NET 8) that makes 2D drawings from 3D models better than Rhino's Make2D: faster, more reliable, and much simpler to use.

The Frame2D MVP (`C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/output/frame2d-mvp`) is reference material only. Its engine pieces are ported selectively, after review and tests. Its UX (3 components, about 30 inputs) is rejected.

## Principles

Owner decision (2026-11-16, beta.4): `cutonly = on | off` (also `cut only`, default `off`) in Settings, `m2d.cutonly` view-object user text and the **Cut only** menu toggle. On keeps the section correctness pipeline, Cut/Hatch, Frame/Info and cut-related Unresolved (including refused-cut markers), and skips all projection beyond the cut: no HLD, and empty Visible/Hidden/Beyond/Far clip. Uncut views give Frame/Info only with a remark. It is a visibility cache key; Info says `Cut only: projection beyond the cut skipped` and the drawing footer step is `sections`. Workers use the same behavior and existing codec.

1. **Simplicity first.** One main component. Few inputs, smart defaults, and rare options kept out of the way (right-click menu or an optional settings input). An architect should get a correct plan, section or elevation by wiring geometry and a view.
2. **Never wrong without saying so.** Unresolved or uncertain output is flagged, never shown as visible. Unsupported geometry is reported, never dropped silently.
3. **Never hang Rhino.** Work runs async in the background, shows progress, and stale runs are cancelled when inputs change.

## Scope

| Area | Decision |
|---|---|
| Platform | Rhino 8, Windows only. Product name M2D+ (repo `m2d-plus`). |
| Projection | Parallel views in v1: plan, elevation, section, axonometric. Perspective comes later. |
| Engine | Rhino HiddenLineDrawing only, multithreaded; native visibility is not certified. Physical Route C sections, independent Cut/Hatch certification. |
| View input | All three: Rhino named views/viewports, Rhino 8 clipping planes (with their section styles), and Grasshopper planes/section planes. |
| Placement | Optional target plane: the 2D result is placed on a user-given plane in 3D. No MVP-style docking/anchor system. |
| Output | Grasshopper outputs plus a one-click bake to tidy Rhino layers (Visible / Hidden / Cut / Hatch / Beyond, per source layer). |
| Run mode | Automatic, async, cancellable, with progress on the component. |

## Geometry that must work

Brep (planar, curved, trimmed, polysurface, open), Extrusion, SubD, Mesh (closed, open, quad, non-manifold), Line, Polyline, Curve, Circle/Arc, Pipe, hollow sections (RHS, CHS, I-beams: solids with inner voids), Point, and Block instances (nested, mirrored). Text and annotations are nice to have.

- 3D curves are projected and hidden behind solids like any edge. Points project to points and are dropped when hidden. Native Unset points are suppressed with per-source diagnostics; this failure does not quarantine the view.
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

## Decisions on views and settings (owner, 2026-11-14)

The View Range & Settings editor was rejected: too heavy, and rectangle views still had no proper depth input.
See [UX revision 4](design/ux.md) §2.2 and §6.

1. **Views are editable Rhino objects.** A referenced planar surface, closed rectangle or existing M2D+ view box
   in View is checked by right-click **Convert to view boxes** (named *Validate views* before beta.2): planar, a rectangular outer boundary with four straight
   edges at right angles, trimmed rectangles allowed, not degenerate; a reason per refused object). Valid ones
   are converted **in place** (same object id, one undo) into an **M2D+ view box**: an `Extrusion` whose profile
   is the view frame (and the cut plane where it passes through the model) and whose path runs into the model
   along the look direction (−Z of the frame); its length is the depth, edited by dragging the back face. Default
   depth: to the back of the scene plus a margin (superseded on 2026-11-16 by the depth pop-up and Auto by view type,
   below). **Flip view direction** reverses it. Per-view settings are
   Attribute User Text `m2d.*` keys (`m2d.view = 2` marks new boxes; version 1 retains its legacy anchor); Convert to view boxes writes `default` for missing keys
   and never overwrites a value. The component reacts to object and attribute changes.
2. **Create view from current viewport** bakes a view box matching the active parallel viewport, named
   `M2D View 01`…, on an `M2D+::Views` layer (now `$M2D Views`, see 2026-11-15 below), and adds it to View.
   Perspective viewports are refused with a message.
3. **New fifth input Depth (D)**, appended after Settings (never reorder inputs), list access matched to View, optional:
   the depth of Plane, Rectangle and clipping-plane views as a number or a unit string (`8 m`). Boxes and view boxes
   take their depth from their geometry; a given Depth is then ignored with a remark.
4. **The editor is removed** with its persistence, double-click and S dot. Global defaults stay in the right-click
   menu and the Settings text. Precedence, strongest first: Depth input (depth only) > Settings input > view-object
   user text > menu defaults > built-in default / auto. Double-click selects the view objects in Rhino.
   The preview draws each view's frame, look arrow and depth volume (cobalt `#1F6FD1` when selected).

## Visibility decision (2026-11-17)

Rhino HLD is the only visibility engine, including points, contacts and duplicate ownership.
The earlier exact/composed roadmap is superseded; its archive is `archive/exact-engine`
at `0cfc171` (beta.8). No custom visibility classifier, ray verifier or dense audit remains.
Route C physically trims/caps participating geometry before HLD, preserves holes and
scope, substitutes clear glass feature curves, and builds regions/crop/merge/hatches
independently. Excluded geometry stays whole and occludes. Cut-only and zero-depth
far-clipped views make zero HLD calls; positive sub-tolerance depth remains uncertain.
Failed participating clips demote only overlapping projected footprints and warn by body.
Rejected sources, failed segments and Unset points are skipped/diagnosed without spreading;
detachable Unset curves alone are Unresolved. Whole-view quarantine is reserved for an HLD
solve with no drawing/frame. Cut diagnostics do not affect successfully clipped bodies. Native valid
Unset points are suppressed with per-source diagnostics without view quarantine.

## Ignoring objects seen only through small gaps (owner, 2026-11-15)

- **Setting:** `min visible = <length>` in Settings, or `m2d.min_visible` on a view object. Default `0` (off). Normal precedence applies.
- **Rule:** an object is *ignored* when it is partly hidden in the view and its longest continuous visible piece is shorter than the threshold, measured in 2D. Objects that are fully visible are never ignored, however small (the setting targets modelling gaps, not small details). Points, annotations and section cuts/hatches are never ignored.
- **Output:** all visible lines of an ignored object move to a new, separate **Ignored** output (appended as output 10; the nine existing indices stay fixed), plus an `Ignored` bake layer. Nothing is deleted, and Info reports the count.
- **Cost:** a presentation filter over the per-source classification. Changing the threshold never re-runs visibility. It does not speed up the solve; culling before the solve is a possible later experiment, behind its own correctness gate.
- **Timing:** presentation filtering reuses the cached HLD realization; hidden-off disables it with a grey remark.

## Output trees (owner, 2026-11-15)

- **Setting:** `tree = auto | layer | object`, default `auto`, in Settings, as `m2d.tree` on a view object, and in the menu (*Output tree ▸ Auto · By layer · By object*). Normal precedence applies. It replaces *Per source branch / Branch per object*; saved `branch` reads as `auto`.
- **Auto:** by layer when Geometry is empty or every item is a referenced Rhino object; by object as soon as one item is Grasshopper geometry (mixed input is by object).
- **By layer:** `{view; layerIndex}` (the layer-table index, stable when layers are added; sparse paths are fine). Block parts go to their own effective layer, as in Make2D.
- **By object:** `{view; <Geometry input path…>; item}`, 1:1 with the input items. In document or referenced mode an object is a top-level Rhino object (a block instance is one object), in object-id order.
- **Cut/Hatch** add a trailing region index, `{…group…; region}`, keeping loops and hatch aligned; merged cuts go to the lowest group that took part. Frame and Info stay at `{view}`. All line outputs share the group paths.
- **Key**, a new 11th output (`K`, index 10; the ten earlier outputs never move): one label per group at `{view; group}` (layer full path; object id and name; or the GH input path and item).
- **Cost:** presentation only. Layer and object values are captured on the UI thread; changing `tree` makes no HLD call. Bake keeps its layer scheme. See [UX](design/ux.md) §1.3.

## View box layer and front-face colour (owner, 2026-11-15)

- **Layer `$M2D Views`:** one top-level layer for view boxes, created only when missing (cobalt `#1F6FD1`, No Print).
  An existing layer is never restyled. *Create view from current viewport* puts new boxes there, and *Convert to view boxes*
  moves every converted object and every re-converted view box onto it in the same undo, keeping the object id,
  name and user text. An old `M2D+::Views` layer is not deleted or renamed; drawing bake layers are unchanged.
- **Front-face fill:** a see-through fill on each visible view box's front face, in every display mode: warm red
  `#D9412B` (25 %) when the front face cuts the model (section, plan), cobalt `#1F6FD1` (22 %) when it doesn't
  (elevation, axo). View boxes stay Wireframe-override `Extrusion`s, so the fill is a display conduit registered when
  the GHA loads. It never hides the model's wires and does not change picking or the selection highlight.
- **`m2d.color`** (`#RRGGBB` or `#RRGGBBAA`) overrides the fill per view; `default` or blank is by type. Convert to view boxes
  writes `default` when the key is missing. An invalid value falls back to by type with a remark on the component.
- **Cut detection for colouring** is a cheap, conservative display hint: a visible scene object's bounding box
  straddles the front-face plane and overlaps the frame. It uses an RTree, is cached per view box and is recomputed
  only after document changes (debounced), never per frame. It may show red where the exact cut finds nothing,
  never the reverse. See [UX](design/ux.md) §2.2.

## Pre-beta polish: look, warnings, colours, re-runs (owner, 2026-11-15)

- **Placement:** the component lives in **Params ▸ Util** (its own group, `quarternary`), beside Panel+ and HopperFormat.
  There is no M2D+ tab. The component GUID is unchanged, so saved definitions load.
- **Look (layout B, owner's choice from rendered mockups, 2026-11-16):** no icon in the body; a slim name strip in the
  centre; the six inputs at GH's even spacing snapped to the 20-unit row grid, so every label shares a row with an
  output; a full-width footer with the status on the left, a fixed-width Bake / Cancel / Run button flush right (equal
  inset) and the progress bar along its top edge; one 4-unit margin throughout. See [UX](design/ux.md) §1.
- **Severity:** a warning (orange) means the user has to act: a quarantined view, objects not drawn that could hide
  others, a layer name that matches nothing, a tolerance too coarse for the scale, bad settings. Unresolved pieces in a
  normal run, harmless unsupported objects and the tolerance detail census are remarks. Unresolved output stays in `?`,
  orange in the preview, on its bake layer and in Info. See [UX](design/ux.md) §5.2.
- **Preview colours:** lines take the source object's display colour (`DrawColor`, By Parent through the instance
  path); GH geometry uses ink; class and fade rules apply on top; Unresolved stays orange. *Preview ▸ Object colours /
  Ink*, default object colours. Colours are host-captured plain values; a colour change makes no HLD call. See §3.6.
- **Re-runs:** with Geometry given, showing, hiding, layer on/off and per-viewport visibility never re-run the drawing;
  only changes to what it draws (geometry, layer, colour, style, material, name, view objects) do. Document mode follows
  model visibility. Viewport-only changes never re-run. See [UX](design/ux.md) §5.1a.
- **Layer-name inputs ignore visibility** (owner, answering the open question): `Walls` or `Structure::*` selects every
  object on the matching layers, including hidden objects and objects on layers that are off (or under a parent that is
  off), like referenced objects. Hiding, showing or switching those layers never re-runs; adding, deleting or editing
  objects there, or moving an object onto or off those layers, does. Block parts are still drawn as Rhino shows them.

## Beta.1 feedback (owner, 2026-11-16)

From the owner's first real use of v0.1.0-beta.1 on a ~6,000-object model.

- **Loading is faster and off the UI thread where it can be.** Content fingerprints are computed once per Rhino object
  (by its serial number) and, with normalisation and the scene build, on background threads over owned copies; block
  parts are resolved once per definition; the scene identity is a streamed hash; interactive documents rely on Rhino's
  events instead of re-snapshotting every object. See the capture profile in [UX](design/ux.md) §5.4.
- **Changing settings never reloads objects.** Settings, Depth, Target, Source, menu and view-object changes re-solve from the
  kept scene (no capture, normalisation or preparation); presentation-only settings make no HLD call. See §5.1, §5.1a.
- **View boxes and M2D+ bakes are never scene geometry**, in every mode, recognised by their user text markers (which
  move with them) or their layers; explicitly referenced ones are reported in Info. See §3.1.
- **"Validate views" is renamed "Convert to view boxes"**, with the same behaviour and a one-line result
  ("Converted 3 to view boxes; refused 1: …").
- **The footer says what is being processed and how far along** (loading, waiting, preparing, drawing per view and
  step, workers, output), with a progress bar that only moves forward, a shimmer for stages without sub-progress and a
  rough estimate when there is enough history. See §5.2.

## Rhino HLD settings and workers (owner decision, 2026-11-17)

- Engine and Joints are removed from menus and new view-box/copied settings defaults.
  Explicit saved `engine=exact|native|fast` and `joints=on|off` still load and are preserved,
  but are ignored with deduplicated grey remarks. `default` and built-in defaults are quiet.
  Legacy edits change remarks only: no HLD calls, run/cache/timing-key changes.
- Hidden defaults on. HLD receives the requested flag; hidden edits invalidate projection.
  With hidden off, min-visible is disabled: retain all objects and give a grey remark.
  Tangents/seams default off. Glass clear remains non-occluding after physical clipping.
- Info says `Engine: Rhino HLD; multithreaded; visibility not certified`, with actual flags,
  route, call/extraction/region/hatch/refusal counts and fingerprints. Joints suppression
  is gone; fresh solves may change duplicate ownership/source attribution and Ignored.
- Workers use identical inputs/settings/fingerprints and adapter policy. Threaded solves
  need not have identical bytes. Schema 4 rejects old peers; one result's transport is
  lossless. Geometric fixture gates use declared tolerances, topology, areas and coverage.
- GUIDs, five inputs and eleven output indices/trees remain fixed. Sections, Target,
  preview, Bake, cache leases and complete-current publication remain required.

## View box depth pop-up and Auto depth by view type (owner decision, 2026-11-16, beta.4)

- **Pop-up:** *Convert to view boxes* and *Create view from current viewport* open a small Eto dialog, **View box depth**,
  once per run and before anything changes, when the run makes at least one new view box. It has a depth text box
  (document units or a distance with units, read by the settings unit parser: `8 m`, `3000 mm`, `10'`), an **Auto**
  button (the default) that fills in the Auto value, a one-line hint saying what Auto means for this selection, OK and
  Cancel. One depth applies to every box in the run; Auto gives each box its own Auto depth. Invalid input shows an inline
  error and keeps the dialog open. **Cancel** aborts the whole run with no change and no undo record. Re-converting
  existing view boxes never asks and never changes their depth. It is modal on the Rhino UI thread, owned by the Rhino
  main window, and works from the V and component menus.
- **Explicit exception:** the owner rejected *settings* dialogs (2026-11-14, UX §6.1). This depth prompt is an explicit
  owner exception: it asks one question when a box is made and stores nothing of its own. The depth goes into the
  geometry only (the box's length, edited afterwards with the back face); no new user text.
- **Never blocks:** Grasshopper's non-interactive paths (tests, headless runs, scripts) never show it. Non-interactive
  overloads take an explicit depth or Auto.
- **Auto by view type** (when the user keeps Auto, and whenever no dialog is shown): a **plan** (a horizontal frame looking
  down that cuts the model) is **one storey, 3 m** in document units (3000 mm in a mm file), extended to the back of what is
  inside the frame when nothing lies within that storey. A **section** (any other frame that cuts) and an **elevation** or
  uncut view go to the **back of the geometry inside the frame's extrusion** plus a small margin, counting only objects
  whose bounds overlap the frame's projected rectangle. **Nothing inside:** the frame's longer side.
- **What counts** is what document mode draws: hidden objects and layers that are off don't count; view boxes and M2D+
  bakes never count (the #36 exclusion rule). Bounding boxes in an RTree, never per-face work: fast on 50k objects.
  See [UX](design/ux.md) §2.2.

## Bake layer layout (owner decision, 2026-11-16, for beta.4)

- **Setting:** `bake = view-layer | layer-view | layer | view-type`, from Settings, view-object `m2d.bake` user text, or
  *Bake layers ▸ Per drawing · Merged*. Normal precedence applies.
- **Owner decision, 2026-11-17:** the menu offers only `view-layer` (Per drawing, default) and `layer` (Merged).
  `layer-view` and `view-type` remain text-only options in Settings, `m2d.bake` and saved files. New line-type layers
  shade source display/print colours independently: Cut = 60 % source RGB; Visible/profile and Far clip = source; Hidden =
  half desaturated, 60 % + 40 % white; Hatch layer = 40 % + 60 % white; Beyond = 55 % source + 45 % white; Ignored = 25 % source + 75 % white; Unresolved = orange `#E07A00`.
  Source grouping levels copy their corresponding prefixes' exact colours; classic type parents stay black (except
  orange Unresolved), with source shades only on their source sublayers. Shaded layers carry `m2d.shade = v1`; objects on
  untagged layers (earlier betas, classic type parents) still get class lightening per object. Root/drawing and GH groups stay black. Existing layers keep their
  colours, including earlier-beta layers. Per-object depth fading starts from the layer shade without repeating class
  lightening. `weights = layers` keeps its prior per-object source colour and class/depth fading even on existing black
  layers; section-style cut/hatch object colours and the beta.3 legacy cleanup signature are unchanged. See UX §3.6/§8.1.
- **Layouts** (under the `M2D+` root, source layer paths mirrored as nested sublayers):
  - **`view-layer`, the new default:** `M2D+ › <drawing> › <source layer path…> › <line type>`.
  - `layer-view`: `M2D+ › <source layer path…> › <drawing> › <line type>`.
  - `layer`: `M2D+ › <source layer path…> › <line type>`; every drawing is merged into the same layers.
  - `view-type`: the layout of beta.3 and earlier (`M2D+ › <drawing> › <line type> › <source layer path…>`), kept
    for existing files.
- **The line type is the leaf** (Visible, Hidden, Beyond, Far clip, Cut, Hatch, Unresolved, Ignored). **GH geometry**
  has no source layer and goes under a `Grasshopper (no layer)` group at the source-layer level (classic keeps its old place). Rhino refuses layer names starting with `(` or `[`, so the requested `(Grasshopper)` name became `Grasshopper (no layer)`.
- **Every bake rule stays:** line-type styling when a layer is created; re-baking never restyles a layer the user
  changed (the `m2d.weights` exception still updates unchanged preset widths); per-object fade and fill colours; the
  `m2d.bake` / `m2d.class` markers, so baked geometry is never scene geometry; one undo record; replace-bake.
- **Replace-bake across layouts:** the drawing's previous bake is found by its markers, not by layer path, so re-baking
  with another layout replaces the old objects instead of leaving them on the old layers. Empty layers M2D+ created are
  removed only when unchanged (style signature `m2d.style`, or the classic style for older layers); layers the user
  created or changed are never removed. A view object's `m2d.bake = <layout>` is told apart from a bake marker
  (`<component id>/<drawing>`).
- **Bake time only:** changing `bake` never re-solves (0 HLD calls) and does not re-run presentation; the component
  republishes the drawing it has and the next Bake uses the new layout. See [UX](design/ux.md) §8.1, §8.2.

## Surface view orientation and Source placement (owner, 2026-11-17)

- Untrimmed rectangular planar surfaces (one face, referenced or supplied as geometry) look against their actual face normal. The source plane is centred on the surface, X follows U and Y follows V. A reversed face keeps V up and reverses U to stay right-handed. Frame axes follow the boundary edge closest to U, with an orthogonal Y aligned with V, accepting slight skew within document angle tolerance. Normal and derivatives use the same face parameter space. Frames that cannot be established fall back to model-facing behavior with a remark. Rhino Flip chooses the viewing side; a section removes the normal side. Existing definitions using these surfaces may change direction and placement.
- Trimmed rectangular surfaces and closed rectangle curves retain model-facing behaviour, with a grey remark suggesting an untrimmed surface for normal control. Convert to view boxes preserves an untrimmed surface’s centre, U/V frame and viewing side; existing view boxes keep their orientation. New boxes store the origin schema in Attribute User Text (`m2d.view = 2`), preserving placement through Move, Copy and extrusion rebuilds; legacy version 1 boxes retain their lower-left anchors.
- Sixth optional input **Source (Src)** is a Plane list appended after Depth at index 5. The first five input indices, GUIDs and eleven output indices stay fixed. It acts as Orient A, Target as B: use the Source → Target transform as given without flattening, for every view kind, including a one-Target row. One Source applies to all views or one per view; other counts are an error. Without Target the drawing stays in place, with a grey remark. Empty Source uses the view’s own plane. Source is placement-only and never re-solves visibility; preview, Bake, Frame and Info share placement and Key stays associated with each group.
