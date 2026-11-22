# M2D+ agent notes

MIT open-source Make2D-style drawings for Rhino 8 on Windows. **Beta:**
one M2D+ Drawing component with Rhino HiddenLineDrawing visibility, sections, Target
placement, preview, Bake and headless drawing workers.
Read [PLAN.md](docs/PLAN.md), [REQUIREMENTS.md](docs/REQUIREMENTS.md) and
[UX](docs/design/ux.md) before changing contracts.

## Build and test

```powershell
dotnet build M2DPlus.sln -c Debug
dotnet build M2DPlus.sln -c Release
dotnet test tests/M2DPlus.Core.Tests -c Debug --no-build
dotnet test M2DPlus.sln -c Debug --no-build # Rhino test is skipped by default
```

Warnings are errors. No cloud CI. Enable the local hook per clone with
`git config core.hooksPath .githooks`; don't bypass it. It builds the solution and
runs **Core and GH UI-model tests** (no Rhino licence) for code changes. Manually verify using
`M2DPLUS_HOOK_FORCE=1 sh .githooks/pre-commit` (Git's sh on Windows).
Licensed integration tests require explicit `M2D_RHINO_TESTS=1` and an
existing compatible Rhino.Inside resolver. Use up to three owned headless processes for correctness; never curved-M or campus.
See [test instructions](tests/M2DPlus.Rhino.Tests/README.md).
Never attach to, terminate or alter another person's Rhino processes.

Generated test models (building, curved and view suites, campus) and archived run evidence live
outside git in `~/.t3/m2d-plus-models` (override with `M2D_MODELS`). The tools read them from there; never
commit them or point tools at temporary worktrees.

## Installation and release

Yak is the only installation path, including development. Never copy binaries to
Grasshopper Libraries. Keep GHA, Core and Rhino DLLs together. Don't install for
the owner unless asked; they can drop a `.yak` onto Rhino and restart it.

```powershell
./tools/Generate-Icons.ps1
./tools/Pack-Yak.ps1 -AllowDirty # local trial; does not commit
./tools/Pack-Yak.ps1            # clean candidate; does not commit
# Only when explicitly authorized: -Version bumps and COMMITS on a clean tree.
./tools/Pack-Yak.ps1 -Version 0.1.0-alpha.2
```

`-Platform mac` packs the separate `rh8_20-mac` distribution from the `net8.0` build (no workers); it is
unverified until [the on-Mac checklist](docs/macos-test-checklist.md) passes, and `Publish-Yak.ps1` publishes only
the Windows package. Packaging never publishes. `Publish-Yak.ps1` requires explicit release authorization;
public versions are immutable. Dirty trial candidates cannot be published. The Yak
ships the drawing worker for both runtimes (`workers/net8`, `workers/net10`) next to
the GHA and its two libraries.

## Workflow and stable identity

- Sol (Codex `gpt-6.1-sol`) implements and tests; Astra (`gpt-6-astra`) reviews designs;
  Opus does UX; Claude reviews diffs, commits and opens PRs.
- Small squash-merged PRs. Don't commit or push as an implementation sub-agent.
- Keep `M2DPlus.Core` free of Rhino/GH types. `M2DPlus.Rhino` has no Grasshopper dependency.
- Stable plug-in GUID: `acfcd722-881a-453e-a2f1-36a297b54c8d`.
- Stable M2D+ Drawing component GUID: `c8d0f9fe-5498-45dd-8caf-10201b80d211`.
  Generated once for M0, recorded in source comments and Yak keywords; never regenerate. It lives in
  **Params ▸ Util** (no M2D+ tab; `UI/Model/ComponentPlacement`).
- Append new GH inputs **after existing ones** to preserve saved definitions. Current
  Geometry/G and Settings/S use tree access; View/V, Target/T, Depth/D (5th, appended
  in UX rev. 4) and Source/Src (6th, index 5) use list access. Source is placement-only, like Target. Only View is required. Append outputs the same way and keep
  their indices fixed: eleven outputs, Ignored/Ig (index 9, for `min visible`) appended after Info/i, then
  Key/K (index 10, one label per `tree` group, UX §1.3).

## Runtime and ownership contracts

macOS ([design](docs/design/macos.md)): Rhino and GH projects multi-target `net8.0-windows` (Windows package,
unchanged) and `net8.0` (mac package). `HostPlatform`/`HostRouteCapabilities` select the route: off Windows there
are no workers, every drawing runs in-process under `InProcessPolicy` admission, Parallel views is hidden and
**Platform self-test…** is shown. Keep Windows behaviour identical; gate any new platform difference on
`HostPlatform`. `tests/fixtures/platform/selftest-baseline.json` is the Windows baseline the Mac self-test
compares with; regenerate it only for an intended engine change (`PlatformSelfTestTests`).

Projects target .NET 8; Rhino/GH packages are pinned via `RhinoApiVersion` using
the conduit-plus 8.20 reference baseline and excluded from output. The GHA is a
managed library intended for both Rhino's .NET 8 and .NET 10 hosts; native host
loading must be verified on supported Rhino builds, never inferred from compilation.
M0 headless Rhino/GH load-and-solve checks passed on Rhino 8.36.26296.17001 under
.NET 8.0.34 and .NET 10.0.15. An installed-Yak interactive check remains manual.
Yak warns that assembly name `M2D+` differs from package name `m2d-plus`; both
names are intentional and package inventory/restore-GUID validation still passes.

Drawing sets (two or more views) and jobs too large for in-process run in a pool of
persistent headless workers, one whole view per worker ([design](docs/design/drawing-workers.md)):
named pipes, Job Object memory caps sized from machine RAM, hard cancel, crash and
stale-reply isolation, same inputs/settings/fingerprints as in-process; threaded HLD realizations may differ.
Transport of one result remains lossless (schema 4). The codec must carry
every `ViewDrawing`/`DrawingPiece` field; `WorkerCodecStateTests` enforces it. See
[S1a](docs/research/s1a-worker.md): Rhino 8.36 defaults to **.NET 10** here. Workers
must match actual host runtime/architecture and exact Rhino build/revision,
RhinoCommon and engine fingerprints/schema before accepting scene data. A .NET 8
worker is unavailable to a .NET 10 host under that contract. Don't silently change
the owner's runtime or fall back to a heavy in-process solve.

[PLAN ownership contracts](docs/PLAN.md#contracts-decided-before-engine-code):
the UI thread resolves live docs/objects/definitions/styles; compute gets owned
geometry and plain values. Cache entries own data, jobs/previews hold leases until
native work ends, and GH outputs are independent copies. Publish only a complete,
current generation across all views. Keep quality/uncertainty separate from job
state. Keep Unset/extraction failures local; failed participating clips demote only
overlapping footprints. Quarantine projection only when HLD returns no drawing/frame. One global budgeted
HLD scheduler; UI cancellation immediately rejects stale work but does not imply
native release. Hard kill/memory limits exist only on the worker route. Cut region
branches hold all loops with aligned hatch/null; crop edges have separate provenance.
Layered cache keys use content fingerprints plus stable identity, never CRC alone.

Pre-HLD exclusion (owner, 2026-11-19): before capture, objects and block instances whose cheap bounding box lies completely outside every requested view volume (outside the profile; for cut participants, wholly in front of the front face or beyond the back face where far clip applies) are excluded: their geometry is never copied, fingerprinted or normalised, though they stay watch dependencies. Applies only when every view has a fixed volume: an M2D+ view box, a GH Box, or a Rectangle with authored axes and explicit depth; other requests keep the whole scene. Scene bounds are the survivors' bounds, which changes only the HLD camera radius, so outputs are compared by locus, not bytes.

Visibility defaults to threaded Rhino HLD, using physical Route C sections. No joint
suppression. One opt-in exception (owner, 2026-11-19): **Pipes (experimental)**,
`visibility = pipes`, a surface ray classifier in `src/M2DPlus.Rhino/Experimental/PipeVisibility`
([README](src/M2DPlus.Rhino/Experimental/PipeVisibility/README.md)). It must stay opt-in,
never change HLD output or cache identity, and fall back to HLD with a remark for any
view it cannot admit or any failure. Known wrong-visible junction errors are disclosed in
Info; don't extend it beyond that folder or add other custom classifiers without the owner. Legacy engine/joints values load with
deduplicated grey remarks and do not key compute/run/timing identity. Hidden is
a projection key; hidden-off disables min-visible. Exact engine archive:
`archive/exact-engine` at `0cfc171` (beta.8). See [native engine](docs/design/native-engine.md).
