# micromound-c — the C mirror of the MICROMOUND wire format

The first piece of the constrained-controller firmware (ROADMAP M5), built where it can be proven:
a portable C99 library that produces the **exact canonical bytes** the C# runtime produces
(`Micromound.Protocol`), digests and signs them the same way, and is checked byte for byte against
the golden fixtures in [`tests/Micromound.Tests/Golden/files`](../../tests/Micromound.Tests/Golden/)
by `gcc` and `clang` on the host. Nothing here needs a board. Everything here is what a board will
run.

```bash
make            # build/libmicromound.a
make test       # 900+ checks, including every golden file, byte for byte
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
| `mm_bodies` | `mm_bodies.h` | The reduced-profile bodies field for field: `mound_sync`, `action_record`, `ack`, `charter` | `canonical-envelopes.txt`, `canonical-bodies.txt` |

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
  tests/              mm_test.h harness; one test file per module; test_golden.c reads the fixtures
  Makefile
```

`mm_ed25519.c` `#include`s `tweetnacl.c` rather than linking it, to reach the file-static field
arithmetic for the detached functions and the seed keypair. TweetNaCl's own `randombytes` symbol
is satisfied by a stub that aborts — reaching it is a programming error, since nothing here makes
keys.

## What this is not, yet

- **Not a reader.** The device receives `charter`, `stop` and `ack` as JSON; parsing them in C
  (bounded, no allocation, refusing anything outside the fixed shape) is the next M5 slice.
- **Not the kernel.** The capability kernel in C — same check order, the same three-tier limit
  intersection, the same closed set of refusal reasons — follows the reader.
- **Not fast.** TweetNaCl signs in tens of milliseconds on an ESP32-class core; adequate for a sync
  beat, not for anything hotter. The backend sits behind `mm_ed25519.h` and the tests prove a swap
  did not change the bytes.
- **Not the firmware.** [`firmware/esp32`](../esp32/README.md) is still a placeholder; when it
  lands as an ESP-IDF project, this directory is its `mm_protocol` component.

## Portability notes

- Requires a correctly rounded `printf("%.*e")` and `strtod` (glibc, musl, newlib, MSVCRT ≥ 2015).
  `canonical-doubles.txt` is the check for the libc in use; run `make test` on the target toolchain.
- Integer widths: `long long` for C# `long`, `double` for everything the protocol types as a number.
  TweetNaCl's `u32` is `unsigned long` and is masked where it matters; the sanitizer build in CI
  (`-fsanitize=address,undefined`) is clean.
- Endianness: SHA-256 and the hex helpers are byte-oriented; the double fixture is decoded via an
  integer, so it reads correctly on either byte order.
