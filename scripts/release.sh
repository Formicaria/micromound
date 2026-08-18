#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# MICROMOUND guarded release. Run this on main AFTER the version-bump PR is
# merged and local main matches origin/main.
#
#   bash scripts/release.sh
#   RELEASE_REMOTE=upstream bash scripts/release.sh   # explicit remote override
#
# Checks, all of which must pass before anything is pushed:
#   • you are on main
#   • the working tree is clean
#   • local main == <remote>/main
#   • CHANGELOG.md has a matching "## vX.Y.Z" section
#   • the tag does not already exist, locally or on the remote
#
# Mirrors ANTHILL's scripts/release.sh. The difference worth knowing: ANTHILL
# has a release workflow that a pushed tag triggers, and this repository does
# not yet — so the GitHub Release is created here, from the CHANGELOG section
# that the tag is named after. One source for the release notes, not two.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail
cd "$(dirname "$0")/.."

ver="$(grep -o '<MicromoundVersion>[^<]*</MicromoundVersion>' Directory.Build.props \
       | head -1 | sed 's/.*>\(.*\)<.*/\1/')"
[ -n "$ver" ] || { echo "✗ Could not read <MicromoundVersion> from Directory.Build.props"; exit 1; }
tag="v$ver"

remote="${RELEASE_REMOTE:-$(git config branch.main.remote 2>/dev/null || true)}"
remote="${remote:-origin}"
echo "→ Releasing $tag via remote '$remote' ($(git remote get-url "$remote" 2>/dev/null || echo '?'))."

branch="$(git rev-parse --abbrev-ref HEAD)"
[ "$branch" = "main" ] || { echo "✗ On '$branch', not main. Run: git checkout main && git pull"; exit 1; }

if [ -n "$(git status --porcelain)" ]; then
  echo "✗ Working tree is not clean. A tag must name a commit, not a commit plus your desk."
  git status --short
  exit 1
fi

git fetch -q "$remote" main || { echo "✗ Could not fetch $remote/main."; exit 1; }
if [ "$(git rev-parse HEAD)" != "$(git rev-parse "$remote/main")" ]; then
  echo "✗ Local main is not in sync with $remote/main."
  echo "  The version-bump PR is probably not merged yet, or you need: git pull $remote main"
  exit 1
fi

grep -q "^## v$ver\b" CHANGELOG.md || {
  echo "✗ CHANGELOG.md has no '## v$ver' section (release notes)."
  exit 1
}

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "✗ Tag $tag already exists locally."
  echo "  To re-release: git tag -d $tag && git push $remote :refs/tags/$tag"
  exit 1
fi
if git ls-remote --exit-code --tags "$remote" "refs/tags/$tag" >/dev/null 2>&1; then
  echo "✗ Tag $tag already exists on $remote. Pick the next version instead of overwriting a release."
  exit 1
fi

# The release notes ARE the changelog section. Extract from "## v<ver>" to the
# next "## " heading, dropping the horizontal rule the file uses as a separator.
notes="$(awk -v v="## v$ver" '
  $0 ~ "^" v {found=1; next}
  found && /^## / {exit}
  found {print}
' CHANGELOG.md | sed '/^---$/d')"

echo
echo "─── release notes for $tag ───────────────────────────────────────────"
echo "$notes" | head -40
echo "──────────────────────────────────────────────────────────────────────"
echo
echo "✓ On main, clean, synced with $remote, Version=$ver, CHANGELOG has ## v$ver, $tag is free."
read -r -p "Tag and release $tag via $remote? [y/N] " ok
case "$ok" in y|Y|yes) : ;; *) echo "Aborted."; exit 0 ;; esac

git tag -a "$tag" -m "$tag"
git push "$remote" "$tag" || { echo "✗ Tag push failed. Nothing was released."; exit 1; }
echo "✓ Pushed $tag to $remote."

if command -v gh >/dev/null 2>&1; then
  printf '%s\n' "$notes" > /tmp/micromound-release-notes.md
  gh release create "$tag" --title "$tag" --notes-file /tmp/micromound-release-notes.md \
    && echo "✓ GitHub Release $tag created." \
    || echo "! Tag is pushed but the GitHub Release was not created. Run: gh release create $tag --notes-file /tmp/micromound-release-notes.md"
else
  echo "! gh not found. The tag is pushed; create the Release manually from the CHANGELOG section above."
fi
