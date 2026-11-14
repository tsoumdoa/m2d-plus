# S1a — headless worker and ownership feasibility

Research date: **2026-11-14**. Feasibility code and results only; no product implementation, commit, Grasshopper Libraries installation or changes to the owner's Rhino sessions.

**GO for the hybrid worker route on Windows desktop.** A fresh, UI-capable Rhino 8 session on .NET 8 coexisted with four separately licensed Rhino.Inside worker processes under this machine's **standalone evaluation** installation. Named pipes, File3dm packages, exact-version rejection, native cancellation, process-tree termination, allocation containment and restart all worked. Keep small jobs in-process; start production isolation with **one persistent, globally budgeted worker**. Four-worker production admission, commercial/Cloud Zoo deployment, and a universal one-second native cancellation promise are **not** established by this spike.

## Method, identity and evidence

Read PLAN v2's contracts/S1a, Astra issues 1 and 5, and performance.md before implementation. All code and results are in [m2d-plus-s1a-20261114](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114). The C# [console prototype](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/src/Program.cs) targets `net8.0-windows`, x64, initializes the existing McNeel Rhino.Inside resolver, then constructs one `RhinoCore` on its STA main thread. No Rhino.Compute server or service was used. Rhino geometry is loaded into the worker process itself, rather than attached to an existing Rhino process.

| Identity | Measured value |
|---|---|
| OS / CPU | Windows 11 Pro 10.0.26300; AMD Ryzen AI MAX+ 395, 16 physical / 32 logical cores |
| Visible RAM | 66,739,012 KiB, 63.65 GiB |
| Rhino / RhinoCommon | **8.36.26296.17001**, revision `67b86f484e418b0eb5432b6f9733383ebb6b7caf` |
| RhinoCommon SHA-256 | `5fd146708cd1c357760557fb073933e4d873babb56fcee57580c1bf1f8d8fa8b` |
| C# hosts/workers | .NET **8.0.34**, framework-dependent, WindowsDesktop runtime, x64 |
| Existing resolver | `Rhino.Inside_v8_net8.0.dll`, assembly 8.0.7.0; copied from the prior research venv, no installation |
| Licence API | `Standalone`, installation `Evaluation`, BuildType 200, evaluation expires 2027-01-06; `CloudZooLeaseIsValid=false` |
| Campus fixture | 552,924,158-byte prior research asset; SHA-256 `02a56b5803d0d6a80415bcd4a3b2b0cf44e1377ed9c7746a3cd50903238aa324` |
| Campus scope | 2,983 objects, approximately 14 million mesh triangles; full top view, tolerance 0.001 |
| Building scope | Same analytic 305-object repeated building as prior research, represented by **3 definitions + 305 placements**; tolerance 0.0001 |
| Native settings | Full interacting scene, parallel top camera, hidden included, tangent edges/seams off, flatten on, native `multipleThreads=false` |

Evidence: [machine](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/machine.json), [summary](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/summary.json), [artifact manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/artifact-manifest.json). Each result contains the **actually loaded C# DLL hash**, Rhino identity and runtime. Several incremental scratch builds were used; the manifest retains their binaries. The apphost EXE hash alone does not identify the experiment code.

The Python [supervisor](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/supervise.py) launches only owned processes, samples their aggregate private bytes every 100 ms, applies an outer 28 GiB sampled watchdog and a wall timeout. C# independently applies an **OS Job Object** to each worker and descendants. Normal worker job limit: 12 GiB committed memory. Campus transport had a 900 s outer wall budget; ownership campus 600 s. Large transport and ownership work overlapped with some lightweight probes and with each other: **these are operational feasibility timings, not isolated performance calibration**. There was no reboot, standby-cache purge, randomized order, power-policy change or attempt to disturb other applications. Reported medians have n=3 unless specified; campus transport is n=1 initial solve + one baseline + one retained-scene solve.

