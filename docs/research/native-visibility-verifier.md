# Native visibility verification

Base: `4bbe6b5` (main, including #20 and #21). Local Windows/Rhino 8.36
measurements; no installation, commits or pushes. External model archives were
read only. All hosts started by this task ran serially. Shared-machine timing is
indicative; model measurements respect the suite's shared admission mutex. The
correctness-only composition gate uses its existing mutex-free owned hosts.

## Planar controls and rank 7

The two-box controls were rerun first on unchanged main, then with verification.
The independent analytic halfspace/ray witnesses from `view_audit.py` give:

| View / world witnesses (z = 0 and 1) | Analytic truth | Raw native | Main and final composition |
|---|---|---|---|
| 17°, (1, -1.5, z) | Visible | Hidden | Visible |
| 45°, (1, -1.5, z) | Visible | Visible | Visible |
| 45°, (1, -0.5, z) | Hidden | Visible | Hidden |

Thus #20 fixes both reported planar errors. Both controls make zero HLD calls,
emit no Unresolved curves and never instantiate the verifier. Their complete
classed locus JSON files are byte-identical before/after, SHA-256:

* 17°: `27701fe263392250ef1fd3b7bf7daa2b82390edad01c75980d87a781332f0908`
* 45°: `1ba5e0baaa898ce0148ef2624dfd6e85e765c786ff598b71387e2d6583f29bd8`

The rank-7 reproducers also completed on main and this change:

| Reproducer | Visible / Hidden / Unresolved, both runs | Detected contradictions |
|---|---|---:|
| round tower / Radial 030 forward | 33 / 39 / 12 | 0 |
| rotated curved-S 17 / South elevation | 1537 / 2014 / 868 | 0 |

These curved views have no analytic class oracle in the original findings.
Unchanged counts and zero sampled contradictions **do not resolve or certify**
the earlier orientation/periodicity swaps. Tower exhausted 4095 curved ray tests
at 1078 samples (424 budget skips, 486 inconclusive samples); rotated S used
16382 samples and 4095 tests (17345 skips, 157 inconclusive). Skip counts include
both incomplete sample queries and pieces skipped for lack of sample budget.

## Implementation

`NativeVisibilityVerifier` borrows owned, normalized, post-glass/post-trim
geometry; it never accesses a document. It includes source self-occlusion.
Planar faces use the exact kernel point predicate. Curved Breps use finite
curve/Brep intersections through their trims; meshes use ray intersections.
Surface/extrusion wrappers get owned temporary Breps. A spatial index conservatively
selects possible curved occluders; it does not decide visibility.

Samples include both ends and at least three interiors, with arc-length spacing
at most 1/128 of the view diagonal (8 model-tolerances minimum spacing). A definite
curved decision requires agreement of a central ray and four rays displaced by
4 tolerances in the view plane. This rejects grazing/endpoint and fitting-margin
false alarms. Failed/invalid intersections and gap margins are inconclusive.
Depth gaps at or below tolerance do not occlude; the exact planar route keeps the
Visible tie convention. No sampled result is promoted to Visible or Hidden.

A contradicted sample vetoes its local sample cell. Adjacent veto cells merge;
the native curve is split into native-confidence and Unresolved spans. Every
contradiction records source, parameter, world point, native class and hit witness
in Info. World-depth geometry, source identity and ownership survive splitting.
In composition, HLD sees curved occluders only: its audit therefore checks that
claim before the exact planar union. A later complete planar Hidden proof still
decides the union. The native fallbacks (uncut and route C) audit their full owned
scene. Fully planar output bypasses all of this. Native-only remains a raw
comparison adapter.

`NativeSettings.Verification` configures deterministic per-view caps: 4096
individual curved ray tests and 16384 samples by default. A zero sample cap
explicitly disables the audit and reports that in Info. There is no wall-clock
cutoff: stopping at 10% of an observed native time would make output depend on
machine load. Timing lives in phase reports, outside deterministic diagnostics.

This is a bounded contradiction detector, **not a continuous visibility proof**.
Unsampled interiors, budget skips and inconclusive rays retain an explicit
unverified-native Info verdict. No optional sampling-based class upgrades were
added. General freeform output remains uncertified.

## Measurements and acceptance

All five enabled-repeat drawing hashes match, including diagnostics. Counts
below are from the first enabled run, not summed across repeats. "Pieces" counts
native records with contradicted sample cells; each produces one Unresolved span
in these cases. Final Unresolved counts also include pre-existing uncertainty.

| Model / view | Wrong-Visible / false-Hidden samples | Pieces | Final Unresolved (off → on) | Audit phase | Native solve |
|---|---:|---:|---:|---:|---:|
| curved-perf-S-blocks SW | 18 / 1 | 6 | 157 → 163 | 1.204 s | 25.865 s |
| curved-perf-S-blocks North | 13 / 0 | 3 | 372 → 375 | 0.882 s | 16.052 s |
| curved-facade SW | 5 / 0 | 1 | 47 → 48 | 0.326 s | 0.962 s |
| curved-steel SW | 26 / 0 | 9 | 0 → 9 | 1.050 s | 0.519 s |
| diagrid-shell SW | 14 / 0 | 2 | 30 → 32 | 1.762 s | 1.950 s |

Total: **76 wrong-Visible and 1 false-Hidden sample, affecting 21 native pieces**.
Audit phase overhead is 4.7% / 5.5% of native time on curved-S SW/North, and
33.9% / 202.3% / 90.4% on facade/steel/diagrid. This is the measured tradeoff of
a fixed work cap on fast views; there is no 10%-of-wall-time guarantee. Paired
wall times (off → on) were 26.287 → 27.226, 16.165 → 17.020,
0.966 → 1.302, 0.545 → 1.573 and 1.596 → 3.719 seconds. Native warm-state and
shared-machine load also affect those wall differences; use the explicit audit
phase for audit cost. The nested `native ray audit work` timer is a subtotal,
not another additive phase.

Every run reached 4095/4096 curved ray tests. The respective sample / budget-skip /
inconclusive counts were SW S 16383 / 21283 / 95; North S 16383 / 19224 / 72;
facade 4711 / 4197 / 319; steel 1546 / 742 / 461; diagrid 2789 / 2182 / 96.
These limits are reported in Info, not treated as clear rays or complete coverage.

**Final acceptance:** Debug and Release builds pass with **0 warnings / 0 errors**.
The full `Test-WalkingSkeleton.ps1` run passes on **each of .NET 8 and .NET 10**:
218 Core, 40 UI-model and 187 licensed Rhino tests, no skips. Eight new licensed
cases cover contradictions and splitting, no class flipping, self-occlusion,
contact tolerance, mesh contact masking, deterministic work exhaustion/cancellation,
detached depth geometry, and the 17°/45° controls. The forced local hook passes;
10 harness and 11 view-contract/collector/oracle tests pass in their appropriate
Python environments. The measurement and independent-audit tools also build
without warnings.

**Owner branch gate: PASS, 217/217 required views complete, 0 known bugs,
0 evidenced wrong-Visible samples.** The raw whole-output comparison remains
separate: after certificate verification, 186/217 strict rows pass and 28568
samples remain unclassified. The light gate retains 23 cases as Unresolved/Info,
including all eight M views; it does not certify those comparisons as Visible.
The full 233-view beta gate is still required. L and curved-S supplemental gate
views were excluded with `--required-only`; curved-S measurements above are separate.

The raw reference flagged 150 point checks: 16 on six uncut planar views,
2 on `crossing_curve / Plan-section`, and 50/82 on M blocks/exploded SW.
Fresh all-pairs interval/interior-point witnesses and, for the mixed cases,
complete curved-body ray checks clear all 150 under the existing tolerance
certificate rule. Raw rows and failed samples are preserved. The planar section
certificate makes no cut-region/topology claim. No production classes, tolerance,
or gate acceptance rules were changed to clear these checks.
Both mixed captures reproduce 210 curved bodies: 52500 and 86100 complete
body/ray decisions respectively (including conservative bound exclusions), all clear.

The first M batch hit its 1800 s process watchdog during independent adjudication,
after drawing had completed. Its receipt and partial evidence are preserved in
`capped-M-blocks/`. The final run used a 7200 s oracle/process budget and completed
all required views. This oracle cost is excluded from engine/audit timings above.

The following evidence files are generated locally under the ignored
`artifacts/native-verifier/` directory:

* `baseline/analytic-rays.json`, `baseline/` and `verified/`: main/final controls,
  classed loci, SVGs, engine/fixture hashes and owned-job receipts.
* `measurements/measurements.json`, `north-retry/measurements.json`: audit-off,
  audit-on and enabled-repeat solves on the same prepared scene, with 120 s
  per-solve cancellation and owned 400 s / 8 GiB process limits.
* `summary.json` and `results.html`: measurement summary and actual before/after
  contradiction close-ups; orange is Unresolved. These are drawing tessellations,
  not shaded geometry or continuous proof.
* `routing/`: complete serialized outputs and independent witnesses;
  `light-gate/merge-gate.json`: the owner-sanctioned branch verdict.

Reproduction (use the research Python venv for Rhino.Inside, default Python with
rhino3dm for the independent reference preparation):

```powershell
dotnet build tools/perf-f -c Release
python -B tools/perf-f/native-verifier.py --resolver <resolver>
dotnet build tools/hc-perf -c Release
python -B tools/hc-perf/composition.py --required-only --publication --watchdog-seconds 7200 --resolver <resolver> --output artifacts/native-verifier/routing
dotnet build tools/routing-audit -c Release
python -B tools/hc-perf/routing_reference.py artifacts/native-verifier/routing
python -B tools/hc-perf/certify_intervals.py artifacts/native-verifier/routing
python -B tools/hc-perf/certify_mixed_intervals.py artifacts/native-verifier/routing --resolver <resolver>
python -B tools/hc-perf/routing_light_gate.py --inventory artifacts/native-verifier/routing/manifest.json --output artifacts/native-verifier/light-gate artifacts/native-verifier/routing
./tools/Test-WalkingSkeleton.ps1 -Resolver <resolver>
```

The interval certificate generator leaves raw gate rows untouched. For a
planar-only tolerance-boundary contradiction it requires a complete rational
interval comparison, a nearby Visible interval, and a freshly checked interior
point that the independent all-pairs point oracle calls Visible. Certificates
bind all input/output hashes and are consumed by the existing gate verifier.

Mixed certificates use only the disputed planar candidates, visiting every
planar face for their independent intervals and interior-point checks. They do
not claim complete line agreement. A separate serial `trace-native` run must
reproduce the entire planar snapshot and canonical native input signatures, then
check those interior points against every owned curved body with centre and four
tolerance-offset rays. Conservative world-box projections may exclude bodies;
no production spatial index or HLD visibility label decides these point proofs.
The native trace, body fingerprints, complete body/ray decision counts and owned
process receipt are hash-bound too. Section certificates cover disputed visibility
points only; region/topology comparisons remain separate.
