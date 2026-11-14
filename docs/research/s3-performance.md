# S3 performance spike and early S4 census

Research dates: 2026-11-14–15 (Asia/Hong_Kong); scratch created November 14. Completed research spike; indicative timings and explicit correctness gates.

Prioritize M4 early section bounds and definition-local reuse. Keep native multithreading off. Native view tiling and current convex routing are **NO-GO** on the measured tiers; a broader planar-kernel correctness spike is **GO**. Native hidden-off has a large measured compute gain but an unresolved Visible-output equivalence gate.

The user reprioritized the study after Fable's second opinion: a smaller scaling sample; culling with hidden lines off; threading; Fable census plus curved-only native cost; then 2×2/4×4 tiling on M/L. 8×8 and XL tiling are deferred. All timings are **indicative shared-desktop measurements**, with interleaved research batches and no clean-machine isolation.

| Build / host identity | Value |
|---|---|
| Source | `5f89d8264c6bf60662b531e7c0ee3130b2f00ff4`, detached `origin/m2/wiring` |
| Research assembly SHA-256 | `EB6E9D820310B3B104DD20FCB4E8AE5326CC1CFF8CFA74CD1515409906198574` |
| Empty-tile replay SHA-256 | `0409B3D1AB87BBE04723D16C2C9A0B7AF9C18C86BA18EE6BA6E1B8ABF21E6517` |
| Null-safe prep replay SHA-256 | `707EF89966289ECBE4C9C9EABAEDD4424422662940FA8971A6FA58192F10DA1F` |
| Reference API | RhinoCommon `8.20.25147.11001-rc`, excluded from runtime output |
| Actually loaded Rhino / CLR | `8.36.26296.17001` / `.NET 8.0.34`, Windows x64 |
| Loaded RhinoCommon SHA-256 | `5FD146708CD1C357760557FB073933E4D873BABB56FCEE57580C1BF1F8D8FA8B` |
| Native rhcommon_c.dll SHA-256 | `C7A01969A1F6946566B1444284B1997010D15F0F8849696185E7E544324EC90E` |
| Machine | Ryzen AI MAX+ 395, 16 physical / 32 logical cores; about 63.65 GiB RAM; Windows build 26300 |
| Inputs | Metres, tolerance 0.0001; exact model / sidecar hashes recorded per batch |

The separate bounds-preparation executable references a byte-identical copy of this research assembly; its executable hash and referenced-assembly hash are in its watchdog record. Compilation into a scratch host is not an installed-Yak or interactive host-load test. No .NET 10 performance equivalence is inferred.

## Scope and reproducibility

This is research only. No product source changes, installation, commits or pushes are part of the experiment. The source was checked out detached at `origin/m2/wiring`, commit `5f89d8264c6bf60662b531e7c0ee3130b2f00ff4`, using the requested scratch worktree. The harness compiles the original Core and Rhino source files directly into a separate scratch executable; its `bin` and `obj` are outside the detached source tree. [Frozen source archive](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/frozen-source.zip) and [initial source manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/source-manifest.json) preserve the source identity.

Read PLAN S3/S4/M4/M5 and spike decisions; Astra issues 2, 6 and 7; performance.md; S1a; and M2 route C before designing the probes. The model-suite documentation was read from `test/model-suite` at `f709154c8ab0955fec7be7efb6ca2a69cf43eb5e` and saved as [test-models.md](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/evidence/test-models.md) and [models README](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/evidence/models-README.md). Both architectural and curved generated model directories were present. Model bytes must match their sidecar hashes; each worker records the actual model and sidecar hashes, not just the filename.

