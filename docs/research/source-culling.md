# Per-view source culling (14bc0e5 working tree)

**Strict byte-identity acceptance has not passed.** Changes remain uncommitted; no package was installed.

Existing pruning: `SectionProjectionBounds` separates participating bodies from near/far planes;
composition skips planar native queries without curved overlap or with complete planar Hidden
coverage. No shared whole-source crop filter preceded route selection.

`NativeDrawing.SolveView` now filters before exact/native/section dispatch, including workers.
Tight view-frame boxes enclose owned transformed definition leaves. Strict separation reserves
tolerance plus the existing 128-epsilon arithmetic margin; touching/invalid bounds survive.
Far culling respects clipping participation and `farclip=none`. Near-side handling stays unchanged.
Original scene/camera bounds and input-derived frames are preserved. Info counts culled sources;
progress/timings include culling. The default-on internal switch keys cache and worker requests.

## Exact output gate

Hashes cover exact archived pieces, depth geometry, source paths/items/styles, region loops,
edge provenance, hatches, Frame, Error and quarantine. Diagnostic/work counters and timings are
excluded; coordinates and segmentation are never normalized.

The exact corpus matches 26/27 archives; native matches 17/27. The 13 exact planar controls,
curved-S blocks/exploded SW, space-frame SW, pipes Top/SW/nodebox and
four unbounded `region_*` sections match. The additional cropped `region_branches` case changes
42 pieces to 43 after removing two curves. The perf-M tenth-area box retains 957 pieces, with
six differing archives (including roundoff-scale coordinates and parameter domains). Bidirectional
classified-locus sampling at 1.25 tolerance and 0.01-unit spacing finds no misses in either case;
this does **not** waive the byte gate. Native-mode repeated culling-off Top hashes already differ.

## Timings

5,000-instance `perf-M-blocks`; central crop covers 10% of Top frame area and the full depth;
4,164 sources culled. Seconds below are means of two runs per state after one warmup, ABBA,
holding `Local\M2DPlusLocalModelSuiteRhino`. Each solve is under a minute. Campus was skipped.

| Engine/view | Wall off → on | Native solve off → on |
|---|---:|---:|
| Exact / tenth | 3.866 → 0.618 | 0.364 → 0 |
| Exact / full Top | 3.106 → 3.254 | 0.373 → 0.355 |
| Native / tenth | 18.426 → 4.123 | 18.165 → 3.912 |
| Native / full Top | 19.354 → 18.301 | 19.135 → 18.111 |

Forced hook passes. WalkingSkeleton passes on .NET 8 and 10: 504 Core, 74 UI,
393 Rhino tests per runtime; one optional capture-profile skip each. Added 11 unit and five
licensed cases, including transformed blocks and persistent-worker switch/view isolation.

Evidence: `~/.t3/m2d-plus-models/evidence/source-cull-14bc0e5/`: `perf-M/`,
`perf-M-native/`, `final-correctness/`, `native-correctness/`, timing summary, hook and suite logs.
