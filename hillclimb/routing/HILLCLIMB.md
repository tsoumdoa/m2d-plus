# Kernel routing continuation

Base: `54f95d8a4dbe9163d9ac9b7b7c4959b386c517b2` (`origin/engine/kernel-routing`). No commits or pushes.
The mandatory gate remains zero unexplained and zero bugs over all 233 views.
Focused trials do not certify acceptance. Every metric below uses owned serial
Rhino processes, without a timing mutex; no interactive Rhino process is touched.

## Baseline instrumentation (routing-before)

Fresh native-only then routing on the same captured/prepared scene, adapter and
settings. Capture/normalisation are common per case and charged to both routes.
The comparator and evidence serialization are outside solve timing. Baseline
publication was instrumented in the product but not executed by the original
Python adapter. Final measurements execute actual component Emit and GH volatile
trees in an owned STA RhinoCore. Scheduling, downstream GH solves and redraw are
excluded and no interactive performance claim is made.

| View | Routed seconds | Fresh native seconds |
| --- | ---: | ---: |
| M blocks North | 6.565 | 15.750 |
| M blocks SW | 13.951 | 26.506 |
| L blocks North | 23.784 | 76.662 |
| Curved S blocks SW | 23.377 | 23.328 |

The original reported 2x curved slowdown did not reproduce with a fresh comparator.
HLD remains the largest phase. Kernel point decisions always used rationals in
integration; they now use certified interval filters with exact fallback.
Reconciliation removed each piece separately from a List (quadratic); it now uses
a set and one RemoveAll pass. No planar participation bypasses reconciliation and
duplicate witness indexing, retaining native candidate semantics.

## Diagnostic climbs

1. Native endpoint inverse-transform roundoff created missing-coverage slivers.
   Snap only machine-roundoff endpoint drift; do not fill real missing coverage.
   A real world edge projecting to roundoff-sized extent is counted as projecting;
   isolated short world features survive. Smallest two planar cut failures clear.
2. Welded nonplanar mesh silhouettes are smooth native loci, not topology-edge
   facing transitions. Keep those whole meshes, including occlusion, in native.
   All three curved-facade views clear (3907/3403/361 -> 0/0/0 samples).
3. Route C must use the same full-scene camera bounds as native-only, rather than
   changing camera placement from clipped inputs. The curved-S long section is
   part of the final rerun; the earlier 12-sample verdict is not reused.
4. Planar Hidden must decide the union even if native duplicate attribution is
   Unresolved. Duplicate front-witness comparisons now budget only inverse-frame
   roundoff (capped at tolerance/64), never a model-tolerance behind alias.
5. Planar edge profile metadata is derived from adjacent oriented face facing;
   naked boundaries and facing transitions retain profile weight metadata.

The pre-existing final range-split guard is already present on the requested base
and remains in place. Regression tests cover its prior case plus the new contact,
mesh, profile, projection and provenance cases.

## Acceptance

The owner replaced the branch merge gate after stopping L exact adjudication.
The final lighter gate passes 217/217 required small-fixture and M views, with
zero known bugs and zero evidenced wrong-Visible samples. Uncertified comparisons
remain Unresolved/Info. This is not a full 233-view pass; see the final known list.
Raw evidence: `artifacts/hc-perf/routing-*`; per-case model/producer hashes, exact
projected and world-depth curves, planar inputs, full-scene order controls and
per-sample failures remain local for Claude's review. Rejected publication probes
under Python (native teardown crash) are retained and excluded from scores.

## Owner decisions and final run

The owner sanctioned Visible for zero-gap contact (signed gap within model
tolerance) and an independent exact decision where native is Unresolved/Unset.
The final gate records `tieConvention` and `exactCheckDecided` separately, with
face IDs, signed gaps, candidate identities, native curved witnesses and complete
all-pairs counts in hash-bound `*.exact-witnesses.jsonl`. Curved uncertainty stays
strict unless a planar Hidden proof decides the union. The reference visits every
face for each disputed candidate bound; scalar bound rejection is amortised across
samples on that same candidate. It does not call the production filtered query.

`All(Eligible)` is restored. Raw kernel candidate footprints also veto native-order
exceptions even when output reconciliation removed a lower-class contributor.
The six repeated witness/query trials have stopped; all are recorded in
`scores.jsonl`, including per-case comparisons against `routing-validated`.

