# Changelog

Every stateful feature ships with a changelog entry — MICROMOUND.md design rule 9. This file
exists to answer one question quickly: *what changed about what this thing is allowed to do, and
when?* Entries are newest first. Versions are the `MicromoundVersion` in `Directory.Build.props`,
which always matches the release tag.

Because this repository governs physical actuation, entries call out separately anything that
**narrows or widens authority**, changes the **canonical wire bytes**, or alters a **refusal
reason**. A device in the field is only as safe as the oldest firmware still talking to it, so a
wire change is never a footnote here.

---

## v0.9.11 — enrollment aligned with the reference controller

The M4 slice that gets a real mound through ANTHILL's front door. Reading both codebases side by side
showed that the signed sync path needs nothing: ANTHILL compiles against `Micromound.Protocol` and
`Micromound.Crypto`, so the envelope, canonical bytes, Ed25519 verification, hash chain, protocol
version, and downlink kinds are the same code on both sides. The only place the two could drift was
the hand-matched enrollment handshake — and it had drifted into a hard blocker. No wire change to any
signed envelope, no new refusal reason, no authority widened.

### Fixed

- **Enrollment against ANTHILL was refused outright.** The daemon declared `tier: "mound_major"`, a
  label that existed only in this repository's simulator; the controller validates the tier against
  `edge_queen` / `deterministic_controller` and refuses anything else. The device now declares
  `edge_queen` by default (`--tier` overrides; a Pi running the full colony IS an edge queen), and the
  vocabulary lives in one shared place both sides compile against — `Micromound.Protocol.ControllerTiers`
  — so it cannot drift again. The simulator's tier constants now read from the same place.

### Added

- **The enroll request says what the device is.** Besides the token and key it now sends its manifest
  `mound_id` (a cross-check against the mound the operator minted the token for — the device signs every
  later uplink with that id, so a mismatch is refused at the door with both names instead of surfacing
  as unexplained signature refusals on every beat), `protocol_version` (explicit, so a skew is refused
  rather than defaulted away), and `capabilities[]` (the structured list the fleet view is built from;
  `hardware_profile` stays for controllers that read only that). Every field name and type matches the
  controller's DTO exactly, verified by round-tripping the client's own body through a replica of it.
- **The enroll response is read in full.** `IEnrollmentClient.TryEnroll` now yields a
  `ControllerEnrollment` — the key plus the controller's `mound_id`, `sync_interval_s`,
  `protocol_version`, and `colony_version`. The device checks the bound `mound_id` against its own
  manifest and the protocol version against its own, and refuses either mismatch itself (a belt for a
  controller that did not cross-check). Every field but the key is optional, so an older controller
  that returns only the key still enrolls.
- **The controller's refusal reason is surfaced.** A 4xx carrying `{accepted:false, reason}` now reports
  the reason — "unknown tier 'mound_major'", "enrollment token expired" — instead of a generic "token
  burned or unknown". It is the operator standing next to the hardware who needs it.
- **The controller's sync cadence is honoured.** The controller judges a mound offline from
  `sync_interval_s` (missed beats × interval), so a mound syncing on its own schedule was being
  mis-judged. The cadence returned at enrollment is the bootstrap — persisted beside the key in an
  additive `controller.meta.json` sidecar (an older state directory with only `controller.pub` still
  loads), because enrollment happens once per token and anything learned then and not persisted would be
  lost on the first reboot — and the **active charter's** `sync_interval_s` takes over as the live
  authority once chartered (`MoundService.EffectiveSyncInterval`). `MoundHost.ResolveControllerLink`
  is the rich form of `ResolveControllerKeys`, which remains as a wrapper.

### Authority / safety

- **The cadence throttles the sync beat ONLY.** It is deliberately not the tick interval: the tick also
  releases elapsed actuation holds, kicks the independent watchdog, and refreshes the heartbeat, and
  those keep `--interval-s`. A controller asking to hear from the mound every 60 s is choosing how often
  it hears from it — it is not asking for a valve's 5 s hold to be released 60 s late. Tested: a 60 s
  cadence with a 5 s hold releases the hold on the next tick while the sync stays throttled.
- **A too-long cadence is self-limiting, so no cap is imposed.** A cadence longer than the lease TTL
  means the mound cannot renew and quiesces — the fail-safe direction. The lease is the bound.
- **The one cross-repo hazard the review caught.** The shared tier constants were first named
  `MoundTiers`; ANTHILL already declares its own `MoundTiers` in a namespace it imports alongside
  `Micromound.Protocol`, so that name would have made every unqualified use on its side ambiguous and
  broken its build the next time it compiled against this repository — exactly the lockstep breakage
  the shared-source arrangement invites. Renamed to `ControllerTiers` (PROTOCOL.md §3's own phrase),
  with the reason recorded on the type so it is not renamed back.

### Notes

- PROTOCOL.md §3 now documents the actual enroll request and response bodies, the refusal shape, the
  tier vocabulary, and the cadence rule. The daemon's usage text and stale header comment were
  refreshed too (it no longer claims to be "running offline until a transport is configured").
- Because ANTHILL source-references `Micromound.Protocol`/`Micromound.Crypto`, the two repositories
  must stay in lockstep on those assemblies; publishing them as pinned packages is the eventual
  decoupling, not part of this slice.

---

## v0.9.10 — the independent watchdog thread

The M4 slice that closes the safety gap v0.9.9 opened. A held actuation keeps a line energized between
ticks, so a service loop that *hangs* could leave a line hot — the soft, loop-driven heartbeat refuses
new actuations but cannot release a line the loop is no longer running to release. This adds a
hardware-independent watchdog on its own thread that notices the loop has stopped and drives the mound
to a de-energized, stopped state without the loop's cooperation. It was flagged in v0.9.9 as a
prerequisite before a mound holds real loads unattended; it now exists. No wire change, no new refusal
reason, no authority widened.

### Added

- **`LoopWatchdog`** (`Micromound.Host`): the pure timing core — `Kick(now)` pushes the deadline
  forward, `CheckUnresponsive(now)` fires ONCE (latched) when the loop has been silent past the
  timeout. Holds no thread and no clock (it is fed the time), so its whole decision is unit-tested
  against a fake clock.
- **`WatchdogThread`** (`Micromound.Host`): the thin wrapper that runs the watchdog on its OWN
  background thread (not the loop's, and not a thread-pool thread a blocked continuation could
  starve), waking on a short cadence to check. `Start` / `Kick` / `Dispose(join)`. The clock is
  injectable so even the thread is tested deterministically.
- **`MoundHost.WatchdogStop(reason)`**: the fire action — a sticky, persisted stop that de-energizes
  every driver and records an auditable trip, run on the watchdog thread.
- **The daemon arms it.** New `--watchdog-s`: the hard timeout; `0` disables, omitted auto-derives a
  generous `max(3×heartbeat, 6×interval)`. The loop kicks after each completed tick and disposes the
  watchdog before a deliberate shutdown (so a clean stop is never mistaken for a hang).

### Authority / safety

- **A held line can no longer stay hot behind a hung loop.** When the loop stops kicking for the whole
  timeout, the watchdog thread de-energizes and stops the mound. The stop is sticky and persisted — a
  loop that had to be rescued by the independent watchdog is not trusted until an operator has looked,
  and a restart never clears it. Set the timeout generously so an ordinary GC or scheduling pause never
  trips it.
- **The concurrency is made correct, not assumed.** `GuardAnt` is now internally thread-safe (the one
  component touched by both the loop and the watchdog thread). The host serialises its safe-state path
  (`EnterSafeState` / `Stop` / `ServiceActuations` / `PersistAuthority` / `WatchdogStop`) behind one
  gate, with a consistent lock order (gate → guard) so there is no deadlock, and the watchdog takes the
  gate with a *bounded* wait so it can never itself wedge. The service loop answers the watchdog at the
  TOP of each tick — reading the trip through the Guard's lock, a memory barrier — so a loop resuming
  from a hang stops *itself* before sync could authorize an actuation on a stale, not-yet-stopped view
  of authority (a real window on the weak-memory ARM target).
- **The one residual gap, named.** If the loop is wedged INSIDE a hardware op holding the safe-state
  gate, the watchdog cannot safely drive the same drivers; it records the trip it can (the Guard is
  independently thread-safe) and logs loudly, leaving process supervision (systemd `Restart=`, whose
  restart de-energizes at configure time) as the documented backstop. Every ordinary hang — sync, a
  stalled delay, a busy loop — leaves the gate free and the mound is driven fully safe.

### Notes

- The watchdog's logic and the cross-thread stop are proven in the sandbox (a fake clock for the
  timing, real threads with an injected clock for the thread, and a real host for `WatchdogStop`,
  including a concurrency stress that runs the gated safe-state ops against the running watchdog with
  no deadlock). The physical de-energize rides the existing driver safe-state path, so its on-hardware
  behavior rests on the GPIO port validated on a real board.
- SAFETY.md's Layer-1 note is updated: the hung-loop gap the timed hold introduced is now closed by
  this thread, with the wedged-in-driver case as the named residual.

---

## v0.9.9 — the digital actuator holds its line for a real duration

The M4 slice that makes an actuation actually last. Until now the generic digital actuator was
*momentary* — it pulsed the line active then immediately safe within one execution, so on real
hardware a valve never opened. It now drives the line active and **holds it for the effective
`on_s`**, releasing it on the service loop's cadence. This is a driver-mechanism change behind an
existing capability: no wire change, no new refusal reason, no authority widened.

### Added

- **`ITimedDriver`** (`Micromound.Drivers`): the clock-driven seam a driver with a time-based
  obligation implements — `ServiceHolds(now)` releases a hold whose duration has elapsed. The host
  services only the drivers that declare it, as it wires only the evidence sources for
  `IEvidenceSource`.
- **`DigitalActuatorDriver` now holds and releases.** An execution drives the line active and records
  a deadline of `started_at + on_s`; `ServiceHolds` releases it once the deadline passes. `IsHolding`
  exposes the state for a health view or a test.
