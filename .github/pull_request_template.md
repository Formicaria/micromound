<!--
  MICROMOUND pull request. One PR per concern. Keep the diff focused.
  The PR title should read like the changelog line, e.g.:
    v0.2.3: <short summary>
-->

## Summary

<!-- What this PR does, in 1–3 sentences. -->

Closes #<!-- issue number -->

## Changes

<!-- Bullet the notable protocol / kernel / runtime / firmware / docs changes. -->
-

## Version

- [ ] `MicromoundVersion` in `Directory.Build.props` bumped (if this PR is releasable)
- [ ] `CHANGELOG.md` has a matching `## v<version>` section (design rule 9; release notes come from it)
- [ ] The entry calls out anything that **narrows or widens authority**, changes the
      **canonical wire bytes**, or alters a **refusal reason**

## Verification

- [ ] `bash scripts/validate.sh --full` green locally (or CI is green on this branch)
- [ ] New behavior has tests; tests fail without the change
- [ ] Wire-contract changes regenerated the golden fixtures ON PURPOSE, and the fixture diff was
      reviewed (`MICROMOUND_UPDATE_GOLDEN=1 dotnet test`, then a clean validate run)
- [ ] Protocol changes updated `docs/PROTOCOL.md` in the same PR (doc and code never split)

## Safety / non-regression

- [ ] No change weakens a SAFETY.md invariant without changing SAFETY.md itself, loudly, first
- [ ] Hazardous is still never a legal charter ceiling; per-action authorization only
- [ ] Nothing above the capability kernel gained a way to widen a bound below it
- [ ] Disconnection still never widens authority (lease/quiesce tests untouched or strengthened)
- [ ] Unsupported success still degrades to `unverified` — commands are not evidence
- [ ] No path added that could configure, suppress, or reset a Layer 0 safety device
- [ ] No secrets or private keys in code, logs, tests, or fixtures

## Screenshots / notes

<!-- Optional extra context for reviewers. -->
