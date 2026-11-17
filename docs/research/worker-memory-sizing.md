# Worker memory sizing (separate uncommitted change)

The old input-only cap gave curved-M 2 GiB and a fixed 4 GiB retry, below the owner's
reported 4.48/4.55 GiB .NET 8/10 native peaks. Curved-M and campus were **not run** here.

## Small-model calibration and model

Three unbounded SW fixtures, exact production route, serial native HLD, default audit,
one owned Rhino process at a time, holding `Local\M2DPlusLocalModelSuiteRhino`.
Initial measurements used an explicit 8 GiB cap to avoid censoring the fit. Job Object
peak commit includes startup, retained scene, solve and serialized replies. Each worker
archive was compared byte-for-byte with its fresh in-process reference.

| Fixture | Curved faces | Edges in curved Breps | Peak .NET 8 / 10 (GiB) | Automatic cap |
|---|---:|---:|---:|---:|
| curved-S blocks (500 sources) | 750 | 2,632 | 0.729 / 0.743 | 2 GiB |
| diagrid-shell (25 sources) | 33 | 72 | 0.569 / 0.575 | 2 GiB |
| curved-steel (8 sources) | 32 | 166 | 0.557 / 0.557 | 2 GiB |
| curved-M prediction (10 × S) | 7,500 | 26,320 | not run | **6 GiB** |

The small-feature peak is about 0.57 GiB; the S increment gives roughly 55–61 KiB per
additional curved-face/edge unit. Use **128 KiB per unit**, over twice that slope,
plus the existing 1 GiB fixed allowance and three times the original preparation/archive
estimate. Thus `cap = ceilGiB(max(2 GiB, 3*P(1) + H + 1 GiB))`,
`H = 128 KiB * (curved faces + edges in curved Breps)`.
Tenfold S costs predict 5.956 GiB before rounding, hence 6 GiB for M. This is an
empirical extrapolation, not an allocation guarantee or a measured M success.
Topology is counted once per owned definition and charged per instance. Near-planar
faces may conservatively count as curved. Interactive admission also includes `views*H`,
preventing heavy fallback into the host when the input archive itself is small.

## Recovery and reclamation

Capture failed-job peak before disposing the job. Retry once alone at the peak-derived
target `ceilGiB(max(old cap + 1 GiB, 1.5*peak + 1 GiB))`; if unavailable, double.
Clamp to fresh machine budget after host reply reservation. A cap no larger than the
old cap or observed peak is refused. Hard-limit peaks can be censored observations.

Before a low-memory refusal, reap the pool's own idle workers and re-read actual free
RAM/commit. Working-set credit alone misses paged-out private commit. Never reclaim
another process or start a replacement before confirming the old owned process exited.

Automatic 2 GiB verification succeeded for all three small fixtures on both runtimes,
with six byte-identical worker/reference pairs. The peak/reclaim/persistent-worker tests
pass on both runtimes; Core policy tests cover extrapolation, RAM/reply limits,
peak-aware retry and host admission. The culling diff and its acceptance status are unchanged.

Evidence: `~/.t3/m2d-plus-models/evidence/worker-memory-14bc0e5/` contains
`calibration/`, `default-cap/`, focused test results, the hook log, and separate patches.
