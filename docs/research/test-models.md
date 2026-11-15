# Local test model suite and native baseline

Local-only research tools; no CI, hook integration, plug-in changes, install, commit or push. Generated archives, sidecars, raw results and visuals live under gitignored `artifacts/`. See [exact commands](../../tools/models/README.md).

## Scope and reproducibility

The generator uses a fixed seed (20261114), metres by default, and the installed RhinoCommon 8 geometry constructors. Existing frozen primitive fixtures are reused with source SHA256 hashes, rather than copied into a second fixture set. The sidecars carry the UX view vocabulary, native depth/scope, product settings and analytic/metamorphic expectations. Native output is a baseline, not an oracle for itself.

## Models

| Model | Objects / expanded | MB | Purpose |
|---|---:|---:|---|
| annotations | 5 / 5 | 0.014 | Text, dot, dimension, leader and hatch unsupported-input census. |
| blocks | 403 / 403 | 0.242 | Four nested levels, asymmetric mirror and nonuniform scale; 200 windows plus 200 copies. |
| curves-points | 10 / 10 | 0.028 | Gridlines, hidden rebar, front/back points and a surface-contact curve. |
| cut-topology | 17 / 17 | 0.295 | Hollow profiles, nested islands, touching styles, overlap, contacts and a crop splitting an L. |
| edges | 6 / 6 | 0.104 | Filleted box, cylinder/revolution seams, SubD canopy and extrusion/Brep twins. |
| glass | 16 / 16 | 0.273 | Transparent-material curtain wall, mullions, furniture and a column behind. |
| perf-L-blocks | 20,000 / 20,000 | 8.375 | L architectural tier, blocks; 1 building(s), 30 levels. |
| perf-L-exploded | 20,000 / 20,000 | 340.515 | L architectural tier, exploded; 1 building(s), 30 levels. |
| perf-M-blocks | 5,000 / 5,000 | 2.293 | M architectural tier, blocks; 1 building(s), 8 levels. |
| perf-M-exploded | 5,000 / 5,000 | 85.127 | M architectural tier, exploded; 1 building(s), 8 levels. |
| perf-S-blocks | 500 / 500 | 0.467 | S architectural tier, blocks; 1 building(s), 2 levels. |
| perf-S-exploded | 500 / 500 | 8.533 | S architectural tier, exploded; 1 building(s), 2 levels. |
| perf-XL-blocks | 50,000 / 50,000 | 20.623 | XL architectural tier, blocks; 5 building(s), 12 levels. |
| perf-XL-exploded | 50,000 / 50,000 | 851.120 | XL architectural tier, exploded; 5 building(s), 12 levels. |
| perf-XS-blocks | 50 / 50 | 0.284 | XS architectural tier, blocks; 1 building(s), 1 levels. |
| perf-XS-exploded | 50 / 50 | 0.866 | XS architectural tier, exploded; 1 building(s), 1 levels. |
| perf-XXL-blocks | 150,000 / 150,000 | 61.212 | XXL architectural tier, blocks; 15 building(s), 12 levels. |
| perf-XXL-exploded | 150,000 / 150,000 | 2553.195 | XXL architectural tier, exploded; 15 building(s), 12 levels. |
| section-styles | 8 / 8 | 0.152 | Layer, custom, clipping-plane and nested-parent styles; no-fill, viewport fill, fallback colours. |
| units-coords-ft | 19 / 19 | 0.337 | Units/large-coordinate metamorphic building. |
| units-coords-m | 19 / 19 | 0.337 | Units/large-coordinate metamorphic building. |
| units-coords-mm | 19 / 19 | 0.337 | Units/large-coordinate metamorphic building. |
| units-coords-offset | 19 / 19 | 0.337 | Units/large-coordinate metamorphic building. |
| view-range | 9 / 9 | 0.164 | Known 2/3/4 m depths, far-plane crossings and a long receding wall. |
| view-sources | 19 / 19 | 0.352 | All seven view forms; named parallel/perspective, clip depth/scope/flipped normal. |

Performance scene cells contain slabs, cores, solid/CHS/RHS columns, piers/lintels around wall openings, glazing/mullions, stair treads and furniture. Roof plant, curved fins and SubD canopies are present; XL/XXL add a 3,200-triangle terrain mesh. Blocks and exploded forms use identical source geometry and placements. Counts exclude clip widgets and definition members. Detailed by-type counts, Brep faces, mesh triangles and definition/instance counts are in every sidecar.

XS is one detailed room; S is a two-storey house; M has eight storeys; L has a 30-storey tower plus a three-level podium. XL has five buildings and XXL fifteen. The tier object targets are exact (50, 500, 5,000, 20,000, 50,000 and 150,000), excluding view widgets.

## Fixture verification

All 13 feature archives were reopened with native read logs, validated as geometry and verified at ≤2,000,000 bytes each. The six independent hollow/overlap section checks pass their region/area/hole expectations. A separate planar Boolean of the L profile and diagonal crop gives two regions of total area 2 m². Clipping depth, scope and flipped normal are read back from the archive. `artifacts/models/validation.json` retains these checks.

The installed RhinoCommon 8 API uses `ObjectSectionAttributesSource.FromLayer`, `FromObject`, `FromSectioner` and `FromParent`; custom styles use `SetCustomSectionStyle`. `SectionStyle` exposes `HatchIndex`, `HatchScale`, `BackgroundFillMode`, `BackgroundFillColor`, `BoundaryVisible` and `BoundaryWidthScale`. The saved clipping surfaces use `PlaneDepthEnabled`, `PlaneDepth`, `SetClipParticipation` and `ParticipationListsEnabled`. All requested style properties were set and read back; no unavailable style property is recorded. `File3dm.Objects.AddSurface` preserves the clipping subtype and metadata.

All 13 feature archives and both forms of XS and S matched SHA256 byte-for-byte across regeneration (`artifacts/repeat/byte-comparison.json`). New native-assigned definition/member/dimension-style UUIDs are canonicalized in CRC-verified chunks, and native archive reads validate the result. Generation uses a fresh process per model group. Larger tiers use the same deterministic mechanism but have not been independently regenerated twice for byte comparison. Tier archive metadata includes the stable worktree staging path; byte equality is scoped to the same Rhino build/worktree.

The 13 small feature archives and their sidecars are prepared under `tests/fixtures/features/`, with hashes and future `hook` eligibility in the manifest. No hook or CI execution is enabled. Performance archives and all report/render artifacts are gitignored. Nothing was staged, committed or pushed.

Local verification also passed all six unlicensed archive/supervisor tests, the forced pre-commit hook (Debug build and 27 Core tests), the Release solution build, and normal Debug solution tests (27 Core passed; one licensed Rhino smoke test skipped). The separate offscreen renderer builds with zero warnings.

## Model reference PNGs

The Models gallery at the top of `artifacts/baseline/index.html` embeds SW and Front views of every feature, and SW/Front/Top of both forms of every tier including XXL. All are 1200×800 with a light background, layer/object colours and green view-source frame overlays. Exact PNG paths are in `artifacts/models/screenshots/manifest.json`.

A dedicated hidden Rhino.exe `/nosplash /notemplate /netcore-8 /runscript=...` attempt produced no capture before its 90 s cap (`artifacts/capture-pilot.log`); its owned Job Object alone was closed. The delivered PNGs use the documented **offscreen CPU mesh fallback**, with native Rhino meshing and a depth buffer. They are not claimed to be native viewport screenshots. Materials render opaque; annotation screen labels are simplified. The renderer is outside the plug-in/solution and uses no other Rhino process.

## Baseline method

One fresh .NET 8 Rhino.Inside worker per view, no interactive documents. The Windows supervisor creates the worker suspended and assigns it to a private Job Object **before any launcher code runs**, includes all job descendants in sampled private memory, enforces an 8 GiB hard job cap and a 600 s total wall cap (startup/read/compute/extraction). Memory is polled every 200 ms; the peak is retained, the logged time series is decimated to roughly one second, and Windows job peak memory is recorded separately. Only that job can be terminated. XS–M use both `multipleThreads` settings; features and L+ use off. Each row records host/engine/input/sidecar/harness hashes, CPU/wall/compute/preparation/read time, counts, errors and native Unset proxies. There is no first-preview stage.

Definition recursion retains cumulative instance transforms. SubD conversion to Brep is explicitly recorded; raw HLD has no direct SubD or instance-reference support. Other unsupported AddGeometry results remain visible in the diagnostics. Native clipping is used for section, depth and crop planes, including per-source scope. No heavy custom visibility engine is mixed in.

SVGs contain all extractable visible/hidden curves, with heavier native section silhouettes. Curves are tessellated at document tolerance for rendering only; the native curves are not rewritten as an engine output. Hidden points are counted but not drawn. Unset or parentless proxies are counted without dereferencing unsafe native proxies.

`Flatten=false` retains native depth during extraction; SVG paths use the resulting view-frame X/Y coordinates. The additional class-length diagnostics measure native 3D segment arclengths before depth is discarded, with unit comparisons normalized to metres. They are invariance checks, not projected plot-length totals. Hidden section curves retain dashed grey styling with the heavier cut width.

XXL has one bounded long-overlap probe per form; other XXL views were not attempted.

## Measured performance

| Tier | Form | MT | Completed / attempted | Median HLD s | Max HLD s | Total wall s | Peak private GiB |
|---|---|---|---:|---:|---:|---:|---:|
| XS | blocks | off | 9/9 | 0.389 | 0.489 | 21.9 | 0.54 |
| XS | blocks | on | 9/9 | 0.344 | 0.460 | 21.7 | 0.54 |
| XS | exploded | off | 9/9 | 0.386 | 0.487 | 21.5 | 0.54 |
| XS | exploded | on | 9/9 | 0.348 | 0.410 | 21.3 | 0.54 |
| S | blocks | off | 9/9 | 1.339 | 3.319 | 32.5 | 0.56 |
| S | blocks | on | 9/9 | 1.342 | 2.075 | 31.6 | 0.56 |
| S | exploded | off | 9/9 | 1.401 | 4.081 | 42.8 | 0.57 |
| S | exploded | on | 9/9 | 1.568 | 2.319 | 40.6 | 0.57 |
| M | blocks | off | 9/9 | 11.245 | 19.676 | 161.2 | 0.71 |
| M | blocks | on | 9/9 | 16.000 | 23.422 | 172.2 | 0.81 |
| M | exploded | off | 9/9 | 11.038 | 23.749 | 159.2 | 0.92 |
| M | exploded | on | 9/9 | 15.935 | 25.845 | 191.7 | 1.01 |
| L | blocks | off | 9/9 | 51.677 | 75.168 | 570.2 | 2.03 |
| L | exploded | off | 9/9 | 51.111 | 82.135 | 614.5 | 2.81 |
| XL | blocks | off | 1/1 | 118.272 | 118.272 | 208.7 | 2.64 |
| XL | exploded | off | 1/1 | 133.446 | 133.446 | 226.8 | 4.15 |
| XXL | blocks | off | 0/1 | — | — | 396.7 | 7.84 |
| XXL | exploded | off | 0/1 | — | — | 181.2 | 7.84 |

