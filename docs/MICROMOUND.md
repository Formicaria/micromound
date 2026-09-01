# OPERATION MICROMOUND — Canonical Design Doc

Status: active. Where this file and the code disagree, one of them is a bug. Where this file and
[`SAFETY.md`](SAFETY.md) disagree, SAFETY.md wins.

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — runtime layers, capability kernel, drivers, routines, reasoning
- [`ANTS.md`](ANTS.md) — the six default workers and how specialized ones are added
- [`CAPABILITIES.md`](CAPABILITIES.md) — capability naming, registry, routines
- [`CONFIGURATION.md`](CONFIGURATION.md) — the declarative mound manifest
- [`PROTOCOL.md`](PROTOCOL.md) — wire contract
- [`SAFETY.md`](SAFETY.md) — safety model; canonical
- [`UPSTREAM.md`](UPSTREAM.md) — the upstream controller contract, and ANTHILL as its reference implementation
- [`ROADMAP.md`](ROADMAP.md) — milestones and build order

## Mission

MicroMound is a lightweight, headless edge-colony runtime for physical computing. It runs on
Raspberry Pis, Linux SBCs, ESP32-class controllers, robots, sensors, cameras, relays, motors,
fabrication equipment, and building systems.

Its purpose is to provide a small, reliable colony runtime close to physical hardware: receive
bounded work, observe the environment, execute deterministic physical actions, enforce hard
operating limits, verify outcomes with independent evidence, retain enough local state to survive
temporary network loss, and synchronize its records with an authorized upstream controller.

It is not a desktop application, a UI product, or a general-purpose agent harness.

**A standard MicroMound functions without a language model.** Optional lightweight reasoning may
be enabled on capable hardware when a use case genuinely requires ambiguity handling, visual
interpretation, local adaptation, or disconnected reasoning. No reasoning system may bypass
deterministic authorization or hardware safety boundaries.

## Where the upstream controller fits

MicroMound receives its authority from an **upstream controller**: the party that holds the
signing key, issues charters, receives evidence, and holds the stop controls. The protocol
deliberately does not name one. Any implementation of the contract in [`UPSTREAM.md`](UPSTREAM.md)
can fill the role.

[ANTHILL](https://github.com/Formicaria/Anthill) is the reference implementation, and a separate
repository. MicroMound support is an **optional integration** on the ANTHILL side, and all
user-facing configuration and colony visualization live there — MicroMound ships no UI of its own
and is not intended to. A mound does not know or care whether the controller that chartered it is
ANTHILL, a small self-hosted issuer, or a test harness.

That separation is a design property, not an accident of ordering. A runtime that assumed one
controller would grow assumptions about that controller's data model, and the boundary that keeps
authority explicit would quietly become an integration seam instead.

## Core design principles

1. **Headless by design.** MicroMound exposes runtime, protocol, configuration, telemetry, and
   management interfaces. It requires no graphical UI of its own.
2. **No language model required.** The default runtime uses structured workflows, rules, state
   machines, routines, and deterministic capability execution.
3. **Reasoning is optional and subordinate.** A local or remote reasoning provider may *propose*
   decisions. It cannot control hardware or expand authority.
4. **Physical control is capability-based.** Workers request semantic capabilities —
   `sense.temperature`, `act.water_valve` — never GPIO pins, bus addresses, or device commands.
5. **Deterministic enforcement always wins.** Every physical action passes through a capability
   kernel that validates authorization, limits, stop state, parameters, and hardware constraints.
6. **Commands are not evidence.** Issuing a command does not prove a physical result occurred.
   Required outcomes must be supported by independent sensor data, telemetry, images, or state.
7. **Disconnection never creates authority.** Offline operation continues only within
   already-issued bounded authority, and only until its lease expires.
8. **Safety systems remain outside intelligent control.** Emergency stops, hardware watchdogs,
   mechanical limits, thermal protection, interlocks, and breakers cannot be disabled, reset, or
   reconfigured by a model or by any normal mission path.
9. **Ant does not mean language model.** An ant is a specialized logical worker — deterministic
   code, an algorithm, a sensor worker, an actuator worker, or optionally a reasoning-enabled one.
10. **One conceptual model across hardware tiers.** Pi-class and ESP32-class deployments share the
    same protocol, capabilities, authority model, evidence model, and vocabulary, while using
    runtimes appropriate to their resources.

## Terminology

| Term | Meaning |
|---|---|
| **Upstream controller** | The authority that signs charters and receives evidence. ANTHILL's Primary Colony in the reference integration. |
| **MicroMound** | A subordinate physical colony running on an edge device or controller. |
| **Mound Major** | The local coordinator inside a Pi-class mound. Replaces the earlier "Edge Queen" name. Subordinate to the controller; acts only inside explicitly delegated authority. |
| **Ant** | A specialized worker. Not necessarily a separate process, and never necessarily a model. |
| **Capability** | A machine-readable primitive describing what physical functionality exists: `sense.soil_moisture`, `act.relay`, `routine.water_cycle`. |
| **Routine** | A pre-defined deterministic local behaviour, especially important on constrained hardware. |
| **Charter** | A signed delegation defining what a mound may do, with which capabilities, under which limits, to what action ceiling, with what evidence requirements, safe state, and lease. |
| **Lease** | The time-bounded authorization attached to a charter. Losing connectivity never extends it. |
| **Capability kernel** | The single physical authority boundary every actuation passes through. |

## The default colony

A standard Pi-class MicroMound is one Mound Major coordinating six default logical ants:

```text
MICROMOUND
    Mound Major
        Scout Ant       observation and sensing
        Forager Ant     requested physical action
        Guard Ant       runtime health and operational safety
        Witness Ant     physical outcome confirmation
        Cache Ant       short-term operational persistence
        Runner Ant      secure external communication
```

These names are deliberately distinct from any upstream colony's own roles, so that a controller's
"Verifier" (which judges whether a mission succeeded) and a mound's "Witness Ant" (which judges
whether a valve actually opened) are never mistaken for one another.

