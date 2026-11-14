# Kernel routing integration: failed acceptance gate

The working diff on `engine/kernel-routing` is **not an accepted engine pivot**.
The composition contract was written before implementation in
[engine-composition.md](engine-composition.md). The full comparison run completed,
but its required zero-unexplained/zero-bug verdict was not achieved. Nothing has
been committed or pushed.

## Whole-output comparison

All 233 requested view cases completed with matching model and compiled gate
producer hashes: 171 passed and 62 failed. There were 1,279,885 unexplained
**comparison samples** and 2,472 samples with positive native-order reproduction
evidence. These are bidirectional sample counts, not distinct spans or lengths.
Tie/tolerance classification is unfinished; zero entries in those classes do not
mean the remaining discrepancies cannot belong to them. Bug clearance is unknown.

The inventory includes root fixtures, every feature fixture, orthogonal M/L
blocks and exploded forms in North/SW/Top/long section, curved S in both forms and
the same four views, and both cut views of every section fixture. Curved steel and
diagrid shell passed all three uncut views. Curved facade failed all three.
Curved S passed all six uncut cases; each long section had 12 unexplained samples.
M/L and several entirely planar section cases remain failures.

Native always receives canonical M1b source ordering and hidden curves on. A
native-order exception requires the same full-scene input signatures in reverse
order and a reproduced class change at an exclusively native-owned sample. A
kernel-owned contributor disqualifies the exception. The evidence files retain
exact output geometry, per-sample classes, input signatures and control hashes.
No kernel-owned discrepancy was cleared by native-order evidence.

The local evidence is under `artifacts/hc-perf/composition/`:

* `verified-gate.json`: complete manifest, verdicts and compiled producer hashes.
* `per-view.csv`, `report.json`, `timings.html`: per-view end-to-end times,
  contextual archived-native comparisons and route participation.
* `*.failures.json`, `*.order-witnesses.json`, `*.order-control.json`: failure and
  exception evidence, with serialized exact curves in the corresponding outputs.

One completed L-exploded North case has a hash-bound
`case-completed-before-coordinator-cancel` supervision record. Its later sibling
views were rerun to normal completion; the completed case is not represented as a
normal process exit. All requested rows are present.

## Contextual timings and routing

Seconds are geometric means. All eight views per tier have new end-to-end times.
Six per tier match archived native hashes/views; uncut Top has no matching archive.
Archived timings used the model-suite adapter, while current timings include
capture, preparation, routing and presentation (plus route C in cut views).
These are contextual ratios, not a controlled same-adapter performance result.
The correctness-native runs were serial and used no timing mutex.

| Tier | All-view current seconds | Matched current seconds | Archived native seconds | Native/current |
| --- | ---: | ---: | ---: | ---: |
| Orthogonal M | 14.35 | 17.74 | 18.32 | 1.032x |
| Orthogonal L | 57.10 | 69.88 | 70.37 | 1.007x |
| Curved S | 30.26 | 36.26 | 18.82 | 0.519x |

| Tier | Kernel candidates | Native curved candidates | Planar candidates also sent to native | Kernel share | Native participation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Orthogonal M | 312,494 | 11,336 | 6,830 | 96.50% | 5.61% |
| Orthogonal L | 1,254,420 | 44,688 | 21,638 | 96.56% | 5.11% |
| Curved S | 0 | 33,302 | 0 | 0% | 100% |

The denominator is drawable kernel candidates plus drawable native parent curves.
Native participation also includes the planar curves queried against curved
occluders, so the two percentages overlap. Native invalid extraction can make
that census incomplete; per-view rows flag this. Fully planar hidden candidates
need no foreign visibility query. Entirely planar scenes make zero HLD calls.

## Remaining acceptance work and final-fix provenance

The complete gate predates one final presentation fix: an already sub-tolerance
projected curve could acquire identical interpolation endpoints when split at a
Beyond threshold, causing range splitting to throw. The final guard discards a
null trimmed interval only when the entire projected curve extent is within model
tolerance, and counts it as projecting. A regression reproduces the failure.
The final runtime suites and contact sheets include the guard; the complete
whole-output gate was not rerun after it. The compiled gate binary and pre-fix
presentation source remain in local artifacts. No acceptance claim is made for
the final binary.

Other material limits remain:

* Unexplained kernel-owned and native-owned discrepancies need geometric audits;
  tie/tolerance/native-quirk classification and bug clearance are incomplete.
* Planar profile metadata is not reconstructed: emitted kernel edge candidates
  currently carry `Profile=false`. Profile-weight parity is not certified.
* Curve proximity is exact, but coverage is sampled in both directions at model
  tolerance, with at least eight samples per curve and maximum 50 mm spacing.
  This is not a symbolic proof of full nonlinear locus equivalence.
* Cut-loop loci, region areas/loop counts and existing style/export tests are
  checked. Hatch-pattern loci do not have a separate full-output oracle.
* Large validation uses an isolated 16 GiB scheduler/watchdog budget; production
  still uses the existing global 512 MiB admission budget.

The final `Test-WalkingSkeleton.ps1` run passed 204 Core, 40 UI and 149 licensed
Rhino tests on each of .NET 8 and .NET 10, including the range-split regression.
Final test and render logs are under `artifacts/kernel/`. The requested building,
curved-steel and curved-facade plan/section contact sheets are generated under
`artifacts/render/kernel/`; they are review evidence, not correctness approval.
