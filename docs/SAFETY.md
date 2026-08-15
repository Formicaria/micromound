# MICROMOUND Safety Model

Canonical safety text. Where this document and any other file disagree, this document wins.
Nothing in this repository may weaken a rule here without this file changing first, loudly.

## Layer 0 — Independent safety systems (not ours)

Emergency stops, hardware watchdogs, interlocks, limit switches, thermal fuses, mechanical
stops, RCDs/breakers. These belong to the electrical and mechanical design of each device, sit
below all software in this repo, and are **not AI-addressable**:

- No protocol envelope, charter field, firmware routine, or Edge Queen tool may configure,
  suppress, reset, or depend on defeating a Layer 0 device.
- Software treats Layer 0 trips as observed facts to report (with evidence), never as states to
  manage or recover automatically.
- A device whose Layer 0 protection is known-faulty is unfit for any charter above `observe`.

## Layer 1 — Deterministic enforcement on-device

- Firmware/runtime clamps: every actuation passes through deterministic code that enforces the
  intersection of (a) firmware-compiled hardware limits and (b) charter limits. The narrower
  bound always wins; charters can only narrow.
- Software watchdog: loss of the runtime's own heartbeat drops actuation and enters the
  charter's declared `safe_state`. Safe states are de-energized/passive by construction.
- On the Edge Queen, any local model output is a *proposal* to the deterministic layer — the
  model holds no direct actuation path (mirrors ANTHILL: no mission agent holds apply
  permission).

## Layer 2 — Authority (charters and leases)

- No charter → `observe` only. Expired lease → `safe_state`. Ambiguity → downward.
- Disconnection never widens authority; nothing on-device can extend a lease.
- `hazardous`-class actions (physical risk to people, property, or the device's surroundings —
  fabrication tools, motion near people, building systems) require explicit per-action
  authorization from the Primary Colony, never a standing grant, expiring on use or timeout.
  Until M5 ships that pipeline with tests, hazardous actions are refused unconditionally.

## Layer 3 — Colony oversight

- Every actuation is audited with evidence; `unverified` actions gate missions as failures.
- Stops: physical (Layer 0), per-mound (API/UI), global (`.anthill/MICROMOUND_STOP`). Stop
  processing precedes all other downlink and needs no valid charter. Resume is always explicit,
  never automatic.
- The approval pipeline (IApprovable, blast-radius scoring) fronts all `controlled` actions,
  exactly as homelab actions are fronted.

## Prohibited by construction

- Unsigned protocol traffic; unsigned firmware acceptance of charters.
- Mound-to-mound delegation or authority transfer.
- Any code path that retries a `hazardous` action without fresh authorization.
- Any tool, endpoint, or envelope that reads back or exports device private keys.
- Silent failure: every refusal, clamp, trip, and validation failure is reported and audited.