Single cold trials on the shared desktop, not statistical performance claims. Cold means a fresh process/scene; the OS file cache was not flushed. No unrelated processes were inspected or suspended to make the machine quiescent. The chart in `artifacts/baseline/index.html` compares the same long-overlap section view with threading off against object count; the table summarizes all attempted views. Capped attempts are retained, excluded from completed medians and chart points, and must not be read as fast successes. XL/XXL probes have only one long-overlap section per form, so their table timings are not nine-view medians.

## Feature view measurements

| Model | View | HLD s | Wall s | Visible / hidden / cut pieces | Unset | Status |
|---|---|---:|---:|---:|---:|---|
| annotations | Top | 0.000 | 5.020 | 0 / 0 / 0 | 0 | unsupported |
| blocks | Plan cut | 1.092 | 3.413 | 0 / 0 / 1612 | 0 | ok |
| blocks | SW | 2.232 | 4.618 | 3735 / 1590 / 0 | 0 | ok |
| blocks | Top | 1.086 | 3.214 | 1618 / 0 / 0 | 0 | ok |
| curves-points | Plan cut | 0.296 | 2.411 | 0 / 0 / 4 | 0 | ok |
| curves-points | SW | 0.206 | 2.413 | 14 / 6 / 0 | 0 | ok |
| curves-points | Top | 0.342 | 2.411 | 9 / 3 / 0 | 0 | ok |
| cut-topology | L split rectangle | 0.168 | 2.411 | 0 / 0 / 8 | 0 | ok |
| cut-topology | Plan cut | 0.223 | 2.412 | 8 / 4 / 63 | 0 | ok |
| cut-topology | SW | 0.296 | 2.412 | 168 / 86 / 0 | 0 | ok |
| cut-topology | Top | 0.222 | 2.413 | 75 / 0 / 0 | 0 | ok |
| edges | Plan cut | 0.299 | 5.418 | 2 / 0 / 18 | 0 | ok |
| edges | SW | 0.250 | 2.411 | 57 / 10 / 0 | 0 | ok |
| edges | Top | 0.320 | 2.411 | 34 / 1 / 0 | 0 | ok |
| glass | Front clear | 0.355 | 2.410 | 37 / 22 / 0 | 0 | ok |
| glass | Front opaque | 0.353 | 2.411 | 37 / 22 / 0 | 0 | ok |
| glass | SW | 0.284 | 2.410 | 146 / 83 / 0 | 7 | extraction_partial |
| section-styles | GH plane fallback | 0.214 | 2.212 | 0 / 0 / 32 | 0 | ok |
| section-styles | Styled clipping plane | 0.213 | 2.212 | 0 / 0 / 32 | 0 | ok |
| units-coords-ft | Plan cut | 0.344 | 2.413 | 4 / 0 / 26 | 25 | extraction_partial |
| units-coords-ft | SW | 0.405 | 2.410 | 65 / 104 / 0 | 14 | extraction_partial |
| units-coords-ft | Top | 0.518 | 2.412 | 4 / 27 / 0 | 0 | ok |
| units-coords-m | Plan cut | 0.339 | 2.212 | 4 / 0 / 26 | 25 | extraction_partial |
| units-coords-m | SW | 0.411 | 2.412 | 64 / 107 / 0 | 20 | extraction_partial |
| units-coords-m | Top | 0.515 | 2.413 | 4 / 26 / 0 | 0 | ok |
| units-coords-mm | Plan cut | 0.341 | 2.411 | 4 / 0 / 26 | 25 | extraction_partial |
| units-coords-mm | SW | 0.407 | 2.411 | 63 / 104 / 0 | 11 | extraction_partial |
| units-coords-mm | Top | 0.515 | 2.412 | 4 / 27 / 0 | 0 | ok |
| units-coords-offset | Plan cut | 0.337 | 2.412 | 4 / 0 / 27 | 23 | extraction_partial |
| units-coords-offset | SW | 0.413 | 2.411 | 62 / 105 / 0 | 24 | extraction_partial |
| units-coords-offset | Top | 0.528 | 2.411 | 4 / 22 / 0 | 0 | ok |
| view-range | Range line | 0.241 | 2.212 | 32 / 0 / 4 | 0 | ok |
| view-range | Range noline | 0.263 | 5.218 | 32 / 0 / 4 | 0 | ok |
| view-range | Range none | 0.238 | 2.211 | 32 / 0 / 4 | 0 | ok |
| view-sources | Depth clip | 0.348 | 2.411 | 4 / 0 / 26 | 25 | extraction_partial |
| view-sources | Direction Front | 0.272 | 2.211 | 47 / 20 / 0 | 0 | ok |
| view-sources | Flipped clip | 0.336 | 2.211 | 4 / 0 / 26 | 25 | extraction_partial |
| view-sources | Front | 0.318 | 2.611 | 47 / 20 / 0 | 0 | ok |
| view-sources | GH box | 0.007 | 2.012 | 0 / 0 / 0 | 0 | ok |
| view-sources | GH plane | 0.335 | 2.412 | 4 / 0 / 26 | 25 | extraction_partial |
| view-sources | GH rectangle | 0.007 | 2.010 | 0 / 0 / 0 | 0 | ok |
| view-sources | Perspective must error | — | 2.010 | 0 / 0 / 0 | 0 | view_error |
| view-sources | Plain clip | 0.460 | 2.612 | 4 / 0 / 26 | 25 | extraction_partial |
| view-sources | Rotated parallel | 0.329 | 2.409 | 4 / 32 / 0 | 0 | ok |
| view-sources | Structure only | 0.517 | 2.612 | 4 / 26 / 0 | 0 | ok |
| view-sources | Top | 0.541 | 2.611 | 4 / 26 / 0 | 0 | ok |
| view-sources | Vector oblique | 0.392 | 2.411 | 58 / 110 / 0 | 19 | extraction_partial |

## Expanded input equivalence

Before interpreting blocks/exploded output differences, a separate native geometry comparison expands both archives into world coordinates and checks each named source with Brep.IsDuplicate at 0.001 × model tolerance (exact GeometryEquals for meshes). `artifacts/models/pair-validation.json` records the results.

- XS: 50 sources checked; equivalent=True; 0 mismatches.
- S: 500 sources checked; equivalent=True; 0 mismatches.
- M: 5,000 sources checked; equivalent=True; 0 mismatches.
- L: 20,000 sources checked; equivalent=True; 0 mismatches.
- XL: 50,000 sources checked; equivalent=True; 0 mismatches.
- XXL: 150,000 sources checked; equivalent=True; 0 mismatches.

The unit/large-coordinate inputs are also normalized to metres and translated back before a separate Brep geometry comparison at 1e-7 m:
- units-coords-mm: 19 sources; equivalent=True; 0 mismatches.
- units-coords-ft: 19 sources; equivalent=True; 0 mismatches.
- units-coords-offset: 19 sources; equivalent=True; 0 mismatches.

## Limited output assessments

31 / 135 limited checks match their expectations or comparison aggregates. These include analytic beam/point occlusion, annotation rejection, unit/translation class lengths, threading comparisons and blocks-versus-exploded class lengths. See `artifacts/baseline/assessment.json` for every result. Equal lengths/counts do not prove equal loci.

