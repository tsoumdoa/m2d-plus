# Unresolved reduction

Local Windows / Rhino 8.36.26296.17001 research, starting from the supplied main
worktree. No installation, commits, pushes or changes to external models. Evidence
is under the ignored `artifacts/unresolved-reduction/` directory. All 50 census
views and all 219 required light-gate views completed.

## Scope and measurement

The census contains 50 views: building S and M, each blocks and exploded, plus
curved S blocks and exploded, facade, steel, diagrid and space-frame. Every model
gets North, SW, Top, long section and plan. North is the existing negative-Y view;
sections use the perf-F harness's established bounds-centred long cut and
`minZ + min(1.2, height * .4)` plan cut. These are explicit harness views, not a
claim to have checked every named viewport in each model. The light gate uses its
own unchanged inventory and section definitions: 171 small-fixture views, eight
M views and 40 section-fixture views (219 actual manifest entries).

The diagnostic census retains Hidden curves, matching the visibility gate. A
separate **fixed-audit Hidden-off presentation subset** removes pieces whose
`SolvedClass` is Hidden, exactly as the presentation filter does. It is derived
from these same solves, not a new native solve with a differently consumed audit
budget. This distinction matters: restoring previously quarantined Hidden ink
improves the diagnostic census but is not extra Visible ink in the default UI.

Length is the sum of **projected drawing curve lengths**, including Visible,
Hidden, Unresolved, every Cut region boundary loop and any other emitted curve
class; points and hatch fill interiors contribute zero.
It is not the number of pieces or the world-space length of a foreshortened edge.
Lengths use `Curve.GetLength()` on the actual projected curves, not their display
tessellations. This is a sum over emitted curves; any unreconciled curved
contributor multiplicity is retained. Sources with no trustworthy output are
counted separately; their missing drawing length is not guessed.
Regions and their withheld outlines are counted separately from source/line
quarantine. Blocks and exploded models are separate observations, not deduplicated
population estimates. All models use document units (metres in these suites).

`before/` preserves the initial complete census and original compiled engine.
`baseline-bin/` archives that engine. `before-causes/` replays it with observational
cause capture; replay classed loci agree in all 50 cases. `final/` and
`after-fix-1-summary.json` preserve the first complete fix. `final-stable/`
contains the final drawings, native curve lengths, raw-record overlap cause ledgers,
and the independent all-body ray audit of restored confidence. Each receipt binds
the model, engine and harness hashes and the owned Windows Job Object execution.
No external model was modified or copied into this worktree.

The cause ledger partitions final Unresolved length: quarantine first, then
retained Unset records, native Duplicate overlaps, verifier-vetoed native support,
and missing native coverage/event gaps. At overlapping causes this is an explicit
attribution priority, not an assertion that only one fault contributed. Tiny
tolerance-scale pieces are also reported as a subset. Budget skips are an Info
coverage warning: they do **not** produce Unresolved length or prove visibility.
The tie convention is unchanged. Unsupported bodies and section refusals are
identified separately from native Unset propagation.

## Fix 1: retain local Unset evidence and partition footprint quarantine

A valid nonzero Unset proxy previously quarantined every emitted piece with the
same source identity. The adapter now detaches that actual world curve before HLD
disposal, retains it as Unresolved, and bounds its uncertainty by its projected
footprint. It does not change the registered occluder scene or re-query a reduced
scene. Invalid proxies and missing/unbounded attribution keep the existing source
or whole-view quarantine.

The native and route-C adapters split affected curves using planes around the
uncertain projected boxes. These plane events are only partitioning aids. A
subcurve retains Hidden **only if its entire conservative bounding box is
disjoint from every uncertain footprint, with model tolerance**. A missing
or approximate event can therefore only withhold too much. The actual Unset
proxy and all overlapping intervals stay Unresolved. Points in the footprint
remain withheld. Region topology is atomic: an affected region/Hatch stays
withheld, rather than constructing an uncertified partial region.

