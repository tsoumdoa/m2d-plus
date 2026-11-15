#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$Models,
    [Parameter(Mandatory)][string]$Resolver,
    [string]$Output = 'artifacts/worker-bench/replay'
)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(Test-Path -LiteralPath $Output){throw 'Choose a new output directory; existing evidence is never overwritten.'}
& dotnet build (Join-Path $repo 'M2DPlus.sln') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Solution build failed'}
& dotnet build (Join-Path $PSScriptRoot 'M2DPlus.WorkerBench.csproj') -c Release --nologo
if($LASTEXITCODE -ne 0){throw 'Harness build failed'}
$exe=Join-Path $PSScriptRoot 'bin/Release/net8.0-windows/M2DPlus.WorkerBench.exe'
$worker=Join-Path $repo 'src/M2DPlus.Worker/bin/Release/net8.0-windows'
$saved=$env:DOTNET_ROLL_FORWARD
try {
    $env:DOTNET_ROLL_FORWARD='LatestPatch'
    foreach($tier in 'small','M','L') {
        $model=if($tier -eq 'small'){'small'}else{Join-Path $Models "perf-$tier-blocks.3dm"}
        $folder=Join-Path $Output $tier
        & $exe export $Resolver $model $folder
        if($LASTEXITCODE -ne 0){throw "Baseline $tier failed"}
        # Baseline RhinoCore has exited before starting up to four headless workers.
        foreach($count in $(if($tier -eq 'small'){@(1)}else{@(1,2,4)})) {
            & $exe workers $folder $worker $count
            if($LASTEXITCODE -ne 0){throw "Workers $tier/$count failed"}
        }
    }
} finally {$env:DOTNET_ROLL_FORWARD=$saved}
