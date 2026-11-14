# M2D+ performance and section feasibility research

Research date: 2026-11-14. **Measured results**, **derived bounds**, **historical evidence**, and **untested estimates** are labelled separately. No product code was changed or installed. The old workspace and interactive Rhino sessions were left untouched.

## Decision supported by the measurements

Build an immutable, instance-aware prepared scene and a separate per-object section-region pipeline. Keep native HLD as the main general-purpose exact-curve baseline and retain the MVP convex interval solver as a restricted repair path. Sharing silhouettes/meshes is promising, but **it does not remove global inter-object visibility cost**. On the supplied full campus plan, native HLD consumed 93.6% of external solve time. A general Custom replacement is not supported by the reliability or performance evidence.

The MVP's existing cache is not enough: it retained view/placement-dependent geometry, had zero hits in the new 48-sphere cross-view experiments, and generated almost 4.8 million mesh faces for those 48 spheres at the selected tolerance. Cache capacity alone did not explain the misses.

## Machine, binary and method

| Item | Value |
|---|---|
| Host | Windows 11 Pro, build 10.0.26300 |
| CPU | AMD Ryzen AI MAX+ 395, 16 physical cores / 32 logical processors |
| Visible RAM | 66,739,012 KiB, approximately 63.65 GiB |
| Rhino / runtime | Rhino 8.36.26296.17001 / .NET 8.0.34 |
| Python | 3.12.9, Anaconda build, task's existing venv, Rhino.Inside |
| Core target | MVP assembly targets net48; loaded into Rhino's .NET 8 runtime |
| Core SHA-256 | `a4433126988cd51b9e779e0894b9c2b9aea7c22ad2de533e148de83c9e9ebc26` |
| GHa SHA-256 | `d819af0565e12bed6fc1d5404667b1e036bb7493981fb0643735df6a51d058cd` |
| Model SHA-256 | `02a56b5803d0d6a80415bcd4a3b2b0cf44e1377ed9c7746a3cd50903238aa324` |

Evidence: [machine.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/machine.json), [performance.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/performance.json), [large-native-plan-roi.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/large-native-plan-roi.json), [reuse_validation.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/reuse_validation.json), [controls.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/controls.json). The [scratch manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/artifact-manifest.json) identifies support assemblies, source/scripts and fixtures. Result files record the actually loaded DLL and harness hashes. Old failed harness pilots are excluded, retained and explained in geometry-matrix.md.

All headless workers ran **serially**, in fresh licensed .NET 8 processes. Normal limits were 12 GiB aggregate private memory and a per-experiment wall cap; the large worker had a 300 s wall cap and requested cancellation at 150 s per solve. Memory peaks are sampled aggregate private bytes of the owned launcher and descendants, not exact high-water marks. No unrelated process was terminated. There was no clean-machine benchmark isolation, randomized engine order, cold-disk guarantee, GPU implementation, or interactive GH/redraw benchmark.

The standard performance/section fixtures use metres and 0.0001 tolerance. The supplied large harness uses its model manifest's tolerance (0.001). Do not compare their timings as if tolerance, route or geometry scope were identical. Timing intervals exclude Rhino startup/model read unless stated; independent GH output tests exclude solve/placement/validation/disposal. Repeated output signatures demonstrate equivalence within that check, not drawing truth.

## Measured: repeated building scene and six views

Generated [305-object building](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/repeated_building_305.3dm): five storeys, 250 repeated box panels, 50 curved cylindrical columns and five slabs. Native mode, full interacting scene every time, no crop, hidden included, two serial trials per view in one worker. External solve wall seconds:

| View | Trials | Median | Output pieces |
|---|---|---:|---:|
| Plan | 1.814, 2.798 | 2.306 s | 214 |
| North elevation | 1.596, 1.993 | 1.795 s | 1,020 |
| South elevation | 1.545, 1.886 | 1.716 s | 1,020 |
| East elevation | 1.674, 1.782 | 1.728 s | 170 |
| West elevation | 1.928, 1.765 | 1.846 s | 163 / 168 |
| Oblique | 3.105, 1.808 | 2.457 s | 3,764 |

