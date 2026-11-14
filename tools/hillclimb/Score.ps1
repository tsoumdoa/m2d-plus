#Requires -Version 7
param(
    [Parameter(Mandatory)][int]$Iteration,
    [Parameter(Mandatory)][string]$Summary,
    [string]$EngineAssembly = '',
    [string]$Resolver = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
& dotnet build "$PSScriptRoot/M2DPlus.SectionScore.csproj" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Scorer build failed.' }
if ($EngineAssembly) {
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $EngineAssembly).Path -Destination "$PSScriptRoot/bin/Debug/net8.0-windows/M2DPlus.Rhino.dll"
}
& dotnet "$PSScriptRoot/bin/Debug/net8.0-windows/M2DPlus.SectionScore.dll" $Resolver $repo $Iteration $Summary
if ($LASTEXITCODE -ne 0) { throw 'Certification gate failed; inspect hillclimb/scores.jsonl.' }
