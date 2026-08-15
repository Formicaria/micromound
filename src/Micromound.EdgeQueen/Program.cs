// Micromound Edge Queen — M2 scaffold.
//
// This runtime does not ship until M0 (protocol + simulator tests) and M1 (ANTHILL read-only
// integration) are green — see docs/MICROMOUND.md phase plan. The skeleton exists so the
// solution shape is settled from day one.
//
// M2 scope, in build order:
//   1. Device identity: on-device Ed25519 keypair, enrollment flow (PROTOCOL.md §3).
//   2. Sync beat loop: device-initiated HTTPS, backoff with jitter, durable uplink queue.
//   3. Charter store: validate → accept → enforce (CharterValidator + LimitClamp are already
//      in Micromound.Protocol and covered by tests).
//   4. Evidence store: hash-chained local ring buffer, ack-aware eviction.
//   5. Deterministic actuation layer: the ONLY path to hardware; any local model output is a
//      proposal to this layer (SAFETY.md Layer 1).
//   6. Software watchdog + safe-state transitions.

Console.Error.WriteLine(
    "Micromound.EdgeQueen is an M2 deliverable and is not runnable yet. " +
    "Run Micromound.Sim for the M0 simulated mound, and see docs/MICROMOUND.md.");
return 2;
