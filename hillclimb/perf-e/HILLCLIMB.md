# PERF hill climb — theory E: certified image-space visibility prepass

Branch `hc/perf-e-raster`. Bench: [`tools/hc-perf`](tools/hc-perf/README.md). Scores: [`hillclimb/scores.jsonl`](hillclimb/scores.jsonl).
Pure logic: `src/M2DPlus.Core/Visibility/Raster/` (no Rhino types). Rhino adapter: `src/M2DPlus.Rhino/Visibility/RasterPrepass.cs`,
wired into `NativeDrawing.SolveView` behind the opt-in `NativeSettings.Raster` (null keeps the plain native route unchanged).

## The idea

Rasterise the scene on the CPU (multithreaded, deterministic; the AMD iGPU was not used: the raster is never the bottleneck,
and CPU arithmetic lets every bound be rounded in the safe direction) into a **certified-opaque far-depth buffer**:
for each pixel, an upper bound on the depth by which every ray through every point of the pixel square has met an opaque
surface. Then:

1. **Cull** objects whose conservative footprint is wholly covered by strictly nearer certified occluders. They leave the
   native solve. With hidden lines on, their hidden output is emitted directly (all-planar Breps: non-tangent edges;
   curved Breps: non-tangent edges plus parallel-view tangent silhouettes; curves: the curve).
2. **Certify isolated objects** whose self-visibility is exact without a solve (curves, points, convex planar solids,
   single planar faces) and whose conservative box meets no other remaining surface: emitted directly.
3. Everything else (the ambiguous remainder) goes to **one** native HLD call. Native has a fixed cost of ~0.3–0.6 s per
   `Compute` call regardless of size, frustum, tolerance or threading (measured with `probe`), so the prepass never makes
   a second native call, and a view whose remainder is empty makes none.

## Certificates (each conservative by construction)

**Occluder coverage (planar faces).** A patch is a set of planar faces of one opaque object. Its boundary set holds every
loop segment except those on an edge shared by exactly two faces of the patch that lie on opposite sides of it in
projection (decided from the trims' left-hand material side, so face orientation flags are never trusted). The union U of
the projected faces then has its boundary inside the boundary set. A pixel not touched by any boundary segment dilated
by the positional error (2 × tolerance, arc sagitta or certified Bézier chord deviation, numerical slack) has a square that
misses ∂U, so the square is wholly inside or outside U; its centre is inside a face, so it is inside U. Every point of the
square is then covered by a face that touches the pixel, whose depth there is at most that face's plane maximum over the
square (plus tolerance/|n·d|) and at most its vertex maximum. The stored far depth is the maximum of those bounds, rounded up.

**Occluder coverage (curved, untrimmed faces).** The NURBS surface is decomposed into rational Bézier patches (knot
insertion) and split at parameter halves (de Casteljau, homogeneous, exact) until every cell side's projected control row
lies within ε of its chord. With positive weights each cell is in the convex hull of its net (depth range, projected box)
and each side is within ε of its chord. For a union D of cells (grouped by projected orientation), the projection f is
continuous and f(∂D) is within ε of the chord polygon P, so for x farther than ε from P, deg(f, D, x) = winding(P, x);
if nonzero, the surface covers x. No assumption on folds or contours is needed. Depth: maximum hull depth over the cells
whose projected hull box touches the pixel. This replaces "trust the mesher's tolerance" with a proof.

**Culling.** An object is culled only if, at every pixel its conservative footprint touches (per-face boxes or polygon
outer coverage, dilated), the far bound is strictly below the object's lower depth bound there minus a margin of
10 × tolerance. Ties and contacts are therefore never culled. Footprints use exact vertices, arc sagitta, or control-point
hulls (curved faces, NURBS edges).

**Composition.** The hidden part of a curve is the union over occluders. If object A is culled, every point of A lies
behind an occluder O that is strictly nearer at that screen point; any point of another object B hidden by A is behind A
along the same parallel ray, so it is hidden by O too. Removing A therefore changes no other object's visibility (the chain
of strictly nearer occluders is finite and ends in a remaining object), and all of A's candidates are hidden. Crop: objects
whose footprint lies wholly outside the grid (frame plus two pixels, far more than the crop tolerance) produce nothing.

**Isolation.** A remaining object whose conservative box meets no other remaining surface's box neither occludes nor is
occluded by any remaining object; culled objects cannot matter (anything in front of a culled object is behind a remaining
occluder that would overlap). A convex solid shows an edge iff an adjacent face is front-facing or edge-on (an edge-on face
of a convex solid projects onto its silhouette); edges between two edge-on faces project to points.

**Tie and contact rules.** Coverage is strict (margin 10 × tol, rounding toward safety). Coincident or touching geometry
always falls to the exact solve. Native's own quirks are documented exceptions in the gate (see below).

## Iterations

