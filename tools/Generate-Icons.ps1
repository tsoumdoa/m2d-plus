#Requires -Version 7
# Regenerate the Yak PNG from the same code used by the GHA. SVG is the equivalent vector mark.
param([string]$Sheet)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/..").Path
Add-Type -AssemblyName System.Drawing
if (-not ('M2DPlus.Grasshopper.M2DIcons' -as [type])) {
    Add-Type -Path "$repo/src/M2DPlus.Grasshopper/M2DIcons.cs" -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Drawing.Common, System.Private.Windows.GdiPlus, System.Private.Windows.Core
}
$icons = [M2DPlus.Grasshopper.M2DIcons]
$logo = $icons::Logo(256)
try { $logo.Save("$repo/assets/icon.png", [System.Drawing.Imaging.ImageFormat]::Png) } finally { $logo.Dispose() }
Write-Output 'Wrote assets/icon.png'
if ($Sheet) {
    $bitmap = [System.Drawing.Bitmap]::new(224, 224)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(212, 212, 212))
        $g.InterpolationMode = 'NearestNeighbor'
        $g.PixelOffsetMode = 'Half'
        $drawing = $icons::Drawing(24)
        $mark = $icons::Logo(24)
        try {
            $g.DrawImage($drawing, 44, 20, 24, 24)
            $g.DrawImage($mark, 156, 20, 24, 24)
            $g.DrawImage($drawing, 8, 120, 96, 96)
            $g.DrawImage($mark, 120, 120, 96, 96)
        } finally { $drawing.Dispose(); $mark.Dispose() }
        $bitmap.Save([IO.Path]::GetFullPath($Sheet), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output "Wrote $Sheet"
    } finally { $g.Dispose(); $bitmap.Dispose() }
}
