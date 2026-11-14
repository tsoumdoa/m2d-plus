# PERF B2 — native-only hill climb

Branch **hc/perf-b2-native**, base **69e4a8a7d13a02d7d91e683dedbd119159e255be**.
No product source changes, installation, commits or pushes. **No accepted speedup.**

The owner supplied completed S3 evidence on 2026-11-15 and requested modest usage.
The queued threading/culling/box-meshing sweep and remaining large baseline were
stopped. Tiling was never implemented or run here; threading was never timed here.
Neither will be restarted by `batch.py`. The remaining work became focused native
hidden-flag diagnostics. These are not accepted performance iterations.

| # | Hypothesis | Change | Gate | Primary cold / warm | Secondary | Kept? |
|---|---|---|---|---|---|---|
| 0 | Current monolithic native, threads off | Owned-process baseline and full manifest | **Incomplete**: 64/688 primary rows completed in finished workers; 4 further row files from an interrupted worker | null / null | Peak owned benchmark aggregate 10.184 GiB; XXL not attempted | Baseline evidence only |
| 1 | Hidden generation should leave Visible loci unchanged | Compare saved exact curves from all 32 completed orthogonal tier/form/view pairs | **FAIL** under the strict comparator; geometry, source and multiplicity categories recorded | null / null | No further HLD calls; observed on/off per-view time ratios 1.13–8.70, not a certified speedup | No |
| 2 | Reduce a genuine Visible mismatch | Bounded reduction of S North; independent three-object replay | **FAIL**: 17.177348994 mm missing, tolerance 0.1 mm; identical piece count and no attribution changes in standalone replay | null / null | 500 → 6 shortlisted → 3 inputs, 47 reduction trials, about 36 s | Reproducer only |
| 3 | Tighten native tolerance to resolve the missing span | Native tolerances 1e-4, 1e-5, 1e-6 on those same three objects | **FAIL**: identical 17.177348994 mm omission at all three | null / null | Small diagnostic only; changed native tolerance cannot qualify as a fixed-settings optimization | No |

Append-only machine records: [hillclimb/scores.jsonl](hillclimb/scores.jsonl).
Detailed evidence: `artifacts/native-hillclimb/` (ignored build/research artifacts).

## What was established

- The native `IncludeHiddenCurves` flag changes actual Visible loci even with
  `multipleThreads=false`. This is not repairable by sorting, reversing curves,
  joining split curves or removing duplicates.
- The decisive independent replay reads **three world-space Breps**, without blocks,
  the source model, SceneCapture, M2D+ extraction/quarantine or a section pipeline.
  Both calls return **25 visible pieces**, yet one loses the 17.177 mm span and
  also changes another approximately 0.278 mm locus. Counts alone miss the bug.
- The affected parent feature is **BrepEdge 26**, `SilhouetteType.Boundary`.
  Its projected span runs approximately from `(4, 3.100000007)` to
  `(3.999750911, 3.117174111)` in world X/Z. Two foreground boxes and a 12-face
  Structure Brep are sufficient. Native hidden-off has no Visible segment on the
  target span; native hidden-on does.
- At the sampled physical midpoint, the ray toward the North camera is exactly
  +Y. It lies about **0.21795 mm beyond** the foreground fixture box's maximum X.
  No foreground box bounds cover it. A CurveBrep check at 1e-8 tolerance reported
  no point intersections. The parameterless RayShoot routine instead returned a
  point clamped onto that box edge, off the actual ray; it must not be treated as
  an independent exact oracle here. These diagnostics support a near-boundary
  native classification problem; they do not prove the internal C++ root cause
  or certify every point of the original large drawing.
- Many SW differences also include **adapter confidence changes**, not only raw
  visibility. S invalid extraction counts change 123 → 27 and Unresolved pieces
  342 → 112; M 552 → 123 and 1657 → 629; XL 4318 → 826 and 11140 → 4328.
  The North reproducer has zero invalid extraction in both modes and the raw
  standalone replay bypasses that adapter entirely.
- Tightening native absolute tolerance does not repair this reproducer. No safe
  fast general correction was established. Keep the current default; if strict
  Visible parity is required, the safe reference remains hidden-on HLD followed
  by suppression of Hidden output, which sacrifices the hidden-off compute gain.

