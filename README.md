# OPERATION MICROMOUND

**A lightweight, headless edge-colony runtime for physical computing.**

MicroMound runs on Raspberry Pis, Linux SBCs, ESP32-class controllers, robots, sensors, cameras,
relays, motors, fabrication equipment, and building systems. It receives bounded work from an
authorized upstream controller, observes the environment, executes deterministic physical actions,
enforces hard operating limits, verifies outcomes with independent evidence, survives temporary
network loss without gaining authority, and synchronizes its records when it reconnects.

It is not a desktop application, a UI product, or a general-purpose agent harness.

**A standard MicroMound needs no language model.** Work executes as structured workflows,
deterministic rules, and pre-defined routines. Optional lightweight reasoning can be enabled on
capable hardware for genuinely ambiguous tasks — and even then, model output is a proposal that
still has to survive the capability kernel.

## The shape of it

```text
upstream controller
        │  signed charter / mission / configuration
        ▼
MICROMOUND
    Mound Major                 local coordinator
        Scout Ant               observation and sensing
        Forager Ant             requested physical action
        Guard Ant               runtime health and operational safety
        Witness Ant             physical outcome confirmation
        Cache Ant               short-term operational persistence
        Runner Ant              secure external communication
        ▼
    Capability Kernel           the single physical authority boundary
        ▼
    Drivers  →  Hardware  →  Independent evidence
        │  signed sync / evidence / results
        ▼
upstream controller
```

An ant is a specialized logical worker — deterministic code, an algorithm, a sensor worker. It is
not a language model instance, and on a constrained controller several ants compile into one
firmware image.

## Four ideas the rest follows from

**Capability-based control.** Workers request `act.water_valve` for ten seconds. They never
request `GPIO17 = HIGH` — there is no field through which they could, and no worker holds a driver
handle to send it through. Hardware → driver → capability → ant, so new devices never change the
runtime.

**Deterministic enforcement always wins.** Every actuation passes through the capability kernel,
which intersects three limit tiers — hardware ∩ device configuration ∩ charter — and refuses with
a specific reason rather than a bare "no". Nothing above can widen a bound below it.

**Commands are not evidence.** A command being issued does not prove a physical result occurred. A
valve command needs flow detection; a motor command needs an encoder delta. Unsupported success
degrades to `unverified`; it is never assumed.

**Disconnection never creates authority.** Offline operation continues only inside already-issued
bounded authority, only until the lease expires, and then the mound quiesces to its declared safe
state. Reconnection resumes nothing.

## Status

**Current version:** v0.9.15

**M0 frozen at `v0.2.1`; M1 done at `v0.3.0`; M2 done at `v0.6.0`; M3 done at `v0.9.1`; M4 in
progress (`v0.9.2`–`v0.9.15`).** Protocol contracts, Ed25519 signing, frozen wire bytes, the
capability kernel with deterministic authorization, the Mound Major that walks missions — and now
all six default ants as runtime services, a durable uplink queue whose chain is enforced at enqueue,
restart recovery that never clears a stop, never extends a lease, and never silently resumes physical
work it cannot prove finished, a bounded evidence store that says what a gap cost, a **durable
file-backed state store** (`v0.9.2`), a **driver-resolution seam** turning a manifest's hardware into
generic driver primitives (`v0.9.3`), a **`MoundHost`** that composes and runs a mound from a manifest
(`v0.9.4`), a **runnable daemon** with a safe service lifecycle (`v0.9.5`), a **real HTTP sync
transport** over HTTPS (`v0.9.6`), **device enrollment** (PROTOCOL.md §3): the mound presents a
one-time token, receives and persists the controller's key, and can then verify downlink (`v0.9.7`),
the **first real driver port**: a Linux GPIO output over `/sys/class/gpio` (`SysfsDigitalOutput`) that
the generic digital actuator drives instead of an in-memory line, opened fail-closed from the
manifest's `pin` (`v0.9.8`), a **digital actuator that holds its line for a real duration**: an
actuation drives the line active and holds it for the effective `on_s`, released on the service loop's
cadence and by the safe state on any stop, quiesce, shutdown, or trip (`v0.9.9`), the **independent
watchdog thread**: a hardware-independent timer on its own thread that de-energizes and stops the mound
if the service loop hangs (`v0.9.10`), and — new in `v0.9.11` — **enrollment aligned with the reference
controller (ANTHILL)**: the device now declares a tier the controller accepts (`edge_queen`; the old
hardcoded `mound_major` was refused), sends its `mound_id` as a cross-check plus `protocol_version` and
`capabilities`, surfaces the controller's refusal reason, and honours the controller's `sync_interval_s`
— throttling the sync beat only, never the safety rhythm (`v0.9.11`), and — new in `v0.9.12` — a
**durable evidence store**: the proof a mound captures now lives in a directory of files under
`<state>/evidence` with the v0.9.0 retention policy unchanged above it, so it survives a reboot and is
uplinked afterwards instead of evaporating with the heap (`v0.9.12`), and — new in `v0.9.13` — the
Guard's heartbeat evidence **rate-limited to what is informative** (the first reading, every
fresh↔stale transition, and a per-minute liveness record) so a durable mound writes ~12× less, with the
refusal logic untouched, and — new in `v0.9.14` — **the analog port is real**: the generic analog sensor
samples one channel of an ADS1115 over I2C (`LinuxI2cBus` + `Ads1115AnalogInput`, single-shot, in volts,
with optional `scale`/`offset` calibration), a missing chip refuses bring-up and a failed read is a fault
with no reading, and the daemon's new **`--hardware`** flag finally composes the real GPIO and ADC ports
instead of in-memory ones, and — new in `v0.9.15` — **the device describes its own hardware vocabulary**:
a machine-readable driver-settings schema (`DriverSchemaCatalog`, sent at enrollment as `driver_schemas`,
printed by `micromound --describe-drivers`) so the controller can offer a plain-language hardware form
instead of a raw settings console, pinned to the drivers by test. What's still ahead: a libgpiod
backing, and the GPIO writes and I2C transfers themselves verified on a physical board. End-to-end simulator missions run against an in-process
controller that verifies every byte. The v0 canonical bytes will not change again inside v0. The host
now has both a real digital line and a real analog channel available, but has not yet been run on a
device against real hardware — that boundary finishes M4; real firmware is M5. See
[`docs/ROADMAP.md`](docs/ROADMAP.md) and [`CHANGELOG.md`](CHANGELOG.md).

