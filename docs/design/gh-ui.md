# M3 Grasshopper UI layer

Implements UX revision 3 §1 (custom attributes), §2.1 (View input menu), §5.2 (states and the
message tab), §6 (the View Range & Settings editor, precedence, "set by input") and §7 (component
menu) on top of the M3 Core API ([m3-core.md](m3-core.md)). Core semantics are unchanged: the UI
stores sparse Settings **text** and lets `Settings.Resolve` decide everything.

## Screenshots

| File | What it shows | Made by |
|---|---|---|
| [gh-ui/canvas.png](gh-ui/canvas.png) | Six states on GH's default skin: Done · **Bake**, Drawing 72 % · **Cancel** (determinate progress line), Reading · Cancel (indeterminate sweep), Paused · **Run**, Cancelled · Run, and No view (GH's own required-input warning). Cobalt dot beside **S** where the editor holds values; a Panel wired into S. | `GrasshopperUiTests.CanvasAndEditorRender` (real GH canvas, `GenerateHiResImageTile`, 2×) |
| [gh-ui/canvas-dark.png](gh-ui/canvas-dark.png) | The same on a dark GH skin. | same test |
| [gh-ui/menu.png](gh-ui/menu.png) | The component's right-click items after `hidden = off` and `farclip = line` from the menu. | `GrasshopperUiTests.AttributesMenusUndoAndSavedGhRoundTrip` |
| [gh-ui/editor.png](gh-ui/editor.png) | The editor, **Section A** row selected: "·" inheritance in the table, Reset links, Hidden lines locked "set by input", the unused row, the weight-preset strip. | `tools/EditorPreview` |
| [gh-ui/editor-all-views.png](gh-ui/editor-all-views.png) | The editor, **All views** row ("Reset to defaults"). | `tools/EditorPreview` |

The editor PNGs come from the **same** `SettingsEditorForm.cs` compiled against public Eto 2.9 on
its WinForms backend. Inside Rhino the form runs on Rhino's Eto 2.11 (WPF) and follows Rhino's
light/dark theme (`UseRhinoStyle`, looked up by reflection). The headless Rhino test opens it on
that real platform and asserts layout, lock and row state. It cannot capture pixels: this session is
a disconnected desktop, where WPF composes nothing (even a bare `TextBlock` renders empty), while
GDI works. Regenerate:

```powershell
# Canvas + menu (licensed, own Rhino.Inside process only)
$env:M2D_UI_SCREENSHOTS = "$PWD/docs/design/gh-ui"   # plus the usual M2D_RHINO_* variables
dotnet test tests/M2DPlus.Rhino.Tests -c Debug --filter FullyQualifiedName~GrasshopperUiTests
# Editor (no Rhino, no licence)
dotnet run --project tools/EditorPreview -- docs/design/gh-ui
```

## Structure

All new code is under `src/M2DPlus.Grasshopper/UI/`.

