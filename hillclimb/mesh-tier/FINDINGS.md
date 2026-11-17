# Mesh tier (stage 3) — can meshes decide visibility for freeform sources, soundly and at native's speed?

Research date: 2026-11-17. Base: `main` at `fa3f1e0`. Harness only (`tools/perf-f/MeshTier.cs`, `mt_run.py`, a
`PERFF_MT=1` path in `CurvedEngine.cs` / `CurvedCore.cs`, an `mt-census` switch). No product code changed. Nothing
pushed, installed or published.

## 1. Verdict

**Build with caveats. Do not drop native from exact mode on this evidence.**

1. **Sound: yes, with certificates in place of the sampled own-face rule.** The two pieces the curved-engine spike
   left open (section 6, stage 3a) were built from the surfaces' control nets, with no Rhino ray test and no
   heuristic. Over the final runs the independent ray check settled about 3,900 samples and contradicted none. Two
   holes were found on the way and closed (section 5); both are the kind that only show on awkward models.
2. **Fast enough: on one of five views.** Curved S SW runs in 21.9 s against native's 25.9 s (1.2× faster). The North
   elevation is 0.6×, and the three small models are 0.1–0.5×. Most of the gap is cost the harness pays per
   instance that a product would pay per definition; that is an estimate, not a measurement (section 4).
3. **Unresolved is the real problem.** The tier leaves 7–28 % of the linework Unresolved where native leaves
   0.5–6 %. On curved S SW: 483 m against 134 m. Native's lower figure is partly false confidence: the tier proves
   Hidden about 32 m that native draws Visible on that view, confirmed by the ray check. But an owner who switches
   native off today gets three to four times as much orange.
4. **It cannot cover everything native covers.** Welded and non-planar meshes (107 sources on campus) have no
   candidate rule here, and analytic members in contact must stay with the analytic tier.

What to build first is the part that pays whatever is decided later: the certificates and the per-definition mesh
as a **dense audit** of native output (replacing the 4,096-ray sample), then the tier as an occluder and candidate
class behind a switch, then the decision in section 8.

## 2. Conditions

| Item | Value |
|---|---|
| Rhino / runtime | 8.36.26296.17001 / .NET 8, Windows x64 |
| Machine | Ryzen AI MAX+ 395, shared with other agents; machine busy 7–25 % during runs |
| Process rule | One owned Rhino.Inside process at a time. The final runs held `Local\M2DPlusLocalModelSuiteRhino`. |
| Samples | n = 1. Read differences under 10 % as noise. |
| Native time | `NativeDrawing.SolveView`, warm, ray audit off, same prepared scene and process |
| Tier time | Everything from native's inputs to classified pieces, less one harness stand-in (section 4) |
| Settings | Band asked: 1 mm (10 × tolerance). Mesh asked of Rhino at 0.2 mm. Certificate cells 12.5 mm. Band floor 0.5 mm. |
| Views | Uncut parallel views of all-curved models |

Not run: curved M (three native solves of 7 min each), campus, .NET 10, sections. The pipe node could not be run:
the harness takes native's inputs, and the product makes no native call there since stage 2. Space-frame and the
round tower are entirely analytic, so the tier has nothing to do on them.

## 3. What was built

### The bound: certify Rhino's mesh, do not trust it

Rhino's mesher does not hold its tolerance (15 × the band asked for on one curved S definition), and its texture
coordinates are packed, not surface parameters, so they cannot be used to find a triangle's place on the surface.

`FaceCert` cuts each face's surface into Bezier cells and keeps an enclosure of S_u, S_v, S_uu, S_uv, S_vv per cell.
Polynomial surfaces take these from control-point differences. Rational surfaces (71 % of the freeform faces here:
swept pipes, tori) use the quotient rule on enclosures of numerator and weight about a local origin.

For a triangle with known parameters the distance between surface and flat triangle is at most a sixth of the
largest edge value of (M_uu + M_uv) du² + (M_vv + M_uv) dv². Each triangle carries its own band, so one bad
triangle no longer widens the whole body.

