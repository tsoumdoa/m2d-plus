# M2D+ Grasshopper UX (v1 design)

Status: design sub-agent, 2026-11-14, revision 4 (owner decisions on view objects; the settings editor is removed).
Revision 3 decided cut fill and scale; revision 2 covered settings, view range, rectangle views, placement and
section styles. It follows [REQUIREMENTS.md](../REQUIREMENTS.md).
Visual mockup: [ux-mockup.html](ux-mockup.html).
Addendum, 2026-11-15: `min visible` and the tenth output **Ignored** (owner decision in REQUIREMENTS, "Ignoring
objects seen only through small gaps"; §3.9).
Addendum, 2026-11-15: `tree = auto | layer | object` and the eleventh output **Key** (owner decision in REQUIREMENTS,
"Output trees"; §1.2, §1.3).
Addendum, 2026-11-15 (pre-beta polish, owner request): the component moves to **Params ▸ Util** and gets a compact
look (§1); preview lines take the **source object's colour** (§3.6); a **severity policy** keeps normal runs out of the
orange warning state (§5.2); and a **re-run trigger table** stops visibility and viewport changes re-running the
drawing when Geometry is given (§5.1).

**The idea:** one component and five inputs, of which only **View** must be set. Plug in a view and you
get a correct drawing. M2D+ works out everything else from the view source and the document: what
to cut, the crop, the tolerance, the hatches and the line weights. The Frame2D prototype needed about 30
inputs to say these things. Here they are inferred or given a sensible default.

**Views are Rhino objects you can edit.** Reference a planar surface (or a closed rectangle) as the View and
right-click **Validate views**: M2D+ turns it, in place, into an **M2D+ view box**, a box whose front face is the
view frame and whose length is the view depth. You move it, resize it and drag its back face with the gumball
like any Rhino object, and its per-view settings are Attribute User Text keys (`m2d.hidden = off`) in Rhino's
Properties panel (§2.2). Global defaults are right-click menu toggles. The optional **Settings** input takes the
same keys as `key = value` text for parametric control, and **Depth** sets the depth of plane and rectangle views
from a slider or a number (§6).

**What changed in revision 4** (owner decisions, 2026-11-14)

| Area | Change | Why |
|---|---|---|
| Views | **Validate views** converts a referenced planar rectangle into an editable **view box** (an `Extrusion`, same object id, one undo). **Create view from current viewport** bakes one. **Flip view direction** reverses it. The component previews each view's frame, look arrow and depth volume (§2.2). | Owner: rectangle views had no proper depth input, and views should be editable Rhino objects. |
| Per-view settings | Live on the view object as Attribute User Text `m2d.*` keys (§2.2). | Edited where the view is, in Rhino's own Properties panel. No dialog to learn. |
| Inputs | New fifth input **Depth (D)**, appended after Settings: depth of Plane/Rectangle/clipping-plane views from a number or `8 m`. Boxes and view boxes take depth from their geometry (§1.1, §6). | A slider-driven depth was not possible without it. |
| Editor | The **View Range & Settings editor is removed**, with its double-click, its stored per-view rows and the S dot. Double-click now selects the view objects in Rhino (§1, §10). | Owner: too heavy. |
| Precedence | Depth input > Settings input > view object (`m2d.*`) > menu defaults > built-in default / auto (§6). | One simple chain, strongest = most specific and most visible. |

**What changed in revision 3** (owner decisions, 2026-11-14)

| Area | Change | Why |
|---|---|---|
| Cut fill | M2D+ has **no hatch system of its own**. A Rhino 8 section style is honoured when one is assigned (§3.7). With no style, a cut is a **solid fill in the object's display colour**. `hatch = styles \| colour \| black \| none`. This replaces "solid poché at 85 % ink". | Owner: Rhino's section styles and object colours are already the source of truth. |
| Scale | `scale` works like Revit's **View Scale**. It only picks the **line-weight preset** (default `1:100`, §3.6). Drawings always stay **1:1 in the model**, and placement never shrinks them (§4). Sheets are scaled with GH transforms or Rhino Layout details. | Owner: "scale = line weights only". |

**What changed in revision 2**

| Area | Change | Why |
|---|---|---|
| Settings | New double-click **editor** (Revit-style dialog, per-view rows; removed in revision 4). Settings input becomes **tree** access with optional `[view]` headers. | The owner couldn't see how Settings worked. A GH Panel splits lines into items, so "one item per view" was broken. |
| View range | New keys `depth`, `farclip`, `fade`, `fade.start`, `fade.end`, `fade.limit`. `beyond` is kept as Revit's *Bottom* / `<Beyond>` threshold. | Owner asked for Revit's view range and depth cueing. |
| Outputs | New output **Far clip (Fc)**, 9 outputs in all. | "Clip with line" draws a line class that downstream users need to separate. |
| Rectangle / Plane / Box | One rule: the source's plane is the view plane, its size is the crop, `depth` is the far limit. | Owner: "a rectangle frames the model; if it cuts, it's a section". |
| Placement | Anchor is the source's **own plane** (rectangle, plane, box, clipping plane, named view). World origin only for Vector/direction. View[i] → Target[i] is a rigid 1:1 plane-to-plane map. | Owner: "a 1-to-1 relationship". The old world-origin anchor made rectangle sections land in surprising places. |
| Section styles | Written out against the actual RhinoCommon 8 API (§3.7). | Owner asked how section style works. |

---

## 1. The component

| | |
|---|---|
| Name | **M2D+ Drawing** |
| Nickname | **M2D+** |
| Tab / panel | **Params ▸ Util**, beside the owner's Panel+ and HopperFormat (owner decision 2026-11-15). Its own group (`GH_Exposure.quarternary`) after GH's canvas helpers (primary: Panel, Scribble, Panel+, HopperFormat), data-flow tools (secondary) and cluster hooks (tertiary). There is **no M2D+ tab** (no category icon is registered). GH finds saved components by GUID, so moving it never breaks a definition. |
| Description | "Hidden-line plans, sections, elevations and axonometrics from your model and a view. Runs in the background. Reference a planar surface as View and right-click Validate views to get an editable view box; click Bake on the component to bake to tidy layers." |
| Icon | Uses the dark rounded tile that Panel+, HopperFormat and Conduit+ share (tile `#181E1A`, white glyph, 24-unit grid), drawn in code the same way as `ConduitIcons.cs`. **Glyph:** a cube in axonometric with its near corner cut away. The cut face is a solid white poché, the visible edges are white strokes and one hidden edge is a soft (50 % white) dash. It reads as "section + hidden line" at 24 px. **Plug-in logo:** "M+" drawn in the same way as the C+ / P+ marks. |
| Look (2026-11-15) | **Icon, not the nickname capsule.** The component shows its dark tile icon in the body (icon display), so the tall dark "M2D+" capsule is gone. A new component, and a saved one that followed GH's application setting, shows the icon; an explicit right-click *Display ▸ Name* is kept. **Inputs on the output rows:** GH spreads few inputs over the height of many (5 beside 11 landed 44 px apart); the five inputs sit on the middle output rows (G…D beside Fc…F), centred as one block at the outputs' 20 px pitch. **Message tab:** GH's message tab shape, 16 units tall, 4-unit padding, text measured with GH's zoom-independent metrics (as GH measures its own message), so sizes and padding match at every canvas zoom. A long message shrinks to 80 % at most, then ends in an ellipsis; it never runs under the button. The body is at least 148 units wide so `6 drawings · 41 s` and **Bake** fit without shrinking. Renders: `artifacts/ui/` (idle, running, remark, warning at 100 % and 200 %), from `ComponentLookTests`. |
| Custom attributes | Standard GH body, plus (a) a 2 px cobalt `#1F6FD1` progress line along the bottom edge of the body while a job runs, (b) a small **Bake** button at the right end of the message tab (it becomes **Cancel** while running and **Run** when paused), and (c) **double-click selects the referenced view objects in Rhino**, so their `m2d.*` user text is right there in Properties (with no view objects, double-click does nothing special). Nothing else is custom. Revision 3's cobalt dot beside **S** is gone: it meant "the editor holds values", and there is no editor. Menu defaults show as check marks in the menu, view-object values in Rhino's Properties, and Info lists every effective value with its origin. |

### 1.1 Inputs (5)

| # | Name | Nick | Access | Type | Optional | Default |
|---|---|---|---|---|---|---|
| 0 | Geometry | G | tree | Generic: geometry, referenced Rhino objects, Rhino 8 model objects / block instances, object IDs, layer names | yes | **The whole visible Rhino document** |
| 1 | View | V | list | Generic: referenced planar surface, closed rectangle or **M2D+ view box**; text; clipping plane (reference or ID); Plane, Rectangle, Box, Vector, Rhino view object | **no** (pick or choose by right-click, or wire one) | none: the component shows "Pick a view" |
| 2 | Target | T | list | Plane | yes | **In place** (each drawing lies on its own view plane in 3D) |
| 3 | Settings | S | **tree** | Text, `key = value` lines | yes | View objects' `m2d.*` values, then the menu defaults (§6) |
| 4 | Depth | D | list (matched to View) | Text: a number in document units or a distance such as `8 m` | yes | Auto (§3.2). **Appended in revision 4**; inputs are never reordered. |

Description text shown in GH (keep these short, as Conduit+ does):

- **Geometry**: "Scene to draw, shared by every view. Leave empty to draw the visible Rhino model. Also accepts layer names such as `Walls` or `Structure::*`, and block instances (repeats are drawn once and reused)."
  A layer name selects the **visible** objects on matching layers (as before), so showing, hiding or turning those
  layers on and off changes that input and re-runs it (§5.1). Referenced objects and Grasshopper geometry are drawn
  whatever their visibility.
- **View**: "What to draw. A referenced planar surface, rectangle or M2D+ view box (right-click Validate views to make one), a named view, clipping plane name or direction (Top, Front, SW…), a referenced clipping plane, a Plane, a Rectangle (its plane is the view plane and it crops; if it cuts the model you get a section), a Box (cut, crop and depth) or a Vector (look along it). A list gives one drawing per view."
- **Target**: "Optional plane per view. The view's own plane is moved onto it 1:1 (View[i] → Target[i]). One plane for several views lays them out in a row. Leave empty to show each drawing in place."
- **Settings**: "Optional `key = value` lines, for example `hidden = off` or `farclip = line`. They override view-object user text (`m2d.*`) and the right-click menu. One branch applies to all views; one branch per view, or `[View name]` headers, set views separately."
- **Depth**: "Optional view depth for Plane, Rectangle and clipping-plane views: a number in document units or text such as `8 m`. One value for all views, or one per view. Beats `depth` in Settings. Boxes and view boxes take their depth from their geometry."

#### Why each input exists (and why there are no others)

1. **Geometry**: the only thing a user must be able to choose freely, so it can't be inferred. It is optional because the common Rhino case is "draw my model", which is what Make2D does with the selection. Leaving it empty also gives the fastest path for 50k objects. The engine reads `RhinoObject`s directly, keeps block instances as instances so repeats are reused, and avoids wrapping 50k geometries in GH goo. Layer-name strings give the same fast path for subsets.
2. **View**: a drawing has no meaning without one. One smart input replaces the prototype's View component and its 10 inputs (name, capture pulse, plane, width, height, near, far, cut on/off, cut depth, crop), because the type of value you pass says what you mean (§2).
3. **Target**: placement is geometric, often computed (a sheet grid, a strip), and it is the one thing the prototype's Layout component did that people actually need. Seven layout inputs collapse to one plane, because the anchor is the view's own plane (§4).
4. **Settings**: view objects and menu toggles cover hand-set values, but they can't be driven by a slider or computed per view. Settings is text so that one wire carries every key without adding inputs.
5. **Depth**: the one view-range value people drive parametrically (a slider for "how far back do I draw this section"), and the one a Plane or Rectangle has no geometry for. A view box *is* its depth, so it needs no input; a GH plane can't carry one. Writing `depth = 8 m` into a Settings Panel to drive it from a slider was the clumsy workaround the owner rejected. Depth is a list matched to View, like Target, and it is appended after Settings so saved definitions keep their wires.

Not inputs, with where they went: Run, Async, Mode (Native/Custom/Auto), Tolerance, Include hidden, Capture (§2.1, §5);
Near/Far/Cut, Crop (§2, §3.2); Scale (now the line-weight preset key `scale`, §3.6); Anchor U/V, Offset X/Y, Rotation,
Dock to/side/gap (§4); Fade on/start/end/min
(now the Revit-style `fade.*` keys, §3.2); Show hidden (§3.8).

### 1.2 Outputs (11)

| # | Name | Nick | Type | Contents |
|---|---|---|---|---|
| 0 | Visible | V | Geometry (curves + points) | Visible linework: profiles, edges, projected curves and points, nearer than the *beyond* distance. |
| 1 | Hidden | H | Curve | Lines hidden by other geometry. Empty when Hidden lines is off. |
| 2 | Beyond | B | Curve | Visible lines farther than the *beyond* distance (§3.2). They are moved out of Visible, not copied. |
| 3 | Far clip | Fc | Curve | **New.** Where the far clip plane cuts geometry, drawn only with `farclip = line` (§3.2). Otherwise empty. |
| 4 | Cut | C | Curve | Section boundaries. One branch per cut region holding its outer loop and holes (one region per object by default). Open outlines from open geometry get their own branches, with a null hatch. |
| 5 | Hatch | Ha | Hatch (Rhino 8) | One hatch per Cut **region branch**, at the same path (PLAN contract 5): a Cut region branch holds all its loops (outer + holes), and the matching Hatch branch holds exactly one item. The pattern comes from the object's Rhino 8 section style when it has one; otherwise it is a **solid** hatch, filled in the object's display colour in the preview and bake (§3.7). Where there is no fill (a style without fill, an open outline branch, `hatch = none`), the item is null, so the branches stay aligned. |
| 6 | Unresolved | ? | Curve | Pieces whose visibility the engine could not decide. They never appear in Visible or Hidden. Usually empty. |
| 7 | Frame | F | Rectangle | One placed drawing frame per view, at 1:1. It does not depend on the output's bounds (§3.3). For a Rectangle view it is that rectangle, placed (§4). |
| 8 | Info | i | Text | One report per view: source, effective settings and where each came from, counts, styles, unresolved and unsupported objects, timing (§5.4). |
| 9 | Ignored | Ig | Curve | **Appended 2026-11-15.** Lines of objects seen only through small gaps: partly hidden objects whose longest continuous visible piece is shorter than `min visible` (§3.9). They are moved out of Visible, Beyond and Far clip, never deleted. Empty while `min visible` is off (the default). |
| 10 | Key | K | Text | **Appended 2026-11-15.** One label per output group, at the same `{view; group}` path the group's lines use (§1.3): the layer's full path (`Walls::Exterior`) by layer; the object id, then its name if it has one (`3f2a…c9 Column A`), for a referenced object by object; the input path and item (`{0;0} 3`) for Grasshopper geometry by object. Only groups that hold something in that view are listed. |

Outputs are only ever appended, so saved definitions keep their wires: a definition saved with the nine outputs before
Ignored, or with the ten before Key, loads with those unchanged (same indices and ids, so their wires reconnect) and
the missing outputs added empty (Ignored at index 9, Key at index 10). The component GUID never changes.

Line weights, fill colours and depth-cue fading are not outputs. The preview and Bake apply them (§3.6, §3.7). The
curve outputs are plain, so they work with every downstream component. All geometry outputs are at 1:1 in model units.

### 1.3 Output trees

Owner decision, 2026-11-15 (REQUIREMENTS, "Output trees"). One setting, `tree = auto | layer | object` (default
`auto`), says how the outputs split into **groups**. It is in the Settings input, on view objects as `m2d.tree`, and in
the right-click menu as *Output tree ▸ Auto · By layer · By object*; normal precedence applies (§6).

- **Every path starts with `view`**, the index of the view in View's list (a tree wired to View is flattened in path
  order), even for one view. Going from 1 to 3 views never changes the shape of the downstream tree.
