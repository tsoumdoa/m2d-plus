# Visibility engine composition

This contract supersedes PLAN's whole-scene-only S4 restriction. A2 (PR #14)
certifies the planar kernel, not mixed composition. Mixed routing has its own
whole-output gate. No timing mutex is used for native correctness checks.

**Integration status:** the first complete mixed-routing gate failed. See
[the validation report](engine-composition-validation.md); this contract does not
certify the working implementation or waive its remaining acceptance work.

## Candidates and occluders

Routing consumes owned, normalised world geometry after glass substitution and,
for route C, physical near/far trimming and capping. It never resolves documents.
Each registration retains its original source identity, path, item, section style
and far-candidate flag. Multiple clipped pieces may belong to one original source.
An object is planar-polyhedral only when every face is planar and every edge is
linear at model tolerance. Manifold meshes with planar faces and explicit unwelded creases use triangle
occluders and topology-edge candidates. Nonmanifold meshes, nonplanar quads and
welded nonplanar meshes remain complete native registrations: Rhino
interpolates smooth silhouette loci that are not the topology-edge facing
transitions. Silently triangulating a nonplanar quad can also change native occlusion.
Substituting those edges lost curved-facade loci; the continuation
regression keeps native candidate generation and occlusion together for this class. The A2 spike's all-crease census remains its default validation
policy; production explicitly requests the native mesh candidate policy. Linear curves and points are non-occluding kernel candidates.
Other curves and bodies require native candidate generation.

For a candidate c, H(c) = union over ALL participating occluders of H(c,o).
Candidate ownership is independent of occluder participation:

* Kernel candidates retain their own edge index and world endpoints. The kernel
  supplies planar-hidden intervals. Complete planar Hidden coverage decides the
  union without a curved query; those non-occluding candidates are omitted from
  HLD. Curved bodies plus only projection-overlapping
  kernel edges (as non-occluding curves) supply curved-hidden intervals. Every
  submitted edge must have complete native coverage; missing/ambiguous native
  attribution is Unresolved, never an implicit visible remainder.
* Native owns curved edges, seams and silhouettes and self-occlusion. Retain their
  unflattened world curves before disposing HLD. Split them at every projected
  polygon-boundary plane event AND every face depth-gap plane event, including
  overlap endpoints. This uses curve/plane intersection, never polyline sampling.
  On each event interval, the kernel's planar point predicate supplies the
  planar-hidden decision. Intersection failure or uncertifiable event intervals
  are Unresolved. Native Hidden is never made Visible by a planar test.
* Points use the same union. Hidden points are withheld. Uncertain points are
  withheld and counted/reported; they are never published confidently.

Native camera placement uses the same full-scene bounds in both adapters, including
route C; clipped-input bounds must not change the native camera.
All engines use the same parallel frame, larger view Z toward the viewer, and
model tolerance. HLD always receives IncludeHiddenCurves=true; hidden-off is a
downstream presentation filter. A scene consisting entirely of kernel-eligible planar sources makes zero HLD calls.

## Contacts, coincident pieces and ownership

Native Visible/Hidden curve records also receive an independent, deterministic
ray audit against owned curved Breps/meshes. In composed views the exact planar
union below the audit supplies planar occlusion; a planar hit is a proof, not a
contradiction of an HLD solve that intentionally omitted planar bodies. Native
fallbacks audit the full owned scene, extracting exact planar faces as needed.
The audit includes source self-occlusion. It uses finite curve/Brep intersections
and mesh rays, never native HLD classifications to decide a ray. Definite curved
decisions require agreement of the central ray and four tolerance-offset rays;
failures, grazing intersections and margin cases are inconclusive.

Samples include ends and arc-length-spaced interior points (at least four cells,
spacing bounded by 1/128 of the view diagonal, with an 8-tolerance floor). A
contradiction splits the native piece at neighbouring sample-cell boundaries and
moves that neighbourhood to Unresolved. Sampling never flips confidence or proves
a whole interval. The later exact planar union may still prove a span Hidden.
Info records each contradiction and aggregate coverage. Unchecked interiors,
budget skips and inconclusive rays retain an explicit **unverified native** Info
verdict; this is not certification of general freeform output.

`NativeSettings.Verification` configures fixed per-view sample and intersection
caps (defaults 16384 samples / 4096 individual curved ray tests). A zero sample
budget is an explicit disabled audit with an Info diagnostic. Native-only remains
the raw comparison adapter. The cap bounds work rather than wall time: using
elapsed native time to stop at 10% would make classification depend on machine
load. Timing is reported separately in phases, never deterministic diagnostics.
Fully planar scenes never instantiate the verifier and preserve their output.

