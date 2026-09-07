# The acceptance sequence

`docs/ROADMAP.md` states MICROMOUND's target as criteria rather than a milestone number: what a
minimal bench must be *seen* to do, in order, before this is a functional physical edge colony.

This document is that list, numbered, with the argument for why each item is on it — and it is
executable. `src/Micromound.Acceptance` runs the whole sequence against a real mound and, when the
C tools are built, against the real port-server firmware in its own process on the other end of a
real byte stream.

```
dotnet run --project src/Micromound.Acceptance          # both legs
dotnet run --project src/Micromound.Acceptance -- --in-memory
bash scripts/validate.sh --full                          # runs it, and CI runs this
```

It exits `0` only when every applicable criterion is met, and prints one line per criterion saying
what was actually observed — not "ok", but *"asked 60s → ran 10s"*.

## What it is and is not

**It is not a substitute for the bench.** Nothing here proves a solenoid moves. What it does is
make the bench day short: every criterion that can be met without a soldering iron is met here
first, deterministically, and what remains on the bench is the wiring.

**Nothing in it is a code path built for the check.** Every criterion is asserted against the same
objects the daemon runs: a real `MoundHost` composed from a manifest, a real `CapabilityKernel`, the
real default ants, real generic drivers, a real `FileStateStore` and `FileEvidenceStore` on disk, and
a real signed wire to an in-process controller. What the harness supplies is the *bench* — a
controller that enrolls and charters, a clock it advances by hand, and a world in which opening a
valve has a consequence a separate sensor can see.

**A criterion that cannot be met on this leg is `n/a` with the reason, never silently passed.**

**What a pass here does not claim.** Two limits, stated up front because a green report is easy to
read too widely:

- *The sequence is not the whole bench.* `ROADMAP.md`'s target names a stepper/servo axis and a
  position encoder alongside the switch, output and ADC. These eighteen criteria exercise the
  relay/switch/ADC scenario only — there is no motion primitive in the shipped drivers yet. Phase P3
  of the roadmap closes that; until it does, "the acceptance sequence passes" and "the acceptance
  bench is built" are different statements.
- ~~*Criterion 11 is weaker than its name.*~~ **Closed at `v0.9.30`.** It used to ask only whether an
  independent line was read after the act, not whether that line read the right thing — so a limit
  switch reporting "open" after a close command confirmed the actuation. The mission now carries a
  postcondition (`expect: eq 1 closed`) and the Witness compares the reading against it, so the
  criterion tests agreement rather than presence. Criterion 12 is its mirror: the same mission, with
  the witness blinded, must not confirm.

## The two legs

| Leg | What is real | What is modelled |
|---|---|---|
| **firmware** | the mound, and the board: `firmware/micromound-c/build/mm_board_sim` is the C the ESP32 image is built from — `mm_ports`, `mm_frame`, the same handlers, the same refusals, the same compiled `max_on_s` and link watchdog — in its own process, reached over a real `LinkPortsClient` speaking §12 framing over the pipe | only the world below the board's HAL: a valve line, a limit switch that closes 2 s after the valve opens, an ADC channel that rises while it is open |
| **in-memory** | the mound | the ports: one valve line, one switch wired to it, one tank channel |

The board's clock only moves when the harness tells it to, so a whole run is deterministic — no
sleeps, no wall clock, no flakes. Build the board with `make -C firmware/micromound-c tools`; without
it the firmware leg is skipped with a notice.

## The bench the harness composes

Three devices, one manifest, one charter, one mission — the same three generic driver types a real
deployment uses:

| Device | Driver type | Capability | On the board |
|---|---|---|---|
| `irrigation` | `digital_actuator` | `act.valve` | pin 5, active high, the board's own `max_on_s` 30 s |
| `tank` | `analog_sensor` | `sense.tank_level` | channel 0, rising while pin 5 is driven |
| `limit` | `digital_sensor` | `sense.valve_closed` | pin 12, active **low**, closes 2 s after pin 5 |

The charter grants `max_on_s` 10 s, `min_off_s` 300 s, a 3600 s lease, and requires evidence for
`act.*`. The mission reads the tank, opens the valve for **60 s** — more than anything allows — and
then, after a 3 s settle, asks the limit switch whether it moved.

Every number there is doing work. The 60 s is refused down to 10 s by the charter, which is what
criterion 9 reads. The switch's 2 s travel is why the verify step settles first — the whole point of
`settle_s`. And the switch reads the valve **line**, not the command that drove it, because a switch
the harness simply set to "closed" after each act would confirm nothing, and a criterion it
satisfied would be worth nothing.

## The eighteen criteria

The prose of `docs/ROADMAP.md` "The target", split at its semicolons and numbered.

