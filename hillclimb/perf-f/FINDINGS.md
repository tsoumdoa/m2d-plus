# PERF F — where section and curved time goes, and the next wins

Research date: 2026-11-15. Nothing committed, pushed, installed or published.
Base: `main` at `ffc33a4`.

**Status.** Sections 1–4 are the investigation, with its switch-driven prototype. Win 1 has since
been turned into production code in this worktree; **section 5 describes what shipped, the final
timings against `main`, and two things the investigation got wrong or did not see.** The
prototype switches named in section 3 no longer exist.

**Headline.** The section hot spot is not trimming or region building. It is a per-source
self-intersection check (`SectionGeometry.ValidateFaceIntersections`) that runs twice for every
body in the scene, whether or not the cut plane touches it. Removing the duplicate work and
sharing the verdict between congruent sources takes the L long section from **270.0 s to 41.7 s
(6.5×) with byte-identical output**. Native HLD is then 88 % of what is left.

## Conditions

| Item | Value |
|---|---|
| Rhino / runtime | 8.36.26296.17001 / .NET 8.0.34, Windows x64, `multipleThreads=false` |
| Machine | Ryzen AI MAX+ 395, 32 logical cores. **Under contention throughout**: two other long benchmark jobs were running. Each row in the timing JSON records machine busy fraction (7–16 %) and an estimate of foreign busy cores (0.2–3.7). |
| Process rule | One owned Rhino.Inside process at a time. Baseline and candidate run back to back in the same process on the same prepared scene. |
| Samples | n = 1 per variant unless stated. The M baseline was run at both ends of one process: 59.9 s and 62.6 s, so treat differences under ~5 % as noise. |
| What a time includes | One view solve (`NativeDrawing.SolveView`), called directly. It excludes file open, capture and scene preparation (L: 0.27 / 1.53 / 0.57 s) and the tolerance census. |
| Admission | The harness bypasses the in-process admission cap. Through the public path, L is refused today ("Worker unavailable: too large for in-process"), so L timings are not reachable from the component in this build. |

Harness: `tools/perf-f/` (not in the solution). Raw logs, per-run timing JSON and full drawing
dumps: `artifacts/perf-f/` (git-ignored).

## 1. Phase profiles

### L long section (perf-L-blocks, 20,000 sources, cut at the scene's central Y)

Seconds. Candidate = `single+cache+behind+overlaps+subtract+parallel` (no policy change).

| Phase | Baseline | Share | Candidate |
|---|---:|---:|---:|
| Self-intersection validation (inside `Convert`, 2 × 20,000 calls) | 174.7 | 64.7 % | 0.4 (29 calls; 19,971 cache hits) |
| Other conversion, component split | 3.5 | 1.3 % | 2.6 CPU |
| Classification against the plane | 2.0 | 0.7 % | 1.5 CPU |
| Trim (`Brep.Trim`, 1,299 crossing) | 2.1 | 0.8 % | 2.8 CPU |
| Cap + join | 2.2 | 0.8 % | 3.8 CPU |
| Trim-edge provenance | 7.0 | 2.6 % | 1.0 CPU |
| Section loops + planar regions | 1.7 | 0.6 % | 2.8 CPU |
| Overlap report, region build, hatch | 0.7 | 0.3 % | 0.4 |
| **Section preparation, wall** | **195.6** | **72.4 %** | **2.4** |
| Native HLD solve (9,746 inputs) | 38.1 | 14.1 % | 36.6 |
| Extraction: subtract cut loci from native segments | 33.7 | 12.5 % | 0.5 |
| Extraction: other | 0.8 | 0.3 % | 0.8 |
| Region assignment, finalize | 1.4 | 0.5 % | 1.0 |
| **View solve, wall** | **270.0** | | **41.7** |

"CPU" rows are summed across up to 8 worker threads, so they exceed their share of wall time.

Three things were hiding inside "trimming and region building":

