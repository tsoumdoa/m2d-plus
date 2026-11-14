# M2D+ plan review — Astra

Reviewed 2026-11-14 against PLAN.md draft v1. **Verdict: revise before engine implementation.** M0 scaffolding can proceed. The three library boundaries, native baseline, separate section-region pipeline, one-component UX, and hybrid execution direction are sound. The plan does not yet define a safe ownership contract, a correct mixed-engine composition, or an adequate acceptance contract for its reliability promise. Those are architectural decisions, not M4–M5 optimizations.

This is a design review only. No implementation or new benchmark was performed. Sources were read in the requested order: [requirements](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/REQUIREMENTS.md), [plan](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/PLAN.md), [UX](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/design/ux.md), then [MVP audit](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/research/mvp-audit.md), [geometry matrix](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/research/geometry-matrix.md), and [performance evidence](C:/Users/tomosandego/.t3/projects/m2d-plus/docs/research/performance.md). The [Conduit+ conventions](C:/Users/tomosandego/.t3/projects/conduit-plus/AGENTS.md) and actual `src/` layout were inspected as reference material; their implementation/packaging workflow was not invoked.

## Ranked issues

“Blocking” gates the affected architectural contract or feature, not all independent work. The recommendations preserve the owner's scope, including opaque-by-default glass, crossing-only cuts, solid poché fallback, disk-side reopen cache, and one main component.

### 1. Blocking — establish owned snapshots, leases, and publication generations in M1

**Where:** Architecture / Scene; M1 versus M4; UX §11.3–5.

“Immutable snapshot of RhinoObjects/GH geometry” is ambiguous. UX §11.3 explicitly proposes retaining document references as immutable geometry in under 100 ms for 50k objects. Read-only document access is not a detached lifetime or a guarantee of concurrent safety. Rhino documents can replace/delete objects, update definitions and close while work is running. GH inputs can expose mutable geometry. A C# immutable wrapper does not make the wrapped native object immutable. McNeel documents `EnsurePrivateCopy` as the mechanism for detaching an object retained beyond a command; this is not a blanket thread-safety guarantee. [CommonObject API](https://developer.rhino3d.com/api/rhinocommon/type/rhino.runtime.commonobject/)

**Recommended change:** Specify the ownership table and build its minimal implementation in M1:

- The host/UI thread resolves document objects, views, definitions, attributes, materials, styles and referenced GH inputs. Workers receive owned geometry plus value metadata; no `RhinoDoc.ActiveDoc`, live `RhinoObject`, document-table lookup, GH document access or UI call crosses the boundary.
- Establish private geometry ownership before dispatch using a tested duplicate/detach path. Share a prepared definition under immutable-by-convention leases; operations that may mutate geometry or populate unsafe native caches use private working copies or serialized access. Keep HLD results and proxies job-local. Copy curves/points into owned results before disposing HLD.
- A scene version is coherent: retain already-owned unchanged entries, capture changed entries, then verify the document generation before accepting the snapshot. Chunk cold capture on the UI thread and restart if needed. A giant single-object copy may still exceed a slice budget; measure it. Treat “under 100 ms” as an unproven target, not justification for borrowing live geometry.
- A cache owns entries; jobs and previews hold leases. Eviction retires an entry and disposes only after its last lease ends. Cancellation does not dispose inputs while native compute may still read them. Define release on completion, failure, component removal, GH/Rhino document close, cache clear and worker death; finalizers are only a backstop.
- GH outputs get independently mutable copies. Neither `GH_Curve.Value` nor downstream mutation may alter cached results. Preview holds a separate lease. Bake works from a current complete result on the host thread.
- Each request carries scene/view/settings versions and a monotonically increasing generation. Completion is scheduled back through GH's solution lifecycle; publish only if all versions still match. Stale progress, previews and worker replies are rejected too. All requested views settle before one publication, preserving per-view failures and stable view indices.

**Acceptance:** Run two components sharing a scene; edit/delete a source and definition while drawing; close the document; evict/clear the cache during a solve; mutate an emitted GH curve; cancel just before completion. No stale output, use-after-dispose, cross-component mutation, document access from a worker, or accumulating native allocations is acceptable.