- **Line outputs** (Visible, Hidden, Beyond, Far clip, Unresolved, Ignored) use **`{view; group}`**. All of them use
  the same group paths, so a layer or object has the same path in every output, and in **Key**.
- **Cut and Hatch** add one trailing level for the region: **`{view; group; region}`**. `region` counts that group's
  regions from 0, so each region's loops (outer + holes) and its single Hatch item share one path (PLAN contract 5).
  An open outline keeps its own region branch with a null hatch. A merged region (`cuts = merge`) goes to the lowest
  group (in path order) whose sources took part.
- **Frame and Info** stay one item per view, at `{view}`.
- **Key** holds one label per group at `{view; group}` (§1.2).

**`tree = layer`: `group = layerIndex`**, the Rhino layer-table index of each drawn piece's layer.

- The index stays the same when layers are added, renamed or reordered (unlike a sorted position), so the paths of a
  drawing don't move when the model grows. Paths are sparse: `{0;2}`, `{0;7}` and `{0;12}` is a normal tree.
- **Block instances are split by their parts' own layers**, as Rhino's Make2D does. A frame inside a window block on
  layer `Frames` goes to the `Frames` branch, whatever layer the instance itself is on; nested blocks resolve through
  the instance path to the innermost object's layer. Rhino has no layer *By Parent* (only colour, linetype, print
  colour/width, section style and material inherit), so a part on `Default` inside a block is drawn on `Default`,
  exactly as Rhino shows it in BlockEdit and Properties.
