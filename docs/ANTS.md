# Ants

An **ant is a specialized logical worker.** It is not necessarily a separate process, and it is
never necessarily a language model. An ant may be deterministic code, an algorithm, a sensor
worker, a hardware controller, an integration worker, or — optionally, on capable hardware — a
reasoning-enabled worker.

The visual colony metaphor and the runtime implementation are intentionally separate concerns. A
controller's UI may draw six ants on an ESP32 that is running one firmware image with six
functions in it, and both descriptions are correct.

## Implementation status

The full default roster is implemented as of `v0.6.0`, and hardened since. Every ant runs as a
runtime service over the real kernel, proven end to end against `Micromound.Sim`.

| Ant | Status |
|---|---|
| Mound Major | **Implemented** (`v0.3.0`) — mission state machine, conditions, dispatch, reports; durable in-flight recovery (`v0.9.1`) |
| Scout Ant | **Implemented** (`v0.4.0`) — submits under its own ceiling; the reading is evidence |
| Forager Ant | **Implemented** (`v0.4.0`) — submits under its own ceiling; holds no driver |
| Guard Ant | **Implemented** (`v0.4.0`) — the software watchdog SAFETY.md Layer 1 promised |
| Witness Ant | **Implemented** (`v0.5.0`) — confirms an action against a later observation; only a reading captured at or after the act can confirm it (`v0.8.0`) |
| Cache Ant | **Implemented** (`v0.6.0`) — operational persistence; restart never clears a stop or extends a lease, and now recovers in-flight mission state without replaying unprovable physical work (`v0.9.1`) |
| Runner Ant | **Implemented** (`v0.6.0`) — transport, enrollment, durable uplink; the chain enforced at enqueue, retention governed by acknowledgement, bounded storage with a loud spill policy (`v0.9.0`) |

The roster is complete. What remains is not another ant but the substrate under two of them — the
disk backing for Cache's durable state and for the evidence store — which lands with the M4 host.

## The default roster

A standard Pi-class mound ships with one Mound Major and six ants.

```text
MICROMOUND
    Mound Major
        Scout Ant
        Forager Ant
        Guard Ant
        Witness Ant
        Cache Ant
        Runner Ant
```

These names are deliberately distinct from any upstream colony's own worker names. A controller's
Verifier judges whether a mission succeeded; a mound's Witness Ant judges whether a valve actually
opened. Sharing a name would make two very different questions look like one.

### Mound Major

The local colony coordinator. Replaces the earlier "Edge Queen" terminology.

Accepts and validates bounded mission authority; loads the current mission and workflow; inspects
available workers and capabilities; sequences local work; dispatches tasks to local ants;
evaluates deterministic conditions; tracks mission progress; manages cancellation and stop
transitions; requests optional reasoning only when configured and required; ensures all actuation
passes through the capability kernel; coordinates evidence collection; produces structured mission
outcomes.

It is primarily a workflow and state-machine coordinator — **not** an always-running agent.

### Scout Ant — observation and sensing

Reads sensors, captures camera observations, collects telemetry, reads GPIO inputs, obtains
position and environmental state, reports machine and device state, normalizes raw driver readings
into capability results, timestamps observations, and makes them available to workflows and to
evidence.

Examples: temperature, humidity, soil moisture, GPS, lidar, encoder state, door contacts, battery
voltage, camera capture, machine status.

A Scout Ant's reading *is* evidence, and is returned as an evidence item rather than a bare
number. That is why a sense executor that returns nothing sees its own result gated down to
`unverified`.

### Forager Ant — requested physical action

Accepts requested physical operations from the Mound Major, translates worker-level requests into
capability requests, submits them to the capability kernel, and returns the structured outcome:
accepted, clamped, refused, stopped, failed, or completed.

**It never directly manipulates physical hardware.** It holds no driver and no executor.

Examples: request a relay operation, request motor movement, request a servo position, request a
water valve cycle, invoke a precompiled routine.

### Guard Ant — runtime health and operational safety

Monitors runtime heartbeat, watchdog state, battery and power, thermal conditions, connectivity,
hardware faults, and interlock and limit status. Detects stalled or abnormal devices, reports
violations and degraded state, and initiates software-defined safe-state transitions where
permitted.

**Guard Ant treats independent safety trips as facts.** It does not override, reset, or manage
independent physical safety systems — see [`SAFETY.md`](SAFETY.md) Layer 0.

### Witness Ant — physical outcome confirmation

Gathers evidence independent of the actuation path, correlates it with action records, evaluates
freshness and validity, determines whether required evidence exists, marks unsupported claimed
successes as `unverified`, and produces structured evidence bundles.

