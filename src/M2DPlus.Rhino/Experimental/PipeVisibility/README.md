# Pipes (experimental) visibility engine

Ported from draft PR #65, commit `2e0f9b0`, `tools/worker-bench/PipePoc`.
The surface classifier is unchanged. Integration adds namespace/value records,
cancellation checkpoints and exception cleanup. Silhouettes are always drawn.

`PipeProjection` adapts owned prepared geometry to ordinary drawing pieces. It
supplies source identity, Visible/Hidden, projected geometry and original depth
geometry. It does not claim HLD profile, duplicate, section or certification data.
The normal presentation path handles placement, min-visible, tree/Key and Bake.
The ordinary global compute scheduler and headless worker routing remain in use.
Scene transport carries captured unit eligibility and angle tolerance; settings
and exact assembly fingerprints identify the experiment on both worker runtimes.

The `visibility` setting defaults to `hld`. Right-click **M2D+ Drawing → Visibility
engine → Pipes (experimental)**, or wire `visibility = pipes` to Settings. `hidden`
works normally. Smooth manifold edges and seams are always skipped, so `tangents`
and `seams` have no effect; Info and the component remark disclose this. If the
engine throws on an admitted view, that view is drawn with HLD and says so.

Requires millimetres, parallel views, valid closed Breps with
optional valid curves/points. Direct closed Extrusion/Surface conversions can be
used; blocks, meshes, SubD, open/invalid bodies and unsupported scene objects
fall back to HLD with an explicit reason. Named-view crop and non-box section/far
clip also fall back. Transparent bodies with `glass=clear` fall back. Cut-only
keeps its normal sections-only behavior. Perspective retains the ordinary view
error. No accuracy fixes have been made yet: approximately 23 mm of wrong-visible
disagreement at the owner's central Top junction remains known. General valid
closed Breps are admitted but have not been accuracy-certified.

Box views are admitted when participating solids are strictly separated from the
front and active back depth planes. Plane-frame geometry enclosures, with modelling
and arithmetic margins, only shortcut clear separation/exclusion. Ambiguous solids
undergo Brep/plane intersection; any loci (including tolerance contacts) fall back
with per-plane object counts. No loci or invalid bounds do not prove separation:
these fall back with a "separation cannot be certified" remark. This deliberately
also refuses disconnected shells straddling a plane without intersecting it.
Entirely excluded solids are removed before visibility. Side-plane crossings are
allowed: the shared NativeDrawing.Crop/Inside helpers crop the projected drawing,
with matching world-depth trims. Curves/points use the existing Route C halfspace
clipping. Beyond/fade, Source/Target, min-visible, tree/Key, preview and Bake use
ordinary presentation. There are no physical cut regions or crop-created cut edges
on this no-cut route; Far clip is empty. `farclip=none` disables the back plane as
in HLD. Zero/sub-tolerance clipping depths fall back. Compute/cache/timing/worker
identity retains visibility=pipes, the view/depth settings and assembly fingerprints;
worker views run the same admission after transport. Admission time is included in
the per-view time and recorded separately as "pipe admission".

The standalone `pipe-poc` worker-bench command calls these moved classes.
Opt-in and experimental: HLD stays the default and its output and cache identity
are unchanged when `visibility = hld`. The engine never emits Unresolved, so its
wrong-visible errors are not flagged in the drawing.
