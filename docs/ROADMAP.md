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
| **M4** — The Linux/Pi host and first real drivers | **In progress (`v0.9.2`–`v0.9.17`)** | Landing as slices. **`v0.9.2`:** a **file-backed `IStateStore`** (atomic per-key writes, restart-survivable) so operational state survives on real disk, plus persist-report-before-clear and cold-start de-energize. **`v0.9.3`:** the **driver-resolution seam** — `ManifestDriverComposer` turns a manifest's hardware into configured drivers, fail-closed, plus the first **generic driver primitives** (a digital actuator, an analog sensor). **`v0.9.4`:** a shared **`MoundComposition`** and a **`MoundHost`** that brings a mound up from a manifest over the file store, runs missions, and recovers across restarts. **`v0.9.5`:** a **runnable daemon** — a `MoundService` lifecycle (heartbeat, sync beat, watchdog, graceful safe shutdown) and a real `micromound` entry point; a safety trip escalates to a stop that survives a reboot. **`v0.9.6`:** a **real HTTP sync transport** (`HttpSyncTransport`) — the daemon POSTs signed envelopes to `<controller>/micromound/v0/sync` over HTTPS and reads the downlink (PROTOCOL.md §1), with offline a normal, non-throwing state and a bounded response. **`v0.9.7`:** **device enrollment** (`HttpEnrollmentClient` + `MoundHost.ResolveControllerKeys`, PROTOCOL.md §3) — the mound presents a one-time token, receives and persists the controller's key (validated before trust; recoverable; downlink dropped until enrolled), so a live link can now verify downlink, not just POST. **`v0.9.8`:** the **first real driver port** — `SysfsDigitalOutput` (a Linux GPIO output over `/sys/class/gpio`) and a `SysfsDigitalActuatorFactory` that opens it from the manifest's `pin`, giving the generic digital actuator a real line instead of an in-memory one; opening the port is fail-closed, and the momentary pulse is now fail-safe against a throwing port (a failed release re-drives safe and faults rather than leaving a line latched hot). The value writes must still be verified on real hardware — this is substrate, not the milestone. **`v0.9.9`:** the **digital actuator now holds its line for a real duration** — an actuation drives the line active and holds it for the effective `on_s`, released on the service loop's cadence (`ITimedDriver.ServiceHolds` + `MoundHost.ServiceActuations`, called each `MoundService.Tick`) and by the safe state on any stop/quiesce/shutdown/trip; the hold is clamped and capped at `max_on_s`, and a line that will not de-energize escalates to a persisted stop. A timed hold trades the momentary primitive's self-release for a real hold, so a *hung* loop can leave a line hot — which `v0.9.10` closes. **`v0.9.10`:** the **independent watchdog thread** — a hardware-independent timer (`LoopWatchdog` + `WatchdogThread`) on its own thread that de-energizes and stops the mound (`MoundHost.WatchdogStop`, a sticky persisted stop) if the service loop hangs past a hard timeout (daemon `--watchdog-s`, auto-derived by default). The concurrency is made correct: `GuardAnt` is thread-safe, the host serialises its safe-state path behind one gate (consistent lock order, bounded wait so the watchdog can't wedge), and the loop answers the watchdog at the top of each tick so a resuming loop stops itself before it could actuate on a stale view (the weak-memory ARM window). Residual: a loop wedged inside a driver op can't be de-energized by the watchdog — process supervision (systemd `Restart=`) is the named backstop. **`v0.9.11`:** **enrollment aligned with the reference controller (ANTHILL).** Reading both codebases side by side: the signed sync path, envelope, crypto and downlink kinds are shared code (ANTHILL compiles against `Micromound.Protocol`/`Micromound.Crypto`), so the only drift was in the hand-matched enroll handshake — and it was a hard blocker: the device declared `tier: mound_major`, which the controller refuses. Now the tier is the shared `ControllerTiers` vocabulary (`edge_queen` default, `--tier`), the device sends `mound_id` as a cross-check, `protocol_version`, and `capabilities[]`, parses the full response (and checks the bound `mound_id` and version itself), surfaces the controller's 4xx `reason`, and honours `sync_interval_s` — enrollment's as the bootstrap (persisted in an additive `controller.meta.json` sidecar), the active charter's as the live authority — throttling the sync beat ONLY, never hold release, the watchdog, or the heartbeat. **`v0.9.12`:** the **evidence store survives a restart** — `FileEvidenceStore` (beside `FileStateStore`, sharing the extracted `DurableFiles` atomic-write/dir-fsync primitives): one file per item named by insertion sequence, an `.ack` marker per acknowledgement, `counters.json` for the not-yet-reported evicted/spilled counts; the v0.9.0 retention policy is unchanged and verified at exact parity with the in-memory store; crash order chosen so proof is only ever re-sent, never lost or resurrected; corrupt items skipped and reported. The host runs it at `<state>/evidence`. Cost stated plainly at the time: ~4 fsyncs per tick on a chartered mound, dominated by the per-tick heartbeat evidence. **`v0.9.13`:** that follow-up — **heartbeat evidence rate-limited to what is informative**: `GuardAnt.Poll` emits a reading on the first poll, on every fresh↔stale transition (the reading that explains a stop is never suppressed), and otherwise no more often than `heartbeatEvidenceIntervalSeconds` (default 60 s; 0 = the old every-poll behaviour). Staleness is still recomputed on every poll, so the kernel's refusal is never delayed. Measured on a real host over the real durable store: 120 evidence files → 10 over ten minutes of 5 s ticks (12×), plus the per-actuation reading eliminated. Still use endurance media for a long-lived Pi deployment; the residual write rate is now dominated by real readings and acknowledgements. **`v0.9.14`:** the **analog port is real** — `II2cBus`/`LinuxI2cBus` (i2c-dev over three libc calls) and `Ads1115AnalogInput` (one single-ended channel of the ADS1115 in single-shot mode, result in volts, chip probed at construction so a missing chip refuses bring-up), an `Ads1115AnalogSensorFactory` that opens the channel from the manifest's `channel`/`bus`/`address`/`gain` (validated before the bus is touched; a refusal never leaks a device node), `scale`/`offset` calibration on the analog sensor, a failed read reported as a fault with NO reading, and a daemon `--hardware` flag (`MoundHost.HardwareDriverFactories`) so the sysfs GPIO and ADS1115 ports are actually composed on a device — until now the daemon only ever built in-memory ports. Proven against a register-level fake chip; the I2C transfers must be verified on a board. **`v0.9.15`:** the **device describes its own hardware vocabulary** — `DriverTypeSchema`/`DriverSettingSchema` and `DriverSchemaCatalog.Shipped` in the protocol library (label, help, kind, required, default, bounds, choices, `hardware_only`, `advanced` per setting), `IDriverFactory.Schema` + `DriverFactoryRegistry.Describe()`, sent at enrollment as the additive `driver_schemas` and printed by `micromound --describe-drivers`, so the controller can generate a plain-language hardware form instead of hard-coding setting names; pinned to the drivers by a recording-dictionary test (every key a driver reads is described, every default is real). Descriptive only — authority still comes only from a charter. **`v0.9.16`:** **GPIO over the character device** — `ILinuxIo`/`LibcIo` (open/ioctl/close seam), `GpioChardevOutput` (`/dev/gpiochipN` uapi v2: one `GET_LINE` request as OUTPUT with the initial value in the request, `SET_VALUES` per write; layout and ioctl numbers measured from `linux/gpio.h` and pinned in tests), `GpioChardevActuatorFactory` (`pin`, `chip` default 0), daemon `--gpio chardev|sysfs` (chardev default). Safety fix for both backings: a line is requested ALREADY at `!active_high` (sysfs now writes `high`/`low` as the direction), so an active-low relay is never pulsed at bring-up; a reconfigure releases the old line. **`v0.9.17`:** **ready for the board** — `--check-hardware` (`HardwareCheck`: claim every manifest port as bring-up would, read each sensor once, per-device report, exit 0 only if all claimed; actuates nothing), `--simulate` with a REFUSAL of a physical manifest in memory without it, `LinuxI2cBus` over the `ILinuxIo` seam (value-argument ioctl for `I2C_SLAVE`; every error path tested against a fake kernel), sysfs export-settle wait, and the deployment kit (`deploy/micromound.service` with `Restart=always` and device-scoped hardening, `install.sh`, `docs/DEPLOY.md` — six checkable steps to the M4 boundary). **Remaining:** DEPLOY.md on a board, to the end — then `v0.10.0`. `v0.10.0` is reserved for the boundary where the host runs on a real device against real hardware — not these slices. (A relational store like SQLite is deliberately *not* used: the `IStateStore` contract is a narrow string-keyed document store, and its own design note calls for a directory of files rather than a database.) |
| **M5** — Constrained controller firmware | **In progress (`v0.9.18`–`v0.9.27`)** | ESP32 reduced controller implementing the same protocol and capability kernel in C, verified byte-for-byte against the golden fixtures, over a compact versioned Pi↔ESP32 packet protocol. **`v0.9.18`: the C mirror exists, host-verified.** `firmware/micromound-c` — portable C99, no allocation, `-Wall -Wextra -Werror -pedantic` under gcc and clang: the canonical JSON writer (`mm_json`), .NET's double layout (`mm_format`), SHA-256, Ed25519 over vendored TweetNaCl with a seed keypair and *detached* sign/verify (`mm_ed25519`), envelopes with the `"sig":""` canonical form, digest, sign, in-place signature splice and strict verify (`mm_envelope`), and the reduced-profile bodies `mound_sync`, `action_record`, `ack`, `charter` (`mm_bodies`). `make test` reproduces the golden `mound_sync`/`action_record`/`charter` envelopes and both bodies byte for byte, checks every envelope digest and the chain, and runs the RFC 8032 vectors plus a cross-implementation signature. To get there the protocol's string escaping was made a rule instead of a runtime behaviour (PROTOCOL.md §2; `CanonicalJsonEncoder`), and two new fixtures pin it (`canonical-strings.txt`, `canonical-doubles.txt`). **`v0.9.19`: the C reader.** `mm_json_read` (bounded pull parser, full escape grammar, depth-capped, unknown members skipped), `mm_decode` (the envelope frame; `charter`/`stop`/`ack`/`action_record` into fixed-capacity structs with the C# defaults; `EnvelopeValidator` and `CharterValidator` with the same closed refusal set; signature verified over the bytes AS RECEIVED with the sig cut out, no re-serialization), `mm_time` (protocol timestamps ↔ epoch). A new fixture with REAL signatures (`canonical-signed.txt`, fixed test seeds) is produced by BouncyCastle and verified, decoded, re-encoded and re-signed identically by TweetNaCl. 1,273 checks; sanitizer findings fatal. **`v0.9.20`: the kernel in C.** `mm_kernel` = `Micromound.Capabilities`: compiled capability/routine tables validated with the registries' rules, `KernelAuthority` (charter, lease, stop/quiesce, device limits), the thirteen authorization checks in the host's order, hardware ∩ device ∩ charter, duty cycle and rate across every capability a routine moves, clamping that names what narrowed, execution through a function pointer, the evidence gate — and the same refusal reasons with the same detail text. A new fixture, `kernel-decisions.txt`, scripts 42 steps against a fixed device in C#; the C kernel replays the script and reproduces every reason, detail, effective parameter, state and record. 1,690 checks. **`v0.9.21`: the device loop.** `mm_device` = `RunnerAnt`: signed, chained uplink on a bounded queue (full = refuse to record), the beat and its acknowledgement-driven drain, downlink verified from the bytes as received and handled stops-first (stop → safe state + ack; charter → accepted or refused with reasons; anything else → `refused_unknown_kind`), an idempotency window, lease renewal on the ACKNOWLEDGED beat, quiesce on expiry, offline as a normal state. `device-session.txt` — a whole scripted session — is written by the C test and **verified by the host** (`DeviceSessionTests`: every uplink envelope under the device key, the chain across an outage's re-sends, every body through the typed contracts). 1,792 checks. **`v0.9.22`: the board layer, host-simulated.** Everything a board runs, written over a seven-function hardware abstraction (`mm_hal`: clock, entropy, one HTTPS POST, protected kv storage, a digital output, an analog input): `mm_enroll` (PROTOCOL.md §3 as `HttpEnrollmentClient` does it — same request body, same verdicts in the same words; a new fixture `enroll-exchange.txt` pins fifteen scripted responses), `mm_link` (`HttpSyncTransport`), `mm_drivers` (the relay and the probe as kernel executors — safe at bring-up, held and released, no evidence from a command, a failed read is a fault not a zero), and `mm_app` (identity created once and stored, enrollment with a one-time token that burns on a definite refusal, the service loop, the trip). `test_board.c` drives the whole thing against a fake HAL through first boot, outage, refusal, enrollment, charter, readings, hold, reboot, stop and trip. 1,954 checks. And `firmware/esp32` is no longer a placeholder: an ESP-IDF project binding `mm_hal` to SNTP, `esp_http_client`, NVS, GPIO and ADC in one file, with `app_main`, a board description and a task watchdog — written first, and — **`v0.9.23`: compiled.** `idf.py build` under ESP-IDF v5.3.2 produces a 1.0 MB image (`libmicromound_c.a` 37.8 KB of flash code; the static `mm_app` 40 KB of DRAM with an uplink queue of 8; 105 KB of DRAM left at link), and CI builds it on every push. Two things the real toolchain caught in the library — gcc 13's format-truncation heuristic at `-Og` on two audit lines — were fixed with the fields' own bounds; nothing about the wire or the kernel changed. **Not yet flashed or run.** **`v0.9.24`: readings reach the controller.** `action_record` gains `evidence` — the referenced items inline (PROTOCOL.md §6): a reduced-profile device, which has no `evidence_bundle`, sends its proof on the record that cites it; a Pi leaves it empty and keeps its bundles. The last in-place v0 wire amendment; every fixture regenerated and reviewed; the host's evidence gate is satisfied by a device record alone (`DeviceSessionTests`). **`v0.9.25`: the Pi↔ESP32 link.** PROTOCOL.md §12: the board's two HTTP exchanges framed over a serial cable (`"MM" ver type seq len payload crc32`) to a bridge on a Pi that performs the HTTPS half — transport, not authority; not a byte of any envelope changes. Pinned by `link-frames.txt` at both ends (`LinkFrame`/`LinkBridge` and `micromound --bridge` on the host; `mm_frame`/`mm_serial` in C, 2,402 checks); a second firmware configuration with no network stack at all, 296 KB. **`v0.9.26`: the board as the Pi's hands.** PROTOCOL.md §12 port requests: over the same framing, a Pi-class mound's own kernel authorizes every action and reaches the board's pins and channels through its unchanged generic drivers by one manifest setting (`link`); the board (`mm_ports`, a third firmware image of 259 KB with no identity at all) keeps only its compiled `max_on_s` per pin and a link watchdog, and trips on a release that fails. Pinned by `port-exchange.txt` (written by the C board, read by the Pi's client tests). **`v0.9.27`: the acceptance bench, in software.** A third generic primitive (`digital_sensor` / `mm_switch`, and `micromound/link/ports/read_pin` with an `inputs` array in `hello`) — the independent observer without which no actuation a mound performs can honestly be called verified; `tools/mm_board_sim`, the real port server as a host process with only the world below its HAL modelled and a clock that only moves when told; and `src/Micromound.Acceptance`, the eighteen criteria below as an executable sequence run against a real mound and that real firmware over real framing — **all eighteen met on both legs** ([`ACCEPTANCE.md`](ACCEPTANCE.md)). It immediately found two defects every unit test was green through: the `verified` outcome was unreachable for any honest actuator, and a lease only expired when somebody happened to ask. Both fixed in the same release; a mission step gained a bounded `settle_s` so a `verify` can wait for an actuator to travel. **Remaining for M5:** the bench run of `firmware/esp32` — any of its three images against real hardware. |
| **Acceptance** — Generic Physical Mound | Criteria, not a code milestone. **The eighteen-criterion sequence is met in software at `v0.9.27`. The bench run remains, and so does the axis/encoder half of the bench inventory (phase P3).** | The end-to-end proof on a minimal real bench that a fresh mound boots its default colony, is configured and chartered from upstream, moves generic hardware through the kernel, verifies with independent evidence, survives disconnect/reboot/lease-expiry safely, and synchronizes an auditable history back. See [The target](#the-target-a-generic-physical-mound), and [`ACCEPTANCE.md`](ACCEPTANCE.md) for the criteria numbered and runnable. |
| **P0–P7** — After the bench | **Planned; P0 is next** | The milestones above answered *what has to be true before something can move*. The phases answer *what has to be true before someone else can depend on this*: correctness and recovery debt, an extension model, hardware qualification, and optional device packages that ship independently. See [the phase plan](#after-the-bench-the-phase-plan). |
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
   store were proven in memory and in the simulator, and their disk backing (a directory of files,
   not a database) belongs to M4.
3. **What must exist before real hardware can move?** M4, **in progress**: the durable file-backed
   state store (`v0.9.2`), the driver-resolution seam and generic driver primitives (`v0.9.3`), a
   `MoundHost` that composes and runs a mound from a manifest over that store (`v0.9.4`), a runnable
   daemon with a safe service lifecycle (`v0.9.5`), a real HTTP sync transport to the controller
   (`v0.9.6`), device enrollment over that transport (`v0.9.7`), the first real driver *port* — a Linux
   GPIO output over sysfs behind the generic digital actuator (`v0.9.8`), a digital actuator that *holds*
   its line for a real `on_s`, released on the loop's cadence and by the safe state (`v0.9.9`), and an
   **independent watchdog thread** that de-energizes and stops the mound if the service loop hangs, so
   a held line cannot stay hot behind a stuck loop (`v0.9.10`), and an enrollment handshake aligned with
   the reference controller so a real mound actually gets through ANTHILL's front door (`v0.9.11`),
   a durable evidence store so the proof a mound captures survives a reboot (`v0.9.12`), and heartbeat
   evidence rate-limited so that durability costs ~12× fewer writes with the refusal logic untouched
   (`v0.9.13`), and the analog port made real — an ADS1115 channel over I2C behind the generic sensor,
   with the daemon's `--hardware` flag composing the real GPIO and ADC ports (`v0.9.14`), and the
   device describing its own driver settings so the controller's hardware form can be generated rather
   than hand-matched (`v0.9.15`), and GPIO over the character device with every line brought up already
   at its safe level (`v0.9.16`), and the operator's kit — a wiring check that actuates nothing, a
   refusal to fake a physical manifest, a systemd service and a six-step bring-up guide (`v0.9.17`).
   Still remaining: running the guide on a board, to the end. The daemon now has a real digital
   line that holds an actuation, a real analog channel to sample, an independent watchdog guarding
   both, a controller it can enroll with, and evidence that outlives a restart — everything but the
   hardware verification that `v0.10.0` marks.
4. **What comes immediately after that?** M5 — the ESP32 as a subordinate deterministic controller
   speaking a compact Pi↔ESP32 protocol, running the same kernel in C, byte-verified against the
   golden fixtures. Not a second colony. Its software already exists and is host-verified:
   `firmware/micromound-c` — encoder, signer, reader, validators, the capability kernel, the
   device loop and the board layer (enrollment, transport, drivers, the service loop over an
   eight-function HAL) in C, with a whole session accepted by the host's own verifier
   (`v0.9.18`–`v0.9.27`). `firmware/esp32` binds that HAL to ESP-IDF and compiles to three images under
   ESP-IDF v5.3.2 — a 1.0 MB Wi-Fi mound, a 297 KB serial-link mound, a 261 KB port server that is the
   Pi's hands; flashing one and running it against real hardware is what remains. Since `v0.9.27` that
   port server also runs as a host process (`tools/mm_board_sim`), which is how the acceptance sequence
   drives real firmware over real framing with no board on the desk.
5. **When is Micromound physically usable?** At the **Generic Physical Mound** acceptance below —
   the first time the whole path runs on real hardware. That is the line between a software
   architecture and a functional physical edge colony. As of `v0.9.27` every one of its eighteen
   criteria is met in software ([`ACCEPTANCE.md`](ACCEPTANCE.md)). What is left is the wiring, the
   axis and encoder the bench inventory names and no driver yet provides (phase P3), and the P0
   correctness work that a bench would otherwise be qualifying on top of.
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

**This sequence is executable, and as of `v0.9.27` it passes.** [`ACCEPTANCE.md`](ACCEPTANCE.md)
numbers the sentence above into eighteen criteria and `src/Micromound.Acceptance` runs them — against
a real mound (real kernel, real ants, real drivers, real durable store, real signed wire) and, on the
firmware leg, against the real port-server C in its own process on the other end of a real byte
stream. It is not a substitute for the bench; it is what makes the bench day short, because
everything that can be proven without a soldering iron is proven first. What is left for the bench is
the physical world itself: that a line at 3.3 V closes the relay, that the relay opens the valve,
that the switch observes the thing it claims to, and that the board survives its environment.

**Two honest qualifications on that pass, recorded so the claim is not read wider than it is.**

*The bench inventory is not complete.* The paragraph above names "one stepper/servo axis, one
position encoder" alongside the switch, output and ADC. The eighteen criteria exercise the
relay/switch/ADC scenario only — there is no axis, no motion primitive and no encoder in the
shipped drivers. The **sequence** passes in software; the **bench** the target describes is not yet
built. Phase P3 below closes that gap explicitly rather than letting a relay stand in for a motor.

*A passing criterion is not a proof of the property it is named for.* Criterion 11 asks whether an
independent line was read after the act. It does not ask whether the line read the **right thing** —
`WitnessAnt.Confirm` checks that a confirming observation exists, is fresh, and was captured after
the action began, and then hands it to the evidence gate; nothing anywhere compares the reading to
the state the action was supposed to produce. A limit switch reporting "open" after a close command
confirms the actuation today. That is P0.1 below, and it is the single most important thing on this
roadmap: it is the difference between "something looked" and "what it saw agrees".

## After the bench: the phase plan

Milestones M0–M5 answered *what has to be true before something can move*. That question is nearly
answered. The next one is different — **what has to be true before someone other than its author can
depend on this** — and it does not decompose into the same numbered milestones, because the work is
now a mix of correctness debt, an extension model, hardware qualification and optional packages that
must be able to ship independently.

So the work below is organized as **phases in dependency order, not milestones and not dates**. A
phase is done when its exit condition holds. Hardware qualification and protocol coordination with
the controller set the pace, and neither is a thing this repository can schedule alone.

The milestone record above is history and stays as written. These phases are what comes after it.

| Phase | Result | Boundary |
|---|---|---|
| **P0** — Trustworthy execution and recovery | False confirmation, replay, timing, persistence and queue defects closed | First. Blocks every wider physical-control claim, including the bench |
| **P1** — Extensible runtime and configuration | A small adapter contract, typed operations, durable cancellable jobs, real manifest activation | Builds on P0. Wire changes designed with the controller and the C mirror |
| **P2** — Qualified core release | A reproducible Pi + ESP32 bench install someone else can repeat | P0 plus whatever P1 that release advertises. **This is the `v0.10.0` boundary** |
| **P3** — Motion package | Servo and DC motor, then a bounded stepper axis with independent feedback | P1 and a qualified core. Closes the axis/encoder half of the acceptance target |
| **P4** — Network and printer packages | Shared HTTP/credential substrate, then Moonraker, then OctoPrint | P1 and a qualified core. Independent of motion |
| **P5** — Camera and media package | Content-addressed artifacts, snapshots, optional stream/PTZ | P1's artifact and job contracts |
| **P6** — Smart workflows and optional reasoning | Deterministic feedback behaviours first; the reasoning seam finished after | Uses capabilities already qualified in P3–P5. Reasoning stays optional |
| **P7** — Maintainable product | Runtime currency, release profiles, updates, diagnostics, a contributor path | **Starts during P0**, not after everything else |

**The core release does not wait for every adapter.** Qualify packages separately and say precisely
which combination passed. A GPIO deployment ships before camera analysis exists.

### P0 — close the correctness and recovery gaps

**Goal: a command cannot silently become a false success, repeat itself after a restart, outlive its
time budget because the network blocked, or vanish from the record after having had an effect.**

Every row below was reproduced or read directly out of the named source in this repository on
7 September 2026. None of them is hypothetical, and every one of them was green through the whole
test suite, the simulator and the eighteen acceptance criteria — which is the point: they are gaps
between components, and the existing tests check components.

| ID | What is true today | What has to change |
|---|---|---|
| **P0.1** | **A wrong reading confirms an action.** `WitnessAnt.Confirm` checks a confirming observation exists, postdates the action and passes the freshness gate; nothing compares its **value** to the state the action was meant to produce. A switch reading "open" after a close command yields `succeeded` | Typed postconditions bound to a source capability, with units and either a tolerance or an exact boolean/enum comparison, and a bounded observation window. Wrong, stale, malformed or unrelated evidence must not confirm. A request with no expressible assertion stays `unverified` where proof is required |
| **P0.2** | **A hold can outlive its deadline.** The daemon takes one `DateTimeOffset.UtcNow` per tick and `MoundService.Tick` runs the blocking sync before `ServiceActuations(now)` — so a slow HTTPS round trip is time the hold never sees. Elapsed durations are measured on the wall clock, which an NTP correction can move | Service deadlines independently of sync, storage and mission waits. Monotonic time for elapsed durations; UTC only for signed timestamps and absolute expiry. Re-check authority after any wait and before any effect. Prove hold release under slow network, slow disk and a clock step |
| **P0.3** | **A stop waits for the backlog.** `RunnerAnt.Sync` defers every non-ack downlink until the drain loop settles. The ordering *within* a batch is stop-first, which is what the code was written for — but the stop still waits out every remaining exchange, and every further batch | Bound the work per sync cycle and act on an authenticated stop the moment it is verified, not when the drain finishes. An exhausted queue must never delay stopping. Measure receipt-to-output separately from controller delivery latency |
| **P0.4** | **A completed mission can be replayed.** The host's `CacheAnt` persists `authority` and the in-flight mission checkpoint; there is no durable record of *which signed work has already been executed*. Redelivering a completed mission after a clean restart actuates again | A durable command/mission ledger — accepted identity, intent, result, bounded validity — consulted before dispatch. Duplicate delivery returns the stored result or an explicit "uncertain". Test the same envelope, and the same mission id under a new envelope, across a reboot |
| **P0.5** | **A restart resets the duty cycle.** `ActuationHistory` is two in-memory dictionaries. Nothing saves or restores it, so `min_off_s` and `max_rate_per_h` — real safety limits — start empty on every boot. A 300 s cooldown is enforced before a restart and gone after one | Persist the operating budget, or reconstruct it conservatively when it cannot be trusted. Cover routine-backed resources, power loss and clock rollback. A restart must never shorten an off-interval or refresh a rate budget |
| **P0.6** | **The C mound forgets more than it should.** `mm_app` persists the seed, controller key, sync interval and enrollment token. A sticky stop, the pending uplink, and the chain/sequence anchor are not among them, so a reboot loses them | Persist sticky stop, replay state, operating budgets and uplink continuity with crash-safe ordering — or negotiate an explicit boot epoch and reconciliation. Test against a controller that checks continuity across a reboot, not merely individual signatures. Applies to the autonomous images; the identity-free port server has its own contract |
| **P0.7** | **The audit path is unbounded and rewritten whole.** `DurableUplinkQueue` keeps an unbounded `List<Envelope>` and reserializes the entire pending list on every change. The evidence store's item ceiling does not bound evidence already embedded in queued envelopes | Reserve durable audit capacity **before** an effect. Bounded append/segment storage on the host, bounded wear-aware persistence on firmware, item and byte limits, a reserved area for critical records, and reported loss. Never silently drop a signed chain segment or claim complete history across a gap |
| **P0.8** | **One throwing driver can leave the others energized.** `MoundHost.EnterSafeState()` is properly isolated — per-driver `try`/`catch`, a reported trip, under `_safeGate`. But `WatchingForSafeState`, the path taken on *every* transition into stopped or quiesced from a sync, a mission, or (since `v0.9.27`) an expired lease, calls `driver.EnterSafeState()` raw in a bare `foreach`: the first driver to throw aborts the loop, every later driver stays live, the failure reports no trip, and the exception escapes into the caller. It also runs outside `_safeGate`, so it races the watchdog thread. Separately, a loop wedged *inside* a driver call is named in `SAFETY.md` as relying on `Restart=always`, which does not itself kill a still-running process | Route that transition through `MoundHost.EnterSafeState()` — a one-line change, and the first thing in P0 to do. Then the rest: a real supervised-liveness failure path with bounded escalation, every stop/quiesce/recovery path exception-safe and attempted per driver, failures recorded, and no racing action able to re-energize after a stop. Prove one blocked driver cannot keep unrelated outputs live |
| **P0.9** | **Boot can erase the device's identity.** `app_main.c` calls `nvs_flash_erase()` on `ESP_ERR_NVS_NO_FREE_PAGES` / `NEW_VERSION_FOUND` — the ordinary ESP-IDF idiom, which on a mound throws away the identity seed and the controller key, and would throw away a persisted stop | Separate recoverable storage repair from deliberate reprovisioning. A storage fault must not silently erase identity or clear a stop. Bring hardware to a safe level as early as feasible. Test corrupt, full and interrupted writes |
| **P0.10** | **.NET 9 reaches end of support on 10 November 2026.** `Directory.Build.props` targets `net9.0`; the open dependency-update PRs are untriaged | Migrate to .NET 10 LTS and re-run the canonical-byte, signature and C interoperability fixtures — **a runtime upgrade must not change a signed byte.** Triage the open PRs on their merits rather than merging or losing them wholesale. Record the ESP-IDF and toolchain pins with their support dates |

Two design notes these rows depend on, so they are decided once rather than per row.

**Give every accepted operation a durable identity before dispatch**, and keep the existing
interrupted-actuation refusal: an adapter may retry a *read*; a start-print or a move whose response
was lost needs reconciliation or a device-supported idempotency key. A local ledger cannot by itself
make a remote physical effect exactly-once. Bound replay history without making an expired entry
executable again — an authenticated sequence/epoch rule or an explicit validity horizon, not simply
forgetting. Stop delivery stays idempotent and is never subject to ordinary retention rules.

**Bound the whole system, not one store**: pending envelopes, evidence bytes, action and audit
collections, completed jobs, deduplication state, retry work, media and logs. If persistence of a
stop fails, the physical response still happens and the failure to record it is surfaced — never the
other way round.

**P0 exit:** every finding above has a regression check in the real host and C harnesses;
power-loss, restart and slow-I/O tests show the required behaviour; the existing suites still pass or
were deliberately updated for stricter semantics. That is software evidence — hardware timing and
electrical behaviour still belong to P2 and P3.

### P1 — finish the extension and configuration model

**P1.1 A compact, versioned adapter contract.** Build on the existing driver factories, registry,
schemas and kernel — this is the missing *contract*, not a replacement plugin framework. It needs:
device identity (stable id, protocol version, expected identity at reconnect); capability description
(the existing `sense.` / `act.` / `routine.` ids, action class, typed input/output, units, effective
bounds, availability); operation semantics (read, bounded actuation, or long-running job; deadline;
idempotency and reconciliation; cancellation; required evidence); resource ownership (which pin,
axis, serial channel or machine is reserved, and what concurrent reads are allowed); failure
behaviour (timeout, unavailable, disconnected, **uncertain**, stop capability, safe state); and
installation metadata (versions, host-vs-MCU support, permissions, tested models, resource cost).

One device may expose many capabilities — a printer exposes state, temperatures, job control and
file staging without anyone writing a Printer Ant. Network adapters sit behind the same kernel
boundary, which sharpens rather than contradicts "the Runner is the only outward-facing worker":
**the Runner owns controller communication; a trusted adapter may talk to its configured device; a
generic worker still never receives a raw socket or driver handle.** Small trusted adapters stay
in-process; large, native, vendor-specific or less-trusted ones belong in a sidecar behind a bounded
IPC protocol, holding only the credentials its profile needs. An interface is not a sandbox.

**P1.2 Typed arguments and observations, with compatible wire evolution.** Mission and execution
parameters are `Dictionary<string, double>` today. That cannot express a file reference, a job id, an
enum or a structured machine status, and numeric readings cannot carry the evidence an integration
needs. Add a small bounded vocabulary — number with unit, boolean, enum, bounded string/identifier,
artifact reference, explicitly bounded structured output — with limits on nesting, length and
collection size, and **no expression language in the kernel**. Separate the facts that are currently
conflated: request accepted, operation active/terminal, evidence pending/confirmed/unverified/
contradicted, and parameters adjusted are four different things. Observation provenance must
distinguish a command acknowledgement from device-reported state from independently observed state.

Design it with the controller and the C mirror, and **use a new protocol version or mandatory feature
negotiation for anything an old device could silently ignore** — postconditions above all, plus typed
actuation, replay epochs and stop behaviour. An unknown required feature is refused before work
starts. Regenerating a golden fixture does not make a semantic change compatible.

**P1.3 Durable, cancellable jobs.** `settle_s` (`v0.9.27`) is a bounded blocking wait, and it is the
right primitive for an actuator's travel time — it is not a foundation for a print that runs for six
hours. Add a small scheduler and durable operation state: queued/accepted/running/cancel-requested/
terminal, external job identity, progress timestamp, deadline, evidence status, recovery decision.
Cancellation has a requested state and an observed outcome, and they are not the same fact. Persist
the association before dispatch; recover an uncertain operation by asking the device, never by
assuming a checkpoint means motion may resume. Reserve resources so two missions cannot drive one
axis, and revalidate preconditions immediately before dispatch — an external user at the machine's
own UI is a normal event, and job ownership cannot be inferred from "the printer is busy".

**P1.4 Make signed configuration real.** `MoundMajor.ApplyManifest` validates the manifest and
applies its authority settings, and that is all: it does not recompose drivers, and
`MoundComposition` registers the six default ants and an **empty routine registry**. Declared
workers are schema-validated and then never registered. So today a signed manifest that changes a
hardware binding is accepted and ignored — the exact failure mode the safety doctrine forbids.

Implement validate → resolve installed adapters → stage → activate → persist active revision →
report. Keep the previous working configuration when staging fails, and expose accepted revision,
active revision, validation errors and restart requirement as separate facts. **Staging a hardware
change for a controlled restart is a perfectly good first implementation** — the capability registry
is immutable for a process lifetime and that simplicity is worth keeping; hot rebinding is optional
later work. What is not acceptable is silently accepting a binding the running process ignores. A
worker declaration must bind to an installed behaviour template — a name and a purpose do not create
executable logic — and an unsupported declaration fails visibly. Add real routine registration and
execution, starting with reusable bounded sequences (sample-and-check, hold-and-confirm,
home-an-axis); a charter enables an installed routine, it never uploads code.

**P1.5 Deterministic scheduling and real Guard inputs.** Add bounded interval and event sampling with
debounce, hysteresis and freshness, so a headless mound collects its configured observations while
idle instead of needing an upstream mission per sample, and so a reconnect does not produce a
catch-up burst. Wire configured limit, interlock, driver-fault, power and thermal observations into
Guard rules — today the Guard polls heartbeat health, and the broader monitoring in the docs is
description, not connected inputs. Report the observed fact and the resulting action separately.
Expired authority and stop stay dominant; an offline `drain` policy never extends a lease.

**P1 exit:** one unchanged host build with the same six ants runs two materially different device
profiles; a signed hardware change activates and survives a restart; an unsupported field fails
loudly; a job stays interruptible through slow I/O; and declared worker, routine and offline
behaviour is exercised rather than merely validated.

### P2 — qualify and ship the core

**Goal: someone else installs a release on documented hardware and reproduces its claims.**

Publish a minimum supported Pi and OS, one ESP32 board revision, the exact wiring, power, storage
media, firmware configuration, manifest and procedure. The relay/switch/ADS1115 scenario is the core
bench. Qualify the three ESP32 roles **separately** — a passed port-server bench says nothing about
autonomous-mound persistence:

| Profile | Owns | Required proof |
|---|---|---|
| Wi-Fi mound | Identity, reduced kernel, enrollment, local work, signed uplink | Reboot-safe stop/queue/sequence, real TLS and enrollment, outage and lease behaviour, power interruption |
| Serial mound | The same authority, with a Pi bridging transport | Everything above, plus framing, bridge failure, reconnect and clock recovery |
| Port server | The Pi's local endpoints; no identity of its own | Enforced local bounds, link-loss response, input readback, reset behaviour, explicit re-arm before further work |

The port server's CRC detects damage; **it is not authentication.** Keep its physical-trust assumption
explicit and do not expose its raw actuation protocol on a network as a shortcut.

Fix installation permissions for the hardware actually chosen: the unit grants GPIO and I2C, and the
serial link also needs serial-device access and the corresponding group or udev rule. Prefer stable
device identity so a USB renumbering cannot silently select a different board. Publish measured hold
overshoot, stop response, watchdog escalation, boot-to-ready, resource use and persistence
behaviour, and exercise controller outage, link loss, unplugged serial, both reboots, disk full,
corrupt state, a stalled driver, a contradicting sensor, and lease expiry while both idle and busy.
Run a 24-hour bench exercise before claiming initial qualification; keep a longer soak for the stable
gate.

**P2 exit:** artifact → clean install → enrollment → signed configuration → authorized operation →
independent observation → outage and restart → auditable recovery, on named hardware, with versions
and logs recorded. **This is the `v0.10.0` boundary** — for the qualified scope, not for every device
anyone might imagine.

### P3 — motion, without becoming a motion controller

This closes the half of the acceptance target that the eighteen criteria do not reach.

| Increment | Build | Evidence that it works |
|---|---|---|
| **P3.1** PWM and servo | Hardware-timed PWM; calibration; bounded angle/pulse range; output disable; defined startup state | Correct waveform under host and network load; a commanded angle with no feedback stays **unverified** |
| **P3.2** DC motor | Direction, bounded speed, acceleration ramp, enable, defined coast/brake; current, speed and fault inputs where available | An encoder or tachometer confirms motion; stall, fault and limit produce the declared response |
| **P3.3** Stepper axis | STEP/DIR through a qualified driver; bounded position, velocity and acceleration; homing, travel limits, move/cancel/status | Independent feedback reaches the commanded target within tolerance — **a counted pulse total is not position proof** |
| **P3.4** Motion routines | Installed home / move-and-confirm / scan routines, resource locks, cancellation, recovery | Routine scope and limits stay enforced; an interrupted or externally displaced axis loses position validity until re-established |

Keep pulse generation, encoder counting and time-critical limit response on MCU peripherals or a
dedicated controller; the Linux host sends bounded operations. Add generic concepts for axis
identity, frame, units, calibration revision, position validity, homed state, limits and fault state
— a single duration/magnitude limit is not enough, and every motion parameter needs limits across all
three tiers. Decide per parameter which may clamp and which must refuse: **silently moving to a
different target is not universally acceptable**, which is a genuinely different rule from `on_s`.
Extend the §12 link with versioned move/status/cancel/feedback operations and operation ids; never
stream step pulses over a general-purpose link, and keep the board's own duration and travel bounds
independent of the host.

Treat electrical disable, controlled deceleration and mechanical braking as three mechanisms. An axis
that falls when power is removed needs a physical answer, not a manifest string: if richer safe-state
profiles are needed, amend `SAFETY.md` and the protocol deliberately. Hazardous-class work stays
refused until its own per-action authorization design exists.

**P3 exit:** a real Pi and board move an axis, home it, read an independent encoder, enforce narrowed
limits, cancel under load, and recover from an unplug and a reboot without falsely retaining position
or resuming movement — with the six ants unchanged.

### P4 — network devices and printers

**P4.1 Shared substrate first.** One host-side endpoint and credential layer: endpoint identity,
credential references, TLS validation, size limits, timeouts, cancellation, health, bounded backoff
and typed errors. Secrets never appear in mission bodies, evidence or diagnostic exports. A device
profile restricts what a generic adapter may reach — **a signed mission selects a declared
capability; it does not supply a URL, a shell command or an unrestricted vendor command.**
Distinguish three disconnections that imply different recovery: controller unavailable, device
unavailable, adapter unavailable. Discovery is optional, bounded, grants no authority, and installs
nothing.

Order: HTTP/JSON with optional WebSocket events → Moonraker/Klipper → OctoPrint (a second
implementation is what proves the abstraction is real) → MQTT device mapping → Modbus TCP/RTU →
later CAN, BLE, Zigbee/Matter gateways. Two boundaries worth stating once: MQTT's QoS 2 is
exactly-once *message delivery* and **not** exactly-once physical actuation — that still needs
operation identity and reconciliation, and retained actuation commands are replay by another name.
And Modbus specifies operations, not what a given machine's registers mean; protocol support and
device support are separate columns in the compatibility matrix.

**P4.2 Printer capabilities** (proposed ids, not registered ones): `sense.printer.state`,
`act.printer.file.stage`, `act.printer.job.start`, `act.printer.job.pause` / `resume`,
`act.printer.job.cancel`, `sense.printer.job`. The rule throughout: **an HTTP 200 is command
acceptance, not physical work** — a start is confirmed by the matching job becoming active, a cancel
by a terminal state, and a timeout stays unresolved rather than being retried blindly. Use
OctoPrint's explicit pause and resume rather than its toggle, which is unsafe to retry. Slicing,
low-level motion and heater protection stay in the printer's own stack; a raw G-code tunnel is a
separate capability with its own policy, never the default integration and never classed benign to
dodge the ceiling. A printer reporting "complete" is a device-reported claim, not a usable part —
camera inspection is a separate observation with its own predicate.

**P4.3 Long jobs and leases.** Decide offline behaviour before allowing a multi-hour job. `continue`
operates only inside existing authority and `drain` cannot extend it. If a device cannot enforce a
deadline and contact may be lost, the adapter must disclose that loss of control; **a lost connection
is never reported as a confirmed stop.** If "submit a job the machine may finish independently" is
ever wanted, it is an explicit reviewed safety profile — not a quiet reinterpretation of lease expiry.

**P4 exit:** both printer stacks complete a real staged-job lifecycle; a timeout after start does not
duplicate a job; the three disconnection kinds are distinguished; competing activity at the machine's
own UI is detected; cancellation is reconciled; and metadata survives a host restart.

### P5 — cameras, artifacts and optional vision

Snapshots before a video platform, in four steps: **artifact transport** (bounded, content-addressed,
hash/type/size/capture-time/source, retention state; bytes stay out of the signed backlog and the
metadata that refers to them is what gets signed — and metadata acknowledgement is not confirmed
receipt of the image); **capture adapters** (one local path and one network path qualified; a failed
capture is missing evidence, **never an old frame relabelled**); **optional streaming, events and
PTZ** (a capture process with explicit CPU, memory, bandwidth and storage limits); then **optional
inspection**, starting with cheap frame-quality checks.

Starting a capture is observation; moving a PTZ head is actuation with its own bounds and authority.
"Supports ONVIF" is not a feature list — the adapter probes and qualifies the specific features it
exposes. Require maximum dimensions and bytes, frame rate, capture deadline, retention age and upload
retry limits; distinguish source capture time from host receipt time and record clock uncertainty.
**A full image spool or a failed decoder must not starve stops, motion deadlines or sync.**

**P5 exit:** a real local and a real network camera produce fresh, integrity-checkable artifacts; a
reboot or reconnect never mislabels a stale frame; retained bytes stay in budget; and media failure
leaves core control responsive.

### P6 — smart behaviour that stays optional and bounded

**Most of the value here needs no model.** State transitions with freshness and source confidence
(ready, busy, disconnected, faulted, uncertain); debounce, hysteresis, rolling summaries, range and
trend checks; preconditions, postconditions, retry limits, timeouts, event-triggered workflows; and
explanations grounded in the record — which limit clamped, which observation contradicted the goal,
which device disappeared, why a retry is unsafe. **A read retry and a physical retry have different
rules**, and "smart retry" must not become another route to repeated physical work.

Then finish the existing seam: wire `IReasoningProvider` into an explicitly configured coordinator
step with `none` remaining the standard mode, bounded time, context, artifact access, output schema
and cost. A provider must be able to abstain; its proposals are constrained to the offered set,
validated, and rechecked against current authority before anything happens. Text from devices,
filenames and OCR is an untrusted observation, not an instruction. **No model clears a stop, extends
a lease, widens a limit, or certifies its own action.** Evaluate narrow tasks against held-out
observations and record false positives, false negatives and abstentions; reported confidence is not
a reliability measurement.

**P6 exit:** with the reasoner absent, core workflows behave predictably; with it enabled, a tested
interpretation improves a named task without acquiring any control privilege.

### P7 — maintainable and shippable (starts during P0)

**Runtime currency is now urgent: .NET 9 reaches end of support on 10 November 2026.** Plan the
.NET 10 LTS migration now, coordinate the shared protocol and crypto packages with the controller,
and re-run the canonical-byte and C interoperability gates — the upgrade must not move a signed byte.
Triage the open dependency-update PRs on their merits; an open version-bump PR is not by itself
evidence of a vulnerability. Record toolchain support dates and publish a dependency and licence
inventory per release profile.

Ship separate **release profiles** — minimal host, host with selected adapters, MCU port server,
serial mound, Wi-Fi mound — with firmware flash bundles carrying exact bootloader/partition/app
components, hashes and board compatibility. Provision identity per device; **a shared image must never
contain an enrollment secret.** Specify upgrade and downgrade paths, schema migration, and recovery
from an interrupted update, preserving identity, sticky stop, replay history and queued evidence
across it. Ship a reliable manual update and recovery procedure before any unattended OTA, and never
use factory reset as the recovery mechanism.

**Diagnostics:** bounded structured status — active manifest revision, installed adapters and
capabilities, worker and routine state, driver health, active jobs and external ids, authority and
lease state, last verified observation, queue bytes and age, storage losses, clock quality, versions
— distinguishing configured from available. Extend `--describe-drivers` and `--check-hardware` with a
configuration dry-run and a redacted diagnostic export. **A health check may read; it must never
unexpectedly move an axis, home equipment or start a printer**, and it never prints a credential.

**A contributor must be able to add a device without rewriting Micromound:** an adapter template, a
sample profile, a fake endpoint, contract tests (malformed output, stale status, wrong device
identity, unsupported feature, timeout after dispatch, duplicate command, disconnect, stop, bounded
memory) and a release checklist. Publish a compatibility matrix with explicit evidence levels —
**described / simulated / protocol-tested / bench-tested / soak-tested** — which are this project's
own test labels and not an external certification. A tested snapshot path does not imply tested PTZ.

### Resource budgets — make "lightweight" measurable

The three v0.9.27 image sizes (1,026,704 / 296,560 / 260,560 bytes) are build measurements, not
runtime memory and not board reliability. The following are **proposed starting budgets to validate
in P2**, not measurements of the current host; freeze the real ones after measuring, and make any
later change name its cost and its reason.

| Budget | Proposed rule |
|---|---|
| Minimal host resident memory | ≤128 MiB peak for a declared baseline of eight low-rate capabilities, one active job and bounded queues; no media or model process |
| Minimal host idle CPU | ≤2% of one core over ten minutes at a 60 s observation cadence, with the exact platform recorded |
| Host metadata spool | Configurable 64 MiB cap plus a reserved critical-record area; item cap and maximum record size as well |
| Media spool | Separate byte and age caps with an explicit admission policy; absent entirely without the camera package |
| MCU images | Port server ≤384 KiB, serial mound ≤512 KiB, Wi-Fi mound ≤1.25 MiB, subject to the flash layout and update strategy |
| MCU runtime memory | No unbounded core allocation; measure peak heap and stack under TLS, reconnect and full queues; keep ≥25% headroom |
| Deadline behaviour | Per-device maximum hold, stop and limit response agreed and measured; network or media load may not relax it. Report worst case, not only typical |
| Long-run stability | Seven-day mixed-operation soak before any stable support claim: no monotonic memory growth, runaway retries, identity loss or unbounded queue growth |
| Flash and disk wear | Writes, bytes and sync cost per observation, action and sync cycle; batch non-critical metadata while keeping crash ordering for physical intent and results |
| Optional package cost | Download size, loaded memory, idle activity and peak cost published separately; installing a package must not start a worker nobody asked for |

Use size and memory regression checks in CI and physical timing on the bench. Investigate trimming or
Native AOT only if a measurement identifies a benefit. **A container, database, broker, media stack or
local language model must never be required for basic GPIO operation.**

### The stable-release gate, and what it does not include

A stable core release needs: qualified identity, authority and recovery behaviour; full advertised
manifest activation; bounded jobs and storage; reliable stop handling; one supported Pi platform; one
qualified ESP32 profile; supported toolchains; reproducible install, update and rollback; and the
seven-day soak. Anything that has not passed is explicitly experimental or simply absent.

The broad-integration demonstration — the same unchanged core and ants driving a local sensor and
output, an axis with an encoder, a real printer and a real camera through installed packages — is a
separate claim, and it is not made until that exact combination has been exercised.

**Definition of done for any new capability:** it is discoverable through the schema catalog with a
valid example profile; its action class, types, limits, ownership, offline and stop behaviour are
explicit; every operation enters through the kernel; accepted / active / terminal / verified are
distinguishable and missing evidence stays missing; duplicate delivery, timeout-after-dispatch,
restart, disconnect and cancellation have tested outcomes; memory, storage and background work are
bounded and measured; device and firmware compatibility is recorded with real hardware evidence
behind any physical-control claim; and the docs, fixtures and upstream compatibility move with it.

### Deferred, deliberately

SPI peripherals; further ADC, environmental and distance sensors; GPS and lidar; CAN and BLE devices;
serial and USB machine protocols; additional printer vendors; ONVIF event and PTZ variants;
building-system protocols; gateway integrations; richer vision models; multi-axis and mobile
robotics. The architecture's named examples are candidates, not support. Promote one when there is a
concrete target device, a documented protocol, a maintainer and a test plan.

Still deferred on principle: a mandatory local language model, a fleet dashboard inside the edge
daemon, a general scripting runtime with raw hardware or network access, and hazardous-class
execution without its own per-action authorization design. The reduced MCU profile stays reduced —
compiled hardware maps and bounded routines are the constraint, not a gap to be filled.

## What M3 covered

M3 was taken in slices, each a coherent release that preserved all prior behavior. **M3 closed at
`v0.9.1`.** Its closure condition was: the record a mound produces survives and travels correctly —
pinned on the wire, verified only by evidence that followed the act, bounded in storage without
silent loss, and durable across a restart mid-mission — all proven end to end against
`Micromound.Sim`, with no v0 canonical-byte change. The one thing deliberately **left to M4** was the
persistent-disk substrate beneath those semantics (file-backed durable state, and the evidence
store's disk backing); that is a storage-engine change, not a rule change, so it did not hold M3
open. The durable state half of it has since landed at `v0.9.2`, the first M4 slice.

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
  offline for a week now bounds its storage and reports exactly what the gap cost. The disk backing
  followed at `v0.9.12` (`FileEvidenceStore`, at exact policy parity with the in-memory store), so
  this gap is fully closed. **What replaced it is broader, and is now P0.7:** the evidence store's
  item ceiling does not bound evidence already embedded in queued envelopes, and
  `DurableUplinkQueue` is itself unbounded and rewritten whole on every change.
- **Downlink is signature-verified but not hash-chained.** The uplink stream is chained per
  PROTOCOL.md §6; downlink relies on signatures alone, and each side deduplicates by envelope id.
  Deliberate — a controller fans out to many mounds and a per-mound downlink chain buys little —
  but recorded here rather than assumed. **The missing requirement is not a second chain; it is
  durable anti-replay** (P0.4): the deduplication window lives in memory, so a signed mission
  redelivered after a restart is accepted as new and actuates again.
- **A wrong reading confirms an action** (P0.1). `WitnessAnt.Confirm` establishes that a confirming
  observation exists, postdates the action, and passes the freshness gate. It never compares the
  observation's *value* to the state the action was supposed to produce, because no contract
  expresses that expectation yet. Every layer above it is therefore reporting "something looked",
  not "what it saw agrees" — and the eighteen acceptance criteria pass either way. This is the
  largest correctness gap in the repository and the first item of P0.
- **A restart resets the duty cycle** (P0.5). `ActuationHistory` is in-memory only; nothing persists
  or restores it, so `min_off_s` and `max_rate_per_h` — limits a charter grants and the kernel
  enforces — begin empty on every boot.
- **A hold is measured against a pre-sync wall clock** (P0.2). The daemon takes one `UtcNow` per
  tick and the blocking sync runs before `ServiceActuations(now)`, so a slow round trip is time the
  hold never sees; elapsed durations use a clock NTP can step.
- **The manifest is accepted but not activated** (P1.4). `ApplyManifest` applies authority settings
  and nothing else: drivers are not recomposed, declared workers are validated and never
  registered, and the production routine registry is created empty and stays empty. A signed
  manifest that changes a hardware binding is accepted and then ignored.
- ~~**Driver de-energizing on stop and quiesce is the composition root's job**, and only the
  simulator's composition root does it yet.~~ **Closed in M4.** `MoundHost.WatchingForSafeState`
  wraps every sync and mission and drives the drivers safe on the transition into stopped or
  quiesced, exactly as SimMound does; `MoundService.Tick` answers the watchdog on both sides of the
  tick, and since `v0.9.27` checks lease expiry there too. **What remains is P0.8:** the escalation
  path when a driver *blocks* rather than throws, and per-driver isolation on the safe-state walk.

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
happens to agree today. The same idea carries into M5: the C mirror is built and tested on the
host against the fixtures the C# tests froze, so by the time it is flashed the bytes it produces
are already known to be the bytes the controller verifies.
