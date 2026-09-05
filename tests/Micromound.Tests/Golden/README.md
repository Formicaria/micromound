# Golden files

`files/` holds frozen copies of the exact bytes MICROMOUND puts on the wire: canonical envelope
serializations, their sha256 digests, and the JSON shape of every typed body.

They exist for one reason. M5 ships a C protocol mirror for the ESP32 — `firmware/micromound-c`,
consumed by `firmware/esp32` — and two independent implementations of the same wire format drift
silently unless something pins them together. These files are that pin: the C mirror's host tests
feed the same fixed inputs and must produce byte-identical output.

## Working with them

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
| `canonical-envelopes.txt` | six chained envelopes: canonical bytes, `prev_digest` linkage, digests | every digest and link; `mound_sync`, `action_record`, `charter` rebuilt byte for byte |
| `canonical-bodies.txt` | the bare JSON of every typed body | `charter` and `action_record` rebuilt |
| `canonical-strings.txt` | the §2 escaping rule: `<utf-8 hex> TAB <literal>` | every row through `mm_json_escape` |
| `canonical-doubles.txt` | .NET's number layout: `<IEEE bits> TAB <text>` | every row through `mm_format_double` |

The fixtures are **current** — they were regenerated when the v0 contracts were last amended
(`routines` on charters; `mission_id` / `routine_id` / `requested_parameters` / `evidence_required`
on action records) and `v0.9.18`'s escaping change left every existing byte untouched (all were
ASCII). `firmware/micromound-c`'s `make test` reads these four files and is the other half of the pin.

## What `sig` actually does here

`sig` is **zeroed, not omitted**. The canonical bytes contain the field with an empty value:

```text
…,"prev_digest":"","sig":""}
```

No fixture contains a real signature, and that is the contract: a device signs the canonical bytes
and chains the same digest, without re-serializing and without the signature perturbing either.

The distinction matters for the C mirror. An encoder written to "exclude the signature" would drop
the field and produce a different digest for identical data — a divergence that would surface only
as an unverifiable device in the field, which is the precise failure these files exist to prevent.
Emit `"sig":""`.