| # | Hypothesis | Change | Gate | Primary (geomean s) | Secondary | Kept? |
|---|---|---|---|---|---|---|
| 0 | Baseline | Plain native HLD via `NativeDrawing.SolveView` (serial), hidden lines on (default) | n/a (reference) | **0.4326** | groups: fixture 0.095, feature 0.220, curved 2.42, perf-S 3.31, M 24.4, L 107.7, XL 494.3, curved-S 34.5, curved-M 344.2; peak 3.7 GB | yes |
| 1 | Most objects in axo/plan views are wholly behind nearer opaque faces | Certified raster culling (planar patches, per-face box footprints); culled planar Breps emit non-tangent edges as hidden; single native call | superseded (see final gate) | 0.4309 | cull 37 %; perf-M 13.5 s (24.4), L 65 s (108), XL 297 s (494); fixtures slightly slower (+4 ms median prepass) | yes |
| 2 | Native costs ~0.3–0.6 s per call even for one box, so certify whole small views | Isolation certificate (curves, points, convex planar solids, single planar faces) | superseded; two real bugs caught and fixed first (tilted-arc projection; a solid occluding curves) | 0.2802 | e.g. `brep_box` 0.03 s vs 0.67 s native | yes |
| 3 | Curved-outline slabs are lost occluders | Free-form trims via certified rational-Bézier chord bounds | superseded | 0.2794 | within noise | yes |
| 4 | Elevation culling (0.6 %) is blocked by one-pixel seams between abutting objects | Cross-object seam merging | probe only | — | identical cull counts on S/M, all views; raster +0.03–0.27 s; coverage image shows the gaps are real see-through gaps | no |
| 5 | Curved faces as occluders | Bézier cells + winding (degree) certificate | — | 0.2770 vs 0.2762 | cull 38.6 %; curved-S culls 4–32/500 (ceiling 35–208) | no (noise) |
| 6 | Culled curved objects need not go to native | Direct hidden emission: non-tangent edges + tangent silhouettes (stacked on 5: two changes vs best) | — | 0.2728 vs 0.2762 | curved-M 263 s (284), XL 275 s | yes |
| 7 | Small scenes pay for a 2048² buffer | Adaptive resolution clamp(64·√N, 256, 2048) | **fails strictly** (below) | **0.1778** | fixture geomean 0.025 s (0.052); native-time ratio 0.455; cull 38.6 %; certified length 58 %; peak 0.67 GB | yes |

Timing protocol: each pass runs all configurations back to back per view in one process. Native was re-timed back to back
only for views under 10 s (the rest reuse iteration 0); in pass 2 the previous best (r3) was re-timed only for views under
60 s native (heavier views from pass 1). The visible-over-hidden output pass (added after review) was timed only on gate views.
Excluded from the set from iteration 0 on: curved-perf L/XL (no native reference obtainable; L never finished in the curved
suite's own baseline).

## Gate rules (final) and every exception

Strict: (1) no culled object has any visible locus in plain native (holds: 0 over 199 gate views); (2) on sources the prepass
emits itself (culled or isolated) every locus disagreement longer than 2τ fails; (3) metamorphic (permutation, two 180°
rotations) on fixtures, feature models and XS, same attribution. Normalisation: per drawing, hidden loci coinciding with a
visible or unresolved locus are dropped (native reports coincident pieces once); our output now applies the same
visible-over-hidden convention as the A/A2 kernel.

Reported but not failing, with evidence:
- **Native order/composition instability on sources native still solves.** Plain native re-solving the *same full scene* in a
  different input order produced 13 class-swap spans on M North tile 1 and 61 significant spans (+359 m Unresolved flips,
  including a missing 0.48 m visible piece) on S SW axo; iteration 0 already showed native failing its own rotation check on
  `elliptical_columns_100`. Removing a certified-hidden object cannot change another object's true visibility (composition
  argument above), so disagreements on native-solved sources are native's answer for a different scene: 159.9 m over the
  gate set (0.03 % of 471 km reference length).
- **Unresolved flips** (native Unset quarantine depends on scene composition): 6,940 m; our total Unresolved length equals
  native's within 0.4 %.

Failing (strict), 11 of 199 gate views:
1. **perf-S-blocks SW axo, 0.039 m extra hidden on culled column 8afc757f** — exception *candidate* with evidence:
   exact curve–Brep ray tests from 201 samples on each nearby edge all hit an occluder 4.3–10.4 m in front; native's raw
   segments mark this edge and three siblings `Duplicate`, yet no locus along it appears in native's output, so native
   de-duplicated it against a partner it never emitted (hidden locus lost: a native defect).
2. **curved-perf-S blocks/exploded North elevation, 0.056 m extra hidden on a culled object** — same pattern; not individually proven.
3. **perf-M blocks/exploded SW axo crop tiles 1–4 (8 views), 4.5–30.5 m per tile — OPEN.** Native's remainder solve omits
   visible edges of *non-culled* objects that its full-scene solve shows (dumped: b2d4a620's edge (0,−12.25)–(0,−9.80) is
   Visible in the full solve and absent in the remainder solve), leaving our coincident certified-hidden edges as class swaps.
   Visible-over-hidden cannot fix it because our drawing has no visible piece there. This is a real output difference (missing
   visible lines, ~1–3 % of tile visible length), most likely native's coincident-curve (`Duplicate`) resolution choosing a
   hidden partner. Not excused.

## Next ideas

1. Fix (3) without trusting native's Duplicate resolution: keep native `Duplicate` segments of remaining sources and re-decide
   coincident groups ourselves (visible wins), or keep coincident culled objects in the native call (cull only objects with no
   edge coincident with a remaining object's edge in projection).
2. Edge-aware culling for elevations: cull an object when at every uncovered footprint pixel it has no candidate curve and no
   other candidate lies behind it (faces visible through real gaps are irrelevant to the drawing). Ceiling: 72 % on M North vs 0.6 % now.
3. Feed the culled set to the exact planar kernel (A/A2): the certificate is engine-agnostic and also shrinks the freeform remainder.
4. De-duplicate coincident hidden output (we emit 234 k pieces where native emits 132 k on L SW axo; loci are equal).
