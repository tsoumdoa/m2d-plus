# Detached launch: survives the caller's shell limits. Usage: launch.ps1 <spec.json> <outdir>
param([string]$Spec, [string]$Out)
$here = Split-Path $MyInvocation.MyCommand.Path
$resolver = if ($env:M2D_RHINO_INSIDE) { $env:M2D_RHINO_INSIDE } else { 'C:/Users/tomosandego/.t3/scratch/2026-11-13-i-noticed-that-make2d-in-2f66c87c/.venv/Lib/site-packages/rhinoinside/Rhino.Inside_v8_net8.0.dll' }
dotnet build "$here/M2DPlus.HcPerf.csproj" -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
New-Item -ItemType Directory -Force $Out | Out-Null
Remove-Item -Recurse -Force "$Out/bin" -ErrorAction SilentlyContinue
Copy-Item -Recurse "$here/bin/Release/net8.0-windows" "$Out/bin"
$p = Start-Process dotnet -ArgumentList @("`"$Out/bin/M2DPlus.HcPerf.dll`"", "`"$resolver`"", "`"$Spec`"", "`"$Out`"") -RedirectStandardOutput "$Out.log" -RedirectStandardError "$Out.err" -WindowStyle Hidden -PassThru
$p.Id
