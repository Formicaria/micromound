// Micromound host — the headless Raspberry Pi / Linux daemon.
//
// M4 deliverable; see docs/ROADMAP.md. The composition order below is the build order, and it is
// deliberate: nothing that can move hardware is constructed until the thing that authorizes it
// exists.
//
//   1. Identity      — load or generate the device Ed25519 keypair from /var/lib/micromound/identity
//   2. Manifest      — load /etc/micromound/mound.json, validate, fail closed
//   3. Drivers       — resolve and configure from the manifest's hardware bindings
//   4. Registries    — register capabilities and routines from what the drivers actually expose
//   5. Kernel        — CapabilityKernel over those registries; bind executors
//   6. Evidence      — local store and pending-sync queue
//   7. Sync          — Runner Ant transport, durable uplink queue, enrollment if unenrolled
//   8. Reasoning     — NoReasoningProvider unless the manifest configures otherwise
//   9. Runtime       — Mound Major, worker registry, the six default ants
//  10. Watchdog      — software heartbeat; on loss, drop actuation into the declared safe state
//
// Local layout:
//   /etc/micromound/mound.json          bootstrap: identity, controller key, endpoint, hardware
//   /var/lib/micromound/identity/       device keypair, never transmitted, never exported
//   /var/lib/micromound/state/          active charter, mission, lease, worker state
//   /var/lib/micromound/evidence/       local evidence store
//   /var/lib/micromound/queue/          durable outbound queue
//
// The daemon requires no browser and no graphical environment. All user-facing configuration and
// visualization belongs to the upstream controller — see docs/UPSTREAM.md.

// The runtime is composable and runnable: MoundHost.Create brings a mound up from a manifest over
// a durable file store, using the same MoundComposition the simulator does, and runs missions,
// persists state, and recovers across restarts. What this entry point still lacks is the daemon
// SERVICE LOOP — a real network transport to the controller, the sync-beat loop, signal-driven
// graceful shutdown, and the timing watchdog — which is the next M4 slice. Until then a mound is
// brought up in-process (see MoundHost / the tests), not from this argv-less stub.

Console.Error.WriteLine(
    "micromound host: the runtime composes and runs from a manifest (MoundHost), but the daemon " +
    "service loop (network transport, sync beat, signal handling, watchdog) is the next M4 slice. " +
    "Run Micromound.Sim for the simulated mound; see docs/ROADMAP.md for what lands when.");

return 2;
