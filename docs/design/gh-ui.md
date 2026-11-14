# M3 Grasshopper UI layer

Implements UX revision 4 §1 (custom attributes), §1.1 (the Depth input), §2.1–2.2 (View input menu and view
objects), §5.2 (states and the message tab), §6 (precedence) and §7 (component menu) on top of the M3 Core API
([m3-core.md](m3-core.md)). Revision 3's View Range & Settings editor (PR #7) is **removed** (owner, 2026-11-14):
global defaults are menu toggles, per-view values are `m2d.*` user text on view objects, and `Settings.Resolve`
still decides everything.

## Screenshots

| File | What it shows | Made by |
|---|---|---|
| [gh-ui/canvas.png](gh-ui/canvas.png) | The component with **five inputs** (G V T S **D**) in six states on GH's default skin: Done · **Bake**, Drawing 72 % · **Cancel** (determinate progress line), Reading · Cancel (indeterminate sweep), Paused · **Run**, Cancelled · Run, and No view (GH's required-input warning). Panels wired into S (`hidden = off`) and D (`8 m`). No S dot. | `GrasshopperUiTests.CanvasRender` (real GH canvas, `GenerateHiResImageTile`, 2×) |
| [gh-ui/canvas-dark.png](gh-ui/canvas-dark.png) | The same on a dark GH skin. | same test |
| [gh-ui/menu.png](gh-ui/menu.png) | The component's right-click items after `hidden = off` and `farclip = line` from the menu; the view-object group on top (greyed: V references no Rhino objects in that test). | `GrasshopperUiTests.AttributesMenusUndoAndSavedGhRoundTrip` |
| [gh-ui/view-menu.png](gh-ui/view-menu.png) | The View input's right-click items. | same test |
| [gh-ui/view-box-preview.png](gh-ui/view-box-preview.png) | A validated view box's preview (frame, look arrow, dotted depth volume, name) over two boxes, unselected (subtle) and selected (cobalt). An **offscreen GDI axonometric** of the exact `ViewPreview` lines the component draws, not a Rhino viewport capture (headless Rhino has no viewports). | `ViewObjectTests.SettingsPrecedence…` |

Regenerate (licensed, own Rhino.Inside process only):

```powershell
$env:M2D_UI_SCREENSHOTS = "$PWD/docs/design/gh-ui"   # plus the usual M2D_RHINO_* variables
dotnet test tests/M2DPlus.Rhino.Tests -c Debug --filter "FullyQualifiedName~GrasshopperUiTests|FullyQualifiedName~ViewObjectTests"
```

## Structure

