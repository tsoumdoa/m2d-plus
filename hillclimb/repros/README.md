# Native hidden-flag negative regression

Three normalized, world-space Breps; no original model or blocks required.
Rhino 8.36.26296.17001 / .NET 8.0.34, x64. Both calls use `multipleThreads=false`,
`Flatten=false`, tolerance 0.0001, tangents/seams off. Only IncludeHiddenCurves
changes. The raw Visible sets differ by a 0.017177348994 m span on BrepEdge 26.
The independent replay returns 25 Visible pieces in both modes, so count equality
is not an equivalence certificate. This artifact deliberately records a failure.

Keep the camera/frustum and original scene bounds from the adjacent JSON; fitting
the camera to the reduced geometry changes the reproduction.

```powershell
dotnet build tools/native-hillclimb/NativeHillclimb.csproj -c Release
$benchPython = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Scripts/python.exe'
& $benchPython -B tools/native-hillclimb/replay.py
```

This launches only an owned Rhino.Inside process under a hard job cap. It writes
`artifacts/native-hillclimb/reproducer-replay/replay.json` and both Visible outputs.
`negativeRegressionReproduced=true` means the native bug persists; it is not a
passing visibility gate. A future Rhino build may legitimately eliminate it.
