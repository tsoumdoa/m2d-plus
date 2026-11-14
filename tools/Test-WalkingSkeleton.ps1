#Requires -Version 7
<# Serial, opt-in licensed acceptance. Hosts only its own Rhino.Inside test processes. #>
param(
    [string]$Resolver = $env:M2D_RHINO_INSIDE,
    [string]$Filter = '',
    [string]$RhinoSystem = 'C:/Program Files/Rhino 8/System',
    [string]$BuildingFixture = 'C:/Users/tomosandego/.t3/scratch/m2d-plus-research-20261114/fixtures/repeated_building_305.3dm'
)
$ErrorActionPreference = 'Stop'
if (-not $Resolver -or -not (Test-Path -LiteralPath $Resolver -PathType Leaf)) {
    throw 'Pass -Resolver pointing to an existing runtime-compatible Rhino.Inside resolver DLL.'
}
$repo = Split-Path $PSScriptRoot -Parent
$names = 'M2D_RHINO_TESTS', 'M2D_RHINO_INSIDE', 'M2D_RHINO_SYSTEM', 'M2D_EXPECT_RUNTIME', 'DOTNET_ROLL_FORWARD', 'M2D_BUILDING_FIXTURE'
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    & dotnet build (Join-Path $repo 'M2DPlus.sln') -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
    $env:M2D_RHINO_TESTS = '1'
    $env:M2D_RHINO_INSIDE = (Resolve-Path -LiteralPath $Resolver).Path
    $env:M2D_RHINO_SYSTEM = $RhinoSystem
    $env:M2D_BUILDING_FIXTURE = $BuildingFixture
    $testFilter = if ($Filter) { @('--filter', $Filter) } else { @() }
    foreach ($major in 8, 10) {
        $env:M2D_EXPECT_RUNTIME = "$major"
        $env:DOTNET_ROLL_FORWARD = if ($major -eq 10) { 'LatestMajor' } else { 'LatestPatch' }
        & dotnet test (Join-Path $repo 'tests/M2DPlus.Core.Tests') -c Debug --no-build --nologo --logger "trx;LogFileName=m3-core-net$major.trx"
        if ($LASTEXITCODE -ne 0) { throw "Core .NET $major tests failed." }
        & dotnet test (Join-Path $repo 'tests/M2DPlus.Grasshopper.Tests') -c Debug --no-build --nologo --logger "trx;LogFileName=m3-ui-net$major.trx"
        if ($LASTEXITCODE -ne 0) { throw "UI-model .NET $major tests failed." }
        & dotnet test (Join-Path $repo 'tests/M2DPlus.Rhino.Tests') -c Debug --no-build --nologo @testFilter --logger "trx;LogFileName=m3-rhino-net$major.trx"
        if ($LASTEXITCODE -ne 0) { throw "Licensed .NET $major acceptance failed." }
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
