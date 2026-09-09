# MicroMound Safety Model

Canonical safety text. Where this document and any other file disagree, this document wins.
Nothing in this repository may weaken a rule here without this file changing first, loudly.

## Layer 0 — Independent safety systems (not ours)

Emergency stops, hardware watchdogs, interlocks, limit switches, thermal fuses, mechanical stops,
RCDs and breakers. These belong to the electrical and mechanical design of each device, sit below
all software in this repository, and are **not addressable by anything here**:

- No protocol envelope, charter field, manifest entry, routine, driver, or reasoning provider may
  configure, suppress, reset, or depend on defeating a Layer 0 device.
- Software treats Layer 0 trips as observed facts to report — with evidence — never as states to
  manage or recover from automatically. A Guard Ant reports an interlock trip; it does not clear
  one.
- A device whose Layer 0 protection is known-faulty is unfit for any charter above `observe`.

## Layer 1 — Deterministic enforcement on-device

The capability kernel is the single physical authority boundary. Every actuation, on every
hardware tier, passes through it — not by convention but by construction: drivers are reachable
only through `ICapabilityExecutor`, executors are held only by the kernel, and nothing hands one
out.

**Limits intersect across three tiers, innermost first:**

```text
hardware/firmware   ∩   device manifest   ∩   charter   =   effective
```

Ceilings take the minimum, floors take the maximum. An outer tier can only narrow. A charter that
asks for a longer run than the relay tolerates, or a shorter cooldown than the pump requires, does
not get one — the request is intersected away at execution and the attempt is reported at
validation.

Also at this layer:

- **Software watchdog.** Loss of the runtime's own heartbeat drops actuation and enters the
  declared `safe_state`. Safe states are de-energized or passive by construction. Enforced by the
  Guard Ant (`Micromound.Runtime`): a stale heartbeat or an observed safety trip makes it demand a
  safe state, and the coordinator engages the stop rather than continuing. A stale heartbeat is
  self-healing — a watchdog that latched on a scheduling hiccup is one nobody leaves enabled — but
  **an observed trip is sticky and nothing in software clears it**, because software that could
  clear a trip is software that could be asked to.
- **The safe-state walk is per driver, and every path takes the same one.** Driving the hardware safe
  means walking every driver, and one driver that throws must not decide the fate of the rest: each is
  isolated, a failure becomes a sticky safety trip rather than a silent gap, and the whole walk is
  serialised behind the host's safe-state gate so the service loop and the watchdog thread cannot
  interleave on the same hardware. Every transition into stopped or quiesced — from a sync, a mission,
  an expired lease, a cold start with a mission in flight, a shutdown, or the watchdog — goes through
  that one method. **Before `v0.9.29` two of those paths walked the drivers themselves in a bare loop**,
  so the first driver to throw left every later one energized during a stop, with no trip recorded; the
  fix was to route them through the isolated walk that already existed beside them.
- **A timed actuation is held, and its release is owed on every path.** A digital actuator drives its
  line active and holds it for the effective `on_s`, so a real valve is open for its duration rather
  than pulsed; the hold is bounded (the requested `on_s` is clamped to the intersected limit tiers and
  capped again at the effective `max_on_s`), and it is released on the service loop's cadence and by
  the `safe_state` on any stop, quiesce, shutdown, or trip. A line that will not de-energize is not
  swallowed: it keeps its hold pending and escalates to a sticky, persisted stop, because a line that
  cannot be proven safe is treated as unsafe.
- **A deadline is measured in elapsed time, not in clock readings.** A hold bounds how long a line
  may stay hot, so nothing that merely *takes time* may buy it more: the service tick releases due
  holds **before** the blocking sync as well as after it, and the span the sync actually cost is
  measured monotonically and added to the tick's own clock, so a slow round trip or a timeout is time
  the hold sees. The hold itself carries two deadlines — a wall-clock instant and a monotonic
  duration — and releases on whichever comes first, because a wall clock can be stepped: an NTP
  correction jumping backwards must not postpone a release that is physically already due. Earliest
  wins is the fail-safe direction here; de-energizing early is safe, holding late is the failure the
  bound exists to prevent. The lease is re-checked after the sync for the same reason.
