# Raster (experimental) visibility engine

Owner-approved 2026-11-28. Ported from the stage 0 `raster-poc` prototype, with the
stage 1 candidate-ray correction. Right-click **Visibility engine > Raster
(experimental)** or wire `visibility = raster`. HLD stays the default. The HLD
adapter, policy string and default settings/run/cache key text are unchanged.

`RasterProjectionRoute` receives the complete ordered prepared registration batch
at the same boundary as HLD: source culling, block transforms, normalized
SubD/extrusion/surfaces, clear-glass substitution, Route C physical near/far
trim/caps and curve/point clipping have already run. The engine never invokes HLD.
An unsupported batch, failed tessellation or other projection failure discards the
entire experimental projection and sends the whole batch to HLD with a grey
remark. Cancellation propagates and never starts fallback. Sections/regions/hatches
remain independent. Cut-only and zero-depth retain their no-projection path.

Supported prepared geometry is Brep (open or closed), mesh, curve and point in
parallel views. Unknown types, non-finite bounds and failed tessellations refuse.
Rhino-invalid prepared sources may still tessellate, as in the original prototype;
they remain in the complete batch and their count is disclosed in a grey remark.
Neither Rhino validity nor successful meshing certifies visibility. Existing unsupported
scene diagnostics still apply; normalization does not certify a surface. Direct
mesh candidates come from naked/sharp and view-sign-change topology edges. Brep
candidates use exact edges and native tangent silhouettes (not HLD). Tangents and
seams honor their requested flags. Profile marks a tangent candidate, not HLD's
scene-profile ownership. Hidden-off omits hidden strokes; min-visible follows the
ordinary hidden-off rule. Duplicate/coincident strokes can remain.

Core's `RasterBuffer` builds double-depth/front-ID pixels plus deterministic 32x32
triangle bins. The image is only the broad phase. Each candidate sample intersects
its own parallel view ray with actual triangles in that tile, with barycentric
inside-triangle checks. Subpixel triangles need not cover a pixel centre. There is
no extrapolated pixel-centre plane, slope bias or face-ID visibility shortcut:
wrapping/folded faces can hide their own rear candidates. Triangles are two-sided.

Precision policy `RasterPolicy.Identity` is versioned and keyed only for Raster.
The adaptive long dimension is `scene diagonal / (64 * document tolerance)`,
clamped to 64..8192 pixels; the actual grid is clamped by aspect ratio, a 512 MiB
image ceiling and the projection memory budget. Brep tessellation explicitly requests double-precision mesh vertices; float rounding
at survey coordinates can otherwise fragment grazing silhouettes. Mesh distance tolerance is
`max(document tolerance, nominal pixel / 32)`; depth allowance is
`2 * document tolerance + mesh tolerance`. Samples are at most 0.4 actual pixel
apart along exact projected candidates; transitions receive eight bisections.
Native meshes are approximate, and the allowance is not a certified error bound.
Short visibility events can be missed between samples. Exact candidate trims,
source/path/depth/Profile/FarCandidate go through ordinary crop, Cut precedence,
min-visible, tree/Key, placement, preview and Bake.

Memory admission estimates native/managed meshes, triangle list/array storage,
bins, candidate/output headroom and the image. Windows dispatch and worker caps
reserve an additional raster estimate; unavailable heavy workers never trigger a
heavy in-process solve. The runtime checks actual tessellation counts and bin
allocations against the remaining scheduler budget, clamping the image or refusing
the entire view. Estimates are not hard native allocation bounds. Worker Job
Objects keep their existing hard caps. macOS has no workers and rechecks live
`InProcessPolicy` headroom; cancellation remains cooperative. Checks occur between
native meshing/silhouette calls and during managed triangle/sample/output loops.
A single native tessellation call may finish after cancellation.

Settings, actual pixel size (millimetres captured on the host), triangle/candidate
counts, memory estimate, phases, diagnostics and quality survive worker transport
and presentation. Schema 4 is retained: its generic settings converter carries the
new enum, and the mandatory exact Core/Rhino assembly fingerprint rejects every
older peer/cache before scene transfer. `WorkerCodecStateTests` covers all new
stored result fields; the scene carries captured unit scale as well as angle/units.
Raster visibility-cache and retained-worker-scene keys include that scale, even
when changing units leaves geometry and tolerance unchanged. HLD projection/run keys
are unchanged. SceneCache's existing scene-unit discriminator adds `/units=` for
non-mm HLD scenes; HLD outputs are unchanged.

Info and a grey remark disclose **Engine: Raster (experimental); approximate,
pixel N mm; visibility not certified**, plus fallback reasons. Small gaps, shallow
contacts, grazing faces, duplicate ownership, short intervals and junctions may
differ from HLD. The engine does not emit a certificate or enumerate every wrong
line in Unresolved. Stage 1 reduces wrong-visible length but increases wrong-hidden
length in some controls; see the [measured report](../../../../docs/research/raster-engine.md).
No installed-Yak interactive or on-Mac validation is claimed.

Native meshing, validity checks, topology and silhouettes run serially on the
in-process route. Owned headless workers may parallelize them. Managed raster
and candidate classification retain their configured parallelism. Critical
failures (including aggregated OOM) and cancellation propagate without HLD fallback.
The scheduler holds the scene estimate plus Raster's excess over its already
reserved HLD working set;
the image clamps to that reservation. Worker sizing reuses existing HLD working-set
and rounded-cap headroom before requesting a larger cap.

Reservation calibration uses two triangles per planar face and a continuous,
monotone envelope for curved triangles: the minimum of `32,768 * curved faces`,
`1,048,576 + 12,288 * curved faces`, and `8,388,608 + 1,024 * curved faces`.
This covers the sparse broad-face controls and densely trimmed owner scenes in
stage 1 without a sudden reservation drop at a face-count threshold. The full
projection estimate includes the 512 MiB image ceiling plus 128 MiB headroom and
per-source/candidate allowance. Runtime triangle/bin/output guards remain essential;
the envelope cannot bound every surface. Under a smaller reservation the image
clamps, or the whole view refuses before/after tessellation and uses HLD. A larger
Raster worker cap is retained for subsequent HLD/Raster jobs only if the aggregate
reservation at that cap still fits the current machine budget.