1. **Validation, twice per source.** `PhysicalSectionClipper.Clip` and `SectionRegionEngine.Compute`
   each call `SectionGeometry.Convert`, which runs a face-pair `Intersection.BrepBrep` check on
   every body. 10,138 of the 20,000 sources are wholly in front of the plane and 7,284 wholly
   behind; all of them pay it twice.
2. **Subtracting cut loci is quadratic.** Each of 29,612 native segments is tested against every
   cut loop, and `EdgeProvenance.Split` recomputes each loop's bounding box on every test.
3. **The overlap report is all-pairs.** `ReportOverlaps` builds a planar arrangement for every
   pair of regions with incompatible styles, with no bounds test. It is small on L (0.2 s) but
   3.0 s on the M long section and **13.9 s on the M plan**.

Actual trim, cap and region work is 6 s of 270 s. The S3 report attributed 32 s (M plan) to
"trim/cap"; that figure was measured around a call that includes `Convert`, so it was mostly
validation. Definition-local cut reuse (cut once per local plane, then transform) can now save
at most about 2 s of wall time on L and is no longer a priority.

### Curved S, SW view (curved-perf-S-blocks, 500 sources, 8 block definitions)

| Phase | Seconds |
|---|---:|
| Open / capture / prepare | 0.11 / 0.16 / 0.01 |
| Native setup (routing-branch table) | 0.03 |
| **Native HLD solve** | **23.6** (23.65, 23.34 repeat; identical output) |
| Output construction (routing-branch table) | 0.25–0.28 |

All 500 sources are non-planar, so planar routing removes nothing here. Native is opaque, so the
solve was attributed by subsets (cost attribution only, not drawings):

| Definition | Instances | Faces | Alone | Saved when removed |
|---|---:|---|---:|---:|
| `f31421e7` sphere | 82 | 1 sphere | 5.43 | 7.9 |
| `07ba956b` freeform solid | 82 | 2 freeform + 2 planar | 5.25 | 6.6 |
| `f278ad58` freeform solid | 84 | 2 freeform + 2 planar | 3.13 | 5.9 |
| `f2e836f1` open freeform | 84 | 1 freeform | 0.42 | 2.8 |
| `b1c320ed` torus solid | 42 | 2 torus + 2 planar | 1.63 | 1.9 |
| `a41102b2` cylinder solid | 42 | 2 cylinder + 2 planar | 0.89 | 1.8 |
| `48d70d4d` open freeform | 42 | 1 freeform | 0.40 | 1.2 |
| `6943c696` freeform solid | 42 | 1 freeform + 2 planar | 0.35 | 0.3 |
| Sum of "alone" | | | **17.5** | |

The eight single-definition solves add up to 17.5 s of the 23.6 s (74 %). On this model the cost
is mostly **per object** (silhouette and curve generation), not interaction between objects.
82 identical spheres cost 5.4 s by themselves — 66 ms each — to produce 84 visible arcs.

## 2. Ranked ideas

Gains are end to end on the view solve. "Measured" means a back-to-back pair in this study.