Correlation is temporal, not merely by reference: a confirming reading counts only if it was
captured **at or after the action began**. A reading from before the act — one carrying the right
tag but taken earlier, or one reordered by clock skew — is the temporal mirror of "commands are
not evidence" and cannot confirm an effect that had not yet happened, so it is left out of the
proof and, if nothing else confirms, the action degrades to `unverified` (since `v0.8.0`).

Examples: relay command followed by contact-sensor state; valve command followed by flow
detection; motor command followed by encoder movement; movement command followed by localization
change; watering command followed by soil-moisture change; inspection request followed by a
captured image artifact.

### Cache Ant — short-term operational persistence

Persists active authority, active mission, workflow progress, and worker state. Retains recent
observations, current device state, local action history, and evidence awaiting synchronization.
Maintains the durable outbound queue and the last acknowledged sequence. Recovers safely after a
process or device restart.

**An operational state store, not a knowledge system.** Anything a mound could rebuild from its
charter and its hardware does not belong here.

### Runner Ant — secure external communication

Enrollment, device identity exchange, signed message transport, heartbeat and sync beats,
authority updates, mission receipt, acknowledgement handling, durable retry, reconnect, backlog
upload, evidence synchronization, stop-order receipt, and protocol version negotiation.

The only outward-facing worker. Everything else in the mound is local by construction.

## Specialized ants — an optional extension, never the standard colony

**The standard colony is always the same.** Every mound — greenhouse, rover, workshop, or bench —
ships the *identical* Mound Major and six default ants above, unchanged, and specializes to its
hardware through **configuration and capabilities**, not by swapping in device-specific ants. There
is no built-in "Soil Ant" or "Watering Ant" type in the runtime, and a device-specific class in the
core (`WateringAnt`, `GreenhouseRuntime`) is the signal an abstraction is wrong.

On top of that unchanged roster, a deployment *may* — optionally — declare application-specific
workers **in the manifest** when generic sensing and actuation genuinely need domain logic of their
own (a navigation planner, a vision-inspection classifier). These are data in the manifest, not code
in the runtime, and they are the exception, not the rule: most mounds need none, because the default
ants plus the right capabilities already cover generic physical execution.

The rosters below are **illustrations of that extension point**, not a catalogue of standard ant
types. The bottom group in each column is what a given deployment *chose* to declare; the top group
is always present and always identical.

```text
Greenhouse MicroMound          Rover MicroMound            Workshop MicroMound
    Mound Major                    Mound Major                 Mound Major        ─┐
    Scout Ant                      Scout Ant                   Scout Ant           │ the standard
    Forager Ant                    Forager Ant                 Forager Ant         │ colony, always
    Guard Ant                      Guard Ant                   Guard Ant           │ the same six +
    Witness Ant                    Witness Ant                 Witness Ant         │ Mound Major
    Cache Ant                      Cache Ant                   Cache Ant           │
    Runner Ant                     Runner Ant                  Runner Ant         ─┘
    ----------------               ----------------            ----------------
    (optional, manifest-declared specialized workers this deployment chose)
    Climate planner                Navigation planner          Machine-state monitor
    Vision inspector               Vision inspector            Vision inspector
                                   Battery monitor             Material-handling planner
```

A constrained ESP32 controller runs a **reduced profile** of the same design — the same kernel and
protocol in C, with only the workers a small controller needs (sensing, actuation via the kernel,
the watchdog, transport). It is not a different colony and it does not introduce new ant types; it
is the standard model with fewer moving parts.

## Declaring a specialized ant

A worker definition names what the ant is and what it may do. It is data in the manifest, not code
in the runtime — see [`CONFIGURATION.md`](CONFIGURATION.md).

| Field | Meaning |
|---|---|
| `name` | Display name, unique within the mound |
| `purpose` | One line, for humans and for a colony view |
| `runtime_type` | `deterministic`, `algorithmic`, `sensor`, `actuator`, or `reasoning` |
| `consumes` | Capabilities and routines this ant reads or requests |
| `exposes` | Capabilities this ant offers to other workers |
| `action_ceiling` | The ant's own highest action class |
| `required_evidence` | Evidence tags its work must produce |
| `offline_behaviour` | `continue`, `drain`, or `suspend` |
| `requires_reasoning` | True when it cannot function without a reasoning provider |

`action_ceiling` is intersected with the charter's ceiling on every request the ant makes. A Scout
Ant declared at `observe` cannot actuate even under a charter that would otherwise allow it — the
kernel refuses it as `action_class_exceeded`, naming the worker.

The ceiling is stamped onto the request **by the ant making it**, not by its caller. A ceiling
supplied from outside is discarded; otherwise a worker's declared limit would be advice rather
than a limit, and the first caller in a hurry would route around it.

`requires_reasoning: true` on a mound configured `reasoning.mode: none` fails manifest validation
rather than producing an ant that silently never works.
