# MicroMound Architecture

How the runtime is built. Concepts and vocabulary are in [`MICROMOUND.md`](MICROMOUND.md); the
safety model that overrides everything here is in [`SAFETY.md`](SAFETY.md).

## Layers

```text
Layer 1  Transport and identity     Micromound.Sync, Micromound.Crypto
Layer 2  Local colony runtime       Micromound.Runtime
Layer 3  Capability kernel          Micromound.Capabilities        ← the authority boundary
Layer 4  Hardware drivers           Micromound.Drivers
Layer 5  Physical world             —
Layer 6  Evidence                   Micromound.Evidence
```

`Micromound.Protocol` sits under all of them and depends on nothing.

### Layer 1 — Transport and identity

Device identity (Ed25519, generated on-device, private half never leaves), enrollment, signed
envelopes, endpoint configuration, sync beats, durable retry queue, reconnect and offline
synchronization. This is Runner Ant logic.

Device-initiated only: mounds dial the controller, and the controller never needs a path into the
device network.

### Layer 2 — Local colony runtime

The Mound Major, worker registration, the local mission state machine, structured workflow
execution, worker lifecycle, cancellation, task dispatch, and the optional reasoning seam.

This gives MicroMound its colony behaviour without a heavyweight agent framework. The Mound Major
is a workflow and state-machine coordinator, not an always-running agent.

**It decides nothing about authority.** Every actuation goes through the capability kernel, and the
coordinator holds no executor, no driver, and no route to one. If the two ever disagreed about
whether something may happen, the kernel would be the one that decided, because it is the only one
asked. That is why the interesting logic at this layer is about *order* and *evidence* rather than
permission.

Three ordering rules carry the weight, and each exists because of what a mound is attached to:

**Refused whole, never partially run.** A mission is validated against the active charter before
any step executes. A mission that fails halfway leaves physical state nobody planned, and there is
no compensating action for a valve that opened.

**After a halting step the mission stops acting but keeps looking.** When a step is refused, fails,
or is stopped, no later step may actuate — its premise is gone. Later `sense`, `verify` and
`report` steps still run, because the most valuable thing after a partial actuation is a reading of
where the physical world was actually left. Halting outright would discard exactly the observation
an operator most needs.

**The verdict names the first thing that went wrong**, not the worst label in the report. Steps
suppressed after a halt are marked refused because they were never attempted; counting those would
let a suppression label outrank the real cause, and a hardware fault would get reported as an
authority refusal — sending someone to read the charter instead of the relay. A stop is the one
exception and outranks everything wherever it appears, because a stop is an instruction that
arrived rather than an outcome that emerged.

A step whose condition did not hold is `skipped`, and its promised evidence was never due. A
mission that correctly declines to water wet soil is `completed`, not `unverified` — grading it
otherwise would teach an operator to ignore the one outcome that has to keep meaning something.

### Layer 3 — Capability kernel

The most important boundary in the system. See [`Micromound.Capabilities`](../src/Micromound.Capabilities/README.md)
for the full check order and rationale.

The kernel owns: the capability registry, capability availability, charter authorization, lease
checks, action-class checks, stop state, parameter validation, hardware-limit enforcement,
device-limit enforcement, charter-limit enforcement, duty-cycle limits, rate limits,
workspace/geofence limits, routine authorization, safe-state enforcement, and structured refusal
reasons.

No ant, no Mound Major, no local model, and no upstream mission bypasses it.

A worker requests:

```text
act.water_valve, duration 10 seconds
```

It does not request:

```text
GPIO 17 HIGH
```

The kernel resolves the semantic request to a registered implementation only after every check
passes.

**Limits intersect; they never replace.**

```text
effective  =  hardware/firmware limit
              ∩  configured device limit      (the mound manifest)
              ∩  delegated mission limit      (the active charter)
```

