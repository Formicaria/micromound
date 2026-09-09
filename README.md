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

**Current version:** v0.9.37

**M0 frozen at `v0.2.1`; M1 done at `v0.3.0`; M2 done at `v0.6.0`; M3 done at `v0.9.1`; M4 in
progress (`v0.9.2`–`v0.9.17`); M5 in progress (`v0.9.18`–`v0.9.27`).** Protocol contracts, Ed25519 signing, frozen wire bytes, the
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
instead of a raw settings console, pinned to the drivers by test, and — new in `v0.9.16` — **GPIO over
the character device** (`GpioChardevOutput`, `/dev/gpiochipN`, the libgpiod interface, encoded against
`linux/gpio.h` and pinned to the header's numbers; daemon `--gpio chardev|sysfs`, chardev default), with
**both GPIO backings bringing a line up already at its safe level** so an active-low relay is never
pulsed at bring-up, and — new in `v0.9.17` — **ready for the board**: `--check-hardware` claims every
port a manifest names and reads each sensor once without actuating anything, a manifest naming physical
ports is refused in memory unless `--simulate` is said, the I2C bus is testable through the same syscall
seam as GPIO, and a deployment kit (systemd unit, installer, [`docs/DEPLOY.md`](docs/DEPLOY.md)) walks
a Pi from bench to the M4 boundary, and — new in `v0.9.18` — **the C mirror**: `firmware/micromound-c`,
a portable C99 library (canonical JSON writer, .NET-exact number layout, SHA-256, Ed25519 over vendored
TweetNaCl with detached sign/verify, envelopes, the reduced-profile bodies) that reproduces the golden
wire bytes byte for byte under gcc and clang on the host — the encoder-and-signer half of the ESP32
firmware, proven before any board is involved, and the change that made the protocol's string escaping
a written rule (PROTOCOL.md §2) instead of a runtime behaviour, and — new in `v0.9.19` — **the C
reader**: a bounded, allocation-free JSON reader and decoders for the `charter`, `stop` and `ack` a
device receives, verifying each downlink envelope's signature from the bytes as received and applying
the same validators and the same refusal reasons as the host, pinned by a new golden fixture of REAL
signatures (`canonical-signed.txt`) that BouncyCastle and TweetNaCl both reproduce, and — new in
`v0.9.20` — **the kernel in C**: `mm_kernel` reproduces `CapabilityKernel` decision for decision — the
same fourteen checks in the same order, the three-tier limit intersection, duty cycle and rate across
every capability a routine moves, clamping that says what narrowed, the evidence gate — pinned by a new
golden fixture (`kernel-decisions.txt`) in which a C# test scripts 42 steps against a fixed device and the
C kernel must reproduce every reason, detail, effective parameter, state and record, and — new in
`v0.9.21` — **the device loop**: `mm_device` is the Runner Ant in C — signed, chained uplink on a bounded
queue, the sync beat and its drain, downlink verified from the bytes as received and handled stops-first
(stop → safe state + ack; charter → accepted or refused with reasons; anything else → `refused_unknown_kind`),
acknowledgement-driven eviction, lease renewal on the acknowledged beat, quiesce on expiry. A whole
scripted session against a fake controller is recorded as `device-session.txt`, which the C test replays
byte for byte and a C# test verifies with the host's verifier, chain validator and typed contracts —
**the controller now accepts what the C device sends** — and — new in `v0.9.22` — **the board layer,
host-simulated**: enrollment with the host's exact verdicts (`enroll-exchange.txt`), the sync transport,
the relay and probe as kernel executors, and the service loop, all over a seven-function hardware
abstraction and driven through first boot, outage, refusal, enrollment, charter, hold, reboot, stop and
trip against a fake of it; `firmware/esp32` binds that abstraction to ESP-IDF in one file and — new in
`v0.9.23` — **compiles**: a 1.0 MB image under ESP-IDF v5.3.2, built by CI on every push, not yet flashed
or run — and, new in `v0.9.24`, **a device's readings reach the controller**: `action_record` carries its
referenced evidence items inline (the last in-place v0 wire amendment; every fixture regenerated), so the
host's evidence gate is satisfied by a board's record alone — and, new in `v0.9.25`, **the Pi↔ESP32
link**: the board's exchanges framed over a serial cable to `micromound --bridge` on a Pi (PROTOCOL.md
§12, pinned by `link-frames.txt` at both ends), and a second firmware image with no network stack at
all — and, new in `v0.9.26`, **the board as the Pi's hands**: port requests over the same link, the Pi's
own kernel the only authority, one manifest setting (`link`) putting a line or channel on the board, and
a third image (259 KB) that keeps only its compiled `max_on_s` and a watchdog for itself — and, new in
`v0.9.27`, **the acceptance bench, in software**: a third generic primitive (`digital_sensor` — the
independent observer a mound needs before any actuation it performs can honestly be called verified,
over a real GPIO input line or a board's input over the link), `mm_board_sim` (the real port-server
firmware as a host process, so a whole mound can be composed against real C over real framing with no
board on the desk), and `src/Micromound.Acceptance` — the eighteen ROADMAP acceptance criteria as an
executable sequence ([`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md)), **all eighteen now met in software on
both legs**. It earned its keep immediately: it found that the `verified` outcome was unreachable for
any honest actuator, and that a lease only expired when somebody happened to ask — both fixed in the
same release. What's still ahead for M4 is only the board itself.

**New in `v0.9.37`:** a mound that cannot record what it did must not do it. The uplink queue has
always been bounded, but the bound was enforced *after* the effect: the actuation happened, the
record was written, and the oldest envelope was spilled to make room — trading history the mound
already owed for work it had not done yet. The kernel now has a fourteenth check that refuses new
physical work when the audit path has no room for its record (`no_record_capacity`), and the queue
holds an eighth of its bound back so the refusal can always itself be filed. Observation is exempt,
for the same reason a stop does not blind the mound. The C mound had the same defect wearing the
opposite failure — it never dropped anything, but it acted first and only then found the queue full
— and is fixed in the same slice, under the same rule, pinned by six new steps in
`kernel-decisions.txt`.

**New in `v0.9.36`:** the runtime is .NET 10 LTS, and not one signed byte moved. .NET 9 is a
standard-term release that leaves support on 10 November 2026 — two months from now — while .NET 10
is supported to 14 November 2028. The migration is one line, so the whole slice is the proof: every
frozen wire fixture was **regenerated from scratch** on .NET 10 and came back byte-identical, which
is the only evidence worth having when a mound in the field verifies signatures produced by a mound
that was upgraded. The language version deliberately stays at C# 13; a runtime move should not also
change how the code is written.

**New in `v0.9.35`:** the C mound's stop is durable, and its identity is not erased to make room. A
stop lived in RAM on the ESP32, so power-cycling a stopped board brought it back willing to actuate —
the one thing [`docs/SAFETY.md`](docs/SAFETY.md) says a restart must never do. It is now one byte in
protected storage, written on the tick the stop arrives (a downlinked stop, or a relay that will not
release), read back before anything can act, and cleared by neither a restart nor a fresh charter.
Separately, the firmware no longer follows ESP-IDF's stock recipe of erasing all of NVS when it will
not initialise: that partition holds the seed this mound's identity *is*, the controller's key, and
now the stop — so a full or newer-format partition halts the board with its outputs safe and says
why, instead of silently coming back as a different, un-stopped device. Reprovisioning is a
deliberate act.

**New in `v0.9.34`:** the audit path is bounded and no longer rewritten whole. 4,000 queued records
used to mean a 2.4 MB state document rewritten on every enqueue at ~12 ms each, growing until the
disk filled; each envelope is now its own segment — a 609 B largest document at a flat ~2 ms — with
item and byte bounds, oldest-first spill counted and reported on the beat as `spilled_envelopes`,
and in-place migration of an existing queue.

**New in `v0.9.33`:** a restart no longer hands back what the hardware owes. The actuation history
persists, so a reboot cannot refresh a cooldown or a rate budget; and the handled-downlink ledger
persists too — keyed on the mission id as well as the envelope id, because a controller that
re-queues work mints a fresh envelope around the same mission, and that was the case that actuated
twice. Bounded by a validity horizon rather than a count, so an entry cannot expire into being
executable again.

**New in `v0.9.32`:** an authenticated stop now takes effect on the exchange that delivered it and
ends the drain there, instead of waiting out the backlog — measured on a twelve-mission queue, 64
exchanges before, 1 after — and a sync beat is bounded in how many batches it will push.

**New in `v0.9.31`:** a hold can no longer outlive its deadline because something else took time.
The tick releases due holds before the blocking sync as well as after it, adds the span the sync
actually cost to its own clock, and re-checks the lease on the far side; a hold now carries a
monotonic deadline alongside its wall-clock one and releases on whichever comes first, so a
backwards NTP step cannot extend it.

**New in `v0.9.30`:** the largest of those findings is closed. A `verify` step now says what it
expects to observe (`expect`), and the Witness compares the reading against it — so a limit switch
reporting "open" after a close command no longer confirms the close. An action whose confirming
observation disagrees degrades to `unverified` naming both sides; one that cannot be read degrades
too, with a different reason, because "the hardware did not do it" and "I could not tell" are
different facts. With feature negotiation both ways so a controller can tell what a device enforces
before it sends. Acceptance criterion 11 now tests agreement rather than presence.

**New in `v0.9.29`:** the first of those findings is fixed — `WatchingForSafeState` and the cold-start
recovery walk now go through the isolated `MoundHost.EnterSafeState()` instead of a bare `foreach`, so
one driver that refuses to de-energize can no longer end the walk and leave every later driver hot
during a stop. Two-actuator regression tests; every previous safe-state test had one actuator and
could not see it.

**New in `v0.9.28`** (documentation only): [`docs/ROADMAP.md`](docs/ROADMAP.md) gains **eight
dependency-ordered phases** for the work after the milestones — correctness and recovery debt, the
adapter and configuration model, a qualified core release, motion, network and printer packages,
cameras, optional reasoning, and packaging — together with ten findings read straight out of the
source, each of which the whole test suite is green through. The largest: **a wrong reading confirms
an action** — the Witness checks that a confirming observation exists, is fresh and postdates the
act, and never compares its value to the state the act was supposed to produce. The same release
retracts two stale gap entries and narrows the `v0.9.27` acceptance claim: the sequence passes; the
bench inventory's axis and encoder do not exist yet. End-to-end simulator missions run against an in-process controller that verifies every
byte. The v0 canonical bytes of every existing fixture are unchanged. The host has both a real digital
line and a real analog channel available, but has not yet been run on a device against real hardware —
that boundary finishes M4; what remains of M5 is the bench run of `firmware/esp32`. See [`docs/ROADMAP.md`](docs/ROADMAP.md) and [`CHANGELOG.md`](CHANGELOG.md).

Releases continue as patch versions (`v0.9.2`, `v0.9.3`, …), including the internal M4 substrate
slices; `v0.10.0` is reserved for the M4 boundary where the host actually runs on a device over real
disk and drivers — not the automatic successor to `v0.9.x`.

```bash
bash scripts/validate.sh                    # guards + restore + build + test
bash scripts/validate.sh --full             # and the simulator smoke run, the C mirror, and the acceptance sequence
dotnet test Micromound.sln                  # just the tests
make -C firmware/micromound-c test          # just the C mirror against the golden fixtures (gcc or clang)
make -C firmware/micromound-c tools         # build/mm_board_sim: the port-server firmware as a host process
dotnet run --project src/Micromound.Acceptance   # the acceptance sequence (docs/ACCEPTANCE.md)
```

On Windows without bash on PATH, `.\scripts\validate.ps1` runs the same steps.

On a Raspberry Pi with real hardware, run the daemon with `--hardware`: digital actuators then claim
GPIO lines on the character device (`/dev/gpiochipN`; manifest `pin`, optional `chip`; `--gpio sysfs`
for a legacy kernel) and analog sensors open ADS1115 channels over I2C (manifest `channel`, `bus`,
`address`, `gain`). Enable I2C (`raspi-config` → Interfaces), run as a user in the `i2c` and `gpio`
groups, and keep every ADC input below VDD + 0.3 V — the gain setting is resolution, not protection.
`micromound --describe-drivers` prints every setting; `--check-hardware` claims every port the manifest
names and reads each sensor once without actuating anything. Without `--hardware` a manifest that names
physical ports is refused unless you pass `--simulate`. [`docs/DEPLOY.md`](docs/DEPLOY.md) is the
step-by-step bring-up (systemd unit and installer in `deploy/`).
Releases are cut with `scripts/release.sh` (or `scripts/release.ps1`) from a synced `main`.

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — the LTS line,
supported to 14 November 2028. The language version stays at C# 13.

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
src/Micromound.Acceptance/     the ROADMAP acceptance sequence, executable (docs/ACCEPTANCE.md)
deploy/                        systemd unit, environment template, installer for a Pi
firmware/micromound-c/         the C mirror: wire format, reader, kernel, device loop and board layer of a reduced-profile mound (C99, host-tested)
                               tools/mm_board_sim.c — that same port server as a host process, for the acceptance run
firmware/esp32/                the ESP-IDF project: three images — Wi-Fi mound, serial-link mound, port server (compile under IDF v5.3.2; not yet run)
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
| [`docs/DEPLOY.md`](docs/DEPLOY.md) | Bringing a mound up on a Raspberry Pi: buses, install, check the wiring, enroll, charter, first mission |
| [`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md) | The acceptance sequence: the eighteen criteria, why each is on the list, and how to run them |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Milestones and build order |

## Non-goals

Not a desktop UI, not a browser-based management application, not a coding or research agent, not
a long-term memory platform, not a mandatory language-model runtime, not a system where model
output controls GPIO, not a platform where edge devices expand their own authority, and **not a
replacement for independent physical safety hardware**.

## License

[Apache License 2.0](LICENSE)
