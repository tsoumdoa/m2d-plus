# M1b baseline capability

Implementation branch `m1b/baseline`, based on M1a `0a75ced`. No commit, push,
installation, runtime-setting change or publication. Neither section-engine
directory nor `tools/models/` was touched.

## First renders

`tools/render/Render.ps1` first ran the unchanged M1a engine on all 38 bundled
fixtures plus the research 305-object building, Top / Front / Right / SW: **156
views**. `artifacts/render/m1a/index.html` embeds every SVG, light/dark, with no
scripts or network dependencies. It includes timings, piece counts and full
Info. Individual SVGs and 800×560 PNGs sit beside it. Representative stems:
`repeated_building_305-SW`, `repeated_building_305-Top`, `chs_hollow-SW`,
`rhs_hollow-Top`, `box_probe-Top`, `block_nested_mirrored-SW`.

That building run took 1.544 / 1.436 / 1.267 / 2.356 s per view. SVGs are display
tessellations of the engine's owned curves, not exact CAD export. The initial
sheet's “Capture” time includes file open. Annotation-only M1a scenes showed a
no-geometry error; M1b now returns an empty, reported result without calling HLD.
The original sheet is retained rather than overwritten with later behaviour.

The runner calls real `SceneCapture` → `NativeDrawing` directly. Actual GH
adapters/publication are tested separately. `IRenderEngine` selects `m2d` or
`raw`; raw compares HLD over the same captured/normalised inputs, explicitly
not a correctness oracle. Use separate `-Output` folders for adapters/builds
and run serially. See `tools/render/README.md`. Large models remain external.

## Views and normalisation

Text resolves case-insensitive named view, then named clipping plane, then
direction keyword. A matched clipping plane reports M2 instead of falling
through to a colliding keyword. Named parallel views, `ViewInfo` / `ViewportInfo`
and Rhino 8 Model View goo become host snapshots of camera axes and frustum
intervals. Compute gets only values. Named-view revisions join the conservative
document witness; rename/camera/frame edits invalidate whole drawings.
Perspective errors at its original view index with the UX's “Set the named view
to Parallel” message, while the other views finish.

Named views retain roll and projected frame centre; their plane moves only
along its normal to the scene front. Crop defaults on. Point filtering and
exact curve/side-plane interval trimming restrict output to the frame. The
complete occluder scene is solved before cropping: under parallel projection,
wholly offscreen objects cannot hide anything inside, and retaining crossing
objects whole preserves boundary occlusion without adding artificial crop edges.
An analytic line/box test checks visible/hidden intervals, points, offscreen
geometry, rolled axes, crop-off value requests and perspective partial failure.
The GH `crop` Settings key waits for M3; default-on is constant here.
Plane/Rectangle/Box/clipping-plane views remain disabled.

Box, BoundingBox, Sphere, Cylinder, Cone and Torus values become Breps;
Rectangle3d becomes NURBS; Line/Polyline/Arc/Circle become curves; Point3d becomes
Point. Existing Surface/Extrusion/SubD conversion remains explicit in Info.
The GH host adapter resolves referenced geometric goo and Rhino 8 Model Objects,
including inline geometry. The actual component is tested with Model Objects,
referenced GH_Brep, GH_Box, a primitive wrapper and Model View. No GH type enters
Rhino/Core compute contracts.

Layer strings match full paths/wildcards (`Structure::*`) or leaf names
(`Walls`), preserve incoming GH paths and append object identity. Hidden and
excluded sources follow document-mode filtering. Missing layers are reported
as non-occluding. Nested definitions retain local geometry and ordered
transforms, with a 256-level safety limit. Cycles, excessive nesting and
empty/unavailable definitions are explicit unsupported potential occluders.
An 80-level asymmetric box/circle fixture checks reflection plus nonuniform
scale against hand-composed bounds, volume, orientation and the ellipse
equation. Nondeformable curves become NURBS before affine deformation; other
types must accept `MakeDeformable` or fail clearly. A genuine linked `.3dm`
with 24 nested definitions is loaded into a private document and checked
against analytic bounds and volume. Compute never resolves linked files.

## Native policy and property gates

