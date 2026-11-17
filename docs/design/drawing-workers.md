# Persistent drawing workers (schema 4)

Implemented 2026-11-15. This replaces the M0 worker identity-only executable. It does not change the owner's Rhino runtime, install anything, or publish a package.

## Boundary and ownership

`DrawingCache` routes misses through `DrawingExecution`. Workers invoke the same
threaded HLD adapter, physical Route C sections, requested hidden flag, ordered source
registrations and owned geometry/settings as the host. One budgeted HLD call per
process; bounded worker admission limits CPU contention and respects memory caps. Rhino native
thread counts remain outside managed processor control.

Schema 4 removes NativeOnly, serial/threading overrides, verification and dense-audit
options and kernel-only result state. Culling travels with each request. Runtime
version/architecture, exact Rhino build/revision, RhinoCommon and Core/Rhino hashes
must match before scene transfer. Processor count is recorded, not an equality promise.

Only owned scene archives, exact transform chains, source attributes/paths, style and
scope snapshots cross the boundary. Definition geometry is sent once per key and
retained per scene. No live Rhino/GH/UI objects cross it. Region/geometry leases and
whole-current generation publication, stale-reply rejection, crash/cancel isolation,
Job Object caps and no heavy in-process fallback remain unchanged.

Equality means same inputs/settings/fingerprints and policy. Fresh threaded HLD
realizations may differ in fragments, ordering, duplicate ownership and Ignored.
Single-result transport stays lossless: geometry/depth, classes/paths, loops/provenance,
hatches/styles, settings/authorship, quality/counters/diagnostics. The codec field census
is mandatory. Tests compare independent classed loci at fixture-declared document
tolerance, topology/hole counts and analytic areas, not independent solve hashes.
Timing keys use visibility settings and fingerprints; engine/joints edits affect remarks
only. Schema/policy/assembly/Rhino changes invalidate durable and timing records.

The previous exact engine is archived at `archive/exact-engine`, `0cfc171` (beta.8).
Dated benchmarks below retain their original fingerprints and equality claims as
historical evidence; they do not certify the current HLD-only policy.

## Protocol and containment

A random, current-user-only duplex named pipe uses a little-endian 32-bit length prefix, an exact-read loop and a 1 GiB compressed frame bound. Schema 4 gzip envelopes contain a one-byte operation-name length (1–16 lowercase ASCII letters), the name, a little-endian 32-bit raw payload length, and those payload bytes. Declared expanded lengths are checked against 8 GiB and .NET's array limit before allocation; short payloads and trailing expanded data are rejected. Host replies additionally use the smaller admission-derived expanded bound below. The host streams gzip directly from a length-bounded pipe frame, without allocating the compressed frame. This removes the previous giant outer base64 JSON token: the real 50,000-object scene exceeded System.Text.Json's streaming buffer growth range despite staying under its worker cap. Scene/drawing JSON, the inner Reply.Drawing base64 and all stable output bytes are unchanged. The handshake remains an uncompressed identity frame, limited to 16 KiB on host receive. Before any scene bytes, both peers compare schema, exact CLR version and major, process architecture, exact Rhino build and source revision, RhinoCommon SHA-256 and the combined Core/Rhino engine assembly SHA-256. A net8 apphost cannot roll forward to net10. A mismatched or absent executable is unavailable.

The child waits for its private stdin containment acknowledgement before resolving or initializing Rhino. Its parent first assigns it to an unnamed Windows Job Object with kill-on-close and a hard aggregate job commit cap (including descendants). Cancel and request deadlines terminate only this owned job. The parent confirms process death before releasing its slot. A request deadline is a per-view timeout failure; caller cancellation remains whole-batch cancellation. A worker claims a view only after its startup handshake succeeds. A failed startup retires that slot for the batch without taking work from ready siblings. Once every view finishes, unused startup attempts are cancelled and their owned processes are reaped; they cannot hold a completed batch until the startup deadline. A queued view fails only when no slot remains able to take it, with an explicit Info explanation. If none can start, the existing unavailable/fallback policy applies. A crashed worker fails only its current view with a quarantined Info error; remaining views can start a fresh process after confirmed death. Memory failure uses the single isolated retry described below; other completed views survive.