The building's generated block form matches all 305 original bounding boxes and solid volumes at 1e-6, in addition to using the same explicit box/cylinder construction. This is a fixture-specific check, not general duplicate recognition. See [fixture verification](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/fixture-building.json). `building-blocks-v1.3dm` preserves the earlier IDs; final transport uses `building-blocks.3dm`.

## 1. Licensing and concurrency

**Initially no interactive Rhino.exe was running.** The final test launched its own ordinary UI-capable `Rhino.exe /nosplash /notemplate /netcore-8`, with its window hidden. A startup Python script ran on **that session's UI thread**, confirmed an active document, Rhino build, licence node and .NET 8.0.34 before workers started. It stayed alive throughout the batches. This proves coexistence with an initialized desktop Rhino session; it does not measure a user's editing/redraw responsiveness. No pre-existing process was attached to, automated or terminated.

| Simultaneous workers, in addition to owned Rhino.exe and C# Rhino host | Concurrent building solve wall | Per-worker result | Licence/startup outcome |
|---:|---:|---|---|
| 1 | 1.843 s | 3,160 raw pieces | Ready; solve completed; desktop Rhino alive |
| 2 | 2.006 s | 3,160 each | Both ready and solving concurrently; no licence errors |
| 4 | 2.186 s | 3,160 each | All ready and solving concurrently; no licence errors |
| Commercial standalone / Cloud Zoo / LAN Zoo | Not measured | This machine supplies evaluation standalone | No licence was moved, replaced or checked out for the experiment |

Evidence: [concurrency](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/concurrency-headful.json), [verified .NET 8 UI startup](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/headful-net8-ready.json). Workers reported the same standalone evaluation status and no valid Cloud Zoo lease. Successful geometry probes, file writes and HLD demonstrate functional availability; there was no Zoo server telemetry from which to count seats or checkout transactions. Do not infer a measured commercial licence-checkout policy from evaluation success.

Current vendor guidance, checked 2026-11-14:

