# Persistent drawing workers (schema 2)

Implemented 2026-11-15. This replaces the M0 worker identity-only executable. It does not change the owner's Rhino runtime, install anything, or publish a package.

## Boundary and ownership

`DrawingCache` routes a visibility cache miss through `DrawingExecution`; presentation and target placement still run on the receiving side. `NativeDrawing` remains the explicit in-process entry point for correctness harnesses. Workers invoke that same adapter, including SectionDrawing and ComposedVisibility, with native multipleThreads disabled. Each process has one serial, budgeted native scheduler.

The host encodes only an already-owned PreparedScene. Definition geometry is serialized once per content key using Rhino's own JSON/binary archive (Rhino 8, user data included); source identities, paths, styles, transparency, scoped-clipping IDs and exact transform chains accompany it. The receiver reconstructs leases and runs the ordinary preparation code, preserving section canonicalization and mirrored-instance semantics. No RhinoDoc, live object, viewport, GH wrapper or document table crosses this boundary. Scenes are sent once per participating process and retained by scene revision; warm solves avoid even re-encoding an unchanged scene. One scene is retained per process.

Results contain independent Rhino geometry archives, depth geometry, every source/path/class/profile value, section loops and edge provenance, aligned hatches and resolved styles, quality and extraction counters. Order is preserved. Settings values use an explicit type whitelist and sorted keys, with their effective/authored values and provenance preserved. The stable result comparison includes all serialized geometry, settings and quality; it excludes wall/phase timings and diagnostic prose, which necessarily differ between processes. No rounding, locus canonicalization or tolerance-based repair is used for this byte comparison.

## Protocol and containment

A random, current-user-only duplex named pipe uses a little-endian 32-bit length prefix, an exact-read loop and a 256 MiB compressed frame bound. Protocol envelopes use streaming gzip with a separately enforced 1 GiB expanded-data limit; large view results do not require an unbounded frame. The handshake remains an uncompressed, bounded identity frame. Before any scene bytes, both peers compare schema, exact CLR version and major, process architecture, exact Rhino build and source revision, RhinoCommon SHA-256 and the combined Core/Rhino engine assembly SHA-256. A net8 apphost cannot roll forward to net10. A mismatched or absent executable is unavailable.

The child waits for its private stdin containment acknowledgement before resolving or initializing Rhino. Its parent first assigns it to an unnamed Windows Job Object with kill-on-close and a hard aggregate job commit cap (including descendants). Cancel and request deadlines terminate only this owned job. The parent confirms process death before releasing its slot. A crashed worker fails only its current view with a quarantined Info error; remaining views can start a fresh process after confirmed death. Memory failure uses the single isolated retry described below; other completed views survive.

Every solve request and reply carries the complete RequestIdentity (scene/view/settings revisions and generation) plus view index. The receiver checks all of them before reconstructing results. The existing GH RequestGate/document-generation check remains the final publication authority; the whole current generation publishes together. Cancellation immediately rejects publication, independently of process cleanup. Internal native-entry signalling supports deterministic crash/cancel/memory fault tests; it is not a GH setting.

## Pool and admission

There is one shared pool across drawing components, with one batch reservation at a time. The CPU ceiling is `clamp(logical cores / 4, 1, 4)`; actual concurrency is chosen from memory and the scene estimate for every batch. One complete view per process, deterministic round-robin assignment, no tiling or native HLD multithreading. Workers expire after two idle minutes; busy workers never expire.

`WorkerMemoryPolicy` is pure Core code, unit-tested with fake RAM values. Windows supplies total physical RAM and the lesser of available physical RAM and available commit. The aggregate cap budget is the smaller of 60% of physical RAM and 80% of available memory. The initial per-worker cap is `ceilGiB(max(2 GiB, 3 * SceneCost.Estimate(1) + 1 GiB))`: the measured M scene gets 3 GiB, L gets 6 GiB. This includes headroom for the native solve and serialized outputs; it is a calibration heuristic, not a proof of peak allocation for arbitrary geometry.

