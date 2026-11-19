# Worker benchmark

Build the solution and this project in Debug or Release; use an existing compatible
Rhino.Inside resolver. All Rhino processes are owned by the harness. Models/evidence
stay outside git under `~/.t3/m2d-plus-models`; never overwrite existing evidence.

Current commands (see Program.cs for the complete replay/transport arguments):

- `engine <resolver> <model> <new-output-folder>` profiles threaded HLD, hidden on/off.
- `hld <resolver> <model> <new-output-folder> <variant>` retains raw native controls;
  native audit is removed and native compute remains threaded.
- `cutonly <resolver> <model> <new-output-folder> [building|long|offset:<mm>|pool:<mm>]`
  checks independently certified Cut/Hatch and zero HLD calls for cut-only. Fresh
  normal-result hashes are observations, not equality gates.
- `export`, `workers`, `pool`, `dump`, `restore`, `identity`, `load` and memory probes
  retain owned scene/request/result archives, strict handshake and resource checks.
  Native restoration of one result stays a strict lossless byte gate.

Independent threaded solves may differ. Use the licensed fixture gates for classed
loci at declared tolerances, topology/area, ordered inputs and policy/fingerprints.
`engine-fast-report.py <evidence-root>` summarizes the current hidden-on/off records.
Kernel, exact/fast comparison, planar-kernel generation and verifier/dense-audit
commands are retired at `archive/exact-engine`, `0cfc171` (beta.8). Historical findings
retain their original fingerprints and do not certify the HLD-only policy.

Scope-first capture replay (all generated models and output folders stay under `M2D_MODELS`):

- `scope <resolver> <model> <new-output> <case:n[:count]|storey|warm-storey|elevation|warm-elevation|warm-tenth|tenth|ninety|full|building|section|sectionbox|terrain>`.
  `SCOPE_BENCH_REPEATS` defaults to 2, `SCOPE_BENCH_WARMUPS` to 1; `SCOPE_BENCH_INPUTS=1`
  records exact ordered HLD input hashes. Identity archives include both quarantine flags.
- `scope-conversions <resolver> <new-model.3dm>` generates 2,500 extrusions and 25 SubDs.
  Warm modes seed capture and drawing in the same SceneCache, edit a top-storey extrusion outside the box, then measure the rerun.
  Results separate UI active capture time, wall capture time, max Step time and initial enumeration.
- `scope-terrain <resolver> <new-model.3dm>` generates a small 256-tile control; never campus.
- `scope-export <resolver> <new-output>` exits its licensed exporter before
  `scope-worker <output> <matching-worker-directory>` starts one persistent worker for two views.
  The coordinator never initializes Rhino. A second batch supplies no scene bytes to verify scene reuse;
  sequential crash/cancel/deadline/stale/runtime/memory/idle checks follow after that worker exits.
  Export afresh after a build/fingerprint change.

- `scope-storeys <resolver> <new-model.3dm>` generates ten storeys with slabs, columns, facade openings,
  interiors and a Brep/Extrusion/SubD mix. `storey` and `elevation` use authored finite boxes.
- `scope-corpus <resolver> <models-root> <section-fixtures> <output>` replays all view-box descriptors
  except curved-M and the section fixtures with an explicit-depth rectangle. Use one measured run, zero warmups.
- `scope-compare <resolver> <main-output> <branch-output> <output>` runs bidirectional RTree locus sampling
  at 1.25 x tolerance, with class/Profile/SolvedClass agreement, exact cut/hatch archives and fixed frames.
  It permits engine fingerprint differences only in this research reader; native/runtime fingerprints must match.
  Drawing archives accompany each timing row, so the same comparator can check benchmark outputs.

V2 measurements include CPU time and lifetime peak commit/working set (startup and warmup
are included in the peaks). `SCOPE_BENCH_RESUME=1` skips completed result folders during
corpus recovery. The locus tool returns nonzero when any pair fails; preserve that result.
Its short-curve parameter fallback and per-case mismatch examples are documented in
`docs/research/scope-first.md`; it never widens the 1.25 x matching tolerance.

Attribution controls are research-only (internal `NativeSettings`, never parsed from product settings):
`SCOPE_BENCH_SINGLE_THREAD=1` passes `multipleThreads: false` to HLD and preserves the flag in archives.
`SCOPE_BENCH_PIN_MAIN=<main corpus root>` with `SCOPE_BENCH_OUTPUT_ROOT=<candidate corpus root>`
uses each matching main row's whole-scene bounds only for the HLD viewport camera/frustum radius.
`SCOPE_BENCH_CHEAP_RADIUS=1` instead uses the union of live drawable objects' non-tight cached
geometry bounds, without conversion or copies. Its enumeration time is `cheapBoundsMilliseconds`,
recorded outside the capture interval; it must be added when assessing this proposed extra UI work.
Defaults remain threaded HLD and survivor camera bounds. One owned Rhino process at a time for
scope-first attribution, including comparison and licensed-test commands.

`scope-radius-cost <resolver> <model> <new-output>` measures that extra drawable/bbox
walk on the host thread, with a first pass, one additional warmup and 20 measured warmed
passes. It performs no HLD solve, conversion or owned geometry copy; startup and model
loading are outside the interval. Evidence includes every sample and the resulting radius.
The first pass does not promise that Rhino's file-loaded bbox caches were empty.
