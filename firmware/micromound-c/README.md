# micromound-c — a reduced-profile mound in C

The software of the constrained-controller firmware (ROADMAP M5), built where it can be proven: a
portable C99 library that produces the **exact canonical bytes** the C# runtime produces
(`Micromound.Protocol`), digests and signs them the same way, reads and validates what a device
receives, decides exactly what the C# capability kernel decides, and runs the Runner Ant's loop —
checked against the golden fixtures in [`tests/Micromound.Tests/Golden/files`](../../tests/Micromound.Tests/Golden/)
by `gcc` and `clang` on the host, and, for the device loop, checked the other way: the host's own
verifier accepts a whole session the C device recorded. Nothing here needs a board. Everything here
is what a board will run.

```bash
make            # build/libmicromound.a
make test       # 2,500+ checks, including every golden file, byte for byte
make tools      # build/mm_board_sim — this port server as a host process (see below)
make CC=clang test
```

No dynamic allocation, no dependencies beyond the C standard library (`snprintf`/`strtod` in the
number formatter; `memcpy`; `abort` behind a stub that is never reached), `-std=c99 -Wall -Wextra
-Werror -pedantic`. Every public function takes a caller-supplied buffer and reports the length it
needed when the buffer was too small.

## What is here

| Module | Header | Does | Pinned by |
|---|---|---|---|
| `mm_json` | `mm_json.h` | The canonical writer: no whitespace, comma and nesting tracking, every JSON value type, the PROTOCOL.md §2 escaping rule from UTF-8 input (invalid UTF-8 refused) | `canonical-strings.txt` |
| `mm_format` | `mm_format.h` | .NET's `double` text: shortest round-trip digits, plain while `-3 <= digPos <= max(digits, 17)`, else `d.dddE±XX`; `-0`; NaN/∞ refused | `canonical-doubles.txt` |
| `mm_sha256` | `mm_sha256.h` | FIPS 180-4 SHA-256, incremental; hex encode/decode | FIPS vectors; every envelope digest |
| `mm_ed25519` | `mm_ed25519.h` | Ed25519 over vendored TweetNaCl: keypair from a 32-byte seed (no RNG), **detached** sign and verify (message read in place), non-canonical `S` rejected | RFC 8032 §7.1 vectors 1–3; a cross-implementation signature |
| `mm_envelope` | `mm_envelope.h` | The envelope: canonical bytes with `"sig":""` present and empty, `sha256:` digest, `ed25519:` signature, strict verify, and an **in-place splice** of the signature into the last field | `canonical-envelopes.txt` |
| `mm_bodies` | `mm_bodies.h` | The reduced-profile bodies field for field: `mound_sync`, `action_record` (with inline `evidence` items), `ack`, `stop`, `charter` — and `evidence_bundle`, so the item shape is pinned | `canonical-envelopes.txt`, `canonical-bodies.txt` |
| `mm_json_read` | `mm_json_read.h` | A bounded pull parser: full escape grammar, strict UTF-8, JSON number grammar, depth-capped, skips unknown members, hands values on as raw slices | `test_json_read.c` |
| `mm_time` | `mm_time.h` | `yyyy-MM-ddTHH:mm:ssZ` ↔ epoch seconds; accepts the offset/fractional forms §2 asks readers to tolerate | `test_time.c` |
| `mm_decode` | `mm_decode.h` | The receive side: the envelope frame, **signature verified from the bytes as received**, `charter`/`stop`/`ack`/`action_record` into fixed-capacity structs, `EnvelopeValidator` and `CharterValidator` with the host's reason lines character for character, views to re-encode | `canonical-signed.txt` |
| `mm_kernel` | `mm_kernel.h` | **The capability kernel**: compiled capability/routine tables, `KernelAuthority` (charter, lease, stop, quiesce, device limits), the fourteen authorization checks in the host's order, hardware ∩ device ∩ charter, duty cycle and rate, clamping, execution through a function pointer, the evidence gate; the host's refusal reasons and detail text | `kernel-decisions.txt` |
| `mm_device` | `mm_device.h` | **The device loop** (`RunnerAnt`): signed, chained uplink on a bounded queue, the beat and its acknowledgement-driven drain, downlink verified from the bytes as received and handled stops-first, acks, lease renewal on the acknowledged beat, quiesce, offline as a normal state | `device-session.txt` (written here, verified by the host) |
| `mm_hal` | `mm_hal.h` | **What a board supplies**, and nothing else: clock, entropy, one HTTPS POST, protected key/value storage, a digital output, an analog input, and — since `v0.9.27` — a digital input (`gpio_read`; a line that cannot be read is a fault, never a `0`), and — since `v0.9.38` — an OPTIONAL monotonic uptime (`monotonic_s`, may be NULL): nine function pointers | `test_board.c`'s fake of it |
| `mm_enroll` | `mm_enroll.h` | PROTOCOL.md §3 as `HttpEnrollmentClient` does it: the same request body, the same reading of the response, the same verdicts in the same words; persists the controller key before anything else | `enroll-exchange.txt` |
| `mm_link` | `mm_link.h` | `HttpSyncTransport`: POST one envelope, split the JSON array that comes down into slices the device verifies; non-2xx is a failed exchange, no exchange is offline | `test_board.c` |
| `mm_drivers` | `mm_drivers.h` | The three generic drivers as executors: `mm_relay` = `DigitalActuatorDriver` (safe at bring-up, held for the clamped `on_s`, released by `mm_relay_service` or any stop, **no evidence — a command is not evidence**); `mm_probe` = `AnalogSensorDriver` (volts × scale + offset, a `reading` evidence item; a failed read is a fault, never a zero); `mm_switch` = `DigitalSensorDriver` (`v0.9.27`: a digital input read through the manifest's polarity as a `reading` of 1 or 0 — the independent observation that lets an actuation be confirmed at all; a failed read is a fault with no reading) | `test_board.c` |
| `mm_frame` | `mm_frame.h` | The Pi↔ESP32 link framing (PROTOCOL.md §12): `"MM" ver type seq len payload crc32`, request/response payloads, an incremental decoder that resynchronises and counts what it drops | `link-frames.txt` |
| `mm_serial` | `mm_serial.h` | The HAL's `http_post_json` over a byte stream to a bridge — the same exchanges, framed; timeouts are offline; the bridge's clock on request | `test_frame.c` (a fake pipe, a scripted bridge) |
| `mm_ports` | `mm_ports.h` | **The board as a port server** (PROTOCOL.md §12, port requests): hello/write/read/**read_pin** over the link with the Pi's kernel as the only authority — and the two things the board keeps: each pin's compiled `max_on_s`, released by the board itself, and a link watchdog that drives everything safe when the Pi goes quiet; a failed release trips it. A line is an input or an output, never both, and one that will not read at bring-up is not offered | `port-exchange.txt` (written here, read by the host's `LinkPortsTests`) |
| `mm_app` | `mm_app.h` | **The firmware above the HAL**: identity from protected storage (created once from the board's RNG), enrollment with a one-time token, the service loop — holds released first, quiesce, the beat on the charter's cadence, the compiled schedule through the kernel — the trip (a relay that will not release stops the mound), and the sticky stop, written to storage on the tick it happens and read back at bring-up so a restart cannot clear it (`v0.9.35`) | `test_board.c` |

## `tools/mm_board_sim` — this port server, as a host process

`make tools` builds `build/mm_board_sim`: the real `mm_ports` and `mm_frame` above — the same
handlers, the same refusals, the same compiled `max_on_s` and link watchdog — speaking §12 frames
over stdin/stdout, with only the world **below** its HAL modelled. It is not a re-implementation of
the board; it is the board, with a fake multimeter attached.

```bash
make tools
build/mm_board_sim --profile bench --watchdog 60 \
  --pin 5:high:30 --input 12:low:follows=5:delay=2 --channel 0:0.20:rises=5@0.05
```

- `--pin PIN[:low][:MAX_ON_S]` an output line and the board's own bound for it
- `--input PIN[:low][:follows=PIN][:delay=S]` an input line, optionally a switch that closes S
  seconds after an output is driven — an actuator's travel time
- `--channel CH[:VOLTS][:rises=PIN@RATE]` an ADC channel, optionally rising while a pin is driven

Three paths the simulator answers itself, before the board ever sees them (a real board answers
them `404`, which is the correct answer): `micromound/link/sim/advance` `{"seconds":N}` moves the
clock one simulated second at a time, running the physics and `mm_ports_service` each second;
`…/sim/world` returns what a multimeter and a stopwatch would say; `…/sim/fault` injects a line that
will not drive, one that will not read, or a dead ADC. **The clock only moves when it is told to**,
which is what makes [`docs/ACCEPTANCE.md`](../../docs/ACCEPTANCE.md) deterministic: no sleeps, no
wall clock, no flakes. CI builds it under gcc and clang with the same `-Werror` the library gets.

Deliberately absent, per PROTOCOL.md §8: `mission`, `mission_report`, `evidence_bundle`, `config`.
A constrained controller runs compiled routines selected by charter; it never plans. Its readings
reach the controller on the record that cites them: `mm_device` sets `mm_kernel.inline_evidence`, and
every item an executor produced rides in the record's `evidence` array (PROTOCOL.md §6, `v0.9.24`) —
the kernel itself mirrors the host's and inlines nothing.

## Using it

A device builds one buffer and never re-serializes. Because `sig` is the last field of the
envelope, signing is an append:

```c
#include "mm_bodies.h"
#include "mm_envelope.h"

mm_mound_sync body = { "chartered", 0 };          /* state, queue_depth */
mm_envelope e = {
    "11111111-1111-4111-8111-111111111111",       /* id */
    "mm-7f3a0000-0000-4000-8000-000000000001",    /* mound_id */
    0,                                            /* seq */
    "2026-08-14T21:04:11Z",                       /* sent_at */
    MM_KIND_MOUND_SYNC,
    "",                                           /* prev_digest: "" anchors the chain */
    mm_body_mound_sync, &body
};

char wire[1024];
char digest[MM_DIGEST_TEXT_LEN + 1];
size_t n = mm_envelope_write_signed(&e, sk, wire, sizeof wire, digest);
/* n == 0: the buffer was too small. Otherwise wire[0..n) is the signed envelope and
   digest is what the NEXT envelope's prev_digest must carry. */
```

`sk` is the 64-byte NaCl secret key (`seed || pk`) from `mm_ed25519_seed_keypair`. The seed comes
from the device's own entropy at provisioning and from protected storage after that; this library
never generates one (SAFETY.md: nothing reads a seed back, nothing here could write one).

Verifying a received envelope's signature over bytes you already have:

```c
if (mm_envelope_verify(canonical, n, sig_text, controller_pk) != 0) { /* refuse it */ }
```

Anything malformed — wrong algorithm prefix, wrong length, a non-hex digit, `S >= L`, a public key
that is not a curve point — is `-1`, never an exception and never a guess.

Receiving a downlink envelope, in the order the host's `RecordAnts` does it:

```c
#include "mm_decode.h"

char digest[MM_DIGEST_TEXT_LEN + 1];
mm_envelope_in frame;
mm_refusal why;
int err;

if (mm_envelope_verify_wire(wire, n, controller_pk, digest) != 0) return REFUSE;   /* 1. who sent it */
if (mm_envelope_parse(wire, n, &frame, &err) != 0) return REFUSE;                   /* 2. the frame  */
if (strcmp(frame.mound_id, my_mound_id) != 0) return REFUSE;                        /*    addressed to me */
if (mm_envelope_validate(&frame, &why) != 0) return REFUSE;                         /* 3. shape, kind (§8) */
if (strcmp(frame.kind, MM_KIND_CHARTER) == 0) {                                     /* 4. the body   */
    mm_charter_in charter;
    if (mm_charter_parse(frame.body, frame.body_len, &charter, &err) != 0) return REFUSE;
    if (mm_charter_validate(&charter, my_mound_id, now, my_caps, n_caps, my_routines, n_routines, &why) != 0)
        return REFUSE;   /* why.reasons[] holds the host's exact reason strings, for the audit line */
    /* accept: hand it to the kernel */
}
```

Step 1 works on the bytes as received because a signed envelope on the wire *is* its canonical
bytes with the signature spliced into the last field; the verifier hashes the prefix and the two
closing bytes as two parts. Nothing is decoded before the signature is known good, and nothing is
re-serialized after — a sender that did not emit canonical form is refused, which is the fail-closed
direction (PROTOCOL.md §8).

## Why the escaping rule had to change first

The C# side used `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, which leaves most non-ASCII
literal but escapes a **Unicode-version-dependent** set of code points (7,886 of them in the BMP
under one runtime). Two mounds on different runtimes would have signed different canonical bytes
for the same device name, and no C encoder can mirror a table it cannot see. `v0.9.18` replaced it
with `CanonicalJsonEncoder`, the rule `mm_json.c` implements in fifty lines: printable ASCII
literal, `\"` and `\\`, `\b \t \n \f \r`, `\uXXXX` (uppercase hex, surrogate pairs above the BMP)
for everything else. Canonical bytes are pure ASCII. No existing golden byte changed.

## Layout

```text
firmware/micromound-c/
  include/            the public headers (one per module)
  src/                the modules
  third_party/tweetnacl/   TweetNaCl, verbatim, with a provenance README
  tests/              mm_test.h harness; one test file per module; test_golden.c, test_kernel.c, test_device.c, test_board.c, test_frame.c and test_ports.c cover the ten fixtures
  Makefile
```

`mm_ed25519.c` `#include`s `tweetnacl.c` rather than linking it, to reach the file-static field
arithmetic for the detached functions and the seed keypair. TweetNaCl's own `randombytes` symbol
is satisfied by a stub that aborts — reaching it is a programming error, since nothing here makes
keys.

## The kernel

`mm_kernel` is `Micromound.Capabilities` with the tables compiled in:

```c
static const char *const RELAY_PARAMS[] = { "on_s" };
static const mm_param_range RELAY_RANGES[] = { { "on_s", 1, 3600 } };
static const mm_capability_desc CAPS[] = {
    { "sense.temp",  0, { {0,0},{0,0},{0,0},{0,0},{0,0} }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
    { "act.relay_1", 1, { {1,60},{1,120},{0,0},{0,0},{1,4} },      /* hw: max_on_s 60, min_off_s 120, 4/h */
      RELAY_PARAMS, 1, RELAY_PARAMS, 1, RELAY_RANGES, 1, "on_s", NULL },
};

mm_kernel k;
char err[256];
if (mm_kernel_init(&k, my_mound_id, CAPS, 2, NULL, 0, err, sizeof err) != 0) halt(err);
mm_kernel_bind_executor(&k, &relay_executor);          /* { "act.relay_1", drive_relay, &relay, 1 } */

/* a charter arrived and verified (mm_decode) */
mm_refusal why;
if (mm_kernel_accept_charter(&k, &charter, now, &why) != 0) audit(mm_refusal_join(&why, line, sizeof line));

/* a routine wants the relay for 50 s */
mm_param on_s = { "on_s", 50 };
mm_request req = { "act.relay_1", &on_s, 1, "", "", -1 };
mm_action_record_in record;
mm_kernel_execute(&k, &req, now, action_id, &record);  /* refused, clamped, succeeded, failed, unverified — as the host would */
```

The record is an `mm_action_record_in`; `mm_action_record_bind` + `mm_body_action_record` put it in an
envelope. `tests/test_kernel.c` runs the 42-step script in `kernel-decisions.txt` against exactly this
API and matches every line the C# kernel wrote.

## The device

`mm_device` is the whole reduced-profile mound over the kernel — what `firmware/esp32`'s `app_main`
will drive:

```c
mm_device_config cfg = {
    my_mound_id, device_sk, controller_pk,          /* identity; the controller key from enrollment */
    CAPS, 2, ROUTINES, 1,                           /* the compiled tables */
    my_uuid_source, NULL,                           /* fresh envelope ids, from the board's RNG */
    de_energize_everything, NULL                    /* the safe state, on stop and on quiesce */
};
static mm_device dev;                               /* ~40 KB: the queue is 16 × 2 KB */
if (mm_device_init(&dev, &cfg, err, sizeof err) != 0) halt(err);
mm_kernel_bind_executor(&dev.kernel, &relay_executor);

for (;;) {                                          /* the service loop */
    int64_t now = clock_now();
    mm_device_tick(&dev, now);                      /* quiesce when the lease runs out */
    if (due_for_a_beat(now)) {
        mm_sync_outcome out;
        mm_device_sync(&dev, now, https_exchange, &link, &out);   /* beat, drain, handle, renew */
    }
    if (routine_wants_to_act(now)) {
        mm_action_record_in record;
        mm_device_act(&dev, &request, now, &record);              /* through the kernel; recorded and queued */
    }
}
```

`https_exchange` is `ISyncTransport.TryExchange`: POST one envelope to `<controller>/micromound/v0/sync`,
hand back the array that came down, return -1 when the link is down. `tests/test_device.c` runs this
loop against a scripted controller and records the session as `device-session.txt`; the C#
`DeviceSessionTests` then verifies that transcript with the host's verifier, chain validator and typed
contracts — the controller accepts what the C device sends.

## The board layer

Above `mm_device` sits everything a board runs, written against eight function pointers:

```c
#include "mm_app.h"

static mm_relay relay;  static mm_probe probe;  static mm_app app;      /* static; nothing allocates */
mm_hal hal = { 0 };                                                   /* zero FIRST: NULL is how an optional hook is declared absent */
hal.ctx = &board;
hal.now = board_now;                 hal.random_bytes = board_random;
hal.http_post_json = board_https_post;
hal.kv_get = board_kv_get;           hal.kv_set = board_kv_set;
hal.gpio_write = board_gpio_write;   hal.gpio_read = board_gpio_read;
hal.adc_read = board_adc_read;
hal.monotonic_s = board_uptime_s;    /* optional; without it a stepped wall clock is believed */

mm_relay_init(&relay, &hal, "act.relay_1", 5, 1);                     /* the line comes up at its SAFE level */
mm_probe_init(&probe, &hal, "sense.temp", 0, 100.0, -50.0, "C");     /* volts × 100 − 50 → degrees */
mm_app_config cfg = { my_mound_id, "sense.temp,act.relay_1", CAPS, 2, NULL, 0, NULL, 0, &relay, 1, &probe, 1, SCHEDULE, 2 };

char err[256];
if (mm_app_init(&app, &hal, &cfg, err, sizeof err) != 0) halt_safe(err);   /* seed created or loaded; enrolled if a key is stored */
for (;;) { mm_app_tick(&app, 0); sleep_ms(1000); }                          /* holds, quiesce, enrollment or the beat, the schedule */
```

`tests/test_board.c` runs exactly this against a fake HAL: first boot creates and stores the seed (the
fixture's, so the enrollment request equals `enroll-exchange.txt`'s `request:` line byte for byte);
no token, an outage and a 500 are retried with the token kept; a 200 stores the key and cadence and
burns the token; a 409 burns it too; the first enrolled tick beats, is chartered, reads the probe and
energizes the relay under the charter's clamp; the charter's cadence wins over enrollment's; the hold
is released at the top of the tick whose time has come; a reboot on the same storage keeps the identity
and the enrollment; a stop releases the relay and the kernel refuses the next scheduled actuation; a
release write that fails trips the mound and is retried. Every `## case` of `enroll-exchange.txt` is
replayed through `mm_enroll_read_response` and must reach the host's verdict in the host's words.

## What this is not, yet

- **Not yet run on a board.** [`firmware/esp32`](../esp32/README.md) binds `mm_hal` to ESP-IDF — the
  clock, `esp_http_client`, NVS, GPIO, ADC — in one file, and compiles to a 1.0 MB image under ESP-IDF
  v5.3.2 (37.8 KB of it is this library). Everything above that file has run, here; the board has not.
- **Not fast.** TweetNaCl signs in tens of milliseconds on an ESP32-class core; adequate for a sync
  beat, not for anything hotter. The backend sits behind `mm_ed25519.h` and the tests prove a swap
  did not change the bytes.

## Portability notes

- Requires a correctly rounded `printf("%.*e")` and `strtod` (glibc, musl, newlib, MSVCRT ≥ 2015).
  `canonical-doubles.txt` is the check for the libc in use; run `make test` on the target toolchain.
  The xtensa gcc 13 in ESP-IDF v5.3.2 compiles the library at `-Og -Wall -Werror=all -Wextra`; its
  format-truncation heuristic wants composed audit lines built in a buffer as large as their parts
  (`MM_DETAIL_CAP`) before they are bounded to `MM_REASON_CAP` — which is how they are written now.
  The library has not yet run on the target (see `firmware/esp32`).
- Integer widths: `long long` for C# `long`, `double` for everything the protocol types as a number.
  TweetNaCl's `u32` is `unsigned long` and is masked where it matters. The sanitizer build in CI
  (ASan + UBSan, every finding fatal) is clean, with one named exemption: `shift-base`, which
  TweetNaCl's field arithmetic trips by left-shifting negative limbs — see the `SANITIZE` flag set
  in the Makefile. Nothing of MicroMound's own is exempt.
- Endianness: SHA-256 and the hex helpers are byte-oriented; the double fixture is decoded via an
  integer, so it reads correctly on either byte order.
- Memory, measured on x86-64 (`sizeof`): `mm_app` ~60 KB static — `mm_device` 48 KB (the queue is 16 × 2 KB; lower
  `MM_DEVICE_QUEUE` / `MM_DEVICE_WIRE_CAP` for a smaller board) and `mm_link`'s 8 KB response buffer
  (`MM_LINK_RESPONSE_CAP`; a downlink batch larger than it reads as unreadable and the exchange fails,
  so a controller must keep a batch under it) — `mm_kernel` 10 KB inside it,
  `mm_charter_in` 4 KB, `mm_action_record_in` 9 KB (16 inline evidence items of 392 B, `v0.9.24`),
  `mm_outcome` 5.5 KB. **Stack:** one exchange's
  batch (`MM_DEVICE_BATCH` × 440 B frames) plus a charter, a refusal set, a decision, an outcome and a
  record on the way through `mm_device_sync` peaks near 14 KB; `mm_enroll` adds a 4 KB response and a
  2 KB request on its own path — run the service loop on a task with 32 KB of stack, or lower
  `MM_DEVICE_BATCH`. Nothing allocates, so this is the whole budget.
- **Zero an `mm_hal` before filling it in.** The optional hooks (`monotonic_s`) are recognised as
  absent by being NULL; a board that assigns field by field and misses one passes a stack value the
  library will call. `-Wextra` catches the initializer-list form of the mistake, not this one.
- Storage keys are at most 15 characters (`mm.seed`, `mm.ctl_pk`, `mm.sync_s`, `mm.token`, `mm.stopped`): NVS's limit.
- **`kv_get` answers ABSENT and FAULT apart** (`MM_KV_ABSENT` -1, `MM_KV_FAULT` -2; `v0.9.41`). It
  matters for one key: "no seed" is a fresh device and the library mints an identity, while "the seed
  is there and unreadable" is a device whose identity must not be replaced, and the library halts. A
  HAL that cannot tell them apart returns -1 for both and gets the older behaviour, which is why -1 is
  the default and anything else is the stronger claim. `mm.seed` is stored as 32 secret bytes plus a
  4-byte SHA-256 checksum of them; a bare 32-byte blob is the older form and migrates in place.
  A `kv_set` of zero bytes may be implemented as an erase; the library treats "absent" and "empty" alike.
- Clock: the app never acts on a zero clock, and a relay hold is released when `now` passes the deadline
  — a clock that jumps forward releases early (the safe direction); one that jumps back holds longer,
  bounded by the next tick after the jump is corrected. Set the clock before the loop, not during it.
- Text fields are bounded (`MM_DETAIL_CAP` 320, `MM_REASON_CAP` 192): a detail or reason longer than
  that is truncated, where the host would carry it whole. The fixtures contain no such line.
