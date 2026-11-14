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
linear at model tolerance. Mesh triangles are planar occluders; naked/unwelded crease edges and view-facing transitions of welded faces
are candidates. The A2 spike's all-crease census remains its default validation
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

All engines use the same parallel frame, larger view Z toward the viewer, and
model tolerance. HLD always receives IncludeHiddenCurves=true; hidden-off is a
downstream presentation filter. An entirely planar scene makes zero HLD calls.

## Contacts, coincident pieces and ownership

The A2 tie convention is closed projected polygon boundaries with a STRICT
view-depth gap greater than model tolerance. Equal-depth contacts do not occlude.
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