The standard colony is always exactly this Mound Major and six default ants, on every mound; a
mound specializes to its hardware through capabilities, not by swapping in device-specific ants.
*Optionally*, a deployment may declare application-specific workers — a vision inspector, a
navigation planner — in the manifest, layered on top of the unchanged roster. These are the
exception, not the rule (most mounds need none), and they are data in the manifest, never built-in
types in the runtime. See [`ANTS.md`](ANTS.md).

On constrained hardware several ants compile into one firmware image and remain ants only in the
metadata a UI renders. The colony metaphor and the runtime implementation are intentionally
separate concerns.

## The default workflow

```text
SENSE  →  EVALUATE  →  ACT  →  SENSE AGAIN  →  VERIFY  →  REPORT
```

Water when soil moisture is below 20%:

1. Scout Ant reads `sense.soil_moisture` → 17%.
2. Mound Major evaluates the deterministic threshold.
3. Forager Ant requests `act.water_valve` for 10 seconds.
4. The capability kernel validates charter, lease, action class, limits, and stop state.
5. The driver operates the relay.
6. Witness Ant obtains flow and soil evidence.
7. Mound Major produces the structured outcome.
8. Runner Ant queues or transmits the result and its evidence.

No language model is involved at any step.

The second sense is not redundancy. It is the entire reason the mound can claim anything happened:
the first reading justifies the action, the second is independent evidence of its effect, and
without it the outcome is `unverified` no matter what the driver returned.

## Authority model

Authority flows one way: controller → mound, always explicit, always bounded.

- **Charter.** The delegation document. Names the mound, the mission, granted capabilities,
  enabled routines, operating limits, action-class ceiling, evidence requirements, safe state, and
  a hard expiry. A mound refuses work absent a valid charter covering it.
- **Lease.** Every charter carries a lease TTL, renewed on each acknowledged sync beat. A
  disconnected mound may continue only work already authorized, only until the lease expires, and
  then must quiesce to its declared safe state. Reconnection resumes nothing automatically.
- **Action classes.** `observe` (sense and report) < `benign` (reversible, no physical risk) <
  `controlled` (reversible-with-effort or costly; needs approval) < `hazardous` (physical risk;
  needs explicit per-action authorization, never standing). A charter sets a ceiling; nothing the
  mound does, says, or infers can raise it.
- **No self-elevation.** Loss of communication, sensor anomalies, mission urgency, worker output,
  or model output never expand authority. Every ambiguous case resolves downward.

## Verification and evidence

- Every action record pairs the command issued with what actually ran and with independent
  evidence of the outcome. An action without evidence is `unverified` — never silently assumed
  done.
- Evidence is hash-chained per mound, so gaps and reordering after offline periods are detectable.
- Outcomes are a closed set: `succeeded`, `failed`, `clamped`, `refused`, `stopped`, `unverified`.

## Non-goals

MicroMound is explicitly **not**:

- a desktop UI or browser-based management application
- a general coding agent or web research agent
- a large long-term memory or knowledge platform
- a mandatory language-model runtime
- a system where natural-language model output directly controls GPIO
- a platform where edge devices can expand their own authority
- a replacement for independent physical safety hardware

Long-term user memory, global mission history, cross-mound learning, and colony-wide planning stay
with the upstream controller. A mound keeps only what it needs to operate safely and recover.

## Design rules that hold for every phase

1. **Observe before act.** Read-only lands before action-gated; actions arrive behind an approval
   pipeline.
2. **Disconnection never widens authority.** Leases only run down; nothing on-device extends one.
3. **Commands are not evidence.** Verification requires sensing independent of the actuation path.
4. **Safety systems are not addressable.** No envelope, charter, routine, or driver path may
   configure, suppress, or reset an independent safety device.
5. **One charter authority.** Only the upstream controller signs charters; mounds never re-delegate
   to other mounds.
6. **Per-device keys, minimal secrets.** A compromised mound yields its own identity and nothing
   else.
7. **Hardware is not the top-level abstraction.** Hardware → driver → capability → ant. Adding a
   sensor must never require changing the colony runtime.
8. **Deterministic code is plain service code.** Any model only plans, summarizes, and explains
   within charter; it holds no path to actuation that the kernel does not mediate.
9. **Every stateful feature ships with** model, persistence, API (if surfaced), tests, version
   note, and changelog entry.

## Success condition

One small runtime deployed across many kinds of physical system: understandable, testable, safe
without a language model, scaling through standardized capabilities and drivers, and supporting
specialized physical colonies without redesigning the core for every new sensor, robot,
controller, or machine.