### 2. Blocking — S4 cannot split objects into independent convex and HLD scenes

**Where:** “What the research changes” item 3; S4; Engines.

“Route convex objects through our kernel and only the rest through HLD” loses mutual occlusion unless both routes retain the full relevant occluder scene. A convex wall can hide a curved pipe; the pipe can hide a wall edge. At penetration/contact, matching silhouettes alone also misses split events and duplicate ownership. A verified convex-only solver is not thereby verified against arbitrary nonconvex/curved occluders.

**Recommended change:** Define **candidate ownership separately from occluder participation**. Initially route only a complete retained scene that satisfies the convex solver's eligibility contract, or a conservatively proven projection-disjoint component. Otherwise use native HLD on the complete scene. A later mixed repair may replace certified source intervals, but it must retain all foreign occluders and all events required for those intervals. If native HLD still receives all geometry, do not claim that output filtering avoids its candidate-generation cost.

Before enabling mixed routing, S4 must establish a common view/tolerance convention, contact tie policy, interval/source identity, and a rule for reconciling coincident pieces across engines. Test partial overlaps, shared faces, convex/curved penetrations, narrow slivers, hollow occluders, transparent objects, and cut/crop boundaries from oblique as well as axial views. Compare the complete partition of visible/hidden/unresolved loci and missing candidates against independent truth on tractable scenes; native agreement alone is insufficient.

The fallback is the full native route with its stated validation status, not the known-incomplete general Custom engine. S4 coverage must measure expected native work removed and retained foreign-occluder cost, not merely the percentage of objects that are convex. The terrain-heavy campus is particularly weak evidence for a convex-building speedup.

### 3. Blocking — correct section-style resolution and settle the region/output contract before M2

**Where:** Sections; M2; UX §1.2–1.3, §3.7, §11.8.

The proposed order **per-object regions → region crop → optional same-style merge → hatch** is right. Three details presently make it unsafe to implement literally.

**Style resolution is not an unconditional clipping-plane → object → layer override chain.** Rhino's object properties explicitly select By Layer, By Parent, By Clipping Plane, or Custom. By Parent matters for nested instances. The clipping plane's style participates when selected by that source mode, and can itself come from its layer. Implement and test a source-mode resolver before applying M2D+'s explicit hatch override. [Rhino 8 object properties](https://docs.mcneel.com/rhino/8/help/en-us/properties/object.htm) [Clipping-plane properties](https://docs.mcneel.com/rhino/8/help/en-us/commands/clippingplane.htm)

Snapshot resolved pattern definitions and all relevant style values; do not carry only document-local pattern indices across workers or disk caches. Test no style versus an explicit no-fill style: only the former invokes solid poché. Merge compatibility must include effective pattern/scale/rotation/origin, colors/background and boundary policy, not just a style name or index. If coincident regions use different styles, retain both per the separate-object rule, with deterministic preview/bake ordering and a diagnostic for ambiguous overlapping fill.

**A region is not one curve.** An RHS has an outer loop and a hole but one hatch; cropping can split it into multiple regions. Therefore “plain closed Cut curves,” “one hatch per region,” and literal `Cut[i] == Hatch[i]` alignment cannot all hold in the proposed flat branch layout. Open cuts also conflict with the “Cut (closed)” output label. Recommended UX amendment: retain eight outputs and plain curves, but give Cut/Hatch explicit matching region subpaths; Cut holds all loops in a region branch and Hatch holds one hatch or a null. Open outlines get explicitly identified outline branches with null hatches. Document this exception to the general tree rule and carry source membership for merged regions. Do not fabricate a retraced single curve to encode holes or duplicate hatches to fake alignment.

**Crossing and fill eligibility must precede region construction.** Preserve the owner policy: a valid closed solid must truly cross the plane to produce material fill. An open surface's closed intersection is still not evidence of volume; it produces an outline and warning, no hatch. Non-manifold/self-intersecting inputs need an explicit unsupported-region result unless material topology is established. Coplanar/tangent contact gets no hatch and no silent plane nudge; wholly removed geometry is absent, and retained coplanar faces participate in normal visibility/beyond classification. A whole-object bounding box crossing test is only a broad phase: disconnected components on opposite sides can yield a false positive. Define the tolerance-band case as ambiguous rather than pretending signed-distance samples prove a crossing.

