# Frame2D MVP audit for M2D+

Research date: 2026-11-14. No product implementation, installation, or commits were made. The original workspace was read only. All experiments and copied code are under [the research scratch folder](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114). This audit covers every production C# file in Frame2D.Core and Frame2D.Grasshopper; diagnostic scripts were inspected for their harness, scoring and oracle contracts. The larger historical diagnostic corpus was not independently re-certified.

## Verdict

Keep the ownership/lifecycle discipline, independent placement contract, native adapter, conservative culling/indexing ideas, and the verified-convex interval solver **as a restricted specialist**. Rewrite the scene model, normalization, section-region pipeline, caching boundaries, engine selection/quality contract, and GH-facing API. Do not port the existing three-component UX.

The MVP is a useful geometry experiment, not a general replacement for Make2D. Auto is a deterministic routing heuristic, not a native-output correctness detector. The general Custom path can return `ok` with duplicates, and can return unresolved on very simple curved/mesh fixtures. New measured evidence is in [geometry-matrix.md](geometry-matrix.md) and [performance.md](performance.md).

## Identity and source scope

The copied, tested DLLs match the handoff's final release, without rebuilding:

| Artifact | SHA-256 |
|---|---|
| Frame2D.Core.dll | `a4433126988cd51b9e779e0894b9c2b9aea7c22ad2de533e148de83c9e9ebc26` |
| Frame2D.gha | `d819af0565e12bed6fc1d5404667b1e036bb7493981fb0643735df6a51d058cd` |

Important migration fact: the MVP **targets net48**, despite being successfully loaded by the existing Rhino.Inside harness into Rhino 8's .NET 8 runtime. Its Core directly references RhinoCommon. It is not the owner's requested pure, Rhino-free .NET 8 Core. See [Directory.Build.props](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Directory.Build.props:1) lines 1–16 and [Core project](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/Frame2D.Core.csproj:1).

All file/line citations below point to the task-local copy, so later edits to the old live workspace cannot change the cited code. `source/mvp` in the handoff is the portable snapshot; the live `mvp` supplied the matching final binaries and was copied for testing.

## Actual architecture

1. GH reads all interacting sources, retains tree paths/reference-derived IDs, hashes them, and duplicates geometry on the solution thread.
2. `DepthProcessor.Prepare` converts surfaces/extrusions, makes owned original and local copies, rejects whole objects outside conservative bounds, intersects the section plane, and physically trims/caps retained geometry.
3. `DrawingEngine` selects Native, Custom, or a native baseline plus selected custom repair.
4. Native HLD or Custom returns owned world and flattened curves. Output is clipped again; artificial clipping features are suppressed by source-boundary checks. Independently collected Cut curves are appended.
5. GH retains a single drawing result. Layout transforms and depth-band splitting use that result without invoking visibility. Output construction makes independently mutable GH curve copies and string metadata; bake adds objects and style layers.

The boundaries are sensible, but the scene is rebuilt for each solve/view. There is no definition/instance graph, shared immutable prepared scene, durable section-region representation, incremental dependency graph, or general cross-view feature cache.

## Parts worth retaining, with limits

