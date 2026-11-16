# Opt-in fast native engine

Owner decision 2026-11-16. Branch `feat/engine-fast`, based on PR #38 at
`85fe459` because that PR was not yet in `origin/main` when work started.

## Behavior

`engine = exact | fast` defaults to **exact**. Settings input, `m2d.engine`
view-object user text and *Engine ▸ Exact · Fast (Make2D-like)* use the ordinary
precedence. The setting is per view, persisted with menu defaults and carried
through the worker settings whitelist. No inputs, outputs or GUIDs change.

Only native HLD options change: fast uses `multipleThreads=true` and follows the
requested hidden flag. Exact keeps the existing serial, hidden-on native policy.
Tolerance, tangents/seams, frames, registrations, sections/certificates, native
admission and verifier budgets do not change. A shared `NativeHldOptions` resolves
the policy at all three native call sites: composition, fallback and route C.

Every kernel still receives the original hidden-on solve contract. The requested
flag survives in `SettingEntry.AuthoredValue`, exposed as `RequestedHidden`; only
fast native HLD reads it. This keeps planar and upcoming analytic kernels
independent of the native shortcut and preserves hidden evidence for min-visible.
Presentation drops hidden lines afterward. Fast native can return some Hidden
fragments even with IncludeHiddenCurves=false; those residual fragments are still
audited. Info says false-Hidden checks are skipped for **uncomputed** hidden curves.

Engine always enters `EffectiveSettings.VisibilityKey()`. Requested hidden enters
that key only in fast mode. Exact hidden remains presentation; min-visible,
placement and styling remain presentation in both modes. Switching modes always
invalidates visibility, even when the view ends up entirely planar.

Actual native use records `FastNativeSources`, copied through presentation and the
worker codec. N counts distinct source identities submitted to native, including
planar candidates needing curved native occlusion; planar/analytic-only views
remain at zero. Info uses the requested disclosure:

```text
Engine: fast — Make2D-like shortcuts on N curved/freeform sources; their lines are not certified and may differ slightly between runs.
```

This produces a component **remark**, not a warning. No fast remark when native
was unused. The footer's second row says `fast` during native phases; checking
and other phases keep their own labels. Native scheduling remains serial per
process; multithreading is inside the selected native call.

Fast output has no repeat/worker byte-identity promise. Existing worker hash tests
explicitly select exact; a separate fast-worker test checks native flags, progress,
Info, remark state and lossless transport of a single result, without comparing
independent threaded solves. No curve or unresolved certificate is relaxed.

## Measurements

Rhino 8.36.26296.17001, .NET 8.0.34, North (`Back`), millimetres and tolerance
0.01 mm. Fresh visibility is computed in each cell, with owned scene/preparation
reuse. M2D+ wall includes compute, tolerance census and presentation; it excludes
capture, startup, GH publication and evidence serialization. The explicit isolated
in-process benchmark uses a 12 GiB scheduler allowance; product dispatch/memory
policy is unchanged. All are n=1 on a shared workstation, with at most two owned
Rhino benchmark processes. Exact off/on still compute the same hidden-on native
policy; their timing difference reflects ordering, caches and machine contention.

| Scene / hidden | Exact wall | Fast wall | Actual Make2D wall | Exact / fast native phase | Exact / fast ray audit |
|---|---:|---:|---:|---:|---:|
| Curved M / off | 329.071 s | 48.570 s | 42.032 s | 309.822 / 44.493 s | 1.874 / 1.928 s |
| Curved M / on | 286.731 s | 166.608 s | 149.984 s | 265.841 / 143.863 s | 2.011 / 2.761 s |
| Curved steel / off | 1.911 s | 1.804 s | 0.759 s | 0.509 / 0.367 s | 1.309 / 1.417 s |
| Curved steel / on | 1.959 s | 1.491 s | 1.101 s | 0.589 / 0.468 s | 1.358 / 1.010 s |

Fast reduces the heavy M native cost substantially. It does **not** beat Make2D
on whole-view wall in these runs. The unchanged audit does different work over
the changed candidate set and dominates steel. In the earlier steel matrix,
hidden-off fast was slower than exact (1.773 versus 1.488 s); final confirmation
is above. Fast is opt-in, not a universal speed guarantee.

Curved M was measured before the final hidden-contract refactor, which changes
settings plumbing rather than native inputs, options, geometry or audit budgets.
Steel and the full planar matrix were repeated on the final implementation.
All 32 final planar hashes also match the earlier implementation, including
exact/fast, both hidden flags and all eight views. Per-folder fingerprints are
preserved; these observations do not imply cross-run equality for fast native.