Region validation must cover containment, nested holes/islands, self-intersections, zero-area loops and area conservation through crop/merge. Preserve crop-created closure edges as distinct provenance from physical section edges. Suppress coincident Visible/Hidden strokes under Cut only over the actual overlapping locus, without deleting a hole boundary or collapsing separate source regions. This directly addresses the sphere and mesh duplicate counterexamples.

**Acceptance:** RHS/CHS and multiple islands; cropped-box area 2; crop splitting one object into two regions; open curved surface with a closed contour and no fill; coplanar contact and near-contact; same/different-style touching and overlapping regions; nested By Parent styles; absent versus explicit no fill; pattern remapping after worker transfer, bake, save and reopen. The 500-object result proves feasibility, not these semantics.

### 4. Blocking — replace “matrix passes” with a completeness and uncertainty contract

**Where:** M1 done condition; Rhino integration tests; working rule “independent oracle.”

The research matrix explicitly contains U verdicts and narrow property passes. It cannot become a full geometry-certification gate by treating `ok`, valid curves, matching counts, area alone, or agreement with HLD as correctness. An interval checker cannot find a candidate that was never generated. The native threading experiment changes hidden geometry, not just output ordering; deterministic tests alone do not validate the production route.

**Recommended change:** Make per-view/per-source quality separate from execution state. Record route, tested capability domain, approximation/error budget, normalization changes, missing-source failures, uncertain intervals, invalid native extraction, region status and provenance. “Finished” must not imply “certified.” The shipped route must be tested with its actual native threading mode. Start with single-threaded native computation where the evidence supports repeatability, then enable internal multithreading only after equivalence checks; do not assert that single-threaded means mathematically correct.

Known uncertain curves go only to Unresolved. A missing curve or uncertain point cannot be represented by that curve output: report the affected source/view explicitly and suppress its untrustworthy result. If a failed/unsupported source may occlude other sources and the effect cannot be bounded, fail or conservatively quarantine the affected view/region instead of marking the remaining lines confidently visible. Invalid Unset proxies need a classified diagnostic and regression rule; neither indiscriminate dereferencing nor silent skipping establishes completeness. A native null return must distinguish cancellation from failure using request state. [HLD Compute API](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.hiddenlinedrawing/compute)

Use a layered oracle suite:

1. Independent analytic full-output expectations for boxes/polyhedra, curves/points behind solids, simple spheres/cylinders, hollow sections, and adversarial contacts. Check missing and extra loci, class intervals, endpoints within tolerance, holes, and partial overlap/retracing.
2. Independent section topology and numeric area expectations, including component/hole counts and containment. The same `CreatePlanarBreps`/`Hatch.Create` pipeline twice is not an independent topology oracle.
3. Metamorphic tests: rigid transforms and mirrors; unit changes with tolerance scaling; input permutation; block versus expanded geometry; crop versus restricted full result; worker versus in-process; cache on/off; clean recompute versus edits; tiled/mixed routes versus their baseline. These expose inconsistencies but do not replace truth oracles.
4. Small held-out scenes and explicit bounded/unknown status for general freeform cases without whole-output truth. Include asymmetric mirrored and nonuniformly scaled blocks, trimmed holes obliquely viewed, grazing thin occluders and varied tolerances/large coordinates.
5. Lifecycle/failure injection: native failure, worker crash/OOM, stale reply, truncated cache, deleted definition, missing hatch pattern and interrupted publication.

The principle “never wrong without saying so” should mean no **known or detected** uncertainty silently promoted to trusted output, plus an honest stated validation domain. Arbitrary freeform native correctness cannot be guaranteed by finite fixtures. Report that boundary without treating every execution success as proof.

### 5. Blocking — S1 must gate the execution contract, not merely precede M5 implementation

**Where:** Hybrid worker host; M1 cancellation; M5 one-second cancel and RAM promise; S1.

Async scheduling protects the UI from waiting on ordinary managed work; it cannot kill an in-process native hang, cap native allocations safely, or isolate a native crash. Cancellation-token support has no documented 100 ms or one-second response guarantee. Those properties must not be promised for the in-process route. [HLD Compute API](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.hiddenlinedrawing/compute)

