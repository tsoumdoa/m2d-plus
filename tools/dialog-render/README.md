# View box depth dialog renders

`tools/dialog-render` compiles the GHA's pure-Eto dialog file
(`src/M2DPlus.Grasshopper/UI/DepthDialog.cs`) with M2D+ Core and writes PNGs of it
for review. It is not in the solution and never ships.

```powershell
dotnet build tools/dialog-render -c Debug
tools/dialog-render/bin/Debug/net8.0-windows/M2DPlus.DialogRender.exe artifacts/ui
```

It writes `depth-dialog-plan-mm.png` (one plan, mm file, Auto filled in),
`depth-dialog-mixed.png` (four boxes of mixed type), `depth-dialog-section-m.png`
(a section with `8 m` typed) and `depth-dialog-invalid.png` (an invalid entry and its
inline error) to the folder given (git ignores `artifacts/`).

Each dialog opens off every screen and closes itself. Eto lays it out with its WinForms
platform from cached NuGet packages (Rhino 8 uses Eto.Wpf with Rhino's style). The
frame and background come from the form's `DrawToBitmap`, and each control is drawn
at the bounds Eto gave it, with its real text, font and colour, by Windows' themed
renderers. The layout and text are the shipped code; the native control chrome is
approximate. Some sessions can't rasterise WPF, read the screen or use `PrintWindow`,
which is why this approach is used. The licensed test
`ViewObjectTests.TheDepthDialogFillsAutoAndKeepsItselfOpenOnInvalidInput` builds the
same dialog in Rhino's own Eto platform and checks its behaviour without showing it.
