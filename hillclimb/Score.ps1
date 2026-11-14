#Requires -Version 7
param([Parameter(Mandatory)][int]$Iteration, [Parameter(Mandatory)][string]$Summary,
    [string]$Resolver = 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$names = @('M2D_HILLCLIMB_ROOT','M2D_HILLCLIMB_ITERATION','M2D_HILLCLIMB_SUMMARY','M2D_HILLCLIMB_MANAGED_SCORE')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:M2D_HILLCLIMB_ROOT = $repo
    $env:M2D_HILLCLIMB_ITERATION = "$Iteration"
    $env:M2D_HILLCLIMB_SUMMARY = $Summary
    $env:M2D_HILLCLIMB_MANAGED_SCORE = '1'
    $failed = $false
    try { & "$repo/tools/Test-WalkingSkeleton.ps1" -Resolver $Resolver -RuntimeMajors @(8) }
    catch { $failed = $true; Write-Host $_ }
    $score = Get-Content -Raw "$PSScriptRoot/iteration-$Iteration.score.json" | ConvertFrom-Json
    [xml]$trx = Get-Content -Raw "$repo/tests/M2DPlus.Rhino.Tests/TestResults/m3-rhino-net8.trx"
    $testFailures = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed' | ForEach-Object { 'Licensed test failed: ' + $_.testName })
    $score.gate.failures = @($score.gate.failures) + $testFailures
    $score.gate.passed = $score.gate.passed -and -not $failed -and $testFailures.Count -eq 0
    $score | Add-Member -NotePropertyName licensedGate -NotePropertyValue @{ total = $trx.TestRun.ResultSummary.Counters.total; failed = $trx.TestRun.ResultSummary.Counters.failed }
    Add-Content -LiteralPath "$PSScriptRoot/scores.jsonl" -Value ($score | ConvertTo-Json -Depth 20 -Compress)
    Write-Host ($score | ConvertTo-Json -Depth 5)
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
