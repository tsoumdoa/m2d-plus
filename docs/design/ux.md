# M2D+ Grasshopper UX (v1 design)

Status: proposal by the design sub-agent, 2026-11-14, revision 3 (owner decisions on cut fill and on scale).
Revision 2 covered settings, view range, rectangle views, placement and section styles. It follows
[REQUIREMENTS.md](../REQUIREMENTS.md).
Visual mockup: [ux-mockup.html](ux-mockup.html).

**The idea:** one component and four inputs, of which only **View** must be set. Plug in a view and you
get a correct drawing. M2D+ works out everything else from the view source and the document: what
to cut, the crop, the tolerance, the hatches and the line weights. The Frame2D prototype needed about 30
inputs to say these things. Here they are inferred or given a sensible default.

Settings that an architect expects to *set*, such as Revit's view range (view depth, far clipping, depth
cueing), are not inputs either. They live in a **View Range & Settings editor** that opens when you
double-click the component, and they are stored on the component. The optional **Settings** input takes the
same keys as `key = value` text for parametric control, and wins where it is wired (§6).

**What changed in revision 3** (owner decisions, 2026-11-14)

| Area | Change | Why |
|---|---|---|
| Cut fill | M2D+ has **no hatch system of its own**. A Rhino 8 section style is honoured when one is assigned (§3.7). With no style, a cut is a **solid fill in the object's display colour**. `hatch = styles \| colour \| black \| none`. This replaces "solid poché at 85 % ink". | Owner: Rhino's section styles and object colours are already the source of truth. |
| Scale | `scale` works like Revit's **View Scale**. It only picks the **line-weight preset** (default `1:100`, §3.6). Drawings always stay **1:1 in the model**, and placement never shrinks them (§4). Sheets are scaled with GH transforms or Rhino Layout details. | Owner: "scale = line weights only". |

**What changed in revision 2**

| Area | Change | Why |
|---|---|---|
| Settings | New double-click **editor** (Revit-style dialog, per-view rows). Settings input becomes **tree** access with optional `[view]` headers. | The owner couldn't see how Settings worked. A GH Panel splits lines into items, so "one item per view" was broken. |
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
| Tab / panel | `M2D+` / `Drawing` (the only component in v1) |
| Description | "Hidden-line plans, sections, elevations and axonometrics from your model and a view. Runs in the background. Double-click for view range and settings; click Bake on the component to bake to tidy layers." |
| Icon | Uses the dark rounded tile that Panel+, HopperFormat and Conduit+ share (tile `#181E1A`, white glyph, 24-unit grid), drawn in code the same way as `ConduitIcons.cs`. **Glyph:** a cube in axonometric with its near corner cut away. The cut face is a solid white poché, the visible edges are white strokes and one hidden edge is a soft (50 % white) dash. It reads as "section + hidden line" at 24 px. **Plug-in logo:** "M+" drawn in the same way as the C+ / P+ marks. |
| Custom attributes | Standard GH body, plus (a) a 2 px cobalt `#1F6FD1` progress line along the bottom edge of the body while a job runs, (b) a small **Bake** button at the right end of the message tab (it becomes **Cancel** while running and **Run** when paused), (c) **double-click opens the editor** (§6.1), and (d) a 5 px cobalt dot next to the **S** input label when the editor holds values, so stored settings are visible on the canvas. Nothing else is custom. |

### 1.1 Inputs (4)

| # | Name | Nick | Access | Type | Optional | Default |
|---|---|---|---|---|---|---|
| 0 | Geometry | G | tree | Generic: geometry, referenced Rhino objects, Rhino 8 model objects / block instances, object IDs, layer names | yes | **The whole visible Rhino document** |
| 1 | View | V | list | Generic: text, clipping plane (reference or ID), Plane, Rectangle, Box, Vector, Rhino view object | **no** (choose one by right-click, or wire one) | none: the component shows "Pick a view" |
| 2 | Target | T | list | Plane | yes | **In place** (each drawing lies on its own view plane in 3D) |
| 3 | Settings | S | **tree** | Text, `key = value` lines | yes | The editor's values (§6) |

Description text shown in GH (keep these short, as Conduit+ does):

