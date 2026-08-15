# MICROMOUND Protocol — v0 draft

Wire contract between the Primary Colony (ANTHILL) and Micromound devices. Implemented in
`src/Micromound.Protocol` (C# records, System.Text.Json, snake_case like ANTHILL's existing
JSON surface). This document is normative; code and tests must match it.

## 1. Transport

- Device-initiated HTTPS only. Mounds dial ANTHILL at `/micromound/v0/*`; ANTHILL never needs to
  reach into the device network. (Any optional future outbound path obeys the homelab D1 target
  allowlist and is off by default.)
- The **sync beat**: each mound POSTs a `MoundSync` envelope on an interval set by its charter
  (default 15 s connected Edge Queen, 60 s controller; exponential backoff with jitter on
  failure, mirroring `HomelabScheduler` discipline). The response carries any pending downlink
  envelopes (charter updates, mission assignments, stop orders).
- Offline is a normal state, not an error. Uplink envelopes queue durably on-device and drain
  oldest-first on reconnect.

## 2. Envelope

Every message either direction is one signed envelope:

```json
{
  "v": 0,
  "id": "uuid",
  "mound_id": "mm-7f3a…",
  "seq": 4182,
  "sent_at": "2026-08-14T21:04:11Z",
  "kind": "mound_sync | charter | mission | action_record | evidence_bundle | stop | ack | enroll",
  "body": { },
  "prev_digest": "sha256:…",
  "sig": "ed25519:…"
}
```

- `seq` is per-mound, monotonic, gap-checkable. `prev_digest` hash-chains a mound's uplink
  stream so offline gaps and tampering are detectable (§6).
- `sig`: `ed25519:<lowercase hex>`. Mounds sign uplink with their device key; ANTHILL signs
  downlink with the colony key. Unsigned or badly signed envelopes are dropped and audited, never
  processed. There is no unsigned mode and no trust-on-first-use: a key the verifier's directory
  does not hold is a refusal, because enrollment (§3) is the only way a key becomes known.
- The signature covers the envelope's **canonical bytes**: every field except `sig` itself,
  serialized with the shared options. `prev_digest` hashes those same bytes, so signing never
  perturbs the chain and the chain never covers the signature — both are checked, separately.
  A device can therefore sign and hash one buffer it has already built, which is what makes the
  reduced ESP32 profile (§8) practical.
- Refusals are specific, not boolean: `missing`, `malformed_format`, `unsupported_algorithm`,
  `unknown_key`, `bad_signature`. "Dropped and audited" is only auditable if the reason survives.

### Encoding rules (normative — these bytes are what gets signed)

Two implementations that agree on the data and disagree on the encoding produce different
digests, and the disagreement surfaces as an unverifiable device in the field. So:

- **Timestamps** are exactly `yyyy-MM-ddTHH:mm:ssZ` — UTC, second precision, no fractional
  digits, no numeric offset. Twenty fixed bytes, formattable with one `snprintf`. Emitters must
  produce this form; readers should accept an offset form too, because a mound built against an
  older library is better read than bricked.
- **Escaping is minimal**: `+` is a literal `+`, a quote inside a string is `\"`. Not `+`
  and not `"` — no hand-written C encoder emits those, and the C# side must not either.
- **Every field is always present**, including nulls for unset optional limits. A fixed shape
  means the firmware encoder never branches on whether an optional field is set.
- Field order is declaration order, and it is part of the contract.

`tests/Micromound.Tests/Golden/` freezes the resulting bytes; the M3 C mirror is verified against
those files.
- Unknown `kind` or unknown fields: ignore fields, refuse unknown kinds with an `ack` carrying
  `status: "refused_unknown_kind"`. Refusal is loud, never silent (ContractGate discipline).

## 3. Enrollment

1. Operator creates the mound in ANTHILL (Integrations tab → Micromound → add mound), which
   mints a one-time enrollment token stored write-only in the credential store.
2. Device boots with the token, generates its Ed25519 keypair on-device (private key never
   leaves), and POSTs `enroll` with its public key, hardware profile, and controller tier.
3. ANTHILL binds the public key to the mound record, burns the token, and returns the colony
   public key. From here on, only signed traffic.
4. Re-enrollment (key rotation, reflash) requires a new operator-minted token. There is no
   self-service re-key.

## 4. Charter

```json
{
  "charter_id": "uuid",
  "mound_id": "mm-7f3a…",
  "mission_ref": "anthill mission id",
  "issued_at": "…", "expires_at": "…",
  "lease_ttl_s": 900,
  "action_ceiling": "observe | benign | controlled",
  "capabilities": ["sense.temp", "sense.camera", "act.relay_1", "routine.water_cycle"],
  "limits": { "act.relay_1": { "max_on_s": 30, "min_off_s": 300 } },
  "evidence": { "required_for": ["act.*", "routine.*"], "min_interval_s": 60 },
  "safe_state": "all_actuators_off",
  "sync_interval_s": 15
}
```

