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
model tolerance. In the default `engine = exact`, HLD receives IncludeHiddenCurves=true
and serial computation; hidden-off is a downstream presentation filter. Owner decision
2026-11-16 adds opt-in `engine = fast`: only native calls enable multithreading and follow
the view's hidden-generation flag. The planar and analytic kernels, composition proofs,
quarantine rules and verifier budgets do not change. Residual Hidden fragments returned
by native are still audited; omitted hidden curves cannot receive false-Hidden checks,
and Info states that limitation. Fast native output is unverified and can differ between
runs. A scene consisting entirely of kernel-eligible planar sources still makes zero
HLD calls and retains the same geometry/classes, including hidden evidence for min-visible
presentation. Engine always keys visibility; hidden additionally keys it in fast mode.

## Exact analytic solids (third class)

`AnalyticVisibility` recognises a whole owned body as a sphere, a cylinder between two planes (solid or hollow)
or an extruded arc (one cylindrical face between two rulings and two arcs). Recognition is strict and fails closed:
every face on the expected surfaces at tolerance, only rims may be creases, every rim edge within tolerance of the
exact rim, the caps must not meet inside the cylinder, and the volume (or area) must equal the closed form within
1e-4. Directions are compared as distances over the body's extent, not as angles. Info names each refusal reason and,
for a recognised solid beside a body that is not exact (or kept on native HLD), that body. It runs on the normalised world
geometry, so placements, mirrors and scales need no special case: a squashed or sheared instance is no longer one
of the shapes and is refused. Clipped route-C pieces and glass substitutes are recognised the same way, or not.
`tangents = on` or `seams = on` turns the class off for the view (native then draws lines the class does not make).

H(c) stays a union over all occluders. A recognised source S leaves HLD only when nothing is lost on either side:

* **As an occluder.** S hides kernel edges (closed-form intervals on the straight edge, applied to the kernel result
  before anything else reads it), native curves (cut on chords with a half-tolerance sideways margin, because a
  native curve is only known to the native tolerance), points, and the candidates of other exact solids. Nothing in
  HLD needs S.
* **As a candidate.** S's own lines are its rims and its closed-form outlines. They are hidden by planar faces
  (the existing curve/plane events and point predicate), by every recognised solid including S itself (exact), and
  by native bodies, which only HLD knows. An unrecognised body *may* hide S when its view-frame box overlaps S's in
  the drawing and reaches in front of S's far side.
  By default S then stays in HLD whole, as before; its exact solid is still used against the candidates of sources
  that did leave.
* **The mixed route (`NativeSettings.AnalyticMixed`, off by default, no user setting).** S leaves all the same and
  its lines are also given to the view's one HLD call as curves of the same source. A curve hides nothing, so HLD's answer for them is about the bodies still
  in HLD alone. The answers are united per stretch: Hidden where HLD, an exact solid or a planar face says Hidden;
  not drawn where HLD reports the stretch as a duplicate under another native line in front of it (that line is
  drawn); otherwise Visible only where every side says Visible. A stretch HLD returns nothing for, or that either
  side cannot decide, is Unresolved. HLD's pieces are placed on the candidate by closest point (ends and middle,
  within four tolerances); a piece that cannot be placed counts as no answer. The native ray audit and Unset
  handling apply to these curves as to any native curve. In `engine = fast` native hidden curves can be off, so
  there S stays in HLD whole. It is off by default because it was measured to change which lines native HLD gets
  wrong among the bodies that stay (curved S, SW: new wrong-Visible samples and four times the Unresolved length,
  for 15 % less time); see `hillclimb/curved-engine/FINDINGS.md`, section 9.

A scene of planar and exact sources makes zero HLD calls. While any source has left, native Duplicate records are
never dropped unseen (they go through duplicate resolution, as with planar faces), and the locus reconciliation runs.