| # | Idea | Gain | Correctness risk | Size |
|---|---|---|---|---|
| 1 | **Validate once, share the verdict, index the two quadratic loops, run per-source work in parallel** (prototype below) | Measured. L long 270.0 → 41.7 s (6.5×). M long 59.9 → 9.8–11.0 s. M plan 63.3 → 3.4 s (19×). M exploded long 58.3 → 16.4 s cold, 9.5 s warm. Curved S sections 95.6 → 8.6 s and 106.8 → 11.0 s. | Low. Output is byte-identical on all six pairs. The one assumption is that the self-intersection verdict is the same for congruent copies of a shape (see §3). | Small: about 230 changed lines in 7 files plus a 90-line helper. |
| 2 | **Route cut views through the planar kernel** once the in-flight routing branch lands. Trimmed and capped pieces of planar solids are still planar polyhedra. | Estimated 2–2.5× on what is left after #1 (L long ≈ 42 → 17–20 s; M long ≈ 10 → 5 s). Basis: native is 88–90 % of the remaining time, and the routing branch reports L North 72.7 → 28.8 s and M North 16.7 → 8.5 s end to end. Not measured here. | Medium. Needs the routing branch's gates extended to trimmed inputs, cap edges and the cut/far provenance split. | Medium, mostly wiring. |
| 3 | **Skip validation for sources clear of every cut plane** ("slab" switch) | Measured. M exploded long 16.4 → 9.6 s cold. No gain on block models, where the cache already removes the cost. | **A policy change, not a free win.** On curved S long it changes the drawing: 84 sources the baseline quarantines are drawn (Visible 931 → 1,491 pieces, Unresolved 742 → 391). Uncut views already pass the same objects to native with no such check, so it is consistent, but the owner should decide. | Tiny (in the prototype behind its own switch). |
| 4 | **Keep the verdict with the scene across views** | Measured as the "warm" row: M exploded 16.4 → 9.5 s. The verdict does not depend on the plane, so every section after the first pays nothing, with no policy change. Gives exploded models what #3 gives, from the second view on. | Low. Needs the cache hung on the scene lease with its lifetime rules instead of the prototype's static dictionary. | Small. |
| 5 | **Analytic spheres, cylinders and tori in the exact kernel** | Estimated ceiling on curved S SW: removing the sphere, cylinder and torus definitions saves 7.9 + 1.8 + 1.9 s when removed one at a time (not additive), so at best roughly 23.6 → 12–14 s. Cylinders alone: 23.6 → 21.7 s measured as a counterfactual (8 %). Spheres are the larger target on this model. | High. The kernel's occluders are planar; a quadric has a nonlinear depth field, so exact occlusion needs new predicates and a new oracle. | Large. |
| 6 | **Polyhedral inner/outer enclosures for curved occluders** (the "change everything" idea) | Not measured. Solve every curved occluder twice in the planar kernel, once with a mesh inside the surface and once outside. Where the two answers agree the result is certain; where they differ the interval goes to Unresolved or to a small local native solve. Silhouettes come per definition and relative view direction (8–19× on that stage in earlier research). Kernel times are seconds where native takes 24–310 s. | High but has a correctness story: disagreement is never promoted to Visible. Band width and the Unresolved share on real models are unknown. | Large; needs a spike first. |
| 7 | Replace each closed sphere by its viewer-facing hemisphere before native | Measured 23.6 → 18.7 s (−21 %); spheres alone 5.4 → 2.5 s. | **Not accepted.** Fails the locus gate: 0.14 m of baseline linework missing and 0.35 m extra out of 4,449 m, with visible/hidden change points up to 18 mm apart. Both outputs lie on the exact silhouette circles (see §3), so the two native solves disagree about where occlusion starts. No oracle here says which is right. | Small. |
| 8 | Definition-local cut reuse (cut once per local plane, transform) | At most ≈ 2 s wall on L after #1; trim + cap + loops + regions total 6 s serial. | Medium (mirrors, contact, tolerance under scale). | Medium. |
| 9 | Shrink trimmed faces to their trim bounds before native | Measured: none (23.69 vs 23.65 s, identical counts). | — | Rejected. |
| 10 | Split closed / bipolar / kinky faces before native | Measured: slower (25.2 vs 23.6 s) and the split edges are emitted, so output changes (Visible 1,534.7 → 1,632.0 m). | — | Rejected. |

Informational only: a 10× looser native tolerance gives 23.6 → 17.0 s and a different drawing.

What dominates once planar goes to the kernel, from the routing branch's own table (L North,
28.8 s): kernel solve 5.6, native solve 7.2, native extraction/duplicates 6.7, native setup 3.0,
planar extraction 1.5, interval union 1.3, output 1.0. Roughly half of it is no longer a solver.
The extraction/duplicates step grew from 0.9 to 6.7 s in that branch and is the obvious next
target there. I did not touch or re-measure that branch.

## 3. Prototype results

