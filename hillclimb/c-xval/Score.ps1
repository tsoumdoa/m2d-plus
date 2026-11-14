#Requires -Version 7
param([Parameter(Mandatory)][int]$Iteration, [Parameter(Mandatory)][string]$Summary,
    [string]$Resolver = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$names = @('M2D_HILLCLIMB_ROOT','M2D_HILLCLIMB_ITERATION','M2D_HILLCLIMB_SUMMARY','M2D_HILLCLIMB_MANAGED_SCORE')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:M2D_HILLCLIMB_ROOT = $repo
    $env:M2D_HILLCLIMB_ITERATION = "$Iteration"
    $env:M2D_HILLCLIMB_SUMMARY = $Summary
    $env:M2D_HILLCLIMB_MANAGED_SCORE = '1'
    # Restoring a candidate can preserve older source timestamps. Always compile
    # the actual candidate rather than allowing a newer trial binary to survive.
    & dotnet build "$repo/M2DPlus.sln" -c Debug --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Forced candidate build failed.' }
    $runtimeScores = @()
    foreach ($major in @(8, 10)) {
        $failed = $false
        try { & "$repo/tools/Test-WalkingSkeleton.ps1" -Resolver $Resolver -RuntimeMajors @($major) }
        catch { $failed = $true; Write-Host $_ }
        $score = Get-Content -Raw "$PSScriptRoot/iteration-$Iteration.score.json" | ConvertFrom-Json
        if ([int]($score.runtime.Split('.')[0]) -ne $major) { throw "No fresh score for .NET $major" }
        [xml]$trx = Get-Content -Raw "$repo/tests/M2DPlus.Rhino.Tests/TestResults/m3-rhino-net$major.trx"
        $testFailures = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed' | ForEach-Object { 'Licensed test failed: ' + $_.testName })
        $score.gate.failures = @($score.gate.failures) + $testFailures
        $score.gate.passed = $score.gate.passed -and -not $failed -and $testFailures.Count -eq 0
        $score | Add-Member -NotePropertyName licensedGate -NotePropertyValue @{ total = $trx.TestRun.ResultSummary.Counters.total; failed = $trx.TestRun.ResultSummary.Counters.failed }
        $runtimeScores += $score
        Copy-Item -LiteralPath "$PSScriptRoot/iteration-$Iteration.json" -Destination "$PSScriptRoot/iteration-$Iteration.net$major.json"
        $score | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath "$PSScriptRoot/iteration-$Iteration.net$major.score.json"
    }
    $bothPassed = @($runtimeScores | Where-Object { -not $_.gate.passed }).Count -eq 0
    foreach ($score in $runtimeScores) {
        $score | Add-Member -NotePropertyName bothRuntimeGate -NotePropertyValue $bothPassed
        Add-Content -LiteralPath "$PSScriptRoot/scores.jsonl" -Value ($score | ConvertTo-Json -Depth 20 -Compress)
        Write-Host ($score | ConvertTo-Json -Depth 5)
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
