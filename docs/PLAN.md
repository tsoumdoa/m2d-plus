# M2D+ plan

Status: v2, 2026-11-14. It folds in [Astra's review](design/plan-review-astra.md). Inputs: [REQUIREMENTS.md](REQUIREMENTS.md), [design/ux.md](design/ux.md), [research/](research/).

## What the research says

- **Native HLD is the cost.** On the campus plan, `HiddenLineDrawing.Compute` took 84 of 90 s. Making everything around it faster gains at most about 7 % per view. The main lever for one view is to **give HLD less to solve**: conservative crop, depth and cut culling, with per-source clipping scope respected. For drawing sets, it is **bounded parallel workers**. Two levers are experiments behind their own correctness gates, not part of the baseline promise:
  - partitioning a view into tiles (S3);
  - convex routing is retired; see `archive/exact-engine` (`0cfc171`, beta.8).
- **Repetition helps per-object stages, not the global solve.** Reusing definition-local meshes was 44× faster for that stage, and silhouettes 8–19×. Native HLD with per-instance transforms gave no speedup. Reuse pays off in sections, silhouettes, culling and cross-view preparation.
- **Avoid HLD whenever we can.** A valid cached result, or a change that only affects style or placement, must never call visibility. Changes to material/opacity and clipping scope do invalidate it.
- **Rhino HLD is the only visibility engine.** All sources and points use threaded HLD with the requested hidden flag. Route C physically clips/caps participating sources; regions/hatches remain independently certified. No custom visibility classifier, verifier or joint suppression, except the opt-in **Pipes (experimental)** engine (`visibility = pipes`; see its [README](../src/M2DPlus.Rhino/Experimental/PipeVisibility/README.md)). See [native engine](design/native-engine.md).
- **Sections need a new pipeline:** per-object material regions → crop the regions → optional merge of compatible styles → hatch. Region and hatch construction is feasible (1.8 s for 500 hollow objects), but the semantics are hard.
- **Native quirks become regression fixtures:** hidden lines that vary under multithreading, Unset proxies, occluding-section mode dropping points, and blocks/SubD/points needing explicit normalisation.

## Contracts decided before engine code

1. **Ownership.** The host/UI thread resolves the document, views, definitions, attributes, styles and GH inputs. Workers get *owned* geometry plus plain values. Nothing crosses the boundary that touches `RhinoDoc`, a live `RhinoObject`, GH documents or the UI.
   - The cache owns its entries. Jobs and previews hold leases, and an entry is disposed only after its last lease ends.
   - GH outputs are independent copies.
   - Every request carries scene/view/settings versions and a generation number. Only a current, complete result is published, with all views together.
2. **Quality is separate from execution state.** Each view and source records its route, normalisation changes, uncertain intervals, invalid native extraction and region status. Known uncertainty only goes to Unresolved or Info. Rejected/unsupported sources and failed records are skipped with diagnostics, without spreading uncertainty. Failed participating section clips demote only overlapping footprints. Whole-view projection quarantine is reserved for an HLD solve with no drawing/frame.
   - "Never wrong without saying so" means: no known or detected uncertainty is silently promoted, and the validated domain is stated honestly.
3. **Execution.** Threaded HLD, one call at a time in each process-wide budgeted scheduler for all components. UI cancel (immediate, stale results rejected) is kept separate from native resource release. No new native work starts while a cancelled call is still alive. Hard kill and memory caps are promised **only on the worker route**.
4. **Section styles** follow Rhino's source modes: By Layer / By Parent (nested blocks) / By Clipping Plane / Custom. Resolved pattern definitions are snapshotted, not document indices. "No style" (→ solid poché) is distinct from an explicit "no fill".
5. **Cut/Hatch output shape.** A region branch holds all of its loops (outer + holes) in Cut, and one hatch or null in Hatch. Open outlines get their own outline branches with null hatches. Crop-created edges keep a separate provenance from physical cut edges.
6. **Cache identity** is layered: definition-local prep → source × local cut plane regions → view visibility → style/placement → serialized result. Keys use document/definition-leaf/instance-path identity plus content fingerprints, never 32-bit CRCs alone. Disk records carry policy/assembly/Rhino/schema fingerprints and a checksum; old records miss. Cache retains one completed realization.

Pre-HLD exclusion (owner, 2026-11-19): before capture, objects and block instances whose cheap bounding box lies completely outside every requested view volume (outside the profile; for cut participants, wholly in front of the front face or beyond the back face where far clip applies) are excluded: their geometry is never copied, fingerprinted or normalised, though they stay watch dependencies. Applies only when every view has a fixed volume: an M2D+ view box, a GH Box, or a Rectangle with authored axes and explicit depth; other requests keep the whole scene. Scene bounds are the survivors' bounds, which changes only the HLD camera radius, so outputs are compared by locus, not bytes.

## Architecture

```
M2DPlus.Core (net8.0, no Rhino types, xUnit)
  ids/revisions/generations, settings parser (key = value, units), view-source rules, line classes,
  style-source resolution rules, placement math, interval math, region topology rules,
  cache/lease & scheduler policy, job/progress/cancel model, quality/report model
M2DPlus.Rhino (net8.0-windows, RhinoCommon library; no GH dependency)
  Host side (UI thread only): snapshot/normalise (blocks as definition + transform chain, SubD, points),
                              view resolution, style resolution, bake
  Compute side (no document access): prepared-scene cache, threaded HLD adapter (+ points),
                              culling, section regions/crop/merge/hatch
M2DPlus.Worker (exe): headless Rhino host; versioned request/result protocol; reuses the compute side
M2DPlus.Grasshopper: the one component, attributes (progress, Bake), menu, view objects (view boxes, m2d.* user text), preview, publication
tests/  Core.Tests (xUnit) · Rhino.Tests (Rhino.Inside, frozen fixtures + independent oracles, serial)
tools/  Pack-Yak.ps1 · Publish-Yak.ps1 · bench harness (process-tree RAM cap, benchmark manifest)
```

## Test strategy (layered oracles)

1. Independent analytic full-output truth: boxes/polyhedra, curves and points behind solids, spheres/cylinders, hollow sections, adversarial contacts. Check missing *and* extra loci, intervals, holes, and overlap/retracing.
2. Independent section topology and area checks, using component/hole counts and containment. They must not reuse the production pipeline.
3. Metamorphic checks:
   - rigid transforms and mirrors, units, permutation;
   - block vs expanded geometry, crop vs full;
   - worker input/settings/fingerprint equality; independent fixture loci and lossless single-result transport; cache on/off, edit vs clean recompute;
   - no repeat byte-identity promise for fresh threaded HLD solves.
4. Held-out scenes, plus an explicit "unknown" status for general freeform input.
5. Lifecycle and failure injection: native failure, worker crash/OOM, stale reply, deleted definition, missing pattern, document close mid-solve.

Each benchmark records a manifest: engine/fixture/oracle hashes, Rhino build, route, threading, tolerance, cache state, worker count, cold/warm, wall/CPU/peak memory, time to first preview and time to complete result. Small fixtures are frozen in the repo. The 527 MiB campus stays an external, hash-verified asset. A generated 50k-object, high-overlap fixture is added.

## Milestones and spikes

Each milestone is a set of small PRs. Sol implements, Claude reviews and commits, and the owner gets a `.yak` after each milestone.

| Stage | Scope | Gate |
|---|---|---|
| **M0** | Solution and projects as above. Directory.Build.props (warnings as errors), MIT LICENSE, AGENTS.md/CLAUDE.md, pre-commit hook, Yak tooling, icon. Component registered with its **final** inputs/outputs (stable GUIDs, empty outputs plus a diagnostic). Worker entry-point skeleton. Fixture manifest. | `.yak` loads; Core and Rhino test projects run |
| **S1a** (during M0) | Worker feasibility: licensing with interactive Rhino plus 1..n workers, matching-runtime handshake, smallest scene/result round trip, kill/crash/Windows job-object memory limit. Plus an ownership/lifetime micro-spike. | Go/no-go decision on worker transport and unavailable-worker behaviour |
| **M1a** | Smallest walking skeleton. Owned versioned scene, one global scheduler, native route. Two direction/vector views of a box + crossing curve + point + asymmetric nested block. Generation-gated batch publication, detached outputs, leases, cancellation races. A second component reuses preparation. | Lifecycle acceptance tests: edit/delete while drawing, document close, cache clear mid-solve, mutate output, cancel at completion |
| **M1b** | Baseline capability: full normalisation, all *uncut* view forms (named view, keyword, vector), guarded native extraction diagnostics, 305-object six-view run, geometry matrix with property-level P/F/U gates. | Matrix gates; no known uncertainty promoted |
| **S2** (during M1, blocks M2) | Native clipping vs physical trim on the counterexamples: points, Unset proxies, scoped clipping, zero depth, flipped plane, contact tolerance. Probes for style source modes and the region output shape. | Decision recorded in docs/design |
| **M2** | Sections: Plane/Rectangle/Box/clipping-plane views, per-object material regions, open outlines, region crop, compatible-style merge, Rhino section-style resolution, per-source scope/depth. | Region/hatch/path/export gates, not area alone |
| **M3** | Target placement, Settings + right-click menu + view objects (UX rev. 4: the View Range editor was removed), line classes/weights/depth cueing/far clip, glass, tangent/seam, preview, one-undo replace-bake. | Style/placement edits make zero HLD calls; baked hatches survive save/reopen |
| **M4** | Dependency-aware invalidation, definition/section reuse, bounded memory, durable disk cache. **S1b:** cold/warm/multi-view IPC benchmark and threshold calibration. Production single-worker isolation ships. | Heavy jobs go to the worker; reopen reuses the cache |
| **S3** (after a stable M2) | HLD scaling with overlap, safe partitions, seam equivalence against the monolithic solve. | Gates partitioning only |
| **S4** (after the oracles; an eligibility census can run early) | Retired: custom exact visibility deleted; archive `archive/exact-engine` at `0cfc171`. | Historical only |
| **M5** | Calibrated heavy-job admission, bounded parallel workers, campus + 50k/high-overlap tests, cancellation/resource recovery, UI responsiveness. No custom visibility engine. | Campus set completes within the memory policy, and Rhino stays responsive |
| **M6** | Release checks, documented supported/uncertain domains and worker/licence behaviour, examples, installed-Yak smoke tests, public beta. | Public beta |

## Spike decisions

- **S1a (2026-11-14): go** for the worker route, with one persistent worker and named pipes. It must match the host runtime (Rhino 8.36 defaults to .NET 10). See [s1a-worker.md](research/s1a-worker.md).
- **S2 (2026-11-14): route C.** Owned copies are physically trimmed and capped before HLD, with no global native clipping planes. Section regions are built independently. Points are fed to HLD with the same clipped occluders; valid Unset points are suppressed with per-source diagnostics, without quarantining the view. The far plane is trimmed physically, and the Far clip curves are made from their own intersections. Scope is resolved before clipping. Exact contact is never nudged. See [s2-clipping.md](research/s2-clipping.md).

## Working rules

- Sol (Codex `gpt-6.1-sol`) implements and tests. Astra (`gpt-6-astra`) reviews designs. Opus handles UX work. Claude reviews, commits and opens PRs.
- Small squash-merged PRs. Stable component GUIDs. A local hook builds and tests every commit. No cloud CI. Yak is the only install path.
- A measurement claim needs a manifest and an independent oracle. `ok` never means correct.

## HLD-only cutover (2026-11-17)

One PR contains extraction, routing, settings/UI/Info, schema 4, exact-engine deletion
and documentation. Engine/joints are compatibility-only; hidden invalidates projection.
Min-visible is disabled with a grey remark when hidden is off. Fresh solves may change
duplicate ownership/source attribution and Ignored. Sorting does not promise native
repeatability. Section admission, ownership, failure quarantine and current-generation
publication remain strict. See the native-engine note for the acceptance gates.
