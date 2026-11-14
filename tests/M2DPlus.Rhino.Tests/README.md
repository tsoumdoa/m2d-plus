# Licensed Rhino integration tests

Plain `dotnet test` discovers skipped integration tests and never initializes Rhino.
The pre-commit hook and Pack-Yak run Core tests only. Integration tests are serial
and share one in-process RhinoCore on one STA host thread; they never attach
to or terminate an interactive Rhino session. A compatible Windows x64 runtime,
installed licensed Rhino 8 and McNeel Rhino.Inside resolver are required.

```powershell
$env:M2D_RHINO_TESTS = '1'
$env:M2D_RHINO_SYSTEM = 'C:/Program Files/Rhino 8/System'
$env:M2D_RHINO_INSIDE = 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
try {
    dotnet test tests/M2DPlus.Rhino.Tests -c Debug --filter Category=RhinoIntegration
} finally {
    Remove-Item Env:M2D_RHINO_TESTS, Env:M2D_RHINO_SYSTEM, Env:M2D_RHINO_INSIDE -ErrorAction SilentlyContinue
}
```

The existing resolver on the M0 research machine is at
`C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll`.
No resolver binary or licence is redistributed. The smoke test constructs a solid
box, independently checks its volume, and reads the frozen `brep_box.3dm` fixture.
M1a adds lifecycle races during a real HLD progress callback, independent analytic
interval/point/mirror oracles, filtering/normalization, shared preparation and live
GH async publication. See [implementation notes](../../docs/design/m1a-notes.md).
Section tests use frozen root/S2 fixtures and generated analytic geometry, rectangle
areas, circle integrals, loop/hole/region counts, containment and independent volume
checks. They cover crop, merge, contact, cap/provenance and embedded-pattern hatch
save/reopen. See [the API and supported-domain notes](../../docs/design/m2-sections.md).

Run both native hosting runtimes **serially** with:

```powershell
./tools/Test-WalkingSkeleton.ps1 -Resolver 'C:/path/to/Rhino.Inside_v8_net8.0.dll'
```

The second run uses `DOTNET_ROLL_FORWARD=LatestMajor` and asserts runtime major 10
inside the GHA load/solve test. It does not change the owner's Rhino runtime
setting. TRX results are in `TestResults/m1b-rhino-net8.trx` and `m1b-rhino-net10.trx`.
The harness keeps a host document alive for GH's process-wide document witness,
and pumps scheduled GH solutions without showing an editor. Both runs load the
installed native Rhino build and the built GHA; no binaries are installed or
copied to Grasshopper Libraries. These checks do not certify a future worker or
replace an installed-Yak interactive check.

M1b adds all bundled uncut fixture property gates (P/F/U), primitive and Model
Object/View adapters through the actual GH component, named-view crop and
perspective partial failure, 80-level nonuniform/mirrored and 24-level linked
blocks, binary fingerprints, stable order, native extraction failure assertions,
estimate refusal, and 305-object/six-view and 5k-box smokes. Times are recorded,
never gated. Use `-BuildingFixture` (or `M2D_BUILDING_FIXTURE` for direct runs)
for an alternate research fixture path. See [M1b notes](../../docs/design/m1b-notes.md).
