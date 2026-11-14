# S2 — clipping, section semantics and region shape

Research performed 2026-11-14. **Decision for M2: C for visibility — physical clipping of owned copies, cap valid crossing solids at the cut, then HLD without globally registered clipping planes.** Build section regions independently. Use a separate point classifier against the same clipped occluders whenever native point classification is absent/Unset. This is a C-based hybrid, not the proposed native-clipping D baseline.

Native A has a reproducible false-visible crossing curve. B's per-object occluding-section flag repairs that curve but drops points. Both native routes failed an excluded-occluder control. C is a foundation to implement with explicit contact/provenance checks, **not a claim that the MVP trim loop is production-ready**: the prototype itself loses a half-tolerance sliver and an exact contact face. The repairs and failure policy below are part of the recommendation.

Only this report was written in the repository. All scripts, generated fixtures and results are in [the S2 scratch folder](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114). No commit, product-code change, installation, interactive document access, or intervention in another Rhino process was performed.

## Method and build identity

Read PLAN's S2 row and all six contracts, Astra issues 3 and 9, UX §§3.2/3.7, REQUIREMENTS' cut policy, geometry-matrix counterexamples and the fixture manifest before probing. The existing research harness was read as reference; these probes use RhinoCommon directly and do not load Frame2D/M2D+ assemblies.

| Item | Recorded value |
|---|---|
| Rhino / loaded RhinoCommon | **8.36.26296.17001** |
| Host | Python 3.12.9 x64, pythonnet/rhinoinside from the existing research venv; `rhinoinside.load(8, 'net8.0')` |
| Actual CLR | **8.0.34**; no .NET 10 equivalence claim |
| OS | Windows 11, 10.0.26300 |
| Geometry convention | Metres, absolute tolerance **0.0001 m**; parallel camera toward world −Z, up +Y; plane at z=0, retain z≤0 |
| Native settings | `Flatten=false`, hidden curves on, tangent edges/seams off, `Compute(parameters, false)`; no parallel HLD within a process |
| Inputs/caches | Fresh processes per probe group; owned/generated or File3dm geometry; no product cache, no worker transport |
| Loaded `System/netcore/RhinoCommon.dll` SHA-256 | `5fd146708cd1c357760557fb073933e4d873babb56fcee57580c1bf1f8d8fa8b` |
| `System/rhcommon_c.dll` SHA-256 | `c7a01969a1f6946566b1444284b1997010d15f0f8849696185e7e544324ec90e` |
| `System/RhinoCore.dll` SHA-256 | `86b99105cdc280e032d5ec51bd773ca7e16e24bf309b47853505c00839b5c7b5` |
| Non-loaded `System/RhinoCommon.dll` SHA-256 | `42c89da0abe95cdc4e7be989466b8df72ed6b92917d8cc88b4dd5c2c6581f9dd` — distinct from the netcore assembly |

The route matrix has **36 scenes × five configurations = 180 route results**, including one empty C preparation, plus one recorded rejection for direct SubD. It includes six new adversarial scenes and 30 bundled scenes. Separate groups cover blocks, scope, depth, contacts, styles and regions. All bundled fixture hashes match `tests/fixtures/manifest.json`. The scratch manifest records input, contract, script, result and generated-fixture SHA-256 values.

Visibility oracles are independent geometric expectations: exact x-intervals behind boxes/RHS material bands, halfspace membership, and analytic point positions. `score.py` checks both missing and extra visible/hidden interval loci; symmetric difference allowance is four tolerances for multiple endpoints. Successful listed line endpoints differ from truth by less than 0.00003 m. Curves are scored in projection: the sloping crossing line's retained 3D length is √10, but its retained projected length is 3. Area oracles use rectangle arithmetic and circle integrals, not agreement between two Rhino pipelines. Native records retain classification, source, feature kind, length, closure, world samples and extraction exceptions; the final far probe additionally retains `ClippingPlaneIndex`.

`ok` in JSON means Compute/extraction completed, not geometric correctness. General curved/trimmed scenes without full-output truth remain **U**. Timings are indicative single observations: other headless jobs were permitted; the end of the contacts group overlapped the start of our controls group. CPU, peak native memory and campus-scale costs were not measured. No timing establishes a route speedup.

## 1. How the cut reaches visibility

Configurations: **A** = registered native plane, global option false, per-geometry flag false. **B_per** = global false/per-geometry true. **B_global** = global true/per-geometry false. **B_both** = both true. **C** = physical halfspace trim/cap of copies, then no native clipping planes or occluding-section flags. Native meshes are tested directly; C's polyhedral mesh control converts to an exact planar-face Brep before trim/cap, so this does not validate a general mesh-capping algorithm.

### Independent visibility results