- **`MoundHost.ServiceActuations(now)`** and a **`MoundService.Tick`** step that calls it each tick, so
  every held line is released on the loop's cadence; `Shutdown` already releases via the safe state.

### Authority / safety

- **A held line is bounded on every side.** `on_s` arrives already clamped to the intersected limit
  tiers, and the driver caps it again at the effective `max_on_s` as a last-resort belt, so even a
  contract violation upstream cannot hold a line beyond the hardware ceiling. A non-positive or a
  non-finite-and-unbounded duration is refused, never held.
- **The release is owed on every orderly path.** A stop, quiesce, shutdown, or trip drives the line
  safe and ends the hold immediately; on the normal path the tick sweep releases it within one tick of
  the deadline. A line that will not de-energize keeps its hold pending (the next tick retries) and is
  escalated to a **sticky, persisted stop** — a line that cannot be proven safe is treated as unsafe.
- **The trade this makes, stated plainly.** A timed hold gives up the momentary primitive's
  self-releasing property: the line is deliberately held active between ticks, so its safety now
  depends on the loop continuing to tick. Every orderly path is covered and a stuck line trips, but a
  fully *hung* loop can leave a line energized — the stale-heartbeat rule still refuses new actuations
  but cannot release a line already held (a restart de-energizes at configure time). Closing that gap
  is the **dedicated watchdog thread** (a hardware-independent timer, still to land), which this slice
  elevates from a nicety to a **prerequisite before a mound holds real loads unattended**. Until it
  lands, keep `max_on_s` conservative and the tick interval short. Recorded in SAFETY.md and ROADMAP.

### Notes

- Release granularity is one tick: a hold can run up to one tick interval past `on_s`, so a hard
  hardware bound should carry that margin. The kernel already models the action as spanning `on_s`
  (it infers the end from the duration parameter), so duty-cycle / `min_off_s` accounting is unchanged
  — the hardware now matches that model instead of pulsing.
- The simulator's `SimRelayDriver` stays a simple momentary model for its scripted scenario; the timed
  hold is a real-driver mechanism, proven here by driver, host, and service tests over a fake clock.

---

## v0.9.8 — the first real driver port (Linux GPIO over sysfs)

The M4 slice that gives the generic digital actuator a *real* line to drive: `SysfsDigitalOutput`,
a Linux GPIO output over `/sys/class/gpio`, and a hardware factory that opens it from the manifest's
`pin`. Until now every actuator ran on an in-memory line — the simulator's world. This is the first
port that toggles a real pin. It is **substrate, not the milestone**: the value writes themselves
must still be verified on a physical board, so this is a `v0.9.x` slice, not `v0.10.0`.

### Added

- **`SysfsDigitalOutput`** (`Micromound.Drivers`, implementing `IDigitalOutput`): a GPIO output line
  over the sysfs file protocol — write the pin to `export` to claim it, `out` to its `direction`,
  `1`/`0` to its `value`; `Dispose` writes it to `unexport`. The sysfs root is injectable, so the
  file protocol is exercised against a fake tree with no hardware. An already-exported pin (a prior
  run that did not release it) is reused, not treated as an error. It is polarity-agnostic — it
  writes the logical level it is handed; the driver above owns active-high/low.
- **`SysfsDigitalActuatorFactory`** (`Micromound.Drivers`): builds the generic digital actuator over
  a real GPIO line, reading the line's `pin` from the manifest settings. Same driver kind
  (`digital_actuator`), same capabilities, limits, class, and polarity — **only the port backing
  changes** from the in-memory default. This is the factory a device's registry substitutes for
  `DigitalActuatorFactory` on real hardware.
- **The digital actuator now opens its port at configure time from the manifest**, not at
  construction — a real line needs the `pin` setting, which is only known when the manifest slice is
  applied. `DigitalActuatorFactory` gains a settings-keyed port-builder constructor; the settings-free
  constructors (in-memory default, fixed test line) are unchanged.

### Authority / safety

- **Opening the port is fail-closed and is done last.** A missing or non-integer `pin`, a busy line,
  or no GPIO on the host makes the driver refuse configuration and stay `Absent` — the kernel never
  acts on an unbacked line, and a half-validated slice never opens hardware.
- **The momentary pulse is now fail-safe against a *throwing* port.** An in-memory line never failed
  a write; a real sysfs write can. Both writes of the drive-active-then-release pulse are guarded: if
  the energize fails, nothing was actuated; if the **release** fails — the dangerous case — the driver
  re-attempts a best-effort drive to safe and returns a **fault**, so a physical line is never left
  latched hot with an exception sailing past. (The kernel already turns a thrown executor exception
  into a fault, but catching the exception does not de-energize the line; this does.)
- **Still momentary, by design.** Without a hardware scheduler the pulse drives active then back to
  safe within one execution rather than latching a line hot that only a later `EnterSafeState` could
  clear. On a real board this is a near-instantaneous pulse: **a valve or heater needs the timed-hold
  driver (a later slice) to actually actuate for a duration.** The effective `on_s` is required and
  recorded, never defaulted.
- **No wire change, no new refusal reason, no authority widened.** This is a new hardware backing
  behind an existing seam; canonical bytes, envelopes, and the refusal enum are untouched.

### Notes

- sysfs GPIO is deprecated in favour of the libgpiod character device, and the kernel creates a pin's
  directory *asynchronously* after `export`. This slice keeps the file protocol simple and testable;
  a **libgpiod (chardev) backing, export-settle retries, and the timed-hold driver** are follow-ups,
  and the value writes here **must be verified on real hardware**.
- The analog/ADC input's real port (I2C, chip-specific) is the remaining driver-port work and is a
  separate follow-up. `v0.10.0` stays reserved for the host running on a device against real hardware.

---

## v0.9.7 — device enrollment

The M4 slice that completes the live controller link: a mound presents its one-time, operator-minted
token, receives the controller's public key, and persists it — so it can from then on *verify*
downlink, not just POST uplink (PROTOCOL.md §3). The real Linux driver ports are what remain.

### Added

- **`HttpEnrollmentClient`** (`Micromound.Host`, implementing `IEnrollmentClient`): POSTs the token,
  the device public key, its hardware profile, and tier to `<controller>/micromound/v0/enroll` and
  reads back the controller's public key. A **4xx is a definite refusal** (burned or unknown token —
  no retry); a 5xx or an unreachable controller is transient ("not enrolled yet"); nothing throws.
- **`MoundHost.ResolveControllerKeys`**: loads the controller key from a prior enrollment
  (`<state>/controller.pub`), or enrolls now with a supplied token and persists the key so later
  boots skip enrollment. The daemon gains `--enroll-token`; with `--controller` set it resolves the
  key before bring-up and hands it to the verifier.

### Authority / safety

- **The controller key is validated before it is ever trusted.** A returned key that is the wrong
  length or all-zero is rejected — it would verify nothing yet, once persisted, block downlink
  forever (a permanent brick). Validated both in the client and when loading the stored key.
- **Fail-closed on trust, fail-open on availability.** With no token and no stored key the mound is
  un-enrolled: it still boots and uplinks, but the verifier holds no controller key, so *unverifiable
  downlink is dropped* rather than trusted. An enrollment failure degrades to un-enrolled-but-running,
  never a crash — the enroll step is inside the daemon's fail-closed bring-up, and even a disk write
  failure leaves the mound enrolled in memory for the boot rather than throwing.
- **Recoverable.** A corrupt `controller.pub` with a fresh token clears the bad file and re-enrolls,
  instead of being wedged un-enrolled. The persisted key is flushed to disk so a power cut cannot
  leave an empty file that reads back as a zero key. The token is a one-time secret: it is never
  persisted or logged, a burned token is not re-usable, and — per §3.4 — recovery from a lost enroll
  response (token burned, key never stored) requires an operator to mint a new token; there is no
  self-service re-key.
- **Trust boundary unchanged.** Enrollment only teaches the mound which key to trust; downlink is
  still verified by the Runner exactly as before. **No wire change to signed envelopes; canonical
  bytes unchanged; no refusal reason changed; no authority widened.**

### Notes

- The enroll exchange is a bare JSON POST (not a signed envelope) because it is the bootstrap that
  precedes the controller knowing the device key; the token is what authorizes it.
- Remaining M4: the real **Linux driver ports** (GPIO/ADC over sysfs/libgpiod/I2C/SPI) behind the
  generic primitives. `v0.10.0` is reserved for the host running on a device against real hardware.

---

## v0.9.6 — the daemon dials a controller over HTTPS

The M4 slice that lifts the daemon out of offline-only: a real HTTP sync transport so a mound POSTs
its signed envelopes to the controller and reads back the downlink, per PROTOCOL.md §1. Device
enrollment and real hardware ports are still ahead; this is the pipe they run over.

### Added

- **`HttpSyncTransport`** (`Micromound.Host`) — an `ISyncTransport` that POSTs one signed uplink
  envelope to `<controller>/micromound/v0/sync` (PROTOCOL.md §1, device-initiated) and deserializes
  the response body as the downlink envelopes. It carries envelopes; it does not touch them — the
  same frozen wire JSON goes out with its `sig` intact, so canonical bytes and signatures are
  unchanged, and every downlink still flows through the Runner's existing verification.
- **`micromound --controller <url>`** wires it into the daemon; without the flag the daemon runs
  offline as before.

### Authority / safety

- **HTTPS only** (PROTOCOL.md §1): the daemon rejects a non-`https` `--controller` URL as a usage
  error rather than dialing cleartext or an undialable scheme.
- **Offline is a normal state, never a crash.** An unreachable controller, a timeout, a non-2xx
  status, an unreadable body, or a scheme `HttpClient` cannot dial all return a failed exchange with
  a reason — the durable uplink queue keeps the backlog and re-sends oldest-first. Nothing throws
  into the service loop. A failed-but-delivered exchange advances no ack, so the controller
  deduplicates the resend by sequence number (§2); no envelope is lost or double-processed.