Visible needs an additional continuous certificate. The candidate's complete
world box and every owned body's world box are transformed by all eight corners
into the view frame and expanded for roundoff. Every body, including the source,
must be XY-disjoint or have maximum forward gap at most half the model tolerance
throughout the candidate. Otherwise Visible remains Unresolved, even outside the
Unset footprint. Untrimmed original bounds are allowed as conservative supersets
in route C. This can refuse useful spans, but cannot discard a possible occluder.

This guard is necessary: the rejected footprint-only candidate's initial audit
flagged 265/14 wrong-Visible sample witnesses in curved-S North/SW, including
newly exposed native errors. Those receipts and drawings are preserved under
`rejected-native-confidence/`. They are not final results. A later audit also
found two unchanged baseline Visible records per view at already-Unresolved
loci; the final audit excludes geometrically identical baseline Visible pieces
and checks newly restored confidence separately. The initial audit receipts are
preserved in `continuous-proof-audit-initial/`.

The first complete fix reduces total Unresolved length from 1885.551999 to
1054.363194 units (44.08%). It emits no newly Visible suite spans: all remaining
native Visible restoration requests refuse the continuous certificate. Reductions
come from retaining Hidden outside the uncertain footprints and from independent
section output. Its region-withholding bucket nevertheless grows from 387.188798
to 423.982277 units because candidate boxes reach other certified regions; fix 2
addresses that separate evidence error.

Composition can carry a detached, attributable Unset curve through its existing
exact planar event/occlusion union. A complete planar Hidden interval can decide
the union; a planar clear sample cannot promote an uncertain curved interval to
Visible. Native-only curve classifications remain the raw comparator. No runtime, scheduler,
admission threshold or worker codec contract changes.

Tests cover the length/domain partition, conservative complete-bound exclusion,
curved footprints, detached depth support, preserved source/profile/kernel/solved
class metadata, unbounded fallbacks, point withholding and cancellation.

The light gate also exposed a real M long-section failure: native Trim returned
null for a tiny nonzero partition cell. Partition construction is now atomic.
On any refused cell, all provisional pieces are disposed and the complete original
curve is retained Unresolved. No interval is lost or promoted and the view does
not fail. A failure-injection regression refuses the second trim, after a first
successful piece, and checks complete rollback, domain and length conservation.

## Fix 2: preserve independent section certificates

SectionDrawing now snapshots section-source and occluder uncertainty immediately
after physical section/trim preparation. Only that evidence, an excluded foreground
occluder, or whole-view quarantine can withhold a certified region/Hatch. Later
native candidate Unset proxies and ray-verifier curve vetoes do not change a
physical material intersection or its loop topology. All source geometry still
participates in visibility; this is not a reduced-scene inference.

This applies equally to composed and native-only adapters: HLD supplies curve
visibility, while the existing route-C section pipeline supplies their common
regions. Raw native curve classifications are unchanged. Genuine section
certificate refusals, failed trims, unsupported occluders and affected regions
remain quarantined. No region is split into an uncertified partial fill.

New licensed regressions use the frozen hollow mullion and diagrid normal stations
and independently known analytic areas, with one hole and aligned hatch. They
check both the native candidate route and the composed route forced by an unrelated
non-occluding line; that routing choice must not alter the physical section.

## Fix 3: whole-span rational duplicate witness certificate

The retained-witness check previously required matching NURBS degree, knots and
proportional weights (apart from its existing straight-support certificate).
The fallback certificate now inserts the common knot cuts without changing
parameterisation, converts nonempty spans to positive-weight rational Beziers,
and bounds `QW - PV` over the complete parameter interval. Bernstein product
coefficients and outward-rounded arithmetic bound XY separation and the witness's
depth difference; positive lower bounds on `WV` bound the rational quotient.

Every span must pass. Degree > 12, more than 256 cuts, nonpositive weights,
failed conversion, nonfinite arithmetic, incompatible parameterisation, behind
interiors and excessive projected separation all refuse the certificate.
Endpoint agreement and sampling never authorize it. Existing projected overlap
tests still establish actual coverage, and the witness must be an independently
retained confident candidate. Certifying duplicate ownership suppresses only that
duplicate; source/depth metadata still come from actual contributors.