Conventions. In depth the tie rule holds: cover must extend more than a tolerance in front of the point; the
decision has a window (tolerance/16 to tolerance/8 of cover beyond the tolerance) inside which the result is
Unresolved. Sideways the solids are closed, like polygons in the planar kernel: a ray along an outline, in the plane
of a cap seen edge-on or along a sheet's edge is covered, so nothing shows through the joint of two stacked members.
Bores are open. An undecided run shorter than one tolerance between decided neighbours of different classes is a
change point known to that accuracy and is shared between them (four tolerances on native curves). Between neighbours
of one class it is not a change point: on a native curve it stays Unresolved, because behind a sliver narrower than
the half-tolerance margin the whole hidden stretch is undecided; on an exact candidate it is a tie of at most a
tolerance and goes to the neighbour. A Hidden curve that lies exactly under
a Visible, Unresolved or earlier Hidden curve of this class is not drawn twice. Coordinates binary64 cannot resolve
to tolerance/64 give Unresolved.

A candidate of one exact solid often lies exactly on the boundary of another (the rims of stacked columns in plan, a
mitre joint). Its chords are therefore refined until the parabola through each chord's ends and middle is within an
eighth of the graze width (tolerance/2^16) of the curve, so the whole curve is seen on that boundary and gets one
class, not one class per chord. A straight candidate seen end-on has no drawing and is not solved.

### Cut tubes (stage 2)

A body that is none of the plain shapes is tried as a **cut tube** (`CutTubeRecognition`): a hollow or solid round
section whose ends, and wall, are cut by planes and by other cylinders (coped and saddle ends, mitres, Boolean pipe
nodes). Every face must be a plane or a circular cylinder; the body is then a union of cells of the arrangement of
those surfaces inside the tube's wall. The exact solid is `AnalyticCsg` in Core: the tube (inside the outer cylinder,
outside the bore, between two virtual end planes) and, for each cutting surface, one bit of a cell number. Cells are
labelled solid or void by asking Rhino's point-in-solid test at points clear of every surface; a cell never reached,
or where weak answers disagree, is *unknown*, and a ray that may pass through an unknown cell is Unresolved. Two
strong answers that disagree in one cell refuse the body. Derived planes (mitre planes, planes across the tube at
each cutter, six sector planes) split cells that would otherwise hold both material and void.

Faces need not be exact. A face that is a NURBS surface is fitted to the nearest plane or circular cylinder and its
distance from that surface is **bounded, not sampled**: the face is refined by knot insertion and the bound is taken
from the convex hulls of its control points, so it holds for every point of the face. Edges are bounded against their
two surfaces the same way. The body is accepted only if every bound is under min(256 tolerances, a sixteenth of the
wall or radius), and if no edge reaches beyond the tube's outer cylinder.

What the real body may differ from the exact one by is carried, not assumed away:

* **Each surface has a slack**: its faces' bound, or, if larger, the distance of the edges on it from their surfaces
  plus their stored edge and vertex tolerances (a real face runs on to the stored edge, not to the exact
  intersection). The certified test shrinks sure stretches and grows possible ones by it, so a ray that passes within
  the slack of a surface is Unresolved unless both answers agree.
* **Each drawn line has a gap**: stored edges are drawn as stored, and the exact intersection of their two surfaces is
  what is classified; outlines are Rhino's silhouette curves, and the exact ruling is what is classified. The gap
  between the two (across the view and along it) is the candidate's lateral radius and depth range, so a decision is
  one that holds for the drawn line too. A point that does not snap onto its exact curve is Unresolved. For an outline
  the gap is bounded through the curve's control points (it is linear in the point); for an edge it is measured at 65
  points and taken a quarter larger.
* **Change points** against such bodies are known to a few times that: an undecided run up to eight times (gap +
  largest slack) between decided neighbours of different classes is shared between them; anything else stays
  Unresolved.
* **Contact.** Surfaces of two bodies that agree to within their slacks are one surface: a line on it is in contact
  with the other body, and contact is not cover. The exact rim where two mitred cutters meet is also on their mitre
  plane by construction.
* **Coincident lines.** A Hidden piece under another piece of this class is not drawn twice, and neither is an
  Unresolved piece that lies under a Visible piece of the same source no nearer the viewer than it: that source draws
  a Visible line there whichever class it has (the far edge of a cope seen square-on). Two drawn curves are one locus
  when they agree to half a tolerance plus their gaps.

