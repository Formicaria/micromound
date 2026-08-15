<!--
  MICROMOUND pull request. One PR per concern. Keep the diff focused.
  The PR title should read like the changelog line, e.g.:
    v0.0.2: <short summary>
-->

## Summary

<!-- What this PR does, in 1–3 sentences. -->

Closes #<!-- issue number -->

## Changes

<!-- Bullet the notable protocol / runtime / firmware / docs changes. -->
-

## Version

- [ ] `MicromoundVersion` in `Directory.Build.props` bumped (if this PR is releasable)
- [ ] `README.md` "Current version" marker matches
- [ ] `CHANGELOG.md` has a matching `## v<version>` section (the Release workflow extracts it)

## Verification

- [ ] `./scripts/validate.sh` green locally (or CI is green on this branch)
- [ ] New behavior has tests; tests fail without the change
- [ ] Protocol changes updated `docs/PROTOCOL.md` in the same PR (doc and code never split)

## Safety / non-regression

- [ ] No change weakens a SAFETY.md invariant without changing SAFETY.md itself, loudly, first
- [ ] Hazardous is still never a legal charter ceiling; per-action authorization only
- [ ] Disconnection still never widens authority (lease/quiesce tests untouched or strengthened)
- [ ] Charters still only narrow firmware limits (LimitClamp semantics intact)
- [ ] No path added that could configure, suppress, or reset a Layer 0 safety device
- [ ] No secrets or private keys in code, logs, tests, or fixtures

## Screenshots / notes

<!-- Optional extra context for reviewers. -->
