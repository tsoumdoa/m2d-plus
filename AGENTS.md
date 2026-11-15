# M2D+ agent notes

MIT open-source Make2D-style drawings for Rhino 8 on Windows. **Pre-alpha, M0:**
one registered component with empty outputs and a diagnostic; no drawing engine.
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
Licensed, serial integration tests require explicit `M2D_RHINO_TESTS=1` and an
existing compatible Rhino.Inside resolver. See [test instructions](tests/M2DPlus.Rhino.Tests/README.md).
Never attach to, terminate or alter another person's Rhino processes.

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

Packaging never publishes. `Publish-Yak.ps1` requires explicit release authorization;
public versions are immutable. Dirty trial candidates cannot be published. Worker
distribution is deferred until a matching-runtime worker actually exists; M0 Yak
contains only the in-process skeleton and its two libraries.

## Workflow and stable identity

- Sol (Codex `gpt-6.1-sol`) implements and tests; Astra (`gpt-6-astra`) reviews designs;
  Opus does UX; Claude reviews diffs, commits and opens PRs.
- Small squash-merged PRs. Don't commit or push as an implementation sub-agent.
- Keep `M2DPlus.Core` free of Rhino/GH types. `M2DPlus.Rhino` has no Grasshopper dependency.
- Stable plug-in GUID: `acfcd722-881a-453e-a2f1-36a297b54c8d`.
- Stable M2D+ Drawing component GUID: `c8d0f9fe-5498-45dd-8caf-10201b80d211`.
  Generated once for M0, recorded in source comments and Yak keywords; never regenerate.
- Append new GH inputs **after existing ones** to preserve saved definitions. Current
  Geometry/G and Settings/S use tree access; View/V, Target/T and Depth/D (5th, appended
  in UX rev. 4) use list access. Only View is required. Append outputs the same way and keep
  their indices fixed: eleven outputs, Ignored/Ig (index 9, for `min visible`) appended after Info/i, then
  Key/K (index 10, one label per `tree` group, UX §1.3).

## Runtime and ownership contracts

Projects target .NET 8; Rhino/GH packages are pinned via `RhinoApiVersion` using
the conduit-plus 8.20 reference baseline and excluded from output. The GHA is a
managed library intended for both Rhino's .NET 8 and .NET 10 hosts; native host
loading must be verified on supported Rhino builds, never inferred from compilation.
M0 headless Rhino/GH load-and-solve checks passed on Rhino 8.36.26296.17001 under
.NET 8.0.34 and .NET 10.0.15. An installed-Yak interactive check remains manual.
Yak warns that assembly name `M2D+` differs from package name `m2d-plus`; both
names are intentional and package inventory/restore-GUID validation still passes.

The worker only emits an M0 identity (schema 0, no capabilities) and exits. See
[S1a](docs/research/s1a-worker.md): Rhino 8.36 defaults to **.NET 10** here. Future
workers must match actual host runtime/architecture and exact Rhino build/revision,
RhinoCommon and engine fingerprints/schema before accepting scene data. A .NET 8
worker is unavailable to a .NET 10 host under that contract. Don't silently change
the owner's runtime or fall back to a heavy in-process solve.

[PLAN ownership contracts](docs/PLAN.md#contracts-decided-before-engine-code):
the UI thread resolves live docs/objects/definitions/styles; compute gets owned
geometry and plain values. Cache entries own data, jobs/previews hold leases until
native work ends, and GH outputs are independent copies. Publish only a complete,
current generation across all views. Keep quality/uncertainty separate from job
state; quarantine potentially occluding unsupported sources. One global budgeted
HLD scheduler; UI cancellation immediately rejects stale work but does not imply
native release. Hard kill/memory limits exist only on the worker route. Cut region
branches hold all loops with aligned hatch/null; crop edges have separate provenance.
Layered cache keys use content fingerprints plus stable identity, never CRC alone.