| File | Role |
|---|---|
| `Model/ComponentUiState.cs` | Immutable persisted UI state: All views row, per-view rows keyed by identity (raw text, any key), Run automatically, Preview layers, unknown top-level JSON. `ToEditorSettings()` hands Core only the keys it knows. |
| `Model/UiStateSerializer.cs` | Versioned compact JSON (v1), defaults omitted. Unknown row keys, preview names and top-level properties survive a round trip; newer versions read with a note; bad data falls back to defaults and never throws. |
| `Model/SettingsCatalog.cs` | Labels/choices for the §6.3 keys, canonical text, display text, weight-preset strip (from `Styling.LineWeightPreset`). |
| `Model/SettingsPresentation.cs` | Precedence display model: per row and key, the shown value, origin (row / All views / default / input), lock and note. Resolves through `Settings.Resolve`. |
| `Model/EditorSession.cs` | Toolkit-free editor: working copy, select row, validate (Core's message and suggestion), set/clear, Reset, Remove unused, Copy as text (`Settings.CopyEditorAsText`), Apply/Revert/Rebase. |
| `Model/MenuModel.cs` | `ComponentMenu` (§7) and `ViewMenu` (§2.1) as plain `MenuNode` trees + commands. |
| `Model/Runtime.cs` | `DrawingPhase`/`DrawingRuntime`, footer button choice, job-progress fraction (prepare 0–40 %, draw 40–100 %, Reading/Queued indeterminate). |
| `DrawingUi.cs` | Controller owned by the component: state, GH Write/Read, undo (`RecordUndoEvent`), menu execution, editor window, events, fallback view context. |
| `DrawingAttributes.cs` | Canvas: progress line, message tab with button, double-click, S dot, ≤10 Hz repaint. |
| `SettingsEditorForm.cs` | Eto form (modeless), Eto + models only. |
| `ViewParam.cs` | View input (`Param_GenericObject` subclass) with the §2.1 menu. GUID `a865210f-23f7-4626-8610-6f6e66cb8984`, hidden. |
| `MenuRenderer.cs` | `MenuNode` → GH `ToolStrip` items. |
| `DrawingComponent.Ui.cs` | `partial class DrawingComponent`: the hook-up overrides (below). |

### Canvas attributes (§1, §5.2)
- **Progress line**: 2 px cobalt `#1F6FD1` inside the bottom edge of the body, over a faint cobalt
  track, while `Running`. Fraction from `DrawingRuntime.Fraction`; null → a quarter-width sweep.
  A WinForms timer repaints at 100 ms (≤ 10 Hz) only while running and visible, and stops itself.
- **Message tab**: drawn by M2D+ in GH's own shape and palette edge colour (so it follows skins), 17
  units tall, message centred left of the button and shrunk to fit as GH does. The base pass
  renders with `Message` temporarily null so GH's tab is not drawn twice.
- **Button**: Bake (done) / Cancel (running) / Run (paused, cancelled); none otherwise. Bake/Run are
  cobalt with white text; Cancel is a quiet outline in the tab's ink. Shift-click Bake = add as copy.
  Fades with `GH_Canvas.ZoomFadeMedium` like GH's tab; hidden when zoomed far out. Tooltips per button.
- **Minimum width 148**: the extra width goes to the parameter columns (the icon/nickname box keeps
  GH's size), so the message and button stay readable. Inputs/outputs and their order are unchanged.
- **Double-click** the body or tab (not the button) opens the editor. **S dot**: 5 px cobalt when the
  editor holds values.

### Editor (§6.1)
Upper table: All views, each view, then unused rows (greyed); "·" = as All views; "(input)" marks
input-set cells. Lower half edits the selected row in four groups: **View range** (plane, depth,
beyond, far clip none/line/noline, crop, depth cueing on + start/end/limit sliders), **Lines**
(hidden, tangents, seams, glass, weights), **Cuts** (hatch styles/colour/black/none, cuts
separate/merge), **Placement** (scale "line weights only" with the preset strip, gap, name). Each
field shows a note ("as All views", or cobalt "set by input" with the control disabled and showing
the input's value) and a Reset link when the row sets it. Invalid text shows "not valid" with Core's
message as tooltip and is not stored. Footer: Copy as text, Reset to All views / Reset to defaults,
Remove unused (n), OK, Cancel, Apply. Esc = Cancel. Apply/OK commit one GH undo record and re-solve
(which cancels the stale job). Menu toggles or undo while open rebase the editor (pending edits kept).

### Menus (§7, §2.1)
Component items follow §7 order, including Scale ▸ (with "Other… (editor)"). Toggles write the
**All views** row. A key the input sets for every view is greyed "(set by input)"; for some views the
item stays enabled and says "(set by input for 1 of 3 views)". Typed values that are not presets
show as a checked, disabled first item. Cancel appears only while running. Bake drawing / Bake one ▸
are enabled only with a complete drawing. Bake, Bake one, Clear drawing cache and (until wired)
Cancel raise events and show a remark when nothing is subscribed. Copy info copies the Info output;
Copy settings as text uses `Settings.CopyEditorAsText`. Preview ▸ only redraws.

View param: Named views ▸ (multi-check, active doc), Clipping planes ▸ (named), Directions ▸,
All named views, All clipping planes, Pick clipping planes in Rhino… (unique name, else object id),
Capture active view as named view… (`M2D 01`…, refuses perspective), Clear. All greyed with a wire.
Choices are persistent `GH_String` values; other persistent goo is kept.

### Persistence
`GH_IWriter.SetString("M2DPlus.UiState", json)`. Example: `{"v":1,"all":{"hidden":"off"},
"views":{"text:section a":{"depth":"40 m"}},"auto":false}`. Results are never stored.

## Hook-up (merge these by hand)

Two lines change in existing files; everything else is new.

`src/M2DPlus.Grasshopper/DrawingComponent.cs`, `RegisterInputParams` (line 23): replace
`p.AddGenericParameter("View", "V", …, GH_ParamAccess.list);` with
```csharp
p.AddParameter(new UI.ViewParam(), "View", "V", "<same description>", GH_ParamAccess.list);
```
`ViewParam` derives from `Param_GenericObject`, so saved definitions, casts and `ClearData` checks still work.

`src/M2DPlus.Grasshopper/DrawingComponent.Async.cs`, `SolveDrawing`, directly after `if (_started) return;` (line 43):
```csharp
if (!Ui.ShouldStart()) { Message = "Paused · out of date"; return; }
```

`UI/DrawingComponent.Ui.cs` (new, same partial class) adds `Ui`, `CreateAttributes`,
`AppendAdditionalComponentMenuItems`, `Write`, `Read`, wires **Cancel** to `Invalidate()` and supplies
`UiRuntime()`, which reads `_drawing`, `_error`, `_started`, `_progress`/`_progressSync` and
`Message == "Cancelled"`. If M1b renames those fields or adds any of these overrides, merge there.

Still for the component to wire (not done here, to stay out of M1b's files):
- `Settings.Resolve(views, branches, units, Ui.EditorSettings)` (replaces the M1a `Settings.Parse`).
- `Ui.Context = () => new UiContext(realContexts, sortedBranches, units)` once views resolve, so rows
  use real identities/names/sources. Until then `DrawingUi.FallbackContext` uses `text:<lower text>`
  or `index:<i>` identities — rows keyed differently later appear as "unused" and can be removed.
- `Ui.BakeRequested` / `Ui.ClearCacheRequested` when bake and the side cache exist; `Ui.RunRequested`
  only if Run needs more than the built-in gate.
- `Ui.ReadProblem` as a Remark during solve (a newer or unreadable stored state).

## Tests

- `tests/M2DPlus.Grasshopper.Tests` (new, in the solution, no licence; 42 tests): persistence round
  trip incl. unknown/future data and corrupt input, precedence presentation and locks, editor session
  (validation, locks, reset, apply/revert/rebase, Copy as text → Settings input reproduces every
  value), component and View menu models, footer button and progress mapping. It loads the `.gha`
  through a module-initializer resolver because a `.gha` cannot sit in the runtime's trusted list.
  The pre-commit hook still runs Core tests only; add this project there if wanted.
- `tests/M2DPlus.Rhino.Tests/GrasshopperUiTests.cs` (licensed, opt-in): attributes and View param
  types, View menu toggles, menu building, one-step GH undo, Run automatically gate, `.gh` write →
  reopen round trip of editor state and View texts, canvas render in two skins, editor opened on
  Rhino's Eto platform with locks. Full licensed suite (48) passes on .NET 8 and .NET 10 hosts.

## Not in this slice
Hover tooltip on S with effective settings per view; Esc-to-cancel on the canvas; preview layer
rendering (only the toggles are stored); cache size in the menu.