| Scene / independent expectation | A | B_per / B_both | B_global | C |
|---|---|---|---|---|
| RHS + probe at z=−2: visible x outside [−1,1] **and through void [−.7,.7]**; hidden two .3-wide material bands | Pass: V 5.4, H .6 | Pass | Pass | Pass; cap retains void |
| Crossing box + smaller box behind it + probe farther behind: V 4, H 2 | Pass usable intervals; **4 invalid Unset proxies** | Pass; no extraction errors | Same four errors as A | Pass; no extraction errors |
| Sloping curve (−3,0,−1)→(3,0,1), cut box: keep x [−3,0]; V [−3,−1], H [−1,0] | **Fail: all retained x [−3,0] Visible** | Pass: projected V≈2.00003, H≈.99997 | Same failure as A | Pass |
| Box wholly in removed side + curve/point behind it: curve V 6, point Visible | Pass | Curve passes; points missing | Pass | Curve passes; native point **Unset** without an area occluder; own point path required |
| Point at (0,0,−.0002), inside cut solid immediately behind cap | **False Visible** | No points returned | False Visible | Hidden with clipped capped solid |
| Point at (0,0,−2), point at (3,0,−2), point in removed side | Hidden / Visible / omitted | **All omitted**, including outside point | Same as A | Hidden / Visible / omitted |
| Closed mesh straddling cut + background probe | V≈4, H≈2; point Hidden | Curve passes; points missing | Same as A | Curve and point pass on planar-face conversion |
| Asymmetric mirrored block, expanded leaf x [−1,2]: background V 3, H 3 | Pass | **Fail on inward mirrored Brep**: H 6, cut length 32 instead of 8 | Pass | Pass |
| Same mirrored leaf corrected to outward solid orientation | — | **Pass**; cut length 8, V≈3, H≈3 | — | — |

The mirror failure repeated on three independently rebuilt Breps. CRC, bounds and signed volume did not change during Compute; this was not evidence of native input mutation. Signed volume was −6 before orientation correction. Passing the unmirrored base with an instance transform also passed. Expand block definitions explicitly and normalise closed-solid orientation after mirrored transforms; raw `InstanceReferenceGeometry` was rejected (`AddGeometry=false`), while expanded leaves and the transform overload were accepted.

The independent line scorer passes C in all six scored scenes. A/B_global fail the crossing-curve scene; B_per/B_both fail the deliberately inward mirror control. These are property-level results, not an all-geometry certification.

### Global versus per-object switches

[global_modes.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/global_modes.json) tests global false/true against three actual overloads: `AddGeometry(g, tag)`, `AddGeometry(g, tag, false)`, and `AddGeometry(g, tag, true)`. On this build, toggling the global property alone changed neither the crossing-curve failure nor the points. Explicit **true** repaired the curve and returned zero points with either global setting. The solids-only flag control also dropped all points when point flags were false. Therefore the measured culprit is the **per-geometry occluding-section route**; the earlier combined-option experiment could not distinguish the two switches. Do not assume the global property substitutes for per-geometry flags.

### Separate judgments required by S2

| Property | A: default native | B: per-object occluding sections | C: physical trim/cap | D: native visibility + own regions/points |
|---|---|---|---|---|
| Background correctness | F on crossing curve; F on scoped occluder below | Repairs crossing curve; F on scoped occluder; requires outward solids | Pass tractable visibility controls; tolerance-band trim is unsafe without guard | Repairs points, but inherits native scope failure |
| Cut-boundary curves | `SectionCut` available; native labels coplanar/contact faces incorrectly for our policy | Available; also needs policy; inward mirror and coplanar sheet produce pathological cuts | Cap/trim edges lose native `SectionCut` identity; own intersections are authoritative | Own regions resolve topology, not native visibility errors |
| Points | Present, but point inside removed-face opening is false Visible | Missing entirely, even flags only on solids | Tested capped-solid points correct; area-free scenes can be Unset | **12 analytic point classifications pass** against the same clipped solids/voids/removed occluder |
| Unset proxies | Four throwing proxies in the straddle/behind scene | Zero extraction errors in the 36-scene matrix | Zero extraction errors in that matrix | Avoid B's point extraction, but keep source/view diagnostics |
| Duplicate/retraced strokes | Native Duplicate/Projecting records must be filtered | Same; wrong orientation inflated cut boundary fourfold | Native ordinary edges overlap independent cut loops if both are emitted | Must choose one owner of section boundaries |
| Artificial trim/cap edges | No physical input edges, but synthetic section output | No physical input edges | **Leak if emitted blindly**: box cap boundary is Visible `Crease`; tapered example has Boundary/NonSilhouetteCrease | Native part avoids physical cap edges; own region boundary must still replace native SectionCut output |
| Cost | One HLD solve, no physical trim | One solve; own regions still needed | Trim + cap + one HLD; own sections reusable for cap/provenance | HLD + own regions + clipped occluders/rays for points; a second full HLD is unnecessary |

For the closed quad/tri mesh controls, current raw native Visible section length is **8**, not the older MVP's 8+1. The older duplicate is therefore not reproduced as an inherent raw-HLD result here. Native Duplicate records still overlap the contour. Adding an independent 8-unit Cut contour without replacing native output would yield 16 units. Partial overlap/retracing must be tested along loci, not just equal-curve hashes; this report does not certify arbitrary curved overlap removal.

The measured D prototype uses native B for curves and `Intersection.CurveBrep` rays against C's clipped/capped occluders for points. Solid: centre-near/centre-behind/material-band points Hidden, outside Visible. RHS: centre-near/centre-behind Visible through void, material-band point Hidden. Wholly removed box: all four Visible. This specifically avoids an uncut second point solve, but does not repair native scope semantics, so **D is not the M2 baseline**.

