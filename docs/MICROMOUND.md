# OPERATION MICROMOUND — Canonical Design Doc

Status: Active design doc, pre-M0. This file mirrors the role `docs/HOMELAB.md` plays in ANTHILL:
it tracks phase status and the design decisions that hold across phases. The wire protocol lives
in `docs/PROTOCOL.md`; the safety model in `docs/SAFETY.md`.

## What MICROMOUND is

MICROMOUND extends ANTHILL into physical devices: Raspberry Pis, ESP32s, sensors, cameras,
robots, fabrication equipment, and building systems.

The **Primary Colony** (the ANTHILL install) remains in command. It delegates missions,
permissions, and operating limits to smaller **Micromound colonies**. Each Micromound runs one of
two controller tiers, chosen by its hardware:

- **Edge Queen** — Pi-class device (full Linux, .NET 9 runtime, optionally a local model via
  Ollama). Can plan within its charter, sequence work, and produce rich evidence.
- **Deterministic Controller** — microcontroller-class device (ESP32). No local intelligence:
  a fixed firmware state machine that executes only pre-approved routines from its charter.

Micromounds can continue authorized work while disconnected, retain mission context, record
evidence locally, and synchronize with ANTHILL on reconnect. **Losing communication never grants
additional authority.** Hazardous actions remain prohibited without explicit authorization.

Commands alone do not prove physical work succeeded. Micromounds must verify results with sensor
data, telemetry, images, or other independent evidence. Safety systems — emergency stops,
watchdogs, interlocks, operating limits — are physically and logically separate from AI control.

One colony directs the mission. Many colonies extend its reach. Each may act locally, but only
within the authority it has been given.

## Where it lands in ANTHILL

MICROMOUND surfaces in the **Integrations** tab (`/tools/integrations`), alongside model
providers, coding agents, and the homelab. ANTHILL-side code is a future
`Anthill.Modules.Micromound` module that follows the homelab pattern:

- Registers a `micromound` integration kind in the integration catalog (category `infra`,
  auth mode `token`), publishing typed widget payloads (`mound_fleet`, `mission_status`,
  `evidence_feed`) the widget runtime renders without special-casing.
- Adds `/micromound/*` endpoints (mound registry, charters, missions, evidence, stop controls)
  gated by new permissions `read_micromound`, `manage_micromound`, `approve_micromound_actions` —
  same tiering as `read_homelab` / `manage_homelab_integrations` / `approve_homelab_actions`.
- Reuses, never duplicates: the credential store (`FieldCipher`-backed) for device enrollment
  secrets, the target allowlist discipline (D1) for any host ANTHILL dials out to, the
  `IApprovable` approval pipeline for physical actions, and the existing event/audit streams.
- Kill switches: `.anthill/MICROMOUND_STOP` halts all mound-directed action (mirrors
  `HOMELAB_STOP`); per-mound stop lives in the mound record and in every charter.

This repository (`micromound`) holds everything device-side plus the shared protocol contracts.
The ANTHILL-side module is specified here and in `docs/PROTOCOL.md`, and is implemented as a PR
against the ANTHILL repository when M1 begins.

## Phase plan

| Phase | Status | Scope |
|---|---|---|
| M0 — Protocol + simulator | **In progress** | Shared contracts (envelopes, charters, evidence), simulated mounds, network-free protocol tests. Nothing physical, nothing in ANTHILL yet |
| M1 — ANTHILL read-only integration | Planned | `Anthill.Modules.Micromound`: mound registry, enrollment, telemetry/inventory sync, Integrations-tab widgets. Zero command path — ANTHILL can see mounds, not direct them |
| M2 — Edge Queen runtime | Planned | Pi-class runtime: charter acceptance, mission execution, offline continuation, local evidence store, reconnect sync. Actions still limited to `benign` class |
| M3 — Deterministic Controller firmware | Planned | ESP32 firmware skeleton: charter-limited routine execution, sensor reporting, hardware watchdog integration |
| M4 — Approval-gated physical actions | Planned | `controlled`-class actions through the ActionProposal pipeline: propose → blast-radius → approve → lease → execute → verify with evidence → audit |
| M5 — Hazardous action authorization | Future | Per-action, non-standing authorization for `hazardous`-class work (fabrication, actuation near people, building systems). Two-person-rule capable |

Phases land in order. A later phase never ships while an earlier phase's tests are red.

## Controller tiers

| | Edge Queen | Deterministic Controller |
|---|---|---|
| Hardware | Pi 4/5, other Linux SBCs | ESP32 family |
| Runtime | .NET 9 (`Micromound.EdgeQueen`) | C firmware (ESP-IDF), no dynamic code |
| Intelligence | Optional local model; may sequence and adapt within charter | None. Fixed routines only, selected by charter reference |
| Charter scope | Missions with bounded planning latitude | Enumerated routine IDs with fixed parameter ranges |
| Offline behavior | Continues authorized missions until lease expiry, then quiesces | Continues current routine; on lease expiry enters declared safe state |
| Evidence | Sensor logs, images, telemetry, structured action records | Sensor readings + routine outcome codes |
| Protocol | Full envelope set | Reduced envelope set (see PROTOCOL.md §8) |

