# M3 Core API and adapter contract

Implemented against UX revision 3, REQUIREMENTS and PLAN. This slice contains pure
immutable values and decisions only. It does not connect the editor, preview,
Rhino clipping, or bake mutation. `Settings.Parse` and both original distance
parsing signatures remain compatible; the typed entry point is `Settings.Resolve`.
The existing numeric identities of Visible, Hidden and Unresolved are preserved;
Beyond, FarClip, Cut, Hatch and Profile are appended to `LineClass`.

## Settings

`Settings.Resolve(views, inputBranches, units, editor)` returns a
`SettingsResolution` with immutable per-view `EffectiveSettings` and diagnostics.
The host supplies `ViewSettingsContext` with stable view identity, display name,
source kind, actual cut status, scene depth behind the unchanged view plane,
document tolerance, optional source depth, and optionally the inferred drawing name.
Supply source depth only for Box height or an enabled clipping-plane depth;
disabled clipping-plane depth is null. All numbers use document units. Geometry
view identities should encode their list index; document views use stable IDs.

`DocumentUnits` covers mm, cm, m, km, inches and feet. The distance parser accepts
the UX suffixes, quote notation, decimal/scientific numbers, and bare document-unit
values. It rejects mixed feet/inches, fractions, decimal commas and nonfinite
values. Parsing a distance alone permits negative numbers; settings reject
negative depth/beyond/gap and nonpositive tolerance. Zero depth is valid.

All 20 keys in UX §6.3 are supported. The typed properties on `EffectiveSettings`
cover every key. Depth is a resolved double, Beyond a nullable double, Crop a
resolved bool, and Gap a `DistanceSetting` (Auto is resolved by row layout).
The enums for cuts and hatch reuse M2's `Sections.CutMode`/`HatchMode`.

Each `SettingEntry` exposes:

- `Value`: effective value (null means Beyond is off).
- `AuthoredValue` and `Text`: the winning instruction, including Auto/Off.
- `Source`: input, editor view row, editor All views row, default, or auto.
- `AssignedBy`: who authored the instruction, even when Source is auto.
- `IsSetByInput` and `SourceDescription`: editor/menu locks and Info text.

The host stores immutable `EditorSettings`, with sparse `SettingsRow` values and
per-view rows keyed by identity. Input overrides individual keys only; it never
mutates editor values. Removing the input therefore restores editor inheritance.
Unused editor rows stay in that object for the host's unused-rows UI. For keys
with dynamic defaults (depth, beyond, crop, gap, name, document tolerance), Source
is Auto; AssignedBy still distinguishes the editor/default/input instruction.
`EffectiveSettings.Describe()` provides text suitable for the effective-settings
portion of Info; resolved distances there are in document units.

The host sorts GH branches by path before passing them to Core. Zero branches
means no input. One branch addresses all views; N branches address N views by
index. Other counts yield an error while editor/default values remain available.
Join semantics support both one item per Panel line and multiline items.
Headers override branch matching until another header: `[View name]`, `[#0]`
(**zero-based**), and `[*]`. Named header matching is case-insensitive; ambiguous
duplicate names require an index. Within input, the last valid addressed
assignment wins, including assignments after `[*]`.

Bad syntax, unknown keys/values/headers produce located warnings without dropping
valid assignments. Key/value/header/unit spelling suggestions use edit distance.
An unknown or malformed header disables that block until the next valid header,
so values do not leak into a previously addressed view. A contradictory fade
start/end rejects the start through its inheritance chain until start <= end;
it preserves end and unrelated keys. Equal start/end is an allowed step fade.
Beyond >= depth is ignored with a remark only when far clipping is enabled (S2);
with no far clip, deeper lines remain eligible for Beyond. Rectangle and Box
always crop; `crop=off` on those sources produces a remark.

`Settings.CopyEditorAsText(editor, views)` copies sparse editor rows with headers,
preserving inheritance. Unused rows stay stored but are not copied because they
have no current view binding. `Settings.CopyAsText(effectiveViews)` copies all
winning authored instructions with index headers. Reparse in the same document
units to reproduce values; provenance naturally becomes Input. Names and header
names support JSON quoting so comments, brackets, backslashes and quotes round-trip.
`#` comments otherwise follow the UX rules. Drawing names cannot contain `::`
or control characters because they become one layer-tree segment.