The tilted-arc regression uses a retained front witness with a different rational
basis and overlapping depth envelopes. It changes a refused duplicate owner to
complete certified ownership. Adversarial tests include a behind interior with
matching endpoints, a behind whole curve, a translated projected support, and
degree/knot changes. The control changes one unresolved duplicate owner to zero;
all its overlap is certified. Suite measurements below state any actual benefit separately
from this controlled certificate improvement.

## Verification and remaining limits

Final Debug and Release solution builds pass with 0 warnings / 0 errors. The
complete final `Test-WalkingSkeleton.ps1` run passes on each of .NET 8.0.34 and
.NET 10.0.15: 287 Core, 56 UI-model and 227 licensed Rhino tests, no skips. The forced local
hook passes, and the ten Python harness tests pass. An earlier worker test run
hit its 45-second startup/handshake timeout; the complete final rerun passes.
The first publication gate attempt exposed a stale harness call to `Emit`
after the output-tree contract added captured source groups. The probe now passes
those host-captured groups, with no component/UI implementation changes.

Both fully planar 17°/45° controls make zero HLD calls and have byte-identical
complete drawing JSON before/final (including lengths, classes, provenance and
diagnostics). Their SHA-256 values with this census dump schema are respectively
`42c85b79cadf5e393af995201801c94391d470ced251e53b5d2067e5cecaf8ef` and
`df7b98e7969d9a720851732fed56333c9835af7e7704907bb35802df5ec0a3ac`.

The restored-confidence audit checks samples that were on baseline Unresolved
support and are now Visible. It visits every registered owned body in native
fallbacks, or every registered curved body in composition, for each central ray
and four tolerance-offset rays. Composition's planar union has the separate
independent all-pairs gate. The ray audit includes self-occlusion and full world-box scalar
exclusions, and has no sample/intersection budget. It does not use production
spatial indices or native visibility labels. Five definite hidden rays against
the same body are a wrong-Visible witness; contact/intersection margins remain
inconclusive. Zero sampled contradictions is not a continuous freeform proof.
The continuous proofs for narrowing quarantine are complete-bound disjointness
and, for restored Visible, the all-body depth/XY exclusion certificate;
native confidence elsewhere retains its stated freeform validation limits.

All census/gate hosts share the local model-suite mutex and run serially. The
owner explicitly authorized an exception for licensed `WorkerTests`, which
deliberately start multiple worker hosts; those tests run while the census/gate
mutex is held. Only owned Job Objects/processes are controlled. The owner's Rhino
process is never attached to or changed.

### Final light gate

`routing-stable/` contains the final producer run. Every required case has an
owned-process receipt with exit code 0; there are no native/kernel execution
errors. `light-gate/merge-gate.json` reports **PASS: 219/219 complete, zero known
bugs, zero evidenced wrong-Visible, no missing cases**. Its SHA-256 is
`6582e2fe5569a0832550184db5bbe59676036096c1aff46595792cc5fa2c11b6`.

| Final verification status | Views |
|---|---:|
| Strict whole-output comparison passed | 188 |
| Independent complete planar agreement | 8 |
| Explicit Unresolved/Info comparison | 23 |
| Required total | 219 |

The raw producer retains 150 potential wrong-Visible sample flags in nine cases.
Fresh hash-bound witnesses explain all 150 as tolerance artefacts: 18 planar
interval/interior-point witnesses, and 132 mixed witnesses (50 M blocks SW, 82 M
exploded SW). Each mixed witness has independent all-pairs planar support plus
central/four-offset rays against **all 210 registered curved bodies**: 52,500 and
86,100 body/ray pairs respectively, all clear. This decides those disputed sample
locations within drawing tolerance; it does not certify unsampled mixed spans.
Raw rows, failure points and exact witness streams are preserved unchanged.

The strict comparison still contains 31,295 unclassified discrepancies. It also
records 202,157 exact-check decisions, 18,883 tie-convention explanations and the
150 tolerance artefacts. The 23 remaining Info cases include all eight M views,
the freeform facade/steel/diagrid views and `region_chs` long section; they are
not promoted to certified output. The light gate is the requested zero-known-bug
gate, not a claim that every freeform output span has an exact certificate.