Count is the minimum of the CPU ceiling, view count and `floor(budget / cap)`. **Reduce count first; never shrink the default cap below the scene requirement.** If even one cap cannot fit, refuse heavy worker admission with an actionable message; do not silently run it in-process. Retained workers whose cap changed, and excess workers, exit before replacements start. Owned idle resident memory is treated as reclaimable in the available-memory snapshot; other applications' memory is never counted as reclaimable. The shared batch reservation prevents different components' worker batches from oversubscribing the same budget.

An allocation failure is detected through Windows Job Object memory-limit notifications and explicit memory errors, including errors returned from the native adapter. Finish the other views, close **all** original workers, take a fresh memory snapshot, and retry each failed view **once, alone**. The retry cap is the smaller of twice its original cap and the fresh aggregate budget rounded down to GiB; it must be strictly larger. No headroom means no identical-cap retry. A failed retry affects only that view and Info tells the user to close other applications or reduce scene/view complexity. Cancellation, runtime mismatch and stale replies do not enter this memory retry loop. The pool never falls back to a heavy in-process solve on a memory failure.

Environment configuration remains available for experts and is read when the shared pool is created:

| Variable | Default | Bounds / meaning |
|---|---|---|
| M2DPLUS_WORKERS | CPU ceiling | 1–4, still constrained by memory |
| M2DPLUS_WORKER_MEMORY_MIB | automatic | 256–16384 MiB initial cap override, still constrained by aggregate budget; a recovery retry may grow it |
| M2DPLUS_WORKER_IDLE_SECONDS | 120 | 1–3600 seconds |
| M2DPLUS_WORKER_DIRECTORY | adjacent workers/net8 or workers/net10 | Development override; must match the loaded assemblies |

Startup and per-request deadlines remain 45 seconds and 10 minutes. Only the owned worker job is killed, and its death is confirmed before reusing its slot.

### Why 11.6 GiB on L does not authorize four workers on a 16 GiB laptop

The earlier 11.641 GiB is the **sum of four Job Object peak commit counters**. Each worker peaks at a different time and solves different views; it is a conservative upper bound on observed aggregate commit, not a per-worker requirement or a hard aggregate reservation. Four 6 GiB caps permit up to 24 GiB even though that sample consumed less. Sizing therefore uses the sum of caps, not 11.6 GiB.

For a hypothetical 16 GiB machine:

| Scene / available RAM | Budget | Choice | If the cap is hit |
|---|---:|---|---|
| L / 12 GiB | 9.6 GiB | 1 worker x 6 GiB | After all workers exit, one retry up to 9 GiB if the fresh snapshot is still 12 GiB available |
| L / 8 GiB | 6.4 GiB | 1 worker x 6 GiB | No larger whole-GiB cap fits: fail only that view and advise freeing memory/reducing complexity |
| L / 6 GiB | 4.8 GiB | No worker admitted | Ask the user to free memory or simplify; do not lower the cap to squeeze in a process |
| M / 8 GiB | 6.4 GiB | 2 workers x 3 GiB | A failed view can retry alone up to 6 GiB, subject to the fresh snapshot |

These are tested sizing-policy examples, not performance claims on a laptop. Job commit caps do not reserve physical RAM; external applications can change available memory after admission. The OS cap remains the hard backstop, and completed outputs in the host also need memory.

Single views admitted by the existing 512 MiB in-process estimate and face/triangle limits remain in-process. Sets of two or more views and jobs refused by that admission policy use workers. Missing/incompatible workers retain the existing refusal for heavy jobs; already-admissible jobs may use the existing local path with an unavailable-worker explanation. NativeScheduler never silently expands the interactive host's budget. Worker admission uses its own job budget, with the OS cap as the hard backstop. The threshold is deliberately a conservative dispatch policy, not a universal time predictor; startup calibration and the M/L measurements below are the evidence for its present choice.

## Packaging

The trial Yak contains the usual GHA/Core/Rhino payload plus `workers/net8` and `workers/net10`, each containing its own apphost, worker assembly, deps/runtimeconfig and byte-matching Core/Rhino assemblies. Apphosts are framework-dependent and require the corresponding installed WindowsDesktop runtime; Rhino supplies its own installed native/managed libraries. The bootstrap resolves managed Rhino assemblies from System/netcore and native libraries from System. No Rhino DLL, licence or external resolver is redistributed. Building now needs the .NET 10 SDK as well as .NET 8 targeting support. Pack-Yak validates the complete 19-file inventory and all payload hashes. No installation or publication is performed.