- **Bounded response.** A single downlink response is capped (8 MB on a client the transport owns),
  so a hostile or misconfigured controller cannot OOM a constrained mound; an oversize body is a
  failed exchange. The trust boundary is unchanged: the transport never verifies or acts on downlink
  — an unsigned or unknown-key charter or stop in an HTTP response is still dropped and audited by
  the verifier, exactly as over the in-process link.
- **No wire change. Canonical bytes unchanged. No refusal reason changed. No authority widened.**

### Notes

- `ISyncTransport` carries no cancellation token, so a shutdown signal is observed between exchanges,
  not during one; the per-exchange timeout (default 10 s) is what bounds an in-flight call. Threading
  the shutdown token through the seam is a possible follow-up.
- Remaining M4: device **enrollment** over this transport (so a mound learns the controller's key and
  the controller learns the mound's — PROTOCOL.md §3; until then a live link can POST but cannot
  verify downlink), and the real **Linux driver ports**. `v0.10.0` is reserved for the host running
  on a device against real hardware.

---

## v0.9.5 — a runnable daemon with a safe lifecycle

The M4 slice that turns the composable host into a running service: a heartbeat-and-sync loop, a
watchdog that responds physically, and a graceful, safe shutdown — plus a real `micromound` entry
point that brings a mound up from a manifest and runs it. A real network transport and real hardware
ports are still ahead; this is the lifecycle around them.

### Added

- **`MoundService` — the service lifecycle.** `Tick(now)` marks the runtime alive, runs a sync beat,
  refreshes the watchdog, and responds to it; `Shutdown(now)` drives every actuator to safe state and
  persists authority. It is clock-driven (the caller passes the time) so the loop's safety behaviour
  is deterministic and testable without a real timer.
- **A real daemon entry point.** `micromound --manifest <path> [--state <dir>] [--interval-s n]
  [--heartbeat-s n]` loads the device identity, loads and validates the manifest, brings the mound up
  (`MoundHost`), recovers any interrupted mission, then runs the tick loop until SIGINT/SIGTERM, on
  which it shuts down safely. Fails closed: bad arguments or an unreadable manifest exit non-zero, and
  a mound that cannot come up safely does not come up.
- Watchdog and lifecycle accessors on `MoundHost` (`Guard`, `Beat`, `PollHealth`, `EnterSafeState`,
  `Stop`, `PersistAuthority`), and `IGuardAnt.HasTrip` to distinguish a sticky trip from a self-healing
  stale heartbeat.

### Authority / safety

- **A safety trip survives a reboot.** A sticky trip (interlock, thermal cut-out) lived only in memory;
  a graceful restart would have cleared it and re-enabled actuation. The service now **escalates a trip
  to a persisted stop** — de-energized and durably halted — and a restart never clears a stop, so the
  mound comes back up stopped until the controller intervenes. A stale heartbeat is self-healing and is
  *not* escalated: its protection is the kernel refusing every actuation while the beat is stale.
- **De-energize failures are not silent.** A driver that throws while being made safe is isolated so
  the others still de-energize, but the failure is recorded as a sticky safety trip and written to
  stderr, rather than leaving an output possibly energized with no record (SAFETY.md).
- **The watchdog's safe-state entry rides the wire.** The guard's health readings are now wired to the
  evidence sink, so a mound that forced safe state can prove afterwards why.
- **No wire change. Canonical bytes unchanged. No refusal reason changed. No authority widened.** The
  slice adds a service loop and a daemon entry point over the existing seams.

### Notes

- Within a running loop the watchdog's *physical* response fires on a sticky trip; the stale-heartbeat
  guarantee is the kernel's per-actuation refusal. A dedicated watchdog thread that de-energizes a loop
  that has *hung* mid-tick is deferred to the transport slice, and the code says so rather than
  over-claiming it.
- Remaining M4: a real network transport to the controller (the daemon runs offline until then, the
  durable queue holding the backlog), and the real Linux driver ports (GPIO/ADC) behind the generic
  primitives. `v0.10.0` is reserved for the host running on a device against real hardware.

---

## v0.9.4 — the host composes and runs a mound from a manifest

The M4 slice that ties the substrate together: one shared composition, and a `MoundHost` that brings
a mound up from a manifest over the durable file store and runs it — the same runtime the simulator
proves, now driven by a real manifest and real disk. The daemon's service loop and real hardware
ports are still ahead; this makes the runtime itself composable and runnable.

### Added

- **`MoundComposition.Build` — the one place a mound is wired together.** The `kernel → registries →
  evidence → ants → Mound Major → Runner` composition was factored out of the simulator into the
  runtime, so the simulator and the host compose the *identical* runtime and cannot drift. It takes
  the driver layer's output (capability descriptors and executors) and the crypto as signer/verifier
  interfaces, so it depends on no concrete driver or crypto implementation — the composition root
  supplies those. The report-before-clear and recovery orderings live here too (`RunAndReport`,
  `RecoverAndReport`), shared by both roots so the safety-critical sequence has a single definition.
- **`MoundHost` — the real composition root.** Brings a mound up from a `MoundManifest`: resolves its
  drivers through the `v0.9.3` composer, composes over a `FileStateStore`, applies the manifest's
  authority slice, and runs missions, persists state, and recovers across restarts — the full
  manifest → generic drivers → kernel → ants → mission path, proven end to end over real disk.
  `LoadOrCreateIdentity` persists the device's Ed25519 seed (owner-only, flushed to disk) and reloads
  it across a restart.
- **`IEvidenceSource`** — the small interface the composition wires a driver's readings through, so
  the evidence sink is shared rather than sim-specific.

### Authority / safety

- **Fails closed on bring-up.** An unresolvable driver, a malformed manifest, or a missing `safe_state`
  throws rather than starting a half-configured mound; if bring-up fails after drivers are configured,
  they are driven to safe state before the error propagates, so a failed start never leaves hardware
  energized or half-claimed.
- **Recovery semantics are unchanged and now shared.** The `v0.9.1` no-replay rule and the `v0.9.2`
  report-before-clear ordering are defined once in `MoundComposition` and used by both the simulator
  and the host; the host recovers an interrupted mid-actuation mission from disk exactly as the
  simulator does — reported `failed`, never replayed, checkpoint cleared only after the report is
  durably queued.
- **No wire change. Canonical bytes unchanged. No refusal reason changed. No authority widened.** The
  extraction is behaviour-preserving (the simulator's full suite and the ten-claim smoke run are
  unchanged); `MoundHost` adds a composition root, not a protocol.

### Notes

- The device identity seed is written owner-only (`0600`) and flushed; a boot that loses a
  create race loads the winner's seed rather than clobbering it.
- Remaining M4: the daemon **service loop** (a real network transport to the controller, the sync-beat
  loop, signal-driven graceful shutdown, the timing watchdog) and the **real Linux driver ports**
  (GPIO/ADC) behind the generic primitives, which will also add the `Dispose` seam the primitives
  note. `v0.10.0` is reserved for the boundary where the host runs on a device.

---

## v0.9.3 — a manifest resolves to generic drivers

The second M4 substrate slice: the seam that turns a manifest's hardware section into configured
drivers, plus the first two generic driver primitives. Specialization comes from capabilities and
settings, not from device-specific driver types — a greenhouse and a rover run the same primitives,
configured differently. Still no real hardware ports and no runnable host; this is the resolution
step the host will call.

### Added

- **`IDriverFactory` + `DriverFactoryRegistry`.** A manifest binds a device to a driver-*type* name;
  a factory creates a fresh instance of that type, which is then handed its own settings to configure.
  A manifest naming a type this build does not have fails composition rather than being skipped.
- **`ManifestDriverComposer.Compose`.** Turns a manifest's hardware section into configured drivers,
  **fail-closed as a whole**: an unknown driver, a setting that will not parse, a malformed capability
  id, or two devices resolving to the same driver identity discards the *entire* resolution and
  reports every reason — a mound never comes up half-wired with some hardware silently missing.
- **Two generic driver primitives.** `DigitalActuatorDriver` (a binary `act.` actuator over an
  `IDigitalOutput`) and `AnalogSensorDriver` (a `sense.` sensor over an `IAnalogInput`), configured
  entirely from capabilities and settings. The actuator produces no evidence of its own (a command is
  not evidence) and is momentary and fail-safe — it never latches a line active relying on a later
  safe-state call; the sensor's reading is its evidence. IO sits behind a narrow `IDigitalOutput` /
  `IAnalogInput` seam, backed in-memory here so the primitives' logic is proven with no hardware; real
  Linux GPIO/ADC ports are a later M4 slice.

### Authority / safety

- **Fails closed on every configuration fault, including the ones a bare prefix check would miss.**
  A hardware limit that is non-numeric, negative, or `NaN`/`Infinity` is rejected — a `NaN` in the
  innermost limit tier would otherwise propagate through `Math.Min` and neutralize the device and
  charter tiers layered under it. A capability id must be well-formed, not merely `act.`/`sense.`
  prefixed, so the composer's "valid" means the kernel will actually register it. An unparseable
  `active_high` is refused, because safe-state polarity must be known before a line can be trusted to
  de-energize. A digital actuator may be pinned to `controlled` but never `hazardous` or `observe`.
- **No wire change. Canonical bytes unchanged. No refusal reason changed. No authority widened.**
  This is composition and driver code under the existing `IDriver`/capability seams; the golden
  fixtures are untouched.

### Notes

- Real hardware ports have no `Dispose`/release seam yet, so when they arrive a later bad device in a
  fail-closed resolution should release the ports already opened — tracked for the hardware slice.
- Remaining M4: the runnable `Micromound.Host` daemon (composition + service loop + watchdog), the
  real Linux driver ports behind these primitives, and the evidence store's disk backing. `v0.10.0`
  is reserved for the boundary where the host runs on a device, not these substrate slices.

---

## v0.9.2 — operational state survives on real disk

The first M4 substrate slice: MICROMOUND's durable state finally has a disk backing, so a restart
recovers from a file on disk rather than only from state a test kept in memory. No new capability,
no runnable device host yet — this is the foundation the M4 host will stand on, landed and proven
behind the existing persistence seam.