- Grasshopper geometry has no Rhino layer. With an explicit `tree = layer` it shares one reserved branch,
  `{view; 2147483647}` (the largest path index, which no layer can use; GH paths can't be negative), labelled
  `(no layer)` in Key. `auto` never puts Grasshopper geometry here.

**`tree = object`: `group = <Geometry input branch path…>; item`.** One branch per input item, so the outputs keep
the input tree's shape and relate 1:1 to what was wired in: input `{0;1}` item 3 draws to `{view; 0; 1; 3}`.

- In **document mode** (Geometry empty) the input path is `{0}` and `item` numbers each **top-level** Rhino object in
  the scene in order of object id, so a drawing of the same model always has the same order; adding or deleting
  objects can renumber the ones after them (Key tells you which is which). A block instance is one object: all its
  parts share its branch.
- A **referenced** item (an object id, or a referenced Rhino object) is also one top-level object. A **layer name**
  item (`Walls`, `Structure::*`) is one input item, so every object it selects shares that item's branch.

**`tree = auto`** (the default) is **by layer** when Geometry is empty (document mode) or every Geometry item is a
referenced Rhino object (ids, referenced objects, layer names), and **by object** as soon as one item is geometry made
in Grasshopper, so mixed input falls back to by object. Info says which it chose and why:
`Output tree: by layer (auto: document mode)`.

Examples, for view 0 of a plan:

```
Document with Walls::Exterior (layer 2), Columns (5), and a window block whose parts are on Frames (7) and Glass (8):
  tree = auto → by layer        Visible {0;2} {0;5} {0;7} {0;8}       Cut {0;2;0} {0;5;0} {0;5;1} {0;7;0} …
                                Key {0;2} "Walls::Exterior"  {0;5} "Columns"  {0;7} "Frames"  {0;8} "Glass"
Three GH boxes, input {0} items 0–1 and {1} item 0:
  tree = auto → by object       Visible {0;0;0} {0;0;1} {0;1;0}        Cut {0;0;0;0} {0;0;1;0} {0;1;0;0}
                                Key {0;0;0} "{0} 0"  {0;0;1} "{0} 1"  {0;1;0} "{1} 0"
The same document, tree = object:
                                Visible {0;0;0} … {0;0;3}               Key {0;0;3} "9b1e…07 W1" (the window, with its name)
```

- Grouping is **presentation only**. Layer and object values are read on the host when the scene is captured, and the
  outputs are grouped from the cached solve: changing `tree` never re-runs visibility and makes no HLD call.
- The old *Output tree ▸ Per source branch* (`tree = branch`, `{view; source path}`) is gone. Saved menu state, user
  text or Settings text with `branch` reads as `auto`; `object` keeps meaning by object.
- Geometry is **never matched item by item to View**. Every view sees the whole scene, so nothing that
  could hide something is ever left out.

---

## 2. The View input: one value, one meaning

**One convention for every source that has a plane:** the plane *is* the view plane. You look along its **−Z**
(plane Z points toward the viewer, Rhino's camera convention), its X is drawing-right and its Y is drawing-up.
Geometry on the +Z side (in front) is removed. If the plane passes through geometry, that geometry is cut and you
get a section or a plan. If it doesn't, nothing is cut and you get an elevation. The far limit is the `depth` setting.

| You wire / choose | View plane | Cut? | Crop (extent) | Far limit when `depth = auto` |
|---|---|---|---|---|
| **M2D+ view box** (referenced, §2.2) | its **front face** (the frame) | if the front face passes through geometry | **the frame** | **its back face** (the box length; always, §6) |
| **Planar surface or closed rectangle** (referenced, not validated yet) | the rectangle, faced at the model (§2.2) | as Rectangle | **the rectangle** | scene extent; Info suggests Validate views |
| **Rectangle** | the rectangle's plane | if it passes through geometry | **the rectangle** | scene extent behind the plane |
| **Plane** | the plane | if it passes through geometry | none (scene bounds) | scene extent behind the plane |
| **Box** | its **+Z face** | if that face passes through geometry | the box's XY | **the box's −Z face** (its height) |
| **Clipping plane** (referenced, model object, GUID or name text) | the clipping plane, looking at the side it keeps | yes | none | Rhino 8 clipping plane *Depth* (`ClippingPlaneSurface.PlaneDepth`) when `PlaneDepthEnabled`, else scene extent |
| **Text: named view** (`"Level 1"`) or Rhino 8 view object | the camera plane at the scene front | no | the view's frame | scene extent |
| **Text: direction** (`Top`, `Front`, `Right`, `Back`, `Left`, `Bottom`, `SW`, `SE`, `NE`, `NW`) | perpendicular to the direction, at the scene front | no | none | scene extent |
| **Vector** | perpendicular to the vector, at the scene front | no | none | scene extent |

So a **Rectangle is "Plane + crop"** and a **Box is "Rectangle + explicit depth"**; a **view box is a Box you edit in
Rhino**. They are the same view with more said. World XY at height *h* is a plan at *h*. A rectangle standing in a building is a cropped section. The same
rectangle moved outside the building is a cropped elevation.

Text is resolved in this order: named view, then named clipping plane, then direction keyword (case-insensitive).
A name that isn't found is an error for that view only. The other views continue (§5, *Partial*).

**Perspective named views** are an error for that view only (§11, Q4).

**Up direction.** A source with its own axes (Rectangle, Plane, Box, clipping plane, named view) keeps them. A Vector
or direction keyword uses world Z projected onto the view plane as up. When looking straight down or up, it uses
world Y (north up).

### 2.1 View right-click menu (on the input parameter)

This works like Conduit+'s `Param_TextOption`. Choosing an item sets persistent data, and choosing is
greyed out while a wire is connected. The three view-object commands also act on wired references.

```
Pick views in Rhino…                 (planar surfaces, rectangles, view boxes, clipping planes)
Validate views                       (§2.2; also on the component menu)
Flip view direction                  (view boxes only)
Create view from current viewport    (bakes "M2D View 01" on the $M2D Views layer and adds it)
───────────────
Named views     ▸  Level 1 · Level 2 · Roof · …          (multi-check: one drawing each)
Clipping planes ▸  Section A · Section B · Plan cut L1 · …
Directions      ▸  Top · Front · Right · Back · Left · Bottom │ SW · SE · NE · NW
All named views
All clipping planes
───────────────
Clear
```

The active viewport is never tracked live, because orbiting would re-run the job. **Create view from current
viewport** freezes it into a view box instead (revision 3's "Capture active view as named view" is replaced: a named
view has no depth and no settings of its own). Picked objects are stored as references to the object id, in a form
that survives Validate; picked clipping planes are stored as their name text, as before.

### 2.2 View objects: view boxes

A **view box** is the view as a Rhino object. It is an `Extrusion`:

```
              look direction (−Z of the frame)
   viewer  ──────────────────────────────────▶
          ┌───────────────────────────────────┐
  front   │ frame = profile = cut plane        │ back face = far limit
  face ──▶│ (crop; cuts what it passes through)│◀── drag with the gumball to change depth
          └───────────────────────────────────┘
          ◀──────────── path length = depth ──────────▶
```

- **Profile** = the view frame (crop), at the path start. It is the cut plane when it passes through the model.
- **Path** runs from the frame into the model along the look direction. Its length **is** the depth; the back face
  is the far limit. Move, rotate, scale or gumball-drag the back face to edit the view. Nothing else stores it.
- **Resolves exactly like a GH Box** (m2d-wiring): cut at the front face, crop to the profile, far limit at the back.
- **Look direction** follows the existing convention: the frame plane's +Z points at the viewer and the view looks
  along −Z. The preview draws an arrow, so the direction is never a guess.
- **Ignored as scene geometry.** View boxes (and any surface the View input references) are never drawn, so a box
  around the model never hides it. In Rhino they get a Wireframe display override for the same reason.
- **Layer `$M2D Views`.** View boxes live on one top-level layer, `$M2D Views` (the `$` sorts it to the top of the
  layer panel). M2D+ creates it only when it is missing: cobalt `#1F6FD1`, **No Print** (plot weight −1), so view
  boxes never print. An existing `$M2D Views` layer is used as it is: the user's later colour or print edits are never
  overwritten. Validate and Create put view boxes there (below); an old `M2D+::Views` layer from earlier builds is
  neither deleted nor renamed. Drawing bake layers (§8.1) are unchanged.
- **Front-face fill.** M2D+ draws a see-through fill on each view box's **front face** (the frame, view plane and cut
  plane) in every display mode, so the type of view shows at a glance:

  | Front face | Fill (opacity) |
  |---|---|
  | **cuts** the model: a section or plan | warm red `#D9412B` at 25 % |
  | doesn't cut: an elevation or axo | cobalt `#1F6FD1` at 22 % (slightly lighter: these faces usually sit in front of the whole model) |
  | `m2d.color = #RRGGBB` | that colour at the by-type opacity |
  | `m2d.color = #RRGGBBAA` | that colour and alpha (`FF` is opaque) |

  `m2d.color` blank or `default` means by type. An invalid value (`purple`, `#12345`) falls back to by type and the
  component shows a remark naming the view; it is display only and never changes the drawing. The fill is a
  display conduit registered when the GHA loads, not part of the object: a view box stays an `Extrusion` (which can't
  hold per-face colour), keeps its Wireframe override, and its back face stays gumball-draggable. It is drawn only
  for view boxes (`m2d.view`) in the active document that are visible (not hidden, layer and parent layers on, and on
  in that viewport or detail). It is unlit, transparent and written after the model without depth, so the model's
  wires show through it; it takes no part in picking, so selection is unchanged. A selected view box keeps Rhino's
  highlight, and the component preview (frame, look arrow, depth volume, below) is drawn as before.
- **How "cuts" is decided for the fill** (a display hint only; the drawing uses the component's exact cut rule, REQUIREMENTS *Cut policy*).
  A front face counts as cutting when some **visible scene object's bounding box** straddles the front-face plane by
  more than the document tolerance on both sides, overlaps the frame rectangle (projected onto the plane) by more
  than the tolerance, and meets the frame's own bounding box. Scene objects are what document mode draws (not
  hidden, layer and parents on, not on an `M2D+` bake layer, no `m2d.*` user text, so no view boxes or baked
  drawings) and that the component can cut: surfaces, polysurfaces, extrusions, SubDs, meshes and block instances.
  The bounding boxes sit in an RTree; each view box's answer is cached and recomputed only after a document change
  (object added, deleted, moved or edited, attributes, layer visibility, block definitions, tolerance), debounced
  200 ms after the last change, and only for view boxes near the changed objects. Nothing is computed per frame.
  Measured headless: 50 176 objects and 300 view boxes build in about 140 ms; one moved object updates in about 1 ms.
  **Differences from the real cut**, all conservative toward red: a bounding box can straddle the plane where the
  object doesn't (an L-shaped object, a block whose parts miss the frame, a box near an inclined frame); the hint
  always looks at the whole visible document, even when Geometry is wired or the view's far limit excludes the
  object; a referenced surface that is not yet a view box counts as scene. A real cut inside the frame always shows
  red. Curves and points never make a face red (the component does not cut them).
- If Rhino turns a view box into a polysurface (some sub-object edits do), the view reports it with the fix:
  undo the edit, or reference a planar surface and validate again. It never guesses the orientation back.

**Validate views** (right-click on V or on the component), one Rhino undo record for the whole run:

1. Checks every referenced object can frame a view: **planar**, an outer boundary that is a **rectangle** (4 straight
   edges at right angles within the document angle tolerance; a split edge is still one edge), **not degenerate**
   (no edge shorter than 10 × the absolute tolerance). Trimmed planar surfaces whose outer loop is a rectangle are
   fine. Each refusal says why: "not planar", "its corners are not right angles (78.7°)", "its outline has 3
   corners, not 4", "a polysurface with 6 faces", "the curve is not closed"…
2. Converts each good one **in place** with `ReplaceObject`: same object id, name and attributes (user text
   included), so the GH reference survives, and moves it onto the **`$M2D Views`** layer (created if missing) in
   the same undo record. References stored on V are re-typed to a Brep reference (a GH *Surface* reference can't
   hold a box). A surface that reaches V through a **Surface or Curve parameter wire** is refused with that reason
   instead of being converted, because converting it would break the wire.
3. **Orientation.** Y (up) is the rectangle edge nearest world Z, or nearest north for a horizontal frame; X makes
   it right-handed; the origin is the lower-left corner (the placement anchor, §4). A **horizontal** frame looks
   down (a plan); any other frame looks toward the middle of the scene. **Flip view direction** reverses either.
4. **Depth: auto**, to the back of the scene's bounding box along the look direction plus 1 % of its diagonal
   (with nothing behind the frame: the frame's longer side). Edit it by dragging the back face.
5. Names unnamed objects `M2D View 01`, `M2D View 02`… and writes the user-text keys below. **Missing keys get
   `default`; an existing value is never overwritten.** Validating a view box again only re-adds deleted keys, and
   moves it back onto `$M2D Views` if it was put on another layer.
6. Reports each object on the Rhino command line and on the component (remarks; refusals stay as warnings until
   the next view command or a change to V).

**Per-view settings: Attribute User Text.** Edit them in Rhino's *Properties ▸ Attribute User Text*. `default`
(or blank) means "not set here": the menu default, then the built-in default, applies (§6).

| Key | Values | Written by Validate |
|---|---|---|
| `m2d.view` | `1` (marker and format version) | `1` |
| `m2d.name` | drawing name (bake layer); `default` = the object's name | `default` |
| `m2d.farclip` | `none` · `line` · `noline` | `default` |
| `m2d.beyond` | `auto` · `off` · distance | `default` |
| `m2d.fade` | `on` · `off` | `default` |
| `m2d.fade.start` / `m2d.fade.end` / `m2d.fade.limit` | 0–100 % | `default` |
| `m2d.hidden` · `m2d.tangents` · `m2d.seams` | `on` · `off` | `default` |
| `m2d.glass` | `opaque` · `clear` | `default` |
| `m2d.scale` | `1:n` (line weights only) | `default` |
| `m2d.weights` | `auto` · `layers` | `default` |
| `m2d.hatch` | `styles` · `colour` · `black` · `none` | `default` |
| `m2d.cuts` | `separate` · `merge` | `default` |
| `m2d.min_visible` | `off` · length (§3.9) | `default` |
| `m2d.color` | `default` (by type) · `#RRGGBB` · `#RRGGBBAA`: the front-face fill (above). Display only, not a setting | `default` |

The values are the Settings syntax of §6.3, so any `m2d.<key>` of §6.3 is read (including `m2d.gap` or `m2d.tree`
if a user adds them); `depth` and `crop` come from the box itself. A bad value is a warning naming the view, with
the closest valid value, exactly as for Settings text. Referenced clipping planes read `m2d.*` user text the same
way. The component re-runs when a view object's geometry, name or `m2d.*` user text changes (§5.1). Hiding, showing or
recolouring a view box, or moving it to another layer, changes nothing the drawing reads and never re-runs it.

**Create view from current viewport** (right-click on V or the component): bakes a view box matching the active
**parallel** viewport's visible rectangle, with its front face just in front of the scene and its back just behind
it (so it starts as an elevation, cutting nothing), names it `M2D View 01`…, puts it on the **`$M2D Views`** layer
(above), writes the user-text defaults, and adds it to V. One undo. A perspective viewport gets "M2D+ draws parallel
views only. Switch the viewport to Parallel and try again."

**Flip view direction** (view boxes): the front face stays where it is and the box extends to the other side with the
same depth; X flips with Z so the drawing is never mirrored. One undo.

**Preview.** For each view the component draws its frame (solid), a look-direction arrow from the frame's centre, the
depth volume (dotted back rectangle and depth edges) and the view's name. Subtle grey-blue normally, **cobalt
`#1F6FD1` while the component is selected**. Boxes and cropped views show at once; plane, clipping-plane and
direction views show once the scene is read. Toggle: *Preview ▸ View frames*.

---

## 3. What is inferred, and how

### 3.1 The scene (Geometry empty = document mode)

- **Included:** normal and locked objects on visible layers. Block instances are kept as instances, nested and mirrored ones too.
- **Ignored without a warning:** hidden objects, objects on hidden layers, lights, clipping planes, named-view
  widgets, cameras, and **anything M2D+ baked before**. Baked objects are recognised by their `m2d.*` user
  strings or by sitting on an `M2D+` layer, so an in-place bake is never drawn again.
- **Reported:** objects that could be drawn but aren't supported, such as text in v1 or point clouds. They appear in Info
  with counts by type: a remark for objects that hide nothing (text, dots, point clouds), a warning for objects that could
  hide others (§5.2).
- **Clipping scope:** when a Rhino 8 clipping plane only clips some layers or objects (its participation lists), only
  those are cut. The others are drawn whole, so the drawing matches the clipped viewport.
- **Updates:** only changes that affect the drawing re-run it (§5.1). In document mode that includes showing and
  hiding objects and turning layers on or off; a viewport-only change never re-runs anything.

### 3.2 View range (Revit-style)

Every view has a **view volume**: the view plane, the crop extent, and a far plane at `depth` behind the view plane.
All distances are measured **along the view direction from the view plane**: from the cut plane for a section or
plan, from the rectangle or plane even when it cuts nothing, and from the front of the scene for named views,
directions and vectors.

```
 viewer   view plane (cut)       beyond             fade start          fade end      depth (far clip plane)
   ▷  ──────────┃──────────────────┊───────────────────┊───────────────────┊───────────┃░░░░░ clipped
               0 %                3 m                 10 %                 90 %        100 %
      Visible ───────────────────▶ Beyond ─────────────────────────────────────────────▶  Far clip line
      intensity 100 % ──────────────────────────────────╲________________________________ fade limit
```

| Concept | Key | Revit equivalent | What it does |
|---|---|---|---|
| **View depth** | `depth = auto \| <distance>` (or the **Depth** input) | Plan: *View Depth*. Section/elevation: *Far Clip Offset*. | Places the far plane. A Box or view box always uses its own length (§6). `auto` = the clipping plane's *Depth* when enabled, else the scene extent, so nothing is clipped. |
| **Far clipping** | `farclip = none \| line \| noline` | *Far Clipping*: No clip / Clip with line / Clip without line | `none`: nothing is clipped at the far plane; `depth` then only sets 100 % for fading. `noline` (default): geometry beyond the far plane is removed and the open ends are left open. `line`: as `noline`, plus the far plane's intersection with the geometry is drawn as **Far clip** lines (own output, own class, own bake layer). |
| **Beyond** | `beyond = auto \| off \| <distance>` | Plan view range *Bottom*, below which lines use the `<Beyond>` line style | Visible lines farther than this go to the **Beyond** output and class (thinner, grey). `auto` = **3 m** (one storey, in document units) in cut views, off in uncut views. Ignored, with a remark, when it is ≥ `depth`. |
| **Depth cueing** | `fade = on \| off` | *Depth Cueing*: Enable | Fades projected lines with distance. Off by default (§11, Q9). |
| Fade start | `fade.start = 0–100 %` | *Start* | Lines nearer than this keep full intensity. Default 0 %. Percent of `depth`. |
| Fade end | `fade.end = 0–100 %` | *End* | Lines at or beyond this reach the fade limit. Default 100 %. |
| Fade limit | `fade.limit = 0–100 %` | *Fade Limit* | The minimum intensity, so far lines never disappear. Default 30 %. |

**How fading looks and is stored.** Intensity at depth *d* is 100 % before *start*, falls linearly to *limit* at *end*,
and stays at *limit* after it. It applies to Visible, Beyond, Hidden and Far clip lines, never to Cut and Hatch (they lie
on the cut plane, at 0 %). Each drawn piece takes the intensity at its **mid-depth**. Curves are not split, so fading
costs no geometry. In the preview the colour blends from ink toward the viewport background. On bake each piece gets an
object colour and print colour blended toward white, quantised to 5 % steps, so the layers stay tidy. With `fade = on`
and `beyond` also active, a beyond line is both thin-grey (its class) and faded (its depth).

**Why Beyond stays and is not merged into fade.** They answer two different Revit questions. *Beyond* is a line
**class**: it decides the output, line weight and bake layer, like Revit's *Bottom* + `<Beyond>` line style in a plan.
*Depth cueing* is a **graphic ramp** on top of whatever class a line has, like Revit's depth cueing in sections and
elevations. Merging them would either make fading change which output a line lands in (surprising downstream), or
make Beyond a fuzzy band. Keeping both costs one extra key and maps one-to-one to what an architect already knows.

**Considered and rejected for v1**

| Revit concept | Decision |
|---|---|
| Plan *Top* / *Primary Range* above the cut | Rejected. In Revit it only affects categories Revit knows about. M2D+ has no categories, and geometry in front of the cut is removed by definition. |
| *Overhead* lines (things above the cut drawn dashed in a plan) | Deferred to v1.x as `overhead = <distance>` with its own output. Today: add a second view looking up (a plane flipped at the same height) and use its Visible lines dashed. |
| *Underlay* (another level greyed in) | Rejected: two views and a Target already do it, with full control. |
| *Section box* | Already covered: a **Box** View source. |
| *Bottom* | Kept, as `beyond`. |
| Far clip line in Revit's per-category style | One **Far clip** class (the preset's Visible width, 0.18 mm at 1:100, §3.6). Its layer can be restyled after the first bake. |

### 3.3 Frame and crop

- **Crop** happens only when the source has an extent: a Rectangle (its own size), a Box (its XY), or a named view (its
  visible rectangle). It clips the linework and what can hide it, and cut regions stay closed (crop the 2D regions,
  then close them). A clipping plane, Plane, Vector or direction has no crop.
- **Frame** (output, and used for layout) is the crop rectangle if there is one. Otherwise it is the scene's bounding boxes
  inside the view volume, projected onto the view plane. It is computed from the *input* bounds, not the output, so the frame
  doesn't move when visibility changes ("never re-fit").
- `crop = off` turns off a named view's crop. Rectangle and Box always crop, because cropping is the reason to use them.

### 3.4 Tolerance

- The drawing tolerance is the document's absolute tolerance. Curves are approximated within it. Native HLD may
  lose detail near 2 × tolerance; remaining short pieces are preserved. The former claim that the adapter drops
  every piece shorter than 2 × tolerance was not implemented and is withdrawn after the small-features measurement.
- Tangent and seam edges are classified with the document's angle tolerance.
- When the document's tolerance is unreasonably coarse or fine for the scene size (more than 1/1,000 or less than 1e-7 of the extents), Info adds a
  remark. The tolerance is never changed silently.
- Warn when the effective tolerance is coarser than the drawing scale needs: **1:1–1:5 → ≤ 0.01 mm;
  above 1:5 and below 1:50 (including 1:10–1:20) → ≤ 0.1 mm; 1:50 and above → ≤ 1 mm**.
  Compare in physical millimetres regardless of document units. These are advisory bands; scale still never changes geometry.
- `tolerance = 0.1 mm` explicitly overrides drawing tolerance; it never edits the document tolerance.
- A GH runtime **remark** reports detail at risk (§5.2: it is a census, not a measured loss; a tolerance too coarse for
  the scale, above, stays a warning). Info separates **adapter tolerance drops** (currently zero), **source edges
  at or below 2 × tolerance** (risk, not a measured loss), and **uncertain section components** at the selected tolerance.
  Native HLD does not expose every internal omission, so these counts are observations, not a completeness certificate.
  Source-edge risk includes bounded floating-point error at the threshold and edges hidden in the view.
  Info also counts positive gaps ≤ 2 × tolerance between adjacent source bounds in view X/Y. This inexpensive
  conservative check catches the abutting-plate fixture; it is not an exhaustive geometry-distance search.

### 3.5 (merged into §3.2)

Beyond and depth fade are now part of the view range, §3.2.

### 3.6 Line weights and styles (automatic): `scale` is Revit's View Scale

**`scale` only picks the line-weight preset.** It works like Revit's *View Scale*: the drawing is still made at **1:1
in model space**, and the scale decides how heavy its lines print. It never shrinks geometry, never moves anything and
never changes the Frame, the gap or hatch pattern scale (§4, §3.7). Default **`1:100`**. It takes `1:n` (or a bare `n`).

**Print widths in mm per preset band:**

| Class | 1:20 – 1:50 | **1:100** (default) | 1:200 – 1:500 | Linetype | Colour | Preview px |
|---|---|---|---|---|---|---|
| Cut (boundary) | 0.70 | **0.50** | 0.35 | section style boundary linetype, else Continuous | section style boundary colour, else ink | 3 |
| Profile / silhouette (part of Visible) | 0.50 | **0.35** | 0.25 | Continuous | ink × fade | 2 |
| Visible | 0.25 | **0.18** | 0.13 | Continuous | ink × fade | 1 |
| Hidden | 0.18 | **0.13** | 0.09 | `Hidden` (made if missing) | ink × fade | 1, dashed |
| Beyond | 0.13 | **0.09** | 0.05 | Continuous | 55 % ink × fade | 1 |
| Far clip (= Visible) | 0.25 | **0.18** | 0.13 | Continuous | ink × fade (at 100 % depth, so the fade limit when fading) | 1 |
| Hatch pattern lines (= Beyond) | 0.13 | **0.09** | 0.05 | from the pattern | section style pattern colour, else the object's display colour | fill |
| Unresolved (= Profile) | 0.50 | **0.35** | 0.25 | Continuous | orange `#E07A00`, never faded | 2 |
| Ignored (§3.9, width = Beyond) | 0.13 | **0.09** | 0.05 | Continuous | 25 % ink × fade | 1 (preview off by default) |

- The Cut width is multiplied by the section style's *BoundaryWidthScale* when a style applies (§3.7). A solid fill has
  no lines, so the hatch width only matters for patterns.
- **Band edges.** The nearest band on a log scale wins, so the edges are the geometric means: `n ≤ 70` → 1:20 – 1:50,
  `71 ≤ n ≤ 141` → 1:100, `n ≥ 142` → 1:200 – 1:500. So **1:75 → 1:100**, **1:150 → 1:200 – 1:500**, 1:10 or 1:5 →
  1:20 – 1:50, **1:1000** and smaller → 1:200 – 1:500. Info names the band used (§5.4).
- **Preview px widths are fixed per class** (the right column) and do not change with `scale`. Keeping them fixed keeps
  the canvas readable at any zoom; the print widths are what changes, and Rhino's print preview shows them.
- Each view can have its own `scale` (`m2d.scale` on its view object or a `[view]` header), so one component can bake a 1:50 detail and a
  1:200 site plan. Their bake layers are separate (§8.1), so the widths never clash.

"Ink" means black on a light background and near-white on a dark viewport. The preview blends toward
Rhino's viewport colour. `weights = layers` overrides the preset: each line takes its **source layer's** print width and
colour instead, and `scale` then changes nothing.

**Preview colours (owner decision 2026-11-15).** With *Preview ▸ Object colours* (the default) the canvas preview draws
each line in its **source object's display colour**, the colour Rhino shows the object in: `ObjectAttributes.DrawColor(doc)`
(object colour, else layer colour, else material), with *By Parent* resolved through the block instance path, exactly
as the no-style cut fill (§3.7). *Preview ▸ Ink* draws every line in ink, as before.

| Line | Preview colour |
|---|---|
| Visible, profile, far clip | the object colour (ink in Ink mode) |
| Beyond | the object colour, lighter (55 %, the class rule) |
| Hidden | the object colour, dashed |
| Depth fade | the object colour faded toward the background (§3.2), on top of the class rule |
| Grasshopper geometry (no Rhino colour) | ink |
| An object colour with too little contrast to the viewport (WCAG ratio below 1.6: black on a dark viewport, white on white) | ink, so no line vanishes |
| Unresolved | orange `#E07A00`, never the object colour |
| Ignored (preview off by default) | faint ink (25 %) |
| Cut boundary, hatch | unchanged: section style, else the object's display colour for fills and ink for lines (§3.7) |
| `weights = layers` | the source layer's colour, as before |

Colours are read on the UI thread with the scene, as plain values next to the output-tree values (`SceneCapture.Colours`),
never added to `DrawingPiece` or `ViewDrawing`, so the worker codec is untouched. A colour change in Rhino re-runs the
presentation from the cached solve: it makes **no HLD call** (`RerunTests`). Bake is unchanged (classes and layers, §8).

### 3.7 Section styles and cut fill: where cut styling comes from

**M2D+ has no hatch system of its own**: no pattern presets and no style editor. Cut styling comes from Rhino only, and
it is **deterministic**: for each cut region M2D+ picks exactly one Rhino 8 `SectionStyle`, by the same rules Rhino uses.
When there is none, the cut is a **solid fill in the object's display colour**. Nothing is guessed from geometry, names
or materials.

**Step 1: which style.** For a cut object *O*, cut by the view source *C*:

| # | When | Style used | RhinoCommon 8 |
|---|---|---|---|
| 1 | *O*'s section source is **By clipping plane**, and *C* is a Rhino clipping plane with its own style | the clipping plane's style | `O.Attributes.SectionAttributesSource == ObjectSectionAttributesSource.FromSectioner`; style = `C.Attributes.GetCustomSectionStyle()` |
| 2 | *O*'s source is **By object** | *O*'s own style | `FromObject`; `O.Attributes.GetCustomSectionStyle()` |
| 3 | *O*'s source is **By parent** (inside a block) | the block instance's resolved style, recursively up the nesting | `FromParent` |
| 4 | *O*'s source is **By layer** (Rhino's default), or any rule above found no style | *O*'s layer's style | `FromLayer`; `doc.Layers[O.Attributes.LayerIndex].GetCustomSectionStyle()` |
| 5 | still nothing | **No style: a solid fill in *O*'s display colour**, boundary at the preset's Cut width in ink | `O.Attributes.DrawColor(doc)`, see *Display colour* below |