| File | Role |
|---|---|
| `src/M2DPlus.Rhino/Views/ViewBoxes.cs` | Rhino-only, no GH: `Establish` (frame from a surface, trimmed surface, curve or box, with a reason when it can't), `Face`, `Flip`, `AutoDepth`, `Build`/`ReadBox` (the `Extrusion`), `ReadRow`/`WriteDefaults` (user text), and the document commands `Validate`, `Flip`, `FrameFromViewport`, `Create` (each one undo record), `SceneBounds`, `NextName`, `ViewsLayer`. |
| `src/M2DPlus.Rhino/Views/ViewPreview.cs` | Frame, arrow, front/back/depth edges for one resolved view: the lines the component draws and the screenshot renders. |
| `src/M2DPlus.Rhino/Services/Views.cs` | `ViewRequest.Parse` now reads referenced view boxes (exactly like a Box), not-yet-validated frame surfaces/curves (like a Rectangle, faced at the model, with a remark) and raw Brep/Curve/Extrusion values; adds `Kind`, `ObjectId`, `Note`. |
| `src/M2DPlus.Rhino/Services/HostCapture.cs` | `SceneCapture(..., excluded)` keeps referenced view objects out of document mode; `IsDrawable` is shared with `SceneBounds`. |
| `UI/Model/ComponentUiState.cs` | Persisted UI state: the **menu row** (sparse text, any key), Run automatically, Preview layers (new `views` = View frames), unknown top-level JSON. `ToEditorSettings(viewRows)` joins the view objects' rows for Core. |
| `UI/Model/UiStateSerializer.cs` | Versioned compact JSON (v1). The removed editor's `views` rows in pre-release files are kept verbatim as unknown data and never resolved. |
| `UI/Model/SettingsPresentation.cs` | `UiContext` (+ `ViewRows`, `Depth`) and the menu's view of a key: locked when the input sets it for every view, "(set on n of m view objects)" when objects override it. |
| `UI/Model/MenuModel.cs` | `ComponentMenu` (§7), `ViewMenu` (§2.1) and the shared `ViewCommands` group as plain `MenuNode` trees. |
| `UI/ViewObjects.cs` | GH side of view objects: which Rhino objects V references (`IdOf`, `References`), Validate (refuses Surface/Curve **wires**, re-types references stored on V to Brep), Flip, Create (+ adds the reference), Select. Reports to the command line and the component. |
| `UI/ViewParam.cs` | View input (`Param_GenericObject` subclass, GUID `a865210f-…`, hidden) with Pick views in Rhino…, the view-object commands and the text choices. |
| `UI/DrawingUi.cs` | Controller: state, GH Write/Read, undo, menu execution, view-command notes, double-click selection, fallback context (object rows included). |
| `UI/DrawingAttributes.cs` | Canvas: progress line, message tab with button, double-click → `SelectViewObjects`. No S dot. |
| `UI/DrawingComponent.Preview.cs` | `DrawViewportWires`/`ClippingBox` for the view previews. |
| `DrawingComponent*.cs` | Input 4 **Depth** (text list, optional), Settings resolution after capture, depth applied to the engine. |

### Solve wiring
`SolveDrawing` parses views on the host (view boxes via their object id), collects the Settings branches (the hidden
`multiplethreads` switch is read here and kept from Core), the Depth items and each view object's `m2d.*` row, and
stores a real `UiContext` for the menus. After capture, with the scene's depth behind each view known,
`ResolveSettings` calls `Settings.Resolve(views, branches, units, menu + view rows, depth)`. **Only depth reaches
the engine in this slice**: an explicit depth sets `CutFrame.SourceDepth` for Plane, Rectangle, unvalidated frame and
clipping-plane views; Box and view box keep their geometric depth (Core remarks when another depth was given). Other
resolved keys appear in Info with their origin, and one remark lists them as "resolved but not applied yet".
Diagnostics become component messages prefixed with the view name.

### Canvas attributes (§1, §5.2)
Unchanged from PR #7 except: **double-click** the body or tab (not the button) selects the referenced view objects
in Rhino (GH's default when there are none), and the cobalt **S dot is gone** (there are no editor values to signal).

### Menus (§7, §2.1)
Component: Validate views · Flip view direction · Create view from current viewport · Select view objects
(Double-click) │ Bake … │ Run … │ setting toggles (menu defaults) · Reset menu defaults │ Preview ▸ (View frames ✓ …) ·
Output tree │ Copy info · Copy settings as text (menu + view objects, with headers) · Clear drawing cache. Scale ▸ has
the five presets (other values via text) and the preset widths as its tooltip.
View param: Pick views in Rhino… · Validate views · Flip view direction · Create view from current viewport │ Named
views ▸ · Clipping planes ▸ · Directions ▸ · All named views · All clipping planes │ Clear (texts and references).
Choosing is greyed while wired; the view-object commands still work on wired references.

### Persistence
`GH_IWriter.SetString("M2DPlus.UiState", json)`, e.g. `{"v":1,"all":{"hidden":"off"},"auto":false}`. A definition
saved with four inputs reads into five (the licensed test unregisters Depth, saves, and reads into a new component).

## Tests

- `tests/M2DPlus.Core.Tests/DepthPrecedenceTests.cs` (no licence): Depth > Settings > auto, units, one-for-all /
  one-per-view / wrong count / bad values, Box geometry wins over all four sources with one remark, per-key
  precedence input > view object > menu > default with descriptions.
- `tests/M2DPlus.Grasshopper.Tests` (no licence; 40): persistence incl. the removed `views` rows, view-object rows
  joining the menu row, menu order (no editor items), view-command enabling, Reset, Scale presets, input locks and
  "(set on n of m view objects)", Depth reaching the presentation, catalogue values parse.
- `tests/M2DPlus.Rhino.Tests/ViewObjectTests.cs` (licensed, opt-in): Establish on good/bent/parallelogram/triangle/
  disc/polysurface/sliver/open inputs and on trimmed rectangles (split edge, rotated, cut from a 100 m plane);
  build/read round trip and Box-equivalent parse; in-place conversion keeps the id, re-types the GH reference, writes
  defaults, and **one Rhino undo** restores surface and attributes; Surface-wire refusal; user text never
  overwritten; Flip (front face kept, extends to the other side, not mirrored, undo); Create view from a parallel
  viewport (geometry builder, name, layer, reference, undo, perspective refusal); component Depth input on Plane and
  Rectangle views with independently placed boxes (far box present at auto, absent at 4 m, Depth beats Settings,
  unit conversion, wrong count); Box and view box geometry win with a remark; precedence end to end and a re-solve
  after a user-text edit; preview geometry.
- `GrasshopperUiTests` (licensed): five inputs and their access, editor gone, menus, undo, `.gh` round trip, 4-input
  definition upgrade, canvas render in two skins.

Headless Rhino raises **no** object-modify/replace events (measured), so the event → re-run path relies on the M1a
document watch and is an interactive check; the test re-solves explicitly to prove nothing stale is reused.

## Not in this slice
Applying farclip/beyond/fade/hidden/… to the engine (resolved and reported only); hover tooltip on inputs; Esc to
cancel on the canvas; preview of drawing layers; interactive checks that a gumball drag of a view box's back face
keeps it an `Extrusion` in Rhino 8, and of the Wireframe display override.
