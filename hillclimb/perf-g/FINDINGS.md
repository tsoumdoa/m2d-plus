# PERF G: worker admission, scheduling and exact face preparation

Worktree base: `28d232dd050c06001b9d659555b27f36a63f959f` (main). Windows x64, 32 logical processors, 63.65 GiB usable RAM; Rhino 8.36.26296.17001, runtime 8.0.34 for performance runs. Shared machine: other agents and the owner's interactive Rhino remain outside our control. Nothing committed, pushed, installed or published.

The full campus 8-/16-view scaling matrix remains **blocked and unverified**: its automatic caps cannot fit four/eight workers on this machine, and the North reference remained in native HLD after 29.9 minutes. Completed transport and Top controls are reported separately below.

## Retained changes

- CPU ceiling is cores minus two, limited by views and the aggregate memory reservation. The 32 GiB machine class (30 GiB reported usable, allowing hardware reservations) uses min(75% physical, 85% available); smaller hosts keep min(60%, 80%). Default view caps remain conservative and unbounded above 16 GiB. The sizing and limits tables are in `docs/design/drawing-workers.md`.
- Schema 3 replaces the outer base64 JSON envelope with bounded gzip over an operation header and raw payload bytes. The real 50k scene exposed a streaming JSON buffer-growth failure rather than a memory-cap hit. Scene/result JSON stays byte-identical; exact schema/engine matching rejects older workers.
- A worker deadline now fails only its view; it no longer escapes as caller cancellation and discards sibling results. A licensed blocked-native regression verifies this distinction.
- Work is taken from one queue, keeping reply/publication indices fixed. Subsequent same-scene batches start the longest measured views first. History holds only the current batch, resets with scene revision, and does not learn failures. Managed processor allowances are divided across workers; children run BelowNormal priority.
- Triangle planarity is a construction invariant, so exact residual validation is unnecessary. For larger polygons, outward interval arithmetic certifies the *same* squared plane-residual inequality; inconclusive cases still validate exactly. Filtered clipping and exact fallback are unchanged. This also removes eager exact preparation from the point-query index used during composition.

Scene shipping was already once per participating process. View-only changes already avoid encoding/shipping the retained scene, so no duplicate scene/delta cache was added. Warm spawning and output/GH changes were not pursued: startup is a small part of these sets, and other agents own the GH work. No rejected curved-culling/mesh-visibility algorithms were repeated.

## Method and limits

The eight-view set is North/South/East/West/Top/SW, the model's named long section, and z=1.2 plan. Sixteen views append NE/NW/SE/Bottom, transverse and reverse sections, and two plans at quarter/three-quarter model height. Hidden is on, native threading off, document tolerance. Local solves use production NativeDrawing outside interactive admission, one view at a time on the same owned scene. Local wall excludes capture, scene encoding and result hashing/serialization; worker wall includes process startup, exact handshake, scene shipping/preparation, solve, compressed reply transfer and coordinator file writes. Hash checking and its GC are outside the worker timer.

Peak local memory is process-private bytes sampled every 25 ms (including archive/hash work). Worker peak is the **sum of Job Object peak commit counters**, a conservative upper bound, not simultaneous aggregate resident memory. Their numbers are not directly comparable. Cold means fresh processes, not cold OS caches. Each configuration is one cold and one retained-scene pass, not a statistical performance claim. Report actual admitted counts, not merely requested counts; availability changes during the run. Original baseline worker passes were affected by coordinator verification garbage (not collected between passes in the original harness), so their varying counts are not a controlled scheduler comparison.

All stable wire bytes are hashed, including geometry archives, attribution, ordering, depth, settings, quality, all stored drawing fields, regions, provenance and hatches. Only timings, phase counters and diagnostic prose are excluded by the existing worker comparison. PERF F dumps are restored from these stable payloads; their independent comparator checks bidirectional same-class locus cover and section topology. M comparison runs use `--step-mult 200` (spacing only; geometric tolerance is unchanged).

Raw evidence is git-ignored under `artifacts/perf-g`; final verification logs are `artifacts-perfg-*.log`. Baseline binary snapshots are retained there. Models remain under `~/.t3/m2d-plus-models`; no generated models are in Git.

## Before/after local pairs