A missing style falls through to the next row, never to a guess. A GH Plane, Rectangle or Box is not a Rhino
clipping plane, so rule 1 finds nothing and *O* falls to its layer style.

**Step 2: which fields.** Every field of the chosen `SectionStyle` is used:

| `SectionStyle` field | Used for |
|---|---|
| `SectionFillRule` (`SolidObjects` / `ClosedCurves`) | When to fill. `SolidObjects`: only closed solids get a Hatch, which matches M2D+'s cut policy (open geometry gives an outline, flagged in Info). `ClosedCurves`: any closed section loop is filled, open geometry included. |
| `HatchIndex` → `doc.HatchPatterns` | The Hatch output's pattern. |
| `HatchScale`, `HatchRotationRadians` | Pattern scale and rotation, measured in the drawing's frame (§4). The scale is **used unchanged**: the drawing is 1:1 in model space, so it looks the same as the clipped viewport. `scale` does not touch it in v1 (a later `hatch.scale = view` option could multiply it by the view scale). |
| `HatchPatternColor`, `HatchPatternPrintColor` | Hatch colour in preview and bake. `Color.Empty` means "from the object", as in Rhino: the object's display colour (below). |
| `BackgroundFillMode` (`None` / `SolidColor` / `Viewport`), `BackgroundFillColor`, `BackgroundFillPrintColor` | A solid fill under the pattern. `SolidColor` → baked as the hatch's `ObjectAttributes.HatchBackgroundFillColor`. `Viewport` depends on a display mode, which a drawing doesn't have, so it uses the object's display colour (as the default fill) and is noted in Info (§11, Q14, revised). |
| `BoundaryVisible` | Whether the Cut curve is drawn in the preview and baked. The **Cut output always contains it**, because it is the region's geometry. |
| `BoundaryWidthScale` | Multiplies the preset's Cut print width (0.50 mm at 1:100, §3.6). |
| `BoundaryColor`, `BoundaryPrintColor`, `GetBoundaryLinetype()` | Cut curve colour and linetype. Empty means the default (ink, Continuous). |