Indicative per-configuration medians over the 36 heterogeneous scenes: A .425 s, B_per .456 s, B_global .441 s, B_both .434 s, C .449 s native time; C preparation median .00325 s. These are measured observations, not paired performance evidence. Additional cap/section reuse and point-ray acceleration savings are estimates only.

**M2 extraction contract:** feed all retained opaque occluders into one visibility solve; filter native Duplicate/Projecting classifications; never dereference an Unset/null-parent proxy as a normal curve. Preserve an explicit invalid-extraction diagnostic and quarantine a view if completeness cannot be bounded. Generate Cut from our region pipeline and suppress/reclassify the corresponding cap/trim candidate intervals using provenance. A plane-membership test alone must not delete an original edge or a retained coplanar face. Failed trim/cap/normalisation is a source/view failure, never permission to omit its occlusion.

## 2. Depth, far clipping and Beyond

| Probe | Measured result | Recommendation |
|---|---|---|
| Two opposite native planes at z=0 and z=−d | Ordinary depths remove farther geometry, but A leaves a line inside the retained solid Visible; B repairs it | Apply per-source physical near/far clipping and conservative culling; no global native plane in baseline |
| `farclip=none` | Single near-plane control retains the probe at z=−2 despite nominal depth .5 | Do not add/trim at far plane; depth remains fade reference |
| Native far outline identity | `HiddenLineDrawingObjectCurve.ClippingPlaneIndex` is 0/1 for near/far SectionCut curves | If native clipping is later enabled, use this index, not just `SectionCut` or z sorting |
| Tapered cone, d=2 | Native and physical give near circumference π at z=0, far 3π at z=−2. Both are visible and spatially distinct | Own intersections give distinct Cut/FarClip provenance; no far Hatch |
| d=0 | A returns no segments; B returns only near cut perimeter; C trim returns no solid | Explicit section-only path; do not call zero-thickness volume HLD |
| d=.5 tol | Box A mixes near/far perimeter ownership; cone A fragments coincident contours; B returns no far contour; C loses slab | Treat near/far separation as unresolved at this tolerance; do not nudge, increase depth or pretend it is exactly zero |
| d=2 tol | Box far boundary is coincident in projection and classified Duplicate; cone far circle is split Visible/Duplicate | Build complete far contour independently; near/far precedence must reconcile coincident projection intervals |
| Independent cone intersections | Radius .5 and 1.5; closed; plane error zero; maximum radial error 2.23e−16 m | Reliable on tested analytic geometry, even when native contour extraction/classification is insufficient |

Physical prototype: `Brep.Trim(Plane,tol)`, followed by `CapPlanarHoles(tol)` for valid crossing solids. For far clipping, retain an open far end in the prepared visibility body unless a cap is needed internally; any such cap is an occlusion aid, never an automatic drawing feature or fill. The tapered body was tested both capped and uncapped: the two identifiable boundary loci survived; removing the far-boundary class implements noline for this control. A general cap-edge provenance adapter remains M2 work.

For `line`, intersect the pre-far-clipped source (after near handling) with the unchanged far plane, clip to crop, classify it against the same retained occluders, and route the canonical far-boundary intervals to Far clip. For `noline`, suppress only generated far-boundary candidates. Keep pre-existing edges on the far plane by their original provenance. Hidden far intervals stay subject to the hidden/visibility policy; do not unconditionally paint a hidden contour over nearer material. Coincident Cut/FarClip loci use deterministic precedence (Cut owns the drawn stroke), with both plane identities retained in Info/provenance. Small-depth occlusion remains explicitly uncertain where the tolerance cannot separate the planes.

For depth **exactly zero**, publish the directly constructed Cut regions/outlines and no participating-source background volume or duplicate far outline. Out-of-scope sources remain whole; they still participate as occluders. In a mixed-scope section-only view, test section candidates against those whole occluders. This mixed zero-depth presentation is a recommended policy, not a measured viewport-parity result. For `0<depth≤tol`, return the independently computed section topology but flag/suppress untrustworthy background and far-line classification; do not silently coerce it to zero. Negative depth should be rejected.

`beyond` is neither a third clipping plane nor an occluder filter. After visibility, split visible intervals at view-local depth `d=−z=beyond` and move the deeper pieces to Beyond; do not duplicate them. When beyond≥finite depth with far clipping enabled, issue the UX remark and ignore it. With `farclip=none`, retain deeper geometry. No fade arithmetic should divide by zero in section-only mode. Beyond splitting/fade was not implemented in this research.

## 3. Scope, depth metadata and flipped planes

