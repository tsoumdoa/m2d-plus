#Requires -Version 7
<#
.SYNOPSIS
Build, test and verify a local Yak release candidate (Windows by default, or -Platform mac). Never publishes.
.DESCRIPTION
-Platform win (default) is the unchanged Windows package: the net8.0-windows GHA and libraries plus .NET 8/10
drawing workers. -Platform mac packages the cross-platform net8.0 build for Rhino 8 for Mac as its own Yak
distribution (rh8_20-mac): no workers (Rhino.Inside runs only on Windows) and the macOS README. See docs/design/macos.md.
With -Version, first bumps <Version> in Directory.Build.props and commits that change
("Release VERSION") on a clean working tree, then builds the candidate from that commit.
.EXAMPLE
./tools/Pack-Yak.ps1 -Version 0.1.0-alpha.2
.EXAMPLE
./tools/Pack-Yak.ps1   # repackage the version already in Directory.Build.props
.EXAMPLE
./tools/Pack-Yak.ps1 -AllowDirty   # local trial; no version bump or commit
.EXAMPLE
./tools/Pack-Yak.ps1 -Platform mac -AllowDirty   # local macOS trial (unverified on a Mac until the checklist passes)
#>
param(
    [string]$Version,
    [string]$YakPath = $(if ($env:YAK_PATH) { $env:YAK_PATH } else { 'C:\Program Files\Rhino 8\System\Yak.exe' }),
    [switch]$StageOnly,
    [switch]$AllowDirty,
    [ValidateSet('win', 'mac')][string]$Platform = 'win'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
. (Join-Path $PSScriptRoot 'AssemblyPlatform.ps1')

if ($Version -and $Platform -ne 'win') { throw '-Version bumps and commits; run it with the Windows package, then pack -Platform mac from that commit.' }
if ($Version) {
    Assert-ReleaseVersion $Version
    if ($AllowDirty) { throw '-Version commits the bump; it cannot be combined with -AllowDirty.' }
    if (@(& git -C $repo status --porcelain).Count -gt 0) { throw 'Working tree is dirty. Commit or stash changes before bumping the version.' }
    $current = Get-ReleaseVersion $repo
    if ((Compare-ReleaseVersion $Version $current) -le 0) { throw "New version $Version must be greater than the current $current." }
    & git -C $repo rev-parse -q --verify "refs/tags/v$Version" | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "Tag v$Version already exists." }
    Set-ReleaseVersion $repo $Version
    & git -C $repo add -- Directory.Build.props
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stage Directory.Build.props.' }
    # The pre-commit hook builds and tests this commit (Debug); a failure leaves the bump uncommitted.
    & git -C $repo commit -m "Release $Version" -- Directory.Build.props
    if ($LASTEXITCODE -ne 0) { throw "Version bump commit failed; Directory.Build.props now says $Version but is not committed." }
    Write-Output "Bumped $current -> $Version and committed."
}

$version = Get-ReleaseVersion $repo
$manifest = Get-Content -LiteralPath (Join-Path $repo 'packaging/manifest.yml') -Raw
if ([regex]::Matches($manifest, '(?m)^version: "@VERSION@"\r?$').Count -ne 1) {
    throw 'Expected exactly one version: "@VERSION@" placeholder in packaging/manifest.yml.'
}
if (-not $StageOnly -and -not (Test-Path -LiteralPath $YakPath -PathType Leaf)) { throw "Yak executable not found: $YakPath" }
$status = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git working tree status.' }
$dirty = $status.Count -gt 0
if ($dirty -and -not $AllowDirty) { throw 'Working tree is dirty. Commit/stash changes, or use -AllowDirty for a local trial candidate.' }
$commit = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source commit.' }

& dotnet build (Join-Path $repo 'M2DPlus.sln') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
& dotnet test (Join-Path $repo 'tests/M2DPlus.Core.Tests/M2DPlus.Core.Tests.csproj') -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

