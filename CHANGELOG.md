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

## v0.9.40 — the roadmap says what actually happened

**Documentation only. No source file changed, no test changed, no fixture moved.**

### What was wrong

`docs/ROADMAP.md`'s phase section was titled **"After the bench: the phase plan"**, and the P0–P7
status read "Planned; P0 is next" — while P0 had been running since `v0.9.29` and eight of its ten
items had shipped. The document described an order the work had not followed.

It was also wrong as a plan, which is the more useful half. P0 is the correctness and recovery debt,
and every row in it is a defect that the whole test suite, the simulator and the eighteen acceptance
criteria were green through: an unreachable `verified` outcome, a lease that expired only when
somebody asked, a stop a reboot cleared, an actuation nobody could account for, a cooldown a clock
step refunded, a blocked driver that kept unrelated outputs live. Those are exactly what a bench
surfaces the expensive way — intermittently, one at a time, with a relay wired to the end of them. A
mound with those closed is a far better thing to attach hardware to. So P0 belongs before the bench,
and the heading now says the thing that actually governs: the phases are ordered against each other,
not against the hardware run.

### What changed

- The section is **"Beyond the bench: the phase plan"**, with a paragraph saying plainly that the old
  title was wrong twice over and why P0 came first. The anchor and the link to it move with it.
- **M4 and M5 read "Built; open on the bench run alone"** rather than "In progress". Both are
  finished and host-verified; each waits on the same single thing — `docs/DEPLOY.md` walked to the
  end on a Pi, and any one of the three ESP32 images flashed — and neither is waiting on code or can
  be closed from this repository. The Status preamble says so at the top, where it is answerable at a
  glance.
- **The P0–P7 row states what has actually shipped** (`v0.9.29`–`v0.9.39`; P0.1–P0.5, P0.7, P0.8,
  P0.10 done, P0.6 and P0.9 half done) instead of "Planned".
- **Three "known gaps, recorded" are struck through and marked closed** — P0.1's wrong-reading
  confirmation (`v0.9.30`), P0.5's reset duty cycle (`v0.9.33`, `v0.9.38`) and P0.2's pre-sync wall
  clock (`v0.9.31`). They had been sitting in the list as open findings for up to ten releases after
  they were fixed.
- **Two counts in the milestone record that had gone stale**: the C kernel's "thirteen authorization
  checks" (fourteen since `v0.9.37`) and `mm_hal`'s "seven-function" abstraction (nine now). The
  milestone rows are history and stay as written, so both are dated rather than rewritten.
- **One present-tense claim the last release made false**: M4's note that a loop wedged inside a
  driver op cannot be de-energized by the watchdog, with process supervision as the backstop. `v0.9.39`
  closed that for the walk; supervision is now the backstop for the stuck line alone.
- `README.md`'s status line stops saying M4 and M5 are "in progress".

### Why this is its own release rather than a line in the next one

Because the sweep rule exists — every release must leave no stale documentation anywhere — and this
is what it looks like when the rule is applied to the sweep's own blind spot. The rule catches
counts, sizes and version markers that a *change* made untrue. It did not catch a heading that had
quietly become untrue because of the ORDER the work was done in, and nothing but reading the document
as a whole would have. Recorded here so the next such drift is looked for deliberately.

### Verified

`bash scripts/validate.sh` guards, 602 C# tests, 2,632 C checks. Nothing else could change, and
nothing did.

---

## v0.9.39 — P0.8: no single driver may hold the safe-state walk

Roadmap P0.8, the remaining half, and it closes the row. **Host only. No wire change, no C source
change, no fixture moved** — the firmware images differ only by the version string compiled into
them.

### What was wrong

`v0.9.29` fixed the driver that THROWS on the way to safe: caught per driver, reported as a trip,
walk continues. A driver that BLOCKS is a different problem and it cannot be caught. It stops the
walk at itself, so every driver after it in the manifest stays energized — and the caller holds
`_safeGate` the whole time it waits, so the independent watchdog cannot take the gate either. One
stuck I2C transaction on a temperature sensor keeps a pump running, and the watchdog whose job is to
notice is locked out by the very call it would have rescued.

`SAFETY.md` named this and pointed at systemd `Restart=always` as the backstop — which does not kill
a still-running process, so it was a backstop that did not reach.

### What changed

Every per-driver safe-state and hold-release call goes through one bounded call
(`HostOptions.SafeStateTimeoutSeconds`, default 5 s — a GPIO write is microseconds and an I2C
transfer milliseconds, so five seconds is already pathological while being long enough that no
healthy driver is ever abandoned). Past the bound the mound stops WAITING: it records a trip, which
`MoundService` already escalates to a persisted stop, abandons that driver, and carries on to the
rest.

**The hold-release walk is bounded for the same reason**, and it is the one that leaves a line
literally hot: it runs on every tick over every driver, so a driver that blocks there holds every
*other* driver's elapsed hold open behind it. The line that should have de-energized five seconds ago
stays live because an unrelated sensor stopped answering.

**An abandoned driver is never called again.** It has proved it does not answer, the mound is
stopping because of it, and each further attempt would strand another thread-pool thread — on a mound
that goes safe on every tick that is a leak with no ceiling.

**With the bound at zero the call is inline on the caller's thread**, exactly as before this existed:
no task, no pool thread, no behaviour change. That is what a deterministic bench wants, and it is why
this is a bound rather than a rewrite.

### What this does NOT promise, stated exactly

A blocked call cannot be cancelled in .NET. Nothing here interrupts the stuck driver or makes ITS
line safe. The guarantee is narrower and worth saying precisely: **one blocked driver cannot keep
unrelated outputs live**, and the safe-state gate is released in bounded time so the watchdog can
take it. For the stuck line itself, process supervision remains the only thing that reaches it — but
it is now the backstop for one line rather than for the whole mound.

### Confirmed red — and this one did not fail, it hung

With the bound removed and the call made inline, the test suite **does not finish**. It wedges in the
first blocking test and was killed at 300 s; with the bound it completes in 17 s. That is the defect
stated more precisely than any assertion could put it, and it is exactly what a mound would do in the
field. The abandoned-set has its own narrower red: with only that guard removed, the suite completes
and `A_driver_that_has_been_abandoned_is_not_called_again` fails 1 vs 2.

Three tests, all on the two-actuator harness `v0.9.29` introduced: a stop with the first driver
wedged (the second de-energizes, the trip is recorded, and the walk returns inside a bounded time),
an idle tick past an expired lease with the first driver wedged (the second de-energizes and the
mound ends up *stopped*, not merely quiesced — the trip escalates), and the abandoned driver never
being asked twice.

### Remaining, named

The bound protects the WALK, not the line behind the stuck driver. Reaching that needs something
outside the process, and process supervision is still what does it.

### Verified

602 C# tests, 2,632 C checks under gcc and clang at `-O0` and `-O2` and again under ASan + UBSan with
recovery off, all three ESP-IDF images rebuilt under v5.3.2 (Wi-Fi 1,027,216 B; serial 297,056 B;
port server 260,736 B — unchanged but for the version string), acceptance 18/18 on the firmware leg
and 15/15 applicable in memory, the console harness, and the simulator's lifecycle claims. Every
frozen fixture is byte-identical. Not run: the NuGet restore (firewalled), and any of it on real
hardware.

---

## v0.9.38 — P0.5: a stepped clock cannot hand back a cooldown

Roadmap P0.5, the remaining half, and it closes the row. **No wire change. Not one frozen byte
moved** — including `kernel-decisions.txt`, which is the point: the guard is off where there is no
real clock to check against, and every fixture is exactly that.

### What was wrong

`min_off_s` and `max_rate_per_h` are both answers to "has enough time passed?", and both were
computed by subtracting two readings of a wall clock. A wall clock moves for reasons other than time
passing. The case is not exotic — it is the normal life of the hardware this runs on: a Pi or an
ESP32 with no battery-backed RTC boots believing it is 1970 and its first NTP or controller sync
steps it forward by decades. At that instant every cooldown reads as elapsed and every rate window
as empty, at the exact moment the mound has least reason to trust its own sense of time.

**Correcting this row's own claim.** P0.5 said `min_off_s` was "conservative in both" directions and
only the rate window was exposed. That was wrong, and reading it again is what caught it: a forward
step makes `now < last_end + min_off_s` false exactly as it ages starts out of the trailing hour.
Both were exposed. Both are guarded now.

### What changed

Every recorded instant carries a monotonic stamp beside it, and an entry's age is the **smaller** of
what the two clocks claim. For "has enough time passed?" the smaller answer is the safe one — the
mirror image of `v0.9.31`'s rule for releasing a hold, where the question is "is it time to
de-energize?" and the LARGER elapsed wins. A step backward was already conservative and stays so.

`ActuationHistory.Time` and `mm_history.monotonic_now` are the two sides of it, and both default to
absent. **That default is deliberate and is what kept every fixture byte-identical.** A bench that
advances a fake wall clock by an hour between steps has no real hour to show a monotonic counter;
pairing a fake wall clock with a real one would make every cooldown permanent and every golden file
wrong. Absent means "no monotonic evidence, and the wall clock is all there is" — which is also the
honest description of a restored entry. The daemon passes `TimeProvider.System`; on the board,
`mm_hal` gained an OPTIONAL ninth hook, `monotonic_s`, filled by `hal_esp32.c` from
`esp_timer_get_time()`, and left NULL by the port server, which keeps no budgets to age.

`CapabilityKernel` now asks `History.MinOffElapsed(...)` rather than subtracting from `LastEnd`
itself. `LastEnd` is still what the refusal DETAIL names, because an operator reading a record needs
the instant the hardware actually stopped, not the number the guard used to decide.

### The adversarial find: growing `mm_hal` segfaulted the test HAL