The [C# harness](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/harness/Program.cs) uses M2D+ `SceneCapture`, `SceneCache`, definition normalization and `PreparedScene.Prepare`. Section visibility bodies and regions use the original `SectionRegionEngine` and `PhysicalSectionClipper`. Explicit `routeC` probes call the original `SectionDrawing.Solve`. Other timing probes call raw RhinoCommon HLD over those detached prepared objects, with the same view convention and settings. These raw probes isolate native cost and permit whole-geometry tile selection without adding product routing code. Raw section outputs include native trim-edge candidates and are not represented as the complete production Cut/Visible/Hidden precedence pipeline.

Raw section timing rows contain only certified retained bodies and carry the section engine's uncertainty count. An unsupported/uncertain source can still occlude the view: those rows are **not usable complete drawings** and do not override Route C's quarantine policy. Timing a smaller certified subset is not proof that the excluded uncertainty is harmless.

Default native policy: parallel projection, document tolerance, `Flatten=false`, hidden curves included, tangent edges/seams excluded, `multipleThreads=false`. Top and Front mean uncut full-scene plan projection and South elevation. SW is the product SW direction. Separate Plan is a physical cut at world z=1.2 m. Long cuts at the scene's central Y with +Z up, retaining signed plane distance ≤0. Depth uses that same section with a 3 m retained depth. The central crop retains the middle half of each frame axis, or 25% of the rectangular view area; it is a change of requested scope, not an equivalent whole-model speedup.

Hidden-off probes set the actual native `IncludeHiddenCurves=false` flag; they are not merely post-filtered full solves. The audit export intentionally retains any residual native Hidden records. A production hidden-off adapter must still suppress those records: McNeel explicitly documents that Hidden visibility records may remain when hidden-curve generation is disabled, and recommends this flag for performance. The managed wrapper forwards the flag to native. [Native API contract](https://developer.rhino3d.com/api/cpp/class_o_n___hidden_line_drawing.html), [managed wrapper](https://github.com/mcneel/rhino3dm/blob/8.x/src/dotnet/opennurbs/opennurbs_hiddenlinedrawing.cs). The API contract does not adjudicate the visible-locus disagreements measured below.

Fresh worker startup, archive read, document capture and world preparation are recorded separately. Multiple operations inside a worker share its prepared scene; HLD results are disposed between calls. No disk-cache flush, desktop isolation, power-policy change or disturbance of other applications is attempted. Most configurations are reconnaissance n=1; threading has three repetitions per setting/view. Exact sample counts accompany the final tables. No interactive UI, GH publication, Bake, installed Yak or runtime migration is tested.

Headless capture uses a 300 s Step budget over its private immutable document. The source's Verify path computes document witnesses on each Step; a 12 ms interactive-style budget would repeatedly scan a large headless document and confound HLD experiments. Reported capture durations therefore do not establish interactive capture latency, and no source verification rule was bypassed or edited.

## Containment and scheduling

The [supervisor](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/harness/owned_job.py) is copied from the frozen model-suite tooling. Each child starts suspended, is assigned to a new private Windows Job Object, then resumes and receives an stdin handshake before loading Rhino. Job descendants are contained; kill-on-close and a hard job commit limit are set. Aggregate private bytes of job members are sampled every 200 ms, with roughly 1 s logged samples and a separate Windows job peak. Only these owned jobs can be terminated. HLD receives a 300 s cancellation token; the outer job wall watchdog remains the hard bound when cancellation does not return.

Serial batches have a 10 GiB job cap. The explicitly requested parallel-tiles experiments use two independent processes at 6 GiB each, giving at most 12 GiB combined job commit allowance, plus the small non-Rhino coordinator. All other headless work remains serial and participates in the suite's shared `Local\M2DPlusLocalModelSuiteRhino` mutex. The .NET 8 research host does not change the owner's runtime. Production worker selection must still match the actual host runtime, architecture and exact Rhino/engine fingerprints under S1a.

Per-HLD private memory is additionally sampled inside the owned process every 25 ms. It includes the retained complete prepared scene and output allocations; it is not a measurement of native HLD's allocator alone. A warm tiled worker retains the full prepared scene even when a tile submits few objects. Parallel peak sums are conservative upper bounds unless explicitly reconstructed from aligned samples. Cold process wall time includes startup/read/capture/extraction/JSON output, while HLD compute and warm tile operation wall are separate columns.

## Tiling prototype and equivalence method

Tile selection uses each complete object's conservative projected bounding box, expanded by an 8-tolerance halo. **Every depth** participates. The original geometry remains whole and the full-view camera/frustum remains fixed for every tile. No native tile clipping plane and no tile-generated physical edge are introduced. HLD's emitted curves are projected into the common frame and clipped to exact tile interiors. Curves lying along an internal maximum-X or maximum-Y boundary belong to the neighboring tile; the outermost maximum edges are closed. Zero-measure shared endpoints are permitted; positive-length duplicates are not.

Cut material regions are computed once independently of tile linework. A region retains all loops, hole flags and its aligned hatch; tile linework never reconstructs region topology or creates one hatch per tile. Points use the same retained whole occluders, then one half-open tile owns each projected visible point. The prototype's point-ray implementation covers Brep occluders; mesh-point cases require the production point adapter before activation.

The raw **section timing helper is body-only**; it omits loose-curve and point section handling. Its section point output is not a correctness gate. Uncut point checks and the independent region pass do not certify complete production Route C tile composition. The feature Plan probe uses z=1.2 m, not the fixture sidecar's analytic z=0 cut; only metamorphic comparisons apply there.

The [offline comparator](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/harness/analyze.py) checks both directions of continuous polyline locus coverage, grouped first by visibility class and then by source/class. It checks every emitted polyline, rather than isolated endpoint samples or total length alone. Native curves are converted with a requested chord deviation of tolerance/16. Comparison buffers use 0.75 tolerance, leaving room for both chord approximations; this relies on Rhino's polyline approximation contract and is not an independent proof of arbitrary freeform truth. Positive-length cross-tile intersections are counted per source/class. Source changes at coincident features are reported separately and never silently merged. Point identities/coordinates and separate region topology records are compared as well.

Sorting orientation-normalized, rounded polylines provides an additional canonical signature. It can remove order and direction differences; it cannot repair changed visibility, missing intervals or source attribution. Synthetic comparator controls accept a split straight curve and detect a deliberately inserted gap and duplicate interval. Native baseline agreement is metamorphic evidence, not an oracle for native correctness.

Reported uncovered lengths sum over emitted pieces; they are not necessarily unique geometric union lengths. Exact cross-tile duplicate intersections are checked, but near-coincident distinct feature ownership is not fully certified because the timing export retains source identity rather than complete native feature/parameter provenance. These limitations reinforce a no-go when a tested invariant fails; they cannot turn a visual match into a shipping gate.

## S4 and coarse-occlusion correctness conditions

The census invokes the MVP's actual private `TryConvex` predicate through reflection, after centering a copy as `CanSolveAnalytically` does. It checks validity, closed/manifold topology, 4–64 planar faces, vertex halfspaces and nonzero volume. This is an eligibility census only; the general Custom route and mixed routing are never enabled. Definition identity avoids repeating the same eligibility query where affine placement preserves the predicate.

Potential-occlusion components are conservatively connected by tolerance-expanded projected bounding-box overlap. Only a component whose entire retained occluder scene is eligible can be routed independently. A curved or hollow source, a terrain mesh or a large canopy can poison an otherwise highly convex component. Percentage of eligible objects/edges is reported separately from percentage of safely removable native work. Edge shares are cost proxies, not measured HLD cost attribution.

The coarse occlusion prototype uses a stronger sufficient certificate than sampled depth: a target's complete projected bounding rectangle must lie strictly inside the projected convex hull of a certified closed opaque polyhedron, and its foremost depth must be behind that occluder's rearmost depth, with an 8-tolerance margin. For a convex closed solid, every such target ray intersects the occluder first. The certificate is conservative and misses sloped/partial-depth opportunities; no raster sample authorizes removal.

**Removing a fully hidden source is incompatible with requested Hidden output.** It removes that source's legitimate hidden candidates. Visible-only visibility work may be reduced after preserving the independent cut-region pass, scope/opacity policy and transitive retained-occluder witnesses. Certification must use the actually retained clipped scene. Transparent, unsupported, uncertain, open or hollow occluders cannot acquire a convex-solid certificate through their bounding boxes.

## Measurements and decisions

The ranked M4/M5 decisions follow. Detailed measurement and equivalence tables are retained below.

The first attempted worker entry point bound RhinoCommon before resolver initialization. Those failed before Rhino startup, are excluded from all statistics, and are retained in `bootstrap-binding-attempt/`. The corrected bootstrap uses reflection only after resolver initialization; the guarded XS pilot completed successfully. It found class-locus equivalence for 2×2 Top within the comparison budget, but 0.3997 m changed source ownership, with no positive-length cross-tile duplicates. This distinction matters to per-source styles and provenance.

### Fable context reused, not an S3 performance claim

[Fable's report](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/evidence/fable-second-opinion.md) and [raw census example](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/evidence/fable-census/M-North.json) count 4,790 planar-polyhedral sources, 209 curved sources and one skipped SubD in M. The planar share is 95.8% of the 5,000 original sources. Its predicate permits nonconvex planar polygons and holes; this is broader than the MVP convex-solid predicate used here. Fable's 0.25 s L elevation solve is promising algorithmic cost evidence, not certified complete native-output equivalence: the report states unexplained axo length disagreement, approximate curved occluders and missing silhouettes. No mixed route is built in this study.

An object with no visible candidate edges is not necessarily fully hidden. A solid plate whose outer boundary is covered by four foreground frame bars can still have an exposed interior that hides a line behind it. Removing that plate changes the drawing through the frame aperture. The sufficient convex-hull/depth certificate used here avoids that error; a separate analytic/native counterexample is part of the culling controls.

### Inherited context, not rerun measurements

The original suite's blocks/serial-HLD long-overlap sections recorded XS 0.243 s, S 0.973 s, M 9.116 s, L 44.514 s and XL 118.272 s. They use the suite's native clipping adapter, not this spike's physical Route C preparation. They cannot be substituted for the new tiling, culling or census measurements. A [hash-identified inherited subset](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/evidence/inherited-baseline-summary.json) preserves the source paths and watchdog metadata for comparison.



## Interpretation and M4/M5 recommendation

These measurements change the priority for **physical sections**: in the current M2 Route C API, preparation can dominate native visibility. M Plan spends 66.2 s preparing and 2.4 s in HLD; L Long spends 208.2 s preparing and 39.7 s in HLD. The earlier native-clipping/campus measurements remain valid for their pipelines, but cannot establish the bottleneck of this physical clipping implementation. Optimize measured phases separately.

1. **M4: dependency-aware result reuse, early conservative section bounds, then definition-local section reuse. GO.** A valid visibility cache hit or a style/placement-only edit should make zero HLD calls, as PLAN already requires; that is a contract, not a newly timed cache implementation. The measured M bounds prototype reduces section API inputs from 5,000 to 588/2,650/1,082 for Plan/Long/3 m depth. Preparation falls from 66.2/37.9/39.3 s to approximately 20.9/22.6/11.6 s including bounds. The derived preparation-plus-HLD gains are about 3.0×/1.5×/2.6×, before other production stages. Preserve contact bands, unknown bounds, source scope, uncertainty and independent cut regions; never nudge a plane. Match boundary loci and per-source regions with full Route C oracles before activation. Definition reuse findings below refine this priority; they do not eliminate global occlusion work.

   The completed M block prototype reduces trim/cap from 32.35 to 1.21 s (Plan, 194 local-plane groups) and 26.28 to 1.01 s (Long, 114 groups): approximately 26× for this local stage. Exploded controls have 5,000 groups and nearly unchanged trim/cap. Local region paths are fast but omit the global overlap pass; preserve that pass before claiming a full preparation gain. Prospective Plan silhouette generation falls 5.65→0.035 s with matching 59,590 valid curves; current native HLD does not consume this external candidate cache. Long silhouette diagnostics include 420 null results in both paths, so that candidate list is not certified complete.

2. **S4/M5: fund a broader planar-kernel correctness spike. Research GO; existing convex routing NO-GO on these tiers.** Fable's broader planar predicate covers 95.8% of original sources, while the MVP strict convex predicate covers roughly 94% of normalized objects/edges. Yet only L Top has an independently eligible projection component: 200 objects, about 1% of edges; all other measured M/L/XL plan/elevation cells have zero. Individual convexity is not permission to remove foreign occluders. Paired M/L curved-only native solves cost 4.6–9.7% of complete native solves, a useful cost counterfactual, not an additive native-work attribution or verified 10–22× hybrid gain. A broader kernel must preserve planar holes, curved/mesh/SubD candidate generation and foreign occlusion, physical cuts, exact contact, source ownership and point semantics. Whole-scene eligibility or conservative projection-disjoint complete components remain the only verified routing boundary; mixed routing was not built.

3. **Native hidden-lines-off: conditional GO behind a correctness gate.** The native flag reduces measured M/L HLD compute by 2.3–11.1× (view dependent). Post-filtering a hidden-on solve cannot obtain this gain. However, Visible loci differ from the hidden-on baseline on all four tests; a same-worker M elevation repeat reproduces the discrepancy. Neither baseline is an independent truth oracle at coincident/contact cases. Resolve those intervals against analytic fixtures and define tie/source policy before silently changing production output. Filter residual Hidden records even with the flag off, as the native API explicitly allows them.

4. **M4/M5: keep bounded matching-runtime worker isolation and admission calibration. GO under S1a, with native multithreading OFF.** Architecture Top/Front private memory grows by roughly 114 KiB per expanded object plus 0.38 GiB in this family, with 0.40 GiB maximum fit residual. XL reaches 6.14 GiB in the measured envelope. This is a calibration observation, not a universal admission limit; curved topology, full scene retention and concurrent workers need explicit headroom. Three repetitions per mode/view show MT-on medians 18–70% slower and changed output loci/piece counts. MT-off repeats have identical canonical signatures. Sorting cannot repair changed visibility or source ownership; no middle ground is established by this experiment. Bound parallel **independent view jobs** after S1b calibration, rather than assuming native thread count is a resource governor.

5. **Certified whole-object occlusion: NO-GO as an unconditional optimization in this prototype.** Top certificates remove 44–50% of objects but certification costs 0.87–2.97 s; total certificate-plus-HLD time loses to the unculled hidden-off solve in all four tested views. Elevation removes only about 1%. Definition-local eligibility/hull caching may change that balance, but was not measured. Keep it a conditional future prefilter, enabled only for opaque, certified retained solids and visible-only visibility output; preserve cut-region work. A source with no visible edges can still hide another source through its exposed interior, as the plate/frame/backline control proves.

   Even if certificate cost vanished, the observed Top HLD ratios after hidden-off are only 1.16× (M) and 1.53× (L); these are derived ratios of indicative measurements, not guaranteed limits. Hidden-off itself supplies the larger measured reduction. The same-worker M elevation control has medians 14.620 s hidden-on versus 5.542 s hidden-off, corroborating the compute effect while reproducing the Visible discrepancy.

6. **View tiling: no production activation unless the complete seam/provenance gate passes.** The reduced sample below measures the actual tradeoff. All potentially occluding objects at every depth must enter each overlapping tile, with whole geometry and a common camera. Large terrain/roof footprints duplicate work in many tiles; subdivision does not remove their cost. Half-open output ownership avoids positive-length seam duplication but cannot restore native context-sensitive class/source changes. Independent cut loops and hatches must remain whole; the body-only section helper does not certify production point or loose-curve composition. The explicit serial/parallel results and final decision are stated below.

### What the scaling fits establish

Count and edge count are nearly collinear in the architectural tiers, so this suite cannot identify either as the unique cause. A descriptive six-cell M/L/XL Top/Front fit is `T ≈ 3.886e−5 · E^1.034 · (1 + P/N)^0.347 · 1.131^[Front]`, where P counts projected bounding-box overlap pairs. Training log-R² is 0.9999 and within-family leave-one-out RMS factor is 1.04, but there are only two residual degrees of freedom. Later repetitions vary by tens of percent. This is not a precise real-scene predictor. The all-tier simple count powers and controlled prisms are reported below.

The controlled prisms separate geometry complexity from count: at N=2,000, changing 4 sides to 16 sides increases sparse Top time from 7.06 to 37.34 s (5.3×). Exact aligned depth stacks can be faster than sparse layouts, despite many more overlapping bounds: identical projections collapse native candidate work. Therefore **candidate/face complexity and actual projected intersections dominate a simple object-count or bbox-pair rule**. Bbox overlap is a useful conservative scheduler feature, not a monotonic cost law. Axo is costlier on S/M/L; physical long-section preparation and native cost are separate measured quantities. Arbitrary curved topology and staggered depth overlap need more calibration than this small sample.

### Correctness and performance limits

Central crops change requested scope; 25% frame area removes 49–63% of objects in three cells but only 4% in L Top, where native time gets worse. Raw subset/crop HLD is not perfectly metamorphically equivalent to clipping the full native result: coincident/contact classification and source ownership change. Bounds safety is a geometric argument; native baseline equivalence remains a separate regression gate. Do not present crop timings as an equivalent whole-scene acceleration.

Curved-only / complete-scene time is neither an additive work share nor a mathematical upper/lower bound for a correct hybrid. Removing planar foreign occluders changes visibility interactions and candidate costs. XL uses an unpaired earlier full-scene reference, explicitly marked in the table. The curved-heavy control prevents extrapolating the architecture family's small curved edge share to arbitrary Rhino scenes.

In that curved-heavy M Front control, **all 5,000 objects and all 26,262 edges** fall into the curved subset: full/curved-only compute is 287.666/290.566 s, with identical emitted piece count and lengths. There is no planar native cost to remove in this scene. The first full-scene call reaches 3.84 GiB sampled private memory versus roughly 1 GiB for architectural M; the complete two-call batch reaches 6.09 GiB, including retained allocator/output costs. The architecture-only memory fit must not govern arbitrary Rhino scenes.

At the same 5,000-object scale, this is about 20.5× the standard M elevation's paired full-scene native time (14.024 s), despite fewer topological edges (26,262 versus 59,817). This is a cross-fixture indicative comparison, not a controlled curvature-only experiment, but it decisively limits object/edge-count admission rules. Curved surface intersections and silhouette/candidate complexity need their own cost features and calibration.

No reported local-stage ratio is an end-to-end GH speedup. The study does not include live document resolution, publication, UI responsiveness, durable cache/IPC costs, installed Yak validation, multi-view generation atomicity or a .NET 10 performance comparison. The harness preserves uncertainty; unsupported subsets do not become complete drawings by being faster.

Native audit exports separately count empty sentinels, invalid nonempty/Unset records and duplicates. Invalid records are excluded from curve-locus comparisons and retained as diagnostics; they must not become silent success in a production adapter. Baseline S/M/L axos report 70/409/2,266 invalid records; the curved-heavy full elevation reports 149. These timings remain native-cost measurements, not complete-drawing correctness results. Standard Top/Front baselines have zero invalid records.

The initial XL batch retains its config, build/input identity, worker log and complete per-call compute/private-peak records, but its external `watch.json` summary was not retained. XL's 6.14 GiB figure is therefore a per-call sampled private peak, not an independently persisted aggregate job peak. No missing watchdog trace is reconstructed or replaced with a claimed measurement. Use a new supervised XL replay before deriving a production aggregate admission budget from that tier.

## Final S3/S4 decisions

**S3 native view tiling: NO-GO for production.** Every measured M/L Top/Front 2×2/4×4 comparison fails class and source-locus equivalence. Class missing piece-locus ranges from 85.8 to 498.9 m; extra piece-locus ranges from 16.0 to 322.1 m, at a document tolerance of 0.0001 m. Exact positive-length cross-tile duplicate intersections are zero in all eight comparisons under the source/class comparator. This is not a seam-duplication-only failure. Independent oracles would be needed to adjudicate the changed native subset output; clipping and canonical sorting cannot supply the missing visibility/source contract.

Serial native totals are 17.6–33.7 s for M and 65.8–129.1 s for L. They are 1.32–3.39× the original monolithic native times. These ratios use **unpaired earlier references**, so they are indicative: later monolithic L Top/Front counterfactual batches take 99.1/70.7 s, demonstrating load/phase variation large enough to overlap some tiled timings. There is no robust measured total-cost win. Input duplication is modest (1.04–1.32×), so this sample does not isolate duplication as the sole cause; repeated native solves and uneven tile loads matter. L per-call private peaks fall by about 16–35% relative to corresponding original Top/Front calls, while M has little or no memory benefit because the complete prepared scene remains resident.

Two-process M Front 4×4 reduces the warm tile operation from about 30 s serial to 15.1 s for the slower shard; cold elapsed is 20.5 s. The parallel and serial tile exports have identical canonical signatures and zero source/class locus differences. The two worker peaks sum to 1.54 GiB (upper bound), versus 0.96 GiB serial. It improves the latency of the tiled plan, not the demonstrated monolithic drawing path, and does not cure its correctness failure. Neither this sample nor S1a substitutes for S1b persistent-worker IPC/admission calibration.

Large terrain/roof stress, XL tiling and 8×8 were **not run** under the user's reduced scope. A heavy whole object whose projected footprint spans every tile must be submitted unchanged k times for k tiles. A first-order estimate is `serial native work ≈ k·C_heavy + Σ C_local`, rather than one `C_heavy`; with p independent workers its repeated-heavy contribution is still about `k·C_heavy/p`, before overhead and interaction effects. This is an estimate, not a terrain benchmark. The reduced M/L suite contains no terrain mesh; no claim of a measured large-terrain gain or loss is made. Adaptive/connected-component partitions could be investigated only after the existing subset-output discrepancy has an oracle and provenance contract.

**S4 existing verified convex kernel: NO-GO for whole-tier activation or individual-object mixed routing.** Whole-scene eligibility fails for every tier tested; projection-disjoint complete eligible components account for zero edges in all tested M/L/XL plan/elevation cells except L Top (~1%). The roughly 94% individual convex edge share cannot be converted to 94% removed native work. **GO for a separately gated broader planar visibility/cut kernel research spike**, using Fable's cost evidence and the measured architectural curved-only counterfactual, with brute-force/analytic and metamorphic oracles before routing. The curved-heavy all-curved control remains a native-worker case.

M4 should first combine conservative pre-section bounds with definition-local trim/region reuse and correct dependency invalidation; then measure that combined pipeline, rather than multiplying separate stage ratios. M5 should retain bounded matching-runtime worker isolation, geometry-sensitive admission and native multithreading off. Native hidden-off deserves the next focused visibility-oracle check because it supplies the largest measured native reduction, while uncached whole-object certification and regular-grid tiling do not currently justify their cost.

The initial L 4×4 tile operations failed when a tile contained no geometry. Their partial exports are excluded; the empty-tile replay explicitly skips native compute for empty tiles and classifies point-only tiles without a native geometry call. Initial Long prep-reuse operations hit a null silhouette result; null-safe prep replays preserve those diagnostics instead of silently declaring complete candidates. The two additional scratch assembly fingerprints are listed above. Their source changes are limited to these guards; product source is identical. Reuse timing excludes grouping-key construction and requires mirrored-solid orientation normalization and full boundary/ownership checks before production.

<!-- MEASUREMENTS_START -->
## Measured Q1: scaling and memory

All rows below are indicative; native multithreading is off. Peak means the sampled owned-process private peak during compute/extraction, including the retained scene.

| Tier | Objects | Edges | Top bbox pairs | Elevation bbox pairs | Top s | Elevation s | Axo s | Max private GiB |
|---|---|---|---|---|---|---|---|---|
| XS | 50 | 617 | 78 | 204 | 0.372 | 0.446 | — | 0.55 |
| S | 500 | 6005 | 2203 | 4960 | 1.503 | 1.511 | 3.058 | 0.59 |
| M | 5000 | 59817 | 102721 | 111026 | 9.957 | 11.244 | 19.919 | 1.04 |
| L | 20000 | 239201 | 1632194 | 473869 | 66.139 | 49.754 | 75.362 | 3.32 |
| XL | 50000 | 602887 | 1668481 | 2131043 | 124.934 | 155.726 | — | 6.14 |

<svg xmlns:xlink="http://www.w3.org/1999/xlink" width="713.878594pt" height="245.699437pt" viewBox="0 0 713.878594 245.699437" xmlns="http://www.w3.org/2000/svg" version="1.1">
 <metadata>
  <rdf:RDF xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:cc="http://creativecommons.org/ns#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
   <cc:Work>
    <dc:type rdf:resource="http://purl.org/dc/dcmitype/StillImage"/>
    <dc:date>2026-11-15T00:25:02.489597</dc:date>
    <dc:format>image/svg+xml</dc:format>
    <dc:creator>
     <cc:Agent>
      <dc:title>Matplotlib v3.11.2, https://matplotlib.org/</dc:title>
     </cc:Agent>
    </dc:creator>
   </cc:Work>
  </rdf:RDF>
 </metadata>
 <defs>
  <style type="text/css">*{stroke-linejoin: round; stroke-linecap: butt}</style>
 </defs>
 <g id="figure_1">
  <g id="patch_1">
   <path d="M 0 245.699437 
L 713.878594 245.699437 
L 713.878594 0 
L 0 0 
z
" style="fill: #ffffff"/>
  </g>
  <g id="axes_1">
   <g id="patch_2">
    <path d="M 43.040703 207.876625 
L 356.938594 207.876625 
L 356.938594 21.406313 
L 43.040703 21.406313 
z
" style="fill: #ffffff"/>
   </g>
   <g id="matplotlib.axis_1">
    <g id="xtick_1">
     <g id="line2d_1">
      <path d="M 85.942935 207.876625 
L 85.942935 21.406313 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_2">
      <defs>
       <path id="mf9b4a7530f" d="M 0 0 
L 0 3.5 
" style="stroke: #000000; stroke-width: 0.8"/>
      </defs>
      <g>
       <use xlink:href="#mf9b4a7530f" x="85.942935" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_1">
      <!-- $\mathdefault{10^{2}}$ -->
      <g transform="translate(78.022935 223.336625)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">2</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_2">
     <g id="line2d_3">
      <path d="M 181.063508 207.876625 
L 181.063508 21.406313 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_4">
      <g>
       <use xlink:href="#mf9b4a7530f" x="181.063508" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_2">
      <!-- $\mathdefault{10^{3}}$ -->
      <g transform="translate(173.143508 223.336625)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">3</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_3">
     <g id="line2d_5">
      <path d="M 276.184081 207.876625 
L 276.184081 21.406313 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_6">
      <g>
       <use xlink:href="#mf9b4a7530f" x="276.184081" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_3">
      <!-- $\mathdefault{10^{4}}$ -->
      <g transform="translate(268.264081 223.246625)">
       <text>
        <tspan x="0" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.40625" style="font-size: 6.3px; font-family: 'DejaVu Sans'">4</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_4">
     <g id="line2d_7">
      <defs>
       <path id="m81ce7525d2" d="M 0 0 
L 0 2 
" style="stroke: #000000; stroke-width: 0.6"/>
      </defs>
      <g>
       <use xlink:href="#m81ce7525d2" x="48.090653" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_5">
     <g id="line2d_8">
      <g>
       <use xlink:href="#m81ce7525d2" x="57.308789" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_6">
     <g id="line2d_9">
      <g>
       <use xlink:href="#m81ce7525d2" x="64.840555" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_7">
     <g id="line2d_10">
      <g>
       <use xlink:href="#m81ce7525d2" x="71.208572" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_8">
     <g id="line2d_11">
      <g>
       <use xlink:href="#m81ce7525d2" x="76.724799" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_9">
     <g id="line2d_12">
      <g>
       <use xlink:href="#m81ce7525d2" x="81.590456" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_10">
     <g id="line2d_13">
      <g>
       <use xlink:href="#m81ce7525d2" x="114.57708" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_11">
     <g id="line2d_14">
      <g>
       <use xlink:href="#m81ce7525d2" x="131.326982" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_12">
     <g id="line2d_15">
      <g>
       <use xlink:href="#m81ce7525d2" x="143.211226" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_13">
     <g id="line2d_16">
      <g>
       <use xlink:href="#m81ce7525d2" x="152.429362" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_14">
     <g id="line2d_17">
      <g>
       <use xlink:href="#m81ce7525d2" x="159.961127" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_15">
     <g id="line2d_18">
      <g>
       <use xlink:href="#m81ce7525d2" x="166.329144" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_16">
     <g id="line2d_19">
      <g>
       <use xlink:href="#m81ce7525d2" x="171.845372" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_17">
     <g id="line2d_20">
      <g>
       <use xlink:href="#m81ce7525d2" x="176.711029" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_18">
     <g id="line2d_21">
      <g>
       <use xlink:href="#m81ce7525d2" x="209.697653" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_19">
     <g id="line2d_22">
      <g>
       <use xlink:href="#m81ce7525d2" x="226.447555" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_20">
     <g id="line2d_23">
      <g>
       <use xlink:href="#m81ce7525d2" x="238.331799" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_21">
     <g id="line2d_24">
      <g>
       <use xlink:href="#m81ce7525d2" x="247.549935" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_22">
     <g id="line2d_25">
      <g>
       <use xlink:href="#m81ce7525d2" x="255.0817" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_23">
     <g id="line2d_26">
      <g>
       <use xlink:href="#m81ce7525d2" x="261.449717" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_24">
     <g id="line2d_27">
      <g>
       <use xlink:href="#m81ce7525d2" x="266.965945" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_25">
     <g id="line2d_28">
      <g>
       <use xlink:href="#m81ce7525d2" x="271.831602" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_26">
     <g id="line2d_29">
      <g>
       <use xlink:href="#m81ce7525d2" x="304.818226" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_27">
     <g id="line2d_30">
      <g>
       <use xlink:href="#m81ce7525d2" x="321.568128" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_28">
     <g id="line2d_31">
      <g>
       <use xlink:href="#m81ce7525d2" x="333.452372" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_29">
     <g id="line2d_32">
      <g>
       <use xlink:href="#m81ce7525d2" x="342.670508" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_30">
     <g id="line2d_33">
      <g>
       <use xlink:href="#m81ce7525d2" x="350.202273" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_31">
     <g id="line2d_34">
      <g>
       <use xlink:href="#m81ce7525d2" x="356.57029" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="text_4">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="199.989648" y="236.337328" transform="rotate(-0 199.989648 236.337328)">Expanded objects (log)</text>
    </g>
   </g>
   <g id="matplotlib.axis_2">
    <g id="ytick_1">
     <g id="line2d_35">
      <path d="M 43.040703 171.654755 
L 356.938594 171.654755 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_36">
      <defs>
       <path id="m18186f247b" d="M 0 0 
L -3.5 0 
" style="stroke: #000000; stroke-width: 0.8"/>
      </defs>
      <g>
       <use xlink:href="#m18186f247b" x="43.040703" y="171.654755" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_5">
      <!-- $\mathdefault{10^{0}}$ -->
      <g transform="translate(20.200703 175.884755)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">0</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="ytick_2">
     <g id="line2d_37">
      <path d="M 43.040703 106.988191 
L 356.938594 106.988191 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_38">
      <g>
       <use xlink:href="#m18186f247b" x="43.040703" y="106.988191" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_6">
      <!-- $\mathdefault{10^{1}}$ -->
      <g transform="translate(20.200703 111.173191)">
       <text>
        <tspan x="0" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.40625" style="font-size: 6.3px; font-family: 'DejaVu Sans'">1</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="ytick_3">
     <g id="line2d_39">
      <path d="M 43.040703 42.321627 
L 356.938594 42.321627 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_40">
      <g>
       <use xlink:href="#m18186f247b" x="43.040703" y="42.321627" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_7">
      <!-- $\mathdefault{10^{2}}$ -->
      <g transform="translate(20.200703 46.551627)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">2</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="ytick_4">
     <g id="line2d_41">
      <defs>
       <path id="mdbdf13d04c" d="M 0 0 
L -2 0 
" style="stroke: #000000; stroke-width: 0.6"/>
      </defs>
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="205.467527" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_5">
     <g id="line2d_42">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="197.388169" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_6">
     <g id="line2d_43">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="191.121331" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_7">
     <g id="line2d_44">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="186.000952" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_8">
     <g id="line2d_45">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="181.671733" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_9">
     <g id="line2d_46">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="177.921593" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_10">
     <g id="line2d_47">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="174.613735" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_11">
     <g id="line2d_48">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="152.18818" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_12">
     <g id="line2d_49">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="140.800963" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_13">
     <g id="line2d_50">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="132.721604" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_14">
     <g id="line2d_51">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="126.454767" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_15">
     <g id="line2d_52">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="121.334387" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_16">
     <g id="line2d_53">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="117.005169" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_17">
     <g id="line2d_54">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="113.255029" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_18">
     <g id="line2d_55">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="109.947171" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_19">
     <g id="line2d_56">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="87.521616" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_20">
     <g id="line2d_57">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="76.134399" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_21">
     <g id="line2d_58">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="68.05504" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_22">
     <g id="line2d_59">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="61.788202" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_23">
     <g id="line2d_60">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="56.667823" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_24">
     <g id="line2d_61">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="52.338604" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_25">
     <g id="line2d_62">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="48.588464" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_26">
     <g id="line2d_63">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="45.280606" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="ytick_27">
     <g id="line2d_64">
      <g>
       <use xlink:href="#mdbdf13d04c" x="43.040703" y="22.855051" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="text_8">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="14.038594" y="114.641469" transform="rotate(-90 14.038594 114.641469)">HLD seconds (log)</text>
    </g>
   </g>
   <g id="line2d_65">
    <path d="M 57.308789 199.400702 
L 152.429362 160.207529 
L 247.549935 107.108771 
L 304.818226 53.931815 
L 342.670508 36.069691 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #166c8c; stroke-width: 1.5; stroke-linecap: square"/>
    <defs>
     <path id="m3475c2a3e3" d="M 0 3 
C 0.795609 3 1.55874 2.683901 2.12132 2.12132 
C 2.683901 1.55874 3 0.795609 3 0 
C 3 -0.795609 2.683901 -1.55874 2.12132 -2.12132 
C 1.55874 -2.683901 0.795609 -3 0 -3 
C -0.795609 -3 -1.55874 -2.683901 -2.12132 -2.12132 
C -2.683901 -1.55874 -3 -0.795609 -3 0 
C -3 0.795609 -2.683901 1.55874 -2.12132 2.12132 
C -1.55874 2.683901 -0.795609 3 0 3 
z
" style="stroke: #166c8c"/>
    </defs>
    <g clip-path="url(#p79bf2f4999)">
     <use xlink:href="#m3475c2a3e3" x="57.308789" y="199.400702" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="152.429362" y="160.207529" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="247.549935" y="107.108771" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="304.818226" y="53.931815" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="342.670508" y="36.069691" style="fill: #166c8c; stroke: #166c8c"/>
    </g>
   </g>
   <g id="line2d_66">
    <path d="M 57.308789 194.356056 
L 152.429362 160.062214 
L 247.549935 103.696556 
L 304.818226 61.926577 
L 342.670508 29.882236 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #c06722; stroke-width: 1.5; stroke-linecap: square"/>
    <defs>
     <path id="mb34fea8767" d="M 0 3 
C 0.795609 3 1.55874 2.683901 2.12132 2.12132 
C 2.683901 1.55874 3 0.795609 3 0 
C 3 -0.795609 2.683901 -1.55874 2.12132 -2.12132 
C 1.55874 -2.683901 0.795609 -3 0 -3 
C -0.795609 -3 -1.55874 -2.683901 -2.12132 -2.12132 
C -2.683901 -1.55874 -3 -0.795609 -3 0 
C -3 0.795609 -2.683901 1.55874 -2.12132 2.12132 
C -1.55874 2.683901 -0.795609 3 0 3 
z
" style="stroke: #c06722"/>
    </defs>
    <g clip-path="url(#p79bf2f4999)">
     <use xlink:href="#mb34fea8767" x="57.308789" y="194.356056" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="152.429362" y="160.062214" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="247.549935" y="103.696556" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="304.818226" y="61.926577" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="342.670508" y="29.882236" style="fill: #c06722; stroke: #c06722"/>
    </g>
   </g>
   <g id="line2d_67">
    <path d="M 152.429362 140.267453 
L 247.549935 87.634921 
L 304.818226 50.26589 
" clip-path="url(#p79bf2f4999)" style="fill: none; stroke: #7255a3; stroke-width: 1.5; stroke-linecap: square"/>
    <defs>
     <path id="md95c5da6ad" d="M 0 3 
C 0.795609 3 1.55874 2.683901 2.12132 2.12132 
C 2.683901 1.55874 3 0.795609 3 0 
C 3 -0.795609 2.683901 -1.55874 2.12132 -2.12132 
C 1.55874 -2.683901 0.795609 -3 0 -3 
C -0.795609 -3 -1.55874 -2.683901 -2.12132 -2.12132 
C -2.683901 -1.55874 -3 -0.795609 -3 0 
C -3 0.795609 -2.683901 1.55874 -2.12132 2.12132 
C -1.55874 2.683901 -0.795609 3 0 3 
z
" style="stroke: #7255a3"/>
    </defs>
    <g clip-path="url(#p79bf2f4999)">
     <use xlink:href="#md95c5da6ad" x="152.429362" y="140.267453" style="fill: #7255a3; stroke: #7255a3"/>
     <use xlink:href="#md95c5da6ad" x="247.549935" y="87.634921" style="fill: #7255a3; stroke: #7255a3"/>
     <use xlink:href="#md95c5da6ad" x="304.818226" y="50.26589" style="fill: #7255a3; stroke: #7255a3"/>
    </g>
   </g>
   <g id="patch_3">
    <path d="M 43.040703 207.876625 
L 43.040703 21.406313 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="patch_4">
    <path d="M 43.040703 207.876625 
L 356.938594 207.876625 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="text_9">
    <text style="font-size: 10.8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="199.989648" y="15.406313" transform="rotate(-0 199.989648 15.406313)">Native cost by tier and view</text>
   </g>
   <g id="legend_1">
    <g id="patch_5">
     <path d="M 49.340703 69.108422 
L 100.902266 69.108422 
Q 102.702266 69.108422 102.702266 67.308422 
L 102.702266 27.706313 
Q 102.702266 25.906313 100.902266 25.906313 
L 49.340703 25.906313 
Q 47.540703 25.906313 47.540703 27.706313 
L 47.540703 67.308422 
Q 47.540703 69.108422 49.340703 69.108422 
z
" style="fill: #ffffff; opacity: 0.8; stroke: #cccccc; stroke-linejoin: miter"/>
    </g>
    <g id="line2d_68">
     <path d="M 51.140703 33.194906 
L 60.140703 33.194906 
L 69.140703 33.194906 
" style="fill: none; stroke: #166c8c; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#m3475c2a3e3" x="60.140703" y="33.194906" style="fill: #166c8c; stroke: #166c8c"/>
     </g>
    </g>
    <g id="text_10">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="76.340703" y="36.344906" transform="rotate(-0 76.340703 36.344906)">Top</text>
    </g>
    <g id="line2d_69">
     <path d="M 51.140703 46.695609 
L 60.140703 46.695609 
L 69.140703 46.695609 
" style="fill: none; stroke: #c06722; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#mb34fea8767" x="60.140703" y="46.695609" style="fill: #c06722; stroke: #c06722"/>
     </g>
    </g>
    <g id="text_11">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="76.340703" y="49.845609" transform="rotate(-0 76.340703 49.845609)">Front</text>
    </g>
    <g id="line2d_70">
     <path d="M 51.140703 60.196313 
L 60.140703 60.196313 
L 69.140703 60.196313 
" style="fill: none; stroke: #7255a3; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#md95c5da6ad" x="60.140703" y="60.196313" style="fill: #7255a3; stroke: #7255a3"/>
     </g>
    </g>
    <g id="text_12">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="76.340703" y="63.346313" transform="rotate(-0 76.340703 63.346313)">SW</text>
    </g>
   </g>
  </g>
  <g id="axes_2">
   <g id="patch_6">
    <path d="M 392.780703 207.876625 
L 706.678594 207.876625 
L 706.678594 21.406313 
L 392.780703 21.406313 
z
" style="fill: #ffffff"/>
   </g>
   <g id="matplotlib.axis_3">
    <g id="xtick_32">
     <g id="line2d_71">
      <path d="M 435.682935 207.876625 
L 435.682935 21.406313 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_72">
      <g>
       <use xlink:href="#mf9b4a7530f" x="435.682935" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_13">
      <!-- $\mathdefault{10^{2}}$ -->
      <g transform="translate(427.762935 223.336625)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">2</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_33">
     <g id="line2d_73">
      <path d="M 530.803508 207.876625 
L 530.803508 21.406313 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_74">
      <g>
       <use xlink:href="#mf9b4a7530f" x="530.803508" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_14">
      <!-- $\mathdefault{10^{3}}$ -->
      <g transform="translate(522.883508 223.336625)">
       <text>
        <tspan x="0" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.60175" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.31875" style="font-size: 6.3px; font-family: 'DejaVu Sans'">3</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_34">
     <g id="line2d_75">
      <path d="M 625.924081 207.876625 
L 625.924081 21.406313 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_76">
      <g>
       <use xlink:href="#mf9b4a7530f" x="625.924081" y="207.876625" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_15">
      <!-- $\mathdefault{10^{4}}$ -->
      <g transform="translate(618.004081 223.246625)">
       <text>
        <tspan x="0" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">1</tspan>
        <tspan x="5.726074" y="-0.68925" style="font-size: 9px; font-family: 'DejaVu Sans'">0</tspan>
        <tspan x="11.538281" y="-4.40625" style="font-size: 6.3px; font-family: 'DejaVu Sans'">4</tspan>
       </text>
      </g>
     </g>
    </g>
    <g id="xtick_35">
     <g id="line2d_77">
      <g>
       <use xlink:href="#m81ce7525d2" x="397.830653" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_36">
     <g id="line2d_78">
      <g>
       <use xlink:href="#m81ce7525d2" x="407.048789" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_37">
     <g id="line2d_79">
      <g>
       <use xlink:href="#m81ce7525d2" x="414.580555" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_38">
     <g id="line2d_80">
      <g>
       <use xlink:href="#m81ce7525d2" x="420.948572" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_39">
     <g id="line2d_81">
      <g>
       <use xlink:href="#m81ce7525d2" x="426.464799" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_40">
     <g id="line2d_82">
      <g>
       <use xlink:href="#m81ce7525d2" x="431.330456" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_41">
     <g id="line2d_83">
      <g>
       <use xlink:href="#m81ce7525d2" x="464.31708" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_42">
     <g id="line2d_84">
      <g>
       <use xlink:href="#m81ce7525d2" x="481.066982" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_43">
     <g id="line2d_85">
      <g>
       <use xlink:href="#m81ce7525d2" x="492.951226" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_44">
     <g id="line2d_86">
      <g>
       <use xlink:href="#m81ce7525d2" x="502.169362" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_45">
     <g id="line2d_87">
      <g>
       <use xlink:href="#m81ce7525d2" x="509.701127" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_46">
     <g id="line2d_88">
      <g>
       <use xlink:href="#m81ce7525d2" x="516.069144" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_47">
     <g id="line2d_89">
      <g>
       <use xlink:href="#m81ce7525d2" x="521.585372" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_48">
     <g id="line2d_90">
      <g>
       <use xlink:href="#m81ce7525d2" x="526.451029" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_49">
     <g id="line2d_91">
      <g>
       <use xlink:href="#m81ce7525d2" x="559.437653" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_50">
     <g id="line2d_92">
      <g>
       <use xlink:href="#m81ce7525d2" x="576.187555" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_51">
     <g id="line2d_93">
      <g>
       <use xlink:href="#m81ce7525d2" x="588.071799" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_52">
     <g id="line2d_94">
      <g>
       <use xlink:href="#m81ce7525d2" x="597.289935" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_53">
     <g id="line2d_95">
      <g>
       <use xlink:href="#m81ce7525d2" x="604.8217" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_54">
     <g id="line2d_96">
      <g>
       <use xlink:href="#m81ce7525d2" x="611.189717" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_55">
     <g id="line2d_97">
      <g>
       <use xlink:href="#m81ce7525d2" x="616.705945" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_56">
     <g id="line2d_98">
      <g>
       <use xlink:href="#m81ce7525d2" x="621.571602" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_57">
     <g id="line2d_99">
      <g>
       <use xlink:href="#m81ce7525d2" x="654.558226" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_58">
     <g id="line2d_100">
      <g>
       <use xlink:href="#m81ce7525d2" x="671.308128" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_59">
     <g id="line2d_101">
      <g>
       <use xlink:href="#m81ce7525d2" x="683.192372" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_60">
     <g id="line2d_102">
      <g>
       <use xlink:href="#m81ce7525d2" x="692.410508" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_61">
     <g id="line2d_103">
      <g>
       <use xlink:href="#m81ce7525d2" x="699.942273" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="xtick_62">
     <g id="line2d_104">
      <g>
       <use xlink:href="#m81ce7525d2" x="706.31029" y="207.876625" style="stroke: #000000; stroke-width: 0.6"/>
      </g>
     </g>
    </g>
    <g id="text_16">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="549.729648" y="236.337328" transform="rotate(-0 549.729648 236.337328)">Expanded objects (log)</text>
    </g>
   </g>
   <g id="matplotlib.axis_4">
    <g id="ytick_28">
     <g id="line2d_105">
      <path d="M 392.780703 185.392225 
L 706.678594 185.392225 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_106">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="185.392225" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_17">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="188.81117" transform="rotate(-0 385.780703 188.81117)">1</text>
     </g>
    </g>
    <g id="ytick_29">
     <g id="line2d_107">
      <path d="M 392.780703 155.136416 
L 706.678594 155.136416 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_108">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="155.136416" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_18">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="158.555362" transform="rotate(-0 385.780703 158.555362)">2</text>
     </g>
    </g>
    <g id="ytick_30">
     <g id="line2d_109">
      <path d="M 392.780703 124.880608 
L 706.678594 124.880608 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_110">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="124.880608" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_19">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="128.299553" transform="rotate(-0 385.780703 128.299553)">3</text>
     </g>
    </g>
    <g id="ytick_31">
     <g id="line2d_111">
      <path d="M 392.780703 94.6248 
L 706.678594 94.6248 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_112">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="94.6248" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_20">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="98.043745" transform="rotate(-0 385.780703 98.043745)">4</text>
     </g>
    </g>
    <g id="ytick_32">
     <g id="line2d_113">
      <path d="M 392.780703 64.368991 
L 706.678594 64.368991 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_114">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="64.368991" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_21">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="67.787937" transform="rotate(-0 385.780703 67.787937)">5</text>
     </g>
    </g>
    <g id="ytick_33">
     <g id="line2d_115">
      <path d="M 392.780703 34.113183 
L 706.678594 34.113183 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.25; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_116">
      <g>
       <use xlink:href="#m18186f247b" x="392.780703" y="34.113183" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_22">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="385.780703" y="37.532128" transform="rotate(-0 385.780703 37.532128)">6</text>
     </g>
    </g>
    <g id="text_23">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="373.892344" y="114.641469" transform="rotate(-90 373.892344 114.641469)">Private GiB</text>
    </g>
   </g>
   <g id="line2d_117">
    <path d="M 407.048789 199.400702 
L 502.169362 198.362759 
L 597.289935 191.174027 
L 654.558226 132.408088 
L 692.410508 51.973694 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #166c8c; stroke-width: 1.5; stroke-linecap: square"/>
    <g clip-path="url(#p6400fbbbe4)">
     <use xlink:href="#m3475c2a3e3" x="407.048789" y="199.400702" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="502.169362" y="198.362759" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="597.289935" y="191.174027" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="654.558226" y="132.408088" style="fill: #166c8c; stroke: #166c8c"/>
     <use xlink:href="#m3475c2a3e3" x="692.410508" y="51.973694" style="fill: #166c8c; stroke: #166c8c"/>
    </g>
   </g>
   <g id="line2d_118">
    <path d="M 407.048789 199.126472 
L 502.169362 197.862081 
L 597.289935 188.372862 
L 654.558226 137.874918 
L 692.410508 29.882236 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #c06722; stroke-width: 1.5; stroke-linecap: square"/>
    <g clip-path="url(#p6400fbbbe4)">
     <use xlink:href="#mb34fea8767" x="407.048789" y="199.126472" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="502.169362" y="197.862081" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="597.289935" y="188.372862" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="654.558226" y="137.874918" style="fill: #c06722; stroke: #c06722"/>
     <use xlink:href="#mb34fea8767" x="692.410508" y="29.882236" style="fill: #c06722; stroke: #c06722"/>
    </g>
   </g>
   <g id="line2d_119">
    <path d="M 502.169362 197.760168 
L 597.289935 184.112253 
L 654.558226 115.346261 
" clip-path="url(#p6400fbbbe4)" style="fill: none; stroke: #7255a3; stroke-width: 1.5; stroke-linecap: square"/>
    <g clip-path="url(#p6400fbbbe4)">
     <use xlink:href="#md95c5da6ad" x="502.169362" y="197.760168" style="fill: #7255a3; stroke: #7255a3"/>
     <use xlink:href="#md95c5da6ad" x="597.289935" y="184.112253" style="fill: #7255a3; stroke: #7255a3"/>
     <use xlink:href="#md95c5da6ad" x="654.558226" y="115.346261" style="fill: #7255a3; stroke: #7255a3"/>
    </g>
   </g>
   <g id="patch_7">
    <path d="M 392.780703 207.876625 
L 392.780703 21.406312 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="patch_8">
    <path d="M 392.780703 207.876625 
L 706.678594 207.876625 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="text_24">
    <text style="font-size: 10.8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="549.729648" y="15.406313" transform="rotate(-0 549.729648 15.406313)">Owned-process memory envelope</text>
   </g>
   <g id="legend_2">
    <g id="patch_9">
     <path d="M 399.080703 69.108422 
L 450.642266 69.108422 
Q 452.442266 69.108422 452.442266 67.308422 
L 452.442266 27.706313 
Q 452.442266 25.906313 450.642266 25.906313 
L 399.080703 25.906313 
Q 397.280703 25.906313 397.280703 27.706313 
L 397.280703 67.308422 
Q 397.280703 69.108422 399.080703 69.108422 
z
" style="fill: #ffffff; opacity: 0.8; stroke: #cccccc; stroke-linejoin: miter"/>
    </g>
    <g id="line2d_120">
     <path d="M 400.880703 33.194906 
L 409.880703 33.194906 
L 418.880703 33.194906 
" style="fill: none; stroke: #166c8c; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#m3475c2a3e3" x="409.880703" y="33.194906" style="fill: #166c8c; stroke: #166c8c"/>
     </g>
    </g>
    <g id="text_25">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="426.080703" y="36.344906" transform="rotate(-0 426.080703 36.344906)">Top</text>
    </g>
    <g id="line2d_121">
     <path d="M 400.880703 46.695609 
L 409.880703 46.695609 
L 418.880703 46.695609 
" style="fill: none; stroke: #c06722; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#mb34fea8767" x="409.880703" y="46.695609" style="fill: #c06722; stroke: #c06722"/>
     </g>
    </g>
    <g id="text_26">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="426.080703" y="49.845609" transform="rotate(-0 426.080703 49.845609)">Front</text>
    </g>
    <g id="line2d_122">
     <path d="M 400.880703 60.196313 
L 409.880703 60.196313 
L 418.880703 60.196313 
" style="fill: none; stroke: #7255a3; stroke-width: 1.5; stroke-linecap: square"/>
     <g>
      <use xlink:href="#md95c5da6ad" x="409.880703" y="60.196313" style="fill: #7255a3; stroke: #7255a3"/>
     </g>
    </g>
    <g id="text_27">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="426.080703" y="63.346313" transform="rotate(-0 426.080703 63.346313)">SW</text>
    </g>
   </g>
  </g>
 </g>
 <defs>
  <clipPath id="p79bf2f4999">
   <rect x="43.040703" y="21.406313" width="313.897891" height="186.470312"/>
  </clipPath>
  <clipPath id="p6400fbbbe4">
   <rect x="392.780703" y="21.406313" width="313.897891" height="186.470312"/>
  </clipPath>
 </defs>
</svg>


Rough log fits, descriptive training data only:

| View | Model T ≈ k·N^a | log R² | Typical fit factor |
|---|---|---|---|
| Top | 0.00929·N^0.867 | 0.982 | 1.35× |
| Front | 0.01124·N^0.849 | 0.979 | 1.37× |


Controlled polygonal-prism Top views. Same object count and shape complexity per pair; the depth stack uses exactly aligned projections, not staggered intersections.

| Objects | Sides | Sparse s | Aligned depth-stack s | Aligned / sparse |
|---|---|---|---|---|
| 100 | 4 | 0.548 | 0.584 | 1.07 |
| 100 | 16 | 1.191 | 1.287 | 1.08 |
| 500 | 4 | 1.586 | 1.123 | 0.71 |
| 500 | 16 | 5.881 | 3.281 | 0.56 |
| 2000 | 4 | 7.058 | 4.370 | 0.62 |
| 2000 | 16 | 37.336 | 14.238 | 0.38 |


## Measured Q2: native hidden flag and certified occlusion

Native compute seconds are separate from certification cost. Culling applies a sufficient whole-object convex-shadow certificate. No Hidden-output equivalence is claimed after deleting hidden sources.

| Tier | View | Hidden on s | Hidden off s | Certified removed | Culled HLD s | Certificate s | Culled sum s | vs hidden-off |
|---|---|---|---|---|---|---|---|---|
| L | Front | 49.754 | 15.262 | 1.0% | 15.694 | 3.003 | 18.697 | 0.82× |
| L | Top | 66.139 | 5.962 | 49.6% | 3.888 | 2.969 | 6.857 | 0.87× |
| M | Front | 11.244 | 4.870 | 1.0% | 4.670 | 0.997 | 5.667 | 0.86× |
| M | Top | 9.957 | 3.191 | 44.4% | 2.759 | 0.871 | 3.630 | 0.88× |


Central-half-axis crop (25% frame area), changed requested scope:
| Tier | View | Removed | HLD inputs | Crop HLD s | vs full HLD |
|---|---|---|---|---|---|
| L | Front | 48.9% | 10222 | 23.118 | 2.15× |
| L | Top | 4.0% | 19209 | 70.555 | 0.94× |
| M | Front | 60.8% | 1958 | 5.706 | 1.97× |
| M | Top | 62.6% | 1872 | 4.584 | 2.17× |


Existing Route C section API preparation followed by raw HLD. The last column is the derived best possible gain from eliminating **only HLD** from the sum of these two measured stages, excluding other production stages.
| Tier | View | Retained bodies | Prep s | HLD s | Regions | Uncertain diagnostics | HLD elimination bound |
|---|---|---|---|---|---|---|---|
| M | Plan | 588 | 66.158 | 2.416 | 364 | 0 | 1.037× |
| M | Long | 2650 | 37.947 | 6.766 | 122 | 0 | 1.178× |
| M | Depth | 1082 | 39.285 | 5.348 | 122 | 0 | 1.136× |
| L | Plan | 660 | 164.326 | 2.506 | 418 | 0 | 1.015× |
| L | Long | 9746 | 208.155 | 39.706 | 1299 | 232 | 1.191× |
| L | Depth | 4434 | 183.912 | 18.963 | 1299 | 232 | 1.103× |


A separate M probe rejects only certainly removed cut/depth bodies **before the unchanged section API**. Bounds are expanded by eight tolerances; unknown bounds are retained. All sources participate in these plane requests. Region IDs, source IDs, loop/hole counts, hatch presence and areas rounded to 1e−8 match. This is aggregate topology evidence plus a conservative-bound argument, not a full boundary-locus oracle.
| View | API input count | Original prep s | Bounds s | Filtered prep s | Prep ratio | Topology match | Derived prep+HLD ratio |
|---|---|---|---|---|---|---|---|
| Plan | 5000→588 | 66.158 | 0.103 | 20.749 | 3.17× | True | 2.95× |
| Long | 5000→2650 | 37.947 | 0.108 | 22.450 | 1.68× | True | 1.52× |
| Depth | 5000→1082 | 39.285 | 0.106 | 11.482 | 3.39× | True | 2.64× |

<svg xmlns:xlink="http://www.w3.org/1999/xlink" width="569.811094pt" height="224.279437pt" viewBox="0 0 569.811094 224.279437" xmlns="http://www.w3.org/2000/svg" version="1.1">
 <metadata>
  <rdf:RDF xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:cc="http://creativecommons.org/ns#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
   <cc:Work>
    <dc:type rdf:resource="http://purl.org/dc/dcmitype/StillImage"/>
    <dc:date>2026-11-15T00:25:02.765760</dc:date>
    <dc:format>image/svg+xml</dc:format>
    <dc:creator>
     <cc:Agent>
      <dc:title>Matplotlib v3.11.2, https://matplotlib.org/</dc:title>
     </cc:Agent>
    </dc:creator>
   </cc:Work>
  </rdf:RDF>
 </metadata>
 <defs>
  <style type="text/css">*{stroke-linejoin: round; stroke-linecap: butt}</style>
 </defs>
 <g id="figure_1">
  <g id="patch_1">
   <path d="M 0 224.279437 
L 569.811094 224.279437 
L 569.811094 0 
L 0 0 
z
" style="fill: #ffffff"/>
  </g>
  <g id="axes_1">
   <g id="patch_2">
    <path d="M 38.653203 201.078734 
L 562.611094 201.078734 
L 562.611094 21.406313 
L 38.653203 21.406313 
z
" style="fill: #ffffff"/>
   </g>
   <g id="matplotlib.axis_1">
    <g id="xtick_1">
     <g id="line2d_1">
      <defs>
       <path id="m7e61d0cb34" d="M 0 0 
L 0 3.5 
" style="stroke: #000000; stroke-width: 0.8"/>
      </defs>
      <g>
       <use xlink:href="#m7e61d0cb34" x="122.898807" y="201.078734" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_1">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="122.898807" y="214.917328" transform="rotate(-0 122.898807 214.917328)">Plan: original / bounds</text>
     </g>
    </g>
    <g id="xtick_2">
     <g id="line2d_2">
      <g>
       <use xlink:href="#m7e61d0cb34" x="300.632148" y="201.078734" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_2">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="300.632148" y="214.917328" transform="rotate(-0 300.632148 214.917328)">Long: original / bounds</text>
     </g>
    </g>
    <g id="xtick_3">
     <g id="line2d_3">
      <g>
       <use xlink:href="#m7e61d0cb34" x="478.36549" y="201.078734" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_3">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="478.36549" y="214.917328" transform="rotate(-0 478.36549 214.917328)">3 m depth: original / bounds</text>
     </g>
    </g>
   </g>
   <g id="matplotlib.axis_2">
    <g id="ytick_1">
     <g id="line2d_4">
      <path d="M 38.653203 201.078734 
L 562.611094 201.078734 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_5">
      <defs>
       <path id="m5ec3baf7fe" d="M 0 0 
L -3.5 0 
" style="stroke: #000000; stroke-width: 0.8"/>
      </defs>
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="201.078734" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_4">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="204.49768" transform="rotate(-0 31.653203 204.49768)">0</text>
     </g>
    </g>
    <g id="ytick_2">
     <g id="line2d_6">
      <path d="M 38.653203 176.12517 
L 562.611094 176.12517 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_7">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="176.12517" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_5">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="179.544116" transform="rotate(-0 31.653203 179.544116)">10</text>
     </g>
    </g>
    <g id="ytick_3">
     <g id="line2d_8">
      <path d="M 38.653203 151.171606 
L 562.611094 151.171606 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_9">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="151.171606" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_6">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="154.590551" transform="rotate(-0 31.653203 154.590551)">20</text>
     </g>
    </g>
    <g id="ytick_4">
     <g id="line2d_10">
      <path d="M 38.653203 126.218042 
L 562.611094 126.218042 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_11">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="126.218042" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_7">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="129.636987" transform="rotate(-0 31.653203 129.636987)">30</text>
     </g>
    </g>
    <g id="ytick_5">
     <g id="line2d_12">
      <path d="M 38.653203 101.264478 
L 562.611094 101.264478 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_13">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="101.264478" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_8">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="104.683423" transform="rotate(-0 31.653203 104.683423)">40</text>
     </g>
    </g>
    <g id="ytick_6">
     <g id="line2d_14">
      <path d="M 38.653203 76.310914 
L 562.611094 76.310914 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_15">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="76.310914" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_9">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="79.729859" transform="rotate(-0 31.653203 79.729859)">50</text>
     </g>
    </g>
    <g id="ytick_7">
     <g id="line2d_16">
      <path d="M 38.653203 51.357349 
L 562.611094 51.357349 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_17">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="51.357349" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_10">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="54.776295" transform="rotate(-0 31.653203 54.776295)">60</text>
     </g>
    </g>
    <g id="ytick_8">
     <g id="line2d_18">
      <path d="M 38.653203 26.403785 
L 562.611094 26.403785 
" clip-path="url(#p6acffd5677)" style="fill: none; stroke: #b0b0b0; stroke-opacity: 0.2; stroke-width: 0.8; stroke-linecap: square"/>
     </g>
     <g id="line2d_19">
      <g>
       <use xlink:href="#m5ec3baf7fe" x="38.653203" y="26.403785" style="stroke: #000000; stroke-width: 0.8"/>
      </g>
     </g>
     <g id="text_11">
      <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: end" x="31.653203" y="29.822731" transform="rotate(-0 31.653203 29.822731)">70</text>
     </g>
    </g>
    <g id="text_12">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="14.038594" y="111.242523" transform="rotate(-90 14.038594 111.242523)">Seconds</text>
    </g>
   </g>
   <g id="patch_3">
    <path d="M 62.469471 201.078734 
L 122.898807 201.078734 
L 122.898807 35.990141 
L 62.469471 35.990141 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_4">
    <path d="M 240.202812 201.078734 
L 300.632148 201.078734 
L 300.632148 106.38723 
L 240.202812 106.38723 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_5">
    <path d="M 417.936154 201.078734 
L 478.36549 201.078734 
L 478.36549 103.048779 
L 417.936154 103.048779 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_6">
    <path d="M 62.469471 35.990141 
L 122.898807 35.990141 
L 122.898807 29.962142 
L 62.469471 29.962142 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_7">
    <path d="M 240.202812 106.38723 
L 300.632148 106.38723 
L 300.632148 89.503702 
L 240.202812 89.503702 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_8">
    <path d="M 417.936154 103.048779 
L 478.36549 103.048779 
L 478.36549 89.704856 
L 417.936154 89.704856 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_9">
    <path d="M 122.898807 201.078734 
L 183.328143 201.078734 
L 183.328143 149.047152 
L 122.898807 149.047152 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_10">
    <path d="M 300.632148 201.078734 
L 361.061485 201.078734 
L 361.061485 144.789818 
L 300.632148 144.789818 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_11">
    <path d="M 478.36549 201.078734 
L 538.794826 201.078734 
L 538.794826 172.162414 
L 478.36549 172.162414 
z
" clip-path="url(#p6acffd5677)" style="fill: #166c8c"/>
   </g>
   <g id="patch_12">
    <path d="M 122.898807 149.047152 
L 183.328143 149.047152 
L 183.328143 143.019153 
L 122.898807 143.019153 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_13">
    <path d="M 300.632148 144.789818 
L 361.061485 144.789818 
L 361.061485 127.906289 
L 300.632148 127.906289 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_14">
    <path d="M 478.36549 172.162414 
L 538.794826 172.162414 
L 538.794826 158.818492 
L 478.36549 158.818492 
z
" clip-path="url(#p6acffd5677)" style="fill: #c06722"/>
   </g>
   <g id="patch_15">
    <path d="M 38.653203 201.078734 
L 38.653203 21.406313 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="patch_16">
    <path d="M 38.653203 201.078734 
L 562.611094 201.078734 
" style="fill: none; stroke: #000000; stroke-width: 0.8; stroke-linejoin: miter; stroke-linecap: square"/>
   </g>
   <g id="text_13">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="92.684139" y="27.965857" transform="rotate(-0 92.684139 27.965857)">68.6</text>
   </g>
   <g id="text_14">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="153.113475" y="141.022868" transform="rotate(-0 153.113475 141.022868)">23.3</text>
   </g>
   <g id="text_15">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="270.41748" y="87.507416" transform="rotate(-0 270.41748 87.507416)">44.7</text>
   </g>
   <g id="text_16">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="330.846816" y="125.910004" transform="rotate(-0 330.846816 125.910004)">29.3</text>
   </g>
   <g id="text_17">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="448.150822" y="87.708571" transform="rotate(-0 448.150822 87.708571)">44.6</text>
   </g>
   <g id="text_18">
    <text style="font-size: 8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="508.580158" y="156.822207" transform="rotate(-0 508.580158 156.822207)">16.9</text>
   </g>
   <g id="text_19">
    <text style="font-size: 10.8px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: middle" x="300.632148" y="15.406313" transform="rotate(-0 300.632148 15.406313)">M cut-view phases: early bounds matter more than HLD tuning</text>
   </g>
   <g id="legend_1">
    <g id="patch_17">
     <path d="M 438.775313 55.607719 
L 556.311094 55.607719 
Q 558.111094 55.607719 558.111094 53.807719 
L 558.111094 27.706313 
Q 558.111094 25.906313 556.311094 25.906313 
L 438.775313 25.906313 
Q 436.975313 25.906313 436.975313 27.706313 
L 436.975313 53.807719 
Q 436.975313 55.607719 438.775313 55.607719 
z
" style="fill: #ffffff; opacity: 0.8; stroke: #cccccc; stroke-linejoin: miter"/>
    </g>
    <g id="patch_18">
     <path d="M 440.575313 36.344906 
L 458.575313 36.344906 
L 458.575313 30.044906 
L 440.575313 30.044906 
z
" style="fill: #166c8c"/>
    </g>
    <g id="text_20">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="465.775313" y="36.344906" transform="rotate(-0 465.775313 36.344906)">Section preparation</text>
    </g>
    <g id="patch_19">
     <path d="M 440.575313 49.845609 
L 458.575313 49.845609 
L 458.575313 43.545609 
L 440.575313 43.545609 
z
" style="fill: #c06722"/>
    </g>
    <g id="text_21">
     <text style="font-size: 9px; font-family: 'DejaVu Sans', 'Bitstream Vera Sans', 'Computer Modern Sans Serif', 'Lucida Grande', 'Verdana', 'Geneva', 'Lucid', 'Arial', 'Helvetica', 'Avant Garde', sans-serif; text-anchor: start" x="465.775313" y="49.845609" transform="rotate(-0 465.775313 49.845609)">Native HLD</text>
    </g>
   </g>
  </g>
 </g>
 <defs>
  <clipPath id="p6acffd5677">
   <rect x="38.653203" y="21.406313" width="523.957891" height="179.672422"/>
  </clipPath>
 </defs>
</svg>


## Measured Q4: native threading

| Tier-view | n each | MT off median s | MT on median s | Speed ratio | Off pieces | On pieces |
|---|---|---|---|---|---|---|
| M-Top | 3 | 13.584 | 18.077 | 0.75× | [6860, 6860, 6860] | [6886, 6991, 6762] |
| M-Front | 3 | 14.620 | 17.541 | 0.83× | [11450, 11450, 11450] | [11499, 11508, 11448] |
| L-Top | 3 | 79.311 | 134.911 | 0.59× | [19060, 19060, 19060] | [20281, 20394, 19463] |
| L-Front | 3 | 59.704 | 70.595 | 0.85× | [45437, 45437, 45437] | [45755, 45068, 45601] |


## Measured Q5: census and native cost counterfactual

| Tier | View | MVP convex objects | Convex edges | Projection components | Eligible component objects | Routable edge proxy |
|---|---|---|---|---|---|---|
| XS | Top | 92.00% | 89.47% | 2 | 0 | 0.00% |
| XS | Front | 92.00% | 89.47% | 1 | 0 | 0.00% |
| S | Top | 93.40% | 93.32% | 2 | 0 | 0.00% |
| S | Front | 93.40% | 93.32% | 1 | 0 | 0.00% |
| M | Top | 93.70% | 93.99% | 2 | 0 | 0.00% |
| M | Front | 93.70% | 93.99% | 1 | 0 | 0.00% |
| L | Top | 93.81% | 94.12% | 4 | 200 | 1.00% |
| L | Front | 93.81% | 94.12% | 1 | 0 | 0.00% |
| XL | Top | 93.74% | 93.29% | 1 | 0 | 0.00% |
| XL | Front | 93.74% | 93.29% | 4 | 0 | 0.00% |


Curved-only subscene / complete-scene native time is a **cost counterfactual**, not an additive attribution, a mathematical bound or a verified hybrid speedup. Removing planar foreign occluders changes the task. SubD-to-Brep normalization participates here; Fable skipped the raw SubD. XL uses an unpaired earlier full-scene reference; other rows are paired within one worker.
| Scene-view | Curved objects | All objects | Curved edge share | All HLD s | Curved-only s | Time ratio | All / curved-only |
|---|---|---|---|---|---|---|---|
| share-curved-M-Front | 5000 | 5000 | 100.00% | 287.666 | 290.566 | 101.0% | 0.99× |
| share-M-Top | 210 | 5000 | 1.80% | 13.389 | 0.971 | 7.3% | 13.79× |
| share-M-Front | 210 | 5000 | 1.80% | 14.024 | 1.169 | 8.3% | 12.00× |
| share-M-SW | 210 | 5000 | 1.80% | 22.920 | 2.182 | 9.5% | 10.51× |
| share-L-Top | 826 | 20000 | 1.74% | 99.109 | 4.549 | 4.6% | 21.79× |
| share-L-Front | 826 | 20000 | 1.74% | 70.738 | 5.619 | 7.9% | 12.59× |
| share-L-SW | 826 | 20000 | 1.74% | 119.682 | 11.552 | 9.7% | 10.36× |
| share-XL-Top | 2088 | 50000 | 1.77% | 124.934 | 8.069 | 6.5% | 15.48× |
| share-XL-Front | 2088 | 50000 | 1.77% | 155.726 | 13.122 | 8.4% | 11.87× |


## Measured Q3: reduced tiling sample

| Tier | View | Grid | Input duplication | HLD sum s | Warm wall s | Private GiB | HLD speedup | Missing m | Extra m | Source missing m |
|---|---|---|---|---|---|---|---|---|---|---|
| M | Top | 2×2 | 1.05× | 17.630 | 18.208 | 0.80 | 0.56× | 122.651 | 28.264 | 185.310 |
| M | Top | 4×4 | 1.16× | 33.727 | 34.494 | 0.85 | 0.30× | 130.945 | 15.951 | 190.571 |
| M | Front | 2×2 | 1.12× | 19.205 | 19.641 | 0.92 | 0.59× | 105.653 | 64.981 | 336.346 |
| M | Front | 4×4 | 1.32× | 29.712 | 30.459 | 0.96 | 0.38× | 85.821 | 68.448 | 337.271 |
| L | Top | 2×2 | 1.15× | 100.949 | 102.282 | 1.81 | 0.66× | 411.892 | 81.339 | 765.931 |
| L | Top | 4×4 | 1.16× | 129.152 | 130.806 | 1.79 | 0.51× | 290.116 | 122.173 | 739.109 |
| L | Front | 2×2 | 1.04× | 65.845 | 67.381 | 2.15 | 0.76× | 498.947 | 291.716 | 1,556.736 |
| L | Front | 4×4 | 1.09× | 70.499 | 72.250 | 1.85 | 0.71× | 489.688 | 322.087 | 1,615.430 |


Two owned separate-process shards, with the same tile inputs and native multithreading off. The warm parallel value is the slower shard operation; cold elapsed includes both independent captures and output extraction. Peak sums are upper bounds, not synchronized aggregate measurements.
| Tier | View | Grid | Serial warm s | Parallel warm max s | Parallel cold s | Parallel HLD sum s | Private peak sum GiB |
|---|---|---|---|---|---|---|---|
| M | Front | 4 | 30.459 | 15.077 | 20.473 | 29.333 | 1.54 |


## Measured Q6: repeated-instance preparation prototype

| Form | View | Definition keys | Local plane groups | Clip s | Reused clip s | Regions s | Reused regions s | Silhouette s | Reused silhouette s | Silhouette nulls direct/reused |
|---|---|---|---|---|---|---|---|---|---|---|
| reuse-M-blocks | reuse-Plan | 15 | 194 | 32.349 | 1.211 | 51.989 | 1.142 | 5.652 | 0.035 | 0/0 |
| reuse-M-blocks | reuse-Long | 15 | 114 | 26.279 | 1.012 | 27.496 | 0.610 | 5.927 | 0.040 | 420/420 |
| reuse-M-exploded | reuse-Plan | 5000 | 5000 | 28.292 | 26.616 | 46.980 | 27.842 | 5.478 | 5.718 | 0/0 |
| reuse-M-exploded | reuse-Long | 5000 | 5000 | 29.287 | 28.805 | 30.217 | 24.606 | 5.845 | 5.218 | 420/420 |


Region reuse omits the global inter-source overlap pass and is an optimistic local-geometry prototype. Silhouette candidates are a prospective stage; the current M2 adapter asks native HLD to generate them internally. Neither ratio is a measured whole-solve speedup.

## Output-equivalence audit

Lengths below sum uncovered intervals over emitted pieces in metres; they are not unique union lengths. Zero refers to the stated tolerance/polyline comparator, not an independent exact drawing oracle.

| Tier | View | Hidden flag missing | Hidden flag extra | Certified cull missing | Certified cull extra | Crop missing | Crop extra |
|---|---|---|---|---|---|---|---|
| M | Top | 0.000 | 0.449 | 0.000 | 0.000 | 92.788 | 19.101 |
| M | Front | 0.747 | 2.269 | 0.000 | 0.000 | 34.864 | 30.275 |
| L | Top | 0.000 | 0.898 | 0.000 | 0.000 | 269.600 | 283.993 |
| L | Front | 2.248 | 22.572 | 0.000 | 0.000 | 198.745 | 175.547 |

The first pair compares only Visible/Point output, hidden-on versus hidden-off. The second compares unculled versus certified-culled hidden-off output. The crop pair compares clipped full-scene output to a smaller native solve. Source/class audits are preserved in each JSON. The same-worker warm M Front hidden-flag control reproduces 0.747 m missing and 2.269 m extra Visible piece-locus.

| Tier | View | Off signatures / 3 | On signatures / 3 | Off repeat missing | Off repeat extra | On repeat missing | On repeat extra |
|---|---|---|---|---|---|---|---|
| M | Top | 1 | 3 | 0.000 | 0.000 | 43.751 | 32.437 |
| M | Front | 1 | 3 | 0.000 | 0.000 | 76.952 | 87.867 |
| L | Top | 1 | 3 | 0.000 | 0.000 | 228.653 | 190.381 |
| L | Front | 1 | 3 | 0.000 | 0.000 | 481.471 | 360.247 |


Small feature controls; section Plan is body-only and uses z=1.2 m, so this is not the sidecar analytic cut oracle.
| Fixture-view-grid | Missing m | Extra m | Points equal | Region topology equal | Cross-tile duplicate m |
|---|---|---|---|---|---|
| curves-points-Plan-g2 | 0.000 | 0.000 | True | True | 0.000 |
| curves-points-Plan-g4 | 0.000 | 0.000 | True | True | 0.000 |
| curves-points-Top-g2 | 0.000 | 0.000 | True | None | 0.000 |
| curves-points-Top-g4 | 0.000 | 0.000 | True | None | 0.000 |
| cut-topology-Plan-g2 | 0.000 | 0.000 | True | True | 0.000 |
| cut-topology-Plan-g4 | 0.000 | 0.000 | True | True | 0.000 |
| cut-topology-Top-g2 | 0.000 | 0.000 | True | None | 0.000 |
| cut-topology-Top-g4 | 0.000 | 0.000 | True | None | 0.000 |


Separate-process versus serial identical tile inputs: canonical equality True, class missing/extra 0.000/0.000 m; source missing/extra 0.000/0.000 m.

## Repeated-instance interpretation

Cold capture/world preparation, one fresh process per form (indicative n=1):
| Form | Objects | Geometry keys | Capture hits | Capture s | World prep s |
|---|---|---|---|---|---|
| blocks | 5000 | 15 | 4985 | 0.480 | 0.181 |
| exploded | 5000 | 5000 | 0 | 0.673 | 0.233 |

| Form | View | Capture hits | Geometry keys | Current per-object calls | Local plane groups | Direction groups | Piece count match | Region count match | Loop count match | Area delta m² |
|---|---|---|---|---|---|---|---|---|---|---|
| reuse-M-blocks | reuse-Plan | 4985 | 15 | 5000 | 194 | 15 | True | True | True | 0.00000000 |
| reuse-M-blocks | reuse-Long | 4985 | 15 | 5000 | 114 | 15 | True | True | True | -0.00000000 |
| reuse-M-exploded | reuse-Plan | 0 | 5000 | 5000 | 5000 | 5000 | True | True | True | 0.00000000 |
| reuse-M-exploded | reuse-Long | 0 | 5000 | 5000 | 5000 | 5000 | True | True | True | 0.00000000 |

Current code reuses definition-local normalization and one prepared world scene, but physical trim/cap and section-region calls remain per source/per view. It has no external silhouette-candidate reuse stage: native HLD generates those candidates internally. The prototype measures that prospective stage separately. Block and exploded cold capture/world-prep times remain in their identities. Grouping uses the definition key and transformed local plane (11-decimal rounding for this prototype); production keys need tolerance/contact witnesses, styles/scope, exact transform semantics, ownership and invalidation. Nonuniform placements are excluded from this reuse prototype. Count/area agreement is useful evidence, not complete region-boundary equivalence. The reused region path omits the baseline global overlap pass, so its apparent gain on exploded geometry must not be attributed to definition reuse. Early bounds and definition reuse avoid some of the same calls: do not multiply their measured speed ratios; benchmark the combined pipeline. No full reuse implementation or mixed native routing is built.

## Reproduction and artifact inventory

The source worktree was verified unchanged and removed normally after the probes. The frozen archive and manifest remain. Recreate `src` at the recorded commit before rebuilding `harness/Spike.csproj`; outputs are directed outside `src`. The MVP census loads the original `C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/mvp/Frame2D.Core/bin/Release/net48/Frame2D.Core.dll`; its byte-identical evidence copy is retained if that external research directory is later removed. Preserve or restore that path for a byte-identical harness replay.

```powershell
$spike = "C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114"
# From the original project, recreate only when replaying after cleanup:
git worktree add "$spike/src" 5f89d8264c6bf60662b531e7c0ee3130b2f00ff4 --detach
dotnet build "$spike/harness/Spike.csproj" -c Release
# Run exactly one recorded batch into a new output directory:
python "$spike/harness/replay.py" "$spike/results/cull-M-Top/config.json" --out "$spike/replay-cull-M-Top"
```

Replay acquires the shared suite mutex and uses the owned-job watchdog. Do not rerun the entire coordinator scripts: they can overwrite their fixed result folders, and the recorded results combine reprioritized per-batch schedules. Configs and per-batch identities are the experiment authority. Independent tile shards were the sole intentional simultaneous headless solves. Workers were released between batches; other agents’ processes were never attached to or terminated.

Analysis: run `harness/stats.py`, then `harness/analyze.py tiles --tiers M,L` and `harness/analyze.py features`; regenerate already-existing audit JSON only intentionally. `harness/tables.py` produces the inline SVG tables/plots. [Artifact manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/artifact-manifest.json) hashes harness sources/binaries, evidence, configurations, measurements and audit artifacts; [source cleanup](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/source-cleanup.json) records the unchanged source check and normal worktree removal. [Input integrity](C:/Users/tomosandego/.t3/scratch/m2d-plus-s3-20261114/input-integrity.json) verifies all nine unique model/sidecar pairs against their recorded worker identities. Scratch Python packages support only offline analysis. No product source changes, commits, pushes or installation were made.