| | Measured deviation | Certified bound | Ratio |
|---|---|---|---|
| Polynomial faces, 100 mm cells | 0.46–0.88 mm median | 1.0–1.5 mm median | 1.7–2.2× |
| Rational faces, 50 mm cells | ≤ 0.8 mm | 4.9 mm median | about 6× |
| Rational faces, 12.5 mm cells | ≤ 0.8 mm | 1.7 mm median | about 2× |

To certify 1 mm, Rhino has to be asked for 0.2 mm: 5.8 M triangles on curved S instead of 2.4 M. An own
tessellator that places vertices by the bound would need fewer. Triangles with no certificate (9 % on curved S,
19 % on curved-steel: faces whose NURBS form has other parameters, and triangles across a seam or a crease) fall
back to the measured body-wide bound, which is not a proof.

### The own-face test: two certificates on a parameter box

The ray starts on the candidate's own surface, so the band says nothing there. The spike's stand-in was a Rhino
line/face intersection at stations; it was wrong three times. Replaced by:

- **Edges: the box is a graph.** If the drawing directions of S_u and S_v stay in two cones with a cross product
  of one sign over the box, the box projects one-to-one, so a ray from a point of it never meets it again.
- **Silhouettes: the box is a clean fold.** With g the lateral position in the drawing and h the height over the
  plane through the ray, h is strictly convex along every curve g = const in the box (one interval expression in
  the first and second derivatives). A ray that touches the box then meets it only at the touching point.

Triangles wholly inside the certified box are exempt. Every other triangle of the face is an ordinary occluder
with its band. No depth limits, stations or chains remain. Boxes grow by doubling from 2 × band until the
certificate fails.

| Model, view | Edge chords with a box | Silhouette chords with a box |
|---|---|---|
| curved S SW | 90 % | 80 % |
| curved S North | 84 % | 70 % |
| diagrid SW | 92 % | 94 % |
| curved-steel SW | 90 % | 76 % |
| curved-facade SW | 87 % | 85 % |

### Silhouettes: Rhino's `Silhouette.Compute`

No reason was found to trace silhouettes ourselves. It is cheap (1.5–1.9 s for the 500 sources of curved S, 0.1 s
for diagrid) and accurate: distance from the curve to the true fold was at most 0.0001 mm on curved S and
0.04 mm on diagrid, against a 0.1 mm tolerance. The drawn line stays Rhino's curve; what is classified is the
fold, which the certificate needs the point to be on.

The cost that does matter is finding each point's surface parameters (3.2–4.0 s on curved S, by closest point). For
edges the trim curves give them for free; for silhouettes a tracer of our own would, and that is the only argument
for writing one.

## 4. Numbers

Lengths in metres. "Tier s" excludes one stand-in a product would not have: pulling every mesh vertex back onto
its face to learn its parameters (20.8 s on curved S SW). Finding the parameters of candidate points is included. Curved S North and curved-facade were run before the band
floor of section 5 was added.

| Model, view | Native s | Tier s | Speed | Native Unresolved | Tier Unresolved | Tier Hidden where native draws Visible | Ray check: right / wrong |
|---|---:|---:|---:|---:|---:|---:|---|
| curved S SW | 25.9 | 21.9 | 1.2× | 134 (3.0 %) | 483 (10.3 %) | 31.8 | 1,316 / 0 |
| curved S North | 22.2 | 36.3 | 0.6× | 201 (6.0 %) | 739 (14.8 %) | 22.7 | 715 / 0 |
| diagrid SW | 2.1 | 3.9 | 0.5× | 7.3 (2.9 %) | 18.9 (7.3 %) | 0.6 | 755 / 0 |
| curved-steel SW | 0.5 | 6.1 | 0.1× | 1.0 (0.5 %) | 57.6 (28.0 %) | 0.9 | 501 / 0 |
| curved-facade SW | 1.3 | 2.5 | 0.5× | 6.8 (2.2 %) | 24.7 (10.0 %) | 2.2 | 617 / 0 |

