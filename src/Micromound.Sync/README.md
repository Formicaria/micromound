# Micromound.Sync

The Runner Ant's transport: enrollment, the signed sync beat, the durable uplink queue,
acknowledgements, reconnect, and backlog drain.

**Device-initiated only.** Mounds dial the controller; the controller never needs a path into the
device network. A mound behind NAT on a residential connection works with no inbound route
existing at all.

Offline is a normal state, not an error. Envelopes queue durably on-device and drain oldest-first
on reconnect with the hash chain intact across the gap — a backlog that reordered itself would be
indistinguishable from one that had been tampered with.

`SyncSchedule` is a pure function: exponential backoff with caller-supplied jitter, so a
controller coming back up is not met by an entire fleet retrying in lockstep, and so its tests do
not depend on a random source.

Status: shipped in M2 (`v0.6.0`) — `DurableUplinkQueue` over `IStateStore`, with the chain
enforced at enqueue and retention governed by acknowledgement. The Runner Ant that drives it lives
in `Micromound.Runtime`; a disk-backed store arrives with the M4 host.