**Recommended change:** Finish an S1 feasibility decision before freezing M1's host contract. Add a separate worker executable/entry point to the architecture; reuse the Rhino engine through a versioned request/result protocol. Start with one globally budgeted HLD solve per process, across all components, not one semaphore per component. Separate immediate UI cancellation/stale-result rejection from actual resource release. Do not start unbounded replacements while a cancelled native call remains alive. A private worker's initialization/document operations, if required, stay on its designated host thread; computational tasks still consume detached inputs.

S1 must produce these artifacts and decisions:

- **Licensing and availability:** verify simultaneous interactive Rhino plus one and multiple local workers under the intended license configurations. A successful serial Rhino.Inside experiment does not answer concurrent licensing or distribution terms. Obtain current vendor guidance; do not assume Rhino.Compute server licensing or arbitrary `Rhino.exe` switches apply. Define a visible worker-unavailable result. Do not silently send an unsafe large job back into the interactive process.
- **Cold and warm costs:** startup/license checkout, input encoding, transfer/read, native reconstruction, solve, extraction, result encoding/decoding and GH materialization, with peak aggregate private memory. Compare one view, many views and unchanged/revised scenes. Existing 1.82 s startup and 3.32 s model-read measurements are useful context, not a worker-IPC benchmark.
- **Protocol/versioning:** handshake exact Rhino runtime/service release, RhinoCommon build, plugin/engine build, schema, units/tolerances and capabilities. Prefer the host's supported matching runtime. Refuse or explicitly segregate incompatible workers/caches; archive compatibility does not establish geometric equivalence. Record engine/threading settings and canonical source order for reproducibility.
- **Transport:** begin with a minimal scene package, likely File3dm plus a manifest carrying identities, attributes/styles and instance graph. Ship each definition once and retain a scene in a warm worker across views. File3dm has convenient native fidelity but serializing/writing/reading and reconstructing hundreds of MiB can dominate small jobs. Shared memory can remove byte transport copies but cannot share live RhinoCommon pointers or eliminate reconstruction. Serialized meshes suit actual mesh inputs or an explicitly approximate route; replacing Breps/SubD with meshes silently changes curve and visibility accuracy. Benchmark before choosing a more complex transport.
- **Containment:** budget host + all workers + duplicated preparation + output buffers, not each worker independently. Exercise a Windows-owned process-tree/job limit, graceful cancel followed by bounded hard termination, crash recovery, cleanup and bounded retries. A sampled watchdog can overshoot a threshold; replace “never above cap” with a tested OS-enforced allocation/resource policy, headroom, and specified OOM behavior. Never target the interactive Rhino process.

Choose the route by predicted time **and memory/risk**, using geometry/face/triangle counts, projected overlap, view kind, number of views, cache residency, transport bytes and prior measurements. Object count alone fails: the campus has only 2,983 objects and around 14 million mesh triangles. Model cold/warm worker overhead separately, use conservative upper bounds and hysteresis, and route unknown high-risk jobs to isolation. Tune the threshold against measured tail latency/UI responsiveness, not one mean solve time. Hard termination is enforceable only in the worker route; the residual in-process risk needs explicit product wording.

### 6. Should-fix — rank native-work reduction separately from enabling infrastructure

**Where:** “Only three things move the big number”; M4–M5; S3–S4.

The 83.902/89.682 s campus result supports native-work reduction as the primary single-view lever. It is one plan of a terrain-heavy 2,983-object model, not a 50k-object or general multi-view performance law. Prepared-scene reuse remains essential for ownership and responsiveness even though eliminating every non-HLD stage would save only about 6% in that measurement.

**Recommended priority order:**