These are modest timings on a synthetic regular scene, not a minutes-to-interactive claim for a real building. Variation is substantial at n=2. West output counts varied, so no complete output-equivalence claim is made for that view.

Typical phase magnitudes in these trials:

- Shared preparation including fingerprint: approximately 0.05–0.14 s; native parameter construction 0.004–0.008 s.
- Native HLD compute: approximately 1.4–2.9 s; owned curve extraction approximately 0.01–0.04 s.
- North output: placement median 3.61 ms, GH tree/curve/metadata build median 6.24 ms for 1,020 pieces. This excludes GH publication, document solution, viewport redraw and bake.
- Preparing the unchanged whole scene once took 43.7 ms. Calling NativeEngine directly on that owned prepared scene still took 1.706 and 1.633 s. Removing that preparation cannot remove HLD's main cost.

**Measured culling control:** a 5×5 crop supplied all 305 objects, rejected 270 by bounds, retained 35 fragments and returned 19 pieces in 1.663 s. This is reduced scope, not equivalent full-scene output. Small-case native overhead dominates enough that the crop is not a large speedup.

## Measured: 527 MiB campus model

The old model was copied into scratch before use: [frame2d-large-campus.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/frame2d-large-campus.3dm), [manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/frame2d-large-campus.json). It is 552,924,158 bytes, 2,983 objects, including 2,113 Breps, 24 extrusions, 18 NURBS surfaces, 721 curves and 107 meshes. It contains about 14.0 million mesh triangles, predominantly tiled terrain. **This is not a Brep-heavy 527 MiB building or a 50k-object benchmark.**

New run, Native mode, n=1 per view:

| Scope | External solve | HLD compute | Input / retained | Pieces | Status |
|---|---:|---:|---:|---:|---|
| Entire model plan | **89.682 s** | **83.902 s** | 2,983 / 2,983 | 110,494 | ok |
| One terrain-tile crop | **2.731 s** | **0.945 s** | 2,983 / 1 | 1,024 | ok |

Startup 1.82 s; model read 3.32 s. The worker's entire 156.52 s includes both solves, inspection and repeated GH/layout benchmarks. Sampled aggregate peak private memory: **5.59 GiB**. Native output contained no invalid curves. `ok` and valid curves do not establish whole-model correctness.

### Full-plan phase accounting

Measured timer values and derived exclusive residuals, seconds:

| Phase | Seconds | Basis |
|---|---:|---|
| Input validation | 0.288 | Direct timer; outside core total timer |
| Geometry/options fingerprint | 0.682 | Direct timer; included in shared preparation |
| Geometry copy / physical preparation | 1.334 | Shared preparation minus fingerprint, explicit Native route |
| Native AddGeometry/viewport preparation | 0.088 | Direct timer |
| Native HLD compute | 83.902 | Direct timer |
| Owned native curve extraction and native cleanup | 1.274 | Native total minus preparation and compute |
| Output clipping / cuts / source mapping / wrapper finish | 1.742 | Core total minus shared preparation and native total; not an isolated ClipSegments timer |
| External remainder, including disposal/call overhead | 0.372 | External wall minus core total and validation |

Native's internal preprocessing/tessellation is opaque within HLD compute; there is no evidence to assign it a separate meshing percentage. The full-plan native adapter does not use the Custom mesh cache.

**Derived bound, not a measured speedup:** eliminating every part of this solve outside HLD compute would improve 89.682/83.902 ≈ **1.07×** at most, unless the optimization also reduces native work. Removing just fingerprint/preparation would save only about 2 s here. The terrain crop has a different cost balance: fingerprint 0.676 s, shared preparation 1.263 s, native compute 0.945 s, and a 0.193 s mesh-boundary RTree build. It rejects 2,982 sources and uses 1,024 indexed boundary queries with 7,496 shortlisted edges and zero exhaustive fallbacks.

The supplied isolated probe's recorded hidden interval matches the manifest's [−41,−39] with boundary/symmetric-difference error about 4.36e−6, below the model tolerance. The existing harness reports oracle_check=null because it only scores the manifest's `oracle_roi` view name; the new run used full_plan. This interval reassessment does not certify the other 110k pieces or independently re-prove every foreign object's disjointness.

