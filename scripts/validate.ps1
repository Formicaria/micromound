# MICROMOUND centralized validation. One command, the same steps CI runs.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
#
#   .\scripts\validate.ps1         # guards + restore + build + test
#   .\scripts\validate.ps1 -Full   # also runs the simulator smoke run
#
# Same steps as scripts/validate.sh. Where they differ, the .sh file is what CI
# runs and is therefore the one that is right.
param([switch]$Full)

# NOT "Stop". Windows PowerShell turns anything a native command writes to stderr
# into an ErrorRecord, and under ErrorActionPreference = "Stop" that terminates
# the script -- so a passing `dotnet build` that emitted one NuGet warning would
# abort validation and read exactly like a failure. Native commands are checked by
# $LASTEXITCODE, which is what it is for; cmdlets that must not fail carry an
# explicit -ErrorAction Stop. (This is the bug that killed release.ps1 on v0.2.1.)
$ErrorActionPreference = "Continue"

Set-Location (Join-Path $PSScriptRoot "..") -ErrorAction Stop

# Guard 1: golden fixtures must not be regenerating. MICROMOUND_UPDATE_GOLDEN=1
# makes the canonical-byte tests rewrite their own expectations and pass
# unconditionally, which is indistinguishable from a real green afterwards.
if ($env:MICROMOUND_UPDATE_GOLDEN) {
    Write-Host "x MICROMOUND_UPDATE_GOLDEN is set - the golden tests would rewrite their fixtures." -ForegroundColor Red
    Write-Host "  Clear it with: Remove-Item Env:MICROMOUND_UPDATE_GOLDEN"
    exit 1
}
Write-Host "==> golden fixtures are being verified, not regenerated" -ForegroundColor Cyan

# Guard 2: the version and the changelog agree (design rule 9).
# -Encoding UTF8 because PS 5.1 reads a BOM-less UTF-8 file as ANSI.
$props = Get-Content -Raw Directory.Build.props -Encoding UTF8 -ErrorAction Stop
if ($props -notmatch '<MicromoundVersion>([^<]*)</MicromoundVersion>') {
    Write-Host "x Could not read <MicromoundVersion> from Directory.Build.props" -ForegroundColor Red
    exit 1
}
$ver = $Matches[1]

if (-not (Test-Path CHANGELOG.md)) {
    Write-Host "x CHANGELOG.md is missing (design rule 9)." -ForegroundColor Red
    exit 1
}
$changelog = Get-Content -Raw CHANGELOG.md -Encoding UTF8 -ErrorAction Stop
if ($changelog -notmatch "(?m)^## v$([regex]::Escape($ver))\b") {
    Write-Host "x Directory.Build.props says $ver, but CHANGELOG.md has no '## v$ver' section." -ForegroundColor Red
    exit 1
}
# The README carries a version marker too, and CI's consistency job compares all three.
# Checking it here means a forgotten bump fails on the machine that can fix it in a
# second, rather than on a runner ten minutes later.
$readme = Get-Content -Raw README.md -Encoding UTF8 -ErrorAction Stop
if ($readme -notmatch '(?m)^\*\*Current version:\*\*\s*v([0-9][0-9A-Za-z.\-]*)') {
    Write-Host "x README.md has no '**Current version:** vX.Y.Z' marker." -ForegroundColor Red
    exit 1
}
$readmeVer = $Matches[1]
if ($readmeVer -ne $ver) {
    Write-Host "x README.md says v$readmeVer, Directory.Build.props says $ver." -ForegroundColor Red
    exit 1
}
Write-Host "==> version $ver agrees across Directory.Build.props, README.md and CHANGELOG.md" -ForegroundColor Cyan

Write-Host "==> dotnet restore" -ForegroundColor Cyan
dotnet restore Micromound.sln
if ($LASTEXITCODE -ne 0) { Write-Host "x restore failed" -ForegroundColor Red; exit 1 }

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
dotnet build Micromound.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { Write-Host "x build failed" -ForegroundColor Red; exit 1 }

Write-Host "==> dotnet test (Release) - protocol contracts, authority, kernel, limits, signatures, golden bytes" -ForegroundColor Cyan
dotnet test Micromound.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { Write-Host "x tests failed" -ForegroundColor Red; exit 1 }

if ($Full) {
    Write-Host "==> simulator smoke run" -ForegroundColor Cyan
    dotnet run --project src/Micromound.Sim -c Release --no-build
    if ($LASTEXITCODE -ne 0) { Write-Host "x simulator smoke run failed" -ForegroundColor Red; exit 1 }
}

Write-Host "==> ALL VALIDATIONS PASSED (v$ver)" -ForegroundColor Green