$output = Join-Path $repo 'artifacts/yak'
$stageName = if ($Platform -eq 'win') { 'stage' } else { "stage-$Platform" }
$stage = [IO.Path]::GetFullPath((Join-Path $output $stageName))
# Verify the resolved absolute workspace path before recursively replacing generated staging files.
$expected = [IO.Path]::Combine([IO.Path]::GetFullPath($repo), 'artifacts', 'yak', $stageName)
if ($stage -ne $expected -or -not $stage.StartsWith([IO.Path]::GetFullPath($repo) + [IO.Path]::DirectorySeparatorChar)) {
    throw "Unsafe staging path: $stage"
}
# GetFullPath does not resolve junctions or symbolic links: never delete through one.
foreach ($part in @((Join-Path $repo 'artifacts'), $output, $stage)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to stage through a link or junction: $part"
    }
}
# Windows: the net8.0-windows build. macOS: the cross-platform net8.0 build, with no Windows desktop framework reference.
$ghOutput = Join-Path $repo $(if ($Platform -eq 'win') { 'src/M2DPlus.Grasshopper/bin/Release/net8.0-windows' } else { 'src/M2DPlus.Grasshopper/bin/Release/net8.0' })
$payload = @{
    'M2DPlus.Rhino.dll' = Join-Path $ghOutput 'M2DPlus.Rhino.dll'
    'M2DPlus.Core.dll' = Join-Path $ghOutput 'M2DPlus.Core.dll'
    'M2DPlus.Grasshopper.gha' = Join-Path $ghOutput 'M2DPlus.Grasshopper.gha'
    'LICENSE' = Join-Path $repo 'LICENSE'
    'README.md' = Join-Path $repo $(if ($Platform -eq 'win') { 'packaging/README.md' } else { 'packaging/README-mac.md' })
    'icon.png' = Join-Path $repo 'assets/icon.png'
}
# Framework-dependent apphosts are built for both supported host runtimes. Never roll across majors.
# The mac package has none: its drawings run in Rhino's own process.
foreach ($major in $(if ($Platform -eq 'win') { @(8, 10) } else { @() })) {
    $worker = Join-Path $repo "src/M2DPlus.Worker/bin/Release/net$major.0-windows"
    foreach ($name in 'M2DPlus.Worker.exe', 'M2DPlus.Worker.dll', 'M2DPlus.Worker.deps.json', 'M2DPlus.Worker.runtimeconfig.json', 'M2DPlus.Core.dll', 'M2DPlus.Rhino.dll') {
        $payload["workers/net$major/$name"] = Join-Path $worker $name
    }
}
foreach ($source in $payload.Values) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
        throw "Missing or empty packaging input: $source"
    }
    if ([IO.Path]::GetExtension($source) -in @('.rhp', '.gha', '.dll')) {
        $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($source).ProductVersion
        if ($actual -cne $version) { throw "Assembly version mismatch in ${source}: expected $version, found $actual" }
        # The Windows build carries TargetPlatform(Windows); the mac payload must be the plain net8.0 build.
        if ($Platform -eq 'mac' -and (Test-WindowsTargetedAssembly ([IO.File]::ReadAllBytes($source)))) {
            throw "Windows-targeted assembly in the mac payload: $source"
        }
    }
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($name in $payload.Keys) {
    $target = Join-Path $stage $name
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $payload[$name] -Destination $target
}
$packagedManifest = $manifest.Replace('@VERSION@', $version)
if ($Platform -eq 'mac') {
    # Same name, version, keywords and restore GUIDs; only the description says what the mac distribution is.
    $description = [regex]'(?m)^description: >\r?\n(?:  [^\r\n]*\r?\n)+'
    if ($description.Matches($packagedManifest).Count -ne 1) { throw 'Expected one folded description block in packaging/manifest.yml.' }
    $macDescription = "description: >`n  Make2D-style plans, sections, elevations and axonometric drawings for Rhino 8 for Mac`n" +
        "  and Grasshopper (preview). Drawings run inside Rhino's process: no background workers,`n" +
        "  cooperative cancel and a memory-based admission budget. See the README.`n"
    $packagedManifest = $description.Replace($packagedManifest, $macDescription, 1)
}
$packagedManifest | Set-Content -LiteralPath (Join-Path $stage 'manifest.yml') -Encoding utf8
if ($StageOnly) {
    Write-Output "Staged $version at $stage (Release build and tests passed; no Yak archive built)."
    return
}
Push-Location -LiteralPath $stage
try {
    & $YakPath build --platform $Platform
    if ($LASTEXITCODE -ne 0) { throw 'Yak build failed.' }
} finally { Pop-Location }
$packages = @(Get-ChildItem -LiteralPath $stage -Filter '*.yak' -File)
if ($packages.Count -ne 1) { throw "Expected one Yak package, found $($packages.Count)." }
$package = $packages[0].FullName
& (Join-Path $PSScriptRoot 'Test-YakPackage.ps1') -Package $package -Version $version -SourceDirectory $stage -Platform $Platform
$candidateOutput = if ($dirty) { Join-Path $output 'trials' } else { $output }
New-Item -ItemType Directory -Path $candidateOutput -Force | Out-Null
$destination = Join-Path $candidateOutput $packages[0].Name
Copy-Item -LiteralPath $package -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $($packages[0].Name)" | Set-Content -LiteralPath "$destination.sha256" -Encoding ascii
$files = foreach ($name in @($payload.Keys | Sort-Object)) {
    [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{
    version = $version
    sourceCommit = $commit
    dirty = $dirty
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    platform = $Platform
    minimumRhino = '8.20'
    runtime = $(if ($Platform -eq 'win') { 'host net8.0/net10.0; matching workers net8.0/net10.0' } else { 'Rhino 8 for Mac host net8.0+; in-process only, no workers' })
    package = $packages[0].Name
    sha256 = $hash
    validation = 'Release build, unit tests, archive inventory, assembly versions and staged payload hashes passed'
    manualRhinoValidation = 'pending'
    files = @($files)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$destination.json" -Encoding utf8
Write-Output "Local candidate: $destination"
Write-Output "SHA-256: $hash"
Write-Output 'No package was published or installed. See AGENTS.md for the reviewed release workflow.'