- **Geometry**: "Scene to draw, shared by every view. Leave empty to draw the visible Rhino model. Also accepts layer names such as `Walls` or `Structure::*`, and block instances (repeats are drawn once and reused)."
- **View**: "What to draw. A named view, clipping plane name or direction (Top, Front, SW…), a referenced clipping plane, a Plane, a Rectangle (its plane is the view plane and it crops; if it cuts the model you get a section), a Box (cut, crop and depth) or a Vector (look along it). A list gives one drawing per view."
- **Target**: "Optional plane per view. The view's own plane is moved onto it 1:1 (View[i] → Target[i]). One plane for several views lays them out in a row. Leave empty to show each drawing in place."
- **Settings**: "Optional `key = value` lines, for example `depth = 8 m` or `farclip = line`. They override the editor (double-click the component). One branch applies to all views; one branch per view, or `[View name]` headers, set views separately."

#### Why each input exists (and why there are no others)

1. **Geometry**: the only thing a user must be able to choose freely, so it can't be inferred. It is optional because the common Rhino case is "draw my model", which is what Make2D does with the selection. Leaving it empty also gives the fastest path for 50k objects. The engine reads `RhinoObject`s directly, keeps block instances as instances so repeats are reused, and avoids wrapping 50k geometries in GH goo. Layer-name strings give the same fast path for subsets.
2. **View**: a drawing has no meaning without one. One smart input replaces the prototype's View component and its 10 inputs (name, capture pulse, plane, width, height, near, far, cut on/off, cut depth, crop), because the type of value you pass says what you mean (§2).
3. **Target**: placement is geometric, often computed (a sheet grid, a strip), and it is the one thing the prototype's Layout component did that people actually need. Seven layout inputs collapse to one plane, because the anchor is the view's own plane (§4).
4. **Settings**: the editor covers hand-set values, but an editor can't be driven by a slider or computed per view. Settings is text so that one wire carries every key without adding inputs.

Not inputs, with where they went: Run, Async, Mode (Native/Custom/Auto), Tolerance, Include hidden, Capture (§2.1, §5);
Near/Far/Cut, Crop (§2, §3.2); Scale (now the line-weight preset key `scale`, §3.6); Anchor U/V, Offset X/Y, Rotation,
Dock to/side/gap (§4); Fade on/start/end/min
(now the Revit-style `fade.*` keys in the editor, §3.2); Show hidden (§3.8).

### 1.2 Outputs (9)

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

Line weights, fill colours and depth-cue fading are not outputs. The preview and Bake apply them (§3.6, §3.7). The
curve outputs are plain, so they work with every downstream component. All geometry outputs are at 1:1 in model units.

### 1.3 Output trees

- **Path = `{view; source path}`**, always, even for one view. Going from 1 to 3 views never changes the
  shape of the downstream tree.
  - `view` is the index of the view in View's list. A tree wired to View is flattened in path order.
  - `source path` is the Geometry input's branch path. Items are the drawn pieces of that branch, in
    source item order. Document mode (Geometry empty) uses `{view; 0}`.
- Right-click **Output tree ▸ Branch per object** adds the source item index: `{view; source path; item}`.
  It is off by default, because 50k objects × 7 outputs would mean 350k branches.
- Merged cut regions (`cuts = merge`) go to the lowest source path that took part.
- Frame and Info have one item per view, at `{view}`.
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
| **Rectangle** | the rectangle's plane | if it passes through geometry | **the rectangle** | scene extent behind the plane |
| **Plane** | the plane | if it passes through geometry | none (scene bounds) | scene extent behind the plane |
| **Box** | its **+Z face** | if that face passes through geometry | the box's XY | **the box's −Z face** (its height) |
| **Clipping plane** (referenced, model object, GUID or name text) | the clipping plane, looking at the side it keeps | yes | none | Rhino 8 clipping plane *Depth* (`ClippingPlaneSurface.PlaneDepth`) when `PlaneDepthEnabled`, else scene extent |
| **Text: named view** (`"Level 1"`) or Rhino 8 view object | the camera plane at the scene front | no | the view's frame | scene extent |
| **Text: direction** (`Top`, `Front`, `Right`, `Back`, `Left`, `Bottom`, `SW`, `SE`, `NE`, `NW`) | perpendicular to the direction, at the scene front | no | none | scene extent |
| **Vector** | perpendicular to the vector, at the scene front | no | none | scene extent |

So a **Rectangle is "Plane + crop"** and a **Box is "Rectangle + explicit depth"**. They are the same view with more
said. World XY at height *h* is a plan at *h*. A rectangle standing in a building is a cropped section. The same
rectangle moved outside the building is a cropped elevation.

