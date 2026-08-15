# Deterministic Controller firmware (ESP32) — M3

Placeholder until M3. Nothing here compiles yet by design: the firmware only starts once the
protocol contracts (M0) are frozen and the ANTHILL read-only integration (M1) is shipped.

## What this firmware will be

An ESP-IDF (C) project implementing the reduced protocol profile from `docs/PROTOCOL.md` §8:

- Envelope kinds: `enroll`, `mound_sync`, `charter`, `action_record`, `stop`, `ack` only.
- Ed25519 signing (libsodium/monocypher — well within ESP32 capability). No unsigned mode
  exists; a board that cannot sign does not join the mesh.
- Routines are enumerated at compile time. A charter can only enable compiled routines, and
  parameters clamp to firmware-compiled ranges regardless of charter contents — the charter
  narrows, never widens (mirrors `LimitClamp.Intersect` in `Micromound.Protocol`).
- Hardware watchdog integration: loss of the firmware loop drops actuation into the declared
  safe state. Layer 0 devices (e-stops, interlocks) are wired outside the MCU's control and
  are reported as observed facts only — see `docs/SAFETY.md`.

## Layout (when M3 lands)

```text
firmware/esp32/
  main/            app_main, sync beat task, watchdog task
  components/
    mm_protocol/   reduced-envelope encode/decode + signing (C mirror of Micromound.Protocol)
    mm_routines/   compiled routine table + clamped parameter ranges
  test/            Unity-based host tests for the protocol mirror
```

The C protocol mirror is verified against the C# implementation by golden-file tests in
`tests/Micromound.Tests` (same canonical bytes, same digests) so the two implementations
cannot drift silently.

Those fixtures exist now, ahead of the firmware: see
[`tests/Micromound.Tests/Golden/`](../../tests/Micromound.Tests/Golden/README.md). `mm_protocol`'s
Unity host tests read the same files and must reproduce every `canonical:` line and `digest:` line
byte for byte. The signature format they have to parse is deliberately trivial —
`ed25519:<lowercase hex>`, no base64, no JSON nesting — and `sig` is excluded from the canonical
bytes, so the firmware can sign and hash a buffer it has already built.
