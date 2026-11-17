# One visibility engine: Rhino HLD

Design note · owner option B, 2026-11-17. Approved implementation specification, updated with review findings. Supersedes the exact/native choice and engine-composition roadmap.

## Routing and sections

All projection uses threaded `HiddenLineDrawing.Compute`. Keep one budgeted HLD
call per process and bounded parallel worker views. HLD owns line/point visibility,
contacts and duplicates; no custom classifier, ray verifier or dense audit runs.

- **Uncut:** owned, normalised sources → conservative view culling → clear-glass
  substitution → HLD → extraction/crop → presentation/placement. A finite far
  limit uses the physical clipping adapter even without a near section.
- **Cut:** use route C. Resolve participation, physically trim participating
  owned Breps/meshes, cap crossing solids with holes intact, clip curves/points
  by near/far halfspaces. Excluded sources stay whole and can occlude. No HLD
  clipping planes, `AddGeometryAndPlanes` or occluding-section flags.
- Build near-cut material regions independently with `SectionRegionEngine`:
  crossing volumes only; exact contact/coplanar faces are not cuts; open sources
  yield outlines/null hatches. Crop **regions**, then merge compatible styles and
  hatch. Keep physical-cut/crop-edge provenance and aligned Cut/Hatch branches.
- Feed retained geometry, contact features, far-line candidates **and points**
  into the same HLD solve. Replace clear transparent bodies with feature curves
  and silhouettes after clipping; use original material geometry for Cut/Hatch.
  Glass remains linework that opaque bodies can hide, but cannot occlude others.
- Physically trim the far halfspace only for `farclip=line|noline`; `none` leaves
  it untrimmed. Caps aid occlusion, never supply automatic Cut/Hatch. Preserve
  original edges; suppress generated trim edges by provenance. Own far-plane
  intersections are candidates classified by HLD; only visible intervals become
  Far clip, with Cut taking precedence on coincident loci. Crop projected lines
  and points afterwards; crop boundaries do not become visible model edges.
- `cutonly=on` returns sections, hatches, cut-related Unresolved, Frame/Info,
  **without HLD**. Zero depth with far clipping also has no volume projection;
  a positive separation within tolerance stays explicitly uncertain, never nudged.
  Uncut cut-only views give Frame/Info and a grey remark.

Today's native-mode limits disappear for regions/hatches/merge, clear glass,
scope clipping and cut-only skipping. HLD tolerance/contact/freeform errors,
threaded variation and lack of joint suppression remain, as do section refusals
and unsupported trim/cap domains. Excluded foreground occluders can still withhold
overlapping cut regions.

## Settings, compatibility and Info

Remove Engine/Joints from the menu. Read `engine=exact|native|fast`, `joints=on|off`
and `default` from Settings, `m2d.*` and saved GH menu rows; preserve authored values
but exclude them from compute/run identities. Deduplicate grey remarks for explicit
legacy instructions: `engine is always Rhino HLD now` and
`joints is ignored; Rhino HLD controls contact edges`. `fast` gets the same engine
remark. Defaults produce no obsolete-setting remark. New Copy settings text and
view-box defaults omit both keys. Keep GUIDs, five inputs and eleven output indices/trees.

Hidden stays on by default. Set `IncludeHiddenCurves` to the requested Hidden
flag; remove hidden-on normalisation. Hidden changes invalidate projection;
tangents/seams remain HLD flags (both off by default). `min visible` stays a
presentation filter, with cut objects, points and uncertain sources exempt.
With hidden off it cannot establish partly-hidden evidence: retain all objects
and give a grey remark. Do not compute hidden curves implicitly for this filter.

Visibility keys: depth, farclip, crop, hidden, tangents, seams, glass, tolerance,
cutonly, scene/view/scope and compute fingerprints. Cut-only omits only hidden, tangents
and seams; glass still controls admission behind excluded foreground bodies. Min-visible, beyond, fade, weights/scale, hatch/cuts, tree and Target stay
post-solve; bake stays bake-time. Ignored legacy edits make zero HLD calls,
including through component run and worker timing-history keys.

When HLD runs, Info says `Engine: Rhino HLD; multithreaded; visibility not certified`;
zero-call views say HLD did not run (cut-only: sections only). The stored threading
policy stays true even with no registrations. Show joints disclosure only for explicit
legacy joints values. Cache the process fingerprint in one static Lazy. Report
actual flags, section route, HLD calls, extraction/region/hatch/refusal counts,
tolerance risks and fingerprints. Remove deterministic/kernel claims and obsolete
native limitations. Keep grey engine/Unresolved remarks and quarantine warnings.
Footer steps: sections, native, output; remove exact-lines/checking.

