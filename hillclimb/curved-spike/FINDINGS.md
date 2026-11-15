# Curved spike — kernel-proven culling, and curved sources as meshes through the exact kernel

Research date: 2026-11-15. Base: `main` at `4bbe6b5`. Nothing committed, pushed, installed or
published. No product code changed; everything is in the harness (`tools/perf-f/Spike.cs`,
`MeshKernel.cs`, `spike_compare.py`, and a `spike:` switch in `Program.cs`).

**Headline.** Neither idea is a go as specified.

1. **Culling behind planar occluders: no-go.** On the five curved models there is nothing to
   prove with: they contain **no planar-polyhedral occluders at all** (0 planar faces in all 30
   views), so the count is 0. On the mixed building (perf-M) the proof removes 0–114 of 210
   curved sources and saves **0–12 % of the view** (20–37 % of the native solve, which is only
   about a third of the view there). That is under the 20 % bar. A cheaper exact variant found on the
   way — an instance sitting straight behind another instance of the same solid — saves
   11–19 % on gridded elevations and nothing elsewhere.
2. **Curved sources as meshes through the kernel: not ready, and not faster today.** The lines
   land in the right place (within the chord tolerance on the diagrid model, and on every native
   line of curved-steel once kinked faces are split). But the kernel spends 41–45 µs preparing
   each triangle, so it took **107 s where native takes 26 s** on curved S; Rhino's mesher does
   not hold a chord tolerance; 7–8 % of visible length on the diagrid model is classified
   differently from native with no oracle to say who is right; and the candidate rules
   over-generate. With a triangle
   fast path the estimate is about 2× faster than native on curved S, not more.

## Conditions

