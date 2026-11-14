# Sections theory C: independent evidence certificate

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