Both tiers speak the same signed envelope format over device-initiated HTTPS. Devices dial in;
ANTHILL never needs an outbound connection to a mound (and any optional outbound path obeys the
homelab target allowlist).

## Authority model

Authority flows one way: Primary Colony → Micromound, always explicit, always bounded.

- **Charter.** The delegation document. Names the mound, the mission, granted capabilities,
  operating limits (ranges, rates, geofence/workspace bounds), action-class ceiling, evidence
  requirements, and a hard expiry. Signed by the Primary Colony; a mound refuses work absent a
  valid charter covering it.
- **Lease.** Every charter carries a lease TTL. Connected mounds renew on each sync beat.
  A disconnected mound may continue only work already authorized, only until the lease expires,
  then must quiesce to its declared safe state. Reconnection resumes nothing automatically —
  the mound reports state and awaits renewal.
- **Action classes.** `observe` (sense/report) < `benign` (reversible, no physical risk) <
  `controlled` (reversible-with-effort or costly; needs ActionProposal approval) < `hazardous`
  (physical risk; needs explicit per-action authorization, never standing, M5). A charter sets a
  ceiling; the ceiling cannot be raised by anything the mound does, says, or infers.
- **No self-elevation.** Loss of communication, sensor anomalies, mission urgency, or model
  output never expand authority. Every ambiguous case resolves downward.

## Verification and evidence

- Every action record pairs the command issued with independent evidence of outcome: sensor
  deltas, telemetry windows, images, or checkable state. An action without evidence is
  `unverified` and is reported as such — never silently assumed done.
- Evidence is hash-chained per mound (each bundle references the previous bundle's digest), so
  gaps and reordering are detectable after offline periods.
- ANTHILL's existing Verifier role consumes synced evidence; missions over mounds get the same
  verification gate as code missions ("Commands are not proof" is the physical analogue of
  "the diff is not the test run").

## Safety model (summary — canonical text in SAFETY.md)

1. Independent safety layer: e-stops, hardware watchdogs, interlocks, and limit switches act
   below and outside the controller software. Nothing in this protocol can read, disable, or
   bypass them; firmware treats their trips as facts to report, not states to manage.
2. Operating limits in a charter are enforced twice: by the mound's controller AND, where
   hardware allows, by configuration outside AI reach (fuses, clamps, mechanical stops).
3. Every stop is reachable three ways: physically at the device, per-mound from ANTHILL, and
   globally via `.anthill/MICROMOUND_STOP`. Stop always wins over any in-flight mission.
4. Watchdog default: an Edge Queen that stops hearing its own runtime heartbeat, or a controller
   that stops hearing its firmware loop, drops actuation and enters safe state.

## Design rules that hold for every phase

1. Observe before act: read-only lands before action-gated; actions only arrive behind the
   approval pipeline. (Inherited verbatim from homelab rule 1.)
2. Disconnection never widens authority. Leases only run down; nothing on the device can extend
   them.
3. Commands are not evidence. Verification requires sensing independent of the actuation path.
4. Safety systems are not AI-addressable. No envelope, charter, or firmware path may configure,
   suppress, or reset an independent safety device.
5. One charter authority. Only the Primary Colony signs charters; mounds never re-delegate to
   other mounds.
6. Per-device keys, minimal secrets. A compromised mound yields its own identity and nothing
   else; ANTHILL-side secrets stay in the existing credential store, write-only.
7. Deterministic device code is plain C#/C service code; any LLM on an Edge Queen only plans,
   summarizes, and explains within charter — it holds no tool that touches actuation directly
   without the deterministic layer enforcing limits. (Homelab rule 2, extended to the edge.)
8. Every stateful feature ships with: model, persistence, API (if UI-facing), tests, version
   note, changelog entry. (Homelab rule 7, verbatim.)

## Repository layout

```text
src/Micromound.Protocol/     Shared contracts: envelopes, charters, evidence, action classes
src/Micromound.EdgeQueen/    Pi-class runtime (M2)
src/Micromound.Sim/          Simulated mounds — protocol development and tests without hardware
firmware/esp32/              Deterministic Controller firmware skeleton (M3, ESP-IDF)
tests/Micromound.Tests/      Contract and protocol tests (network-free, like the homelab mock harness)
docs/                        This file, PROTOCOL.md, SAFETY.md
```

The solution follows ANTHILL conventions: `Directory.Build.props` pins net9.0 / C# 13 /
nullable-enabled / deterministic builds; project names use the `Micromound.*` prefix as ANTHILL
uses `Anthill.*`.
