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
make test       # 1,700+ checks, including every golden file, byte for byte
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
| `mm_bodies` | `mm_bodies.h` | The reduced-profile bodies field for field: `mound_sync`, `action_record`, `ack`, `stop`, `charter` | `canonical-envelopes.txt`, `canonical-bodies.txt` |
| `mm_json_read` | `mm_json_read.h` | A bounded pull parser: full escape grammar, strict UTF-8, JSON number grammar, depth-capped, skips unknown members, hands values on as raw slices | `test_json_read.c` |
| `mm_time` | `mm_time.h` | `yyyy-MM-ddTHH:mm:ssZ` ↔ epoch seconds; accepts the offset/fractional forms §2 asks readers to tolerate | `test_time.c` |
| `mm_decode` | `mm_decode.h` | The receive side: the envelope frame, **signature verified from the bytes as received**, `charter`/`stop`/`ack`/`action_record` into fixed-capacity structs, `EnvelopeValidator` and `CharterValidator` with the host's reason lines character for character, views to re-encode | `canonical-signed.txt` |
| `mm_kernel` | `mm_kernel.h` | **The capability kernel**: compiled capability/routine tables, `KernelAuthority` (charter, lease, stop, quiesce, device limits), the thirteen authorization checks in the host's order, hardware ∩ device ∩ charter, duty cycle and rate, clamping, execution through a function pointer, the evidence gate; the host's refusal reasons and detail text | `kernel-decisions.txt` |
| `mm_device` | `mm_device.h` | **The device loop** (`RunnerAnt`): signed, chained uplink on a bounded queue, the beat and its acknowledgement-driven drain, downlink verified from the bytes as received and handled stops-first, acks, lease renewal on the acknowledged beat, quiesce, offline as a normal state | `device-session.txt` (written here, verified by the host) |

Deliberately absent, per PROTOCOL.md §8: `mission`, `mission_report`, `evidence_bundle`, `config`.
A constrained controller runs compiled routines selected by charter; it never plans.

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
  tests/              mm_test.h harness; one test file per module; test_golden.c, test_kernel.c and test_device.c cover the seven fixtures
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

## What this is not, yet

- **Not the board.** [`firmware/esp32`](../esp32/README.md) is still a placeholder. What it must add
  is exactly what this library declines to guess at: an HTTPS transport and the enrollment exchange,
  drivers as executors with real hold/release timing, a clock, and protected key storage.
- **Not fast.** TweetNaCl signs in tens of milliseconds on an ESP32-class core; adequate for a sync
  beat, not for anything hotter. The backend sits behind `mm_ed25519.h` and the tests prove a swap
  did not change the bytes.
- **Not enrollment.** PROTOCOL.md §3 is an HTTP exchange, not an envelope; it belongs with the
  transport on the board.

## Portability notes

- Requires a correctly rounded `printf("%.*e")` and `strtod` (glibc, musl, newlib, MSVCRT ≥ 2015).
  `canonical-doubles.txt` is the check for the libc in use; run `make test` on the target toolchain.
- Integer widths: `long long` for C# `long`, `double` for everything the protocol types as a number.
  TweetNaCl's `u32` is `unsigned long` and is masked where it matters. The sanitizer build in CI
  (ASan + UBSan, every finding fatal) is clean, with one named exemption: `shift-base`, which
  TweetNaCl's field arithmetic trips by left-shifting negative limbs — see the `SANITIZE` flag set
  in the Makefile. Nothing of MicroMound's own is exempt.
- Endianness: SHA-256 and the hex helpers are byte-oriented; the double fixture is decoded via an
  integer, so it reads correctly on either byte order.
