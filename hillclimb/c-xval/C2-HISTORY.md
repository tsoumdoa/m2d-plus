# Sections theory C: independent evidence certificate

C's historical log is retained below. See [C2 final retained result](#c2-final-retained-result)
for the active continuation verdict.

Branch: `hc/sec-c-evidence`, based on `origin/m3/wiring`. No commit, push, installation,
or owner-process access. Licensed hosts run serially through `tools/Test-WalkingSkeleton.ps1`.

## Frozen score and reproduction

`EvidenceCertificationTests.FullEvidenceScore` covers all six curved feature files
in `hillclimb/fixtures`, all sidecar section planes, plus generated plan/side cuts for
the continuity fixture (its sidecar has no cut). Block members are expanded with
their complete transforms. SubD is converted to owned Breps; mesh geometry retains
its polyhedral interpretation. Model SHA-256, engine hash, Rhino and CLR versions
are included in every score.

Generated cases: thick CHS torus arcs at 0/30/60 degrees; spline RHS; twisted RHS;
filleted I-beam arc; pipe contact at 0, ±0.5 and ±2 tolerances; two disjoint lumps
straddling without crossing and a third crossing lump; overlapping spherical nodes;
closed SubD canopy converted to Brep; open cylindrical shell.
The spline RHS, twisted RHS and filleted I-beam reuse the frozen generator's bodies
at additional independently specified cut planes; the other certification bodies
are constructed by the test. The closed SubD control is a subdivided, flattened box.

Independent expectations use analytic circle/profile arithmetic, a 65,536-edge
line integral of the exact torus parametrization, or numerical quadrature of exact
material scanline chords. The latter uses CurveBrep roots and interior predicates,
never BrepPlane loops, Trim, caps, or section-region mass properties. Its two-grid
convergence delta is recorded in `hillclimb/oracles.json`; expectations are frozen
before candidate scoring. General area allowance is max(2e-5, 0.2% of expected area)
plus the recorded quadrature delta. Contact band cases intentionally expect no fill.
The scorer checks wrong fills, closed outer loops, analytic hole counts, and loop
loci on source boundaries. Existing topology/hole/crop/merge tests remain mandatory.

Primary: certified crossing source/view cases divided by independently expected
crossing cases. Missing/uncertain crossings reduce this fraction. Secondary:
median measured source certification milliseconds and uncertain reasons. Oracle
generation is outside the timed interval. Timings are paired serial observations
on a shared machine, not an isolated performance claim.

```powershell
./hillclimb/Score.ps1 -Iteration 0 -Summary 'Unchanged analytic-face baseline'
```

## Iteration 0 — unchanged implementation

Harness setup: the first attempt exceeded the ordinary host's 90-second test
timeout during independent oracle generation. No score was emitted. The scorer
now explicitly requests a longer timeout; ordinary tests retain 90 seconds.
Oracles are saved incrementally. No case was removed.

Before renders: `artifacts/render/sec-c-before/`, via `tools/render/Render.ps1`.

## Certificate under test

Each already-separated material lump must be valid, closed, manifold and outward.
Every planar material region needs a verified interior with boundary clearance
and strict material witnesses on both sides beyond the tolerance band. Transverse
intersection checks reject planar band contacts and detected tangent normals.
Both closed, outward, nondegenerate half-solids must stay in their respective
halfspaces. Their volumes must conserve the original within 1e-6 relative
(64 tolerance cubed absolute floor), and each cap must conserve planar region area.
All routes share these gates and leave the original plane unchanged.

Routes: Trim+CapPlanarHoles, BooleanSplit with a bounding halfspace box, then
Split+JoinBreps with the complete material caps. Every native temporary is owned
and disposed. Route and conservation residual are reported through Info diagnostics.

## Iterations

All candidates below were rejected. The active product implementation is restored
to the baseline. Rejected prototype source is retained as `.cs.txt` files under
`hillclimb/rejected/` for review only; it is not compiled or routed at runtime.

Iteration 0 completed: **54/176 = 30.6818%**, 804 source/view cases; zero wrong
fills, gate passed. Median 117.289 ms/object, crossing median 123.431 ms.
118 crossings failed the legacy certificate; 4 failed face-intersection validation.
Rhino 8.36.26296.17001 / CLR 8.0.34. Full score is in scores.jsonl.

