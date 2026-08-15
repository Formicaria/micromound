# Changelog

All notable changes to MICROMOUND. The Release workflow extracts the matching `## v<version>`
section as the release notes, so every releasable version must have one.

## v0.1.0

M0 hardened: real cryptography, real enforcement, frozen wire bytes — and the CI/Release train
that guards all of it.

- `Micromound.Crypto` — Ed25519 device identity: on-device keypairs, envelope signing and
  verification, a colony-side public-key directory with revocation. No unsigned mode exists.
- Layer 1 enforcement in the simulator: firmware ∩ charter limit clamping (`clamped` outcomes
  are loud), duty-cycle and rate refusals, and the `EvidenceGate` — a dead sensor turns a
  "successful" actuation into `unverified`, because commands are not evidence.
- Quiesce is now a reported state: lease expiry queues a `quiesced` sync report the colony
  reads on reconnect; only a fresh charter lifts it.
- Frozen wire bytes: golden files pin canonical envelope serialization and digests so the M3
  C firmware mirror cannot drift from the C# implementation silently.
- CI workflow: build+test on Linux and Windows, simulator lifecycle smoke run, docs/version
  consistency, and safety invariant guards (hazardous ceiling, limit clamp, evidence gate,
  unsigned-mode ban, golden files, reduced profile, SAFETY.md canon, no-Python).
- Release workflow: tag must match `MicromoundVersion`, full V&V before packaging,
  linux-x64 / linux-arm64 / win-x64 artifacts, draft GitHub Release with notes extracted
  from this file.
- Production repo structure: CodeQL C# analysis (PRs + weekly), Dependabot (NuGet + Actions),
  least-privilege workflow permissions, per-job timeouts, PR template with V&V and safety
  checklists, `scripts/validate.sh` mirroring CI locally.

## v0.0.1

M0 foundation. Nothing physical ships; the protocol and its invariants do.

- `docs/MICROMOUND.md` — canonical design doc: controller tiers (Edge Queen / Deterministic
  Controller), authority model (charters, leases, action classes), phase plan M0–M5.
- `docs/PROTOCOL.md` — signed envelope protocol: hash-chained uplink, enrollment, charters,
  lease lifecycle, evidence bundles, stop orders, reduced controller profile, ANTHILL-side
  endpoint surface for the Integrations tab (M1).
- `docs/SAFETY.md` — four-layer safety model. Independent safety systems are not
  AI-addressable; hazardous work is per-action authorized, never standing.
- `Micromound.Protocol` — envelopes with canonical digests, charter + fail-closed validation
  (full error lists, never silent), `LimitClamp` (charters narrow firmware limits, never
  widen), reduced-profile envelope enforcement.
- `Micromound.Sim` — network-free simulated mound proving the authority rules end to end.
- `Micromound.Tests` — 20 tests: charter validation, authority (no charter ⇒ no actuation,
  lease expiry quiesces, stop wins, no self-elevation), chain integrity (tamper + gap
  detection), reduced-profile refusals.
- Scaffolds for the M2 Edge Queen runtime and M3 ESP32 firmware.
- Conventions mirror ANTHILL: net9.0, C# 13, nullable, deterministic builds, xunit.
