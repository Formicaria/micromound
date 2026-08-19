# MicroMound Safety Model

Canonical safety text. Where this document and any other file disagree, this document wins.
Nothing in this repository may weaken a rule here without this file changing first, loudly.

## Layer 0 — Independent safety systems (not ours)

Emergency stops, hardware watchdogs, interlocks, limit switches, thermal fuses, mechanical stops,
RCDs and breakers. These belong to the electrical and mechanical design of each device, sit below
all software in this repository, and are **not addressable by anything here**:

- No protocol envelope, charter field, manifest entry, routine, driver, or reasoning provider may
  configure, suppress, reset, or depend on defeating a Layer 0 device.
- Software treats Layer 0 trips as observed facts to report — with evidence — never as states to
  manage or recover from automatically. A Guard Ant reports an interlock trip; it does not clear
  one.
- A device whose Layer 0 protection is known-faulty is unfit for any charter above `observe`.

## Layer 1 — Deterministic enforcement on-device

The capability kernel is the single physical authority boundary. Every actuation, on every
hardware tier, passes through it — not by convention but by construction: drivers are reachable
only through `ICapabilityExecutor`, executors are held only by the kernel, and nothing hands one
out.

**Limits intersect across three tiers, innermost first:**

```text
hardware/firmware   ∩   device manifest   ∩   charter   =   effective
```

Ceilings take the minimum, floors take the maximum. An outer tier can only narrow. A charter that
asks for a longer run than the relay tolerates, or a shorter cooldown than the pump requires, does
not get one — the request is intersected away at execution and the attempt is reported at
validation.

Also at this layer:

- **Software watchdog.** Loss of the runtime's own heartbeat drops actuation and enters the
  declared `safe_state`. Safe states are de-energized or passive by construction. Enforced by the
  Guard Ant (`Micromound.Runtime`): a stale heartbeat or an observed safety trip makes it demand a
  safe state, and the coordinator engages the stop rather than continuing. A stale heartbeat is
  self-healing — a watchdog that latched on a scheduling hiccup is one nobody leaves enabled — but
  **an observed trip is sticky and nothing in software clears it**, because software that could
  clear a trip is software that could be asked to.
- **Clamp, don't lie.** Where a limit narrows a request, the work proceeds and the outcome is
  `clamped`, carrying both the requested and the effective parameters plus the limit responsible.
  A silent clamp is a false statement about what the mound did.
- **Model output is a proposal.** Any reasoning provider produces proposals to the deterministic
  layer and holds no actuation path. This is enforced structurally: `Micromound.Reasoning` does
  not reference `Micromound.Capabilities`, so a provider cannot call the kernel, hold an executor,
  or touch a driver.

## Layer 2 — Authority (charters and leases)

- No charter → `observe` only. Expired lease → `safe_state`. Ambiguity → downward.
- Disconnection never widens authority; nothing on-device can extend a lease. Renewal happens only
  when the controller acknowledges a sync beat.
- Reconnection resumes nothing. A quiesced mound reports its state and waits for fresh authority.
- Registration-time refusals, because a misconfigured device should fail at startup rather than at
  first use: a `sense.` capability may not be classed above `observe`; nothing may be registered
  as `hazardous`; a routine may not be classed below a capability it drives.
- **`hazardous`-class actions** — physical risk to people, property, or surroundings; fabrication
  tools, motion near people, building systems — require explicit per-action authorization from the
  controller, never a standing grant, expiring on use or timeout. **Until that pipeline ships with
  tests, hazardous actions are refused unconditionally**, and `hazardous` is not a legal charter
  ceiling.

## Layer 3 — Controller oversight

- Every actuation is audited with evidence; `unverified` actions gate missions as failures.
- Stops: physical (Layer 0), per-mound, and global. Stop processing precedes all other downlink
  and needs no valid charter. Clearing a stop restores nothing — the mound returns to
  observe-only and waits for a fresh charter.
- **A stop ceases actuation; it does not blind the mound.** Observation continues, as PROTOCOL.md
  §7 has always specified, and the same section requires the stop acknowledgement to carry a
  post-stop sensor snapshot — which a mound that refused to sense could never produce. Refusing
  every capability would also darken the instruments at the exact moment an operator most needs to
  see what the hardware is doing. The kernel decides this from the capability id's namespace,
  before the registry is consulted, so stop still works when the registry, the charter and the
  drivers are all broken.
- An approval pipeline fronts all `controlled` actions.

## Prohibited by construction

- Unsigned protocol traffic; unsigned acceptance of charters or configuration.
- Mound-to-mound delegation or authority transfer.
- Any code path that retries a `hazardous` action without fresh authorization.
- Any tool, endpoint, or envelope that reads back or exports a device private key.
- Any argument, field, or flag through which a caller can request an exception on grounds of
  urgency. Ambiguity resolves downward, and the way to guarantee that is to give ambiguity
  nowhere to enter.
- **Silent failure.** Every refusal, clamp, trip, and validation failure is reported and audited,
  with a specific reason. A refusal without a reason is itself a contract violation.