### Added

- **`FileStateStore` — the durable `IStateStore`.** One file per key under a state directory,
  exactly the "directory of files on a Pi" the `IStateStore` contract describes — no database, no
  schema, the same three operations the in-memory store offers, backed by disk. Each `Put` is
  atomic per key: the value is written to a uniquely named temporary file, flushed, then moved over
  the destination in a single filesystem rename, so a crash never leaves a torn value; orphaned
  temporaries from an interrupted write are swept on open and never read. Keys that carry
  filesystem-reserved characters (`cache:mission`, queue keys) are reversibly percent-encoded to
  safe filenames. A missing key is "absent" (restores to observe-only); a file that exists but
  cannot be read is a real fault and propagates rather than masquerading as absent.

### Changed — durability ordering (the two duties `v0.9.1` deferred to M4)

- **The terminal mission report is now persisted before its checkpoint is cleared.** Previously the
  Mound Major's `Finish` cleared the in-flight checkpoint itself; on a durable store that ordered
  the clear *before* the report was queued, so a crash in between could lose the report. `Finish` no
  longer clears — `ClearMissionCheckpoint()` does, called by whoever publishes the report (the
  Runner on the downlink path, the composition root locally) immediately **after** the report is
  durably queued. A crash between the two now re-reports the mission on the next restart rather than
  losing the record — the audit-record analogue of the `v0.9.1` no-replay rule.
- **A cold start drives actuators to safe state.** When a restart finds an interrupted mission, the
  drivers are de-energized to the declared safe state before recovery proceeds — not only is the
  ambiguous actuation never replayed, the hardware is made safe. (In the simulator the drivers
  de-energize on `EnterSafeState`; the real host will map the checkpoint's `safe_state` to concrete
  driver positions.)

### Authority / safety

- **Narrows behavior, never widens it.** No path here grants authority; the changes only make an
  interrupted mission's outcome more durable and its hardware safer on restart.
- **No wire change. Canonical bytes unchanged. No refusal reason changed.** `FileStateStore` is
  local infrastructure behind the `IStateStore` seam; nothing here touches an envelope, and the v0
  golden fixtures are untouched.

### Notes

- **Terminal mission reports are now at-least-once, idempotent by `mission_id`.** The report-then-
  clear reorder trades a lost-report window for a re-report one: a crash after a report is durably
  queued but before its checkpoint is cleared makes the next restart re-report the mission. The
  upstream contract resolves it — a terminal report is idempotent by `mission_id`, and a `completed`
  report is authoritative over any later recovery (`failed`/interrupted) report for the same mission.
  This is the correct trade for an audit trail (never silently drop a record), and it changes no
  wire bytes.
- The durability of "report before clear" assumes the uplink queue and the checkpoint's cache share
  one durable store; the M4 host must wire both on the same `FileStateStore`.
- Still no runnable device host and no real drivers — the file store is the substrate, not the host.
  Remaining M4: the runnable `Micromound.Host` daemon, manifest-driven real driver primitives,
  service lifecycle, and the watchdog. `v0.10.0` is reserved for that boundary (the host running on
  a device), not these internal substrate slices.

---

## v0.9.1 — a restart never repeats physical work it cannot prove finished

The cleanup-and-hardening slice that closes M3. A mission interrupted by a restart is now durable:
the runtime remembers that physical work was in progress, and refuses to guess its outcome.

### The gap

Restart recovery restored **authority** correctly — a stop was never cleared, a lease was never
extended, expired authority never came back. But it remembered nothing about a *mission in flight*.
A mound that crashed one instruction into opening a water valve came back with no record that a
valve was ever touched. Nothing replayed the step — but nothing reported it either, and the only
reason the actuator did not fire twice is that missions were not resumed across restarts at all.
That is a safe accident, not a decided rule. M3 asks for the rule to be explicit and provable.

### Added

- **A durable mission checkpoint.** The Mound Major now persists a small `cache:mission` record
  the moment a mission begins and clears it the moment the mission finishes. Around any step that
  drives an actuator it follows a strict order — persist *intent* (which step is about to fire)
  → execute the hardware → persist the *result*. A crash in the ambiguous window between execute
  and result leaves the checkpoint marking that step `actuation_in_flight`.
- **A deterministic recovery path.** On restart, after authority is re-evaluated, the Major reads
  the checkpoint and decides once, with no ambiguity:
  - a **stop** in force → the mission stays stopped, the stop is not cleared;
  - authority **did not survive** (quiesced / expired / no charter) → the mission fails, closed;
  - a step was **mid-actuation** → the mission fails as *ambiguous*: its physical outcome cannot
    be proven across a restart and is **never replayed**;
  - interrupted **before** any actuation → the mission fails as interrupted, not resumed;
  - **completed** or **no mission** → nothing to recover, no phantom report.

### Authority / safety

- **Narrows behavior, never widens it.** Every recovery outcome is `failed` or `stopped` — a
  restart can only end an interrupted mission, never silently continue one. This is the fail-closed
  rule from SAFETY.md applied to in-flight physical work: *if MicroMound cannot prove whether an
  actuation occurred, it does not automatically repeat it.*
- **No new wire state.** Recovery reports reuse the existing `MissionReport` vocabulary
  (`failed` / `stopped`); the checkpoint is local cache state (`cache:mission`), never on the wire.
  **Canonical bytes are unchanged.** No refusal reason changed.

### Cleanup

- Removed a dangling `IEvidenceBundler` interface that had no implementation and no caller (a
  loose end noted in the v0.9.0 entry).
- Documentation now describes the **standard six-ant colony configured by capabilities**, not a
  catalogue of device-specific ants (no "Soil Ant" / "Watering Ant" as if they were distinct
  types). Specialized workers remain an explicit, optional extension point. ROADMAP marks **M3
  complete** and states the remaining M4 work concretely.
- Versioning convention recorded: the M3 line continues as patch releases (`v0.9.1`, `v0.9.2`, …);
  `v0.10.0` is reserved for the next real milestone.

---

## v0.9.0 — the store bounds itself and says what it cost

An M3 durability slice: the local evidence store no longer grows without bound when a mound is
disconnected, and it never loses proof silently.

### The gap

`InMemoryEvidenceStore` had one rule under pressure — reclaim acknowledged proof oldest-first past
capacity — and one deliberate hole: when nothing was acknowledged it kept growing, on the correct
principle that silently dropping unacknowledged proof is indistinguishable from never capturing it.
Correct, but not a complete answer: a mound offline for a week grows without limit. And the
accounting that was supposed to make eviction visible was never wired — `IEvidenceBundler` had no
implementation and `TakeEvictedCount()` had no caller, so `evicted_acked_items` rode no bundle.

### Added

- **A hard ceiling with an explicit spill policy.** `InMemoryEvidenceStore` now takes a
  `hardCeiling` (default twice the soft capacity, never below it). Under pressure it reclaims
  acknowledged proof first; unacknowledged proof is still retained past the soft capacity, but past
  the hard ceiling the oldest unacknowledged item **spills** — dropped and counted, never silently.
  A long-disconnected mound bounds its storage and reports exactly what the gap cost.
- **`spilled_unacked_items` on the evidence bundle**, a sibling of `evicted_acked_items`: an evicted
  item was delivered and acknowledged, a spilled one was not. Both counts are now actually attached
  to emitted bundles at the composition root (closing the never-wired accounting), each reported
  once and then reset.

### Wire

**Additive, not a break.** `spilled_unacked_items` is a new field on the `evidence_bundle` body
(default `0`). Per PROTOCOL.md §11 additive fields are always legal while v0 is fluid, and no
firmware has shipped. The frozen bodies otherwise stand: `charter`, `action_record`, `mission`,
`mission_report`, and every existing field of `evidence_bundle` are byte-for-byte unchanged. The
golden fixtures were regenerated — the `evidence_bundle` body gains the one field, and because it
sits mid-chain the canonical-envelope fixture re-hashes from that envelope onward; the frozen
`mound_sync` and `action_record` envelopes before it are untouched, and the chain still links.

### Tests

Spill drops oldest-unacknowledged first and counts it; acknowledged proof is always reclaimed
before any unacknowledged spill; the spill count rides one bundle then resets; and the default
ceiling still retains a small unacknowledged backlog without spilling (the prior never-evict
guarantee, now bounded rather than infinite). Goldens regenerated and verified through the real
serializer; the smoke run's ten enforcement claims and the end-to-end mission are unaffected.

## v0.8.0 — a reading from before the act is not evidence of it

An M3 evidence-correlation slice, and a real verification change: a confirming reading is now
required to come from *after* the action it confirms, not merely to be fresh.

### The gap

"Commands are not evidence" has a mirror the code never enforced. The evidence gate checked that a
confirming reading resolved, parsed, was not stale, and was not from the future — but not that it
was captured *after* the action it was meant to confirm. So a reading carrying the right tag but
taken before the act, or one reordered by a few seconds of clock skew, could confirm an effect
that had not yet happened. The Witness was already careful never to let a mound nominate its own
corroboration — the correlator resolves only the refs a record cites — but a *cited* reading from
before the act still counted, and a reading from before the command is no more evidence of its
effect than the command itself.

### Authority

- **A confirming reading must come from at or after the moment the action began.** The Witness now
  filters confirming observations by time: only those captured at or after the confirmed action's
  `started_at` can be part of the proof. If none qualifies, the action degrades to `unverified`
  with a reason that says the confirmation predates the act. A pre-act reading is dropped from the
  action's evidence refs entirely, so a controller re-running the gate over the synced record
  reaches the same verdict — the demotion travels with the record, it is not a private judgement.
- The reference is when the action *began*, not when it ended: the synchronous runtime walks a
  mission on one clock, so the confirming reading is stamped the same second the action started,
  and that boundary must count as valid. An action with no parseable timestamp imposes no ordering
  it cannot justify and falls back to the gate's existing freshness rules.

### Not the wire