Releases continue as patch versions (`v0.9.2`, `v0.9.3`, …), including the internal M4 substrate
slices; `v0.10.0` is reserved for the M4 boundary where the host actually runs on a device over real
disk and drivers — not the automatic successor to `v0.9.x`.

```bash
bash scripts/validate.sh                    # guards + restore + build + test
bash scripts/validate.sh --full             # and the simulator smoke run
dotnet test Micromound.sln                  # just the tests
```

On Windows without bash on PATH, `.\scripts\validate.ps1` runs the same steps.

On a Raspberry Pi with real hardware, run the daemon with `--hardware`: digital actuators then open
sysfs GPIO lines (manifest `pin`) and analog sensors open ADS1115 channels over I2C (manifest
`channel`, `bus`, `address`, `gain`). Enable I2C (`raspi-config` → Interfaces), run as a user in the
`i2c` and `gpio` groups, and keep every ADC input below VDD + 0.3 V — the gain setting is resolution,
not protection. Without `--hardware` every port is in-memory and the daemon says so at start-up.
Releases are cut with `scripts/release.sh` (or `scripts/release.ps1`) from a synced `main`.

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

## Layout

```text
src/Micromound.Protocol/       wire contracts: envelopes, charters, missions, manifests, evidence
src/Micromound.Crypto/         device identity, Ed25519 signing and verification
src/Micromound.Capabilities/   the capability kernel — the physical authority boundary
src/Micromound.Runtime/        Mound Major, worker registry, the six default ants
src/Micromound.Drivers/        bus abstractions and hardware drivers
src/Micromound.Evidence/       capture, correlation, local store, pending-sync queue
src/Micromound.Sync/           Runner Ant transport (enrollment, sync beat, durable uplink) and the
                               disk-backed stores: FileStateStore, FileEvidenceStore
src/Micromound.Reasoning/      optional reasoning provider, and the null default
src/Micromound.Host/           the headless Linux/Pi daemon
src/Micromound.Sim/            simulated mounds — the real kernel over fake hardware
firmware/esp32/                reduced deterministic controller (ESP-IDF)
tests/Micromound.Tests/        contract, authority, kernel, evidence, and golden-byte tests
```

## The upstream controller

MicroMound receives authority from an upstream controller: whoever holds the signing key, issues
charters, receives evidence, and holds the stop controls. The protocol deliberately does not name
one.

[ANTHILL](https://github.com/Formicaria/Anthill) is the reference implementation and a separate
application. **MicroMound support is an optional integration on the ANTHILL side** — ANTHILL is
complete without it, and MicroMound runs without ANTHILL. All user-facing configuration and colony
visualization live upstream; MicroMound ships no UI of its own.

See [`docs/UPSTREAM.md`](docs/UPSTREAM.md).

## Documentation

| Document | What it covers |
|---|---|
| [`docs/MICROMOUND.md`](docs/MICROMOUND.md) | Canonical design doc: mission, principles, terminology, authority model, non-goals |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Runtime layers, the capability kernel, drivers, routines, structured work, reasoning |
| [`docs/ANTS.md`](docs/ANTS.md) | The six default workers, and how specialized ants are declared |
| [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md) | Capability naming, the registry, routines |
| [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) | The declarative mound manifest |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | Wire contract: envelopes, charters, missions, evidence, canonical bytes |
| [`docs/SAFETY.md`](docs/SAFETY.md) | Safety model. **Where documents disagree, this one wins** |
| [`docs/UPSTREAM.md`](docs/UPSTREAM.md) | The controller contract, and ANTHILL as its reference integration |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Milestones and build order |

## Non-goals

Not a desktop UI, not a browser-based management application, not a coding or research agent, not
a long-term memory platform, not a mandatory language-model runtime, not a system where model
output controls GPIO, not a platform where edge devices expand their own authority, and **not a
replacement for independent physical safety hardware**.

## License

[Apache License 2.0](LICENSE)