1. Build ownership, bounded scheduling, revision tracking and phase telemetry first as infrastructure. Avoid visibility calls altogether for valid result-cache hits and placement/style-only changes; this is a missing high-value lever. Material/opacity and some style-related policies affect occlusion and must not be misclassified as display-only edits.
2. Preserve conservative crop/cut/depth culling. Reject only certainly irrelevant objects using scope-aware, tolerance-expanded bounds. Named-view cropping must restrict retained rays without manufacturing new physical edges. ROI timings are evidence for smaller requested scope, not equivalent full-model speedup.
3. Use workers first for isolation; add bounded parallel views only after throughput/RAM/CPU tests. More workers improve drawing-set throughput, not inherently one-view latency. Compare few processes with internal HLD multithreading against more processes with it off; leave CPU and memory headroom for Rhino.
4. Reuse definition-local preparation and exact local-plane section data where measured stages justify it. Mesh/candidate stage ratios do not imply an HLD speedup. Start with explicit definitions and rigid/mirrored transformations with verified policies; reject unsafe reuse under nonuniform scaling until tolerance distortion is handled.
5. Explore S3 for difficult overlap/memory cases and S4 for demonstrably eligible scenes. Neither partitioning nor mixed convex routing belongs in the baseline performance promise. The current evidence does not rank their eventual gains; measurements must decide.

“Occluder-only handling” is not yet a demonstrated native HLD optimization. Verify an actual API/algorithmic mechanism before listing it as saved native work. Conservative coarse occlusion can be a later experiment, but raster/depth samples cannot authorize dropping subpixel candidates. General non-block duplicate recognition and arbitrary silhouette-angle quantization are lower priority than explicit definitions and exact keys.

### 7. Should-fix — make S3 a correctness-gated experiment with a monolithic fallback

**Where:** S3; M5 partitioning.

All objects whose projected extents overlap a tile can occlude it regardless of which world-space chunk contains them. Large terrain slabs or roofs may appear in every tile, defeating both time and memory savings. A halo does not repair missing distant-depth occluders.

**Recommended change:** Solve each tile using every relevant occluder at every retained depth, with conservative projected selection. First keep original geometry whole and clip emitted results to exact tile interiors; if geometry itself must be trimmed to bound memory, separately prove that new trim/cap edges and changed silhouettes cannot contaminate output. Use a tolerance halo for numerical boundary behavior and a deterministic half-open ownership rule for emitted seam pieces. Preserve source/feature/parameter provenance; geometric near-equality alone must not merge distinct coincident sources.

S3 must check tile-boundary tangencies, a curve lying along a seam, long curves crossing many tiles, occluders with no local output edges, hollow/transparent geometry, clipped points, crop corners, and cuts spanning tiles. Build/merge/hatch regions per source independently of tile linework, or explicitly prove correct topology reconstruction. Compare tile sizes/origins and all-threading modes against monolithic results plus independent tractable oracles. Fall back when duplicated input cost, mismatch, or uncertain seam reconciliation exceeds the benefit. A slower correct monolithic result or explicit bounded failure is preferable to an apparently complete stitched drawing.

### 8. Should-fix — define revision dependencies and durable cache identity now

**Where:** M4 “edit-one-object re-runs only affected views”; UX §3.1 and §11.2/9; reopen decision.

An occluder can hide an object far behind it. Removing or moving it requires old **and** new bounds, including candidates absent from the visible result. A block-definition edit affects every instance. Layer visibility, material transparency, clipping scope, units, tolerance and named-view edits are also dependencies. Automatic frame extents and row placement can change even when a view's visibility result is reusable.

**Recommended change:** Separate geometry/definition, scene membership, view/clipping, visibility policy, section topology, style and placement revisions. Use document/session-qualified identities and definition-leaf/instance-path identity, with content fingerprints for durable reuse. Runtime object serials, file paths, timestamps or hashed 32-bit CRCs alone are insufficient across reopen or GH recomputation. Verify GH data that lacks a reliable revision source; conservative invalidation is acceptable.

M1 should reuse whole owned scenes and invalidate whole affected views using conservative old/new influence bounds. Defer within-view partial visibility recomputation until candidate/occluder dependencies are represented and tested. An unbounded view may legitimately need full recompute. Do not promise “only affected views” as “only changed objects.”

Use layered cache keys: definition-local preparation; source/local-cut-plane regions; view visibility; styles/placement; serialized final results. Section keys include local plane offset as well as orientation. Avoid placing generated section curves or view-dependent silhouettes in a universally view-independent cache. Mesh quality/tolerance, transform class and normalization version are part of relevant keys.