No canonical bytes change and no golden fixture moves — this is a rule about which evidence the
Witness will *accept* as confirmation, evaluated on the mound. The frozen v0 bodies, including the
`mission`/`mission_report` pins from v0.7.0, are untouched. What changes is the verdict a mound may
reach, and therefore what a `mission_report` truthfully says: an action confirmed only by a stale
or reordered reading now reads `unverified` rather than `succeeded`.

### Tests

Five new cases on the Witness: a reading from before the act cannot confirm it; a reading after
the act does; a reading at the exact instant the act began still does (the boundary the runtime
actually produces); among mixed readings only the ones after the act become proof; and an action
with no parseable time imposes no ordering. The end-to-end watering mission still verifies, since
its confirming reading is taken the same second the valve opened.

### Roadmap

`docs/ROADMAP.md` reconciled against the generic-physical-mound target: the status table now marks
M3 in progress (with the `v0.7.0`/`v0.8.0` slices) and M4 as next; a "Reading this roadmap" section
answers what is complete, in progress, needed before hardware moves, next, when the mound is
physically usable, and what stays out of scope; and a "Generic Physical Mound" acceptance target
records the minimal real-hardware bench and the end-to-end sequence that marks the line between a
software architecture and a functional physical edge colony. No milestone was renumbered and no
completed work was dropped.

## v0.7.0 — both ends agree on the mission

The first slice of M3, and a pure wire-hardening one: no authority changes, no new refusal
reasons, no behavior on the device changes at all. It closes a gap the roadmap had recorded since
M2 — the two bodies nothing checked.

### The gap

The golden fixtures froze `charter`, `action_record`, and `evidence_bundle`, and the M5 C mirror
will be verified byte-for-byte against them. `mission` and `mission_report` were not among them. A
constrained controller never decodes a mission — §8 keeps it out of the reduced profile — which is
why the omission was reasonable at the time. But a Pi-class mound and a full controller both encode
*and* decode them, and nothing anywhere checked that the two implementations agree on a single
byte. A field order or a default-emission difference between them would surface as a broken chain
on the first real mission, in the field, with a device on the other end.

### Added

- **`mission` and `mission_report` in both golden fixtures.** They join the bare-body freeze
  (field order, naming, default emission) and the canonical-envelope chain, where a `mission_report`
  (uplink) and a `mission` (downlink) now extend the pinned chain and so pin their `prev_digest`
  linkage as well as their bytes. The frozen v0 bodies were untouched — the change is purely
  additive, exactly as a §11 additive-field change must be.
- **A decode-and-re-encode round trip for both bodies.** `The_mission_and_report_survive_a_decode_
  and_re_encode_unchanged` states the cross-implementation contract directly: serialize, parse,
  re-serialize, and the bytes are identical. Paired with a digest-preservation round trip for
  `mission_report`, since it is uplink and therefore chains — a shifted byte there would break the
  chain at exactly that envelope.

### Wire

No change to any existing canonical bytes. The v0 bytes frozen at `v0.2.1` are untouched; `mission`
and `mission_report` were already on the wire and already serialized this way — this release only
*pins* what they were, so a future change to them becomes a version bump caught by a red fixture
rather than a silent divergence. PROTOCOL.md §11 records the expanded fixture set.

### Not yet

The rest of M3 is still ahead: deeper evidence correlation across a mission's window, durable
in-flight mission state so a restart mid-mission resumes coherently, and the sync hardening that
goes with them. This release deliberately does none of it — it makes the record the controller
already receives one the two implementations can be proven to agree on, and stops there.

## v0.6.0 — the record leaves the mound

M2 complete: the two ants that act on the record rather than on the mission, the durable queue
between them, and the simulator rebuilt into the full composition — proven end to end against a
controller that verifies every byte.

### The gap

A mound could act, prove, and judge — and then everything it knew lived in one process's memory
and went nowhere. `ICacheAnt` and `IRunnerAnt` were declared and implemented by nothing;
`IUplinkQueue` and `IStateStore` had no implementations; the simulator held its own private
envelope chain instead of the runtime's; and no test anywhere ran both ends of the wire. The
protocol described a conversation, and the repository could only speak half of it.

### Added

- **`DurableUplinkQueue`** (`Micromound.Sync`) — the queue owns the chain. `Enqueue` refuses, by
  throwing, an envelope that skips a sequence number, anchors to the wrong digest, or is unsigned:
  a forked uplink chain is a programming error on the device, not wire input to tolerate.
  PROTOCOL.md §6 makes gaps *detectable*; this is why they never need to be detected. Two
  watermarks move independently — the chain head advances on every enqueue and never retreats;
  the ack watermark governs retention, and until it covers a sequence number the envelope is
  retained and re-sent. Every mutation persists through `IStateStore`, so a power cut between
  enqueue and drain loses nothing.

- **`IStateStore` / `InMemoryStateStore`** (`Micromound.Sync`) — the persistence seam: a
  string-keyed document store the M4 host will back with files and the M5 firmware with flash.
  Defined next to its first consumer, wrapped by the Cache Ant above.

- **`CacheAnt`** — operational persistence and the restart path. `SaveAuthority` snapshots the
  charter, lease expiry, stop and quiesce flags after anything that changes them;
  `TryRestoreAuthority` rehydrates through the new `KernelAuthority.Restore`.

- **`KernelAuthority.Restore`** — the one place authority enters the kernel without a controller
  signing it just now, so every rule resolves downward (see Authority below).

- **`RunnerAnt`** — the only envelope factory on the mound: sequence and anchor come from the
  queue, the signature from the device key, and no path produces an envelope outside the chain.
  `Sync` queues the beat, drains the backlog oldest-first, handles acks inline (the drain's
  progress depends on them), and defers everything else until the drain settles so that ordering
  is by kind, not by arrival. Downlink is verified against the controller key before anything is
  processed; what fails is dropped and audited and — deliberately — never acknowledged. Unknown
  kinds, and known kinds that are not downlink, get an `ack` with `refused_unknown_kind`.

- **`AckBody`** (`Micromound.Protocol`) — the typed acknowledgement: cumulative `through_seq`
  (a controller acking a week-long backlog must not enumerate it), received `evidence_ids`
  (what unlocks eviction on the device), and a closed status set.

- **`SimSensorDriver` / `SimRelayDriver`** — fake hardware behind the real `IDriver` seam, with
  hardware limits compiled into the driver where a real GPIO relay driver declares them. The
  relay's `OnActuated` hook is the fake physics: a harness makes watering raise soil moisture.

- **`SimController` / `SimLink`** — the other end of the wire as a test double. Binds keys at
  enrollment, signs all downlink, verifies uplink signatures AND the chain, deduplicates by
  sequence, acknowledges cumulatively, never dials the mound. Enrollment is idempotent because
  reconnection is not re-enrollment — resetting the chain anchor on reconnect would turn a
  faithfully preserved backlog into a wall of refusals.

- **`SimMound` rebuilt** as the composition a Pi will run: drivers → registries → kernel → the
  six ants → Mound Major → Runner, over one `IStateStore`. Same public surface; every envelope
  now flows through the runtime's own queue instead of a simulator-private chain.

- **24 new tests**, including end-to-end: the documented watering mission assigned over the wire
  and verified at the controller; offline continuation and reconnect with the chain intact across
  a restart; a stop and a mission in the same batch; tampered uplink refused per-envelope and
  tampered downlink refused at the mound; ack-driven evidence eviction; lease renewal by beat and
  decay by silence.

### Authority

- **A restart never clears a stop.** A stopped snapshot restores to stopped, whatever else it
  carried. Power-cycling a mound is not a way around an operator's stop order.
- **A restart never extends a lease.** The restored expiry is the saved value — there is no path
  through `Restore` that touches the TTL. A saved expiry already in the past comes back quiesced,
  exactly as if the process had stayed up.
- **A charter that no longer validates restores nothing.** Re-validated against what the device
  has *now*; failure means observe-only, with the reasons reported. Restore over live authority is
  refused outright.
- **Renewal is the acknowledged beat, not the successful send.** `RenewLease` moved onto
  `IMoundMajor` so the Runner — the component that hears the acknowledgement — can report it, and
  it fires only when the controller's ack covers the beat's own sequence number. A transport that
  returns true is not a controller that said yes.
- **Stops are processed ahead of all other downlink in the same exchange** — PROTOCOL.md §7, now
  enforced by ordering rather than described: a batch carrying both a mission and a stop executes
  the stop and the mission runs into it, wherever each sat in the batch.

### Changed

- Mission-produced action records now leave the coordinator only after the walk completes, because
  a `verify` step can demote an earlier record — a record published at dispatch time would go up
  claiming a success its own mission later withdrew.
- The controller side of every exchange is idempotent by sequence number: re-delivery of an
  acknowledged envelope re-acks and processes nothing, because the ack, not the delivery, may be
  the thing that was lost.

### Wire

`AckBody` gives the existing `ack` kind a typed body — additive; no golden fixture pins an ack.
The v0 canonical bytes frozen at `v0.2.1` are untouched. Downlink remains signature-verified but
not hash-chained (only the uplink stream chains, §6); each side deduplicates by envelope id or
sequence. Recorded in `ROADMAP.md` as a decision rather than an assumption.

### Fixed in review, before release

Three findings from this release's adversarial review, each fixed with a pinning test or guard:

- **A restart dropped the manifest tier.** The authority snapshot carried charter and lease but
  not the operator's `device_limits`, so a power cycle restored hardware ∩ charter instead of
  hardware ∩ device ∩ charter — the one way a reboot could quietly widen what a mound may do.
  The snapshot now carries the device limits and the manifest safe state, and `Restore` applies
  them before any branch, the stop branch included.
- **A stop received over the wire flipped the flag without de-energizing the drivers.** The
  Runner reaches only the coordinator, and the drivers belong to the composition root — so the
  composition root now watches for the stopped/quiesced transition around every sync and mission
  and enters driver safe state on it, wherever the stop came from. The M4 host must do the same
  around its own loop; `ROADMAP.md` records it as a requirement.