Switches live in `src/M2DPlus.Rhino/Services/Perf.cs` and default to off. With all of them off
the refactored code reproduces the original output (same dump hash as the pre-refactor
instrumented build on M long: `970DB15B…`).

| Switch | What it does | Changes output? |
|---|---|---|
| `single` | One conversion, component split and near-plane classification per source, shared by the visibility clip and the region pass. Also halves the freeform certificate cost. | No |
| `cache` | Self-intersection verdict keyed by definition content key (+ scale for similar, non-rigid transforms; none for non-similar) and tolerance. | No on tested models |
| `behind` | A piece that is an untouched duplicate gets "original" edge provenance directly. | No |
| `overlaps` | Bounds test before building an arrangement for a region pair. | No |
| `subtract` | R-tree over cut loci; `EdgeProvenance.Split` still applies its own exact bounds test. | No |
| `parallel` | Per-source work in `Parallel.For` (8 threads), merged in source order. | No |
| `slab` | Skip validation when the source is more than 2 × tolerance clear of every plane. | **Yes, on models with flagged sources** |

### Before / after pairs (same process, same scene, seconds)

| Model, view | Baseline | Candidate (no policy change) | Speedup | Dump hash equal |
|---|---:|---:|---:|---|
| perf-L-blocks, long section | 270.0 | 41.7 | 6.5× | Yes (`AD7F4B01…`) |
| perf-M-blocks, long section | 59.9, 62.6 | 11.0 (serial), 9.8 (+slab+parallel) | 5.4–6.2× | Yes (`970DB15B…`, 7 runs) |
| perf-M-blocks, plan z = 1.2 | 63.3 | 3.4 | 19× | Yes (`10F2F6F2…`) |
| perf-M-exploded, long section | 58.3 | 16.4 cold; 9.5 warm | 3.6×; 6.1× | Yes (`C0352477…`, 5 runs) |
| curved-perf-S-blocks, transverse | 95.6 | 8.6 | 11× | Yes (`EDE879E0…`) |
| curved-perf-S-blocks, long | 106.8 | 11.0 | 9.7× | Yes (`2AB78D2A…`) |
| curved-perf-M-blocks, sidecar "Section transverse" | timed out at 600 s (earlier run, not repeated) | **48.9** | — | No baseline to compare |

Step by step on M long (blocks): base 59.9 → `single` 39.2 → `+cache` 16.8 →
`+behind+overlaps+subtract` 11.0 → `+slab+parallel` 9.8. Native solve is 8.4–9.1 s in every row.

On the exploded model nothing is shared between sources, so the first view depends on
parallelism: the validation stage goes 20.4 s serial → about 7 s wall on 8 threads (56 s of CPU;
scaling is poor under contention). `slab` or a warm cache removes it.

The curved-M section finishing in 48.9 s is consistent with the cause above: curved faces make
each face-pair check expensive, and the baseline ran about 10,000 of them. That run has no
baseline to compare against; its evidence is the identical output on the two curved-S pairs,
which take the same code path.

### Correctness evidence