| Model | Views | Original s | Candidate s | Equal stable view hashes |
|---|---:|---:|---:|---:|
| M | 8 | 59.918 | 51.370 | 8/8 |
| M | 16 | 156.189 | 133.859 | 16/16 |
| L | 8 | 208.503 | 177.177 | 8/8 |
| L | 16 | 562.063 | 515.535 | 16/16 |

Full per-view hashes (original = candidate):

| M view | Stable SHA-256 |
|---|---|
| 0: North | `E74712F3FA8752A50AE7BADB42967744BCCECE47E72A3F185E56467E6404C817` |
| 1: South | `A0377C9307357F681D466BD3529D5DCF89A7D7E1C3A2AE46A7EDA1300CF4F758` |
| 2: East | `0E0CAB9FD025CE88465E594DA99D27BE5FADB04110AEE23C701BEFB791A9E437` |
| 3: West | `48261D518DAC1B7A75FEA457CC0C6B5C12388DE103231DEB88593B6A62A2E455` |
| 4: Top | `B1FD9EBBB3CC30ABA71F9A7FB323A4761523F13D142677B67C2590BA122BF764` |
| 5: SW | `01FB51139C528E95CE77324D9FC3A4D19D17048CCC6FB2D50D48A2C4A6C95421` |
| 6: Section long overlap | `647867865406E24A0DD1ABE3EA103937AE45B7EE928AFB8BC9989DCBFFB3BFF1` |
| 7: Plan | `5D3D59BADD7F7C6744E5C2CC67AA37AFA4EA3C4184DE51181AC8687CC31A246C` |
| 8: NE | `76263BC4ED196B6D9D51A8E3CD9D185BBA3A904C51F45303D221BADF541EE561` |
| 9: NW | `44000FCACF0F2FC6AD0D7A0CE2A587895A2B0E33140891DA517F9F99D1F6FA26` |
| 10: SE | `69548C62F03DE40CD3207616ACA2540738531ABA1D2D6D3C0B92F70A98B4E3F6` |
| 11: Bottom | `F3B726E3C8A35747A34991836D45A4CC7F61715ED96413BF992E6D4094659C10` |
| 12: Transverse | `62D469F474259C835A0CF0D973A77C959842FC1EEB0F94D811FBA6030B84E095` |
| 13: Plan quarter | `62C7731350228A501F492B953AE025752F5BE3B08392CE386A506D6D768A8717` |
| 14: Plan upper | `D2DC98C47D934200201AB3C989548C9507383A821F0EDA27C8BBB7433F7D182C` |
| 15: Long reverse | `A2DBF58B4C1010E8B2543563DA03C3B764D7F1B43E82E60332A56A0D7C0AB0EC` |

Full L per-view hashes (original = candidate):

| L view | Stable SHA-256 |
|---|---|
| 0: North | `7EFA1A1322E2943BB4D0B902805F60716235EDABB1405A59A8DF61A8E58B1C22` |
| 1: South | `B78E88628BE5DBFF0E973D2F5DF853B160E585381A02FBBE8C9545FBDD489180` |
| 2: East | `550BF1C2450E48DE36920A031297F0BE59C36E5D91FACB4286BC7AF9845605E4` |
| 3: West | `6D116D2555CC70715713F19A1AE827082938F04A25457A9D2D306C88F6162BCF` |
| 4: Top | `74559A63CE425C5F6C8392E7AAAB171C79BB009AA00938483FDE76DDE937E620` |
| 5: SW | `B0B7828BB497FDD23B996796F90A9DCD21B815650C568CA351F451CE0C47CA1B` |
| 6: Section long overlap | `ACBED1122D4BCE538AC186FAAA5892824CF4F99F28024D4E9F62BBEB66C823F5` |
| 7: Plan | `4C894B939BD1C0622D17E2E1B6C3C15F897132767DD847BAD6FDB6446500D004` |
| 8: NE | `95B8C241E53B579EA0540DB6128C28C4A82A70317953F15127D6AD5ECF0D72C9` |
| 9: NW | `18821DFE4FE12FC08A5F5DDED542F9A5806B5D06CF6F43176C8816758FB55205` |
| 10: SE | `431A099257E6B358453366743BF6718687ACC743CA8D7829BA9335D22F10652D` |
| 11: Bottom | `0B687C9E8C1D49FE942D9DB316B5B761B44549D4257A07330740F910AA0A2C17` |
| 12: Transverse | `332CFDEEF95AB2CDD29D282CC206F5E7565C4C95385688907B41B4B3308F2159` |
| 13: Plan quarter | `77D5A8AF82A9B5509AF77016D2F02C9D4D56B3E074584141ADCE6B571B6DA689` |
| 14: Plan upper | `C07D97D21118FADAFAD8E9F8BC467E538C8A2E0F51BF1D614B27C4A666F5EB81` |
| 15: Long reverse | `E99B3D889CF8E61282B7131328AE13B13B725E751BFF247F8CD9AFCDD67ECF41` |