Every solve request and reply carries the complete RequestIdentity (scene/view/settings revisions and generation) plus view index. The receiver checks all of them before reconstructing results. The existing GH RequestGate/document-generation check remains the final publication authority; the whole current generation publishes together. Cancellation immediately rejects publication, independently of process cleanup. Internal native-entry signalling supports deterministic crash/cancel/memory fault tests; it is not a GH setting.

## Pool and admission

There is one shared pool across components, with one batch reservation at a time. The CPU ceiling is `max(1, logical cores - 2)`. Expert count overrides cannot exceed it. Threaded HLD adds a conservative admission ceiling: **one worker below 16 host logical cores, at most two on 16+ cores**, regardless of larger expert count overrides. Actual concurrency is the minimum of both CPU ceilings, the configured count, the view count and `floor((memory budget - host reply reservation) / per-view cap)`. One whole view runs per process with threaded HLD, without tiling. Host and worker use the same required threading/requested-hidden policy.

A shared work queue lets each free worker take another view. For another batch on the same scene, previously measured longest views start first; unmeasured views retain input order. Results remain in input order. History retains only the current batch and clears on scene-revision changes. Scene transport was already retained per process; changing only views still avoids scene encoding and transfer.

Each process advertises `max(1, floor((host logical cores - 2) / admitted workers))` processors through `DOTNET_PROCESSOR_COUNT`. Children also run at BelowNormal process priority. This advertises a managed processor allowance only: DOTNET_PROCESSOR_COUNT does not limit Rhino's native HLD pool. Each solve can use all host cores. The one/two-worker admission ceiling limits concurrent full pools; on large hosts two pools can still oversubscribe CPU. It is not OS affinity or a native thread cap. The host's separate in-process HLD slot and other Rhino applications are outside this worker-admission limit. The same conservative worker ceiling also applies to all-cut-only batches, avoiding per-job scheduling exceptions. A changed processor allowance retires idle workers, as does a changed cap. An isolated memory retry gets the full managed allowance. Busy workers never expire; idle workers now expire after ten minutes.

Windows supplies physical RAM and the lesser of available physical RAM and available commit. Below the 32 GiB machine class the aggregate budget remains `min(60% physical, 80% available)`. At 32 GiB class and above (at least 30 GiB reported usable physical RAM, allowing hardware reservations) it is `min(75% physical, 85% available)`. Thus 32/64/128 GiB hosts leave at least 8/16/32 GiB outside worker caps **and the host transport reservation** for Rhino, retained results and the OS, with a further current-availability constraint. Owned idle resident memory is reclaimable for admission; other applications' memory is never counted as reclaimable. If working-set credit still gives a refusal, reap the pool's idle workers before taking a fresh RAM/commit snapshot and sizing again: paged-out private commit is not covered by working-set credit.

The host reply reservation is `min(4 GiB, floor(budget / 4))`, subtracted before choosing any workers, including expert-cap admission. The maximum expanded reply envelope is `min(Array.MaxLength, floor((reservation - 1 MiB) / 2))`. At most one reply is received, decompressed, decoded and restored at a time across the shared pool. Raw reply JSON plus its decoded Drawing byte array need at most 1.75 times the expanded length; the reservation allows twice that length plus 1 MiB for bounded gzip/pipe buffering. A frame length is read before taking the receive gate, so a slow native solve does not block other workers' scene acknowledgements or compute. Waiting workers keep their results inside their Job Object caps and are backpressured by the pipe. Oversize declarations fail before payload allocation with actionable Info, preserving sibling views; they do not trigger a futile larger-worker-cap retry. This budgets transport buffers, not the eventual retained drawing/native geometry or all host allocations; existing host headroom and fresh availability snapshots still matter.

The default per-view cap is `ceilGiB(max(2 GiB, 3 * P(1) + H + 1 GiB))`: `P` is the original preparation/archive estimate; `H = 128 KiB * (curved Brep faces + edges of Breps with curved faces)`, counted per instance after normalization. `SceneCost.Estimate(views) = P(views) + views * H` also guards interactive admission, so a small archive cannot enable a heavy host fallback. Planar M/L policy examples retain 3/6 GiB caps; curved-S/diagrid/curved-steel select 2 GiB and tenfold curved-S topology predicts curved-M at 6 GiB. See [small-model calibration](../research/worker-memory-sizing.md). This is calibration, not an allocation guarantee. **Reduce count first; never shrink the automatic cap below the scene requirement.** Automatic caps have no 16 GiB ceiling. If even one view cannot fit, give an actionable refusal, with no heavy in-process fallback. Retire excess/changed-cap workers before starting replacements.

