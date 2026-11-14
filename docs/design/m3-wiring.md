# M3 engine, placement, preview and bake wiring

Implements UX revision 4 on top of the M3 Core and GH UI slices. This work does not
install a Yak, commit, publish, or change the owner's Rhino runtime.

## Drawing and presentation boundaries

`ViewRequest.Settings` carries the host-resolved immutable `EffectiveSettings`.
The component reports every value and its provenance. The default for hidden
lines is the single `Settings.DefaultHidden` constant, currently `true`.

HLD receives hidden/tangent/seam switches. Extraction also filters hidden records:
Rhino 8.36 can return them even with `IncludeHiddenCurves=false`. Native
`segment.IsSceneSilhouette` is retained as profile metadata, giving profile print
widths and two-pixel preview strokes without changing the plain Visible output.

Sections use route C's existing physical near/far trim, independent far-plane
intersections, per-source scope and Cut-over-FarClip precedence. `none` does not
far-trim; `noline` suppresses generated boundaries; `line` publishes them at output
index 3. Non-cut views retain the uncut adapter when their entire scene fits the
range; a far limit inside the scene uses route C. Auto depth includes a tolerance
margin beyond input bounds. No tolerance contact is silently nudged.

Native curves retain an owned, unflattened world-space counterpart, with the same
parameter domain through provenance/crop trims. `DrawingPresentation` intersects
that curve with the Beyond plane using `Intersection.CurvePlane`; it trims both
representations at **every** root and classifies the resulting interiors. It never
interpolates general curve extrema or samples to find split parameters. Hidden
pieces stay Hidden. Points retain Visible classification (Beyond is a curve-only
output) and still receive depth fade. Depth extrema come from the trimmed curve in view coordinates;
`ViewRange.Intensity` supplies the per-piece fade. Curve outputs remain ordinary
curves, without display attributes. Fade is multiplied by Beyond's 55% ink in
preview/bake, and bake quantizes the fade to 5% steps.

`DrawingCache` retains one raw batch per component. Its key includes the scene
visibility revision (geometry, identity, clipping participation and opacity), view definition, depth/farclip/crop/hidden/tangents/seams/glass/
tolerance and native threading. Beyond, fade, weights, scale, hatch, cuts, name,
tree, gap and Target do not call HLD. Hatch/cut policy is reapplied by the section
engine from certified regions: all loops and crop provenance survive, and only
compatible styles merge. Merged regions use the lowest contributing source path.
Source item indices are captured before compute for `tree=object`. Source
colour/width/section-style edits refresh presentation from the latest capture
without HLD; clipping-plane styles are similarly excluded from visibility keys.

The cache owns the raw batch; presentation holds a lease. Clear retires owners
and prevents a running solve from repopulating the cleared cache. A per-component
gate serializes its cache access, and the process-wide scheduler still serializes
HLD. Both cache misses **and hits** run presentation work away from the UI thread.
The component's existing generation gate publishes only complete current batches.
GH outputs and placed results own independent geometry.

## Clear glass rule

Opaque is the default. In `glass=clear`, a source is glass when its resolved Rhino
material has **any positive Transparency**, or any segment of its source layer
path contains **Glass** or **Glazing**, case-insensitively (thus `Facade Glazing`
and `Curtain::Glass panels` both match). Parent transparency is carried through
block capture. These are host snapshots and participate in the scene fingerprint.

Clear bodies are replaced in the HLD occluder scene by their view silhouettes and
feature curves; other bodies can still hide those curves. Their surfaces never
occlude objects behind them. Section regions are computed from the original body,
so cut boundaries and fills remain available. GH geometry without document
material/layer metadata remains opaque. No additional behind-glass line class is
introduced.

## Placement and preview

`PlacementPlan` maps each source anchor onto its Target rigidly at 1:1. One target
lays out a row along target X using each effective gap; other mismatches say
`Give 1 target, or one per view`. Plane/rectangle/box/clipping-plane anchors use
their resolved source plane. Direction/vector anchors project world origin onto
the scene-front drawing plane. Named views retain their camera target, projected
onto that front plane. With no target the transform is identity.

Frames use crop extents or projected, volume-restricted **input** boxes. Box/plane
intersections contribute restriction vertices. Empty/degenerate scenes have an
explicit fallback extent. Visibility, hatch, weights and output bounds never fit
or move the frame. Scale only selects the preset; neither geometry nor hatch
pattern scale is scaled.

The custom preview replaces GH's default output rendering. It uses the preset's
per-class pixel widths, dashed Hidden, orange Unresolved, and fade blended toward
the viewport background. Source-layer mode uses the source colour/width. Hatch
display geometry is built from its captured pattern definition, including solid
fills/backgrounds. Hidden/Beyond/Far clip/Hatch/Frame menu toggles control preview
only. View-frame previews retain the UX revision 4 frame/arrow/depth display.
Rhino's Escape event cancels the selected component's running job immediately;
native lifetime is still governed by the scheduler and stale-publication gate.

## Bake

`DrawingBake` is a Rhino-only host adapter. The button, Bake/Bake one menus, Shift
copy, and GH's standard Bake overloads all use it. Standard GH layer selection is
ignored with a remark. Component-allocated drawing names persist in a separate
`M3BakeNames` archive chunk; no drawing geometry is stored in the GH definition.