- `hazardous` never appears as a charter ceiling: hazardous work is authorized per-action (M5)
  and expires on use.
- Charters are complete replacements, never diffs. A mound holds at most one active charter per
  mission; a new charter supersedes; absence of charter = `observe` only, then quiesce.
- The mound validates every charter: signature, its own `mound_id`, expiry sanity, and that
  every capability is one it physically has. Validation failure → refuse + report, do nothing.

## 5. Lease lifecycle

- Each accepted `mound_sync` response implicitly renews the lease to `now + lease_ttl_s`.
- Disconnected: mound continues only in-progress authorized work. At lease expiry it enters
  `safe_state`, keeps sensing (if `observe` is charter-covered), keeps recording evidence, and
  waits.
- Reconnect after expiry: the mound uploads its backlog and reports `quiesced`. ANTHILL must
  issue a fresh charter (operator- or policy-approved) to resume — resumption is never implicit.

## 6. Action records and evidence

- Every actuation produces an `action_record`: capability used, parameters, charter_id,
  start/end, outcome code, references to evidence items, and a `detail` string.
- Outcomes are a closed set: `succeeded`, `failed`, `clamped`, `refused`, `stopped`,
  `unverified`. `clamped` means the work happened but a limit narrowed it — the record carries
  the parameters that actually ran, not the ones requested.
- `detail` carries the reason for any non-plain-success outcome: the limit that clamped it, the
  rule that refused it, the evidence that was missing. SAFETY.md prohibits silent failure, so a
  refusal without a reason is itself a contract violation. Refused actions are queued for the
  colony exactly like successful ones.
- Evidence gating is mechanical. An outcome that asserts physical work happened (`succeeded`,
  `clamped`) survives only if every referenced evidence item resolves, parses, and — where
  `evidence.required_for` covers the capability — was captured within `min_interval_s` of the
  action start. Anything else becomes `unverified`. A `refused` or `stopped` record needs no
  proof: it is a definite outcome, not a claim about the physical world.
- `evidence_bundle`: batched sensor windows, images (content-addressed, fetched lazily),
  telemetry summaries. Bundles are hash-chained via envelope `prev_digest`.
- An action whose referenced evidence is missing, stale, or contradictory is `unverified`.
  ANTHILL's Verifier treats `unverified` as failed-until-proven for mission gating.
- Retention on-device: ring buffer sized by hardware profile; evidence pending sync is never
  evicted before acknowledged unless storage exhaustion forces oldest-acked-first eviction,
  which is itself reported.

## 7. Stop orders

- `stop` (per-mound or global) is processed ahead of all queued downlink and never requires a
  valid charter. Effect: cease actuation now, enter `safe_state`, keep sensing and syncing.
- `.anthill/MICROMOUND_STOP` on the colony host forces `stop` into every sync response while
  present.
- Stop acknowledgement carries evidence (post-stop sensor snapshot), same as any action.

## 8. Reduced profile (Deterministic Controller)

ESP32-class devices implement a strict subset:

- Kinds: `enroll`, `mound_sync`, `charter`, `action_record`, `stop`, `ack` (no free-form
  mission planning; `mission` bodies only reference pre-flashed routine IDs).
- Charters may only enable routines the firmware build enumerates; parameters clamp to
  firmware-compiled ranges regardless of what the charter says (defense in depth — the charter
  can narrow firmware limits, never widen).
- Evidence is fixed-shape sensor readings and outcome codes; no images.
- Crypto: Ed25519 as above (well within ESP32 capability); if a specific board cannot, it does
  not join the mesh — there is no unsigned mode.

## 9. ANTHILL-side endpoints (M1 surface)

| Endpoint | Method | Permission | Purpose |
|---|---|---|---|
| `/micromound/mounds` | GET | `read_micromound` | Fleet registry + status |
| `/micromound/mounds` | POST | `manage_micromound` | Create mound, mint enrollment token |
| `/micromound/v0/enroll` | POST | token | Device enrollment (§3) |
| `/micromound/v0/sync` | POST | device sig | Sync beat (§1) |
| `/micromound/missions` | GET | `read_micromound` | Missions/charters per mound |
| `/micromound/charters` | POST | `approve_micromound_actions` | Issue/renew charter |
| `/micromound/evidence` | GET | `read_micromound` | Evidence browse (secret-free) |
| `/micromound/stop` | POST | `approve_micromound_actions` | Per-mound or global stop |
| `/micromound/stop/resume` | POST | `approve_micromound_actions` | Clear stop (never auto) |

No endpoint returns a secret or a private key. Widget payloads (`mound_fleet`,
`mission_status`, `evidence_feed`) are published through the existing integration_state
mechanism so the Integrations tab renders Micromound like any other integration.

## 10. Versioning

`v` bumps on breaking change only; mounds and colony each advertise supported versions at
enroll/sync, lowest common wins, mismatch refuses loudly. Additive fields are always legal.
