# MICROMOUND centralized validation. One command, the same steps CI runs.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
#
#   .\scripts\validate.ps1         # guards + restore + build + test
#   .\scripts\validate.ps1 -Full   # also runs the simulator smoke run
#
# Same steps as scripts/validate.sh. Where they differ, the .sh file is what CI
# runs and is therefore the one that is right.
param([switch]$Full)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

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
$props = Get-Content -Raw Directory.Build.props
if ($props -notmatch '<MicromoundVersion>([^<]*)</MicromoundVersion>') {
    Write-Host "x Could not read <MicromoundVersion> from Directory.Build.props" -ForegroundColor Red
    exit 1
}
$ver = $Matches[1]

if (-not (Test-Path CHANGELOG.md)) {
    Write-Host "x CHANGELOG.md is missing (design rule 9)." -ForegroundColor Red
    exit 1
}
if ((Get-Content -Raw CHANGELOG.md) -notmatch "(?m)^## v$([regex]::Escape($ver))\b") {
    Write-Host "x Directory.Build.props says $ver, but CHANGELOG.md has no '## v$ver' section." -ForegroundColor Red
    exit 1
}
Write-Host "==> version $ver has a CHANGELOG section" -ForegroundColor Cyan

Write-Host "==> dotnet restore" -ForegroundColor Cyan
dotnet restore Micromound.sln
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> dotnet build (Release)" -ForegroundColor Cyan
dotnet build Micromound.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "==> dotnet test (Release) - protocol contracts, authority, kernel, limits, signatures, golden bytes" -ForegroundColor Cyan
dotnet test Micromound.sln -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit 1 }

if ($Full) {
    Write-Host "==> simulator smoke run" -ForegroundColor Cyan
    dotnet run --project src/Micromound.Sim -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host "==> ALL VALIDATIONS PASSED (v$ver)" -ForegroundColor Green