- **Two copies of one downlink envelope inside a single exchange would both execute.** The
  receive-time dedupe only caught re-delivery across syncs. `HandleDeferred` now claims each
  envelope id exactly once, so a controller whose ack was lost mid-exchange cannot make one
  mission run twice.

Also from review: downlink is now checked against this mound's id (a misrouted stop must surface
as an audit line, not as obedience); refused charters and configs ack with status `refused`
rather than a success ack whose refusal lived only in free text; `Peek` returns copies so a
tampering transport corrupts its own view, never the device's durable record; and the sim
controller no longer lets a later caller silently re-key an enrolled mound.

### Not yet

`IStateStore` has no disk backing — that is the M4 host's first job, and until then "durable"
means "survives a process swap sharing the store", which is what the tests exercise. Evidence
storage still exceeds its bound rather than dropping unacknowledged proof; with acks now flowing
the window is bounded by connectivity, and the spill policy lands with real storage.

## v0.5.0 — the second sense finally does something

M2 continued: the Witness Ant, and the half of the default workflow that could not affect anything.

### The gap

`ARCHITECTURE.md` and `MICROMOUND.md` have both carried this since the first commit, about
`SENSE → ACT → SENSE AGAIN → VERIFY`:

> The second sense is not redundancy. It is the entire reason the mound can claim anything
> happened: the first reading justifies the action, the second is independent evidence of its
> effect, and without it the outcome is `unverified` no matter what the driver returned.

`EvidenceGate.Gate` was called in exactly one place in the repository — inside
`CapabilityKernel.Execute`, at the moment of execution — and nothing ever revisited an action
record afterwards. The confirming reading arrived after the verdict was final and could not change
it. `MissionStepOps.Verify` appeared in exactly two places in the entire codebase: one validator
case shared with `sense`, and one test fixture. **A `verify` step did nothing a `sense` step did
not.**

### Added

- **`MissionStep.confirms`** — the earlier step whose action a `verify` step confirms. This is the
  link the doc's sentence needs and never had. Naming the step explicitly, rather than inferring
  the pairing from capability names, keeps missions the deterministic packets §9 says they are:
  one source, named, no matching rules to learn.

  Legal only on a `verify` step; must name a step that runs first; that step's op must be `act` or
  `routine`, because confirming an observation is not confirmation of anything.

- **`WitnessAnt`** — correlates an action with the observation offered as proof and returns the
  outcome the action is entitled to. Distinct from any upstream Verifier on purpose: a controller's
  Verifier judges whether a mission succeeded, and this judges whether a valve actually opened.

- **`InMemoryEvidenceStore` and `EvidenceCorrelator`** (`Micromound.Evidence`) — retention with the
  one rule that overrides capacity, and ref resolution.

### The rules, and why each is that way

- **Confirmation can only lower a verdict.** That is a property of the evidence gate rather than a
  rule the Witness applies: the gate returns the record's own outcome unless that outcome asserts
  physical work, so nothing can talk an `unverified` action back into having succeeded. A reading
  taken afterwards proves the state of the world afterwards; it does not prove the command caused
  it.
- **A refused or stopped action needs no confirmation.** It is a definite result, not a claim about
  the physical world, and demanding proof of an action that never happened would invent a failure
  out of a correctly reported no.
- **The confirmed step stays `executed`; the action and the mission degrade.** The step ran and ran
  correctly. What changed is what the mound may *claim* about its effect, and marking the step
  failed would misattribute the problem to the actuation.
- **The mound does not nominate its own corroboration.** The correlator resolves only the refs a
  record actually carries. Evidence becomes an action's evidence in exactly two ways — an executor
  produced it during the work, or a mission linked it with `confirms` — and both are somebody
  else's decision, made before the outcome was known. A correlator that swept up nearby readings
  would make "commands are not evidence" mean very little.
- **Confirming refs are added to the action's own `evidence_refs`**, so a controller re-running the
  gate over the synced record reaches the same verdict the mound did. A private judgement that did
  not survive the wire would be worth nothing upstream.
- **Unacknowledged proof is never evicted.** Under pressure the oldest *acknowledged* items go, and
  how many is reported as `evicted_acked_items`. When nothing is acknowledged the store exceeds its
  bound rather than dropping proof the controller has never seen.

### Wire

`confirms` is an additive field on `MissionStep`. The golden fixtures pin `charter`,
`action_record` and `evidence_bundle` — no mission body is pinned — so the v0 canonical bytes
frozen at `v0.2.1` are untouched. That absence is itself now recorded as a known gap in
`ROADMAP.md`: a constrained controller never decodes a mission, but a Pi-class mound and a
controller both encode them and nothing checks that they agree.

### Not yet

Cache and Runner remain interfaces — operational persistence and transport, both about what
happens to a record after the mission. No simulated drivers, so a mission still runs against
registered executors rather than `IDriver` implementations.

## v0.4.0 — the watchdog SAFETY.md always promised

M2, first half: the three ants a mission passes *through* while it runs.

### Fixed — authority, and this one is the point of the release

- **A stop no longer blinds the mound.** PROTOCOL.md §7 has always specified the effect of a stop
  as "cease actuation now, enter `safe_state`, keep sensing and syncing", and the same section
  requires the stop acknowledgement to carry a post-stop sensor snapshot. The capability kernel
  refused *every* capability under a stop — so the protocol mandated an artifact the
  implementation made impossible, and an operator lost their instruments at the exact moment they
  most needed to see what the hardware was doing.

  The kernel now refuses actuation under a stop and permits observation. It decides this from the
  capability id's **namespace**, before the registry is consulted, so stop is still genuinely
  first: `act.nonexistent` under a stop is still refused as `stopped` rather than as
  `unknown_capability`, which is the property that makes stop work when the registry, the charter
  and the drivers are all broken.

  This widens what a stopped mound may do, so it is recorded loudly and `SAFETY.md` — which wins
  over every other document — was amended in the same change rather than after it.

### Added

- **`GuardAnt`** — the software watchdog SAFETY.md Layer 1 has promised since the first commit and
  which nothing implemented. `IGuardAnt.SafeStateRequired` was declared and read by no code
  anywhere in the repository.

  A stale heartbeat or an observed safety trip makes it demand a safe state; the coordinator polls
  it before every actuating step and engages the stop rather than continuing. The two triggers
  behave differently on purpose: **a stale heartbeat is self-healing**, because a watchdog that
  latched on a scheduling hiccup is one nobody leaves enabled, and a disabled watchdog protects
  nothing. **A reported trip is sticky, and there is no method that clears one** — SAFETY.md Layer
  0 says a Guard Ant reports an interlock trip and does not clear one, and the way to guarantee
  that is to give it nowhere to enter.

  Health is reported as evidence rather than as a log line: a mound that entered its safe state
  has to be able to prove afterwards why it did, and "it just stopped" is the silent kind of
  failure SAFETY.md forbids.

- **`ScoutAnt` and `ForagerAnt`** — each stamps its **own** declared ceiling onto every request it
  submits. A ceiling supplied by the caller is discarded; otherwise a worker's declared limit
  would be advice rather than a limit, and the first caller in a hurry would route around it. A
  Scout declared `observe` therefore cannot actuate under a `benign` charter — and the refusal
  comes from the kernel naming the class, not from the ant quietly declining. One decider.

  The Forager holds no driver and no executor, and there is no field through which one could be
  supplied: its constructor takes the kernel, and the kernel is the only thing that owns executors.

- **Coordinator dispatch through the ants.** A `sense`/`verify` step runs on the Scout, an
  `act`/`routine` step on the Forager. A mission may name its worker; if that worker is registered
  but is not the right kind of ant — an application ant declared in a manifest with no code behind
  it yet — the coordinator submits directly under that worker's ceiling rather than substituting a
  default ant, because substituting would apply a ceiling the mission never asked for. **With no
  ants registered the mound still works**, submitting to the kernel directly, which is why every
  v0.3.0 mission test passes unchanged.

### Changed

- `IScoutAnt.Sense` now takes a `CapabilityRequest` and returns an `ActionRecord`, matching
  `IForagerAnt.Request`. A reading is an action the mound took and has to account for; one shape
  means the coordinator has one place that turns a record into a step result rather than two.
- `IGuardAnt` gained `Reason`. Part of the interface rather than an implementation detail because
  SAFETY.md is explicit that "a refusal without a reason is itself a contract violation".

### Wire

No change. No new envelope kind, no new field, no change to canonical bytes.

### Not yet

Witness, Cache and Runner remain interfaces; they act on the record rather than on the mission and
land with evidence correlation and transport. No simulated drivers yet, so a mission still runs
against registered executors rather than against `IDriver` implementations. **Physically
de-energizing hardware needs drivers and arrives in M4** — until then "enters the safe state" is
enforced by refusing every actuation, which is the half of it this layer can guarantee on its own,
and the changelog should say so rather than implying a relay opens.

## v0.3.0 — the Mound Major walks a mission

**M1 is done.** The kernel decided, the contracts described, and nothing walked a mission from one
end to the other. It does now.

### Added

- **`MoundMajor`** (`Micromound.Runtime`) — the local coordinator, implementing `IMoundMajor`.
  Charter acceptance with advisory widening notes, manifest application that fails closed, and the
  mission state machine: ordered steps, deterministic conditions, dispatch to the capability
  kernel, evidence resolution, and a structured `mission_report`.

  It decides nothing about authority. It holds no executor, no driver, and no route to one; every
  actuation goes through the kernel, which is the only thing asked. What it owns is *order* and
  *evidence*.

- **`EvidenceReading`** (`Micromound.Protocol`) — the documented numeric shape inside
  `payload_json`: `{"value":17.0,"unit":"percent","capability":"sense.soil_moisture"}`.

  This was the missing link. `StepCondition` compares an earlier step's reading against a constant
  and `MissionStepResult.value` reports one, but `payload_json` was opaque everywhere — so both
  contracts were written in terms of a number nothing could produce or read, and a mission could
  be validated and never executed. Strict out, tolerant in: any payload carrying a numeric `value`
  is accepted, whatever else it holds.

  **Not a wire change.** `payload_json` already existed and is already inside the canonical bytes
  as a string; giving its contents a documented shape adds a convention. The v0 fixtures frozen at
  `v0.2.1` are byte-identical.

