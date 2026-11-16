# Cut only — beta.4 owner decision, 2026-11-16

`cutonly = on | off` (also `cut only`, default off) is available in Settings,
`m2d.cutonly` view-object user text and the **Cut only** menu toggle beside Far clip.
It follows ordinary precedence and is a visibility cache key.

The section route returns after certificates, validation and region construction,
before projection registration. Its existing `finally` still applies cut uncertainty.
Presentation still crops, restyles and merges sections. Cut outlines/regions, Hatch,
Frame and Info survive; only cut-related Unresolved survives among line pieces.
Uncut directions and frames with no cut inside their crop give Frame/Info and the
remark `Cut only: this view cuts nothing`. Info reports
`Cut only: projection beyond the cut skipped`; the drawing step is `sections`.

Worker schema/codec is unchanged: the setting uses its existing boolean transport,
empty pieces use the ordinary drawing payload, and diagnostics carry the empty-view
remark. The projection boundary is the integration hook for R2-1: emit refused-cut
markers above the early return. No section algorithm, contact-edge, analytic-engine
or bake-layout implementation is changed.

## Timings

Seconds, exact engine, hidden on. Local cells time compute plus presentation over
one retained scene, bypassing the drawing-result cache. Ten-plan cells sum ten
sequential per-view wall times; they are not production drawing-set latency.
Capture, archive/hash/file writes and Rhino startup are outside local timers.
Section validation verdicts and prepared scene data remain reusable across modes.
Order is normal, cut-only, off again for each local view. These are n=1 observations;
some local runs overlap the licensed suite, and machine activity is uncontrolled.

| Fixture / route | Normal first | Cut only | Off again | Cut/Hatch regions |
|---|---:|---:|---:|---:|
| Offset v2 origin, 10 plans, local | 50.305 | 19.366 | 49.991 | 1,525 |
| Offset v2 100 m, 10 plans, local | 54.820 | 19.931 | 53.273 | 1,525 |
| perf-M blocks, plan 1.2 m, local | 7.204 | 4.589 | 6.430 | 364 |
| perf-M blocks, long section, local | 5.931 | 2.845 | 6.231 | 122 |
| perf-L blocks, long section, local | 32.948 | 17.856 | 30.421 | 1,183 |
| Offset v2 origin, 10 plans, production pool | 21.473 cold | 4.888 warm | 11.248 warm | 1,525 |

The pool uses default admission/count, six workers with 2 GiB caps and a 4 GiB host
reply reservation throughout all three passes. Its timer includes startup/handshake
on the cold pass, scene transfer, computation and reply restoration. Subsequent
passes retain those workers. The warm normal comparison is therefore **11.248 s
versus 4.888 s**, about **2.30×** faster. Pool measurement ran after the licensed
suite exited. Native threading stays off, as exact mode requires.

All **23 completed local views** and **10 worker views** have byte-identical
Cut/Hatch region payloads between normal and cut-only. Comparison includes curve
and hatch archives, loop order/topology, provenance, style, source attribution and
region state; there is no rounding or geometric-equivalence substitute. Every
off-again drawing matches its original normal stable payload/hash. Stable drawing
hashes exclude only diagnostics and wall/phase timings, following WorkerCodec's
existing convention. Every cut-only cell has zero native calls/kernel edges and
no native solve, kernel solve or ray-verification phase.

An **additional 1 km local run is incomplete and excluded**: plans 0–6 pass all
comparisons; plan 7 fails in normal-mode presentation with
`SectionFailure: Provenance interval extraction failed` in `EdgeProvenance.Split`
via `SectionRegionEngine.Restyle`. Those section/presentation algorithms are
unchanged in this diff. The log is retained for the section reliability work.
Early perf attempts resolving same-named named views instead of clipping planes
are also excluded; the final harness uses explicit planes matching ScaleBench.

## Verification

- Debug and Release solution builds: **0 warnings, 0 errors**. Final benchmark
  build also passes with zero warnings/errors.
- Forced Git for Windows hook passes: **373 Core and 73 GH UI-model tests**.
- Unfiltered `tools/Test-WalkingSkeleton.ps1` passes on **.NET 8.0.34 and .NET
  10.0.15**, Rhino **8.36.26296.17001**: 373 Core, 73 UI-model, 283 licensed Rhino
  tests on each runtime. Only the opt-in capture profiler is skipped.
- A final focused WalkingSkeleton run passes **nine cut-only licensed cases on
  each runtime**, including the subsequently added crop-miss case and second
  (uncut) worker view. Core and UI suites also pass in that final run.
- Coverage: aliases/default/precedence/cache key; menu persistence/input lock;
  exact/native section equality and zero projection; crop-miss/elevation/axo empty
  remarks; retained cut uncertainty; actual DrawingCache off/on/off invalidation
  and fresh-normal equality; worker/local strict equality and transported remarks.
- `git diff --check` passes. No commit, push, installation or publishing.

## Reproduction and evidence

Models and evidence stay outside Git in `~/.t3/m2d-plus-models/`.
Evidence root: `evidence/cut-only-20261116/`. Accepted measurement folders are
`final-origin`, `offset-100m`, `perf-M-sections`, `perf-L-sections`, `pool-origin`.
Each has model/engine/runtime fingerprints, timings, hashes and drawing archives.
`full-tests.log`, `focused-final.log`, `hook-final.log`, `release-final.log`, and
the two saved TRX directories retain verification; `offset-1km.log` retains the
excluded normal-presentation failure. `source/` and `source.diff` bind the final diff.

Build `tools/worker-bench/M2DPlus.WorkerBench.csproj -c Release -p:UseAppHost=false`,
then run its DLL with `cutonly <resolver> <model> <new-output-folder> <mode>`.
Modes: `building` (perf-M plan/long), `long` (perf-L), `offset:0`, `offset:100000`,
or `pool:0` for the production ten-plan batch. Build the Release solution first
for matching worker binaries. Use the resolver documented in the licensed test
README; keep at most two owned non-worker Rhino hosts and never touch the owner's
interactive Rhino. The harness refuses existing evidence directories.
