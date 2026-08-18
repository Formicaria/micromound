# MICROMOUND guarded release. Run this on main AFTER the version-bump PR is
# merged and local main matches origin/main.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
#
#   .\scripts\release.ps1
#   .\scripts\release.ps1 -Remote upstream
#
# Checks, all of which must pass before anything is pushed:
#   * you are on main
#   * the working tree is clean
#   * local main == <remote>/main
#   * CHANGELOG.md has a matching "## vX.Y.Z" section
#   * the tag does not already exist, locally or on the remote
#
# Same gates as scripts/release.sh, for a machine with no bash on PATH. If the
# two ever disagree, the .sh file is what CI would run and is the one that is right.
param([string]$Remote = "")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$props = Get-Content -Raw Directory.Build.props
if ($props -notmatch '<MicromoundVersion>([^<]*)</MicromoundVersion>') {
    Write-Host "x Could not read <MicromoundVersion> from Directory.Build.props" -ForegroundColor Red
    exit 1
}
$ver = $Matches[1]
$tag = "v$ver"

if (-not $Remote) { $Remote = (git config branch.main.remote) }
if (-not $Remote) { $Remote = "origin" }
$url = (git remote get-url $Remote 2>$null)
Write-Host "-> Releasing $tag via remote '$Remote' ($url)." -ForegroundColor Cyan

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") {
    Write-Host "x On '$branch', not main. Run: git checkout main; git pull" -ForegroundColor Red
    exit 1
}

$dirty = (git status --porcelain)
if ($dirty) {
    Write-Host "x Working tree is not clean. A tag must name a commit, not a commit plus your desk." -ForegroundColor Red
    git status --short
    exit 1
}

git fetch -q $Remote main
if ($LASTEXITCODE -ne 0) { Write-Host "x Could not fetch $Remote/main." -ForegroundColor Red; exit 1 }

$local  = (git rev-parse HEAD).Trim()
$origin = (git rev-parse "$Remote/main").Trim()
if ($local -ne $origin) {
    Write-Host "x Local main is not in sync with $Remote/main." -ForegroundColor Red
    Write-Host "  The version-bump PR is probably not merged yet, or you need: git pull $Remote main"
    exit 1
}

$changelog = Get-Content -Raw CHANGELOG.md
if ($changelog -notmatch "(?m)^## v$([regex]::Escape($ver))\b") {
    Write-Host "x CHANGELOG.md has no '## v$ver' section (release notes)." -ForegroundColor Red
    exit 1
}

git rev-parse $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "x Tag $tag already exists locally." -ForegroundColor Red
    Write-Host "  To re-release: git tag -d $tag; git push $Remote :refs/tags/$tag"
    exit 1
}
git ls-remote --exit-code --tags $Remote "refs/tags/$tag" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "x Tag $tag already exists on $Remote. Pick the next version instead of overwriting a release." -ForegroundColor Red
    exit 1
}

# The release notes ARE the changelog section: from "## v<ver>" to the next "## ".
$lines = Get-Content CHANGELOG.md
$notes = New-Object System.Collections.Generic.List[string]
$found = $false
foreach ($line in $lines) {
    if (-not $found) {
        if ($line -match "^## v$([regex]::Escape($ver))\b") { $found = $true }
        continue
    }
    if ($line -match '^## ') { break }
    if ($line -eq '---') { continue }
    $notes.Add($line)
}
$notesText = ($notes -join "`n").Trim()

Write-Host ""
Write-Host "--- release notes for $tag ---------------------------------------"
$notesText -split "`n" | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
Write-Host "------------------------------------------------------------------"
Write-Host ""
Write-Host "OK: on main, clean, synced with $Remote, Version=$ver, CHANGELOG has ## v$ver, $tag is free." -ForegroundColor Green

$answer = Read-Host "Tag and release $tag via $Remote? [y/N]"
if ($answer -notmatch '^(y|Y|yes)$') { Write-Host "Aborted."; exit 0 }

git tag -a $tag -m $tag
if ($LASTEXITCODE -ne 0) { Write-Host "x Could not create the tag." -ForegroundColor Red; exit 1 }
git push $Remote $tag
if ($LASTEXITCODE -ne 0) { Write-Host "x Tag push failed. Nothing was released." -ForegroundColor Red; exit 1 }
Write-Host "OK: pushed $tag to $Remote." -ForegroundColor Green

$gh = Get-Command gh -ErrorAction SilentlyContinue
if ($gh) {
    $notesFile = Join-Path $env:TEMP "micromound-release-notes.md"
    [IO.File]::WriteAllText($notesFile, $notesText, [Text.UTF8Encoding]::new($false))
    gh release create $tag --title $tag --notes-file $notesFile
    if ($LASTEXITCODE -eq 0) {
        Write-Host "OK: GitHub Release $tag created." -ForegroundColor Green
    } else {
        Write-Host "! Tag is pushed but the GitHub Release was not created." -ForegroundColor Yellow
        Write-Host "  Run: gh release create $tag --notes-file $notesFile"
    }
} else {
    Write-Host "! gh not found. The tag is pushed; create the Release manually from the section above." -ForegroundColor Yellow
}