Text is resolved in this order: named view, then named clipping plane, then direction keyword (case-insensitive).
A name that isn't found is an error for that view only. The other views continue (§5, *Partial*).

**Perspective named views** are an error for that view only (§11, Q4).

**Up direction.** A source with its own axes (Rectangle, Plane, Box, clipping plane, named view) keeps them. A Vector
or direction keyword uses world Z projected onto the view plane as up. When looking straight down or up, it uses
world Y (north up).

### 2.1 View right-click menu (on the input parameter)

This works like Conduit+'s `Param_TextOption`. Choosing an item sets persistent data, and items are
greyed out while a wire is connected.

```
Named views     ▸  Level 1 · Level 2 · Roof · …          (multi-check: one drawing each)
Clipping planes ▸  Section A · Section B · Plan cut L1 · …
Directions      ▸  Top · Front · Right · Back · Left · Bottom │ SW · SE · NE · NW
───────────────
All named views
All clipping planes
Pick clipping planes in Rhino…
Capture active view as named view…                   (creates "M2D 01", adds it)
───────────────
Clear
```

The active viewport is never tracked live, because orbiting would re-run the job. **Capture active view**
saves it as a Rhino named view, which is stored in the document and can be edited in Rhino's Named Views panel.

---

## 3. What is inferred, and how

### 3.1 The scene (Geometry empty = document mode)

- **Included:** normal and locked objects on visible layers. Block instances are kept as instances, nested and mirrored ones too.
- **Ignored without a warning:** hidden objects, objects on hidden layers, lights, clipping planes, named-view
  widgets, cameras, and **anything M2D+ baked before**. Baked objects are recognised by their `m2d.*` user
  strings or by sitting on an `M2D+` layer, so an in-place bake is never drawn again.
- **Reported:** objects that could be drawn but aren't supported, such as text in v1 or point clouds. They appear in Info
  and as one warning with counts by type.
- **Clipping scope:** when a Rhino 8 clipping plane only clips some layers or objects (its participation lists), only
  those are cut. The others are drawn whole, so the drawing matches the clipped viewport.
- **Updates:** document changes are debounced (1 s after the last change). They re-run only views whose
  volume contains the changed objects' bounding boxes.

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
| **View depth** | `depth = auto \| <distance>` | Plan: *View Depth*. Section/elevation: *Far Clip Offset*. | Places the far plane. `auto` = the source's own depth (Box height, clipping plane *Depth*), else the scene extent, so nothing is clipped. |
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

- The drawing tolerance is the document's absolute tolerance. Curves are approximated within it, and pieces shorter than
  2 × tolerance are dropped (and counted in Info).
- Tangent and seam edges are classified with the document's angle tolerance.
- When the document's tolerance is unreasonably coarse or fine for the scene size (more than 1/1,000 or less than 1e-7 of the extents), Info adds a
  remark. The tolerance is never changed silently.
- `tolerance = 0.1 mm` exists for diagnosis only.

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

- The Cut width is multiplied by the section style's *BoundaryWidthScale* when a style applies (§3.7). A solid fill has
  no lines, so the hatch width only matters for patterns.
- **Band edges.** The nearest band on a log scale wins, so the edges are the geometric means: `n ≤ 70` → 1:20 – 1:50,
  `71 ≤ n ≤ 141` → 1:100, `n ≥ 142` → 1:200 – 1:500. So **1:75 → 1:100**, **1:150 → 1:200 – 1:500**, 1:10 or 1:5 →
  1:20 – 1:50, **1:1000** and smaller → 1:200 – 1:500. Info names the band used (§5.4).
- **Preview px widths are fixed per class** (the right column) and do not change with `scale`. Keeping them fixed keeps
  the canvas readable at any zoom; the print widths are what changes, and Rhino's print preview shows them.
- Each view can have its own `scale` (editor row or `[view]` header), so one component can bake a 1:50 detail and a
  1:200 site plan. Their bake layers are separate (§8.1), so the widths never clash.

"Ink" means black on a light background and near-white on a dark viewport. The preview blends toward
Rhino's viewport colour. `weights = layers` overrides the preset: each line takes its **source layer's** print width and
colour instead, and `scale` then changes nothing.

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
  Turning it off empties the Hidden output and saves a little memory.
