<img src="assets/icon.svg" width="64" height="64" alt="">

# m2d-plus

M2D+ is an MIT open-source Grasshopper plugin for Rhino 8 on Windows, for Make2D-style
plans, sections, elevations and axonometric drawings from a model and a view.

**Status: pre-alpha M1a.** **M2D+ → Drawing → M2D+ Drawing** draws small scenes
asynchronously with native hidden lines. Wire `Top`, `Front`, another direction keyword,
or a Vector into View. Empty Geometry draws the visible document. Visible curves and
points, Hidden, Unresolved, Frame and Info are available; other outputs remain empty.
Sections, named views, Target placement, view objects (UX rev. 4) are in progress; Bake is a future milestone.
See [M1a notes](docs/design/m1a-notes.md) for ownership, validation and admission limits.

Install only through Yak: drop a locally built `.yak` onto Rhino, accept installation,
then restart Rhino. There is no public package yet. Don't copy DLLs into Grasshopper
Libraries. Targets .NET 8; intended to load in Rhino's .NET 8 and .NET 10 runtimes.
The future headless worker must separately match the host runtime (Rhino 8.36 defaults
to .NET 10 here); M0's worker only prints an identity and exits.

```powershell
dotnet build M2DPlus.sln -c Debug
dotnet build M2DPlus.sln -c Release
dotnet test tests/M2DPlus.Core.Tests -c Debug --no-build
./tools/Generate-Icons.ps1
./tools/Pack-Yak.ps1 -AllowDirty # no commit; candidate in artifacts/yak/trials
```

Requires the .NET SDK with .NET 8 targeting support; installed Rhino is needed only
for Yak packaging and [licensed integration tests](tests/M2DPlus.Rhino.Tests/README.md).
Plain `dotnet test` skips Rhino integration unless explicitly enabled. See
[AGENTS.md](AGENTS.md) for the local hook, runtime notes and development contracts,
[PLAN.md](docs/PLAN.md) for milestones, and [LICENSE](LICENSE) for the MIT licence.

Created in [T3 Code](https://t3.codes).