"Tier Hidden where native draws Visible" counts only samples more than 25 mm from any native line of the tier's
class. Another 65 m on curved S SW differs within 25 mm of a change point. The GPU spike's 11.6 m on this view was
what its raster could prove after the product's audit; the two are not the same measure.

**Where the time goes, curved S SW (21.9 s):** certificates 7.7, meshing 3.8, welding 4.0, parameters of candidate
points 3.2, index 2.1, silhouettes 1.5, own-face boxes 1.5, solve 0.8.

- Certificates, meshing and welding (15.5 s) are paid here per instance: 584 faces, 189 distinct certificates
  after sharing between translated copies. The model has **six** non-analytic definitions and 39,000 triangles at
  1 mm. Paid per definition and cached in the scene, this is a fraction of a second. That would put curved S SW
  near 7–9 s, about 3× native. **Not measured.**
- The North solve takes 11.2 s (1.16 billion triangle tests) because every layer along a ray is visited. A
  depth-ordered early-out, or the GPU spike's conservative CPU raster as a broad phase, is the fix. Not built.
- curved-steel is slow because Rhino returns 1.1 M triangles for eight objects at 0.2 mm.

**Where the Unresolved comes from, curved S SW (483 m):**

| Cause | m | Reducible? |
|---|---:|---|
| Silhouette, band reaches the own surface outside the box | 193 | Yes: tighter certified band (own tessellation), larger boxes |
| Silhouette, no certificate | 138 | Mostly: 220 m of silhouette chords fail "on the fold to 0.001 rad"; cause not broken down |
| Near another object | 86 | Partly: lines within the band of another body's outline. On North this is 494 m (objects in a grid, one exactly behind the other) |
| Edge, band reaches the own surface outside the box | 48 | Yes, as the first row |
| Edge, no certificate | 19 | Partly: faces seen edge-on have none by nature |

Shrinking the band alone does not help: at 0.3 mm (17.3 M triangles) the total was 364 m against 329 m at 1 mm,
both before the floor.

## 5. What the ray check caught

