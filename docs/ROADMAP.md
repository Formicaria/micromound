# Roadmap

The build order protects the physical authority boundary before adding anything on top of it.
Every milestone is ordered against a single question: **what has to be true before something can
move?**

Milestones land in order. A later milestone never ships while an earlier one's tests are red.

## Status

| Milestone | Status | What it is |
|---|---|---|
| **M0** — Protocol, identity, kernel | **Frozen at `v0.2.1`** | Wire contracts, Ed25519 signing, canonical bytes, charters, leases, evidence contracts, and the capability kernel with deterministic authorization |
| **M1** — Runtime interfaces and the Mound Major | **Next** | Driver, worker, routine, evidence, persistence, and transport interfaces; the Mound Major workflow and mission state machine |
| **M2** — The six default ants | Planned | Scout, Forager, Guard, Witness, Cache, Runner as lightweight runtime services; simulated drivers; end-to-end simulator missions |
| **M3** — Evidence, offline state, and sync | Planned | Evidence correlation, durable offline state, reconnect and backlog synchronization |
| **M4** — The Linux/Pi host and first real drivers | Planned | The headless daemon, configuration loading, service lifecycle, watchdog, and a small initial set of real hardware drivers |
| **M5** — Constrained controller firmware | Planned | ESP32 reduced controller implementing the same protocol and capability concepts, verified byte-for-byte against the golden fixtures |
| **M6** — Optional reasoning | Planned | The reasoning provider interface wired in — only after deterministic execution is mature |

Upstream integration is not a milestone here. It is a separate deliverable in a separate
repository, and it can begin as soon as M0 is frozen — see [`UPSTREAM.md`](UPSTREAM.md).

Hazardous-class work has no milestone yet, deliberately. Until a per-action authorization pipeline
exists with tests, hazardous actions are refused unconditionally and cannot even be registered.

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

Deliberately **not** in M0, and recorded so nobody mistakes an absence for an oversight — the v0
validators accept a `sense` step naming an `act.` capability, accept `mission.safe_state` and
`mission.worker` without checking them, and do not validate `WorkerDefinition.exposes`,
`runtime_type`, or `required_evidence`. Each is a contract question that the runtime consuming
these fields has to answer, so each belongs to M1. None is an authority hole: the kernel still
refuses at execution on class, grant, and limits regardless of what a packet claims.

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