## View range and styling

`new ViewRange(effectiveSettings)` or its numeric constructor implements the
range policy. Positive depth is behind the view plane (`d = -localZ`). Exact
zero with far clipping gives `SectionOnly`; `0 < depth <= tolerance` gives
`UncertainThinVolume`. No clip retains background volume and uses depth only as
a fade reference. Negative/nonfinite depths are rejected. The engine must use
independent section topology in section-only/thin modes, retain out-of-scope
occluders, and suppress uncertain background/far line classification. Core never
nudges or silently widens a clipping interval.

`Partition(DepthSpan, participatesInClipping)` returns immutable, disjoint
parameter intervals and interior split points at near/Beyond/far thresholds.
`DepthSpan` explicitly certifies **affine** depth between its parameter endpoints.
Ascending, descending and constant-depth spans are supported. A general Rhino
curve's min/max depth alone cannot locate parameter roots: the adapter must find
all threshold intersections geometrically and classify resulting intervals using
their certified depth ranges. Do not interpolate a nonlinear curve's extrema.
`Classify(depth, participatesInClipping)` is also available for that adapter.
Beyond is strictly deeper than the threshold; threshold points belong to Visible.
Beyond splitting follows visibility, never filters occluders, and is applied to
visible pieces (profile status can remain separate metadata). Hidden pieces retain
their Hidden class. Scope exclusion disables near/far clipping, not fade/style.

`GenerateFarClipLines` requests independent unchanged-far-plane intersections
only for `line` in a separated volume. `none` never far-trims; `noline` trims but
suppresses only generated far-boundary candidates. The adapter owns physical
near/far trimming, provenance, hidden far intervals, Cut/FarClip coincident-stroke
precedence, and original features lying on either plane. Far clip never hatches.

`Intensity(DepthInterval, class)` uses `(minimum + maximum)/2` depth, safely
computed, and a linear ramp from start% to end%, clamped to limit%. It does not
split geometry for fade. Cut, Hatch and Unresolved never fade. A zero fade
reference returns full intensity. `QuantizeForBake` rounds intensity to 5% steps;
preview blends toward the viewport background, bake toward white. The adapter
applies per-piece object and print colours without proliferating layers.

`Styling.LineWeightPreset(scale, weights)` covers all eight classes. Standard
cut/profile/visible/hidden/beyond widths are .50/.35/.18/.13/.09 mm; the other
bands and preview widths match §3.6. Integer band edges are 70/71 and 141/142;
fractional denominators use the actual geometric means sqrt(5000), sqrt(20000).
`BandName` is ready for Info. `UseSourceLayers` means source width **and colour**
win over scale. `PrintWidth` applies a section boundary multiplier only to preset
Cut. Preview widths stay fixed; Hatch has 0 pixels to mean fill/pattern rendering.
Unresolved uses orange #E07A00 in the adapter. Profile bakes to Visible but keeps
its per-object profile print width. Nothing here changes geometry, hatch pattern
scale/rotation, frames or placement.

## Placement

`Placement.Vector3`, `Plane3`, `Frame2`, `RigidPlacement` use no Rhino types.
Planes validate and normalise orthogonal axes, retaining a right-handed frame.
`RigidPlacement` maps points/vectors, exposes an inverse, and preserves distances.

`AnchorRules.Resolve` retains rectangle/plane origin and axes, moves a box's
original plane to its +Z face, flips clipping-plane X together with Z when the
host's resolved kept side requires it, uses a named view's camera target, and
projects world origin for vectors/directions. `FromDirection` derives right/up
using projected world Z, falling back to world Y for vertical looks. The host
resolves named-view versus clipping-plane versus direction text precedence,
rejects perspective per view, and supplies owned plain frames.

`PlacementPlan.Create(views, targets, gaps)` returns an immutable plan or
`Give 1 target, or one per view`. No target uses the drawing's original plane;
N targets map View[i] rigidly to Target[i]; one target makes a row along target
X. View 0's anchor stays at the target origin. Each subsequent offset uses the
previous frame's max X and the next frame's min X, retaining all Y origins.
Auto gap = mean input-frame width / 10; `gaps[i]` is the gap **before** view i,
so the first view's gap is unused. Pass each view's effective Gap instruction.