1. **Byte-identical dumps.** After each run the harness writes every piece (class, source
   identity, profile flag, coordinates in round-trip format), every region (id, sources, status,
   area, hatch presence, each loop's hole flag, edge origins and coordinates) and the sorted
   diagnostics. The SHA-256 of that file is equal for baseline and candidate in all six pairs.
2. **Independent comparator.** `tools/perf-f/compare.py` shares no code with the product. It
   samples each drawing by arc length and requires every sample to lie within 1.25 × tolerance
   of same-class linework in the other drawing, in both directions, and matches regions by
   sources, loop structure, area and length. L long passes (23,808 pieces, 1,183 regions, zero
   missed length either way). Four negative controls on M long all fail as they should: a
   dropped piece, a reclassified piece, a piece shifted by 3 × tolerance, a removed region.
3. **Tests.** `dotnet build M2DPlus.sln -c Debug`: 0 warnings, 0 errors. Core 211 and GH UI 40
   pass. All **146 licensed Rhino tests pass three times**: switches off, switches on, and on
   with `slab`. A marker file written from the test host confirms the switches were active.

Assumptions and limits to carry into review:

- **Verdict sharing.** The prototype took the verdict from whichever instance validated first.
  Production computes it on the definition's own geometry instead (section 5). One real case
  where an instance's own copy gets a different verdict turned up; it is reported there.
- **Parallel RhinoCommon calls.** The prototype ran every per-source step in parallel and the
  six pairs matched. That was luck, not safety: Rhino's curve-curve intersection is not
  repeatable under concurrent calls (section 5). Production keeps validation serial and gates
  every curve-curve call.
- The prototype cache was a static, unbounded dictionary. Production hangs it on the scene.
- Mesh sources are not covered: their `Convert` path (self-intersection scan, mesh-to-Brep) is
  still paid twice without `single` and is not cached.
- Not tested: .NET 10 host, crop and merge on large models, installed Yak, XL/XXL.

## 4. Recommended next step

Ship idea #1 as one small PR: `single`, `cache` (moved onto the scene lease, which also delivers
#4), `behind`, `overlaps`, `subtract`, `parallel`. It is the largest measured gain, changes no
output, and turns the two curved-M section timeouts into a 49 s solve. Leave `slab` out until the
owner decides the policy in #3.

Then extend planar-kernel routing to cut views (#2). After #1, native HLD is 88–90 % of section
time, so that is where the next factor of two is.

For curved models, do not start with analytic cylinders: on the curved S model they are 8 % of
the solve. Spike the enclosure idea (#6) or analytic spheres first, with an oracle for where
occlusion begins on a curved occluder — #7 shows native does not agree with itself there.

## Side findings (quality, not speed)

- **L long section is 38 % Unresolved by length**: 5,829 Unresolved pieces (5,575 m) against
  6,721 Visible (3,500 m), from 116 components classified Uncertain at the plane. Baseline and
  candidate agree; this is existing behaviour worth a look.
- **Curved S: the "self-intersecting" definition is a false positive of the validation.**
  Definition `f278ad58` (84 instances; a curved solid with two planar caps) is rejected with
  "Self-intersecting Brep faces away from their shared topology", so all 84 are dropped from
  section views and their footprints quarantined. Uncut views draw them. The geometry is fine:
  for every face pair, the face-face intersection curve lies on the edges the two faces share to
  within 1.4e-10 (0.000001 × tolerance), and no sample of any intersection curve is interior to
  both faces. The check fails in its attribution step. Each cap rim is split into two edges, and
  `Intersection.CurveCurve` reports the rim's coincidence with one of them as a single point
  instead of an overlap, and stops the other overlap about 4e-5 short. The check then sees an
  "unattributed" stretch 0.063 long whose midpoint is 1.8e-15 from the shared edge. Evidence:
  `artifacts/perf-f/final/selfcheck/log2.txt` (`selfcheck@f278ad58`). Not fixed in this change.
  A distance-based rule (an interval is attributed when it stays within tolerance of a shared
  edge) would not depend on how Rhino reports overlaps.
- **Native disagrees with itself on curved occlusion boundaries** by up to 18 mm (180 ×
  tolerance) between a sphere and its front hemisphere, which have the same silhouette and the
  same front surface.

## 5. Production change (win 1)

Everything below is the code now in this worktree, measured against `main` built from a clean
`git archive` (`artifacts/perf-f/main-src`). `main` and the new build ran one after the other, each
in its own Rhino.Inside process. The machine was quieter than during the investigation (busy
4–15 %), so `main` is faster here than in section 3.

### What shipped

| Part | Behaviour |
|---|---|
| One conversion per source | The visibility clip and the region pass share one converted copy, component split and near-plane classification (`SharedSource`). |
| Verdict per definition shape | The self-intersection verdict is computed once per (definition content key, scale, tolerance) on the **definition-local** geometry, scaled when the key carries a scale. It never depends on which instance asked first. Rigid placements, mirrors included, share the definition's key; a similarity adds its scale rounded to 12 significant digits; a non-similar transform gets no key and validates its own copy. |
| Scene-owned cache | `PreparedScene.SectionVerdicts`. Every view of a drawing set reuses it; it is cleared when the scene is disposed. A direct engine call without a scene shares verdicts within that call only. |
| Whole-piece provenance | A piece wholly behind every plane is an untouched duplicate; its edges are labelled original without a search. |
| Indexed loops | Bounds test before a region-pair arrangement; R-tree over cut loops when subtracting them from native segments. |
| Parallel cutting | Per-source cutting runs on min(8, cores − 1) threads and is merged in source order. Validation and native visibility never run in parallel. Cancellation disposes every finished outcome. |

Not shipped: skipping validation for sources clear of the planes (idea #3). It changes output.

### Final timings against main (seconds, one run each)

"Warm" is a second view solve on the same scene. "Serial" is a third, on one thread.

| Model, view | main | New, cold | New, warm | New, serial | Dump SHA-256 (main = all new runs) |
|---|---:|---:|---:|---:|---|
| perf-L-blocks, long section | 216.0 | 35.3 | 33.3 | 39.0 | `AD7F4B01CCF31F24…` |
| perf-M-blocks, long section | 61.7 | 7.5 | 6.9 | 8.0 | `970DB15B7E186F55…` |
| perf-M-blocks, plan z = 1.2 | 67.8 | 3.7 | 3.8 | 5.6 | `10F2F6F2A41874C9…` |
| perf-M-exploded, long section | 49.9 | 23.9 | 7.0 | 7.9 | `C03524777F152F97…` |
| curved-perf-S-blocks, long | 80.3 | 8.9 | 8.2 | 8.4 | `2AB78D2AA6268531…` |
| curved-perf-S-blocks, transverse | 76.5 | 6.7 | 6.4 | 6.1 | `EDE879E0771B5489…` |
| curved-perf-M-blocks, sidecar "Section transverse" | timed out at 600 s earlier | 47.0 | 46.4 | — | no baseline; see below |

All six pairs have identical dump hashes, and `compare.py` passes main against the cold run and
against the serial run for each (zero missed length either way, regions and diagnostics equal).
Reports: `artifacts/perf-f/final/<pair>/compare-*.json`.

The exploded model's cold view is slower than the prototype's (23.9 s against 16.4 s) on purpose:
its 5,000 distinct shapes are now validated one at a time, because validation is not repeatable
in parallel. Every later view of that scene takes 7 s.

### Surprise 1: Rhino's curve-curve intersection is not repeatable under concurrency

Found while auditing verdicts: validating 500 curved bodies on 8 threads gave a spurious
"self-intersecting" on a random valid instance in 3 of 21 parallel runs (4 instances), and in
none of the serial runs. A stress test
then ran each per-source step serially for a reference and again on 8 threads
(`stress-parallel`, curved S, 500 bodies):

| Step | Mismatches, step alone on 8 threads | Mismatches, mixed with other steps |
|---|---:|---:|
| `Intersection.CurveCurve` overlaps, called directly | **848 of 6,000 (14 %)** | 0 of 6,000 |
| Interval attribution before the gate existed (same call inside `EdgeProvenance.Split`) | **918 of 6,000 (15 %)** | 2 of 6,000 |
| Interval attribution through the gate (product code now) | 0 of 6,000 | 0 of 12,000 |
| Convert + component split | 0 of 12,000 | 0 of 24,000 |
| Classification + freeform certificate | 0 of 15,000 | 0 of 27,000 |
| Plane intersection + join (+ planar regions) | 0 of 15,000 | 0 of 27,000 |
| Trim + cap + join | 0 of 15,000 | 0 of 27,000 |
| Face-face intersection (`BrepBrep`) | 0 of 5,000 | 0 of 3,000 |

Counts are summed over three stress runs (`artifacts/perf-f/final/stress/`). Serial repeats of
every step matched their reference exactly.

The failure is always the same: a closed edge tested against itself is reported as overlapping
only part of its length. Serial repeats never differ. Production therefore:

1. settles every validation verdict serially, on the calling thread, before any parallel work;
2. takes one process-wide gate around every curve-curve, curve self-intersection and
   loop-relationship call in the section code (`SectionGeometry.CurveIntersectionGate`).

A licensed test runs the attribution 2,560 times on worker threads and requires it to match the
serial result. With the gate removed it fails (106 mismatches); with it, it passes.

Two limits to be plain about. The other steps are clean in 36,000–42,000 concurrent runs each,
on eight curved definitions; that is evidence, not a guarantee from McNeel. And the gate only covers
this code: Rhino's own UI thread, or another plug-in, can call curve-curve intersection while our
background solve does. That exposure already exists on `main` and only the worker-process route
removes it. If the owner prefers no parallel geometry at all, set
`SectionRegionEngine.Parallelism` to 1; the serial column above is what that costs.

### Surprise 2: one real case where the instance's own copy disagrees with its definition

The audit (`audit-verdicts`) compares the definition-local verdict with validating each
instance's own world-space copy, which is what `main` does.

| Model | Bodies | Distinct shapes | Disagreements |
|---|---:|---:|---:|
| perf-M-blocks / perf-L-blocks / perf-M-exploded | 5,000 / 20,000 / 5,000 | 15 / 15 / 5,000 | 0 |
| curved-perf-S-blocks | 500 | 8 | 0 |
| blocks, block_nested_mirrored | 403, 2 | 202, 1 | 0 |
| units-coords-offset (coordinates to 1,000,008), -mm, -ft | 19 each | 19 each | 0 |
| **curved-perf-M-blocks** | 5,000 | 8 | **1** |

The one case is instance `22565916-…` of definition `ecaa0f35` on curved M. It reproduces on one
thread and on eight. Its own copy is rejected as self-intersecting; the definition, and every
other instance of it, is valid. The instance does not self-intersect: its cap/wall intersection
curve lies on the shared rim edge to 1.2e-11. At that position Rhino reports the closed rim's
overlap with itself as covering 94 % of its length, and the check treats the missing 6 % as an
intersection away from shared topology. This is the same false positive as the side finding
below, appearing on one placement only.

Consequence: on a curved-M section that includes this instance, `main` drops it and quarantines
its footprint, and the new code draws it. The new answer is the geometrically correct one, but it
is a difference from `main`, and it is why the curved-M row above cannot be called identical even
in principle. Evidence: `artifacts/perf-f/final/audit-final/`,
`artifacts/perf-f/final/selfcheck/curved-M-instance.txt`.

The reverse case (definition rejected, an instance's own copy accepted) was not seen. Where a
definition is rejected, every keyed instance is rejected, as before.

### Tests

Seven licensed tests in `tests/M2DPlus.Rhino.Tests/SectionReuseTests.cs`: a self-intersecting
definition is rejected for every instance (plain, moved, mirrored, scaled, mirrored and scaled,
stretched) from one verdict per scale; a valid definition validates once for 40 instances and
three views; keys for moved, mirrored, scaled and non-similar placements; serial and 4- and
8-thread runs publish identical drawings; cancelling mid-parallel disposes every outcome and
leaves the engine usable; verdicts are cleared when the scene is disposed; curve overlap
attribution is repeatable on worker threads.

## Reproduce

See [tools/perf-f/README.md](../../tools/perf-f/README.md). The investigation's switch variants
(`single+cache+…`) and its phase tables came from a prototype build that is no longer in the tree;
a copy of that prototype's sources is in `artifacts/perf-f/proto-src/` (git-ignored).

Evidence folders under `artifacts/perf-f/`: `L1`, `M1`, `Mplan`, `ME`, `CSsec`, `CSlong`, `CM`,
`CS`, `tests` (investigation); `final/` (production change: pairs, audits, stress, selfcheck, tests).