| Part | Evidence in source | Reuse recommendation |
|---|---|---|
| Explicit view/depth convention | [Models.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/Models.cs:8), lines 8–48; [ViewCapture.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/ViewCapture.cs:21), lines 21–55 | Keep right-handed view plane, depth along negative Z, finite range validation, off-axis viewport frame capture, explicit rejection of perspective. |
| Owned curves and source provenance | [Models.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/Models.cs:54), lines 54–98 | Preserve intent; replace mutable public DTOs with clear scene/result ownership and typed status/provenance. Current metadata has no source layer, definition path or section style. |
| Native HLD adapter | [NativeEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/NativeEngine.cs:11), lines 11–100 | Good thin starting point: cancellation, explicit tangent/seam policy, owned exact NURBS extraction, source tags, native disposal, phase timings. Add native Points extraction and native clipping/section experiments before carrying over physical trimming. |
| Convex analytic kernel | [CustomEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/CustomEngine.cs:410), lines 410–447 and 674–801 | Keep behind a strict eligibility contract, with independent interval tests. It verifies planar, manifold, closed convex Breps and solves linear candidates using halfspaces. No claim for hollow/freeform geometry. |
| Exact curve shape during clipping/styling | [DepthProcessor.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DepthProcessor.cs:324), lines 324–385; [LayoutEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/LayoutEngine.cs:143), lines 143–221 | Keep rational-curve trimming and exact depth-plane event splitting; finite trim failures are not silently discarded. |
| Fixed-frame independent placement | [LayoutEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/LayoutEngine.cs:14), lines 14–136 | Keep the mathematical separation and target-plane transform. Docking/UV anchors do not need to become public M2D+ inputs. |
| Conservative culling | [DepthProcessor.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DepthProcessor.cs:46), lines 46–63; [CustomEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/CustomEngine.cs:187), lines 187–232 | Keep rejection only when provably disjoint. Selected-repair culling preserves potentially relevant foreign occluders. |
| Boundary acceleration | [DepthScene.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DepthScene.cs:164), lines 164–281 | Keep lazy source-owned RTree indexing and exhaustive fallback. The underlying five-sample curve-support predicate, lines 149–161, is still heuristic. |
| Bounded preparation cache discipline | [ScenePreparationCache.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/ScenePreparationCache.cs:62), lines 62–167 | Keep witnesses, GeometryEquals verification, copies for consumers, generation invalidation, byte/entry bounds. Redesign keys and ownership around definition-local geometry. |
| Async stale-result control | [DrawingComponent.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Grasshopper/DrawingComponent.cs:120), lines 120–202; [Goo.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Grasshopper/Goo.cs:47), lines 47–85 | Keep UI-thread snapshots, cancellation generations, single active worker per component, current-key-only publication and drawing leases. Finalizer-dependent release and mutable exposed ScriptVariable need a fresh ownership review. |
| GH output ownership | [LayoutOutputBuilder.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Grasshopper/LayoutOutputBuilder.cs:18), lines 18–77 | Keep independent Visible/Depth curve copies and reused path branches. Avoid forcing all detailed metadata/duplicate output trees when they are not requested in a new API. |

## What Native, Custom and Auto really mean

### Native in the MVP

`DrawingEngine` first validates only Curve, Brep, Surface, Extrusion and Mesh ([DrawingEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DrawingEngine.cs:40), lines 40–46). Thus the MVP's Native mode is **narrower than RhinoCommon HLD**. Points are rejected before HLD; raw HLD has a separate Points collection. SubD, InstanceReferenceGeometry, TextEntity and TextDot are rejected too. Lines/polylines/circles/arcs must reach this boundary as Curve geometry, not bare value types.

The adapter uses native Brep/mesh/surface candidate and visibility generation, includes hidden curves, disables tangent edges/seams, skips `Duplicate` and `Projecting` segments, and maps other unexpected classifications to Unresolved. It never extracts HLD points. It receives already trimmed geometry, does not add HLD clipping planes, and reconstructs world curves before flattening. Raw HLD tests therefore distinguish native capabilities from errors introduced in preprocessing/extraction.

McNeel's [HLD parameters reference](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hiddenlinedrawingparameters/) documents supported AddGeometry types, transformed geometry, active clipping planes, and Rhino 8 occluding-section controls. Those are real avenues for a new adapter; the MVP does not exploit them. Native section curves are not a per-object closed-region/hatch API.

### Custom

Despite the name, this is **not an independent geometry kernel**. It uses Rhino Silhouette.Compute, intersections, meshing, MeshRay, Brep ray refinement and curve operations. It avoids HiddenLineDrawing.Compute specifically.

For verified convex scenes, it enumerates Brep edges and handles linear candidates analytically. For everything else it traces silhouettes/creases/boundaries, collects projected curve intersections and physical Brep–Brep/curve–Brep events, meshes occluders, and classifies intervals using three visibility samples. See [CustomEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/CustomEngine.cs:253), lines 253–356, 450–510 and 514–612.

Three agreeing samples only support the interval **if all visibility-changing events were found**. The code does not prove that premise. Mesh contact, grazing silhouettes, non-manifold geometry, overlapping candidate curves and self-occlusion remain weak spots. A global incomplete-preparation flag can conservatively turn output into Unresolved; that is good failure behavior but not successful recovery.