Disk records need content/engine/Rhino/schema fingerprints, a checksum and atomic completion, pattern definitions, bounded eviction and concurrent-reader/writer handling. On corruption/version mismatch, recompute. Unsaved GH geometry needs content verification; pathname and document modification time cannot prove identity. The reopen scan itself must be asynchronous/owned safely and measured. Keep results outside `.gh` as agreed.

### 9. Should-fix — make clipping scope/depth and geometric contracts explicit in S2

**Where:** S2; M2; UX §2–§3.

For a scoped clipping plane, objects outside the scope stay whole and can occlude the cut. Applying the cut/far bounds globally before scope resolution silently changes that drawing. Rhino supports include/exclude participation and zero custom depth as section-only display. These need explicit test cases, including nested instances and flipped planes. [Selective clipping](https://www.rhino3d.com/features/clipping/selective-clipping/)

**Recommended change:** Resolve per-source participation and retained halfspace/depth before culling or HLD setup. Freeze the selected plane's state; do not accidentally apply every active viewport clipping plane. Per the agreed UX, a named view is uncut unless a future explicit decision changes that behavior. Define whether a Settings depth override applies globally or preserves the clipping plane's participation scope; default to preserving the source's scope. Treat zero depth explicitly, with a direct section result rather than hoping a zero-thickness native volume behaves correctly. Verify the selected clipping plane's visible side against the GH plane convention instead of assuming their stored normals match.

S2 should separately judge background visibility, cut topology and point visibility for native clipping versus physical trim/cap. A points workaround must still see the same clipped occluders; running uncut HLD for points is not equivalent. Per-source clipping support exists in the HLD parameter API, but the measured Unset/point cases still require integration proof. [HLD parameters](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hiddenlinedrawingparameters/)

Physical trim fallback must preserve provenance and distinguish artificial cut/crop/far-cap features from source edges; the far bound is not automatically a second hatched section. Missing trims, failed caps or uncertain occlusion are reported. Preserve the owner rule forbidding hatches on open geometry even if Rhino's display style offers to section open objects; “match viewport” is subordinate to that deliberate cut policy.

### 10. Should-fix — refine assembly boundaries and keep the smallest M1 genuinely small

**Where:** Architecture and M0–M1.

Core/Rhino/GH is the right split. Keep pure interval/topology **contracts**, policy, IDs, scheduling decisions and numeric math in Core; exact Rhino intersections/Boolean operations, region realization and hatch construction stay Rhino-side. Rhino snapshot/bake services may use document APIs on the host thread; the compute service must not. GH owns input resolution adapters, scheduling publication, preview and controls. Disk-cache policy/format ownership belongs below the GH component so workers and tests use the same validation rules.

**Recommended change:** Add the worker host entry point explicitly, keep Rhino engine services reusable without a GH assembly dependency, and choose whether the Rhino project is a library or also a plugin host. Conduit+ actually separates Display from its `.rhp` entry assembly; copy the separation principle, not an accidental `.rhp` dependency for the worker. Exclude bundled RhinoCommon/Grasshopper runtime copies as the reference projects do, pin supported API/runtime versions, and retain Yak-only installation.

Do not require the entire geometry matrix, every view input and six production-quality drawings to call M1 a walking skeleton. Those are broad functional gates. First prove one source snapshot can safely serve two views and two components through cancellation, cache reuse and a compatible worker request/result round trip. Register final input/output identities early; feature-disabled outputs can remain empty with an explicit diagnostic. This avoids reordering saved GH definitions as milestones add Cut/Hatch/Beyond.

### 11. Nice — make provenance and benchmark evidence easy to audit

**Recommended change:** Standardize a benchmark/report manifest from M0: engine/fixture/oracle hashes, Rhino build, route, native threading, tolerance, retained scope, cache state, worker count, cold/warm status, wall/CPU/peak memory, UI capture/publication latency, and correctness verdict. Record time-to-first-preview separately from complete-result time. Freeze small fixtures in-repo and keep the 527 MiB campus as a hash-verified external test asset rather than repeatedly copying it into each checkout/package.

Add a generated 50k-object fixture with repeated and unique geometry, plus high projected-overlap elevations. Preserve the campus and 305-object fixtures for continuity. M3's owner test on a real model is useful but is not a guaranteed gate when the agreed test data explicitly provides no owner project models yet; use reproducible generated fixtures and offer optional owner validation.

### 12. Nice — avoid overpromising draft latency and per-piece fade

**Recommended change:** Make the sub-second draft pass an optional measured enhancement after the exact pipeline works. On tiny jobs it can add latency; on expensive inputs it may itself be unbounded. A draft must retain its stale/approximate label and cannot populate outputs or Bake.

Clarify lines crossing the Beyond threshold: split at the category boundary if Visible and Beyond are exclusive. Four depth-fade bands without geometry splits can use a documented per-piece representative depth, but cannot simultaneously promise exact depth-varying object color along one baked curve. Keep this as a small UX specification correction, not extra component inputs.

## Suggested milestone and spike order

Keep M0–M6 labels, but move ownership/scheduling ahead of broad functionality, and decouple required isolation from speculative acceleration.

| Stage | Revised scope and gate |
|---|---|
| **M0** | Solution, pure Core tests, Rhino engine/integration harness, GH registration with stable four inputs/eight outputs, worker entry-point skeleton, Yak-only loading and fixture manifests. No performance or completeness claims. |
| **S1a during M0, before M1 contract freeze** | Licensing/concurrent launch, matching-runtime handshake, smallest scene/result round trip, crash/kill/resource experiment. Decide viable transport and unavailable-worker behavior. Add an ownership/lifetime micro-spike here or as an explicit M1 prerequisite. |
| **M1a: smallest walking skeleton** | Owned versioned scene, one process-wide scheduler, one native route, two direction/vector views of a box plus a crossing curve/point and asymmetric nested block; current-generation-only batch publication, detached outputs, leases and cancellation races. A second component demonstrably reuses preparation. Whole-view invalidation is sufficient. |
| **M1b: baseline capability** | Complete declared normalization and non-section view forms; deterministic native adapter/extraction diagnostics; 305-object six-view run; geometry matrix with explicit property-level P/F/U gates. A Plane is always a section: do not temporarily expose it as an uncut view. Hold Plane support until S2/M2, or include a correct minimal section slice. |
| **S2 during M1, blocks M2** | Native clipping/physical trim decision, cut contact/tolerance policy, points, Unset proxies, scoped/zero depth and flipped plane. Include style-source and region/output contract probes before M2 starts. |
| **M2** | Per-object material regions, open outlines, crop, optional compatible-style merge, hatch/style inheritance, per-source scope/depth and output precedence. Region/hatch/path/export correctness gates, not area alone. |
| **M3** | Target, settings/menu, categories/fade/glass, preview and one-undo replace bake. Verify style/placement edits avoid HLD and opacity edits invalidate it. Test save/reopen of baked hatch definitions. |
| **M4** | Extend the M1 foundation: dependency-aware invalidation, definition/section reuse, bounded memory and durable reopen cache. Complete **S1b** end-to-end cold/warm/multi-view IPC and threshold calibration; ship production single-worker isolation by the end of M4. Heavy jobs remain explicitly gated until isolation is ready. |
| **S3 after stable monolithic M2 baseline** | Scaling with overlap, safe partitions and seam equivalence; gates only partitioning. Early timing reconnaissance is useful, but tiling engineering should follow fixed cut/visibility contracts. |
| **S4 after baseline/oracles, before any own-kernel routing** | Cheap eligibility census may run early; correctness, mutual occlusion and whole-output composition must precede activation. Convex-only routing may ship independently if proven. Mixed-scene routing is optional until certified. |
| **M5** | Calibrated heavy-job admission, bounded parallel workers, campus plus 50k/high-overlap tests, cancellation/resource recovery and interactive responsiveness. Ship partitioning/convex acceleration only if their independent gates pass; neither should block an otherwise correct isolated beta. |
| **M6** | Reproducible local release checks, documented supported/uncertain domains and worker/license behavior, examples, installed-Yak smoke tests and public beta. |

The smallest meaningful M1 demonstrates the ownership and execution architecture under change. The full geometry matrix then expands its capability. This avoids building a broad native wrapper in M1–M3 only to replace its scene, scheduler, output and caching lifetimes in M4–M5.