The rule of thumb: **fills and patterns take the object's colour, lines take ink**, unless the style says otherwise.

**Display colour (the no-style fill).** M2D+ uses the colour Rhino itself displays the object in, resolved by
RhinoCommon 8, not its own logic:

| Object's `ObjectAttributes.ColorSource` | Fill colour | How it is read |
|---|---|---|
| `ColorFromLayer` (Rhino's default) | the layer's colour | `O.Attributes.DrawColor(doc)` |
| `ColorFromObject` | the object's own colour | `O.Attributes.DrawColor(doc)` |
| `ColorFromMaterial` | the render material's diffuse colour | `O.Attributes.DrawColor(doc)` |
| `ColorFromParent` (inside a block) | the block instance's resolved colour, up the nesting | the instance path: `InstanceObject.Explode(true, out pieces, out pieceAttributes, out transforms)` gives each piece's attributes with *By parent* already resolved, then `DrawColor(doc)` on those |

- `DrawColor(doc)` is the model colour. Per-viewport layer colour overrides (`DrawColor(doc, viewportId)`) are **not**
  used: a drawing belongs to no viewport.
- **Print colour** on bake comes the same way from `O.Attributes.ComputedPlotColor(doc)` (it honours *Print colour by
  display / layer / object / parent*). The preview uses the display colour.
- **Unbaked Rhino 8 Model Objects** use their own *Display colour* attributes and the document layer named in *Layer*,
  resolved the same way. **Plain GH geometry** has no colour and no layer, so its fill is **ink** (as `hatch = black`), and
  Info counts it as `no colour (GH geometry)`.
- The first engine spike confirms `ColorFromParent` and `ColorFromMaterial` against the Rhino 8 viewport with a fixture.

**Plain GH geometry has no attributes**, so it gets neither a section style nor a colour. Three ways to give it styles:

- leave Geometry empty, or pass **referenced Rhino objects / object IDs**: their attributes and layers are read from the document;
- pass **Rhino 8 Model Objects**: a referenced one is read from the document by its Id; an unbaked one uses the section style
  of the document layer named in its *Layer* (GH's Model Object exposes no section-style field of its own, so a per-object
  custom style isn't possible for unbaked GH geometry), and its own display colour;
- override everything with the `hatch` key.

**The override: `hatch = styles | colour | black | none`.**

| Value | Cut fill | Cut boundary |
|---|---|---|
| `styles` (default) | the section style when one is set (Step 1, rows 1–4); otherwise solid in the object's display colour (row 5) | style, else ink |
| `colour` | ignores section styles: solid in the object's display colour for every cut | ink, preset Cut width |
| `black` | solid **ink** poché for every cut (black on bake) | ink, preset Cut width |
| `none` | no fill: every Hatch item is null (lists stay aligned); Cut outlines only | ink, preset Cut width |

With `colour` and `black`, `SectionFillRule` no longer applies: closed solids are filled and open geometry stays an
outline, as M2D+'s cut policy says. Menu: **Hatch ▸ From section styles / Object colour / Black / None**.

**Merging.** `cuts = merge` merges touching regions only when their resolved styles (or fill colours) are identical.

**Info** lists the styles used per view, with counts and the rule that picked them, for example:
`Styles: "Concrete" (layer Structure::Core) 212 · "Brick" (object) 18 · object colour 976`.

**Not exposed in RhinoCommon 8 (checked against RhinoCommon 8.36, 2026-11):**
there is no document-level section-style table (styles exist only as custom styles on layers and object attributes),
and there is no separate "clipping plane overrides all objects" flag; a clipping plane's style is its own object
attributes' custom style and only reaches objects set to *By clipping plane*. The engine's first spike must confirm with
a fixture that this is exactly what the Rhino 8 viewport shows (including whether sublayers inherit a parent layer's
style; the API suggests they don't).

### 3.8 Edges

- **Tangent edges:** off by default. **Seams:** off by default. Both are menu toggles, as in Make2D.
- **Hidden lines:** on by default. It is cheap, because the engine finds visibility for every edge anyway.
  Turning it off empties the Hidden output. It is a presentation choice: the solve always keeps hidden lines (they also
  tell `min visible` which objects are partly hidden), so toggling it never re-runs visibility.
- **Glass:** opaque by default (as in Make2D); `glass = clear` makes objects with a transparent material or on a layer
  named `*Glass*`/`*Glazing*` non-occluding.

### 3.9 Objects seen only through small gaps: `min visible`

Owner decision (REQUIREMENTS, 2026-11-15). Modelling gaps (a 2 mm joint between wall panels) let slivers of the objects
behind them through into the drawing. `min visible = <length>` moves those objects' lines to the **Ignored** output.
Default `off` (`0`). Normal precedence (§6): Settings input > view object (`m2d.min_visible`) > menu > built-in. Units as
for any length (`2 mm`, `0.002`; a bare number is in document units). The key may be written `min visible` or
`min_visible`; Info and *Copy settings as text* use `min_visible`.

**The rule.** An object is ignored when it is *partly hidden* and its *longest continuous visible piece* is shorter than
the threshold, both measured in 2D in the drawing (1:1):

- **Object** = one source: a Rhino object, a GH item, or one block instance (its instance path). Pieces of one instance
  never count for another instance of the same definition.
- **Visible pieces** are its Visible (including profiles), Beyond and Far clip lines. Beyond and Far clip count because
  they are visible linework split only by depth; a Beyond piece joined to a Visible piece is one continuous piece.
- **Continuous** = pieces of the object that touch, end to end or an end on another piece, within the drawing tolerance,
  joined into one run; its length is the sum of the joined pieces' 2D lengths. So one edge cut by the solve into
  adjacent fragments counts whole, and so does an outline of several edges. Pieces with a gap between them are separate
  glimpses and are never joined, even when collinear.
- **Partly hidden** = some of the object's hidden linework lies outside the 2D bounding box of its visible linework.
  An object's outline is never hidden by the object itself, so self-hidden back edges (a small box in an axonometric)
  do not make it partly hidden: a small object that nothing covers is never ignored, however small. An object covered
  only in its middle, with both extremes still drawn, is treated as fully visible too; the rule errs towards keeping.
- **Never ignored:** objects with no visible piece, objects with any Unresolved piece (their visibility is not known),
  objects that are cut in the view (their Cut and Hatch are never ignored, so their lines stay with them), points,
  and annotations.

**What moves.** Every Visible, Beyond and Far clip line of an ignored object moves to **Ignored**; its Hidden lines stay
in Hidden; nothing is deleted. Info reports `Ignored (min visible 5): 2 objects, 4 pieces` per view (threshold in document units). Bake puts the lines
on an `Ignored` category layer (`m2d.class = ignored`, §8). **Preview:** like Hidden, it is a *Preview ▸* toggle, but
**off by default**, because these lines are left out of the drawing; when on, they draw very faint (25 % ink).

**Cost.** A presentation filter over the per-source classification, like beyond, fade and placement. Changing only the
threshold (or `hidden`) never re-runs visibility and makes no HLD call. It does not speed up the solve; culling before
the solve is a possible later experiment behind its own correctness gate.

---

## 4. Placement (Target): the view's plane, moved 1:1

**One rule:** each view has an **anchor frame** (an origin, a right axis and an up axis on the view plane). The drawing is
the view seen in that frame. Placing it is a rigid **plane-to-plane** move of the anchor frame onto the Target plane,
**always at 1:1**. Drawings never shrink. No offsets, no fitting, no scaling, no dependence on the drawing's bounds.

| View source | Anchor frame | Frame output (before placing) |
|---|---|---|
| **Rectangle** | **the rectangle's own plane** (`Rectangle3d.Plane`: its origin and X/Y axes, wherever the origin is) | the rectangle itself |
| **Plane** | the plane | scene bounds projected on the plane |
| **Box** | the box's plane, moved along its Z to the +Z (cut) face | the +Z face |
| **Clipping plane** | the clipping plane object's own plane (`ClippingPlaneSurface.Plane`), with X flipped if needed so the drawing is never mirrored | scene bounds projected on the plane |
| **Named view** | the camera target as origin, the camera's right and up axes | the view's visible rectangle |
| **Vector / direction** | **world origin** projected onto the view plane; right and up from §2 | scene bounds projected on the view plane |

So for a **Rectangle**: *Frame = the source rectangle, moved and rotated onto the Target.* A point that sits at
(u, v) in the rectangle's plane sits at (u, v) in the Target plane. Draw the rectangle with its origin where you
want the sheet's reference point (a grid corner, the ground-floor level) and that point lands on the Target origin.

- **No Target:** the drawing lies **in place** on its anchor frame in 3D: on the rectangle itself, on the plane, on the
  clipping plane, or (for named views, vectors and directions) on the view plane just in front of the scene. It overlays
  the model exactly where it was cut. Views never overlap one another.
- **N targets for N views:** View[i] → Target[i]. This is the 1:1 case.
- **One Target and several views → a row.** View 0's anchor goes on the target origin. Each next view is shifted along the
  target's X so its frame starts one gap after the previous frame ends (gap = 1/10 of the mean frame width, or `gap = …`).
  Y is not shifted, so views whose anchors share a height share a level line. Vector and direction views (world-origin
  anchor) always share world Z = 0 as their level line; for rectangles, put each rectangle's origin on the ground level.
- **Any other count** is an error with a clear message ("Give 1 target, or one per view").
- **Scale does not place.** `scale` only picks line weights (§3.6). To put a drawing on a sheet at 1:100, scale it
  downstream with a normal GH transform (*Scale*, factor 0.01, about the Target origin, then bake), or bake it 1:1 and
  show it in a **Rhino Layout detail** set to 1:100, which keeps hatches and print widths right.
- Rotation is the target plane's rotation. There are no Offset, Anchor, Rotation or Dock inputs.

**Why the anchor changed.** Revision 1 anchored every drawing on the world origin projected onto the view plane. That
keeps elevations aligned but puts a rectangle section somewhere unrelated to the rectangle the user drew. Now every source
with its own plane uses that plane, so the mapping is exactly what you see: rectangle → target. The world origin remains
the anchor only where the source has no plane of its own (Vector, direction), which keeps "four elevations in a row
share the Z = 0 line" working.

---

## 5. Runtime: async, progress, states

### 5.1 Lifecycle

- `SolveInstance` never blocks. It snapshots the request (scene references, views, effective settings), compares it
  with the cached result, and either publishes the cached result or starts or restarts a job.
- **Changing an input, a menu setting, or a view object in Rhino cancels a stale job at once.** The new job starts after 250 ms
  without further changes, so slider drags don't queue work.
- The outputs only ever hold a **complete result for the current inputs**. While a job runs, the outputs are
  empty and the preview shows the previous result greyed, labelled *Stale*. Draft results are **preview only**.
- **All views publish together**, in one downstream solve. The preview fills in view by view as each finishes (Q5).
- **Auto-run** is on by default. When it is off, the component shows *Paused · out of date*, and the footer
  button reads **Run**.
- **Cancel:** the footer button (it reads Cancel while running), **Esc** while the component is selected, the right-click
  **Cancel** item, or disabling the component. A cancelled job publishes nothing.
- **Reopen:** finished drawings are kept in a disk side-cache and reused when the model, views and settings haven't changed.
  Otherwise they rerun in the background. Results are never stored in the `.gh`. (Menu defaults *are* stored in the `.gh`;
  they are settings, not results. Per-view settings are stored on the view objects, in the `.3dm`.)

### 5.1a What re-runs the component

Owner request, 2026-11-15: the drawing re-ran whenever object visibility changed in Rhino, even with Geometry wired. Every
document change is now classified against **what the current drawing read** (the scope: drawn objects and block parts,
their layers, block definitions, the view objects and layer-name items) by one policy (`RerunPolicy`, Core). A re-run re-reads
the scene on the UI thread; visibility is solved again only when its content changed, so colour, name, section-style,
`hidden` and `tree` changes reuse the cached solve and make no HLD call.

| Change in Rhino | Geometry given (GH geometry, referenced objects, ids) | Document mode (Geometry empty) |
|---|---|---|
| Drawn object's geometry modified, moved, deleted, undeleted | **re-run** (re-solve) | **re-run** (re-solve) |
| Object hidden or shown (Hide / Show) | no | **re-run** (the visible document changed) |
| Layer on / off | no, except a layer holding parts of a drawn block, or one a layer-name item matches | **re-run** when the layer or a sublayer holds drawable objects |
| Object or layer on / off **in one viewport or detail** | no | no |
| Viewport camera, zoom, display mode, view changes | no (they raise no document change) | no |
| Object locked / unlocked, selected, other user text, display-mode override | no | no |
| Object moved to another layer | **re-run** | **re-run** |
| Drawn object's or its layer's colour, print colour / width | **re-run**, no HLD call (preview colours, hatch fill, bake) | **re-run**, no HLD call |
| Section style (object or layer), render material (glass) | **re-run** (restyle / glass) | **re-run** |
| Drawn object's name (Key labels) | **re-run**, no HLD call | **re-run**, no HLD call |
| An object that is not drawn (another object, an M2D+ bake, a view box's colour or visibility) | no | no, unless it is now visible and drawable: **re-run** |
| Object added | no (unless a layer-name item selects it) | **re-run** if visible and drawable; hidden objects, bakes and view boxes: no |
| View object: geometry, name or `m2d.*` user text | **re-run** | **re-run** |
| Block definition of a drawn instance edited | **re-run** | **re-run** |
| Hatch pattern table (when a drawn style uses one), tolerance or units | **re-run** | **re-run** |
| Named views, clipping planes | **re-run** only when a View item is text (a named view, clipping plane name or direction) | same |

- Block parts are drawn as Rhino shows them, so a part's own visibility and its layer's on/off matter in both modes.
- The `$M2D Views` front-face fill (§2.2) is a display conduit that only reads the document: its index refresh and redraws
  raise no document change and never re-run the component (`RerunTests`).
- Rhino 8 *Model Object* parameters upstream (Query Model Objects…) are expired by Grasshopper itself on attribute
  changes. M2D+ then re-reads the scene and reuses the cached solve (no HLD call) when nothing it draws changed.
- Interactive Rhino reports changes as events; headless documents can omit them, so the watch also compares a snapshot of
  the same facets under the same policy (at most every 200 ms while reading the scene, and before publishing).
- Tested by `RerunTests` (solves and HLD calls counted per change, both modes) and `RerunPolicyTests` (the table).

### 5.2 Phases and the message line

The GH message tab under the component shows one short line. The progress line on the body shows the whole job
fraction.

| State | Message | Progress line | Outputs | Preview | Balloon |
|---|---|---|---|---|---|
| No view | `Pick a view` | — | empty | — | Remark: "Right-click View to choose a named view, clipping plane or direction, or wire one." |
| Reading | `Reading 50,210 objects` | indeterminate | empty | stale, grey | — |
| Preparing | `Preparing 38 %` | 0–40 % | empty | stale / draft | — |
| Draft | `Draft · refining 61 %` | continues | empty | draft at 60 % opacity | — |
| Drawing | `Drawing 3/6 · 72 %` | 40–100 % | empty | finished views + draft for the rest | — |
| Done | `Section A · 2.1 s` or `6 drawings · 41 s` | hidden | **complete** | full style | — |
| Done, unresolved | `6 drawings · 41 s` | hidden | complete | unresolved in orange | **Remark** per view: "Section A: 12 unresolved pieces in the Unresolved output (orange in the preview, own layer when baked). Check them before issuing." |
| Not drawn, harmless | (as Done) | | complete | | **Remark**: "Section A: 4 objects not drawn (3 TextDot, 1 PointCloud). They hide nothing. See Info." |
| Not drawn, could hide | (as Done) | | complete | | **Warning**: "Section A: 2 objects not drawn (2 …). They could hide other objects. See Info." |
| Quarantined view | (as Done) | | complete; every line Unresolved | orange | **Warning**: "Section A: visibility could not be trusted for the whole view, so its lines are Unresolved (orange). Check Info for the cause." |
| Partial | `Partial · 5/6 drawings` | hidden | the 5 good views; the failed view's branches are empty | | Error naming the view: "View 4 'Section C': no clipping plane or named view with that name." |
| Paused | `Paused · out of date` | — | empty (or the result, if it still matches) | stale | — |
| Cancelled | `Cancelled` | — | empty | stale | — |
| Error | `Error` | — | empty | — | Error text |

**Severity policy** (owner decision 2026-11-15, `NoticePolicy` in Core). It follows the GH convention: a **warning (orange)
means the user has to act**, because the drawing may be wrong or an input is wrong. What a normal run finds goes to a
**remark (grey)** and Info. "Never wrong without saying so" still holds: unresolved pieces are always in the `?` output,
orange in the preview, on their own bake layer, counted in Info and named in a remark.

| Level | When |
|---|---|
| **Error** (red) | A view failed (no such named view or clipping plane, perspective view, extraction failure); Target count mismatch. |
| **Warning** (orange) | A quarantined view; objects not drawn that **could hide** other geometry; a layer-name item that matches no layer; the effective tolerance **too coarse** for the scale (§3.4); unknown settings keys and bad values (§5.3); Validate views refusals. |
| **Remark** (grey) | Unresolved pieces in a normal view; objects not drawn that hide nothing (text, dots, hatches, point clouds); the tolerance detail-risk census (short source edges, close gaps, uncertain section components, which are drawn Unresolved); ignored Depth, bad `m2d.color`, and other notes. |

The message line stays the plain progress or result (`5 drawings · 9.9 s`); a remark never turns the component orange.

### 5.3 Settings errors

Unknown keys and bad values are warnings that suggest the closest match ("Unknown setting `farclp`. Did you mean
`farclip`?"). Valid keys still apply. An unknown `[view]` header is a warning naming the views that exist.

### 5.4 Info (one per view)

```
Section A — View box (front face origin 12.0, 6.0, 0.0), cuts 214 objects
View range: depth 40 m (view box) · far clip with line (view object) · beyond 3 m (auto)
Depth cueing: on, 10 %–90 %, limit 25 % (view object) · line weights 1:100 preset (scale 1:100, component menu)
Scene 50,210 objects · 31,004 block instances of 212 definitions (reused)
18,422 visible · 6,108 hidden · 2,340 beyond · 96 far clip · 1,206 cut regions · 1,206 hatches
Ignored (min visible 0.002): 14 objects, 31 pieces. Their lines are in the Ignored output; nothing is deleted.
Styles: "Concrete" (layer Structure::Core) 212 · "Brick" (object) 18 · object colour 976 (hatch = styles)
Unresolved: 12 pieces on 3 objects (orange) · Not drawn: 3 Text, 1 Point cloud
Tolerance 0.001 m (document) · 41 short pieces dropped
2.1 s for this view · preparation 22.4 s shared by 6 views (cached)
```

---

## 6. Settings: where they live, and which wins

There is **one set of settings per view**. From strongest to weakest:

1. **Depth input** (`depth` only, when wired): one value for all views, or one per view.
2. **Settings input** (when wired): only the keys it contains, for the views it addresses.
3. **The view object's user text** (`m2d.*` on a view box or clipping plane, §2.2).
4. **Right-click menu defaults** (stored on the component, apply to every view).
5. **Built-in default**, often *Auto* = inferred from the source (§2, §3).

One exception, for depth only: **a Box or view box always takes its depth from its geometry.** If a Depth input,
Settings text, user text or menu value also gives a depth, the geometry wins and a remark says which instruction was
ignored and how to change the box instead. A box *is* its view volume; two competing depths would make the box lie.

A key the Settings input sets for every view is greyed in the menu with "(set by input)". A key some view objects
override keeps its menu item enabled and says so: "Depth cueing (set on 1 of 3 view objects)". Unwire the input and
the lower values come back; nothing is lost. Info lists every effective value with its origin (`set by Depth input`,
`set by input`, `view object`, `component menu`, `default`, `auto`). **Copy settings as text** copies the menu defaults
and every view object's values as Settings text with `[view]` headers.

### 6.1 Why no dialog

Revision 3 had a View Range & Settings editor. It is removed (§10): the menu covers global defaults, view objects
cover per-view values where the view itself lives, and text covers parametric control. There is nothing else to
store, so there is nothing to open.

### 6.2 Settings text

- One `key = value` per line. `#` starts a comment. Keys are case-insensitive. Text items may hold several lines
  (a Panel with *Multiline data* on), or one line each (a Panel's default): **all items of a branch are joined**.
- **Which views:** one branch applies to **all views**. A tree with one branch per view (branch *i* ↔ view *i*, in path
  order) addresses each view. Inside any text, a line `[Section A]` (a view name) or `[#2]` (a view index) starts a block
  that applies to that view only, until the next header; `[*]` returns to all views. Any other branch count is an error
  ("Give one Settings branch, or one per view").
- **Units:** distances accept `mm cm m km in ft '` and `"` (`3 m`, `300 cm`, `10'`). A bare number is in document units.
  Percentages accept `25 %` or `25`. Scale accepts `1:100` or `100` (it picks line weights, §3.6). On/off accepts
  `on/off`, `true/false`, `yes/no`, `1/0`.

```
scale  = 1:100
hidden = off

[Section A]
depth   = 40 m
farclip = line
fade    = on
fade.start = 10 %
fade.end   = 90 %
fade.limit = 25 %
```

### 6.3 Settings key reference

| Key | Values | Default | Units | Revit equivalent | Also in |
|---|---|---|---|---|---|
| **View range** | | | | | |
| `depth` | `auto` · distance | `auto`: clipping plane *Depth*, else scene extent. Box / view box: always its length | distance from the view plane | View Depth (plan) / Far Clip Offset | **Depth input**, the view box itself |
| `farclip` | `none` · `line` · `noline` | `noline` | — | Far Clipping: No clip / Clip with line / Clip without line | view object, menu |
| `beyond` | `auto` · `off` · distance | `auto` (3 m in cut views, off otherwise) | distance from the view plane | View range *Bottom* → `<Beyond>` line style | view object, menu |
| `fade` | `on` · `off` | `off` | — | Depth Cueing | view object, menu |
| `fade.start` | 0–100 | `0 %` | % of depth | Depth Cueing *Start* | view object |
| `fade.end` | 0–100 | `100 %` | % of depth | Depth Cueing *End* | view object |
| `fade.limit` | 0–100 | `30 %` | % intensity | Depth Cueing *Fade Limit* | view object |
| `crop` | `auto` · `off` | `auto` | — | Crop View | text only (boxes always crop) |
| **Lines** | | | | | |
| `hidden` | `on` · `off` | `on` | — | Show Hidden Lines | view object, menu |
| `tangents` | `on` · `off` | `off` | — | — (Make2D option) | view object, menu |
| `seams` | `on` · `off` | `off` | — | — (Make2D option) | view object, menu |
| `glass` | `opaque` · `clear` | `opaque` | — | — | view object |
| `min visible` (`min_visible`) | `off` · length | `off` | 2D length in the drawing | — | view object (`m2d.min_visible`), menu |
| `scale` | `1:n` · `n` | `1:100` | — (picks the nearest preset band, §3.6) | View Scale (line weights only; the drawing stays 1:1) | view object, menu |
| `weights` | `auto` · `layers` | `auto` (the `scale` preset, §3.6) | — | Object Styles | view object, menu |
| **Cuts** | | | | | |
| `hatch` | `styles` · `colour` · `black` · `none` | `styles` (section style, else object colour) | — | Cut pattern (material / V/G override) | view object, menu |
| `cuts` | `separate` · `merge` | `separate` | — | Join Geometry | view object, menu |
| **Placement** (always 1:1) | | | | | |
| `gap` | `auto` · distance | `auto` (1/10 of mean frame width) | distance on the target | — (sheet placement) | text only |
| `name` | text | auto (§8.1): the view object's name | — | View Name | view object (`m2d.name`), the object's Rhino name |
| **Output and diagnosis** | | | | | |
| `tree` | `auto` · `layer` · `object` | `auto` (by layer for the document or referenced objects, else by object, §1.3) | — | — | view object (`m2d.tree`), menu |
| `tolerance` | distance | document absolute tolerance | distance | — | text only |

---

## 7. Right-click menu (component)

These items come after GH's standard items (Preview, Enabled, Bake…, Help). Every toggle edits the component's
**menu defaults** (every view, below view objects and input, §6); an item whose key the input sets for every view is
greyed and shows "(set by input)", and one some view objects override says "(set on 1 of 3 view objects)".

```
Validate views                    (§2.2)
Flip view direction               (view boxes)
Create view from current viewport
Select view objects               Double-click
────────────────
Bake drawing                      Shift-click: add as a copy
Bake one ▸        Section A · Section B · South elevation · …
────────────────
Run automatically            ✓
Run now
Cancel                            (only while running)
────────────────
Hidden lines                 ✓        hidden   = on | off
Tangent edges                         tangents = off | on
Seams                                 seams    = off | on
Min visible ▸ Off ✓ · 1 mm · 2 mm · 5 mm · 10 mm      min_visible = off | <length>   (§3.9)
Beyond ▸     Auto ✓ · Off · 1 m · 3 m · 10 m          beyond  = auto | off | <distance>
Far clip ▸   No clip · Clip with line · Clip without line ✓   farclip = none | line | noline
Depth cueing                          fade     = off | on   (start/end/limit: m2d.fade.* or text)
Cuts ▸       Separate per object ✓ · Merge touching (same style)   cuts = separate | merge
Hatch ▸      From section styles ✓ · Object colour · Black · None   hatch = styles | colour | black | none
Scale ▸      1:20 · 1:50 · 1:100 ✓ · 1:200 · 1:500                    scale = 1:n   (line weights only; other n via text)
Line weights ▸ From scale ✓ · From source layers                    weights = auto | layers
Reset menu defaults
────────────────
Preview ▸    View frames ✓ · Hidden ✓ · Beyond ✓ · Far clip ✓ · Hatch ✓ · Frame · Ignored ─ Object colours ✓ · Ink   (§3.6)
Output tree ▸ Auto ✓ · By layer · By object                          tree = auto | layer | object   (§1.3)
────────────────
Copy info
Copy settings as text
Clear drawing cache (412 MB)
```

---

## 8. Bake

**One click:** the **Bake** button on the message tab, or the menu item, bakes every view. GH's standard Bake
command does the same, and its layer dialog is ignored, with a remark saying so.

### 8.1 Layers

```
M2D+
└─ Section A                       ← drawing name
   ├─ Visible   ── Arch::Walls, Arch::Doors, Structure::Columns …   (source layer paths mirrored)
   ├─ Hidden    ── …
   ├─ Beyond    ── …
   ├─ Far clip  ── …               (only when farclip = line)
   ├─ Cut       ── …
   ├─ Hatch     ── …
   ├─ Unresolved                   (only when there is something unresolved; orange)
   └─ Ignored   ── …               (only when min visible ignores something, §3.9)
```

- Category first, then source layer, as Make2D does. One click hides every hidden line, and the source
  sublayers still let you hide, for example, furniture.
- Category layers get the styles in §3.6, with the print widths of the drawing's `scale` preset, when they are
  **created**. **Re-baking never restyles a layer the user has changed,** so the user's own layer styling survives.
  One exception keeps `scale` working: each category layer records the widths M2D+ gave it (user string
  `m2d.weights = 1:100`); if the drawing's preset has changed and the layer still has exactly those widths, re-bake
  updates them to the new preset. A layer the user restyled is kept, and the result says so. Depth-cue fading is per
  object (object colour + print colour), so it never touches layers.
- Cut fills that come from the object's colour are per object too (object colour + print colour, §3.7), so the
  Hatch layer's own colour doesn't matter and one Hatch layer can hold every material colour.
- GH geometry with no source layer goes straight onto the category layer.
- **Drawing name** = the named view or clipping plane name. Otherwise it is an automatic description:
  `Plan +1.20`, `Section 1`, `Elevation South`, `Axo SW`. It is unit-aware. Duplicates get ` (2)`, and `name = …`
  overrides it.

### 8.2 Replace, not pile up

- Each baked object carries user strings: `m2d.bake = <component id>/<drawing name>`, `m2d.source =
  <object id>`, `m2d.class = visible|hidden|beyond|farclip|cut|hatch|unresolved|ignored`, `m2d.hash = <geometry hash>`.
- **Re-bake replaces** the objects from the earlier bake of the same component and drawing. Objects the user has since
  **edited** (hash changed) or **moved to another layer** are kept, and the result says how many:
  `Baked Section A: 3,412 objects (replaced 3,390 · kept 2 you edited)`. Empty sublayers that M2D+ created are
  removed.
- **Shift-click Bake** adds a copy instead (`Section A (2)`).
- **Undo:** each Bake is **one Rhino undo record** ("M2D+ Bake 6 drawings"), deletes included.
- Locked target layers are unlocked for the bake and locked again afterwards. A layer hidden by the user stays hidden, and
  the result mentions it.
- Objects are baked **at 1:1** where the preview shows them (Target or in place). Hatches are baked as Rhino hatch
  objects: with a section style, its pattern, pattern scale (unchanged), rotation, colours and background fill; without
  one, a **Solid** hatch whose object colour is the source object's display colour and whose print colour is its
  computed print colour (§3.7); with `hatch = black`, a Solid hatch in black. The frame is not baked.
- Bake is allowed when there are unresolved pieces. They go to the orange `Unresolved` layer, so they can't be
  mistaken for visible lines.

---

## 9. Common workflows (wiring recipes)

**1. Plan of the Rhino model (no wiring at all)**

```
            ┌─────────┐
  (empty) ──┤G  M2D+  │    right-click V ▸ Clipping planes ▸ "Plan cut L1"
  (menu)  ──┤V        │──  preview: plan in place, cut fill from section styles, else object colour
            └─────────┘    click Bake → M2D+::Plan cut L1::…
```

**2. Four elevations in a row on the ground**

```
  Panel ┌Front┐
        │Right│──V┐
        │Back │   ├─ M2D+ ──V,H,B…
        │Left ┘   │
  XY Plane ───────T┘   (one target → a row; direction views share the Z = 0 level line)
```

**3a. A section you edit in Rhino (view box)**

```
  Rhino: draw a vertical planar surface through the building (PlaneSrf / Rectangle + PlanarSrf)
  GH:    right-click V ▸ Pick views in Rhino… ▸ pick it ▸ right-click V ▸ Validate views
  → "M2D View 01": a view box looking at the model, depth to the back of the scene, arrow + depth volume in preview
  Rhino: gumball-drag its back face to 12 m; Properties ▸ Attribute User Text: m2d.farclip = line, m2d.fade = on
  Double-click the component to select it in Rhino again. Flip view direction to look the other way.
```

**3b. A cropped section from a GH rectangle, placed 1:1 on a sheet plane**

```
  Rectangle (in a vertical plane through the building) ── V┐
  Plane (sheet origin, off to the side) ────────────────── T│
  Panel "farclip = line / fade = on" ───────────────────── S├─ M2D+ ── Frame = the rectangle, moved to T
  Slider 12 (m) ─────────────────────────────────────────── D┘   depth from a slider
  The rectangle cuts the building → section. Move it outside → elevation. Same wires.
```

**4. Sections driven by Grasshopper**

```
  Building (GH) ─────────────── G┐
  Curve ─ Perp Frames (n = 5) ── V├─ M2D+ ── Cut ─→ Area     trees: {0;0} … {4;0}
                                  │          Hatch ─→ (preview)
  Slider moves the cut: draft preview straight away, final result after it.
```

**5. A whole drawing set from the document**

```
  (empty) ─────────────── G┐
  right-click V ▸ All named views + All clipping planes      ├─ M2D+   click Bake (one undo)
  Rectangular Grid ─ planes ─ T┘   (one target per view = a sheet layout)
  Menu: Scale ▸ 1:100 (line weights), Depth cueing on; each section view box: m2d.farclip = line
  The set stays 1:1 in the model. For 1:100 sheets: Rhino Layout details at 1:100 (recommended),
  or GH Scale 0.01 about each target origin before baking.
```

**6. Axonometric of selected layers, with hidden lines**

```
  Panel "Structure::*" ─ G┐
  Panel "SW"           ─ V├─ M2D+ ── V, H (dashed)
  Panel "tangents = on" ─ S┘
```

---

## 10. Alternatives considered and rejected

| Alternative | Why rejected |
|---|---|
| The prototype's View → Drawing → Layout pipeline (3 components, ~30 inputs) | The owner rejected it. Every view needed three components set up, and most of the inputs repeated what the view source already says. |
| A separate "View" or "Settings" builder component | It is a second component, which goes against the agreed decision. View boxes give the same visual control as Rhino objects, and the Settings text covers parametric use. |
| **The View Range & Settings editor** (revision 3: a double-click, Revit-like dialog with per-view rows, stored on the component) | **Removed by the owner, revision 4.** Too heavy for what it held: a second place to look, a dialog to learn, and per-view rows keyed to identities that silently became "unused" when views changed. Depth for rectangle views still had no proper input. View objects put per-view values on the view itself (Properties ▸ Attribute User Text, with the box's own geometry as depth), the menu keeps global defaults one click away, and the Depth input covers sliders. Its double-click now selects the view objects; its S dot is gone. |
| View range values as inputs (Depth, Far clip, Fade start/end/limit) | Five more inputs, mostly left at default. Far clip and fade are view-object keys, menu items and Settings keys. **Depth is the exception since revision 4**: it is the one value people drive from a slider, and planes and rectangles have no geometry to hold it. |
| Settings *only* as text (revision 1) | Invisible: nothing on the canvas said what was set or what could be set. View objects (selected by double-click), menu check marks and Info fix that; text stays for parametric control. |
| Settings matched item by item to View (revision 1) | A GH Panel outputs one item per line, so a 3-line Panel with 3 views was misread. Branch matching and `[view]` headers are unambiguous. |
| Merging Beyond into depth cueing | Fading would change which output a line lands in. Revit keeps them separate too (§3.2). |
| Revit *Top*, *Overhead*, *Underlay* | §3.2: not meaningful without categories, or covered by a second view. Overhead may come in v1.x. |
| Near / Cut depth / Cut enabled inputs | Inferred from the source type (§2). "A plane always cuts if it passes through geometry"; a Box or `depth` covers the far side. |
| A GH plane that doesn't cut, plus a flag to make it a section | It adds a flag. A plane outside the model cuts nothing and gives an elevation for free. Vector and direction keywords cover "look, never cut". |
| World-origin anchor for every source (revision 1) | A rectangle section landed somewhere unrelated to the rectangle. Own-plane anchors make Target a plain 1:1 move (§4). |
| A Rectangle as Target (target frame instead of plane) | Would invite "fit to frame", i.e. non-1:1 scaling, which the owner doesn't want. A plane is exact. |
| `scale` shrinking the drawing about its anchor (revision 2) | Owner, 2026-11-14: scale works like Revit's View Scale and only picks line weights. Drawings stay 1:1 in the model, so they overlay and dimension correctly; sheets are scaled with GH transforms or Layout details (§4). |
| An M2D+ hatch system (pattern presets, a style editor) | Owner, 2026-11-14: Rhino 8 section styles are the source of truth, and object colour is the fallback (§3.7). A second system would drift from what the Rhino viewport shows. |
| A live **Active viewport** source | Orbiting would re-run the job. Capture to a named view instead. |
| A Capture pulse and a Run toggle as inputs | Pulse inputs are fragile and keep hidden state in the GH file. Named views plus Auto-run, Run now and Cancel replace them. |
| Anchor U/V, Offset, Rotation, Dock | The own-plane anchor plus one Target plane (or a row) covers sheet layout with no extra inputs. |
| Mode (Native / Custom / Auto), Tolerance and Async inputs | Engine routing is internal and reported in Info. Tolerance comes from the document. The job is always async. |
| "Zoomable" optional extra inputs (variable parameters) | They bring input sprawl back with a hidden "+" button. View objects and the menu give the same power visibly. |
| Per-view settings in an object's geometry user data, or in a hidden GH store | Invisible and lost on export or copy. Attribute User Text shows in Rhino's Properties, copies with the object and survives `ReplaceObject`. |
| Recovering a view box that Rhino turned into a polysurface by guessing its front face | A box has three face pairs and no stored orientation survives a rotation. M2D+ reports it and asks for an undo or a re-validate instead of guessing (principle 2). |
| A bake boolean input or a separate Bake component | The bake-on-true toggle is a known GH anti-pattern. One button gives one undo record. |
| Auto-bake / live sync into the document | It would fight the user's edits to baked linework and flood undo. |
| A single "Drawing" output plus an Explode component | Needs a second component. Nine plain outputs are cheap. Inputs are what hurt. |
| Far clip lines inside Visible or Cut | Visible would mix in lines that aren't edges; Cut must stay closed regions aligned with Hatch. A ninth output is cleaner. |
| Rhino 8 Model Object outputs that carry layer and weight attributes | Elegant, but less universal downstream. Bake already applies the styles. Could be `output = model` later. |
| Draft (fast) results on the outputs | Downstream components would solve twice, and a draft could be baked by mistake. |
| Guessing hatches from materials or layer names | Not deterministic. Section styles are Rhino's own source of truth (§3.7). |
| Perspective | Out of scope in v1 (requirements). |

---

## 11. Open questions for the owner (with recommendations)

**Owner decisions, 2026-11-14:** Q1 yes (empty Geometry draws the whole visible model). Q2 revised: with no section
style, a cut is a solid fill in the object's display colour; M2D+ has no hatch system of its own. `scale` works like
Revit's View Scale and only picks line weights; drawings stay 1:1. The Settings input stays as `key = value` text.
Reopening a file uses a disk cache (§5.1). Q3–Q8 take the recommendations below until the owner says otherwise. The owner
accepted all Q9–Q15 recommendations on 2026-11-14, which locked the component at 4 inputs and 9 outputs; revision 4
(owner) appends the fifth input, Depth. **Orchestrator decisions, 2026-11-14 (owner may override):** Q16–Q19 take the recommendations. Q14 is revised: a style's *Viewport* background fill uses the object's display colour, the same as the default fill, and is noted in Info. **Q16–Q19 are
new in revision 3** and follow from the two decisions.

| # | Question | Recommendation |
|---|---|---|
| Q1 | Should an empty Geometry input draw the whole visible document? | **Yes** (decided). |
| Q2 | Default cut fill with no section style? | **Solid fill in the object's display colour** (decided 2026-11-14, replacing "solid poché at 85 % ink"). `hatch = black` gives ink poché. |
| Q3 | Should a named view crop to its visible frame? | **Yes**, with `crop = off`. |
| Q4 | What happens with a perspective named view? | **Error for that view only:** "Perspective comes in a later version. Set the named view to Parallel." |
| Q5 | Should outputs publish view by view while a long set runs? | **No.** Publish once. The preview fills in per view. |
| Q6 | No Target: in place or on World XY? | **In place** (on the rectangle / plane / clipping plane itself). |
| Q7 | Beyond Auto (3 m behind the cut) for cut views, off for elevations? | **Yes.** Revisit after testing with real models. |
| Q8 | Bake button drawn on the component? | **Yes.** |
| Q9 | Depth cueing on or off by default? | **Off**, as in Revit. Beyond already greys far lines in plans and sections; fading on top by default double-greys them. One click turns it on (a menu default), or `m2d.fade = on` on one view. |
| Q10 | Far clip default? | **Clip without line** (Revit's default for sections). It only matters once `depth` is set, because `depth = auto` clips nothing. |
| Q11 | Anchor = the source's own plane for Rectangle/Plane/Box/clipping plane/named view; world origin only for Vector/direction? | **Yes** (§4). It is what "1-to-1" means. |
| Q12 | New ninth output **Far clip**? | **Yes.** Empty unless `farclip = line`. |
| Q13 | Settings input as tree access with `[view]` headers? | **Yes.** It fixes the Panel line-splitting problem and lets Copy settings as text round-trip menu and view-object values. |
| Q14 | A section style with background fill *Viewport* (display-mode dependent)? | **Treat as no background fill**, noted in Info. A drawing has no display mode. The alternative is a solid fill in the object's display colour. |
| Q15 | A clipping plane's own style applies only to objects set to *By clipping plane*? | **Yes, follow RhinoCommon** and confirm with a viewport fixture in the first spike (§3.7). |
| Q16 | Band edges for scales between the presets? | **Nearest on a log scale**: ≤ 1:70 → 1:20–1:50 weights, 1:71–1:141 → 1:100, ≥ 1:142 → 1:200–1:500. So 1:75 → 1:100 and 1:150 → 1:200 (§3.6). |
| Q17 | Plain GH geometry has no display colour. What fills its cuts? | **Ink** (as `hatch = black`), counted in Info. Use referenced objects or Model Objects to get colours. |
| Q18 | Re-bake after `scale` changes: update the category layers' print widths? | **Yes, only where the layer still has the widths M2D+ gave it**; restyled layers are kept and reported (§8.1). |
| Q19 | Should `scale` also scale section-style pattern scale? | **Not in v1**: the drawing is 1:1, so the style's own scale already matches the viewport. A later `hatch.scale = view` option could add it. Q14 may also be worth revisiting: style fill *Viewport* could now use the object's display colour, like the default fill. |

---

## 12. What the engine must provide for this UX to work

1. **Batch multi-view solving:** `Solve(scene, views[], settings[])`. View-independent preparation (meshes, edges, silhouette
   candidates, section curves, block definitions) is done once and shared by all views.
2. **A process-wide preparation cache** keyed by object id + geometry hash (and block definition + transform for instances), with an LRU memory cap. It is
   shared across components, invalidates one object at a time, and reports its size for the "Clear drawing cache" item.
3. **Snapshots by reference:** the scene is captured on the UI thread in under 100 ms for 50k objects, by holding immutable `RhinoObject` geometry and
   instance references, not copies.
4. **Progress:** phase (reading / preparing / draft / drawing view *k* / cut & hatch) plus a done/total count, delivered at
   up to about 10 Hz. The UI repaints at most at 10 Hz.
5. **Cancellation** with checkpoints at least every 100 ms, including inside the preparation of large single objects.
6. **A draft pass:** a fast, flagged, preview-only result, in under about 1 s for one view of a typical building.
7. **Per-piece classification:** visible / profile / hidden / beyond / far clip / cut / unresolved, with **depth range (min, mid, max)
   along the view direction**, source object id, source layer, instance path and GH source path. This drives the trees,
   the weights, the fade and the bake layers.
8. **Cut regions** as closed planar curves with holes, one per object, cropped as 2D regions so they stay closed. Optional merging of
   touching regions with identical resolved styles or fill colours. Each hatch is built from its region and the section style
   resolved by §3.7 (including `FromParent` through nested blocks), or, with no style, as a Solid hatch carrying the source
   object's **resolved display colour and print colour** (`DrawColor` / `ComputedPlotColor`, with `ColorFromParent` resolved
   through the instance path). It honours the clipping plane's participation lists and depth. No pattern is ever rescaled.
9. **A view volume per view:** view plane, crop extent (rectangle, box XY or named-view frame) and far plane at `depth`. Geometry
   beyond the far plane is removed before hidden-line work (`farclip = line | noline`), and with `farclip = line` the
   **far-plane section curves** of the trimmed geometry are computed and their visible parts output as Far clip lines.
10. **Anchor frames** per source type (§4) and a rigid 1:1 plane-to-plane placement (no scaling). Placement is a
    transform applied after drawing, so changing Target re-places cached drawings without re-solving.
11. **Cheap frame inference** from bounding boxes before any meshing. A **relevance test** between changed objects and view volumes.
12. **Reports:** unsupported objects by type, unresolved counts per object, styles used per rule, timings, cache hits. Deterministic
    output order, so trees don't shuffle between runs.
13. **Unit-aware defaults:** the document tolerance and angle tolerance, and metre defaults (3 m beyond) converted to document units.
14. **Settings-independent caching:** depth cueing, line weights (`scale`, `weights`), fill colours and the `hatch` override are
    styling only and must not invalidate the drawing cache. Neither do `hidden` (the solve always keeps hidden lines) or
    `min visible` (§3.9). As built, the visibility solve is keyed on `depth`, `farclip`, `crop`, `tangents`, `seams`,
    `glass` and `tolerance` only (`Settings.VisibilityKeys`); `beyond` and `cuts` are applied over the cached solve too.