The Make2D numbers are new runs of the actual installed `_-Make2D` command, not
an HLD proxy, in an owned hidden Rhino.Inside UI host with the matching parallel
viewport. The ordinary NoWindow/headless document cannot execute RunScript.
Options: View layout, ByOutputLayers, requested hidden on/off, tangents, scene
silhouette, clipping intersections, rectangle and grouping off. Full scripts,
command history, output counts/archives and wall times are retained.

### M building invariance

The supplied `perf-M-blocks` model is **mixed**, containing CHS members, curved fins
and a SubD canopy. It calls native HLD in all eight views. Its exact/fast hashes
are not an appropriate planar-invariance gate (they also include actual native
threading and fast-source state). It was nevertheless measured unchanged, with
both hidden settings, in `building-M-mixed`; all 32 rows and phases are retained.

A planar-only derivative removes entire top-level sources with any leaf rejected
by complete planar extraction: 210 removed, 4,790 retained. It preserves the
remaining blocks, geometry, identities, transforms, units and tolerance. The
source file is untouched. The derivative and its generation manifest are at
`~/.t3/m2d-plus-models/mm/perf-M-blocks-planar.3dm[.generation.json]`.

North, South, East, West, Top, SW, long section and plan at 1.2 m, each with hidden
off/on: **16/16 exact/fast output hashes match** and every run has zero native
calls, zero fast-source count and serial metadata. Hashes normalize authored
engine/settings metadata, retaining every geometry, class, provenance, quality
and routing field. Input metadata and Info naturally differ by engine selection.
The abandoned first `building-M` pilot is retained but excluded from summaries.

## Reproduction and evidence

Stable root: `~/.t3/m2d-plus-models/evidence/engine-fast-20261116/`.
`measurements.md` contains the full original/planar building tables; `phases.csv`
contains all measured phases. Each `engine.json` records model hash, engine/CLR/
Rhino fingerprints, source count, parameters, timings, Info, output hash and
classification counts. Exact detached drawing archives remain outside Git.

```powershell
dotnet build tools/worker-bench/M2DPlus.WorkerBench.csproj -c Release -o artifacts/engine-fast-bench
# Pass the existing resolver, a model, and a NEW evidence folder.
./artifacts/engine-fast-bench/M2DPlus.WorkerBench.exe engine $resolver $model $output
# All eight building views, including sections:
./artifacts/engine-fast-bench/M2DPlus.WorkerBench.exe engine $resolver $building $output building
# Create the documented planar-only derivative, refusing overwrite:
./artifacts/engine-fast-bench/M2DPlus.WorkerBench.exe engine $resolver $building $modelsFolder generate-planar
./artifacts/engine-fast-bench/M2DPlus.WorkerBench.exe engine $resolver $planar $output planar
python tools/worker-bench/engine-fast-report.py $evidenceRoot
```

No commit, push, installation, publication or cloud CI. Only owned Rhino hosts
were used. The permitted process-cap exception applies only to the full worker
test suite. Fable's analytic algorithm/hook is not implemented or modified here;
the overlap in `ComposedVisibility` is confined to native option construction and
the native Compute call.

## Verification

Debug/Release solution builds, standalone Release benchmark build and the forced
local hook pass with zero compiler warnings/errors. The **unfiltered**
`tools/Test-WalkingSkeleton.ps1` passes on both .NET 8.0.34 and .NET 10.0.15:
367 Core, 72 UI-model and **266 licensed Rhino tests per runtime**, including all
worker equality, crash, recovery, memory, cancellation and presentation cases.
The only default skip is the separate opt-in `CaptureProfileTests.ProfileHostCapture`
performance experiment (`M2D_PROFILE_CAPTURE=1`); no worker tests are excluded.

New regressions cover default/precedence/user text, invalid-value fallback,
engine/conditional-hidden cache identity and actual cache misses, hidden-on
kernel normalization with separately requested native flags, settings whitelist
round trips, mixed/native-fallback/route-C parameters, unchanged audit execution,
Info and actual GH remark publication, the real two-line footer, planar geometry/
class/quality byte identity including min-visible, and fast worker transport and
progress without a cross-run byte promise. A final targeted two-runtime run also
checks that every displayed non-engine setting is equal before hash normalization.

Final logs: `artifacts-fast-full-acceptance-final.log`,
`artifacts-fast-hash-metadata-guard.log`, `artifacts-fast-release-final.log`,
`artifacts-fast-hook-final.log`. Timings, source diff, log copies and SHA-256
evidence manifest are retained under the stable evidence root above.