## Reproducer

[Input geometry](hillclimb/repros/hidden-flag-three-objects.3dm) and
[view/settings descriptor](hillclimb/repros/hidden-flag-three-objects.json) are
small, hash-pinned, standalone research artifacts. The descriptor preserves the
original full-scene camera/frustum; **do not auto-fit to the reduced geometry**.
See [replay instructions](hillclimb/repros/README.md). Independent replay passed
as a **negative regression** on Rhino 8.36.26296.17001 / .NET 8.0.34, x64.
It deliberately demonstrates a failed equivalence gate, not a corrected engine.

Raw evidence:

- `artifacts/native-hillclimb/visible-audit/`: 32 exact-curve audits and suspect curves.
- `artifacts/native-hillclimb/hidden-probe-ray/`: minimization trace and native feature records.
- `artifacts/native-hillclimb/tolerance-diagnostic/`: tolerance sweep and ray diagnostics.
- `artifacts/native-hillclimb/standalone-replay/`: independent raw-native replay and both Visible drawings.

The first marker-only target export in the reduction harness was empty; it is not
used as evidence. The target was recovered from the successfully read-back native
Visible result. The delivered three-input fixture was read back and independently
replayed. Failed bootstrap diagnostics remain in artifacts and are not scored.

## Benchmark scope and limits

The original manifest remains **688 primary rows**: orthogonal and curved S/M/L/XL,
blocks and exploded, all recursively bundled 3dm fixtures, North/SW/Top/long section,
both hidden modes. XXL adds 16 secondary rows. Unmeasured rows remain explicit;
there is no reduced-domain accepted score. Cold/warm full-set scores are **null**.
XXL was **not retested** after the owner's scope/budget reduction.

Completed orthogonal baseline native-on calls include XL blocks North 282.439 s,
SW 529.384 s, Top 251.137 s and long section 292.204 s. The current M3 Route C path
and its output handling differ from S3 raw/native-clipping timings; these are not
interchangeable baselines. All times are shared-machine measurements.

The owned executable compiles the original Rhino engine unchanged and calls its
same private per-view method. Its research-only admission bypass permits large
scenes outside the product's 512 MiB policy; it never sends heavy work into an
interactive Rhino. Worker commit was capped at 15.875 GiB, with 128 MiB reserved
and monitored for the supervisor. Peak aggregates include benchmark validation;
they are not a production six-view GH publication or shipping IPC measurement.
Cold timings were derived from measured startup/capture divided by six plus
per-view medians, with three warm trials below ten seconds. Result encoding and
receiving-copy measurements were tightened during harness validation; candidate
paired controls, rather than a cross-version baseline ratio, would be required
for an accepted speed claim. No such claim is made.

The curve scorer uses full CurveCurve overlap intervals in both directions,
class/source audit and pointwise multiplicity, not SVGs or sampled signatures.
It is conservative: 0/32 strict comparisons does **not** mean all 32 have equally
large physical errors. Tolerance-band/numeric certificate failures are retained;
the 17 mm raw-native counterexample is the decisive substantive result.

## Retired theories and next work

Per the owner's S3 evidence, drop native multithreading and tiling. The culling
prototype was not benchmarked here and cannot contradict S3's certification-cost
loss. General inner occluder proxies were not implemented: an inner coverage
subset only prevents extra hiding; it can still reveal extra Visible loci.
Strict box meshing remains an unactivated laboratory mode, not a measured result.

Next: send the three-object reproduction to McNeel through the owner; investigate
native feature classification/geometry tolerances without guessing a correction.
For performance, prioritize S3's conservative early section bounds and exact
local-plane trim/cap reuse with complete region/provenance gates. Do not multiply
separate stage ratios or claim a new end-to-end gain from inherited results.

## Verification

Debug and Release solution builds: 0 warnings/errors. Existing licensed acceptance
passed serially on .NET 8 and .NET 10: **186 Core + 40 GH-model + 136 Rhino/GH tests
per runtime**. The research project builds separately with warnings as errors.
Final forced hook and default solution test results are recorded in artifacts.
Only owned controllers/worker jobs were stopped; no unrelated Rhino was touched.