| | Failure | Fix |
|---|---|---|
| 1 | curved-steel: one sample drawn Visible that is Hidden, and one Hidden that is Visible. A swept section is one face with creases inside it; the fold argument assumed a smooth box, and a triangle across a crease got a second-derivative bound that does not exist. | Knots of full multiplicity are creases. No fold box may span one; a triangle across one has no certified bound. |
| 2 | curved-steel: one silhouette piece drawn Hidden that is Visible, 12 mm from a change point. Certified bands had become 0.07 mm, thinner than what the candidate itself is known to (chord sagitta of half a tolerance, the curve's own accuracy, welded vertices). | A floor under the band (0.5 mm here). The product must add the candidate's own error to the band, as stage 2 does with its gap, not use a constant. |

After both, curved-steel: 501 right, 0 wrong. The first is a proof gap that was closed; the second was closed by
a constant, so it stays open until the candidate's error is carried explicitly.

Still not proofs in this harness: the normal direction for the fold test is taken at three points of a chord, not
enclosed over it; interval arithmetic is not outward-rounded; the mesh region near a trim is assumed to follow
the trim curve; uncertified triangles use a measured bound.

## 6. The five items deferred from stage 2's review

| | Item | Mesh tier subsumes it? | Recommendation |
|---|---|---|---|
| 1 | Rule (a) is tested at three points per stretch | No. It is a coincidence test between two curves, not an occlusion question. The tier meets the same case (a far edge whose ray passes through the near edge) and needs the same rule. | Standalone fix now, as designed by stage 2's author: cover by the nearest of any piece with a Lipschitz step bound. Put it in the shared locus code so the tier uses it too. |
| 2 | Cut-tube gap measured at 65 points, unbounded at shallow crossings | Yes, as a fallback. The tier classifies the stored edge itself, so drawn and classified are one curve and there is no gap. | Do both: the standalone floor (sum of edge-to-surface distances over the sine of the least crossing angle) keeps tubes exact with nothing Unresolved; a tube that fails it goes to the tier, not to native. |
| 3 | Sliver check weak when slacks overlap; faces with no test point skipped | Yes. The tier needs no cell labels, so nothing rests on point-in-solid samples. | Make recognition strict: any sliver face that can only be checked by orientation refuses the body. That costs nothing once the tier catches the refusals. Until then keep the check as is. |
| 4 | Independent check is a 4 mm march, 10–12 % not judged | Yes. The tier's Hidden and Visible certificates are a second, unrelated code path with a resolution of the band, not 4 mm, and they are dense. | Use the tier as the witness for the analytic class in tests and the gate once it exists. No standalone fix. |
| 5 | Far rim not dropped under the near Visible rim down the cutter axis | No. Same family as item 1 (the projection doubles back, so no cut is made where two Visible pieces meet). | Standalone fix with item 1. |

## 7. Implementation plan

| Step | What | Size | Risk |
|---|---|---|---|
| A | `FaceCert` in Core: Bezier cells, polynomial and rational enclosures, deviation, graph and fold certificates, creases, outward rounding, normal enclosed over the chord. Tests against closed forms. | ~700 lines | Low–medium |
| B | Per-definition mesh with parameters and per-triangle bounds, cached in the scene. First with Rhino's mesh pulled back once per definition; own trim-conforming tessellator only if step D shows it is needed. | ~500 lines (+ ~900 for the tessellator) | Medium (tessellator: high) |
| C | **Dense audit of native output**: every native Visible piece the tier proves Hidden goes to Unresolved. Replaces the sampled ray audit on curved sources. Ships alone. | ~400 lines | Low |
| D | `MeshIndex` and solver in Core with a depth early-out; fourth class in `ComposedVisibility` behind an internal switch, off by default; candidates from edges and `Silhouette.Compute`; candidate error carried in the band; composition both ways with planar and analytic occluders. | ~1,400 lines | Medium–high |
| E | Gate: ray check on every curved view, tier-against-analytic cross-check, crease and seam fixtures. | ~600 lines | Medium |

About 3,600 lines, 4–6 weeks. A to C are about half of that and are worth having whatever is decided about native.

## 8. Decision rule for dropping native from exact mode

Drop it when all four hold on every view of the curved suite (S, M, facade, steel, diagrid, tangent, SubD) and
campus, measured under the mutex:

1. **No wrong line.** 0 flagged by the independent ray check and 0 by the tier-against-analytic cross-check.
2. **Speed.** Wall time at most 1.2 × today's exact mode. Today: met on 1 of 5 views.
3. **Unresolved.** At most native's Unresolved plus the length native is proved wrong on, or 5 % of the view's
   linework, whichever is larger. Today: 7–28 %. This is the bar the tier is furthest from, and it is the owner's
   to set; the speed bar alone would let through a drawing with a quarter of its lines orange.
4. **Every source has a route.** Welded and non-planar meshes need a candidate rule, or an explicit decision that
   they are Unresolved whole in exact mode. Without one, campus cannot leave native.

Until then: exact mode keeps native for what the tier cannot decide, with step C's audit on top.

## Reproduce

```sh
dotnet build tools/perf-f -c Release
export M2D_RHINO_INSIDE=<Rhino.Inside_v8_net8.0.dll>
dotnet $EXE $M2D_RHINO_INSIDE $M/curved/curved-perf-S-blocks.3dm <out> mt-census eps:10      # faces, bounds, silhouettes
export PERFF_MT=1 PERFF_MT_MESH=0.2 PERFF_MT_CELL=0.0125 PERFF_MT_FLOOR=5
python -B tools/perf-f/mt_run.py <label> curved/curved-perf-S-blocks.3dm ce:SW eps:10 adjudicate:250   # holds the mutex
```

`PERFF_MT=1` turns the certificates on in the `ce:` prototype; without it the prototype is as in the
curved-engine findings. `PERFF_MT_MESH` is the tolerance asked of Rhino's mesher as a fraction of the band,
`PERFF_MT_CELL` the certificate cell size in model units, `PERFF_MT_FLOOR` the least band in tolerances. Output
(git-ignored): `artifacts/mesh-tier/<label>/`.
