# MicroMound Protocol — v0

Wire contract between an upstream controller and MicroMound devices. Implemented in
`src/Micromound.Protocol` (C# records, System.Text.Json, snake_case). This document is normative;
code and tests must match it.

The controller is whatever authority signs this mound's charters — see [`UPSTREAM.md`](UPSTREAM.md).
The protocol does not name one.

## 1. Transport

- Device-initiated HTTPS only. Mounds dial the controller at `/micromound/v0/*`; the controller
  never needs to reach into the device network.
- The **sync beat**: each mound POSTs a `mound_sync` envelope on an interval set by its charter
  (default 15 s for a Pi-class mound, 60 s for a controller; exponential backoff with jitter on
  failure). The response carries any pending downlink — charter updates, configuration, mission
  assignments, stop orders.
- Offline is a normal state, not an error. Uplink envelopes queue durably on-device and drain
  oldest-first on reconnect.

## 2. Envelope

Every message in either direction is one signed envelope:

```json
{
  "v": 0,
  "id": "uuid",
  "mound_id": "mm-7f3a…",
  "seq": 4182,
  "sent_at": "2026-08-14T21:04:11Z",
  "kind": "mound_sync | charter | config | mission | mission_report | action_record | evidence_bundle | stop | ack | enroll",
  "body": { },
  "prev_digest": "sha256:…",
  "sig": "ed25519:…"
}
```

- `seq` is per-mound, monotonic, gap-checkable. `prev_digest` hash-chains a mound's uplink stream
  so offline gaps and tampering are detectable (§6).
- `sig`: `ed25519:<lowercase hex>`. Mounds sign uplink with their device key; the controller signs
  downlink with its own. Unsigned or badly signed envelopes are dropped and audited, never
  processed. There is no unsigned mode and no trust-on-first-use: a key the verifier's directory
  does not hold is a refusal, because enrollment (§3) is the only way a key becomes known.
- Refusals are specific, not boolean: `missing`, `malformed_format`, `unsupported_algorithm`,
  `unknown_key`, `bad_signature`. "Dropped and audited" is only auditable if the reason survives.
- Unknown `kind`: refuse with an `ack` carrying `status: "refused_unknown_kind"`. Unknown fields
  within a known kind are ignored. Refusal is loud, never silent.
- The `ack` body is typed: `status` (`ok` | `refused` | `refused_unknown_kind`), `refers_to`
  (envelope id), `through_seq` (cumulative, inclusive; negative acknowledges nothing), and
  `evidence_ids` (received and stored, so the device may evict them under pressure). `through_seq`
  is what lets a mound let go of its records — until an ack covers a sequence number, the uplink
  queue retains the envelope and re-sends it, and the receiver deduplicates by sequence. A refusal
  ack never advances `through_seq`: acknowledging an envelope nobody processed would tell the
  sender to discard it.

### Canonical bytes (normative — these are what gets signed)

The signature covers the envelope's **canonical bytes**, and `prev_digest` hashes those same
bytes. So signing never perturbs the chain and the chain never covers the signature — both are
checked, separately. A device can therefore sign and hash one buffer it has already built, which
is what makes the reduced controller profile (§8) practical.

**`sig` is zeroed, not omitted.** The canonical bytes contain the field with an empty value:

```text
…,"prev_digest":"","sig":""}
```

This matters more than it reads. A C mirror written to "exclude the signature" would drop the
field entirely and produce a different digest for identical data — the exact silent divergence the
golden fixtures exist to prevent.

### Encoding rules (normative)

Two implementations that agree on the data and disagree on the encoding produce different digests,
and the disagreement surfaces as an unverifiable device in the field. So:

- **Timestamps** are exactly `yyyy-MM-ddTHH:mm:ssZ` — UTC, second precision, no fractional digits,
  no numeric offset. Twenty fixed bytes, formattable with one `snprintf`. Emitters must produce
  this form; readers should also accept an offset form, because a mound built against an older
  library is better read than bricked.
- **Escaping is minimal**: `+` is a literal `+`, a quote inside a string is `\"`. No hand-written
  C encoder emits the HTML-safe forms, and the C# side must not either.
- **Every field is always present**, including nulls for unset optional limits. A fixed shape
  means a firmware encoder never branches on whether an optional field is set.
- **Field order is declaration order**, and it is part of the contract.

`tests/Micromound.Tests/Golden/` freezes the resulting bytes; the C mirror is verified against
those files.

## 3. Enrollment

1. An operator creates the mound in the controller, which mints a one-time enrollment token stored
   write-only.
2. The device boots with the token, generates its Ed25519 keypair on-device (private key never
   leaves), and POSTs `enroll` with its public key, hardware profile, and controller tier.
3. The controller binds the public key to the mound record, burns the token, and returns the
   controller public key. From here on, only signed traffic.
4. Re-enrollment (key rotation, reflash) requires a new operator-minted token. There is no
   self-service re-key.

## 4. Charter

```json
{
  "charter_id": "uuid",
  "mound_id": "mm-7f3a…",
  "mission_ref": "mission id",
  "issued_at": "…", "expires_at": "…",
  "lease_ttl_s": 900,
  "action_ceiling": "observe | benign | controlled",
  "capabilities": ["sense.soil_moisture", "sense.temperature", "act.water_valve"],
  "routines": ["routine.water_cycle"],
  "limits": { "act.water_valve": { "max_on_s": 30, "min_off_s": 300, "min": null, "max": null, "max_rate_per_h": 6 } },
  "evidence": { "required_for": ["act.*", "routine.*"], "min_interval_s": 60 },
  "safe_state": "all_actuators_off",
  "sync_interval_s": 15
}
```

- `hazardous` never appears as a charter ceiling: hazardous work is authorized per-action and
  expires on use.
- Charters are complete replacements, never diffs. A mound holds at most one active charter per
  mission; a new charter supersedes; absence of a charter means `observe` only.
- `capabilities` and `routines` are separate lists. A `routine.` id in `capabilities` is a
  drafting error and fails validation. A routine's backing capabilities need not be separately
  granted — the routine is the unit of delegation.
- `limits` is keyed by capability **or** routine id, and every key must match something the
  charter granted. It is the outermost of the three tiers the kernel intersects (§6); geofence and
  workspace bounds are expressed as `min`/`max` on the relevant positional capability.
- The mound validates every charter: signature, its own `mound_id`, expiry sanity, that every
  capability is one it physically has, and that every routine is one it registers. Validation
  failure → refuse and report, do nothing.
- A charter is not accepted while a stop is in force. Clearing a stop is an explicit act, and
  paperwork must not be able to substitute for it.

## 5. Lease lifecycle

- Each acknowledged `mound_sync` renews the lease to `now + lease_ttl_s`. That is the only
  renewal path; nothing on-device can extend a lease.
- Disconnected: the mound continues only in-progress authorized work. At lease expiry it enters
  `safe_state`, keeps sensing where the charter covers it, keeps recording evidence, and waits.
- Reconnect after expiry: the mound uploads its backlog and reports `quiesced`. Fresh authority
  must be issued to resume — resumption is never implicit, and renewal is not resumption.

## 6. Action records and evidence

Every actuation produces an `action_record`:

```json
{
  "action_id": "uuid",
  "mission_id": "uuid or empty",
  "charter_id": "uuid",
  "capability": "act.water_valve",
  "routine_id": "routine id, or empty for a direct request",
  "requested_parameters": { "on_s": 60 },
  "parameters": { "on_s": 30 },
  "started_at": "…", "ended_at": "…",
  "outcome": "succeeded | failed | clamped | refused | stopped | unverified",
  "evidence_required": true,
  "evidence_refs": ["uuid"],
  "detail": "…"
}
```

- **Both parameter sets are carried.** `requested_parameters` is what a worker asked for;
  `parameters` is what actually ran. They differ whenever a limit narrowed the request, and
  reporting only the effective value would hide the clamp from the audit trail that exists to
  surface it.
- **Limits intersect across three tiers**, innermost first: hardware/firmware ∩ device manifest
  (§10) ∩ charter (§4). Ceilings take the minimum, floors the maximum. An outer tier can only
  narrow.
- `detail` carries the reason for any non-plain-success outcome: the limit that clamped it, the
  rule that refused it, the evidence that was missing. Refusal reasons are a closed set —
  `stopped`, `unknown_capability`, `capability_unavailable`, `no_charter`, `lease_expired`,
  `not_granted`, `routine_not_registered`, `routine_not_enabled`, `action_class_exceeded`,
  `hazardous_prohibited`, `missing_parameter`, `unknown_parameter`, `duty_cycle`, `rate_limit`,
  `executor_missing`, `driver_fault`.
- Refused actions are queued for the controller exactly like successful ones.
- **Evidence gating is mechanical.** An outcome that asserts physical work happened (`succeeded`,
  `clamped`) survives only if every referenced evidence item resolves, parses, and — where
  `evidence.required_for` covers the capability — was captured within `min_interval_s` of the
  action start. Anything else becomes `unverified`. A `refused` or `stopped` record needs no
  proof: it is a definite outcome, not a claim about the physical world.
- For a routine invocation, `capability` and `routine_id` both carry the routine id, because
  evidence policies pattern-match on `capability`.
- `evidence_bundle`: batched sensor windows, images (content-addressed, fetched lazily), telemetry
  summaries. Bundles are hash-chained via envelope `prev_digest`.

### Numeric readings (normative)

`payload_json` is a string, and for most evidence kinds its contents are opaque to the protocol.
One shape is not opaque, because decisions are made from it. An evidence item of type `reading`
carries:

```json
{"value":17.0,"unit":"percent","capability":"sense.soil_moisture"}
```

- `value` — the number. Required, and the only required member.
- `unit` — as the driver reports it (`percent`, `celsius`, `litres_per_minute`). Advisory: no
  runtime path converts units, and a mission comparing a threshold is comparing raw values.
- `capability` — so a bare item is self-describing once separated from its action record.

A mission step's `condition` (§9) compares an earlier step's reading against a constant, and a
`mission_report` step result carries one in `value`. Both are defined in terms of this number, so
without it a mission can be validated and never executed.

Reading is deliberately tolerant: **any** payload carrying a numeric `value` member is accepted,
whatever else it contains and whatever `type` says. Writing is strict — one shape, serialized with
the §2 encoding rules, because these bytes end up inside a signed envelope.

An item whose payload is absent, unparseable, or carries no numeric `value` does not prove a
number. Every consumer treats those three cases identically, and none of them is an error to
handle: the reading simply does not exist, and nothing may be decided from it. A condition whose
source produced no readable value is **refused**, never quietly treated as false — "I could not
see" and "the threshold was not met" are different facts, and collapsing them is how a mound skips
watering a dying plant and reports success.

This is a convention on the contents of an existing string field, not a new field. The v0 canonical
bytes frozen at `v0.2.1` are unchanged.
- Retention on-device: a ring buffer sized by the hardware profile, bounded by two ordered rules.
  Acknowledged proof is reclaimed oldest-first past the soft capacity, reported as
  `evicted_acked_items`. Unacknowledged proof is retained past the soft capacity — silently
  dropping it would be indistinguishable from never capturing it — but not past a hard ceiling:
  beyond it the oldest unacknowledged item spills and is reported as `spilled_unacked_items` (added
  in `v0.9.0` — an additive field; the frozen v0 bytes are otherwise unchanged). Neither loss is
  ever silent.

## 7. Stop orders

- `stop` (per-mound or global) is processed ahead of all queued downlink and never requires a
  valid charter. Effect: cease actuation now, enter `safe_state`, keep sensing and syncing.
- Clearing a stop restores nothing. The mound returns to observe-only and waits for a fresh
  charter; the authority in force before the stop is not reinstated.
- Stop acknowledgement carries evidence (a post-stop sensor snapshot), like any other action.

## 8. Reduced profile (constrained controllers)

ESP32-class devices implement a strict subset:

- Kinds: `enroll`, `mound_sync`, `charter`, `action_record`, `stop`, `ack`. Absent, and why:
  `mission`/`mission_report` (a controller runs compiled routines selected by charter, not open
  work packets), `evidence_bundle` (fixed-shape readings ride on the action record), and `config`
  (a controller's hardware map is compiled in).
- Charters may only enable routines the firmware build enumerates; parameters clamp to
  firmware-compiled ranges regardless of what the charter says — the charter can narrow firmware
  limits, never widen them.
- Evidence is fixed-shape sensor readings and outcome codes; no images.
- Crypto: Ed25519 as above, well within ESP32 capability. A board that cannot sign does not join
  the mesh — there is no unsigned mode.

## 9. Missions (structured work)

The controller sends structured work packets, not natural-language prompts. The authoritative
execution representation stays executable with no language model in the loop.

```json
{
  "mission_id": "uuid",
  "mound_id": "mm-7f3a…",
  "charter_id": "uuid",
  "worker": "Watering Ant",
  "required_capabilities": ["sense.soil_moisture", "sense.temperature"],
  "allowed_routines": ["routine.water_cycle"],
  "steps": [
    { "step_id": "soil_before", "op": "sense", "capability": "sense.soil_moisture",
      "parameters": {}, "condition": null, "evidence_tag": "soil_before" },
    { "step_id": "water", "op": "routine", "capability": "", "routine_id": "routine.water_cycle",
      "parameters": { "on_s": 10 },
      "condition": { "source_step": "soil_before", "op": "lt", "value": 20 },
      "evidence_tag": "watering_action" },
    { "step_id": "soil_after", "op": "sense", "capability": "sense.soil_moisture",
      "parameters": {}, "condition": null, "evidence_tag": "soil_after" },
    { "step_id": "confirm", "op": "verify", "capability": "sense.soil_moisture",
      "confirms": "water", "parameters": {}, "condition": null, "evidence_tag": "" }
  ],
  "required_evidence": ["soil_before", "watering_action", "soil_after"],
  "safe_state": "all_actuators_off",
  "expires_at": "…",
  "context": "advisory only — no runtime path may branch on this"
}
```

- Step ops are a closed set: `sense`, `act`, `routine`, `verify`, `report`. A runtime that meets
  an op it does not know refuses the mission rather than improvising.
- Conditions are deliberately not an expression language: one source step, one operator
  (`lt`, `lte`, `gt`, `gte`, `eq`, `neq`), one number. A condition may only read a step that runs
  before it.
- A mission carries no authority of its own. Everything it references must already be granted by
  the charter it cites, and a mission that references anything outside it is refused **whole** —
  a half-executed mission leaves physical state nobody planned.
- **Ops and namespaces agree.** A `sense` or `verify` step's capability must be in the `sense.`
  namespace; an `act` step's must be in `act.`; a `routine` step names a `routine_id` instead. A
  step that reads an actuator is not a permission question — it is a mission that means something
  other than what it says, and it is refused at validation so the mistake is named rather than
  surfacing later as an unrelated ceiling refusal.
- **`safe_state` may only restate the charter's.** A mission naming a different de-energized state
  is refused: two documents disagreeing about where the hardware goes when the watchdog trips is a
  contradiction nobody can resolve at the moment it matters. Omitting it inherits the charter's.
- `worker` is advisory. An unrecognised name resolves to no worker ceiling rather than an invented
  one, so the charter's ceiling alone applies — which is why it is not validated on the wire.
- The mound replies with a `mission_report`: per-step state (`executed`, `skipped`, `refused`,
  `failed`, `stopped`), sensed values, action ids, evidence refs, and an overall state
  (`completed`, `failed`, `refused`, `stopped`, `quiesced`, `unverified`).
- **Execution stops acting but keeps looking.** After a step is refused, fails, or is stopped, no
  later step actuates; later `sense`, `verify` and `report` steps still run, because a reading of
  where the physical world was actually left is the most valuable thing a partial mission can
  return. Steps suppressed this way report `refused`.
- **The overall state names the first thing that went wrong**, not the worst label present — a
  hardware fault followed by suppressed steps is `failed`, not `refused`. A `stop` outranks
  everything wherever it appears.
- A step whose condition did not hold is `skipped` and its `evidence_tag` was never due. A mission
  that correctly declines to act is `completed`, not `unverified`.

### Verification (normative)

`confirms` names the earlier step whose action a `verify` step confirms. It is the only thing that
distinguishes `verify` from `sense`, and it is what makes the second half of the default workflow —
`SENSE → ACT → SENSE AGAIN → VERIFY` — able to affect an outcome at all.

- `confirms` is legal only on a `verify` step, must name a step that runs **before** it, and that
  step's op must be `act` or `routine`. Confirming an observation is not confirmation of anything.
- When the verify step produces a resolvable observation, its evidence ids are **added to the
  confirmed action's own `evidence_refs`**, so a controller re-running the evidence gate over the
  synced record reaches the same verdict the mound did.
- When the verify step produces no resolvable observation, the confirmed action degrades to
  `unverified` — "without it the outcome is `unverified` no matter what the driver returned".
- **Confirmation can only lower a verdict, never raise one.** A reading taken afterwards proves
  the state of the world afterwards; it does not prove the command caused it. An action already
  `unverified` stays `unverified`, and a `refused` or `stopped` action needs no proof at all —
  demanding it would invent a failure out of a correctly reported no.
- The confirmed **step** remains `executed`; the action's outcome and the mission's state are what
  degrade. The step ran, and ran correctly; what changed is what the mound may claim about its
  effect.

Evidence becomes an action's evidence in exactly two ways: an executor produced it during the
work, or a mission linked it with `confirms`. Both are somebody else's decision, made before the
outcome was known. A mound does not nominate its own corroboration.

## 10. Configuration

A signed `config` envelope carries a mound manifest: hardware bindings, declared capabilities and
routines, worker definitions, `device_limits` (the middle limit tier), reasoning mode, and the
declared safe state. Full shape and validation rules in [`CONFIGURATION.md`](CONFIGURATION.md).

Configuration is validated before activation and fails closed: an invalid manifest leaves the
previous one in force and the refusal is reported.

## 11. Versioning

`v` bumps on breaking change only. Mounds and controller each advertise supported versions at
enroll and sync; the lowest common wins; a mismatch refuses loudly. Additive fields are always
legal.

**v0 is fluid until the first firmware ships.** No physical device is deployed and no C mirror
exists yet, so v0 has been amended in place rather than superseded — most recently to add
`mission_id`, `routine_id`, and `requested_parameters` to action records, `routines` to charters,
and the `config` and `mission_report` kinds. Once a firmware build is in the field this stops:
from that point a change to these bytes is a version bump, and the golden fixtures are the record
of what each version was.

As of `v0.7.0` the golden fixtures pin `charter`, `action_record`, `evidence_bundle`, **`mission`,
and `mission_report`** — the last two added because a Pi-class mound and a full controller both
encode them even though a reduced controller (§8) never decodes a mission, so the M5 C mirror is
verified against a fixture rather than against an agreement nobody checked.
