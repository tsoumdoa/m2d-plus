# Worker benchmark

Opt-in engine comparison: `engine <resolver> <model> <new-output-folder>` measures
exact/fast with hidden off/on, production compute and presentation, verifier
timings and actual native options. Add `building` for eight building views, or
`planar` for that matrix with a strict zero-native/hash-identity gate. Authored
settings metadata is normalized for the output hash; every geometry/class/quality
and routing field remains. `generate-planar` writes a recorded planar-only
derivative under the supplied stable model folder, refusing overwrite. The
original M-blocks fixture is mixed and cannot serve as a planar-only control.
`engine-fast-report.py <evidence-root>` generates all timing/hash tables and phase
CSV. See `docs/research/engine-fast.md`. This explicit isolated in-process harness
uses a 12 GiB scheduler allowance without changing production dispatch policy.

Curved native HLD investigation: see `docs/research/native-curved-hld.md`.
`hld <resolver> <model.3dm> <output-folder> <variant[,variant...]>` records a
parallel North view with raw HLD parameters, progress, counts, exact curve
archives and sampled loci. `command-owned` runs the actual scripted Make2D
command in an isolated hidden UI host; `command-headless` records why the
NoWindow/headless route fails. `HLD_COMMAND_SCRIPT` overrides the complete
command script. `export-command` exports an already archived command result,
undoing only its known viewport placement. `audit` compares alternating old/new
independent ray audits on identical drawing bytes. No flag changes production
defaults. Keep at most two owned Rhino hosts running. `HLD_SKIP_TESSELLATION=1`
retains native timings/counts and exact curves without expensive diagnostic
polyline export; such rows cannot enter sampled comparisons. Compute timing is
flushed before extraction so an export failure cannot erase it.

`hld-report.py <evidence-root> --scene mm-steel` checks both directions by class,
using NumPy and Shapely 2. For `mm-curved-M`, add `--skip-vertices` to use arc
samples without every polyline vertex. These are sampled regressions, not
certificates. `--tables-only` refreshes `measurements.md` and `phases.csv` without
repeating locus queries. A tenfold tolerance, mesh or compound-Brep probe is an
explicit geometry/output experiment, never an equivalence claim.

Unit/offset investigation: see `docs/research/units-and-solve-latency.md`.
`scale <resolver> <model.3dm> <new-output-folder> [indices=5]` records capture,
production/local/native timings, complete Info and all eight worker requests.
Use `production:all` for eight production views, `0,1,2,3,5` for paired
elevation/axo controls, `raw:0,1,2,3` for a direct serial HLD proxy, or
`threaded:0` for an explicitly labelled threaded proxy. `generate` creates a
millimetre model (scale 1,000; tolerance .01 mm) under the chosen stable model
folder, refusing to overwrite; `generate:10000000` also translates its top-level
objects. `capture6k` supplements the open model to 6,000 drawable objects in
memory and measures capture without a solve. `SCALE_BENCH_TOLERANCE` is an
explicit benchmark-only override in document units. Preserve the generated
`.generation.json` beside offset models so the plan follows their translation.
`scale-report.py [evidence-root]` writes measurement tables, phase CSV and a
comparison chart outside Git. Run local exports before workers; use `pool ... 1 2`
for one cold two-worker set with no RhinoCore in the coordinator. A failed hash
is recorded and returns nonzero after the complete batch has been recorded.

`Run.ps1 -Models <read-only model folder> -Resolver <existing Rhino.Inside resolver> -Output <new folder>` builds Release and records the small single-view startup control and eight-view M/L in-process/1/2/4-worker matrix, fresh-process cold then retained-scene warm. The exporter terminates before the coordinator starts workers: at most four owned RhinoCore processes, including the in-process baseline host. No process enumeration, attachment or unrelated termination is used.

The benchmark uses production native/composed/section compute, native threading off, hidden lines on and document tolerance. Its local reference bypasses product admission using the machine memory budget; this does not change the interactive host's 512 MiB threshold. Fixed-count experiments use the automatic scene cap and the same managed processor allowance as production, and refuse a reservation above the memory budget. The named long section and a z=1.2 plan join North/South/East/West/Top/SW. Capture/scene encoding are separate observations. Worker wall includes startup, handshake, shipping/preparation, solve, compression and reply transfer; warm passes retain scenes/processes when sizing stays unchanged. Hash verification is outside batch wall.