All gate rows bind one compiled producer: engine
`bc3641e53e8547f922f8a978abb0b6fc99420533707ca56b29def9012a0d5820`, Core
`f9fbf5a714b9f0dfc0e448ab2805dfb58eadda684e695a19707d758540547ced`, harness
`4bd3eab3cbd6f79b33803edf589bf0d77b49f73f50a551761f0175f2429273f8`.
No newly Visible suite span contradicts an independent oracle; as stated below,
there are no newly Visible suite spans in this final conservative implementation.

The L and curved-S supplemental gate inventory was not run. The verifier's
historical `full233GatePassed`/233-view text is retained by the existing tools;
the current enumerated full inventory is 235 views. Neither full-beta coverage
nor a full-beta pass is claimed here. Counts above use the actual required
manifest, not that historical label.

Reproduction (use the supplied compatible resolver):

```powershell
dotnet build M2DPlus.sln -c Debug
dotnet build M2DPlus.sln -c Release
dotnet build tools/perf-f -c Release
python -B tools/perf-f/uncertainty-census.py --resolver <resolver> --output artifacts/unresolved-reduction/before
# Archive the original engine before rebuilding the changed tree.
python -B tools/perf-f/uncertainty-census.py --resolver <resolver> --output artifacts/unresolved-reduction/final-stable --baseline artifacts/unresolved-reduction/before
./tools/Test-WalkingSkeleton.ps1 -Resolver <resolver>
dotnet build tools/hc-perf -c Release
dotnet build tools/routing-audit -c Release
python -B tools/perf-f/summarize-uncertainty.py artifacts/unresolved-reduction --final-folder final-stable
python -B tools/hc-perf/composition.py --required-only --publication --serial-mutex --watchdog-seconds 7200 --resolver <resolver> --output artifacts/unresolved-reduction/routing-stable
python -B tools/hc-perf/routing_reference.py artifacts/unresolved-reduction/routing-stable
python -B tools/hc-perf/certify_intervals.py artifacts/unresolved-reduction/routing-stable
python -B tools/hc-perf/certify_mixed_intervals.py artifacts/unresolved-reduction/routing-stable --resolver <resolver>
python -B tools/hc-perf/routing_light_gate.py --inventory artifacts/unresolved-reduction/routing-stable/manifest.json --output artifacts/unresolved-reduction/light-gate artifacts/unresolved-reduction/routing-stable
```

## Census results

All 50 views complete before and after. Total diagnostic Unresolved length falls
from **1885.551999 to 894.982468 units (52.53% lower)**. Weighted share of all
emitted curve output, including Cut boundaries, changes from **1.4907% to
0.7075%**. Total curve length is 126486.252488 → 126504.779207; the small increase is
retained real Unset geometry that the old adapter omitted, not invented detail.

The fixed-audit Hidden-off subset falls only from **780.982027 to 759.399751 units
(2.76% lower)**, or 1.5787% → 1.5345% of that subset's
curve output. Most of the larger diagnostic improvement is Hidden classification,
not new Visible output. This is a material limitation for default use.

Whole-source quarantine counts (view/source pairs, including section refusals)
fall from 290 to 106; whole-view quarantine remains 0 → 0. Certified regions
increase from 1907 to 1992. These totals retain the separate block/exploded cases.

The following counts use explicit diagnostic source identities, including sources
with no emitted ink. An affected view has at least one whole-source quarantine
of that cause; it is not a whole-view quarantine. Footprint/region effects on
emitted ink are included in the length table below.

| Whole-source quarantine cause | Affected views before → after | View/source pairs before → after |
|---|---:|---:|
| Native Unset | 12 → 0 | 184 → 0 |
| Section-certificate refusal | 2 → 2 | 82 → 82 |
| Unsupported section/source | 5 → 5 | 24 → 24 |

Fix 2 reduces region withholding from 423.982277 after fix 1 to 264.601550 units,
and total Unresolved from 1054.363194 to 894.982468. The rational duplicate
certificate changes its controlled regression from one refused owner to none,
but makes **no measurable length reduction in these 50 suite views**.