## Verification and remaining limits

Licensed tests cover persistent one/two-worker equality (including mixed planar/curved geometry, points, mirrored blocks, resolved settings, physical sections and hatches), runtime mismatch before scene transfer, crash isolation and next-generation restart, hard cancellation of a deliberately non-cooperative solve, document close and publication rejection, stale replies, OS allocation refusal at the memory cap, idle expiry, set/heavy admission and unavailable-worker refusal. The full existing suite is also run under .NET 8 and .NET 10.

Risks: the cost estimator is conservative and not an allocator prediction; a pathological single view can hit the hard cap or frame bound and fail. Four-way commercial/Cloud Zoo/offline licence recovery and installed-Yak interactive responsiveness remain manual deployment gates. A cancelled batch is rejected; a crashed view is failed without discarding other views; different per-view costs can leave slots idle near batch completion. Cold measurements refer to fresh processes, not purged OS caches. Durable disk caching, tiling and new visibility algorithms are outside this change.


## Original pre-review measurements (2026-11-15)

This table records the previous source base, before #22/#23 and automatic sizing. It remains historical evidence; the review rerun below uses the rebased schema-2 build.

Windows 11, 32 logical cores, 63.65 GiB RAM; Rhino 8.36.26296.17001, .NET 8.0.34, Release, production composed/native/section route, native threading off. Eight views in each set: North, South, East, West, Top, SW, long section and z=1.2 plan. Baselines use a 12 GiB scheduler budget solely to permit the comparison outside product admission. Workers use 6 GiB Job Object caps. Each configuration is n=1 cold plus n=1 warm. Other agents' Rhino work was active; configurations here ran serially. These are operational measurements under contention, not controlled statistical speed claims.

| Scene | Route | Cold/local s | Warm s | Peak GiB | Hash matches |
|---|---|---:|---:|---:|---:|
| small | local | 0.105 | - | 0.262 | 1/1 |
| small | 1 | 1.499 | 0.004 | 0.271 | 2/2 |
| M | local | 62.152 | - | 1.340 | 8/8 |
| M | 1 | 71.135 | 66.158 | 2.449 | 16/16 |
| M | 2 | 38.260 | 34.446 | 3.676 | 16/16 |
| M | 4 | 29.815 | 23.334 | 5.373 | 16/16 |
| L | local | 218.146 | - | 2.739 | 8/8 |
| L | 1 | 227.151 | 222.114 | 4.924 | 16/16 |
| L | 2 | 113.690 | 111.369 | 7.951 | 16/16 |
| L | 4 | 87.469 | 83.343 | 11.641 | 16/16 |

Local time is raw compute wall; worker cold includes startup, exact handshake, scene transfer/preparation, all solves and compressed result transfer. Warm retains the scene and worker. Hash comparison happens after the worker batch timer stops. Local memory is sampled private bytes (25 ms); worker memory is the sum of OS job peak commit counters, a conservative upper bound. They are different memory measures. Cold means a fresh process, not purged OS caches. Capture/scene encoding were separate: see baseline.json and the replay harness.

All **96/96** M/L worker view payloads match the in-process baseline byte for byte after removing timings and diagnostic prose only. All geometry archives, attribution, ordering, settings, quality, section loops/provenance and hatches remain in the comparison. The separate small single-box control also matches cold and warm.

Threshold choice: keep single views under the existing 512 MiB estimate/complexity admission local, and dispatch sets from two views or any refused local job. The box took 0.105 s locally versus 1.499 s in a fresh worker (0.004 s warm). One worker adds isolation/transport overhead on M/L; multiple workers deliver the throughput gain. This does not calibrate a universal seconds predictor: scene overlap, sections and output volume vary. The estimate remains advisory, not a physical memory cap.

The original L test needed a manual 6 GiB setting. **That requirement is removed:** schema 2 chooses the 6 GiB cap from scene and machine memory automatically and reduces concurrency if needed. The 266 MB unwrapped L axonometric result exercised compressed envelopes; expanded data is bounded separately from compressed frame size.

