# Raster stage 0 measurement

Standalone research command; no production routing, `visibility=raster` setting,
worker protocol or shipped assembly changes. See
[the research report](../../../docs/research/raster-engine.md) for scope, measurement
limits, results and the stage 1 recommendation.

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