### Iteration 1 — replace analytic/vertex eligibility with independent evidence

Hypothesis: paired closed-half conservation plus interior/material and contact
checks can certify curved faces without requiring topology vertices to straddle.
One candidate introduces the complete certificate and uniformly gated fallbacks.
A build typo in mesh-point arithmetic was fixed before scoring (no geometry change).

Iteration 1 rejected and reverted. Benchmark: 100/176 (56.8182%), median 115.208 ms;
12 space-frame oblique CHS fills miss the independent area expectation by ~0.7%.
The paired volume/cap checks alone do not detect that discrepancy. Full licensed
run: 134 pass, 3 fail (score, 2-tolerance sliver witness, obsolete blanket freeform
rejection). The latter assertion will be replaced with independent area/material
checks for the newly certifiable fixture, retaining the self-intersecting mesh gate.

### Iteration 2 — independent material chords complete the certificate

Hypothesis: normal material intervals provide strict side evidence even for a
2-tolerance sliver, while independent in-plane exact chord quadrature detects
shared boundary/cap errors invisible to paired volume conservation. Same score
set and area allowance. No area or volume conservation threshold is relaxed.

Iteration 2 rejected and reverted: benchmark gate passes (zero wrong fills), but
47/176 (26.7045%) is below baseline. Median 114.058 ms, crossing median 359.189 ms.
Full licensed gate fails: 31 tests rejected predominantly because the chord query
reports tolerance-band overlaps near its integration endpoints. A standalone
benchmark gate pass is insufficient to accept this candidate.

### Iteration 3 — use numerical integration accuracy below the certificate budget

Hypothesis: native query/mass-property accuracy must be tighter than the residual
being certified. This changes numerical integration/query accuracy only; the
original plane, modeling contact band and all scorer/conservation limits stay fixed.
Use precise volume/area integration and a scale-aware chord fitting tolerance.

Iteration 3 rejected and reverted: **63/176 (35.7955%)** and zero benchmark wrong
fills, but **12 existing licensed tests fail** (125 pass). Median 127.124 ms/object,
crossing median 502.856 ms. Candidate 3 removes numerical mass-property ambiguity
but native CurveBrep overlap reconstruction and chord convergence still reject
legitimate RHS/I-beam/cavity fixtures. Full gate failures are in scores.jsonl.

**Stop: three consecutive non-improving candidates.** No candidate passed the full
gate, so baseline **54/176 (30.6818%)** remains best. No engine modification is kept.
The original self-intersecting/freeform regression is restored unchanged. The
rejected prototype and four associated files are retained only as `.cs.txt` review
artifacts. Continuing with a parameter-only intersection query, adaptive chord
integration, seam cleanup, or preparation reuse would require a subsequent climb;
none was silently introduced after the mandated plateau.

Kept scope: the opt-in scoring test, six frozen feature fixtures and sidecars,
independent oracle cache, per-case records, final-verdict wrapper, and this log.
The test host's optional timeout defaults to its original 90 seconds. The acceptance
script's optional runtime selection defaults to its original serial .NET 8/10 pair.

Rows 0–2 initially report the benchmark gate. Iteration 2 has an appended final
licensed-failure verdict. From iteration 3, `hillclimb/Score.ps1` waits for all
licensed results and includes failed test names in the single final JSON verdict.
No benchmark case, correctness allowance or original contact policy was removed.

## Final acceptance of the retained baseline

- Final full score on CLR **8.0.34 and 10.0.15**: **54/176 (30.6818%)** from
  804 cases, zero wrong fills. Both final rows include the full licensed gate.
- Each runtime passes **186 Core + 40 GH UI-model + 137 licensed Rhino/GH tests**,
  including the scorer. Runs are serial via `tools/Test-WalkingSkeleton.ps1`.
- Debug/Release builds: **0 warnings, 0 errors**. Forced Git-sh local hook passes
  (the current hook runs Core and GH UI-model tests). Ordinary solution tests pass
  with licensed discovery skipped. `git diff --check` passes.
- Original and final engine SHA-256 are identical:
  `F0331914DE6C06036824557A0A66A6EEEDA1695C88DB3662B974A4230A216A40`.