Recognition is **evidence, not proof**: cells are labelled from Rhino's point-in-solid answers at sample points, the
both-sides check of a face is skipped where every point of it is within two margins of another surface (a sliver that
hugs it; counted and reported), and the volume check is 1 %. Info says so and gives, for each cut tube, its face
bound, edge bound and largest surface slack. At most `NativeSettings.CutTubeBudget` (128) possible cut tubes are
examined per view, the first in input order whatever is cached, so that the drawing does not depend on earlier
solves; the rest stay on native HLD and Info counts them. Recognition runs in parallel over distinct geometry objects
and honours cancellation; results are kept by content, least recently used first out.

The class reports its own progress step, `exact curved`. `engine = fast` does not touch it: fast only changes what
still goes to native HLD.

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

The 2026-11-16 contact decision replaces the planar `tieConvention`. Planar inputs
carry source identity, adjacent face indices and certified outward face orientation.
Only closed, outward-oriented Breps/meshes supply that orientation; open geometry
and unnormalised solids carry null normals. Winding normals never prove Flush in
either the kernel or the independent gate oracle. Scene preparation applies the
complete block transform and normalises closed-solid orientation before extraction.
Exact
contact predicates use owned world vertices/normals retained before rounded view
projection, so changing the camera cannot turn an exact world contact into a
tolerance tie. Projected broad-phase bounds are conservatively expanded for
contact candidates. A contact face cannot occlude its own certified contact
interval; other faces/objects still participate normally. No new drawing-result
or worker-codec fields are added.

Exact edge/face incidence plus dominant-plane polygon events certifies contact spans,
including edge-on faces and holes. Matching outward coplanar adjacent faces with
opposite interior half-neighbourhoods certify a flush continuation; symbolic
infinitesimal parity decides sides without sampling offsets. Opposed bearing
faces and folded/same-side faces do not prove flush continuity. Seated contacts
draw normally. Flush contacts draw with `joints=on` (default); `off` suppresses
only certified flush spans, before locus reconciliation. Other objects occlude
retained contacts normally. Coincident blocks retain their exterior outlines.

Complete Hidden coverage stops occlusion clipping and its face budget in both
joints modes. With joints off, remaining pairs perform only contact predicates,
after a conservative filtered world-plane distance test. Full world polygons are
created lazily inside the contact band, not globally prepared as a second set of
occluders. An inconclusive suppression check retains already proved Hidden spans.

Filtered predicates fall back to rationals of the supplied binary64 values.
Nonzero incidence residuals inside the conservative tolerance × L1-normal band,
or approximate planarity/continuity, produce Unresolved unless an occluder proves
Hidden. Approximate contact in the same cell takes precedence over exact Flush
suppression, so joints off cannot silently remove that uncertainty. This band
contains the Euclidean tolerance neighbourhood and may extend
to √3 tolerances. Both ends of the straight support must be in the band: a
transverse crossing has no positive-length contact interval and keeps ordinary
occlusion. The band is an uncertainty bound, never a contact certificate.
Endpoints retain the kernel's quarter-tolerance representability budget. Closed
polygon boundaries otherwise occlude only with a strict view-depth gap above
tolerance. The tie is retired **between source objects only**. Same-source and
self-face tolerance ties remain, including contacts within one mesh building or
multi-lump Brep. These intra-object contacts are not certified; Info says so.
Joints is omitted from the effective visibility key for cut-only views. Fast mode
still runs the exact planar kernel, so its joints value must remain keyed.

Native/freeform candidates keep native contact behaviour and the existing audit;
Info explicitly says that planar joint suppression does not certify them. Fast
native determinism remains outside the contract. No analytic contact certificate
is inferred before the analytic engine supplies one. The gate's `contactEdge`
exception requires independently checked foreign-face incidence and Visible
coverage; self-faces and merely within-tolerance gaps cannot supply it.
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
geometric evidence for a certified contact rule, native quirk or tolerance artefact; bugs
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
from certified contact-edge decisions, native-order quirks and remaining unexplained
samples. Legacy blanket `tieConvention` evidence is no longer accepted.


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
