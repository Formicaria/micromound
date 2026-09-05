# Constrained controller firmware (ESP32) — M5

Placeholder for the ESP-IDF project. Nothing in *this* directory compiles yet, by design: the
firmware starts only once the protocol contracts are frozen and the Pi-class runtime has proven
them. But its protocol half already exists and is host-tested —
[`firmware/micromound-c`](../micromound-c/README.md) (`v0.9.18`): the canonical writer, .NET's
number layout, SHA-256, Ed25519 with detached sign/verify, envelopes and the reduced-profile bodies,
verified byte for byte against the golden fixtures. When this project lands, that library is its
`mm_protocol` component. See [`docs/ROADMAP.md`](../../docs/ROADMAP.md).

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
    mm_protocol/   ../micromound-c — reduced-envelope encode + signing (C mirror of Micromound.Protocol; exists);
                   the decode half (charter/stop/ack reader) is the next slice
    mm_kernel/     capability kernel: limits, action classes, duty cycle, refusal reasons
    mm_routines/   compiled routine table + clamped parameter ranges
    mm_drivers/    GPIO, I2C, ADC
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