- **glass / Front clear** — product clear-glass expectation (not implemented by raw HLD): differs from expectation. Material policy gap; furniture/column remain dashed behind glazing in the SVG. The correct full output is not established by this comparison.
- **units-coords-mm / Top** — units/translation class-length metamorphism: Hidden +4.599906 m; Visible +0.000000 m.  The correct full output is not established by this comparison.
- **units-coords-ft / Top** — units/translation class-length metamorphism: Hidden +3.799906 m; Visible +0.000000 m.  The correct full output is not established by this comparison.
- **units-coords-offset / Top** — units/translation class-length metamorphism: Hidden +18.599978 m; Visible +0.000000 m.  The correct full output is not established by this comparison.
- **units-coords-mm / SW** — units/translation class-length metamorphism: Hidden +0.000000 m; Visible +3.149975 m.  The correct full output is not established by this comparison.
- **units-coords-ft / SW** — units/translation class-length metamorphism: Hidden +0.000000 m; Visible +1.449929 m.  The correct full output is not established by this comparison.
- **units-coords-offset / SW** — units/translation class-length metamorphism: Hidden +0.000003 m; Visible +2.200049 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / East elevation** — native threading class-length/count comparison: Hidden +12.831811 m; Visible -5.254212 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / North elevation** — native threading class-length/count comparison: Hidden -71.776515 m; Visible +2.613659 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Plan level 1** — native threading class-length/count comparison: Cut -0.000019 m; Visible +0.000200 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Plan upper** — native threading class-length/count comparison: Cut +0.000159 m; Hidden +51.437158 m; Visible +3.500617 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section long overlap** — native threading class-length/count comparison: Cut +0.000103 m; Hidden +7.172986 m; Visible -5.898960 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section transverse** — native threading class-length/count comparison: Cut +6.150011 m; Hidden -17.424310 m; Visible +0.000642 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / South elevation** — native threading class-length/count comparison: Hidden -31.607725 m; Visible +26.476337 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / SW axo** — native threading class-length/count comparison: Hidden +40.017406 m; Visible -19.059402 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / West elevation** — native threading class-length/count comparison: Hidden +13.163456 m; Visible +0.000666 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / East elevation** — native threading class-length/count comparison: Hidden +6.329128 m; Visible -2.311097 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / North elevation** — native threading class-length/count comparison: Hidden -28.940839 m; Visible +3.128572 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / Plan level 1** — native threading class-length/count comparison: Cut +0.000168 m; Visible -0.000041 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / Plan upper** — native threading class-length/count comparison: Cut +0.000274 m; Hidden +162.513736 m; Visible +0.000198 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / Section long overlap** — native threading class-length/count comparison: Cut -0.000113 m; Hidden +14.540110 m; Visible -2.899401 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / Section transverse** — native threading class-length/count comparison: Cut +11.249859 m; Hidden +83.375993 m; Visible -0.499233 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / South elevation** — native threading class-length/count comparison: Hidden -64.257324 m; Visible +28.837865 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / SW axo** — native threading class-length/count comparison: Hidden +32.559031 m; Visible +5.959329 m.  The correct full output is not established by this comparison.
- **perf-M-exploded / West elevation** — native threading class-length/count comparison: Hidden +11.047591 m; Visible +0.000862 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / East elevation** — native threading class-length/count comparison: Hidden +8.291981 m; Visible +0.000356 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / North elevation** — native threading class-length/count comparison: Hidden -3.730136 m; Visible +0.071324 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Plan level 1** — native threading class-length/count comparison: Cut +0.000106 m; Visible +0.000173 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Plan upper** — native threading class-length/count comparison: Cut -0.000109 m; Hidden -2.350322 m; Visible +0.050702 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section long overlap** — native threading class-length/count comparison: Cut +0.049915 m; Hidden -2.850382 m; Visible +0.000019 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section transverse** — native threading class-length/count comparison: Cut +0.000000 m; Hidden +2.632355 m; Visible +0.000105 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / South elevation** — native threading class-length/count comparison: Hidden -7.255635 m; Visible +2.831220 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / SW axo** — native threading class-length/count comparison: Hidden +0.299285 m; Visible -1.097245 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / West elevation** — native threading class-length/count comparison: Hidden -0.286865 m; Visible +0.000100 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / East elevation** — native threading class-length/count comparison: Hidden -2.713341 m; Visible -0.213213 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / North elevation** — native threading class-length/count comparison: Hidden -10.600003 m; Visible +0.000160 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / Plan level 1** — native threading class-length/count comparison: Cut -0.000134 m; Visible -0.000045 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / Plan upper** — native threading class-length/count comparison: Cut -0.000128 m; Hidden -3.384306 m; Visible +0.049946 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / Section long overlap** — native threading class-length/count comparison: Cut -0.000170 m; Hidden +4.575806 m; Visible -1.399623 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / Section transverse** — native threading class-length/count comparison: Cut -0.000000 m; Hidden +14.826994 m; Visible +0.000147 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / South elevation** — native threading class-length/count comparison: Hidden +7.911340 m; Visible +1.100809 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / SW axo** — native threading class-length/count comparison: Hidden -0.148316 m; Visible -2.622879 m.  The correct full output is not established by this comparison.
- **perf-S-exploded / West elevation** — native threading class-length/count comparison: Hidden +3.685542 m; Visible +0.000089 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / East elevation** — native threading class-length/count comparison: Hidden +0.120057 m; Visible -0.000027 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / Plan level 1** — native threading class-length/count comparison: Cut -0.000000 m; Visible -0.000002 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / Plan upper** — native threading class-length/count comparison: Cut +1.200000 m; Hidden +0.000000 m; Visible -1.200014 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / South elevation** — native threading class-length/count comparison: Hidden -0.080209 m; Visible +0.080138 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / SW axo** — native threading class-length/count comparison: Hidden -0.000202 m; Visible +0.158541 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / West elevation** — native threading class-length/count comparison: Hidden +0.000238 m; Visible -0.000323 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / East elevation** — native threading class-length/count comparison: Hidden +0.060070 m; Visible +0.005841 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / North elevation** — native threading class-length/count comparison: Hidden +0.019510 m; Visible +0.000402 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / Plan upper** — native threading class-length/count comparison: Cut +1.200000 m; Hidden +0.000000 m; Visible -1.200050 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / Section transverse** — native threading class-length/count comparison: Cut -0.000000 m; Hidden +2.550029 m; Visible -0.000041 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / South elevation** — native threading class-length/count comparison: Hidden -1.740634 m; Visible +1.739983 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / SW axo** — native threading class-length/count comparison: Hidden -0.000154 m; Visible -0.000043 m.  The correct full output is not established by this comparison.
- **perf-XS-exploded / West elevation** — native threading class-length/count comparison: Hidden +0.000163 m; Visible -0.000318 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden +0.059987 m; Visible -0.005868 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden -0.019721 m; Visible -0.000239 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut +0.000000 m; Hidden -2.550000 m; Visible +0.000012 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden -0.840000 m; Visible +0.840000 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden +0.820425 m; Visible -0.819845 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden +0.000029 m; Visible -0.508567 m.  The correct full output is not established by this comparison.
- **perf-XS-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden -0.000018 m; Visible -0.349984 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden -3.192618 m; Visible -0.096227 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden +7.812703 m; Visible +0.117342 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden +1.066757 m; Visible -0.000046 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden +7.936624 m; Visible +0.071118 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Plan upper** — blocks/exploded native aggregate comparison: Cut +0.000000 m; Hidden -0.533737 m; Visible -0.000061 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Plan upper** — blocks/exploded native aggregate comparison: Cut +0.000018 m; Hidden +0.500247 m; Visible +0.000695 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut -40.049670 m; Hidden +49.925877 m; Visible +0.000177 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut -39.999585 m; Hidden +42.499689 m; Visible +1.399819 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut +0.000000 m; Hidden -0.255926 m; Visible +0.000062 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut +0.000000 m; Hidden -12.450565 m; Visible +0.000019 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden +2.947607 m; Visible +1.660124 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden -12.219368 m; Visible +3.390534 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden +4.167153 m; Visible -3.946600 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden +4.614754 m; Visible -2.420966 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / West elevation** — blocks/exploded native aggregate comparison: Hidden -1.073013 m; Visible -0.000008 m.  The correct full output is not established by this comparison.
- **perf-S-blocks / West elevation** — blocks/exploded native aggregate comparison: Hidden -5.045419 m; Visible +0.000003 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden -23.952256 m; Visible -0.096114 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden -17.449573 m; Visible -3.039230 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden +31.845945 m; Visible +0.000869 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden -10.989731 m; Visible -0.514044 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Plan upper** — blocks/exploded native aggregate comparison: Cut +0.000042 m; Hidden +76.885293 m; Visible +0.000131 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Plan upper** — blocks/exploded native aggregate comparison: Cut -0.000072 m; Hidden -34.191285 m; Visible +3.500549 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut +28.700027 m; Hidden -36.264346 m; Visible +0.000023 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut +28.700243 m; Hidden -43.631470 m; Visible -2.999535 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut -7.750107 m; Hidden +64.109282 m; Visible -0.180152 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut -12.849955 m; Hidden -36.691021 m; Visible +0.319723 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden -7.152841 m; Visible -0.279986 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden +25.496758 m; Visible -2.641515 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden +3.108614 m; Visible +8.769031 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden +10.566989 m; Visible -16.249699 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / West elevation** — blocks/exploded native aggregate comparison: Hidden -30.641486 m; Visible +0.000140 m.  The correct full output is not established by this comparison.
- **perf-M-blocks / West elevation** — blocks/exploded native aggregate comparison: Hidden -28.525620 m; Visible -0.000056 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / East elevation** — blocks/exploded native aggregate comparison: Hidden -57.587693 m; Visible -0.000101 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / North elevation** — blocks/exploded native aggregate comparison: Hidden +11.078765 m; Visible +0.000934 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / Plan upper** — blocks/exploded native aggregate comparison: Cut +0.000008 m; Hidden +50.812928 m; Visible +0.200097 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut +28.700098 m; Hidden -33.041575 m; Visible +0.349937 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / Section transverse** — blocks/exploded native aggregate comparison: Cut +4.649935 m; Hidden -11.431101 m; Visible -0.908044 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / South elevation** — blocks/exploded native aggregate comparison: Hidden +2.407392 m; Visible -75.309233 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / SW axo** — blocks/exploded native aggregate comparison: Hidden -13.982720 m; Visible -24.962964 m.  The correct full output is not established by this comparison.
- **perf-L-blocks / West elevation** — blocks/exploded native aggregate comparison: Hidden -29.380783 m; Visible -0.099604 m.  The correct full output is not established by this comparison.
- **perf-XL-blocks / Section long overlap** — blocks/exploded native aggregate comparison: Cut +267.100004 m; Hidden -453.283462 m; Visible -0.100040 m.  The correct full output is not established by this comparison.

## Visual inspection

The inspected feature contact sheet includes `cut-topology / Plan cut` and `L split rectangle`, `glass / Front clear`, `curves-points / Top`, `blocks / SW` and `edges / SW`. The L crop shows two disconnected closed outlines; nested RHS/CHS holes and the inner island are visible. Rebar is dashed, the surface-contact curve is solid, and the two rotated window fields are visually comparable. The clear-glass view visibly retains hidden dashed furniture/column geometry. No hatch fill is shown because raw HLD supplies no material-region/style pipeline.

An enlarged inspection of **cut-topology / Plan cut** shows the shared interior joint at x=2 m between the same-style touching walls (y=5 to 5.3 m). It remains drawn, just like the joint between the different-colour walls. Raw HLD therefore does not satisfy the product’s same-style merged-region expectation; this is a semantic merge gap, not a claim that native HLD implements that policy incorrectly. `artifacts/qa-touching.html` preserves the enlarged inspection.

## Native findings and limits

Execution statuses: `{"extraction_partial": 86, "ok": 87, "private_memory_limit": 2, "unsupported": 1, "view_error": 1}`. Full per-source records are in `artifacts/baseline/*.json`.

176 rows record the Rhino build and RhinoCommon SHA256 but have an empty native-binary hash map. The runner now resolves the parent System directory when RhinoCommon is in System/netcore; earlier measurements were preserved without backfilling unobserved per-run fingerprints.