- A whole bake uses one Rhino undo record, including deletes, pattern insertion,
  layer creation and temporary unlock/restore.
- Paths are `M2D+::<drawing>::<Category>::<source layer path>`. Profile belongs to
  Visible with a per-object profile print width. Frame is not baked. Unresolved
  is orange and never faded.
- New layers receive category styling. Existing colours/linetypes are preserved.
  Only a still-unedited managed width follows a changed preset, through
  `BakeLayerWidths.Decide`; edited widths are reported and retained.
- Fade is per-object object colour **and** print colour. Section boundary widths,
  linetypes and colours and hatch pattern/background colours are applied from the
  resolved section style. Pattern definitions are content-addressed in the Rhino
  pattern table, so a name collision cannot silently substitute a different hatch.
  The full section snapshot is also retained on the hatch for print/background
  provenance.
- `BakePlan.Replace` compares the saved hash/layer identity with freshly observed
  state. Hashes include complete geometry SHA-256 and editable object appearance;
  tags include `m2d.bake/source/class/hash/layer`. Edited or layer-moved objects are
  kept and counted; unchanged owned objects are replaced. Other owners are untouched.
- Locked layer ancestors are temporarily unlocked and restored. Their visibility
  is restored explicitly because Rhino lock setters can change it. Hidden layers
  stay hidden and are listed in the report. Only empty descendants tagged as
  created by this component are removed.
- Shift-copy reserves all existing drawing names, allocates a suffix and leaves
  the normal replacement name intact. A bake detaches the component's document
  watch during its own mutation so events cannot dispose the drawing being baked.

## Validation and artifacts

`M3WiringTests` uses analytic expectations: quadratic Beyond roots, fade extrema,
box/probe hidden intervals, material/layer glass, a filleted box's tangent edges,
a tapered solid's 13-unit far square, known section areas, rigid target positions,
zero HLD calls for presentation edits, replacement/edit preservation, layer styles,
one-step undo, and real document hatch save/reopen and replacement after reopening.
`M3ComponentTests` exercises actual GH Target output, cached style edits, Bake,
Bake one, Shift-copy and Clear cache events. Earlier depth tests now inspect both
Visible and Beyond, since Beyond auto is actually applied.

Run licensed hosts **serially** with `tools/Test-WalkingSkeleton.ps1 -Resolver ...`.
The script runs Core, GH UI model and licensed tests on both .NET 8 and .NET 10,
without changing Rhino settings. Optional `-Filter` narrows licensed tests only.

`tools/render/Render.ps1 -Engine m3 -Models @(...)` writes the contact sheet,
individual PNG/SVG drawings and `results.json` under `artifacts/render/m3/`. The
sheet uses applied profile/cut widths, Hidden dashes and Beyond/fade intensities;
an analytic tapered control exposes generated far-clip lines. The building and
curved-steel/diagrid inputs remain external, and the manifest records their hashes,
the engine hash and native/runtime versions. Artifacts are local and ignored by
Git under the repository's existing artifact policy.

### Final local acceptance (2026-11-14)

- Debug and Release: **0 warnings, 0 errors**.
- **186 Core + 40 GH UI-model + 136 licensed Rhino/GH tests pass on each runtime**,
  .NET 8 and .NET 10, serially through `Test-WalkingSkeleton.ps1`. TRX records are
  `m3-core-net8/net10.trx`, `m3-ui-net8/net10.trx` and
  `m3-rhino-net8/net10.trx` in the respective test projects' `TestResults` folders.
- The forced Git-sh hook passes. Ordinary solution tests pass Core/UI tests and
  skip licensed discovery without starting Rhino. `git diff --check` passes.
- Five final render cards: building plan/section, curved-steel plan/section, and
  the analytic range control. Rhino **8.36.26296.17001**, renderer .NET **8.0.34**.
  There are no execution errors. Building plan: 60 regions and four Beyond pieces;
  section: 5 regions, 355 Visible, 160 Hidden and 360 Beyond pieces, with 129 fade
  intensities. The range control has four FarClip pieces (the independent licensed
  oracle checks their 13-unit total perimeter). Curved-steel section retains 18
  Unresolved pieces and explicit source-certification diagnostics.
- `artifacts/m3-tests-all.log`, `m3-tests-focused.log`, `m3-hook.log`,
  `m3-tests-unlicensed.log` and `m3-render.log` retain the local run output.
- No commit, push, installation or process attachment was performed.

## Limits and manual checks

M2's certification boundary is unchanged: unverified freeform section crossings
remain explicitly uncertain; the curved-steel renders are not a new whole-model
correctness claim. Native silhouette flags identify scene profiles, not every
architectural feature an owner might want heavier. Preview dashed curves are
display tessellations; the output and baked curves remain exact. The contact
sheet's line-pattern raster hatch rendering retains its documented approximation.
Interactive installed-Yak preview, background/display modes, and selected-component
Escape behavior remain manual checks; headless tests do not create an interactive
Rhino viewport. No worker or durable drawing-cache implementation is added here.