- **Glass:** opaque by default (as in Make2D); `glass = clear` makes objects with a transparent material or on a layer
  named `*Glass*`/`*Glazing*` non-occluding.

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
- **Changing an input, or pressing Apply/OK in the editor, cancels a stale job at once.** The new job starts after 250 ms
  without further changes, so slider drags don't queue work.
- The outputs only ever hold a **complete result for the current inputs**. While a job runs, the outputs are
  empty and the preview shows the previous result greyed, labelled *Stale*. Draft results are **preview only**.
- **All views publish together**, in one downstream solve. The preview fills in view by view as each finishes (Q5).
- **Auto-run** is on by default. When it is off, the component shows *Paused · out of date*, and the footer
  button reads **Run**.
- **Cancel:** the footer button (it reads Cancel while running), **Esc** while the component is selected, the right-click
  **Cancel** item, or disabling the component. A cancelled job publishes nothing.
- **Reopen:** finished drawings are kept in a disk side-cache and reused when the model, views and settings haven't changed.
  Otherwise they rerun in the background. Results are never stored in the `.gh`. (Editor values *are* stored in the `.gh`;
  they are settings, not results.)

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
| Done, warnings | `6 drawings · 12 unresolved` | hidden | complete | unresolved in orange | Warning: "12 unresolved pieces in 2 views. They are in the Unresolved output (orange) and on the Unresolved layer when baked. Check them before issuing." |
| Unsupported | (as Done) | | complete | | Warning: "4 objects not drawn: 3 Text, 1 Point cloud. See Info." |
| Partial | `Partial · 5/6 drawings` | hidden | the 5 good views; the failed view's branches are empty | | Error naming the view: "View 4 'Section C': no clipping plane or named view with that name." |
| Paused | `Paused · out of date` | — | empty (or the result, if it still matches) | stale | — |
| Cancelled | `Cancelled` | — | empty | stale | — |
| Error | `Error` | — | empty | — | Error text |

### 5.3 Settings errors