### Auto

Exact routing is in [DrawingEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DrawingEngine.cs:57), lines 57–89:

- If the complete retained scene qualifies as analytic convex solids plus curves, use Custom.
- Otherwise use Native; only if an overlapping Brep pair produces BrepBrep intersection curves does it request mixed repair.
- Mixed repair re-solves analytically eligible source IDs against the interacting scene. Complete source repairs replace native output; curved sources retain native curves. If IncludeHidden is true, completed native curves may supply curved-source events. See lines 116–169.
- An exception in a native Auto solve may trigger general Custom fallback. A wrong but completed native drawing does not trigger that fallback.

This does not diagnose missing curved silhouettes, mesh-specific mistakes, coincident/touching face semantics, general trim errors, or wrong classifications without the trigger. Source IDs must be unique and stable enough for grouping/replacement; they are not arbitrary display labels.

## Prototype debt and correctness limits

1. **Regions/hatching are absent.** `AddSections` returns individual per-source intersection curves, then clips each curve independently ([DepthProcessor.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DepthProcessor.cs:274), lines 274–321). There is no join/containment/nested-void graph, region validity result, material/style resolution, hatch object, or merge policy. Closed solids often give usable loops; cropped loops can become open. See measured crop counterexample in geometry matrix.
2. **Clipping is expensive and changes topology.** Physical Brep.Trim/CapPlanarHoles and mesh Split/planar-cap meshing are repeated per view and boundary (lines 161–271). Coplanar contact may return no trim pieces and mark partial. Artificial-edge removal exempts tangent kinds and uses source-curve sampling. The closed-mesh cut counterexample passes `ok` while retaining overlapping strokes.
3. **No block or SubD adapter.** There is no recursive definition expansion, transform-chain provenance, mirror orientation correction or exact-vs-approximate SubD conversion policy. GH's generic geometry conversion does not supply those semantics.
4. **Cache keys are not model revision IDs.** [DrawingEngine.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/DrawingEngine.cs:17), lines 17–32 hashes serialized options plus each geometry's 32-bit DataCRC. SHA-256 does not repair collisions in those CRC inputs. The one-result drawing cache has no GeometryEquals witness check, unlike ScenePreparationCache. Rehashing all geometry also costs solution-thread time.
5. **Preparation cache is not repeated-instance reuse.** Convex/mesh entries use actual centered geometry and settings. A changed scene center, clipping, orientation or tolerance may miss. Translated copies can still have different scene-centered coordinates. Existing meshes bypass the cache. Silhouettes, events, visibility and section curves are not shared as a complete prepared scene. The global lock encloses some expensive copies/comparisons.
6. **Cancellation is cooperative.** Each GH component has its own semaphore, not a global resource scheduler. Several components can enter native work concurrently. Async work prevents synchronous UI blocking but cannot guarantee bounded native RAM, immediate cancellation or recovery from a native crash. The CLI watchdog's process isolation is not present in GH. `never hang Rhino` requires a stronger operational contract than this MVP provides.
7. **Bake lacks the desired source/style organization.** [LayoutComponent.cs](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Grasshopper/LayoutComponent.cs:174), lines 174–253 adds Frame2D/view/visibility/depth layers. It does not retain original source layers or Rhino 8 section styles, create per-object regions/hatches, merge same-style touching cuts, or update an existing baked drawing. Existing layers can have their color changed.
8. **Statuses certify execution, not completeness.** `partial`, `unresolved`, and warnings are useful, but an `ok` result need not be complete, duplicate-free or region-ready. Warnings may repeat after clipping. The route/status strings are too weak for a public correctness promise.
9. **Provenance is incomplete.** Source curves may get endpoint-based original parameter mapping; post-band exact original piece mapping is explicitly absent. Source-layer/style/document revision/definition-leaf identity are absent. `CopySegment` does not copy ParameterSpace; subsequent mapping repairs explicit curves only. See Models and DepthProcessor lines 433–440, DrawingEngine lines 172–200, LayoutOutputBuilder lines 87–113.
10. **Tests are useful but specialized.** Historical 145-case strict and 24-case held-out results are handoff evidence, not a real-building success rate. The independent mesh checker itself says it cannot find candidate curves the engine omitted. Preserve those oracles and expand them with analytic whole-output, region topology and regression fixtures; do not convert curve-count equivalence into a reliability claim.

