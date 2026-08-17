# Golden files

`files/` holds frozen copies of the exact bytes MICROMOUND puts on the wire: canonical envelope
serializations, their sha256 digests, and the JSON shape of every typed body.

They exist for one reason. M3 ships a C protocol mirror on the ESP32 (`firmware/esp32`), and two
independent implementations of the same wire format drift silently unless something pins them
together. These files are that pin: the C mirror's host tests feed the same fixed inputs and must
produce byte-identical output.

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

> **These fixtures are currently stale, on purpose.** The v0 contracts were amended — `routines`
> on charters, `mission_id` / `routine_id` / `requested_parameters` / `evidence_required` on
> action records — while no device is deployed and no C mirror exists, so v0 was amended in place
> rather than superseded (`docs/PROTOCOL.md` §11). Run the regeneration above once, read the diff,
> and commit it. After the first firmware ships this stops being an option.

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