- **annotations / Top / MT off**: unsupported; Unset proxies unmeasured; extraction errors 0; .
- **glass / SW / MT off**: extraction_partial; Unset proxies 7; extraction errors 0; .
- **perf-L-blocks / Plan level 1 / MT off**: extraction_partial; Unset proxies 2466; extraction errors 0; .
- **perf-L-blocks / Plan upper / MT off**: extraction_partial; Unset proxies 8691; extraction errors 0; .
- **perf-L-blocks / Section long overlap / MT off**: extraction_partial; Unset proxies 25622; extraction errors 0; .
- **perf-L-blocks / Section transverse / MT off**: extraction_partial; Unset proxies 5859; extraction errors 0; .
- **perf-L-blocks / SW axo / MT off**: extraction_partial; Unset proxies 1684; extraction errors 0; .
- **perf-L-exploded / Plan level 1 / MT off**: extraction_partial; Unset proxies 2480; extraction errors 0; .
- **perf-L-exploded / Plan upper / MT off**: extraction_partial; Unset proxies 8825; extraction errors 0; .
- **perf-L-exploded / Section long overlap / MT off**: extraction_partial; Unset proxies 25375; extraction errors 0; .
- **perf-L-exploded / Section transverse / MT off**: extraction_partial; Unset proxies 5858; extraction errors 0; .
- **perf-L-exploded / SW axo / MT off**: extraction_partial; Unset proxies 1390; extraction errors 0; .
- **perf-M-blocks / Plan level 1 / MT off**: extraction_partial; Unset proxies 2246; extraction errors 0; .
- **perf-M-blocks / Plan level 1 / MT on**: extraction_partial; Unset proxies 9110; extraction errors 0; .
- **perf-M-blocks / Plan upper / MT off**: extraction_partial; Unset proxies 3610; extraction errors 0; .
- **perf-M-blocks / Plan upper / MT on**: extraction_partial; Unset proxies 9435; extraction errors 0; .
- **perf-M-blocks / Section long overlap / MT off**: extraction_partial; Unset proxies 6279; extraction errors 0; .
- **perf-M-blocks / Section long overlap / MT on**: extraction_partial; Unset proxies 29112; extraction errors 0; .
- **perf-M-blocks / Section transverse / MT off**: extraction_partial; Unset proxies 1530; extraction errors 0; .
- **perf-M-blocks / Section transverse / MT on**: extraction_partial; Unset proxies 8358; extraction errors 0; .
- **perf-M-blocks / SW axo / MT off**: extraction_partial; Unset proxies 732; extraction errors 0; .
- **perf-M-blocks / SW axo / MT on**: extraction_partial; Unset proxies 770; extraction errors 0; .
- **perf-M-exploded / Plan level 1 / MT off**: extraction_partial; Unset proxies 2230; extraction errors 0; .
- **perf-M-exploded / Plan level 1 / MT on**: extraction_partial; Unset proxies 8917; extraction errors 0; .
- **perf-M-exploded / Plan upper / MT off**: extraction_partial; Unset proxies 3619; extraction errors 0; .
- **perf-M-exploded / Plan upper / MT on**: extraction_partial; Unset proxies 9607; extraction errors 0; .
- **perf-M-exploded / Section long overlap / MT off**: extraction_partial; Unset proxies 6059; extraction errors 0; .
- **perf-M-exploded / Section long overlap / MT on**: extraction_partial; Unset proxies 28044; extraction errors 0; .
- **perf-M-exploded / Section transverse / MT off**: extraction_partial; Unset proxies 1565; extraction errors 0; .
- **perf-M-exploded / Section transverse / MT on**: extraction_partial; Unset proxies 8257; extraction errors 0; .
- **perf-M-exploded / SW axo / MT off**: extraction_partial; Unset proxies 761; extraction errors 0; .
- **perf-M-exploded / SW axo / MT on**: extraction_partial; Unset proxies 789; extraction errors 0; .
- **perf-S-blocks / Plan level 1 / MT off**: extraction_partial; Unset proxies 938; extraction errors 0; .
- **perf-S-blocks / Plan level 1 / MT on**: extraction_partial; Unset proxies 3437; extraction errors 0; .
- **perf-S-blocks / Plan upper / MT off**: extraction_partial; Unset proxies 972; extraction errors 0; .
- **perf-S-blocks / Plan upper / MT on**: extraction_partial; Unset proxies 3576; extraction errors 0; .
- **perf-S-blocks / Section long overlap / MT off**: extraction_partial; Unset proxies 900; extraction errors 0; .
- **perf-S-blocks / Section long overlap / MT on**: extraction_partial; Unset proxies 3618; extraction errors 0; .
- **perf-S-blocks / Section transverse / MT off**: extraction_partial; Unset proxies 249; extraction errors 0; .
- **perf-S-blocks / Section transverse / MT on**: extraction_partial; Unset proxies 974; extraction errors 0; .
- **perf-S-blocks / SW axo / MT off**: extraction_partial; Unset proxies 42; extraction errors 0; .
- **perf-S-blocks / SW axo / MT on**: extraction_partial; Unset proxies 72; extraction errors 0; .
- **perf-S-exploded / Plan level 1 / MT off**: extraction_partial; Unset proxies 923; extraction errors 0; .
- **perf-S-exploded / Plan level 1 / MT on**: extraction_partial; Unset proxies 3378; extraction errors 0; .
- **perf-S-exploded / Plan upper / MT off**: extraction_partial; Unset proxies 977; extraction errors 0; .
- **perf-S-exploded / Plan upper / MT on**: extraction_partial; Unset proxies 3991; extraction errors 0; .
- **perf-S-exploded / Section long overlap / MT off**: extraction_partial; Unset proxies 940; extraction errors 0; .
- **perf-S-exploded / Section long overlap / MT on**: extraction_partial; Unset proxies 3603; extraction errors 0; .
- **perf-S-exploded / Section transverse / MT off**: extraction_partial; Unset proxies 249; extraction errors 0; .
- **perf-S-exploded / Section transverse / MT on**: extraction_partial; Unset proxies 958; extraction errors 0; .
- **perf-S-exploded / SW axo / MT off**: extraction_partial; Unset proxies 55; extraction errors 0; .
- **perf-S-exploded / SW axo / MT on**: extraction_partial; Unset proxies 43; extraction errors 0; .
- **perf-XL-blocks / Section long overlap / MT off**: extraction_partial; Unset proxies 45456; extraction errors 0; .
- **perf-XL-exploded / Section long overlap / MT off**: extraction_partial; Unset proxies 44646; extraction errors 0; .
- **perf-XS-blocks / Plan level 1 / MT off**: extraction_partial; Unset proxies 111; extraction errors 0; .
- **perf-XS-blocks / Plan level 1 / MT on**: extraction_partial; Unset proxies 346; extraction errors 0; .
- **perf-XS-blocks / Plan upper / MT off**: extraction_partial; Unset proxies 117; extraction errors 0; .
- **perf-XS-blocks / Plan upper / MT on**: extraction_partial; Unset proxies 354; extraction errors 0; .
- **perf-XS-blocks / Section long overlap / MT off**: extraction_partial; Unset proxies 63; extraction errors 0; .
- **perf-XS-blocks / Section long overlap / MT on**: extraction_partial; Unset proxies 215; extraction errors 0; .
- **perf-XS-blocks / Section transverse / MT off**: extraction_partial; Unset proxies 24; extraction errors 0; .
- **perf-XS-blocks / Section transverse / MT on**: extraction_partial; Unset proxies 116; extraction errors 0; .
- **perf-XS-blocks / SW axo / MT off**: extraction_partial; Unset proxies 3; extraction errors 0; .
- **perf-XS-blocks / SW axo / MT on**: extraction_partial; Unset proxies 2; extraction errors 0; .
- **perf-XS-exploded / Plan level 1 / MT off**: extraction_partial; Unset proxies 115; extraction errors 0; .
- **perf-XS-exploded / Plan level 1 / MT on**: extraction_partial; Unset proxies 351; extraction errors 0; .
- **perf-XS-exploded / Plan upper / MT off**: extraction_partial; Unset proxies 121; extraction errors 0; .
- **perf-XS-exploded / Plan upper / MT on**: extraction_partial; Unset proxies 365; extraction errors 0; .
- **perf-XS-exploded / Section long overlap / MT off**: extraction_partial; Unset proxies 63; extraction errors 0; .
- **perf-XS-exploded / Section long overlap / MT on**: extraction_partial; Unset proxies 210; extraction errors 0; .
- **perf-XS-exploded / Section transverse / MT off**: extraction_partial; Unset proxies 24; extraction errors 0; .
- **perf-XS-exploded / Section transverse / MT on**: extraction_partial; Unset proxies 119; extraction errors 0; .
- **perf-XS-exploded / SW axo / MT off**: extraction_partial; Unset proxies 1; extraction errors 0; .
- **perf-XS-exploded / SW axo / MT on**: extraction_partial; Unset proxies 1; extraction errors 0; .
- **perf-XXL-blocks / Section long overlap / MT off**: private_memory_limit; Unset proxies unmeasured; extraction errors unmeasured; . Last phase compute; wall 396.75 s; peak 7.84 GiB.
- **perf-XXL-exploded / Section long overlap / MT off**: private_memory_limit; Unset proxies unmeasured; extraction errors unmeasured; . Last phase compute; wall 181.21 s; peak 7.84 GiB.
- **units-coords-ft / Plan cut / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **units-coords-ft / SW / MT off**: extraction_partial; Unset proxies 14; extraction errors 0; .
- **units-coords-m / Plan cut / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **units-coords-m / SW / MT off**: extraction_partial; Unset proxies 20; extraction errors 0; .
- **units-coords-mm / Plan cut / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **units-coords-mm / SW / MT off**: extraction_partial; Unset proxies 11; extraction errors 0; .
- **units-coords-offset / Plan cut / MT off**: extraction_partial; Unset proxies 23; extraction errors 0; .
- **units-coords-offset / SW / MT off**: extraction_partial; Unset proxies 24; extraction errors 0; .
- **view-sources / Depth clip / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **view-sources / Flipped clip / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **view-sources / GH plane / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **view-sources / Perspective must error / MT off**: view_error; Unset proxies unmeasured; extraction errors 0; perspective_not_supported.
- **view-sources / Plain clip / MT off**: extraction_partial; Unset proxies 25; extraction errors 0; .
- **view-sources / Vector oblique / MT off**: extraction_partial; Unset proxies 19; extraction errors 0; .

