# Curved engine — can M2D+ classify curved geometry itself?

Research date: 2026-11-15/16. Base: `main` at `28d232d`. Nothing committed, pushed, installed or published. No product
code changed; everything is in the harness (`tools/perf-f/CurvedCore.cs`, `CurvedEngine.cs`, `CurvedCensus.cs`,
`ce_report.py`, `ce_picture.py`, and a `ce:` / `ce-census` switch in `Program.cs`). `CurvedCore.cs` has no Rhino types.

**Update 2026-11-16:** stage 1 is now built as product code and verified; see [section 7](#7-stage-1-in-production-2026-11-16).
Sections 1–6 are the research as it stood before that.

**Answer.** Yes for analytic shapes, now. Not yet for freeform.

1. **Exact analytic solids are a go.** Spheres, cylinders, hollow cylinders (CHS) and extruded arcs are classified
   exactly, with closed-form change points and no tessellation. Prototype: space-frame Front **1.1 s against native's
   11.2 s (10×)**, space-frame SW **1.1 s against 63.6 s (60×)**, round tower **0.11 s against 0.95 s (9×)**. Nothing is
   Unresolved; native leaves 185 m of the round tower Unresolved. Every sample the independent ray check could settle
   agreed with the prototype. These shapes are **95–100 % of the curved sources in the building models** (pipes, round
   columns, curved wall panels) and 100 % of space-frame, so on building models native HLD would drop out almost
   entirely.
2. **Meshes as occluders work for freeform, but are not ready to replace native.** After three unsound versions were
   caught and fixed (section 4), the ray check finds no wrong certain answer, and it sides with the prototype against
   native in every disagreement it could settle. But 3–16 % of the linework stays Unresolved at a 1 mm chord
   tolerance (native: 0–8 %), and as built it is 2–5× slower than native. It is only faster (4–5× on the axonometric
   views of diagrid and curved S, about level on an elevation) if two Rhino calls it leans on today are replaced by
   our own code: a certified ray test against the candidate's own face, and a tessellator that actually holds its
   tolerance.
3. **A general certified NURBS/SubD engine and a GPU route are no-go.** Both are large, and the measurements say they
   attack the wrong cost: the visibility solve itself is 0.01–0.6 s on every axonometric view here.
4. **Native is wrong more often than its own Unresolved share suggests.** On curved S SW native draws about 59 m as
   Visible (3.9 % of its Visible length) that the prototype classifies Hidden; the ray check confirmed the prototype in
   every one of the 432 such samples it could settle (68 more were too short to judge). This is separate evidence for moving off native where we can.

Recommendation: build stage 1 (exact analytic solids in Core, wired into `ComposedVisibility`) now. Decide on the
mesh tier only after a small spike on its two missing pieces (section 6).

## Conditions

| Item | Value |
|---|---|
| Rhino / runtime | 8.36.26296.17001 / .NET 8.0.34, Windows x64, native `multipleThreads=false` |
| Machine | Ryzen AI MAX+ 395, 32 logical cores, **shared with other agents**. Machine busy fraction was 11–47 % during these runs (1–14 foreign busy cores); every row records it in its JSON. |
| Process rule | One owned Rhino.Inside process at a time, strictly sequential batches. No other Rhino process was touched. |
| Samples | n = 1. One cold native solve is discarded first. The curved spike measured 0–15 % repeat noise on this machine; read differences under about 10 % as noise. |
| Native time | The product's view solve (`NativeDrawing.SolveView`) with the ray audit off, warm, on the same prepared scene and in the same process as the prototype. |
| Prototype time | Everything the prototype does from the same inputs to classified pieces: recognition, candidate curves, chords, meshing, index, solve. The managed solve uses 8 threads; all Rhino calls are serial. Comparison and adjudication are excluded. |
| Views | Uncut parallel views of all-curved models (no planar occluders, so no planar kernel is involved). |
| Model tolerance | 0.1 mm (models are in metres). ε below is the chord tolerance of the mesh tier. |

## 1. What native receives today

`ce-census` classifies every source the product sends to native HLD (not kernel-eligible), by its faces
(`TryGetPlane/Cylinder/Cone/Sphere/Torus` on the untrimmed surface) and by whether the prototype's strict recognition
takes the whole body as an exact solid. "Exact now" = sphere, cylinder between two planes, hollow cylinder, extruded
arc (one cylindrical face bounded by rulings and arcs). Shares are of curved sources; edge-length shares in brackets.

| Model | Leaves | Sent to native | Exact now (sphere, cylinder, tube, extruded arc) | Other analytic (torus, cone, several trimmed faces) | Freeform Brep | Mesh, SubD | Free curves |
|---|---:|---:|---|---|---|---|---|
| curved-facade | 33 | 33 | — | 1 (3.0%; 11% of edge length) | 27 (81.8%; 89% of edge length) | 5 (15.2%) | — |
| curved-perf-M-blocks | 5,000 | 5,000 | 1,248 (25.0%; 10% of edge length) — Ball 831, Tube 417 | 417 (8.3%; 6% of edge length) | 3,335 (66.7%; 83% of edge length) | — | — |
| curved-perf-S-blocks | 500 | 500 | 124 (24.8%; 10% of edge length) — Ball 82, Tube 42 | 42 (8.4%; 6% of edge length) | 334 (66.8%; 83% of edge length) | — | — |
| curved-steel | 8 | 8 | — | 3 (37.5%; 63% of edge length) | 5 (62.5%; 37% of edge length) | — | — |
| cut-topology | 17 | 1 | 1 (100.0%; 100% of edge length) — Tube 1 | — | — | — | — |
| diagrid-shell | 25 | 25 | 16 (64.0%; 4% of edge length) — Ball 16 | — | 9 (36.0%; 96% of edge length) | — | — |
| edges | 6 | 4 | 1 (25.0%; 12% of edge length) — Tube 1 | 1 (25.0%; 45% of edge length) | 2 (50.0%; 43% of edge length) | — | — |
| frame2d-large-campus | 2,983 | 965 | 480 (49.7%; 62% of edge length) — Tube 480 | — | 18 (1.9%; 16% of edge length) | 107 (11.1%) | 360 (37.3%; 22% of edge length) |
| glass | 16 | 1 | 1 (100.0%; 100% of edge length) — Tube 1 | — | — | — | — |
| perf-L-blocks | 20,000 | 826 | 825 (99.9%; 100% of edge length) — CylinderPatch 412, Tube 413 | — | 1 (0.1%; 0% of edge length) | — | — |
| perf-M-blocks | 5,000 | 210 | 209 (99.5%; 98% of edge length) — CylinderPatch 104, Tube 105 | — | 1 (0.5%; 2% of edge length) | — | — |
| perf-S-blocks | 500 | 22 | 21 (95.5%; 86% of edge length) — CylinderPatch 10, Tube 11 | — | 1 (4.5%; 14% of edge length) | — | — |
| space-frame | 669 | 669 | 669 (100.0%; 100% of edge length) — Ball 182, Tube 487 | — | — | — | — |
| subd-vs-nurbs | 3 | 3 | — | — | 2 (66.7%; 100% of edge length) | 1 (33.3%) | — |
| tangent-continuity | 8 | 7 | 2 (28.6%; 6% of edge length) — Ball 2 | 3 (42.9%; 71% of edge length) | 2 (28.6%; 23% of edge length) | — | — |
| view-round-tower | 29 | 29 | 29 (100.0%; 100% of edge length) — CylinderPatch 12, Tube 17 | — | — | — | — |

Reading it:

- **Building models are almost entirely analytic.** In perf-S/M/L every curved source but one is a pipe or an extruded
  arc. The campus model (the only non-generated one) is half round columns; another 37 % are free curves, which occlude
  nothing and only need chords.
- **The curved suite is where freeform lives**, by design: two thirds of curved-S/M sources, 96 % of diagrid's edge
  length, all of curved-facade.
- **Next shapes by count:** torus elbows (8 % of curved-S/M, 25 % of curved-steel) and bodies made of several trimmed
  analytic faces (the filleted I-beam: one source, half of curved-steel's edge length).
- Test data caveat (REQUIREMENTS): these are generated models plus one campus model, so the split is optimistic about
  how tidy real projects are, but pipes, round columns and curved walls are what architectural curved geometry mostly is.

## 2. Architectures compared

"Coverage" is the share of curved sources from section 1. "Measured" is section 3. Sizes are new code including tests.

| | (a) Exact analytic solids | (b) Meshes as occluders, exact curves as candidates | (c) Certified subdivision / interval methods for general NURBS and SubD | (d) GPU or raster with exact refinement |
|---|---|---|---|---|
| What it is | Recognise a whole body as sphere / cylinder / tube / extruded arc. Change points of a chord are roots of quadratics; one exact predicate per interval. | Welded mesh with a chord bound ε per source. A piece is Hidden only when a whole front depth component is crossed an odd number of times, Visible only when no triangle within ε can be in front, else Unresolved. | Bezier clipping or interval Newton on the true surfaces, with trims, for every ray. | Depth / item buffer to pre-classify; exact code near discontinuities. |
| Coverage | Buildings 95–100 %; campus 50 % (+37 % curves); space-frame and round tower 100 %; curved-S/M 25 %; steel, facade 0–3 %. With torus, cone and trimmed analytic faces: +8 % curved-S/M, +38 % steel. | Every Brep and extrusion; SubD through `ToBrep`. Not welded meshes (native's smooth-silhouette rule) until a candidate rule exists. | Everything, in principle. | Everything. |
| Speed against native | **Measured 9–60×** (tower, space-frame Front and SW). Managed solve 0.03–0.4 s; recognition and chords are the rest. | Measured 0.2–0.5× as built (Rhino line/face tests dominate). Without them: **4–5× on diagrid and curved S SW**, 1.1–1.6× on the curved S North elevation, 0.4–0.8× on curved-steel (Rhino's mesher makes 0.25–0.74 M triangles for 8 objects). | Not measured. A certified ray/patch test is ~10–100× a triangle test; as the whole engine it would be slower than (b), not faster. | Not measured. The managed solve is ≤ 0.6 s on axonometric views; raster only speeds up the part that is not slow. |
| Correctness story | Exact up to floating-point margins (production: filter + exact fallback as in the planar kernel). Tie rule and self-occlusion fall out of the predicate. 0 flagged of 1,541 settled checks on the three all-exact views. | Sound if (1) the chord bound really holds and (2) the candidate's own face is handled by a separate certified test. The prototype measures (1) on a sample and uses Rhino for (2). 0 flagged of 8,130 settled checks on the freeform views after the fixes in section 4. Everything else Unresolved: 3–16 % at ε = 1 mm. | Sound by construction, if finished. Tangencies (every silhouette) are the hard case for interval methods. | None by itself: a pixel is not a proof. Results would also differ between GPUs and drivers, and the worker must be byte-identical to in-process. |
| Size and risk | **Small–medium, low risk.** ~900 lines Core (three solids exist as 250 lines of prototype), ~400 lines recognition and routing, gate extension. | **Large, medium–high risk.** Own tessellator with a proven bound, certified own-face ray test, candidate rule for kinked faces, locus merge. The own-surface rule was wrong three times in this prototype. | **Very large, high risk.** NURBS evaluation, trims, SubD limit surface, certified silhouettes. | Large, plus a GPU dependency in headless workers. |
| Verdict | **Go.** | **Hold; spike its two missing pieces first.** | **No-go as an engine.** Use two small pieces of it inside (b). | **No-go.** |

What "better than these" turned up (scope item d):

- **Use (c) only where (b) cannot decide.** The mesh cannot say whether the view ray leaves the candidate's own surface
  cleanly (it starts on it). That is one ray against one face over a short depth: a bounded, classical problem, unlike a
  whole NURBS engine. The same control-net machinery gives a provable chord bound for our own tessellation.
- **Rhino as curve generator, M2D+ as classifier.** `Silhouette.Compute` is cheap (1.6 s for 500 curved-S sources) and
  HLD is not involved. A fully hand-rolled engine would also have to trace freeform silhouettes itself; that is the
  expensive part of (c) and buys nothing measurable here.
- **Closed-form outlines for exact solids.** Replacing Rhino's silhouettes by a great circle and two rulings took
  space-frame Front from 3.6 s to 1.1 s.

## 3. Prototype

### What was built

Inputs are exactly what the product sent to native for the view (observed through `ObserveNativeInputs`).

1. **Candidates.** Brep creases and naked edges (tangent edges and seams off, as native), plus tangent silhouettes:
   closed form for exact solids, Rhino's `Silhouette.Compute` otherwise. Each curve is cut into chords within half a
   tolerance of it; a chord keeps the true curve point at its middle, so points are evaluated on a parabola that follows
   the curve to about 10⁻⁶ m.
2. **Exact tier** (`Ball`, `Tube`, `CylinderPatch` in `CurvedCore.cs`). Recognition is strict: every face on the
   expected surfaces, the cap planes not meeting inside the cylinder, and volume (or area) equal to the closed form
   within 10⁻⁴. For a chord, the parameters where "something of the solid lies more than a tolerance in front" can
   change are roots of quadratics: the ray's foot or start on a surface, the ray tangent to a surface, the ray through a
   rim or ruling. Between consecutive roots one predicate decides. A candidate on its own solid is snapped onto the
   surfaces it lies on; a tangent silhouette skips the surface it grazes.
3. **Mesh tier** (`MeshIndex`). Everything else is meshed (Rhino's mesher at ε/2), welded, and its deviation from the
   surface measured on a sample; the bound used per source is the larger of ε and 1.25 × the worst measured.
4. **Union.** Per chord: exact hidden intervals first, the rest to the mesh tier, which bisects down to ε/2 before
   giving up. Classes: Visible, Hidden, Unresolved.

### Why a certain answer from the mesh tier is certain

Let S be one source's surface and M its welded mesh, with a continuous map from S onto M that moves no point more than
ε. Let R be the view ray from a candidate point, starting one tolerance in front of it (the tie rule).

- If no triangle of M comes within ε of R, S does not meet R. This is the **Visible** test, per source.
- Take a stretch of R whose two ends are farther than ε from M and which stays farther than ε from M's boundary edges.
  Moving M onto S along the map cannot push the surface across either end, nor its boundary across the stretch, so S
  and M cross the stretch the same number of times modulo two. An odd count for M means S crosses it. This is the
  **Hidden** test: triangles near the piece get conservative depth ranges; overlapping ranges form depth components;
  a component wholly in front, with no boundary edge nearby and an odd number of crossings, proves a surface in front.
  (Parity must be per component: a point behind a closed solid sees two crossings.)
- The count is taken at the middle of a short piece. It cannot change parity along the piece without crossing a
  boundary edge, which is flagged; crossing a fold changes it by two.
- Point-in-triangle uses one orientation value per mesh edge, computed on canonically ordered endpoints with a symbolic
  perturbation, so two triangles sharing an edge never both claim or both disclaim a point.

**The candidate's own surface is the exception, and the weak point.** The ray starts on it, so neither test applies to
the triangles around the candidate. The prototype exempts triangles of the candidate's own faces only when a separate
test says the face is not crossed again that close:

| Own face | Test | Status |
|---|---|---|
| Plane | Never crossed again. | Exact. |
| Cylinder, sphere | Other root of the ray with the untrimmed surface, closed form. | Exact. |
| Anything else | First hit of the ray with the face itself, by Rhino's line/face intersection, at stations 50 mm apart along the candidate and up to 0.25 m deep. Spans where a crossing appears between two stations, or where the face turns away from the viewer, are refused. On an edge only triangles that reach the candidate's own depth are exempt; on a silhouette also triangles depth-connected to those, unless they cover part of the piece in the drawing. | **Sampled, not a proof, and it uses Rhino.** A production version needs a certified ray/patch test (section 6). |

### Results

Tables are generated by `tools/perf-f/ce_report.py` from the runs in `artifacts/curved-engine/final2/`.
"Tier 1 off" sends analytic solids through the mesh tier too.

**Time.** The "own-face ray tests" column is the phase that holds the Rhino line/face stand-in (it also holds chord
building, 0.1–0.6 s where it could be measured alone). "Without" removes that phase: an estimate of the engine with our
own ray test, not a measurement of one.

| Model, view | ε mm | Native s | Prototype s | Phase with the own-face ray tests s (tests) | Prototype without that phase s | Speed-up, with / without | Solve s | Triangles | Chords |
|---|---:|---:|---:|---:|---:|---|---:|---:|---:|
| curved S-SW | 3 | 26.25 | 50.39 | 43.94 (109,994) | 6.45 | 0.5× / 4.1× | 0.64 | 2,394,764 | 287,352 |
| curved S-SW | 1 | 26.25 | 49.51 | 43.51 (109,994) | 6.00 | 0.5× / 4.4× | 0.36 | 2,400,140 | 287,352 |
| curved S-North | 3 | 19.25 | 55.71 | 43.31 (103,525) | 12.40 | 0.3× / 1.6× | 6.49 | 2,394,764 | 297,920 |
| curved S-North | 1 | 19.25 | 62.12 | 44.99 (103,525) | 17.13 | 0.3× / 1.1× | 11.41 | 2,400,140 | 297,920 |
| diagrid-shell-SW | 10 | 1.51 | 4.06 | 3.49 (6,710) | 0.57 | 0.4× / 2.6× | 0.19 | 104,357 | 16,425 |
| diagrid-shell-SW | 3 | 1.51 | 3.80 | 3.45 (6,710) | 0.35 | 0.4× / 4.3× | 0.01 | 107,055 | 16,425 |
| diagrid-shell-SW | 1 | 1.51 | 3.69 | 3.39 (6,710) | 0.30 | 0.4× / 5.0× | 0.01 | 111,521 | 16,425 |
| curved-steel-SW | 10 | 0.58 | 2.60 | 1.85 (4,810) | 0.75 | 0.2× / 0.8× | 0.09 | 252,069 | 11,173 |
| curved-steel-SW | 3 | 0.58 | 2.40 | 1.61 (4,810) | 0.79 | 0.2× / 0.7× | 0.04 | 425,025 | 11,173 |
| curved-steel-SW | 1 | 0.58 | 3.52 | 2.03 (4,810) | 1.48 | 0.2× / 0.4× | 0.03 | 743,167 | 11,173 |
| space-frame-Front | 1 | 11.24 | 1.14 | — | 1.14 | 9.8× / 9.8× | 0.39 | 0 | 275,864 |
| space-frame-Front (mesh only) | 1 | 11.54 | 14.16 | — | 14.16 | 0.8× / 0.8× | 5.93 | 1,777,088 | 489,977 |
| space-frame-SW | 1 | 63.58 | 1.06 | — | 1.06 | 59.8× / 59.8× | 0.28 | 0 | 278,360 |
| round-tower-SW | 1 | 0.95 | 0.11 | — | 0.11 | 8.8× / 8.8× | 0.03 | 0 | 25,296 |

- Exact tier: **9–60× faster than native**, end to end, and its time barely depends on the view (space-frame: 1.1 s
  for both, where native takes 11 s and 64 s). The solve is 0.03–0.4 s; recognition (a volume integral per body) and
  chord building are the rest and would shrink with per-definition reuse.
- Mesh tier as built: **slower than native** on every freeform model, because 4,800–110,000 Rhino line/face
  intersections cost about 0.4 ms each. Without that phase it is 4–5× faster on diagrid and curved S SW, 1.1–1.6× on
  the curved S North elevation, and still slower on curved-steel, where Rhino's mesher returns 0.25–0.74 M triangles
  for 8 objects.
- **The visibility solve is small on axonometric views** (0.01–0.6 s, against 0.6–64 s native), which is the argument
  against (c) and (d). It is not small on an elevation of a gridded building: 6–11 s on curved S North and 6 s for
  mesh-only space-frame Front, where dozens of objects stack along each ray and the prototype visits every layer. A
  depth-ordered early-out is the obvious fix and was not built.
- Mesh-only on space-frame: 14 s and 84 % of the non-hidden linework Unresolved, against 1.1 s and 0 % with the exact
  tier. Meshes cannot resolve contact between analytic members (pipe ends in node spheres, aligned members).

**Unresolved share and loci.** Lengths in metres. The prototype does not merge coincident lines, so its lengths count
aligned members once each (space-frame); "native line with no engine line" is free of that.

| Model, view | ε mm | Prototype Visible / Hidden / Unresolved m | Unresolved share of all / of non-hidden | Native Visible / Hidden / Unresolved m | Native Unresolved share of all | Native line with no prototype line m | Prototype line with no native line m |
|---|---:|---|---|---|---:|---:|---:|
| curved S-SW | 3 | 1334.8 / 2929.0 / 417.7 | 8.9% / 23.8% | 1525.7 / 2841.7 / 81.7 | 1.8% | 48.4 | 0.0 |
| curved S-SW | 1 | 1358.6 / 3044.3 / 278.6 | 6.0% / 17.0% | 1525.7 / 2841.7 / 81.7 | 1.8% | 48.4 | 0.0 |
| curved S-North | 3 | 756.4 / 3437.2 / 799.7 | 16.0% / 51.4% | 875.2 / 2300.6 / 175.1 | 5.2% | 0.0 | 0.1 |
| curved S-North | 1 | 771.2 / 3470.3 / 751.8 | 15.1% / 49.4% | 875.2 / 2300.6 / 175.1 | 5.2% | 0.0 | 0.1 |
| diagrid-shell-SW | 10 | 60.8 / 106.7 / 92.1 | 35.5% / 60.3% | 73.7 / 160.4 / 19.6 | 7.7% | 0.0 | 0.0 |
| diagrid-shell-SW | 3 | 68.3 / 176.9 / 14.3 | 5.5% / 17.3% | 73.7 / 160.4 / 19.6 | 7.7% | 0.0 | 0.0 |
| diagrid-shell-SW | 1 | 71.3 / 180.3 / 8.0 | 3.1% / 10.1% | 73.7 / 160.4 / 19.6 | 7.7% | 0.0 | 0.0 |
| curved-steel-SW | 10 | 85.2 / 50.6 / 69.8 | 33.9% / 45.0% | 111.2 / 102.5 / 0.0 | 0.0% | 33.6 | 0.0 |
| curved-steel-SW | 3 | 94.1 / 77.5 / 33.9 | 16.5% / 26.5% | 111.2 / 102.5 / 0.0 | 0.0% | 33.6 | 0.0 |
| curved-steel-SW | 1 | 95.5 / 78.1 / 31.9 | 15.5% / 25.0% | 111.2 / 102.5 / 0.0 | 0.0% | 33.6 | 0.0 |
| space-frame-Front | 1 | 202.5 / 1517.7 / 0.0 | 0.0% / 0.0% | 65.2 / 160.9 / 0.0 | 0.0% | 0.0 | 0.0 |
| space-frame-Front (mesh only) | 1 | 64.7 / 1813.8 / 351.7 | 15.8% / 84.5% | 65.2 / 160.9 / 0.0 | 0.0% | 0.0 | 0.0 |
| space-frame-SW | 1 | 439.4 / 1656.1 / 0.0 | 0.0% / 0.0% | 443.2 / 1575.2 / 0.0 | 0.0% | 0.0 | 0.0 |
| round-tower-SW | 1 | 386.2 / 557.1 / 0.0 | 0.0% / 0.0% | 188.3 / 299.8 / 184.9 | 27.5% | 0.0 | 38.8 |

| Model, view | ε mm | Unresolved, by cause (m) |
|---|---:|---|
| curved S-SW | 3 | edge, own face undecidable 17.2; edge, own mesh too coarse here 36.6; near another object 151.0; silhouette, own face undecidable 93.0; silhouette, own mesh too coarse here 119.9 |
| curved S-SW | 1 | edge, own face undecidable 17.0; edge, own mesh too coarse here 31.0; near another object 94.4; silhouette, own face undecidable 93.2; silhouette, own mesh too coarse here 43.0 |
| curved S-North | 3 | edge, own face undecidable 65.8; edge, own mesh too coarse here 57.7; near another object 548.3; silhouette, own face undecidable 98.2; silhouette, own mesh too coarse here 29.7 |
| curved S-North | 1 | edge, own face undecidable 65.7; edge, own mesh too coarse here 54.1; near another object 510.9; silhouette, own face undecidable 98.2; silhouette, own mesh too coarse here 23.0 |
| diagrid-shell-SW | 10 | edge, own face undecidable 1.8; edge, own mesh too coarse here 5.6; near another object 46.0; silhouette, own face undecidable 1.0; silhouette, own mesh too coarse here 37.7 |
| diagrid-shell-SW | 3 | edge, own face undecidable 1.3; edge, own mesh too coarse here 1.7; near another object 7.0; silhouette, own face undecidable 0.5; silhouette, own mesh too coarse here 3.7 |
| diagrid-shell-SW | 1 | edge, own face undecidable 1.3; edge, own mesh too coarse here 0.2; near another object 2.9; silhouette, own face undecidable 0.5; silhouette, own mesh too coarse here 3.2 |
| curved-steel-SW | 10 | edge, own face undecidable 2.6; edge, own mesh too coarse here 7.0; silhouette, own face undecidable 5.6; silhouette, own mesh too coarse here 54.5 |
| curved-steel-SW | 3 | edge, own face undecidable 2.6; edge, own mesh too coarse here 4.6; silhouette, own face undecidable 5.4; silhouette, own mesh too coarse here 21.3 |
| curved-steel-SW | 1 | edge, own face undecidable 2.5; edge, own mesh too coarse here 4.0; silhouette, own face undecidable 5.3; silhouette, own mesh too coarse here 20.1 |
| space-frame-Front | 1 | none |
| space-frame-Front (mesh only) | 1 | near another object 351.7 |
| space-frame-SW | 1 | none |
| round-tower-SW | 1 | none |

- **Exact tier: nothing Unresolved.** Native: 0 on space-frame, 185 m (27 %) on the round tower.
- **Mesh tier at ε = 1 mm: 3 % of all linework Unresolved on diagrid, 6 % on curved S SW, 15 % on curved S North,
  16 % on curved-steel**; native 8 %, 2 %, 5 %, 0 %. Of the non-hidden linework, which is what a drawing shows, it is
  10 %, 17 %, 49 % and 25 %.
- Three causes. (1) *Near another object*: a line within ε of another body's outline or surface. This dominates the
  elevation (511 m of 752 m), where gridded objects line up exactly behind one another. (2) *Own mesh too coarse*:
  shrinking ε helps until Rhino's mesher stops honouring it; the measured deviation was 0.5–15 × ε, so some sources
  carry a 5–18 mm band instead of 1 mm. (3) *Own face undecidable*: silhouettes on flat or nearly flat stretches of a
  freeform face, and tangential change points.
- **Missing candidates:** 34 m of curved-steel (16 %) and 48 m of curved S (1 %) of native linework has no prototype
  line. On steel these are creases inside single faces (swept sections with corners), as in the curved spike. Splitting
  kinked faces recovers them but over-generates; native's rule for which creases to draw is still unknown.
- **Lines native does not draw:** 39 m on the round tower. They are rims of stacked wall drums lying flush on the
  outer surface. The ray check finds them visible (235 of 235 settled samples); native treats flush contact as Hidden.
  This is the owner's tie convention (REQUIREMENTS, 2026-11-15), not an extra candidate.

**Agreement with native, and who is right.** A sample is one point per 20 mm of prototype linework. "Same class" means
native draws that class within 1 mm. "Near a change point" means native has the prototype's class within 25 mm (native
moves its own change points by 14–18 mm between equivalent solves, perf F). The check is described below; it sampled
up to 250 points per group.

| Model, view | ε mm | Prototype certain m | Same class as native | Native Unresolved there | Differs near a change point m | Differs m | Prototype Hidden where native draws Visible m | Prototype Visible where native draws Hidden m | Checked: prototype right / flagged / inconclusive / too short |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| curved S-SW | 3 | 4180.4 | 4026.0 (96.3%) | 82.0 | 36.5 | 36.0 | 58.8 | 13.7 | 1313 / 0 / 0 / 187 |
| curved S-SW | 1 | 4317.9 | 4153.8 (96.2%) | 82.6 | 42.5 | 39.0 | 64.6 | 16.8 | 1301 / 0 / 0 / 199 |
| curved S-North | 3 | 3738.5 | 3501.3 (93.7%) | 184.8 | 22.8 | 29.5 | 42.6 | 9.7 | 1175 / 0 / 2 / 196 |
| curved S-North | 1 | 3783.6 | 3535.2 (93.4%) | 188.3 | 28.7 | 31.4 | 48.1 | 12.0 | 1159 / 0 / 4 / 291 |
| diagrid-shell-SW | 10 | 166.3 | 158.2 (95.1%) | 6.6 | 0.8 | 0.7 | 0.3 | 1.2 | 584 / 0 / 0 / 127 |
| diagrid-shell-SW | 3 | 242.4 | 220.0 (90.8%) | 15.9 | 5.5 | 0.9 | 4.9 | 1.5 | 805 / 0 / 13 / 136 |
| diagrid-shell-SW | 1 | 248.6 | 223.5 (89.9%) | 17.5 | 6.5 | 1.0 | 5.9 | 1.6 | 809 / 0 / 6 / 159 |
| curved-steel-SW | 10 | 119.2 | 118.4 (99.3%) | 0.0 | 0.4 | 0.4 | 0.8 | 0.0 | 314 / 0 / 0 / 0 |
| curved-steel-SW | 3 | 153.7 | 152.7 (99.3%) | 0.0 | 0.5 | 0.5 | 0.9 | 0.1 | 329 / 0 / 0 / 0 |
| curved-steel-SW | 1 | 155.6 | 154.4 (99.3%) | 0.0 | 0.5 | 0.6 | 1.0 | 0.1 | 341 / 0 / 0 / 3 |
| space-frame-Front | 1 | 1482.4 | 1481.1 (99.9%) | 0.0 | 1.3 | 0.0 | 1.3 | 0.0 | 244 / 0 / 8 / 248 |
| space-frame-Front (mesh only) | 1 | 1789.0 | 1789.0 (100.0%) | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | 250 / 0 / 0 / 0 |
| space-frame-SW | 1 | 2094.4 | 2058.3 (98.3%) | 0.0 | 36.0 | 0.1 | 30.6 | 5.5 | 494 / 0 / 6 / 284 |
| round-tower-SW | 1 | 919.6 | 690.1 (75.0%) | 189.0 | 1.5 | 0.2 | 1.6 | 0.1 | 803 / 0 / 10 / 34 |

| Group (all runs summed) | Samples | Checked | Prototype right | Flagged | Inconclusive | Too short to check |
|---|---:|---:|---:|---:|---:|---:|
| Same class as native | 2,716,496 | 3,500 | 3,489 | 0 | 11 | 0 |
| Prototype Hidden, native Visible | 6,372 | 1,193 | 1,135 | 0 | 2 | 56 |
| Prototype Visible, native Hidden | 2,970 | 1,303 | 1,279 | 0 | 0 | 24 |
| Prototype line with no native line | 2,559 | 287 | 235 | 0 | 0 | 52 |
| Prototype certain, native Unresolved | 63,370 | 2,000 | 1,407 | 0 | 23 | 570 |
| Prototype Hidden, native Visible, near a change point | 17,519 | 2,247 | 1,314 | 0 | 13 | 920 |
| Prototype Visible, native Hidden, near a change point | 1,637 | 1,304 | 1,062 | 0 | 0 | 242 |
| **All** | 2,810,923 | 11,834 | 9,921 | 0 | 49 | 1,864 |

- **Certain and wrong: none found.** 11,834 samples were checked over the 14 runs: 9,921 confirmed the prototype, 0
  contradicted it, 49 were inconclusive and 1,864 lay on pieces too short for the check.
- **Where the two engines disagree, the prototype was right in every settled sample.** On curved S SW at ε = 3 mm the
  prototype is Hidden where native draws Visible on 59 m (3.9 % of native's Visible length), and Visible where native
  draws Hidden on 14 m. Over all runs the check settled 2,449 samples where the prototype says Hidden and native
  draws Visible, and 2,341 the other way round; the prototype was right in all of them. The same holds on the North
  elevation (43 m and 10 m) and on diagrid (5–6 m and 1.5 m of 250 m).
- **Where native is Unresolved, the prototype is often certain and right:** 82 m on curved S SW, 185–188 m on curved S
  North and 189 m on the round tower (1,407 of 1,407 settled samples).

**The independent check.** For a sampled point of a prototype piece: intersect the view ray with every original Brep
(`Intersection.CurveBrep`, conservative box rejection, no index, no mesh, no HLD). An edge point is Hidden if something
lies more than 2 mm in front, Visible if nothing lies more than a tolerance in front, otherwise inconclusive. A
silhouette point grazes its own surface, so the ray is tested against all *other* bodies first; against its own body it
is stepped sideways in the drawing both ways (0.4 and 0.8 mm; 0.02 and 0.04 mm for closed-form outlines), Visible if
either side is clear, Hidden if both are blocked, and the two step sizes must agree. A body that blocks one side only
(an aligned member sharing the outline) or a ray that runs along a surface is a tie and is not judged. The verdict must also hold 2 mm
either way along the curve. Pieces shorter than 5 mm are below the check's resolution and are counted as such, not as
agreement. Limits: it is a sample; it shares Rhino's intersector with the own-face stand-in, so it does not
independently test that stand-in at the same point; and it cannot say anything about lines neither engine drew.

Pictures (git-ignored): `artifacts/curved-engine/tower-SW.png` and `diagrid-SW.png`, native beside the prototype with
Unresolved in orange, made by `ce_picture.py`.

## 4. What the ray check caught on the way

The first three versions of the own-surface rule all certified wrong Visible lines. Each was found by the check, traced
to one triangle list, and fixed. They are listed because they are the real risk of route (b).

| Version | Rule for the candidate's own face | How it failed | Evidence |
|---|---|---|---|
| 1 | Exempt own-face triangles nearer than a limit from the curvature at the candidate point (tangent-ball argument). | A swept RHS is one face with internal kinks. Curvature on a flat side says "no crossing for 0.8 m"; the far wall of the same face is 0.1 m away. | curved-steel: 40 of 45 checked "prototype Visible / native Hidden" samples wrong. |
| 2 | Limit from a ray test against the face, sampled at both ends and the middle of each chord. | Chords on straight edges are up to 0.5 m long; a nearer wall between the three samples was missed. | curved-steel: 26–28 wrong. |
| 3 | Stations every 50 mm; exempt everything depth-connected to the candidate. | At a corner of a wall face the next panel rises in front of the rim between two stations, and a ladder of steep triangles connected it to the rim. Also, a crossing born at depth zero at a tangential change point is invisible to both neighbouring stations. | curved S: 8 of 250 wrong, all on one definition. |
| 4 (final) | Stations; spans refused where a crossing appears or the face turns away; edges exempt only triangles reaching the candidate's own depth; silhouettes stop the chain at a triangle that covers the piece. Not seen to fail. Still a sampled rule: see Limits. | 0 flagged of 8,130 settled checks on the four freeform views. |

The lesson: the far field is easy and provable with meshes. The first few centimetres along the ray from a point on a
freeform surface are not, and every shortcut there was wrong somewhere. That part needs a real proof, not a heuristic.

## 5. Limits

- n = 1 per row on a shared machine. Uncut parallel views only. No sections, crop, glass, .NET 10, or L-size models.
- All-curved models only. The prototype sees native's inputs; on a mixed model it would ignore planar occluders, so
  perf-S/M/L were censused, not solved.
- Arithmetic is doubles with margins, not the planar kernel's filter-plus-rational scheme. Change points are found on
  the straight chord and applied to the curve (error of the order of the half-tolerance sagitta).
- The mesh bound is measured at four points on one triangle in 64, not proven; the map S → M is assumed to be the
  mesher's parametrisation.
- The loose rule for silhouettes can exempt a separate sheet of the same face whose outline lies within ε of the piece
  without covering it. No sample showed it; it is a hole in the proof.
- Free curves, points, meshes and SubD were not exercised (curved-facade has five meshes and was not run).
- Coincident lines are not merged, and source identity is not carried to the output.
- The check is a sample with 0.8 mm / 2 mm resolution (above).

## 6. Staged plan

Ordered by value. Sizes include tests. "Gate" is what must hold before the stage ships.

| Stage | What | Expected gain | Size, risk | Gate |
|---|---|---|---|---|
| **1. Exact analytic solids** — **go** | Sphere, cylinder, tube, extruded arc in `M2DPlus.Core/Visibility` with closed-form outlines; recognition in `M2DPlus.Rhino`; a third class in `ComposedVisibility`. | Native calls drop to zero on every view of perf-S/M/L except for one freeform source, on the round tower and on space-frame. Measured here: 9–60× on all-analytic views, Unresolved 185 m → 0 on the tower. On mixed building views native solve, setup and duplicate extraction are 30–60 % of the view today (routing table, L North: 16.9 of 28.8 s), so expect roughly 1.4–2× there; not measured. | ~900 lines Core, ~400 lines Rhino, tests and an independent brute-force oracle. Low risk: the predicates are 250 lines in the prototype and passed every check. | Existing routing gate extended to analytic sources: 0 unexplained, 0 wrong-Visible. Rational or interval fallback for the quadratic roots, as the planar kernel has. Recognition must fail closed (volume and topology checks). |
| **2. More analytic shapes** — go after 1 | Cone and torus (quartic, isolated roots); extruded profiles of lines and arcs as solids (filleted I-beam, rounded RHS, thick curved walls): a 2D ray against an arc polygon plus a slab. | +8 % of curved-S/M sources, +38 % of curved-steel, the remaining "AnalyticOther" in edges and tangent fixtures. | ~600 lines. Medium: torus events are degree 4–8; trimmed faces need the profile, not CSG. | Same gate. Torus only if real models show elbows. |
| **3a. Spike the two missing pieces of the mesh tier** — do before deciding 3b | (i) Certified ray against one Bezier patch over a short depth, including the tangent start (Bezier clipping with interval bounds). (ii) Tessellation with a proven chord bound from control-net second differences, per definition. Harness only. | Removes both Rhino dependencies. Tells whether the Unresolved share falls to native's level and whether 3–6× holds. | ~800 lines harness. Medium. | Own-face test agrees with Rhino's intersector on 10⁵ rays and is never contradicted; measured deviation ≤ ε on every triangle; curved S SW Unresolved ≤ 3 % at ε = 1 mm; ≥ 2× native end to end on SW and North. |
| **3b. Mesh tier in product** — no-go until 3a passes | `MeshIndex` to Core, candidates from `Silhouette.Compute`, native's crease rule for kinked faces, locus merge, uncertainty reporting. | Freeform Breps leave HLD. Estimated 4–5× on axonometric views of curved S and diagrid, less on elevations until the solver has a depth early-out; the 59 m that native draws Visible and the check finds Hidden on curved S SW goes away. | ~2,000 lines. Medium–high. | Full 233-view gate; 0 flagged by the ray check on all curved views; Unresolved share not above native's on the curved suite. |
| **4. Stays on native** | Welded and non-planar meshes (native's smooth-silhouette rule), SubD until 3b, anything recognition refuses, and silhouette curve generation (`Silhouette.Compute`, not HLD). Keep `NativeVisibilityVerifier` on whatever still goes to HLD. | — | — | — |

Not recommended: a general NURBS/SubD visibility engine (c), GPU pre-classification (d), mesh-only handling of analytic
solids (84 % Unresolved on space-frame).

### How stage 1 plugs into the union composition

`ComposedVisibility` already computes H(c) as a union over occluders with separate candidate ownership. Analytic solids
become a third occluder class and a second kind of kernel-owned candidate:

- **Routing.** A source is analytic when recognition succeeds on its owned, normalised geometry. It is then *not*
  registered with HLD as an occluder. A scene with only planar and analytic sources makes zero HLD calls.
- **Analytic occluders against any candidate.** For kernel edges (straight): the closed-form events are exact on the
  edge; add the hidden intervals to the edge's union before the curved query. For native-owned curves: the existing
  `ClipCurve` step splits a world curve at planar events and asks `PointQuery`; add the analytic events (on chords of
  the curve) and the analytic predicate at each interval's middle. Native Hidden is still never made Visible.
- **Analytic-owned candidates.** Rims are circles or ellipses, outlines are lines and a circle: emit them as
  kernel-owned world curves. Planar occlusion comes from the same `ClipCurve` path; occlusion by remaining native bodies
  from the non-occluding-curve query that kernel edges already use, with the same complete-coverage rule (missing
  native attribution is Unresolved).
- **Ties and contact.** The predicate is "more than a tolerance in front", so flush contact is Visible, matching the
  owner's tie convention. Aligned identical members give coincident Visible lines; the existing locus reconciliation
  (Unresolved > Visible > Hidden) merges them.
- **Worker and cache.** Recognition is a pure function of the owned geometry and tolerance, so it belongs in the
  per-definition layer; the codec needs no new piece fields.
- **Gate.** Analytic-owned loci need their own exact reference. The prototype's ray check (direct `CurveBrep` on the
  originals) is the natural independent oracle; it must stay separate from the engine's code.

## 7. Stage 1 in production (2026-11-16)

Stage 1 is built as product code in this worktree; nothing is committed. Rhino 8.36.26296.17001, model tolerance 0.1 mm.

**Result.** Spheres, cylinders, hollow cylinders and extruded arcs are classified exactly inside `ComposedVisibility`.
A scene of planar and exact sources makes no native HLD call: space-frame 8–40× faster, round tower 2.5–10×, with
nothing Unresolved (native: 185 m on round tower SW). Mixed building views (perf-S/M/L) are 1.1–2.2× faster, the
native solve drops from 1–16 s to about 0.3 s, and their Unresolved length goes to zero or 0.1 m. Scenes where a
freeform body overlaps every recognised solid (curved S/M, diagrid, curved-steel) are unchanged, drawing for drawing.

### What was built

| Part | Where |
|---|---|
| Exact solids, candidates, intervals, grid index. No Rhino types. | `src/M2DPlus.Core/Visibility/AnalyticSolids.cs` |
| Recognition, the removal plan, candidates, composition with kernel edges, native curves and points | `src/M2DPlus.Rhino/Services/AnalyticVisibility.cs` |
| Hooks: take the class after partition, hide kernel edges, clip native curves, append candidates, points | `ComposedVisibility.cs` (about 20 lines) |
| Routing: a scene with a recognised solid goes through composition | `NativeDrawing.cs`, `SectionDrawing.cs` (one condition each) |
| Contract and the removal argument | [engine-composition.md](../../docs/design/engine-composition.md), "Exact analytic solids (third class)" |
| Check harness | `tools/perf-f/AnalyticCheck.cs` (`av:<view>`), `av_report.py` |

`DrawingPiece`, `ViewDrawing` and the worker codec are unchanged. `NativeSettings.Analytic` (internal, default on)
turns the class off; `tangents = on` or `seams = on` turns it off for a view.

**When a source leaves native.** Only when no unrecognised body's view box overlaps it in the drawing and reaches in
front of its far side. Otherwise it stays in HLD whole; its exact solid still hides the candidates of sources that did
leave. The argument is in the header of `AnalyticVisibility.cs` and in the design note.

**Conventions.** Depth: the tie rule (cover must extend more than a tolerance in front). Sideways: solids are closed
(a ray along an outline or in the plane of a cap is covered), bores are open; the graze width is tolerance/2^16.
Anything not proven is Unresolved.

### Measurements

Before = the class off, after = on, back to back on one prepared scene in one process; cold first solve of each. The
machine was shared with another agent's gate and census runs throughout (10–30 % busy, a few runs higher), so
single timings carry a few tenths of a second of noise.

| Model, view | Sources | Recognised / exact / kept on native | Before s | After s | Speed-up | Native calls | Native solve s | Unresolved m (share) |
|---|---:|---|---:|---:|---:|---|---|---|
| curved-perf-M-blocks-SW | 5,000 | 0 / 0 / 0 | 320.46 | 311.81 | 1.0× | 1 → 1 | 316.31 → 307.48 | 445.2 (1.0%) → 445.2 (1.0%) |
| curved-perf-S-blocks-North | 500 | 0 / 0 / 0 | 20.27 | 20.44 | 1.0× | 1 → 1 | 19.26 → 19.33 | 175.3 (5.2%) → 175.3 (5.2%) |
| curved-perf-S-blocks-SW | 500 | 0 / 0 / 0 | 25.75 | 25.68 | 1.0× | 1 → 1 | 24.55 → 24.55 | 82.0 (1.8%) → 82.0 (1.8%) |
| curved-perf-S-blocks-trans | 500 | 0 / 0 / 0 | 12.51 | 12.14 | 1.0× | 1 → 1 | 10.88 → 10.65 | 172.6 (9.5%) → 172.6 (9.5%) |
| curved-steel-SW | 8 | 0 / 0 / 0 | 1.71 | 1.53 | 1.1× | 1 → 1 | 0.58 → 0.62 | 1.0 (0.5%) → 1.0 (0.5%) |
| cut-topology-SW | 17 | 1 / 1 / 0 | 0.50 | 0.06 | 8.1× | 1 → 0 | 0.34 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| cut-topology-long | 17 | 0 / 0 / 0 | 0.01 | 0.00 | 1.6× | 0 → 0 | 0.00 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| diagrid-shell-SW | 25 | 0 / 0 / 0 | 3.45 | 3.46 | 1.0× | 1 → 1 | 1.90 → 1.77 | 21.3 (8.4%) → 21.3 (8.4%) |
| glass-Front | 16 | 1 / 1 / 0 | 0.40 | 0.03 | 15.3× | 1 → 0 | 0.35 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| glass-Front-glass | 16 | 1 / 1 / 0 | 0.42 | 0.03 | 13.6× | 1 → 0 | 0.36 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| glass-North-glass | 16 | 1 / 1 / 0 | 0.64 | 0.04 | 15.2× | 1 → 0 | 0.48 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| glass-Top-glass | 16 | 1 / 1 / 0 | 0.37 | 0.03 | 11.9× | 1 → 0 | 0.33 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-East | 20,000 | 825 / 825 / 0 | 20.92 | 14.97 | 1.4× | 1 → 1 | 5.41 → 0.31 | 50.7 (0.5%) → 0.0 (0.0%) |
| perf-L-blocks-North | 20,000 | 825 / 825 / 0 | 23.67 | 16.07 | 1.5× | 1 → 1 | 8.20 → 0.31 | 1.0 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-Plan-level-1 | 20,000 | 28 / 28 / 0 | 9.63 | 8.94 | 1.1× | 1 → 0 | 0.42 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-SW | 20,000 | 825 / 823 / 2 | 49.44 | 29.64 | 1.7× | 1 → 1 | 16.50 → 0.43 | 74.4 (0.1%) → 0.1 (0.0%) |
| perf-L-blocks-Section-transverse | 20,000 | 472 / 472 / 0 | 19.47 | 15.39 | 1.3× | 1 → 1 | 3.66 → 0.32 | 0.3 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-South | 20,000 | 825 / 825 / 0 | 22.33 | 15.20 | 1.5× | 1 → 1 | 7.93 → 0.25 | 1.0 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-Top | 20,000 | 825 / 825 / 0 | 29.96 | 23.10 | 1.3× | 1 → 1 | 4.25 → 0.37 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-L-blocks-West | 20,000 | 825 / 825 / 0 | 18.22 | 14.74 | 1.2× | 1 → 1 | 4.65 → 0.33 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-East | 5,000 | 209 / 209 / 0 | 5.38 | 3.60 | 1.5× | 1 → 1 | 1.72 → 0.27 | 13.0 (0.5%) → 0.0 (0.0%) |
| perf-M-blocks-North | 5,000 | 209 / 209 / 0 | 5.67 | 4.08 | 1.4× | 1 → 1 | 1.74 → 0.28 | 1.0 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-Plan-level-1 | 5,000 | 28 / 28 / 0 | 6.47 | 5.97 | 1.1× | 1 → 0 | 0.41 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-SW | 5,000 | 209 / 207 / 2 | 13.44 | 7.98 | 1.7× | 1 → 1 | 4.81 → 0.40 | 19.8 (0.1%) → 0.1 (0.0%) |
| perf-M-blocks-Section-transverse | 5,000 | 120 / 120 / 0 | 5.00 | 3.79 | 1.3× | 1 → 1 | 1.14 → 0.31 | 1.2 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-South | 5,000 | 209 / 209 / 0 | 6.08 | 3.54 | 1.7× | 1 → 1 | 2.18 → 0.20 | 1.0 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-Top | 5,000 | 209 / 209 / 0 | 6.51 | 3.99 | 1.6× | 1 → 1 | 0.93 → 0.36 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-M-blocks-West | 5,000 | 209 / 209 / 0 | 5.50 | 4.17 | 1.3× | 1 → 1 | 1.42 → 0.33 | 0.0 (0.0%) → 0.0 (0.0%) |
| perf-S-blocks-North | 500 | 21 / 21 / 0 | 1.86 | 0.84 | 2.2× | 1 → 1 | 0.64 → 0.27 | 0.5 (0.0%) → 0.0 (0.0%) |
| perf-S-blocks-SW | 500 | 21 / 19 / 2 | 2.46 | 1.56 | 1.6× | 1 → 1 | 0.76 → 0.42 | 2.2 (0.1%) → 0.1 (0.0%) |
| perf-S-blocks-Section-transverse | 500 | 8 / 8 / 0 | 1.24 | 1.02 | 1.2× | 1 → 1 | 0.38 → 0.34 | 0.0 (0.0%) → 0.0 (0.0%) |
| space-frame-Front | 669 | 669 / 669 / 0 | 15.76 | 2.01 | 7.9× | 1 → 0 | 14.66 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| space-frame-SW | 669 | 669 / 669 / 0 | 78.15 | 1.97 | 39.7× | 1 → 0 | 76.62 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| space-frame-Top | 669 | 669 / 669 / 0 | 30.00 | 1.73 | 17.4× | 1 → 0 | 28.84 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| view-round-tower-North | 29 | 29 / 29 / 0 | 0.91 | 0.36 | 2.5× | 1 → 0 | 0.60 → 0.00 | 0.0 (0.0%) → 0.0 (0.0%) |
| view-round-tower-SW | 29 | 29 / 29 / 0 | 1.72 | 0.56 | 3.1× | 1 → 0 | 1.15 → 0.00 | 185.0 (27.5%) → 0.0 (0.0%) |
| view-round-tower-Top | 29 | 29 / 29 / 0 | 1.68 | 0.17 | 9.7× | 1 → 0 | 0.99 → 0.00 | 12.4 (14.2%) → 0.0 (0.0%) |

"Recognised / exact / kept": recognised sources, those that left HLD, those kept because an unrecognised body may hide
them. On the curved models the report shows 0 because nothing left: curved S has 124 recognisable sources of 500 and
diagrid 16 of 25, all overlapped by freeform bodies. The native call that remains on perf-S/M/L uncut views is for the
models' non-linear free curves, which HLD still classifies.

### Independent ray check

Sampled points of the new drawing against direct ray/Brep intersections on the original sources (in a section, up to
the cut plane, with solids capped there). Columns 2–3 compare the two drawings as classed loci.

| Model, view | After Visible: before Visible / Hidden / Unresolved / no line m | After Hidden: before Visible / Hidden / Unresolved / no line m | Before line with no after line m | Checked: right / flagged / not judged | Repeat identical |
|---|---|---|---:|---|---|
| curved-perf-M-blocks-SW | 6502.3 / 0.0 / 4.3 / 0.0 | 328.3 / 37024.9 / 31.3 / 0.0 | 0.0 | 1522 / 13 / 26 | yes |
| curved-perf-S-blocks-North | 871.2 / 0.0 / 3.1 / 0.0 | 18.6 / 2260.5 / 12.9 / 0.0 | 0.0 | 1115 / 56 / 65 | yes |
| curved-perf-S-blocks-SW | 1524.7 / 0.0 / 0.7 / 0.0 | 17.2 / 2818.7 / 3.2 / 0.0 | 0.0 | 909 / 29 / 25 | yes |
| curved-perf-S-blocks-trans | 458.7 / 0.0 / 1.3 / 0.0 | 12.0 / 1167.3 / 7.0 / 0.0 | 0.0 | 925 / 24 / 30 | yes |
| curved-steel-SW | 110.1 / 0.0 / 0.1 / 0.0 | 0.3 / 102.1 / 0.0 / 0.0 | 0.0 | 304 / 2 / 4 | yes |
| cut-topology-SW | 192.4 / 0.0 / 0.0 / 0.0 | 0.5 / 92.3 / 0.0 / 0.0 | 0.0 | 610 / 0 / 0 | yes |
| cut-topology-long | 41.4 / 0.0 / 0.0 / 0.0 | 0.0 / 2.8 / 0.0 / 0.0 | 0.0 | 300 / 0 / 0 | yes |
| diagrid-shell-SW | 72.0 / 0.0 / 0.1 / 0.0 | 0.3 / 159.8 / 0.2 / 0.0 | 0.0 | 569 / 34 / 4 | yes |
| glass-Front | 60.1 / 0.0 / 0.0 / 0.0 | 0.0 / 17.0 / 0.0 / 0.0 | 0.0 | 422 / 0 / 9 | yes |
| glass-Front-glass | 77.4 / 0.0 / 0.0 / 0.0 | 0.0 / 0.0 / 0.0 / 0.0 | 0.0 | 438 / 0 / 1 | yes |
| glass-North-glass | 67.6 / 0.0 / 0.0 / 0.0 | 0.0 / 9.0 / 0.0 / 0.0 | 0.0 | 440 / 0 / 0 | yes |
| glass-Top-glass | 30.1 / 0.0 / 0.0 / 0.0 | 0.0 / 0.0 / 0.0 / 0.0 | 0.0 | 330 / 0 / 0 | yes |
| perf-L-blocks-East | 5684.4 / 8.7 / 58.1 / 0.0 | 2.6 / 5275.0 / 0.0 / 0.0 | 0.0 | 1180 / 0 / 60 | yes |
| perf-L-blocks-North | 15908.0 / 0.0 / 1.0 / 0.0 | 42.1 / 7707.1 / 0.0 / 0.0 | 0.0 | 885 / 0 / 27 | yes |
| perf-L-blocks-Plan-level-1 | 849.4 / 0.0 / 0.0 / 0.0 | 0.0 / 28.8 / 0.0 / 0.0 | 0.0 | 300 / 0 / 0 | yes |
| perf-L-blocks-SW | 15895.7 / 0.5 / 39.0 / 0.0 | 264.9 / 65321.5 / 63.2 / 0.0 | 0.0 | 1277 / 0 / 8 | yes |
| perf-L-blocks-Section-transverse | 5131.8 / 0.0 / 0.3 / 0.0 | 1.2 / 5117.0 / 0.0 / 0.0 | 0.0 | 598 / 0 / 6 | yes |
| perf-L-blocks-South | 7794.0 / 0.2 / 1.0 / 0.0 | 14.2 / 15871.0 / 0.0 / 0.0 | 0.0 | 771 / 0 / 18 | yes |
| perf-L-blocks-Top | 1618.8 / 0.0 / 0.0 / 0.0 | 44.6 / 4171.4 / 0.0 / 0.0 | 0.0 | 829 / 0 / 71 | yes |
| perf-L-blocks-West | 5393.1 / 0.0 / 0.0 / 0.0 | 1.0 / 5601.5 / 0.0 / 0.0 | 0.0 | 595 / 0 / 5 | yes |
| perf-M-blocks-East | 1443.1 / 1.3 / 14.7 / 0.0 | 0.8 / 1331.2 / 0.0 / 0.0 | 0.0 | 843 / 0 / 47 | yes |
| perf-M-blocks-North | 4218.9 / 0.0 / 1.0 / 0.0 | 10.2 / 1931.6 / 0.0 / 0.0 | 0.0 | 782 / 0 / 3 | yes |
| perf-M-blocks-Plan-level-1 | 544.9 / 0.0 / 0.0 / 0.0 | 0.0 / 28.8 / 0.0 / 0.0 | 0.0 | 298 / 0 / 2 | yes |
| perf-M-blocks-SW | 4939.5 / 0.5 / 10.3 / 0.0 | 75.3 / 18131.1 / 17.2 / 0.0 | 0.0 | 1222 / 0 / 9 | yes |
| perf-M-blocks-Section-transverse | 1293.2 / 0.0 / 1.8 / 0.0 | 0.7 / 1296.7 / 0.0 / 0.0 | 0.0 | 612 / 0 / 19 | yes |
| perf-M-blocks-South | 2014.7 / 0.2 / 1.0 / 0.0 | 4.1 / 4148.6 / 0.0 / 0.0 | 0.0 | 649 / 0 / 3 | yes |
| perf-M-blocks-Top | 1145.3 / 0.0 / 0.0 / 0.0 | 7.6 / 1049.9 / 0.0 / 0.0 | 0.0 | 651 / 0 / 56 | yes |
| perf-M-blocks-West | 1362.0 / 0.0 / 0.0 / 0.0 | 0.9 / 1420.3 / 0.0 / 0.0 | 0.0 | 591 / 0 / 9 | yes |
| perf-S-blocks-North | 783.3 / 0.0 / 0.5 / 0.0 | 0.2 / 324.3 / 0.0 / 0.0 | 0.0 | 606 / 0 / 5 | yes |
| perf-S-blocks-SW | 1156.4 / 0.2 / 1.1 / 0.0 | 11.1 / 2668.9 / 2.3 / 0.0 | 0.0 | 870 / 0 / 2 | yes |
| perf-S-blocks-Section-transverse | 142.7 / 0.0 / 0.0 / 0.0 | 0.0 / 164.9 / 0.0 / 0.0 | 0.0 | 590 / 0 / 10 | yes |
| space-frame-Front | 65.2 / 0.0 / 0.0 / 0.0 | 0.7 / 151.1 / 0.0 / 0.0 | 0.0 | 324 / 0 / 0 | yes |
| space-frame-SW | 433.6 / 5.8 / 0.0 / 0.0 | 30.1 / 1549.3 / 0.0 / 0.0 | 0.0 | 647 / 0 / 8 | yes |
| space-frame-Top | 533.5 / 1.2 / 0.0 / 0.0 | 13.4 / 1334.3 / 0.0 / 0.1 | 4.9 | 445 / 0 / 31 | yes |
| view-round-tower-North | 214.4 / 9.0 / 0.0 / 0.0 | 0.0 / 162.4 / 0.0 / 0.0 | 0.0 | 469 / 0 / 8 | yes |
| view-round-tower-SW | 257.0 / 0.1 / 45.2 / 38.9 | 1.3 / 297.2 / 142.8 / 0.0 | 0.0 | 912 / 0 / 2 | yes |
| view-round-tower-Top | 137.8 / 0.0 / 12.4 / 0.0 | 0.0 / 0.0 / 0.0 / 0.0 | 0.0 | 542 / 0 / 0 | yes |

Ray check, all runs: 26,103 samples checked, 25,372 right, 158 flagged, 573 not judged.

| Group (all runs) | Samples | Checked | Right | Flagged | Not judged |
|---|---:|---:|---:|---:|---:|
| exact source, before Unresolved | 7,698 | 1,987 | 1,886 | 0 | 101 |
| exact source, no line before | 775 | 301 | 301 | 0 | 0 |
| exact source, now Hidden, before Visible | 1,600 | 1,006 | 957 | 2 | 47 |
| exact source, now Visible, before Hidden | 288 | 288 | 274 | 0 | 14 |
| exact source, same class as before | 578,600 | 9,140 | 8,937 | 29 | 174 |
| other source, before Unresolved | 1,248 | 972 | 953 | 12 | 7 |
| other source, now Hidden, before Visible | 13,178 | 2,943 | 2,697 | 84 | 162 |
| other source, now Visible, before Hidden | 166 | 166 | 166 | 0 | 0 |
| other source, same class as before | 4,819,698 | 9,300 | 9,201 | 31 | 68 |


All 158 flagged samples are on the six views where nothing left HLD and the drawing is byte-for-byte what it was
(curved S ×3, curved M, diagrid, curved-steel): they are native's own errors, the subject of sections 3–4. **On every
view the class changed, no sample is flagged.** `buildings/glass` SW with clear glass has no result: it throws a
NullReferenceException in `GlassGeometry.Features`, on `main` as well.

### Gate

Light gate (`tools/hc-perf/composition.py --required-only`, then `routing_reference.py`, `certify_intervals.py`,
`certify_mixed_intervals.py`, `routing_light_gate.py`): **PASS, 219/219 required views complete, 0 known bugs, 0
evidenced wrong-Visible samples.** 189 rows pass the strict whole-output comparison, 8 more are cleared by the
independent planar reference, 22 stay Unresolved/Info (on `main` at #27: 217 views, 186 strict, 23 Info). The mixed
certificates clear the same 50 and 82 tolerance-tie samples on perf-M blocks/exploded SW that `main` has.

Fully planar scenes are hash-identical to `main` on 13 of 13 views (blocks SW/Top/North/long/plan; curves-points,
brep_concave_polysurface, column_through_slab, block_nested_mirrored SW/Top).

Three things about the gate itself:

- **Its reference did not know exact solids.** It took "curved occluders" to be the native inputs, so a kernel edge
  behind an exact column was "outside every curved-occluder bound", hence Visible, against the drawing's Hidden: 944
  false contradictions on perf-M SW. `NativeSettings.ObserveExactSolids` (harness only) now reports the solids that
  left HLD and `CompositionGate.cs` adds their bounds; those samples are then not judged. The `av:` ray check covers
  them independently (0 flagged).
- **`--publication` is broken on `main`:** `PublicationProbe` calls the component's `Emit` with the argument list
  from before the Key output (#26) and every fixture crashes. It only times Grasshopper output; the gate was run
  without it and the probe is left as it is.
- One fixture (`units-coords-ft`) failed to start Rhino (COM E_FAIL at `RhinoCore` start-up, with another agent's
  Rhino processes on the machine) and was completed with `--resume`.

### What the checks caught on the way

1. **A line showing through the joint of two stacked columns.** The first version called contact Visible sideways.
   The section ray check found a seam visible between caps. Solids are now closed sideways.
2. **A rim cut into about a hundred pieces.** In plan the rims of stacked columns lie exactly on each other's
   outlines; chords dipped in and out of the boundary (84,582 pieces on perf-M Top, 10.6 s against 6.0 s before the
   class). Chords are now refined until their parabola is within an eighth of the graze width of the curve; the graze
   width went from tolerance/2^20 to tolerance/2^16 so that this costs no extra chords on thin tubes. perf-M Top: 2,088
   pieces, 4.0 s.
3. **Hidden duplicates under visible rims** (frozen `chs_hollow` test): a Hidden curve lying exactly under another
   curve of the class is not drawn twice.
4. **Mirrored instances** were refused (negative volume). Fixed; a mirrored block draws like its original.
5. **Straight outlines seen end-on** were clipped against every slab and then dropped. They are now skipped.

### Left on native

- Recognised solids that an unrecognised body may hide (all of curved S/M, diagrid, curved-steel; 2 on perf SW views).
- Cones, tori, trimmed or pierced analytic faces, elliptic columns, fillets, freeform Breps, meshes, SubD (stage 2/3).
- Non-linear free curves: they still cause one small HLD call (about 0.3 s on perf-M) even when no body goes to native.
- Views with `tangents = on` or `seams = on`.
- Section pieces the cut leaves unrecognisable (a cylinder cut along its axis).
- Sources at coordinates binary64 cannot resolve to tolerance/64: Unresolved, not native.

### Tests

Core: 16 new tests (`AnalyticVisibilityTests`), including a brute-force membership comparison at offsets 0, 1e3 and
1e6. Rhino: 15 new tests: recognition and refusals, rigid, mirrored and sheared transforms, composition with planar
and native bodies, points, sections, crop, clear and opaque glass, mirrored block instances, stacked columns in plan.
Existing tests that used a sphere or round column to stand for "a native body" now use an ellipsoid or elliptic
column, or pin `Analytic = false` (4 in `CompositionTests`, 1 each in `MinVisibleRhinoTests`, `GrasshopperTests`).
`tools/Test-WalkingSkeleton.ps1`: Core 303, UI 56, Rhino 233 passed on .NET 8.0.34 and on .NET 10. Debug and Release
build with 0 warnings.

Evidence (git-ignored): `artifacts/stage1/` — `av/` (logs and JSON per view), `tables-final.md`, `tests-final.log`,
`routing/` and `light-gate/` (gate), `batchA-final.log` (planar hashes). `av-round1..3` and `routing-round3` are
earlier passes kept for the history above.

### Rebased onto 0.1.0-beta.3 (2026-11-16)

The work was rebased onto `origin/main` at `e091e09` (#31–#40) as commit `38235d3` and verified again. The tables above
are from before the rebase; the rebased numbers are in `artifacts/rebase/tables-final.md`.

Resolutions:

- **Kernel solve (#37):** main's progress callback and work-budget diagnostics are kept; exact solids hide kernel edges
  right after it.
- **Duplicate resolution (#31):** main's witness-budget callback is kept, with the `!analytic.Any` condition added.
- **`ClipCurve` (#37):** the face-count and work budgets are kept. The box-versus-plane skip runs before the work
  counter, so a plane the curve cannot reach is not charged to the budget.
- **Progress (#37):** the class reports its own step, `exact curved` (`ViewSteps.ExactCurved`), per candidate.
- **Fast engine (#39):** nothing to change. The class does not read the engine setting; exact solids stay exact and
  are not counted as fast sources.
- **Local-frame sections (#40):** nothing to change. Clipped pieces come back in world coordinates and are recognised
  there (test: a plan and an oblique section 400 km from the origin).
- **Publication probe:** `TreeSources` gained a colours field in #36; the probe passes an empty map and
  `--publication` runs again.
- Four tests new on main used a sphere to stand for a body that reaches native (`RerunTests` ×3,
  `UnitScaleRhinoTests`); they use ellipsoids now.

Results on the rebased build: forced hook passed; Debug and Release 0 warnings; `Test-WalkingSkeleton.ps1` Core 383,
UI 72, Rhino 292 (1 skipped) on .NET 8 and .NET 10; planar hashes identical to `origin/main` on 13 of 13 views; ray
check 25,641 samples, 0 flagged on every view the class changes, including glass SW with clear glass (fixed by #34;
448 right) and all eight perf-L views; the 153 flagged samples are on the views where nothing leaves HLD. Light gate
(without `--publication`): **PASS, 219/219, 0 known bugs, 0 evidenced wrong-Visible**; 188 strict, 8 by the planar
reference, 23 Info. The mixed certificates clear 56 and 92 tolerance-tie samples on perf-M blocks/exploded SW. The one
Info case more than before the rebase, `mesh_terrain_brep_building` North, has no exact solid and its drawing is
hash-identical to `origin/main`.

| Model, view | Before s | After s | Native calls |
|---|---:|---:|---|
| space-frame Front / SW / Top | 12.0 / 65.5 / 31.2 | 2.1 / 2.1 / 1.7 | 1 → 0 |
| round tower SW / Top | 1.45 / 1.61 | 0.53 / 0.17 | 1 → 0 |
| perf-M SW / Top | 10.3 / 5.5 | 5.6 / 2.7 | 1 → 1 |
| perf-L SW / North / Top | 42.3 / 16.3 / 24.4 | 21.4 / 9.7 / 18.4 | 1 → 1 |
| glass SW, clear glass | 0.44 | 0.03 | 1 → 0 |

## Reproduce

```sh
dotnet build tools/perf-f -c Release
R=<Rhino.Inside_v8_net8.0.dll>; EXE=tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll; M=~/.t3/m2d-plus-models
dotnet $EXE $R $M/curved/space-frame.3dm <out> ce-census x                  # section 1
dotnet $EXE $R $M/curved/space-frame.3dm <out> ce:Front eps:10              # exact tier
dotnet $EXE $R $M/curved/space-frame.3dm <out> ce:Front eps:10 t1off        # the same through meshes
dotnet $EXE $R $M/curved/curved-perf-S-blocks.3dm <out> ce:SW eps:30 eps:10 # mesh tier at 3 mm and 1 mm
python tools/perf-f/ce_report.py <out>                                      # the result tables
python tools/perf-f/ce_census_report.py <out>                               # the census table
python tools/perf-f/ce_picture.py <out>/<stem>-ce10 picture.png             # native beside prototype
dotnet $EXE $R $M/curved/space-frame.3dm <out> av:SW                        # section 7: production class off/on + ray check
python tools/perf-f/av_report.py <out>                                      # the section 7 tables
```

Switches are listed in [tools/perf-f/README.md](../../tools/perf-f/README.md). Run one process at a time.

Evidence (git-ignored) under `artifacts/curved-engine/`: `census.log` and `census/`; `final/<run>/` with per-run JSON
(timings, lengths, matrices, adjudication, flagged samples) and 2D dumps of both drawings; `final-*.log`; `dev*.log`
(the three unsound versions and their traces). The tables come from `final2/`; `final/` is an earlier pass with a
cruder check.
