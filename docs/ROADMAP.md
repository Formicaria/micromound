# Roadmap

The build order protects the physical authority boundary before adding anything on top of it.
Every milestone is ordered against a single question: **what has to be true before something can
move?**

Milestones land in order. A later milestone never ships while an earlier one's tests are red.

## Status

| Milestone | Status | What it is |
|---|---|---|
| **M0** — Protocol, identity, kernel | **Frozen at `v0.2.1`** | Wire contracts, Ed25519 signing, canonical bytes, charters, leases, evidence contracts, and the capability kernel with deterministic authorization |
| **M1** — Runtime interfaces and the Mound Major | **Done at `v0.3.0`** | Driver, worker, routine, evidence, persistence, and transport interfaces; the Mound Major workflow and mission state machine |
| **M2** — The six default ants | **Done at `v0.6.0`** | Scout, Forager, Guard, Witness, Cache, Runner as lightweight runtime services; simulated drivers; end-to-end simulator missions |
| **M3** — Evidence, offline state, and sync | **Done at `v0.9.1`** | Evidence correlation, durable offline state, reconnect and backlog synchronization. Shipped: `mission`/`mission_report` golden pins (`v0.7.0`); confirming-reading temporal correlation (`v0.8.0`); evidence spill/backpressure policy (`v0.9.0`); durable in-flight mission semantics — a restart never repeats, resumes, or fabricates the outcome of physical work it cannot prove finished (`v0.9.1`). The *semantics* are complete and proven on the in-memory/sim store; the *disk* backing for both durable state and the evidence store is deferred to M4, where the real host lands, and is a storage substrate change, not an M3 rule change. |
| **M4** — The Linux/Pi host and first real drivers | Planned (next) | The headless `Micromound.Host` daemon made runnable, SQLite-backed durable state, a strong declarative hardware manifest, generic driver *primitives* (digital I/O, analog, binary/proportional/position/velocity actuators), device/capability composition from the manifest, service lifecycle, watchdog, host-owned safe-state de-energizing on stop/quiesce/expiry/fault/shutdown, and — because the state store is finally durable — two ordering duties the sim leaves as no-ops: cold-start de-energizing to the mission checkpoint's `safe_state`, and persisting a terminal mission report *before* clearing its checkpoint so a crash between the two re-reports rather than losing the record (the audit-record analogue of the v0.9.1 no-replay rule) |
| **M5** — Constrained controller firmware | Planned | ESP32 reduced controller (`firmware/esp32`, currently a placeholder) implementing the same protocol and capability kernel in C, verified byte-for-byte against the golden fixtures, over a compact versioned Pi↔ESP32 packet protocol |
| **Acceptance** — Generic Physical Mound | Criteria, not a code milestone | The end-to-end proof on a minimal real bench that a fresh mound boots its default colony, is configured and chartered from upstream, moves generic hardware through the kernel, verifies with independent evidence, survives disconnect/reboot/lease-expiry safely, and synchronizes an auditable history back. See [The target](#the-target-a-generic-physical-mound). |
| **M6** — Optional reasoning | Planned (last) | The reasoning provider interface wired in — only after deterministic execution is mature. Never on the physical authority path. |

Upstream integration is not a milestone here. It is a separate deliverable in a separate
repository, and it can begin as soon as M0 is frozen — see [`UPSTREAM.md`](UPSTREAM.md).

Hazardous-class work has no milestone yet, deliberately. Until a per-action authorization pipeline
exists with tests, hazardous actions are refused unconditionally and cannot even be registered.

## Reading this roadmap

Six questions this document should answer at a glance:

1. **What is already complete?** M0 (protocol, identity, kernel — frozen at `v0.2.1`), M1 (runtime
   interfaces and the Mound Major — `v0.3.0`), M2 (the six default ants over simulated hardware,
   end to end — `v0.6.0`), and M3 (evidence, offline state, and sync semantics — closed at
   `v0.9.1`). All proven against `Micromound.Sim`, which runs the real kernel over fake hardware.
2. **What was M3?** The record survives and travels correctly: it is pinned on the wire (`v0.7.0`),
   verified only by evidence that follows the act (`v0.8.0`), bounded in storage by a loud spill
   policy (`v0.9.0`), and durable across a restart mid-mission — a reboot never repeats, resumes,
   or fabricates the outcome of physical work it cannot prove finished (`v0.9.1`). What M3 does
   **not** include is the disk substrate under those semantics: durable state and the evidence
   store are proven in memory and in the simulator, and their SQLite backing lands with M4's host.
3. **What must exist before real hardware can move?** M4 — a runnable `Micromound.Host`, durable
   state on disk, a declarative hardware manifest, and generic driver *primitives*. Nothing turns
   a physical output on until this lands, because the host is what de-energizes it on stop.
4. **What comes immediately after that?** M5 — the ESP32 as a subordinate deterministic controller
   speaking a compact Pi↔ESP32 protocol, running the same kernel in C, byte-verified against the
   golden fixtures. Not a second colony.
5. **When is Micromound physically usable?** At the **Generic Physical Mound** acceptance below —
   the first time the whole path runs on real hardware. That is the line between a software
   architecture and a functional physical edge colony.
6. **What deliberately remains outside Micromound?** The upstream UI, mission authoring, and colony
   management (Anthill owns these — Micromound only exposes the contract); hazardous-class
   per-action authorization (a separate, explicit future design); and any device-specific runtime,
   named appliance driver, or default ant. Reasoning (M6) is optional and never load-bearing.

## The target: a Generic Physical Mound

The whole build points at one acceptance target, expressed as criteria rather than a renumbered
historical milestone. It does not need an elaborate robot — a minimal bench suffices: a Raspberry
Pi, an ESP32, one stepper/servo axis, one position encoder, one home/limit switch, one
controllable output, one digital input.

Against that bench the acceptance sequence proves, in order: a fresh mound boots its **unchanged**
default mini-colony (Mound Major + Scout, Forager, Guard, Witness, Cache, Runner); the ESP32 is
discovered; hardware is enumerated or loaded from the manifest; capabilities register; the mound
enrolls upstream; signed configuration and a signed charter are accepted and persisted;
configuration binds generic hardware to the generic ants **without changing their code**; a mission
is coordinated by the Mound Major; the Forager requests actuation; the kernel validates authority
and limits; a generic driver sends a bounded request to the ESP32; the ESP32 acts deterministically;
the Witness confirms with **independent** evidence; the result reflects verified/unverified/failed
reality; the network drops and the mound continues only inside its existing lease, inventing no new
authority and queuing evidence; the Pi reboots and stop/lease/config/evidence restore correctly; the
lease expires while disconnected and the mound enters its declared safe state with outputs
de-energized; the network returns, expired authority does **not** resume, and the evidence backlog
synchronizes into a complete auditable history.

The load-bearing property throughout: **the same unchanged Micromound binary becomes a specialized
physical mound through configuration, never through a fork.** A device-specific class in the core
(`GreenhouseRuntime`, `RoverAnt`, a named appliance driver) is the signal an abstraction is wrong.

## What M3 covered

M3 was taken in slices, each a coherent release that preserved all prior behavior. **M3 closed at
`v0.9.1`.** Its closure condition was: the record a mound produces survives and travels correctly —
pinned on the wire, verified only by evidence that followed the act, bounded in storage without
silent loss, and durable across a restart mid-mission — all proven end to end against
`Micromound.Sim`, with no v0 canonical-byte change. The one thing deliberately **left to M4** is the
persistent-disk substrate beneath those semantics (SQLite-backed state and evidence); that is a
storage-engine change, not a rule change, so it does not hold M3 open.

- **`v0.7.0` — the record is pinned.** `mission` and `mission_report` joined the golden fixtures
  (bare bodies and the canonical-envelope chain) with round-trip agreement tests, closing the gap
  where the two bodies a Pi and a full controller both encode were checked by nothing.
- **`v0.8.0` — the record is verified by evidence that followed the act.** A confirming reading is
  accepted only if it was captured at or after the action began; a reading from before the act (a
  stale tag, clock skew) can no longer confirm an effect that had not happened. The Witness stays
  generic — it knows expected outcome, evidence requirement, observation, correlation, result, and
  nothing about what the hardware is.
- **`v0.9.0` — the store bounds itself and says what it cost.** An explicit evidence
  spill/backpressure policy: a hard ceiling above the soft capacity, acknowledged proof reclaimed
  first, then oldest unacknowledged proof spilled and counted on the wire as `spilled_unacked_items`.
  A long-disconnected mound bounds its storage without ever silently dropping proof.
- **`v0.9.1` — a restart never repeats physical work it cannot prove finished.** Durable in-flight
  mission state: the Mound Major persists a `cache:mission` checkpoint at mission start and clears
  it at finish, and around every actuating step it persists intent → executes → persists result.
  A crash in the ambiguous window leaves the step marked `actuation_in_flight`. On restart, after
  authority is re-evaluated, recovery is deterministic and fail-closed — a stop stays in force,
  lost authority fails the mission, a mid-actuation step fails as *ambiguous and is never replayed*,
  an interruption before actuation fails as interrupted, and a completed or absent mission recovers
  to nothing. Every outcome is `failed` or `stopped`; a restart can only end an interrupted mission,
  never silently continue one. No new wire state — canonical bytes unchanged. **This closed M3.**
- **Deferred to M4, not M3:** the *disk* backing for durable state, and the disk-backed evidence
  store. The M3 semantics above are complete and proven on the in-memory/sim store; giving them a
  persistent substrate is M4's real-host work, not an open M3 rule.

## What M0 actually covers

M0 was previously described as complete at `v0.1.0`. That was accurate for the protocol half and
not for the rest: the wire contracts, signing, canonical-byte fixtures, and Layer 1 limit clamping
existed, but the capability kernel those rules belong in did not — clamping lived on the simulator's
own actuation path, which meant the simulator and any future runtime could have diverged.

M0 now means: contracts **and** the kernel. It froze at **`v0.2.1`**.

**What "frozen" commits us to.** The v0 canonical bytes are now pinned by the golden fixtures and
will not change again inside v0 — a later change to what gets signed and hashed is a protocol
version bump under PROTOCOL.md §10, not an amendment. That is the property the M5 C mirror is
built against, and it is the property that lets an upstream integration start now rather than
after the runtime lands. Additive fields remain legal; re-encoding existing ones does not.

Done:

- [x] Envelopes, canonical bytes, digests, hash chaining
- [x] Ed25519 signing and verification; specific refusal reasons
- [x] Charters, leases, action classes; `hazardous` refused as a ceiling
- [x] Evidence contracts and the evidence gate
- [x] Golden-file wire fixtures
- [x] Structured mission and manifest contracts
- [x] Capability and routine registries with registration-time validation
- [x] Three-tier limit intersection (hardware ∩ device ∩ charter)
- [x] The capability kernel: stop, availability, authority, grant, worker ceiling, parameters,
      limits, duty cycle, rate, clamp, executor — with structured refusals
- [x] The simulator rebuilt onto the real kernel
- [x] Golden fixtures regenerated for the amended v0 contracts (`901f4dc`, shipped in `v0.2.0` —
      this box was left unticked for a release, which is its own small lesson about trusting a
      checklist over `git log`)
- [x] Mission validator tests
- [x] Manifest validator tests
- [x] A `CHANGELOG.md`, since design rule 9 requires a changelog entry per stateful feature

Deliberately **not** in M0, and recorded at the time so nobody would mistake an absence for an
oversight: the v0 validators accepted a `sense` step naming an `act.` capability, accepted
`mission.safe_state` and `mission.worker` unchecked, and did not validate
`WorkerDefinition.exposes`, `runtime_type`, or `required_evidence`. Each was a contract question
only the runtime consuming those fields could answer, so each was deferred to M1.

**All of them are closed as of `v0.3.0`**, except two that turned out to have no question in them:
`mission.worker` is a runtime concern — an unrecognised name resolves to no worker ceiling rather
than an invented one, which is the answer — and `required_evidence` holds free-form tags whose only
meaningful check is whether a step actually produced them, which is execution's job and now
`MoundMajor`'s. None was ever an authority hole; the kernel refused at execution on class, grant
and limits throughout.

## What M1 actually covers

M1 is the loop between things that already existed. The kernel decided; the contracts described;
nothing walked a mission from one end to the other. It does now.

Done:

- [x] Driver, worker, routine, evidence, persistence and transport interfaces (`v0.2.0`)
- [x] `EvidenceReading` — the documented numeric shape inside `payload_json`, without which a
      mission's conditions and step values were contracts with no number to compare
- [x] `MoundMajor`: charter acceptance with advisory widening notes, manifest application that
      fails closed, and the mission state machine — ordered steps, deterministic conditions,
      dispatch to the kernel, evidence resolution, structured `mission_report`
- [x] Halting behaviour: refuse whole, then stop acting and keep looking
- [x] Verdict attribution to the first failure rather than the worst label

**What M1 is not.** The six ants are interfaces here and services in M2; nothing implements
`IScoutAnt`, `ICacheAnt` or `IRunnerAnt` yet, so a mission runs against registered executors
rather than against workers with lifecycles. There is no persistence backend, no transport, and
no real driver. A mound cannot yet be left alone with a plant.

## Where M2 stands

M2 is being taken in two halves, split where the seam actually is.

**The ants a mission passes through — done at `v0.4.0`:**

- [x] Scout Ant, Forager Ant — each stamping its own declared ceiling onto every request
- [x] Guard Ant — the software watchdog SAFETY.md Layer 1 promised and nothing implemented
- [x] Coordinator dispatch through the ants, with no ant registered still a working mound

**Verification — done at `v0.5.0`:**

- [x] Witness Ant, `InMemoryEvidenceStore`, `EvidenceCorrelator`
- [x] `MissionStep.confirms` — the link that makes `verify` differ from `sense` at all
- [x] The evidence gate applied a second time, so the confirming reading can change an outcome

**The ants that act on the record — done at `v0.6.0`:**

- [x] Cache Ant and operational persistence — `IStateStore`, restart snapshots, and the three
      restore rules: a restart never clears a stop, never extends a lease, and restores
      observe-only when in doubt
- [x] Runner Ant over the durable uplink queue — the chain enforced at enqueue, retention governed
      by acknowledgement, stops processed ahead of everything else in the batch
- [x] Simulated drivers implementing `IDriver`, and `Micromound.Sim` rebuilt to compose
      driver → kernel → ants → Mound Major → Runner
- [x] End-to-end simulator missions, against an in-process controller that verifies every byte

## Known gaps, recorded

- ~~**Missions and mission reports are pinned by no golden fixture.**~~ **Closed at `v0.7.0`.**
  `mission` and `mission_report` now sit in both golden fixtures — the bare-body freeze and the
  canonical-envelope chain — alongside `charter`, `action_record` and `evidence_bundle`, and a
  round-trip test asserts each survives a decode-and-re-encode byte-for-byte. A constrained
  controller still never decodes a mission (§8 keeps it out of the reduced profile); the pin is
  for the Pi-class mound and the full controller, which both encode them, so the M5 C mirror has
  a fixture to match instead of an agreement nobody checked.
- ~~**Evidence storage is unbounded when nothing is acknowledged.**~~ **Spill policy closed at
  `v0.9.0`.** `InMemoryEvidenceStore` now has a hard ceiling above its soft capacity: acknowledged
  proof is reclaimed first, then unacknowledged proof is retained past the soft capacity but spills
  oldest-first past the hard ceiling, and every spill is counted and rides the wire as
  `spilled_unacked_items` (a sibling of `evicted_acked_items`) so the loss is never silent. A mound
  offline for a week now bounds its storage and reports exactly what the gap cost. What remains for
  M4 is the *disk* backing — the policy is proven on the in-memory store; SQLite-backed durability
  lands with the real host.
- **Downlink is signature-verified but not hash-chained.** The uplink stream is chained per
  PROTOCOL.md §6; downlink relies on signatures alone, and each side deduplicates by envelope id.
  Deliberate — a controller fans out to many mounds and a per-mound downlink chain buys little —
  but recorded here rather than assumed.
- **Driver de-energizing on stop and quiesce is the composition root's job**, and only the
  simulator's composition root does it yet: SimMound watches for the stopped/quiesced transition
  around every sync and mission and tells its drivers to enter safe state, wherever the stop came
  from. The M4 host must do the same around its own loop — recorded here so it is a requirement,
  not a rediscovery.

## Ordering rationale

The original priority list is preserved here, mapped onto the milestones above, because the
*order* is the part that matters:

| # | Priority | Milestone |
|---|---|---|
| 1 | Stabilize protocol, identity, canonical serialization, signatures, charters, leases, evidence contracts | M0 |
| 2 | Build the capability registry and deterministic authorization kernel | M0 |
| 3 | Build runtime interfaces for drivers, workers, routines, evidence, persistence, transport | M1 |
| 4 | Implement the Mound Major workflow/state-machine runtime | M1 |
| 5 | Implement the six default ants as lightweight runtime services | M2 |
| 6 | Build simulated hardware drivers and end-to-end simulator missions | M2 |
| 7 | Build evidence correlation and durable offline state | M3 |
| 8 | Build reconnect and backlog synchronization | M3 |
| 9 | Build the Linux/Pi service host | M4 |
| 10 | Add a small initial set of real hardware drivers | M4 |
| 11 | Implement the constrained controller using the same protocol concepts | M5 |
| 12 | Add the optional reasoning-provider interface, only after deterministic execution is mature | M6 |
| 13 | Keep local model support optional and outside all physical enforcement paths | M6 |

Two properties of this ordering are worth stating explicitly, because they are easy to erode:

**The kernel comes before the runtime.** Priorities 1–2 finish before priority 3 begins. If the
runtime were built first, authorization would accumulate inside it — a check here, a guard there —
and the boundary would become a convention instead of a place. Building the kernel first means
every later layer is written against something that already refuses.

**Reasoning comes last.** Not because it is unimportant, but because a reasoning provider added to
a mature deterministic system can only propose, whereas one added early tends to become load
bearing. By M6 there is nothing for a model to be load-bearing *for*: every physical path already
works without it.

## Simulator first, hardware late

Real drivers do not appear until M4, and firmware until M5. Everything before that is proven
against `Micromound.Sim`, which runs the real kernel over fake hardware — so a simulator test that
passes is a statement about the code a Pi will run, not about a parallel implementation that
happens to agree today.
