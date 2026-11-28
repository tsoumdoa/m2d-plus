# Raster measurement harness

The stage 1 harness calls the opt-in product Raster engine directly. Stage 0 was
a standalone prototype; its historic measurements and limitations remain in
[the research report](../../../docs/research/raster-engine.md).

```powershell
dotnet build tools/worker-bench -c Release
$env:M2D_RHINO_TESTS = '1'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
dotnet tools/worker-bench/bin/Release/net8.0-windows/M2DPlus.WorkerBench.dll raster-poc $resolver owner-1 Top $freshEvidenceFolder 2048,4096,8192
```

The resolver must be an existing compatible Rhino.Inside resolver. The command
owns one invisible headless Rhino host, takes the shared local-model timing mutex,
and refuses existing output paths. All output belongs under
`$M2D_MODELS/evidence/raster-2026-11-28/` (default root
`~/.t3/m2d-plus-models`). Never put private models or receipts in git.

Labels: `owner-1`, `owner-2` (resolved through `owner/manifest.json`), `perf-M`,
`curved-S`, `brep_box`, `brep_curved_sphere`, `brep_trimmed_planar_annulus`,
`mesh_closed_tri`, `mesh_closed_quad`, `mesh_open_quad`, `chs_hollow`, `column_through_slab`, `block_nested_mirrored`,
`subd_rounded_cube`. Views use the normal `ViewRequest.Parse` names, including
authored clipping planes. No other model labels are admitted.

The optional last argument `mesh` substitutes mesh-derived tangent silhouettes;
both silhouette extraction costs are always measured separately. Every invocation
also checks the plain raster's depth/face/bounds controls under one and four threads.

`--view-request=<external-json>` reads an archived request's authored View geometry
and depth (and resolves current settings afresh). This is how the existing owner
Plan A box is selected; private coordinates remain outside git. Named view objects
can also be selected by unique object name. `--reference=<case.json>` reuses a
single-threaded HLD archive after exact input/model/settings/fingerprint/frame
checks. `--timing-only=<case.json>` measures production-threaded HLD after those
input checks; `--also-threaded` measures it first on the same currently captured
registrations before the single-threaded reference and raster matrix.

The observer copies precisely the ordered prepared HLD registration list, then
raises a harness sentinel before any HLD registration or solve. Raster and the
single-threaded HLD reference consume those copies. This measures raw projection;
both omit later Route C edge suppression and ordinary presentation. Timing and
correctness receipts keep that boundary explicit.

`case.json` holds HLD timing, fingerprints, source/depth census and preparation
diagnostics. Resolution folders hold timings/errors, lossless raster drawings,
error spans/feature attribution, and red wrong-visible/wrong-hidden PNGs. Comparator
names retain their historical `PipesToHld` / `HldToPipes` labels; here Pipes means
the raster candidate, using the existing comparator unchanged.

`raster-rays <resolver> <case-folder> <resolution> <fresh-output-folder>` makes no
HLD solve. It re-captures and verifies the inputs, then tests representative native
Brep rays at the twelve longest error spans per direction. Native surface hits
and strict solid-interior checks at candidate/pixel-centre rays support mechanism
diagnosis; they are not a length-integrated oracle.

`python report.py <evidence-root> --doc <research-report.md>` produces the local
summary and updates only the explicitly marked numerical block of the requested
research report. It refuses to declare an incomplete matrix complete.


## Stage 1

The harness now calls the shipped `Experimental/RasterVisibility/RasterProjection`
and Core raster buffer directly. There is no separate classifier prototype. The
`mesh` silhouette alternative is retired. Use the same commands and a fresh evidence
folder; `--also-threaded` records the production HLD step for each case. Optional
`--exclude-layer-file=<external UTF-8 text file>` records a second comparison after
removing that layer from **both drawings**, without changing the solved inputs.
The receipt stores the layer hash; private names remain outside git.

`--product-only` skips references/comparison and runs the ordinary full product
pipeline at its calculated worker memory cap, with an HLD-call guard; its receipt
proves admission/crop/section integration rather than raw projection accuracy.
`raster-poc <resolver> score-archives <historic-case> 8192 <external-layer-file> <fresh-output>`
re-scores preserved stage 0 geometry without that layer. It requires the same exact
Rhino build and schema, never imports a historical result into product cache/worker
state, and records archive hashes and the scoring fingerprint.

Summarize a single-resolution stage 1 matrix without overwriting stage 0 summaries:

```text
python tools/worker-bench/RasterPoc/report.py <evidence-root> --matrix stage1/matrix-reviewed --expected-resolutions 1 --output-root <evidence-root>/stage1
```

The optional final `raster-rays` argument is an external layer descriptor; it filters
control selection only, so non-setting-out junction errors can be inspected while
all prepared occluders remain in the native ray query.

`--reservation-only` captures a label and writes its old/new reservation, curved-face
census and HLD worker cap without drawing. `--native-timing` captures the same prepared
batch as stage 1, then runs one Raster projection with serial native preparation
and normal managed threads, without HLD or comparison. Both take the shared timing
mutex and preserve receipts outside git. The latter measures the host's native
execution policy in an owned headless process; it never attaches to interactive Rhino.
