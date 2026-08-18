#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# MICROMOUND centralized validation. One command, the same steps CI runs.
#
#   bash scripts/validate.sh          # guards + restore + build + test
#   bash scripts/validate.sh --full   # also runs the simulator smoke run
#
# Mirrors ANTHILL's scripts/validate.sh so the two repositories stay
# convention-compatible, but the guards at the top are this repository's own —
# they exist because a green test run here is a claim about what a device will
# do to the physical world, and two specific ways of faking that green are
# cheap enough to be worth blocking outright.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
cd "$(dirname "$0")/.."

# ── Guard 1: golden fixtures must not be regenerating ───────────────────────
# MICROMOUND_UPDATE_GOLDEN=1 makes the canonical-byte tests rewrite their own
# expectations and pass unconditionally. That is the right tool when the wire
# contract changes on purpose, and it is indistinguishable from a green run
# afterwards. Validation refuses to run under it rather than report a pass that
# means nothing.
if [[ "${MICROMOUND_UPDATE_GOLDEN:-}" != "" ]]; then
  echo "✗ MICROMOUND_UPDATE_GOLDEN is set — the golden tests would rewrite their fixtures."
  echo "  Unset it and re-run. To regenerate on purpose:"
  echo "     MICROMOUND_UPDATE_GOLDEN=1 dotnet test Micromound.sln"
  echo "  then review the fixture diff and run this script clean."
  exit 1
fi
echo "==> golden fixtures are being verified, not regenerated"

# ── Guard 2: the version and the changelog agree ────────────────────────────
# Design rule 9 requires a changelog entry per stateful feature, and
# scripts/release.sh refuses to tag without a matching section. Catching the
# mismatch here means catching it before the PR rather than at the tag.
ver="$(grep -o '<MicromoundVersion>[^<]*</MicromoundVersion>' Directory.Build.props \
       | head -1 | sed 's/.*>\(.*\)<.*/\1/')"
[ -n "$ver" ] || { echo "✗ Could not read <MicromoundVersion> from Directory.Build.props"; exit 1; }

if [ ! -f CHANGELOG.md ]; then
  echo "✗ CHANGELOG.md is missing (design rule 9)."
  exit 1
fi

grep -q "^## v$ver\b" CHANGELOG.md || {
  echo "✗ Directory.Build.props says $ver, but CHANGELOG.md has no '## v$ver' section."
  exit 1
}
echo "==> version $ver has a CHANGELOG section"

# ── The build ───────────────────────────────────────────────────────────────
echo "==> dotnet restore"
dotnet restore Micromound.sln

echo "==> dotnet build (Release)"
dotnet build Micromound.sln -c Release --no-restore

echo "==> dotnet test (Release) — protocol contracts, authority, kernel, limits, signatures, golden bytes"
dotnet test Micromound.sln -c Release --no-build

if [[ "${1:-}" == "--full" ]]; then
  # The simulator runs the real kernel over fake hardware, so this exercises the
  # authority path end to end — charter, lease, clamp, quiesce, backlog, and a
  # chain verified under the wrong key — in one process.
  echo "==> simulator smoke run"
  dotnet run --project src/Micromound.Sim -c Release --no-build
fi

echo "==> ALL VALIDATIONS PASSED (v$ver)"