| Trial | Focused unexplained | Disposition |
| --- | ---: | --- |
| two-pass | 89,414 | Rejected: two full-native Visible samples became Hidden; suppressing Duplicate did not prove coverage |
| visible-query | 99,120 | Rejected: M SW regressed against the validated baseline |
| visible-witness | 97,691 | Rejected: M SW still regressed |
| final-witness | 95,682 | Reverted at owner direction; M speed relative to fresh native fell to 1.907x |

The linear/query diagnostic rows describe fixes already present in the validated
producer. Its complete run was 192/233 passing, 967,380 unexplained, 3,922 native
order witnesses, and bug clearance unknown in 41 views. That producer is preserved
as historical evidence, not an accepted result under the owner's final gate rules.

The production recovery retains only conservative Hidden-witness reduction:
identical projected endpoint supports keep their frontmost linear depth graph;
crossing graphs survive. An everywhere-behind witness is rejected before costly
curve operations, using the maximum downstream roundoff budget. No visibility
decision is weakened and native registrations are unchanged. New regressions
cover the depth frontier and the independent contact/hole predicates.

The final retained code passed 207 Core, 40 UI and 167 licensed Rhino tests on each
of .NET 8 and .NET 10. The forced local hook passed. No section performance work
was started. `ComposedVisibility.before-linear.cs` and experimental source backups
were deleted; raw trial evidence is retained. `composition.py` now accepts
`--resolver` or `M2D_RHINO_INSIDE`, with no scratch resolver path in its source.

The final-binary inventory was stopped at owner direction during L blocks SW at
`artifacts/hc-perf/routing-owner-final/`; its source and compiled producer hashes
are frozen in `routing-owner-final-producer/manifest.json`. Four primary timing
views use fresh native-only runs followed by routing with actual GH publication.
Other native geometry/control caches have per-file provenance; their durations
are excluded from performance scores. Forty-six remaining non-L views completed
in `routing-light-remaining/` with the same compiled producer. The combined raw,
verified and owner merge verdicts are in `routing-light-gate/`.

### Supervision and boundary certificates

The original 1,800-second per-model watchdog expired during M SW's independent
reference adjudication, after its native pipeline had finished. M North's completed
gate row is hash-bound as `case-completed-before-sibling-timeout`; it is not falsely
labelled a normal worker exit. Partial SW evidence is archived under
`interrupted-M-SW/`. The same final producer continues with `--resume` and a
7,200-second budget that includes exact adjudication. No completed view is re-solved.
The aborted SW case was restarted; its completed fresh timing replaces the aborted
case, rather than borrowing a partial duration. The 16 GiB memory cap remains.

M blocks SW completed with 86,808 `exactCheckDecided`, 322 `tieConvention` and
8,500 raw unexplained samples. North's duplicate/extraction phase recovered from
1.913 s to 0.483 s, and SW from 1.279 s to 0.994 s. The fresh North/SW M pair is
6.908/13.560 s routed versus 16.613/25.707 s native-only (2.13x geometric-mean speed).

The scalar point check identified four small-fixture and fifty M SW endpoint
contradictions with gaps just beyond tolerance by roundoff. Independent full-interval
all-pairs certificates prove Visible loci inside the unchanged comparison-distance
tolerance: 24 complete edge/face pairs for `column_through_slab__SW` and 320,760 for
M SW. Mixed certificates additionally require exclusion by conservative curved-body
bounds or a definite same-world native query. These are `toleranceArtefact`, not
production bugs. Raw gate rows stay intact; `routing_verify.py` binds the interval
certificates into `verified-gate.json` only after the complete inventory exists.
The independent interval tool uses the frozen Core DLL and performs no Rhino solve.

M exploded SW has a further 82/82 boundary certificates (962,280 complete
edge/face pairs). L North's native extraction/duplicate phase recovered from
6.746 s to 1.878 s; routed end-to-end is 25.359 s against a fresh 77.991 s
native-only run (3.08x). These timings do not include the much slower independent
all-pairs adjudication, which is validation work rather than drawing work.

L blocks' independent SW reference exceeded the initial supervision budget.
Only this run's Python coordinator was paused while its owned native worker
continued, retaining the original hard 16 GiB Windows Job cap. A separate
14,400-second supervisor resumes the coordinator on worker exit (or terminates
only that worker at its extended deadline). The extension record is retained as
`watchdog-extension-L-blocks.json`; the original 7,200-second limit is not claimed
to have been met. The owner then stopped this run. `owner-stop.json` records the
cancellation; it takes precedence over the helper's generic worker-exit wording.
No other person's Rhino process was accessed.