## Eight-view phase profiles

Seconds summed over the first eight views. These are instrumented phase totals, not worker batch walls; `native ray audit work` is nested in verification and is intentionally omitted. Capture and encoding precede the timers.

| Phase | M main | M candidate | L main | L candidate |
|---|---:|---:|---:|---:|
| kernel solve | 12.500 | 7.662 | 45.150 | 27.117 |
| composition setup | 5.987 | 1.247 | 26.270 | 6.005 |
| native solve | 14.790 | 14.941 | 55.279 | 57.819 |
| planar extraction | 2.344 | 2.821 | 9.314 | 10.352 |
| native extraction and duplicates | 6.028 | 5.922 | 25.949 | 27.321 |
| native ray verification | 3.617 | 3.645 | 4.659 | 4.554 |
| route C trim | 3.576 | 3.481 | 4.264 | 4.675 |
| curve-plane events | 1.510 | 1.373 | 6.061 | 6.145 |
| interval union | 1.860 | 1.968 | 7.023 | 7.571 |
| output construction | 2.082 | 2.008 | 8.496 | 8.751 |
| section output construction | 1.920 | 2.449 | 2.180 | 2.084 |
| sections | 1.371 | 1.455 | 1.796 | 1.820 |

The remaining local costs are native HLD, kernel clipping/indexing, and native extraction/duplicate processing. Eager rational face preparation was paid twice: once in the kernel and once in the composition point-query index. The retained certificate removes both costs without changing clipping decisions. Native setup/extraction and GH output are left to their current implementations.

Retained-worker phase profiles show the same remaining costs. Values below are summed over eight views, not batch wall time. Main M retained four processes; main L resized to two, while the candidate retained four/three respectively. These are workload profiles under contention, not a controlled concurrency speed comparison. Main phases come from the preserved final reply payloads; candidate phases come from pass 1 of `pool-8-4.json`.

| Worker phase | M main | M candidate | L main | L candidate |
|---|---:|---:|---:|---:|
| kernel solve | 13.617 | 7.648 | 47.082 | 29.728 |
| composition setup | 6.990 | 1.140 | 28.317 | 5.267 |
| native solve | 15.389 | 16.018 | 54.762 | 56.336 |
| planar extraction | 2.678 | 2.502 | 8.803 | 9.091 |
| native extraction and duplicates | 6.315 | 6.417 | 26.643 | 26.358 |
| output construction | 2.055 | 2.018 | 8.679 | 9.070 |

## Synthetic preparation pairs

100,000 faces with 100 crossing candidates, one Core thread, three passes. Simple binary-fraction coordinates make these cheaper than the arbitrary Rhino coordinates in the curved spike; do not compare microseconds directly to its 41-45 us measurement. All three passes have the same complete VisibilityResult hash.

| Faces | Original s (passes 0/1/2) | Candidate s | Result SHA-256 |
|---|---|---|---|
| Triangles | 1.408 / 1.006 / 1.369 | 0.706 / 0.377 / 0.348 | `7DA56226D67FDEED67C3214E589CAF31DA63BB7E82CB7353A41B84B8C1B57471` |
| Quads | 1.622 / 1.603 / 1.606 | 0.379 / 0.371 / 0.347 | `E4BE09CC8FB7212CAAEF821BCAC6858750BF6E6A540E40DED6CFA8847F94EB99` |

## Scaling measurements

Requested counts are **not** claims of actual concurrency. Caps constrain the actual counts. Pass 1 is called retained only if every process ID matches pass 0; changing processor allowances retires workers, so a resized pass is another cold process start. Job peaks are sums of per-job peak commit, not aligned resident peaks. These cases used real current physical/commit availability; the `M-pressure` folder retains the earlier, more constrained observations.