- Before: `artifacts/render/sec-c-before/index.html`; after:
  `artifacts/render/sec-c-after/index.html`. Each contains curved-steel and diagrid
  plan/long-section PNG/SVG cards and Info, plus the analytic range control.
  Both models' long-section PNG files are byte-identical before/after.
- Logs: `artifacts-sec-c-final-tests.log`, `artifacts-sec-c-release.log`,
  `artifacts-sec-c-hook.log`, `artifacts-sec-c-unlicensed.log`, and
  `artifacts-sec-c-final-render.log` in this worktree.

The owner-facing curved/freeform section issue remains unsolved by this climb.
No production certificate or trim/cap fallback is accepted. The evidence routes
are numerical candidates, not a universal proof of embedded freeform topology or
contact. Native chord ambiguity/convergence, seam-loop validation, and existing
cavity/RHS conservation regressions prevent acceptance. No commit, push, Yak
installation/publication, or attachment to another person's Rhino process occurred.

## Continuation C2 — third-method oracle audit

Started from committed `origin/hc/sec-c-evidence` (`cc930f6`) on `hc/sec-c2`.
The old baseline remains 54/176 (30.6818%); the case set stays 804/176.

**Verdict: the oracle was wrong on all 12 disputed cases.** Despite the old
"oblique CHS" description, these particular members are straight circular tubes
cut by a plane parallel to their axes. `SectionOracleAuditTests` inspects the exact
frozen block geometry and full instance transforms: four faces (two cylindrical,
two planar end annuli), radii R=0.065 and r=0.05 m, local z=0..1, orthogonal
transformed cross-section axes of unit length, and a horizontal axis of length
L=1.2 m. There is no faceting, sweep rail or approximated circular section.
The plane z=4.5 is at d=0.05668327095467873 m from the axis (above the bore radius).
Thus the intersection is one solid rectangle, with no hole, of exact area
`2 L (sqrt(R²-d²) - sqrt(max(0,r²-d²))) = 0.07634893013487501 m²`.
This closed-form calculation uses no BrepPlane loops, cap mass properties, native
CurveBrep intersections or engine outputs. Source point-membership checks also
verify the rectangle edges and exclusion of the uncut bore. All 12 engine values
agree within 4e-15 m². All old oracle values and engine values are retained in
`hillclimb/c2-tube-audit.json`; no original iteration/score record is edited.

The old oracle was about 0.07689260048227647 m² (+0.7121%). The sine-grid
midpoint quadrature samples a constant chord length L over an interval with
jump discontinuities. The 128/256 grids give 0.0768940479909996 and
0.0768926004822765: a misleading convergence delta of only 1.45e-6 despite the
0.00054367 error. A 65,536 grid gives 0.0763514591348665, approaching the analytic
answer. Only these 12 frozen expectations are corrected; their exact analytic
errors are zero and hole counts zero. The 0.2% allowance is unchanged (and loses
the old quadrature-error addition). No case or expected crossing is removed.

### Iteration 4 — restart iteration 1 with the audited oracle and sliver witness

Hypothesis: iteration 1's paired-volume, cap-area, interior/material and tangency
certificate was rejecting correct curved cuts because of the oracle, while its
fixed-distance side witnesses unnecessarily rejected legitimate two-tolerance
slivers. Restore the retained source, remove iteration 2/3's chord-area gate and
mass-property accuracy changes, and query the connected normal material interval
with parameter-only CurveBrep roots. Require roots and actual interior witnesses
strictly beyond the original modeling tolerance, on both sides; never move the
plane or reduce the contact band. Replace the obsolete blanket converted-SubD
rejection with independent chord area, topology and a 19×19 material-membership
comparison; retain the self-intersecting mesh rejection.

Iteration 4 **kept**: 112/176 = 63.6364%, zero wrong fills, on both CLR
8.0.34 and 10.0.15. All 138 licensed tests pass on each runtime (plus 186 Core
and 40 GH UI-model tests); Debug builds have zero warnings/errors. Median
certification: 139.257 ms (.NET 8), with complete runtime records in scores.jsonl.
The first .NET 8 row used C's original wrapper; its .NET 10 companion records
`bothRuntimeGate`. Subsequent iterations use a wrapper that saves both complete
runtime records and waits for both full licensed gates before accepting a candidate.

### Iteration 5 — accurate mass integration within the conservation budget