An allocation failure is detected through Job Object notifications and explicit native memory errors. Record that job's peak commit before disposing its handle. Finish the other views, close all original workers, take a fresh memory snapshot, and retry each failed view **once, alone**. With a peak, the target is `ceilGiB(max(original cap + 1 GiB, 1.5 * observed peak + 1 GiB))`; without it, the fallback is twice the original. Clamp to `(fresh budget - fresh host reply reservation)` rounded down to GiB. The cap must exceed both the old cap and any observed peak; otherwise refuse the futile retry. A limit-censored peak does not reveal the complete allocation demand. The retry uses the expanded reply bound from that same fresh snapshot. Cancellation, mismatches and stale replies do not enter this loop. Other completed views survive.

### Sizing examples

Fake-machine unit-tested policy outputs at **75% of physical RAM available** and at least 32 views. Cells are **planar M / L worker counts**, with **3 / 6 GiB** caps (curved-topology counts zero). Fewer views or less available memory reduce these counts. These are planning examples, not speed claims.

| RAM | Budget GiB | Host reply GiB | 8 logical cores | 16 logical cores | 32 logical cores |
|---|---:|---:|---:|---:|---:|
| 16 GiB | 9.6 | 2.4 | 2 / 1 | 2 / 1 | 2 / 1 |
| 32 GiB | 20.4 | 4 | 5 / 2 | 5 / 2 | 5 / 2 |
| 64 GiB | 40.8 | 4 | 6 / 6 | 12 / 6 | 12 / 6 |
| 128 GiB | 81.6 | 4 | 6 / 6 | 14 / 12 | 25 / 12 |

### Limits audit

| Limit | Current policy and reason |
|---|---|
| Active worker count | Threaded HLD admits 1 below 16 host cores, at most 2 on larger hosts; config/view/memory limits may reduce this |
| Host scheduler: 512 MiB estimate | Retained as interactive dispatch threshold, never a worker refusal |
| 600,000 Brep faces times views; 4,000,000 triangles times views | Retained only for in-process dispatch. Division avoids product overflow. Workers use their separate estimate and Job Object cap |
| 50,000 sources | New explicit heavy dispatch, even for an otherwise cheap single view; no worker source-count rejection |
| Scene estimate | Original preparation accounting `P = 4*geometry + 2048*faces + 96*triangles + 4096*sources + views*(65536 + geometry/8 + 512*faces + 24*triangles + 512*sources)`, plus `views * H` for curved native working memory; empirical, not an allocator guarantee |
| Automatic per-worker cap | 2 GiB minimum; formula above; no fixed maximum; reduce count before cap |
| Expert cap override | Minimum raised 256 to 512 MiB; removed 16 GiB ceiling; actual ceiling is aggregate budget |
| Compressed frame | Raised 256 MiB to 1 GiB; checked before reading; host streams directly from the bounded frame without compressed-frame allocation; 32-bit framing retained |
| Expanded envelope | General 8 GiB/array bound retained; host replies use the smaller admission-derived bound, checked before allocation, with exact-read/trailing-data checks and corruption/bomb tests |
| Host reply transport | One receive/restore at a time; reserve min(4 GiB, budget/4) inside the aggregate budget, including raw JSON, decoded Drawing bytes and stream buffering |
| Envelope base64 / streaming JSON buffer | Removed in schema 3: raw payload bytes bypass the giant-token buffer-growth failure seen on the real 993 MB 50k scene |
| Payload representation | .NET arrays and scene/result JSON still impose implementation limits per individual raw payload/token; the expanded ceiling does not promise unbounded scenes. Archive/envelope allocations also consume the worker cap |
| Startup / request / idle | 45 to 120 s / 600 to 3600 s / 120 to 600 s, respectively; finite expert overrides below |
| Child pipe connection | 30 s retained: the parent creates/listens before child bootstrap; separate from Rhino startup deadline |
| Manual-bootstrap scheduler fallback | Server has a 4 GiB fallback only when its private budget variable is absent. Production always passes the resolved cap; this is not an additional production limit |
| Preparation-cache retention | Existing host 128 MiB LRU retained; oversize/live scene leases stay usable, and this is not worker admission or the worker scene lifetime |
| Confirm owned process death | 10 s retained; never replace a slot whose process death is unconfirmed |
| Native scheduling | One budgeted native call per process; one shared host batch reservation; HLD always multithreaded |
| Memory retry | Exactly once, alone, after original workers exit; larger cap only within fresh budget |