Raw HLD does not consume section styles or materials. `glass / Front clear` therefore remains opaque, like `Front opaque`; this is a known API policy gap, not an implemented clear-glass route. `section-styles` verifies stored style inputs but native HLD produces no hatch fills. `view-range / Range line` and `Range noline` pass the same far clipping geometry: HLD does not provide the product’s separate no-line suppression, Beyond or fade classes. Rectangle cuts do not certify closed material regions. These product expectations remain in sidecars for a future engine adapter.

Perspective named views produce a per-view validation error. General curved/freeform full-output truth remains unknown; piece counts alone do not establish missing/extra-locus correctness. Native threading comparisons are single trials and do not establish reproducibility.

The named-camera read-back audit exposed detached `ViewInfo.Viewport` mutations when adding a `ViewInfo` to File3dm. The generator now snapshots a standalone `RhinoViewport`, sets the target explicitly, and verifies saved projection/direction/valid target. The earlier run is retained separately in `artifacts/baseline-projection-audit/` (including one Rhino startup E_FAIL for S blocks / Plan level 1 / MT on) and is excluded from all final tables. Intermediate camera probes in `artifacts/camera-pilot/` are also excluded.

## Engine extension

Pass `--engine module_name` to select another adapter exposing `run(model, sidecar, view, multiple_threads, emit_phase) -> (result_dict, svg_text)`. The worker owns Rhino initialization and File3dm disposal; the adapter owns its drawing and any GH documents. A future M2D+ adapter can initialize headless Grasshopper and solve the component without changing generation, supervision or reporting. M0 has empty outputs, so no fabricated M2D+ comparison is included.


## Curved / freeform extension

Local-only extension, generated with `tools/models/curved.py`; seed 20261114. No plug-in contracts, CI or hook jobs change. All six feature files are below 2 MB and marked `hook: false`; performance archives and results stay gitignored.

| Model | Objects / expanded | MB | Coverage |
|---|---:|---:|---|
| curved-facade | 33 / 33 | 0.399 | Freeform curtain wall, curved mullions/transoms, Brep/mesh glass twins, ruled twisting fins, curved louvres and nine circular perforations. |
| curved-perf-L-blocks | 20,000 / 20,000 | 8.214 | 5 twisting buildings; 17 storeys; rotated curved bays and roof grids. |
| curved-perf-L-exploded | 20,000 / 20,000 | 233.288 | 5 twisting buildings; 17 storeys; rotated curved bays and roof grids. |
| curved-perf-M-blocks | 5,000 / 5,000 | 2.144 | 3 twisting buildings; 7 storeys; rotated curved bays and roof grids. |
| curved-perf-M-exploded | 5,000 / 5,000 | 58.366 | 3 twisting buildings; 7 storeys; rotated curved bays and roof grids. |
| curved-perf-S-blocks | 500 / 500 | 0.323 | 2 twisting buildings; 2 storeys; rotated curved bays and roof grids. |
| curved-perf-S-exploded | 500 / 500 | 5.856 | 2 twisting buildings; 2 storeys; rotated curved bays and roof grids. |
| curved-perf-XL-blocks | 50,000 / 50,000 | 20.354 | 9 twisting buildings; 24 storeys; rotated curved bays and roof grids. |
| curved-perf-XL-exploded | 50,000 / 50,000 | 583.258 | 9 twisting buildings; 24 storeys; rotated curved bays and roof grids. |
| curved-steel | 8 / 8 | 0.307 | Arc and 3D-spline CHS/RHS, segmented filleted I-beam, helical stringer, twisted RHS and an oblique toroidal hollow section. |
| diagrid-shell | 25 / 25 | 0.259 | Open doubly curved NURBS roof with CHS surface-isocurve families and overlapping nodes in different layer styles. |
| space-frame | 669 / 669 | 0.270 | Barrel-vault space frame: 487 hollow members and 182 spherical nodes, stored as two reusable definitions. |
| subd-vs-nurbs | 3 / 3 | 0.084 | One organic canopy as SubD, converted NURBS and a density-3 mesh; translated copies for comparison. |
| tangent-continuity | 8 / 8 | 0.105 | G1 fillets, analytic G1/G2 plane-to-polynomial joins, torus/sphere seam orientations and an exactly edge-on sheet; all four tangent/seam setting combinations. |

### Curved validation and method

Feature validation reads the native archives, checks geometry, size, hashes and edge inventories, and independently intersects the oblique CHS. The torus-plane area comes from a 65,536-sample polygon line integral of the analytic torus parametrization (not HLD): 0.7029040164 m², one region and one hole; convergence delta 3.23e-9 m² and acceptance 2e-5 m². Native Brep-plane section area is 0.7029084851 m². The perforated plate independently checks nine holes and area 9 − 9π(0.25)² m².

G1 and G2 test joins use z=0.6x² and z=0.6x³ respectively adjoining z=0. Their shared edge has matching position/tangent; only the cubic also matches curvature. Sidecars list actual Brep tangent, crease, seam and naked edge indices. The I-beam sweeps each straight/fillet profile segment separately to preserve tangent face boundaries. The roof shell is an open Brep; its cut has outline only, no hatch. The diagrid follows surface isocurves, not claimed geodesics. No node/member union is performed.

Mesh/Brep glass checks sample vertices and face centroids after inverse translation; canopy checks sample NURBS surfaces bidirectionally after explicit SubD conversion, plus mesh face centroids. These measure sampled deviation, not a global Hausdorff bound. `artifacts/models/validation.json`, `curved-pair-validation.json` and the repeat directory retain the checks.

All six feature archives and both S forms regenerated byte-for-byte (`artifacts/curved-repeat/byte-comparison.json`). The diagrid archive produced a different native in-memory JSON reserialization hash when the identical file was loaded twice; the validator retains that diagnostic and treats identical archive bytes as authoritative. All S/M/L/XL blocks/exploded pairs passed world-space geometry and camera equivalence (500/5,000/20,000/50,000 objects). The glass mesh sampled maximum deviation was 0.000406 m; canopy mesh face-centroid deviation was 0.000389 m; SubD-to-NURBS sampled deviation was 2.85e-15 m.

Verification: the forced local pre-commit hook passed a warning-free Debug build and 182 Core tests; Release solution and renderer builds passed; normal solution tests passed 182 Core tests and skipped 60 licensed Rhino cases. Ten unlicensed harness tests passed (including the area oracle, queued admission, admission timeout, and mutex handoff). Separate native generation/validation/baselines used the existing serial Rhino.Inside resolver. All original manifest entries are unchanged; 14 new model IDs include six bundled archives with matching model/sidecar hashes and hook flags off. Nothing was staged, committed, pushed, installed or published. Frozen feature sidecars come from the verified repeat generation; original baseline input sidecars remain under artifacts with their source-at-generation hashes. The later mutex handoff change alters the current common.py dependency hash, not those already-generated archive bytes. Baseline rows retain their own harness hashes.

The ten tier views are four elevations, two plan cuts, transverse and long-overlap roof sections, SW axo and a nearly tangent grazing elevation. Native trials use threading off. The final wrap-up scope is all 40 feature views, both S/M forms (20 views each), and L blocks (10 views): 90 attempts. Native batches omitted at the user’s wrap-up request: curved-perf-L-exploded, curved-perf-XL-blocks, curved-perf-XL-exploded. Their generated models, validation and shaded references remain available. Each fresh worker has a 600 s execution-wall / 8 GiB owned-job cap. After the coordination change, queue time is recorded separately and excluded from that execution cap; earlier rows include process launch in their wall measurement. Raw HLD explicitly normalizes SubD to Brep and does not implement style-aware region merging or hatch semantics.

Exact object counts truncate the final storey/bay. Roof heights and the upper plan cut follow the occupied storey count. Independent native Brep-plane intersections confirm both plan cuts hit facade panels and the roof section hits a roof member in every form/tier (24 checks). An earlier layout trial is retained separately under `artifacts/baseline/curved-layout-pilot/` and excluded from these measurements.

### Curved timings versus earlier orthogonal tiers

| Tier | Form | Completed / attempted | Partial views / Unset (completed) | Curved median s (all) | Curved common-view median s | Earlier orthogonal median s | Ratio | Max HLD s | Wall s | Peak GiB |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| S | blocks | 10/10 | 9 / 13,580 | 11.804 | 11.063 | 1.339 | 8.264 | 22.143 | 166.4 | 0.66 |
| S | exploded | 10/10 | 9 / 13,549 | 11.525 | 11.440 | 1.401 | 8.164 | 23.506 | 166.5 | 0.71 |
| M | blocks | 10/10 | 10 / 121,376 | 132.208 | 104.204 | 11.245 | 9.267 | 309.774 | 1554.3 | 3.26 |
| M | exploded | 10/10 | 9 / 121,212 | 128.710 | 127.736 | 11.038 | 11.572 | 358.447 | 1692.6 | 4.34 |
| L | blocks | 2/10 | 2 / 99,461 | 227.144 | 227.144 | 51.677 | — | 310.864 | 5112.1 | 7.84 |

Capped attempts (no completed drawing is implied):

| Model | View | Limit reached | Last phase | Execution wall s |
|---|---|---|---|---:|
| curved-perf-L-blocks | East elevation | wall_timeout | compute | 600.1 |
| curved-perf-L-blocks | Grazing | wall_timeout | compute | 600.1 |
| curved-perf-L-blocks | North elevation | wall_timeout | compute | 600.2 |
| curved-perf-L-blocks | Section long overlap | wall_timeout | compute | 600.1 |
| curved-perf-L-blocks | Section transverse | wall_timeout | compute | 600.2 |
| curved-perf-L-blocks | South elevation | private_memory_limit | compute | 539.6 |
| curved-perf-L-blocks | SW axo | wall_timeout | compute | 600.2 |
| curved-perf-L-blocks | West elevation | private_memory_limit | compute | 473.3 |