### Three rules worth stating, because each cost a decision

- **Refused whole, never partially run.** Validation happens before any step. There is no
  compensating action for a valve that opened.
- **After a halting step the mission stops acting but keeps looking.** No later step actuates —
  its premise is gone — but later `sense`, `verify` and `report` steps still run, because a
  reading of where the physical world was actually left is the most valuable thing a partial
  mission can return. Halting outright would discard exactly what an operator most needs.
- **The verdict names the first thing that went wrong**, not the worst label in the report. Steps
  suppressed after a halt report `refused` because they were never attempted; counting those would
  let a suppression label outrank the real cause, and a hardware fault would be reported as an
  authority refusal — sending someone to read the charter instead of the relay.

A step whose condition did not hold is `skipped` and its promised evidence was never due. A
mission that correctly declines to water wet soil is `completed`, not `unverified` — the tests
caught that one, and grading it otherwise would teach an operator to ignore the single outcome
that has to keep meaning something.

### Authority — narrowed

The three validation gaps recorded in `v0.2.1` as belonging to M1 are closed:

- A `sense` or `verify` step's capability must be in the `sense.` namespace, and an `act` step's
  in `act.`. A step that reads an actuator is refused at validation, where the mistake can be
  named, rather than later by a worker-ceiling refusal that describes something else.
- `mission.safe_state` may only restate the charter's. Two documents disagreeing about where the
  hardware goes when the watchdog trips is a contradiction nobody can resolve at the moment it
  matters.
- `WorkerDefinition.runtime_type` is a closed set (`deterministic`, `algorithmic`, `sensor`,
  `actuator`, `reasoning`), and `exposes` must name capabilities the mound declares.

Two of the recorded items turned out to contain no question: `mission.worker` is a runtime
concern — an unrecognised name resolves to *no* worker ceiling rather than an invented one, which
is the answer — and `required_evidence` holds free-form tags whose only meaningful check is
whether a step actually produced them, which is execution's job and now `MoundMajor`'s.

### Wire

No change to canonical bytes. `payload_json`'s `reading` shape and the mission execution semantics
are documented in `PROTOCOL.md` §6 and §9.

### Tests

208 → 214 by count of cases; 22 of the new ones are `MissionTests`, which runs the
`ARCHITECTURE.md` "Structured work" example as an executable packet and then bends one thing about
it at a time: wet soil, an unreadable sensor, a spent duty cycle, a driver fault, a dead witness,
a worker ceiling, a stop, an expired lease.

### Not in M1

The six ants are interfaces here and services in M2. Nothing implements `IScoutAnt`, `ICacheAnt`
or `IRunnerAnt`, so a mission runs against registered executors rather than workers with
lifecycles. No persistence backend, no transport, no real driver. A mound cannot yet be left alone
with a plant.

## v0.2.4 — the simulator has to still enforce

Tooling and docs. No `src/`, no wire change.

### Added

- **CI, at last** — landed in `a16b7c2`, unversioned and alone, because a workflow is the one
  thing `dotnet test` structurally cannot validate and a red run should have exactly one candidate
  cause. `ci.yml` runs `scripts/validate.sh --full` on Linux — deliberately the same command a
  developer runs, so green-local and green-CI can never check different things — plus a Windows
  build-and-test leg, a canonical-docs presence check, and safety guards pinning SAFETY.md
  invariants to concrete lines of code and tests. `release.yml` verifies a pushed tag against
  `MicromoundVersion`, rebuilds and retests *from the tag*, and publishes self-contained
  `linux-x64` / `linux-arm64` / `win-x64` binaries. `codeql.yml` scans on PRs and weekly;
  Dependabot watches NuGet and Action pins.

  v0.2.2's changelog said "every green this project has ever had came from one Windows machine."
  That is no longer true. All six jobs passed on their first run.

- **`scripts/validate.* --full` now asserts what the simulator did, not merely that it ran.** Ten
  literal claims over its output: refusal without a charter, the widening attempt reported rather
  than silently intersected away, the clamp, the duty-cycle refusal, `unverified` on a dead
  sensor, lease expiry into `quiesced`, refusal after expiry, the verified backlog, and the
  impostor key refused.

  `dotnet run` exits 0 whether or not the mound still refuses anything, so the previous
  exit-code-only check would have stayed green through a regression where clamping quietly stopped
  clamping. Both mutations were tried against the real output before this shipped; each trips
  exactly one claim.

  The assertions live in the script rather than in `ci.yml` on purpose. `ci-train` originally had
  them as a `sim-smoke` job and the rebase dropped them; in the script, CI and a developer get
  them from one source and cannot drift. `grep -F` on one side and `String.Contains` on the other,
  so both sides assert literally and identically.

### Corrected

- v0.2.3's entry said the incoming CI "compares all three of props, README and CHANGELOG". The CI
  that actually landed does not re-implement that — its comment says so explicitly, deferring to
  `validate.sh` Guard 2. The `**Current version:**` marker is enforced locally only. The marker is
  still worth having; the claim about who checks it was wrong when written.

## v0.2.3 — one owner for the release

Tooling and docs only. No `src/`, no tests, no wire change.

Groundwork for the CI and release workflows landing next, done first and separately so that when
those workflows arrive they are the only change on their own push — the one thing `dotnet test`
structurally cannot validate should never share a push with something it could.

### Changed

- **`scripts/release.*` no longer creates the GitHub Release when a release workflow exists.**
  `.github/workflows/release.yml` re-verifies the tag against `MicromoundVersion`, rebuilds and
  retests *from the tag*, publishes `linux-x64` / `linux-arm64` / `win-x64` binaries, and opens a
  **draft** for a human to read and publish. These scripts cannot attach binaries, so they must
  not create the release first: two owners is a race, and the loser is whichever one was carrying
  the artifacts. The scripts now tag, push, and point at the run.

  The check is `if the workflow file exists`, which is deliberate rather than a flag. It is what
  makes this release possible at all — the workflow does not exist yet, so `v0.2.3` is still cut
  by the script, and the handover happens by itself the moment the workflow lands. Nothing is
  released twice and nothing goes unreleased in between.

- **Draft, not published.** Worth stating as a choice rather than a default inherited from
  ANTHILL: a release is the project speaking in its own voice, and unattended automation should
  not get to do that. The workflow assembles everything and stops.

### Added

- **`**Current version:**` marker in `README.md`,** and `scripts/validate.*` now fails when it
  disagrees with `Directory.Build.props`. The incoming CI compares all three of props, README and
  CHANGELOG; checking it locally means a forgotten bump fails on the machine that can fix it in a
  second, rather than on a runner ten minutes later. Same reason the changelog check already lives
  there.

### Note on what is coming

`origin/ci-train` carries the workflows this release prepares for, but it is rooted on `v0.1.0`
and against current main it is `+1256 / −7267` — merging it would delete `Micromound.Capabilities`,
`Micromound.Runtime`, `Micromound.Sync`, every kernel test, and both v0.2.1 validation suites. The
`.github/**` files get lifted out of it onto current main; the branch itself is not merged. Four of
its fourteen safety guards already point at things that moved in v0.2.0 and will be repointed on
the way in.

## v0.2.2 — the release script releases

Tooling only. No `src/`, no tests, no wire change.

`v0.2.1` was tagged by hand because `scripts/release.ps1` crashed before it reached its own
confirmation prompt. Both halves of the failure were in the PowerShell layer rather than in the
release logic, and both are the kind that only appear on a real run:

### Fixed

- **`$ErrorActionPreference = "Stop"` plus a native command is a trap.** Windows PowerShell turns
  anything a native command writes to stderr into an `ErrorRecord`, and under `Stop` that
  terminates the script — `2>$null` redirects the stream, not the record. `git rev-parse v0.2.1`
  on a tag that does not exist yet writes to stderr *as its way of saying "no such tag"*, so the
  script died on the good news. Both `.ps1` files now run under `Continue` and check
  `$LASTEXITCODE` explicitly, with `-ErrorAction Stop` on the cmdlets that genuinely must not
  fail. The same trap was live in `validate.ps1`: one NuGet warning on stderr from a *passing*
  build would have aborted validation and read exactly like a test failure.
- **Tag existence is now read from output, not from an exit code.** `git tag --list` and
  `git ls-remote --tags` print the match or nothing and always succeed, so there is no stderr for
  PowerShell to trip over.
- **An unreachable remote no longer reads as "the tag is free".** `git ls-remote` prints nothing
  both when a tag does not exist and when it cannot reach the remote at all, and only the exit
  status tells the two apart. The first version of this check looked at output alone, so a network
  failure would have been reported as a passing gate — a failure to check presented as a pass,
  which is the same shape as `ModuleBoundaryTests` silently not looking at Micromound. Found by
  running the new dry run against a remote that did not exist.
- **Repository files are read as UTF-8.** PS 5.1 reads a BOM-less UTF-8 file as ANSI, which turns
  every em dash in `CHANGELOG.md` into mojibake — and those bytes become the published release
  notes. Caught before it shipped, but only because the first bug stopped the run.

- **Two xUnit2013 warnings introduced in v0.2.1 are gone.** `Assert.Equal(1, errors.Count)` is
  now `Assert.Single(errors)`. The long form was chosen to dodge an overload-ambiguity risk that
  does not exist — `Assert.Single` on an `IReadOnlyList<string>` binds the generic overload
  cleanly, as the rest of this suite already demonstrated. Caution about a hazard that was never
  there still cost two warnings and a less readable assertion.

### Added

- **`--dry-run` / `-DryRun`** on both release scripts. It evaluates every gate, prints a pass/fail
  table, and tags nothing. This exists because of exactly what happened here: the only way to
  discover that the release script could not release was to attempt a release. A gate that can
  only be exercised by the irreversible operation it guards is not a gate anyone can trust.
  Outside a dry run the first failure is still fatal — a release must not walk past a red gate
  because the ones after it happen to be green.