Hypothesis: default native mass-property integration is too coarse to certify
1e-6 relative volume/cap conservation for thin curved material. Request 1e-9
relative / 1e-12 absolute integration accuracy for volume and planar region area.
The pinned API has no curve-area accuracy overload; a build-only attempt to use
one was corrected before any score. Loop area keeps its original implementation. Keep every geometric/modeling tolerance and conservation bound fixed.

Iteration 5 **kept**: 112/176 on both runtimes, zero wrong fills and all 138
licensed tests pass per runtime. Same certified cases as iteration 4; median
certification improves from 139.257 to 104.956 ms on .NET 8 (full .NET 10 timing
in scores.jsonl). Computing only the requested mass properties also avoids unused
moments. This is a serial shared-machine observation, not an isolated speed claim.

### Iteration 6 — numerical fitting below the conservation budget

Hypothesis: the remaining cap/volume residuals reflect native intersection/trim
fitting rather than integration. Use 0.001×model tolerance (with a coordinate
roundoff floor) for BrepPlane and native Trim/Split/BooleanSplit/Cap/Join fitting.
The actual plane, classification/contact band, transverse checks, planar topology,
provenance and all conservation/scorer thresholds are unchanged.

Iteration 6 **kept**: 114/176 = 64.7727% on both runtimes; zero wrong fills;
all 138 licensed tests pass per runtime. The newly certified cases are generated
CHS-arc-0 and I-beam-arc-fillets, matching analytic areas within 8.4e-10 m².
Medians: 95.423 ms (.NET 8), 130.506 ms (.NET 10). All source/loop/contact and
conservation checks remain in force. Finer fitting does not resolve the remaining
space-frame/diagrid cap and volume residuals.

### Iteration 7 — reuse validated owned preparation within one compute call

Hypothesis: duplicate source conversion, face-pair intersection validation and
material-component extraction dominate the source median. Prepare once in
SectionRegionEngine, borrow those owned components for PhysicalSectionClipper,
and dispose only in their owner. Public clip callers retain their own preparation.
All clipping, fill, topology, certificate and output-copy behavior is preserved;
there is no durable cache or change to leases/native-work lifetime.

Iteration 7 **rejected and reverted**: 114/176 on both runtimes, zero wrong fills,
all 138 licensed tests pass. Medians 129.674 ms (.NET 8) and 124.090 ms (.NET 10),
versus iteration 6's 95.423/130.506. Coverage/reasons do not improve and the
secondary change is not consistent across runtimes; the primary .NET 8 median
regresses. Do not retain a speculative optimization on shared-machine noise.
Source is retained under rejected/c2-7-*. First consecutive non-improvement.

### Iteration 8 — canonicalize redundant section-curve representations

Hypothesis: redundant knots, joined segments and equivalent arc/line NURBS cause
native overlap/provenance and cap construction ambiguity. Simplify joined section
curves at the numerical fitting accuracy, excluding G1 adjustment; keep every
loop/contact, source-boundary, cap and volume gate. No seam interval is discarded
merely because it is short or adjacent to a periodic boundary.

Iteration 8 **rejected and reverted**: 108/176 = 61.3636% on both runtimes,
zero wrong fills and all 138 licensed tests pass. Medians 130.378/123.697 ms.
Equivalent curve canonicalization loses six previously certified cases through
cap/topology/provenance checks; it does not improve the gated result. The trial
SectionGeometry source is retained under rejected/c2-8-*. Second consecutive
non-improvement. Restore iteration 6 before the next hypothesis.

### Iteration 9 — rigid recentering for conditioned mass integrals

Hypothesis: coordinate cancellation in thin curved material's volume and cap-area
integrals accounts for residual conservation failures. Duplicate each Brep only
for mass integration and translate its bounding-box center to the origin. No
source geometry, intersection/trim/cap geometry or plane is changed; modeling and
integration tolerances and all conservation/scorer bounds stay fixed. The score
metadata now additionally hashes the oracle cache; cases and scoring remain frozen.


Iteration 9 **rejected and reverted**: 114/176 on both runtimes; zero wrong fills;
all 138 licensed tests pass per runtime. Recentered integrals do not certify a new
crossing or reduce uncertainty. Medians 121.674/122.991 ms, versus iteration 6's
95.423/130.506; no consistent secondary improvement. Both trial files are retained
under rejected/c2-9-*. Third consecutive non-improvement: **stop at the plateau**.
No further candidate was introduced. Active production source is restored exactly
to iteration 6.