Adding the ninth function pointer crashed `make test` immediately. The fake HAL fills the struct
field by field on a stack local and never zeroed it, so the new slot held whatever was there — and
the library called it. `-Wextra` catches the initializer-list form of this (it duly failed the board
simulator's positional initializer, which is how that one was found) but has nothing to say about
field-by-field assignment. `mm_hal.h` now states the contract in the struct's own comment, the C
README's example was rewritten to zero first, and that example's field ORDER was wrong as well —
`adc_read` and `gpio_read` were transposed, so anyone who copied it would have bound each to the
other's slot.

### Confirmed red first

With the two-clock rule reverted to a plain wall-clock subtraction: on the host, the forward-step
cooldown test, the forward-step rate test and the restored-entry test fail; in C, three checks in
`test_kernel.c` fail, starting with `!d.authorized`. The other three host tests — real elapsed time
still clears the cooldown, a backward step is conservative either way, and no monotonic source means
the wall clock is believed — pass with and without the fix ON PURPOSE. They are there because
`v0.9.33` shipped a guard for this that passed its own tests with the mechanism removed, and a guard
that simply refused forever would be just as green as one that works.

### Remaining, named and not closable here

**Across a restart the monotonic counter reset with the process**, so a restored budget is aged on
the wall clock alone. The real gap is unknowable from inside the mound — a mound cannot tell an
eight-hour outage from an eight-hour clock error — and closing it needs the controller, which knows
what time it is. That is a protocol question, not a mechanism, and it is not being decided inside a
patch release.

### Verified

599 C# tests, 2,632 C checks under gcc and clang at `-O0` and `-O2` and again under ASan + UBSan with
recovery off, all three ESP-IDF images built under v5.3.2 (Wi-Fi 1,027,216 B; serial 297,056 B; port
server 260,736 B), acceptance 18/18 on the firmware leg and 15/15 applicable in memory, the console
harness, and the simulator's lifecycle claims. Every frozen fixture is byte-identical. Not run: the
NuGet restore (firewalled), and any of it on real hardware.

---

## v0.9.37 — P0.7: a mound that cannot record what it did must not do it

Roadmap P0.7, the harder half, and it closes the row. **No wire change to any body or envelope.**
One addition to a closed set: `no_record_capacity` joins the refusal reasons (PROTOCOL.md §6), which
a controller reading refusals by name will see for the first time.

### What was wrong

The uplink queue has been bounded since `v0.9.34` — it has to be, or a mound offline long enough
fills its disk. But the bound was enforced *after* the effect. The actuation happened, the record
was written, `Trim()` noticed the queue was over its ceiling, and the OLDEST envelope was spilled to
make room. That trades history the mound already owes for work it has not done yet, which is exactly
backwards for a system whose entire claim is that every physical action is accounted for. Refusing
loses only the work, and a controller can ask for that again; spilling loses the account of
something that already happened, and nobody can ask for that back.

**The C mound had the same defect wearing the opposite failure.** The roadmap row used to say the
reduced-profile device "already does it the right way round (a full queue refuses to record)". It
does refuse to record — `mm_device` never drops anything — but `mm_device_act` ran
`mm_kernel_execute` FIRST and only then discovered it had nowhere to file the result. The relay
moved and the record was lost with an audit line saying so. Reading it again while writing this
slice is what caught it; the row is corrected.

### What changed

**A fourteenth authorization check.** Last in the order, deliberately: everything above it answers
"may this happen?", and those answers are the ones somebody can act on — a lease to renew, a charter
to widen, a cooldown to wait out. This one answers "can we account for it?", and it must not mask a
refusal anyone could fix.

**Observation is exempt**, for the same reason a stop does not blind the mound (SAFETY.md Layer 3).
A reading that cannot be queued is a lost reading; an actuation that cannot be queued is a physical
change nobody can account for. Only the second is worth refusing over, and darkening the instruments
when the queue backs up would take an operator's eyes away exactly when they are needed.

**The queue holds a reserve back.** An eighth of each bound — items and bytes — for records that
EXPLAIN rather than report: the refusal this check produces, acknowledgements, the beat that carries
the mound's state out. A mound that could refuse but not record the refusal would have swapped one
silent failure for another. An eighth is chosen as a duration rather than a number: the reserve has
to outlast the outage that filled the queue, at the rate refusals are produced, which is at most the
rate work was being attempted — the same rate that filled it. An eighth of 5,000 records is 625
refusals, four days at a ten-minute schedule.

**Two integers, one rule, both implementations.** The kernel learns `PendingRecords` and
`CapacityForNewWork` through `IAuditCapacity` and applies exactly `pending < capacity`; `mm_kernel`
applies the same rule over the same two `int`s. A bool would have let "full" come to mean two
different things in C and C# with no fixture able to see it — the failure this whole mirror exists
to prevent. When the BYTE bound is the one reached (the usual case on a real mound, where an action
record with inline evidence dwarfs a beat) the queue reports capacity as the current depth, so the
one comparison still says "full" without the kernel learning what a byte is.

The join lives in `Micromound.Runtime` (`UplinkAuditCapacity`), because `Micromound.Sync` is Layer 1
and must not take a reference up to Layer 3 for one pair of integers.

### Pinned

`kernel-decisions.txt` gained six steps, 42 → 48, appended so every earlier step stayed
byte-identical: the audit path reaching capacity, an actuation refused before anything moves, a
`sense` in the same state still authorized, a slot opening, and the actuation then proceeding. The C
kernel replays all 48 and reproduces every reason, detail line and record. `test_device.c` pins the
wiring the fixture cannot see — that `mm_device_act` feeds the kernel its own queue's numbers, and
that the relay does not move.

### Confirmed red first

Each new assertion was run against the unfixed code before being kept: with check 14 disabled, the
fixture and the end-to-end refusal test fail; with the reserve removed, all four queue tests fail;
with the composition line commented out, the wiring test fails; and in C, with the two assignments in
`mm_device_act` removed, six checks fail — including `relay.n == before`, which is the one that says
the line really did move.

### Remaining, named

**Sensing can still fill the queue past the reserve**, because check 14 exempts it. The mound then
loses readings rather than the account of what it physically did, which is the right way round, but
it is a real limit and not a claim to have closed. **The `downlink-ledger` key still rewrites whole**
— the same shape `v0.9.34` fixed for the uplink queue, at a much smaller bound.

### Verified

593 C# tests, 2,624 C checks under gcc and clang at `-O0` and `-O2` and again under ASan + UBSan with
recovery off, all three ESP-IDF images built under v5.3.2 (Wi-Fi 1,027,056 B; serial 296,768 B; port
server 260,704 B — the port server has no kernel and is unchanged), acceptance 18/18 on the firmware
leg and 15/15 applicable in memory, the console harness, and the simulator's lifecycle claims. Every
frozen fixture other than `kernel-decisions.txt` is byte-identical. Not run: the NuGet restore
(firewalled), and any of it on real hardware.

---

## v0.9.36 — P0.10: .NET 10 LTS, and not one signed byte moved

Roadmap P0.10. **No wire change, and that is the whole point of the release.** No source file
changed at all: `Directory.Build.props` targets `net10.0`, the CI workflows install `10.0.x`, and
the documentation says so.

### Why now

.NET 9 is a standard-term release and leaves support on **10 November 2026** — two months out. .NET
10 is the LTS line, released 11 November 2025 and supported to **14 November 2028**. The release
workflow publishes self-contained single-file binaries for `linux-arm64` and `win-x64`, so the
runtime is not something a deployed mound can be upgraded away from independently: whatever the
tag builds is what runs on the Pi until it is replaced. Shipping a mound on a runtime that stops
getting security fixes in two months is not a thing to leave until it is urgent.

### The migration is one line, so the release is the proof

A runtime upgrade must not change a signed byte. That claim is easy to make and easy to get wrong
quietly — a device in the field verifies signatures over canonical bytes produced by a mound that
may have been upgraded, and the two have to agree exactly or the chain breaks with no useful error.

So the fixtures were not re-run. They were **re-derived**: every C#-written frozen fixture deleted
and regenerated from scratch on .NET 10 with `MICROMOUND_UPDATE_GOLDEN=1`, then compared to what is
committed. All eight came back byte-identical — `git status` on `Golden/files` clean:

```
canonical-envelopes.txt   canonical-bodies.txt   canonical-strings.txt   canonical-doubles.txt
canonical-signed.txt      enroll-exchange.txt    kernel-decisions.txt    link-frames.txt
```

`canonical-doubles.txt` is the one that mattered most. It pins .NET's own `double` layout — the rule
`mm_format.c` reimplements in C from first principles — and a number-formatting change between
runtimes would be exactly the kind of silent divergence that shows up months later as a mound whose
readings no longer verify. It did not move. `canonical-signed.txt` carries real Ed25519 signatures
over those bytes and did not move either.

Then the C mirror read the regenerated files and agreed: 2,576 checks under gcc and clang at `-O0`
and `-O2`, and again under ASan + UBSan with recovery off.

### The language version deliberately did NOT move

`LangVersion` stays at `13.0` on a runtime that offers C# 14. Mixing a language change into a
runtime migration would make the byte-for-byte comparison above prove less than it does — a clean
diff would no longer isolate the runtime. C# 14 is a separate decision on its own merits, and this
release does not pre-empt it.

### The seven open dependency PRs, triaged rather than merged wholesale

P0.10 asked for a verdict on each. None is a vulnerability report; an open version-bump PR is not
by itself evidence of one.

| PR | Verdict |
|---|---|
| `actions/checkout` v4→v7 | CI only. Land as one CI-only change where a red run costs nothing |
| `actions/download-artifact` v4→v8 | as above |
| `actions/upload-artifact` v4→v7 | as above |
| `actions/setup-dotnet` v4→v6 | as above — and **not** a blocker here: `setup-dotnet@v4` installs `10.0.x` |
| `github/codeql-action` v3→v4 | as above |
| `BouncyCastle.Cryptography` 2.5.0→2.7.0 | **The Ed25519 signer** — the one bump that can move a signed byte. Its own slice, which regenerates `canonical-signed.txt` and proves it did not |
| xunit + `Microsoft.NET.Test.Sdk` (grouped) | Test-only; touches no shipped code |

Five CI-only bumps merged blind alongside a runtime migration would make a red CI run ambiguous
about which change caused it, which is the reason to keep them separate rather than a reason to
leave them open.

### Toolchain pins recorded, as P0.10 asked

- **.NET 10** — LTS, 11 Nov 2025 → **14 Nov 2028**.
- **ESP-IDF v5.3.2** — Espressif supports each minor release for 30 months from its initial stable
  release (12 months Service, then 18 months Maintenance, bug fixes only). v5.3 stabilised in
  August 2024, so it is in Maintenance now and leaves support in the first half of 2027. The exact
  date is on the branch's own release note and should be read there before the bench work pins a
  version for hardware.
- **BouncyCastle.Cryptography 2.5.0** — the only NuGet dependency in shipped code, and only because
  the BCL has no Ed25519.

### Two things this does not do

**It does not migrate the controller.** ANTHILL compiles against this repository's
`Micromound.Protocol` and `Micromound.Crypto` *sources*, so it has to take the same runtime move or
pin an older commit. That is a coordination item with the upstream and cannot be closed here.

**It does not verify the NuGet restore.** api.nuget.org is firewalled in the sandbox this was built
in, so `BouncyCastle.Cryptography` 2.5.0 building and signing under .NET 10 is asserted from its
`netstandard2.0` targeting, not observed. CI's restore is the check. Everything else below WAS run.

**One operational prerequisite:** the machine that runs `scripts/validate.ps1 -Full` needs the .NET
10 SDK installed. With only the .NET 9 SDK present the build fails at the first project with "the
current .NET SDK does not support targeting .NET 10.0".

### Verified

585 C# tests on .NET 10, all eight frozen fixtures regenerated byte-identical, 2,576 C checks under
gcc and clang at `-O0` and `-O2` and again under ASan + UBSan with recovery off, acceptance 18/18 on
the firmware leg and 15/15 applicable in memory, the console harness, and the simulator's lifecycle
claims. Not run: the NuGet restore, and any of it on real hardware.

---

## v0.9.35 — P0.6/P0.9: on the board, a stop survives the power, and a full flash is not a new mound

Roadmap P0.6 (the sticky stop half) and P0.9 (the erase). **No wire change. No refusal reason
changed. No C# source changed** — both defects were in the C mound and its ESP-IDF entry point.
Storage: one new key, `mm.stopped`, additive; an existing device is unaffected until it stops.

### What was wrong

**A stop did not survive a reboot on the C mound.** `docs/SAFETY.md` has said since the beginning
that a restart never clears a stop, and the Pi-class host has honoured it since it had a state
store — `MoundService.RespondToWatchdog` escalates a trip to `host.Stop()` and persists it, and
acceptance criterion 17 has pinned it release after release. On the ESP32 the stop lived in
`mm_authority` and nowhere else, so a power cycle brought a stopped mound back willing to actuate.
Two ways to reach it, both ordinary: an operator downlinks a stop, the board resets, and it comes
back live; or a relay refuses to release, the mound trips itself to a stop — and the fault that
seized the relay browns out the board, which comes back and drives that same line again. The
second is the one that matters. A trip is the device saying it no longer trusts its own hardware,
and a reboot was enough to talk it out of that.

**A full or newer-format NVS partition erased the device's identity.** `app_main.c` carried
ESP-IDF's stock recipe verbatim: on `ESP_ERR_NVS_NO_FREE_PAGES` or `ESP_ERR_NVS_NEW_VERSION_FOUND`,
erase the partition and carry on. That is right for a partition holding cached Wi-Fi credentials.
Here it holds the Ed25519 seed the mound's identity *is*, the controller's public key, and — as of
this release — the stop. So the recipe answered "the flash is full" by minting a different mound,
orphaning its whole signed history, and clearing a halt meant to survive anything. And a full page
is most likely on the reboot *after* a fault, which is exactly when the stop is real.

### What changed

**The sticky stop is one byte in protected storage.** `MM_KV_STOPPED` (`mm.stopped`, within NVS's
15-character key limit like the other four). `persist_stop()` writes it through the HAL's `kv_set`
the moment the authority is stopped, and marks itself done so it is not rewritten every tick.
Three call sites, chosen so the write always precedes the consequence: at the top of
`mm_app_tick` as the catch-all for anything that happened since the last one; immediately after a
relay's release fails and the trip escalates, *before* the beat that reports it; and immediately
after `mm_device_sync`, for a stop that arrived on that exchange. The read is the last thing
`mm_app_init` does — `mm_authority_stop` then the safe-state callback, in that order — so a board
that comes up with a line hot goes cold during bring-up, before a single tick runs.

Nothing on the device can clear it. `mm_hal` has no delete and is not getting one; a `kv_set` of
zero bytes is treated as absence by the library, but the firmware never calls it on this key.
A fresh charter does not lift a stop either — that was already true of the kernel, and the test now
says so out loud. Clearing it means reprovisioning, which is a person with the board in their hands.

**The autonomous images refuse to erase NVS.** A partition that will not initialise halts the board
in `halt_safe` — every output at its safe level, the reason logged, the task watchdog kept fed —
rather than erasing it. `CONFIG_MM_ALLOW_NVS_ERASE` (default n) restores the stock behaviour for a
development board, and the port-server image still erases unconditionally: it holds no identity and
no authority, so it has nothing to lose. All three images build unchanged in every other respect
(Wi-Fi 1,026,704 B; serial 296,528 B; port server 260,704 B).

### Confirmed red first

Every assertion added here was run against the unfixed code before being kept:

- `rebooted.status.stopped_at_boot`, the reboot's state, and the line being cold — five checks that
  fail without the restore block in `mm_app_init`.
- `kv_len(&f, MM_KV_STOPPED) == 1` after the tick that delivered the stop, and after the tick the
  relay tripped on — both fail with only the top-of-tick write, which is the version that would
  have shipped had the window not been looked for.

The trip scenario in `test_board.c` shares its fake HAL with the reboot scenario, so it now clears
`mm.stopped` first, through a helper that models reprovisioning rather than anything the firmware
can do. That is the test saying, in the only way a test can, that the device has no way out.

### Not fixed, and why

**P0.6's other half — uplink and chain continuity across a reboot — is deliberately still open.**
It is not a storage problem. A mound that reboots and re-signs from a stale sequence number forks
its own chain, and a controller that trusts individual signatures will not notice. The fix is
either pre-reserving sequence numbers so a reboot leaves a gap and never a fork, or an explicit
boot epoch the controller reconciles against. The second changes what the controller must do, so it
is a protocol decision and not one to make inside a patch release.

**P0.9's other half — the write path itself.** A corrupt or interrupted write is still only as safe
as NVS's own atomicity; nothing above it is checksummed, and none of it is tested. That needs a
fault-injecting kv layer in the fake HAL, which is a slice of its own.

### Verified

585 C# tests (unchanged this release, run to confirm the version bump moved nothing), 2,576 C
checks under gcc and clang at `-O0` and `-O2` and again under ASan + UBSan with recovery off, all
three ESP-IDF images built under v5.3.2, acceptance 18/18 on the firmware leg and 15/15 applicable
in memory, and the simulator's lifecycle claims. Not run: any of it on real hardware.

---

## v0.9.34 — P0.7: a bounded audit path that is not rewritten whole

Roadmap P0.7, storage half. **Wire change, additive:** `mound_sync` gains `spilled_envelopes`;
`device-session.txt` regenerated. Storage format change, migrated in place.

### What was wrong, measured

`DurableUplinkQueue` kept an unbounded `List<Envelope>` and reserialized **the entire queue** into
one document on every mutation. Measured on this repository's own `FileStateStore`:

| queued | per-enqueue | largest document |
|---|---|---|
| 100 | 9.3 ms | 60 KB |
| 1,000 | 9.1 ms | 608 KB |
| 4,000 | 12.2 ms | **2.4 MB** |

Every action record cost a full rewrite of everything queued before it, and nothing ever stopped the
growth — the only thing that ended it was the disk filling. On a Pi's SD card that is write
amplification measured in gigabytes per day of outage.

After:

| queued | per-enqueue | largest document |
|---|---|---|
| 100 | 3.9 ms | 605 B |
| 1,000 | 1.5 ms | 607 B |
| 4,000 | **2.7 ms** | **609 B** |

### Fixed

- **Segment storage.** One small document per envelope, keyed by zero-padded sequence, plus a head
  holding the watermarks and counters. An enqueue writes one segment and the head; an
  acknowledgement deletes the segments it covers. There is no index to enumerate — the head knows
  the range and restore walks it, treating a missing segment as a gap rather than an error, which is
  exactly what a spill leaves behind.
- **Bounds:** 5,000 items and 8 MiB by default, both configurable. The byte bound is the one that
  protects a small device, since envelope sizes vary by orders of magnitude between a beat and an
  action record carrying inline evidence.
- **Spill, counted and reported.** Oldest-first when a bound is hit, never the last envelope
  standing. The chain head does not retreat, so what the controller receives still verifies as a
  chain with a visible gap — and the count now rides the beat as `spilled_envelopes`, so the gap can
  be explained rather than merely noticed.
- **Migration.** A queue written in the old whole-document format is moved into segments in place
  rather than dropped: those are signed records a controller has not seen, and "we changed our
  storage format" is not a reason to put a gap in a chain.
- The C mirror sends the same field and always `0` — a reduced-profile device refuses to record when
  its queue is full rather than dropping, which is the stricter behaviour.

### Added

Five `UplinkQueueTests`: segments-not-one-document, the item bound with its counted spill, the byte
bound holding where the item count does not, watermarks surviving a restart, and the old-format
migration. **The two bound tests were confirmed red against the unfixed code.**

### Noticed, not fixed

`canonical-envelopes.txt` pins a `mound_sync` body of `{state, uptime_s}` — a shape *neither*
implementation emits; both send `{state, queue_depth, …}`. The fixture's copy is hand-built in
`test_golden.c`, so nothing was checking the real beat body against it. Recorded here rather than
changed silently, because reconciling it is a fixture decision, not a bug fix.

585 C# tests, 2,567 C checks, acceptance 18/18.

## v0.9.33 — P0.4 / P0.5: a restart does not reset what the mound owes

Two defects with one shape: state that governs whether physical work may happen lived only in
memory, so a reboot handed it back. No wire change.

### P0.5 — a restart reset the duty cycle

`ActuationHistory` was two in-memory dictionaries and nothing wrote them anywhere, so `min_off_s`
and `max_rate_per_h` — limits the manifest declares and the kernel enforces on every actuation —
began empty on every boot. A 300 s cooldown was enforced before a restart and gone three seconds
after one, which made rebooting a way to actuate as often as you liked.

It now persists under its own cache key (its own, not folded into the authority snapshot: it changes
on a different cadence and outlives any particular charter by design — a relay's minimum off-time is
a property of the relay, not of the paperwork), written after every path that can actuate and
restored before anything may ask the hardware for more.

### P0.4 — a completed mission could be replayed

The handled-downlink set was an in-memory `HashSet<string>`, so a mound's memory of what it had
already done evaporated on restart. Every other durable protection was in place — the checkpoint,
the authority, the queue — and this one was simply never written down.

- **The ledger is durable, and keyed on both the envelope id and the mission id.** The second is
  the one that matters. The envelope-id check catches a controller re-sending the same bytes; it
  does **not** catch a controller that re-queues the work, which mints a fresh envelope around the
  same mission — and that is the case that actuates twice. A redelivered mission is refused with an
  ack that says so and names the remedy: issue a new mission id to run it again.
- **Bounded by a validity horizon, not a count**, cutting both ways. A bounded ledger has an edge —
  an id that ages out becomes executable again, which is replay by expiry — so entries older than
  24 h are pruned *and* an envelope claiming a `sent_at` older than the horizon is refused rather
  than run, because the mound can no longer prove it has not already done it. `sent_at` is inside
  the signature, so this cannot be dodged. A **stop is exempt**: idempotent by construction, and a
  stale stop is still a stop.
- A hard cap of 2048 entries sits beneath the horizon as a memory backstop; entries it drops while
  still inside the horizon are **counted and reportable**, not silently forgotten.

### Removed before release — a mechanism that guarded the wrong direction

An earlier draft of this slice added a `max(now, latest_known)` reference to `ActuationHistory` so a
clock step could not refresh a rate budget. It was cut, because the two tests written for it passed
with it reverted — which meant it was proving nothing. The analysis it was built on was backwards:
a **forward** jump is the dangerous direction for a trailing window (it ages starts out early), and
`max(now, …)` only guards the backward one, which was already conservative. The real fix is aging
the window on monotonic elapsed time as `v0.9.31` did for holds, and across a restart the gap is
genuinely unknowable, so it needs a policy decision rather than a mechanism. Recorded as the
remaining half of P0.5 rather than shipped as a placebo.

### Added

- `A_cooldown_survives_a_restart` — actuate, reboot three seconds later, and the 300 s `min_off_s`
  still refuses. **Confirmed red without the fix.**
- `A_completed_mission_redelivered_after_a_restart_does_not_run_again` — the controller re-queues
  the same mission around a new envelope after a reboot. **Confirmed red without the fix.**
- `An_envelope_older_than_the_replay_horizon_is_refused_not_run`.

580 C# tests, 2,567 C checks, acceptance 18/18.

## v0.9.32 — P0.3: a stop acts on receipt, not after the drain

Roadmap P0.3. No wire change.

### What was wrong

`RunnerAnt.Sync` deferred every non-ack downlink until the drain loop settled, then sorted the
deferred set so a stop preceded a charter, a config and a mission. That answered the **ordering**
question — and left the **latency** one unasked. The stop still waited out every remaining exchange
in its batch, and every further batch after that.

Measured on a twelve-mission backlog: **the stop took 64 exchanges to take effect.** A backlog is
precisely the situation in which someone reaches for the stop, so "how much is queued" was an input
to how fast the mound stopped.

### Fixed

- **An authenticated stop is handled the moment it verifies**, inside the drain loop, and ends that
  drain — the queue is durable and the backlog goes out on the next beat, under a mound that is now
  halted. Same test, same backlog: **1 exchange.**
- **A sync beat drains at most `RunnerAnt.MaxBatchesPerSync` (16) batches.** An unbounded drain is an
  unbounded window in which nothing else on the thread runs — the hold release, the watchdog kick,
  and a stop arriving in a later batch.
- `HandleOne` extracted so the immediate path and the deferred path share one implementation: a stop
  must not acquire subtly different semantics for arriving early, and the duplicate check is the
  same either way.

### Added

- `A_stop_takes_effect_on_the_exchange_that_delivered_it_not_after_the_backlog_drains` — twelve
  queued missions, one envelope per exchange, a stop ordered mid-backlog. **Confirmed red against
  the unfixed code (64 exchanges) before being kept.**

577 C# tests, 2,567 C checks, acceptance 18/18.

## v0.9.31 — P0.2: a deadline a slow sync or a stepped clock cannot stretch

Roadmap P0.2. No wire change; no new fixture.

### What was wrong

The tick took **one** `DateTimeOffset.UtcNow` at the top and reused it for the hold release at the
bottom — with a blocking network exchange in between:

```
Beat(now) → watchdog → lease → Sync(now)  ← TLS, DNS, a slow controller, a 30 s timeout
                             → PollHealth(now) → ServiceActuations(now)   ← the SAME now
```

Whatever the sync cost was time a held line never saw. A 5 s hold, a tick a second in, and a 10 s
exchange left the line hot with eleven seconds elapsed — and it stayed hot until some later tick's
timestamp happened to pass the deadline. Separately, the hold's deadline was a wall-clock instant,
and a wall clock can be stepped: an NTP correction jumping backwards postponed a release that was
physically already due, by however far the clock moved.

### Fixed

- **Due holds are released before the blocking sync as well as after it.** The cheap half: a hold
  already past its deadline does not wait behind a network round trip.
- **The tick accounts for what the sync actually cost.** Measured monotonically and *added to the
  caller's clock*, rather than re-reading a wall clock the caller did not supply — which keeps the
  injected-clock discipline this codebase runs on, so a test with a fake provider reproduces the old
  tick exactly while a daemon sees the time that really passed.
- **The lease is re-checked on the far side of the sync**, since it can run out while we are in
  there and everything after would otherwise act on expired authority.
- **A hold carries two deadlines and releases on whichever comes first** — the wall-clock instant it
  always had, plus a monotonic stamp and duration. Earliest-wins is the fail-safe direction: a
  backwards clock step cannot extend a hold, a forward one at worst releases early, and de-energizing
  early is safe where holding late is the failure the bound exists to prevent.
- `MoundService` and `DigitalActuatorDriver`/`DigitalActuatorFactory` take an optional `TimeProvider`,
  defaulting to `TimeProvider.System`.

### Added

- `A_slow_sync_does_not_buy_a_held_line_extra_time` — a transport whose exchange really costs ten
  simulated seconds; the same single tick now releases the line.
- `A_wall_clock_stepped_backwards_cannot_extend_a_hold` — six seconds elapse, the clock is corrected
  an hour backwards, the line still comes down.
- `A_hold_neither_clock_calls_due_is_not_released` — the other direction: no early release.
- `FakeMonotonic` and `SlowTransport` in `MoundServiceTests`.

**Both regression tests were confirmed red against the unfixed code before being kept.**

576 C# tests, 2,567 C checks, acceptance 18/18.

## v0.9.30 — P0.1: a verify step says what it expects, and the Witness checks

The largest correctness gap in the repository, closed. Roadmap P0.1.

**Wire change, additive:** `expect` on a mission step, `required_features` on a mission, `features`
in the enrollment body. `canonical-bodies.txt`, `canonical-envelopes.txt` and `enroll-exchange.txt`
regenerated. No behaviour changes for a mission that carries none of them.

### What was wrong

`WitnessAnt.Confirm` established that a confirming observation existed, was fresh, and was captured
after the action began — and then handed it to the evidence gate. **Nothing anywhere compared what
it saw against what the action was supposed to achieve.** A limit switch reporting "open" after a
close command confirmed the close. The mound reported `succeeded`, the controller held a signed
record saying so, and every layer in between was honest about a fact nobody had checked.

Everything the repository says about verification — "the second sense is not redundancy", "commands
are not evidence", the whole reason `digital_sensor` was added in `v0.9.27` — depends on the second
observation being tested against a claim. There was no claim.

### Added

- **`StepExpectation`** (`expect` on a `verify` step): the closed operator set a condition already
  uses (`lt`/`lte`/`gt`/`gte`/`eq`/`neq`), a `tolerance` for analog readings where exact equality on a
  double is a test that never passes, and an advisory `unit` recorded in the refusal text and
  **never converted** — a mound does not silently reinterpret a number. Same shape as
  `StepCondition` on purpose: one operator, one number, no expression language.
- **The Witness evaluates it.** A disagreeing observation degrades the confirmed action to
  `unverified` with both sides named — *"expected 'eq 1 closed', observed 0"*. An observation with no
  readable value degrades too, with a **different** reason, because "the hardware did not do it" and
  "I could not tell" call for different responses from whoever reads the record. An assertion that
  could not be tested has not been met.
- **`IWitnessAnt.Confirm` takes the expectation as a required parameter**, not an optional one:
  every implementer has to decide what it does with a postcondition, and silently ignoring one is
  the bug this exists to fix.
- **Validation.** An `expect` is legal only on a `verify` step that also names `confirms` — an
  expectation asserts the effect of an action, so there has to be an action to judge. An unknown
  operator is refused at validation rather than read as "not met", so the mistake is named before
  the valve moves; a non-finite value or tolerance is refused too.
- **Feature negotiation, both halves** — because neither alone is sufficient:
  - `required_features` on a mission: a runtime that does not recognise every name refuses the
    mission **whole**, before any step runs. The failure this prevents is silent — a semantic
    addition an older runtime ignores rather than rejects. It cannot retrofit a refusal into
    runtimes that already shipped, which is what the other half is for.
  - `features` in the enrollment body (§3): the named semantics **this build** implements, so a
    controller knows what a device will enforce before it sends work that depends on it. Injected
    like `driver_schemas` rather than fixed, because what a device can honour is a property of the
    build — a reduced-profile device advertises `[]` and that is the correct answer, not a gap: it
    decodes no missions at all, so it implements none of these semantics. The C mirror sends the
    empty list and `enroll-exchange.txt` pins both ends.

### Changed

- **Acceptance criterion 11 now tests agreement, not presence.** The harness mission carries
  `expect: eq 1 closed`, so the criterion asks what `ACCEPTANCE.md` promised it would once this
  landed. Criterion 12 is its mirror and still passes: the same mission with the witness blinded
  must not confirm. 18/18 on the firmware leg, 15/15 applicable in memory.
- `ACCEPTANCE.md`'s "criterion 11 is weaker than its name" qualification is struck, closed.
- `UPSTREAM.md` gains the amendment note — including `v0.9.27`'s `settle_s`, which had been missed —
  and the new refusal reason a controller should expect to surface.

573 C# tests (10 new), 2,567 C checks, acceptance 18/18.

## v0.9.29 — P0.8: the safe-state walk is per driver on every path that reaches it

**This narrows nothing and widens nothing; it makes an existing guarantee actually hold.** Roadmap
P0.8, and the first thing that roadmap said to do.

`MoundHost.EnterSafeState()` has always been careful: `try`/`catch` per driver, a reported trip on
failure, the whole walk under `_safeGate`. But `WatchingForSafeState` — the path taken on **every**
transition into stopped or quiesced, from a sync, a mission, or (since `v0.9.27`) an expired lease —
walked the drivers itself:

```csharp
foreach (var driver in _drivers) driver.EnterSafeState();
```

The first driver to throw ended the walk. Every driver after it in the list stayed **energized,
during a stop**, with no trip recorded and the exception escaping into whichever caller happened to
trigger the transition. It also ran outside `_safeGate`, racing the watchdog thread's own safe-state
path. `MoundHost.Restore`'s cold-start walk had the same shape.

Both now call `EnterSafeState()`. That is the entire fix.

### Why it was invisible

Every existing safe-state test used a manifest with **one** actuator, so there was never a "driver
after the one that threw" to be left hot. The two regression tests added here use two, which is the
smallest bench that can see it at all.

### Added

- `A_driver_that_refuses_to_go_safe_does_not_keep_the_others_energized` — two actuators, the first
  refusing to de-energize; on `Stop()` the second is still driven safe and the failure is recorded
  as a trip rather than swallowed.
- `An_expired_lease_de_energizes_every_driver_it_can_even_when_one_throws` — the same guarantee on
  the idle-tick lease path `v0.9.27` added. It also pins the escalation that follows: a driver that
  will not go safe is a trip, the tick's watchdog response turns a trip into a persisted stop, so
  the mound ends up **stopped** rather than merely quiesced. That is the stricter state and the
  correct one — a mound that cannot prove its hardware is safe is treated as unsafe.
- `RefusesToGoSafe` and a two-actuator manifest in `MoundServiceTests`.

558 C# tests, 2,567 C checks, the acceptance sequence 18/18 on the firmware leg.

## v0.9.28 — the phase plan: what has to be true before someone else can depend on this

Documentation only. No code, no wire change, no behaviour change — but the roadmap it replaces was
making claims this release retracts, so it is a release rather than a drive-by edit.

`docs/ROADMAP.md`'s milestones answered *what has to be true before something can move*, and that
question is nearly answered. The work after it does not decompose the same way: it is correctness
debt, an extension model, hardware qualification, and optional device packages that have to ship
independently. So the roadmap gains **eight dependency-ordered phases (P0–P7)** — explicitly not
milestones and not dates — covering trustworthy execution and recovery, the adapter/configuration
model, a qualified core release, motion, network and printer packages, cameras and artifacts,
optional reasoning, and the maintenance and packaging work that starts alongside P0 rather than
after it. Resource budgets are proposed as numbers to validate on real hardware, a stable-release
gate is defined, and a definition of done is written for any new capability.

### Corrected — claims the previous roadmap was making that are not true

- **`v0.9.27` said the acceptance sequence passes. It does, and that is narrower than it sounded.**
  Two qualifications are now recorded in both `ROADMAP.md` and `ACCEPTANCE.md`: the target's bench
  inventory names a stepper/servo axis and a position encoder that no shipped driver provides, so
  the eighteen criteria exercise the relay/switch/ADC scenario only (phase P3 closes that); and
  criterion 11 is weaker than its name — it establishes that something independent looked, not that
  what it saw agrees.
- **Two "known gaps" were stale.** The evidence store's disk backing landed at `v0.9.12`, and host
  driver de-energizing on stop/quiesce has been `MoundHost.WatchingForSafeState` since M4. Both are
  marked closed, each pointing at the narrower thing that actually remains (P0.7 and P0.8).
- **"Downlink is not hash-chained" was recorded as the gap.** It is a deliberate protocol choice.
  The missing requirement is durable anti-replay (P0.4), which is a different thing.

### Recorded — ten findings, each read out of the named source, none hypothetical

Every one was green through 556 C# tests, 2,567 C checks, the simulator and all eighteen acceptance
criteria, because they are gaps *between* components and the tests check components.

- **P0.1 — a wrong reading confirms an action.** `WitnessAnt.Confirm` checks that a confirming
  observation exists, postdates the action and passes the freshness gate. Nothing compares its
  **value** to the state the action was meant to produce. A limit switch reading "open" after a close
  command yields `succeeded`. The largest correctness gap in the repository.
- **P0.2 — a hold can outlive its deadline.** One `UtcNow` per tick, and the blocking sync runs
  before `ServiceActuations(now)`, so a slow round trip is time the hold never sees; elapsed
  durations ride a wall clock NTP can step.
- **P0.3 — a stop waits for the backlog.** `RunnerAnt.Sync` defers non-ack downlink until the drain
  settles. Stop-first ordering *within* a batch was the intent and works; the latency was not
  considered.
- **P0.4 — a completed mission can be replayed.** The deduplication window is in memory; nothing
  durable records which signed work already ran.
- **P0.5 — a restart resets the duty cycle.** `ActuationHistory` is two in-memory dictionaries;
  `min_off_s` and `max_rate_per_h` start empty on every boot.
- **P0.6 — the C mound forgets its stop, queue and chain anchor** across a reboot; `mm_app` persists
  only seed, controller key, sync interval and token.
- **P0.7 — the audit path is unbounded and rewritten whole.** `DurableUplinkQueue` holds an
  unbounded list and reserializes all of it on every change; the evidence ceiling does not bound
  evidence already embedded in queued envelopes.
- **P0.8 — one throwing driver can leave the others energized.** `MoundHost.EnterSafeState()` is
  isolated per driver; `WatchingForSafeState` — the transition path used by every sync, every
  mission, and (since `v0.9.27`) an expired lease — is a bare `foreach` outside `_safeGate`, so the
  first driver to throw aborts the walk, the rest stay live, and no trip is reported. Routing it
  through `EnterSafeState()` is a one-line fix and the first thing P0 should do. Watchdog escalation
  for a driver that *blocks* rather than throws is the larger half.
- **P0.9 — boot can erase the device's identity.** `app_main.c` calls `nvs_flash_erase()` on the
  ordinary ESP-IDF init errors, which on a mound discards the identity seed and controller key.
- **P0.10 — .NET 9 reaches end of support on 10 November 2026.** The migration to .NET 10 LTS is now
  dated work, and it must not move a signed byte.

Plus **P1.4**: `ApplyManifest` applies authority settings and nothing else — drivers are not
recomposed, declared workers are validated and never registered, and the production routine registry
is created empty. A signed manifest that changes a hardware binding is accepted and then ignored.

### Changed

- `docs/ROADMAP.md`: the phase plan, the two acceptance qualifications, a `P0–P7` status row, and the
  corrected gap list. The M0–M6 record is preserved as written.
- `docs/ACCEPTANCE.md`: a "what a pass here does not claim" section, and a note that both defects the
  harness found were seam defects — which is why the P0 list came from reading seams, not from a
  test going red.
- Version markers to `0.9.28`. `v0.10.0` remains reserved for the P2 boundary: the host running on
  real hardware, qualified.

## v0.9.27 — M5: the acceptance bench, in software — the third primitive, the firmware as a process, and eighteen criteria that run

The largest slice in the line so far, and one phase rather than three: **make the ROADMAP's
acceptance sequence executable before the bench day, against real code all the way down.** Three
parts, in the order they had to be built.

**The wire changed, additively:** a mission step gains `settle_s` (default `0`). `canonical-bodies.txt`
and `canonical-envelopes.txt` are regenerated; the mission envelope's digest changes with it. The C
mirror models no mission steps, so nothing in `firmware/micromound-c` moves. **Authority narrows in
one place and widens in none** — see the two defects below.

### Added

- **A third generic primitive: `digital_sensor`** — a line you read a *fact* from. `IDigitalInput`
  (`Read()` returns the PHYSICAL level and **throws** when the line cannot be sampled: "the switch is
  open" and "I could not see the switch" are different facts, and a backing that returned `false` for
  both would turn a dead input into a confident measurement), `GpioChardevInput` (Linux uapi v2:
  `GET_LINE` as INPUT with a bias, `GET_VALUES` per sample; layout pinned against `linux/gpio.h` and a
  fake kernel), `GpioBias` (`pull_up` default — the wiring of nearly every limit switch — `pull_down`,
  `none`; a floating input is not a reading, it is noise that looks like one), `DigitalSensorDriver`
  (reading `1` when the physical level matches the manifest's `active_high`, a failed read a **fault
  with no reading**), `DigitalSensorFactory` / `GpioChardevSensorFactory`, `link` support, and the
  `DriverSchemaCatalog` entry. **Why it is a primitive and not a convenience:** an actuator produces
  no evidence of its own, so until a mound could read something *the actuation path did not produce*,
  every actuation it performed was honestly `unverified` however well it worked.
- **The same primitive on the board.** `micromound/link/ports/read_pin` and an `inputs` array in
  `hello` (PROTOCOL.md §12): `mm_ports_add_input` (refusing a duplicate, a line already an output, or
  a line that will not read at bring-up), the `read_pin` handler with `400`/`404`/`503` in the board's
  own words, `mm_switch` in `mm_drivers` (the C `DigitalSensorDriver`), an eighth HAL function
  `gpio_read`, and `LinkDigitalInput` / `LinkSettings.OpenInputLine` on the Pi (which refuses a
  polarity the board's compiled table disagrees with, as the output side already did).
  `port-exchange.txt` regenerated with the input scenarios; **2,567 C checks**.
- **`firmware/micromound-c/tools/mm_board_sim`** (`make tools`): the real port server — `mm_ports`,
  `mm_frame`, the same handlers, refusals, compiled `max_on_s` and link watchdog — as a host process
  speaking §12 frames over stdin/stdout, with only the world below its HAL modelled (pins, inputs that
  follow a pin after a travel delay, channels that rise while a pin is driven, injectable faults) and a
  clock that only moves when it is told to. CI builds it under gcc and clang with the library's own
  `-Wall -Wextra -Werror -pedantic`.
- **`firmware/esp32`: `gpio_read` bound, and an optional switch input.** `hal_esp32.c` claims an
  input line with a pull-up on first read (a dry contact to ground is the usual wiring; an output
  pin is refused as an input), and `CONFIG_MM_SWITCH_GPIO` (`-1`, off by default) offers it as
  `sense.valve_closed` on the port server. All three images still build under ESP-IDF v5.3.2:
  1,026,704 B Wi-Fi, 296,560 B serial, 260,560 B port server (+128 B with a switch configured).
- **`src/Micromound.Acceptance`** and **[`docs/ACCEPTANCE.md`](docs/ACCEPTANCE.md)**: the eighteen
  criteria of ROADMAP's "The target", numbered and executable, run against a real `MoundHost` on a real
  `FileStateStore`, real drivers, real ants, a real signed wire — and, on the firmware leg, against
  `mm_board_sim` over a real `LinkPortsClient`. **All eighteen are met on both legs.** Criterion 7 is a
  reflection scan of the five core assemblies for appliance-named public types, because "configuration,
  never a fork" is the property the whole design rests on. `scripts/validate.sh --full`,
  `scripts/validate.ps1 -Full` and CI run it.
- **`settle_s` on a mission step** (PROTOCOL.md §9): a bounded wait before a `sense` or `verify` step,
  so the physical world can catch up with the step before it. Nothing physical is instantaneous, and a
  `verify` that read its switch in the same instant the `act` energised the coil could never confirm
  anything. Bounded at **10 s** because the wait blocks the service tick — heartbeat, hold release,
  watchdog kick — and must stay far inside the default 30 s heartbeat timeout; anything slower is two
  missions. The wait moves the **mission's** clock, and the lease is re-checked on the far side. How the
  wait passes is a seam (`HostOptions.Settle`), so a deterministic bench advances a modelled world
  instead of sleeping.

### Fixed — two defects the acceptance run found, both of which every unit test was green through

- **The `verified` outcome was unreachable for any honest actuator.** `EvidenceGate` demoted any
  record with no evidence refs to `unverified`, and nothing may raise an `unverified` verdict
  afterwards — so a digital actuator, which produces no evidence by design, was `unverified` the
  instant it acted and the confirming read could never lift it. Only a driver that certified its own
  work could be believed, the exact opposite of the rule's purpose. **Fix:** `CapabilityRequest`
  gains `confirmation_expected`; told by the mission that a `verify` step will confirm this action,
  the kernel holds the verdict **open** rather than demoting it, and the Witness settles it. The
  strict rule is untouched — the verdict is held open only for the walk that promised to close it,
  only when the sole thing against the record was that nothing had looked yet, and the Mound Major
  demotes anything still open when the walk ends, **before a single record is published**. A record
  demoted for any other reason, or carrying evidence of its own, is never reopened.
- **A lease only expired when somebody asked.** `QuiesceIfExpired` was called on the mission path and
  at restore, and nowhere else, so an idle mound sat `chartered` with its outputs live for as long as
  nobody happened to send it a mission. A lease is a promise about *time*, and the scenario it exists
  for is precisely the one where nobody is left to ask. **Fix:** `MoundHost.QuiesceIfLeaseExpired`,
  called on every `MoundService.Tick` before the sync beat — de-energizing the hardware and persisting
  the quiesce, so a restart comes back quiesced rather than briefly re-authorized. **This narrows
  authority**, and it is the direction that narrowing should go.

### Changed

- `MoundMajor` and `MoundComposition.Build` take an optional settle seam; `MoundHost.DefaultDriverFactories`
  and `HardwareDriverFactories` register the digital sensor; `GpioSettings.ActiveHigh` extracted so the
  actuator's safe level and the sensor's polarity read the same setting the same way.
- `Micromound.Acceptance` added to `Micromound.sln`. `make -C firmware/micromound-c tools` added to
  both validate scripts and the C-mirror CI job.

## v0.9.26 — M5: the board as the Pi's hands — port requests over the link, the Pi's kernel the only authority

The last undesigned M5 item, chosen and built: the acceptance bench's "a generic driver sends a
bounded request to the ESP32; the ESP32 acts deterministically". The board becomes a port server over
the same §12 framing with the roles reversed; a Pi-class mound's own kernel authorizes everything and
reaches the board's pins and channels through its existing generic drivers by one manifest setting.
No envelope changes. No authority moves to the board — it holds no key and decides nothing — but two
things it keeps for itself are new, and they are why this is safer than an I/O expander.

### Added

- **PROTOCOL.md §12, port requests.** `micromound/link/ports/hello` (discovery and the keepalive:
  profile, firmware, watchdog, tripped, every pin with its polarity and compiled `max_on_s`, every
  channel), `…/write` (`{"pin","level"}`, the level LOGICAL; the board applies polarity), `…/read`
  (`{"channel"}` → volts). `404` unknown pin/channel, `400` malformed, `409` tripped, `503` a line that
  would not drive or a sensor that would not read — never a zero. **The board's own tier:** a pin
  driven active is released by the board when its compiled `max_on_s` passes, whatever the Pi says;
  **the watchdog:** no request for `watchdog_s` drives every pin safe. A release that fails is a trip:
  nothing is driven active again until reboot; driving safe and reading still work.
- **`mm_ports`** (`firmware/micromound-c`): the server — pin and channel tables (a pin that will not
  drive safe at bring-up is not offered), the three handlers with the refusals above, `mm_ports_service`
  (bounds, watchdog, trip), `mm_ports_on_frame` (a request frame in, a response frame out). It needs
  only a monotonic clock. **Fixture `port-exchange.txt`**, written by `test_ports.c` — 29 requests and the
  board's exact answers: bring-up safe levels, hello, a write and its automatic release at the bound, an
  active-low pin, every refusal in the board's words, a failed read, the watchdog firing once per quiet
  period and re-arming on the next request, a line that would not drive active (no hold, no trip), a
  line that would not release (the trip, 409 on the next active write, 503 on the retried release, the
  retry succeeding, the trip standing) — plus the frames around it. 2,530 checks.
- **`LinkPortsClient`** (`Micromound.Drivers`): the Pi side — the request bodies and response parsing the
  fixture pins, `Hello`/`Write`/`Read` with the board's refusals as `LinkPortsException`, a keepalive
  timer, one exchange at a time behind a gate that itself times out (a stuck exchange on a silent board
  cannot make a safe-state write hang; it fails loudly and the host trips, while the board, hearing
  nothing, drives itself safe). **`LinkDigitalOutput`** / **`LinkAnalogInput`**: the `IDigitalOutput` /
  `IAnalogInput` behind the unchanged generic drivers. **`LinkPortsPool`**: one client per serial device,
  discovery by hello, keepalive at a third of the board's watchdog. **The `link` setting** on both
  hardware backings (`GpioChardevActuatorFactory`, `SysfsDigitalActuatorFactory`,
  `Ads1115AnalogSensorFactory` read it first): a line or channel on the board, or the local port when
  empty. Composition refuses fail-closed a board that does not answer, a pin or channel it does not
  offer, a polarity its compiled table disagrees with, or a tripped board. `DriverSchemaCatalog` and
  CONFIGURATION.md describe `link`; the schema test still proves every hardware backing reads exactly
  the described settings. `LinkPortsTests`: the fixture both ways, a fake board over an in-memory
  duplex (logical levels through an active-low pin, refusals, a silent board as a timeout, the keepalive),
  and both factories composing by `link` alone.
- **`firmware/esp32`: a third configuration**, `CONFIG_MM_LINK_PORTS` (`sdkconfig.defaults.ports`): no
  identity, no NVS, no clock — `app_main` runs the port-server loop (UART bytes → `mm_frame` →
  `mm_ports` → UART; bounds and the watchdog once a second on the uptime clock). `board_ports_init`
  offers the relay pin with the CAPS table's hardware `max_on_s` and the probe's channel. Measured:
  **259,360 bytes**, DRAM 12% (12 KB of the library, 9.5 KB of statics). CI builds all three images.
- **`mm_version.h`** (`MM_VERSION`) — the library's version string, reported in the port server's
  hello; `validate.sh` / `validate.ps1` now fail when it disagrees with `Directory.Build.props`.
- `LinkFrame` / `LinkFrameDecoder` moved from `Micromound.Host` to `Micromound.Protocol` — a wire format
  both the bridge and the port client speak, with no I/O of its own. `LinkBridge` stays in Host.

### Notes

- Which arrangement to use. The mound images (Wi-Fi, serial link) make the board a mound: its own key,
  its own charter, its own records, its readings inline on its records. The port server makes it the
  Pi's hands: the Pi's records name the Pi's capabilities, the Pi's evidence store holds the readings,
  and the controller sees one mound with a longer reach. Both are M5; the bench decides which it wants,
  and a board is one flash away from the other.
- What the board still refuses on its own. `max_on_s` and the watchdog are not authority — the Pi's
  kernel already clamped every request under the same bound — they are the board declining to trust a
  silent or faulty master with a live load. That is SAFETY.md's rule for every layer: catching a fault
  above you does not de-energize; make yourself fail-safe.
- The serial device is opened as a plain file, as the bridge does; a board silent mid-exchange holds
  that one exchange until bytes arrive, and every other caller times out at the gate (see
  `LinkPortsPool`'s remarks). A timed serial layer can replace the opener without touching the rest.

---

## v0.9.25 — M5: the Pi↔ESP32 link — the same exchanges, framed over a serial cable to a bridge

The "compact versioned Pi↔ESP32 packet protocol" the roadmap has carried since M5 was named, now
specified (PROTOCOL.md §12), pinned by a fixture both sides must reproduce, implemented at both ends,
and built into a second firmware image a third the size of the first. No change to any envelope: the
link carries the signed protocol byte for byte, and a bridge that altered one would only produce
something the other side refuses. No authority moves — the bridge holds no key.

### Added

- **PROTOCOL.md §12.** A frame is `"MM" ver(1) type(1) seq(1) len(2 LE) payload crc32(4 LE)`, the
  CRC the IEEE 802.3 (zlib) CRC-32 over everything before it; a request payload is `path '\n' body`,
  a response payload `status '\n' body`, with status `0` meaning the bridge could not exchange at all
  — read by the board exactly as its own HAL reads "offline". A response echoes the request's `seq`;
  another `seq` is a late answer and is ignored. One request the bridge answers itself:
  `micromound/link/time` → `{"epoch_s":N}`. Nothing outside `micromound/v0/` is relayed (404).
- **Fixture `link-frames.txt`** (`tests/Micromound.Tests/Golden/LinkFramesTests.cs`): the CRC-32 of
  `123456789` and of nothing, and eleven frames — beats, an enrollment, the time, an empty body,
  nothing downlink, one envelope down, offline, refused, a controller error, a wrapped `seq` — as
  `Micromound.Protocol.LinkFrame` encodes them. Added to the CI golden guard.
- **`Micromound.Protocol.LinkFrame` / `LinkFrameDecoder`**: the codec and an incremental decoder that
  resynchronises on the magic and drops — and counts — the wrong version, an oversize length, a bad CRC.
  **`LinkBridge`**: the Pi side — reads request frames off any `Stream`, relays each to the controller
  over HTTPS byte for byte (`application/json`, the daemon's timeout), frames the status and body back,
  answers the clock, refuses off-protocol paths, counts requests/relayed/offline/refused.
  **`micromound --bridge <device> --controller <url>`**: runs only the bridge — no mound, no key —
  reopening the device when the board is unplugged. Tests: the relay is untouched and echoes `seq`; an
  unreachable controller is status 0 and a 409 is a 409; the time; four off-protocol paths refused and
  never seen by the network; a malformed payload is 400; `Serve` over a stream with junk, a stray
  response frame, and two requests answers in order.
- **`mm_frame`** (`firmware/micromound-c`): the same codec and decoder in C99, no allocation, with the
  same drop-and-resync rules and counters. **`mm_serial`**: the HAL's `http_post_json` over a byte
  stream (two callbacks: write all, read one byte with a timeout) — first-byte and inter-byte timeouts
  are offline; stale responses are skipped; `mm_serial_time` asks the bridge's clock. `test_frame.c`:
  every fixture frame encoded and decoded byte for byte; the decoder's rules (garbage, a bad CRC, the
  wrong version, oversize, joined mid-frame, a lone magic byte, back-to-back frames, refused encodes);
  `mm_serial` over a fake pipe with a scripted bridge (a beat, a 409, a 500, offline, a silent bridge,
  a stale answer first, the time, a truncated body, 260 requests through the `seq` wrap). 2,402 checks.
- **`firmware/esp32`: two link configurations.** `CONFIG_MM_LINK_WIFI` (as before) or
  `CONFIG_MM_LINK_SERIAL`: `hal_esp32.c` swaps `http_post_json` for `mm_serial_post_json` over a UART
  (`uart_driver_install`, `uart_read_bytes` with the link's timeouts), asks the bridge for the clock at
  boot and hourly (`settimeofday`), and `app_main` brings up no Wi-Fi and no SNTP. `sdkconfig.defaults.serial`
  is the overlay (UART 0 — the USB cable that flashes the board — with the console off, since they share
  it). Measured: the serial image is **295,940 bytes** (81% of the partition free, DRAM 35% used) against
  the Wi-Fi image's 1,026,224 — the linker drops Wi-Fi and TLS entirely. CI builds both.
- `docs/DEPLOY.md` §7: a board on the bench through the Pi — `stty … raw -echo`, `micromound --bridge`.

### Notes

- What the link is and is not. It makes a Pi the board's *transport*: the board is still enrolled
  upstream under its own key, chartered by the controller, and its records are its own. It does not
  make the Pi the board's *authority* — the acceptance bench's "a generic driver sends a bounded request
  to the ESP32" is a different arrangement, on top of this link, and is now the one M5 item left
  undesigned (`firmware/esp32/README.md`, "What is still ahead").
- The bridge opens the device as a plain file (`FileStream`), not through `System.IO.Ports`, so the
  line discipline must be set first (`stty … raw -echo`) — and so any byte stream with a path works,
  a pseudo-terminal included.
- `MM_FRAME_MAX_PAYLOAD` (8192) equals `MM_LINK_RESPONSE_CAP`: a downlink batch the link cannot carry
  is answered as status 0 by the bridge rather than truncated into something the board would misread.

---

## v0.9.24 — M5: readings reach the controller — `evidence` rides on the action record

**This release changes the canonical wire bytes of every `action_record`** — the in-place v0
amendment PROTOCOL.md §11 reserves for "before the first firmware ships", and the last one: the
firmware image now exists and the next such change is a version bump. No authority is widened or
narrowed; no refusal reason changes. The question `v0.9.22` wrote down in §8 — how a constrained
device's reading reaches the controller when the reduced profile has no `evidence_bundle` — is
answered the way §8 always said it would be: the readings ride on the action record.

### Changed — wire

- **`ActionRecord.evidence`** (`"evidence"`, after `evidence_refs`, always present, `[]` when empty): the
  referenced `EvidenceItem`s themselves — `evidence_id`, `type`, `captured_at`, `source`, `payload_json`,
  `content_digest`. A reduced-profile device inlines every item its `evidence_refs` name (it has no
  bundle kind and no store); a Pi-class mound leaves it empty and keeps sending `evidence_bundle`s from
  its store, pressure accounting and all. A reader resolving refs looks in `evidence` first, then in its
  store; an item in both is the same item (PROTOCOL.md §6).
- **Regenerated fixtures**, reviewed line by line: `canonical-bodies.txt` (the `action_record` row gains
  `"evidence":[]`), `canonical-envelopes.txt` (the seq 1 record's bytes change, so every digest and
  `prev_digest` from seq 1 on re-chains; seq 0 is untouched), `kernel-decisions.txt` (all 32 records gain
  `"evidence":[]` — the host kernel inlines nothing, and the C kernel replays it to the byte),
  `device-session.txt` (re-signed; every `succeeded`/`clamped` record a device sends now carries its
  proof inline — a `reading` with `{"value":22.5,"unit":"C","capability":"sense.temp"}`, a relay's
  `sensor_window`). `canonical-signed.txt` (no action record), `canonical-strings.txt`,
  `canonical-doubles.txt` and `enroll-exchange.txt` did not move.

### Changed — host

- `EvidenceCorrelator.For` resolves a record's inline items before the store; `SimController` stores a
  record's inline items as evidence and acknowledges their ids, as it does for a bundle's.
- `DeviceSessionTests` now also checks, for every device record, that `evidence` carries exactly the
  items `evidence_refs` name, each `captured_at` canonical and `source` the capability, every `reading`
  readable by `EvidenceReadings.TryRead`, and that the host's `EvidenceGate` — given the record's inline
  items and nothing else — grants the outcome the device claimed.

### Changed — C

- `mm_bodies`: `mm_evidence_item` and `mm_write_evidence_item` (the EvidenceItem shape); `mm_action_record`
  carries `evidence`/`n_evidence`; and `mm_body_evidence_bundle`, a full-profile body the device never
  sends, so the item shape is pinned against the golden bundle (`canonical-envelopes.txt` seq 2 and the
  `canonical-bodies.txt` row are now rebuilt byte for byte too — four of the six chain envelopes).
- `mm_decode`: `mm_evidence_item_in`; `mm_action_record_parse` reads `evidence` (null, unknown members,
  capacity `MM_MAX_EVIDENCE_IDS`); `mm_action_record_bind` re-encodes it. A record with an inline reading
  round-trips to the same bytes (`test_decode.c`).
- `mm_kernel.inline_evidence` — off, the kernel is the host's kernel to the byte (`kernel-decisions.txt`);
  on, the items an executor produced are copied into the record's `evidence` with the refs. `mm_device`
  turns it on at init: the reduced profile's one behavioural difference lives in the profile's own layer,
  not in the kernel.
- `mm_action_record_in` grew from 2.7 KB to 9 KB (16 items × 392 B); `mm_outcome` to 5.5 KB. Both are
  stack objects on the act path; the 32 KB task in `firmware/esp32` covers it, and the image grew by
  ~600 B. Host tests: 1,975 checks under gcc, clang, `-Os`, `-O1`+sanitizers, and `MM_DEVICE_QUEUE=8`.

### Notes

- Why on the record and not a bundle. The reduced profile keeps `evidence_bundle` out on purpose (a CI
  guard enforces it): a device has no evidence store, so the store's eviction and spill accounting has
  nothing to report; and one envelope per reading would halve a constrained queue. A record that carries
  its own proof is one envelope, verifiable on its own, and reads the same at the controller whether a Pi
  or a board sent it.
- Why the Pi does not inline too. It could, and the bytes would be legal; it would also duplicate every
  item on the wire (bundle and record) for no reader that needs it. If a controller ever prefers
  self-contained records from Pis as well, the switch is one line in `RunnerAnt`, not a protocol change.
- ANTHILL: parses and verifies the amended records as-is (unknown members are ignored; signatures cover
  the received bytes) but sees no device readings until it compiles against `v0.9.24` — `docs/UPSTREAM.md`
  now carries a section for exactly this kind of note.

---

## v0.9.23 — M5: the ESP32 firmware compiles

`firmware/esp32` built under ESP-IDF v5.3.2 for the `esp32` target: `micromound_esp32.bin`, 1,025,484
bytes. The first real toolchain to see the board project, one release after it was written. No wire
change; no new refusal reason; nothing about the kernel changed. **Not yet flashed or run on a board.**

### Changed

- **`firmware/esp32` compiles.** `idf.py set-target esp32 && idf.py build` under ESP-IDF v5.3.2 (xtensa
  gcc 13.2) succeeds with no warnings from MicroMound's own files. Measured at link: image 1,025,484 B
  (33% of the 1.5 MB app partition free); `libmicromound_c.a` 37.8 KB of flash code + 2.3 KB rodata;
  `libmain.a` 40.2 KB of `.bss` — the static `mm_app` (device queue, link buffer, HAL context) — with
  DRAM 41.5% used and 105 KB left for Wi-Fi and the TLS handshake; IRAM 73% (all ESP-IDF's).
- **`MM_DEVICE_QUEUE=8` project-wide on the board** (`CMakeLists.txt`, `idf_build_set_property` so every
  component agrees on `sizeof(mm_app)`): an uplink queue of 8 envelopes — a beat and seven records; a
  full queue refuses to record and audits it, as before — saves 16 KB of DRAM against the host default
  of 16. The host tests pass at both capacities (`make test EXTRA_CFLAGS=-DMM_DEVICE_QUEUE=8`).
- **Two audit lines in `mm_device`**, composed from ids and a mound name, are now built in a
  `MM_DETAIL_CAP` buffer and bounded to `MM_REASON_CAP` by `audit()` (they were composed directly in the
  smaller buffer; gcc 13's format-truncation heuristic at `-Og` refused it, correctly — the line could
  exceed the buffer with maximal ids). Same text on the wire and in the audit; nothing else moved.
- `hal_esp32.c`: `ADC_ATTEN_DB_12` used directly (it is an enum, not a macro, so the `#ifdef` fallback to
  the deprecated `_DB_11` name never took and warned).
- **CI: the `esp32` job is no longer advisory.** It builds `firmware/esp32` with `esp_idf_version:
  v5.3.2` on every push; a red job now means the image stopped building.
- Docs: esp32 README status line ("compiles; not yet flashed or run"), micromound-c README portability
  note on the target toolchain, ROADMAP M5, README.

### Notes

- What "compiles" does and does not say. It says the HAL binding uses the IDF v5.3 API surface
  correctly by the compiler's lights and that the whole image links within its partition and its RAM.
  It does not say the board enrolls, beats, or survives a TLS handshake on the heap that is left — those
  are the bench slice, with a heap high-water-mark reading as its first deliverable.
- Toolchain notes for whoever builds next: `IDF_PYTHON_CHECK_CONSTRAINTS=no` needs
  `idf-component-manager~=1.5` pinned by hand (the latest one rejects v5.3's interface version);
  `IDF_GITHUB_ASSETS=github.com` fetches the toolchain from GitHub releases where dl.espressif.com is
  unreachable. Neither is in the repository; both are the environment's problem, not the project's.

---

## v0.9.22 — M5: the board layer, host-simulated — enrollment, transport, drivers and the service loop in C; the ESP-IDF project exists

Everything a constrained board runs above its hardware, written over a seven-function hardware
abstraction and proven on the host against a fake of it — and, for the first time, an ESP-IDF project
that binds that abstraction to a real chip. No wire change; no new refusal reason. One protocol gap
is now written down rather than implied (see Notes).

### Added

- **`mm_hal`** (`firmware/micromound-c/include/mm_hal.h`) — what a board supplies, and nothing else:
  `now` (UTC epoch seconds; zero until the clock is set), `random_bytes`, `http_post_json` (one JSON POST;
  `-1` means no exchange happened — offline), `kv_get`/`kv_set` (protected storage), `gpio_write`,
  `adc_read`. Four storage keys, all within NVS's 15-character limit: `mm.seed`, `mm.ctl_pk`, `mm.sync_s`,
  `mm.token`. The library stores nothing else on a board's behalf.
- **`mm_enroll`** (`mm_enroll.h`, `src/mm_enroll.c`) = `Micromound.Host.HttpEnrollmentClient`, PROTOCOL.md §3:
  the same request body (same members, same order; `driver_schemas` is always `[]` — a device's hardware is
  compiled in), the same reading of the response (4xx with a `reason` → `enrollment refused: HTTP 409 —
  token already used`; without → `(token burned or unknown)`; 5xx → `controller returned HTTP 500;
  enrollment not yet complete`; no key, a zero key, a wrong-length key, a key bound to another mound, a
  protocol-version skew — each in the host's words), and the same acceptance (`enrolled (controller asks
  for a 10s sync cadence)`). `mm_enroll` persists the controller key **before anything else happens**;
  a key that cannot be stored is not an enrollment.
- **`mm_link`** (`mm_link.h`, `src/mm_link.c`) = `HttpSyncTransport`: POST one envelope to
  `micromound/v0/sync`, split the JSON array that comes down into slices `mm_device` verifies from the
  bytes as received; whitespace is "nothing downlink", a non-2xx is a failed exchange (the queue retries),
  no exchange at all is offline. The response lives in the link's own 8 KB buffer, so the slices stay valid
  exactly as long as `mm_exchange_fn` promises.
- **`mm_drivers`** (`mm_drivers.h`, `src/mm_drivers.c`) — the two generic drivers of the reduced profile as
  kernel executors. **`mm_relay`** = `DigitalActuatorDriver`: the line is driven to its SAFE level at
  bring-up (an active-low relay is never pulsed), energized for an `on_s` the kernel already clamped —
  and capped again at the effective `max_on_s` as a last-resort belt — HELD, and released by
  `mm_relay_service` when the deadline passes or by `mm_relay_safe` on any stop, quiesce or trip. **It
  produces no evidence: a command is not evidence.** A release write that fails keeps the hold pending
  (retried every tick) and reports it. **`mm_probe`** = `AnalogSensorDriver`: one ADC channel, volts ×
  scale + offset, reported as a `reading` evidence item whose payload is `EvidenceReadings.Create`'s
  (`{"value":25,"unit":"C","capability":"sense.temp"}`), captured at the read. A failed read is a fault
  with no reading — never a zero.
- **`mm_app`** (`mm_app.h`, `src/mm_app.c`) — the whole firmware above the HAL, one tick at a time.
  Identity: the Ed25519 seed is created from the board's RNG on first boot, stored, and never
  regenerated (no entropy, or no storage → the app refuses to run: *an identity that does not survive a
  reboot is not one*). Enrollment: until a controller key is stored, every 30 s the app spends the
  one-time token — an outage or a controller error retries with the token kept, a definite 4xx burns it,
  success stores the key and cadence and burns it. The loop: relay holds are released **first**, before
  anything else happens in the tick; quiesce on lease expiry; the beat on the charter's `sync_interval_s`
  (enrollment's until chartered, 15 s failing both); the compiled schedule (a capability request on a
  period) through the kernel, which refuses what it must and records the refusal. The **trip**: a relay
  that will not release stops the mound (`mm_app_status.tripped`), and the beat still goes out so the
  controller hears it. **The app never acts on a zero clock.**
- **`mm_evidence_produced`** now carries the whole item — `type`, `source`, and a 128-byte
  `payload_json` — not just the id and `captured_at` the gate reads, so whichever way readings travel
  upstream (Notes) is a serializer, not a redesign.
- **Fixture `enroll-exchange.txt`** (`tests/Micromound.Tests/Golden/EnrollExchangeTests.cs`): the exact
  request body `HttpEnrollmentClient` sends for a fixed device (the fixture's seed → public key
  `03a107bf…`), and the verdict and detail line for fifteen scripted controller responses — accepted
  with a cadence, key only, a fractional cadence, a non-positive cadence ignored, unknown members
  skipped; refused with a reason, without one, with an unreadable body; a controller error; no key, an
  empty response, a zero key, a short key, another mound's binding, a protocol-version skew. The C
  `mm_enroll_request_body` must equal the `request:` line byte for byte and `mm_enroll_read_response`
  must reach every verdict in the same words. Added to the CI golden guard.
- **`tests/test_board.c`** — `mm_app` against a fake HAL (in-memory kv, a scripted HTTP endpoint that
  plays the controller for both paths and signs with the fixture's controller key, fake GPIO and ADC,
  an RNG whose first 32 bytes are the fixture's device seed): first boot creates and stores the seed; a
  zero clock does nothing; no token → said, not posted; an outage and a 500 retried with the token kept;
  a 200 → enrolled, key and `10` stored, token burned; the first enrolled tick beats, is chartered, reads
  the probe and energizes the relay for the charter's 30 s (50 requested, hardware 60); the charter's
  cadence (15) wins over enrollment's (10); one envelope per exchange (the beat and its two records);
  the hold is released at the top of the tick whose time has come; the probe reads on its period and a
  failed read produces no reading; an outage mid-session keeps the queue and the charter; a reboot on the
  same storage keeps the identity and the enrollment (the RNG is not asked) and is re-chartered; a stop
  releases the relay and the next scheduled actuation is refused, not actuated; a release write that fails
  trips the mound, the hold stays pending, the retry releases it and the trip stands; a 409 burns the
  token and nothing is tried again; a board that cannot store the seed refuses to run. Plus every
  `mm_link_parse_downlink` shape and every `mm_link_exchange` outcome.
- **`firmware/esp32`** is no longer a placeholder: an ESP-IDF project — `main/hal_esp32.c` binds `mm_hal`
  to SNTP's clock (zero until the year is plausible), `esp_fill_random`, `esp_http_client` over esp-tls
  (the root bundle, or a private CA embedded from `main/certs/controller_ca.pem`; **no insecure mode**),
  NVS blobs, `gpio_set_level`, `adc_oneshot` with the target's calibration; `main/board.c` describes this
  board (the fixtures' device: one probe, one relay, a reading a minute, the relay 30 s every ten minutes);
  `main/app_main.c` drives the relay safe before the network is up, waits for the clock, and ticks
  `mm_app` under the task watchdog (a hung loop reboots to a safe board); `components/micromound_c`
  compiles `../micromound-c/src` unchanged; `Kconfig.projbuild` holds URL, mound id, bench token, pins and
  scale; `sdkconfig.defaults` sets the 1.5 MB app partition and a 32 KB main-task stack. **Written against
  ESP-IDF v5.2+ and not yet compiled on a bench** — syntax-checked against stub headers only; the README
  says so on its first screen. A new **advisory** CI job (`espressif/esp-idf-ci-action`,
  `continue-on-error`) is the first real toolchain that will see it.
- Tests: 1,954 checks (from 1,792). gcc, clang, `-Os`, gcc+ASan/UBSan (findings fatal), and now also
  `-O2` with the sanitizers (a `format-truncation` heuristic in the evidence gate's stale-evidence line
  was silenced with the fields' own precisions; no behaviour change).

### Notes

- **An open question in the reduced profile, now written down (PROTOCOL.md §8).** An `action_record`
  carries `evidence_refs` — ids — not values, and the profile has no `evidence_bundle`; so a constrained
  device today takes a reading, gates its own outcome on it, and reports the outcome and the reference,
  while the controller never receives the number. Two additive answers exist (an `evidence` member on
  `action_record`, or a bounded `evidence_bundle` admitted to the profile); the choice is deferred to the
  bench slice, where the controller's needs are visible. Until then §8's "fixed-shape readings ride on the
  action record" describes the intent, not the wire.
- The host suffixes two lines with an exception message (`controller unreachable; not enrolled yet: …`,
  `enrollment response unreadable: …`, and the transport's `offline: …`); the C library emits the prefix
  alone. The fixture contains no such line, since the suffix is not deterministic.
- `mm_app_status.last_detail` is the last enrollment or sync line; a trip is the `tripped` flag, because a
  beat follows the trip in the same tick and would overwrite any line.
- A relay hold is released when `now` passes the deadline: a clock that jumps forward releases early (the
  safe direction); one that jumps back holds longer. The app never starts a hold on a zero clock.
- The compiled-in enrollment token in `Kconfig.projbuild` is bench provisioning: a burned token is
  re-provisioned on every boot and refused again, once, harmlessly. A fleet provisions NVS directly.
- `firmware/esp32` renamed the storage keys the library had used for a day (`mm.controller_pk`,
  `mm.sync_interval` were 16 characters; NVS allows 15). Nothing shipped with the old names.

---

## v0.9.21 — M5: the device loop — a reduced-profile mound runs end to end, and the controller accepts it

The Runner Ant in C, over everything `v0.9.18`–`v0.9.20` built. A device now beats, records, chains,
signs, drains, receives, verifies, stops, quiesces and refuses exactly as a Pi does — and for the first
time the proof runs in the other direction: a whole session the C device produced is verified by the
host's own verifier, chain validator and typed contracts. No wire change; no new refusal reason.

### Added

- **`mm_device`** (`firmware/micromound-c/include/mm_device.h`, `src/mm_device.c`) = `RecordAnts.RunnerAnt`:
  - **Uplink.** `mm_device_publish` builds an envelope (id from the device's id source, `seq`, `sent_at`,
    `prev_digest` = the chain head), signs it and queues it — a bounded ring of `MM_DEVICE_QUEUE` (16)
    envelopes of `MM_DEVICE_WIRE_CAP` (2048) bytes. **A full queue refuses to record** rather than
    overwrite unacknowledged proof, and says so in the audit. `mm_device_beat` publishes the runtime's
    real `mound_sync` body `{state, queue_depth}`; `mm_device_act` runs a request through `mm_kernel` and
    publishes the `action_record`.
  - **Downlink.** `mm_device_receive_batch` takes one exchange's worth: each envelope is verified from
    the bytes as received under the controller key, parsed, checked once (a 32-id idempotency window —
    re-delivery is silent, not an incident), addressed to this mound, and shaped for the reduced profile
    — or **dropped and audited, never processed, never acknowledged**, with the host's audit wording
    (`downlink <id> (<kind>) dropped: addressed to '…', this mound is '…'`). Acks are handled inline
    and drive eviction (`AcknowledgeThrough`: cumulative, never backwards); everything else is handled
    **stops first, charters second** within the batch. A stop enters the safe state (a callback) and is
    acknowledged `ok` with the host's detail; a charter is accepted silently or refused with the
    validator's reasons in a `refused` ack (`charter refused: …`); any other kind — including protocol
    kinds that never travel downhill, like `action_record` — gets `refused_unknown_kind`.
  - **The beat.** `mm_device_sync` publishes the beat, then drains oldest-first through a transport
    callback (one envelope up, an array down — `ISyncTransport.TryExchange`), stopping when an exchange
    acknowledged nothing; only what was queued at beat time goes up in that beat, so the acks a stop or a
    refused charter provoke wait for the next one, as they do on a Pi. **The acknowledged beat renews
    the lease** (PROTOCOL.md §5) — not the transport returning. Offline is a return value, not an error:
    the queue keeps everything and the next beat resumes from exactly where this one stopped.
  - **The tick.** `mm_device_tick` quiesces when the lease has run out and enters the safe state.
- **Transcript fixture `device-session.txt`** — the first fixture written by the C side and read by the
  host. `tests/test_device.c` runs a scripted session between an `mm_device` (device seed `00 01 02 …`)
  and a fake controller (seed `20 21 22 …`, verifying what comes up with `mm_decode` as ANTHILL would):
  an observe-only beat; a charter; a clamped actuation and a reading; a three-exchange drain with the
  charter re-delivered; a batch in which the controller misbehaves four ways (a stop for another mound,
  a `mission` kind, an `action_record` downhill, a charter tampered after signing); a stop; refused and
  continuing work while stopped; a charter that cannot clear the stop; an outage and the resumed drain;
  an explicit clear, a 30-second lease and its expiry; a `quiesced` beat. Every `up:` and `down:` line
  is the transcript. The C test compares against the committed file byte for byte; **the new C#
  `DeviceSessionTests` verifies every uplink envelope under the device key with `Ed25519KeyPair`,
  checks the wire form is exactly what the host re-serializes, validates the whole chain with
  `EnvelopeValidator.ValidateChain` (across the outage's re-sends), decodes every body through
  `ActionRecord`/`AckBody` and re-encodes it to the same bytes, and checks the session walked all
  four states and all three ack statuses.** Every signature in the file was also confirmed with a
  third, independent Ed25519 implementation (43 verify; the one tampered charter does not).
- Tests: 1,792 checks (from 1,690). gcc, clang, `-Os`, gcc+ASan/UBSan (findings fatal).

### Notes

- One deliberate difference from the Pi: RunnerAnt defers non-ack downlink across the whole drain and
  sorts it once; the device handles each exchange's batch as it arrives (stops first within the batch)
  because it keeps no second copy of what came down. A stop that arrives in a later exchange than a
  charter is therefore applied after it — the end state is the same, and both are audited.
- The drain's progress test is "did anything get acknowledged", not "did the queue shrink": the acks a
  stop provokes are queued during the drain, which would otherwise read as no progress.
- The device generates no ids and no keys of its own here: `mm_device_config` takes an id source (a
  UUID per call — the firmware plugs its RNG; the test a counter) and the 64-byte secret key. The
  controller key is the one enrollment delivers.
- Not yet in C: enrollment (an HTTP exchange, not an envelope — the ESP-IDF project's transport layer
  owns it) and the hold/release timing of a real actuator (`ITimedDriver`), which belongs with the
  drivers-as-executors on the board.

## v0.9.20 — M5: the kernel in C — the same authority boundary, decision for decision

`Micromound.Capabilities` in C. Not a simplified kernel for a small device: the same thirteen checks
in the same order, the same three-tier limit intersection, the same closed set of refusal reasons with
the same detail text, the same records. A constrained controller that refused differently from a Pi
would make "the mound refused" mean two different things — from this release that is a test failure.
No wire change; no new refusal reason.

### Added

- **`mm_kernel`** (`firmware/micromound-c/include/mm_kernel.h`, `src/mm_kernel.c`):
  - **Compiled tables.** `mm_capability_desc` / `mm_routine_desc` are `static const` in the firmware
    image — the id, class, hardware limits, parameter names, required parameters, driver ranges, the
    duration and magnitude parameters, and for a routine the capabilities it drives. `mm_kernel_init`
    validates them with the registries' rules (well-formed ids; `sense.` is observe, `act.` is above
    observe, no `hazardous`; required ⊆ parameters; ranges/duration/magnitude name parameters; a routine
    drives ≥ 1 registered capability of no higher class) and reports the registry's own message.
  - **`mm_authority`** = `KernelAuthority`: the active charter (a complete replacement, never a diff),
    lease, stopped/quiesced, the manifest's device-limits tier and safe state; `accept_charter`
    (CharterValidator against the device's own tables, then "a stop order outranks a charter"),
    `renew_lease`, `quiesce_if_expired`, `stop`, `clear_stop`, `effective_ceiling`, and the four state
    names. `mm_kernel_review_charter` reports widening attempts as the host does ("the hardware bound
    stands"), never as a refusal.
  - **`mm_kernel_authorize`** = `CapabilityKernel.Authorize`: 1 stop precedes everything but observation,
    decided from the namespace before anything resolves; 2–3 resolve, with a routine only as available
    and as permissive as the capabilities it drives; 4 hazardous never; 5 no charter / lease expired /
    class above the ceiling; 6 granted by this charter; 7 the worker's own ceiling; 8 unknown parameters
    refused (not dropped), required present; 9 hardware ∩ device ∩ charter; 10–11 duty cycle and rate
    across every capability the request would move (a routine cannot run a relay inside the relay's
    own cooldown); 12 clamp — driver range, then `max_on_s` on the duration parameter, then `[min, max]`
    on the magnitude parameter — and say what narrowed; 13 an executor must be bound.
  - **`mm_kernel_execute`** = `Execute`: the record built from the decision (refusals carry
    `"<reason>: <detail>"`, a stop refusal's outcome is `stopped`), the executor run through a function
    pointer (a non-zero return is the C# "driver threw" backstop), `ended_at` from the executor or
    `now + duration`, the duty cycle recorded for every history key whether the driver succeeded or
    faulted, `failed` with `driver_fault:` on a fault, `clamped` or `succeeded`, then the **evidence gate**
    ("commands are not evidence": no evidence referenced, missing, unparseable, captured in the future,
    or stale outside the policy window → `unverified`, the reason appended to the clamp note so neither
    fact hides the other).
  - `mm_history` = `ActuationHistory` (last end and starts per key; starts remembered up to 16 per key,
    pruned to the trailing hour as the host does); `mm_limits_intersect` / `mm_limits_attempts_to_widen`;
    `mm_capability_is_well_formed`; the refusal-reason and action-class wire names.
- **Golden fixture `kernel-decisions.txt`**, frozen by the new `KernelDecisionsTests` (C#): a fixed device
  (`sense.temp`; `act.relay_1` benign with hardware `max_on_s` 60 / `min_off_s` 120 / `max_rate_per_h` 4,
  `on_s` required in `[1, 3600]`; `act.dimmer` controlled with `level` magnitude; `act.fan` with no
  executor; `routine.cool` driving the relay under its own `max_on_s` 45), device limit `max_on_s` 40,
  a fixed clock, and **42 scripted steps**: observe-only, a benign charter that tries to widen the
  hardware rate, the clamp to the narrowest tier, duty cycle, class exceeded, unknown/missing parameter,
  executor missing, unknown and malformed ids, an unregistered routine, a worker ceiling, sensing spending
  no duty cycle, a routine clamped by its own bound and refused under the relay's key, driver
  unavailable, the fourth start in an hour and the fifth refused, a lease renewed and run out, actuation
  refused and sensing continuing after expiry, a fresh charter out of quiesce, stop refusing an
  unresolvable id before anything resolves, a charter unable to clear a stop, an explicit clear, an
  observe-ceiling charter, an expired charter and one naming hardware the device lacks, plus the
  evidence gate demoting for no evidence, for stale evidence, and a driver fault that still spends
  the cooldown. Every step records the reason, detail, effective parameters, effective limits, mound
  state and the action record body (`action_id` normalized). **The C kernel replays the script from the
  fixture's own `at:` and `request:` lines and reproduces every line.**
- Tests: 1,690 checks (from 1,273): the registry rules a compiled table could break, `LimitClamp` on its
  own, and the whole script. gcc, clang, gcc+ASan/UBSan (findings fatal).

### Notes

- Two host behaviours the fixture made visible and the C kernel now shares: a refusal decision carries
  no effective limits (they are computed but not reported — `KernelDecision.Refuse` drops them), and a
  driver fault's record still ends at `now + duration` and still spends the duty cycle.
- `MM_DETAIL_CAP` grew from 160 to 320 so a clamp note and an evidence-gate reason fit together in a
  record's `detail`, as they do on the host.
- Capacities of this kernel, all compile-time: 16 capabilities, 8 routines, 4 capabilities per routine,
  16 remembered starts per history key (a `max_rate_per_h` above 16 saturates at 16 — a limit worth
  raising before a device ever needs it, not silently).

## v0.9.19 — M5: the C reader — a device can now receive a charter, a stop and an ack

The other half of `v0.9.18`. The mirror could write every byte a device sends; now it can read every
byte a device receives, verify who sent it from the bytes as received, and refuse it for exactly the
reasons the host would. No wire change, no new refusal reason — the existing closed set, now in C.

### Added

- **`mm_json_read`** — a bounded pull parser over a byte buffer: no allocation, no token table, depth
  capped at 16, every string decoded with the full escape grammar (`\uXXXX`, surrogate pairs) into a
  caller buffer and refused when it is not valid UTF-8 or not valid JSON, numbers checked against the
  JSON grammar before `strtod` (an `int` field refuses `1.0` and `1e2`), `mm_jr_skip` to walk past
  members it does not know (PROTOCOL.md §11: additive fields are always legal), `mm_jr_raw` to hand a
  value on as its exact source bytes. Ten distinct error codes with names for audit lines; the first
  error stops the read.
- **`mm_decode`** — the receive side of the reduced profile:
  - `mm_envelope_parse` (the frame into a fixed struct; the body as a slice) and
    **`mm_envelope_verify_wire`**: a signed envelope on the wire IS its canonical bytes with the
    signature spliced into the last field, so the signature is verified over the received bytes with
    the sig cut out — two `mm_part`s, no copy, no re-serialization — and the digest the next
    envelope's `prev_digest` must carry is computed the same way. A sender that did not emit canonical
    form fails verification and is refused: the fail-closed direction.
  - `mm_envelope_validate` = `EnvelopeValidator.Validate(reducedProfile: true)`, and
    **`mm_charter_validate`** = `CharterValidator.Validate` — the same checks in the same order, the
    same closed set of refusal reasons as fixed strings (`mm_refusal`), including the ones a Pi checks
    and a controller must too: `mound_id mismatch`, `action_ceiling 'hazardous' is never a legal
    charter ceiling`, `charter already expired`, `expires_at precedes issued_at`, `a routine belongs in
    'routines', not 'capabilities'`, `limits key matches no granted capability or routine`, and the
    device-presence checks when a capability/routine registry is given.
  - `mm_charter_parse`, `mm_stop_parse`, `mm_ack_parse`, `mm_action_record_parse` into fixed-capacity
    structs (16 capabilities, 8 routines, 16 limits, 8 required_for patterns, 16 evidence ids, 8
    parameters; names 48 bytes, ids 64) with the C# contracts' defaults for absent members (`observe`,
    `all_actuators_off`, `sync_interval_s` 15, `min_interval_s` 60, ack `through_seq` -1, record outcome
    `unverified`). Over capacity is `MM_JR_TOO_MANY`, over length `MM_JR_OVERFLOW` — refusals, never
    truncations. `*_bind` views re-encode a parsed body through `mm_bodies`, and the golden bodies
    decode → re-encode byte for byte.
  - `mm_capability_pattern_matches` (`*`, `prefix.*`, exact — the whole glob language), `mm_capability_is_routine`, `mm_action_class_parse`.
- **`mm_time`** — `yyyy-MM-ddTHH:mm:ssZ` ↔ epoch seconds (Hinnant's civil-date arithmetic, the same
  proleptic Gregorian/no-leap-second model as `DateTimeOffset`), accepting the offset and fractional
  forms §2 asks readers to tolerate and nothing else; round-trips every day of two leap cycles.
- **`mm_body_stop`** and **`mm_ed25519_sign_parts` / `mm_ed25519_verify_parts`** (a message as
  consecutive pieces; the single-buffer functions are now wrappers).
- **Golden fixture `canonical-signed.txt`** — the one fixture with REAL signatures: fixed test seeds
  (`00 01 02 …` device, `20 21 22 …` controller; never real keys), the device's `mound_sync` beat and a
  controller's `charter` → `stop` → `ack` chain, each as its `wire:` line and `digest:`. Frozen by
  `CanonicalBytesTests.Signed_wire_envelopes_are_frozen` through BouncyCastle; the C tests verify every
  line under the named key from the bytes as received, decode the frame and the body, re-encode the
  body to the identical slice, and re-sign from the seed to the identical wire — TweetNaCl and
  BouncyCastle agreeing on every byte. Every signature was additionally confirmed with a third,
  independent Ed25519 implementation before the file was committed. The CI golden guard covers it.
- Tests: 1,273 checks (from 917) — the reader's grammar case by case (lone surrogates in both
  positions, raw controls, overlong and truncated UTF-8, trailing commas, a 17-deep nesting bomb,
  `9223372036854775808`), the time parser's edges (`0001-01-01`, `9999-12-31`, leap days, `+HH:MM`),
  each validator reason provoked on its own, capacities at the boundary on both sides, and the signed
  fixture end to end. gcc, clang, and gcc+ASan/UBSan with **findings fatal** (`-fno-sanitize-recover=all`).

### Notes

- The UBSan run now fails on any finding. The one exemption is `shift-base`, which TweetNaCl trips on
  purpose (`car25519`, `modL` left-shift negative limbs — a well-known property of that code that every
  two's-complement compiler defines as wrapping); it is named in the Makefile beside the flag set, and
  nothing of MicroMound's own is exempt. The `v0.9.18` sanitizer step reported those two lines and
  passed anyway; it no longer can.
- What a device does with a decoded charter — accept it into authority, clamp against compiled limits,
  run a routine — is the kernel in C: the next slice. `firmware/micromound-c/README.md` says what is
  still missing.

## v0.9.18 — M5 groundwork: the C mirror, host-verified

The first piece of the constrained-controller firmware, built where it can be proven: a portable C
library that produces the exact canonical bytes the C# runtime produces, digests and signs them the
same way, and is checked byte for byte against the golden fixtures by `gcc` and `clang` on the
host. No board yet — that is the point of doing it now. **One canonical-wire-bytes change**, called
out below, with no golden byte affected.

### Added

- **`firmware/micromound-c`** — C99, no dynamic allocation, `-Wall -Wextra -Werror -pedantic`,
  builds as a static library with `make`, dependencies: the C standard library.
  - `mm_json`: the canonical writer — a fixed buffer, comma/nesting tracking, every value type, the
    PROTOCOL.md §2 escaping rule from UTF-8 input (invalid UTF-8 is refused, never guessed), overflow
    reported with the length that was needed.
  - `mm_format`: .NET's `double` text — shortest round-trip digits, plain while
    `-3 <= digPos <= max(digits, 17)`, otherwise `d.dddE±XX`; `-0` for negative zero; NaN and the
    infinities refused.
  - `mm_sha256`: FIPS 180-4, incremental; hex helpers.
  - `mm_ed25519`: over vendored **TweetNaCl** (public domain, byte-identical copy, provenance and
    hashes in `third_party/tweetnacl/README.md`), included rather than linked so it could gain what
    a device needs and TweetNaCl lacks — a keypair from a stored seed with no RNG, and *detached* sign
    and verify that read the message in place (an incremental SHA-512 over TweetNaCl's block
    function). Verify rejects non-canonical `S >= L`, as BouncyCastle and libsodium do. The `randombytes`
    symbol TweetNaCl's own generators need is a stub that aborts: this library never makes a key.
  - `mm_envelope`: canonical bytes with `"sig":""` present and empty, `sha256:` digest, `ed25519:`
    signature, a **strict** verify (wrong algorithm, wrong length, bad hex, bad signature all `-1`),
    and an **in-place splice** of the signature into the last field — one buffer, no re-serialization,
    which is what §2 promised a constrained device.
  - `mm_bodies`: `mound_sync` (the runtime's real `{state, queue_depth}` shape), `action_record`,
    `ack`, and `charter` (with `CapabilityLimits` nulls and the `limits` table), field for field.
    `mission`, `mission_report`, `evidence_bundle`, `config`: deliberately absent (§8).
  - `make test` (917 checks): the escaping rule case by case, invalid UTF-8, the layout rule at both
    boundaries, FIPS SHA-256 vectors incl. the million-`a` and every-split incremental check, RFC 8032
    §7.1 vectors 1–3 (seed → key, signature, verify), tamper cases, the malleated-`S` twin, an invalid
    public key; then the **golden files**: every envelope in `canonical-envelopes.txt` digested and
    chain-linked, the `mound_sync`/`action_record`/`charter` envelopes and both `canonical-bodies.txt`
    bodies rebuilt from their inputs and compared byte for byte, every row of `canonical-strings.txt`
    and `canonical-doubles.txt`, and a **cross-implementation signature**: the `sig` an independent
    RFC 8032 implementation produced over the golden `seq 0` envelope with a fixed seed.
  - Wired into CI as its own job (gcc, clang, clang+ASan/UBSan) and into `validate.sh --full` /
    `validate.ps1 -Full` after the .NET tests, skipped with a notice where there is no C toolchain.
- **Two new golden fixtures**, frozen by `CanonicalBytesTests`: `canonical-strings.txt` (22 escaping
  vectors — quotes, backslash, all 32 controls, DEL, Latin-1, CJK, emoji, U+2028, NBSP, BOM,
  noncharacters, astral endpoints, mixed scripts — plus escaping in a property name) and
  `canonical-doubles.txt` (~300 IEEE bit patterns with .NET's text, including the plain/scientific
  boundary walked on both sides, subnormals, `E+308`, and 200 random patterns; the bit patterns are
  literal in the test so the fixture cannot drift with arithmetic). The CI golden guard covers both.

### Canonical wire bytes

- **String escaping is now a rule, not a runtime behaviour.** `ProtocolJson.Options` used
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, which leaves most non-ASCII literal but escapes a
  Unicode-version-dependent set — measured at 7,886 BMP code points on one runtime, a different set
  on the next. Two mounds on different runtimes would have signed **different canonical bytes for the
  same device name**, and no C encoder can mirror a table it cannot see. `CanonicalJsonEncoder`
  replaces it: printable ASCII literal (`+ < > & ' /` included), `\"` and `\\`, the five short
  escapes, `\uXXXX` with **uppercase** hex for every other code point below U+0020 and every code
  point from U+007F up, surrogate pairs above the BMP. Canonical bytes are therefore pure ASCII.
  **No existing golden byte changed** — every fixture was ASCII already; what changed is what a
  non-ASCII string canonicalizes to, from unspecified to specified. Applied under PROTOCOL.md §11's
  "v0 is fluid until the first firmware ships" — this is the change that makes a first firmware
  possible. `Micromound.Protocol` now compiles with `AllowUnsafeBlocks` for the encoder's `char*`
  overrides, the only unsafe code in the repository.
- The number layout rule was never written down; it is now (PROTOCOL.md §2), derived from the
  runtime and pinned by the fixture — including the correction that the plain/scientific threshold
  is 17 significant positions (`1e16` plain, `1e17` → `1E+17`), not the 15 of the pre-Core-3.0 `G`
  formatter.

### Notes

- The C library is the encoder, digest and signature half of the reduced profile. A JSON *reader*
  for `charter`/`stop`/`ack`, the kernel in C, and the ESP-IDF project are the rest of M5;
  `firmware/esp32/README.md` is updated to say which parts now exist.
- TweetNaCl is slow (tens of ms per signature on an MCU-class core) and chosen for auditability; the
  backend sits behind `mm_ed25519.h` and the tests are the proof a swap did not change the bytes.
- Windows developers without `make`/`cc` see the C step skipped by `validate.ps1`; CI runs it.
- The reference controller (ANTHILL) serializes through the same `Micromound.Protocol`, so when it
  bumps its pin its canonical bytes for non-ASCII strings change in step — the two sides stay
  consistent by construction, which is the whole point of sharing the library. Until then a
  device on `v0.9.18` and a controller on an older pin would disagree only on strings containing
  non-ASCII characters; every fixture and every identifier the runtime generates is ASCII.

## v0.9.17 — ready for the board: check the wiring, refuse to pretend, deploy as a service

Three passes that turn the substrate of `v0.9.14`–`v0.9.16` into something an operator can carry to a
Raspberry Pi and bring up in an afternoon, with every step checkable before the next. No wire change,
no new refusal reason, no authority widened. `v0.10.0` is still the board's to award — but
[`docs/DEPLOY.md`](docs/DEPLOY.md) now says exactly what it must show.

### Added

- **`micromound --manifest m.json --check-hardware`** (`HardwareCheck` in `Micromound.Host`): opens
  every device the manifest binds through the same factories bring-up uses — fail-closed, the port
  opened last, the line requested at its SAFE level — takes ONE reading from each sensor, prints a
  per-device report (`OK`/`FAIL`, the capability, the driver's own reason or the first reading in the
  manifest's unit) and exits 0 only if every device was claimed. It actuates nothing and composes no
  mound: no authority, no mission, no evidence — just the ports. It exists because the alternative on
  a board is "start the daemon, read the refusal, edit, repeat", and because the first reading from a
  probe is the moment you learn the channel number was wrong. Exercised: a relay declared active-low
  is requested HIGH and never driven; a chip that answers the probe but not the read is reported as
  that, distinctly; an unknown driver type is reported with the types this build has.
- **`--simulate`**, and a refusal without it. A manifest that names physical ports (`pin`, `chip`,
  `channel`, `bus`, `address`) run without `--hardware` used to WARN and then run on in-memory
  ports, producing readings and "actuations" that look real and are neither. It is now **refused**
  (exit 2) unless the operator says `--simulate` in so many words — a development machine can, a
  device cannot by accident. `--hardware --simulate` together is a usage error.
- **`LinuxI2cBus` over the `ILinuxIo` seam** (open / value-argument ioctl / write / read / close). The
  seam gained `write`, `read` and an integer-argument `ioctl` — `I2C_SLAVE` takes the address as the
  argument, not a pointer, and the type now says so. The whole open-select-transfer sequence and every
  error path (missing node, refused address closes the node, short read says how short, unacknowledged
  write carries `errno 121`) are exercised against a fake kernel in `LinuxI2cBusTests`, including the
  ADS1115 driver over the real bus class issuing the datasheet transfers. Before this the bus's libc
  calls were unreachable from any test.
- **sysfs export settles.** `SysfsDigitalOutput` waits up to 500 ms for `gpioN/` to appear after
  `export` — the kernel creates it asynchronously — and refuses with a reason (`did not appear …
  is this a valid pin on this board, and is sysfs GPIO enabled?`) instead of the
  `DirectoryNotFoundException` the next write used to throw. The follow-up `v0.9.8` named.
- **Deployment kit** — `deploy/micromound.service`, `deploy/micromound.env`, `deploy/install.sh`, and
  [`docs/DEPLOY.md`](docs/DEPLOY.md). The unit runs the daemon as an unprivileged user in the `gpio`
  and `i2c` groups with `Restart=always` (SAFETY.md's named supervision backstop), a clean-stop
  timeout so SIGTERM has time to de-energize and persist, `StateDirectory`, and hardening that allows
  exactly the GPIO and I2C character devices and nothing else. The installer is idempotent (user,
  groups, `/opt`, `/etc`, `/var/lib`, unit; a note if I2C is not enabled in firmware) and starts
  nothing. DEPLOY.md walks from bench to the M4 boundary in six steps — buses, install, manifest,
  `--check-hardware` with the relay NOT clicking, enroll and start, charter and one mission — and
  says what each must show, including the SIGKILL-during-a-hold test. Linux release archives now
  carry `deploy/` and `DEPLOY.md`.

### Authority / safety

- **The in-memory refusal closes the last "fake evidence" path.** An operator who forgot
  `--hardware` on a device — or ran the wrong binary — could previously produce a mound whose
  sensors read zero and whose valve "actuated" nothing, with a warning scrolling past. Now the
  daemon does not come up. SAFETY.md notes it under Layer 1.
- **The check is safe by construction.** It requests outputs at `!active_high` and never executes
  an `act.` capability; a sensor read is the one observation that changes nothing. The report is the
  daemon's own view and proves nothing to the controller.
- **Supervision is now a file, not a sentence.** `Restart=always` with `RestartSec=3` is what the
  watchdog's residual case (a loop wedged inside a driver op) has relied on since `v0.9.10`; the
  unit ships it, and DEPLOY.md's step 6 tests it with `SIGKILL` during a hold.

### Notes

- `--check-hardware` always uses the real-port factories (`--gpio` selects the GPIO backing); it has
  no in-memory mode, because checking fake wiring answers nothing.
- The reference controller's console (ANTHILL `v0.3.8.122`) now generates its hardware form from this
  device's `driver_schemas`, keeps the copy each device sent, offers `device_limits` and charter
  limits as rows, and has a watering-station template — the operator-facing half of this release.
- Remaining before `v0.10.0`: DEPLOY.md, on a board, to the end.

---

## v0.9.16 — GPIO over the character device, and a line that comes up safe

The last MicroMound-side hardware item before the board: the digital actuator can now drive a real
line over the Linux GPIO **character device** (`/dev/gpiochipN`, uapi v2 — the interface libgpiod
uses and the one the kernel supports; sysfs GPIO is deprecated). No library: two ioctls, encoded by
hand against `linux/gpio.h` and pinned to the header's own numbers. And a safety fix that fell out of
doing it properly: **both** GPIO backings now bring a line up already at its safe level. Still
substrate — the ioctls must be verified on a board — so `v0.10.0` stays reserved.

### Added

- **`ILinuxIo` / `LibcIo`** (`Micromound.Drivers`): open / ioctl-with-buffer / close behind an
  interface, so a character-device driver's request encoding is exercised against a fake kernel that
  decodes what it was handed. libc via P/Invoke, no `unsafe`.
- **`GpioChardevOutput`** (`IDigitalOutput`, `IDisposable`): opens the chip, issues one
  `GPIO_V2_GET_LINE_IOCTL` for the line as an OUTPUT with an `OUTPUT_VALUES` attribute carrying the
  initial level, closes the chip descriptor, and keeps the line descriptor; each write is one
  `GPIO_V2_LINE_SET_VALUES_IOCTL`; `Dispose` releases the line (so does the kernel if the process
  dies — sysfs never did). Consumer label `micromound`, visible in `gpioinfo`. The `gpio_v2_line_request`
  layout (592 bytes; consumer @256, config.flags @288, num_attrs @296, attrs[0] @320, num_lines @560,
  fd @588) and the ioctl numbers (`0xC250B407`, `0xC010B40F`) were **measured from the kernel header
  with a C compiler** and are pinned in tests, including the check that the ioctl number's size field
  equals the struct size (the kernel refuses a mismatch). Identical on 32- and 64-bit: every field is
  fixed-width and the 64-bit ones `__aligned_u64`.
- **`GpioChardevActuatorFactory`**: the preferred hardware digital-actuator factory. Reads `pin` (line
  offset; BCM on a Pi) and `chip` (default 0) from the manifest and requests the line at
  `!active_high`. `EBUSY` (another process holds the line) is a fail-closed refusal with the reason.
- **Manifest setting `chip`** (`digital_actuator`, hardware-only, advanced, default `0`) — in the
  driver-settings catalog with help text (Pi header = chip 0; chip 4 on a Pi 5 with an older 6.1/6.6
  kernel). The legacy sysfs backing **refuses** a non-zero `chip` rather than guessing: its pin numbers
  are global, so a chardev manifest's `pin` would mean a different line.
- **Daemon `--gpio chardev|sysfs`** (with `--hardware`; default `chardev`) and
  `MoundHost.HardwareDriverFactories(gpioBacking)`. `--describe-drivers` shows the new setting.
- **`GpioSettings`** — `pin`, `chip`, and the safe level parsed once for both backings.

### Authority / safety

- **A line is requested already at its safe level.** Before this slice `SysfsDigitalOutput` wrote
  `out` (which drives LOW) and the driver wrote the safe level a moment later — for an **active-low**
  relay board that is a brief energize-then-release at every bring-up, reconfigure, and restart. Now
  sysfs writes `high`/`low` as the direction (direction and value in one write) and the chardev request
  carries the initial value, so the line's first output level IS the safe level. The factories compute
  it from `active_high` exactly as the driver does. Tested for both backings.
- **A reconfigure releases the old line.** `DigitalActuatorDriver.Reset` now disposes a port that owns
  a line (the analog sensor already did this for its channel). Found by the fake kernel: without it
  the old chardev line stayed claimed, the descriptor leaked, and re-requesting the same pin would
  have been `EBUSY` on a real kernel.
- **What release means, stated plainly** (SAFETY.md): when the daemon exits or crashes, the kernel
  returns the line to its default state and nothing holds the level. Whether that idle state is safe
  is the board's property (a relay input with a pull-up idles off) — Layer 0's job, as it always was.
- **No wire change, no new refusal reason, no authority widened.** A new hardware backing behind an
  existing seam, one additive manifest setting described in the catalog.

### Notes

- **On a Pi:** `--hardware` now uses the character device by default; the user must be in the `gpio`
  group (`/dev/gpiochip*` is `root:gpio 0660` on Raspberry Pi OS). `gpioinfo` shows held lines as
  `consumer=micromound`. Use `--gpio sysfs` only on a kernel built with `CONFIG_GPIO_SYSFS` and no
  character device. Exercised in the sandbox: `--hardware` with no `/dev/gpiochip0` refuses bring-up
  (errno 2); `--gpio sysfs` refuses with the missing sysfs path.
- `LinuxI2cBus` keeps its own P/Invokes for now; moving it onto `ILinuxIo` is a tidy-up, not a change.
- Export-settle retries for sysfs remain a follow-up (moot on the chardev path, which is synchronous).
- Remaining before `v0.10.0`: the on-board verification — sysfs/chardev writes, the I2C transfers, a
  live enroll + sync against a running ANTHILL.

---

## v0.9.15 — the device describes its own hardware vocabulary (driver-settings schema)

A slice for the operator, prompted by what the reference controller's Micromound page looks like today:
every charter, manifest, and mission field at once, in protocol vocabulary, with workers, limits, and
steps typed as pipe-delimited lines. The *documents* are MicroMound's and stay; the *form* is the
controller's to simplify — and the one thing the controller could not do on its own was know, without
hard-coding it, which settings each driver type reads. Now the device tells it. No canonical-bytes
change, no new refusal reason, no authority widened: this is description, not permission.

### Added

- **`DriverTypeSchema` / `DriverSettingSchema` / `SettingKinds` / `DriverRoles`** (`Micromound.Protocol`):
  the machine-readable form of CONFIGURATION.md's driver-settings table. Per driver type: its manifest
  name, a label and summary for a person, its role (actuator/sensor) and capability prefix, what backs it
  on real hardware, and its settings in form order. Per setting: name, label, help text, kind (`text`,
  `integer`, `number`, `boolean`, `choice`, `capability`), required, default (as the string a manifest
  carries), min/max, choices, unit, `hardware_only` (read only by the real backing — a simulator
  manifest may omit it), and `advanced` (fold it away by default).
- **`DriverSchemaCatalog.Shipped`**: the catalog for the two shipped types, `digital_actuator` (7
  settings) and `analog_sensor` (8). It lives in the protocol library so the reference controller —
  which compiles against `Micromound.Protocol` — has it at build time, and so the copy a device sends is
  the same data by construction.
- **`IDriverFactory.Schema`** and **`DriverFactoryRegistry.Describe()`**: every factory names its schema
  (the in-memory and hardware factories of one type share the same catalog entry — one manifest serves
  both), and a registry describes exactly the types THIS build registered.
- **Enrollment carries `driver_schemas`** (PROTOCOL.md §3.2, additive): `HttpEnrollmentClient` sends the
  registry's description with the token, key, tier, and capabilities. The reference controller's enroll
  handler ignores fields it does not know, so nothing changes for it until it chooses to read them.
- **Daemon `--describe-drivers [--hardware]`**: prints the catalog as indented JSON and exits — for a
  controller developer, a script, or a person, with no device or manifest needed.
- **The catalog is pinned to the drivers by test**, not by discipline. `DriverSchemaTests` configures
  every driver — in-memory and hardware-backed (fake sysfs tree, fake chip) — through a *recording*
  settings dictionary and asserts that the set of keys the driver asked for equals the set the catalog
  describes (non-hardware keys for the in-memory backing); that with only the `required` settings each
  hardware driver configures and dropping any one of them refuses; that every stated default is the
  driver's real default (scale 1, offset 0, active-high true → safe level low, bus 1, address 0x48,
  gain 4.096, the PGA choices are `Ads1115AnalogInput.FullScaleRanges`, channel 0..3, class choices
  exclude `hazardous`); that both host registries describe the same shipped catalog; and that the
  enroll body carries it (and an empty list sends none).

### Authority / safety

- **Describes, never grants.** A form built from the schema can only help a person write a manifest
  the drivers will accept; the drivers still parse and validate every string and fail closed, the kernel
  still intersects the three limit tiers, and a mound still acts only under a charter. A controller
  that trusted the schema instead of the device's ack would be wrong in exactly the way PROTOCOL.md
  already forbids ("refusal arrives as an ack, never inferred").
- **It is sent unauthenticated, like `capabilities`**, in the pre-key enroll body. It carries no secret
  and grants nothing, so that is fine; a controller should treat it as the device's *description of
  itself*, not as a claim about the world.

### Notes

- Suggested controller-side changes this enables (ANTHILL, no MicroMound change needed): render the
  "Manifest → hardware" section from `driver_schemas` (a card per device: pick a type, fill labelled
  fields, Advanced folded); pre-fill the charter's capability list and the mission's dropdowns from
  the enrolled device's `capabilities`; hide routines, evidence patterns, reasoning mode, and the
  worker table under Advanced with today's defaults (they are already right); replace the pipe-delimited
  mission steps with rows and dropdowns; offer templates ("Watering station").
- `UPSTREAM.md` and `CONFIGURATION.md` describe the mechanism; PROTOCOL.md §3.2 has the field.
- Remaining before `v0.10.0`: on-board verification of the sysfs writes and the I2C transfers, and
  optionally a libgpiod (chardev) backing.

---

## v0.9.14 — the analog port is real (ADS1115 over I2C), and the daemon can reach its hardware

The second real driver port, the counterpart of `v0.9.8`'s GPIO line under the actuator: the generic
analog sensor now has a *real* channel to sample — one input of a TI ADS1115, the 16-bit four-channel
I2C ADC that is the usual analog front end on a Raspberry Pi — spoken over the kernel's `i2c-dev`
interface with no library. And the two hardware factories are now **reachable from the command line**:
until this slice the daemon only ever composed in-memory ports, so a manifest's `pin` was read by
nothing on a real board. It is still **substrate, not the milestone**: the register protocol is proven
against a fake chip at the byte level; the I2C transfers themselves must be verified on a physical
board. `v0.10.0` stays reserved for that.

### Added

- **`II2cBus` / `LinuxI2cBus`** (`Micromound.Drivers`): one I2C slave as the two operations a
  register-mapped chip needs — write a few bytes, read a few bytes. The Linux backing opens
  `/dev/i2c-N`, selects the slave with the `I2C_SLAVE` ioctl, and carries transfers with plain
  `write(2)`/`read(2)` — three libc P/Invokes, no NuGet, no `unsafe`. Every failure — no bus, no
  acknowledge (a chip that is not there), a permission problem — is an `IOException` with the errno,
  never a silent zero.
- **`Ads1115AnalogInput`** (`IAnalogInput`, plus `IDisposable`): one single-ended channel (AIN0..3
  vs GND) in **single-shot** mode — each read writes the Config register (start, MUX, PGA, single-shot,
  128 SPS, comparator off), polls until the OS bit reports the conversion complete (paced ≥1 ms, bounded
  by `maxPolls`), and reads the two's-complement Conversion register. The result is in **volts**,
  `raw × FSR / 32768`. Full-scale range is one of the PGA's six (default ±4.096 V, the right one for a
  3.3 V system). **Construction probes the chip**: a missing chip throws, and the driver above refuses
  to configure. The Config word is exposed (`ConfigWord`) so a test pins the exact encoding — channel 0
  at ±4.096 V is `0xC383`, the datasheet's value.
- **`Ads1115AnalogSensorFactory`**: the hardware-backed analog-sensor factory. Same driver kind
  (`analog_sensor`), same capability/unit/calibration settings as the in-memory default — only the
  channel backing changes. Settings: `channel` (required, 0..3), `bus` (default 1), `address` (decimal
  or `0x` hex, default `0x48`), `gain` (a PGA range in volts, default 4.096). Everything is validated
  **before** the bus is touched, and a device node opened for a chip that then fails its probe is
  closed again — a refusal never leaks a file descriptor.
- **The analog sensor opens its channel from the manifest at configure time**, like the actuator opens
  its line: `AnalogSensorDriver` takes a settings-keyed channel builder (the `IAnalogInput`
  constructor remains for in-memory/test channels), opens it LAST after the slice validates, and
  fails closed if the open throws. A dropped channel is disposed on reconfigure.
- **Optional linear calibration on the analog sensor**: `scale` and `offset` settings map
  `value = raw × scale + offset` (defaults 1 and 0), so a charter's thresholds can be written in the
  sensor's own unit rather than volts. Both must be finite — `NaN`/`Infinity` fail closed — and a
  non-finite *result* is a fault, not a reading.
- **Daemon `--hardware` flag** and **`MoundHost.HardwareDriverFactories()`**: with the flag, digital
  actuators open sysfs GPIO lines and analog sensors open ADS1115 channels; without it every port is
  in-memory, as before. The start-up banner now says which. A manifest that names physical ports
  (`pin`, `channel`, `bus`, `address`) while the daemon runs in-memory logs a WARNING — readings and
  actuations in that mode look real and are not.

### Authority / safety

- **A sensor read that fails is a fault with no reading.** The in-memory channel never threw; a real
  one can (a chip that stopped acknowledging). The executor now reports `sensor read failed: …` and
  emits **no evidence** — a fabricated number would be worse than a missing one, because it could
  satisfy a mission's `verify` step. Tested: unplug the fake chip after bring-up → fault, zero items
  published.
- **A missing chip refuses bring-up.** Exercised for real in the sandbox: with `--hardware` and no
  `/dev/i2c-1`, the daemon exits 1 with `bring-up refused (fail-closed): … could not open the
  sensor's channel: cannot open /dev/i2c-1 (errno 2)` — the first live traversal of the libc path.
- **Gain is not protection.** The PGA range is a resolution setting; the ADS1115's inputs must never
  exceed VDD + 0.3 V regardless of range. Stated in the driver docs and README's deployment note.
- **No wire change, no new refusal reason, no authority widened.** New hardware backing behind an
  existing seam; canonical bytes, envelopes, and the refusal enum are untouched.

### Notes

- **On a Pi:** enable I2C (`raspi-config` → Interfaces, or `dtparam=i2c_arm=on`), run the daemon as a
  user in the `i2c` group (and `gpio` for the actuator), wire the ADS1115 to SDA/SCL/3V3/GND with ADDR
  to GND for `0x48`, and pass `--hardware`. `i2cdetect -y 1` should show `48` before the daemon does.
- **Tests:** `Ads1115Tests` (38 cases) run against a register-level fake chip — OS bit cleared while
  converting, two's-complement result — pinning the Config encoding for every channel and range, the
  volts scaling (including the −FSR..+FSR−1 LSB edge), the conversion wait and its timeout, the probe,
  fail-closed configuration through the factory for every malformed setting, calibration, and the
  read-failure fault. The adversarial pass found and fixed two things: a bad `channel` opened the bus
  before being refused (fd leak per attempt), and back-to-back polls on a fast bus could exhaust the
  poll budget inside one conversion time — polls are now paced.
- `docs/CONFIGURATION.md` now documents the **shipped driver types and their settings**, and its
  example manifest uses them. A machine-readable form of that table — so a controller can generate
  its hardware form instead of hand-matching setting names — is the natural next protocol addition.
- Remaining before `v0.10.0`: a libgpiod (chardev) GPIO backing, and the on-board verification of the
  sysfs writes and the I2C transfers.

---

## v0.9.13 — heartbeat evidence is rate-limited to what is informative

The follow-up v0.9.12 named. Making the evidence store durable turned every heartbeat reading into an
fsync'd file, and the Guard emitted one on every poll — every service tick AND before every actuation —
so a chartered mound wrote on the order of four fsyncs per tick to an SD card, forever, mostly to say
"still alive" again. A reading now goes out only when it is informative. No wire change, no new refusal
reason, no authority widened; the watchdog's refusal logic is untouched.

### Changed

- **`GuardAnt.Poll` emits a heartbeat reading when it is INFORMATIVE, not on every call.** Three cases
  always emit: the first poll (a baseline), and every fresh→stale and stale→fresh transition — the
  readings that prove afterwards why a mound stopped, or that it recovered. Between those, a routine
  liveness record goes out no more often than `heartbeatEvidenceIntervalSeconds` (default 60 s). `0`
  restores the old every-poll behaviour. Surfaced as `HostOptions.HeartbeatEvidenceIntervalSeconds`
  and `MoundComposition.Build(..., heartbeatEvidenceIntervalSeconds:)`.
- **Staleness is still recomputed on every poll.** Only the *evidence emission* is rate-limited; the
  `SafeStateRequired` the kernel's refusal reads is refreshed exactly as before, including on the
  pre-actuation poll in `MoundMajorRuntime`. Tested: a heartbeat that goes stale inside a long evidence
  interval is refused on that very poll, and its transition reading is emitted.

### Authority / safety

- **Nothing that explains a stop is lost.** A stop caused by a stale heartbeat has its fresh→stale
  reading in the store by construction (a transition is never suppressed). SAFETY.md's "a mound that
  entered its safe state has to be able to prove afterwards why it did" holds unchanged — what is
  removed is the same proof repeated every five seconds, not the proof.
- **Measured, not estimated.** On a real `MoundHost` over the real `FileEvidenceStore`, ten minutes of
  5 s ticks wrote **120 evidence files** under the old behaviour and **10** under the new default — a
  12× reduction in the dominant write source, plus the elimination of the per-actuation reading.
- The trade, stated plainly: the routine liveness record in the audit trail is now per minute rather
  than per tick. A deployment that wants the old density sets the interval to 0 and accepts the wear.

### Notes

- `AntTests.Two_polls_in_the_same_second_do_not_collide` now pins the every-poll mode (interval 0), so
  the id-uniqueness property it tests is unchanged. `AntTests` and `MissionTests` are now part of the
  sandbox compile-check set.
- Pre-existing and still open (v0.9.12 note): offline, heartbeat items accumulate toward the hard
  ceiling and the oldest proof spills first. This slice makes that ~12× slower to reach; ranking real
  actuation proof above liveness readings at spill time is a separate policy question, not taken here.

---

## v0.9.12 — the evidence store survives a restart

The last M4 substrate piece that can be proven without hardware. The proof a mound captures — the
Witness Ant's memory — lived only in the heap, so a week offline followed by a reboot lost exactly the
record the mound had been keeping. It is now a directory of files under `<state>/evidence/`, with the
in-memory store's retention policy (v0.9.0) unchanged above it. No wire change, no new refusal reason,
no authority widened; M3's retention *rules* are untouched — only the substrate under them.

### Added

- **`FileEvidenceStore`** (`Micromound.Sync`, beside `FileStateStore`): an `IEvidenceStore` with a file
  behind every item. One file per item named by its insertion sequence (`0000000000000042.json`) — the
  name IS the order, so oldest-first eviction is recoverable from a directory listing and the id lives
  inside the JSON where it needs no encoding. An acknowledged item has a sibling marker (`.ack`). A
  small `counters.json` holds the evicted/spilled counts that have not yet ridden the wire, so a spill
  the controller was never told about does not go quiet across a reboot. Reads are served from an
  in-memory mirror rebuilt on open; every mutation is written through first.
- **`DurableFiles`** (internal): the atomic-replace and directory-fsync primitives extracted from
  `FileStateStore` so both disk-backed stores share exactly one implementation of "a power cut cannot
  tear this". `FileStateStore` is refactored onto it behaviour-preservingly (its restart tests and the
  harness's `.tmp-` collision check are unchanged and green).
- **The composition takes an injected `IEvidenceStore`** (`MoundComposition.Build(..., evidenceStore:)`;
  `ComposedMound.EvidenceStore` is now interface-typed). The host runs `FileEvidenceStore` at
  `<state>/evidence` (`HostOptions.EvidenceCapacity` / `EvidenceHardCeiling`); the simulator and tests
  keep the in-memory store. `Micromound.Sync` gains a reference to `Micromound.Evidence` (acyclic).

### Authority / safety

- **Retention parity is exact.** The same operation sequence on the in-memory and file stores yields
  identical pending sets and identical evicted/spilled counts, verified side by side. Acknowledged proof
  is reclaimed first past the soft capacity; unacknowledged proof spills only past the hard ceiling,
  counted; a store reopened under a smaller configured bound is brought back inside it on open, with
  the reclaim counted like any other.
- **Crash order chosen so nothing is lost, only re-sent.** An item is written before it is remembered
  (a crash leaves a whole item or none). The ack marker is written after the item (a crash between
  leaves it pending — re-sent, re-acknowledged, harmless). An eviction unlinks the item before its
  marker (a crash between leaves an orphan marker, ignored and swept on open — never a resurrected item
  the policy had chosen to drop). A stale temporary is swept and never read as proof. A file that will
  not parse is skipped and **reported** (`OpenFaults`, written to stderr at bring-up) — a fault, not a
  reason to refuse to start, and not proof.
- **After a restart the mound uplinks the proof it had pending.** Previously that proof evaporated with
  the heap; now the next bundle carries it. A controller that keys evidence by id sees a re-send of any
  item whose ack was lost mid-write, which is the protocol's intended idempotence.

### Notes — read these before deploying on an SD card

- **Write amplification.** Every evidence item is now an fsync'd file (plus a directory fsync), and the
  dominant source is the Guard's heartbeat reading — one item **every tick** (5 s default), forever. At
  steady-state capacity each `Add` also evicts an acknowledged item and rewrites `counters.json`, so a
  chartered, connected mound writes on the order of **four fsyncs per tick** to `<state>/evidence`.
  That is the honest cost of "the audit record survives a reboot", and it is kept deliberately as strong
  as the state store's guarantee rather than weakened. On a Raspberry Pi put `--state` on high-endurance
  or industrial media (or a USB SSD), not a consumer SD card, for a long-lived deployment. The follow-up
  that removes most of this cost is rate-limiting or state-change-gating the heartbeat evidence (it is
  published every poll today, a v0.9.5 choice) and, if still needed, a batched journal — neither is
  part of this slice.
- **Pre-existing, made visible by durability:** offline, heartbeat items accumulate toward the hard
  ceiling and the oldest items spill first — which can be *real actuation proof* buried under later
  heartbeats. That is the v0.9.0 policy, not a change here, but a durable store makes it worth revisiting
  alongside the heartbeat cadence above.

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
