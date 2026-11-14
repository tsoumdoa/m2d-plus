# Licensed Rhino integration tests

Plain `dotnet test` discovers a skipped smoke test and never initializes Rhino.
The pre-commit hook and Pack-Yak run Core tests only. Integration tests are serial
and initialize their own in-process RhinoCore on an STA thread; they never attach
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
Future tests add full-output oracles, not production-engine-derived expectations.

These tests host .NET 8 explicitly. Rhino 8.36's interactive default on this machine
is .NET 10; see [S1a](../../docs/research/s1a-worker.md). A passing .NET 8 smoke test
does not certify the future worker against a .NET 10 host.