## Inputs the engine needs versus inputs UX can infer

The current GH components expose **34 registered inputs**: 10 View, 7 Drawing, 17 Layout. This is larger than the owner's approximate count. Engine separation does not require three visible components.

| Information | Must be supplied or deliberately chosen | Can be inferred/defaulted |
|---|---|---|
| Geometry + interacting occluders | Scene scope is essential; separated per-object solves lose mutual occlusion. | Tree paths/IDs, referenced source layers/attributes and definition graph can be captured automatically. |
| View orientation | Named/captured view, explicit GH plane, or clipping plane. | Frame axes from viewport/plane; standard plan/elevation conventions; projection is parallel in v1. |
| Section plane and kept side | A section requires actual plane/kept-side semantics. | A clipping-plane input implies cut enabled; plane location replaces separate enabled/depth inputs. |
| Crop/frame | User intent matters when a fixed viewport or crop is required. | Capture viewport frustum; otherwise fit source bounds once on explicit request/initialization, preserving stable frame afterward. |
| Near/far/beyond policy | Only necessary for intentional bounded views/depth categories. | Derive broad defaults from scene bounds with tolerance padding; avoid arbitrary ±1000 units. |
| Tolerance/units | Engine needs explicit resolved numeric values. | Use Rhino document absolute/angle tolerance and model units; keep an advanced override. |
| Tangent/seam/hidden/style policy | These change requested output, not geometry validity. | Sensible architectural defaults, optional settings/menu and source section styles. |
| Mode/threading/async | Implementation needs routing and scheduling choices. | Internal policy, diagnostics and bounded async scheduling; do not require routine Mode/Async wires. |
| Placement | Optional target plane only, per current requirements. | WorldXY default; scale/anchor/rotation can be handled by normal GH transforms or an advanced settings object. |
| Hatches and per-object regions | Needs region topology and hatch/style policy. | Per-object grouping and original layer/style resolution are automatic; optional override/merge setting. |
| Fade bands/dock/offset inputs | Presentation choices, not visibility inputs. | Internal styles/settings; docking is not required by current M2D+ scope. |

The main ordinary inputs can therefore be Geometry, View/Section and optional Target/Settings. This is a dependency finding, not the later UX design pass.

## Recommended new repository boundaries

Follow the reference owner's architecture: [conduit-plus AGENTS.md](C:/Users/tomosandego/.t3/projects/conduit-plus/AGENTS.md:1) and [Directory.Build.props](C:/Users/tomosandego/.t3/projects/conduit-plus/Directory.Build.props:1). Those reference-repo instructions were read for conventions; its implementation/reviewer/packaging workflow was not invoked for this research task.

- **M2DPlus.Core, net8.0, no Rhino types:** immutable IDs/revisions, scene/instance dependency contracts using plain numeric data, interval solver, cache/scheduler policies, statuses, region/source/style grouping and unit-testable math.
- **M2DPlus.Rhino, net8.0-windows:** Rhino geometry ownership and normalization, block/SubD conversion, prepared definitions and scene indexes, native HLD adapter, selected analytic repair, exact intersections, closed section regions and Hatch creation.
- **M2DPlus.Grasshopper:** one primary component, input snapshot/attributes, progress/publication, settings UI, preview and bake. Internal separation of scene preparation, views, drawing, and placement remains useful.
- **Tests and tools:** pure xUnit in Core plus isolated licensed Rhino integration workers, frozen .3dm fixtures/oracles, phase telemetry, artifact hashes, watchdogs and Yak-only packaging. Do not run the reference Pack-Yak script here: its version mode commits, which is outside this task.

The new native path should first benchmark native clipping with Rhino 8 occluding sections against physical clipping. The general Custom engine should remain experimental until candidate completeness, self-contact and duplicate policy are demonstrably stronger than the baseline. Engine work should prioritize immutable shared scene preparation and exact per-object sections before attempting a universal new HLD kernel.
