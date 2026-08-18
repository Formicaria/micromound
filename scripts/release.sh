#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# MICROMOUND guarded release. Run this on main AFTER the version-bump PR is
# merged and local main matches origin/main.
#
#   bash scripts/release.sh
#   bash scripts/release.sh --dry-run                 # evaluate every gate, tag nothing
#   RELEASE_REMOTE=upstream bash scripts/release.sh   # explicit remote override
#
# Gates, all of which must pass before anything is pushed:
#   • you are on main
#   • the working tree is clean
#   • local main == <remote>/main
#   • CHANGELOG.md has a matching "## vX.Y.Z" section
#   • the tag does not already exist, locally or on the remote
#
# --dry-run evaluates all of them and prints a table instead of stopping at the
# first failure. That exists because the PowerShell half of this pair could not
# release v0.2.1: it crashed before reaching the prompt, and there was no way to
# find that out short of attempting a real release. A gate that can only be
# exercised by the irreversible operation it guards is not a gate you can trust.
#
# Kept in step with scripts/release.ps1. This file is the authoritative one.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail
cd "$(dirname "$0")/.."

dry=0
[ "${1:-}" = "--dry-run" ] && dry=1

failures=0
gate() { # gate <name> <ok:0|1> <detail>
  if [ "$2" -eq 1 ]; then
    printf '  [ok]   %s\n' "$1"
  else
    printf '  [FAIL] %s: %s\n' "$1" "$3"
    failures=$((failures + 1))
    # Outside a dry run the first failure is fatal — a release must not proceed
    # past a gate just because the ones after it happen to pass.
    [ "$dry" -eq 1 ] || exit 1
  fi
}

ver="$(grep -o '<MicromoundVersion>[^<]*</MicromoundVersion>' Directory.Build.props \
       | head -1 | sed 's/.*>\(.*\)<.*/\1/')"
[ -n "$ver" ] || { echo "✗ Could not read <MicromoundVersion> from Directory.Build.props"; exit 1; }
tag="v$ver"

remote="${RELEASE_REMOTE:-$(git config branch.main.remote 2>/dev/null || true)}"
remote="${remote:-origin}"

echo
[ "$dry" -eq 1 ] && echo "DRY RUN — nothing will be tagged or pushed."
echo "→ $tag via remote '$remote' ($(git remote get-url "$remote" 2>/dev/null || echo '?'))"
echo

branch="$(git rev-parse --abbrev-ref HEAD)"
[ "$branch" = "main" ] && ok=1 || ok=0
gate "on main" $ok "on '$branch'. Run: git checkout main && git pull"

dirty="$(git status --porcelain | wc -l | tr -d ' ')"
[ "$dirty" -eq 0 ] && ok=1 || ok=0
gate "working tree clean" $ok "$dirty modified path(s). A tag must name a commit, not a commit plus your desk."

git fetch -q "$remote" main && fetched=1 || fetched=0
gate "fetched $remote/main" $fetched "could not reach $remote"

if [ "$fetched" -eq 1 ]; then
  local_head="$(git rev-parse HEAD)"
  remote_head="$(git rev-parse "$remote/main")"
  [ "$local_head" = "$remote_head" ] && ok=1 || ok=0
  gate "local main == $remote/main" $ok \
    "$local_head vs $remote_head. The version-bump PR may not be merged; try: git pull --ff-only $remote main"
fi

grep -q "^## $tag\b" CHANGELOG.md && ok=1 || ok=0
gate "CHANGELOG has ## $tag" $ok "no '## $tag' section to use as release notes"

# `git tag --list` prints the match or nothing and always exits 0.
[ -z "$(git tag --list "$tag")" ] && ok=1 || ok=0
gate "$tag free locally" $ok "already exists. To re-release: git tag -d $tag && git push $remote :refs/tags/$tag"

# Exit status, not just emptiness. An unreachable remote also prints nothing, and
# reading that as "the tag is free" would turn a failure to check into a pass —
# which is the same failure mode as a test that never runs.
remote_tags="$(git ls-remote --tags "$remote" "refs/tags/$tag" 2>/dev/null)"
if [ $? -ne 0 ]; then
  gate "$tag free on $remote" 0 "could not query $remote for tags; the tag state is unknown"
elif [ -z "$remote_tags" ]; then
  gate "$tag free on $remote" 1 ""
else
  gate "$tag free on $remote" 0 "already released. Pick the next version instead of overwriting a release."
fi

# The notes ARE the changelog section: from "## <tag>" to the next "## ". A "###"
# subheading does not match "^## " and so does not end the section.
notes="$(awk -v v="## $tag" '
  index($0, v) == 1 && !found {found=1; next}
  found && /^## / {exit}
  found {print}
' CHANGELOG.md | sed '/^---$/d')"

echo
if [ "$dry" -eq 1 ]; then
  if [ "$failures" -gt 0 ]; then
    echo "DRY RUN: $failures gate(s) would block this release."
    exit 1
  fi
  echo "DRY RUN: every gate passes. A real run would release $tag with these notes:"
  echo "──────────────────────────────────────────────────────────────────────"
  echo "$notes" | head -20
  echo "──────────────────────────────────────────────────────────────────────"
  exit 0
fi

echo "─── release notes for $tag ───────────────────────────────────────────"
echo "$notes" | head -40
echo "──────────────────────────────────────────────────────────────────────"
echo

read -r -p "Tag and release $tag via $remote? [y/N] " answer
case "$answer" in y|Y|yes) : ;; *) echo "Aborted."; exit 0 ;; esac

git tag -a "$tag" -m "$tag" || { echo "✗ Could not create the tag."; exit 1; }
git push "$remote" "$tag" || { echo "✗ Tag push failed. Nothing was released."; exit 1; }
echo "✓ Pushed $tag to $remote."

if command -v gh >/dev/null 2>&1; then
  printf '%s\n' "$notes" > /tmp/micromound-release-notes.md
  gh release create "$tag" --title "$tag" --notes-file /tmp/micromound-release-notes.md \
    && echo "✓ GitHub Release $tag created." \
    || echo "! Tag is pushed but the GitHub Release was not created. Run: gh release create $tag --notes-file /tmp/micromound-release-notes.md"
else
  echo "! gh not found. The tag is pushed; create the Release manually from the section above."
fi