## Final owner merge gate and known list

`routing-light-gate/merge-gate.json` binds 217 required completed views: all small
fixtures (including sections) and all eight M views. It records zero known bugs
and zero independent contradictions left without a certificate. The stricter
whole-output comparison remains separate: 203/226 completed views pass, with
30,644 unclassified samples. Seven L views have no completed final verdict. No
unclassified sample is accepted as Visible by the lighter audit verdict.

The required 217 views contain 18,817 `tieConvention`, 197,882 `exactCheckDecided`
and 150 `toleranceArtefact` samples; native-order exceptions are zero. Including
the completed L North adds 1,355 ties and 3,186 unclassified samples. Each accepted
classification has hash-bound evidence; `All(Eligible)` stays restored.

The reported small-case `knownBugs: 1` was `column_through_slab__SW`: four scalar
endpoint probes. Its complete independent interval certificate (24 face pairs)
proves Visible coverage within the unchanged comparison tolerance. The same
certificate verifier binds it as `toleranceArtefact`, like M SW. Five unit/source
variants add twelve certified endpoint probes; `crossing_curve__Plan-section`
adds two (six complete pairs). Together with the M certificates there are 150.
A regression keeps the scalar strict-gap result Hidden while requiring an actual
independent Visible interval for the boundary certificate; tolerance is not widened.

The eight previously unknown small cases were resolved as follows:

| Case | Final audit disposition |
| --- | --- |
| `block_instance__SW` | Independent complete planar line agreement, 288 face pairs, no missing/extra loci |
| `block_instance_expanded__SW` | Independent complete planar line agreement, 288 face pairs, no missing/extra loci |
| `curves-points__SW` | Unresolved/Info; planar lines agree, point attribution remains uncertified |
| `cut-topology__North` | Unresolved/Info; native duplicate/coverage uncertainty |
| `cut-topology__SW` | Unresolved/Info; definite native/routed coverage difference not independently cleared |
| `edges__North` | Unresolved/Info; native attribution uncertainty |
| `glass__North` | Unresolved/Info; native attribution uncertainty |
| `glass__SW` | Unresolved/Info; missing native coverage and unmatched world-support witnesses |

All eight M views also retain Unresolved/Info for residual uncertified comparisons.
These audit dispositions do not certify those comparisons as Visible or claim
whole-output equivalence. Raw GH geometry and stricter gate rows are preserved
for review, rather than silently relabelled by the reporting tool.

Large-model gaps are **known and non-blocking for this branch**:

| L view | Final evidence / remaining gap |
| --- | --- |
| Blocks North | Completed: 3,186 unclassified samples; no independent contradiction |
| Blocks SW | Exact adjudication cancelled by owner; partial 656,855,040-byte witness file deleted |
| Blocks Top | Not completed on final producer; previous validated run had 966 unexplained samples |
| Blocks Long-section | Not completed on final producer; previous validated run had 58,576 unexplained samples |
| Exploded North | Not started on final producer; previous validated run had 4,195 unexplained samples |
| Exploded SW | Not started on final producer; previous validated run had 305,303 unexplained samples |
| Exploded Top | Not started on final producer; previous validated run had 956 unexplained samples |
| Exploded Long-section | Not started on final producer; previous validated run had 58,809 unexplained samples |

Historical counts in this list use the previous stricter classifications; they
are not substituted for final results. The full 233-view gate must run again
before the public beta. All eight curved-S supplementary views completed with
zero raw disagreements, including both long sections; no section performance
work was started. Section-file edits remain limited to phase accounting, routing
census, zero-kernel bypass and preserving the full-scene camera bounds.

Fresh same-adapter end-to-end pairs: M North 6.908/16.613 s and M SW
13.560/25.707 s (routing/native); M geometric-mean speedup 2.135x. L North
25.359/77.991 s, 3.075x. Curved S SW 18.937/19.039 s, 1.005x (native parity).
The four-view geometric-mean speedup is 1.938x. See `phase-timings.md` for the
complete before/after and fresh native-only phase tables and measurement limits.

Final verification: Debug and Release builds, zero warnings/errors; 208 Core,
40 UI and 167 licensed Rhino tests on **each** of .NET 8 and .NET 10 through
`Test-WalkingSkeleton.ps1` with `M2D_RHINO_TESTS=1`. The extra Core regression is
test-only; the four compiled producer hashes remain unchanged. No commits or
pushes were made. Artifacts are gitignored; no tools `obj` output is retained.