- **An independent watchdog releases a held line behind a hung loop.** Because a timed actuation is
  held between ticks, a service loop that *hangs* would leave a line hot — the stale-heartbeat rule
  refuses new actuations but cannot release a line already held. A hardware-independent watchdog on its
  own thread (`LoopWatchdog` / `WatchdogThread`, daemon `--watchdog-s`) notices the loop has stopped
  kicking and drives the mound to a de-energized, sticky, persisted stop without the loop's help. The
  concurrency is made correct rather than hoped for: the Guard is thread-safe, the host's safe-state
  path is serialised behind one gate with a consistent lock order and a bounded wait so the watchdog
  cannot itself wedge, and the loop answers the watchdog at the top of each tick — through the Guard's
  lock, a memory barrier — so a loop resuming from a hang stops itself before it could actuate on a
  stale, not-yet-stopped view of authority. The one residual case is a loop wedged *inside* a driver
  op holding the gate: the watchdog records the trip it can and logs loudly, and process supervision
  (systemd `Restart=`, whose restart de-energizes at configure time) is the backstop. Set the timeout
  generously so an ordinary GC or scheduling pause never trips it.
- **A device never fakes its hardware by accident.** A manifest that names physical ports (a pin, a
  channel, a bus address) is refused by a daemon running on in-memory ports unless the operator says
  `--simulate` in so many words (`v0.9.17`): in-memory readings and actuations look real and are
  neither, and a warning scrolling past a log is not consent. `--check-hardware` claims every port
  the manifest names and reads each sensor once — at the safe level, never actuating — so the wiring
  is checked before any authority exists.