Earlier orthogonal rows are preserved from PR #6, not rerun here. Common-view medians exclude the added grazing view; these scenes differ and the ratios are descriptive, not a controlled curvature-only experiment. Ratios are suppressed unless all nine common views complete (one for XL). XL comparisons, when present, use one section per form. Capped attempts remain in denominators and are excluded from medians; partial extraction counts as completed compute but not certified geometry.

### Shared research timing qualification

Indicative timings. A shared research session is identified; simultaneous non-mutex CPU work was not instrumented. Earlier runs are not certified machine-quiescent. The user reported S3 performance research waiting on the shared mutex during the M batch. The active solve finished normally; the old controller stopped at a pre-Rhino gate. The revised runner queues before Rhino initialization, releases after each view, and leaves a 30-second mutex-free handoff window. Its 3,600-second admission timeout is separate from the 600-second execution cap. No other owner’s process was attached to or terminated.

The identified shared-session views below accumulated 4170.1 s of measured admission time. Per-result `coordination`, `admissionSeconds` and `elapsedSeconds` retain the evidence; execution wall and HLD compute exclude the queue and handoff. A shared session does not establish simultaneous Rhino solves: cooperating work is serialized by the mutex. Earlier trials lack concurrency instrumentation and must also be treated as indicative.

| Model | Shared-session views | Recorded queue s |
|---|---|---:|
| curved-perf-L-blocks | East elevation, Grazing, North elevation, Plan level 1, Plan upper, Section long overlap, Section transverse, South elevation, SW axo, West elevation | 2612.4 |
| curved-perf-M-blocks | Grazing | not recorded |
| curved-perf-M-exploded | East elevation, Grazing, North elevation, Plan level 1, Plan upper, Section long overlap, Section transverse, South elevation, SW axo, West elevation | 1557.7 (some views unrecorded) |

### Curved feature view measurements

| Model | View | HLD s | Unset | Visible / hidden / cut | Status |
|---|---|---:|---:|---|---|
| curved-facade | Front | 0.966 | 3 | 582 / 218 / 0 | extraction_partial |
| curved-facade | Grazing | 0.772 | 1 | 286 / 472 / 0 | extraction_partial |
| curved-facade | Plan cut | 0.566 | 0 | 106 / 81 / 20 | ok |
| curved-facade | SW | 0.900 | 3 | 566 / 310 / 0 | extraction_partial |
| curved-facade | Top | 0.872 | 0 | 447 / 111 / 0 | ok |
| curved-steel | Front | 0.434 | 0 | 139 / 61 / 0 | ok |
| curved-steel | Grazing | 0.515 | 0 | 185 / 78 / 0 | ok |
| curved-steel | Oblique hollow cut | 0.370 | 14 | 4 / 2 / 5 | extraction_partial |
| curved-steel | Plan cut | 0.360 | 0 | 56 / 40 / 8 | ok |
| curved-steel | SW | 0.489 | 0 | 184 / 60 / 0 | ok |
| curved-steel | Top | 0.465 | 0 | 108 / 57 / 0 | ok |
| diagrid-shell | Front | 1.538 | 0 | 169 / 142 / 0 | ok |
| diagrid-shell | Grazing | 1.640 | 0 | 193 / 161 / 0 | ok |
| diagrid-shell | Plan cut | 0.783 | 242 | 94 / 80 / 32 | extraction_partial |
| diagrid-shell | Roof section | 1.401 | 546 | 123 / 115 / 27 | extraction_partial |
| diagrid-shell | SW | 1.473 | 1 | 147 / 173 / 0 | extraction_partial |
| diagrid-shell | Top | 1.301 | 0 | 166 / 179 / 0 | ok |
| space-frame | Front | 7.817 | 0 | 251 / 762 / 0 | ok |
| space-frame | Grazing | 69.160 | 0 | 1381 / 9115 / 0 | ok |
| space-frame | Plan cut | 16.625 | 4163 | 685 / 1586 / 264 | extraction_partial |
| space-frame | SW | 51.319 | 0 | 2544 / 9366 / 0 | extraction_partial |
| space-frame | Top | 26.392 | 0 | 2037 / 6744 / 0 | ok |
| subd-vs-nurbs | Front | 0.221 | 0 | 151 / 0 / 0 | ok |
| subd-vs-nurbs | Grazing | 0.343 | 0 | 79 / 100 / 0 | ok |
| subd-vs-nurbs | Plan cut | 0.261 | 0 | 97 / 0 / 17 | ok |
| subd-vs-nurbs | SW | 0.180 | 0 | 151 / 0 / 0 | ok |
| subd-vs-nurbs | Top | 0.207 | 0 | 151 / 0 / 0 | ok |
| tangent-continuity | Front tangents off seams off | 0.378 | 0 | 26 / 5 / 0 | ok |
| tangent-continuity | Front tangents off seams on | 0.412 | 0 | 26 / 5 / 0 | ok |
| tangent-continuity | Front tangents on seams off | 0.414 | 0 | 38 / 5 / 0 | ok |
| tangent-continuity | Front tangents on seams on | 0.411 | 0 | 38 / 5 / 0 | ok |
| tangent-continuity | Grazing | 0.506 | 0 | 38 / 16 / 0 | ok |
| tangent-continuity | SW tangents off seams off | 0.467 | 0 | 40 / 2 / 0 | ok |
| tangent-continuity | SW tangents off seams on | 0.437 | 0 | 40 / 2 / 0 | ok |
| tangent-continuity | SW tangents on seams off | 0.464 | 0 | 72 / 32 / 0 | ok |
| tangent-continuity | SW tangents on seams on | 0.457 | 0 | 72 / 32 / 0 | ok |
| tangent-continuity | Top tangents off seams off | 0.418 | 0 | 34 / 1 / 0 | ok |
| tangent-continuity | Top tangents off seams on | 0.416 | 0 | 34 / 1 / 0 | ok |
| tangent-continuity | Top tangents on seams off | 0.426 | 0 | 48 / 1 / 0 | ok |
| tangent-continuity | Top tangents on seams on | 0.412 | 0 | 48 / 1 / 0 | ok |

### Curved inspection findings

- **curved-facade / SW**: Native reports 3 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **curved-facade / Front**: Native reports 3 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **curved-facade / Grazing**: Native reports 1 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **diagrid-shell / SW**: Native reports 1 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **diagrid-shell / Plan cut**: Native reports 242 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **diagrid-shell / Roof section**: Native reports 546 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **space-frame / Plan cut**: Native reports 4,163 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **curved-steel / Oblique hollow cut**: Native reports 14 Unset/parentless proxies; these are omitted safely and extraction is partial. This is not a measured count of missing visible silhouettes.
- **tangent-continuity / Front, SW and Top (both tangent settings)**: Turning seams on changes no extracted SVG paths or piece counts in all six matched comparisons, although archived torus/sphere seam trims exist and IncludeTangentSeams is recorded true. The expected seam display is absent in these native outputs; a native seam-setting limitation remains to investigate.
- **tangent-continuity / Front tangents off/on, seams off**: Tangent toggle adds the visible fillet boundaries: visible pieces change from 26 to 38. No Unset proxies. The edge-on sheet projects as a line, not an area.
- **curved-steel / Oblique hollow cut**: Visual inspection retains the outer and inner non-circular cut loops. The independent area/hole check passes; raw HLD supplies curves, not a filled region or topology certificate.
- **curved-perf-L-blocks / Plan upper**: One of the largest completed-tier diagnostics: 88,288 Unset/parentless proxies. Omitted safely; partial extraction. This count does not establish how many visible silhouettes are missing.
- **curved-perf-M-blocks / Section long overlap**: One of the largest completed-tier diagnostics: 62,898 Unset/parentless proxies. Omitted safely; partial extraction. This count does not establish how many visible silhouettes are missing.
- **curved-perf-M-exploded / Section long overlap**: One of the largest completed-tier diagnostics: 62,490 Unset/parentless proxies. Omitted safely; partial extraction. This count does not establish how many visible silhouettes are missing.
- **curved-facade / SW**: Visible silhouette/edge loss: the two exposed swept louvre bands are present in the shaded reference, but native output has mainly their end-cap edges and one dashed longitudinal arc per member. Long visible band boundaries are absent. Each louvre has eight Visible and one Hidden native pieces. These are valid closed sweeps; the long faces retain the profile's internal kinks. This is an observed native-output limitation on this representation, not a mesh-render faceting claim.
- **space-frame / SW**: One hidden segment from CHS-0475/CHS-unit/member-0 cannot be tessellated by Curve.ToPolyline for SVG export. The result is extraction_partial with zero Unset proxies; this is a display-extraction failure, not proof that HLD failed to compute the curve.
- **subd-vs-nurbs / Grazing and SW**: The three canopy representations remain visible; all five views have zero Unset proxies. The mesh is an intentional approximation. No additional obvious silhouette loss was confirmed in these inspected views; this is not a complete correctness certificate.
- **curved-perf-M-exploded / curved-perf-L-blocks / SW axo / Plan upper**: Inspected the final PNGs: the three twisting M towers and five freeform L plan rings remain recognizable. The L upper-plan cut removes the roof by construction; its absence compared with the unclipped shaded Top reference is expected. Dense overlap and partial extraction remain; no additional specific visibility defect was certified from these overview images.

The **Curved** section of `artifacts/baseline/index.html` embeds shaded SW/Front feature references, SW/Front/Top tier references, previews of all completed native drawings and the comparison chart. Drawings over 1 MB use compact PNG previews with links to full native SVGs beside the HTML; retain that folder when sharing the gallery. Shaded images are explicitly the offscreen CPU mesh fallback. Native SVG curves are tessellated only for display. The screenshot list is `artifacts/models/screenshots/curved-manifest.json`.

<!-- VIEW CASES START -->
## View cases (5–8)

