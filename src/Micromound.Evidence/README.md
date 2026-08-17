# Micromound.Evidence

Capture, correlation, the local hash-chained store, and the pending-sync queue — the Witness Ant's
machinery.

The verdict rule itself is **not** here. `EvidenceGate` lives in `Micromound.Protocol` because it
is a statement about what a contract means, it does no I/O, and the ESP32 mirror needs the same
rule in C. This project is what feeds it: locating prior readings, pairing a "before" with an
"after", and deciding which items an action's claim actually rests on.

One retention rule overrides capacity: evidence pending synchronization is never evicted before it
is acknowledged, unless storage exhaustion forces oldest-acked-first eviction — and that eviction
is itself reported on the wire as `evicted_acked_items`. Silently dropping proof is
indistinguishable from never having captured it.

Status: M3.