Ceilings take the minimum, floors take the maximum. No higher layer can widen a lower boundary. A
charter asking for a longer run than the relay tolerates does not get one — the attempt is
intersected away at execution and reported at validation, because a clamp nobody was told about
is a lie about what the mound did.

### Layer 4 — Hardware drivers

Deterministic adapters between generic capabilities and physical hardware. Buses: GPIO, I2C, SPI,
UART, PWM, CAN, USB, serial, BLE, camera. Devices: BME280, DHT22, ADS1115, VL53L0X, generic relay,
generic servo, motor controllers, cameras, GPS, lidar, encoders, battery monitors.

Drivers expose typed capabilities, validate device-level parameters, return structured results,
expose health state, declare their safe-state behaviour, remain independent of any reasoning
provider, and are unit-testable through hardware abstractions.

**Hardware is not the top-level abstraction:**

```text
BME280  →  BME280 driver  →  sense.temperature      →  Climate Ant
                             sense.humidity
                             sense.pressure

Relay   →  GPIO relay driver  →  act.water_valve    →  Watering Ant
```

New hardware is added without changing the colony runtime, and without the upstream controller
learning anything about boards, buses, or part numbers.

### Layer 6 — Evidence

Evidence is a first-class runtime object, not a log line.

Each action record carries: action id, mission id, capability or routine, requested parameters,
effective (clamped) parameters, start and end timestamps, outcome, refusal or failure reason,
whether evidence was required, and linked evidence ids.

Evidence itself may be sensor readings, telemetry windows, images, location data, encoder
movement, electrical measurements, state transitions, or routine outcome codes. Bundles are signed
and hash-chained so offline gaps, reordering, and modification are detectable.

Outcomes are closed: `succeeded`, `failed`, `clamped`, `refused`, `stopped`, `unverified`. If
required evidence is missing or invalid, success degrades to `unverified` — it is never assumed.

Pairings that make an outcome real:

| Action | Evidence |
|---|---|
| relay command | contact-sensor state |
| valve command | flow detection, soil moisture change |
| motor command | encoder delta |
| movement command | localization change |
| door-open command | contact sensor |
| inspection request | captured image artifact |

## Structured work

The controller sends structured mission packets, not large natural-language prompts. A packet
carries a mission id, mound id, charter id, requested worker, required capabilities, ordered
steps, deterministic conditions, allowed routines, evidence requirements, safe-state behaviour,
and expiry — plus one optional free-text `context` field that is advisory and that no runtime path
may branch on.

```text
Mission: inspect greenhouse and water only if necessary

Steps:
  1. read  sense.soil_moisture           → soil_before
  2. read  sense.temperature
  3. if soil_before < 20: run routine.water_cycle
  4. read  sense.soil_moisture           → soil_after
  5. verify
  6. report

Allowed:  sense.soil_moisture, sense.temperature, routine.water_cycle
Evidence: soil_before, watering_action, soil_after
```

Conditions are deliberately not an expression language: one source step, one operator, one number.
A mission that references anything outside its charter is refused whole rather than partially run,
because a half-executed mission leaves physical state nobody planned.

## Routines

A routine is a deterministic, named local behaviour: `routine.water_cycle`, `routine.open_door`,
`routine.scan_room`, `routine.return_home`, `routine.capture_environment`, `routine.dock`,
`routine.emergency_shutdown`.

Routines exist so a controller can delegate useful physical work without micromanaging every
low-level hardware transition across a link that may drop mid-sequence. They matter most on
constrained controllers, where they are the only work available.

Each routine declares a stable id, accepted parameters, compiled hard limits, required
capabilities, evidence expectations, cancellation behaviour, and safe-state behaviour, and is
independently testable.

A charter can enable a routine and narrow its parameters. It can never register one, and it can
never widen the compiled boundaries. A routine also cannot be a cheaper route to a higher action
class — the registry refuses a `benign` routine that drives a `controlled` capability.

## Optional reasoning

Reasoning is a pluggable interface, not a core dependency. Modes:

| Mode | Behaviour |
|---|---|
| `none` | **Default.** Deterministic workflows, state machines, rules, and routines only. |
| `remote` | Ask a configured external reasoning service to resolve an ambiguous step. Requires connectivity by definition. |
| `local` | A lightweight local model on capable hardware, for latency-sensitive or disconnected work. |

Plausible uses: visual inspection, scene classification, choosing among already-authorized
routines, navigating around an unexpected obstacle, summarizing observations, interpreting
ambiguous physical conditions, limited disconnected adaptation.

Even with reasoning enabled:

- model output is a **proposal**
- models have no direct hardware access
- the capability kernel remains authoritative
- models cannot alter hardware safety limits
- models cannot extend leases
- models cannot elevate action class
- models cannot disable stop conditions
- models cannot self-authorize restricted work

This is enforced structurally rather than by convention: `Micromound.Reasoning` does not reference
`Micromound.Capabilities`. A provider cannot call the kernel, hold an executor, or touch a driver,
because the project reference that would let it does not exist — and adding one would be a visible
change to a `.csproj` rather than a line buried inside a method.

A caller also discards any proposal that invents an option it never offered. A provider that
answers outside the offered set has produced a string, not a decision.

## Constrained controllers

ESP32-class hardware runs a reduced deterministic controller rather than the full runtime.

```text
MICROMOUND CONTROLLER
    Scout logic
    Forager logic
    Guard logic
    Runner logic
        ↓
    Capability kernel
        ↓
    Compiled routines
        ↓
    Drivers
        ↓
    Hardware
```

Requirements: no model, no dynamic code execution, no open-ended planning, a fixed firmware state
machine, declared capabilities, compiled routines, fixed parameter ranges, the signed reduced
protocol, a hardware watchdog, a declared safe state, and durable state only where necessary.

Logical ants may still be represented in metadata even when several roles compile into one image,
so a controller mound renders in a colony view like any other.

## Local state

Persist only what is required for safe local operation and recovery: device identity, trusted
controller identity, active charter, active mission, lease state, worker definitions,
hardware/driver manifest, routine registry, recent observations, current device state, action
history, pending evidence, last acknowledged sequence, outbound queue, health state.

Do not duplicate the controller's long-term memory, global mission history, cross-mound learning,
global skills, project context, or colony planning state.

## Repository layout

```text
src/Micromound.Protocol/       wire contracts: envelopes, charters, missions, manifests, evidence
src/Micromound.Crypto/         device identity, Ed25519 signing and verification
src/Micromound.Capabilities/   the capability kernel — registries, limits, action classes, refusals
src/Micromound.Runtime/        Mound Major, worker registry, the six default ants, mission state machine
src/Micromound.Drivers/        bus abstractions and hardware drivers
src/Micromound.Evidence/       capture, correlation, local store, pending-sync queue
src/Micromound.Sync/           Runner Ant transport: enrollment, sync beat, durable uplink
src/Micromound.Reasoning/      optional reasoning provider interface and the null default
src/Micromound.Host/           the headless Linux/Pi daemon
src/Micromound.Sim/            simulated mounds — the real kernel over fake hardware
firmware/esp32/                reduced deterministic controller (ESP-IDF)
tests/Micromound.Tests/        contract, protocol, authority, kernel, evidence, golden-byte tests
```

The dependency graph is acyclic and the direction is the point:

```text
Protocol ← Crypto
Protocol ← Capabilities ← Drivers
Protocol ← Capabilities ← Evidence
Protocol ← Reasoning                    (deliberately NOT ← Capabilities)
Protocol, Crypto ← Sync
Capabilities, Evidence, Reasoning, Sync ← Runtime
Runtime, Drivers ← Host
```

## Build conventions

`Directory.Build.props` pins net9.0, C# 13, nullable enabled, deterministic builds. Project names
use the `Micromound.*` prefix.