## Workers, codec and cache

Equality means **same inputs/settings/fingerprints**: owned scene archives,
transforms/attributes, ordered HLD registrations, view/scope, tolerance, flags and
threaded policy reach the same adapter. Keep exact runtime version/architecture,
Rhino build/revision, RhinoCommon and Core/Rhino hashes in the handshake. Record
processor count; matching it cannot promise identical HLD output.

Bump `WorkerCodec.Schema` from 3 to 4 when routing/request semantics change;
reject older peers before scene transfer. Remove obsolete NativeOnly,
verification/dense-audit options; threading is required true. Retire kernel-only
fields with codec coverage updates. Transport of **one result** stays lossless:
geometry/depth, classes/source paths, loops/provenance/hatches/styles, settings,
quality, counters and diagnostics.

Keep strict codec round trips and `WorkerCodecStateTests`' field census. Replace
local/worker, repeat, warm/cold and processor-count byte/hash gates with input
capture/policy equality, independent fixture properties and geometric locus
comparisons within fixture-declared document tolerances. Compare class/coverage
on tractable controls, topology/hole counts and analytic areas on sections;
fragment counts/order/native duplicate owner need not match across solves.
Record unexplained HLD differences with manifests; do not widen tolerances to
make failures pass. Native variation itself has no universal equality guarantee.

Version visibility/timing keys with policy and assembly/Rhino fingerprints; old
records miss, including durable records. Cache one completed realisation; preserve
sorting without promising repeatability. Generation/request/view/scene identities,
leases, complete-current publication, stale-reply rejection, cancellation and Job
Object caps stay strict, independent of result bytes. No heavy in-process fallback.

## Unresolved (output index 6)

Keep refused/uncertain section markers, unsafe trim/cap/provenance, ambiguous
scoped cuts and recoverable invalid extraction. Safely drop/count empty slots and
exact zero-length records; retain detachable nonzero Unset curves as Unresolved.
Only a failed HLD solve (null drawing or unavailable frame) quarantines all projection.
Rejected sources and failed segment extraction are skipped, counted and diagnosed;
Unset never spreads to neighbouring curves. A detached Unset curve alone is Unresolved,
retaining its far-candidate provenance. An undetachable record is dropped with a diagnostic.
Unset point visibility is checked before SourceObject access, inside a guarded inspection;
Unset/source-less points are suppressed without quarantine. A failed participating body
clip demotes only pieces overlapping that body's projected SectionFootprints and warns
by source. Classification/region/merge refusals do not affect successfully clipped and
registered bodies. Cut/Hatch admission and refusal ownership remain independent.
Accepted HLD labels are native results; Unresolved cannot enumerate every Rhino error.

## File-level implementation/deletion plan

Paths below are relative to `src/`; service filenames are in
`M2DPlus.Rhino/Services/` unless qualified.

| Action | Files and call sites |
|---|---|
| Rewire | `NativeDrawing.cs:296`: remove Engine dispatch; `:322–325`: remove composition. Extract its plain HLD setup/extraction into shared `HldProjection.cs`; make `NativeHldOptions.cs` enforce threaded/requested-hidden policy. |
| Separate sections from projection | `SectionDrawing.cs:181–253`: replace composed/plain branches with the shared adapter; retain registration/provenance/far/crop assembly. Replace `Hidden` point rays with HLD points. Extract owned region/clip/refusal assembly to `Sections/SectionPreparation.cs`; keep shared validation/components in `Sections/SectionRegionEngine.cs` (currently `prepareVisibility` couples trim/cap and region checks). |
| Keep/move exceptions | Keep `Sections/*`, `SectionProjectionBounds.cs`, `ViewSourceCull.cs`, `GlassGeometry.cs`, capture/scene ownership, frames, viewport, placement, preview and Bake. Move `CurveIntervals.cs` to a shared geometry location: `EdgeProvenance`, `NativeDrawing.Crop` and `DrawingPresentation` use it. Move `VisibilityInput` out of `ComposedVisibility.cs` (drop KernelCandidate), retaining `VisibilityInputOrder.cs`. Move guarded `Inspect`/`TryDetach` from `NativeUncertainty.cs` into `NativeExtractionDiagnostics.cs`; replace proof/recovery with conservative failure handling. |
| Delete after B works | Entire `M2DPlus.Core/Visibility/*`; services `AnalyticVisibility.cs`, `AnalyticCutTube.cs`, `ComposedVisibility.cs`, `CertifiedMesh.cs`, `RationalDepthCertificate.cs`, `NativeVisibilityVerifier.cs`, `DenseNativeAudit.cs`, `BoundedCurveCover.cs`, `PlanarExtraction.cs`, remaining `NativeUncertainty.cs`, and obsolete `NativeModeDrawing.cs`. Sections/culling do not require PlanarExtraction; remove its eligibility/preparation hook in `Scene.cs`, kernel fields and certified-mesh cache. |
| Update contracts/callers | `DrawingPresentation.cs`/`DrawingCache`: remove native region bypass and Engine conditionals; always restyle certified regions. Update `ViewNotices.cs`, `PhaseTimings.cs`, Core `Progress.cs`, `Settings/SettingsValues.cs`, `SettingsModels.cs`, `SettingsResolution.cs`, `WorkerSettingsConverter.cs`; GH `DrawingComponent*.cs`, `UI/Model/{SettingsCatalog,MenuModel,SettingsPresentation,ComponentUiState,UiStateSerializer}.cs` and view-object defaults. Update `WorkerCodec.cs`, `DrawingWorkerPool.cs`, `DrawingWorkerServer.cs`, Core `WorkerProtocol.cs` and affected harness callers. |

