# Local model suite

These generators and native baselines are **manual and local only**. Nothing here
is invoked by CI, `dotnet test`, the pre-commit hook or packaging. They require
licensed Rhino 8 on Windows x64 and a Python environment with Rhino.Inside.
Headless geometry workers never use `RhinoDoc.ActiveDoc`. The optional native
capture script uses only its newly launched process's document. The suite never
attaches to an existing interactive process, installs a plug-in, or terminates a
process by name. No binaries go into GH Libraries.

From the repository root in PowerShell, on the research machine:

```powershell
$py = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
& $py -B -u tools/models/generate.py --features --tiers XS,S,M,L,XL,XXL
& $py -B -u tools/models/validate.py --features
& $py -B -u tools/models/validate_pairs.py --tiers XS,S,M,L,XL,XXL
& $py -B -u tools/models/run.py --features --tiers XS,S,M,L --xl-once --wall 600 --memory 8
# One bounded stress probe per XXL form; all other XXL views are omitted.
& $py -B -u tools/models/run.py --tiers XXL --views 'Section long overlap' --threads off --wall 600 --memory 8
dotnet build tools/models/renderer/ModelRenderer.csproj -c Release
& $py -B -u tools/models/screenshots.py --offscreen --wall 600 --memory 16
& $py -B tools/models/assess.py
& $py -B tools/models/report.py
Start-Process artifacts/baseline/index.html
```

The venv is used read-only (`-B` prevents import caches). The suite explicitly
hosts .NET 8; it does not change the owner's interactive Rhino runtime. All
Rhino work is serial, including generation and validation. A named mutex
rejects overlapping suite workers across worktrees. The generated files live
under gitignored `artifacts/models/`; SVGs, JSON measurements, logs and the
self-contained HTML contact sheet live under `artifacts/baseline/`.

| Tier | Objects | Architecture |
|---|---:|---|
| XS | 50 | One detailed room |
| S | 500 | Two-storey house |
| M | 5,000 | Eight-storey mid-rise |
| L | 20,000 | 30-storey tower and three-level podium |
| XL | 50,000 | Five-building campus and terrain |
| XXL | 150,000 | Fifteen-building urban block and terrain |

Each has blocks/exploded forms and nine standard views: four elevations, two
plan cuts, transverse and long-overlap sections, and a SW axonometric.