### Not fixed, and worth naming

Neither script has an automated test. `--dry-run` makes them *exercisable* on demand, which is a
real improvement over "find out at the tag", but it is not the same as something that runs without
being asked. There is also still no CI in this repository at all: every green this project has ever
had came from one Windows machine. Both belong to the same piece of work.

## v0.2.1 — M0 frozen

Milestone M0 is complete: the wire contracts, the identity layer, and the capability kernel are
now all covered by tests, and this file exists. No production code changed in this release — it
closes the three items `ROADMAP.md` listed as remaining, one of which turned out to be a
bookkeeping error rather than work.

### Added

- **`MissionValidationTests`** — 23 test cases over `MissionValidator`, which shipped in v0.2.0 with no
  direct coverage. The suite validates the worked example from `ARCHITECTURE.md` "Structured work"
  as its passing case, on the principle that a design doc whose own example does not validate is a
  doc that is wrong. It pins: charter identity, mound identity, expiry, per-op capability and
  routine authorization, the mission's own `allowed_routines` narrowing the charter, the closed
  condition-operator set, backward-only condition references, and evidence promised by the mission
  that no step is tagged to produce.
- **`ManifestValidationTests`** — 29 test cases over `ManifestValidator`, likewise uncovered. Pins:
  mound identity, safe state, the reasoning-mode/provider pairing, driver availability against a
  build's actual driver set, capability-id well-formedness, the `routine.` namespace, worker
  uniqueness and ceilings, offline behaviours, and device limits keyed to nothing the mound
  declares.
- **`CHANGELOG.md`** — this file, backfilled to the first tag.
- **`scripts/validate.sh` + `.ps1`, `scripts/release.sh` + `.ps1`** — one validation
  command and one guarded release command, mirroring ANTHILL's. Validation refuses to run
  while `MICROMOUND_UPDATE_GOLDEN` is set, because a golden test that rewrites its own
  expectation reports the same green as one that verified something; and it fails when
  `Directory.Build.props` and `CHANGELOG.md` disagree about the version, which is design
  rule 9 checked before the PR rather than at the tag. `release.sh` refuses to tag unless
  the tree is clean, main is synced, the section exists, and the tag is free — then builds
  the GitHub Release notes from that same CHANGELOG section, so there is one source for
  them rather than two. Both scripts exist in bash and PowerShell because this project is
  developed on Windows without bash on PATH and released from the same machine — a release
  step that only runs under a shell the maintainer does not have is not a release step.

### Fixed

- `ROADMAP.md` listed "regenerate the golden fixtures for the amended v0 contracts" as remaining
  M0 work. That was done in `901f4dc` and merged as part of v0.2.0; only the checkbox was left
  unticked. The list now reflects what the repository actually contains.

### Authority, wire, refusals

Unchanged. No new grant, no new refusal reason, no change to canonical bytes. The golden fixtures
are byte-identical to v0.2.0.

### Known gaps, recorded rather than quietly fixed

Writing the two suites surfaced three things the v0 validators do not check. None is a regression
and none is fixed here, because each is a contract decision rather than a bug, and a release that
adds tests should not also change behaviour:

- `MissionValidator` does not require a `sense` step's capability to be in the `sense.` namespace,
  nor an `act` step's to be in `act.`. A mission can therefore `sense` an actuator id. The kernel
  still refuses at execution on class and grant, so this is a validation gap, not an authority
  one.
- `Mission.safe_state` and `Mission.worker` are accepted unvalidated. Nothing yet requires a
  mission's safe state to be compatible with the manifest's.
- `ManifestValidator` does not check `WorkerDefinition.exposes`, `runtime_type`, or
  `required_evidence`.

These belong to M1, where the runtime that consumes them lands.

---

## v0.2.0 — the capability kernel, and a standalone mission

The restructure. MicroMound stopped being described as ANTHILL's device arm and became a runtime
with an abstract **upstream controller**; ANTHILL is named as the reference implementation in
`UPSTREAM.md` and appears nowhere in the contracts.

### Added

- **`Micromound.Capabilities` — the capability kernel.** The single physical authority boundary.
  Thirteen ordered checks (stop, registration, driver health, hazardous, class and lease, grant,
  worker ceiling, parameters, limit intersection, duty cycle, rate, clamp, executor bound), each
  with a structured refusal reason. `Authorize` is pure and `Execute` is the only path that moves
  anything; drivers are reachable only through `ICapabilityExecutor`, and executors are held only
  by the kernel.
- **Three-tier limit intersection** — hardware ∩ device manifest ∩ charter, ceilings taking the
  minimum and floors the maximum (`Limits.cs`). `AttemptsToWiden` exists so a widening attempt can
  be *reported* even though intersection makes it harmless.
- **Structured missions and manifests** — `Mission.cs` (ordered steps, one-source/one-operator
  conditions, an advisory-only `context` field no runtime path may branch on) and `Manifest.cs`
  (hardware bindings, declared workers, device limits, reasoning configuration).
- **`Micromound.Runtime`** (Mound Major and the six default ants), **`Micromound.Drivers`**,
  **`Micromound.Evidence`**, **`Micromound.Sync`**, **`Micromound.Reasoning`**, and
  **`Micromound.Host`** as project scaffolding with documented boundaries.
- **`CapabilityId`** — the closed `sense.` / `act.` / `routine.` namespace with strict
  well-formedness, and `CapabilityPattern.MatchesAny`.
- Docs: `ARCHITECTURE.md`, `ANTS.md`, `CAPABILITIES.md`, `CONFIGURATION.md`, `UPSTREAM.md`,
  `ROADMAP.md`. `MICROMOUND.md`, `PROTOCOL.md` and `SAFETY.md` rewritten.

### Changed

- **"Edge Queen" is now "Mound Major".** `src/Micromound.EdgeQueen/` removed.
- **Layer 1 enforcement moved out of the simulator and into the kernel.** In v0.1.0 the clamping
  lived on `SimMound`'s own actuation path, which meant the simulator and any future runtime could
  have diverged. `Micromound.Sim` was rebuilt on top of the real kernel, so a passing simulator
  test is now a statement about the code a Pi will run.
- **Milestones renumbered.** ESP32 firmware M3 → **M5**; optional reasoning is now **M6**, last on
  purpose. M0 was re-scoped from "protocol only" to "contracts *and* kernel".
- **Reasoning is structurally subordinate.** `Micromound.Reasoning` does not reference
  `Micromound.Capabilities`, so a provider cannot call the kernel, hold an executor, or touch a
  driver. Adding one would be a visible `.csproj` change rather than a line inside a method.

### Wire — **breaking within v0**

Protocol version stays `0`; the golden fixtures were regenerated in `901f4dc`. Any implementation
built against v0.1.0 bytes must be rebuilt.

- New envelope kinds: `config` (downlink declarative configuration) and `mission_report` (uplink
  structured outcome). Neither is in the reduced profile — a controller's hardware map is compiled
  in, and it runs charter-selected routines rather than open work packets.
- `Charter` gained `routines`. A charter selects from behaviour that already exists; it can enable
  a registered routine and narrow its parameters, and can never define one.
- `ActionRecord` gained `mission_id`, `routine_id`, `requested_parameters`, and
  `evidence_required`. Reporting only the effective parameters would hide a clamp from the audit
  trail that exists to surface it.
- `CapabilityLimits` gained `max_rate_per_h`.
- `sig` is documented as **zeroed, not omitted**, inside canonical bytes. A C mirror written to
  "exclude the signature" would drop the field and produce a different digest for identical data.

### Authority

Narrowed, in two places worth naming:

- Registration-time refusals, so a misconfigured device fails at startup rather than at first use:
  a `sense.` capability may not be classed above `observe`, nothing may be registered as
  `hazardous`, and a routine may not be classed below a capability it drives.
- `hazardous` is refused before authority is consulted at all, so no charter can ever be the
  reason it was allowed.

---

## v0.1.0 — signing, and bytes that stay put

### Added

- **`Micromound.Crypto`** — Ed25519 device identity, signing, and verification (BouncyCastle
  2.5.0, since .NET 9 has no Ed25519 in the BCL). `Micromound.Protocol` stays dependency-free and
  declares the discipline only: `IEnvelopeSigner`, `IEnvelopeVerifier`, `IPublicKeyDirectory`.
- **Signature enforcement.** PROTOCOL.md §2 had always said unsigned or badly signed envelopes are
  dropped and audited; nothing checked `sig` until now. Refusals carry a specific reason —
  `missing`, `malformed_format`, `unsupported_algorithm`, `unknown_key`, `bad_signature` — never a
  bare no.
- **Golden-file wire fixtures** freezing the canonical bytes for the future C mirror.
- **`EvidenceGate`** — "commands are not evidence" as a pure function. `succeeded` and `clamped`
  survive only when the referenced evidence resolves, parses, and is fresh; everything else
  degrades to `unverified`.
- **`ProtocolTime`** — one wire timestamp format, strict on the way out and tolerant on the way
  in.

### Fixed

The golden fixtures caught two encoding bugs on their first run, before any device existed to be
broken by them:

- The default `System.Text.Json` encoder escaped `+` as `+` and `"` as `"`. Fixed with
  `UnsafeRelaxedJsonEscaping`.
- Timestamps serialized as `…0000000+00:00`, disagreeing with PROTOCOL.md §2's
  `2026-08-14T21:04:11Z`. Fixed by `ProtocolTime`, and §2 gained a normative encoding block.

Both would have surfaced in the field as a device whose envelopes could not be verified.

### Wire — **breaking within v0**

- `ActionRecord` gained `detail`. SAFETY.md prohibits silent failure, so every non-success outcome
  carries its reason on the wire.
- Lease expiry now yields `quiesced` rather than dropping the charter; the charter is retained for
  reporting.
- Refused actuations are queued for the controller rather than dropped.

---

## v0.0.1 — M0 foundation

Design docs, the v0 protocol contracts (envelopes, canonical bytes, digests, hash chaining,
charters, leases, action classes, evidence), the in-memory simulator, and the network-free
authority tests.