Measured candidate files contain 2,802 Core + 4,354 Rhino lines; keep the 47-line
CurveIntervals utility separately. With diagnostic extraction, old native mode
and routing/cache cleanup, expect roughly **7,000 production LOC removed**, before
replacement adapter code; tests/tools are additional. Archive exact-only
`tools/{contact-census,routing-audit,hc-perf}` and worker-bench `KernelBench.cs`;
remove exact/audit commands in `tools/perf-f`, rewire `tools/render/ViewCaseRunner.cs`
and worker-bench `EngineFastBench.cs`/`NativeHldBench.cs`. Do not leave broken tools
or direct Compile links to deleted files. Historical evidence stays historical.

## PR sequence and acceptance

One PR, internally staged: extract shared code without behaviour changes, switch all
projection to HLD, then delete the exact engine/tests/tools and reconcile docs.
Do not commit, push, install, publish or bump versions in implementation.
The local archive tag `archive/exact-engine` marks main `0cfc171` (beta.8).

### Section certification invariant

Projection preparation never determines Cut/Hatch admission. Preserve the existing
near-cap recheck after a failed whole-source trim, the stable-region exception,
refusal markers and owned lifetimes. A valid component may retain its certified
region when a sibling fails. Cut-only, zero depth and positive sub-tolerance depth
must exercise this same admission path.

### Failure policy

Owner/orchestrator review decision: this local-orange policy overrides the earlier table.

| Evidence | Projection | Cut/Hatch |
|---|---|---|
| HLD AddGeometry rejects a source | Skip/count it; named warning; no quarantine | Independent admission unchanged |
| One segment has no tag/curve or extraction fails | Drop/count/diagnose that segment only | Unchanged |
| Detachable nonzero Unset curve | Only that curve Unresolved; preserve FarCandidate; no footprint spreading | Unchanged |
| Undetachable Unset curve | Drop/count; per-source diagnostic where available; no spreading | Unchanged |
| Unset/source-less/invalid point | Check Unset before metadata access inside try; suppress/count/diagnose | Unchanged |
| Participating body clip fails | Demote only overlapping projected footprint pieces; named warning | Keep certified regions; publish refusals |
| Classification/component/merge refusal but visibility clip succeeds | Keep native projection; no footprint or quarantine | Independent certificate/refusal rules |
| Unsupported input | Report skipped source; native labels on other sources stand | Independent admission unchanged |
| Successful section recovery | Accept HLD labels; no recovery-triggered proof | Keep certified recovered regions |
| HLD null drawing or unavailable frame | Whole-view projection quarantine; do not cache this failed realization | Keep certified regions independently |

Zero depth and positive sub-tolerance depth have distinct grey notices when cut-only is off,
including views with no crossing bodies. They never claim that Cut only was enabled.
Worker admission allows one full native pool below 16 logical cores, at most two on larger
hosts, also limited by the configured count, views and memory. DOTNET_PROCESSOR_COUNT
controls only managed code; no native thread cap/CPU affinity is promised. On larger hosts
two native solves can oversubscribe the machine by roughly two full pools; other Rhino
processes remain outside this policy. Keep this explicit limitation instead of pretending
managed processor advertisement controls Rhino's native pool.

Mandatory point gates: isolated points, curve+point scenes, behind caps/through
holes, excluded points and worker round trip. HLD is the only point classifier.
Legacy edits change remarks only, including component run and worker timing
identity; hidden invalidates projection. Schema 4 invalidates old durable records.
Coincident-source fixtures cover source trees and Bake; fresh solves may change
duplicate ownership/source attribution and Ignored. Hidden-off disables min-visible;
joints suppression is gone. These changes must be disclosed in Info and release notes.