The default seed is `20261114`. Use `--seed` and `--out` for another series.
Each model group is constructed in a fresh process, so Rhino's SubD serialization
metadata does not depend on which models were selected before it. Feature files
use `ToByteArray` with fixed revision metadata (no clock or output path).
Rhino assigns random block-definition/member and annotation-style UUIDs; the
generator replaces only these known IDs with UUIDv5 identities and repairs
CRC-verified archive chunks. `archive.py` fails closed on unprotected changes;
native read-back and byte-for-byte repeats validate the result. The chunk layout
is documented by [McNeel's openNURBS header](https://github.com/mcneel/opennurbs/blob/8.x/opennurbs_3dm.h).
Large tiers stream through a stable worktree-local staging filename to avoid a
multi-gigabyte managed byte array; archive metadata therefore includes that
worktree path. Byte repeatability is scoped to the same Rhino build and worktree.
To regenerate one feature group or a tier:

```powershell
& $py -B -u tools/models/generate.py --only view-sources,section-styles,cut-topology
& $py -B -u tools/models/generate.py --tiers XS --flavors blocks,exploded
& $py -B -u tools/models/run.py --select curves-points --views Top --threads off
& $py -B -u tools/models/run.py --tiers L --resume --wall 600 --memory 8
& $py -B tools/models/report.py
```

Verify generated features and repeatability, without invoking HLD:

```powershell
& $py -B -u tools/models/generate.py --features --out artifacts/repeat
& $py -B -u tools/models/validate.py --features --compare artifacts/repeat
& $py -B -u tools/models/validate_pairs.py --tiers XS,S,M,L
& $py -B tools/models/test_harness.py
```

Validation includes native archive read logs, geometry validity, archive hashes,
feature size limits, stored clipping metadata, independent hollow-profile
area/region/hole checks and the two-region L crop. These checks do not claim full
visibility correctness. The six unlicensed harness tests exercise nested CRC
repair, rejection without mutation and the owned-process handshake/timeout.

To prepare the small fixtures and their manifest entries for a reviewer:

```powershell
& $py -B tools/models/freeze.py --all-features
```

This enforces the 2 MB per-file limit and copies only feature archives/sidecars to
`tests/fixtures/features/`. Performance models remain external/generated entries.
The script never stages, commits or pushes.

## Shaded model reference PNGs

Run serially, after HLD completes. A native UI-capable attempt can be made with:

```powershell
& $py -B -u tools/models/screenshots.py --select view-sources --wall 90
```

It launches only its own hidden `Rhino.exe /nosplash /notemplate /netcore-8`, runs
`capture_rhino.py`, and closes that process. The current machine produced no
capture within 90 s; the owned job was terminated. The delivered reference PNGs
use the explicit fallback below:

```powershell
dotnet build tools/models/renderer/ModelRenderer.csproj -c Release
& $py -B -u tools/models/screenshots.py --offscreen --wall 600 --memory 16
& $py -B tools/models/report.py
```

The renderer is a separate local helper, outside the plug-in solution. It uses
Rhino's meshes and a CPU depth buffer, a light background and layer/object colours.
Green dashed overlays show the view-source frames. It does not modify the model.
Shading is opaque; annotation labels are simplified. This is labeled **offscreen
fallback**, not native viewport capture. Every feature has SW/Front PNGs; every
performance form including XXL also has Top. Files are 1200×800 at
`artifacts/models/screenshots/<model>-SW.png`, `-Front.png` and `-Top.png`.
`manifest.json` in that folder lists all paths. The offline report embeds them in
its Models gallery. Use `--resume` only when retaining the same renderer build.

`--select` overrides tier selection. `--views` uses exact comma-separated names.
Normal runs use all feature views with native threading off, all nine standard
views of both XS/S/M forms with threading off **and** on, and L with threading
off. `--xl-once` adds just `Section long overlap` for each XL form, capped at
600 seconds including startup and extraction. The separate XXL command above
attempts only its long-overlap sections with the same caps. To explicitly run all
nine XXL views later, omit `--views`; this is a manual stress experiment.
`--resume` skips completed/capped matching-input rows; delete only a specific
result and its `.watch.json` if deliberately repeating that attempt. Keep
separate `--out` directories for statistical repeats.

Every model's `.json` describes units, tolerance, seed, view sources and settings,
known independent expectations, source fixture hashes, object/face/triangle and
block counts, archive size and SHA256. Object counts exclude definition members
and clipping-plane widgets. `hook: true` marks candidates for a future small
correctness subset; **it does not enable licensed tests in the hook**.

The worker is created suspended, assigned to a new Windows Job Object, then
resumed and released through an stdin handshake. This also contains children of
the Python venv launcher. The job has a hard aggregate memory limit and
kill-on-close. Descendant private memory is polled every 200 ms, with the peak
retained and the logged time series decimated to roughly one second. The Windows
job peak is recorded separately. A timeout or cap terminates only
this job. `--wall` is total worker wall time, not merely HLD compute time. The
watchdog JSON retains samples and termination reason even without a drawing.

## Native adapter and future M2D+ comparison

`native.py` calls raw `HiddenLineDrawing.Compute` with hidden on and
tangent/seam edges off. It recursively resolves instance definitions and passes
their cumulative transforms to `AddGeometry`; SubD-to-Brep normalization is
explicitly recorded. Native clipping planes implement the input cut/depth/crop
volume, including participation lists. The adapter does not implement a
production region/hatch, style, glass, Beyond or fade pipeline. Its diagnostics
list these limits; no unavailable feature is represented as a passing check.
Sidecar `source.kind` is authoritative: saved clipping planes also have camera
aliases in the named-view table. A future component adapter must resolve a typed
clipping-plane name to its model object/GUID, rather than pass ambiguous text
(UX text resolution gives named views precedence). Native runs resolve the
clipping-plane object explicitly.

An engine module implements:

```python
def run(model, sidecar, view, multiple_threads, emit_phase):
    # model: borrowed File3dm owned by worker; do not dispose it.
    # sidecar/view: JSON values; emit_phase(name, optional_dict) checkpoints progress.
    # Own/dispose all native/Grasshopper objects you create.
    return result_dict, svg_text
```

Run it with `--engine module_name`. Initialization, limits, input hashing, result
paths and reporting are shared. An M2D+ adapter can load GH headlessly and solve
the registered component in its own document. M0 has no drawing engine, so this
suite does not invent an M2D+ baseline. Independent analytic expectations belong
in sidecars/validation, never in a second call to the engine being evaluated.

`report.py` rebuilds `docs/research/test-models.md` and both offline HTML reports
from the current result directory. The full report embeds every SVG and needs
no external scripts, fonts, network connection or local HTTP server.
