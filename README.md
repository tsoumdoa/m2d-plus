<img src="assets/icon.svg" width="64" height="64" alt="">

# m2d-plus

M2D+ is an MIT open-source Grasshopper plugin for Rhino 8 on Windows, for Make2D-style
plans, sections, elevations and axonometric drawings from a model and a view.

**Status: beta.** **Params → Util → M2D+ Drawing** (there is no M2D+ tab) draws
asynchronously using multithreaded Rhino HiddenLineDrawing. Wire a direction, named
view, plane, rectangle, view box or clipping plane into View. Empty Geometry draws
the visible document. Sections retain closed regions/holes, hatches and crop/merge;
Target, preview, Bake and headless workers are supported.

Right-click **Tolerance** to use the document tolerance (default) or **0.001 / 0.01 /
0.05 / 0.1 / 1 mm**, or type a positive distance under **Custom…**, for this component.
The menu gives scale guidance and flags presets that are too coarse. Bare custom
numbers use document units; entering `document` restores the default.
Settings text (`tolerance = document` or a positive
distance) overrides view-object `m2d.tolerance`, which overrides the menu. It never
changes the document tolerance; coarser values can help sections cut inaccurate geometry
but lose small detail. See [tolerance and settings](docs/design/ux.md#34-tolerance).

HLD visibility is not certified. Engine/Joints are retired compatibility settings;
hidden-off disables min-visible. Fresh solves may change duplicate ownership and
Ignored results. See [the unreleased change notes](docs/release-notes-hld.md) and
[native-engine specification](docs/design/native-engine.md). The exact engine is
archived at `archive/exact-engine` (`0cfc171`, beta.8).

Install only through Yak: drop a locally built `.yak` onto Rhino, accept installation,
then restart Rhino. There is no public package yet. Don't copy DLLs into Grasshopper
Libraries. Targets .NET 8; intended to load in Rhino's .NET 8 and .NET 10 runtimes.
Drawing sets and heavy jobs use persistent headless workers matched exactly to the host
runtime and Rhino/engine build; the Yak includes .NET 8 and .NET 10 apphosts.
See [worker design and measurements](docs/design/drawing-workers.md).

Right-click **Parallel views** to choose **Auto** (1/2/4 by logical core count), **1 / 2 / 4 / 6 / 8**, or a
**Custom…** integer up to `max(1, logical cores - 2)`; presets above that ceiling are hidden. This machine setting
is shared by all components and persists outside `.gh` files. `M2DPLUS_HLD_WORKERS` takes precedence and locks
the menu. Changes apply to the next worker batch without restarting Rhino. It helps sets of **2+ views**:
more views run at once if memory allows, and the memory check may start fewer. Each view uses its own background
Rhino with a full copy of the scene; each view gets somewhat slower, but the set finishes sooner. On 32 threads,
four plans measured **217 s with 2 workers** and **131 s with 4** (one measured run).

```powershell
dotnet build M2DPlus.sln -c Debug
dotnet build M2DPlus.sln -c Release
dotnet test tests/M2DPlus.Core.Tests -c Debug --no-build
./tools/Generate-Icons.ps1
./tools/Pack-Yak.ps1 -AllowDirty # no commit; candidate in artifacts/yak/trials
```

Requires the .NET 10 SDK with .NET 8 targeting support; installed Rhino is needed only
for Yak packaging and [licensed integration tests](tests/M2DPlus.Rhino.Tests/README.md).
Plain `dotnet test` skips Rhino integration unless explicitly enabled. See
[AGENTS.md](AGENTS.md) for the local hook, runtime notes and development contracts,
[PLAN.md](docs/PLAN.md) for milestones, and [LICENSE](LICENSE) for the MIT licence.

Created in [T3 Code](https://t3.codes).