## C2 final retained result

**114/176 = 64.7727%**, versus **54/176 = 30.6818%** baseline: +60 crossings,
+34.0909 percentage points. The 804 cases and 176 expected crossings never change.
The oracle correction does not change the baseline score: all 12 corrected cases
had no baseline fill. No allowance is loosened; the corrected analytic cases have
zero quadrature-error allowance and explicit zero holes.

| Iteration | Certified / 176 (both runtimes) | Median ms (.NET 8 / 10) | Verdict |
|---|---:|---:|---|
| 4 | 112 | 139.257 / 139.143 | Kept |
| 5 | 112 | 104.956 / 102.296 | Kept |
| 6 | 114 | 95.423 / 130.506 | Kept |
| 7 | 114 | 129.674 / 124.090 | Rejected |
| 8 | 108 | 130.378 / 123.697 | Rejected |
| 9 | 114 | 121.674 / 122.991 | Rejected |

Winning changes: audited analytic oracle correction; the restored evidence
certificate with parameter-only normal intervals and strict material witnesses;
accurate mass-property integration; tighter native fitting under unchanged model
contact and conservation gates. The obsolete converted-SubD refusal is replaced
by independent area, one-loop topology and 361 material probes. Self-intersecting
mesh rejection and all existing topology/cavity/crop/merge/contact tests remain.
The reuse, curve-simplification and mass-recentering candidates are not active.

Remaining 62 uncertain crossings: 56 conservation/closed-half failures, four face
validation failures, one unattributed boundary interval and one tangent-screened
2-tolerance cylindrical grazing cut. This remains numerical certification of the
tested domain, not a universal freeform topology proof. In particular, curved steel
and the diagrid are improved only where the evidence passes; remaining pieces must
still be withheld/marked unresolved. No heavy in-process fallback is added.

Oracle cache SHA-256 (old / corrected):
`82270F3757F1B2385A8DAAF803484DE5F0038470B1DDDB484BDD96E3F0846AE5` /
`D8923DAB12C0DB108B6871BD1F94F172A25287F5D865F943A3F80C461A79B993`.


### Build-input audit — iteration 7 invalidated, plateau claim withdrawn

A final SHA check caught an incremental-build hazard: Copy-Item preserves source
mtime. A newer trial DLL can therefore survive a source restore. Iteration 7's
engine SHA equals iteration 6 exactly: its preparation-reuse candidate was not
compiled, so its apparent performance regression is not valid experimental
information. Its old measurements remain in the log; appended build-audit verdicts
in scores.jsonl explicitly invalidate that experiment. Iterations 4, 5, 6, 8 and 9
have distinct engine hashes and compiled their actual candidates. The restored
iteration 6 hash matches exactly after a forced rebuild. No wrong-fill or licensed
gate failure was concealed by the audit.

The previous three-in-a-row plateau claim is withdrawn: only iterations 8 and 9
are valid consecutive non-improvements. Score.ps1 now forces a nonincremental
candidate build before either runtime. This changes build freshness, never the
804 cases, oracle allowance, or licensed test gate. Actual reuse is re-tested as
iteration 10. Final conclusions above remain provisional until that result.

### Iteration 10 — actual preparation-reuse candidate (repeat of invalid 7)

Same single hypothesis/source change proposed in iteration 7, restored on the
iteration 6 winner, now with a forced rebuild and a distinct engine SHA required.

## User-directed stop and cross-validation handoff

Climbing stopped at the owner's explicit change of task. Iteration 10 is abandoned
mid-run with no complete score or acceptance claim. Only this task's verified
PowerShell/dotnet/testhost process tree was terminated; no Rhino owner process was
touched. The last accepted C2 candidate is iteration 6, 114/176, but all uncommitted
engine changes are now reverted for independent validation of D. The 0.7% verdict
remains **oracle error**, proven by exact finite-cylinder chord arithmetic on all
12 frozen tubes. Old values are preserved in c2-tube-audit.json. Oracle allowance
is unchanged, with zero quadrature error and zero holes for the corrected records.