- **A line comes up at its safe level, and stays there only while the daemon holds it.** Both GPIO
  backings now request a line already at `!active_high` — the character device carries the initial
  value in the line request, sysfs writes `high`/`low` as the direction — so an active-low relay is
  never energized for the instant between "becomes an output" and "is written safe" (`v0.9.16`). The
  flip side is stated plainly: when the daemon exits, crashes, or releases a line, the kernel returns
  it to its default state (usually an input, floating or with the board's pull), and the level is no
  longer held by anything. Whether that idle state is safe is a property of the board — a relay input
  with a pull-up idles off; one without may not — and it is exactly the case Layer 0 exists for.
  Process supervision restarts the daemon, which requests the line at the safe level again.
- **Clamp, don't lie.** Where a limit narrows a request, the work proceeds and the outcome is
  `clamped`, carrying both the requested and the effective parameters plus the limit responsible.
  A silent clamp is a false statement about what the mound did.
- **Model output is a proposal.** Any reasoning provider produces proposals to the deterministic
  layer and holds no actuation path. This is enforced structurally: `Micromound.Reasoning` does
  not reference `Micromound.Capabilities`, so a provider cannot call the kernel, hold an executor,
  or touch a driver.

## Layer 2 — Authority (charters and leases)

- No charter → `observe` only. Expired lease → `safe_state`. Ambiguity → downward.
- **The expiry is checked on every service tick, whether or not anything is happening** (`v0.9.27`).
  A lease is a promise about TIME, so it runs out on an idle mound exactly as it does on a busy one,
  and the scenario the lease exists for is precisely the one where nobody is left to ask the mound
  anything. Before this it was checked only when a mission arrived or a process restarted, which left
  an idle mound `chartered` with its outputs live for as long as the silence lasted.
  `MoundService.Tick` calls `MoundHost.QuiesceIfLeaseExpired` before the sync beat; crossing the
  expiry de-energizes every driver and persists the quiesce, so a restart comes back quiesced rather
  than briefly re-authorized.
- Disconnection never widens authority; nothing on-device can extend a lease. Renewal happens only
  when the controller acknowledges a sync beat.
- Reconnection resumes nothing. A quiesced mound reports its state and waits for fresh authority.
- Registration-time refusals, because a misconfigured device should fail at startup rather than at
  first use: a `sense.` capability may not be classed above `observe`; nothing may be registered
  as `hazardous`; a routine may not be classed below a capability it drives.
- **The audit path is bounded, and what it loses is counted.** A queue that grows without limit ends
  in a full disk, and a mound that cannot write cannot record what it did. The uplink queue is
  therefore bounded by items and bytes, spills oldest-first when it must, and reports the count on
  the next beat — the chain makes a gap detectable, and the count makes it explicable.
- **A mound that cannot record what it did must not do it.** The bound above is now enforced BEFORE
  the effect rather than after it: the kernel's fourteenth check refuses new physical work once the
  audit path has no room for the record that work would produce (`no_record_capacity`, `v0.9.37`).
  Enforcing it afterwards meant spilling — trading history the mound already owed for work it had
  not done yet, which is the wrong trade in a system whose whole claim is that every actuation is
  accounted for. Refusing loses only the work, and the controller can ask again. Both profiles do
  this now; the reduced-profile device used to refuse to *record* while still acting, which was the
  same defect wearing the opposite failure. The queue holds a slice of its bound back so the refusal
  can always itself be recorded — a mound that could refuse but not say so would have swapped one
  silent failure for another. **Observation is exempt**, for the same reason a stop does not blind
  the mound, which means sensing can still fill a queue past that reserve; the mound then loses
  readings rather than the account of what it physically did.
- **What the hardware owes survives a restart.** A capability's minimum off-time and its rate budget
  are limits on the DEVICE, not on a session: they persist and are restored before anything may ask
  the hardware for more, so a reboot cannot hand back a cooldown that was already spent. And what the
  mound has already been told to do persists too — a controller redelivering a completed mission,
  whether as the same envelope or as a fresh one around the same mission id, is answered with a
  refusal rather than a second actuation. That ledger is bounded by a validity horizon rather than a
  count, so an entry can never expire into being executable again: an instruction older than the
  horizon is refused, because the mound can no longer prove it has not already run it.
- **`hazardous`-class actions** — physical risk to people, property, or surroundings; fabrication
  tools, motion near people, building systems — require explicit per-action authorization from the
  controller, never a standing grant, expiring on use or timeout. **Until that pipeline ships with
  tests, hazardous actions are refused unconditionally**, and `hazardous` is not a legal charter
  ceiling.

## Layer 3 — Controller oversight

- Every actuation is audited with evidence; `unverified` actions gate missions as failures. An
  actuator's own report is never that evidence — a command is not evidence — so what makes an
  actuation verifiable at all is a **second, independent observation**: the `digital_sensor`
  primitive (a limit switch, an interlock contact, a float) read by a mission's `verify` step. A
  mission may promise that observation up front, which holds the verdict open until it arrives; a
  promise not kept demotes the record to `unverified` before it is published (PROTOCOL.md §9).
- Stops: physical (Layer 0), per-mound, and global. Stop processing precedes all other downlink
  and needs no valid charter. Clearing a stop restores nothing — the mound returns to
  observe-only and waits for a fresh charter.
- **A stop is acted on when it arrives, not when the queue empties.** An authenticated stop takes
  effect on the exchange that delivered it and ends that drain, and a sync beat is bounded in how
  many batches it will push. A deep backlog is exactly the situation an operator reaches for the stop
  in, so the amount of queued work must never be an input to how fast the mound stops.
- **A stop survives the power, on every profile.** "A restart never clears a stop" has been true of
  the Pi-class host since it had a state store, and was NOT true of the C mound until `v0.9.35`: the
  stop lived only in RAM there, so power-cycling a stopped board brought it back willing to actuate —
  and a fault that stops a mound is precisely the kind of event that also power-cycles it. It is now
  one byte in the same protected storage the identity uses, written on the tick the authority stops
  rather than on the beat that reports it, and read back before the first tick can run: the authority
  is stopped and the hardware driven safe during bring-up, so a board that comes up hot goes cold
  without waiting for anything. Nothing on the device can clear it — the storage abstraction has no
  delete, deliberately — and a fresh charter does not lift it either. Reprovisioning does, which is a
  person with the board in their hands. The identity-free port server is out of scope: it holds no
  authority at all, and its equivalent is the link watchdog that drops every line when the Pi goes
  quiet.
- **A storage fault does not become a new identity.** ESP-IDF's stock boot recipe erases the whole
  NVS partition when it will not initialise. On a mound that partition holds the Ed25519 seed the
  identity *is*, the controller's key, and now the stop — so the stock recipe answers "the flash is
  full" by minting a different mound and clearing a halt meant to survive anything, and it is the
  reboot after a fault that is most likely to hit a full page. The autonomous images refuse and halt
  with their outputs safe instead (`v0.9.35`); erasing is a deliberate act by a person.
- **A stop ceases actuation; it does not blind the mound.** Observation continues, as PROTOCOL.md
  §7 has always specified, and the same section requires the stop acknowledgement to carry a
  post-stop sensor snapshot — which a mound that refused to sense could never produce. Refusing
  every capability would also darken the instruments at the exact moment an operator most needs to
  see what the hardware is doing. The kernel decides this from the capability id's namespace,
  before the registry is consulted, so stop still works when the registry, the charter and the
  drivers are all broken.
- An approval pipeline fronts all `controlled` actions.

## Prohibited by construction

- Unsigned protocol traffic; unsigned acceptance of charters or configuration.
- Mound-to-mound delegation or authority transfer.
- Any code path that retries a `hazardous` action without fresh authorization.
- Any tool, endpoint, or envelope that reads back or exports a device private key.
- Any argument, field, or flag through which a caller can request an exception on grounds of
  urgency. Ambiguity resolves downward, and the way to guarantee that is to give ambiguity
  nowhere to enter.
- **Silent failure.** Every refusal, clamp, trip, and validation failure is reported and audited,
  with a specific reason. A refusal without a reason is itself a contract violation.
