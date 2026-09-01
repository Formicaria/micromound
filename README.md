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

**Current version:** v0.9.2

**M0 frozen at `v0.2.1`; M1 done at `v0.3.0`; M2 done at `v0.6.0`; M3 done at `v0.9.1`; M4 begun at
`v0.9.2`.** Protocol contracts, Ed25519 signing, frozen wire bytes, the capability kernel with
deterministic authorization, the Mound Major that walks missions — and now all six default ants as
runtime services, a durable uplink queue whose chain is enforced at enqueue, restart recovery that
never clears a stop, never extends a lease, and never silently resumes physical work it cannot prove
finished, a bounded evidence store that says what a gap cost, and — new in `v0.9.2` — a **durable
file-backed state store** so operational state survives a restart on real disk (behind the same seam
the in-memory store uses), with the terminal report now persisted before its checkpoint is cleared
and a cold start driving actuators to safe state. Still simulated drivers behind the real `IDriver`
seam; end-to-end simulator missions run against an in-process controller that verifies every byte.
The v0 canonical bytes will not change again inside v0. No real drivers or firmware yet, and the
host does not run on a device yet — those complete M4 and M5. See [`docs/ROADMAP.md`](docs/ROADMAP.md)
and [`CHANGELOG.md`](CHANGELOG.md).

Releases continue as patch versions (`v0.9.2`, `v0.9.3`, …), including the internal M4 substrate
slices; `v0.10.0` is reserved for the M4 boundary where the host actually runs on a device over real
disk and drivers — not the automatic successor to `v0.9.x`.

```bash
bash scripts/validate.sh                    # guards + restore + build + test
bash scripts/validate.sh --full             # and the simulator smoke run
dotnet test Micromound.sln                  # just the tests
```

On Windows without bash on PATH, `.\scripts\validate.ps1` runs the same steps.
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
src/Micromound.Sync/           Runner Ant transport: enrollment, sync beat, durable uplink
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