There are **no newly Visible suite spans**. The new-confidence ray audit therefore
has zero required samples/body-ray pairs; its zero contradictions must not be
presented as sampled certification of unchanged native curves. Continuous
all-body exclusion is the production guard, and every suite Visible restoration request
lacking that proof remains Unresolved. North/SW changed drawings repeat
byte-for-byte, and all 50 baseline cause replays match the original classed loci.

### By cause

Lengths below are a disjoint attribution of emitted Unresolved curves. The final
Native Unset propagation row includes source Visible spans refused by the
continuous certificate. Other-footprint quarantine is conservative propagation
around retained native Unset footprints onto nearby contributors. Newly retained
Unset intervals remain explicit Unresolved geometry. No sample-only upgrades,
source drops, tolerance changes or tie-rule changes are used to reduce a row.

| Cause (ranked by baseline affected length) | Before | After fix 1 | Final |
|---|---:|---:|---:|
| native Unset propagation / continuous-proof refusal | 1180.111793 | 258.014688 | 258.014688 |
| region withholding | 387.188798 | 423.982277 | 264.601550 |
| unsupported-source footprint quarantine | 142.959341 | 119.155519 | 119.155519 |
| section-certificate footprint quarantine | 105.342667 | 85.783089 | 85.783089 |
| native duplicate attribution | 47.882080 | 47.882080 | 47.882080 |
| verifier contradiction | 22.067320 | 22.063969 | 22.063969 |
| native coverage/event gap | 0.000000 | 0.000000 | 0.000000 |
| other footprint quarantine | 0.000000 | 78.951504 | 78.951504 |
| native Unset interval | 0.000000 | 18.530069 | 18.530069 |
| Unsupported-source whole-view quarantine | 0 | 0 | 0 |
| Verifier budget skips (Unresolved length) | 0 | 0 | 0 |
| Tie rule (Unresolved length) | 0 | 0 | 0 |

Tolerance-scale Unresolved pieces (length ≤ 2× model tolerance, a subset of the
rows above) total 0.010439 → 0.120971 units: retaining tiny real Unset proxies
increases this count honestly. The residual native-coverage/event gap is about
4.4×10⁻¹⁴ units, not a meaningful length contributor. Verifier budget skips remain
322310 before and after; their unchecked native confidence is reported in Info,
not treated as a continuous visibility proof. Region/certificate refusals and
unsupported-source footprints remain separate causes. There is no threshold
relaxation: the remaining genuine refusals still lack the required proof.

### Every view

The share column includes Cut region boundaries and uses Hidden-on diagnostic
output. `summary.json` also contains the fixed-audit Hidden-off subset per view,
raw receipt/drawing hashes, source counts and the complete cause ledgers.

