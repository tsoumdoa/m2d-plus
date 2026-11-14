#Requires -Version 7
param(
    [string]$Resolver = $env:M2D_RHINO_INSIDE,
    [string]$Output = 'artifacts/render/m1a',
    [string]$Engine = 'm2d',
    [string[]]$Models = @()
)
$ErrorActionPreference = 'Stop'
if (-not $Resolver) {
    $Resolver = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll'
}
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
& dotnet build "$PSScriptRoot/M2DPlus.Render.csproj" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Render build failed' }
if ($Models.Count -eq 0) {
    $Models = @(Get-ChildItem "$repo/tests/fixtures/*.3dm" | Sort-Object Name | ForEach-Object FullName)
    $Models += 'C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/repeated_building_305.3dm'
}
& dotnet "$PSScriptRoot/bin/Debug/net8.0-windows/M2DPlus.Render.dll" $Resolver $repo $Output $Engine @Models
if ($LASTEXITCODE -ne 0) { throw 'Render failed' }