### Measured GH costs on exactly these curves

Full-plan placement median **0.216 s**, n=3, 110,494 pieces. Actual GH output builder A/B on the same placement, n=5 randomized order per pair:

| Materialization path | Median |
|---|---:|
| Frozen legacy output loop | 0.951 s |
| Current MVP builder | 0.616 s |

All path/geometry-CRC/metadata signatures matched. Both paths made **220,278 independent curve copies** and 110,494 metadata records; nothing was omitted to get the difference. This is a 1.54× builder-stage ratio, not a 1.54× drawing speedup. The current builder remains worth retaining.

Actual private GH input reader and fingerprint stages, emulated on all 2,983 source objects, n=3: borrowed input read median **34.5 ms**, cache key **691.7 ms**, owned snapshot **302.1 ms**. These are not full GH document solution timings. A drawing cache hit still pays the key scan in the present design.

### Historical evidence retained, not rerun here

The handoff's matching final Core build completed Auto full plans at median 86.724 s, with approximately 141k pieces, but full sections were partial. It also recorded a native full elevation stopped by a **241.2 s wall watchdog at sampled 22.55 GiB**, and an earlier 16 GiB-limited elevation worker. These remain historical observations with their recorded route/budgets, not current rerun measurements. See [inherited native elevation](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/inherited/release-native-elevation-upper-budget.json), [inherited full section](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/inherited/release-full-section.json), [handoff study](C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/output/frame2d-mvp/docs/PERFORMANCE_STUDY.md).

Do not compare the new Native 110,494 pieces directly with the old Auto 141k pieces as a speedup or correctness loss. Routes/output policy differ. Full elevation/oblique time/RAM hazards were not re-run because the inherited bounded failures already established the planning risk; a second expensive timeout would add little evidence.

## Measured: cross-view cache behavior

48 identical radius-0.45 spheres at different positions, no cuts, 0.0001 tolerance, Custom, all scene geometry retained:

| View | Cache disabled | Existing 256 MiB cache | Cache hits after cached solve |
|---|---:|---:|---:|
| Top | 9.495 s | 9.451 s | 0 |
| Oblique | 9.547 s | 9.724 s | 0 |
| Side | 9.578 s | 12.465 s | 0 |

All six results were **unresolved**; this is cost evidence, not acceptable-quality output. The cache held only 41 placements (about 251 MiB estimated) and incurred 103 evictions across the three cached views. Default sequence ordering allows cyclic eviction; this is not useful definition reuse.

A separate **512 MiB capacity control** held all 48 entries after top view, with zero evictions, but the oblique view still produced **48 new misses, zero hits**, increasing to 83 retained entries and then 13 evictions. Times were 9.558 / 9.523 s, both unresolved. Thus larger capacity alone does not fix cross-view reuse. The pipeline transforms geometry into/out of view coordinates and keys scene-centered geometry plus high-precision scene-span-derived meshing settings; view-induced coordinate/settings changes are plausible key differences. Exact key differences were not instrumented, so the root cause is not asserted beyond the measured miss behavior.

The two views generated **4,790,040 / 4,790,263 mesh faces**. That cost is disproportionate to 48 simple shapes, with silhouette/ray uncertainty still unresolved. Definition-local meshes, a documented quality/error policy and tolerance-aware invalidation are higher priorities than merely enlarging this LRU.

## Measured/prototyped: repeated instances and non-block copies

### Definition mesh reuse

300 translated/quarter-turned cylinders, same definition and meshing settings, n=3:

- Mesh each expanded Brep: median **0.5045 s**.
- Mesh one definition then duplicate/transform its meshes: median **0.01145 s**.

The stage ratio is about **44×**, but this is **not an exact-output comparison**: Rhino's independent meshes contained 55,177 faces total versus 56,400 for shared copies. Transforming an already generated mesh preserves its selected discretization; regenerating in different coordinates did not produce identical tessellation. This supports avoiding repeated tessellation, not a certified visibility speedup. No complete BVH reuse or inter-object occlusion was included.