Unknown keys and bad values are warnings that suggest the closest match ("Unknown setting `farclp`. Did you mean
`farclip`?"). Valid keys still apply. An unknown `[view]` header is a warning naming the views that exist.

### 5.4 Info (one per view)

```
Section A — Rectangle (plane origin 12.0, 6.0, 0.0), cuts 214 objects
View range: depth 40 m (editor) · far clip with line (editor) · beyond 3 m (auto)
Depth cueing: on, 10 %–90 %, limit 25 % (editor) · line weights 1:100 preset (scale 1:100, editor)
Scene 50,210 objects · 31,004 block instances of 212 definitions (reused)
18,422 visible · 6,108 hidden · 2,340 beyond · 96 far clip · 1,206 cut regions · 1,206 hatches
Styles: "Concrete" (layer Structure::Core) 212 · "Brick" (object) 18 · object colour 976 (hatch = styles)
Unresolved: 12 pieces on 3 objects (orange) · Not drawn: 3 Text, 1 Point cloud
Tolerance 0.001 m (document) · 41 short pieces dropped
2.1 s for this view · preparation 22.4 s shared by 6 views (cached)
```

---

## 6. Settings: the editor and the text input

There is **one set of settings per view**, with three ways to set it. From strongest to weakest:

1. **Settings input** (when wired): only the keys it contains, for the views it addresses.
2. **Editor, per-view row** (stored on the component).
3. **Editor, "All views" row** (stored on the component). The right-click menu toggles edit this row.
4. **Built-in default**, often *Auto* = inferred from the source (§2, §3).

A key set by the input is shown in the editor and menu as **locked, with the input's value** and a "set by input"
tag. Unwire the input and the editor's own value comes back; nothing is lost.

### 6.1 The View Range & Settings editor

Opens on **double-click** on the component, or **right-click ▸ View range & settings…**. A compact, Revit-like
dialog (Eto, so it follows Rhino's light/dark theme). The mockup shows it.

```
┌ M2D+ · View Range & Settings ───────────────────────────────────────────────────┐
│ View                Source            Depth   Beyond   Far clip      Fade        │
│ ▸ All views                          Auto    Auto     Without line  Off          │
│   Section A         Rectangle · cuts  40 m    ·        With line     On          │  ← selected
│   Section B         Rectangle · cuts  ·       ·        ·             ·           │  (· = as All views)
│   South elevation   Rectangle         60 m    Off      ·             On          │
├──────────────────────────────────────────────────────────────────────────────────┤
│ Section A                                                                          │
│ View range                         │ Depth cueing                                  │
│  View plane  Rectangle · cuts 214   │  [x] Fade with distance                       │
│  Depth       [40 m     ] [ ] Auto   │  Start  ──●──────────  10 %                   │
│  Beyond      [         ] [x] Auto   │  End    ───────────●─  90 %                   │
│  Far clip    ( ) No clip            │  Limit  ────●────────  25 %                   │
│              (•) Clip with line     │                                               │
│              ( ) Clip without line  │ Drawing                                       │
│                                     │  Hidden lines [x] 🔒 set by input             │
│                                     │  Scale  [1:100 ▾]  line weights only          │
│                                     │  Hatch  [From section styles ▾]  Weights [Auto ▾]│
│                                     │  Cut 0.50 · Profile 0.35 · Visible 0.18 · …    │
├──────────────────────────────────────────────────────────────────────────────────┤
│ [Copy as text]   [Reset view to All views]                 [OK] [Cancel] [Apply]  │
└──────────────────────────────────────────────────────────────────────────────────┘
```

- **Rows** are the current views (from the View input), plus **All views** at the top. A blank cell (`·`) inherits
  from All views. With one view there are still two rows, so the model is always the same.
- **Rows are keyed by the view's identity** (named view / clipping plane name or id; for GH geometry, the view index),
  so reordering named views keeps their settings. Values for views that no longer exist are kept, greyed, under
  "Unused (3)", until the user removes them.
- The lower half edits the selected row. Every field has a tooltip with its key (`farclip = line`).
- **Scale** is a drop-down (1:20, 1:50, 1:100, 1:200, 1:500) that also takes typed `1:n`. Its label says **"line weights
  only"**, and a one-line strip under it shows the preset's widths (Cut 0.50 · Profile 0.35 · Visible 0.18 · Hidden 0.13 ·
  Beyond 0.09 at 1:100), so it is clear that nothing gets smaller. With `weights = layers` the strip reads "from source
  layers" and Scale is greyed.
- **Hatch** offers *From section styles* (default, "else object colour"), *Object colour*, *Black* and *None* (§3.7).
- **OK / Apply** start a re-run (one GH undo record per Apply). **Cancel** discards. The dialog is modeless, so you can
  orbit Rhino while it is open.
- **Copy as text** copies the editor's values as Settings text (with `[view]` headers), ready to paste into a Panel.
- On the canvas, a cobalt dot next to **S** means "this component has editor values"; hovering **S** shows the
  effective settings per view and where each came from (input / editor / default).

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
| `depth` | `auto` · distance | `auto`: Box height, clipping plane *Depth*, else scene extent | distance from the view plane | View Depth (plan) / Far Clip Offset | editor |
| `farclip` | `none` · `line` · `noline` | `noline` | — | Far Clipping: No clip / Clip with line / Clip without line | editor, menu |
| `beyond` | `auto` · `off` · distance | `auto` (3 m in cut views, off otherwise) | distance from the view plane | View range *Bottom* → `<Beyond>` line style | editor, menu |
| `fade` | `on` · `off` | `off` | — | Depth Cueing | editor, menu |
| `fade.start` | 0–100 | `0 %` | % of depth | Depth Cueing *Start* | editor |
| `fade.end` | 0–100 | `100 %` | % of depth | Depth Cueing *End* | editor |
| `fade.limit` | 0–100 | `30 %` | % intensity | Depth Cueing *Fade Limit* | editor |
| `crop` | `auto` · `off` | `auto` | — | Crop View | editor |
| **Lines** | | | | | |
| `hidden` | `on` · `off` | `on` | — | Show Hidden Lines | editor, menu |
| `tangents` | `on` · `off` | `off` | — | — (Make2D option) | editor, menu |
| `seams` | `on` · `off` | `off` | — | — (Make2D option) | editor, menu |
| `glass` | `opaque` · `clear` | `opaque` | — | — | editor |
| `scale` | `1:n` · `n` | `1:100` | — (picks the nearest preset band, §3.6) | View Scale (line weights only; the drawing stays 1:1) | editor, menu |
| `weights` | `auto` · `layers` | `auto` (the `scale` preset, §3.6) | — | Object Styles | editor, menu |
| **Cuts** | | | | | |
| `hatch` | `styles` · `colour` · `black` · `none` | `styles` (section style, else object colour) | — | Cut pattern (material / V/G override) | editor, menu |
| `cuts` | `separate` · `merge` | `separate` | — | Join Geometry | editor, menu |
| **Placement** (always 1:1) | | | | | |
| `gap` | `auto` · distance | `auto` (1/10 of mean frame width) | distance on the target | — (sheet placement) | editor |
| `name` | text | auto (§8.1) | — | View Name | editor |
| **Output and diagnosis** | | | | | |
| `tree` | `branch` · `object` | `branch` | — | — | menu |
| `tolerance` | distance | document absolute tolerance | distance | — | text only |

---

## 7. Right-click menu (component)

These items come after GH's standard items (Preview, Enabled, Bake…, Help). Every toggle edits the editor's
**All views** row; an item whose key is set by the input is greyed and shows "(set by input)".

```
View range & settings…            Double-click
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
Beyond ▸     Auto ✓ · Off · 1 m · 3 m · 10 m          beyond  = auto | off | <distance>
Far clip ▸   No clip · Clip with line · Clip without line ✓   farclip = none | line | noline
Depth cueing                          fade     = off | on   (start/end/limit in the editor)
Cuts ▸       Separate per object ✓ · Merge touching (same style)   cuts = separate | merge
Hatch ▸      From section styles ✓ · Object colour · Black · None   hatch = styles | colour | black | none
Scale ▸      1:20 · 1:50 · 1:100 ✓ · 1:200 · 1:500 · Other… (editor)   scale = 1:n   (line weights only)
Line weights ▸ From scale ✓ · From source layers                    weights = auto | layers
────────────────
Preview ▸    Hidden ✓ · Beyond ✓ · Far clip ✓ · Hatch ✓ · Frame
Output tree ▸ Per source branch ✓ · Branch per object               tree = branch | object
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
   └─ Unresolved                   (only when there is something unresolved; orange)
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
  <object id>`, `m2d.class = visible|hidden|beyond|farclip|cut|hatch|unresolved`, `m2d.hash = <geometry hash>`.
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

**3. A cropped section from a rectangle, placed 1:1 on a sheet plane**

```
  Rectangle (in a vertical plane through the building) ── V┐
  Plane (sheet origin, off to the side) ────────────────── T├─ M2D+ ── Frame = the rectangle, moved to T
  Panel "depth = 12 m / farclip = line / fade = on" ────── S┘
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
  Double-click: All views → scale 1:100 (line weights), depth-cue on; Section rows → far clip with line
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
| A separate "View" or "Settings" builder component | It is a second component, which goes against the agreed decision. The editor (double-click) gives the same visual control without a second component, and the Settings text covers parametric use. |
| View range values as inputs (Depth, Far clip, Fade start/end/limit) | Five more inputs, mostly left at default. They are editor fields and Settings keys instead. |
| Settings *only* as text (revision 1) | Invisible: nothing on the canvas said what was set or what could be set. The editor and the S dot fix that; text stays for parametric control. |
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
| "Zoomable" optional extra inputs (variable parameters) | They bring input sprawl back with a hidden "+" button. The editor gives the same power visibly. |
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
accepted all Q9–Q15 recommendations on 2026-11-14, which locks the component at 4 inputs and 9 outputs. **Orchestrator decisions, 2026-11-14 (owner may override):** Q16–Q19 take the recommendations. Q14 is revised: a style's *Viewport* background fill uses the object's display colour, the same as the default fill, and is noted in Info. **Q16–Q19 are
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
| Q9 | Depth cueing on or off by default? | **Off**, as in Revit. Beyond already greys far lines in plans and sections; fading on top by default double-greys them. One click turns it on, and a project can store it in the editor's All views row. |
| Q10 | Far clip default? | **Clip without line** (Revit's default for sections). It only matters once `depth` is set, because `depth = auto` clips nothing. |
| Q11 | Anchor = the source's own plane for Rectangle/Plane/Box/clipping plane/named view; world origin only for Vector/direction? | **Yes** (§4). It is what "1-to-1" means. |
| Q12 | New ninth output **Far clip**? | **Yes.** Empty unless `farclip = line`. |
| Q13 | Settings input as tree access with `[view]` headers? | **Yes.** It fixes the Panel line-splitting problem and lets Copy as text round-trip the editor. |
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
    styling only and must not invalidate the drawing cache; `depth`, `farclip`, `beyond`, `crop`, `hidden`, `tangents`, `seams`, `glass` and `cuts` do.