| Scene/views | Requested | Workers p0 / p1 | Cap GiB | Wall s p0 / p1 | Job peak upper GiB p0 / p1 | Equal hashes | Pass 1 |
|---|---|---|---:|---|---|---|---|
| M/8 | 4 | 4 / 4 | 3 | 25.259 / 16.015 | 4.786 / 6.075 | 16/16 | retained |
| M/8 | 8 | 7 / 6 | 3 | 21.893 / 21.095 | 6.969 / 6.308 | 16/16 | restarted/resized |
| M/8 | max | 7 / 6 | 3 | 21.322 / 22.082 | 6.911 / 6.224 | 16/16 | restarted/resized |
| M/16 | 4 | 4 / 4 | 3 | 49.155 / 42.675 | 8.674 / 9.919 | 32/32 | retained |
| M/16 | 8 | 5 / 4 | 3 | 46.442 / 50.388 | 7.148 / 6.142 | 32/32 | restarted/resized |
| M/16 | max | 7 / 5 | 3 | 36.554 / 43.276 | 10.847 / 9.315 | 32/32 | restarted/resized |
| L/8 | 4 | 3 / 3 | 6 | 76.870 / 64.816 | 9.406 / 11.868 | 16/16 | retained |
| L/8 | 8 | 3 / 2 | 6 | 76.182 / 95.438 | 9.374 / 6.815 | 16/16 | restarted/resized |
| L/8 | max | 3 / 3 | 6 | 75.488 / 64.747 | 9.370 / 10.538 | 16/16 | retained |
| L/16 | 4 | 3 / 2 | 6 | 321.454 / 320.425 | unavailable after retry / unavailable after retry | 32/32 | restarted/resized |
| L/16 | 8 | 3 / 2 | 6 | 328.291 / 323.032 | unavailable after retry / unavailable after retry | 32/32 | restarted/resized |
| L/16 | max | 3 / 1 | 6 | 267.658 / 536.731 | unavailable after retry / unavailable after retry | 32/32 | restarted/resized |
| M/8 | 8, expert cap | 8 / 8 | 2.5 | 25.910 / 18.791 | 7.665 / 9.634 | 16/16 | retained |
| M/16 | 8, expert cap | 8 / 7 | 2.5 | 38.133 / 35.400 | 12.006 / 11.035 | 32/32 | restarted/resized |

The completed production-pool matrix above has **336/336** stable payload matches. The expert cap is an explicit benchmark override, not a new automatic cap: it enabled a real eight-worker control within the aggregate budget, with no memory-recovery retries. On this eight-view set four workers were faster; more capacity is not a universal latency optimization. Automatic caps remain 3/6 GiB for M/L.

An earlier requested-eight M/16 cold run hit the 120 s startup/handshake deadline after 15 replies. The old exception path discarded those sibling results; that failed attempt is not a completed timing or equivalence run. A stale earlier JSON was preserved as `M-pressure/stale-original-pool-16-8.json`, not used as the failed case's evidence. The table uses the subsequent complete final-source replay when present. Production now quarantines just the unavailable view when matching siblings worked, retains the all-unavailable fallback/refusal contract, and recovers on the next generation. A real blocked-startup licensed regression covers both result survival and next-generation byte equality.

## Matched scheduler ablation

Same compute build, four 3 GiB workers, seven advertised managed processors each. Both configurations retain processes/scenes for pass 1. Original round-robin uses the existing direct-client benchmark; queue/history uses the production pool. All 96 view payloads across these pairs match the local references.

| M set | Round-robin cold / retained s | Queue/history cold / retained s | Retained reduction |
|---|---|---|---:|
| 8 views | 29.095 / 20.368 | 25.259 / 16.015 | 21.4% |
| 16 views | 52.873 / 46.555 | 49.155 / 42.675 | 8.3% |

The eight-view 21% retained improvement supports keeping the scheduler. The 16-view 8% difference is near the earlier study's repeat noise; it is not a separate strong speed claim. These are single pairs on a shared machine.

The chart is `profile-and-scheduling.png` in the archived evidence. Original/candidate 16-view local process-private peaks (including encoding/hash work, sampled at 25 ms):

| Scene | Original GiB | Candidate GiB |
|---|---:|---:|
| M | 1.535 | 1.536 |
| L | 4.582 | 4.092 |

All six independent PERF F comparisons pass (M and L elevation, SW axonometric and long section), zero missed same-class length in either direction. Full local and worker hashes cover the metadata/order/quality that this geometric comparator does not.

