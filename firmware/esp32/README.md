# Constrained controller firmware (ESP32) — M5

Placeholder for the ESP-IDF project. Nothing in *this* directory compiles yet, by design: the
firmware starts only once the protocol contracts are frozen and the Pi-class runtime has proven
them. But the software of the controller already exists and is host-tested —
[`firmware/micromound-c`](../micromound-c/README.md) (`v0.9.18`–`v0.9.21`): the wire format, the
reader and validators, the capability kernel and the device loop, verified against the golden
fixtures and, for the device loop, accepted by the host's own verifier. What this project adds is the
board: an HTTPS transport and enrollment, drivers as executors, a clock, key storage, and `app_main`
driving `mm_device`. See [`docs/ROADMAP.md`](../../docs/ROADMAP.md).

## What this firmware will be

An ESP-IDF (C) project implementing the reduced protocol profile from
[`docs/PROTOCOL.md`](../../docs/PROTOCOL.md) §8:

- **Envelope kinds:** `enroll`, `mound_sync`, `charter`, `action_record`, `stop`, `ack` only.
  Absent, and why: `mission` and `mission_report` (a controller runs compiled routines selected by
  charter, not open work packets), `evidence_bundle` (fixed-shape readings ride on the action
  record), and `config` (the hardware map is compiled in).
- **Ed25519 signing** — today over TweetNaCl in `micromound-c` (auditable, slow); libsodium or
  monocypher behind the same `mm_ed25519.h` if the beat needs it. No unsigned mode exists; a board
  that cannot sign does not join the mesh.
- **The same capability kernel, in C.** Not a simplified one: the same check order, the same
  three-tier limit intersection, the same closed set of refusal reasons. A controller that
  refused differently from a Pi would make "the mound refused" mean two different things.
- **Compiled routines.** Enumerated at build time. A charter can only enable routines this image
  already contains, and parameters clamp to compiled ranges regardless of charter contents — the
  charter narrows, never widens (mirrors `LimitClamp.Effective` in `Micromound.Protocol`).
- **Hardware watchdog.** Loss of the firmware loop drops actuation into the declared safe state.
  Layer 0 devices — e-stops, interlocks — are wired outside the MCU's control and are reported as
  observed facts only. See [`docs/SAFETY.md`](../../docs/SAFETY.md).

Logical ants may still be represented in metadata even when Scout, Forager, Guard, and Runner
compile into one image, so a controller mound renders in a colony view like any other.

## Layout (when it lands)

```text
firmware/esp32/
  main/            app_main, sync beat task, watchdog task
  components/
    micromound_c/  ../micromound-c — wire format, reader, validators, kernel, device loop (exists, host-tested)
    mm_routines/   the compiled capability/routine tables (mm_capability_desc / mm_routine_desc) for this board
    mm_drivers/    GPIO, I2C, ADC as mm_executor implementations, with real hold/release timing
    mm_link/       HTTPS transport (mm_exchange_fn) and the enrollment exchange (PROTOCOL.md §3)
  test/            Unity-based host tests for the protocol mirror
```

## The fixtures already exist

`micromound-c`'s host tests read the same golden files the C# tests do, and reproduce every
`digest:` line and every reduced-profile `canonical:` line byte for byte — see
[`tests/Micromound.Tests/Golden/`](../../tests/Micromound.Tests/Golden/README.md). Run them with
`make -C firmware/micromound-c test`.

Two properties of the wire format exist specifically to make this practical:

- The signature format is deliberately trivial — `ed25519:<lowercase hex>`, no base64, no JSON
  nesting.
- `sig` is **zeroed, not omitted**, in the canonical bytes. The field is present with an empty
  value: `…,"prev_digest":"","sig":""}`. A C encoder written to "exclude the signature" would drop
  the field and produce different digests for identical data. Emit `"sig":""`.

Together these let the firmware sign and hash one buffer it has already built, with no
re-serialization pass and no dynamic allocation.
