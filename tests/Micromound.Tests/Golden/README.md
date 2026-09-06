# Golden files

`files/` holds frozen copies of the exact bytes MICROMOUND puts on the wire — canonical envelope
serializations, their sha256 digests, the JSON shape of every typed body, real signatures under fixed
test seeds — and, since `v0.9.20`, the capability kernel's decisions over a scripted session; since `v0.9.21`, one
fixture runs the other way — `device-session.txt` is produced by the C device and verified by the host; since
`v0.9.22`, the enrollment exchange (`enroll-exchange.txt`), the one HTTP exchange that is not an envelope.

They exist for one reason. M5 ships a C protocol mirror for the ESP32 — `firmware/micromound-c`,
consumed by `firmware/esp32` — and two independent implementations of the same wire format drift
silently unless something pins them together. These files are that pin: the C mirror's host tests
feed the same fixed inputs and must produce byte-identical output.

## Working with them

`device-session.txt` and `port-exchange.txt` are the exceptions to "the C# test writes, the C test
reads": `make -C firmware/micromound-c test` writes them when missing (and fails that run, like
`GoldenFile`), and compares against them afterwards; the C# `DeviceSessionTests` and `LinkPortsTests`
only read them. Regenerate one by deleting it and running the C tests twice, then read the diff as you
would any other golden change.

A missing golden file fails the run rather than writing itself green — a fixture that
regenerates on demand pins nothing. To bootstrap the files on a fresh checkout, or to accept an
intentional protocol change:

```powershell
# PowerShell
$env:MICROMOUND_UPDATE_GOLDEN = "1"
dotnet test Micromound.sln              # rewrites the files, fails once
Remove-Item Env:\MICROMOUND_UPDATE_GOLDEN
dotnet test Micromound.sln              # green
```

```bash
# bash / zsh
MICROMOUND_UPDATE_GOLDEN=1 dotnet test Micromound.sln   # rewrites the files, fails once
dotnet test Micromound.sln                              # green
```

Unset the variable before the verifying run. Left set, every run rewrites and fails, and the
fixtures stop pinning anything.

Read the diff before committing. **An unexpected change here is a protocol change**, not a stale
test — it means the bytes a deployed mound would send no longer match what a deployed controller
(or an ESP32 in the field) expects. Fixing the golden file to match new code is only correct once
`docs/PROTOCOL.md` says so and the version rule in §11 has been applied.

## The files

| File | What it pins | C mirror test |
|---|---|---|
| `canonical-envelopes.txt` | six chained envelopes: canonical bytes, `prev_digest` linkage, digests | every digest and link; `mound_sync`, `action_record`, `evidence_bundle`, `charter` rebuilt byte for byte |
| `canonical-bodies.txt` | the bare JSON of every typed body | `charter`, `action_record` and `evidence_bundle` rebuilt |
| `canonical-strings.txt` | the §2 escaping rule: `<utf-8 hex> TAB <literal>` | every row through `mm_json_escape` |
| `canonical-doubles.txt` | .NET's number layout: `<IEEE bits> TAB <text>` | every row through `mm_format_double` |
| `canonical-signed.txt` | four REAL signed wire envelopes (fixed test seeds): a device beat and a controller's charter/stop/ack chain | each verified from the bytes as received, decoded, re-encoded to the same body, re-signed to the same wire |
| `kernel-decisions.txt` | the capability kernel's decisions: a fixed device, a fixed clock, 42 scripted steps — reason, detail, effective parameters, limits, state, record | `mm_kernel` replays the script and must match every line |
| `device-session.txt` | **written by the C side** (`test_device.c`): a whole device↔controller session — every `up:` and `down:` wire envelope, an outage, re-sends | the C test compares its session to the file; `DeviceSessionTests` (C#) verifies every uplink envelope, the chain, and every body with the host's code |
| `link-frames.txt` | the Pi↔ESP32 link framing (PROTOCOL.md §12): the CRC-32 on two inputs, and eleven request/response frames as `Micromound.Protocol.LinkFrame` encodes them | `mm_frame` encodes every case to the same `frame:` bytes and decodes each `frame:` to the same `payload:` (`test_frame.c`) |
| `port-exchange.txt` | **written by the C side** (`test_ports.c`): the board as a port server — 37 requests (hello, writes, analog reads, digital-input reads, every refusal, the watchdog, the trip) and the board's exact answers. The input scenarios arrived with `v0.9.27`: an active-low limit switch read open and closed, an output pin that is not an input (`404`), a `read_pin` with no pin (`400`), a line that will not read (`503`, never a `false`), and the `inputs` array in `hello` | the C test compares itself to the file; `LinkPortsTests` (C#) parses every `resp:` with the Pi's client and checks its own request bodies against the `req:` lines |
| `enroll-exchange.txt` | the PROTOCOL.md §3 enrollment exchange as `HttpEnrollmentClient` performs it: the exact request body for a fixed device, and the verdict + detail line for fifteen scripted controller responses | `mm_enroll_request_body` must equal the `request:` line; `mm_enroll_read_response` must reach every `verdict:` in the `detail:` words (`test_board.c`) |

The fixtures are **current** — they were regenerated when the v0 contracts were last amended
(`routines` on charters; `mission_id` / `routine_id` / `requested_parameters` / `evidence_required`
on action records; and, in `v0.9.24`, `evidence` on action records — which re-chained
`canonical-envelopes.txt` from seq 1 and re-signed `device-session.txt`, while `canonical-signed.txt`,
which carries no action record, did not move) and `v0.9.18`'s escaping change left every existing byte
untouched (all were ASCII). `firmware/micromound-c`'s `make test` reads every one of these files and is the other half of the pin.

## What `sig` actually does here

`sig` is **zeroed, not omitted**. The canonical bytes contain the field with an empty value:

```text
…,"prev_digest":"","sig":""}
```

The canonical fixtures contain no real signature, and that is the contract: a device signs the canonical
bytes and chains the same digest, without re-serializing and without the signature perturbing either.
(`canonical-signed.txt` and `device-session.txt` carry real signatures under fixed test seeds — over
exactly those canonical bytes.)

The distinction matters for the C mirror. An encoder written to "exclude the signature" would drop
the field and produce a different digest for identical data — a divergence that would surface only
as an unverifiable device in the field, which is the precise failure these files exist to prevent.
Emit `"sig":""`.