## Campus: verified controls and blocked full matrix

The supplied 552,924,158-byte campus has **2,983 sources, 11,400 Brep faces and 14,024,704 mesh triangles**, predominantly terrain; it is not the separate 50,000-Brep fixture. Its one-view estimate is 3.888 GiB, selecting a **13 GiB** automatic worker cap. Capture/encoding took 14.105/16.729 s in the initial scene-only export.

**The requested full 8-/16-view campus scaling matrix is not completed or certified.** Four 13 GiB caps require 52 GiB, while this machine's physical-budget ceiling is about 47.74 GiB; eight require 104 GiB. They cannot be admitted under this policy on this hardware even before current commit pressure. A concurrent worker attempt also correctly refused when current commit headroom could not fit one cap. No heavy in-process fallback or unrelated-process termination was used.

An owned in-process North reference remained in Rhino's serial `HiddenLineDrawing.Compute` at a stack sample. It was stopped after **1,792.274 s**, with **2,521.984 CPU s** and **9.424 GiB process-private bytes at stop**, to release memory for contained-worker controls. This is an incomplete measurement, not a solve wall or a hash result. Its PID/start/path were checked before terminating only that owned benchmark tree. The owner's Rhino was untouched. Completing the full matrix needs appropriate RAM/commit headroom and a separately budgeted investigation of these native elevation tails; the planar face change does not accelerate that opaque native call.

The pre-schema-3 transport-only control succeeded: **788,136,298 raw bytes**, **532,500,403 compressed bytes**, **23.203 s** for startup/handshake/transfer/restore, **10.352 GiB** worker peak commit. A separate repeat encoding took 12.103 s. This real frame exceeds the old 256 MiB bound; no drawing solve is claimed by this control.

The separately measured Top reference took **190.715 s** locally. Worker passes: 217.533 s / 170.614 s, job-peak upper bounds 12.076 GiB / 12.076 GiB. All 2 Top payloads match `CEF62AEFB1DB761C6DA28004659A44610F329BD5CAC008097C226292890BF708`. This one-view control is not the missing full campus matrix.

The final schema-3 Top replay also matches the same local hash: 209.469 s, 11.482 GiB job peak; sampled private peaks 11.481 GiB worker roots and 13.653 GiB aligned with the coordinator. Native archive restoration is checked separately.

The first Top subset control was invalidated by a harness copy: Python JSON parsing changed signed-zero inputs. All geometry archives were equal, but the frame's zero signs differed. The corrected control preserves original request bytes and changes only the root ViewIndex field; its strict hashes pass without output normalization. The invalid control is retained separately, not counted as a product mismatch.

## Rejected managed-heap prototype

A third prototype assigned half of each worker cap to the managed heap, using the hexadecimal environment setting described in [.NET GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector). Both runtimes reported the intended limit and passed all 222 licensed tests, but L/16 with one 6 GiB worker still required isolated retries for NE and SE. It took 611.567 s versus the earlier 536.731 s one-worker pass with one recovery. All 16 result hashes matched. Sampled worker-root private peak was 6.217 GiB (including the larger isolated retry). The change was rejected and completely removed from production; GC settings remain inherited/default. Source/binary snapshots and logs are retained as gc-prototype-* and gc-L.

The benchmark now samples worker-root, coordinator and combined private bytes at 25 ms across worker retirement. Earlier L/16 job-peak counters were lost when the original workers and isolated retries were disposed: zeros in those raw records are **not zero memory measurements**. The table labels these unavailable peaks. All six L/16 matrix passes needed at least one larger-cap recovery; successful payload matches include those recoveries. The final default-policy replay below supplies a measured sampled peak. The earlier pre-schema-3 final-source one-worker replay took 549.583 s with one NE recovery; its sampled worker/coordinator/aligned peaks were 6.160/7.774/13.934 GiB (`final-L-default`). These scaling runs used the pre-schema-3 envelope, except the explicitly labelled final M/16 requested-eight replay; embedded fingerprints identify each build.

## Raw-byte envelope prototype: retained correctness fix and measured cost

candidate-M: legacy encode/decode 0.041/0.014 s, 1,081,169 bytes; raw encode/decode 0.008/0.003 s, 420,212 bytes. Exact raw payload SHA-256 `7D6860864EA71A1F9EA2DE7626EF624E7BF7A48F32C8420DA6047A33EEA7F1EA`.