Future verification (none run here): retain Core ownership/cache/scheduler,
section policy, settings/units, min-visible, output-tree and worker protocol tests.
Delete Core `AnalyticCsgTests`, `AnalyticVisibilityTests`, `BandedMeshIndexTests`,
`ContactTests`, `SmallFeatureKernelTests`, `SurfaceCertificateTests`,
`VisibilityTests` and kernel-only `UnitScaleTests`. Rewrite `EngineSettingsTests`,
`CutOnlySettingsTests` and affected settings-key expectations. Keep all GH
UI-model suites; rewrite Menu/Persistence/Presentation/CutOnly expectations for
ignored saved engine/joints, hidden invalidation and absent menu items.

Delete Rhino `AnalyticSoundnessTests`, `AnalyticVisibilityTests`, `CompositionTests`,
`DenseNativeAuditTests`, `NativeVisibilityVerifierTests`; replace contact tests'
kernel assertions with native-policy controls. Rewrite `EngineNativeTests`,
`WorkerNativeSettingsTests`, `WorkerTests`, startup/retry processor/equality gates
and `UncertaintyReductionTests` for the contracts above. Preserve section
regressions, offset/units/small-feature fixtures, geometry matrix, glass, cut-only,
scope/culling, cache/rerun, lifecycle, GH wiring, preview/Bake and codec-state
tests; remove embedded kernel oracle/routing assertions from mixed suites
(`OffsetSection`, `UnitScaleRhino`, `SmallFeature`, `ReviewBeta4`, `SectionWiring`).
Keep `SectionProjectionAndIntervalsTests` for bounds and curve trimming.

Run normal Debug/Release and Core/GH checks in implementation. Licensed checks
require explicit `M2D_RHINO_TESTS=1`, the existing compatible resolver and .NET 8/.NET 10 hosts/workers (up to three headless processes). Use **only** small fixtures, curved-S, diagrid,
curved-steel, curved-facade, space-frame, perf-S/M **planar**, and pipe node;
never curved-M or campus. Audit fixture selectors before executing suites.

Sections-on-HLD gate: closed outer/hole loops, independent area/containment and
non-null hatches for filled box/RHS/CHS cuts; aligned nulls for open/no-fill cuts;
crop closure with separate edge provenance; clear-glass background visible while
opaque blocks it; scoped excluded geometry retained; cut-only/zero-depth makes
zero HLD calls; far none/line/noline, hidden far intervals and Cut precedence
correct; no synthetic cap strokes leak. Check points behind caps/through voids
and in area-free scenes. Round-trip and present these on workers; check hatch
save/reopen and stale/cancelled generations cannot publish.

## Risks and remaining questions

Known S2 native/physical clipping disagreements make route C authoritative.
Area-free HLD points can be Unset: suppress with per-source diagnostics, never retain
`SectionDrawing.Hidden` as another engine. Establish point support on the pinned
build. Threading can change loci/owners and Ignored results; cache stabilises reuse
only. Hidden-off HLD can also change visible geometry. Bound worker admission
against CPU oversubscription while keeping HLD threaded. Open validation questions:
point limits and fixture tolerances/expected loci. Measure speed; disclose
hidden-off min-visible degradation and loss of certified contacts in release notes.

## Documentation reconciliation map

| Document | Sections |
|---|---|
| `docs/PLAN.md` | What the research says (exact/convex route); Contracts 2/3/6 (quality, threaded execution, cache); Architecture; Test strategy (worker/repeat gates); Milestones M1b/S2/M3/M4/S4/M5; Spike decisions (retain route C). |
| `docs/REQUIREMENTS.md` | Principles cut-only paragraph; Scope/Engine; Geometry that must work (point limits); Drawing features (contacts); Decisions after research (glass/cut/execution); Decisions after the hill climbs; Engine composition decisions; Ignoring objects seen only through small gaps; Pre-beta polish (remarks/re-runs); Exact and native engines (replace with this decision). |
| `docs/design/ux.md` | §1.2 Unresolved; §2.2 user-text table/defaults; §3.2 cut-only, §3.8 edges/engines/glass, §3.9 min visible; §5.1/5.1a caching/re-runs, §5.2 phases/severity, §5.4 Info; §6.2/6.3 copied settings/key reference; §7 menu; §10 engine alternatives; §12.1/7/9/12/14 engine/caching/report contracts. |

Also update AGENTS introduction/runtime equality, `docs/design/drawing-workers.md`
boundary/protocol/verification and mark `docs/design/engine-composition.md`
superseded with the archive tag/SHA. Preserve dated experimental results with
their original fingerprints rather than rewriting them as HLD-only evidence.