Every full result is SHA-256 compared byte for byte after excluding timings and diagnostic prose only. Geometry archives, source metadata, classification, depth geometry, section provenance, hatches, settings and quality are included; nothing is rounded or resorted by the comparator. Missing/mismatched views are recorded per view. `baseline.json`, `workers-{views}-{count}.json`, scene/request archives and expected result hashes form the evidence. Stable baseline result payloads are also retained to diagnose mismatches. Keep the complete folder outside Git; hash it in a manifest before sharing measurement claims.

Baseline memory is sampled process-private bytes every 25 ms. Worker memory includes the sum of OS Job Object peak commit counters, a conservative upper bound rather than aligned aggregate peak. The production-pool harness also samples worker-root, coordinator and aligned combined private bytes every 25 ms, including workers retired during recovery. Earlier records with a zero job peak after all workers were retired have an unavailable peak, not zero memory usage. External application/agent contention and OS caches are uncontrolled; report these runs as n=1 and do not infer a universal performance model.


Production path: `pool <folder> <worker directory> <passes> [requested-count=0] [take-views=0] [cap-MiB=0]`. Zero count uses the CPU default; zero cap uses automatic sizing. The coordinator never initializes RhinoCore. `pool-{views}-{requested-count}[-cap{MiB}].json` records chosen count/cap/budget, hashes, phases, process IDs, recovery flags and sum of job peaks. Count and cap environment overrides must be absent. Nonzero cap is an explicitly labelled expert experiment, still constrained by the aggregate budget. Failed views are recorded and return nonzero status. A scene-only export establishes transport/admission without an unmeasured local hash claim. `workers <folder> <worker directory> <count> [take-views]` is the fixed round-robin comparison, writing `workers-{views}-{count}.json`.

PERF G: set `WORKER_BENCH_VIEWS=16` before `export` or `scene` to append NE/NW/SE/Bottom, transverse and reverse sections, and two plans at quarter/three-quarter height. Use `take-views=8` or `16` against the same archive. Run requested counts 4, 8 and 0 serially; the production memory policy may admit fewer. More than four Rhino processes are permitted only for this short scaling experiment. `identity <worker directory> <folder/identity.json>` refreshes exact engine evidence, starting and closing one contained worker before the batch. Reuse existing archives only when detached scene/request/result encodings are unchanged; refreshing identity cannot make an old worker compatible with a new host. Schema 3 changes only the live outer envelope, preserving these archives.

`kernel <output.json>` compares synthetic triangle/quad preparation with result hashes (no RhinoCore). `dump <resolver> <folder> <comma-separated-indices>` restores stable local result archives and emits the ordinary PERF F drawing dumps for `tools/perf-f/compare.py`. `restore <resolver> <folder>` checks every returned archive against its stable hash in a separate RhinoCore after all workers have exited. Keep every baseline/candidate engine binary, timing, model hash and comparison report with the evidence.

`load <folder> <worker directory>` isolates startup/scene shipping/restore without computing a drawing, using the automatic scene cap and real memory admission. It records the actual compressed frame size and job peak; the separately measured repeat compression is outside the round-trip timer. `WORKER_BENCH_REFERENCE_VIEWS=4` limits local reference compute to the explicitly labelled Top view while keeping all requests available. A subset reference is never a complete-set correctness claim.

`envelope <folder>` compares legacy base64 JSON/gzip against schema-3 raw-byte/gzip envelopes without RhinoCore, recording encoding/decoding times, compressed size and exact payload hashes. A legacy failure is evidence, not a successful timing. Scene and result JSON are unchanged across this envelope schema change; refreshing identity is safe only with that explicit compatibility evidence. `load` also records the cap, job peak and memory-limit notification if scene transfer fails.

When making a one-view replay folder, preserve the original request bytes and change only the root `ViewIndex` integer. Parsing/reserializing with another JSON implementation can change signed zeros (`-0` to `0`), changing the input bits and invalidating a strict hash control. Keep the original scene and expected result hash. Pass 1 is retained only when the recorded PID arrays are nonempty and unchanged and neither pass recovered a view; a memory-driven resize may restart every process.
