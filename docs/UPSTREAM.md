# The upstream controller

MicroMound receives its authority from an **upstream controller**: the party that holds the
signing key, issues charters and missions, receives evidence, and holds the stop controls.

The protocol deliberately does not name one. A mound does not know or care whether the controller
that chartered it is a full colony platform, a small self-hosted issuer, or a test harness — it
knows a public key and an endpoint. That is what keeps MicroMound a runtime rather than a client.

## What a controller must do

To fill the role, an implementation has to:

1. **Mint enrollment tokens.** One-time, operator-created, stored write-only. Bind a device's
   public key on first use and burn the token. Re-enrollment needs a fresh token — there is no
   self-service re-key.
2. **Hold a signing key** and sign every downlink envelope with it.
3. **Issue charters.** Complete replacements, never diffs. Set capabilities, routines, limits,
   action ceiling, evidence policy, safe state, lease TTL, and a hard expiry.
4. **Answer the sync beat.** Accept uplink envelopes, acknowledge them, and return pending
   downlink. Acknowledgement is what renews a lease; nothing on the device can.
5. **Verify uplink.** Check signatures and the hash chain over drained backlogs. Refuse loudly and
   specifically.
6. **Consume evidence** and treat `unverified` as failed-until-proven when gating anything.
7. **Provide stop.** Per-mound and global, processed ahead of all other downlink, needing no valid
   charter. Resume must be explicit and must never restore the previous authority implicitly.
8. **Never require an inbound path.** Mounds dial out. A controller that needs to reach into the
   device network has changed the security model.

## What a controller must never do

- Issue a charter with a `hazardous` ceiling. Hazardous work is per-action, never standing.
- Expect a mound to widen a limit. Limits intersect; the narrowest tier wins, always.
- Expect resumption after lease expiry without issuing fresh authority.
- Ask for a device private key. No endpoint or envelope exists that returns one, by construction.

## The user-facing surface belongs upstream

MicroMound is headless and ships no UI. Everything an operator configures lives in the
controller's interface:

mound enrollment · naming · hardware profile · attached devices · driver configuration ·
capability assignment · specialized ant creation · routines · reasoning mode · local model
configuration · safety limits · charter policy · offline behaviour · evidence requirements ·
mound status · worker status · health · telemetry · firmware and runtime updates

The controller generates the manifest; the mound validates, stores, and executes it. See
[`CONFIGURATION.md`](CONFIGURATION.md).

## ANTHILL as the reference integration

[ANTHILL](https://github.com/Formicaria/Anthill) is the reference implementation of this contract,
and a **separate repository and application**. MicroMound support is an **optional integration**
on the ANTHILL side: ANTHILL is complete without it, and MicroMound runs without ANTHILL.

Where it lands, on the ANTHILL side:

- An `Anthill.Modules.Micromound` module registering a `micromound` integration kind, publishing
  typed widget payloads (`mound_fleet`, `mission_status`, `evidence_feed`).
- `/micromound/*` endpoints — mound registry, enrollment, charters, missions, evidence, stop
  controls — gated by `read_micromound`, `manage_micromound`, `approve_micromound_actions`.
- Reuse, never duplication: the existing credential store for enrollment secrets, the target
  allowlist discipline for any outbound host, the existing approval pipeline for physical actions,
  and the existing event and audit streams.
- Kill switches: `.anthill/MICROMOUND_STOP` halts all mound-directed action; per-mound stop lives
  in the mound record and in every charter.

None of that lives in this repository. This repository holds the device-side runtime and the
shared protocol contracts; the ANTHILL module is implemented as a PR against ANTHILL.

### Colony view

MicroMound is not meant to be buried as another settings-page integration. A mound is a
subordinate physical colony, and the intent is that it appears directly in the colony view
alongside software workers:

```text
ANTHILL COLONY

Primary Mound                  Greenhouse MicroMound          Rover MicroMound
    Researcher                     Mound Major                    Mound Major
    Coder                          Scout Ant                      Scout Ant
    Tester                         Forager Ant                    Forager Ant
    Verifier                       Guard Ant                      Guard Ant
    ProxmoxAnt                     Witness Ant                    Witness Ant
    StorageAnt                     Cache Ant                      Cache Ant
                                   Runner Ant                     Runner Ant
                                   · optional specialized ·       · optional specialized ·
                                   (e.g. vision inspector)        (e.g. navigation planner)
```

Every MicroMound in the colony runs the *same* standard Mound Major and six default ants; the two
here differ only in hardware and in whatever *optional* specialized workers each manifest chose to
add (see [`ANTS.md`](ANTS.md)). Neither is a device-specific colony — both are the one generic
colony configured by capabilities.

Connectivity is part of the picture, because a disconnected mound is a normal state and not an
error:

```text
Rover MicroMound        LOCAL / OFFLINE

Lease remaining: 08:42
Mission: perimeter inspection
Last sync: 01:18 ago

Vision Ant       ACTIVE
Navigation Ant   ACTIVE
Drive Ant        ACTIVE
Runner Ant       DISCONNECTED
```

On reconnect the controller can show backlog syncing, observations received, actions completed,
evidence received, and mission status updated. The visual representation should correspond to real
runtime state — every field above is something the protocol actually reports.

## Intended installation experience

1. Install the controller.
2. Enable MicroMound support.
3. Install MicroMound on a Pi, or flash the firmware to an ESP32.
4. The device generates its identity and enters enrollment mode.
5. The controller accepts the mound.
6. Name it.
7. Configure attached hardware.
8. The controller maps hardware to capabilities.
9. Enable or create specialized ants.
10. The controller pushes the signed manifest.
11. The mound appears in the colony view.
12. Authorized physical missions can now be routed to it.
13. The mound executes locally and reports evidence.
14. The controller learns from mission history and physical evidence.

## Commercial boundary

The architecture is meant to allow a lightweight standalone edge runtime alongside optional paid
fleet-management, hardware provisioning, OTA updates, health dashboards, historical telemetry,
advanced robotics profiles, packaged device templates, first-party hardware, and community-built
compatible devices.

One constraint follows from that and is worth stating as a rule: **the protocol and runtime avoid
unnecessary dependence on any hosted cloud service.** A mound and a self-hosted controller on the
same LAN must be a complete, working system with no third party involved. Local ownership is not a
deployment option to be supported later; it is the shape of the thing.