One global scheduler slot remains held through actual native completion,
including cancellation. `multipleThreads=false` stays default; the existing
opt-in is experimental, and sorting does not establish classification
equivalence. Output sorts by numeric source path, ordinal source identity,
geometric key (bounds plus complete archive SHA-256), then class. Capture sorts
source insertion by path/identity too, so coincident-edge attribution and scene
revisions are stable when the same inputs are permuted. A permutation
and cache-retirement test includes several intervals from one source and
numeric paths 2 and 10.

Unset classification precedes proxy/parent access. Invalid, parentless and
source-less extraction is counted. Detected potential occlusion failures
quarantine the view: curves go to Unresolved and uncertain points are suppressed.
Unsupported annotations, text dots, hatches and point clouds remain reported
non-occluders, preserving Claude's M1a fix.

`GeometryMatrixTests` has a P/F/U property gate for every bundled fixture.
P includes analytic lengths, all square side intervals (coverage without
retracing), circular/elliptical loci, independent Simpson integration of a
tilted circle's speed, hollow profiles, contact curves and box/probe hidden
intervals. F asserts that annotation drawing remains absent, so an improvement
is visible. U asserts bounded successful extraction and an explicit unknown
validation domain, not certified whole freeform output.

Research Custom-engine failures and section topology/area verdicts are not
passed off as native uncut verdicts. Sections are explicitly not evaluated.
Reports state “analytic baseline properties only; general freeform visibility
unknown; sections not evaluated (M2)”. M1a's independent point/block/interval
and lifecycle gates remain active. Two additional expected-F tests are deliberate:

- A coplanar line plus external point without a solid gives an invalid native
  point extraction in this build. The absent point is reported, the line is
  Unresolved and the view quarantined. Improvement fails this assertion for
  deliberate upgrade to P.
- Raw clipping reproduces the research box/probe Unset proxies with its exact
  camera/frustum and `multipleThreads=true`. The test counts proxies without
  accessing ParentCurve or CurveGeometry. Threading/frustum changes can change
  reproduction; this is not an all-modes failure claim or enabled section route.

## Fingerprints

Rhino 8.36.26296.17001, .NET 8.0.34, licensed serial headless host. One sequential
pass over every top-level geometry, JSON + SHA-256 first, binary archive +
SHA-256 second. Times include serialization and hashing; allocations are
current-thread managed bytes, not native memory. Ordered passes are not a
controlled speed benchmark. Initial measurements:

| Model | Objects | JSON ms | Binary ms | JSON managed bytes | Binary managed bytes |
|---|---:|---:|---:|---:|---:|
| Campus | 2,983 | 14,733.1 | 13,927.8 | 5,711,612,008 | 560,265,680 |
| 50k boxes | 50,000 | 3,743.2 | 3,342.6 | 7,124,007,968 | 812,041,552 |
| Building | 305 | 25.5 | 22.1 | 48,191,400 | 5,413,096 |

The material gain is allocation reduction, not a large native serialization
speedup. `GeometryFingerprint` hashes the full binary archive plus metadata,
including user data, exposed by RhinoCommon `GetObjectData`. No CRC identity,
formatter or deserializer is used. The narrow SYSLIB0050 suppression only
covers legacy `SerializationInfo` construction required by Rhino's API.
Unexpected archive metadata fails closed. Each document leaf is content-hashed
once per coherent capture, even through repeated definitions. Cross-capture
keys still need full content hashing, session identity, local leaf identity and
normalisation version. Tests check duplicates, user data and interior changes
with identical bounds.

## Admission and selective cache retirement

No source-count cutoff remains. With geometry bytes B, Brep faces F,
triangulated mesh faces T, sources N and views V, estimated bytes are:

```
4 B + 2048 F + 96 T + 4096 N
  + V (65536 + B/8 + 512 F + 24 T + 512 N)
```

The 512 MiB scheduler budget also requires F×V ≤ 600,000 and T×V ≤ 4,000,000.
Even empty views pay fixed overhead. Refusal starts with **“Worker unavailable:
too large for in-process in this build”**, followed by estimate/topology/views.
A gate verifies refusal releases the lease without preparation or HLD.