### Relative view directions

For a rigid instance transform T and parallel camera direction d, definition-local direction is T⁻¹d. The reuse key must also include feature policy/tolerance, definition revision and transformation class. Direction equality alone does not encode section-plane position, crop, global occluders or in-plane output placement.

The first 300-cylinder top-view prototype produced 600 candidate curves either way: median **0.1451 s** for independent silhouettes, **0.00354 s** for one definition plus curve copies/transforms, n=3. It is a symmetric case and only establishes stage feasibility.

A stronger test used **100 asymmetric elliptical cylinders**, four rotations and three camera directions. For each instance, independent world silhouettes were compared with transformed definition-local silhouettes using symmetric nearest-curve checks at 17 arc-length samples per curve. Results:

| View | Relative direction groups | Independent median | Reuse median | Sampled locus max error |
|---|---:|---:|---:|---:|
| Top | 1 | 58.60 ms | 7.49 ms | 9.5e−16 |
| Oblique | 4 | 57.81 ms | 7.06 ms | 1.1e−14 |
| Side | 4 | 221.41 ms | 11.64 ms | 1.8e−15 |

All nine repetitions passed that sampled-locus check at 0.0001 tolerance. These are approximately **7.8× / 8.2× / 19.0× candidate-generation ratios**, not whole-solve speedups or a formal Hausdorff proof. Self-occlusion and global occlusion were excluded. Cache at exact/canonical direction groups first; quantizing nearly equal angles without an error bound risks silhouette changes and is not justified by this test.

### Native transformed AddGeometry

Native HLD accepted definition geometry plus per-object Transform. For 300 cylinders, expanded inputs median **1.442 s**, shared definition plus transforms **1.466 s**, n=2. All counts matched in this test. **No native full-solve benefit was demonstrated.** Merely passing transforms to HLD does not expose a reusable HLD/BVH scene to the plugin.

### Detecting non-block duplicates

A cheap prototype combined a translation-normalized geometry CRC with a rigid-invariant coarse bucket (face count, sorted edge lengths, volume). For the 300 known copies, combined fingerprint construction took **0.133 s**: translation-normalized CRC split them into **73 buckets**, while the coarse rigid-invariant signature grouped all into one.

The coarse bucket is **not proof of equivalent shape**. Unrelated/chiral shapes can share those quantities; symmetric shapes have ambiguous reference frames; trim topology/parameterization may differ; floating transforms perturb exact CRCs. Recommended design: prefer explicit block definition IDs; for ordinary copies, use the coarse bucket only to shortlist, then solve/verify a rigid transform using topology/landmarks and geometry comparison within an explicit error budget. Store the verified transform and mirror class. Robust general duplicate recognition was not implemented or timed.

## Measured: native threading and repeatability