The owner-sanctioned `tieConvention` is VISIBLE for zero-depth-gap contact,
including flush panel joints and coincident blocks. Closed projected polygon
boundaries occlude only with a STRICT view-depth gap greater than model tolerance.
A contact whose gap is within model tolerance does not occlude. A native Hidden
sample may be classified `tieConvention` only with independently checked visible
coverage and recorded supporting faces and gap values.
Holes use even/odd parity across all loops. Curved-native contacts retain native
visibility; native Unset/non-attributable Duplicate records are uncertainty unless
an independently retained coincident candidate proves complete coverage.

At the drawing locus, Unresolved takes precedence over confidence; Visible takes
precedence over Hidden, matching A2. Coincidence is established over actual overlap
intervals, not matching endpoints or source bounding boxes. Keep world depth and
source metadata from an actual contributing candidate; choose a deterministic
source among equal-class coincident contributors. Never assign a whole unioned
support to an unrelated source or interpolate its depth from another candidate.
Native Duplicate does not by itself prove that a different engine retained the
owner: preserve/reconcile native duplicate geometry or mark its locus Unresolved.

Cut, Far clip, crop, regions/hatches, Beyond and fade remain downstream. Generated
cap/trim edges retain source provenance. Cut-over-FarClip precedence and generated
far-boundary suppression apply after visibility exactly as in route C.

## Lifetime, cancellation and evidence

The scene lease and route-C clip owner outlive both engines. Native proxies never
escape HLD lifetime; results own detached projected AND world-depth geometry.
Managed extraction, index construction, pair/event work and output reconciliation
check cancellation. HLD retains the process-global scheduler and native lifetime
rules. Complete current-generation batch publication remains unchanged.

Info records route, candidate counts, kernel/native participation and native calls
per view. Native-only remains an explicit validation adapter. Gate comparisons
cover complete Visible/Hidden/Unresolved output in both directions (including
curved loci and points), and section outputs separately. Every disagreement needs
geometric evidence for native quirk, tie convention or tolerance artefact; bugs
and unexplained disagreements fail the gate. Archived native timings are context
only, with absent rows left absent. End-to-end measurements include extraction,
native work, composition and downstream section/presentation work.

When the native reference is Unresolved/Unset, the owner-sanctioned independent
exact all-pairs check decides the comparison. It visits every planar face for
each disputed candidate bound and uses rational polygon/depth predicates at the
comparison samples. Scalar bounds may amortise rejection across samples of one
candidate; no production spatial index, clipping predicate or interval union
decides the reference. Curved occlusion participates only through definite native
query/candidate intervals on the same world support, or exclusion by every curved
occluder's conservative bound. A planar Hidden proof decides the occluder union;
Visible requires a definite curved decision too. Inconclusive checks remain strict.
Evidence records candidate identity, face IDs, signed gaps, curved-native witnesses
and complete pair counts. These samples are counted as `exactCheckDecided`, separate
from `tieConvention`, native-order quirks and remaining unexplained samples.


## Native order control (perf-E evidence)

Native insertion order is semantic on Rhino 8.36. Production and the canonical
native-only comparator use M1b's path/ordinal-identity order. Generated pieces of
one source are ordered by geometry SHA-256 and far-candidate flag; eligible and
curved groups must never become two separate insertion-order groups. Shared
native registrations therefore retain their canonical relative order.

When only native-owned disagreements remain, an audit reverses the **same full
scene**, retaining every occluder. Earlier diagnostic controls also covered
native-owned subsets of otherwise failing views; their positive witnesses remain
valid, without excusing any kernel-owned contributor. That
control is explicit, native-only, serial and correctness-only. Its complete input
geometry/source signature must equal the reversal of the canonical signature.
Only discrepancies at exclusively native-owned candidate loci that reproduce
under that full-scene permutation may receive `native-order-quirk`; the control
output hash and sample/XY/classes are retained. A coincident kernel-owned
contributor disqualifies the exception. Removing planar occluders can legitimately
change visibility and is not, by itself, a native-subset exception. Kernel-owned
spans and all non-reproducing differences remain unexplained until independently
accounted for. Zero unexplained remains mandatory.
The veto indexes the original projected kernel candidates as well as published
pieces: locus reconciliation may remove a lower-class contributor without erasing
its participation in the routing decision.

## Temporary branch merge gate

The owner's final branch decision permits merge with zero known bugs and zero
evidenced wrong-Visible samples across all small fixtures and M views. Comparisons
without a certificate retain an Unresolved/Info audit verdict; they are not
certified Visible. Large-model gaps are explicitly non-blocking for this branch
and listed in `hillclimb/routing/HILLCLIMB.md`. This does not relax the full
233-view gate required again before public beta, nor change production visibility
semantics. Raw whole-output results remain separate from the branch merge verdict.