- The [Rhino 8 EULA, updated March 6, 2026](https://www.rhino3d.com/en/8/eula/) permits multiple sessions for one user on one computer/VM; its simultaneous-login-session condition still applies. This supports the local desktop design, not a multi-user rendering service.
- McNeel's [Rhino.Inside process discussion](https://discourse.mcneel.com/t/running-multiple-processes-with-rhino-inside/126655) explains one RhinoCore per process and communicating between separate processes; persistent hosts are possible without adopting Compute's REST layer.
- In the [headless licensing discussion](https://discourse.mcneel.com/t/managing-multiple-rhino-8-instances-in-headless-mode/194039), McNeel staff say multiple single-computer instances should work, but the thread also records service/DCOM failures and account-context issues. Treat those deployment reports separately from a desktop child process.
- The [LAN Zoo guide](https://www.rhino3d.com/en/docs/guides/licensing/lan-zoo/) says several Rhino instances in the same login session on one computer consume one licence. That is vendor guidance, not a LAN Zoo measurement here.

**Conclusion:** local worker licensing is feasible on the tested installation, and vendor guidance supports same-user desktop instances. Commercial standalone, Cloud Zoo, LAN Zoo, offline/expired licence and licence-loss recovery need deployment smoke tests. Do not assume Rhino.Compute billing/licensing rules are the worker's rules.

## 2. Startup and persistent-worker cost

Ready means core initialized, a valid Brep created, licence identity obtained, request/control pipes connected and exact handshake accepted. Process-start timers are taken by the C# parent; core timers start in the child managed entry point.

| State / interval | Measurement | Interpretation |
|---|---:|---|
| First empty-process launch, core / local readiness | 1.895 / 1.905 s | First experiment launch; **not verified cold disk/OS cache** |
| Five later fresh workers, core initialization | Median 1.513 s; 1.359–1.704 s | OS/Rhino files already warm |
| Same five, parent Process.Start → protocol ready | **Median 1.649 s; 1.508–1.861 s** | Includes process/bootstrap, geometry probe and handshake |
| Persistent-worker ping round trip, 50 requests | Median **8.17 ms**; 4.94–15.30 ms | Includes JSON framing and querying worker memory; not pure pipe latency |
| Empty worker, one second after ready | Median **284.45 MiB** private; 283.93–285.14 MiB | n=5 |
| Building retained after solves | **383.54 MiB** private | Last final building trial |
| Campus retained after two solves | **3.306 GiB** private | Not an empty-worker idle figure |
| Campus worker lifetime job peak | **5.179 GiB** | OS job accounting; transport experiment |
| Campus host + worker sampled aggregate peak | **9.105 GiB** | Includes source file, owned snapshot, buffers, result construction and repeated baseline work |

Evidence: [startup](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/startup-final.json), [campus transport/watchdog](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/transport-campus-watchdog.json). Some concurrent-start probes took longer under load; 1.65 s is not a service-level maximum. A true machine-cold/licence-cache-cold measurement remains unavailable because this spike did not reset the owner's machine or licence state.

**Recommendation, policy estimate:** start lazily, retain one worker across views, and retain a scene only under a bounded cache lease. A five-minute idle timeout is a reasonable initial tuning value, not measured optimal. Retire a large retained scene first; recycle the process when its native high-water footprint remains material. One 284 MiB empty worker is practical here; four permanently campus-loaded workers are a different memory decision.

## 3. Transport round trip and in-process comparison

Two duplex **named pipes**: one for framed JSON + File3dm bytes, one for cancellation. A frame has 32-bit JSON/payload lengths followed by UTF-8 JSON and binary archive bytes. The input manifest carries source IDs, document attribute values, definition membership/IDs, instance transforms, units/tolerance and scene/view/settings/generation fields. Full File3dm attributes/tables travel in the archive too; production resolution of styles/material inheritance is outside this spike.

Definitions are serialized **once**, not expanded per instance. The worker duplicates each definition geometry into its owned scene once, and supplies each leaf to HLD with its composed transform and source path. Campus has no block definitions. No Brep was silently replaced by a mesh. Decoding includes native File3dm reconstruction, duplicate/detach and manifest parsing. Worker scene caching was demonstrated by a second solve with no scene re-send.

Results contain File3dm curves with source/visibility user strings plus per-piece JSON: curve GUID, source/instance path, native visibility, silhouette kind and index. **All extractable raw native classes**, including Duplicate/Projecting, are retained for transport accounting. Consequently 3,160 / 131,532 are not the filtered 214 / 110,494 drawing counts in performance.md. This experiment is not a new drawing-output policy.

| Stage | 305 placements, n=3 median | Full campus, n=1 |
|---|---:|---:|
| Initial source File3dm read, outside round trip | 0.00454 s | 3.367 s |
| Owned source capture from File3dm, outside round trip | 0.0103 s | 0.296 s |
| Host File3dm write to byte array | **0.00213 s** | **13.217 s** |
| Host JSON scene manifest encode | 0.00197 s | 0.0442 s |
| Input transfer + frame parse → worker receipt acknowledgement | **0.00138 s** | **0.211 s** |
| Worker decode/reconstruct | 0.00664 s | **3.343 s** |
| HLD parameter/bounds preparation | 0.00148 s | 0.1158 s |
| Worker HLD, first solve | **1.620 s** | **179.414 s** |
| Owned extraction + output object construction + signature sampling | 0.0436 s | 2.472 s |
| Result File3dm + per-piece JSON encode | 0.0255 s | 1.181 s |
| Return envelope/transfer residual, derived¹ | 0.0219 s | 1.031 s |
| Host File3dm decode, independent curve duplication, validation/disposal | **0.0410 s** | **2.762 s** |
| Host per-piece JSON → owned value records, separate n=3 probe² | 0.00927 s | 0.3566 s |
| Same in-process HLD, identical scene/settings | **1.605 s** | **184.118 s** |
| Same in-process HLD + preparation/extraction, derived sum³ | About 1.657 s | **186.873 s** |
| Initial scene pipeline, excluding startup/read/capture/metadata probe⁴ | **1.767 s** | **203.872 s** |
| Retained-scene request → received result, before host curve decode | **1.743 s** | **191.097 s** |

¹ Request/receive round-trip minus the worker's timed solve total. Includes return framing, outer JSON serialization/parsing, signature finalization and transfer; **not a pure bandwidth timer**. Input receipt timing likewise includes frame parsing. No unsupported subtraction is presented as pure wire time.

² File text was loaded before timing. C# parsed the complete per-piece manifest into independent strings/values and a GUID map, then disposed the JSON document. Campus: 131,532 distinct GUIDs; building: 3,160. This was measured separately, not integrated into the original curve-decode timer. The JSON metadata and curve ownership checks are separate; a production atomic geometry-to-metadata join/publication adapter is still required.

³ Sum of equivalent native preparation, compute and extraction timers. The raw in-process prototype also serializes its result for verification: whole call 1.703 s median / 188.898 s campus. Production in-process output need not pay File3dm result encoding. Building row sums of individual medians are approximate, not a separate trial.

⁴ Sum per trial of host scene encodes, load round trip, solve/result round trip and host curve decode; then median. Adding the separately measured metadata median and measured startup is an **estimate**, approximately **3.42 s building / 205.73 s campus**, not a separately observed end-to-end trial. Source capture/read and GH output/publication remain outside those totals.

| Payload / equivalence check | Building | Campus |
|---|---:|---:|
| Scene archive / JSON | 153,850 / 91,991 bytes | 552,925,308 / 862,856 bytes |
| Reconstructed owned geometries / HLD placements | **3 / 305** | 2,983 / 2,983 |
| Result archive / unescaped JSON | 2,724,751 / 584,604 bytes | 102,056,627 / 19,414,180 bytes |
| Raw result curves | 3,160, all valid | 131,532, all valid |
| Worker, baseline and retained-scene signatures | Equal in all three trials | Equal across all three solves |
| Host copies after File3dm disposal | Valid, detached; length checked | Valid, detached; length checked |

Evidence: [building transport](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/transport-building-final.json), [campus transport](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/transport-campus.json), [metadata probe](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/metadata.json). The retained campus HLD itself took 185.904 s; differences from 179.414 / 184.118 s exceed individual IPC phase costs. **No worker speedup is claimed.** Single-thread native operation is slower than the prior 83.902 s internally threaded campus measurement; this spike selected deterministic settings rather than comparing unlike threading configurations.

Signatures sort source ID, visibility, length and five curve samples rounded to 1e-6. They establish sampled transport equivalence, **not full geometric truth**. An independent [box-and-crossing-line oracle](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/oracle.json) passed in both routes: expected hidden x interval [-1,1], hidden/visible lengths 2 / 2; measured hidden endpoint error 2.75e-5, below 1e-4 tolerance. No independent whole-campus drawing oracle was added.

**Transport decision:** use named pipes for control and binary File3dm scene/result frames initially. The 527 MiB scene transfer was 0.211 s versus 13.217 s to encode and 3.343 s to reconstruct: memory mapping would not remove those dominant costs. Keep a scene resident across views and send revisions/definitions once. Production should bound frame sizes, avoid nested JSON strings/base64 and large simultaneous byte-array copies, support chunked/backpressured payloads and validate checksums/IDs before publication. A temporary archive plus pipe descriptor is a reasonable later bulk-storage option; neither it nor MMF was benchmarked here.

## 4. Cancellation, process-tree and allocation containment

The parent creates a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_JOB_MEMORY`, assigns its owned worker, prevents breakaway, and uses a completion port for job events. Cancel travels on the independent control pipe while the main thread is inside HLD. Native cancellation keeps input geometry alive until compute returns; the worker then disposes its scene/core and exits. Hard termination uses `TerminateJobObject`, never a process-name sweep.

| Injection, n=1 each | Detection / release measurement | Outcome |
|---|---|---|
| Protocol cancel, 200 ms after campus HLD marker | Ack **2.81 ms**; request → worker exit **0.384 s** | `cancelled` reply, exit 0; no hard escalation |
| Job kill, 200 ms into campus HLD | Kill request → worker exit **0.175 s** | Exit 77; EOF detected; owned sentinel descendant gone and job active-process-zero observed during cleanup |
| Job allocation limit during campus HLD | Cap **2,223,706,112 bytes (2.071 GiB)**; solve dispatch → exit **15.594 s** | `JOB_MEMORY_LIMIT` event, worker exit 1, EOF; no clean OOM result, no host crash, no watchdog kill |
| Intentional `Environment.FailFast` | Crash request → EOF/exit detection **0.147 s** | Exit -2146232797; host remained alive |
| Replacement after cancel / kill / memory / crash | **1.453 / 1.739 / 1.577 / 1.450 s** to ready | New process identity, successful handshake and ping in the surviving host |

Evidence: [cancel](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/contain-cancel.json), [tree kill](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/contain-kill.json), [memory](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/contain-memory.json), [crash](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/contain-crash.json). The tree-kill number times the worker process wait; descendant/job-empty verification followed immediately, without a separately instrumented tree-cleanup stopwatch. It is evidence of cleanup, not a universal 175 ms tree-release bound.

For the OOM probe the scene was first loaded under the normal 12 GiB cap, then the limit was lowered to current worker private bytes + 128 MiB. HLD's own allocations reached it; no synthetic allocation leak was used. The exact instant of the limit notification was not timestamped, so **15.594 s is not hit-to-exit latency**. Native/managed OOM subtype was not recoverable from a structured reply. Lifetime job peak (2,224,435,200 bytes) includes the earlier load before the limit was lowered and must not be interpreted as an exact post-limit maximum.

Microsoft documents [job memory limits as failed commit requests](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information), not guaranteed graceful exceptions or automatic termination. [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) also provide child containment and kill-on-close. A job commit limit is **not a working-set or whole-machine RAM cap**. The sampled outer watchdog is a separate fail-safe and can overshoot its threshold.

**Recommended policy, not yet a production implementation:**

- One parent-owned job per worker tree, no breakaway, kill-on-close. Create suspended → assign job → resume in production; the prototype assigns immediately after Process.Start and has a small startup race.
- Initially 12 GiB maximum job commit per admitted heavy worker on this 64 GiB machine, further reduced by the **shared scheduler's aggregate allowance**. Reserve at least max(8 GiB, 25% of physical RAM) for Windows/interactive Rhino and account for current available memory, source snapshot, transport/output buffers and all retained workers. These reservation values are estimates requiring workload calibration.
- Admit one native solve per process. Do not start replacements until the old job is empty and its memory charge has settled. Limit event, EOF or abnormal exit invalidates the whole request; never publish a partial archive.
- UI cancel immediately rejects that generation. Send protocol cancellation, allow an initial **500 ms** grace estimate, then terminate the worker job and wait for exit/job-empty. In-process native work cannot receive this guarantee.
- Treat a memory-limit notification as fatal for that worker and terminate promptly; do not wait for a tidy OOM message. Also watch process exit, pipe failure and a wall deadline: ordinary job completion-port notification delivery is not itself guaranteed. Retry at most once **after** release and only with changed budget/scope or explicit user action. Do not automatically repeat the identical over-budget solve forever. Recovery here proves replacement readiness, not automatic resumption of a lost job.

## 5. Version/runtime handshake

| Check exercised | Result |
|---|---|
| Matching identities | Request/control pipes became ready, subsequent HLD completed |
| Independently alter schema, plugin DLL SHA-256, Rhino build, Rhino revision, RhinoCommon version, RhinoCommon SHA-256, runtime version or architecture | **8/8 refused**, exact differing key returned, exit 0 before scene load |
| Same Rhino 8.36 build, UI launched without a runtime override | **.NET 10.0.15**, startup script executed and active document verified |
| Same UI launch with `/netcore-8` | **.NET 8.0.34**, matches tested worker; 1/2/4 concurrency succeeded |

Evidence: [handshake injection](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/handshake.json), [actual default UI runtime](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/headful-default.json), [explicit .NET 8 UI identity](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/headful-net8-ready.json). An actual mismatched-runtime UI-to-worker pipe session was not built; refusal was tested by changing the expected runtime field. This validates the comparison mechanism, not a .NET 10 worker implementation.

The new [Rhino 8 runtime knowledge-base article](https://www.rhino3d.com/kb/rhino-8-net-core-runtime-and-lan-zoo/) says 8.36+ defaults to .NET 10. The older [runtime selection guide](https://developer.rhino3d.com/guides/rhinocommon/moving-to-dotnet-core/) documents `/netcore-8`. The measured installed behavior, rather than “Rhino 8 means .NET 8,” must drive launch selection.

**Contract:** have the real host advertise its loaded build/runtime/architecture, plugin/engine hash and protocol schema; launch a compatible worker using that installation and supported runtime, then compare before accepting scene bytes. Include engine settings/threading, units/tolerance, capabilities, source-order policy, revisions and generation in request identity/cache keys. Exact patch matching is deliberately conservative initially. Reject mismatch as `WorkerUnavailable(VersionMismatch)` with expected/actual values. This .NET 8 spike is **not** ready to silently handle the default .NET 10 UI runtime: either supply/test a matching worker runtime variant or document the supported .NET 8 launch. Do not change the owner's runtime settings automatically.

## 6. In-process ownership and lifetime micro-spike

An owned `RhinoDoc` was created/opened on the C# **STA main thread (thread 1)**. That same thread enumerated RhinoObjects, duplicated geometry and attribute values, resolved definition members once and captured transforms. Each geometry receives `Duplicate()` then `EnsurePrivateCopy()`. No live RhinoObject, document, definition lookup or document attribute wrapper enters the Task.Run solve. A main-thread “UI” here is the designated Rhino.Inside host thread; the timing tests were **not an interactive GH solution/redraw benchmark**.

| Fixture / capture path, n=3 each | Total median (range) | Longest measured uninterrupted chunk | Owned geometry / placements |
|---|---:|---:|---:|
| Campus, whole | **215.8 ms** (205.3–255.3) | 255.0 ms | 2,983 / 2,983 |
| Campus, chunks of 250 | **215.7 ms** (173.4–254.7) | **142.6 ms** | 2,983 / 2,983 |
| 50k small Breps, whole | **2.732 s** (2.607–3.293) | 3.293 s | 50,000 / 50,000 |
| 50k small Breps, chunks of 250 | **2.784 s** (2.717–2.938) | **67.0 ms** | 50,000 / 50,000 |
| Block building, whole | 1.43 ms (1.13–12.48) | 12.17 ms | **3 / 305** |
| Block building, chunks of 250 | 1.01 ms (0.80–9.10) | 1.41 ms | **3 / 305** |

Chunk timers exclude the synthetic `Application.DoEvents` yield itself; whole elapsed includes it. There was no real UI workload between chunks, so these are copy-slice costs, not measured screen-response latencies. The first block capture includes JIT/table traversal warmup. No forced “deep materialization” or cache-warming geometry operation was applied to artificially make the copies cheaper.

The generated [50k fixture](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/fixtures/small-breps-50000.3dm) has 50,000 independent 0.2×0.3×0.4 boxes on a spaced grid, **689,359,654 bytes**. Generation/addition to the owned document took 7.081 s; writing it took 46.809 s. It is a snapshot stress fixture, **not** the plan's projected-high-overlap HLD stress oracle. Snapshotting 50k objects in under 100 ms was not achieved.

| Lifetime/semantics probe | Measured outcome |
|---|---|
| Borrow live document geometry | `IsDocumentControlled=true` |
| Duplicate, then mutate duplicate | Duplicate not document-controlled; original document geometry CRC unchanged |
| Retain a wrapper after `EnsurePrivateCopy`, called twice | Not document-controlled; valid length-1 curve after source deletion and document disposal |
| Dispatch owned snapshot, wait until HLD entered, transform 10 sources and delete 10 on main thread, then close document | Campus, 50k and block tests: HLD still running when edit began; owned input CRCs unchanged; every owned geometry valid afterward |
| Modify block definition while its detached snapshot solves | Passed: definition replaced, document closed, 3 owned definitions still valid; block result signature equals the transport's earlier equivalent scene signature |
| Campus background solve | Full 2,983-object HLD completed, 210.274 s; 131,561 raw pieces, none invalid |
| 50k background solve | All 50k owned inputs CRC/validity checked; **only first 305** sent to HLD (1.320 s). A full 50k solve was not attempted |

Evidence: [campus ownership](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/ownership-campus.json), [50k ownership](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/ownership-50k.json), [block ownership](C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114/results/ownership-building-blocks.json). The campus document-loaded run has 29 more raw pieces than the File3dm transport runs despite virtually identical total length. Source ownership survived, but this **does not establish whole-output equivalence across document/file load paths**; retain it as a repeatability/normalization investigation. CRC is only an intra-test mutation detector, never the production cache identity.

McNeel's [CommonObject contract](https://developer.rhino3d.com/api/rhinocommon/type/rhino.runtime.commonobject/) documents detaching retained wrappers with EnsurePrivateCopy. The installed [GeometryBase API](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.geometrybase/) distinguishes Duplicate's deep copy from DuplicateShallow's shared copy-on-write data. **EnsurePrivateCopy is not an immutability lock or a blanket thread-safety guarantee.** Never detach/mutate a borrowed wrapper concurrently with document activity; do it on the host thread before dispatch. Do not retain BrepFace/edge/proxy wrappers as independent owners. HLD/proxies stay job-local; returned curves are copied before HLD disposal. Keep snapshots alive until native work has actually stopped, even after UI cancellation.

**Ownership recommendation:** retain already-owned unchanged entries under leases; cold-capture changed geometry on the host thread with an initial 8–16 ms time budget rather than 250 objects. Measure and report giant single-object copy overruns. Resolve IDs/definitions/attributes on that thread, snapshot definition geometry once, and verify document/definition generations between slices and before accepting the scene. If they changed, retry or discard the capture. No generation-race publication, cache eviction/lease race, GH output-mutation or real interactive responsiveness acceptance test was implemented here; those remain M1 gates.

## Execution recommendation and initial thresholds

| Decision | Recommendation | Basis / remaining calibration |
|---|---|---|
| Worker route | **GO**, implement in M0/M1 contract; ship one-worker isolation before parallel admission | Operational coexistence, round trip and containment demonstrated |
| Warm worker | Lazy, persistent, scene retained across views, bounded memory/idle expiry | Empty 284 MiB practical; campus retained 3.31 GiB needs policy |
| Transport | Named-pipe control + binary File3dm/JSON packages, definition-aware | Encode/reconstruct dominates campus byte transfer; MMF not justified yet |
| Native scheduler | One global budget across components/routes; one HLD per process | Four small workers worked; no parallel-campus throughput/correctness claim |
| Memory | Worker Job Object commit cap plus aggregate admission/reserved headroom | 12 GiB worked for campus plan; memory pressure may terminate abruptly |
| Worker unavailable | Visible structured reason; retain last valid result only as stale; no new confident partial output | No silent heavy-job fallback into interactive Rhino |
| Retry | Bounded restart after old job death, no identical OOM retry loop | Replacement ready in 1.45–1.74 s; lost computation not resumed |
| Default UI runtime | Detect and match; .NET 8-only worker is unavailable to a .NET 10 host under exact contract | Actual Rhino 8.36 default is .NET 10 on this installation |

**Initial routing estimates, not calibrated thresholds:** allow in-process only for known low-risk scenes with predicted native time below about **2–3 s**, predicted additional memory below roughly **0.5 GiB**, no hard-release requirement and a cheap/owned snapshot. The building is a useful boundary sample, not a universal 305-object cutoff. Route campus-scale scenes, unknown overlap-heavy views, expensive multi-view sets, or any request requiring hard cancellation/crash isolation to a worker even when isolation adds latency. A worker is not a latency optimization for small cold requests: startup alone is about 1.65 s here.

Use at least: serialized bytes; Brep face/edge and mesh triangle counts; geometry types and largest-object size; conservative projected overlap/depth occupancy; view direction/section/crop; number of views; expected output pieces; resident scene/worker/cache state; predicted aggregate private/commit peaks; and previous solve tails. Whole-scene object count is inadequate. On this campus, encoding/reconstruction/owned-output costs total several seconds even though HLD dominates. With a retained scene, avoid the 13.2 s encode and 3.3 s reconstruction entirely. Treat a snapshot over a UI slice budget as a capture-scheduling problem, not permission to borrow document pointers.

**No-go for freezing broader promises yet:** commercial/Zoo/offline recovery, a matching .NET 10 runtime variant, production secure/bounded framing and generation-gated batch publication, real GH/Rhino editing latency, memory-admission calibration, higher-overlap 50k solves, nested-definition lifecycle cases, and multi-campus-worker throughput remain unproven. S1a supports the worker boundary and unavailable-worker contract; it does not replace S1b/M1/M5 acceptance work.

## Reproduction and limitations

Use the existing venv Python; build outputs stay in scratch. Example:

```powershell
$s1a='C:/Users/tomosandego/.t3/scratch/m2d-plus-s1a-20261114'
$py='C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
dotnet build "$s1a/src/S1a.csproj" -c Release -o "$s1a/bin-rerun"
$env:S1A_EXE="$s1a/bin-rerun/S1a.exe"
& $env:S1A_EXE $s1a fixtures
& $py -B "$s1a/supervise.py" bench concurrency-headful 180
& $py -B "$s1a/supervise.py" bench startup 120
& $py -B "$s1a/supervise.py" bench handshake 180
& $py -B "$s1a/supervise.py" bench transport-building-final 120
& $py -B "$s1a/supervise.py" bench transport-campus 900
& $py -B "$s1a/supervise.py" bench metadata 120
& $py -B "$s1a/supervise.py" bench contain-cancel 180
& $py -B "$s1a/supervise.py" bench contain-kill 180
& $py -B "$s1a/supervise.py" bench contain-memory 180
& $py -B "$s1a/supervise.py" bench contain-crash 120
& $py -B "$s1a/supervise.py" ownership building-blocks 180
& $py -B "$s1a/supervise.py" ownership campus 600
& $py -B "$s1a/supervise.py" ownership 50k 300
& $py -B "$s1a/supervise.py" bench oracle 120
& $py -B "$s1a/analyze.py"
```

Run reruns serially and preserve existing result/fixture IDs first. Reproduction needs the licensed installed Rhino build and the existing resolver/runtime. Earlier hosting/command-line pilots failed before valid measurements; final coexistence uses Rhino's documented `/runscript="..."` spelling and an atomically written readiness marker. Those pilot failures are not licence-concurrency failures. All owned Rhino/worker processes were cleaned up; final process inventory contained no running Rhino.exe or S1a.exe. No reboot, licence manipulation, unrelated process termination, commit or Grasshopper installation was performed.