Local-only extension at `origin/main` 80981d7 (PRs #1–#14). Generators: `tools/models/view_cases.py`; runner adapter: `view_engine.py` + `tools/render/ViewCaseRunner.cs`; managed-only audits/report: `view_audit.py` / `view_report.py`. No CI, installation, commit or push. Large archives and every drawing remain gitignored. Only the 20 JSON view definitions and expectations are frozen in `tests/fixtures/view-cases/`, with manifest hashes and `hook: false`; all generated geometry is ignored.

| Model | Objects | Views |
|---|---:|---:|
| view-curved-facade | 33 | 18 |
| view-curved-perf-M-blocks | 5000 | 49 |
| view-curved-perf-S-blocks | 500 | 49 |
| view-curved-steel | 8 | 43 |
| view-diagrid-shell | 25 | 69 |
| view-grid-curved-perf-M-blocks-00 | 5000 | 12 |
| view-grid-curved-perf-M-blocks-17 | 5000 | 12 |
| view-grid-curved-perf-M-blocks-45 | 5000 | 12 |
| view-grid-curved-perf-S-blocks-00 | 500 | 12 |
| view-grid-curved-perf-S-blocks-17 | 500 | 12 |
| view-grid-curved-perf-S-blocks-45 | 500 | 12 |
| view-grid-perf-M-blocks-00 | 5000 | 12 |
| view-grid-perf-M-blocks-17 | 5000 | 12 |
| view-grid-perf-M-blocks-45 | 5000 | 12 |
| view-grid-perf-S-blocks-00 | 500 | 12 |
| view-grid-perf-S-blocks-17 | 500 | 12 |
| view-grid-perf-S-blocks-45 | 500 | 12 |
| view-planar-control | 2 | 2 |
| view-round-tower | 29 | 26 |
| view-space-frame | 669 | 23 |

Case 5 uses the original blocks-form S/M orthogonal and curved geometry, rotated rigidly about world Z by 17° and 45°. Four unrotated control archives supply matching views. Each has four grid elevations, two grid sections, two plan levels and SW/SE/NE/NW axos. Coordinates are compared in co-rotated frames; no scale fitting or best-fit deformation is allowed. The model and view rotations should cancel.

Cases 6–7 cover five interior normalized-parameter stations (.125, .25, .5, .75, .875) per selected rail/profile family. Steel includes arc/spline CHS and RHS, the filleted I, twisted RHS, helical stringer and large curved CHS. All eight distinct diagrid isocurves are covered. Repeated buildings use the first instance of each curved mullion/transom/roof-diagonal prototype; the existing space frame has straight CHS chords, so four connection families are explicitly labeled as straight. This is archetype coverage, not five cuts of every repeated instance. Facade views use five plan-guide stations; tilted roof planes are 15°, 30°, 60° from vertical. Plan guides are not claimed exact parametrizations of the doubly curved glass.

Case 8 uses twelve diametral planes at 15° increments, each looking both ways: 24 distinct directions over 360°, plus two reference plans. The new round tower has an annular core, four annular slabs, twelve columns and twelve curved curtain-wall sectors, with a 30° rotational period. Diagrid and curved tiers are not rotationally periodic, so their radial series has no fabricated symmetry pass. For multi-building tiers the centre axis is the first tower at world X=Y=0; the complete surrounding scene remains present.

Every sidecar stores GH Plane, Rectangle and Box values and the GUID/front/back of a persisted Extrusion view box. The bridge reads the saved object through `ViewRequest.Parse(Guid, document)`, the same resolver called by the component, and checks normal/up, front plane, crop and depth against all three GH value forms. `view_aux.py component` additionally solves six representative planes through the real registered GH component, with the saved box GUID and document-mode geometry; it checks nine outputs and aligned Cut/Hatch branch paths. This does not claim 423 full GH UI solves.

Raw native HLD runs first with native clipping, MT off; main `NativeDrawing` runs route C with physical trim/cap and the stability certificate. Both see the complete same scene, excluding view objects. Native evidence is checkpointed before route C, so an engine timeout cannot erase the completed baseline. The owned-process cap is 600 seconds / 8 GiB for features/S; after the first curved-M timeout the owner requested 120 seconds for remaining M views, with S/features prioritized. The already active second M attempt retained its original cap. Admission is excluded; normal priority and a five-second mutex-free handoff. Queued auxiliary work uses the same slot. Timings are indicative shared-machine measurements, not quiescent benchmarks.

The exact planar kernel runs only for a wholly eligible scene. Even the “orthogonal” building tiers contain CHS, curved fins and SubD: they are explicitly ineligible, not silently tested as a planar subset. Two small oblique planar control views run the production exact kernel against its independent rational oracle and raw native HLD.

Analytic normal-profile expectations are CHS π(R²−r²), two loops; RHS .4×.6−.3×.5 = .09 m², two loops; filleted I .44−(4−π)×.04² m², one loop. The twisted loft is sampled at its interpolation stations with the prescribed profile rotation. The helical sweep has no assumed exact profile area. Engine region area/loops and independently reassembled output-polyline area/containment are retained separately; RHS output bounds are checked in its co-rotated frame. No production intersection routine supplies the expected area.

The native profile audit uses visible section ink: raw HLD supplies no region objects. A failed loop reconstruction can therefore reflect hidden/absent cut edges rather than an incorrect intersection area. The main engine region-area oracle is reported separately.

Locus audits union coincident support-line intervals with Unresolved > Visible/Cut > Hidden, then sample both directions at 0.05 m (minimum eight subdivisions per segment), at four times model tolerance to cover display-chord approximation. Missing ink, Unresolved/confident differences and confident Visible/Hidden swaps are counted separately. These are sampled locus checks, not a continuous Hausdorff certificate. Collector failures are retained and make the affected metamorphic pair incomplete, never a pass. When Rhino tessellation fails on a valid curve, the collector subdivides positive-weight rational Bézier spans until their control hull bounds chord error by model tolerance. Analytic-circle and independently evaluated rational-cubic tests validate the implementation. Zero-extent returned curves retain their point coordinates and diagnostics; point loci do not contribute to the common line-locus union. Small closed loops are preserved. Original collector failures and retry history remain in `artifacts/view-retries/` and `view-cases/retries.json`. Native Duplicate/Unset counts flag possible quirks; they never automatically excuse a disagreement. Native ordering attribution needs an explicit rerun.

Current measured inventory (capped/incomplete views remain in the denominator):

```json
{
  "models": 20,
  "views": 423,
  "cases": {
    "6": 140,
    "7": 33,
    "8": 104,
    "5": 146
  },
  "status": {
    "complete": 263,
    "wall_timeout": 2,
    "skipped_owner_decision": 158
  },
  "sectionOracles": {
    "pass": 68,
    "fail": 73,
    "unknown": 122,
    "incomplete": 160
  },
  "nativeCutProfileAudits": {
    "pass": 89,
    "fail": 52,
    "unknown": 122,
    "incomplete": 160
  },
  "m2dOutputProfileAudits": {
    "pass": 68,
    "fail": 73,
    "unknown": 122,
    "incomplete": 160
  },
  "m2dUnresolvedPieces": 79902,
  "boundedTessellationFallbacks": {
    "native": 17,
    "m2d": 4
  },
  "zeroExtentFallbackPoints": {
    "native": 1,
    "m2d": 2
  },
  "locusCollectorIncompleteViews": 0,
  "disagreementViewsByClass": {
    "missingInkSamples": 214,
    "uncertaintyClassSamples": 177,
    "confidentVisibleHiddenSwapSamples": 216
  },
  "exactPlanar": {
    "pass": 2
  },
  "metamorphic": {
    "native/rotation/incomplete": 48,
    "m2d/rotation/incomplete": 48,
    "native/rotation/fail": 35,
    "m2d/rotation/fail": 38,
    "native/rotation/pass": 13,
    "m2d/rotation/pass": 10,
    "native/radial periodicity/fail": 16,
    "m2d/radial periodicity/pass": 13,
    "native/radial periodicity/pass": 6,
    "m2d/radial periodicity/fail": 9
  },
  "completedLocusDisagreements": 250
}
```

Per-view timings, native diagnostics, M2D+ Unresolved counts, analytic region verdicts, canonical locus audits and metamorphic results are linked from `artifacts/baseline/index.html` → **View cases**. Full JSONs and M2D+ SVG/PNG renders are in `artifacts/baseline/view-cases/`. Shaded SW/Front references use the labeled offscreen CPU mesh fallback at `artifacts/view-models/screenshots/`; no existing Rhino process is used.

### View cases: observed failures

**Execution stopped by owner decision.** The active curved-S roof-diagonal station 5 finished; all subsequent cases were skipped. No optional M probes were started. Normal-priority native jobs ran serially, releasing the shared Rhino slot between views; only this task's owned controllers/jobs were stopped. A queued worker exited before Rhino admission and is counted as skipped, not an engine crash; its original admission/watch logs remain.

**Inventory:** 20 models, 423 defined views; 263 complete, 2 timeouts, 158 owner-skipped. Twelve grid archives (eight rotated and four controls), six curved/feature extensions, a round tower and a two-box planar control. Cases 5/6/7/8 contain 146/140/33/104 views. The review set contains only 20 JSON view-definition/expectation fixtures; all geometry, screenshots, timings and output loci remain ignored artifacts.

**Severity order and causal attribution.** Reproducers below are the smallest existing suite scenes exhibiting each class (two-box control: 2 objects; steel: 8; diagrid: 25; tower: 29). They are not claimed to be delta-minimized. Native HLD is a comparator, not the truth oracle. Route C uses native HLD after physical clipping; the exact planar kernel is separate.

| Rank / class | Small reproducer(s) | Responsible engine and consequence |
|---|---|---|
| 1. Wrong-Visible, independently confirmed | [view-planar-control / Planar oblique 45](../../artifacts/baseline/view-cases/view-planar-control__Planar-oblique-45__off__view_engine.analytic-rays.json) | **Native HLD**, inherited unchanged by route C. At world (1,-0.5,0) and (1,-0.5,1), the frontmost cross-box edge is inside the opaque box; the viewing ray hits its foreground face at (1.5,-1,z). Both engines output Visible. Exact planar kernel and independent box-ray oracle say Hidden. Fix or quarantine this before trusting confident native visibility. |
| 2. False-Hidden / missing visible ink | [view-planar-control / Planar oblique 17](../../artifacts/baseline/view-cases/view-planar-control__Planar-oblique-17__off__view_engine.analytic-rays.json) | **Native HLD**, inherited by route C. Rays from (1,-1.5,0/1) immediately leave the cross-box and move away from the other box, yet the edges are Hidden. Exact kernel says Visible. Reversing the two input objects preserves this failure; it is not explained by input order in this control. |
| 3. Certified crossing fails physical trim | [view-curved-steel / Normal RHS-arc station 3](../../artifacts/baseline/view-cases/view-curved-steel__Normal-RHS-arc-station-3__off__view_engine.json) (also RHS-arc station 2) | **Route C sections**: certificate D succeeds, then “Certified crossing produced no trimmed geometry.” Expected 0.09 m² / 2 loops; emitted 0 / 0. Raw visible cut-profile audit passes at station 3. Missing region/ink; 2 Unresolved pieces are conservative, not wrong-Visible. |
| 4. Section-loop certificate rejection | [view-curved-steel / Normal I-beam-filleted-web-flanges station 3](../../artifacts/baseline/view-cases/view-curved-steel__Normal-I-beam-filleted-web-flanges-station-3__off__view_engine.json); [view-curved-facade / Normal curved-mullion-2 station 2](../../artifacts/baseline/view-cases/view-curved-facade__Normal-curved-mullion-2-station-2__off__view_engine.json) | **Route C sections** reject self-intersecting/degenerate loops and withhold the region. I profile expects 0.4386265482 m² / 1 loop; mullion expects 0.0025132741 m² / 2 loops. Both emit 0 / 0. Missing section output, not a measured wrong area on an emitted region. I case has 0 Unresolved pieces despite an explicit uncertainty diagnostic; mullion has 135. Whether rejection is over-conservative requires isolating the intersection loop. |
| 5. Source self-intersection guard | [view-diagrid-shell / Normal diagrid-iso-0-0 station 1](../../artifacts/baseline/view-cases/view-diagrid-shell__Normal-diagrid-iso-0-0-station-1__off__view_engine.json) (also station 2) | **Route C validation** reports self-intersecting Brep faces away from shared topology and excludes the source. Missing region; not evidence that its guard is incorrect. The generator's nominal CHS area remains an expectation, but a self-intersecting sweep is outside the certified solid domain. Review source construction and validator together before relaxing this guard. |
| 6. Region removed by uncertainty propagation | [view-curved-steel / Normal twisted-RHS station 4](../../artifacts/baseline/view-cases/view-curved-steel__Normal-twisted-RHS-station-4__off__view_engine.json); [view-diagrid-shell / Normal diagrid-iso-1-0 station 3](../../artifacts/baseline/view-cases/view-diagrid-shell__Normal-diagrid-iso-1-0-station-3__off__view_engine.json) | **Native Unset → route C quarantine** in twisted RHS (2 invalid native segments, 53 Unresolved); **route C scene-footprint quarantine** in diagrid (other sources rejected). Target certificate succeeds, but final region is withheld. Conservative Unresolved is harmless for visibility truth, yet the missing region is a real completeness limitation. Do not “fix” it by relabeling Visible. |
| 7. Remaining orientation / periodicity instability | [view-round-tower / Radial 030 forward](../../artifacts/baseline/view-cases/view-round-tower__Radial-030-forward__off__view_engine.json); [view-grid-curved-perf-S-blocks-17 / South elevation](../../artifacts/baseline/view-cases/view-grid-curved-perf-S-blocks-17__South-elevation__off__view_engine.json) | **Native and route C paths**, not the exact planar kernel. Equivalent-angle outputs differ; raw native also fails metamorphic pairs. Missing ink, confidence changes and confident class swaps are distinct evidence classes. Except for ranks 1–2, this audit cannot tell which confident side is correct or whether Duplicate/order/clipping caused it. Treat unresolved confident swaps as high-priority triage, not as proved wrong-Visible counts. |
| 8. Section performance | [view-grid-curved-perf-S-blocks-00 / West elevation](../../artifacts/baseline/view-cases/view-grid-curved-perf-S-blocks-00__West-elevation__off__view_engine.json); [view-curved-perf-M-blocks / Normal b00-f00-bay00-mullion-000002 station 1](../../artifacts/baseline/view-cases/view-curved-perf-M-blocks__Normal-b00-f00-bay00-mullion-000002-station-1__off__view_engine.json) | **Route C sections**: curved-S West takes 112.105 s, first HLD observed at 101.002 s (capture 0.126 s); much time precedes HLD. M mullion stations 1/2 hit 600.090/600.080 s execution caps in phase `m2d`. They predate the phase probe, so no finer attribution is available. Remaining M cap is 120 s, but those runs were skipped. |

The exact planar kernel passed **2/2** independent rational-oracle controls, with no Unresolved output. No exact-kernel failure was found in this run; this says nothing about ineligible curved scenes. Duplicate records alone do not prove missing ink, and Unset evidence is separated from benign empty/zero-length slots. The 17° order reversal tested both orders of the two-object control; no general order-independence claim is made.

**Why the section oracle reported 69 fails.** Those were completed attempts with a numeric analytic area/loop expectation. Every failed target emitted zero material regions (area 0, loops 0), rather than a subtly wrong area. This end-to-end completeness oracle deliberately counts safety withholding as a failure to deliver the expected section; it does not classify every such failure as an unsafe geometry algorithm.

| Cause of zero target region | Owner snapshot (69) | Final |
|---|---:|---:|
| certified trim failed | 3 | 3 |
| section-loop certificate rejected | 37 | 40 |
| source self-intersection guard | 14 | 15 |
| native Unset propagated to region | 1 | 1 |
| region withheld by scene uncertainty | 14 | 14 |

The last class is a code-supported inference from successful target certificates, other source uncertainty, and the final region-removal rule in `src/M2DPlus.Rhino/Services/SectionDrawing.cs`; the saved result does not trace every individual region-removal decision. Native Unset on twisted RHS is directly logged. No additional native isolation runs were made after the owner stopped the matrix.

**Why 167 were incomplete.** At the quoted snapshot, 164 views had not run, one was starting, and two M views timed out: 165 without a completed solve plus 2 caps. These are not 167 section-kernel failures. The 121 “unknown” rows were completed views without an independent numeric area/loop oracle (e.g. elevations, axos, freeform/tilted cuts); unknown is not pass. After the final active solve, section results are {"fail": 73, "incomplete": 160, "pass": 68, "unknown": 122}. Incomplete now consists of owner-skipped cases and the two timeouts. Native visible-cut profile reconstruction is a separate audit: HLD exposes no region objects, and hidden cut edges can prevent a closed-loop reconstruction.

**Locus evidence:** 250 completed views disagree; 216 have confident Visible/Hidden swaps, 214 missing ink, 177 uncertainty-class differences. Classes overlap; these counts must not be added. Total reported M2D+ Unresolved pieces: 79902; collector-incomplete views: 0. Comparisons use canonical support-line unions and bidirectional samples (0.05 m spacing, at least 8 per segment, 4× model tolerance). They are not a continuous-locus proof. The report retains per-view timing, region/loop, Unresolved and mismatch evidence in the local [gallery](../../artifacts/baseline/index.html).

**Metamorphics:** for 48 completed S rotation pairs per engine, main M2D+ is 10 pass / 38 fail, native is 13 / 35; the 48 M pairs per engine remain incomplete. For 22 equivalent-angle tower pairs, M2D+ is 13 / 9 and native 6 / 16. These are classed-locus verdicts after co-rotating the view, with no best-fit scale/deformation. Geometry-only and confidence-only details remain in `metamorphic.json` and each pair's audit.

**Review and reproduction:** unchanged engine from `origin/main` 80981d7; generated definitions cover all 423 views. Use the exact model/view names above with `tools/models/run.py --select ... --views ... --engine view_engine --threads off`; any future M probe must use `--wall 120`. The local stop receipt blocks deferred queues. No CI jobs, installs, commits or pushes were made. Shaded references cover SW + Front for every model; six real GH component/view-box checks passed. Definitions-only fixtures and manual unlicensed harness checks accompany the generators.

**Validation:** Debug and Release builds pass with zero warnings; 201 Core and 40 GH UI tests pass; forced local hook passes. Manual checks: 11 view-contract/oracle/collector tests and 10 harness tests pass. No further licensed runs were made during wrap-up.

### View cases: performance timeouts

Every capped attempt is a finding for the next section-performance work, not a correctness verdict. `elapsed` is execution wall time after admission, including startup/capture and any completed native baseline. The phase identifies the engine active when the owned job was stopped. Later workers sample the managed native-call counter once per second: `m2d/pre-native` means route C had not yet called HLD; `m2d/native-or-extraction` means that call had started. This probe touches no live geometry or document and does not change the engine. The first two M attempts predate the probe. Completed native timing remains in the per-view JSON.

| Model / view | Engine | Phase reached | Elapsed s |
|---|---|---|---:|
| view-curved-perf-M-blocks / Normal b00-f00-bay00-mullion-000002 station 1 | M2D+ route C | m2d | 600.090 |
| view-curved-perf-M-blocks / Normal b00-f00-bay00-mullion-000002 station 2 | M2D+ route C | m2d | 600.080 |

Largest observed delays before the first M2D+ HLD call (one-second sampling, includes capture):

| Model / view | Before first HLD, s | Capture s | Engine total s |
|---|---:|---:|---:|
| view-grid-curved-perf-S-blocks-17 / West elevation | 105.013 | 0.116 | 116.753 |
| view-grid-curved-perf-S-blocks-45 / SE axo | 105.009 | 0.136 | 125.893 |
| view-grid-curved-perf-S-blocks-00 / NW axo | 105.005 | 0.105 | 125.825 |
| view-grid-curved-perf-S-blocks-45 / East elevation | 104.013 | 0.108 | 114.041 |
| view-grid-curved-perf-S-blocks-45 / North elevation | 103.014 | 0.128 | 119.074 |
| view-grid-curved-perf-S-blocks-00 / West elevation | 103.013 | 0.125 | 114.240 |
| view-grid-curved-perf-S-blocks-00 / East elevation | 103.013 | 0.164 | 113.591 |
| view-grid-curved-perf-S-blocks-00 / Section transverse | 103.008 | 0.138 | 107.277 |
| view-grid-curved-perf-S-blocks-45 / NW axo | 103.003 | 0.105 | 123.582 |
| view-grid-curved-perf-S-blocks-17 / Section long overlap | 103.003 | 0.121 | 112.667 |