Frames are supplied in anchor coordinates from crop or projected **input**
bounds. `Frame2.FromInputBounds` projects already volume-restricted bounding-box
corners; empty scenes require an explicit host fallback frame. Rectangle/Box
crops always win; a named view crops unless disabled. Frames never re-fit to
outputs. For a named view, pass `PlacementView.ProjectionPlane` as the scene-front
plane: the camera target supplies the lateral reference, projected onto that
plane. This keeps no-target drawings at the scene front and targeted 2D drawings
on the target, without carrying a camera-target depth offset onto a sheet.

## Bake plan

`Bake.BakeNames.LayerPath` produces
`M2D+::<drawing>::<Category>::<source layer path>`; no source layer means the
category itself. Profile shares Visible. Frame is not baked.
`Infer(DrawingNameRequest, units)` uses an explicit name, then a named-view or
clipping-plane name, otherwise Plan/Section/Elevation/Axo geometry. Supply the
view's outward Z normal and the plan height in document units; direction views
can supply a semantically meaningful plan plane rather than a padded scene-front
height. Section numbers are assigned by the host. Metre plans use `Plan +1.20`;
other units use an explicit suffix (mm levels have no decimals, others two).
`Unique` allocates case-insensitive ` (2)` suffixes against supplied reserved
names. For ordinary re-bake, retain the current drawing's allocated name; for
Shift-copy reserve existing names and allocate a new owner/name.

`BakeTags.Create` returns `m2d.bake = <component UUID>/<drawing>`, `m2d.source`,
`m2d.class`, `m2d.hash`, plus **`m2d.layer`**. This last provenance field is needed
to detect layer moves after save/reopen; use a stable layer identity string
instead of a transient table index (a full path is also supported, but renaming
then counts as editing). `m2d.class` uses the seven lower-case UX names;
Profile uses visible. Hashes must cover final placed geometry, computed by the
adapter with a stable content fingerprint, never CRC alone.

`BakePlan.Replace(component, drawing, previous, records)` compares each owned
previous object's stored original hash/layer with its freshly observed hash/layer.
Unchanged objects are deleted even if their new drawing has no pieces. Edited,
moved or incompletely tagged objects are kept. Other owners are untouched and
absent from this plan. Every new record is added, including the replacement for
an edited old piece. IDs/new keys must be unique. The immutable plan returns
Delete/Keep/Add and the UX summary text with invariant number formatting.

`BakeLayerWidths.Decide` implements Q18. New category layers receive the preset
and `m2d.weights = 1:n`; existing widths update only on an exact match with the
recorded preset. Missing/invalid tags and edited widths preserve the layer and
produce an explanatory message. Test each managed layer independently; do not
overwrite colours, linetypes, visibility or locks. Source-layer mode delegates
width/colour to the adapter instead of applying a preset. Style boundary width
multipliers and profile widths remain per-object adjustments.

The Rhino adapter executes the entire plan in one undo record, resolves locked
layers temporarily and restores locks, preserves user-hidden layers, removes
only empty layers it owns, creates the Hidden linetype if needed, and writes all
tags after adding geometry. It must identify baked geometry during scene capture
using m2d.* tags/M2D+ layers. Copy-bake must allocate names before creating tags.

## Integration and validation

The GH editor/menu edits the All views or stable per-view row and serialises the
editor into the component. It handles undo, Apply/Cancel, input locks, unused rows,
and Info diagnostic presentation. Adapters convert planes and distances explicitly,
with no live Rhino/GH/document references passed into Core.

Cache invalidation remains layered: scale/weights/fade/hatch colour policy are
style changes; target/gap only re-place cached drawings. Depth/farclip/beyond/crop,
hidden/tangents/seams/glass/cuts/tolerance affect drawing. Name/tree changes affect
presentation/bake ownership/output construction, not visibility. All views publish
as a complete current generation, and outputs remain independent owned copies.

Core tests exercise unit failures/conversions, every key, provenance/inheritance,
headers, round trips, exact interval splits, S2 regimes, fade curves, preset band
edges, anchors/row layout and bake replacement/width preservation. Native loading,
clipping and hatch save/reopen remain Rhino-side gates; these pure tests do not
establish those properties.