| Item | Value |
|---|---|
| Rhino / runtime | 8.36.26296.17001 / .NET 8.0.34, Windows x64, native `multipleThreads=false` |
| Machine | Ryzen AI MAX+ 395, 32 logical cores, shared. Every row records machine busy fraction (7–39 %) and foreign busy cores (1–11). |
| Pairs | Baseline and candidate back to back in one process on the same prepared scene. One cold solve is discarded first. n = 1. |
| Noise | Repeats of the same raw native solve differed by 0–15 % (space-frame SW: 67.7 s then 57.5 s). Treat differences under about 10 % as noise. |
| What a time includes | "View" is the product view solve (`NativeDrawing.SolveView`), bypassing admission. "Raw native" is `HiddenLineDrawing.Compute` alone on exactly the inputs the product sent to native (observed through the product's own hooks). |
| Views | `Front`, `Right` (elevations), `SW` (axonometric), `Top`, `long`, `trans` (sections through the scene centre). |

**A mistake in my own process, and what it affects.** Between 19:40 and 20:00 three of my batch
loops ran at once: stopping two earlier batches killed their shells' parents but not the loops.
That broke the one-process rule for 20 minutes and contended my own timings. I stopped all of my
own processes (no other Rhino process was touched) and reran the important rows one process at a
time. Rows marked "timing contended" below come from that window: their **counts and their
output comparisons are valid** (native is serial and deterministic), their **seconds are not**.
Logs from that window are kept apart in `artifacts/curved-spike/contended/`.

## 1. Kernel-proven culling

### What was counted

For each curved input the product sends to native (a "curved source"):

| Column | Meaning | Is it a proof? |
|---|---|---|
| Proved (planar) | The source's bounding rectangle in the view frame, placed at the source's nearest depth, is covered everywhere by the planar faces the kernel already holds. Slab decomposition of the rectangle by every face boundary and depth-crossing line; one exact `PointQuery.Classify` per cell. | Yes, except that the prototype orders the events in doubles (see limits). |
| Sampled (planar) | The same rectangle, 9 × 9 exact point decisions. | No. An upper estimate of what a better planar proof could reach. |
| Aligned repeats | A closed solid that is another instance of the same definition moved straight along the view direction, away from the viewer (all corresponding Brep vertices equal in the drawing, constant depth shift > 10 × tolerance). Every view ray through it passes through the nearer copy first. | Yes (exact argument; uncut views only). Not asked for; found during the census. |
| Any occluder (sampled) | 9 × 9 samples against planar faces **plus render meshes of the curved sources**. | No. Estimates what a proof with conservative proxies for curved occluders could reach. |
| Ceiling | Sources with no non-hidden output in the full solve. | No, and **not a valid cull set**: an open surface can have every edge hidden and its middle visible. Upper bound only. |

### Census

"Raw native saved" is the raw native time with the set removed, against the mean of two full
runs in the same process. Blank cells were not run for that view.

| Model, view | Curved sources | Planar faces | Proved (planar) | Sampled (planar) | Aligned repeats | Any occluder (sampled) | Ceiling | View wall s | Native solve s | Raw native saved: proved / aligned / ceiling | Note |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|
| curved-facade, Front | 33 | 0 | 0 | 0 | — | — | 0 | 1.18 | 1.17 | — / — / — |  |
| curved-facade, Right | 33 | 0 | 0 | 0 | — | — | 5 | 1.37 | 1.35 | — / — / 13 % |  |
| curved-facade, SW | 33 | 0 | 0 | 0 | — | — | 0 | 0.93 | 0.91 | — / — / — |  |
| curved-facade, Top | 33 | 0 | 0 | 0 | — | — | 6 | 0.87 | 0.86 | — / — / 14 % |  |
| curved-facade, long | 2 | 0 | 0 | 0 | — | — | 0 | 0.34 | 0.18 | — / — / — |  |
| curved-facade, trans | 23 | 0 | 0 | 0 | — | — | 2 | 1.38 | 0.97 | — / — / 5 % |  |
| curved-perf-S-blocks, Front | 500 | 0 | 0 | 0 | 6 | 34 | 179 | 20.96 | 20.81 | — / -1 % / 17 % |  |
| curved-perf-S-blocks, Right | 500 | 0 | 0 | 0 | — | — | 318 | 15.00 | 14.88 | — / — / 36 % | timing contended |
| curved-perf-S-blocks, SW | 500 | 0 | 0 | 0 | 0 | 4 | 28 | 23.10 | 22.92 | — / — / 13 % |  |
| curved-perf-S-blocks, Top | 500 | 0 | 0 | 0 | — | — | 80 | 20.48 | 20.35 | — / — / 15 % | timing contended |
| curved-perf-S-blocks, long | 238 | 0 | 0 | 0 | — | — | 6 | 12.37 | 10.26 | — / — / 4 % | timing contended |
| curved-perf-S-blocks, trans | 208 | 0 | 0 | 0 | — | — | 22 | 8.88 | 8.27 | — / — / -1 % | timing contended |
| curved-steel, Front | 8 | 0 | 0 | 0 | — | — | 0 | 0.59 | 0.57 | — / — / — |  |
| curved-steel, Right | 8 | 0 | 0 | 0 | — | — | 1 | 0.65 | 0.64 | — / — / 6 % |  |
| curved-steel, SW | 8 | 0 | 0 | 0 | 0 | — | 0 | 0.51 | 0.51 | — / — / — |  |
| curved-steel, Top | 8 | 0 | 0 | 0 | — | — | 0 | 0.66 | 0.66 | — / — / — |  |
| curved-steel, long | 7 | 0 | 0 | 0 | — | — | 2 | 0.63 | 0.46 | — / — / -3 % |  |
| curved-steel, trans | 5 | 0 | 0 | 0 | — | — | 1 | 0.50 | 0.48 | — / — / 11 % |  |
| diagrid-shell, Front | 25 | 0 | 0 | 0 | — | — | 5 | 2.22 | 2.21 | — / — / 13 % |  |
| diagrid-shell, Right | 25 | 0 | 0 | 0 | — | — | 4 | 2.11 | 2.10 | — / — / 18 % |  |
| diagrid-shell, SW | 25 | 0 | 0 | 0 | 0 | — | 4 | 1.73 | 1.72 | — / — / — |  |
| diagrid-shell, Top | 25 | 0 | 0 | 0 | — | — | 1 | 1.71 | 1.70 | — / — / 5 % |  |
| diagrid-shell, long | 13 | 0 | 0 | 0 | — | — | 0 | 1.43 | 0.86 | — / — / — |  |
| diagrid-shell, trans | 13 | 0 | 0 | 0 | — | — | 0 | 1.34 | 0.85 | — / — / — |  |
| perf-M-blocks, Front | 210 | 29160 | 0 | 0 | 73 | 70 | 170 | 5.96 | 2.31 | — / 27 % / 53 % |  |
| perf-M-blocks, Right | 210 | 29160 | 9; 114 with raised limits | 114 | 75 | 115 | 166 | 5.64 | 1.81 | 37 % / 39 % / 58 % | raw saving is for the 114 |
| perf-M-blocks, SW | 210 | 29160 | 80 | 116 | 41 | 125 | 125 | 12.17 | 4.23 | 20 % / 16 % / 32 % | 36 footprints over the proof limit |
| perf-M-blocks, Top | 210 | 29160 | 92 | 122 | — | 122 | 182 | 8.06 | 1.25 | 47 % / — / 58 % | timing contended |
| perf-M-blocks, long | 84 | 15688 | 0 | 0 | — | 12 | 55 | 6.90 | 1.48 | — / — / 46 % | timing contended |
| perf-M-blocks, trans | 121 | 17478 | 29 | 67 | — | 67 | 88 | 6.08 | 1.28 | 24 % / — / 64 % | timing contended |
| perf-S-blocks, Front | 22 | 2912 | 0 | 0 | — | — | 8 | 1.27 | 0.64 | — / — / 15 % |  |
| perf-S-blocks, Right | 22 | 2912 | 2 | 6 | — | — | 14 | 1.22 | 0.54 | 10 % / — / 27 % |  |
| perf-S-blocks, SW | 22 | 2912 | 2 | 2 | — | — | 3 | 2.27 | 0.85 | 11 % / — / 12 % |  |
| perf-S-blocks, Top | 22 | 2912 | 2 | 2 | — | — | 10 | 1.18 | 0.38 | 3 % / — / 10 % |  |
| perf-S-blocks, long | 9 | 2006 | 0 | 0 | — | — | 3 | 1.11 | 0.39 | — / — / 21 % |  |
| perf-S-blocks, trans | 9 | 1634 | 3 | 4 | — | — | 8 | 1.07 | 0.48 | 6 % / — / 14 % |  |
| space-frame, Front | 669 | 0 | 0 | 0 | 156 | 156 | 594 | 10.76 | 10.71 | — / 18 % / 66 % |  |
| space-frame, Right | 669 | 0 | 0 | 0 | — | — | 396 | 26.48 | 26.34 | — / — / 31 % |  |
| space-frame, SW | 669 | 0 | 0 | 0 | — | — | 5 | 66.55 | 66.28 | — / — / 3 % |  |
| space-frame, Top | 669 | 0 | 0 | 0 | — | — | 188 | 28.11 | 27.95 | — / — / 14 % |  |
| space-frame, long | 320 | 0 | 0 | 0 | — | — | 191 | 37.16 | 8.91 | — / — / 38 % | timing contended |
| space-frame, trans | 357 | 0 | 0 | 0 | — | — | 46 | 43.42 | 17.92 | — / — / 1 % | timing contended |

Reading it:

- **Curved models: 0 everywhere.** curved-perf-S-blocks, curved-facade, curved-steel,
  diagrid-shell and space-frame have no planar-polyhedral source, so the kernel holds no
  occluder. (Their solids do have planar caps, but with curved rims, so they are routed to
  native whole.)
- **Mixed M building: view dependent.** Front 0, long 0, trans 29, SW 80, Top 92, Right 114, of
  84–210 curved sources. Where it works the native solve drops 20–37 % in clean runs, but native
  is only 16–39 % of the view on this model, so the view gains at most 12 % (clean pairs below).
- **The ceiling is well above the provable count** (perf-M Front: 170 hidden, 0 provable by
  planar faces, 73 by alignment; space-frame Front: 594 hidden, 156 by alignment). Most hidden curved sources are hidden by other curved sources or by
  several occluders whose outlines coincide with theirs, which a conservative bound cannot prove.
- **Curved S, the target model:** even the ceiling is 28 of 500 in the SW view (10 % of the
  view) and 179 in Front (16 %). The provable sets are 0–6 sources and save nothing.

### Before / after pairs (product view solve, clean runs)

| Model, view | Set | Sources removed | Full view s | With set removed s | Saved | Native solve s, full → removed |
|---|---|---:|---:|---:|---:|---|
| perf-M-blocks, Right | proved (default limits) | 9 | 4.96 | 4.81 | 3 % | 1.55 → 1.45 |
| perf-M-blocks, Right | proved (raised limits) | 114 | 5.19 | 4.59 | **12 %** | 1.66 → 1.10 |
| perf-M-blocks, SW | proved | 80 | 12.06 | 10.61 | **12 %** | 4.00 → 3.45 |
| perf-M-blocks, Front | proved | 0 | — | — | 0 | — |
| perf-M-blocks, Front | aligned | 73 | 5.36 / 5.96 | 4.34 / 4.88 | **19 % / 18 %** (two runs) | 2.02 → 1.24 |
| perf-M-blocks, Right | aligned | 75 | 5.05 / 5.39 | 4.24 / 4.65 | **16 % / 14 %** | 1.62 → 1.05 |
| perf-M-blocks, SW | aligned | 41 | 11.71 | 10.41 | 11 % | 4.01 → 3.42 |
| space-frame, Front | aligned | 156 | 11.32 | 9.32 | **18 %** | 11.29 → 9.29 |
| curved-perf-S-blocks, Front | aligned | 6 | 20.45 | 20.73 | −1 % (noise) | 20.20 → 20.45 |
| curved-perf-S-blocks, SW | proved / aligned | 0 | — | — | 0 | — |
| curved-perf-S-blocks, SW | ceiling (upper bound) | 28 | 23.62 | 21.36 | 10 % | 23.51 → 21.21 |
| curved-perf-S-blocks, Front | ceiling (upper bound) | 179 | 20.34 | 17.16 | 16 % | 20.23 → 17.08 |
| space-frame, Front | ceiling (upper bound) | 594 | 12.38 | 4.18 | 66 % | 12.08 → 4.17 |
| perf-M-blocks, Front | ceiling (upper bound) | 170 | 5.67 / 5.53 | 3.83 / 4.14 | 32 % / 25 % | 2.04 → 0.96 |

All pairs, including perf-S and the contended rows: `artifacts/curved-spike/tables.md`.
The proof itself cost 0.1–3 s per view; on perf-M SW it made 1.39 M point queries (about 12 s
in the prototype, unindexed), which would eat the whole gain unless the proof is indexed.

### Does native's reduced-scene behaviour change the remaining output?

Each pair was compared with the independent comparator (`tools/perf-f/compare.py`, budget
1.25 × tolerance, both directions). "Non-hidden" compares everything except Hidden; the removed
sources have no non-hidden output, so the two drawings should be equal. The comparator samples
every 0.05 m and counts a zero-length piece as one sample, so 0.05 m is one sample; many of the
Unresolved entries are zero-length native Unset specks.

| Model, view | Set | Removed | Non-hidden length m | Full solve only | Reduced solve only | Hidden lines only in the reduced solve m |
|---|---|---:|---:|---|---|---:|
| perf-M-blocks, Front | aligned | 73 | 2007.2 | 0 | 0 | 0 |
| perf-M-blocks, Right | aligned | 75 | 1455.2 | 0 | 0 | 0 |
| perf-M-blocks, SW | aligned | 41 | 4964.8 | 0 | 0 | 0 |
| space-frame, Front | aligned | 156 | 65.2 | 0 | 0 | 0 |
| curved-perf-S-blocks, Front | aligned | 6 | 1043.2 | 0 | 0 | 0.96 |
| perf-M-blocks, Right | proved | 9 | 1455.2 | 0 | 0 | 0 |
| perf-M-blocks, Right | proved (raised limits) | 114 | 1455.2 | Visible 0.33 m | Unresolved 0.40 m | 0.30 |
| perf-M-blocks, SW | proved | 80 | 4964.8 | Visible 1 mm, Unresolved 0.10 m | Visible 2 mm, Unresolved 0.35 m | 0.30 |
| perf-M-blocks, Top | proved | 92 | 1113.9 | Unresolved 0.10 m | 0 | 0 |
| perf-M-blocks, trans | proved | 29 | 690.3 | 0 | 0 | 0.23 |
| perf-S-blocks, Right / trans | proved | 2 / 3 | 213.6 / 142.3 | 0 | 0 | 0 |
| perf-S-blocks, SW | proved | 2 | 1159.2 | Unresolved 0.05 m | Unresolved 0.20 m, Visible 0.3 mm | 0 |
| perf-S-blocks, Top | proved | 2 | 479.0 | Unresolved 0.05 m | Unresolved 0.05 m | 0 |
| space-frame, Right | ceiling (not a proof) | 396 | 216.0 | Visible 0.66 m | Visible 0.70 m (same lines, up to 14 mm apart) | 0.36 |

So: **yes, native answers differently for the sources that remain, but on provably hidden sets
the change is small.** Five of five aligned pairs and four of nine proved pairs are identical in
non-hidden output. The other five differ by Unresolved pieces moving (0.05–0.40 m), by 1–2 mm of
Visible, and in one case by 0.33 m of Visible (0.02 %) turning Unresolved. Nothing hidden turned visible
beyond 2 mm. Hidden output changes more: up to 0.96 m of hidden linework appears that the full
solve did not draw. Removing sources that are merely hidden by other curved sources (space-frame
Right) moves visible/hidden change points by up to 14 mm, the same native self-disagreement as
perf F's hemisphere finding. Reports: `artifacts/curved-spike/cullcompare-*.md` and
`*-compare-*.json` beside each pair.

Two further correctness points for any cull:

- **Hidden lines.** A culled source's hidden lines are no longer produced. That is only
  acceptable when hidden output is not requested; with hidden on, each culled source needs a
  solo solve (cacheable per definition and view direction) whose output is all Hidden.
- **The prototype is not yet a proof.** It orders slab events in doubles; a sliver cell could be
  missed. A production version needs rational event ordering in Core.

### Recommendation

| Idea | Verdict | Gain | Size |
|---|---|---|---|
| Cull curved sources hidden behind planar occluders | **No-go** for now | 0 on curved models. 0–12 % of the view on mixed M (Front 0, Right and SW 12 %). | Medium: rational slab proof with a face index in Core (≈200–300 lines), wiring in `ComposedVisibility`, a hidden-line policy, and a gate exception for native's changed specks. |
| Aligned-repeat cull (same solid straight behind itself) | **Hold**; cheap if gridded elevations matter | 14–19 % of the view on perf-M and space-frame elevations, 11 % on perf-M axo, 0 on curved S. | Small: ≈100 lines, exact, non-hidden output identical in 5 of 5 pairs. Same hidden-line caveat. |
| Proof with proxies for curved occluders | Not prototyped | Estimate only, from sampling: perf-M SW 125 sources (22 % of the view when removed), perf-M Right 115 (14–17 %), curved S 4–34 (0–2 %). | Large; needs inner proxies and overlaps with enclosure idea #6 in perf F. |

## 2. Curved sources as meshes through the exact kernel

### What was built

Per curved Brep (Extrusion and SubD through `ToBrep`): mesh it, weld vertices by exact
coordinates, and give the kernel

- **occluders**: every triangle;
- **candidates**: mesh edges lying on non-smooth Brep edges or naked edges, and mesh edges whose
  two triangles lie on the same side in the drawing (the mesh silhouette). Tangent Brep edges
  are left out, as native does with tangent edges off.

Candidates are mesh edges, so they share exact vertices with the occluders and the kernel's tie
rule decides self-contact; no depth bias is used. Free curves go in as polylines. Planar sources
use the product's own extraction. Two outputs:

- **poly**: the mesh polylines as the kernel classifies them;
- **smooth**: Brep-edge intervals mapped to parameter intervals of the original edge and trimmed
  from it; mesh-silhouette intervals transferred to Rhino's exact silhouette curves
  (`Silhouette.Compute`, tangent type) by proximity in the drawing (within 4 × chord tolerance,
  Visible winning a tie); exact silhouette with no mesh partner is Unresolved.

Reference: the product's drawing of the same view (pure native on these models).

### Finding A: Rhino's mesher does not hold a chord tolerance

Deviation measured at triangle centroids and edge midpoints against the Brep (`meshparams`):

| Setting (Tolerance = ε, RefineGrid on) | curved-steel, ε = 1 mm | curved-steel, ε = 10 mm | curved S (60 Breps), ε = 1 mm | diagrid, ε = 1 mm |
|---|---|---|---|---|
| Tolerance alone, no angle or grid minimum | 21.8 × ε, 47 k triangles | 4.2 × ε, 9.5 k | **240 × ε** (0.24 m), 62 k | — |
| Constructor defaults + Tolerance | 4.2 × ε, 501 k | 0.87 × ε, 244 k | 240 × ε, 328 k | — |
| QualityRenderMesh + Tolerance | 21.8 × ε, 62 k | 2.2 × ε, 39 k | 6.6 × ε, 216 k | — |
| Grid minimum 16, grid angle 20°, Tolerance = ε | 4.2 × ε, 501 k | 0.86 × ε, 126 k | — | 1.7 × ε, 122 k |
| **Used:** grid minimum 16, grid angle 20°, Tolerance = ε/2 | 4.2 × ε, 743 k | 0.46 × ε, 252 k | — | 1.1 × ε, 175 k |

No tested setting holds ε on every model, and the ones that come close need 10–25× the triangles
of the loosest. In the prototype runs the measured deviation was 0.09–0.67 × ε at ε = 10 mm, and
1.4–4.9 × ε on curved-steel at 3 mm and 1 mm. A product version needs its own tessellation with a
checked bound.

### Finding B: time

Seconds. ε = 10 mm is 100 × model tolerance, about 0.1 mm on paper at 1:100.

| Model, view | ε | Native view | Triangles | Meshing | Weld + candidates | Kernel, total | of which face preparation | Smooth recovery | Rhino silhouettes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| curved-steel, SW | 10 mm | 0.49 | 252,069 | 0.22 | 0.19 | 10.06 | ≈10.3 | 0.16 | 0.07 |
| curved-steel, SW | 3 mm | 0.49 | 425,025 | 0.36 | 0.24 | 17.69 | ≈17.3 | 0.22 | 0.06 |
| curved-steel, SW | 1 mm | 0.49 | 743,167 | 0.72 | 0.37 | 31.11 | ≈30.3 | 0.43 | 0.07 |
| curved-steel, SW, kinks split | 10 mm | 0.51 | 71,058 | 0.16 | 0.08 | 2.95 | ≈3.5 | 0.29 | 0.07 |
| diagrid-shell, SW | 10 mm | 1.73 | 119,717 | 0.08 | 0.11 | 5.39 | ≈4.9 | 0.25 | 0.19 |
| diagrid-shell, SW | 1 mm | 1.73 | 175,009 | 0.12 | 0.13 | 6.81 | ≈8.0 | 0.64 | 0.15 |
| space-frame, Front | 10 mm | 11.79 | 1,549,952 | 2.19 | 1.37 | 73.52 | ≈66.2 | 8.92 | 2.26 |
| curved-perf-S-blocks, SW | 10 mm | 26.28 | 2,426,672 | 2.10 | 1.83 | 103.17 | ≈108.7 | 3.79 | 1.85 |

- **The mesh route is 3–20× slower than native as built, at ε = 10 mm** (65× on curved-steel at
  1 mm). Almost all of it is the kernel
  preparing faces: 41–45 µs per triangle, on one thread, because every face gets an exact
  rational plane-residual check and an exact polygon. A triangle is planar by construction and
  needs neither. Face preparation was timed by a separate solve with no candidates; it varies by
  ±5 % run to run, so "kernel minus preparation" (the actual candidate tests) is only known to be
  small: about 0.5 s on diagrid, about 7 s on space-frame (420 k candidates), within noise on
  curved S.
- **Estimate with a triangle fast path, curved S SW:** meshing 2.1 + weld 1.8 + triangle boxes
  and index (a few seconds for 2.4 M triangles) + tests (a few seconds) + smooth 3.8 +
  silhouettes 1.9 ≈ **10–15 s against 23–26 s native**. Meshing and welding are per definition
  (8 definitions here), so they would nearly vanish with reuse. Not measured.
- **Triangle count is inflated by Finding A.** 4,850 triangles per object at ε = 10 mm; a sphere
  of 0.3 m radius needs about 150 at that tolerance.
- **Rhino's exact silhouettes are cheap:** 1.85 s for all 500 curved S sources (3.7 ms each).
  Candidate curves are not what makes native slow.

### Finding C: agreement with native

Independent comparator, both directions. "Locus" ignores classes: is a line drawn there at all?
Lengths in metres; a budget is the distance within which a partner must exist.

| Model, view | Output | ε | Native lines with no mesh line within ε | Mesh lines with no native line within ε | Native Visible with no mesh Visible, budget 10 mm | Mesh Visible with no native Visible, budget 10 mm |
|---|---|---:|---:|---:|---:|---:|
| diagrid-shell, SW | poly | 1 mm | 0.02 of 253.7 | 0 | 5.2 of 73.7 (7 %) | not reliable (see note) |
| diagrid-shell, SW | smooth | 1 mm | 0 (also 0 at 0.125 mm) | 0 | 5.5 of 73.7 (7 %) | 11.5 of 74.3 |
| diagrid-shell, SW | smooth | 10 mm | 0 | 0 | 5.7 of 73.7 (8 %) | 8.0 of 75.7 |
| curved-steel, SW | smooth | 10 mm | **32.0 of 213.7 (15 %)** | 0.6 | 7.7 of 111.2 | 1.4 of 109.7 |
| curved-steel, SW, kinks split | smooth | 10 mm | 0 | **240 of 516** | 0.5 of 111.2 (0.5 %) | 61.9 of 177.8 |
| curved-steel, SW, kinks split | smooth | 1 mm | 0 | 260 of 516 | 0.8 of 111.2 (0.7 %) | 65.9 of 175.1 |

Note: the comparator adds one sample (0.02 m here) for every unmatched piece however short, so
candidate-side lengths are overstated for the poly output, which is thousands of short segments.
Native-side columns and the smooth output are not affected in the same way.

- **Line position is good.** On diagrid (pipes on a freeform shell) every native line has a mesh
  polyline within ε and the reverse; the smooth output sits on native's lines within 0.125 mm
  because it is cut from the exact curves. No jaggies beyond tolerance in position.
- **Kinked faces were missing.** Without help, 15 % of curved-steel's native linework was absent:
  creases inside single faces (swept sections with corners). Splitting kinked faces first
  (`SplitKinkyFaces`, same point set) recovers every native line, and class agreement there is
  then within 0.5–0.7 % at 10 mm, with the rest being change points up to 30–100 mm apart.
- **But the split over-generates.** After it the mesh route draws 2.4× native's length: kink
  edges native does not draw, plus exact silhouettes that have no mesh partner (120–130 m
  Unresolved). The candidate rules do not match native's yet.
- **Classification is not settled.** On diagrid 7–8 % of native's visible length is hidden in
  the mesh route. The figure is the same at ε = 10 mm and 1 mm and barely shrinks with a wider
  budget (5.7 m at 10 mm, 4.6 m at 100 mm), so it is not tessellation error or a shifted change
  point. About half of the reverse difference lies on one source that
  native leaves Unresolved (19.6 m). The rest is unexplained: no oracle says which side is right.
- **Change points move by √(ε·r), not ε.** Where an edge crosses a silhouette tangentially, the
  mesh silhouette is at the nearest vertex. For r = 0.3 m, ε = 1 mm gives about 24 mm
  (0.24 mm on paper at 1:100); ε = 5 mm gives about 55 mm. Native disagrees with itself by
  14–18 mm in the same places.
- **Coincident copies are not merged.** space-frame Front: native 65.2 m Visible and 160.9 m
  Hidden; mesh route 88.8 m and 4,580 m, because lines of exactly aligned members are drawn once
  per member. It needs the product's locus union.
- **Not finished:** the comparator did not complete on the curved S and space-frame dumps
  (30–90 MB) in the time box. Lengths only for curved S SW: Visible 1,563 m against native
  1,526 m Visible + 82 m Unresolved; Hidden 3,084 m against 2,842 m; 6.7 % of exact silhouette
  length found no mesh partner (1–2 % on diagrid, 0.8 % on space-frame).

### Recovering smooth curves

What worked: Brep-edge candidates map back cleanly, because mesh boundary vertices lie on the
edge; parameter intervals are merged and trimmed from the original curve. Silhouettes map back
by proximity to `Silhouette.Compute` curves, with 90–99 % of exact silhouette length finding a
partner (before kink splitting; after it the kink creases are Brep edges and the exact
silhouettes there have no partner).

What I would build instead: use the exact curves as the candidates in the first place. Rhino
gives Brep edges and exact silhouettes cheaply; split them into short chords carrying their
parameter range, test those against the mesh occluders, and trim the original curve. To stop a
silhouette being hidden by its own surface's mesh, test it moved outward by ε along the surface
normal (which lies in the drawing plane at a silhouette). That removes the proximity step and
the over-generation, and leaves the mesh as an occluder only. Not prototyped.

The deeper gap is uncertainty. The kernel reports a mesh answer as certain. Within about ε of a
curved occluder's outline the true answer is unknown, and "never wrong without saying so"
requires those intervals to be Unresolved. That is the inner/outer enclosure idea (perf F #6);
this spike did not measure the width of that band.

### Could it replace native for some classes?

| Class | Evidence here | View |
|---|---|---|
| Cylinders, pipes (CHS) | diagrid members and space-frame: positions within ε; classes differ on 7–8 % of visible length on diagrid, unexplained. | Plausible, not shown. Analytic cylinders were already rejected at 8 % gain. |
| Spheres | space-frame nodes and curved S: positions fine; coincident copies need merging. No class comparison finished. | Plausible; the 82 spheres cost native 5.4 s and would be about 150 triangles each. |
| Revolved, swept, tori | curved-steel: all native lines found after kink splitting, classes within 0.5–0.7 %; candidate rules over-generate. | Closest to working; needs native's crease rule. |
| Gentle freeform | curved S: lengths within 3 % (Visible) and 9 % (Hidden); no locus or class comparison. | Unknown. |

### Recommendation

**No-go for replacing native now. Go for one narrower follow-up only if the curved verifier
gives an oracle.** In order:

| Step | Why | Size |
|---|---|---|
| Triangle fast path in the kernel (skip exact plane validation and the exact polygon for 3-vertex faces; build boxes in parallel) | Removes 90–100 % of the mesh route's time; also helps planar triangle meshes today. Measured cost now: 41–45 µs per triangle. | Small (≈40 lines in `EdgeFaceKernel.Face`), needs its own exactness review. |
| Own tessellation with a checked chord bound, per definition | Rhino's mesher misses ε by up to 240× or needs 5–50× the triangles. | Medium. |
| Exact curves as candidates, mesh as occluder only, ε-offset silhouettes | Removes proximity matching and over-generation. | Medium. |
| Uncertainty band from inner/outer meshes | Without it a mesh answer is published as certain. | Large (perf F #6). |
| Class agreement against an oracle on diagrid and curved S | 7–8 % unexplained today. | Depends on the verifier. |

Estimated gain if all of it works: about 2× on curved S at ε = 10 mm (23–26 s → 10–15 s),
more with per-definition reuse; at ε = 1 mm the triangle count is 3× higher and the gain is
uncertain.

## Limits

- n = 1 per row, shared machine, 0–15 % repeat noise.
- The planar proof orders events in doubles and gives up above 400 boundary segments or 50,000
  cells per source (raised to 6,000 / 600,000 for the two "raised limits" rows).
- Footprints are view-frame bounding rectangles at the nearest depth. A hull or silhouette
  footprint would prove more, mostly for slanted members.
- Section views were counted and timed through the product path, but the aligned test is
  uncut-view only.
- Meshes (welded, non-planar) and points are not handled by the mesh prototype; none of the
  compared models needed them except curved-facade, which was not run.
- .NET 10 host, L and larger models, crop, glass: not tested.

## Reproduce

```sh
dotnet build tools/perf-f -c Release
R=<Rhino.Inside_v8_net8.0.dll>; EXE=tools/perf-f/bin/Release/net8.0-windows/M2DPlus.PerfF.dll
dotnet $EXE $R <model.3dm> <out> spike:SW census cull        # census, raw native attribution, cull pairs + dumps
dotnet $EXE $R <model.3dm> <out> spike:SW mesh:100 mesh:10   # mesh route at 100 x and 10 x tolerance
dotnet $EXE $R <model.3dm> <out> spike:SW meshparams:10      # which meshing settings hold a chord tolerance
python tools/perf-f/spike_cullcompare.py <out>               # cull pairs through compare.py
python tools/perf-f/spike_compare.py ref-all.json mesh100-smooth-all.json --budgets=0.01,0.03
```

`PERFF_PROOF_SEGMENTS` / `PERFF_PROOF_QUERIES` raise the proof limits; `PERFF_NO_KINK=1` turns
kink splitting off. Run one process at a time.

Evidence (git-ignored) under `artifacts/curved-spike/`: `census.log` (single process until
19:39), `clean.log`, `final.log`, `contended/`, `tables.md`, `cullcompare-*.md`, and the folders
`census/`, `clean/`, `big/` (raised limits), `mesh/`, `mesh-kink/` with per-run JSON, dumps and
comparator reports.