Environment configuration is read when the shared pool is created:

| Variable | Default | Bounds / meaning |
|---|---|---|
| M2DPLUS_WORKERS | CPU ceiling | Parser: 1 to `max(1, cores - 2)`; active count further capped at 1/2 by threaded-HLD admission, views and memory |
| M2DPLUS_WORKER_MEMORY_MIB | automatic | 512 to 2147483647 MiB parser range; aggregate budget is the effective maximum; recovery may grow it |
| M2DPLUS_WORKER_IDLE_SECONDS | 600 | 1 to 86400 seconds |
| M2DPLUS_WORKER_STARTUP_SECONDS | 120 | 15 to 3600 seconds |
| M2DPLUS_WORKER_SOLVE_SECONDS | 3600 | 1 to 86400 seconds per scene transfer or view request |
| M2DPLUS_WORKER_DIRECTORY | adjacent workers/net8 or workers/net10 | Must match loaded engine/runtime |

Only the owned job is killed on cancellation/deadline; its death is confirmed before slot reuse.

### Why 11.6 GiB on L does not authorize four workers on a 16 GiB laptop

The earlier 11.641 GiB is the **sum of four Job Object peak commit counters**. Each worker peaks at a different time and solves different views; it is a conservative upper bound on observed aggregate commit, not a per-worker requirement or a hard aggregate reservation. Four 6 GiB caps permit up to 24 GiB even though that sample consumed less. Sizing therefore uses the sum of caps, not 11.6 GiB.

For a hypothetical 16 GiB machine:

| Scene / available RAM | Budget | Choice | If the cap is hit |
|---|---:|---|---|
| L / 12 GiB | 9.6 GiB | 1 worker x 6 GiB + 2.4 GiB host reply | After all workers exit, one retry up to 7 GiB if the fresh snapshot is still 12 GiB available |
| L / 8 GiB | 6.4 GiB | No worker admitted after reserving 1.6 GiB host reply | Ask the user to free memory or simplify |
| L / 6 GiB | 4.8 GiB | No worker admitted | Ask the user to free memory or simplify; do not lower the cap to squeeze in a process |
| M / 8 GiB | 6.4 GiB | 1 worker x 3 GiB + 1.6 GiB host reply | A failed view can retry alone up to 4 GiB, subject to the fresh snapshot |

These are tested sizing-policy examples, not performance claims on a laptop. Job commit caps do not reserve physical RAM; external applications can change available memory after admission. The OS cap remains the hard backstop for children. The host transport has bounded allocations and concurrency, and completed outputs in the host also need memory.

Single views below 50,000 sources and admitted by the existing 512 MiB estimate and face/triangle limits remain in-process. Sets of two or more views and jobs refused by that admission policy use workers. Missing/incompatible workers retain the existing refusal for heavy jobs; already-admissible jobs may use the existing local path with an unavailable-worker explanation. NativeScheduler never silently expands the interactive host's budget. Worker admission uses its own job budget, with the OS cap as the hard backstop. The threshold is deliberately a conservative dispatch policy, not a universal time predictor; startup calibration and the M/L measurements below are the evidence for its present choice.

## Packaging

The trial Yak contains the usual GHA/Core/Rhino payload plus `workers/net8` and `workers/net10`, each containing its own apphost, worker assembly, deps/runtimeconfig and byte-matching Core/Rhino assemblies. Apphosts are framework-dependent and require the corresponding installed WindowsDesktop runtime; Rhino supplies its own installed native/managed libraries. The bootstrap resolves managed Rhino assemblies from System/netcore and native libraries from System. No Rhino DLL, licence or external resolver is redistributed. Building now needs the .NET 10 SDK as well as .NET 8 targeting support. Pack-Yak validates the complete 19-file inventory and all payload hashes. No installation or publication is performed.