The API exposes a `multipleThreads` switch and cancellation/progress overloads; it does not document a general contract allowing arbitrary concurrent HLD.Compute calls against shared mutable Rhino geometry. See McNeel [Compute reference](https://developer.rhino3d.com/api/rhinocommon/rhino.geometry.hiddenlinedrawing/compute) and a [McNeel developer explanation of internal threading](https://discourse.mcneel.com/t/make2d-speed-test/120545/4). Internal native parallelism is separate from reentrancy across views. No simultaneous Compute stress was run; all processes and calls were serial.

A targeted control revisited the variable west elevation on identical frozen sources. Input geometry DataCRCs stayed unchanged:

| Native threading | Three piece counts | Visible length | Hidden lengths | Signatures |
|---|---|---:|---|---|
| On | 161, 164, 171 | ≈278.79994 | 2.98006, 3.52006, 4.78006 | All different |
| Off | 165, 165, 165 | 278.800000003 | 3.700000005 each | All identical |

Times overlapped: on 1.465–1.845 s; off 1.299–1.706 s. These small n=3 results do not establish a universal speed advantage for either setting or explain the native differences. The visible totals are almost unchanged; differing hidden/duplicate retention at coincident projections is a stronger suspicion than missing large visible features, but full locus truth was not adjudicated. This is a regression fixture and a reason to preserve a deterministic diagnostic mode, not evidence that HLD is generally thread-unsafe.

Start with one budgeted native solve at a time per model/process. Pure immutable indexing/mathematics and independent owned geometry preprocessing can later be parallelized after contract testing. Unbounded per-view Task.Run calls risk RAM multiplication and CPU oversubscription even if a particular kernel operation is reentrant.

## Measured: closed section regions and hatches for 500 objects

Fixture: [section_500_objects.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/section_500_objects.3dm), 200 boxes, 150 hollow RHS and 150 hollow CHS, all crossed at z=0, spaced apart, no union across objects. RHS/CHS were Boolean-constructed and checked by volume before use. This deliberately tests hollow topology rather than a failed inner-profile constructor.

Three trials performed per-object BrepPlane intersection → JoinCurves → closed-loop check → CreatePlanarBreps → Hatch.Create. Median external total **1.775 s**, range **1.726–1.807 s**:

| Stage | Median |
|---|---:|
| Exact intersections | 0.958 s |
| Curve joining | 0.040 s |
| Planar region construction + area checks | 0.522 s |
| Hatch creation + boundary/area inspection | 0.232 s |

Stage sums differ slightly from total due wrapper work and independent medians. This is a Python-controlled RhinoCommon research implementation, not a tuned C# lower bound. It includes geometry/area inspection and disposals; no native hidden-line solve is included.

All three trials: **800 closed loops, zero open loops, 500 planar regions, 500 valid hatches, 300 inner loops, zero exceptions**. Analytic total area is **1,346.3318379996 m²**; measured hatch boundary area agrees to rounding, and planar-region area differs by about 3e−6 m². Hatch creation was given all loops **per object together**; creating separate hatches for outer and inner loops would incorrectly fill holes.

The MVP's physical section preparation on those same 500 objects took **2.825 s**, yielding 500 retained fragments and 800 Cut segments, status ok. This is a different scope: it trims/caps occluding geometry but does not build regions/hatches. The stage costs cannot simply be added/subtracted as an optimized replacement claim.

The timed file export contained **500 object-specific Cut layers, 800 boundary curves, 500 Hatch objects**. File/layer/geometry construction took **1.041 s**, file writing **0.215 s**, n=1. Reopening caught a separate export defect: every Hatch was geometrically valid, but pattern index 0 had no definition in the File3dm pattern table. The original timed [export](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/section_500_hatched.3dm) is retained as evidence. The usable companion is [section_500_hatched_resolved.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/section_500_hatched_resolved.3dm), with an explicit solid pattern definition; reopening verified **zero missing pattern references**. Its separate write took 0.182 s. See [asset-verification.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/asset-verification.json). Geometry-valid Hatch objects alone do not certify a complete CAD export. Rhino 8 object/layer section-style inheritance, pattern scaling, pattern display generation and interactive bake were not benchmarked.

McNeel's [Hatch API](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.hatch/) supports curve-loop and explicit outer/inner-loop creation. Region/hatch generation is feasible at building scale; the hard part is deciding and preserving valid topology and style semantics, not the raw Hatch constructor cost.

### Section pitfalls established or still open

- **Measured:** open Breps/meshes can return open contours and zero regions. Do not force closure or fabricate solid fill. An open surface may nevertheless give a closed planar intersection; closure alone does not prove solid material.
- **Measured:** the cropped-box example returns two open strokes while reporting ok in all MVP modes. Build closed 2D regions first, intersect the regions with the crop domain, then recover boundaries, including crop edges. Keep display-only crop edges distinct from physical cut edges if the product style requires it.
- **Measured:** hollow RHS/CHS work when outer and inner loops are processed together; cut areas match independent numeric truth. Multiple levels of islands/voids and touching inner loops were not tested.
- **Measured:** column-through-slab cuts remain separate objects, even where their regions overlap. Do not union all cuts globally. Optional same-style merge needs its own ownership/provenance/area contract; merge cost was not measured.
- **Measured/historical:** exact coplanar faces/contact can differ from true volume crossings. The old six-source large-section reproduction returned partial at the storey plane but not ±0.01 m. This is a policy/trim-contact issue; silently nudging the plane changes the drawing and is not a fix.
- **Measured:** native occluding-section flags are not a drop-in guarantee. The box/probe scene with occluding sections enabled returned usable intervals; default clipping exposed invalid Unset proxies. In the point+box scene, enabling the global option returned **zero points**, even when the per-geometry occluding flag was restricted to solids. Keep points on a separately tested path or obtain a kernel clarification before choosing this mode globally.
- **Not tested:** complex non-manifold mesh regions, self-intersecting loops, per-style touching-solid Boolean union, robust nested islands, patterned hatch rendering cost, or retrieval of full Rhino section-style inheritance. These must be explicit tests in implementation.

## Ranked optimization recommendations

The ordering combines **expected saved work × implementation risk**, informed by the measurements. Numeric stage ratios above are measured; the benefits below outside those scopes are estimates, not promises.

| Priority | Change | Expected gain and evidence | Risk / required proof |
|---|---|---|---|
| 1 | Immutable normalized scene with revisions; definition-local geometry/meshes/edges; bounded shared cache | Removes repeated snapshots/fingerprints/preparation across N views; hundreds of ms of solution-thread work on the large model, larger custom meshing savings. Essential foundation for all instance reuse. | Low–medium. Stable revision invalidation, native ownership and bounded memory; no reuse of mutable HLD objects. Does not by itself accelerate the 84 s native compute. |
| 2 | Independent cached per-object section regions/hatches, keyed by geometry revision + local cut plane + tolerance | 500-region feasibility measured at 1.775 s. Reuse the same cut plane across views/layout/style changes; avoid unnecessary trim/cap topology when native clipping is proven adequate. | Medium. Open/coplanar/crop/nested-hole policies and point behavior must be correct first. |
| 3 | Preserve conservative crop/depth/cut culling and lazy boundary indexing | Large ROI: 2,982 of 2,983 objects rejected; 2.731 s solve. Strong measured ROI benefit; existing indexing avoids repeated mesh-edge scans. | Low if only certain disjointness is rejected. ROI is smaller output scope; never market it as whole-model speedup. |
| 4 | Definition-relative silhouette and self-visibility reuse, then global occlusion | Measured candidate-only 7.8–19× on asymmetric copies; shared meshes also much cheaper. High upside for repeated facades if those stages are material. | Medium–high. Prove exact local direction/transform policy, mirror handling and self-visibility; external occlusion still global. Avoid unbounded direction caches or angular approximation without error bounds. |
| 5 | Versioned incremental recompute | Style/placement already need zero visibility calls. View-only changes can share geometry prep; an object edit can invalidate overlapping projected dependency regions rather than the entire model. | Medium–high. Both old and new bounds must invalidate; moving/removing an occluder can reveal formerly hidden candidates behind it. Store dependencies, not just visible output. No runtime gain measured here. |
| 6 | Per-source candidate selection with exact occluder scene retained | Avoid generating unnecessary output candidates while preserving every relevant occluder. The MVP selected-repair path is a useful reference. | Medium. Native HLD has no demonstrated public occluder-only optimization switch; filtering output afterward does not avoid kernel candidate work. Hollow/transparent geometry defeats crude solid proxies. |
| 7 | Conservative coarse visibility prepass / GPU raster as a hint | Could reduce work on huge projected-overlap scenes where native compute dominates. No implementation or speedup measured. McNeel distinguishes GPU technical display from CPU Make2D in its [developer explanation](https://discourse.mcneel.com/t/hiddenline-drawing/87005/10). | High. Pixel samples cannot prove subpixel edges hidden; preserve thin/grazing/near-contact/transparent objects. Use conservative bounds and exact fallback; never drop vectors based only on sampled depth. |
| 8 | View-space tiles / partitions | May bound worst-case native memory and allow future parallel batches. No prototype or measured speedup. | High. Include all overlapping occluders at every depth, tolerance halos and exact curve-boundary splits; deduplicate by source/parameters. Independent spatial chunks lose cross-chunk occlusion. Partitions may duplicate most inputs. |
| 9 | Parallel multiple HLD.Compute calls | Potential throughput for independent views, not established latency win. | High operational risk until reentrancy, cancellation, deterministic output and memory budgets are tested. One measured large view already used 5.59 GiB; inherited elevation exceeded 22 GiB before timeout. Internal multipleThreads is not a license for unconstrained parallel jobs. |
| 10 | Replace general HLD with the MVP Custom engine | No evidence supports this. 48 simple spheres took 9–12 s and remained unresolved; pipe/mesh fixtures fail useful contracts. | Very high. Candidate completeness and exact visibility remain unsolved. Keep the verified convex kernel as a specialist, not the universal fallback promise. |

A robust non-block shape matcher is a secondary optimization after explicit definition reuse. An inexpensive coarse fingerprint is useful for shortlisting but must not identify shapes by volume/edge lengths alone. Cache plane intersections in definition coordinates: two instances with equal local camera directions may have **different local cut offsets**, so section regions cannot be reused on direction equality alone. Reflections need a separate handedness/winding class; nonuniform scaling needs tolerance and geometry-policy treatment beyond the rigid-transform argument.

## Recommended implementation sequence and architecture

1. Create the Rhino-free net8 Core and Rhino/GH boundaries described in mvp-audit.md. Preserve the owner's one-main-component UX constraint; internal scene/view/result objects do not imply more public components.
2. Normalize every supported input explicitly, retaining document/object/layer/style/definition-leaf identity. Own snapshots before worker entry. Add points and explicit block/SubD conversion policies; report unsupported annotations.
3. Build one versioned prepared scene per model: shared definition-local mesh/feature data, instance transforms, spatial bounds, per-plane section cache and a bounded scheduler. Keep derived drawing results separately per view/policy.
4. Establish Native baseline plus section regions, hatch topology and output precedence. Compare physical clipping with native clipping on the frozen counterexamples, including points. Make incomplete regions a separate status from successful line generation.
5. Port only independently verified convex repair; keep full-scene occlusion and explicit unresolved output. Add instance candidate reuse after comparing whole-output geometry/status on adversarial and held-out scenes.
6. Explore global visibility acceleration only after profiling actual owner-like models. Native full elevation projected-overlap growth, not GH string formatting, is the unresolved scale bottleneck.

For strict hard cancellation/crash/RAM containment, a separate licensed worker process is the architectural boundary; async code in Rhino alone cannot guarantee those properties. This is a planning recommendation, not a measured comparison of IPC overhead or licensing/startup policies. A small in-process route and a bounded large-job worker could coexist, subject to product decisions and integration tests.

## Rerun and limitations

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
$r = 'C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114'
& $py -B -u "$r/scripts/supervise.py" performance 600 12
& $py -B -u "$r/scripts/supervise.py" reuse_validation 180 12
& $py -B -u "$r/scripts/supervise.py" controls 120 12
& $py -B -u "$r/mvp/tests/run_large_stress.py" --model "$r/fixtures/frame2d-large-campus.3dm" --manifest "$r/fixtures/frame2d-large-campus.json" --result "$r/results/large-native-plan-roi.json" --engine native --views full_plan,terrain_tile_roi_plan --repeats 1 --trial-seconds 150 --wall-seconds 300 --memory-gb 12 --layout --gh-output --gh-input
& $py -B "$r/scripts/summarize.py"
& $py -B -u "$r/scripts/verify_assets.py"
```

Run serially, using a copied scratch folder to preserve existing evidence. Helpers: [performance_experiments.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/performance_experiments.py), [reuse_validation.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/scripts/reuse_validation.py), [statistics.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/results/statistics.json). A licensed Rhino 8/.NET 8 installation and the copied binaries are required; no Grasshopper Libraries installation is involved.

No 50k-object, Brep-heavy 500 MiB, GPU, real owner-project, complete freeform drawing oracle, parallel-view throughput, interactive UI latency, worker IPC, or full section-style integration benchmark was performed. Expected gains outside the measured stage/fixture scopes remain estimates. The repeated Native west-view hidden-line variation and the native section/point behavior are open kernel/integration investigations, not explained bugs.