| Model | Geometry bytes | Faces | Triangles | Six-view estimated bytes | Decision |
|---|---:|---:|---:|---:|---|
| Building, 305 | 4,466,680 | 1,680 | 0 | 32,397,786 | admit |
| 5,000 boxes | 70,000,000 | 30,000 | 0 | 522,333,216 | admit |
| Campus | 593,264,816 | 11,256 | 14,024,704 | 6,263,342,916 | refuse |
| 50k boxes | 700,000,000 | 300,000 | 0 | 5,219,793,216 | refuse |

Initial six-view acceptance completed the building in 9.53 / 10.59 s (.NET 8/10),
including capture, without invalid extraction or quarantine. The 5k-box smoke
actually draws Top and asserts 20,000 strokes. Six-view admission is asserted,
not a six-view runtime claim. It measured 0.57 / 0.81 s capture and 30.57 / 29.35 s
HLD/extraction. Whole test-host peak working set was about 1.06 GB, including
Rhino/GH and earlier fixtures. Estimates are not native allocation caps,
deadlines or UI responsiveness guarantees. Capture still has nonpreemptible
archive/copy work and host witnesses. Matching workers/calibration remain M4/M5.

A shared 128 MiB byte LRU retires individual least-recently-used scene/local
owners, replacing full clears at 512 entries/eight scenes. Hits touch owners;
oversize entries retire immediately without flushing useful entries. Retired
entries with live leases remain alive until final release. Scenes can retain
local geometry after its direct owner retires. Accounting is conservative
(estimated preparation reservation plus local owners), not exact resident native
memory. Core tests cover selective eviction, touches, oversize entries, live
retired leases and final disposal. Clear-during-HLD and detached-output gates
remain active.

## Reproduction and review

`tools/Test-WalkingSkeleton.ps1 -Resolver <existing resolver>` builds Debug,
runs Core and licensed tests serially on .NET 8 and .NET 10. The second host rolls
forward without changing Rhino settings. `-BuildingFixture` overrides the
external building path; missing acceptance data fails explicitly. TRX files
are `m1b-rhino-net8.trx` / `m1b-rhino-net10.trx` under the test project's
`TestResults`. Ordinary solution tests skip licensed gates and start no Rhino.

Review decisions: named crop on; whole-scene occlusion before 2D crop; binary
archive content fingerprints; selective byte LRU; estimated small/medium
admission; bounded P/F/U claims. Installed-Yak interaction, sections,
Target/Settings UI, durable cache, worker and hard cancellation remain later.

## Final local validation

- Debug and Release: zero warnings/errors. Core: 42 pass on .NET 8 and 10,
  also 42 in Release. Licensed suite: 69 pass on each native hosting runtime.
- Ordinary solution tests pass Core and skip licensed discovery without a
  Rhino host. The forced Git pre-commit hook passes; nothing was staged/committed.
- Final building six-view totals: 9.817 s / 9.530 s (.NET 8/10), capture
  28.7 / 39.5 ms. Final 5k Top: HLD/extraction 35.013 / 31.046 s, capture
  581.9 / 607.6 ms, whole-host peak working set 1,061,715,968 / 1,058,914,304 bytes.
  These are observations, not timing gates or native allocation caps.
- Final fingerprint rerun: campus JSON 14,410.1 → binary 13,693.5 ms;
  50k boxes 3,643.8 → 3,372.1 ms. Managed allocations: campus
  5,711,601,824 → 560,313,592 bytes; 50k 7,124,000,000 → 812,041,552 bytes.
  `artifacts/bench/m1b/fingerprints.json` records final fixture/engine hashes;
  `initial-fingerprints.json` preserves the first measurement above.
- `artifacts/render/m1b/index.html` adds a second full 156-view sheet, 156 SVGs
  and 156 PNGs, with zero per-view execution errors. The original M1a sheet is
  intact. Both sheets retain the coincident block-instance SW quarantine
  (one invalid Unset segment, 11 unresolved strokes). Source insertion order
  can change native segmentation/counts between M1a and M1b; counts alone do
  not establish a visibility improvement. General building visibility remains
  bounded/unknown, despite successful performance smoke and extraction.
