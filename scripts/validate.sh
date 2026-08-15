#!/usr/bin/env bash
# MICROMOUND centralized validation, mirroring ANTHILL's scripts/validate.sh.
# One command that runs every required recurring validation. CI runs the same steps, so a green
# run here should predict a green CI run — that is the whole point of the script.
#
#   ./scripts/validate.sh           # restore + build + test + sim smoke + consistency guards
#   ./scripts/validate.sh --quick   # restore + build + test only
set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> dotnet restore"
dotnet restore Micromound.sln

echo "==> dotnet build (Release)"
dotnet build Micromound.sln -c Release --no-restore

echo "==> dotnet test (Release)"
dotnet test Micromound.sln -c Release --no-build

if [[ "${1:-}" == "--quick" ]]; then
  echo "==> QUICK VALIDATION PASSED (sim smoke + guards skipped)"
  exit 0
fi

echo "==> Simulator smoke run (authority lifecycle: clamp, evidence gate, quiesce, signatures)"
out=$(dotnet run --project src/Micromound.Sim -c Release)
echo "$out"
for expect in \
  "charter accepted=True state=chartered" \
  "actuate 60s -> clamped" \
  "actuate 30s later -> refused" \
  "actuate with dead sensor -> unverified" \
  "lease expired -> quiesced=True state=quiesced" \
  "actuate after expiry -> refused" \
  "chain+signatures valid=True" \
  "same backlog under a wrong key -> valid=False"
do
  grep -q "$expect" <<<"$out" || { echo "FAIL: sim output missing '$expect'"; exit 1; }
done

echo "==> Version markers agree (props / README / CHANGELOG)"
pver=$(grep -oP '(?<=<MicromoundVersion>)[^<]+' Directory.Build.props)
grep -q "\*\*Current version:\*\* v$pver" README.md \
  || { echo "FAIL: README 'Current version' marker != Directory.Build.props ($pver)"; exit 1; }
grep -q "^## v$pver" CHANGELOG.md \
  || { echo "FAIL: CHANGELOG.md has no '## v$pver' entry"; exit 1; }

echo "==> Safety invariant guards"
grep -q "hazardous' is never a legal charter ceiling" src/Micromound.Protocol/Validation.cs \
  || { echo "FAIL: hazardous-ceiling refusal missing from CharterValidator"; exit 1; }
grep -q "Hazardous_is_never_a_legal_ceiling" tests/Micromound.Tests/CharterValidationTests.cs \
  || { echo "FAIL: hazardous-ceiling test missing"; exit 1; }
grep -q "class LimitClamp" src/Micromound.Protocol/Validation.cs \
  || { echo "FAIL: LimitClamp missing"; exit 1; }
grep -q "A_charter_cannot_widen_firmwares_on_time" tests/Micromound.Tests/LimitEnforcementTests.cs \
  || { echo "FAIL: firmware-widening test missing"; exit 1; }
grep -q "class EvidenceGate" src/Micromound.Protocol/EvidenceGate.cs \
  || { echo "FAIL: EvidenceGate missing"; exit 1; }
grep -q "An_unsigned_envelope_is_refused" tests/Micromound.Tests/SignatureTests.cs \
  || { echo "FAIL: unsigned-envelope refusal test missing"; exit 1; }
for f in tests/Micromound.Tests/Golden/files/canonical-envelopes.txt \
         tests/Micromound.Tests/Golden/files/canonical-bodies.txt; do
  [ -s "$f" ] || { echo "FAIL: golden file missing or empty: $f"; exit 1; }
done
for phrase in "not AI-addressable" "never a standing grant" "Resume is always explicit"; do
  grep -q "$phrase" docs/SAFETY.md || { echo "FAIL: SAFETY.md lost canonical phrase: '$phrase'"; exit 1; }
done

echo "==> ALL VALIDATIONS PASSED"