| # | Criterion | What the run actually checks |
|---|---|---|
| 1 | a fresh mound boots its unchanged default mini-colony | Mound Major + Scout, Forager, Guard, Witness, Cache, Runner are all registered, and the state is `observe_only` — a mound with no charter is not entitled to act |
| 2 | the board is discovered over the link | `hello` over §12 framing: not tripped, and it offers pins, inputs and channels |
| 3 | hardware is loaded from the manifest and capabilities register | the three devices resolve to three declared capabilities |
| 4 | the mound enrolls upstream | a token is exchanged for the controller's key, the key is **persisted**, and downlink verifies against it from then on |
| 5 | signed configuration is accepted and persisted | a `config` downlink narrows the device tier to 20 s, verified under that key |
| 6 | a signed charter is accepted and persisted | the mound becomes `chartered`; the lease starts running |
| 7 | configuration binds generic hardware to the generic ants, without changing their code | a **reflection scan** of the five core assemblies for appliance-named public types (`valve`, `pump`, `tank`, `rover`, `greenhouse`, …). A device-specific class in the core is the signal an abstraction is wrong, and this is the criterion that will not let one in quietly |
| 8 | a mission is coordinated by the Mound Major | the three steps run in order and the mission completes |
| 9 | the Forager requests actuation and the kernel validates authority and limits | asked 60 s, ran 10 s, outcome `clamped` — and the record says which limit did it |
| 10 | a generic driver sends a bounded request to the board and the board acts | the board saw a drive **and** a release, and the line is at its safe level afterwards |
| 11 | the Witness confirms with independent evidence | a *separate* `sense.valve_closed` line read `1` after the act **and satisfied the mission's stated postcondition** (`eq 1 closed`), and its evidence id is now on the action's own refs |
| 12 | the result reflects verified / unverified / failed reality | the witness is blinded — not the valve. The actuation still happens; nothing independent can see it; the record must say `unverified` rather than claim success |
| 13 | the network drops: work continues inside the lease, inventing no authority, and evidence queues | a mission runs offline, nothing is delivered, and the lease expiry does **not** move |
| 14 | the Pi reboots and stop, lease, configuration and evidence restore | a whole new host over the same state directory: still chartered, same expiry, the pushed 20 s tier intact |
| 15 | the lease expires while disconnected and the mound enters its declared safe state | **an idle service tick and a clock, nothing else.** Nobody asks this mound to do anything; it quiesces by itself and de-energizes |
| 16 | the network returns: expired authority does not resume and the backlog syncs into a complete auditable history | reconnecting does not un-quiesce anything, the controller refuses **zero** envelopes, and every evidence ref on every record resolves to an item it holds |
| 17 | a stop de-energizes and is not cleared by a restart | a stop order, then a whole new host: still stopped |
| 18 | the board keeps its own limit tier and drops its outputs when the Pi goes quiet | the harness plays a Pi that **died mid-actuation**: it drives a line over the link and says nothing more. The board releases it on its own compiled bound, then trips its link watchdog. Nothing above the board helps it |

Criterion 18 needs that last trick because a healthy Pi always releases within the board's bound, so
the tier below the kernel never bites — and "it never had to" is no evidence at all that it works.

## What this harness has already caught

Two defects, both of which every unit test in the repository was green through. That is the case
for having it: they are not bugs in a unit, they are the system not adding up to a mound.

**The verified path was unreachable** (fixed in `v0.9.27`). The evidence gate demoted any action
record with no evidence refs to `unverified`, and nothing may raise an `unverified` verdict
afterwards. A digital actuator produces no evidence — a command is not evidence — so every
actuation on real hardware was `unverified` the instant it happened, and the confirming read could
never lift it. Only a driver that certified its own work could be believed, which is the exact
opposite of the rule's purpose. The fix is `confirmation_expected`: told by the mission that a
`verify` step will confirm this action, the kernel holds the verdict **open** instead of demoting
it, and the Witness settles it — with the Mound Major demoting anything still open when the walk
ends, before a single record is published.

**A lease only expired when someone asked** (fixed in `v0.9.27`). `KernelAuthority.QuiesceIfExpired`
was called on the mission path and at restore, and nowhere else — so an idle mound sat `chartered`
with its outputs live for as long as nobody happened to send it a mission. A lease is a promise
about *time*, and the scenario it exists for is the one where nobody is left to ask. It is now
checked on every `MoundService.Tick`, before the sync beat.

Both were gaps *between* components, which is why every unit test was green through them — and it is
also why the next set of findings (roadmap phase P0) came from reading the same seams rather than
from any test going red. A harness that reaches only as far as this one does will keep finding them
one at a time; the P0 work is to make the seams themselves checkable.

## Adding a criterion

Add it to the ROADMAP prose first — the list here is that sentence, not a separate opinion about
what matters — then add a `Check(n, …)` to `AcceptanceRun` in sequence order. Two rules:

1. **Assert against the mound, not the harness.** If a criterion can be satisfied by the bench
   telling itself what it wants to hear, it is worth nothing. Criterion 11 reads a switch wired to
   the valve line for exactly this reason.
2. **Say what was observed, not that it passed.** The detail line is the deliverable; a criterion
   whose evidence line reads "ok" cannot be reviewed by anyone.

## What remains on the bench

Everything about the physical world: that a line at 3.3 V actually closes the relay, that the
relay actually opens the valve, that the switch is wired to the thing it claims to observe, that the
ADC reads the tank and not noise, and that the board survives the electrical environment it is in.
The sequence above is the same sequence, run over real wiring, on a Pi and a flashed board — and by
then everything else about it will already have been proven.