| Model / view | Unresolved length before → after | Share before → after | Whole-source quarantines before → after |
|---|---:|---:|---:|
| curved-facade__long | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 5 → 5 |
| curved-facade__North | 13.980478 → 7.806284 | 4.4371% → 2.4772% | 1 → 0 |
| curved-facade__plan | 82.687997 → 81.142770 | 60.6437% → 59.5104% | 5 → 5 |
| curved-facade__SW | 18.754068 → 5.941444 | 6.1013% → 1.9329% | 2 → 0 |
| curved-facade__Top | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
| curved-perf-S-blocks__long | 190.283405 → 50.484045 | 8.7225% → 2.3134% | 16 → 0 |
| curved-perf-S-blocks__North | 175.308106 → 37.904442 | 5.2317% → 1.1310% | 14 → 0 |
| curved-perf-S-blocks__plan | 6.410748 → 6.410748 | 0.5116% → 0.5116% | 0 → 0 |
| curved-perf-S-blocks__SW | 82.017740 → 34.179720 | 1.8435% → 0.7682% | 8 → 0 |
| curved-perf-S-blocks__Top | 0.070436 → 0.070436 | 0.0024% → 0.0024% | 0 → 0 |
| curved-perf-S-exploded__long | 203.756168 → 57.001164 | 9.3381% → 2.6113% | 18 → 0 |
| curved-perf-S-exploded__North | 175.148772 → 30.701964 | 5.2181% → 0.9145% | 14 → 0 |
| curved-perf-S-exploded__plan | 3.466491 → 3.466491 | 0.2769% → 0.2769% | 0 → 0 |
| curved-perf-S-exploded__SW | 59.825235 → 24.729725 | 1.3447% → 0.5559% | 6 → 0 |
| curved-perf-S-exploded__Top | 0.050317 → 0.050317 | 0.0017% → 0.0017% | 0 → 0 |
| curved-steel__long | 0.226251 → 0.226251 | 0.2157% → 0.2157% | 0 → 0 |
| curved-steel__North | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
| curved-steel__plan | 1.033760 → 1.033760 | 0.7597% → 0.7597% | 0 → 0 |
| curved-steel__SW | 0.993972 → 0.993972 | 0.4652% → 0.4652% | 0 → 0 |
| curved-steel__Top | 1.413102 → 1.413102 | 0.7165% → 0.7165% | 0 → 0 |
| diagrid-shell__long | 133.836275 → 60.624098 | 85.4796% → 38.5203% | 10 → 4 |
| diagrid-shell__North | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
| diagrid-shell__plan | 53.449367 → 53.435863 | 92.2985% → 92.2752% | 2 → 2 |
| diagrid-shell__SW | 21.303763 → 8.725850 | 8.3959% → 3.4389% | 1 → 0 |
| diagrid-shell__Top | 2.406899 → 2.406899 | 0.7801% → 0.7801% | 0 → 0 |
| perf-M-blocks__long | 0.126068 → 0.126068 | 0.0023% → 0.0023% | 0 → 0 |
| perf-M-blocks__North | 1.008786 → 1.008786 | 0.0164% → 0.0164% | 0 → 0 |
| perf-M-blocks__plan | 0.000130 → 0.000130 | 0.0000% → 0.0000% | 0 → 0 |
| perf-M-blocks__SW | 19.833350 → 19.833350 | 0.0852% → 0.0852% | 0 → 0 |
| perf-M-blocks__Top | 0.000141 → 0.000141 | 0.0000% → 0.0000% | 0 → 0 |
| perf-M-exploded__long | 0.126048 → 0.126048 | 0.0023% → 0.0023% | 0 → 0 |
| perf-M-exploded__North | 1.008590 → 1.008590 | 0.0164% → 0.0164% | 0 → 0 |
| perf-M-exploded__plan | 0.000207 → 0.000207 | 0.0000% → 0.0000% | 0 → 0 |
| perf-M-exploded__SW | 19.833510 → 19.833510 | 0.0852% → 0.0852% | 0 → 0 |
| perf-M-exploded__Top | 0.000125 → 0.000125 | 0.0000% → 0.0000% | 0 → 0 |
| perf-S-blocks__long | 0.252049 → 0.252049 | 0.0251% → 0.0251% | 0 → 0 |
| perf-S-blocks__North | 0.516747 → 0.516747 | 0.0466% → 0.0466% | 0 → 0 |
| perf-S-blocks__plan | 0.000058 → 0.000058 | 0.0000% → 0.0000% | 0 → 0 |
| perf-S-blocks__SW | 2.216100 → 2.216100 | 0.0575% → 0.0575% | 0 → 0 |
| perf-S-blocks__Top | 0.000090 → 0.000090 | 0.0000% → 0.0000% | 0 → 0 |
| perf-S-exploded__long | 0.252000 → 0.252000 | 0.0251% → 0.0251% | 0 → 0 |
| perf-S-exploded__North | 0.504313 → 0.504313 | 0.0455% → 0.0455% | 0 → 0 |
| perf-S-exploded__plan | 0.000071 → 0.000071 | 0.0000% → 0.0000% | 0 → 0 |
| perf-S-exploded__SW | 2.290415 → 2.290415 | 0.0595% → 0.0595% | 0 → 0 |
| perf-S-exploded__Top | 0.000063 → 0.000063 | 0.0000% → 0.0000% | 0 → 0 |
| space-frame__long | 223.995307 → 211.383268 | 98.5143% → 92.7295% | 96 → 84 |
| space-frame__North | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
| space-frame__plan | 387.164482 → 166.880993 | 57.1015% → 24.1083% | 92 → 6 |
| space-frame__SW | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
| space-frame__Top | 0.000000 → 0.000000 | 0.0000% → 0.0000% | 0 → 0 |