candidate-L: legacy encode/decode 0.104/0.042 s, 4,092,269 bytes; raw encode/decode 0.026/0.012 s, 1,542,180 bytes. Exact raw payload SHA-256 `4C7896362CB0C963747C5C9681710399A7FFAB1299EACB137886E54A4D0025EC`.

campus: legacy encode/decode 12.426/3.029 s, 532,500,403 bytes; raw encode/decode 8.972/1.690 s, 412,350,629 bytes. Exact raw payload SHA-256 `99EADCED91F322F95506135B65D0253F848086D84E99297BC6ABE68BFA1FD933`.

objects-50000: legacy streaming JSON decode failed with `OutOfMemoryException: Array dimensions exceeded supported range`; raw encode/decode 1.523/0.717 s, 80,143,729 bytes. Exact raw payload SHA-256 `626430E450C1D1AC0FD796243C4D11113C37CD7AC765A6842F73D5A649D1F068`.

The 50k legacy worker failed at just 2.486 GiB job peak under a 12 GiB cap, with no memory-limit notification. The independent pure managed replay reproduces the giant-token buffer-growth exception, separating it from native allocation or physical-memory exhaustion. Schema 3 uses gzip with a short operation header and raw payload bytes, checked against expanded/array limits before allocation. It removes this hard serialization boundary and base64 expansion, without changing scene/result JSON. M/L retained-scene solves already avoid scene shipping; this improvement matters for first load and changed scenes. These are one-pair transport microbenchmarks, not new end-to-end scaling claims.

## Final verification and reproduction

Final-source M: 16/16 hashes match main and candidate local references; native restoration is recorded separately.

Final-source L: 16/16 hashes match main and candidate local references; native restoration is recorded separately.

The final schema-3 L/16 replay requested one automatic 6 GiB worker and took 495.390 s. Isolated memory retries: []. Sampled private peaks (25 ms): worker roots 5.483 GiB, coordinator 6.004 GiB, aligned combined 10.625 GiB. The sampler includes any recovery workers and is separate from job commit counters.

The final schema-3 release source therefore rechecks all **32/32 unique M/L view payloads** after the lifecycle and transport fixes. The performance algorithm and scene/result JSON were unchanged by the transport fix.

Actual 50k-source admission/transport control: 50,000 sources, cap 12 GiB, raw 993,099,611 bytes, compressed 80,143,729 bytes, 13.499 s, worker job peak 4.359 GiB. The scene was accepted by the worker; no drawing completion is claimed by this transport-only control.

A preceding pre-schema-3 three-worker L replay encountered a real RhinoCore startup COM E_FAIL for North; the fixed pool preserved all 15 successful matching siblings and returned an explicit quarantined failed view. That partial run is retained as `verified-L`, not counted as a complete equivalence result. The successful replay above verifies all 16 views independently.

Debug and Release builds passed with zero warnings/errors. `Test-WalkingSkeleton.ps1` passed **316 Core, 56 UI-model and 222 licensed Rhino tests on each of .NET 8 and .NET 10**. The strengthened runtime-mismatch test requires the fingerprint rejection rather than any startup failure. Both-runtime Core/UI/licensed tests, forced hook and fresh trial Yak logs are archived as `artifacts-perfg-*.log`. Final fingerprints and trial SHA are in `verification.json` in the evidence. The Yak is a dirty trial only; no installation/publication/commit/push was performed. Installed-Yak interaction remains outside this task.

Reproduce with `tools/worker-bench` (see its README): export 16 views, run `pool` for requested 4/8/0 taking 8/16, then `workers` at matched counts for the round-robin control. Keep count/cap environment overrides absent; the explicit 2560 MiB cap controls are labelled above. `kernel` runs the pure Core preparation pairs. `dump` plus `tools/perf-f/compare.py --step-mult 200` runs the independent checks. `load` isolates scene transport; `WORKER_BENCH_REFERENCE_VIEWS=4` computes only the explicitly labelled Top control. `restore` rechecks native archive reconstruction after workers exit.

Evidence is archived outside Git under `~/.t3/m2d-plus-models/evidence/perf-g-20261115-7dc4037d`, including raw payloads, baseline binary snapshots, source diff/snapshot, logs, fingerprints, per-view hashes and a SHA-256 manifest. Generated models were read only from the canonical external model folder.