The exact APIs are `ClippingPlaneObject.ClippingPlaneGeometry` → `ClippingPlaneSurface`; then `ParticipationListsEnabled`, `GetClipParticipation(out IEnumerable<Guid> objectIds, out IEnumerable<int> layerIndices, out bool isExclusionList)`, `PlaneDepthEnabled`, `PlaneDepth`, and `Plane`. The fixture round-trip retained include/exclude lists, object GUID, layer index, enabled flags and depths **0/.25**. `ViewportIds()` identifies affected viewports but is not an object-participation list. See [the RhinoCommon clipping-surface API](https://developer.rhino3d.com/api/rhinocommon/type/rhino.geometry.clippingplanesurface/).

| Test | Result |
|---|---|
| Include/exclude metadata, object + layer lists | Both modes read back correctly in memory and from `scoped_depth.3dm` |
| Object-only and layer-only include/exclude resolution | Four actual document-list cases exercised; host predicate matches object ID **or** layer index, then inverts for exclusion |
| Out-of-scope foreground box z [.1,1], probe z=−2, registered HLD plane | Box edges retained, but **probe V 6 and point Visible**; expected V 4/H 2 and Hidden. Failed A, B_per, B_global, B_both |
| Same control with all per-object clip lists empty | Still fails while plane is registered globally |
| Per-object clip lists supplied without registering global plane | Box occludes correctly, but that configuration does not establish working clipping: it also did not remove geometry through those lists |
| Physical clipping after scope resolution, no global native plane | Excluded box remains whole and gives V≈3.99998/H≈2.00002, point Hidden for both object/layer exclusion |
| Include box (wholly removed), no surviving area occluder | Curve V 6; native point Unset. Classify point Visible from empty-occluder path; never output Unset as Visible by assumption in a nonempty scene |
| Raw versus expanded block | Direct instance rejected; expanded asymmetric mirrored leaf accepted; orientation normalisation required for B |
| Stored CP plane +Z / flipped −Z | Retained world point changes z=−1 → z=+1; removed point changes correspondingly. Stored `Plane.Normal` reads +Z/−Z |

Use selected-source metadata only, resolved on the host thread **before** cut/far culling. With participation disabled, every source participates. Otherwise snapshot include/exclude GUIDs and stable layer identities; carry a boolean/effective cut-range per expanded leaf to compute. Objects outside scope retain their complete geometry, including portions in front of the cut/far range. Settings depth overrides should preserve that participation scope. Do not resolve layers by a worker's table index or apply all viewport-active planes to a named-view source.

Nested block scope needs root/instance-path identity as well as definition-leaf identity/layer. The report proves block expansion and simple object/layer scope separately; **Rhino viewport precedence for nested instance versus leaf-layer membership was not measured**. Recommended M2 policy: a selected/excluded instance applies to its descendants, with effective leaf/layer participation resolved once on the host and recorded. Until its conflict cases are verified against Rhino, mark ambiguous nested scope unsupported rather than applying a global fallback.

GH plane convention works without a hidden normal reversal: keep `Plane.DistanceTo(p)≤0`, look along `−plane.ZAxis`. For a flipped right-handed frame use **X→−X, Y unchanged, Z→−Z**. The actual stored CP fixture changes world (1,.5,+1) to local (−1,.5,−1), which is behind the flipped plane. Transform with that frame to preserve handedness; changing camera direction alone is insufficient. These are headless stored-plane/HLD tests; interactive widget-arrow/render parity remains manual.

## 4. Contacts and tolerance

All tests used the original z=0 plane; no displacement. `bottom` = box [offset,1]; `top` = box [−1,offset]; sheet = open planar face at offset. The table records native **cut perimeter length**, not fill eligibility. A zero means no SectionCut records, not absent geometry.

| Offset | bottom A / B_per | top A / B_per | sheet A / B_per |
|---|---|---|---|
| −2 tol | 8 / 8 | 0 / 0 | 0 / 0 |
| −.5 tol | 0 / 0 | **8 / 0** | **8 / 8** |
| 0 | **8 / 8** | 0 / 0 | **8 / 24** |
| +.5 tol | **8 / 8** | 0 / 0 | **8 / 8** |
| +2 tol | 0 / 0 | 8 / 8 | 0 / 0 |

Neither native mode encodes the owner policy. In particular, exact bottom contact is labelled SectionCut despite no volume crossing; a coplanar open sheet in B returns perimeter 24 rather than 8 and hides the entire 6-unit probe. A/B_global and B_per/B_both respectively agree in these controls. Near-contact may also change occlusion: a top at +.5 tol leaves the probe all Visible in A but correctly partitioned in B. Labels cannot be used as a crossing oracle.

C is also tolerance-sensitive: bottom at −2 tol leaves a valid capped .0008 m³ sliver; at −.5 tol it returns **zero pieces** despite a mathematically crossing solid. Exact bottom contact returns zero pieces. Top at +.5 tol remains whole above the plane, while +2 tol trims to z=0. A separate exact coplanar-face extraction retained one surface with visible perimeter 8 and background H≈2, with **zero Cut regions/Hatches**. This is the required special case, not a plane nudge.

| Per-object classification | Deterministic recommendation | Info / publication |
|---|---|---|
| Certified closed material component with points strictly below −tol and above +tol, valid transverse intersection, valid closed material loops | Crossing; cut at original plane; cap visibility copy; hatch own regions | Record tolerance, source, route and region status |
| Exact coplanar/tangent contact, certified geometrically | Not a cut. Preserve retained coplanar face/edge as ordinary visibility/beyond geometry; no hatch | Count contacts; preserve original plane/feature provenance |
| Signed extremum/feature in [−tol,+tol] prevents a crossing/side certificate | **Near-contact ambiguous**; no confident fill; do not guess from native SectionCut labels | Source/component range, band width, ambiguity reason; affected visibility Unresolved/quarantined if its occlusion cannot be bounded |
| Certified entirely behind / in front outside band | Keep whole / remove, within participation scope | Count kept/culled sources; exclude contact faces from the removal shortcut |
| Open geometry, including closed intersection contour | Trim as surface geometry; section outline only, Hatch null | Explicit open-source warning |
| Non-manifold/invalid material topology | No inferred solid fill/cap | Unsupported-region diagnostic; quarantine affected visibility if occluding |

The ±2 tol boxes certify the relevant side/crossing; ±.5 tol boxes are intentionally ambiguous, even when their exact mathematical box crossing is known to the oracle. Do not promote near-contact to coplanar or crossing merely because the API returned a loop. Exact-plane detection must use geometry/topology predicates and numerical error bounds, not equality of a few samples or an arbitrary smaller replacement tolerance.

Bounds are broad phase only. A single Brep containing disconnected boxes z [−2,−1] and [1,2] has a crossing bounding box but zero plane intersections; `GetConnectedComponents()` reports two components on their respective sides. For general Breps, retain shell/void nesting when identifying material components: splitting disconnected surface shells of a cavity into independent filled solids would be wrong. Crossing witnesses plus certified connected material topology, validated intersections and successful nondegenerate trim/cap are necessary; sparse surface samples/control-point signs alone do not certify a freeform solid. When those checks cannot decide, the result is uncertainty, not an implicit tolerance change.

## 5. Section-style sources and values

The tiny real headless document contains a styled layer (scale 2), custom object (3), custom clipping plane (4), and nested By Parent→By Parent→Custom instance path (5), plus a clipping plane By Layer and an unstyled child layer. Sources and custom-style payloads survive File3dm save/reopen. This probes stored API data and explicit path resolution, **not interactive rendering equivalence**.

| Value | Exact RhinoCommon 8.36 read | Measured fixture value / implication |
|---|---|---|
| Object source | `ObjectAttributes.SectionAttributesSource` | `ObjectSectionAttributesSource.FromLayer / FromObject / FromParent / FromSectioner`; UI's By Clipping Plane is **FromSectioner** |
| Custom object style | `attributes.GetCustomSectionStyle()` | scale 3, rotation .37; null for unassigned custom style |
| Layer style | `doc.Layers[attributes.LayerIndex].GetCustomSectionStyle()` | scale 2 |
| Nested By Parent | Each leaf/instance's `Attributes.SectionAttributesSource`, then recurse through the captured instance path | Leaf and middle remain FromParent with no custom payload; outer FromObject gives scale 5. Getter does not resolve inheritance for us |
| Clipping-plane custom style | `clippingPlaneObject.Attributes.GetCustomSectionStyle()` | scale 4, only selected for sources FromSectioner |
| Clipping plane By Layer | Its `Attributes.SectionAttributesSource`, then its own layer's getter | Custom getter null; layer gives scale 2. Object's layer is not the clipping plane's layer |
| Pattern | `SectionStyle.HatchIndex` → `doc.HatchPatterns[index]` | Real pattern `S2-lines`; index 0 in this tiny document, **not inherently Solid** |
| Pattern definition | `HatchPattern.Name`, `FillType`, `HatchLineCount`, `HatchLineAt(i)`; `HatchLine.Angle`, `BasePoint`, `Offset`, `DashCount`, `DashAt(j)` | Lines, one line, angle .2, base (0,0), offset (0,.1), dashes [.2,−.1] |
| Scale / rotation | `HatchScale`, `HatchRotationRadians` | 2/3/4/5 by source; .37 radians; remain unchanged in a 1:1 drawing |
| Pattern colours | `HatchPatternColor`, `HatchPatternPrintColor` | Red / DarkRed |
| Background | `BackgroundFillMode`, `BackgroundFillColor`, `BackgroundFillPrintColor` | SolidColor / AliceBlue / Blue; enum also exposes None and Viewport |
| Boundary | `BoundaryVisible`, `BoundaryWidthScale`, `BoundaryColor`, `BoundaryPrintColor`, `GetBoundaryLinetype()` | false, 2.5, Green, DarkGreen, copied custom `S2-boundary` |
| Fill rule | `SectionFillRule` | New SectionStyle defaults to **ClosedCurves** on this build; do not let it override the open-geometry no-hatch requirement |
| No style versus explicit no fill | null getter versus non-null style with HatchIndex −1 and BackgroundFillMode None | Distinct stored states; only absence invokes default solid poché |
| Child layer with no own custom style | `childLayer.GetCustomSectionStyle()` | null despite styled parent; no automatic parent payload from this getter |

Resolve the source enum first; do not apply an unconditional clipping-plane→object→layer override chain. For FromSectioner, resolve the clipping plane's own source/custom-or-layer style; if unavailable, use the object's documented layer fallback. FromParent recurses through instance attributes. Only after source resolution apply M2D+'s hatch override. Missing style and explicit no-fill remain distinct. Snapshot the **pattern definition**, style values, custom boundary linetype and source provenance; remap pattern indices in the receiving/bake document.

Important observed API detail: a default style's pattern colour is ARGB **0x00ffffff**, alpha 0, but **`IsEmpty=false`**. Testing only `Color.Empty` would miss this unset-colour representation. Preserve the raw ARGB sentinel in the snapshot and resolve source colour deliberately; viewport verification of unset-colour interpretation remains a gate. The local XML describes source-derived default colours. `Viewport` background has no drawing viewport; use the UX fallback and report it, not an invented viewport dependency.

**Not exposed in this installed API / not established here:** reflection finds no RhinoDoc section-style table; no SectionStyle hatch-origin field or separate clipping-plane-overrides-everything flag. `Hatch.BasePoint` exists for an actual hatch, but is not a SectionStyle getter. The current online API lists some newer plot-weight/index properties that this 8.36 reflection does not expose; do not assume those compile against the project's 8.20 reference baseline. Sublayer renderer inheritance, GH unbaked Model Object style transport, material/parent display-colour parity, and styled hatch baking were not tested. Source/style fixture data and pattern serialization passed; full viewport/bake parity did not run. See [the official SectionStyle API](https://developer.rhino3d.com/api/rhinocommon/type/rhino.docobjects.sectionstyle/) alongside the frozen local `api.json` inventory.

## 6. Region output and crop prototype

Pipeline actually exercised: plane intersection (`Intersection.BrepPlane`, or `MeshPlane` in base utilities) → join **per source** → `Brep.CreatePlanarBreps(all closed loops,tol)` → split each planar material face by crop with `BrepFace.Split` → retain pieces inside crop → extract each connected face's outer/inner loops → `Hatch.Create(all loops of that region,patternIndex,rotation,scale,tol)`.

The interior point used for crop selection is a certified-on-face tessellation triangle centroid, not the area centroid (which can lie in a hole). The crop split is performed on an already built material region, so holes are retained and bridges can be split. Area is checked against independent arithmetic/integrals. Pattern index 0 in the dedicated output File3dm is explicitly populated with Solid; it is not assumed to be a document-independent pattern number.

| Fixture | Expected regions / loops | Measured regions / loops; one Hatch each | Expected / measured area m² |
|---|---|---|---|
| RHS outer square ±1, void ±.7 | 1 / 2 | 1 / 2, closed, valid | 2.04 / 2.039999999999999 |
| CHS R=1, r=.7 | 1 / 2 | 1 / 2, closed, valid | .51π / 1.602212273103; area error 1.98e−8 |
| Box cut, crop x [−.5,.5], y [−2,2] | 1 / 1 | 1 / 1, closed | 2 / 2; fixes older open-cut counterexample |
| RHS with same crop, one object split through void | **2 / 1 each** | **2 / 1 each**, closed | .6 / .6; .3+.3 |
| CHS with same crop | 2 / 1 each | 2 / 1 each, closed | .643634112010 / .643634112078 |
| Four nested circles radii 4,3,2,1 (hole-in-hole) | **2 / 2 each** | 2 / 2 each | 10π / 31.415926923588; error 3.88e−7 |
| Same nested circles cropped to x [−.5,.5] | **4 / 1 each** | 4 / 1 each | 4.051723005730 / 4.051723005610 |
| Open cylinder, no end caps | One closed outline, **no material fill** | One closed contour; Hatch null by eligibility policy | Not a solid region; no area used to authorise fill |

Crop provenance is retained per resulting edge. RHS/CHS crops each have **four crop-created edges**; box crop has two; nested-circle crop has eight. The prototype compares split-edge midpoints to original physical loops and crop boundary and allows dual `cut+crop` provenance when coincident. Since face splitting already creates intersection vertices, this works on these fixtures. General tangencies, tiny slivers and curved coincident overlaps require interval-level provenance, not a whole-curve midpoint heuristic in production.

The branch prototype serialises real curves and hatches in `region_branches.3dm`, with `output` and `path` attributes, plus JSON path/loop/hatch records. Save/reopen verification found **14 branches, 18 valid closed Cut loops, 14 valid Hatches** with a real Solid pattern. Example paths `{view;source;region}` (illustrative internal source IDs):

| Path | Cut payload | Hatch payload |
|---|---|---|
| `{0;0;0}` RHS | outer loop + inner loop | exactly one annular hatch |
| `{0;3;0}` cropped RHS | bottom closed loop | one hatch |
| `{0;3;1}` cropped RHS | top closed loop | one hatch |
| `{0;6;0}` nested circles | outer radius 4 + hole radius 3 | one hatch |
| `{0;6;1}` nested circles | island radius 2 + hole radius 1 | one hatch |

Thus alignment is **by region branch**, not one Hatch per loop. Open outlines need distinct outline branches with one null Hatch slot. A style-requested no-fill likewise retains its region branch and one null slot. GH DataTree construction itself was not run. Compatible-style merge is after crop; this spike did not implement touching/overlapping multi-source merges or bake ordering. Merge compatibility must include resolved pattern definition, scale/rotation/origin policy, colours/background and boundary fields; never merge merely by document pattern index.

## M2 decision and remaining gates

| Decision | Recommended implementation |
|---|---|
| Cut reaches visibility | **C baseline**: detached, normalised owned copies; scope-aware cull/physical near trim; cap valid crossing solids; single HLD with no global clipping planes; own section topology and point path. A/B/native-D stay experimental |
| Far plane | Scope-aware culling + physical far trim; independent unchanged-plane intersection for line; provenance-suppress generated far edges for noline; none does no far trim; no far hatch |
| Scope | Snapshot selected CP's include/exclude IDs/layers, enable/depth flags and effective instance-leaf scope before culling; excluded objects stay whole and still occlude; preserve scope under depth override |
| Contact | Crossing certificate per valid material component; preserve exact coplanar contacts as ordinary features; ambiguous tolerance band is explicit uncertainty; no plane movement |
| Style | Source-enum resolver over object/layer/instance path/selected plane; copy full definitions/values; respect absent versus explicit no fill; REQUIREMENTS' open-geometry policy wins over ClosedCurves |
| Region | Per-object material regions → region crop → optional compatible-style merge → hatch; matching region branches, all loops + one Hatch/null; crop-edge provenance survives |

S2 establishes the route decision and counterexamples, not a completed engine. M2 gates still include arbitrary mesh cut/cap fidelity and non-manifold handling, freeform crossing certification, complete provenance through trimming, near/far coincident interval ownership, nested-scope conflict parity, section/hatch presentation behind excluded occluders, source colour sentinels, style viewport/bake/save-reopen parity and multi-source style merges. Quarantine any unsupported potentially occluding source; do not substitute uncapped/uncut geometry or silently drop it. No production change or design-contract rewrite was made by this research sub-agent.

## Evidence inventory and rerun

All paths below are local evidence. The manifest contains full SHA-256 values for every bundled input and scratch fixture/script/result, plus hashes of the six contract/manifest inputs. The small generated fixtures were deliberately not copied into the repository.

Use the existing venv executable read-only:

```powershell
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
$s2 = 'C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114'
& $py -B -u "$s2/scripts/api_probe.py"
& $py -B -u "$s2/scripts/s2.py" routes
& $py -B -u "$s2/scripts/s2.py" scope
& $py -B -u "$s2/scripts/s2.py" depths
& $py -B -u "$s2/scripts/s2.py" contacts
& $py -B -u "$s2/scripts/extended.py" controls
& $py -B -u "$s2/scripts/extended.py" styles
& $py -B -u "$s2/scripts/extended.py" regions
& $py -B -u "$s2/scripts/cp_probe.py" cp_probe
& $py -B -u "$s2/scripts/supplemental.py" supplemental
& $py -B -u "$s2/scripts/final_checks.py" final_checks
& $py -B -u "$s2/scripts/global_modes.py" global_modes
& $py -B -u "$s2/scripts/scope_resolution.py" scope_resolution
& $py -B -u "$s2/scripts/region_branches.py" region_branches
& $py -B -u "$s2/scripts/verify.py" verification
& $py -B -u "$s2/scripts/score.py"
```

Run groups serially. These scripts overwrite named scratch results; copy the evidence directory first to preserve this run. They initialise only their own headless native runtime/private documents and contain no process discovery, termination, attachment, or interactive-Rhino manipulation. The prior supervisor was read, not used to manage other jobs. Direct SubD rejection is an expected recorded limitation in routes, and the already-normalised SubD fixture is separately exercised. This research requires the local licensed Rhino installation; native host results cannot be inferred from a dotnet build. No product build/test was necessary for this documentation-only change; the scratch verification and analytic scorer are the relevant checks.

<!-- S2_HASH_INVENTORY -->

Full [manifest](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/manifest.json) SHA-256: `3ab40d0678efc92c0b1a0aee95b2996bbeb3a0074b452c02133f42285c3befe0`.

### Scripts

| Script | SHA-256 |
|---|---|
| [api_probe.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/api_probe.py) | `14f3f9caa1ba8c2ace7ab035d57f4835530c4b9279b96cbccf5df5e612399afb` |
| [cp_probe.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/cp_probe.py) | `05a5273cf1149f53c0aa243ad6c1c241e5c19b5e1e94159b6ea0494343e91918` |
| [extended.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/extended.py) | `c39fbfb61afa2ec2c50d909ee275a9ee474d28fdea5fd70486ec4620035f9eb6` |
| [final_checks.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/final_checks.py) | `796b287c461989fd6b67e49726a8274d0bd37a97e8fd199d9051017cb4b4f032` |
| [global_modes.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/global_modes.py) | `c53635539b82d7f990c4535712d105436917e846398e9c398e37ed2613df1cf7` |
| [region_branches.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/region_branches.py) | `d05111758b1a2384a851fa0bebbb4817328681e1232c777012532757e9ed793d` |
| [s2.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/s2.py) | `5f8e30518a785ddbb07747f83a190531dc8593d5f32a255a7478157b2cb0db48` |
| [scope_resolution.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/scope_resolution.py) | `f3ea862255409edeea74c775d574038de53b34cf8d6fc21fd2129b675aa58d46` |
| [score.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/score.py) | `73ee5af5b4cb10cd1bbc27f25f47cea9f5d2d866558dcc8e2d084b2afb69f5a3` |
| [supplemental.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/supplemental.py) | `b2329530322c44959f14ea615c6736a5caa731c3ffc302fb71f409f27898a4f0` |
| [verify.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/verify.py) | `ebb5957c97d6952b2ddc0170789597e3ac8dcac91cc07e55515ff19278d920f4` |
| [write_inventory.py](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/scripts/write_inventory.py) | `b63ba8519a0575a73127c17450cf4f2b30e57a10e888a95ed663c36593ef0ea7` |

### Result files

| Evidence | SHA-256 |
|---|---|
| [api.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/api.json) | `26e72c86c45241f10f2227b08c70994849c891cd273aca780eed3b31390d5c77` |
| [contacts.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/contacts.json) | `90954ea45289616107f6508ba1b6e287e2f8f16fed6d2e291c8dde6cd5893e9f` |
| [controls.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/controls.json) | `c2203f26cff8ee1d1c6e0e5434a632087e93e19c1cec07c62922d27be7a786f0` |
| [cp_probe.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/cp_probe.json) | `f63a630ae883dbd0f1aa558b601427d52f7a509526e73e9f2c16f3eb3476ea49` |
| [depths.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/depths.json) | `f1215f009e83f64e2b05b4ac55f3348d8885b245aab4bca872a2f2215da5084a` |
| [final_checks.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/final_checks.json) | `4ba58819f9c1d0e4af031f5277a306e64a545840d1db4aea2182d482aba8d33b` |
| [global_modes.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/global_modes.json) | `c66a5a7d26168362a0aaac2d6106910ae2265b10d91a33125157dbf7481d312d` |
| [region_branches.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/region_branches.json) | `1d43bfeca89d69b669f66d2b640e6fc0bbdc92de89e0bf51606ce5862cb62319` |
| [regions.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/regions.json) | `a82803b4c53b28bb71942c3747ba1de376526ed6234e42d5b8079f555af70c1d` |
| [routes.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/routes.json) | `b58e5724066654de12fa6a2fd80ffa2124d0b9e2f6c5b022975c06455fed4d6e` |
| [scope.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/scope.json) | `cca90c2a29225253eee60a6ddb7ed2a2305c2637ffad3570496c4a3bb01a6826` |
| [scope_resolution.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/scope_resolution.json) | `ea1165a28084cbe000ab71c0e55028dc6007aa6095bfc0641423a732484da2fe` |
| [styles.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/styles.json) | `9033cdcf7f9ef4342520322c565f76ad0034386b43467c303c860584a7c75d82` |
| [summary.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/summary.json) | `adc333003bd85bfaadaf35fefdfb3f73fdd5089823cf5d87117b75a9afa13954` |
| [supplemental.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/supplemental.json) | `ac262dba7a231c6b4586dc117cceb317e933b96537a491c8a0d0559494339bcd` |
| [verification.json](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/results/verification.json) | `6acca9c4c40b62be729fb00fda8ddc1e4d2a866c916c865509405799698d0b61` |

### New adversarial/semantic fixtures

| Fixture | SHA-256 |
|---|---|
| [block_expanded.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/block_expanded.3dm) | `be7a1dec826737af4f0db6cf0d53f7d0df38dacc4d50371978f2cea667a4b9ff` |
| [block_straddle.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/block_straddle.3dm) | `8fc7fa66a305dcc05a2815f2ce2762e2bf0e62051d6fc9ac16d383f93c955563` |
| [crossing_curve.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/crossing_curve.3dm) | `9029bb1c07750c41f43a1bae89080818dd20381e91e356bce69bd0aa9cb37037` |
| [mesh_straddle.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/mesh_straddle.3dm) | `5bc2a390f2a688258da2358c4576130fa80d6b94b487cfcf3acf08dbd36a529e` |
| [points_cut_solid.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/points_cut_solid.3dm) | `936ca620160462daee7716b282c5a0825ae70df875621938bb23765e9776b4c5` |
| [region_branches.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/region_branches.3dm) | `b20b7eaca4048523a642e9adee5b0c71c4a39ad7ab06232d80ed591195bfcb90` |
| [removed_occluder.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/removed_occluder.3dm) | `a3ddc948fcbf628bdbe012e74580069a845854039fda8b3844e01436b143418c` |
| [rhs_void.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/rhs_void.3dm) | `0dcdb54da4c2a5879f538b5657d1e6f5b239581a9e3d9cfdc16bcc39b18b9c7d` |
| [scope_resolved.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/scope_resolved.3dm) | `2ded059599c3409067cac28bc22ed9ee57b4adcaf633f18863b9bd959c754111` |
| [scoped_depth.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/scoped_depth.3dm) | `2f2376d731d3940438efabe14aac091d313c2a87fbef66033f58f8e8ef2a7cb5` |
| [section_styles.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/section_styles.3dm) | `a700d84c7502bc105448cefa72fbf3d729b51e30fadf8b73dc43924e25416afd` |
| [straddle_front_removed.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/straddle_front_removed.3dm) | `65f561ae0a81e2f8c22fcffade111a910075bbe9cf6d1041b206e67c5374aa3e` |
| [tapered_depth.3dm](C:/Users/tomosandego/.t3/scratch/m2d-plus-s2-20261114/fixtures/tapered_depth.3dm) | `ae91a8541d9ed73f46975b0cfaa5e1eb2a542b1b5399c7027b362b8a36931764` |

The `bundled_*` scratch fixtures cover arc/circle/line/polyline/NURBS, box/concave/curved/trimmed/open Breps, coplanar annulus, extrusion, converted SubD, closed quad/tri and open/non-manifold meshes, terrain/building, column/slab, curved pipe, material-backed glass, RHS/CHS/I-beam, surface/point/contact and behind-occluder cases. Exact original input paths and hashes are in `manifest.inputs`; `summary.fixtures` gives the executed scene list. Expanded block originals were covered by the separate asymmetric block controls. Text/annotations and external large assets were outside this clipping-correctness spike.