## Verification and remaining limits

Historical verification before the HLD-only cutover covered persistent one/two-worker equality (including mixed planar/curved geometry, points, mirrored blocks, resolved settings, physical sections and hatches), runtime mismatch before scene transfer, crash isolation and next-generation restart, hard cancellation of a deliberately non-cooperative solve, document close and publication rejection, stale replies, OS allocation refusal at the memory cap, idle expiry, set/heavy admission and unavailable-worker refusal. The full existing suite is also run under .NET 8 and .NET 10.

Risks: the cost estimator is conservative and not an allocator prediction; a pathological single view can hit the hard cap or frame bound and fail. Higher-concurrency commercial/Cloud Zoo/offline licence recovery and installed-Yak interactive responsiveness remain manual deployment gates. A cancelled batch is rejected; a crashed view is failed without discarding other views; one indivisible slow view can still leave slots idle near batch completion despite dynamic scheduling. Cold measurements refer to fresh processes, not purged OS caches. Durable disk caching, tiling and new visibility algorithms are outside this change.


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

WorkerCodecStateTests enumerates every instance backing field (including get-only state) of ViewDrawing and DrawingPiece, requires an explicit wire mapping or checked reconstruction, seeds non-default scalar state, and compares it after a real round trip. It also checks geometry archives, phases, regions, diagnostics and source state. A future stored field fails coverage; adding a DTO entry but forgetting assignment fails value comparison. Current licensed cases compare raw/presented fixture loci, topology and policy with hidden off and physical sections; hidden-off min-visible retains all objects. The earlier worker/local byte checks are archived measurements. A quarantined solved-hidden piece explicitly checks SolvedClass preservation.

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


## PERF G follow-up

See [PERF G findings](../../hillclimb/perf-g/FINDINGS.md) for current-source profiles, scaling measurements, byte/hash checks and runtime verification. Historical measurements above retain their original policy and source fingerprints.

## PR #32 review fixes (2026-11-16)

Host replies now stream compressed bytes from the pipe and share one receive/restore gate. Admission and isolated recovery both subtract the host reply reservation before selecting worker caps; expanded reply declarations are checked against that reservation before allocation. Fake 16/32/64/128 GiB machines cover counts, aggregate accounting and retry headroom. Protocol tests exercise short reads, consecutive frames, oversized expansion declarations, truncation, trailing data and cancellation, including a 1 GiB declared frame with no payload and no compressed-frame allocation.

A failed startup requeues its claimed view and disables that slot for the current batch.
Current licensed startup/retry gates check input/policy/fingerprint parity, successful
fixture geometry/topology within declared tolerances, serialized reply consumption and
stale isolation. Processor-count controls do not gate independent solve bytes. Only
transport/restoration of one result stays byte-exact. The dated local-hash measurements
above belong to the archived exact engine, not the current acceptance contract.

Debug and Release builds passed with zero warnings/errors. `tools/Test-WalkingSkeleton.ps1` passed **344 Core, 62 UI-model and 238 licensed Rhino tests on each runtime**, .NET 8.0.34 and .NET 10.0.15, Rhino 8.36.26296.17001. The forced local hook passed after the licensed hosts exited.

Fresh M eight-view in-process references and two production-pool passes per runtime produced **32/32 matching stable view hashes**; subsequent native reconstruction produced **16/16 matching hashes**. Each pass admitted four 3 GiB workers plus a 4 GiB host reply reservation. Evidence, source diff, runtime/engine fingerprints, model hash, test logs and a SHA-256 manifest are retained outside Git in `~/.t3/m2d-plus-models/evidence/worker-review-20261116-bc06c73a-verified`. These are correctness checks, not new performance claims. No commit, push, installation or publication was performed; the owner's Rhino processes were untouched.


The current failure boundary is local: rejected sources, failed segments and uncertain
points are skipped/diagnosed, and detached Unset curves alone are Unresolved. Failed
participating section clips affect only overlapping footprints. A null HLD drawing or
missing frame quarantines projection; section-admission diagnostics alone do not.
Fingerprints are cached in one process-wide Lazy and retained in schema-4 messages.
