# MICROMOUND guarded release. Run this on main AFTER the version-bump PR is
# merged and local main matches origin/main.
# ASCII-only on purpose: Windows PowerShell 5.1 parses BOM-less .ps1 files as ANSI.
#
#   .\scripts\release.ps1
#   .\scripts\release.ps1 -DryRun            # evaluate every gate, tag nothing
#   .\scripts\release.ps1 -Remote upstream
#
# Gates, all of which must pass before anything is pushed:
#   * you are on main
#   * the working tree is clean
#   * local main == <remote>/main
#   * CHANGELOG.md has a matching "## vX.Y.Z" section
#   * the tag does not already exist, locally or on the remote
#
# -DryRun evaluates all of them and prints a table instead of stopping at the
# first failure. That exists because v0.2.1 could not be released by this script:
# it crashed before reaching the prompt, and there was no way to find that out
# short of attempting a real release. A gate that can only be exercised by the
# irreversible operation it guards is not a gate you can trust.
#
# Same checks as scripts/release.sh. If the two ever disagree, the .sh file is
# what CI would run and is the one that is right.
param([switch]$DryRun, [string]$Remote = "")

# NOT "Stop", and this is the bug that ate the v0.2.1 release. Windows PowerShell
# turns anything a native command writes to stderr into an ErrorRecord, and under
# ErrorActionPreference = "Stop" that terminates the script. `2>$null` redirects
# the stream, not the record. `git rev-parse v0.2.1` on a tag that does not exist
# yet writes to stderr AS ITS WAY OF SAYING "no such tag" -- so the script died on
# the good news. Native commands are checked by $LASTEXITCODE or by their output,
# which is what those things are for; PowerShell cmdlets that must not fail carry
# an explicit -ErrorAction Stop.
$ErrorActionPreference = "Continue"

Set-Location (Join-Path $PSScriptRoot "..") -ErrorAction Stop

$failures = New-Object System.Collections.Generic.List[string]

function Gate([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Host ("  [ok]   {0}" -f $name) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] {0}: {1}" -f $name, $detail) -ForegroundColor Red
        $script:failures.Add("$name : $detail")
    }
    # Outside a dry run the first failure is fatal -- a release must not proceed
    # past a gate just because the ones after it happen to pass.
    if (-not $ok -and -not $DryRun) { exit 1 }
}

# ---- version ---------------------------------------------------------------
# -Encoding UTF8 everywhere a repository file is read: PS 5.1 reads a BOM-less
# UTF-8 file as ANSI, which turns every em dash in CHANGELOG.md into mojibake --
# and those bytes become the published release notes.
$props = Get-Content -Raw Directory.Build.props -Encoding UTF8 -ErrorAction Stop
if ($props -notmatch '<MicromoundVersion>([^<]*)</MicromoundVersion>') {
    Write-Host "x Could not read <MicromoundVersion> from Directory.Build.props" -ForegroundColor Red
    exit 1
}
$ver = $Matches[1]
$tag = "v$ver"

if (-not $Remote) { $Remote = (git config branch.main.remote) }
if (-not $Remote) { $Remote = "origin" }
$url = (git remote get-url $Remote)

Write-Host ""
if ($DryRun) { Write-Host "DRY RUN - nothing will be tagged or pushed." -ForegroundColor Yellow }
Write-Host ("-> $tag via remote '$Remote' ({0})" -f $url) -ForegroundColor Cyan
Write-Host ""

# ---- gates -----------------------------------------------------------------
$branch = "$(git rev-parse --abbrev-ref HEAD)".Trim()
Gate "on main" ($branch -eq "main") "on '$branch'. Run: git checkout main; git pull"

$dirty = @(git status --porcelain)
Gate "working tree clean" ($dirty.Count -eq 0) `
    "$($dirty.Count) modified path(s). A tag must name a commit, not a commit plus your desk."

git fetch -q $Remote main
$fetched = ($LASTEXITCODE -eq 0)
Gate "fetched $Remote/main" $fetched "could not reach $Remote"

if ($fetched) {
    $localHead  = "$(git rev-parse HEAD)".Trim()
    $remoteHead = "$(git rev-parse "$Remote/main")".Trim()
    Gate "local main == $Remote/main" ($localHead -eq $remoteHead) `
        "$localHead vs $remoteHead. The version-bump PR may not be merged; try: git pull --ff-only $Remote main"
}

$changelogRaw = Get-Content -Raw CHANGELOG.md -Encoding UTF8 -ErrorAction Stop
Gate "CHANGELOG has ## $tag" ($changelogRaw -match "(?m)^## $([regex]::Escape($tag))\b") `
    "no '## $tag' section to use as release notes"

# `git tag --list` prints the match or nothing and always exits 0 -- no stderr,
# nothing for PowerShell to turn into an error. This is the check that crashed.
$localTag = @(git tag --list $tag)
Gate "$tag free locally" ($localTag.Count -eq 0) `
    "already exists. To re-release: git tag -d $tag; git push $Remote :refs/tags/$tag"

# Exit status, not just emptiness. An unreachable remote also prints nothing, and
# reading that as "the tag is free" would turn a failure to check into a pass --
# which is the same failure mode as a test that never runs.
$remoteTag = @(git ls-remote --tags $Remote "refs/tags/$tag" 2>$null)
if ($LASTEXITCODE -ne 0) {
    Gate "$tag free on $Remote" $false "could not query $Remote for tags; the tag state is unknown"
} else {
    Gate "$tag free on $Remote" ($remoteTag.Count -eq 0) `
        "already released. Pick the next version instead of overwriting a release."
}

# ---- release notes ---------------------------------------------------------
# The notes ARE the changelog section: from "## <tag>" to the next "## ". A "###"
# subheading does not match "^## " and so does not end the section.
$notes = New-Object System.Collections.Generic.List[string]
$found = $false
foreach ($line in (Get-Content CHANGELOG.md -Encoding UTF8 -ErrorAction Stop)) {
    if (-not $found) {
        if ($line -match "^## $([regex]::Escape($tag))\b") { $found = $true }
        continue
    }
    if ($line -match '^## ') { break }
    if ($line -eq '---') { continue }
    $notes.Add($line)
}
$notesText = ($notes -join "`n").Trim()

Write-Host ""
if ($DryRun) {
    if ($failures.Count -gt 0) {
        Write-Host "DRY RUN: $($failures.Count) gate(s) would block this release." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "DRY RUN: every gate passes. A real run would release $tag with these notes:" -ForegroundColor Green
    Write-Host "------------------------------------------------------------------"
    $notesText -split "`n" | Select-Object -First 20 | ForEach-Object { Write-Host $_ }
    Write-Host "------------------------------------------------------------------"
    exit 0
}

Write-Host "--- release notes for $tag ---------------------------------------"
$notesText -split "`n" | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
Write-Host "------------------------------------------------------------------"
Write-Host ""

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
