# Serial headless drawings

Run `./tools/render/Render.ps1` from the repository. This starts only its own
Rhino.Inside host, reads every bundled `.3dm` plus the external 305-object building,
captures owned sources and renders Top/Front/Right/SW with the real `NativeDrawing`
engine. It does not install anything or attach to an existing Rhino process.
Pass `-Resolver` to override the same existing resolver used by
`Test-WalkingSkeleton.ps1`. Run headless commands serially.

The output defaults to `artifacts/render/m1a/index.html`: one portable, inline-SVG,
script-free light/dark contact sheet, individual SVGs, 800×560 PNGs, and machine
readable `results.json`. SVG curves are display tessellations, not exact CAD export.
Black/white = visible, grey dashed = hidden, orange = unresolved, cobalt = frame.
The read/capture timing includes file open; per-view timing includes extraction.
The sheet embeds every SVG and needs no sibling files or network access.

Use `-Output artifacts/render/m1b` to keep a later build separate. `-Models` accepts
an explicit path array. `-Engine raw -Output artifacts/render/raw` selects the
raw-HLD comparison adapter over the same owned/normalised inputs. Raw HLD is not
an independent truth oracle. `IRenderEngine` is the adapter boundary for later
routes/builds; each run records route, Rhino/runtime, counts and diagnostics.
Run separate build checkouts serially for cross-version comparisons.

Fingerprint-only measurements (no HLD or large-scene capture/copies):

```powershell
./tools/render/Render.ps1 -Engine fingerprints -Output artifacts/bench/m1b -Models @(
  'C:/path/to/frame2d-large-campus.3dm',
  'C:/path/to/small-breps-50000.3dm'
)
```

This hashes every top-level geometry with JSON + SHA-256, then binary archive +
SHA-256; logs wall time, current-thread managed allocation, geometry bytes,
faces/triangles and one/six-view admission estimates. It is one sequential pass,
not a statistically controlled speed benchmark. Large files stay external.

## M2 cut views

`./tools/render/Render.ps1 -Engine m2` writes `artifacts/render/m2/` by default.
It draws Plane/long-section/Box/Rectangle cuts of the research 305-building and
RHS/CHS/section-style controls. Each Cut region renders with all holes, its source
or style colour and its solid/line hatch. Line-pattern raster preview is simplified
(minimum two-pixel spacing and approximate dashes/origin); it is not CAD export.
No UI or bake code is involved. The sheet, SVGs, PNGs and results manifest are all
local artifacts. The manifest retains native build/runtime, engine and fixture
hashes, timings and full Info, including uncertainty. Run this serially with the
licensed test harness and other research hosts.