Reproduction: tools/worker-bench/Run.ps1; use a new output folder. Evidence in artifacts/worker-final contains model and engine fingerprints, every request, stable baseline payload, per-view hashes/times, OS commit peaks, verification.json and a SHA-256 manifest. Test logs are artifacts-worker-verified-tests.log; the forced hook passed in artifacts-worker-hook-verified.log. Debug/Release builds had zero warnings/errors. Both runtime suites passed 224 Core, 40 UI-model and 185 licensed Rhino tests each. The local trial Yak passed its 19-file inventory/hash validation; it was not installed or published.


## Review integration and regression coverage

Rebased onto origin/main at 6a3b0f0 (#22 curved section fixes, #23 Ignored/min visible). DrawingCache still canonicalizes hidden-on visibility before dispatch; hidden off and min visible remain presentation-only. The transport preserves DrawingPiece.SolvedClass separately from Class, plus MinVisible, IgnoredObjects, IgnoredPieces and all stored routing state. No output indices or stable identities change.

WorkerCodecStateTests enumerates every instance backing field (including get-only state) of ViewDrawing and DrawingPiece, requires an explicit wire mapping or checked reconstruction, seeds non-default scalar state, and compares it after a real round trip. It also checks geometry archives, phases, regions, diagnostics and source state. A future stored field fails coverage; adding a DTO entry but forgetting assignment fails value comparison. Licensed cases compare raw and presented worker/local bytes with hidden off, min visible = 5 mm through a real 3 mm wall gap, and a physical section with hatches. A quarantined solved-hidden piece explicitly checks SolvedClass preservation.

Retry tests inject an OS-denied allocation in one view, verify one larger-cap retry alone after original worker death, and preserve a different view's authored error. A separate no-headroom case verifies the actionable per-view failure and no retry loop. The earlier crash/cancel/stale/runtime/idle/admission cases remain in the suite.


### Review rerun results

The final code is based on origin/main 6a3b0f001d0de6fc200b14a9ecaaf9994724ecba. Both count and cap overrides were absent. The coordinator used the exact production pool's admission, scheduling, handshake, retention and retry path without initializing RhinoCore, keeping four-worker validation within the four-owned-process limit.

| Current build / set | Chosen workers x cap | Aggregate hard caps | Budget at admission | Solve wall | Result |
|---|---|---|---|---|---|
| M, pass 0 | 4 x 3 GiB | 12 GiB | 22.93 GiB | 31.912 s | 8/8 successful and matching local hashes |
| M, pass 1 | 4 x 3 GiB | 12 GiB | 21.45 GiB | 24.705 s | 8/8 successful and matching local hashes |
| L, pass 0 | 3 x 6 GiB | 18 GiB | 23.42 GiB | 90.402 s | 8/8 successful |

M: 16/16 new worker-vs-in-process hashes match, including the eight-view set's section views. Hidden-off only, hidden-off plus min visible = 5 mm through a 3 mm gap, and a physical section were also compared as raw and presented bytes in licensed tests on both runtimes, with positive Ignored and nonempty section assertions. The codec reflection test seeds all stored state and verifies it round-trips.

L: the complete eight-view set succeeds with defaults on this machine. Available memory selected three 6 GiB workers (18 GiB of aggregate caps), not a fixed four-worker reservation. A separate earlier default run also succeeded (86.483 s; budget 23.17 GiB). After every worker exited, a separate single RhinoCore reconstructed all eight reply archives through WorkerCodec.RestoreDrawing and reserialized them to the same hashes: 8/8 native reconstruction matches, including 155,585 SW pieces and 418 plan regions. This checks host-side geometry reconstruction without a fifth simultaneous owned Rhino process. It is not a new L-vs-in-process oracle claim; the new in-process reference is M.

Final verification: Debug and Release builds pass with zero warnings/errors. Test-WalkingSkeleton.ps1 passes 251 Core, 47 UI-model and 201 licensed Rhino tests on each of .NET 8 and .NET 10. The independently strengthened presentation case also passed again on both runtimes. Forced hook and the fresh 19-file trial Yak passed. Evidence: artifacts/worker-review; logs artifacts-review-full-tests.log, artifacts-review-presentation-tests.log, artifacts-review-hook.log and artifacts-review-yak.log. No commit, push, installation or publication was made.
